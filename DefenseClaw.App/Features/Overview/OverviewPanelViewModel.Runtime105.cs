using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The fail-mode rows of "What needs attention" under DefenseClaw 1.0.5's rule (CUST-397): on a per-user host a saved <c>hook_fail_mode</c> (the connector's own,
/// else the global one) decides what a hook does while the gateway is down, and the policy tier decides only when nothing is saved
/// (<see cref="HookFailModeRule.SavedValueWins"/>). The 1.0.4 row that called a saved <c>closed</c> "not applied" is never shown here, and every hint to close an
/// outage is the runtime's own (<c>config.py</c> <c>outage_block_hint</c>): <c>guardrail fail-mode closed</c> when a saved value decides, the strict rule pack only
/// when nothing is saved. Older runtimes and managed installs never reach this file.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>True on a per-user 1.0.5-or-later host: a saved <c>hook_fail_mode</c> decides a connector's outage mode, the policy tier only when none is saved.</summary>
    internal bool SavedFailModeWins =>
        HookFailModeRule.SavedValueWins(Services.Runtime.Capabilities, Services.Installation.Context.IsManaged);

    /// <summary>
    /// What decides <paramref name="name"/>'s gateway-outage mode on a per-user 1.0.5 host, for the Configuration card's fail-mode row: its own saved value, the
    /// saved global one, or (nothing saved) the policy tier. Null everywhere else, where the row reads as it always did.
    /// </summary>
    internal string? FailModeDecider(string name)
    {
        if (!SavedFailModeWins)
        {
            return null;
        }

        var guardrail = Services.Config.Config.Guardrail;
        return HookFailModeRule.SavedScope(guardrail, name) switch
        {
            SavedFailModeScope.Connector => $"outage mode saved for {name} ({HookFailModeRule.SavedMode(guardrail, name)}), which wins over the policy tier",
            SavedFailModeScope.Global => $"outage mode saved for every connector ({HookFailModeRule.SavedMode(guardrail, name)}), which wins over the policy tier",
            _ => "no outage mode saved: the policy tier decides",
        };
    }

    /// <summary>
    /// One row per configured connector whose outage mode is worth a look: an observing connector whose hook blocks during an outage (because a saved
    /// <c>closed</c> decides it, or nothing is saved and its rule pack is strict), and a saved <c>open</c> that overrides a rule pack config.yaml plainly names
    /// strict (the pairing 1.0.4 told people to rely on, which a saved value now beats). The mode is the one the hook reports when the app has read it, else
    /// the saved value that decides it.
    /// </summary>
    private void AppendSavedFailModeAttention(List<AttentionRow> rows)
    {
        var guardrail = Services.Config.Config.Guardrail;
        foreach (var (name, settings) in guardrail.Connectors)
        {
            if (RemovedConnectors.IsRemoved(name))
            {
                continue;
            }

            _connectorModes.TryGetValue(name, out var live);
            var scope = HookFailModeRule.SavedScope(guardrail, name);
            var saved = HookFailModeRule.SavedMode(guardrail, name);
            var effective = live?.HookFailMode ?? _status.Connector(name)?.FailMode?.Effective ?? saved;
            var closed = string.Equals(effective, "closed", StringComparison.OrdinalIgnoreCase);
            var observing = string.Equals(live?.GuardrailMode ?? settings.Mode, "observe", StringComparison.OrdinalIgnoreCase);
            var where = scope == SavedFailModeScope.Connector ? $"for {name}" : "for every connector (guardrail.hook_fail_mode)";

            if (closed && observing)
            {
                var (why, way) = (scope, saved) switch
                {
                    (SavedFailModeScope.None, _) => (
                        "no fail mode is saved for it and its policy tier blocks during an outage (a strict rule pack)",
                        $"Use the default or permissive rule pack, save a fail mode with {HookFailModeRule.FailModeCommand}, or move to enforce."),
                    (_, "closed") => (
                        $"config.yaml saves hook_fail_mode: closed {where}, and on this runtime a saved fail mode decides what the hook does while the gateway is not running",
                        $"Save open with {HookFailModeRule.FailModeCommand} open{(scope == SavedFailModeScope.Connector ? " --connector " + name : string.Empty)}, or move to enforce."),
                    _ => (
                        $"its hook reports fail mode closed although config.yaml saves open {where}",
                        $"Run {HookFailModeRule.FailModeCommand} to see what each connector's hook does, or move to enforce."),
                };
                rows.Add(new AttentionRow
                {
                    Title = $"{name}: observe mode with a fail-closed hook",
                    Detail = $"The connector only observes, but {why}, so a gateway outage blocks the agent anyway. {way} {HookFailModeRule.SavedWinsOutageRule}",
                    SeverityKey = "High",
                });
            }
            else if (!closed && saved == "open" && HookFailModeRule.KnownPolicyTier(guardrail, name) == "strict")
            {
                var hint = HookFailModeRule.OutageBlockHint(scope, name);
                rows.Add(new AttentionRow
                {
                    Title = $"{name}: a saved hook_fail_mode: open overrides the strict rule pack",
                    Detail = $"config.yaml saves hook_fail_mode: open {where}. On this runtime a saved fail mode wins over the policy tier, so calls run with a " +
                             $"warning while the gateway is not running, although the rule pack is strict. To block them during an outage, run {hint}. " +
                             HookFailModeRule.SavedWinsOutageRule,
                    SeverityKey = "Medium",
                    Command = hint,
                });
            }
        }
    }
}
