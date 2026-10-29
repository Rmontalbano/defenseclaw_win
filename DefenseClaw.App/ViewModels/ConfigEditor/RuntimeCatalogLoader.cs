using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>One start of the runtime's Python: the interpreter, its argv (never a shell), where it runs, what changes in its environment, and how long it may take.</summary>
/// <param name="Environment">Variables to set, or (a null value) to remove, on top of this app's environment with its secrets stripped.</param>
public sealed record RuntimePythonLaunch(
    string PythonPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout);

/// <summary>What one run printed on stdout and how it ended: an exit code, or (with no exit code) why it was stopped or never started.</summary>
public sealed record RuntimePythonOutput(int? ExitCode, string StandardOutput, string? Failure)
{
    public static RuntimePythonOutput Fail(string reason) => new(null, string.Empty, reason);
}

/// <summary>The seam that starts the runtime's Python. Production is <see cref="RuntimeCatalogLoader.RunProcessAsync"/>; a test hands back a fixture dump instead.</summary>
public delegate Task<RuntimePythonOutput> RuntimePythonRunner(RuntimePythonLaunch launch, CancellationToken cancellationToken);

/// <summary>What asking for the catalog came to: the catalog, or the one-line reason there is none (the editor then keeps its built-in, source-driven list).</summary>
public sealed record RuntimeCatalogResult(RuntimeConfigCatalog? Catalog, string? UnavailableReason, string? PythonPath = null)
{
    public static RuntimeCatalogResult Unavailable(string reason, string? python = null) => new(null, reason, python);
}

/// <summary>Everything a catalog read depends on, taken by the view-model when it asks.</summary>
/// <param name="RuntimeVersion">The probed runtime's version (part of the cache key; null while it is unknown).</param>
/// <param name="WorkingDirectory">Where the interpreter runs: the CLI's empty neutral folder, never the data directory.</param>
public sealed record RuntimeCatalogRequest(DefenseClawPaths Paths, InstallationContext Installation, string? RuntimeVersion, string WorkingDirectory);

/// <summary>
/// Reads the runtime's own config catalog (<see cref="RuntimeConfigCatalog"/>) through the interpreter the selected CLI runs on, read-only, and remembers the
/// answer for as long as the runtime and config.yaml stay the same (CUST-391; the Mac's <c>DynamicConfigCatalog.load</c> over <c>CLIRunner.locateRuntimePython</c>).
/// <para>
/// <b>Which Python, per Windows layout</b> (all verified in the upstream source at 1.0.4, never by running anything):
/// <list type="bullet">
/// <item><description><b>install.ps1 of 1.0.x</b> (<c>scripts/install.ps1</c>): a uv-created virtual environment, <c>&lt;DEFENSECLAW_HOME or %USERPROFILE%\.defenseclaw&gt;\.venv</c>
/// (Python 3.12, else 3.11-3.13, <c>:1962-1964</c>), and in <c>%USERPROFILE%\.local\bin</c> a copy of the venv's console-script <c>defenseclaw.exe</c> plus
/// <c>.cmd</c> shims (<c>:93-123</c>). The CLI's folder has no Python, so a CLI found in one of the install-script folders
/// (<see cref="DefenseClawPaths.FallbackBinDirectories"/>) uses <see cref="DefenseClawPaths.VenvPythonPath"/>. No embedded runtime, no frozen executable.</description></item>
/// <item><description><b>The Setup exe of 0.8.7-0.8.10</b> ("the per-user DefenseClaw Setup of 0.8.7-0.8.10, which an upgrade replaces", install.ps1 <c>:100</c>):
/// <c>%LOCALAPPDATA%\Programs\DefenseClaw\bin\defenseclaw.exe</c> with an embedded CPython beside the program folder, <c>runtime\python\python.exe</c>
/// (<see cref="DefenseClawPaths.SetupPythonPath"/>); its <c>._pth</c> names <c>Lib\site-packages</c> (<c>build-windows-installer.ps1:1084-1088</c>).</description></item>
/// <item><description><b>A native launcher</b> (<c>cmd/defenseclaw-launcher/main.go:34-41, 76</c>, the same at 0.8.10 and 1.0.4): <c>bin\defenseclaw.exe</c> runs <c>&lt;install root&gt;\runtime\python\python.exe -I -c …</c>,
/// so the interpreter is always <c>..\runtime\python\python.exe</c> from the CLI. That is the Setup layout above and, when the managed (Secure Client or
/// standalone) layout ships that launcher, <c>&lt;Program Files&gt;\Cisco\…\DefenseClaw\runtime\python\python.exe</c>; a managed layout without one (its scanner
/// runtime is content-addressed, <c>internal/managed/trust_windows.go:204-208</c>) has no interpreter this app can name, and the editor keeps the built-in list.</description></item>
/// <item><description><b>A virtual environment on PATH</b> (a developer install): <c>Scripts\defenseclaw.exe</c> has <c>python.exe</c> beside it.</description></item>
/// <item><description><b>The developer selector's container</b>: the runtime's Python is inside the container; nothing is started.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>How it runs.</b> <c>python.exe -I -B -c &lt;dump&gt;</c> (<see cref="Arguments"/>): isolated (no <c>PYTHON*</c> variables, no user site, no script folder on
/// <c>sys.path</c>, the launcher's own flag), no bytecode written. An argv array with no shell, in the CLI's empty neutral folder, with this app's
/// environment stripped of secret-looking variables (<see cref="ChildEnvironment"/>) and given only the installation's identity
/// (<see cref="InstallationContext.ProtectedEnvironment"/>, as every DefenseClaw child gets), a timeout, a kill-on-close job and a cap on what it may print.
/// Nothing is recorded in Activity: like the runtime probes, it is a read the operator did not ask for by name. It never writes config.yaml.
/// </para>
/// </summary>
public sealed class RuntimeCatalogLoader
{
    /// <summary>How long the dump may take: a cold Python start that imports the TUI's setup panel is a few seconds; a wedged one is stopped.</summary>
    public static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most stdout the dump may print (1.0.4's catalog is about 60 KiB); past it the run is stopped and there is no catalog.</summary>
    internal const int MaxOutputChars = 8 * 1024 * 1024;

    /// <summary>The variables the native launcher drops before it starts the interpreter (<c>launcherEnvFromBase</c>, <c>main.go:108</c>); <c>-I</c> ignores them anyway.</summary>
    private static readonly string[] PythonVariables = { "PYTHONHOME", "PYTHONPATH" };

    private readonly RuntimePythonRunner _runner;
    private readonly Func<string, bool> _fileExists;
    // Written by whichever read finishes last (a load and a rebase can overlap); a reference swap, read once per call.
    private volatile CachedRead? _cached;
    private int _runCount;

    /// <param name="runner">What starts the interpreter; <see cref="RunProcessAsync"/> in the app.</param>
    /// <param name="fileExists">Filesystem probe for the interpreter candidates; overridable for tests.</param>
    public RuntimeCatalogLoader(RuntimePythonRunner runner, Func<string, bool>? fileExists = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary><c>-I -B -c &lt;dump&gt;</c>: the whole argv after the interpreter.</summary>
    public static IReadOnlyList<string> Arguments { get; } = new[] { "-I", "-B", "-c", RuntimeConfigCatalog.DumpScript };

    /// <summary>How many times this loader started the interpreter (a cached answer starts nothing).</summary>
    public int RunCount => Volatile.Read(ref _runCount);

    /// <summary>
    /// The interpreters that belong to the CLI at <paramref name="cliPath"/>, in the order they are tried: its own folder's <c>python.exe</c> (a venv's
    /// <c>Scripts</c>), the native launcher's <c>..\runtime\python\python.exe</c>, and for a CLI in an install-script folder the data directory's venv. Empty
    /// when there is no CLI or the runtime is a container. See the class remarks for the layouts.
    /// </summary>
    public static IReadOnlyList<string> PythonCandidates(string? cliPath, DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrWhiteSpace(cliPath) || paths.Runtime.Kind == RuntimeKind.Container || Path.GetDirectoryName(cliPath) is not { Length: > 0 } folder)
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string> { Path.Combine(folder, "python.exe") };
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } root)
        {
            candidates.Add(Path.Combine(root, "runtime", "python", "python.exe"));
        }

        if (paths.FallbackBinDirectories.Any(dir => SameFolder(dir, folder)))
        {
            candidates.Add(paths.VenvPythonPath);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The environment changes for the dump: <c>PYTHONHOME</c>/<c>PYTHONPATH</c> removed, the developer selector's home for a side-by-side CLI, and the
    /// installation's pinned <c>DEFENSECLAW_HOME</c>, <c>DEFENSECLAW_CONFIG</c> and <c>DEFENSECLAW_DEPLOYMENT_MODE</c>, set last (what the CLI runner gives every
    /// DefenseClaw child), so the runtime reads the same config.yaml the editor shows. A variable the installation does not pin keeps this app's value.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> EnvironmentFor(DefenseClawPaths paths, InstallationContext installation)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(installation);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PythonVariables)
        {
            environment[name] = null;
        }

        if (paths.Runtime is { Kind: RuntimeKind.Cli, HomeDirectory: { } home })
        {
            environment[DefenseClawPaths.HomeVariableName] = home;
        }

        foreach (var (name, pinned) in installation.ProtectedEnvironment())
        {
            if (pinned is not null)
            {
                environment[name] = pinned;
            }
        }

        return environment;
    }

    /// <summary>
    /// The catalog for <paramref name="request"/>: from memory when the interpreter, the runtime (its version and its CLI file) and config.yaml (its time and
    /// length) are what they were at the last read, else read now. <paramref name="force"/> (the Reload button) reads again whatever is remembered. Never
    /// throws: every failure is a <see cref="RuntimeCatalogResult.UnavailableReason"/>.
    /// </summary>
    public async Task<RuntimeCatalogResult> LoadAsync(RuntimeCatalogRequest request, bool force, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (request.Paths.Runtime.Kind == RuntimeKind.Container)
            {
                return RuntimeCatalogResult.Unavailable("the runtime runs in a container, whose Python this app does not start");
            }

            var cli = await request.Paths.FindExecutableAsync(DefenseClawPaths.CliExecutableName).ConfigureAwait(false);
            if (cli is null)
            {
                return RuntimeCatalogResult.Unavailable("the DefenseClaw CLI was not found");
            }

            var python = PythonCandidates(cli, request.Paths).FirstOrDefault(_fileExists);
            if (python is null)
            {
                return RuntimeCatalogResult.Unavailable("no runtime Python was found for the DefenseClaw CLI at " + cli);
            }

            var key = new CacheKey(python, Stamp(python), request.RuntimeVersion, cli, Stamp(cli), request.Paths.ConfigFilePath, Stamp(request.Paths.ConfigFilePath));
            if (!force && _cached is { } cached && cached.Key == key)
            {
                return cached.Result;
            }

            var launch = new RuntimePythonLaunch(python, Arguments, request.WorkingDirectory, EnvironmentFor(request.Paths, request.Installation), DumpTimeout);
            _ = Interlocked.Increment(ref _runCount);
            var result = Interpret(await _runner(launch, cancellationToken).ConfigureAwait(false), python);
            _cached = new CachedRead(key, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return RuntimeCatalogResult.Unavailable("the catalog read was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return RuntimeCatalogResult.Unavailable("the runtime's Python could not be asked: " + ex.Message);
        }
    }

    /// <summary>What a finished run means. The reasons never quote what the dump printed: a traceback can carry config values.</summary>
    internal static RuntimeCatalogResult Interpret(RuntimePythonOutput output, string python)
    {
        if (output.Failure is { Length: > 0 } failure)
        {
            return RuntimeCatalogResult.Unavailable("the catalog dump did not finish (" + failure + ")", python);
        }

        if (output.ExitCode is not 0)
        {
            return RuntimeCatalogResult.Unavailable($"the catalog dump exited {output.ExitCode} (this runtime may not have the catalog this app reads)", python);
        }

        return RuntimeConfigCatalog.Parse(output.StandardOutput) is { } catalog
            ? new RuntimeCatalogResult(catalog, null, python)
            : RuntimeCatalogResult.Unavailable("the runtime printed no catalog this app can read", python);
    }

    /// <summary>
    /// The production runner: starts the interpreter with <see cref="ProcessStartInfo.ArgumentList"/> and no shell, in <see cref="RuntimePythonLaunch.WorkingDirectory"/>,
    /// with this app's environment minus every secret-looking variable plus the launch's changes, stdin closed, inside a kill-on-close job; reads stdout up to
    /// <see cref="MaxOutputChars"/> (stderr is drained and dropped), and kills the whole tree on the timeout, on cancellation or past the cap.
    /// </summary>
    public static async Task<RuntimePythonOutput> RunProcessAsync(RuntimePythonLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // The CLI's own empty folder (created here, at the start, as the CLI runner does), never the data directory; the temp folder if it cannot be made.
            WorkingDirectory = CliWorkingDirectory.TryEnsure(launch.WorkingDirectory) ? launch.WorkingDirectory : Path.GetTempPath(),
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = ChildEnvironment.StripSecrets(startInfo);
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                _ = startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        using var job = WindowsJob.TryCreate();
        try
        {
            if (!process.Start())
            {
                return RuntimePythonOutput.Fail("the interpreter could not be started");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return RuntimePythonOutput.Fail("the interpreter could not be started: " + ex.Message);
        }

        _ = job?.TryAssign(process);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(launch.Timeout);
        try
        {
            process.StandardInput.Close();
            var stdout = ReadCappedAsync(process.StandardOutput, MaxOutputChars, limit.Token);
            var stderr = ReadCappedAsync(process.StandardError, 64 * 1024, limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var (text, overflow) = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return overflow
                ? RuntimePythonOutput.Fail($"it printed more than {MaxOutputChars:N0} characters")
                : new RuntimePythonOutput(process.ExitCode, text, null);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return RuntimePythonOutput.Fail(cancellationToken.IsCancellationRequested ? "cancelled" : $"timed out after {launch.Timeout.TotalSeconds:0} s");
        }
        catch (IOException ex)
        {
            Kill(process);
            return RuntimePythonOutput.Fail(ex.Message);
        }
    }

    /// <summary>Reads a stream to its end, keeping at most <paramref name="cap"/> characters; past the cap the rest is read and dropped so the child never blocks on a full pipe.</summary>
    private static async Task<(string Text, bool Overflow)> ReadCappedAsync(StreamReader reader, int cap, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > cap)
            {
                overflow = true;
                continue;
            }

            _ = builder.Append(buffer, 0, read);
        }

        return (builder.ToString(), overflow);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>A file's last write time and length; a missing or unreadable file is (0, -1), which still compares.</summary>
    private static (long Ticks, long Length) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (0, -1);
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private sealed record CacheKey(
        string Python,
        (long Ticks, long Length) PythonStamp,
        string? RuntimeVersion,
        string Cli,
        (long Ticks, long Length) CliStamp,
        string ConfigPath,
        (long Ticks, long Length) ConfigStamp);

    private sealed record CachedRead(CacheKey Key, RuntimeCatalogResult Result);
}
