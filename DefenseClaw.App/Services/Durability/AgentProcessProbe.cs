using System.Diagnostics;
using System.IO;

namespace DefenseClaw.App.Services;

/// <summary>
/// Whether Codex or Claude Code is running now: the one process look behind the upgrade checklist's rows and the "Codex is running" warning on
/// every review that rewrites Codex's config.toml (CUST-352). DefenseClaw rewrites that file when the gateway starts, Codex rewrites it too, and
/// a collision leaves a stuck pending write that stops the gateway; the warning exists so the operator can quit Codex first.
/// <para>
/// Read-only: it lists process names and nothing else. The names come from an injectable list so a test never looks at the real machine.
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

    private readonly Func<IReadOnlyList<string>> _names;
    private readonly TimeSpan _cache;
    private readonly object _gate = new();
    private IReadOnlyList<string> _last = Array.Empty<string>();
    private long _lastTick = long.MinValue;

    /// <param name="names">The names (no extension, any case) of the processes running now.</param>
    /// <param name="cache">How long one answer is reused (a review that redraws on every keystroke asks often); zero asks every time.</param>
    public AgentProcessProbe(Func<IReadOnlyList<string>> names, TimeSpan cache = default)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _cache = cache;
    }

    /// <summary>The probe over the real machine. An answer is reused for two seconds.</summary>
    public static AgentProcessProbe Machine { get; } = new(RunningProcessNames, TimeSpan.FromSeconds(2));

    /// <summary>A probe that sees no process: what an isolated composition (a test, a harness) starts with.</summary>
    public static AgentProcessProbe None { get; } = new(static () => Array.Empty<string>());

    // The Codex desktop app's always-on LocalSystem service runs even when Codex is closed, so its presence says nothing about Codex being active (CUST-358).
    private static readonly string[] NotCodexNames = { "codex-windows-sandbox-service" };

    /// <summary>True for a Codex process name: <c>codex</c>, or anything that starts with it (<c>codex-windows-sandbox-setup</c>), except the always-on sandbox service.</summary>
    public static bool IsCodexName(string name) =>
        name.StartsWith("codex", StringComparison.OrdinalIgnoreCase) &&
        !NotCodexNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for <c>claude</c>: Claude Code, or the Claude desktop app, which has the same process name.</summary>
    public static bool IsClaudeName(string name) => string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names (no extension, any case) of the processes running now.</summary>
    public static IReadOnlyList<string> RunningProcessNames()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                names.Add(process.ProcessName);
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

        return names;
    }

    /// <summary>The running process names, or none when the machine would not say.</summary>
    public IReadOnlyList<string> Names()
    {
        try
        {
            if (_cache <= TimeSpan.Zero)
            {
                return _names();
            }

            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (_lastTick == long.MinValue || now - _lastTick > _cache.TotalMilliseconds)
                {
                    _last = _names();
                    _lastTick = now;
                }

                return _last;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True when a Codex process is running now.</summary>
    public bool CodexRunning() => Names().Any(IsCodexName);

    /// <summary>True when Claude Code (or the Claude desktop app) is running now.</summary>
    public bool ClaudeRunning() => Names().Any(IsClaudeName);

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
