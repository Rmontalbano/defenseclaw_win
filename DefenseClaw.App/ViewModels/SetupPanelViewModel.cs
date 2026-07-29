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
/// follow the binary if that changes.
/// </para>
/// <para>
/// Uncertified cards stay launchable on purpose. The mac app's lesson — and the codex ghost
/// this project chased — is that hiding the option teaches nothing; the warning travels with
/// the operator into the wizard and onto its review screen instead.
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
        _catalog = WizardCatalog.Shared(services);
        _catalog.DefinitionChanged += OnDefinitionChanged;
        Services.Monitor.StateChanged += OnGatewayStateChanged;
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

        var definitions = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(true);

        _all.Clear();
        foreach (var definition in definitions)
        {
            _all.Add(new WizardCardViewModel(definition));
        }

        IsLoading = false;
        HasLoadError = _catalog.LoadError is { Length: > 0 };
        LoadError = _catalog.LoadError ?? string.Empty;
        ApplyFilters();

        // Warm the per-target help in the background. The hub is already complete and badged
        // from the top-level screen; this fills in real descriptions, the authoritative
        // platform-status line, and the option lists the wizards are built from.
        _ = WarmAsync(cancellationToken);
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
        IsLoading = true;
        StatusNote = "Re-reading the setup catalog from the CLI…";
        Groups.Clear();
        _all.Clear();

        var definitions = await _catalog.ReloadAsync().ConfigureAwait(true);
        foreach (var definition in definitions)
        {
            _all.Add(new WizardCardViewModel(definition));
        }

        IsLoading = false;
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
        // Raised from the probe's continuation, which may be any thread pool thread.
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

        Connectors.Clear();

        foreach (var (name, settings) in config.Guardrail.Connectors.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(name))
            {
                continue;
            }

            Connectors.Add(new ConnectorChipViewModel(
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
                Connectors.Add(new ConnectorChipViewModel(name, null, null, false, fromConfig: false));
            }
        }

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

        var matching = _all.Where(card =>
            (!CertifiedOnly || card.PlatformStatus == PlatformStatus.Certified) &&
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
            "{0} of {1} setup target(s) shown · {2} certified on Windows · {3} help screen(s) read{4}",
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

    /// <summary>The command this card runs, shown small on the card so nothing is a surprise.</summary>
    public string CommandHint => "defenseclaw setup " + Target;

    public void Apply(WizardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

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
}
