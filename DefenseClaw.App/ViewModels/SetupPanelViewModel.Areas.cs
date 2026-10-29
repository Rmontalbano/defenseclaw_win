using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Views.ConfigEditor;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Setup page in the Mac's order (CUST-385; <c>Features/SetupView.swift</c> at v1.1.34): "Setup Areas", the Redaction Policy entry, the area grid
/// (<see cref="Areas"/>, <see cref="SetupAreas.All"/>), then the config editor inline beneath it (<see cref="ConfigEditor"/>, the app's one draft). What only
/// Windows has follows the Mac's tiles in a group of its own (<see cref="ExtraGroups"/>), and the readiness checklist, the connector roster, the guardrail quick
/// controls and the credentials stay on the page as its secondary content.
/// <para>
/// Nothing here is a new flow. A tile opens what the app already had: a catalog wizard (with its list-first editor or routing dialog where it has one), the
/// Connector Setup chooser over the connector wizards, the batch dialog and <c>setup remove</c>, a card further down the page, or the AI Discovery panel's tuning.
/// The one flow added since is the ACP Guard wizard (CUST-294, <see cref="AcpGuardWizard"/>), which is the shared wizard over <c>acp setup</c>.
/// Opening the page runs nothing that changes anything, scans nothing, and writes no config: the catalog, the guardrail status, the credential list and the
/// editor's <c>config show --source</c> are all reads.
/// </para>
/// </summary>
public sealed partial class SetupPanelViewModel
{
    /// <summary>The section the view should bring into view next (<see cref="SectionRequested"/>), or null.</summary>
    public const string CredentialsSection = "credentials";

    /// <summary>The guardrail quick controls' section.</summary>
    public const string GuardrailSection = "guardrail";

    /// <summary>The inline config editor's section.</summary>
    public const string ConfigEditorSection = "config-editor";

    private ConfigEditorSession? _configEditor;

    /// <summary>The Connector Setup chooser is open.</summary>
    [ObservableProperty]
    private bool _isConnectorSetupOpen;

    /// <summary>The readiness checklist is unfolded (secondary content; every disclosure starts open, so nothing the page used to show is hidden).</summary>
    [ObservableProperty]
    private bool _isReadinessExpanded = true;

    /// <summary>The configured-connectors card is unfolded.</summary>
    [ObservableProperty]
    private bool _isConnectorsExpanded = true;

    /// <summary>The guardrail quick controls are unfolded.</summary>
    [ObservableProperty]
    private bool _isGuardrailExpanded = true;

    /// <summary>The Credentials card is unfolded.</summary>
    [ObservableProperty]
    private bool _isCredentialsExpanded = true;

    /// <summary>The Mac's area tiles, in its order (<see cref="SetupAreas.All"/>). Always all of them: a tile that cannot work here stays, disabled, with its reason.</summary>
    public ObservableCollection<SetupAreaViewModel> Areas { get; } = new(SetupAreas.All.Select(spec => new SetupAreaViewModel(spec)));

    /// <summary>The catalog's other targets (no Mac area routes to them), grouped as the hub always grouped them, under the Windows tiles.</summary>
    public ObservableCollection<WizardGroupViewModel> ExtraGroups { get; } = new();

    /// <summary>The connector wizards the Connector Setup chooser lists (and <c>setup remove</c> is not one of them), after the certification filter.</summary>
    public ObservableCollection<WizardCardViewModel> ConnectorChoices { get; } = new();

    /// <summary><c>setup remove</c>'s card, when the CLI has it: the chooser's "Remove a connector".</summary>
    public WizardCardViewModel? RemoveConnectorCard => _all.FirstOrDefault(c => string.Equals(c.Target, "remove", StringComparison.Ordinal));

    /// <summary>The chooser can offer removing a connector.</summary>
    public bool HasRemoveConnector => RemoveConnectorCard is not null;

    /// <summary>The chooser has connector wizards to list.</summary>
    public bool HasConnectorChoices => ConnectorChoices.Count > 0;

    /// <summary>The Windows group has tiles from the catalog (beside its own two).</summary>
    public bool HasExtraGroups => ExtraGroups.Count > 0;

    /// <summary>"Setup Areas · 23" style count line under the heading: how many area tiles the search leaves.</summary>
    public string AreaCountText => string.Format(CultureInfo.CurrentCulture, "{0} of {1}", Areas.Count(a => a.IsShown), Areas.Count);

    /// <summary>
    /// The app's one config.yaml draft (<see cref="ConfigEditorSession"/>): the inline editor at the foot of this page and the detached window are both views of
    /// it. Made on first use; reading it loads nothing (the inline editor loads the file the first time it comes on screen).
    /// </summary>
    public ConfigEditorSession ConfigEditor => _configEditor ??= ConfigEditorSession.For(Services);

    /// <summary>Raised when a tile or a deep link asks for a section further down the page (<see cref="PendingSection"/>); the view brings it into view.</summary>
    public event EventHandler? SectionRequested;

    /// <summary>The section a tile or a deep link asked for, until the view has brought it into view (a first visit asks before the view is loaded).</summary>
    public string? PendingSection { get; set; }

    /// <summary>Asks the view for a section further down the page.</summary>
    private void RequestSection(string section)
    {
        PendingSection = section;
        SectionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The toolbar's "Config editor": brings the inline editor into view. (The editor's own "Open in window" detaches it.)</summary>
    [RelayCommand]
    private void ShowConfigEditor() => RequestSection(ConfigEditorSection);

    /// <summary>A tile of the area grid was pressed. Does nothing for a tile that cannot be opened.</summary>
    [RelayCommand]
    private async Task OpenAreaAsync(SetupAreaViewModel? area)
    {
        if (area is null || !area.CanOpen)
        {
            return;
        }

        switch (area.Spec.Route)
        {
            case SetupAreaRoute.Wizard when area.Card is { } card:
                await LaunchCoreAsync(card, area.Spec.Goal is { } goal ? WizardPreset.ForGoal(goal) : null).ConfigureAwait(true);
                break;

            case SetupAreaRoute.ConnectorSetup:
                IsConnectorSetupOpen = true;
                break;

            case SetupAreaRoute.Credentials:
                IsCredentialsExpanded = true;
                RequestSection(CredentialsSection);
                break;

            case SetupAreaRoute.GuardrailActions:
                IsGuardrailExpanded = true;
                RequestSection(GuardrailSection);
                break;

            case SetupAreaRoute.AiDiscovery:
                // The tuning dialog lives on the AI Discovery panel: open it there. Nothing runs until its review is confirmed.
                RequestNavigation("ai-discovery", AiDiscoveryTune.Instance);
                break;

            case SetupAreaRoute.Panel:
                RequestNavigation(area.Spec.Targets[0]);
                break;

            case SetupAreaRoute.AcpGuard:
                await OpenAcpGuardAsync().ConfigureAwait(true);
                break;
        }
    }

    private bool _acpGuardOpening;

    /// <summary>What opens the ACP Guard wizard: null is <see cref="AcpGuardWizard.ShowAsync"/> over the main window. A test puts its own here.</summary>
    internal Func<Task>? AcpGuardOpener { get; set; }

    /// <summary>
    /// The ACP Guard tile (CUST-294): reads what the wizard offers (the CLI's help and the gateway's ACP catalog and profiles, nothing that changes
    /// anything) and opens it. A second press while that read is under way is ignored.
    /// </summary>
    private async Task OpenAcpGuardAsync()
    {
        if (_acpGuardOpening)
        {
            return;
        }

        _acpGuardOpening = true;
        try
        {
            await (AcpGuardOpener?.Invoke() ?? AcpGuardWizard.ShowAsync(Services, Application.Current?.MainWindow)).ConfigureAwait(true);
        }
        finally
        {
            _acpGuardOpening = false;
        }
    }

    /// <summary>The chooser's close button and Esc.</summary>
    [RelayCommand]
    private void CloseConnectorSetup() => IsConnectorSetupOpen = false;

    /// <summary>The chooser's "Set up several…": the batch dialog (CUST-271).</summary>
    [RelayCommand]
    private void OpenBatchFromChooser()
    {
        IsConnectorSetupOpen = false;
        Batch.Open();
    }

    /// <summary>The chooser's "Remove a connector…": the <c>setup remove</c> wizard, which ends on its review like any other.</summary>
    [RelayCommand]
    private Task RemoveConnectorAsync() =>
        RemoveConnectorCard is { } card ? LaunchCommand.ExecuteAsync(card) : Task.CompletedTask;

    /// <summary>Esc while the chooser is open closes it.</summary>
    internal bool HandleConnectorSetupEscape()
    {
        if (!IsConnectorSetupOpen)
        {
            return false;
        }

        IsConnectorSetupOpen = false;
        return true;
    }

    /// <summary>
    /// Works out every area tile, the Windows group and the chooser's list again from the cards, the installation and the runtime. Cheap and idempotent (a
    /// tile whose state did not move raises nothing), so it runs whenever any of those may have moved.
    /// </summary>
    private void RefreshAreas()
    {
        var needle = SearchText.Trim();
        var connectors = _all.Where(SetupAreas.IsConnectorCard).ToList();

        foreach (var area in Areas)
        {
            _ = area.Apply(StateFor(area.Spec));
            area.IsShown = area.Matches(needle, connectors);
        }

        // The chooser: every connector wizard (not `setup remove`, which is its own button), the certification filter applied as it always was.
        var choices = connectors
            .Where(c => !string.Equals(c.Target, "remove", StringComparison.Ordinal))
            .Where(c => !CertifiedOnly || c.PlatformStatus is PlatformStatus.Certified or PlatformStatus.NotApplicable)
            .OrderBy(c => c.IsAvailable ? 0 : 1)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SyncCollection(ConnectorChoices, choices, c => c.Target, static (a, b) => ReferenceEquals(a, b));

        // The Windows group: what no area and no chooser reaches, grouped and filtered exactly like the hub's own groups. Rebuilt only when it moved,
        // so a config.yaml read (which redraws every state line) does not rebuild its tiles.
        var extras = Groups
            .Select(g => (g.Name, Cards: g.Cards.Where(c => !SetupAreas.Claims(c.Target) && !SetupAreas.IsConnectorCard(c)).ToList()))
            .Where(g => g.Cards.Count > 0)
            .ToList();
        var same = extras.Count == ExtraGroups.Count &&
                   extras.Zip(ExtraGroups).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal) && p.First.Cards.SequenceEqual(p.Second.Cards));
        if (!same)
        {
            ExtraGroups.Clear();
            foreach (var (name, cards) in extras)
            {
                ExtraGroups.Add(new WizardGroupViewModel(name, cards));
            }
        }

        OnPropertyChanged(nameof(RemoveConnectorCard));
        OnPropertyChanged(nameof(HasRemoveConnector));
        OnPropertyChanged(nameof(HasConnectorChoices));
        OnPropertyChanged(nameof(HasExtraGroups));
        OnPropertyChanged(nameof(AreaCountText));
    }

    /// <summary>What one area's tile shows now. See <see cref="SetupAreaState"/>.</summary>
    internal SetupAreaState StateFor(SetupAreaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var blocked = Services.Installation.BlockedReason;

        switch (spec.Route)
        {
            case SetupAreaRoute.Wizard:
            {
                var card = spec.Targets
                    .Select(t => _all.FirstOrDefault(c => string.Equals(c.Target, t, StringComparison.Ordinal)))
                    .FirstOrDefault(c => c is not null);
                if (card is null)
                {
                    var command = "defenseclaw setup " + spec.Targets[0];
                    var missing = IsLoading
                        ? "Reading the setup catalog from the CLI…"
                        : HasLoadError
                            ? "The setup catalog could not be read, so this wizard cannot be opened. Re-read catalog tries again."
                            : $"The installed DefenseClaw has no '{command}' command.";
                    return new SetupAreaState(null, false, missing, true, null, command + Environment.NewLine + missing);
                }

                // The card already knows why it cannot be opened: the platform, the CLI, Docker or Terraform (the tile's text), or the installation (the tooltip's).
                string? reason = card.CanLaunch ? null : card.IsAvailable ? card.InstallationBlockedReason : card.UnavailableReason;
                var tip = spec.Goal is null ? card.TileToolTip : card.TileToolTip + Environment.NewLine + "Opens on its Cisco AI Defense settings.";
                return new SetupAreaState(card, card.CanLaunch, reason, !card.IsAvailable, card.HasStateLine ? card.StateLine : null, tip);
            }

            case SetupAreaRoute.ConnectorSetup:
            {
                var roster = RemoveConnectorCard is { HasStateLine: true } remove ? remove.StateLine : null;
                const string What = "Set up one connector, several at once (setup --connector …), or remove one. Each ends on its review.";
                return new SetupAreaState(null, blocked is null, blocked, false, roster, blocked is null ? What : What + Environment.NewLine + blocked);
            }

            case SetupAreaRoute.Credentials:
                return new SetupAreaState(null, true, null, false, null, "Shows the Credentials card below: defenseclaw keys list, check and set.");

            case SetupAreaRoute.GuardrailActions:
                return new SetupAreaState(null, true, null, false, null, "Shows the guardrail quick controls below: status, enable or disable, hook fail mode.");

            case SetupAreaRoute.AiDiscovery:
                return new SetupAreaState(null, true, null, false, null, "Opens AI Discovery's tuning: mode, cadence, scope and sources, reviewed before anything runs.");

            case SetupAreaRoute.Panel:
                return new SetupAreaState(
                    null,
                    true,
                    null,
                    false,
                    null,
                    "Opens the Registries panel (Discover): add, sync, approve and remove registries there. defenseclaw setup registry needs a terminal on Windows.");

            case SetupAreaRoute.Unsupported:
                return new SetupAreaState(null, false, spec.Blurb, true, null, spec.Blurb);

            case SetupAreaRoute.AcpGuard:
            {
                // On a runtime without the guard (0.8.10), the capability's own sentence and nothing to press. With it, the wizard; on a read-only
                // installation too, because a managed enrollment is only ever made on one: its review then says why Execute is off, and nothing runs.
                var gate = Services.Runtime.Check(spec.Capability);
                if (!gate.IsAvailable)
                {
                    var unsupported = gate.Reason ?? RuntimeCapabilityCatalog.UnsupportedMessage;
                    return new SetupAreaState(null, false, unsupported, true, null, unsupported);
                }

                var tip = blocked is null ? SetupAreas.AcpGuardToolTip : SetupAreas.AcpGuardToolTip + Environment.NewLine + SetupAreas.AcpGuardReadOnlyNote;
                return new SetupAreaState(null, true, null, false, null, tip);
            }

            default:
            {
                // A flow this app has not built yet: on a runtime without its capability, the capability's own sentence; with it, "coming".
                var gate = Services.Runtime.Check(spec.Capability);
                var reason = gate.IsAvailable ? SetupAreas.ComingReason : gate.Reason ?? RuntimeCapabilityCatalog.UnsupportedMessage;
                return new SetupAreaState(null, false, reason, true, null, reason, IsComing: gate.IsAvailable);
            }
        }
    }
}
