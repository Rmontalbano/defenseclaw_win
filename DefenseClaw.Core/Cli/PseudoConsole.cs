using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DefenseClaw.Core.Cli;

/// <summary>The pseudo-console could not be created, or the child could not be started in it.</summary>
internal sealed class PseudoConsoleException : Exception
{
    public PseudoConsoleException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The Win32 error or HRESULT that came back; 0 when there is none.</summary>
    public int ErrorCode { get; }
}

/// <summary>
/// One child process running in a Windows pseudo-console (ConPTY, <c>CreatePseudoConsole</c>, Windows 10 1809 and later): a console that
/// exists only as two pipes. What the child writes comes out of <see cref="Output"/> as terminal text (VT sequences included - see
/// <see cref="VtScreen"/>); what <see cref="Write"/> sends is read by the child as keystrokes.
/// <para>
/// <b>Why this exists (CUST-221).</b> The DefenseClaw CLI reads a secret with <c>getpass.win_getpass</c>, which is <c>msvcrt.getwch</c>:
/// it reads the <i>console input buffer</i> and never a redirected stdin, so a value piped to <c>keys set</c> is never read and the
/// command waits forever (measured with the installed runtime's own interpreter running the one-line stand-in
/// <c>getpass.getpass('Value: ')</c>: a plain pipe was still waiting after 6 s; through this class the same line read the value and
/// printed its length). A pseudo-console gives the child a real console whose keyboard is this process, so the prompt can be answered
/// without a window and without the value ever being on a command line.
/// </para>
/// <para>
/// <b>Three things that are not obvious, all found by running it.</b>
/// <list type="number">
///   <item><description>The child is started with <c>STARTF_USESTDHANDLES</c> and three null handles. Without it, a parent whose own standard
///   handles are redirected (this app launched from a script, the test host, anything with a pipe for stdout) hands them on: the child's
///   <c>print</c> then went to the parent's stdout, not to the console, while its <c>getpass</c> read the console - half a pseudo-console.</description></item>
///   <item><description>The prompt arrives in pieces with sequences in between (<c>V</c>, a window-title sequence, a cursor-visibility toggle,
///   then <c>alue: </c>), so nothing can look for text in the raw stream.</description></item>
///   <item><description>The output pipe stays open until the pseudo-console is closed, not when the child exits, so end of output is "the
///   child is gone and the pipe went quiet", then <see cref="Dispose"/>. <c>ClosePseudoConsole</c> can wait for the output to be drained
///   on older builds, so it runs on its own thread while the reader keeps draining.</description></item>
/// </list>
/// </para>
/// <para>
/// The child is created suspended, put in a kill-on-close <see cref="WindowsJob"/> and only then resumed, so unlike a child started by
/// <see cref="Process.Start()"/> there is no window in which something it starts could be outside the job.
/// </para>
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    /// <summary>The console's size. Wide, so that the lines of a CLI's output do not wrap (a wrapped line is two lines in the transcript).</summary>
    public const short DefaultColumns = 200;

    public const short DefaultRows = 40;

    /// <summary>The most text read from the child over one run. Past it the rest is dropped and <see cref="OutputTruncated"/> says so: a runaway child cannot grow the app.</summary>
    public const int MaxOutputCharacters = 4_000_000;

    /// <summary>The first Windows 10 build with <c>CreatePseudoConsole</c> (1809).</summary>
    public const int MinimumBuild = 17763;

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint WaitObject0 = 0;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Completed when the pipe has been read to its end. Not <c>Output.Completion</c>: that waits until every chunk has also been <i>taken</i> from the channel.</summary>
    private readonly TaskCompletionSource _readerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _reader;
    private readonly WindowsJob? _job;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private SafeFileHandle? _inputPipe;
    private FileStream? _input;
    private SafeFileHandle? _outputPipe;
    private Process? _process;
    private int _outputTruncated;
    private bool _disposed;

    private PseudoConsole(
        IntPtr pseudoConsole,
        SafeFileHandle inputPipe,
        SafeFileHandle outputPipe,
        IntPtr processHandle,
        int processId,
        WindowsJob? job)
    {
        _pseudoConsole = pseudoConsole;
        _inputPipe = inputPipe;
        _input = new FileStream(inputPipe, FileAccess.Write, bufferSize: 1, isAsync: false);
        _outputPipe = outputPipe;
        _processHandle = processHandle;
        ProcessId = processId;
        _job = job;

        try
        {
            // Opened by id while our own handle keeps the id from being reused. The runner needs a Process to cancel and to shut down with.
            _process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Already gone (and reaped): nothing to cancel. The handle still gives the exit code.
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pseudo-console-reader" };
        _reader.Start();
    }

    /// <summary>
    /// True on Windows 10 1809 (build 17763) and later, where <c>kernel32</c> exports <c>CreatePseudoConsole</c>. Older Windows has no pseudo-console and the
    /// caller falls back to a console window.
    /// </summary>
    public static bool IsSupported { get; } = DetectSupport();

    /// <summary>The decoded text the child wrote, in chunks, in order. Completes (once everything in it has been taken) when the pseudo-console is closed and the pipe has been read to its end.</summary>
    public ChannelReader<string> Output => _output.Reader;

    public int ProcessId { get; }

    /// <summary>The child, for <see cref="CliRunner.Cancel(CliInvocation, out string)"/> and shutdown to kill; null when it had already gone.</summary>
    public Process? Process => _process;

    /// <summary>True when more than <see cref="MaxOutputCharacters"/> were written and the rest was dropped.</summary>
    public bool OutputTruncated => Volatile.Read(ref _outputTruncated) != 0;

    /// <summary>True once the child has exited (a zero-wait on its handle: no event, no polling thread).</summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _processHandle == IntPtr.Zero || WaitForSingleObject(_processHandle, 0) == WaitObject0;
            }
        }
    }

    /// <summary>The child's exit code; null while it runs (an exit code of 259 is also what a running process reports, so this asks the handle first).</summary>
    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _processHandle != IntPtr.Zero &&
                       WaitForSingleObject(_processHandle, 0) == WaitObject0 &&
                       GetExitCodeProcess(_processHandle, out var code)
                    ? unchecked((int)code)
                    : null;
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> with <paramref name="arguments"/> in a new pseudo-console.
    /// </summary>
    /// <param name="executable">Full path of the program.</param>
    /// <param name="arguments">The arguments, without the program; quoted here the way <c>CommandLineToArgvW</c> reads them.</param>
    /// <param name="workingDirectory">Where it runs; null is this process's.</param>
    /// <param name="environment">The complete environment of the child (a <see cref="ProcessStartInfo.Environment"/> copy); null inherits this process's.</param>
    /// <param name="job">A job to put the child in before it runs; null (or a job that refuses it) leaves the tree kill as the only guard.</param>
    /// <exception cref="PseudoConsoleException">There is no pseudo-console here, or the program could not be started in it.</exception>
    public static PseudoConsole Start(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IDictionary<string, string?>? environment,
        WindowsJob? job,
        short columns = DefaultColumns,
        short rows = DefaultRows)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!IsSupported)
        {
            throw new PseudoConsoleException("This version of Windows has no pseudo-console (it needs Windows 10 version 1809 or later).", 0);
        }

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        var pseudoConsole = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var processInformation = default(ProcessInformation);
        var started = false;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
            {
                throw Failure("The pipes for the pseudo-console could not be created", Marshal.GetLastWin32Error());
            }

            var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0)
            {
                throw Failure("The pseudo-console could not be created", hr);
            }

            // The pseudo-console holds its own copies of these two ends; keeping ours would stop the pipes from ever reporting the end of the output.
            inputRead.Dispose();
            outputWrite.Dispose();
            inputRead = null;
            outputWrite = null;

            var size = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                var error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(attributes);
                attributes = IntPtr.Zero;
                throw Failure("The process attributes could not be prepared", error);
            }

            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw Failure("The pseudo-console could not be attached to the process", Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles },
                AttributeList = attributes,
            };

            var commandLine = new StringBuilder(CommandLine(executable, arguments), 32768);
            var flags = ExtendedStartupInfoPresent | CreateSuspended;
            if (environment is not null)
            {
                environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
                flags |= CreateUnicodeEnvironment;
            }

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    flags,
                    environmentBlock,
                    string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                    ref startup,
                    out processInformation))
            {
                throw Failure("The program could not be started in the pseudo-console", Marshal.GetLastWin32Error());
            }

            // Into the job before its first instruction runs. A failure leaves the tree kill as the guard; it never stops the run.
            if (job is not null)
            {
                _ = job.TryAssign(processInformation.ProcessHandle);
            }

            if (ResumeThread(processInformation.ThreadHandle) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                _ = TerminateProcess(processInformation.ProcessHandle, 1);
                throw Failure("The program could not be resumed", error);
            }

            _ = CloseHandle(processInformation.ThreadHandle);
            processInformation.ThreadHandle = IntPtr.Zero;

            var console = new PseudoConsole(pseudoConsole, inputWrite!, outputRead!, processInformation.ProcessHandle, processInformation.ProcessId, job);
            started = true;
            return console;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }

            if (!started)
            {
                if (processInformation.ThreadHandle != IntPtr.Zero)
                {
                    _ = CloseHandle(processInformation.ThreadHandle);
                }

                if (processInformation.ProcessHandle != IntPtr.Zero)
                {
                    _ = TerminateProcess(processInformation.ProcessHandle, 1);
                    _ = CloseHandle(processInformation.ProcessHandle);
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsoleAndWait(pseudoConsole);
                }

                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
            }
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> into the child's console: UTF-8 into the input pipe, which the pseudo-console turns into key events
    /// (so a carriage return is Enter). The bytes are cleared once written.
    /// </summary>
    /// <exception cref="IOException">The child is gone, or the pipe is closed.</exception>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        FileStream? input;
        lock (_gate)
        {
            input = _input;
        }

        if (input is null)
        {
            throw new IOException("The pseudo-console is closed.");
        }

        // Not under the lock: a write that waits for a child that is not reading must not keep Kill from taking it.
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The pseudo-console was closed while the keys were being typed.", ex);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>Ends the child and everything it started: the job first (one call, descendants included), then the tree walk.</summary>
    public void Kill()
    {
        _ = _job?.Terminate();

        Process? process;
        lock (_gate)
        {
            process = _process;

            // Under the lock: Dispose closes this handle under it too, and a handle value used after it was closed may be someone else's by then.
            if (_processHandle != IntPtr.Zero)
            {
                _ = TerminateProcess(_processHandle, 1);
            }
        }

        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best effort, as the runner's own kill: the process may have just exited, or a descendant ended between the snapshot and the kill.
        }
    }

    /// <summary>
    /// Closes the pseudo-console (on its own thread, while the reader keeps draining the output pipe) and returns once the output has
    /// been read to its end or <paramref name="wait"/> passed. Call it after the child has exited.
    /// </summary>
    public async Task CloseAsync(TimeSpan wait)
    {
        IntPtr pseudoConsole;
        lock (_gate)
        {
            pseudoConsole = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
        }

        if (pseudoConsole != IntPtr.Zero)
        {
            _ = StartClosing(pseudoConsole);
        }

        try
        {
            await _readerDone.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The pipe did not report its end in time; whatever was read is what there is.
        }
    }

    public void Dispose()
    {
        IntPtr pseudoConsole;
        IntPtr processHandle;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pseudoConsole = _pseudoConsole;
            processHandle = _processHandle;
            _pseudoConsole = IntPtr.Zero;
            _processHandle = IntPtr.Zero;
            _input?.Dispose();
            _input = null;
            _inputPipe = null;
        }

        // A child still running when this is disposed is not left to the pseudo-console's own closing behaviour.
        if (processHandle != IntPtr.Zero)
        {
            if (WaitForSingleObject(processHandle, 0) != WaitObject0)
            {
                _ = TerminateProcess(processHandle, 1);
            }

            _ = CloseHandle(processHandle);
        }

        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsoleAndWait(pseudoConsole);
        }

        // Closing the console ends the pipe, which ends the reader; the handle is released after it, never under it.
        _ = _reader.Join(CloseWait);
        _outputPipe?.Dispose();
        _outputPipe = null;
        _process?.Dispose();
        _process = null;
    }

    private void ReadLoop()
    {
        var pipe = _outputPipe;
        if (pipe is null)
        {
            _ = _output.Writer.TryComplete();
            _ = _readerDone.TrySetResult();
            return;
        }

        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[4096];
        var chars = new char[buffer.Length + 4];
        var total = 0;

        try
        {
            using var stream = new FileStream(pipe, FileAccess.Read, bufferSize: 1, isAsync: false);
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count == 0)
                {
                    continue;
                }

                if (total >= MaxOutputCharacters)
                {
                    Volatile.Write(ref _outputTruncated, 1);
                    continue;
                }

                var take = Math.Min(count, MaxOutputCharacters - total);
                total += take;
                if (take < count)
                {
                    Volatile.Write(ref _outputTruncated, 1);
                }

                _ = _output.Writer.TryWrite(new string(chars, 0, take));
            }
        }
        catch (IOException)
        {
            // The pseudo-console was closed under the read: that is the end of the output.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _ = _output.Writer.TryComplete();
            _ = _readerDone.TrySetResult();
        }
    }

    private static bool DetectSupport()
    {
        if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(NativeLibrary.Load("kernel32.dll"), "CreatePseudoConsole", out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static PseudoConsoleException Failure(string what, int code)
    {
        // A pseudo-console API returns an HRESULT, the rest a Win32 error; an HRESULT_FROM_WIN32 is the Win32 error in its low word.
        var win32 = (code & 0xFFFF0000) == unchecked((int)0x80070000) ? code & 0xFFFF : code;
        var detail = new Win32Exception(win32).Message;
        return new PseudoConsoleException(
            string.Create(CultureInfo.InvariantCulture, $"{what}: {detail} (0x{code:X8})."),
            code);
    }

    /// <summary>
    /// The command line <c>CommandLineToArgvW</c> reads back as <paramref name="executable"/> followed by <paramref name="arguments"/>: the
    /// program always quoted, each argument quoted only where it needs it (the rules .NET's own <c>ProcessStartInfo.ArgumentList</c> applies, so
    /// this starts the program with the same argv the piped runner does).
    /// </summary>
    internal static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    // Before the closing quote: every backslash must be doubled so that none of them escapes it.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[index] == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1).Append('"');
                    index++;
                }
                else
                {
                    builder.Append('\\', backslashes);
                }

                continue;
            }

            if (c == '"')
            {
                builder.Append('\\').Append('"');
                continue;
            }

            builder.Append(c);
        }

        builder.Append('"');
    }

    /// <summary>The UTF-16 environment block <c>CreateProcess</c> takes: <c>NAME=value</c> strings sorted by name, each ended by a NUL, and one more NUL.</summary>
    internal static string EnvironmentBlock(IDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        foreach (var name in environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (environment[name] is { } value)
            {
                builder.Append(name).Append('=').Append(value).Append('\0');
            }
        }

        // An empty block still needs its two terminators.
        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        return builder.Append('\0').ToString();
    }

    private static void ClosePseudoConsoleAndWait(IntPtr pseudoConsole) => _ = StartClosing(pseudoConsole).Join(CloseWait);

    private static Thread StartClosing(IntPtr pseudoConsole)
    {
        // On the builds where closing waits for the output pipe to be drained, a close made while nothing reads it never returns. The reader
        // is draining, so this ends; the thread is a background thread so that a build where it does not cannot keep the app from exiting.
        var closer = new Thread(() => ClosePseudoConsole(pseudoConsole)) { IsBackground = true, Name = "pseudo-console-close" };
        closer.Start();
        return closer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
