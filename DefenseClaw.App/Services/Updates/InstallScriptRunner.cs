using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Updates;

/// <summary>Everything needed to start the installer in a console of its own. The two flags are part of the contract the tests pin.</summary>
/// <param name="Executable">Absolute path of <c>powershell.exe</c>.</param>
/// <param name="Argv">The arguments after the executable (see <see cref="InstallScriptPlan.BuildArgv"/>).</param>
/// <param name="WorkingDirectory">The staging directory.</param>
/// <param name="Environment">The child's whole environment, already stripped of secrets.</param>
/// <param name="NewConsole">Start in a new, visible console window (<c>CREATE_NEW_CONSOLE</c>): the installer asks its questions there.</param>
/// <param name="BreakAwayFromJob">Leave any job object this app is in (<c>CREATE_BREAKAWAY_FROM_JOB</c>): closing or killing the app must not kill the installer.</param>
public sealed record InstallerLaunchRequest(
    string Executable,
    IReadOnlyList<string> Argv,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    bool NewConsole,
    bool BreakAwayFromJob);

/// <summary>A started installer process.</summary>
public interface IInstallerProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>Completes with the exit code when the process ends; null when it could not be read. Never kills the process.</summary>
    Task<int?> WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Starts the installer. A seam: tests record the request and start nothing.</summary>
public interface IInstallerLauncher
{
    /// <exception cref="Win32Exception">The process could not be started.</exception>
    IInstallerProcess Start(InstallerLaunchRequest request);
}

/// <summary>
/// What the app does around the installer: quiet down before it starts (pause monitoring, hand back the SQLite handles on the data directory's
/// databases so the installer can move audit.db) and resume afterwards.
/// </summary>
public interface IUpgradeSession
{
    /// <summary>Called once, immediately before the installer is started.</summary>
    void Begin();

    /// <summary>Called once after the installer has exited, or when it could not be started.</summary>
    void End();
}

/// <summary>One request to run install.ps1 (an upgrade, or with <see cref="Rollback"/> its rollback).</summary>
public sealed record InstallScriptRunRequest(
    string StagedScriptPath,
    string ExpectedSha256,
    string Version,
    bool Yes,
    bool Rollback,
    string DataDirectory);

/// <summary>How a run ended.</summary>
public sealed record InstallScriptRunResult
{
    /// <summary>True when the installer process was started (it may still have failed).</summary>
    public bool Started { get; init; }

    public int? ExitCode { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The newest <c>install-*.log</c> written after the start, when there is one.</summary>
    public string? LogPath { get; init; }

    /// <summary>Why nothing ran or the run could not be followed; null for a run that started and ended.</summary>
    public string? FailureReason { get; init; }

    public bool Rollback { get; init; }

    public InstallExitInfo Exit { get; init; } = InstallScriptPlan.MapExit(null);

    public CliInvocation? Invocation { get; init; }
}

/// <summary>
/// Runs the staged <c>install.ps1</c> (CUST-346). It never starts on its own and never twice at once. Before it starts the installer it re-hashes the
/// staged file, pauses monitoring and releases the app's database handles (<see cref="IUpgradeSession"/>); it starts <c>powershell.exe</c> in a new
/// visible console that survives this app (<see cref="InstallerLaunchRequest"/>), with an environment stripped of secrets
/// (<see cref="ChildEnvironment"/>); it records the run in Activity (start, exit code, duration, the log's path); and it resumes the app afterwards.
/// The run is never cancelled from here: stopping an installer half-way is what the installer's own recovery exists for.
/// </summary>
public sealed class InstallScriptRunner
{
    private readonly CliRunner _cli;
    private readonly IInstallerLauncher _launcher;
    private readonly IUpgradeSession _session;
    private readonly TimeProvider _time;
    private readonly Func<IDictionary<string, string?>> _environment;
    private int _running;

    public InstallScriptRunner(
        CliRunner cli,
        IInstallerLauncher launcher,
        IUpgradeSession session,
        TimeProvider? time = null,
        Func<IDictionary<string, string?>>? environment = null)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _time = time ?? TimeProvider.System;
        _environment = environment ?? CurrentEnvironment;
    }

    /// <summary>True from the moment a run is accepted until it has ended and the app has resumed.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Raised on the thread that started the run when the process exists, with its id.</summary>
    public event EventHandler<int>? ProcessStarted;

    /// <summary>The process environment as a mutable dictionary (names case-insensitive, as Windows treats them).</summary>
    private static IDictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                result[name] = entry.Value as string;
            }
        }

        return result;
    }

    public async Task<InstallScriptRunResult> RunAsync(InstallScriptRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Refused(request, "An install.ps1 run is already in flight.");
        }

        try
        {
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<InstallScriptRunResult> RunCoreAsync(InstallScriptRunRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> argv;
        try
        {
            argv = request.Rollback
                ? InstallScriptPlan.BuildRollbackArgv(request.StagedScriptPath, request.Yes)
                : InstallScriptPlan.BuildArgv(request.StagedScriptPath, request.Version, request.Yes);
        }
        catch (ArgumentException ex)
        {
            return Refused(request, ex.Message);
        }

        if (!File.Exists(request.StagedScriptPath))
        {
            return Refused(request, $"The staged install.ps1 is no longer at {request.StagedScriptPath}. Download and verify it again.");
        }

        // Hashed through a handle that stays open until the process exists, so the file that was checked is the file that starts.
        FileStream? held = null;
        void Release() => Interlocked.Exchange(ref held, null)?.Dispose();
        try
        {
            string actual;
            try
            {
                held = new FileStream(request.StagedScriptPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                actual = Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refused(request, $"The staged install.ps1 could not be re-read: {ex.Message}");
            }

            if (!string.Equals(actual, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Refused(
                    request,
                    $"The staged install.ps1 changed on disk since it was verified (now {actual}, was {request.ExpectedSha256}). Nothing was run. Download and verify it again.");
            }

            var environment = _environment();
            _ = ChildEnvironment.StripSecrets(environment);

            var executable = UpgradeRunner.ResolvePowerShellPath();
            var launch = new InstallerLaunchRequest(
                executable,
                argv,
                Path.GetDirectoryName(Path.GetFullPath(request.StagedScriptPath)) ?? Environment.CurrentDirectory,
                new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase),
                NewConsole: true,
                BreakAwayFromJob: true);

            var started = _time.GetUtcNow();
            var clock = _time.GetTimestamp();

            CliInvocation invocation;
            try
            {
                invocation = _cli.BeginExternalRun(
                    executable,
                    argv,
                    (request.Rollback ? "Rolling back" : "Upgrading to " + request.Version) +
                    ": install.ps1 runs in its own console window. Its prompts and output are there; its transcript goes to ~\\.defenseclaw\\logs.");
            }
            catch (SecretInArgumentException ex)
            {
                return Refused(request, ex.Message);
            }

            _session.Begin();
            IInstallerProcess? process = null;
            try
            {
                try
                {
                    process = _launcher.Start(launch);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    _cli.CompleteExternalRun(invocation, null, "not started - " + ex.Message, null);
                    return new InstallScriptRunResult
                    {
                        Rollback = request.Rollback,
                        FailureReason = "The installer could not be started: " + ex.Message,
                        Exit = new InstallExitInfo(InstallOutcome.CouldNotStart, "Not started", "Bad", "powershell.exe could not be started: " + ex.Message, ChangedMachine: false, OffersRollback: false),
                        Invocation = invocation,
                    };
                }
                finally
                {
                    Release();
                }

                using (process)
                {
                    ProcessStarted?.Invoke(this, process.ProcessId);

                    // CancellationToken.None on purpose: nothing here ends an installer that is half-way through replacing the install.
                    var exit = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    var duration = _time.GetElapsedTime(clock);
                    var info = InstallScriptPlan.MapExit(exit, request.Rollback);
                    var log = InstallScriptPlan.FindInstallLog(request.DataDirectory, started);

                    _cli.CompleteExternalRun(
                        invocation,
                        exit,
                        exit is null ? "the exit code could not be read" : null,
                        $"install.ps1 exited {(exit is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(unknown)")} after " +
                        $"{duration.TotalSeconds:0} s. " + (log is null ? "No install log was found." : "Log: " + log));

                    return new InstallScriptRunResult
                    {
                        Started = true,
                        Rollback = request.Rollback,
                        ExitCode = exit,
                        Duration = duration,
                        LogPath = log,
                        Exit = info,
                        Invocation = invocation,
                    };
                }
            }
            finally
            {
                if (invocation.FinishedAt is null)
                {
                    _cli.CompleteExternalRun(invocation, null, "ended without a result", null);
                }

                _session.End();
            }
        }
        finally
        {
            Release();
        }
    }

    private InstallScriptRunResult Refused(InstallScriptRunRequest request, string reason) => new()
    {
        Rollback = request.Rollback,
        FailureReason = reason,
        Exit = new InstallExitInfo(InstallOutcome.CouldNotStart, "Not started", "Warn", reason + " Nothing was run and nothing was changed.", ChangedMachine: false, OffersRollback: false),
    };
}

/// <summary>Quoting for a Windows command line (the rules <c>CommandLineToArgvW</c> reads back).</summary>
public static class WindowsCommandLine
{
    /// <summary>One argument as it must appear in a command line so the child reads it back as exactly that string.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote would otherwise escape it.
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>The executable then each argument, quoted.</summary>
    public static string Build(string executable, IEnumerable<string> argv) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(argv.Select(Quote)));
}

/// <summary>
/// The real launcher: <c>CreateProcessW</c> with <c>CREATE_NEW_CONSOLE</c> and <c>CREATE_BREAKAWAY_FROM_JOB</c>, because <see cref="Process"/> offers
/// neither. If the job this app is in does not allow breakaway the call fails with access denied; the launcher then starts it without the flag
/// (a new console still keeps it alive when this window closes) and says so through <see cref="BreakawayRefused"/>.
/// </summary>
public sealed class ConsoleInstallerLauncher : IInstallerLauncher
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;
    private const uint WaitTimeout = 0x00000102;

    /// <summary>True once a start had to drop the breakaway flag because the app's job forbids it.</summary>
    public bool BreakawayRefused { get; private set; }

    public IInstallerProcess Start(InstallerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var flags = CreateUnicodeEnvironment | (request.NewConsole ? CreateNewConsole : 0);
        var commandLine = WindowsCommandLine.Build(request.Executable, request.Argv);
        var block = EnvironmentBlock(request.Environment);

        var environment = Marshal.StringToHGlobalUni(block);
        try
        {
            if (!TryCreate(commandLine, flags | (request.BreakAwayFromJob ? CreateBreakawayFromJob : 0), environment, request.WorkingDirectory, out var info))
            {
                var error = Marshal.GetLastWin32Error();
                if (!request.BreakAwayFromJob || error != ErrorAccessDenied)
                {
                    throw new Win32Exception(error);
                }

                BreakawayRefused = true;
                if (!TryCreate(commandLine, flags, environment, request.WorkingDirectory, out info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            _ = CloseHandle(info.ThreadHandle);
            return new InstallerProcess(info.ProcessHandle, info.ProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    private static bool TryCreate(string commandLine, uint flags, IntPtr environment, string workingDirectory, out ProcessInformation info)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var line = new StringBuilder(commandLine, Math.Max(commandLine.Length + 1, 32768));
        return CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, flags, environment, string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref startup, out info);
    }

    /// <summary>The UTF-16 block <c>CreateProcess</c> takes: <c>NAME=value</c> strings sorted by name, each ended by a NUL, and one more NUL.</summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        foreach (var name in environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (environment[name] is { } value)
            {
                builder.Append(name).Append('=').Append(value).Append('\0');
            }
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        return builder.Append('\0').ToString();
    }

    private sealed class InstallerProcess : IInstallerProcess
    {
        private IntPtr _handle;

        public InstallerProcess(IntPtr handle, int processId)
        {
            _handle = handle;
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public Task<int?> WaitForExitAsync(CancellationToken cancellationToken)
        {
            var handle = _handle;
            return Task.Run(
                () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (WaitForSingleObject(handle, 1000) == WaitTimeout)
                        {
                            continue;
                        }

                        return GetExitCodeProcess(handle, out var code) ? (int?)unchecked((int)code) : null;
                    }

                    return null;
                },
                CancellationToken.None);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                _ = CloseHandle(handle);
            }
        }
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
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// The production <see cref="IUpgradeSession"/>: pauses monitoring (only if it was running; the operator's own pause is left alone) and gives back
/// every handle this app keeps on the data directory's databases, for the installer to move. Resumes monitoring at the end. Best-effort throughout:
/// a handle that cannot be released is the installer's to report.
/// </summary>
internal sealed class AppUpgradeSession : IUpgradeSession
{
    private readonly AppServices _services;
    private bool _pausedByUs;

    public AppUpgradeSession(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public void Begin()
    {
        if (!_services.Monitor.IsPaused)
        {
            _pausedByUs = _services.Monitor.SetPaused(true) || _services.Monitor.IsPaused;
        }

        ReleaseDatabaseHandles(_services);
    }

    public void End()
    {
        if (_pausedByUs)
        {
            _pausedByUs = false;
            _ = _services.Monitor.SetPaused(false);
        }
    }

    /// <summary>
    /// Closes the audit change probe's kept connection and clears the connection pool of every <c>*.db</c> in the data directory (the read-only string
    /// every reader uses, and the writable one), file by file: another file's pool is not touched.
    /// </summary>
    internal static void ReleaseDatabaseHandles(AppServices services)
    {
        services.AuditChanges.Release();

        try
        {
            var directory = services.Paths.DataDirectory;
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
            {
                foreach (var connectionString in new[]
                         {
                             DefenseClaw.Core.Audit.AuditReader.BuildReadOnlyConnectionString(file),
                             new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString(),
                         })
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("upgrade: could not release every database handle: " + ex.GetType().Name);
        }
    }
}
