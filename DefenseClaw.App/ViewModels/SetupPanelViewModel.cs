using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Setup hub: every <c>defenseclaw setup</c> flow the installed CLI exposes, as a
/// searchable card grid, plus the connector roster the wizards act on.
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
/// the operator into the wizard and onto its review screen instead.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The connector roster is re-derived on every gateway state change, but only
/// while this panel is <see cref="PanelViewModelBase.IsActive"/>: the subscription, and the
/// card updates that ride the catalog's <c>DefinitionChanged</c>, are attached in
/// <see cref="OnActivated"/> and detached in <see cref="OnDeactivated"/>, with one catch-up pass
/// on the way back in. A hidden tray app must not rebuild a card grid for nobody.
/// </para>
/// </summary>
public sealed partial class SetupPanelViewModel : PanelViewModelBase
{
    private readonly WizardCatalog _catalog;
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

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _connectorNote = string.Empty;

    public SetupPanelViewModel(AppServices services)
        : base(services)
    {
        // No subscriptions here: OnActivated attaches them and OnDeactivated lets go.
        _catalog = WizardCatalog.Shared(services);
    }

    public override string Title => "Setup";

    /// <summary>Opens the config.yaml editor (raw AvalonEdit + generated form tabs).</summary>
    [RelayCommand]
    private void OpenConfigEditor() => Views.ConfigEditor.ConfigEditorWindow.Show(Services);

    /// <summary>Opens the runtime update / release-trust awareness window.</summary>
    [RelayCommand]
    private void OpenUpdates() => Views.Updates.UpdatesWindow.Show(Services);

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
    protected override void OnActivated()
    {
        _catalog.DefinitionChanged += OnDefinitionChanged;
        Services.Monitor.StateChanged += OnGatewayStateChanged;

        BuildConnectors();

        if (SyncCardsWithCatalog())
        {
            ApplyFilters();
        }
    }

    protected override void OnDeactivated()
    {
        _catalog.DefinitionChanged -= OnDefinitionChanged;
        Services.Monitor.StateChanged -= OnGatewayStateChanged;
    }

    /// <summary>Replaces the card list with one card per definition.</summary>
    private void FillCards(IReadOnlyList<WizardDefinition> definitions)
    {
        _all.Clear();
        foreach (var definition in definitions)
        {
            _all.Add(new WizardCardViewModel(definition));
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
        if (card is null)
        {
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

        // A wizard that ran may have changed the roster; the monitor's next poll would catch
        // it, but re-reading config now makes the change visible immediately.
        BuildConnectors();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // A second press while a read is in flight would interleave two reloads into one list.
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusNote = "Re-reading the setup catalog from the CLI…";
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

    private void OnGatewayStateChanged(object? sender, GatewaySnapshotEventArgs e) => BuildConnectors();

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

        IsEmpty = matching.Count == 0 && !IsLoading;
        UpdateStatusNote();
    }

    private void UpdateStatusNote()
    {
        if (IsLoading)
        {
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

    /// <summary>Ok / Warn / Bad / Neutral — the badge colour key the XAML triggers on.</summary>
    [ObservableProperty]
    private string _badgeKey = "Neutral";

    [ObservableProperty]
    private bool _isDetailLoaded;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string _stepNote = string.Empty;

    public WizardCardViewModel(WizardDefinition definition)
    {
        Target = definition?.Target ?? throw new ArgumentNullException(nameof(definition));
        Group = definition.Group;
        Apply(definition);
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

    public void Apply(WizardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        Group = definition.Group;
        PlatformStatus = definition.PlatformStatus;
        Title = definition.Title;
        Summary = definition.Description;
        Badge = PlatformStatusText.Badge(definition.PlatformStatus);
        BadgeKey = PlatformStatusText.Key(definition.PlatformStatus);
        IsDetailLoaded = definition.IsDetailLoaded;

        StepNote = definition.DetailError is { Length: > 0 } error
            ? error
            : definition.IsDetailLoaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} step(s) · {1}",
                    definition.Steps.Count,
                    definition.IsCurated ? "curated layout" : "generated from --help")
                : "reading --help…";
    }

    public bool Matches(string needle) =>
        Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Target.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Summary.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Group.Contains(needle, StringComparison.OrdinalIgnoreCase);
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
}
