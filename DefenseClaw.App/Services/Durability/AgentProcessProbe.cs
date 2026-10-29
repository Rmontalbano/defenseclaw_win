using System.Diagnostics;
using System.IO;

namespace DefenseClaw.App.Services;

/// <summary>
/// Whether Codex or Claude Code is running now: the one process look behind the upgrade checklist's rows and the "Codex is running" warning on
/// every review that rewrites Codex's config.toml (CUST-352). DefenseClaw rewrites that file when the gateway starts, Codex rewrites it too, and
/// a collision leaves a stuck pending write that stops the gateway; the warning exists so the operator can quit Codex first.
/// <para>
/// Read-only: it lists process names, plus the image path of the one process whose name does not say whose it is (see <see cref="IsCodexDesktopMain"/>).
/// The processes come from an injectable list so a test never looks at the real machine.
/// </para>
/// </summary>
public sealed class AgentProcessProbe
{
    /// <summary>The sentence a review carries while Codex is running.</summary>
    public const string CodexRunningMessage =
        "Codex is running. DefenseClaw rewrites Codex's config.toml when the gateway starts, and Codex rewrites it too; a collision can leave a " +
        "stuck pending write that stops the gateway. Quit Codex first.";

    /// <summary>The heading of the warning bar that carries <see cref="CodexRunningMessage"/>.</summary>
    public const string CodexRunningTitle = "Codex is running";

    private readonly Func<IReadOnlyList<RunningProcess>> _processes;
    private readonly TimeSpan _cache;
    private readonly object _gate = new();
    private IReadOnlyList<RunningProcess> _last = Array.Empty<RunningProcess>();
    private long _lastTick = long.MinValue;

    /// <param name="names">The names (no extension, any case) of the processes running now; none of them carries an image path.</param>
    /// <param name="cache">How long one answer is reused (a review that redraws on every keystroke asks often); zero asks every time.</param>
    public AgentProcessProbe(Func<IReadOnlyList<string>> names, TimeSpan cache = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        _processes = () => names().Select(static name => new RunningProcess(name)).ToArray();
        _cache = cache;
    }

    private AgentProcessProbe(Func<IReadOnlyList<RunningProcess>> processes, TimeSpan cache)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _cache = cache;
    }

    /// <summary>A probe over processes that carry an image path where it matters: the seam for the Codex desktop app's main process.</summary>
    public static AgentProcessProbe FromProcesses(Func<IReadOnlyList<RunningProcess>> processes, TimeSpan cache = default) => new(processes, cache);

    /// <summary>The probe over the real machine. An answer is reused for two seconds.</summary>
    public static AgentProcessProbe Machine { get; } = FromProcesses(RunningProcesses, TimeSpan.FromSeconds(2));

    /// <summary>A probe that sees no process: what an isolated composition (a test, a harness) starts with.</summary>
    public static AgentProcessProbe None { get; } = FromProcesses(static () => Array.Empty<RunningProcess>());

    // codex.exe is the CLI, the app-server that the Codex desktop app runs (resources\codex.exe in its package, or the pinned copy under
    // %LOCALAPPDATA%\OpenAI\Codex\bin\<hash>), and the app's small Codex.exe launcher: the processes that rewrite ~/.codex/config.toml and hold the hooks.
    private const string CodexName = "codex";

    // The Codex desktop app's main (Electron) process. The package manifest of OpenAI.Codex 26.1021 names app/ChatGPT.exe as its entry point.
    private const string CodexDesktopMainName = "ChatGPT";

    // The Store packages of the Codex desktop app (stable and beta), as the runtime's own application catalog names them.
    private static readonly string[] CodexPackagePrefixes = { "OpenAI.Codex_", "OpenAI.CodexBeta_" };

    /// <summary>
    /// True for a Codex agent process name. An allow-list, not a prefix match (CUST-358): only <c>codex</c> (codex.exe, any case).
    /// <para>
    /// Every other <c>codex-*</c> process belongs to the Codex desktop app and neither holds nor rewrites hook configuration: the always-on LocalSystem
    /// service <c>codex-windows-sandbox-service</c> (<c>CodexSandboxService.OpenAI.Codex</c>, auto-start, running with Codex closed);
    /// <c>codex-windows-sandbox-setup</c>, the elevated helper that prepares sandbox users and file permissions once (it is an installer helper, and
    /// nothing in this app or the runtime says it reads or writes <c>config.toml</c> or the hooks, so it is left out on purpose); and
    /// <c>codex-command-runner</c>, <c>codex-code-mode-host</c> and <c>codex-computer-use</c>, which only run under a <c>codex.exe</c> that already counts.
    /// </para>
    /// </summary>
    public static bool IsCodexName(string name) => string.Equals(name, CodexName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the Codex desktop app's main process: <c>ChatGPT.exe</c> running from an <c>OpenAI.Codex_*</c> (or <c>OpenAI.CodexBeta_*</c>) package under
    /// <c>WindowsApps</c>. The name alone is not enough, because the separate ChatGPT desktop app has the same one; a process whose path could not be
    /// read is therefore not counted (the app's <c>codex.exe</c> app-server still is).
    /// </summary>
    public static bool IsCodexDesktopMain(string name, string? imagePath)
    {
        if (!string.Equals(name, CodexDesktopMainName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var segments = imagePath.Split('\\', '/');
        var apps = Array.FindIndex(segments, static s => string.Equals(s, "WindowsApps", StringComparison.OrdinalIgnoreCase));
        return apps >= 0 && apps + 1 < segments.Length &&
               CodexPackagePrefixes.Any(prefix => segments[apps + 1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="process"/> is a Codex agent: <see cref="IsCodexName"/> or <see cref="IsCodexDesktopMain"/>.</summary>
    public static bool IsCodexProcess(RunningProcess process) => IsCodexName(process.Name) || IsCodexDesktopMain(process.Name, process.ImagePath);

    /// <summary>True for <c>claude</c>: Claude Code, or the Claude desktop app, which has the same process name.</summary>
    public static bool IsClaudeName(string name) => string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names (no extension, any case) of the processes running now.</summary>
    public static IReadOnlyList<string> RunningProcessNames() => RunningProcesses().Select(static p => p.Name).ToArray();

    /// <summary>The processes running now: each one's name, and the image path of a process named <c>ChatGPT</c> (the only name that needs it), nothing else.</summary>
    public static IReadOnlyList<RunningProcess> RunningProcesses()
    {
        var processes = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                var needsPath = string.Equals(name, CodexDesktopMainName, StringComparison.OrdinalIgnoreCase);
                processes.Add(new RunningProcess(name, needsPath ? ImagePathOf(process) : null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone between the listing and the read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private static string? ImagePathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // A sandboxed helper process of the same app, or one that just exited: not the main process.
            return null;
        }
    }

    /// <summary>The running processes, or none when the machine would not say.</summary>
    public IReadOnlyList<RunningProcess> Processes()
    {
        try
        {
            if (_cache <= TimeSpan.Zero)
            {
                return _processes();
            }

            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (_lastTick == long.MinValue || now - _lastTick > _cache.TotalMilliseconds)
                {
                    _last = _processes();
                    _lastTick = now;
                }

                return _last;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Array.Empty<RunningProcess>();
        }
    }

    /// <summary>The running process names, or none when the machine would not say.</summary>
    public IReadOnlyList<string> Names() => Processes().Select(static p => p.Name).ToArray();

    /// <summary>True when a Codex agent is running now (<see cref="IsCodexProcess"/>); the always-on Codex sandbox service alone is not one.</summary>
    public bool CodexRunning() => Processes().Any(IsCodexProcess);

    /// <summary>True when Claude Code (or the Claude desktop app) is running now.</summary>
    public bool ClaudeRunning() => Processes().Any(static p => IsClaudeName(p.Name));

    /// <summary>The warning bar for a review, or null while Codex is not running.</summary>
    public CommandReviewWarning? CodexWarning() => CodexRunning() ? new CommandReviewWarning(CodexRunningTitle, CodexRunningMessage) : null;

    /// <summary>
    /// True when running <paramref name="argv"/> on <paramref name="executable"/> rewrites Codex's configuration: <c>defenseclaw setup codex ...</c>, a
    /// <c>guardrail fail-mode</c> change (it re-renders the hooks), or a gateway start / restart / stop. The reviews that do carry <see cref="CodexWarning"/>.
    /// </summary>
    public static bool TouchesCodexConfig(string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(argv);

        var name = Path.GetFileNameWithoutExtension(executable);
        if (string.Equals(name, GatewayControl.Executable, StringComparison.OrdinalIgnoreCase))
        {
            return argv.Count > 0 && argv[0] is "start" or "stop" or "restart";
        }

        if (!string.Equals(name, CommandReview.DefaultExecutable, StringComparison.OrdinalIgnoreCase) || argv.Count < 2)
        {
            return false;
        }

        return (string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase) && string.Equals(argv[1], "codex", StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(argv[0], "guardrail", StringComparison.OrdinalIgnoreCase) && string.Equals(argv[1], "fail-mode", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One running process as the probe reads it: its name and, only for a process whose name does not say whose it is, its image path.</summary>
/// <param name="Name">The process name without extension.</param>
/// <param name="ImagePath">The full path of the process image, or null when it was not read (or could not be).</param>
public readonly record struct RunningProcess(string Name, string? ImagePath = null);
