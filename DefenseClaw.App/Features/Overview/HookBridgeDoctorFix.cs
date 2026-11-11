using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The repair doctor names for an agent hook that is still the older Windows PowerShell EncodedCommand bridge (CUST-448, runtime 1.0.8: doctor_hooks.py).
/// </summary>
/// <param name="Agent">The agent as doctor labels it: <c>Codex</c>, <c>Claude Code</c>, <c>Copilot</c>, <c>Antigravity</c>, <c>Devin</c> or <c>Hermes</c>.</param>
/// <param name="Connector">The name <c>defenseclaw setup</c> takes: <c>codex</c>, <c>claude-code</c>, <c>copilot</c>, <c>antigravity</c>, <c>devin</c> or <c>hermes</c>.</param>
/// <param name="Argv">The command's arguments, without <c>defenseclaw</c>.</param>
/// <param name="Command">The whole command as doctor writes it.</param>
internal sealed record HookBridgeRepair(string Agent, string Connector, IReadOnlyList<string> Argv, string Command)
{
    /// <summary>Id of the "Re-run setup" button for this repair; the mode (Copilot) is part of it so two repairs never share one.</summary>
    public string ActionId => "hook-bridge-setup:" + Connector + (Mode is { } mode ? ":" + mode : string.Empty);

    private string? Mode
    {
        get
        {
            var at = Argv.ToList().IndexOf("--mode");
            return at >= 0 && at + 1 < Argv.Count ? Argv[at + 1] : null;
        }
    }
}

/// <summary>
/// Reads doctor's failing "<c>&lt;Agent&gt; hooks</c>" row for the PowerShell bridge that exits 0 when its launcher cannot start. Pure: a row that is not
/// exactly that failure, or whose command is not the exact setup command for the same agent, is nothing. The command is rebuilt from fixed words (the agent
/// is one of six, the mode one of two), never passed on from the file as text.
/// </summary>
internal static class HookBridgeDoctorFix
{
    /// <summary>What doctor's detail says of the old bridge (doctor_hooks.py, 1.0.8).</summary>
    internal const string StaleBridgePhrase = "PowerShell EncodedCommand hook exits 0 in Constrained Language mode";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Label = new(
        "^(?<agent>Codex|Claude Code|Copilot|Antigravity|Devin|Hermes) hooks( \\[[a-z0-9_-]{1,64}\\])?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        Timeout);

    private static readonly Regex Repair = new(
        "run [`'](?<command>defenseclaw setup (?<connector>codex|claude-code|copilot|antigravity|devin|hermes)(?<mode> --mode (?<value>action|observe))? --yes --restart)[`'] to repair the native registration",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        Timeout);

    /// <summary>The repair this check asks for, or null when the check is not that failure.</summary>
    internal static HookBridgeRepair? From(DoctorCheckRow check)
    {
        ArgumentNullException.ThrowIfNull(check);

        try
        {
            if (!string.Equals(check.Status, "fail", StringComparison.Ordinal) ||
                check.Detail is not { Length: > 0 } detail ||
                !detail.Contains(StaleBridgePhrase, StringComparison.Ordinal) ||
                Label.Match(check.Label.Trim()) is not { Success: true } label ||
                Repair.Match(detail) is not { Success: true } repair)
            {
                return null;
            }

            var agent = label.Groups["agent"].Value;
            var connector = repair.Groups["connector"].Value;
            if (!string.Equals(ConnectorFor(agent), connector, StringComparison.Ordinal))
            {
                return null;
            }

            var argv = new List<string> { "setup", connector };
            if (repair.Groups["value"].Success)
            {
                argv.Add("--mode");
                argv.Add(repair.Groups["value"].Value);
            }

            argv.Add("--yes");
            argv.Add("--restart");
            return new HookBridgeRepair(agent, connector, argv, "defenseclaw " + string.Join(' ', argv));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? ConnectorFor(string agent) => agent switch
    {
        "Codex" => "codex",
        "Claude Code" => "claude-code",
        "Copilot" => "copilot",
        "Antigravity" => "antigravity",
        "Devin" => "devin",
        "Hermes" => "hermes",
        _ => null,
    };
}
