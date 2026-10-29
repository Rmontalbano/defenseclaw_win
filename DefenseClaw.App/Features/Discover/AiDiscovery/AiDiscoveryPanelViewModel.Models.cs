using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Gateway;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>One count of the line above the lists: "222 active", "2 new"; or one of its diagnostics, as the TUI words them: "model-lookup=offline".</summary>
public sealed record DiscoveryHeaderChip(string Label, string Value, string ToneKey)
{
    /// <summary>A <c>key=value</c> diagnostic of the TUI's header line rather than a count.</summary>
    public bool IsDiagnostic { get; init; }

    /// <summary>What the chip says in a sentence when it is hovered; empty for a count.</summary>
    public string Description { get; init; } = string.Empty;

    public bool HasDescription => Description.Length > 0;

    public string Text => IsDiagnostic ? $"{Label}={Value}" : $"{Value} {Label}";

    public override string ToString() => Text;
}

/// <summary>Where a <see cref="DiscoveryHeaderCounts"/> came from, which decides how its note words it.</summary>
public enum DiscoveryCountsOrigin
{
    /// <summary>The gateway's own answer (<c>agent discovery status --json</c>) about its last scan.</summary>
    Gateway,

    /// <summary>The newest scan-completion event in the audit trail.</summary>
    AuditTrail,

    /// <summary>Counted from the signals the panel lists, because no scan summary was readable.</summary>
    SignalList,
}

/// <summary>
/// The TUI's header line - <c>active=222 new=2 changed=1 gone=1 files=1000</c> - for the latest scan: the active count always, the
/// churn counts only when they are not zero, and the files read when the scan read any (a process-list check reads none, and "files=0"
/// beside a card that says 1,000 files were read at the last full scan only confuses). A count the source does not have is not
/// shown; it is never filled in with a zero.
/// </summary>
public sealed record DiscoveryHeaderCounts(
    long? Active,
    long? New,
    long? Changed,
    long? Gone,
    long? Files,
    DateTimeOffset? ScannedAt,
    string? ScanSource,
    DiscoveryCountsOrigin Origin)
{
    public IReadOnlyList<DiscoveryHeaderChip> Chips
    {
        get
        {
            var chips = new List<DiscoveryHeaderChip>();
            static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

            if (Active is { } active)
            {
                chips.Add(new DiscoveryHeaderChip("active", N(active), "Neutral"));
            }

            if (New is > 0)
            {
                chips.Add(new DiscoveryHeaderChip("new", N(New.Value), DiscoveryStates.Tone(DiscoveryStates.New)));
            }

            if (Changed is > 0)
            {
                chips.Add(new DiscoveryHeaderChip("changed", N(Changed.Value), DiscoveryStates.Tone(DiscoveryStates.Changed)));
            }

            if (Gone is > 0)
            {
                chips.Add(new DiscoveryHeaderChip("gone", N(Gone.Value), DiscoveryStates.Tone(DiscoveryStates.Gone)));
            }

            if (Files is > 0)
            {
                chips.Add(new DiscoveryHeaderChip("files", N(Files.Value), "Neutral"));
            }

            return chips;
        }
    }

    /// <summary>One sentence on where the numbers are from and when.</summary>
    public string Note
    {
        get
        {
            var when = ScannedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null;
            var kind = string.IsNullOrWhiteSpace(ScanSource) ? "scan" : $"{ScanSource} scan";
            return Origin switch
            {
                DiscoveryCountsOrigin.Gateway when when is not null => $"The gateway's last scan: {kind}, {when}.",
                DiscoveryCountsOrigin.AuditTrail when when is not null => $"The audit trail's last scan: {kind}, {when}.",
                _ => "Counted from the signals listed below; no scan summary could be read.",
            };
        }
    }

    /// <summary>
    /// The counts of the signals themselves: all but the gone ones are active, and the new and changed ones are counted by state. No
    /// signals, no counts: a line saying "0 active" beside a card that says nothing was found (or that discovery is off) adds nothing.
    /// </summary>
    public static DiscoveryHeaderCounts FromSignals(IReadOnlyList<DiscoverySignalRecord> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        if (signals.Count == 0)
        {
            return new DiscoveryHeaderCounts(null, null, null, null, null, null, null, DiscoveryCountsOrigin.SignalList);
        }

        var states = signals.Select(static s => DiscoveryStates.Normalize(s.State)).ToList();
        return new DiscoveryHeaderCounts(
            states.Count(static state => state != DiscoveryStates.Gone),
            states.Count(static state => state == DiscoveryStates.New),
            states.Count(static state => state == DiscoveryStates.Changed),
            states.Count(static state => state == DiscoveryStates.Gone),
            null,
            null,
            null,
            DiscoveryCountsOrigin.SignalList);
    }
}

/// <summary>
/// AI Discovery's two views of what was found - Products (the tools, agents and SDKs: the cards) and Models (the local AI models: a
/// table with an inspector) - and the line of counts for the latest scan above them.
/// <para>
/// <b>Products vs models.</b> A signal of the <c>local_model</c> category that names a model is a model; everything else is a product
/// (so a signal that is a model in name only, with no id, is not lost). A DefenseClaw 0.8.10 install reports its models as one product,
/// "Local Model Artifact", with a signal per file, which is what the Models view replaces with a row per model.
/// </para>
/// <para>
/// <b>Filters.</b> Each exists only when the data can answer it: Modality and Relevance, and the Owners column, when some model names
/// one; Confidence always, since every signal has a detection score. A runtime that classifies its models (a newer one sends an owner, a
/// relevance and a discovery confidence for each) gets the Mac's <b>recommended</b> default - "Show all models" off lists the models worth
/// a look: at least 80% confident, and primary or owned and of a kind that matters (<see cref="DiscoveryModelFilter"/>) - and says how many
/// it hid. A runtime that does not (every model a 0.8.10 install reports) gets no such scope, and nothing is hidden until the operator
/// picks something, as the Mac itself lists everything for data that has neither.
/// </para>
/// <para>
/// <b>The gateway's report.</b> <c>GET /api/v1/ai-usage</c> is read with every load, once, alongside the files
/// (<see cref="LoadUsageAsync"/>). It adds the newer model members to the signals the files list and says whether the running gateway looks
/// model lineage up online (the <c>model-lookup=</c> chip); it adds no row. See <see cref="DiscoveryUsageOverlay"/>.
/// </para>
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel
{
    public const string ProductsViewKey = "products";
    public const string ModelsViewKey = "models";

    private const string AllChoice = "all";
    private const string AnyConfidence = "any";

    private readonly List<DiscoveryModelRow> _allModels = new();

    /// <summary>The signals the files list, before the gateway's report adds to their model blocks; every list is rebuilt from them.</summary>
    private IReadOnlyList<DiscoverySignalRecord> _fileSignals = Array.Empty<DiscoverySignalRecord>();

    /// <summary>The signals the lists are built from, kept for the counts taken from the signals themselves.</summary>
    private IReadOnlyList<DiscoverySignalRecord> _signals = Array.Empty<DiscoverySignalRecord>();

    private LiveDiscoveryStatus? _liveStatus;
    private DiscoveryModelFilter _modelFilter = new();

    /// <summary>The gateway's last usable report; null while it has not answered, after a failed read, and for a gateway that has AI discovery off.</summary>
    private AiUsageSnapshot? _usage;

    /// <summary>The Sources row that says what the last read of the gateway's report did.</summary>
    private DiscoverySourceInfo? _usageSource;

    /// <summary>How many models the recommended scope alone keeps off the list (they pass the search and the pickers, not the scope).</summary>
    private int _hiddenByScope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProductsView), nameof(IsModelsView))]
    private string _activeView = ProductsViewKey;

    [ObservableProperty]
    private int _productCount;

    [ObservableProperty]
    private int _modelCount;

    /// <summary>Some model names a modality, so there is a Modality column and filter.</summary>
    [ObservableProperty]
    private bool _hasModalityFilter;

    /// <summary>Some model names a relevance (primary, supporting, embedded), so there is a Relevance column and filter.</summary>
    [ObservableProperty]
    private bool _hasRelevanceFilter;

    /// <summary>Some model names the application that owns it, so there is an Owners column.</summary>
    [ObservableProperty]
    private bool _hasOwnerData;

    /// <summary>
    /// The runtime classifies its models (some carries an owner, a relevance or a discovery confidence), so the recommended scope applies and
    /// "Show all models" is offered. False for a snapshot that cannot be narrowed - every model a 0.8.10 install reports - which has no
    /// such switch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidenceFilterHelp), nameof(ResetFiltersHelp))]
    private bool _hasRecommendedScope;

    /// <summary>"Show all models": the recommended scope is lifted. Off is the Mac's default.</summary>
    [ObservableProperty]
    private bool _showAllModels;

    [ObservableProperty]
    private string _relevanceFilter = AllChoice;

    /// <summary>"3 models hidden by the recommended view..." - empty when the scope hides nothing from the list the pickers and the search leave.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelScopeNote))]
    private string _modelScopeNote = string.Empty;

    /// <summary>The line of counts above the lists has counts to explain (its note says whose they are); a diagnostic alone has none.</summary>
    [ObservableProperty]
    private bool _hasHeaderNote;

    /// <summary>The Models view has nothing to list at all (see <see cref="EmptyTitle"/> for why).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasNoModels;

    /// <summary>The Models view is on screen and at least one model passes the search and the filters: the table is shown.</summary>
    [ObservableProperty]
    private bool _showModelTable;

    [ObservableProperty]
    private string _modalityFilter = AllChoice;

    [ObservableProperty]
    private string _confidenceFilter = AnyConfidence;

    /// <summary>"12 of 101".</summary>
    [ObservableProperty]
    private string _modelCaption = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelSelection))]
    private DiscoveryModelRow? _selectedModel;

    [ObservableProperty]
    private string _noMatchTitle = string.Empty;

    [ObservableProperty]
    private string _noMatchDetail = string.Empty;

    [ObservableProperty]
    private bool _hasHeader;

    [ObservableProperty]
    private string _headerNote = string.Empty;

    [ObservableProperty]
    private bool _hasActiveModelFilters;

    public bool IsProductsView => ActiveView == ProductsViewKey;

    public bool IsModelsView => ActiveView == ModelsViewKey;

    public bool HasModelSelection => SelectedModel is not null;

    public bool HasModelScopeNote => ModelScopeNote.Length > 0;

    /// <summary>
    /// What the Confidence picker's tooltip says. For a runtime that does not classify its models it is the words 0.8.10's panel has always
    /// had; where the column can show a model's own confidence it says which number the cut is made on.
    /// </summary>
    public string ConfidenceFilterHelp => HasRecommendedScope
        ? "The number the Confidence column shows: the model's own discovery confidence when the runtime reports one, otherwise the strongest detection score among its signals (how sure the scanner was that something matched, not its confidence in the model). Every model is listed until you choose a cut."
        : "The strongest detection score among the model's signals: how sure the scanner was that something matched, not its confidence in the model itself. Every model is listed until you choose a cut.";

    /// <summary>The screen-reader help of Reset filters: with the recommended view on offer, resetting also puts the list back to it.</summary>
    public string ResetFiltersHelp => HasRecommendedScope
        ? "Set the filters back to all and the list back to the recommended models."
        : "Set the modality and confidence filters back to all.";

    /// <summary>The view on screen has nothing at all to list: one empty-state card says why.</summary>
    public bool ShowEmpty => HasNoComponents || HasNoModels;

    public ICollectionView ModelsView { get; }

    public ObservableCollection<DiscoveryHeaderChip> HeaderChips { get; } = new();

    public IReadOnlyList<DiscoveryFilterChoice> ModalityChoices { get; } = new DiscoveryFilterChoice[]
    {
        new(AllChoice, "All modalities"),
        new("generative", "Generative"),
        new("speech", "Speech"),
        new("vision", "Vision"),
        new("embedding", "Embedding"),
        new("audio", "Audio"),
        new("unknown", "Unknown"),
    };

    public IReadOnlyList<DiscoveryFilterChoice> RelevanceChoices { get; } = new DiscoveryFilterChoice[]
    {
        new(AllChoice, "All relevance"),
        new("primary", "Primary"),
        new("supporting", "Supporting"),
        new("embedded", "Embedded"),
        new("unknown", "Unknown"),
    };

    public IReadOnlyList<DiscoveryFilterChoice> ConfidenceChoices { get; } = new DiscoveryFilterChoice[]
    {
        new(AnyConfidence, "Any confidence"),
        new("high", "80% and up"),
        new("low", "Under 80%"),
    };

    partial void OnActiveViewChanged(string value)
    {
        // Each view has its own selection; leaving the Models view closes its inspector.
        SelectedModel = null;
        UpdateEmptyState();
    }

    partial void OnModalityFilterChanged(string value) => RefreshModels();

    partial void OnRelevanceFilterChanged(string value) => RefreshModels();

    partial void OnConfidenceFilterChanged(string value) => RefreshModels();

    partial void OnShowAllModelsChanged(bool value) => RefreshModels();

    /// <summary>Closes the model inspector (its X, Esc).</summary>
    [RelayCommand]
    private void ClearModelSelection() => SelectedModel = null;

    /// <summary>Puts the pickers back to "all" and the scope back to the recommended models.</summary>
    [RelayCommand]
    private void ResetModelFilters()
    {
        ModalityFilter = AllChoice;
        RelevanceFilter = AllChoice;
        ConfidenceFilter = AnyConfidence;
        ShowAllModels = false;
    }

    /// <summary>
    /// Takes the signals the files list, adds what the gateway's last report says about their models (nothing, until it has answered) and
    /// builds the lists from the result.
    /// </summary>
    private void ApplySignals(IReadOnlyList<DiscoverySignalRecord> files)
    {
        _fileSignals = files;
        RebuildLists(_usage is { } usage ? DiscoveryUsageOverlay.Apply(files, usage.Signals).Signals : files);
    }

    /// <summary>
    /// Builds everything the lists show from <paramref name="signals"/>: the product cards, the model rows, the counts on the switch and
    /// the line under the page title.
    /// </summary>
    private void RebuildLists(IReadOnlyList<DiscoverySignalRecord> signals)
    {
        var now = DateTimeOffset.UtcNow;
        _signals = signals;

        _allCards.Clear();
        foreach (var card in BuildCards(signals, now))
        {
            _allCards.Add(card);
        }

        CardsView.Refresh();
        ProductCount = _allCards.Count;
        SetModels(DiscoveryModelRow.Build(signals, now));

        var asOf = (_loadedAt ?? DateTimeOffset.Now).ToString("HH:mm", CultureInfo.InvariantCulture);
        StatusMessage = signals.Count == 0
            ? $"No AI components discovered yet · as of {asOf}"
            : $"{Plural(_allCards.Count, "product")} and {Plural(_allModels.Count, "local model")} from " +
              $"{Plural(signals.Count, "signal")} · as of {asOf}";
    }

    /// <summary>
    /// Replaces the model rows. The chosen model stays chosen when it is still there; the modality and relevance pickers, once no model
    /// names a modality or a relevance, go back to "all" rather than keep hiding rows from a control that is gone.
    /// </summary>
    internal void SetModels(IReadOnlyList<DiscoveryModelRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var selectedKey = SelectedModel?.NormalizedId;
        _allModels.Clear();
        _allModels.AddRange(rows);

        HasModalityFilter = _allModels.Any(static row => row.HasModalityData);
        if (!HasModalityFilter)
        {
            ModalityFilter = AllChoice;
        }

        HasRelevanceFilter = _allModels.Any(static row => row.HasRelevanceData);
        if (!HasRelevanceFilter)
        {
            RelevanceFilter = AllChoice;
        }

        HasOwnerData = _allModels.Any(static row => row.HasOwnerData);
        HasRecommendedScope = _allModels.Count > 0 && !DiscoveryModelFilter.IsLegacySnapshot(_allModels);

        ModelCount = _allModels.Count;
        RefreshModels();

        // The rows are new objects; the one the operator had open is found again by its id, once the table holds the new rows (a table
        // refuses a selection that is not one of its rows), and stays closed when it is gone or no longer passes the filters.
        var restored = selectedKey is null ? null : _allModels.FirstOrDefault(row => row.NormalizedId == selectedKey);
        SelectedModel = restored is not null && FilterModel(restored) ? restored : null;
    }

    private bool FilterModel(object obj) => obj is DiscoveryModelRow row && _modelFilter.Includes(row) && row.Matches(SearchText);

    /// <summary>Applies the pickers and the search to the model rows and re-words everything that says how many there are.</summary>
    private void RefreshModels()
    {
        // The recommended scope is the default; a snapshot that cannot be narrowed (a legacy one) lists everything whatever the switch says.
        _modelFilter = new DiscoveryModelFilter
        {
            RecommendedOnly = !ShowAllModels,
            Modality = ModalityFilter switch
            {
                "generative" => DiscoveryModality.Generative,
                "speech" => DiscoveryModality.Speech,
                "vision" => DiscoveryModality.Vision,
                "embedding" => DiscoveryModality.Embedding,
                "audio" => DiscoveryModality.Audio,
                "unknown" => DiscoveryModality.Unknown,
                _ => null,
            },
            Relevance = RelevanceFilter switch
            {
                "primary" => DiscoveryRelevance.Primary,
                "supporting" => DiscoveryRelevance.Supporting,
                "embedded" => DiscoveryRelevance.Embedded,
                "unknown" => DiscoveryRelevance.Unknown,
                _ => null,
            },
            Confidence = ConfidenceFilter switch
            {
                "high" => DiscoveryConfidenceBand.High,
                "low" => DiscoveryConfidenceBand.Low,
                _ => DiscoveryConfidenceBand.Any,
            },
        }.PreservingLegacySnapshot(_allModels);

        HasActiveModelFilters = ModalityFilter != AllChoice || RelevanceFilter != AllChoice || ConfidenceFilter != AnyConfidence
            || (HasRecommendedScope && ShowAllModels);
        ModelsView.Refresh();

        var shown = ModelsView.Cast<DiscoveryModelRow>().Count();
        ModelCaption = $"{shown.ToString(CultureInfo.InvariantCulture)} of {_allModels.Count.ToString(CultureInfo.InvariantCulture)}";

        // Said apart from the count, so the operator can tell "the search found nothing" from "the recommended view kept it off".
        var unscoped = _modelFilter with { RecommendedOnly = false };
        _hiddenByScope = _modelFilter.RecommendedOnly
            ? _allModels.Count(row => row.Matches(SearchText) && unscoped.Includes(row) && !_modelFilter.Includes(row))
            : 0;
        ModelScopeNote = _hiddenByScope == 0
            ? string.Empty
            : $"{Plural(_hiddenByScope, "model")} hidden by the recommended view. Turn on Show all models to list {(_hiddenByScope == 1 ? "it" : "them")}.";

        if (SelectedModel is { } selected && !FilterModel(selected))
        {
            SelectedModel = null;
        }

        UpdateEmptyState();
    }

    // ---- the line of counts ----------------------------------------------------------------------------------

    /// <summary>
    /// Fills the line above the lists from the best account of the latest scan there is: the gateway's own answer when it gave one, else
    /// the newest scan-completion event in the audit trail, else the signals themselves. Run when the files are read and again when the
    /// gateway answers.
    /// </summary>
    private void RefreshHeader()
    {
        var counts = ChooseHeaderCounts();
        HeaderChips.Clear();
        foreach (var chip in counts.Chips)
        {
            HeaderChips.Add(chip);
        }

        HasHeaderNote = HeaderChips.Count > 0;
        HeaderNote = HasHeaderNote ? counts.Note : string.Empty;

        // The TUI's last header part: whether the running gateway looks model lineage up online. Shown only when the gateway said so - a
        // gateway that does not send the member (0.8.10) is neither online nor offline, and a service that is off has no such setting.
        if (_usage is { Enabled: true, LookupModelProvenanceOnline: { } lookup })
        {
            HeaderChips.Add(new DiscoveryHeaderChip("model-lookup", lookup ? "online" : "offline", "Neutral")
            {
                IsDiagnostic = true,
                Description = lookup
                    ? "Online model lookup is on: the gateway may look up the model cards of the Hugging Face repositories it finds in local model metadata."
                    : "Online model lookup is off (the default): a model's lineage comes from the runtime's built-in publisher catalogue and the model's own metadata only.",
            });
        }

        HasHeader = HeaderChips.Count > 0;
    }

    private DiscoveryHeaderCounts ChooseHeaderCounts()
    {
        if (_liveStatus is { Reachable: true, ScannedAt: { } liveAt, ActiveSignals: not null } live)
        {
            return new DiscoveryHeaderCounts(live.ActiveSignals, live.NewSignals, live.ChangedSignals, live.GoneSignals, live.FilesScanned, liveAt, live.Source, DiscoveryCountsOrigin.Gateway);
        }

        if (ScanHistory.FirstOrDefault(static scan => scan.IsSummary) is { } scan)
        {
            return new DiscoveryHeaderCounts(scan.ActiveSignals, scan.NewSignals, scan.ChangedSignals, scan.GoneSignals, scan.FilesScanned, scan.Timestamp, scan.Source, DiscoveryCountsOrigin.AuditTrail);
        }

        return DiscoveryHeaderCounts.FromSignals(_signals);
    }

    // ---- identity and presence ---------------------------------------------------------------------------------

    /// <summary>
    /// Gives each signal with a component the identity and presence bands of that component. Neither the state file nor <c>ai_signals</c>
    /// has them per signal: the gateway works them out when it answers <c>/api/v1/ai-usage</c>, which this panel reads for the newer model
    /// fields only (CUST-310), so it takes no band from it. What <c>inventory.db</c> has is the same engine's result for the latest scan, one snapshot per component
    /// (<c>ai_confidence_snapshots</c>), which is what the Inventory panel shows; a signal gets the band of its component's snapshot. On a
    /// live 0.8.10 install that is three components of two hundred signals, so most cards have none, and say none. Nothing is read when no
    /// signal has a component, and a database that cannot answer leaves the cards without bands and says so under Sources.
    /// </summary>
    internal async Task<(IReadOnlyList<DiscoverySignalRecord> Signals, DiscoverySourceInfo? Source)> ApplyConfidenceBandsAsync(
        IReadOnlyList<DiscoverySignalRecord> signals,
        CancellationToken cancellationToken)
    {
        const string label = "Confidence bands — inventory.db (ai_confidence_snapshots, latest scan)";

        if (!signals.Any(static s => s.Component is not null && s.IdentityBand is null && s.PresenceBand is null) || !Services.Inventory.Exists)
        {
            return (signals, null);
        }

        try
        {
            var rollup = await Services.Inventory.GetLatestComponentsAsync(cancellationToken).ConfigureAwait(true);
            if (rollup is null)
            {
                return (signals, new DiscoverySourceInfo(
                    label,
                    "inventory.db does not have the confidence snapshot tables this reads, so the cards show no identity or presence band.",
                    null,
                    Available: false));
            }

            var bands = new Dictionary<string, (double? IdentityScore, string? IdentityBand, double? PresenceScore, string? PresenceBand)>(StringComparer.Ordinal);
            foreach (var row in rollup.Rows.Rows)
            {
                var ecosystem = Convert.ToString(row.GetValueOrDefault("ecosystem"), CultureInfo.InvariantCulture) ?? string.Empty;
                var name = Convert.ToString(row.GetValueOrDefault("name"), CultureInfo.InvariantCulture) ?? string.Empty;
                var identityBand = Convert.ToString(row.GetValueOrDefault("identity_band"), CultureInfo.InvariantCulture);
                var presenceBand = Convert.ToString(row.GetValueOrDefault("presence_band"), CultureInfo.InvariantCulture);
                if (name.Length == 0 || (string.IsNullOrWhiteSpace(identityBand) && string.IsNullOrWhiteSpace(presenceBand)))
                {
                    continue;
                }

                bands[ComponentKey(ecosystem, name)] = (
                    Score(row, "identity_score"), NullIfEmpty(identityBand), Score(row, "presence_score"), NullIfEmpty(presenceBand));
            }

            if (bands.Count == 0)
            {
                return (signals, null);
            }

            var enriched = signals
                .Select(signal => signal is { Component: { } component, IdentityBand: null, PresenceBand: null }
                    && bands.TryGetValue(ComponentKey(component.Ecosystem, component.Name), out var band)
                        ? signal with { IdentityScore = band.IdentityScore, IdentityBand = band.IdentityBand, PresenceScore = band.PresenceScore, PresenceBand = band.PresenceBand }
                        : signal)
                .ToList();
            var matched = enriched.Count(static s => s.IdentityBand is not null || s.PresenceBand is not null);

            return (enriched, new DiscoverySourceInfo(
                label,
                $"{Plural(bands.Count, "component")} with an identity and presence band, shown on {Plural(matched, "signal")}.",
                rollup.Scan?.ScannedAt,
                Available: true));
        }
        catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
        {
            return (signals, new DiscoverySourceInfo(label, $"Could not read inventory.db: {ex.Message}", null, Available: false));
        }

        static string ComponentKey(string ecosystem, string name) => (ecosystem + "/" + name).ToLowerInvariant();

        static double? Score(IReadOnlyDictionary<string, object?> row, string key)
        {
            if (!row.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }
    }

    // ---- the gateway's report ------------------------------------------------------------------------------------

    /// <summary>
    /// What the gateway answers to <c>GET /api/v1/ai-usage</c>, as a script for a test: no socket opens when it is set. Left null, the
    /// panel asks the real gateway through its client (<see cref="DefenseClaw.Core.Gateway.GatewayClient.GetBoundedJsonAsync"/>).
    /// </summary>
    internal Func<CancellationToken, Task<GatewayResult<JsonDocument>>>? ReadUsage { get; set; }

    /// <summary>How the signal readers treat a value that names a place on this machine: withheld, unless the runtime says it keeps raw local paths.</summary>
    private DiscoveryReadOptions ReadOptions => new(Services.Config.Config.AiDiscovery.StoreRawLocalPaths);

    /// <summary>
    /// Asks the gateway for its AI usage report (a plain authenticated GET, bounded in size and time, nothing written) and gives the
    /// answer to <see cref="ApplyUsage"/>. Any failure is a state of the Sources card, never of the page: the files list what they list.
    /// </summary>
    internal async Task LoadUsageAsync(CancellationToken cancellationToken)
    {
        AiUsageRead read;
        try
        {
            var result = ReadUsage is { } script
                ? await script(cancellationToken).ConfigureAwait(true)
                : await Services.Gateway.GetBoundedJsonAsync(AiUsageReader.Route, AiUsageReader.MaxBytes, requiresAuth: true, cancellationToken).ConfigureAwait(true);

            // Parsing is linear in the answer and runs off the UI thread; the document is disposed with it.
            var options = ReadOptions;
            read = await Task.Run(
                () =>
                {
                    try
                    {
                        return AiUsageReader.FromGateway(result, options);
                    }
                    finally
                    {
                        result.Value?.Dispose();
                    }
                },
                CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The gateway's report is supplementary; any failure to read it becomes its own state, never the page's.
        catch (Exception ex)
        {
            Trace.TraceError($"AI usage read failed: {ex}");
            read = new AiUsageRead(AiUsageReadStatus.Failed, null, "The read failed unexpectedly (" + ex.GetType().Name + ").");
        }
#pragma warning restore CA1031

        ApplyUsage(read);
    }

    /// <summary>
    /// Takes the gateway's report: a usable one (the service is on) adds the newer model members to the signals the files list and
    /// switches the <c>model-lookup=</c> chip on; anything else - off, unreachable, refused, too large, not a report - leaves the files'
    /// lists as they are and says why under Sources. The previous report never outlives a read that failed: a gateway that stopped answering
    /// is not believed about its models.
    /// </summary>
    internal void ApplyUsage(AiUsageRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _usage = read.Snapshot is { Enabled: true } snapshot ? snapshot : null;

        var (signals, enriched) = _usage is { } usage ? DiscoveryUsageOverlay.Apply(_fileSignals, usage.Signals) : (_fileSignals, 0);

        // A report that changes nothing leaves the lists alone (an expanded card stays expanded); one that adds, or stops adding, rebuilds them.
        if (!ReferenceEquals(signals, _signals))
        {
            RebuildLists(signals);
        }

        if (_usageSource is not null)
        {
            _ = Sources.Remove(_usageSource);
        }

        _usageSource = DescribeUsage(read, enriched);
        Sources.Add(_usageSource);
        RefreshHeader();
    }

    private static DiscoverySourceInfo DescribeUsage(AiUsageRead read, int enriched)
    {
        const string label = "Gateway — GET /api/v1/ai-usage";

        if (read.Snapshot is not { } snapshot)
        {
            return new DiscoverySourceInfo(label, read.Message, null, Available: false);
        }

        if (!snapshot.Enabled)
        {
            return new DiscoverySourceInfo(label, "The gateway reports AI discovery off, so it has nothing to add to the files.", snapshot.ScannedAt, Available: true);
        }

        var detail = new List<string> { $"{Plural(snapshot.Signals.Count, "signal")} read from the gateway." };
        detail.Add(enriched == 0
            ? "No model gained anything from it: the files already say all it does."
            : $"{Plural(enriched, "model signal")} gained an owner, relevance, confidence or lineage from it.");
        if (snapshot.LookupModelProvenanceOnline is { } lookup)
        {
            detail.Add($"Online model lookup is {(lookup ? "on" : "off")}.");
        }

        if (snapshot.SignalsNotRead > 0)
        {
            detail.Add($"{Plural(snapshot.SignalsNotRead, "signal")} past the limit of {AiUsageReader.MaxSignals.ToString("N0", CultureInfo.InvariantCulture)} were not read.");
        }

        if (snapshot.UnreadableEntries > 0)
        {
            detail.Add($"{snapshot.UnreadableEntries.ToString(CultureInfo.InvariantCulture)} entr{(snapshot.UnreadableEntries == 1 ? "y" : "ies")} that {(snapshot.UnreadableEntries == 1 ? "was" : "were")} not a signal {(snapshot.UnreadableEntries == 1 ? "was" : "were")} skipped.");
        }

        return new DiscoverySourceInfo(label, string.Join(' ', detail), snapshot.ScannedAt, Available: true);
    }
}
