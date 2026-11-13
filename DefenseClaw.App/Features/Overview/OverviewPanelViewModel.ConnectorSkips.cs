using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" for a configured connector the gateway did not start (CUST-448, runtime 1.0.7 and later). Such a connector is not in <c>/health</c>
/// <c>connectors[]</c> at all, so it never showed: the Connectors card said "not running" and nothing said the agent's tool calls are not checked. The gateway
/// names them in <c>guardrail.details</c> (<see cref="ConnectorStartHealth"/>) and the CLI's <c>status</c> says "not enforced". One High row per connector, with
/// the next step. Everything is gated on <see cref="RuntimeCapability.AgentNotInstalledSkip"/>, so 1.0.0 to 1.0.6 draw exactly what they drew before, and
/// there is nothing without a <c>/health</c> answer. Reviewed commands only: nothing runs until the operator confirms.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>Prefix of the "Remove connector" button's id; the connector's name follows the colon.</summary>
    internal const string ConnectorSkipRemoveActionPrefix = "connector-skip-remove:";

    /// <summary>The id of the "Restart gateway" button.</summary>
    internal const string ConnectorSkipRestartActionId = "connector-skip-restart";

    /// <summary>The connectors the gateway did not start, as the last answer says; empty on a runtime before 1.0.7 or with no answer.</summary>
    private IReadOnlyList<ConnectorSkip> CurrentConnectorSkips(GatewayHealth? health) =>
        health is not null && Services.Runtime.Capabilities.Has(RuntimeCapability.AgentNotInstalledSkip)
            ? ConnectorStartHealth.From(health.Guardrail)
            : [];

    /// <summary>True when the gateway says it did not start <paramref name="connector"/> (any spelling), on a runtime that says so.</summary>
    private bool IsSkippedAtStart(string connector, GatewayHealth? health)
    {
        var name = OverviewDetectedConnectors.Normalize(connector);
        return CurrentConnectorSkips(health).Any(skip => OverviewDetectedConnectors.Normalize(skip.Name) == name);
    }

    /// <summary>The Connectors card's state for a configured connector that is not in <c>/health</c>: "not running", plus "(not enforced)" when the gateway did not start it.</summary>
    private string NotRunningText(string connector, GatewayHealth? health) =>
        IsSkippedAtStart(connector, health) ? "not running (not enforced)" : "not running";

    /// <summary>
    /// True when the row drawn from the agent's own settings (<see cref="AppendRuntime105Attention"/>: path and fix, read from disk) already says what the
    /// gateway's refusal of <paramref name="skip"/> says, so a second row would double it up.
    /// </summary>
    internal static bool DiskRowCovers(ConnectorSkip skip, AgentHookSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(skip);

        return skip.Kind == ConnectorSkipKind.RefusedByAgentSettings &&
               ((OverviewDetectedConnectors.Normalize(skip.Name) == "claudecode" && switches.ClaudeSettingsPath is not null) ||
                (OverviewDetectedConnectors.Normalize(skip.Name) == "codex" && switches.CodexConfigPath is not null));
    }

    /// <summary>Adds one High row per connector the gateway did not start. Called from <see cref="AppendOverviewHealthAttention"/>.</summary>
    private void AppendConnectorSkipAttention(List<AttentionRow> rows)
    {
        foreach (var skip in CurrentConnectorSkips(_snapshot.Health))
        {
            // The disk row is drawn only for a per-user install whose roster has the connector; the gateway's row covers every other case.
            if (!Services.Installation.Context.IsManaged && IsActiveConnector(skip.Name) && DiskRowCovers(skip, _hookSwitches))
            {
                continue;
            }

            rows.Add(WithRepair(ConnectorSkipRow(skip), skip));
        }
    }

    private AttentionRow ConnectorSkipRow(ConnectorSkip skip)
    {
        var friendly = FriendlyConnectorName(skip.Name);
        var reason = skip.Reason.Length == 0
            ? "It gave no reason."
            : skip.Reason.EndsWith('.') ? skip.Reason : skip.Reason + ".";
        var blocked = AttentionActionBlockedReason(ConnectorSkipRestartActionId, _snapshot);
        var restartBlocked = blocked ?? RestartQueueReview.BlockedReason(Services, _snapshot);
        var restart = new AttentionAction(
            ConnectorSkipRestartActionId,
            "Restart gateway",
            restartBlocked ?? "Opens a review of defenseclaw-gateway restart; nothing restarts until you confirm.",
            Enabled: restartBlocked is null);

        return skip.Kind switch
        {
            ConnectorSkipKind.AgentNotInstalled => new AttentionRow
            {
                Title = $"{friendly} is not enforced: the agent is not installed",
                Detail = $"The gateway did not start this connector. It says: {reason} Reinstall the agent and restart the gateway, or remove the connector.",
                SeverityKey = "High",
                Actions = new AttentionActions(new[]
                {
                    new AttentionAction(
                        ConnectorSkipRemoveActionPrefix + skip.Name,
                        "Remove connector",
                        blocked ?? $"Opens a review of defenseclaw setup remove {skip.Name} --yes; nothing changes until you confirm.",
                        Enabled: blocked is null),
                    restart,
                }),
            },
            ConnectorSkipKind.RefusedByAgentSettings => new AttentionRow
            {
                Title = $"{friendly} is not enforced: its own settings refuse DefenseClaw hooks",
                Detail = $"The gateway did not start this connector. It says: {reason} Fix what it names, then restart the gateway.",
                SeverityKey = "High",
                Actions = new AttentionActions(new[] { restart }),
            },
            _ => new AttentionRow
            {
                Title = $"{friendly} is not enforced: setup failed when the gateway started",
                Detail = $"The gateway did not start this connector. It says: {reason} Read gateway.log for the cause, then run the command below. The app never runs it.",
                SeverityKey = "High",
                Command = $"defenseclaw setup {skip.Name}",
            },
        };
    }

    /// <summary>"Remove connector": the review of <c>setup remove &lt;name&gt; --yes</c>. Only for a connector the last answer says is not installed.</summary>
    private void RemoveSkippedConnector(string name)
    {
        if (!CurrentConnectorSkips(_snapshot.Health).Any(skip => skip.Kind == ConnectorSkipKind.AgentNotInstalled && skip.Name == name))
        {
            return;
        }

        // setup remove prompts unless --yes (cmd_setup.py setup_remove); it restarts the gateway by default, which tears the connector's hooks down.
        var argv = new[] { "setup", "remove", name, "--yes" };
        Review.Open(
            $"Remove the {FriendlyConnectorName(name)} connector?",
            $"Takes {FriendlyConnectorName(name)} out of DefenseClaw's configured connectors and restarts the gateway so it removes that connector's hooks. " +
            "The agent itself is not touched. DefenseClaw refuses to remove the last remaining connector.",
            new[]
            {
                new DiscoverStep(argv, $"Remove the {FriendlyConnectorName(name)} connector from DefenseClaw.", CommandTier.StateChanging, CliRunner.LongRunningTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            restartsGateway: CommandReview.RestartsGatewayFor(argv),
            primaryText: "Remove connector");
    }
}
