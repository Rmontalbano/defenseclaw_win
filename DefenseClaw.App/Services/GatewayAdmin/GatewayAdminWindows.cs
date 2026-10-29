using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>The invoking user, their profile and Program Files, from Windows (never from settings or the environment's USERPROFILE).</summary>
internal sealed class WindowsAdminLayoutSource : IAdminLayoutSource
{
    private readonly Lazy<AdminLayout?> _layout = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public AdminLayout? Get() => _layout.Value;

    private static AdminLayout? Read()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            return sid is null
                ? null
                : AdminLayout.Create(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    sid,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>An isolated composition (tests) has no administrator setup to look at, so it looks at nothing.</summary>
internal sealed class NoAdminLayoutSource : IAdminLayoutSource
{
    public AdminLayout? Get() => null;
}

/// <summary>The app's own token, through the probe the Plane C card already uses (<see cref="WindowsPlaneCProbes"/>).</summary>
internal sealed class WindowsAdminAccountProbe : IAdminAccountProbe
{
    private readonly IPlaneCProbes _probes;

    public WindowsAdminAccountProbe(IPlaneCProbes? probes = null)
    {
        _probes = probes ?? new WindowsPlaneCProbes();
    }

    public AdminAccountKind Read()
    {
        var reading = _probes.ReadElevation();
        if (!reading.Read)
        {
            return AdminAccountKind.Unknown;
        }

        return reading.Value switch
        {
            TokenElevation.Limited => AdminAccountKind.SplitTokenAdministrator,
            TokenElevation.Full => AdminAccountKind.AlreadyElevated,
            TokenElevation.Standard => AdminAccountKind.StandardUser,
            _ => AdminAccountKind.Unknown,
        };
    }
}

/// <summary>Is a process elevated: its token's <c>TokenElevation</c>, through a limited handle. Null when Windows refuses (then the image path decides).</summary>
internal sealed class WindowsProcessElevationProbe : IProcessElevationProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    public bool? IsElevated(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                var buffer = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    return GetTokenInformation(token, TokenElevationClass, buffer, sizeof(int), out _)
                        ? Marshal.ReadInt32(buffer) != 0
                        : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}

/// <summary>The helper the release publishes next to the app: <c>DefenseClaw.GatewayAdmin.exe</c> in the app's own folder, hashed.</summary>
internal sealed class FileHelperStage : IHelperStage
{
    private readonly string? _directory;
    private (string Path, long Length, DateTime LastWrite, string Sha256)? _cached;

    public FileHelperStage(string? directory = null)
    {
        _directory = directory ?? Path.GetDirectoryName(Environment.ProcessPath);
    }

    public StagedHelper? Locate(out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(_directory))
        {
            problem = "The folder this app runs from is unknown.";
            return null;
        }

        var path = Path.Combine(_directory, AdminLayout.HelperFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                problem = $"{AdminLayout.HelperFileName} is not next to the app ({_directory}). Download it from the same release as the app and put it in that folder.";
                return null;
            }

            if (_cached is { } cached && cached.Path == path && cached.Length == info.Length && cached.LastWrite == info.LastWriteTimeUtc)
            {
                return new StagedHelper(path, cached.Sha256);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Hex.HashOf(stream);
            _cached = (path, info.Length, info.LastWriteTimeUtc, sha);
            return new StagedHelper(path, sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{AdminLayout.HelperFileName} could not be read: {ex.Message}";
            return null;
        }
    }
}

/// <summary>Writes the manifest into <c>%LOCALAPPDATA%\DefenseClaw.App\admin-gateway</c> (the user's own folder; the elevated helper only reads it).</summary>
internal sealed class FileManifestWriter : IManifestWriter
{
    public void Write(AdminLayout layout, ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(layout.ManifestDirectory);
        if ((new DirectoryInfo(layout.ManifestDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The manifest folder is a link; it is not written through one.");
        }

        var temp = layout.ManifestPath + ".tmp";
        File.WriteAllText(temp, manifest.ToJson());
        File.Move(temp, layout.ManifestPath, overwrite: true);
    }
}

/// <summary>
/// The one place the app raises a UAC prompt: <c>ShellExecuteEx</c> with the <c>runas</c> verb on the helper file, with the verb as the only
/// parameter. For the first run the file is opened with read-only sharing, hashed against what the operator was shown, and kept open until the
/// helper exits, so it cannot be replaced between the check and the start.
/// </summary>
internal sealed class ShellExecuteElevator : IAdminElevator
{
    /// <summary>The Mac helper's budget for one request.</summary>
    public static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(330);

    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int ErrorCancelled = 1223;
    private const int SwHide = 0;

    public Task<ElevationOutcome> RunAsync(ElevationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Run(request, cancellationToken), cancellationToken);
    }

    private static ElevationOutcome Run(ElevationRequest request, CancellationToken cancellationToken)
    {
        FileStream? held = null;
        try
        {
            if (request.HoldAndVerifySha256 is { } expected)
            {
                held = new FileStream(request.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!string.Equals(Hex.HashOf(held), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return ElevationOutcome.Failed("the helper next to the app is not the file you reviewed");
                }
            }

            var info = new ShellExecuteInfo
            {
                cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
                fMask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi,
                lpVerb = "runas",
                lpFile = request.Executable,
                lpParameters = AdminVerbs.Name(request.Verb),
                lpDirectory = Path.GetDirectoryName(request.Executable),
                nShow = SwHide,
            };

            if (!ShellExecuteEx(ref info))
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorCancelled ? ElevationOutcome.Cancelled : ElevationOutcome.Failed(new Win32Exception(error).Message);
            }

            if (info.hProcess == IntPtr.Zero)
            {
                return ElevationOutcome.Failed("Windows did not return a process to wait for");
            }

            try
            {
                var waited = WaitForSingleObject(info.hProcess, (uint)WaitBudget.TotalMilliseconds);
                if (waited != 0)
                {
                    return ElevationOutcome.Failed("the helper did not finish in time");
                }

                return GetExitCodeProcess(info.hProcess, out var code)
                    ? ElevationOutcome.Completed(unchecked((int)code))
                    : ElevationOutcome.Failed("the helper's exit code could not be read");
            }
            finally
            {
                _ = CloseHandle(info.hProcess);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ElevationOutcome.Failed(ex.Message);
        }
        finally
        {
            held?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
