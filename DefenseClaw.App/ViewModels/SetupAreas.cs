using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>Where a Setup area tile goes (CUST-385). Every route is a flow this app already has; none is new.</summary>
public enum SetupAreaRoute
{
    /// <summary>A <c>defenseclaw setup</c> wizard from the catalog (or what its card already opens instead: a list-first editor, the routing dialog).</summary>
    Wizard,

    /// <summary>The Connector Setup chooser: one connector's wizard, several at once (the batch dialog), or removing one.</summary>
    ConnectorSetup,

    /// <summary>The Credentials card further down the page, opened and brought into view.</summary>
    Credentials,

    /// <summary>The guardrail quick controls (status, enable / disable, hook fail mode) further down the page, opened and brought into view.</summary>
    GuardrailActions,

    /// <summary>The AI Discovery panel's tuning dialog.</summary>
    AiDiscovery,

    /// <summary>
    /// Another panel that already does it (<see cref="SetupAreaSpec.Targets"/> holds the panel id): the Registries panel, where the <c>setup registry</c> card
    /// itself sends the operator (that wizard needs a terminal on Windows).
    /// </summary>
    Panel,

    /// <summary>Not available on Windows at all: the tile says why and offers nothing.</summary>
    Unsupported,

    /// <summary>On a runtime that has it, a flow this app has not built yet: the tile keeps its place, disabled, and says so.</summary>
    Coming,

    /// <summary>The ACP Guard wizard (<c>acp setup</c>, CUST-294), on a runtime with the ACP guard.</summary>
    AcpGuard,
}

/// <summary>One Setup area: the Mac's tile, and the Windows flow it opens.</summary>
/// <param name="Id">The Mac's wizard id (<c>SetupDefinitions.swift</c>), which also picks the tile's glyph (<see cref="WizardTileIcons"/>).</param>
/// <param name="Title">The Mac's title.</param>
/// <param name="Blurb">The Mac's blurb (or, for an area Windows cannot offer, why).</param>
/// <param name="Route">Where the tile goes.</param>
/// <param name="Targets">For <see cref="SetupAreaRoute.Wizard"/>: the catalog targets that can serve it, the first one the CLI has wins. For <see cref="SetupAreaRoute.Panel"/>: the panel id.</param>
/// <param name="Goal">For a wizard with goals: the goal it opens on (<see cref="WizardPreset.ForGoal"/>), or null for its first page.</param>
/// <param name="Capability">The runtime capability the area needs, or null.</param>
public sealed record SetupAreaSpec(
    string Id,
    string Title,
    string Blurb,
    SetupAreaRoute Route,
    IReadOnlyList<string> Targets,
    string? Goal = null,
    RuntimeCapability? Capability = null);

/// <summary>
/// The Setup page's area grid, in the Mac's order (CUST-385): <c>TUIWizards.all</c> in <c>Features/SetupDefinitions.swift</c> at v1.1.34, which the live
/// page lays out as an adaptive grid under "Setup Areas" and the Redaction Policy entry. Titles and blurbs are the Mac's; the routes are this app's own
/// flows (CUST-221/225/266/270/271/328/379). The catalog's other targets, and the tiles only Windows has, follow in a group of their own.
/// </summary>
public static class SetupAreas
{
    /// <summary>The Mac's 23 areas, in its order.</summary>
    public static IReadOnlyList<SetupAreaSpec> All { get; } = new[]
    {
        new SetupAreaSpec("connector", "Connector Setup", "Add or re-run a connector, choose the active hook connector set (batch), or remove one.", SetupAreaRoute.ConnectorSetup, Array.Empty<string>()),
        new SetupAreaSpec("credentials", "Credentials", "List, validate, or securely set env-backed credentials.", SetupAreaRoute.Credentials, Array.Empty<string>()),
        new SetupAreaSpec("ai-defense", "Cisco AI Defense", "Configure the cloud inspection endpoint, credential and scanner mode. Check the result with `defenseclaw doctor`.", SetupAreaRoute.Wizard, new[] { "guardrail" }, Goal: "cisco"),
        new SetupAreaSpec("llm", "LLM", "Configure the unified analyzer and guardrail model.", SetupAreaRoute.Wizard, new[] { "llm" }),
        new SetupAreaSpec("local-observability", "Local OTel", "Manage the bundled Prometheus, Loki, Tempo, and Grafana stack.", SetupAreaRoute.Wizard, new[] { "local-observability" }),
        new SetupAreaSpec("galileo", "Galileo", "Send GenAI traces to Galileo Cloud or self-hosted Galileo without replacing local observability.", SetupAreaRoute.Wizard, new[] { "galileo" }),
        new SetupAreaSpec("token-rotation", "Token Rotation", "Rotate the gateway token and refresh connector hooks.", SetupAreaRoute.Wizard, new[] { "rotate-token" }),
        new SetupAreaSpec("custom-providers", "Custom Providers", "List, add, inspect, or remove custom LLM provider overlays.", SetupAreaRoute.Wizard, new[] { "provider" }),
        new SetupAreaSpec("skill-scanner", "Skill Scanner", "Configure skill analyzers, policy, severity gates, and optional cloud checks.", SetupAreaRoute.Wizard, new[] { "skill-scanner" }),
        new SetupAreaSpec("mcp-scanner", "MCP Scanner", "Configure MCP analyzers and prompt, resource, and instruction scanning.", SetupAreaRoute.Wizard, new[] { "mcp-scanner" }),
        new SetupAreaSpec("gateway", "Gateway", "Configure gateway host, ports, TLS posture, and authentication.", SetupAreaRoute.Wizard, new[] { "gateway" }),
        new SetupAreaSpec("guardrail", "Guardrail", "Configure guardrail mode, scanners, detection strategy, and judge.", SetupAreaRoute.Wizard, new[] { "guardrail" }),
        new SetupAreaSpec("splunk", "Splunk", "Configure Splunk O11y, local logs, or Enterprise HEC pipelines.", SetupAreaRoute.Wizard, new[] { "splunk" }),
        new SetupAreaSpec("observability", "Observability", "Add, list, enable, disable, remove, or test OTel and audit destinations.", SetupAreaRoute.Wizard, new[] { "observability" }),
        new SetupAreaSpec("webhooks", "Webhooks", "Add, inspect, test, enable, disable, or remove alert notifier webhooks.", SetupAreaRoute.Wizard, new[] { "webhook" }),
        new SetupAreaSpec("sandbox", "Sandbox", SandboxReason, SetupAreaRoute.Unsupported, Array.Empty<string>(), Capability: RuntimeCapability.Sandbox),
        new SetupAreaSpec("registries", "Registries", "Add an external skill or MCP catalog and optionally sync and scan it.", SetupAreaRoute.Panel, new[] { "registries" }),
        new SetupAreaSpec("notification-routing", "Notifications Routing", "Route enforced blocks, observe findings, approvals, and source categories.", SetupAreaRoute.Wizard, new[] { "notifications-set", "notifications" }),
        new SetupAreaSpec("ai-discovery", "AI Discovery", "Enable, disable, and tune AI discovery cadence, scope, and privacy.", SetupAreaRoute.AiDiscovery, Array.Empty<string>()),
        new SetupAreaSpec("splunk-dashboards", "Splunk Dashboards", "Apply or destroy the DefenseClaw Splunk O11y dashboards and detectors.", SetupAreaRoute.Wizard, new[] { SplunkDashboards.Target }),
        new SetupAreaSpec("trusted-paths", "Trusted Paths", "List, add, or remove trusted connector-binary discovery prefixes.", SetupAreaRoute.Wizard, new[] { "trusted-paths" }),
        new SetupAreaSpec("guardrail-actions", "Guardrail Actions", "Run connector-scoped guardrail status and policy quick actions.", SetupAreaRoute.GuardrailActions, Array.Empty<string>()),
        new SetupAreaSpec("acp-guard", "ACP Guard", "Observe or enforce policy on editor-to-agent ACP traffic, or enroll with administrator-provisioned policy and token files.", SetupAreaRoute.AcpGuard, Array.Empty<string>(), Capability: RuntimeCapability.AcpGuard),
    };

    /// <summary>Why the Sandbox tile offers nothing here: the runtime's OpenShell sandboxes target Linux and macOS (<see cref="RuntimeCapability.Sandbox"/>).</summary>
    public const string SandboxReason =
        "Sandboxes are Linux and macOS only: DefenseClaw's OpenShell sandbox does not run on Windows, so there is nothing to set up here.";

    /// <summary>What a tile on the <see cref="SetupAreaRoute.Coming"/> route says on a runtime that has its capability: this app has not built the flow yet.</summary>
    public const string ComingReason = "Coming to this app. The installed DefenseClaw has it; until the form is here, use the CLI in a terminal.";

    /// <summary>The ACP Guard tile's tooltip on a runtime with the guard.</summary>
    public const string AcpGuardToolTip =
        "Opens the ACP Guard wizard: editor, agent, profile and mode, then the exact defenseclaw acp setup command to review. Opening it only reads (the CLI's help and the gateway's ACP catalog and profiles).";

    /// <summary>The ACP Guard tile's tooltip on a read-only installation: the wizard still opens (a managed enrollment is only ever on one), and runs nothing.</summary>
    public const string AcpGuardReadOnlyNote =
        "This installation is read-only here: the wizard opens and its review shows the exact command, but nothing runs.";

    /// <summary>
    /// True when an area routes to <paramref name="target"/>: its card is reached through that tile, so it is not drawn again in the Windows group. The
    /// connector cards are the Connector Setup tile's (<see cref="IsConnectorCard"/>).
    /// </summary>
    public static bool Claims(string target)
    {
        // The Registries tile goes to the Registries panel, the place the setup registry card itself sends the operator: the card is reached through it.
        if (string.Equals(target, "registry", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var area in All)
        {
            if (area.Route != SetupAreaRoute.Wizard)
            {
                continue;
            }

            foreach (var claimed in area.Targets)
            {
                if (string.Equals(claimed, target, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A card the Connector Setup chooser lists: a connector's own wizard, or <c>setup remove</c>, by the group the catalog put it in.</summary>
    public static bool IsConnectorCard(WizardCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return string.Equals(card.Definition.Group, WizardGroups.Connectors, StringComparison.Ordinal);
    }
}

/// <summary>What a Setup area tile shows right now, worked out by the Setup panel from the catalog, the installation and the runtime.</summary>
/// <param name="Card">The catalog card the tile opens, for a wizard route the CLI has; null otherwise.</param>
/// <param name="CanOpen">The tile can be pressed.</param>
/// <param name="Reason">Why it cannot, or null.</param>
/// <param name="ReasonReplacesBlurb">True when the reason is the tile's text (the area cannot work here); false when it only goes in the tooltip (a read-only installation, which the page's banner already says).</param>
/// <param name="StateLine">What config.yaml says about the area today, or null.</param>
/// <param name="ToolTip">The tile's tooltip.</param>
/// <param name="IsComing">The runtime has it and this app does not yet (a <see cref="SetupAreaRoute.Coming"/> tile).</param>
public sealed record SetupAreaState(
    WizardCardViewModel? Card,
    bool CanOpen,
    string? Reason,
    bool ReasonReplacesBlurb,
    string? StateLine,
    string ToolTip,
    bool IsComing = false);

/// <summary>One tile of the Setup area grid (CUST-385): the Mac's title, glyph and blurb, and the state <see cref="SetupAreaState"/> gives it.</summary>
public sealed partial class SetupAreaViewModel : ObservableObject
{
    private SetupAreaState _state;

    /// <summary>Whether the setup search leaves the tile on screen.</summary>
    [ObservableProperty]
    private bool _isShown = true;

    public SetupAreaViewModel(SetupAreaSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Icon = spec.Id switch
        {
            "connector" => SymbolRegular.PlugConnected24,
            "acp-guard" => SymbolRegular.ShieldLock24,
            "splunk-dashboards" => WizardTileIcons.For(SplunkDashboards.Target, WizardGroups.Observability),
            _ => WizardTileIcons.For(spec.Id, WizardGroups.Other),
        };
        _state = new SetupAreaState(null, false, null, false, null, spec.Title);
    }

    public SetupAreaSpec Spec { get; }

    public string Id => Spec.Id;

    public string Title => Spec.Title;

    /// <summary>The Fluent glyph (the Mac's SF Symbol, see <see cref="WizardTileIcons"/>).</summary>
    public SymbolRegular Icon { get; }

    /// <summary>The catalog card behind a wizard tile, when the CLI has one.</summary>
    public WizardCardViewModel? Card => _state.Card;

    public bool CanOpen => _state.CanOpen;

    /// <summary>Why the tile cannot be pressed, or null.</summary>
    public string? Reason => _state.Reason;

    /// <summary>What the tile says under its title: the Mac's blurb, or why the area cannot work here.</summary>
    public string Blurb => !CanOpen && _state.ReasonReplacesBlurb && _state.Reason is { Length: > 0 } reason ? reason : Spec.Blurb;

    public string StateLine => _state.StateLine ?? string.Empty;

    /// <summary>The tile carries a state line (and so one line of blurb).</summary>
    public bool HasStateLine => !string.IsNullOrEmpty(_state.StateLine) && CanOpen;

    public string TileToolTip => _state.ToolTip;

    /// <summary>The runtime has it and this app does not yet: the tile wears a "Coming" chip.</summary>
    public bool IsComing => _state.IsComing;

    /// <summary>The name of the tile's button.</summary>
    public string LaunchAutomationName => Spec.Route == SetupAreaRoute.Wizard ? "Configure " + Title : "Open " + Title;

    /// <summary>What a screen reader announces for the tile.</summary>
    public string AutomationName => CanOpen
        ? Title + ". " + Spec.Blurb + (HasStateLine ? " " + StateLine : string.Empty)
        : Title + ", not available. " + (Reason ?? string.Empty);

    /// <summary>Takes in what the tile shows now. Returns true when anything changed.</summary>
    internal bool Apply(SetupAreaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_state == state)
        {
            return false;
        }

        _state = state;
        OnPropertyChanged(string.Empty);
        return true;
    }

    /// <summary>True when the setup search's <paramref name="needle"/> finds the tile: its title, blurb, id or target, or the card behind it.</summary>
    internal bool Matches(string needle, IEnumerable<WizardCardViewModel> connectorCards)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        if (Contains(Title, needle) || Contains(Spec.Blurb, needle) || Contains(Spec.Id, needle))
        {
            return true;
        }

        foreach (var target in Spec.Targets)
        {
            if (Contains(target, needle))
            {
                return true;
            }
        }

        if (Card is { } card && card.Matches(needle))
        {
            return true;
        }

        if (Spec.Route == SetupAreaRoute.ConnectorSetup)
        {
            foreach (var connector in connectorCards)
            {
                if (connector.Matches(needle))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Contains(string text, string needle) => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => AutomationName;
}
