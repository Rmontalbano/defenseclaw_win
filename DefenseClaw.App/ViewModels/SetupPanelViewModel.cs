using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>Why the Setup hub's card grid shows nothing (see <see cref="SetupPanelViewModel.EmptyState"/>).</summary>
public enum SetupEmptyState
{
    /// <summary>Cards are shown, or the catalog is still being read.</summary>
    None,

    /// <summary>The CLI's setup help could not be read.</summary>
    LoadFailed,

    /// <summary>The CLI answered and listed no setup targets.</summary>
    NoTargets,

    /// <summary>There are targets and the search or the certification filter hides all of them.</summary>
    NoMatch,
}

/// <summary>
/// The Setup hub: every <c>defenseclaw setup</c> flow the installed CLI exposes, as a
/// searchable card grid, the connector roster the wizards act on, and the guardrail quick controls.
/// <para>
/// <b>The card list is not written down anywhere.</b> It comes from
/// <see cref="WizardCatalog"/>, which parses the live CLI's help at runtime — so a setup
/// target added in a future 0.8.x appears here without an app update, and one that is removed
/// stops appearing. The certification badge is the CLI's own
/// <c>Platform status on windows:</c> answer, not a table in this app: on 0.8.7 that means
/// Claude Code and Codex are certified and every other connector is not, but the badge will
/// follow the binary if that changes. Certification is a question about <i>connectors</i>;
/// the other flows (rotate-token, webhook, llm, the scanners…) carry
/// <see cref="PlatformStatus.NotApplicable"/> and a neutral badge, and are not counted as
/// certified.
/// </para>
/// <para>
/// Uncertified cards stay launchable on purpose. The mac app's lesson — and the codex ghost
/// this project chased — is that hiding the option teaches nothing; the warning travels with
/// the operator into the wizard and onto its review screen instead. Targets that <i>cannot</i> work
/// on Windows (unsupported connectors, interactive-only wizards) are the exception:
/// they sit in a last "not available" group, disabled, each with its reason and where to go instead
/// (<see cref="WizardWindowsPolicy"/>). The local observability stack joins them only while Docker is not
/// ready (<see cref="WizardWindowsPolicy.NeedsDocker"/>): its card follows the shared Docker look, with the
/// probe's reason, and is launchable the rest of the time. The Splunk dashboards card does the same with Terraform
/// (<see cref="WizardWindowsPolicy.NeedsTerraform"/>); it is the one card the catalog's roster does not list (it is
/// derived from the Splunk card being there, see <see cref="SplunkDashboards"/>).
/// </para>
/// <para>
/// <b>Lifecycle.</b> The connector roster is re-derived on every gateway state change, but only
/// while this panel is <see cref="PanelViewModelBase.IsActive"/>: the subscription, and the
/// card updates that ride the catalog's <c>DefinitionChanged</c>, are attached in
/// <see cref="OnActivated"/> and detached in <see cref="OnDeactivated"/>, with one catch-up pass
/// on the way back in. A hidden tray app must not rebuild a card grid for nobody. The guardrail
/// status is a CLI call (about 1.7 s), so it is read once on activation when it is stale and on
/// Refresh — never on a timer.
/// </para>
/// </summary>
public sealed partial class SetupPanelViewModel : PanelViewModelBase, IAcceptsNavigation
{
    /// <summary>How long a <c>guardrail status</c> read is trusted when the panel is re-entered.</summary>
    private static readonly TimeSpan GuardrailFreshFor = TimeSpan.FromMinutes(2);

    private readonly WizardCatalog _catalog;
    private readonly LocalStackAvailability _localStack;
    private readonly TerraformAvailability _terraform;
    private readonly List<WizardCardViewModel> _all = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _certifiedOnly;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusNote = "Reading the setup catalog from the CLI…";

    [ObservableProperty]
    private string _loadError = string.Empty;

    [ObservableProperty]
    private bool _hasLoadError;

    /// <summary>
    /// Why the card grid is empty, when it is: three different situations that used to share one "No wizards match"
    /// message. Exactly one of <see cref="ShowLoadFailed"/>, <see cref="ShowNoTargets"/> and <see cref="ShowNoMatch"/>
    /// holds while it is not <see cref="SetupEmptyState.None"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadFailed))]
    [NotifyPropertyChangedFor(nameof(ShowNoTargets))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatch))]
    private SetupEmptyState _emptyState;

    /// <summary>The CLI did not answer the setup help: there is no catalog, and the search has nothing to do with it.</summary>
    public bool ShowLoadFailed => EmptyState == SetupEmptyState.LoadFailed;

    /// <summary>The CLI answered and listed no setup targets.</summary>
    public bool ShowNoTargets => EmptyState == SetupEmptyState.NoTargets;

    /// <summary>The catalog has targets and the search or the certification filter hides every one.</summary>
    public bool ShowNoMatch => EmptyState == SetupEmptyState.NoMatch;

    [ObservableProperty]
    private string _connectorNote = string.Empty;

    public SetupPanelViewModel(AppServices services)
        : base(services)
    {
        // No subscriptions here: OnActivated attaches them and OnDeactivated lets go.
        _catalog = WizardCatalog.Shared(services);
        _localStack = services.LocalStack;
        _terraform = services.Terraform;

        Review = new DiscoverActionReview(services);
        Credentials = new CredentialsViewModel(services, review: Review);
        Readiness = new ReadinessViewModel(services, Credentials, Review);
        Routing = new NotificationRoutingViewModel(services);
        Batch = new ConnectorBatchViewModel(services, applied: _ => RefreshAsync());

        // The credential read feeds the "Required Credentials" row.
        Credentials.Loaded += (_, _) => Readiness.Rebuild();
    }

    /// <summary>The Credentials card (<c>keys list --json</c>, <c>keys check</c>, <c>keys set</c> typed in the app or in a console window).</summary>
    public CredentialsViewModel Credentials { get; }

    /// <summary>The readiness checklist with a Fix per failing row.</summary>
    public ReadinessViewModel Readiness { get; }

    /// <summary>The shared review the readiness fixes go through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>
    /// The notification routing dialog (CUST-271): the Notifications and Notification categories tiles open it instead of a one-slot wizard.
    /// It is an overlay on this page with a review of its own, so several changes run as one reviewed plan.
    /// </summary>
    public NotificationRoutingViewModel Routing { get; }

    /// <summary>The connector batch dialog (CUST-271): several connectors in one reviewed <c>setup --yes --connector ...</c>, opened from the roster card.</summary>
    public ConnectorBatchViewModel Batch { get; }

    /// <summary>"Set up several…": opens the batch dialog on what config.yaml says now.</summary>
    [RelayCommand]
    private void OpenBatch() => Batch.Open();

    public override string Title => "Setup";

    /// <summary>Opens the config.yaml editor (raw AvalonEdit + generated form tabs).</summary>
    [RelayCommand]
    private void OpenConfigEditor() => Views.ConfigEditor.ConfigEditorWindow.Show(Services);

    /// <summary>Opens the first-run guided setup: state, agent detection, a form and a reviewed plan.</summary>
    [RelayCommand]
    private void OpenFirstRun() => Views.FirstRun.FirstRunWindow.Show(Services);

    /// <summary>Opens the runtime update / release-trust awareness window.</summary>
    [RelayCommand]
    private void OpenUpdates() => Views.Updates.UpdatesWindow.Show(Services);

    /// <summary>
    /// Where the "Guardrail controls" tile goes (the HILT / block-message / judge view). Null until the shell wires it, and
    /// then the tile is not shown; the wiring is one assignment, <c>setup.OpenGuardrailControls = () =&gt; ...</c>.
    /// </summary>
    public Action? OpenGuardrailControls
    {
        get => _openGuardrailControls;
        set
        {
            _openGuardrailControls = value;
            OnPropertyChanged(nameof(HasGuardrailControlsTile));
            OpenGuardrailControlsTileCommand.NotifyCanExecuteChanged();
        }
    }

    private Action? _openGuardrailControls;

    /// <summary>The "Guardrail controls" tile is offered only once something can open it.</summary>
    public bool HasGuardrailControlsTile => _openGuardrailControls is not null;

    [RelayCommand(CanExecute = nameof(HasGuardrailControlsTile))]
    private void OpenGuardrailControlsTile() => _openGuardrailControls?.Invoke();

    /// <summary>
    /// Where the "Redaction policy" tile goes (the advanced redaction window, CUST-295). Null until the shell wires it, and then the tile is
    /// not shown. Even wired, it is shown only while the connected runtime has <c>setup redaction</c> (<see cref="HasRedactionTile"/>).
    /// </summary>
    public Action? OpenRedaction
    {
        get => _openRedaction;
        set
        {
            _openRedaction = value;
            RaiseRedactionTile();
        }
    }

    private Action? _openRedaction;

    /// <summary>The "Redaction policy" tile: wired, and the runtime has the command. Hidden entirely on 0.8.10, which does not.</summary>
    public bool HasRedactionTile =>
        _openRedaction is not null && Services.Runtime.Check(DefenseClaw.Core.Runtime.RuntimeCapability.RedactionAdvanced).IsAvailable;

    [RelayCommand(CanExecute = nameof(HasRedactionTile))]
    private void OpenRedactionTile() => _openRedaction?.Invoke();

    private void RaiseRedactionTile()
    {
        OnPropertyChanged(nameof(HasRedactionTile));
        OpenRedactionTileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Where the Observability, Webhook and Trusted binary paths tiles go (CUST-270): a list-first window per resource instead of the wizard. The
    /// wizard is still one press away (the window's Add opens it on <c>add</c>). Null until the shell wires it, and then those tiles open their
    /// wizard as they always did; wired, they stay on a managed installation too, because the window only reads there.
    /// </summary>
    public Action<SetupResource>? OpenResourceEditor
    {
        get => _openResourceEditor;
        set
        {
            _openResourceEditor = value;
            foreach (var card in _all)
            {
                card.OpensEditor = ResourceFor(card) is not null;
            }
        }
    }

    private Action<SetupResource>? _openResourceEditor;

    /// <summary>The list the tile opens, when it is one of the three that have an editor and the shell has wired it.</summary>
    private SetupResource? ResourceFor(WizardCardViewModel card) => _openResourceEditor is null ? null : SetupResourceArgv.ForTarget(card.Target);

    private void OnRuntimeChanged(object? sender, EventArgs e) => RaiseRedactionTile();

    /// <summary>Shows these definitions as the tile grid without asking the CLI (tests).</summary>
    internal void ShowCards(IReadOnlyList<WizardDefinition> definitions)
    {
        FillCards(definitions);
        IsLoading = false;
        ApplyFilters();
    }

    public override string Description =>
        "Every defenseclaw setup flow on this machine, discovered from the CLI at runtime. Each one ends on a review screen showing the exact command before anything runs.";

    /// <summary>Cards, bucketed by <see cref="WizardGroups"/>, filtered by search and certification.</summary>
    public ObservableCollection<WizardGroupViewModel> Groups { get; } = new();

    /// <summary>Connectors DefenseClaw is currently configured with, from config.yaml and /health.</summary>
    public ObservableCollection<ConnectorChipViewModel> Connectors { get; } = new();

    public bool HasConnectors => Connectors.Count > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildConnectors();

        try
        {
            var definitions = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(true);
            FillCards(definitions);
        }
        finally
        {
            // Whatever ended the read — a result, a failure, a cancelled wait — the spinner
            // stops. Left true, "Asking the CLI which setup targets it has" would show forever.
            IsLoading = false;
        }

        HasLoadError = _catalog.LoadError is { Length: > 0 };
        LoadError = _catalog.LoadError ?? string.Empty;
        ApplyFilters();

        // Warm the per-target help in the background. The hub is already complete and badged
        // from the top-level screen; this fills in real descriptions, the authoritative
        // platform-status line, and the option lists the wizards are built from.
        _ = WarmAsync(cancellationToken);
    }

    /// <summary>
    /// Attaches to the gateway monitor and the catalog, then catches up once: the panel may have
    /// been off screen while the roster changed and while per-target help landed, and none of it
    /// was being watched. The catch-up is a no-op merge when nothing moved. Runs possibly before
    /// <see cref="InitializeAsync"/> has finished on the first visit — with no cards yet, the
    /// card half of the catch-up simply has nothing to do.
    /// </summary>
    /// <summary>
    /// A deep link (<see cref="IAcceptsNavigation"/>): a <see cref="CredentialSet"/> (the palette's <c>keys set NAME</c>, CUST-328) opens the Credentials card's masked
    /// box on that variable. Any other payload is ignored. Nothing is stored or run by it.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is CredentialSet set)
        {
            _ = Credentials.BeginSetForAsync(set.EnvName);
        }
    }

    protected override void OnActivated()
    {
        _catalog.DefinitionChanged += OnDefinitionChanged;
        Services.Monitor.StateChanged += OnGatewayStateChanged;
        _localStack.Changed += OnProbeAnswerChanged;
        _terraform.Changed += OnProbeAnswerChanged;
        Services.ConfigReloaded += OnConfigReloaded;

        // Whether the runtime has the redaction editor may have been learned while the panel was away.
        Services.Runtime.Changed += OnRuntimeChanged;
        RaiseRedactionTile();

        // The queued gateway restart is app-wide (CUST-267): a save or a run elsewhere may have queued or applied one while the panel was away.
        Services.RestartQueue.Changed += OnRestartQueueChanged;
        RefreshRestartBanner();

        BuildConnectors();
        RefreshCardStates();

        // Either may have moved while the panel was off screen: the cards behind the catalog, and the Docker and Terraform answers that gate
        // the stack's card and the dashboards' card.
        var moved = SyncCardsWithCatalog();
        moved |= SyncLocalStack();
        moved |= SyncTerraform();
        if (moved)
        {
            ApplyFilters();
        }

        // Docker and Terraform are looked at when the hub comes on screen (once per freshness window each), not on a timer; the answers
        // arrive through Changed.
        _ = _localStack.EnsureFreshAsync();
        _ = _terraform.EnsureFreshAsync();

        // The checklist is in-memory work; the credential read is a CLI call, so only when there is none yet or it has gone stale.
        Readiness.Rebuild();
        _ = Readiness.ReloadAsync();
        if (Credentials.IsStale)
        {
            _ = Credentials.RefreshAsync();
        }

        // One read of the guardrail posture when there is none yet or it has gone stale — a CLI call,
        // so never more often than that.
        if (_guardrailLoadedAt is null || DefenseClaw.Core.Time.WallClock.Elapsed(_guardrailLoadedAt.Value) > GuardrailFreshFor)
        {
            _ = LoadGuardrailAsync();
        }
    }

    protected override void OnDeactivated()
    {
        // A value typed into a credential's box is not left waiting while the page is away.
        Credentials.CancelEntries();

        _catalog.DefinitionChanged -= OnDefinitionChanged;
        Services.Monitor.StateChanged -= OnGatewayStateChanged;
        Services.Runtime.Changed -= OnRuntimeChanged;
        Services.RestartQueue.Changed -= OnRestartQueueChanged;
        _localStack.Changed -= OnProbeAnswerChanged;
        _terraform.Changed -= OnProbeAnswerChanged;
        Services.ConfigReloaded -= OnConfigReloaded;
    }

    /// <summary>
    /// Replaces the card list with one card per definition, and one more for the Splunk dashboards when the roster has the Splunk card:
    /// <c>setup splunk dashboards</c> is a nested command, so <c>setup --help</c> does not list it (<see cref="SplunkDashboards"/>).
    /// </summary>
    private void FillCards(IReadOnlyList<WizardDefinition> definitions)
    {
        _all.Clear();
        foreach (var definition in definitions)
        {
            AddCard(definition);
        }

        if (definitions.Any(d => string.Equals(d.Target, SplunkDashboards.ParentTarget, StringComparison.Ordinal)))
        {
            // What the catalog already read when it has (a re-entered hub, a re-read after it was warmed), the stub before that.
            AddCard(_catalog.Find(SplunkDashboards.Target) ?? SplunkDashboards.Stub());
        }
    }

    private void AddCard(WizardDefinition definition)
    {
        var card = new WizardCardViewModel(definition, () => Services.Installation.BlockedReason);
        card.OpensEditor = ResourceFor(card) is not null;

        // A card that needs Docker or Terraform starts from what is known now: the held answer, or "Checking for …" before the first.
        card.ApplyDocker(_localStack.Decision);
        card.ApplyTerraform(_terraform.Decision);
        card.ApplyState(WizardCardStates.For(card.Target, definition.Group, Services.Config));
        _all.Add(card);
    }

    /// <summary>
    /// Draws every card's state line again from config.yaml as the app holds it (CUST-271). Nothing runs: the lines are read from the file, so
    /// this is called when the hub comes on screen and whenever the file is read again.
    /// </summary>
    private void RefreshCardStates()
    {
        foreach (var card in _all)
        {
            _ = card.ApplyState(WizardCardStates.For(card.Target, card.Definition.Group, Services.Config));
        }
    }

    private void OnConfigReloaded(object? sender, EventArgs e) => RefreshCardStates();

    /// <summary>
    /// Gates every card that needs Docker (the local observability stack's) on the shared Docker look. Returns true when a card's
    /// availability moved, which is when its group and the certified filter may have changed too.
    /// </summary>
    private bool SyncLocalStack()
    {
        var decision = _localStack.Decision;
        var changed = false;
        foreach (var card in _all)
        {
            changed |= card.ApplyDocker(decision);
        }

        return changed;
    }

    /// <summary>
    /// Gates every card that needs Terraform (the Splunk dashboards') on the shared Terraform look. Returns true when a card's availability
    /// moved, which is when its group and the certified filter may have changed too.
    /// </summary>
    private bool SyncTerraform()
    {
        var decision = _terraform.Decision;
        var changed = false;
        foreach (var card in _all)
        {
            changed |= card.ApplyTerraform(decision);
        }

        return changed;
    }

    /// <summary>One of the shared looks (Docker, Terraform) changed its answer.</summary>
    private void OnProbeAnswerChanged(object? sender, EventArgs e)
    {
        // Raised from the probe's continuation, which is a pool thread: the cards and the grouped list are bound state.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyProbeAnswers();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(ApplyProbeAnswers);
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post; a dropped update on the way out is fine.
        }
    }

    private void ApplyProbeAnswers()
    {
        var moved = SyncLocalStack();
        moved |= SyncTerraform();
        if (moved)
        {
            ApplyFilters();
        }
    }

    /// <summary>
    /// Brings every card up to the catalog's current definition for its target. Definitions are
    /// replaced wholesale and never mutated, so reference equality is exactly "is this card
    /// behind". Returns true if any card moved, which is when group membership and the
    /// certified filter may have changed too.
    /// </summary>
    private bool SyncCardsWithCatalog()
    {
        var changed = false;
        foreach (var card in _all)
        {
            if (_catalog.Find(card.Target) is { } definition && !ReferenceEquals(card.Definition, definition))
            {
                card.Apply(definition);
                changed = true;
            }
        }

        return changed;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnCertifiedOnlyChanged(bool value) => ApplyFilters();

    [RelayCommand]
    private async Task LaunchAsync(WizardCardViewModel? card)
    {
        if (card is null || !card.CanLaunch)
        {
            return;
        }

        // The list-first editors (CUST-270): the tile opens the list, and the list's Add opens the wizard on add.
        if (ResourceFor(card) is { } resource && _openResourceEditor is { } openEditor)
        {
            openEditor(resource);
            return;
        }

        // The notification tiles open the routing dialog (CUST-271): the master switch and every category in one reviewed run.
        if (NotificationRoutingViewModel.Handles(card.Target))
        {
            Routing.Open();
            return;
        }

        card.IsOpening = true;
        try
        {
            await WizardLauncher
                .ShowAsync(Services, card.Target, Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        finally
        {
            card.IsOpening = false;
        }

        // A wizard that ran may have changed the roster and the guardrail; the monitor's next poll would
        // catch the roster, but re-reading now makes the change visible immediately.
        BuildConnectors();
        _ = LoadGuardrailAsync();
    }

    /// <summary>
    /// The page-level refresh (F5 and the header button): the guardrail posture, the connector
    /// roster, and the looks at Docker and Terraform that gate the local observability stack's card and the Splunk dashboards'. Deliberately
    /// not the catalog — that is thirty-odd CLI help probes; see <see cref="ReloadCatalogCommand"/>.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        BuildConnectors();
        Readiness.Rebuild();

        // "I just installed Terraform": this app's PATH is the one it started with, and the lookups remember where they found (or did not find)
        // an executable. Forgetting them re-reads the machine and user PATH from the registry, which is what makes an install made since the
        // app started visible to the looks below.
        Services.Paths.InvalidateExecutableCache();

        // "I started Docker Desktop": the stack's card is gated on a look at Docker, and this is how it is looked at again now. Not awaited:
        // a starting engine can take its ten seconds to answer, and the refresh button must not wait on it. The card follows when it lands.
        // The same for "I installed Terraform" and the dashboards' card.
        _ = _localStack.RefreshAsync();
        _ = _terraform.RefreshAsync();

        var credentials = Credentials.RefreshAsync();
        var doctor = Readiness.ReloadAsync();
        await LoadGuardrailAsync().ConfigureAwait(true);
        await Task.WhenAll(credentials, doctor).ConfigureAwait(true);
    }

    /// <summary>Forgets every cached help screen and re-asks the CLI which setup targets it has.</summary>
    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        // A second press while a read is in flight would interleave two reloads into one list.
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusNote = "Re-reading the setup catalog from the CLI…";
        EmptyState = SetupEmptyState.None;
        Groups.Clear();
        _all.Clear();

        try
        {
            // Reload forgets the cached help screens too, so this genuinely re-asks the CLI.
            var definitions = await _catalog.ReloadAsync().ConfigureAwait(true);
            FillCards(definitions);
        }
        finally
        {
            IsLoading = false;
        }

        HasLoadError = _catalog.LoadError is { Length: > 0 };
        LoadError = _catalog.LoadError ?? string.Empty;
        ApplyFilters();
        BuildConnectors();

        _ = WarmAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        CertifiedOnly = false;
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        var targets = _all.Select(c => c.Target).ToArray();
        var probes = targets.Select(target => _catalog.EnsureDetailAsync(target, cancellationToken));

        try
        {
            await Task.WhenAll(probes).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateStatusNote();
    }

    private void OnDefinitionChanged(object? sender, WizardDefinitionChangedEventArgs e)
    {
        // Raised from the probe's continuation, which may be any thread pool thread. Attached
        // only while active; an event already queued when the panel goes away still lands here
        // and is applied in full, so the catch-up in OnActivated never finds a half-applied card.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var card = _all.FirstOrDefault(c => string.Equals(c.Target, e.Definition.Target, StringComparison.Ordinal));
            card?.Apply(e.Definition);
            ApplyFilters();
        });
    }

    private void OnGatewayStateChanged(object? sender, GatewaySnapshotEventArgs e)
    {
        BuildConnectors();
        Readiness.Rebuild();

        // Whether the gateway can be asked to restart (Restart now) follows its state.
        RefreshRestartBanner();
    }

    /// <summary>
    /// The roster the wizards act on: connector names from config.yaml unioned with what
    /// <c>/health</c> reports, each carrying its enforcement mode and hook fail-mode.
    /// Observe paired with fail-closed is called out, because it is the recurring bad default
    /// this project keeps running into: the connector records rather than blocks, yet a
    /// gateway error still blocks the tool call.
    /// </summary>
    private void BuildConnectors()
    {
        var config = Services.Config.Config;
        var snapshot = Services.Monitor.Current;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desired = new List<ConnectorChipViewModel>();

        foreach (var (name, settings) in config.Guardrail.Connectors.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(name))
            {
                continue;
            }

            desired.Add(new ConnectorChipViewModel(
                name,
                settings.Mode,
                settings.HookFailMode,
                settings.HasFailModeMismatch,
                fromConfig: true));
        }

        foreach (var name in snapshot.ActiveConnectors)
        {
            if (seen.Add(name))
            {
                desired.Add(new ConnectorChipViewModel(name, null, null, false, fromConfig: false));
            }
        }

        // Merged, not rebuilt: the roster is re-derived on every state change and on every
        // activation, and almost always comes out identical. Clear() + Add() would tear down and
        // re-create every chip's visuals each time for nothing.
        SyncCollection(Connectors, desired, chip => chip.Name, static (existing, wanted) => existing.IsSameAs(wanted));

        ConnectorNote = Connectors.Count == 0
            ? "No connectors are configured yet. Start with a certified one — Claude Code or Codex on this platform."
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} connector(s) configured. Mode and fail-mode come from guardrail.connectors in config.yaml; names without chips were reported by /health only.",
                Connectors.Count);

        OnPropertyChanged(nameof(HasConnectors));
    }

    private void ApplyFilters()
    {
        var needle = SearchText.Trim();

        // "Hide uncertified connectors": what it removes is a connector the CLI says is
        // not_certified / unsupported here, or one whose help is not read yet. A flow that
        // is not a connector (NotApplicable) has nothing to certify, so the filter leaves it
        // alone — exactly what it did before those cards stopped being mislabelled Certified.
        var matching = _all.Where(card =>
            (!CertifiedOnly || card.PlatformStatus is PlatformStatus.Certified or PlatformStatus.NotApplicable) &&
            (needle.Length == 0 || card.Matches(needle)))
            .ToList();

        Groups.Clear();
        foreach (var group in matching
            .GroupBy(c => c.Group, StringComparer.Ordinal)
            .OrderBy(g => WizardGroups.IndexOf(g.Key)))
        {
            Groups.Add(new WizardGroupViewModel(group.Key, group.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)));
        }

        EmptyState = ClassifyEmpty(IsLoading, HasLoadError, _all.Count, matching.Count);
        UpdateStatusNote();
    }

    /// <summary>
    /// Which empty state the grid is in. The order is the point: a failed read leaves no cards, and "the search hides
    /// everything" is only true when there is something to hide.
    /// </summary>
    internal static SetupEmptyState ClassifyEmpty(bool isLoading, bool hasLoadError, int knownTargets, int shownTargets)
    {
        if (isLoading || shownTargets > 0)
        {
            return SetupEmptyState.None;
        }

        if (knownTargets > 0)
        {
            return SetupEmptyState.NoMatch;
        }

        return hasLoadError ? SetupEmptyState.LoadFailed : SetupEmptyState.NoTargets;
    }

    private void UpdateStatusNote()
    {
        if (IsLoading)
        {
            return;
        }

        // Nothing to count: "0 of 0 setup targets shown" reads as a filter result, and it is not one.
        if (_all.Count == 0)
        {
            StatusNote = HasLoadError
                ? "The setup catalog could not be read."
                : "The CLI listed no setup targets.";
            return;
        }

        var shown = Groups.Sum(g => g.Cards.Count);
        var certified = _all.Count(c => c.PlatformStatus == PlatformStatus.Certified);
        var pending = _all.Count(c => !c.IsDetailLoaded);

        StatusNote = string.Format(
            CultureInfo.CurrentCulture,
            "{0} of {1} setup target(s) shown · {2} certified connector(s) on Windows · {3} help screen(s) read{4}",
            shown,
            _all.Count,
            certified,
            _catalog.ProbeCount,
            pending > 0 ? $" · {pending} still loading" : string.Empty);
    }

    // ------------------------------------------------------------------ guardrail quick controls
    //
    // `defenseclaw guardrail status` is text (no --json), so it is parsed by GuardrailStatusParser and
    // shown verbatim as well. The verbs are the ones the CLI documents for day-to-day posture:
    // `guardrail enable|disable [--connector X] [--restart|--no-restart] [--yes]` and
    // `guardrail fail-mode open|closed [--connector X] [--restart|--no-restart] [--yes]` (verified against
    // each verb's --help). All of them restart the gateway by default, so every one goes through a review
    // that shows the exact argv, the CommandTiers tier, and offers --no-restart.

    private DateTimeOffset? _guardrailLoadedAt;
    private bool _guardrailReading;
    private string _pendingGuardrailVerb = string.Empty;
    private string? _pendingGuardrailArg;
    private string _guardrailReviewHeading = string.Empty;
    private string _guardrailReviewNote = string.Empty;

    [ObservableProperty]
    private bool _isGuardrailBusy;

    [ObservableProperty]
    private string _guardrailHeadline = "The guardrail posture has not been read yet.";

    /// <summary>Ok / Warn / Neutral — tone of the headline badge.</summary>
    [ObservableProperty]
    private string _guardrailHeadlineKey = "Neutral";

    [ObservableProperty]
    private string _guardrailAsOf = string.Empty;

    [ObservableProperty]
    private string _guardrailError = string.Empty;

    [ObservableProperty]
    private bool _hasGuardrailError;

    [ObservableProperty]
    private string _guardrailRaw = string.Empty;

    [ObservableProperty]
    private bool _showGuardrailRaw;

    /// <summary>A read succeeded and found no connector: a normal fresh install, not a fault.</summary>
    [ObservableProperty]
    private bool _showNoGuardrailRoster;

    [ObservableProperty]
    private bool _showEnableGuardrail = true;

    [ObservableProperty]
    private bool _showDisableGuardrail = true;

    [ObservableProperty]
    private GuardrailScopeOption? _selectedGuardrailScope;

    [ObservableProperty]
    private bool _isGuardrailReviewOpen;

    /// <summary>
    /// What the guardrail review dialog shows, in the model every confirmation surface shares. Rebuilt whenever the
    /// restart checkbox changes the argv; <c>disable</c> (which tears the live hooks down) is floored to Destructive.
    /// </summary>
    [ObservableProperty]
    private CommandReview? _guardrailReview;

    /// <summary>The review's "restart the gateway" checkbox; off adds <c>--no-restart</c>.</summary>
    [ObservableProperty]
    private bool _guardrailRestartAfter = true;

    [ObservableProperty]
    private bool _isGuardrailRunning;

    [ObservableProperty]
    private bool _hasGuardrailResult;

    [ObservableProperty]
    private string _guardrailResultHeading = string.Empty;

    [ObservableProperty]
    private string _guardrailResultBadge = string.Empty;

    [ObservableProperty]
    private string _guardrailResultKey = "Neutral";

    [ObservableProperty]
    private string _guardrailResultText = string.Empty;

    /// <summary>The roster rows of the last successful read.</summary>
    public ObservableCollection<GuardrailRowViewModel> GuardrailRows { get; } = new();

    /// <summary>Drift and other "!" notes the CLI printed under the roster.</summary>
    public ObservableCollection<string> GuardrailWarnings { get; } = new();

    /// <summary>"All connectors" plus one entry per connector in the roster; only offered when there are two or more.</summary>
    public ObservableCollection<GuardrailScopeOption> GuardrailScopes { get; } = new();

    public bool HasGuardrailRows => GuardrailRows.Count > 0;

    public bool HasGuardrailWarnings => GuardrailWarnings.Count > 0;

    /// <summary><c>--connector</c> is for multi-connector installs; with one connector there is nothing to scope.</summary>
    public bool HasMultipleGuardrailConnectors => GuardrailRows.Count > 1;

    /// <summary>Controls are off while a read or a change is in flight, and for a read-only (managed or invalid) installation.</summary>
    public bool CanUseGuardrailControls => !IsGuardrailBusy && !IsGuardrailRunning && Services.Installation.IsMutable;

    /// <summary>The banner at the top of the page shows: the installation is managed or invalid, and every control below that changes something is off.</summary>
    public bool HasInstallationBlock => InstallationBlockedReason is not null;

    /// <summary>The installation turned read-only (or writable) while the page was open: the tiles, the guardrail buttons and the credential rows are drawn again.</summary>
    protected override void OnInstallationChanged()
    {
        foreach (var card in _all)
        {
            card.RefreshInstallation();
        }

        OnPropertyChanged(nameof(CanUseGuardrailControls));
        OnPropertyChanged(nameof(HasInstallationBlock));
        Credentials.RefreshInstallation();
        RefreshGuardrailReview();
        RefreshRestartBanner();
    }

    partial void OnIsGuardrailBusyChanged(bool value) => OnPropertyChanged(nameof(CanUseGuardrailControls));

    partial void OnIsGuardrailRunningChanged(bool value) => OnPropertyChanged(nameof(CanUseGuardrailControls));

    partial void OnGuardrailRestartAfterChanged(bool value) => RefreshGuardrailReview();

    /// <summary>Reads <c>guardrail status</c> once. Overlapping calls collapse into the one in flight.</summary>
    private async Task LoadGuardrailAsync()
    {
        if (_guardrailReading)
        {
            return;
        }

        _guardrailReading = true;
        IsGuardrailBusy = true;

        try
        {
            var invocation = await Services.Cli.RunAsync(new[] { "guardrail", "status" }).ConfigureAwait(true);
            ApplyGuardrailRead(invocation);
        }
        catch (CliNotFoundException ex)
        {
            SetGuardrailError("defenseclaw was not found, so the guardrail posture cannot be read. " + ex.Message);
        }
        finally
        {
            _guardrailReading = false;
            IsGuardrailBusy = false;
        }
    }

    private void ApplyGuardrailRead(CliInvocation invocation)
    {
        var text = JoinStdout(invocation);

        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            SetGuardrailError("guardrail status did not finish: " + reason + ".");
            return;
        }

        if (invocation.ExitCode is not 0)
        {
            var detail = string.Join(' ', LastLines(invocation, 3));
            var code = invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "without a code";
            SetGuardrailError($"guardrail status exited {code}. {detail}".Trim());
            return;
        }

        var status = GuardrailStatusParser.Parse(text);
        _guardrailLoadedAt = DateTimeOffset.UtcNow;
        GuardrailAsOf = "as of " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        GuardrailError = string.Empty;
        HasGuardrailError = false;
        GuardrailRaw = status.Raw;

        var keepScope = SelectedGuardrailScope?.Key ?? string.Empty;

        var rows = status.Connectors.Select(r => new GuardrailRowViewModel(r)).ToList();
        SyncCollection(GuardrailRows, rows, r => r.Key + "|" + r.Name, static (a, b) => a.IsSameAs(b));

        GuardrailWarnings.Clear();
        foreach (var warning in status.Warnings)
        {
            GuardrailWarnings.Add(warning);
        }

        GuardrailScopes.Clear();
        GuardrailScopes.Add(new GuardrailScopeOption("All connectors", string.Empty));
        foreach (var row in rows.Where(r => r.Key.Length > 0))
        {
            GuardrailScopes.Add(new GuardrailScopeOption(row.Name.Length > 0 ? row.Name : row.Key, row.Key));
        }

        SelectedGuardrailScope = GuardrailScopes.FirstOrDefault(s => s.Key == keepScope) ?? GuardrailScopes[0];

        (GuardrailHeadline, GuardrailHeadlineKey) = status.Enabled switch
        {
            true => ("The guardrail is enabled.", "Ok"),
            false => ("The guardrail is disabled: no connector is being enforced.", "Warn"),
            _ => ("The CLI's status text could not be interpreted; the verbatim output is below.", "Neutral"),
        };

        ShowEnableGuardrail = status.Enabled != true;
        ShowDisableGuardrail = status.Enabled != false;
        ShowNoGuardrailRoster = rows.Count == 0;

        OnPropertyChanged(nameof(HasGuardrailRows));
        OnPropertyChanged(nameof(HasGuardrailWarnings));
        OnPropertyChanged(nameof(HasMultipleGuardrailConnectors));
    }

    private void SetGuardrailError(string message)
    {
        GuardrailError = message;
        HasGuardrailError = true;
    }

    /// <summary>Stdout lines of a finished invocation, one string.</summary>
    private static string JoinStdout(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    /// <summary>The last <paramref name="count"/> non-empty lines of either stream.</summary>
    private static IEnumerable<string> LastLines(CliInvocation invocation, int count) => invocation.OutputLines
        .Select(l => l.Text.Trim())
        .Where(t => t.Length > 0)
        .TakeLast(count);

    [RelayCommand]
    private void EnableGuardrail() => BeginGuardrailReview("enable", null);

    [RelayCommand]
    private void DisableGuardrail() => BeginGuardrailReview("disable", null);

    [RelayCommand]
    private void SetFailModeOpen() => BeginGuardrailReview("fail-mode", "open");

    [RelayCommand]
    private void SetFailModeClosed() => BeginGuardrailReview("fail-mode", "closed");

    [RelayCommand]
    private void CancelGuardrailReview() => IsGuardrailReviewOpen = false;

    [RelayCommand]
    private void DismissGuardrailResult() => HasGuardrailResult = false;

    private void BeginGuardrailReview(string verb, string? arg)
    {
        if (!CanUseGuardrailControls)
        {
            return;
        }

        _pendingGuardrailVerb = verb;
        _pendingGuardrailArg = arg;
        GuardrailRestartAfter = true;
        HasGuardrailResult = false;

        var scope = SelectedGuardrailScope is { Key.Length: > 0 } s ? $" for {s.Label}" : string.Empty;
        _guardrailReviewHeading = verb switch
        {
            "enable" => "Enable the guardrail" + scope + "?",
            "disable" => "Disable the guardrail" + scope + "?",
            _ => $"Set the hook fail mode to {arg}{scope}?",
        };

        _guardrailReviewNote = (verb, arg) switch
        {
            ("disable", _) => "Disabling tears down the connector hooks (for example the live Claude Code hooks), so agents on this machine " +
                              "run without DefenseClaw protection until you enable the guardrail again. The connector's policy is kept.",
            ("enable", _) => "Re-enables the guardrail from the existing configuration and runs connector setup at the next gateway start.",
            (_, "closed") => "Closed blocks a tool call or prompt whenever the gateway answers with an error or cannot be reached — " +
                             "including while the gateway restarts. Use it where every prompt must be inspected.",
            _ => "Open allows the tool call or prompt and logs the failure, so a misbehaving gateway never blocks an agent. Recommended for almost all installs.",
        };

        RefreshGuardrailReview();
        IsGuardrailReviewOpen = true;
    }

    /// <summary>The exact argv for the pending verb, from the review's current choices.</summary>
    private string[] BuildGuardrailArgv()
    {
        var argv = new List<string> { "guardrail", _pendingGuardrailVerb };
        if (_pendingGuardrailArg is not null)
        {
            argv.Add(_pendingGuardrailArg);
        }

        // These verbs ask "Continue?" without it, and the app has no way to answer.
        argv.Add("--yes");

        if (SelectedGuardrailScope is { Key.Length: > 0 } scope)
        {
            argv.Add("--connector");
            argv.Add(scope.Key);
        }

        if (!GuardrailRestartAfter)
        {
            argv.Add("--no-restart");
        }

        return argv.ToArray();
    }

    private void RefreshGuardrailReview()
    {
        if (_pendingGuardrailVerb.Length == 0)
        {
            return;
        }

        GuardrailReview = new CommandReview
        {
            Title = _guardrailReviewHeading,
            Summary = _guardrailReviewNote,
            Steps = new[]
            {
                new CommandReviewStep(
                    BuildGuardrailArgv(),
                    floor: _pendingGuardrailVerb == "disable" ? CommandTier.Destructive : CommandTier.StateChanging),
            },
            RestartsGateway = GuardrailRestartAfter,
            Warnings = new[]
            {
                GuardrailRestartAfter
                    ? CommandReviewWarning.GatewayRestart()
                    : new CommandReviewWarning(
                        "Gateway not restarted",
                        "The gateway is not restarted, so hooks are not regenerated until it next restarts: the change is saved but not yet in effect."),
            },
        }.GuardedBy(Services.Installation);
    }

    [RelayCommand]
    private async Task ConfirmGuardrailAsync()
    {
        if (!IsGuardrailReviewOpen || IsGuardrailRunning || GuardrailReview is { IsBlocked: true })
        {
            return;
        }

        var argv = BuildGuardrailArgv();
        IsGuardrailRunning = true;

        try
        {
            var invocation = await Services.Cli.RunAsync(argv).ConfigureAwait(true);
            IsGuardrailReviewOpen = false;
            ShowGuardrailResult(argv, invocation);

            // Follow-ups are gated on the previous exit code: a failed change leaves the roster as it was
            // read, and re-reading it would only show the same thing while hiding the error.
            if (invocation.ExitCode is 0 && invocation.FailureReason is null)
            {
                // (A change saved with --no-restart is not in effect until the gateway restarts: the runner told Services.RestartQueue as the run
                // finished, and the banner and the readiness row show it. A restarting one cleared it.)
                IsGuardrailRunning = false;
                await LoadGuardrailAsync().ConfigureAwait(true);
            }
        }
        catch (CliNotFoundException ex)
        {
            IsGuardrailReviewOpen = false;
            HasGuardrailResult = true;
            GuardrailResultHeading = "defenseclaw was not found";
            GuardrailResultBadge = "not run";
            GuardrailResultKey = "Bad";
            GuardrailResultText = ex.Message;
        }
        finally
        {
            IsGuardrailRunning = false;
        }
    }

    private void ShowGuardrailResult(IReadOnlyList<string> argv, CliInvocation invocation)
    {
        HasGuardrailResult = true;
        GuardrailResultHeading = "defenseclaw " + string.Join(' ', argv) + " finished";

        (GuardrailResultBadge, GuardrailResultKey) = (invocation.FailureReason, invocation.ExitCode) switch
        {
            ({ Length: > 0 } reason, _) => (reason.StartsWith("cancelled", StringComparison.Ordinal) ? "cancelled" : "failed", "Warn"),
            (_, 0) => ("exit 0", "Ok"),
            (_, { } code) => ("exit " + code.ToString(CultureInfo.CurrentCulture), "Bad"),
            _ => ("exit unknown", "Neutral"),
        };

        var lines = invocation.OutputLines.Select(l => l.Text.TrimEnd()).Where(t => t.Length > 0).TakeLast(40);
        GuardrailResultText = invocation.FailureReason is { Length: > 0 } failure
            ? failure + "\n" + string.Join('\n', lines)
            : string.Join('\n', lines);
    }

}

/// <summary>One entry in the guardrail scope combo: a connector key for <c>--connector</c>, or empty for all of them.</summary>
public sealed record GuardrailScopeOption(string Label, string Key)
{
    public override string ToString() => Label;
}

/// <summary>One connector in the guardrail roster: its posture as <c>guardrail status</c> printed it.</summary>
public sealed class GuardrailRowViewModel
{
    private readonly GuardrailConnectorRow _row;

    public GuardrailRowViewModel(GuardrailConnectorRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public string Name => _row.Name.Length > 0 ? _row.Name : _row.Key;

    public string Key => _row.Key;

    public string State => _row.State;

    public string Mode => _row.Mode;

    public string Fail => _row.Fail;

    /// <summary>Ok when enabled, Warn otherwise — the state chip's tone.</summary>
    public string StateKey => string.Equals(State, "enabled", StringComparison.OrdinalIgnoreCase) ? "Ok" : "Warn";

    /// <summary>Observe + fail-closed is the recurring bad default: records only, yet still blocks on a gateway error.</summary>
    public bool HasMismatch =>
        string.Equals(Mode, "observe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Fail, "closed", StringComparison.OrdinalIgnoreCase);

    public string FailKey => HasMismatch ? "Warn" : "Neutral";

    /// <summary>The remaining columns as one line: "rule pack default · HILT off · scan regex_only · judge off".</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            AddPart(parts, "rule pack", _row.Column("Rule pack"));
            AddPart(parts, "HILT", _row.Column("HILT"));
            AddPart(parts, "scan", _row.Column("Scan"));
            AddPart(parts, "judge", _row.Column("Judge"));
            return string.Join(" · ", parts);
        }
    }

    public string MismatchNote =>
        "observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.";

    public bool IsSameAs(GuardrailRowViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _row.Columns.Count == other._row.Columns.Count &&
               _row.Columns.Zip(other._row.Columns).All(p => p.First.Key == p.Second.Key && p.First.Value == p.Second.Value);
    }

    private static void AddPart(List<string> parts, string label, string value)
    {
        if (value.Length > 0)
        {
            parts.Add(label + " " + value);
        }
    }

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() =>
        $"{Name}: {State}, mode {Mode}, fail-{Fail}" + (HasMismatch ? ". Observe with fail-closed." : string.Empty);
}

/// <summary>One hub section: a <see cref="WizardGroups"/> bucket and its cards.</summary>
public sealed class WizardGroupViewModel
{
    public WizardGroupViewModel(string name, IEnumerable<WizardCardViewModel> cards)
    {
        Name = name;
        Cards = new ObservableCollection<WizardCardViewModel>(cards);
    }

    public string Name { get; }

    public ObservableCollection<WizardCardViewModel> Cards { get; }

    public string CountText => Cards.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>What a screen reader announces for the group heading.</summary>
    public string AutomationName => $"{Name}, {Cards.Count} setup target(s)";

    public override string ToString() => AutomationName;
}

/// <summary>One wizard card. Rebuilt in place as the per-target help lands.</summary>
public sealed partial class WizardCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _badge = string.Empty;

    /// <summary>Ok / Warn / Bad / Neutral — the badge tone key (<c>DcBadge Tag</c>).</summary>
    [ObservableProperty]
    private string _badgeKey = "Neutral";

    [ObservableProperty]
    private bool _isDetailLoaded;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string _stepNote = string.Empty;

    /// <summary>Why this card cannot be launched on Windows, or empty when it can.</summary>
    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    // Why Docker rules a card that needs it out right now (the live probe's sentence), or empty. Held apart from the policy's reason so a
    // definition that lands later (Apply) neither forgets it nor overrides a reason the policy gives.
    private string _dockerReason = string.Empty;

    // The same for Terraform and the card that needs it (the Splunk dashboards').
    private string _terraformReason = string.Empty;

    private readonly Func<string?>? _installationBlockedReason;

    /// <param name="definition">What the card shows.</param>
    /// <param name="installationBlockedReason">
    /// Why nothing may be changed on the installation right now (it is managed or invalid), asked each time the card is drawn; null, or one that
    /// answers null, means the wizard may be opened. A card the platform policy cannot offer at all, or that Docker (or Terraform) rules out
    /// right now, is a different thing (<see cref="UnavailableReason"/>); the installation's reason comes first when both apply.
    /// </param>
    public WizardCardViewModel(WizardDefinition definition, Func<string?>? installationBlockedReason = null)
    {
        _installationBlockedReason = installationBlockedReason;
        Target = definition?.Target ?? throw new ArgumentNullException(nameof(definition));
        Group = definition.Group;
        Apply(definition);
    }

    /// <summary>Why the wizard may not be opened because the installation is read-only; null while it may. The card keeps its place in its group either way.</summary>
    public string? InstallationBlockedReason => _installationBlockedReason?.Invoke();

    /// <summary>The tile opens its wizard: the platform offers it and the installation may be changed (every wizard ends in a change). A tile that opens a list-first editor (<see cref="OpensEditor"/>) is open on a read-only installation too: the editor only reads there.</summary>
    public bool CanLaunch => IsAvailable && (InstallationBlockedReason is null || OpensEditor);

    private bool _opensEditor;

    /// <summary>
    /// The tile opens a list-first editor (observability destinations, webhooks, trusted binary paths; CUST-270) rather than the wizard. Set by the
    /// hub when the shell has wired the editors; the editor lists whatever the installation, and turns every change off on a read-only one.
    /// </summary>
    internal bool OpensEditor
    {
        get => _opensEditor;
        set
        {
            if (_opensEditor == value)
            {
                return;
            }

            _opensEditor = value;
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(TileToolTip));
        }
    }

    /// <summary>The installation turned read-only (or writable): the tile is drawn again.</summary>
    public void RefreshInstallation()
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(InstallationBlockedReason));
        OnPropertyChanged(nameof(TileToolTip));
    }

    public string Target { get; }

    public string Group { get; private set; }

    public PlatformStatus PlatformStatus { get; private set; }

    /// <summary>
    /// The definition this card currently shows. Definitions are immutable and replaced
    /// wholesale, so comparing this by reference with the catalog's says whether the card is behind.
    /// </summary>
    public WizardDefinition Definition { get; private set; } = null!;

    /// <summary>The command this card runs, shown small on the card so nothing is a surprise.</summary>
    public string CommandHint => "defenseclaw setup " + Target;

    private CardState? _state;

    /// <summary>What config.yaml says about this card's subject today (<c>Guardrail on · observe</c>), or empty (CUST-271).</summary>
    public string StateLine => _state?.Text ?? string.Empty;

    /// <summary>The longer form of <see cref="StateLine"/>, for the tooltip and the screen reader.</summary>
    public string StateDetail => _state?.Detail ?? string.Empty;

    /// <summary>The tile shows a state line: the target has one and the card can be opened here.</summary>
    public bool HasStateLine => _state is not null && IsAvailable;

    /// <summary>Takes in the card's state line (null: none). Returns true when it changed.</summary>
    internal bool ApplyState(CardState? state)
    {
        if (Equals(_state, state))
        {
            return false;
        }

        _state = state;
        OnPropertyChanged(nameof(StateLine));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(HasStateLine));
        OnPropertyChanged(nameof(TileToolTip));
        OnPropertyChanged(nameof(AutomationName));
        return true;
    }

    public bool IsAvailable => UnavailableReason.Length == 0;

    public bool HasUnavailableReason => UnavailableReason.Length > 0;

    /// <summary>The name a screen reader announces for the card.</summary>
    public string AutomationName => IsAvailable
        ? $"{Title} setup. {Badge}." + (HasStateLine ? " " + StateDetail : string.Empty)
        : $"{Title} setup, not available on this machine. {UnavailableReason}";

    /// <summary>The name of the card's button: "Configure Claude Code" — the bare word alone says nothing in a list of cards.</summary>
    public string LaunchAutomationName => "Configure " + Title;

    /// <summary>The Fluent glyph the tile wears (see <see cref="WizardTileIcons"/>).</summary>
    public Wpf.Ui.Controls.SymbolRegular Icon { get; private set; }

    /// <summary>
    /// The tile's tooltip: the command it runs, then why it cannot be opened when that is so - one reason. A read-only installation (managed or
    /// invalid) comes first for any wizard the platform offers, including one that only Docker being down (the local stack's) or Terraform being
    /// missing (the Splunk dashboards') rules out right now: Docker coming up, or Terraform being installed, would not make it runnable. A wizard
    /// this platform does not offer at all, or that the installed CLI has no command for, says that.
    /// </summary>
    public string TileToolTip => HasStateLine && BaseToolTip.StartsWith(CommandHint, StringComparison.Ordinal)
        ? CommandHint + Environment.NewLine + StateDetail + BaseToolTip[CommandHint.Length..] // the state under the command; a reason or a note stays last
        : BaseToolTip;

    private string BaseToolTip => OpensEditor && IsAvailable
        ? CommandHint + Environment.NewLine + "Opens the list; Add opens the wizard." +
          (InstallationBlockedReason is { } readOnly ? " Changes are off: " + readOnly : string.Empty)
        : InstallationBlockedReason is { } blocked && (IsAvailable || UnavailableOnlyForLook)
        ? CommandHint + Environment.NewLine + blocked
        : !IsAvailable
            ? CommandHint + Environment.NewLine + UnavailableReason
            : ShowTileBadge ? CommandHint + Environment.NewLine + Badge : CommandHint;

    /// <summary>
    /// True when the only thing keeping this card from opening is a live look at this machine - Docker's or Terraform's - and so the platform
    /// and the installed CLI offer the wizard.
    /// </summary>
    private bool UnavailableOnlyForLook =>
        (_dockerReason.Length > 0 && string.Equals(UnavailableReason, _dockerReason, StringComparison.Ordinal)) ||
        (_terraformReason.Length > 0 && string.Equals(UnavailableReason, _terraformReason, StringComparison.Ordinal));

    /// <summary>What the tile says under its title: the reason when it cannot be opened, otherwise the CLI's own summary.</summary>
    /// <summary>Certification only says something about a connector; the other wizards' tiles stay unbadged to leave the title its room.</summary>
    public bool ShowTileBadge => IsAvailable && PlatformStatus != PlatformStatus.NotApplicable;

    public string TileBlurb => IsAvailable ? Summary : UnavailableReason;

    public void Apply(WizardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        PlatformStatus = definition.PlatformStatus;
        Title = definition.Title;
        Icon = WizardTileIcons.For(Target, definition.Group);
        Summary = definition.Description;
        Badge = PlatformStatusText.Badge(definition.PlatformStatus);
        BadgeKey = PlatformStatusText.Key(definition.PlatformStatus);
        IsDetailLoaded = definition.IsDetailLoaded;

        // In order of how final each answer is: the platform's (the CLI says it is unsupported here), the CLI's (it has no such command), then
        // the live looks (Docker, Terraform), which a card needs at most one of.
        UnavailableReason = WizardWindowsPolicy.UnavailableReason(Target, definition.PlatformStatus)
            ?? (definition.UnavailableReason.Length > 0 ? definition.UnavailableReason : null)
            ?? (_dockerReason.Length > 0 ? _dockerReason : _terraformReason);
        Group = IsAvailable ? definition.Group : WizardGroups.Unavailable;

        StepNote = definition.DetailError is { Length: > 0 } error
            ? error
            : definition.IsDetailLoaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} step(s) · {1}",
                    definition.Steps.Count,
                    definition.IsCurated ? "curated layout" : "generated from --help")
                : "reading --help…";

        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(HasUnavailableReason));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(LaunchAutomationName));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(TileToolTip));
        OnPropertyChanged(nameof(TileBlurb));
        OnPropertyChanged(nameof(ShowTileBadge));
    }

    /// <summary>
    /// Gates the card on the shared Docker look, for a target that needs Docker (<see cref="WizardWindowsPolicy.NeedsDocker"/>; any other
    /// card ignores this): open, the card is launchable; closed, it moves to the "not available" group and says the look's reason.
    /// Returns true when that changed, which is when the hub regroups.
    /// </summary>
    internal bool ApplyDocker(GateDecision decision)
    {
        if (!WizardWindowsPolicy.NeedsDocker(Target))
        {
            return false;
        }

        var reason = decision.IsAvailable ? string.Empty : decision.Reason ?? "Docker is not available.";
        if (string.Equals(reason, _dockerReason, StringComparison.Ordinal))
        {
            return false;
        }

        _dockerReason = reason;
        Apply(Definition);
        return true;
    }

    /// <summary>
    /// <see cref="ApplyDocker"/> for the card that needs Terraform (<see cref="WizardWindowsPolicy.NeedsTerraform"/>; any other card
    /// ignores this): open, the card is launchable; closed, it moves to the "not available" group and says the look's reason.
    /// Returns true when that changed, which is when the hub regroups.
    /// </summary>
    internal bool ApplyTerraform(GateDecision decision)
    {
        if (!WizardWindowsPolicy.NeedsTerraform(Target))
        {
            return false;
        }

        var reason = decision.IsAvailable ? string.Empty : decision.Reason ?? "Terraform is not available.";
        if (string.Equals(reason, _terraformReason, StringComparison.Ordinal))
        {
            return false;
        }

        _terraformReason = reason;
        Apply(Definition);
        return true;
    }

    public bool Matches(string needle) =>
        Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Target.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Summary.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Group.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => AutomationName;
}

/// <summary>One connector in the roster strip, with its mode and hook fail-mode.</summary>
public sealed class ConnectorChipViewModel
{
    public ConnectorChipViewModel(string name, string? mode, string? failMode, bool hasMismatch, bool fromConfig)
    {
        Name = name;
        Mode = string.IsNullOrWhiteSpace(mode) ? "mode unset" : mode.Trim().ToLowerInvariant();
        FailMode = string.IsNullOrWhiteSpace(failMode) ? "fail-mode unset" : "fail-" + failMode.Trim().ToLowerInvariant();
        HasMismatch = hasMismatch;
        FromConfig = fromConfig;
    }

    public string Name { get; }

    public string Mode { get; }

    public string FailMode { get; }

    /// <summary>Observe mode paired with a fail-closed hook: records nothing, blocks on error.</summary>
    public bool HasMismatch { get; }

    public bool FromConfig { get; }

    public string MismatchNote =>
        "observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.";

    public string SourceNote => FromConfig ? "config.yaml" : "/health only";

    /// <summary>Warn when observe is paired with fail-closed, otherwise neutral — the fail-mode chip's tone.</summary>
    public string FailKey => HasMismatch ? "Warn" : "Neutral";

    /// <summary>The tooltip: the mismatch explanation when there is one, otherwise where the values came from.</summary>
    public string ToolTipText => HasMismatch ? MismatchNote : "Read from " + SourceNote;

    /// <summary>True when <paramref name="other"/> would render exactly like this chip.</summary>
    public bool IsSameAs(ConnectorChipViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Name, other.Name, StringComparison.Ordinal) &&
               string.Equals(Mode, other.Mode, StringComparison.Ordinal) &&
               string.Equals(FailMode, other.FailMode, StringComparison.Ordinal) &&
               HasMismatch == other.HasMismatch &&
               FromConfig == other.FromConfig;
    }

    /// <summary>What a screen reader announces for the chip.</summary>
    public override string ToString() =>
        $"{Name}: {Mode}, {FailMode}, from {SourceNote}" + (HasMismatch ? ". " + MismatchNote : string.Empty);
}
