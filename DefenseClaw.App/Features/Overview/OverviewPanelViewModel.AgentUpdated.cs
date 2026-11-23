using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "agent updated itself" row (runtime 1.0.10, G10C-08). When Codex, Amp, Hermes or OpenCode updates itself in place, its executable no longer matches what
/// setup recorded. Up to 1.0.9 that stopped the whole gateway at start; on 1.0.10 the gateway keeps running and does not start that connector. With several connectors /health
/// <c>guardrail.details.connectors_not_started</c> lists it with a reason that contains "executable changed since setup" (agent_executable_changed.go@1.0.10:49-54);
/// with ONE connector there is no such list and the guardrail is in <c>error</c> with that sentence in <c>last_error</c> (<see cref="AgentUpdatedGuardrailError"/>),
/// so nothing at all is enforced.
/// <see cref="ConnectorStartHealth"/> recognises the text (<see cref="ConnectorSkipKind.AgentUpdated"/>); the row is drawn only with
/// <see cref="RuntimeCapability.AgentSelfUpdateKeepsGateway"/>, so 1.0.9 and earlier keep the generic setup-failed row. Codex offers the reviewed
/// <see cref="AttentionActionIds.ReselectCodex"/> action; the others get the runtime's own named fix as a copy-only command built from the connector allow-list, never from the reason text.
/// A folder-moving update (the pinned path is gone) still stops the 1.0.10 gateway at start (codex_policy.go@1.0.10:283-291): that is the durability row, not this one.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private bool KeepsGatewayOnAgentUpdate => Services.Runtime.Capabilities.Has(RuntimeCapability.AgentSelfUpdateKeepsGateway);

    /// <summary>
    /// True when the durability row "Codex's executable moved after an update" already says this for <paramref name="skip"/> (the pinned path is gone), so a second row would double it up.
    /// </summary>
    private bool AgentUpdatedCoveredByDurability(ConnectorSkip skip) =>
        skip.Kind == ConnectorSkipKind.AgentUpdated &&
        OverviewDetectedConnectors.Normalize(skip.Name) == "codex" &&
        _snapshot.Durability?.CodexExecutableMissing == true;

    /// <summary>
    /// Adds the agents a guardrail in <c>error</c> says updated themselves (<see cref="AgentUpdatedGuardrailError"/>): the one-connector form, where the gateway
    /// has no "not started" list and /health shows only the guardrail error. A name the list already has is not added twice.
    /// </summary>
    private static IReadOnlyList<ConnectorSkip> WithSingleConnectorAgentUpdate(IReadOnlyList<ConnectorSkip> listed, GatewayHealth health)
    {
        var fromError = AgentUpdatedGuardrailError.From(health.Guardrail);
        if (fromError.Count == 0)
        {
            return listed;
        }

        var extra = fromError.Where(e => !listed.Any(l => l.Name == e.Name)).ToList();
        return extra.Count == 0 ? listed : listed.Concat(extra).OrderBy(static s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The row for an agent that updated itself. Critical when no connector is enforced at all, High when another one is.</summary>
    private AttentionRow AgentUpdatedRow(ConnectorSkip skip)
    {
        var friendly = FriendlyConnectorName(skip.Name);
        var reason = skip.Reason.EndsWith('.') ? skip.Reason : skip.Reason + ".";
        var detail = $"{friendly} updated itself, so its executable no longer matches what setup recorded. The gateway is still running, " +
                     $"but it is not checking {friendly}'s calls until setup is run again, which admits the new version. The gateway says: {reason}";
        var severity = NoConnectorEnforced(skip) ? "Critical" : "High";

        if (skip.Name == "codex")
        {
            var blocked = AttentionActionBlockedReason(AttentionActionIds.ReselectCodex, _snapshot);
            return new AttentionRow
            {
                Title = $"{friendly} updated itself; DefenseClaw is not checking it",
                Detail = detail + " Re-select Codex runs setup again, keeping Codex's current mode. Quit Codex first.",
                SeverityKey = severity,
                Actions = new AttentionActions(new[]
                {
                    new AttentionAction(
                        AttentionActionIds.ReselectCodex,
                        "Re-select Codex",
                        blocked ?? "Review defenseclaw setup codex --yes with Codex's current mode. Nothing runs until you confirm.",
                        Enabled: blocked is null),
                }),
            };
        }

        return new AttentionRow
        {
            Title = $"{friendly} updated itself; DefenseClaw is not checking it",
            Detail = detail + " Run the command below in a terminal. The app never runs it.",
            SeverityKey = severity,
            Command = AgentUpdatedCommand(skip.Name),
        };
    }

    /// <summary>The fix the runtime names for an agent that updated itself (<c>defenseclaw setup &lt;name&gt; --yes</c>); the name is one of the four the classifier allows, never text from the reason.</summary>
    internal static string AgentUpdatedCommand(string connector) => $"defenseclaw setup {connector} --yes";

    /// <summary>True when no connector is enforced: every connector the last /health lists is one the gateway did not start (or it lists none).</summary>
    private bool NoConnectorEnforced(ConnectorSkip skip)
    {
        var skipped = CurrentConnectorSkips(_snapshot.Health)
            .Select(static s => OverviewDetectedConnectors.Normalize(s.Name))
            .Append(OverviewDetectedConnectors.Normalize(skip.Name))
            .ToHashSet(StringComparer.Ordinal);
        var live = new List<string>();
        if (_snapshot.Health is { } health)
        {
            live.AddRange(health.Connectors.Select(static c => c.Name ?? string.Empty));
            if (health.Connector?.Name is { Length: > 0 } primary)
            {
                live.Add(primary);
            }
        }

        return !live.Select(OverviewDetectedConnectors.Normalize).Any(name => name.Length > 0 && !skipped.Contains(name));
    }
}
