using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

/// <summary>
/// Sets the default owner of this process's token to the user for the length of a scope (what <c>install.ps1</c> does for the same reason), so a
/// file the elevated gateway creates is owned by the user and not by the Administrators group: DefenseClaw 1.x refuses data its user does not own.
/// A child process takes a copy of the token at creation, so the gateway started inside the scope keeps it for life. The previous owner is put back
/// on exit from the scope, so nothing the helper itself writes afterwards is user-owned.
/// </summary>
internal sealed class WindowsTokenOwner : ITokenOwner
{
    public IDisposable Use(string ownerSid)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery | Native.TokenAdjustDefault, out var token))
        {
            throw new InvalidOperationException("The process token could not be opened (" + Marshal.GetLastWin32Error() + ").");
        }

        try
        {
            var previous = ReadOwnerSid(token);
            SetOwner(token, ownerSid);
            return new Restore(token, previous);
        }
        catch
        {
            _ = Native.CloseHandle(token);
            throw;
        }
    }

    private static string ReadOwnerSid(IntPtr token)
    {
        _ = Native.GetTokenInformation(token, Native.TokenOwner, IntPtr.Zero, 0, out var needed);
        var buffer = Marshal.AllocHGlobal(Math.Max(needed, 64));
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenOwner, buffer, Math.Max(needed, 64), out _))
            {
                throw new InvalidOperationException("The token's owner could not be read (" + Marshal.GetLastWin32Error() + ").");
            }

            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetOwner(IntPtr token, string sid)
    {
        var bytes = new byte[new SecurityIdentifier(sid).BinaryLength];
        new SecurityIdentifier(sid).GetBinaryForm(bytes, 0);
        var sidMemory = Marshal.AllocHGlobal(bytes.Length);
        var info = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.Copy(bytes, 0, sidMemory, bytes.Length);
            Marshal.WriteIntPtr(info, sidMemory);
            if (!Native.SetTokenInformation(token, Native.TokenOwner, info, IntPtr.Size))
            {
                throw new InvalidOperationException("The token's default owner could not be set (" + Marshal.GetLastWin32Error() + ").");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(sidMemory);
        }
    }

    private sealed class Restore(IntPtr token, string previous) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                SetOwner(token, previous);
            }
            catch (InvalidOperationException)
            {
                // The process is about to end; a token left with the user as default owner is not used for anything after this.
            }
            finally
            {
                _ = Native.CloseHandle(token);
            }
        }
    }
}

/// <summary>Runs one program with exactly the given argument list and environment, and waits for it with a bounded wait and bounded output.</summary>
internal sealed class WindowsProcessRunner : IProcessRunner
{
    public ProcessOutcome Run(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in request.Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessOutcome(false, null, false, Array.Empty<string>(), ex.Message);
        }

        using (process)
        {
            process.StandardInput.Close();
            var budget = new OutputBudget(request.MaxOutputBytes);
            var readers = new[] { Pump(process.StandardOutput, budget), Pump(process.StandardError, budget) };

            var finished = process.WaitForExit((int)Math.Min(request.Timeout.TotalMilliseconds, int.MaxValue));
            if (!finished)
            {
                TryKill(process);
                return new ProcessOutcome(true, null, true, budget.Lines, null);
            }

            // The gateway daemonizes itself; if its child kept our pipes open the readers would not end, so they get a moment, not forever.
            _ = Task.WaitAll(readers, TimeSpan.FromSeconds(3));
            return new ProcessOutcome(true, process.ExitCode, false, budget.Lines, null);
        }
    }

    private static Task Pump(StreamReader reader, OutputBudget budget) => Task.Run(() =>
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                budget.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed under us.
        }
    });

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private sealed class OutputBudget(int maxBytes)
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();
        private int _bytes;
        private bool _truncated;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    var copy = _lines.ToList();
                    if (_truncated)
                    {
                        copy.Add("(output stopped at its safe limit)");
                    }

                    return copy;
                }
            }
        }

        public void Add(string line)
        {
            lock (_gate)
            {
                if (_bytes + line.Length > maxBytes)
                {
                    _truncated = true;
                    return;
                }

                _bytes += line.Length + 1;
                _lines.Add(line);
            }
        }
    }
}

/// <summary>The helper's own yes/no: a topmost system-modal message box in the elevated process, which a program running at the user's lower integrity level cannot click (UIPI).</summary>
internal sealed class MessageBoxConsent : IConsent
{
    private const uint YesNo = 0x4;
    private const uint IconWarning = 0x30;
    private const uint DefaultNo = 0x100;
    private const uint SetForeground = 0x10000;
    private const uint TopMost = 0x40000;
    private const int IdYes = 6;

    public bool Confirm(string title, string text) =>
        Native.MessageBoxW(IntPtr.Zero, text, title, YesNo | IconWarning | DefaultNo | SetForeground | TopMost) == IdYes;
}

/// <summary>Is a gateway running: <c>gateway.pid</c> in the data folder names a live process.</summary>
internal sealed class PidFileGatewayProbe : IRunningGateway
{
    public bool IsRunning(AdminLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var path = Path.Combine(layout.DataDirectory, "gateway.pid");
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            int pid;
            if (text.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(text);
                if (!document.RootElement.TryGetProperty("pid", out var value) || !value.TryGetInt32(out pid))
                {
                    return false;
                }
            }
            else if (!int.TryParse(text.Trim(), out pid))
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName, "defenseclaw-gateway", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
