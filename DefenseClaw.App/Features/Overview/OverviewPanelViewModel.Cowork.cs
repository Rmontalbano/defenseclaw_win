using System.Globalization;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Claude Cowork on the Overview (runtime 1.0.8, CUST-445): the Configuration card's "Cowork" row, the "What needs attention" row and its buttons, and the
/// Cowork line under the Claude Code connector. All of it comes from the <c>cowork</c> object of the Overview's own <c>defenseclaw status --json</c> read
/// (<see cref="CoworkProtection"/>); green means the CLI said <c>protected: true</c> and nothing else does. The live <c>/health</c> surface counters only add
/// a note: they are the hook's own label on a call, not a verdict. A Cowork that was never set up (<c>cowork_plugin_not_built</c>) is optional: no attention row,
/// a neutral Configuration row. Below 1.0.8 nothing here draws anything.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The id of the button that opens the Cowork wizard (build the plugin, upload it in Claude Desktop, check the result).</summary>
    internal const string OpenCoworkWizardActionId = "open-cowork-wizard";

    /// <summary>The id of the button that opens the Claude Code wizard, for a Cowork that is not protected because Claude Code is off.</summary>
    internal const string OpenClaudeCodeWizardActionId = "open-claude-code-wizard";

    /// <summary>The Configuration row's label.</summary>
    internal const string CoworkRowLabel = "Cowork";

    /// <summary>What opens the Cowork wizard: null is <see cref="CoworkWizard.ShowAsync"/> over the main window. A test puts its own here.</summary>
    internal Func<Task>? CoworkWizardOpener { get; set; }

    /// <summary>What opens the Claude Code wizard: null is <see cref="WizardLauncher.ShowAsync"/> for <c>claude-code</c> over the main window. A test puts its own here.</summary>
    internal Func<Task>? ClaudeCodeWizardOpener { get; set; }

    private bool HasCoworkProtection => Services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection);

    /// <summary>Hands the <c>cowork</c> object of a successful <c>status --json</c> read to the store the Setup readiness list and the Cowork wizard share. Nothing before 1.0.8.</summary>
    private void PublishCowork()
    {
        if (HasCoworkProtection)
        {
            CoworkProtectionStore.For(Services).Publish(_status.Cowork);
        }
    }

    /// <summary>
    /// The Configuration card's Cowork row, right after the hook fail modes: the CLI's sentence as written, its next step, and that Claude Code's settings
    /// apply. Nothing without the capability or when <c>status --json</c> had no <c>cowork</c> object (no plugin and no Claude Desktop: nothing to say).
    /// </summary>
    private void AddCoworkRow(List<(string Label, string Value, string Tone)> rows)
    {
        if (!HasCoworkProtection || _status.Cowork is not { } cowork)
        {
            return;
        }

        // Never set up: nothing the user asked for is missing, so this reads as optional (the CLI's sentence follows, minus its own "not protected: ").
        var value = cowork.IsNotBuilt ? cowork.NotSetUpText
            : cowork.Detail.Length > 0 ? cowork.Detail : cowork.Protected ? "protected" : "not protected";

        if (cowork.Step.Length > 0)
        {
            value += " · Next: " + cowork.Step;
        }

        value += " · uses the Claude Code connector's mode and fail mode";

        // A live label from /health: a note beside the CLI's verdict, never what turns the row green.
        if (LiveCoworkSurface() is { LastActivityAt: { } seen })
        {
            value += " · last Cowork call seen live " + Relative(seen);
        }

        rows.Add((CoworkRowLabel, value, cowork.Protected ? "Ok" : cowork.IsNotBuilt ? "Neutral" : "Warn"));
    }

    /// <summary>The <c>cowork</c> surface of the Claude Code connector in the last /health, or null (no gateway, no Claude Code connector, no labelled call yet).</summary>
    private ConnectorSurfaceHealth? LiveCoworkSurface()
    {
        var health = _snapshot.Health;
        if (health is null)
        {
            return null;
        }

        foreach (var connector in health.Connectors.Cast<ConnectorStatus?>().Prepend(health.Connector))
        {
            if (connector is not null && string.Equals(connector.Name, "claudecode", StringComparison.OrdinalIgnoreCase) && connector.Cowork is { } surface)
            {
                return surface;
            }
        }

        return null;
    }

    /// <summary>
    /// The Cowork line appended to the Claude Code connector's counters: <c>Cowork: 12 calls, last 3m ago (the hook's own label; the Cowork row above is the verdict)</c>. Empty for any other
    /// connector, below 1.0.8, or while the gateway has counted no Cowork call. The counters are the hook's own label on each call, since the gateway started.
    /// </summary>
    private string CoworkSurfaceNote(string connectorName, ConnectorStatus status)
    {
        if (!HasCoworkProtection ||
            !string.Equals(connectorName, "claudecode", StringComparison.OrdinalIgnoreCase) ||
            status.Cowork is not { } surface)
        {
            return string.Empty;
        }

        var calls = surface.Requests.ToString("N0", CultureInfo.CurrentCulture);
        var last = surface.LastActivityAt is { } seen ? ", last " + Relative(seen) : string.Empty;
        return $" · Cowork: {calls} {(surface.Requests == 1 ? "call" : "calls")}{last} (the hook's own label; the Cowork row above is the verdict)";
    }

    /// <summary>
    /// The attention row for a Cowork the user asked for and lacks: a plugin that was built but not uploaded, or whose Claude Code connector is off. Medium: the
    /// CLI itself only warns for every reason (cmd_status.py:1298 at 1.0.8 draws each not-protected state in yellow, none as a failure). Nothing for a Cowork
    /// that was never set up (<c>cowork_plugin_not_built</c>: Claude Desktop is installed, nothing was asked), when protected, when the state depends on the
    /// gateway (its own rows say what to do), on a managed installation (the administrator's side), or without the capability.
    /// The buttons are drawn off, with <see cref="AttentionActionBlockedReason"/> as their tip, when a press would be refused (a read-only installation), as the connector-skip rows do.
    /// </summary>
    private void AppendCoworkAttention(List<AttentionRow> rows)
    {
        if (!HasCoworkProtection || Services.Installation.Context.IsManaged || _status.Cowork is not { Protected: false } cowork || cowork.IsGatewayCondition)
        {
            return;
        }

        var detail = cowork.Detail;
        var withStep = cowork.Step.Length > 0 ? (detail + " " + cowork.Step).Trim() : detail;

        if (cowork.IsNotBuilt)
        {
            return;
        }

        if (cowork.AwaitsUpload)
        {
            rows.Add(CoworkRow("Cowork is not protected yet: upload the plugin in Claude Desktop", withStep, "Medium", OpenCoworkWizardActionId, "Set up Cowork",
                "Opens the Cowork wizard: where the plugin zip is, the upload steps in Claude Desktop, and a check of the result.", AttentionActionBlockedReason(OpenCoworkWizardActionId, _snapshot)));
        }
        else if (cowork.IsClaudeCodeOff)
        {
            rows.Add(CoworkRow("Cowork is not protected: Claude Code is off", detail, "Medium", OpenClaudeCodeWizardActionId, "Set up Claude Code",
                "Opens the Claude Code setup: the Cowork plugin runs the Claude Code connector's hooks.", AttentionActionBlockedReason(OpenClaudeCodeWizardActionId, _snapshot)));
        }
        else if (cowork.ReasonCode.Length > 0)
        {
            // A reason code a later runtime adds: say what the CLI says, offer nothing the app cannot back.
            rows.Add(CoworkRow("Cowork is not protected", withStep, "Medium", null, null, null, null));
        }
    }

    private static AttentionRow CoworkRow(string title, string detail, string severity, string? actionId, string? label, string? tip, string? blockedReason) => new()
    {
        Title = title,
        Detail = detail,
        SeverityKey = severity,
        Actions = actionId is null
            ? AttentionActions.None
            : new AttentionActions(new[]
            {
                new AttentionAction(actionId, label ?? actionId, blockedReason ?? tip ?? string.Empty, Enabled: blockedReason is null),
            }),
    };

    /// <summary>"Set up Cowork": the Cowork wizard, then a fresh <c>status --json</c> so the row shows what the wizard changed.</summary>
    internal async Task OpenCoworkWizardAsync()
    {
        await (CoworkWizardOpener?.Invoke() ?? CoworkWizard.ShowAsync(Services, Application.Current?.MainWindow)).ConfigureAwait(true);
        await RefreshStatusAsync(force: true, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>"Set up Claude Code": the Claude Code wizard, then a fresh <c>status --json</c>.</summary>
    internal async Task OpenClaudeCodeWizardAsync()
    {
        await (ClaudeCodeWizardOpener?.Invoke() ?? WizardLauncher.ShowAsync(Services, "claude-code", Application.Current?.MainWindow)).ConfigureAwait(true);
        await RefreshStatusAsync(force: true, CancellationToken.None).ConfigureAwait(true);
    }
}
