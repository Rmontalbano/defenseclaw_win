using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>Where Docker stands on this machine, as far as a read-only look can tell.</summary>
public enum DockerState
{
    /// <summary>The probe itself failed; nothing is known. The CLI runs its own preflight, so this does not block.</summary>
    Unknown = 0,

    /// <summary>The engine answered. <see cref="DockerStatus.Warnings"/> may still list what the CLI's certified path would refuse.</summary>
    Ready,

    /// <summary>No <c>docker</c> executable was found.</summary>
    NotInstalled,

    /// <summary>The executable is there but the engine did not answer (Docker Desktop is not running, or it is starting).</summary>
    EngineDown,
}

/// <summary>The answer of one <see cref="IDockerProbe"/> look.</summary>
/// <param name="State">What was found.</param>
/// <param name="Summary">One sentence for the card.</param>
/// <param name="Warnings">
/// Things the installed CLI's own preflight (<c>validate_native_docker_preflight</c>) would refuse on Windows — the WSL 2
/// backend, Windows containers, a per-user Docker Desktop, an edition without Hyper-V. They do not disable the option:
/// the CLI is the authority and refuses before it changes anything.
/// </param>
public sealed record DockerStatus(DockerState State, string Summary, IReadOnlyList<string> Warnings)
{
    public static DockerStatus Checking { get; } = new(DockerState.Unknown, "Checking for Docker…", Array.Empty<string>());

    /// <summary>True when the local-Splunk option may be chosen: anything but a definite "not here" / "not running".</summary>
    public bool AllowsLocalSplunk => State is DockerState.Ready or DockerState.Unknown;
}

/// <summary>
/// A read-only look at Docker. <b>It never starts, pulls or runs anything</b>: the only command it issues is
/// <c>docker info</c>, which asks a running engine to describe itself and does nothing else. Injectable so tests never
/// touch a real Docker.
/// </summary>
public interface IDockerProbe
{
    Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>What a probe's process run produced.</summary>
public sealed record DockerProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// <see cref="IDockerProbe"/> over <c>docker info --format "{{json .}}"</c>. Every dependency on the machine is a
/// delegate, so the logic (and the certification warnings, which mirror the CLI's own checks) is tested with canned
/// answers.
/// </summary>
public sealed class DockerProbe : IDockerProbe
{
    /// <summary>How long the engine gets to answer. A stopped Docker Desktop fails fast; a starting one can hang.</summary>
    public static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<Task<string?>> _locate;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<DockerProcessResult>> _run;
    private readonly Func<string?> _windowsEdition;
    private readonly Func<bool?> _wslEngineEnabled;
    private readonly Func<IReadOnlyList<string>> _userRoots;
    private readonly Func<bool> _isX64;

    public DockerProbe(
        Func<Task<string?>> locate,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<DockerProcessResult>> run,
        Func<string?> windowsEdition,
        Func<bool?> wslEngineEnabled,
        Func<IReadOnlyList<string>> userRoots,
        Func<bool> isX64)
    {
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _windowsEdition = windowsEdition ?? throw new ArgumentNullException(nameof(windowsEdition));
        _wslEngineEnabled = wslEngineEnabled ?? throw new ArgumentNullException(nameof(wslEngineEnabled));
        _userRoots = userRoots ?? throw new ArgumentNullException(nameof(userRoots));
        _isX64 = isX64 ?? throw new ArgumentNullException(nameof(isX64));
    }

    /// <summary>The probe that looks at the real machine: PATH for <c>docker</c>, a child process for <c>docker info</c>.</summary>
    public static DockerProbe CreateDefault(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new DockerProbe(
            () => paths.FindExecutableAsync("docker"),
            RunProcessAsync,
            ReadWindowsEdition,
            ReadWslEngineSetting,
            () => new[] { Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("USERPROFILE") }
                .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToArray(),
            () => RuntimeInformation.OSArchitecture == Architecture.X64);
    }

    public async Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var docker = await _locate().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(docker))
            {
                return new DockerStatus(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>());
            }

            var result = await _run(docker, new[] { "info", "--format", "{{json .}}" }, InfoTimeout, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return new DockerStatus(
                    DockerState.EngineDown,
                    "Docker is installed but its engine did not answer within " + (int)InfoTimeout.TotalSeconds + " seconds. Start Docker Desktop and wait for it to say it is running.",
                    Array.Empty<string>());
            }

            if (result.ExitCode != 0 || !TryParse(result.StandardOutput, out var info))
            {
                var detail = FirstLine(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
                return new DockerStatus(
                    DockerState.EngineDown,
                    "Docker is installed but its engine is not running. Start Docker Desktop and wait for it to say it is running." +
                    (detail.Length > 0 ? " (" + detail + ")" : string.Empty),
                    Array.Empty<string>());
            }

            return new DockerStatus(DockerState.Ready, "Docker is running.", Warnings(docker, info));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A probe that cannot even run says nothing about Docker. The CLI will do its own preflight, so this must not lock the option.
            return new DockerStatus(DockerState.Unknown, "Docker could not be checked from here (" + ex.Message + "). The command checks it again itself.", Array.Empty<string>());
        }
    }

    private sealed record Info(string OsType, string OperatingSystem, string KernelVersion);

    private static bool TryParse(string json, out Info info)
    {
        info = new Info(string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

            // A real engine always reports an OSType; a client-only answer (daemon down) does not.
            info = new Info(Get("OSType"), Get("OperatingSystem"), Get("KernelVersion"));
            return info.OsType.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The CLI's <c>_validate_windows_docker_certification</c> and the Linux-containers check, as sentences.</summary>
    private IReadOnlyList<string> Warnings(string dockerPath, Info info)
    {
        var warnings = new List<string>();

        if (!string.Equals(info.OsType, "linux", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Docker is using Windows containers. Local Splunk needs Linux containers: switch Docker Desktop to Linux containers.");
        }

        if (!_isX64())
        {
            warnings.Add("The CLI certifies native Windows local Splunk only on x64 systems.");
        }

        var edition = _windowsEdition() ?? string.Empty;
        if (edition.Length > 0 && !new[] { "professional", "enterprise", "education" }.Any(t => edition.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"This Windows edition ({edition}) is not Pro, Enterprise or Education, which the CLI's no-WSL path requires (Hyper-V).");
        }

        if (!info.OperatingSystem.Contains("docker desktop", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("The CLI's certified path requires Docker Desktop (this engine does not identify as Docker Desktop).");
        }

        if (_userRoots().Any(root => IsUnder(dockerPath, root)))
        {
            warnings.Add("This looks like a per-user Docker Desktop install; the CLI's certified path requires a machine-wide install.");
        }

        if (info.KernelVersion.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
            info.KernelVersion.Contains("microsoft-standard", StringComparison.OrdinalIgnoreCase) ||
            _wslEngineEnabled() == true)
        {
            warnings.Add("Docker Desktop is using the WSL 2 backend. The CLI's certified Windows path requires the Hyper-V backend and refuses WSL 2.");
        }
        else if (_wslEngineEnabled() is null && !info.KernelVersion.Contains("linuxkit", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Docker Desktop's Hyper-V backend could not be verified; the CLI requires it.");
        }

        return warnings;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    // ------------------------------------------------------------------ the real machine

    private static string? ReadWindowsEdition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Docker Desktop's own <c>wslEngineEnabled</c> setting, read the way the CLI reads it; null when it cannot be read.</summary>
    private static bool? ReadWslEngineSetting()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
        {
            return null;
        }

        foreach (var name in new[] { "settings-store.json", "settings.json" })
        {
            try
            {
                var path = Path.Combine(appData, "Docker", name);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(DefenseClaw.Core.IO.SharedFile.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("wslEngineEnabled", out var value) &&
                    value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Runs <c>docker</c> with an argument list (no shell), a hard timeout, and the whole tree killed when it fires.</summary>
    private static async Task<DockerProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        _ = process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new DockerProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new DockerProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }
}
