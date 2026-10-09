using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One hashed evidence fingerprint behind a signal. DefenseClaw does not retain literal
/// file paths (<c>ai_discovery.store_raw_local_paths</c> is false by default) — only a
/// type, a basename, a match quality and a path hash — so this is the honest ceiling of
/// "evidence" the app can show.
/// </summary>
public sealed record DiscoveryEvidenceItem(string Type, string? Basename, double? Quality, string? MatchKind);

/// <summary>
/// One raw detection: a single detector's single hit for one product. The positional members are what the panel always showed; the
/// rest are the parts of the TUI's <c>AIUsageSignal</c> that <c>ai_discovery_state.json</c> and <c>inventory.db</c> carry on a
/// DefenseClaw 0.8.10 install (see <see cref="DiscoverySignalParser"/>) and are null when the source does not have them.
/// </summary>
public sealed record DiscoverySignalRecord(
    string Vendor,
    string Product,
    string? Category,
    string? Detector,
    string? Source,
    double? Confidence,
    string? State,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    IReadOnlyList<DiscoveryEvidenceItem> Evidence)
{
    public string? SignalId { get; init; }

    public string? SignatureId { get; init; }

    public string? Name { get; init; }

    public string? Version { get; init; }

    public DiscoveryComponentRef? Component { get; init; }

    /// <summary>The local model the signal found (a model file, or an entry in a model server's list), when it is one.</summary>
    public DiscoveryModelInfo? Model { get; init; }

    /// <summary>The process behind a process signal.</summary>
    public DiscoveryRuntimeInfo? Runtime { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    /// <summary>
    /// How sure the confidence engine is of what the signal's component is (identity) and that it is there (presence), from the component's
    /// snapshot in <c>inventory.db</c> (<c>ai_confidence_snapshots</c>, one per component of the latest scan). Neither the state file nor an
    /// <c>ai_signals</c> row has them, so they are null for a signal with no component, or whose component has no snapshot.
    /// </summary>
    public double? IdentityScore { get; init; }

    public string? IdentityBand { get; init; }

    public double? PresenceScore { get; init; }

    public string? PresenceBand { get; init; }

    /// <summary>The TUI's <c>sig_id</c>: the signature id, else the name, else the signal id.</summary>
    public string DisplayId =>
        new[] { SignatureId, Name, SignalId }.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim() ?? "(unknown)";
}

/// <summary>
/// One card: every signal for a given (vendor, product) pair, rolled up. This is the unit
/// the panel actually shows — a single product can be detected by several independent
/// detectors (process, config, mcp, package manifest, env var), each contributing its own
/// confidence and evidence. A card also has a state (the strongest among its signals, so a
/// product with one new process sorts as new), the identity and presence bands of its
/// components when the data has them, and the signals themselves with their model and
/// process lines.
/// </summary>
public sealed class DiscoveryComponentCard
{
    /// <summary>The TUI lists 50 signals of a row and points at <c>agent usage --detail --json</c> for the rest.</summary>
    private const int MaxSignalLines = 50;

    private IReadOnlyList<DiscoverySignalLine>? _signalLines;

    public required string Vendor { get; init; }

    public required string Product { get; init; }

    public required IReadOnlyList<DiscoverySignalRecord> Signals { get; init; }

    /// <summary>The moment the "ago" lines count from. Set when the card is built, so a card does not age while it sits on screen.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The strongest state among the signals (new before changed before seen before gone), or empty when none has one.</summary>
    public string State => DiscoveryStates.Strongest(Signals.Select(static s => s.State));

    public bool HasState => State.Length > 0;

    public int StateWeight => DiscoveryStates.Weight(State);

    /// <summary>Tone key for the state pill.</summary>
    public string StateKey => DiscoveryStates.Tone(State);

    /// <summary>"1 new, 28 seen": how many signals are in each state, strongest first. Empty when no signal has a state.</summary>
    public string StateTally
    {
        get
        {
            var counts = Signals
                .Select(static s => DiscoveryStates.Normalize(s.State))
                .Where(static state => state.Length > 0)
                .GroupBy(static state => state)
                .OrderBy(static group => DiscoveryStates.Weight(group.Key))
                .Select(static group => $"{group.Count().ToString(CultureInfo.InvariantCulture)} {group.Key}");
            return string.Join(", ", counts);
        }
    }

    /// <summary>"high (85%)": the identity band of the first signal that has one, else empty.</summary>
    public string IdentityDisplay =>
        Signals.FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s.IdentityBand)) is { } signal
            ? DiscoveryFormat.Confidence(signal.IdentityScore, signal.IdentityBand)
            : string.Empty;

    public string PresenceDisplay =>
        Signals.FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s.PresenceBand)) is { } signal
            ? DiscoveryFormat.Confidence(signal.PresenceScore, signal.PresenceBand)
            : string.Empty;

    public bool HasBands => IdentityDisplay.Length > 0 || PresenceDisplay.Length > 0;

    /// <summary>The identity band for a field, "—" when the card has a presence band and no identity one.</summary>
    public string IdentityText => IdentityDisplay.Length > 0 ? IdentityDisplay : "—";

    public string PresenceText => PresenceDisplay.Length > 0 ? PresenceDisplay : "—";

    public DateTimeOffset? LastActive => Signals.Select(static s => s.LastActiveAt).OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Max();

    public bool HasLastActive => LastActive is not null;

    public string LastActiveDisplay => LastActive is { } active ? active.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>The signals as text blocks, new and changed first, at most 50.</summary>
    public IReadOnlyList<DiscoverySignalLine> SignalLines =>
        _signalLines ??= Signals
            .Select((signal, index) => (signal, index))
            .OrderBy(static pair => DiscoveryStates.Weight(pair.signal.State))
            .ThenBy(static pair => pair.index)
            .Take(MaxSignalLines)
            .Select(pair => DiscoverySignalLine.For(pair.signal, Now, forModel: false))
            .ToList();

    public bool HasSignalOverflow => Signals.Count > MaxSignalLines;

    public string SignalOverflow => HasSignalOverflow
        ? $"...and {(Signals.Count - MaxSignalLines).ToString(CultureInfo.InvariantCulture)} more (use `defenseclaw agent usage --detail --json` for the full list)"
        : string.Empty;

    /// <summary>
    /// Does the card match a search? The vendor, product, state, categories, detectors, component, version, the models and the bands are
    /// searched, as the TUI's <c>_apply_filter</c> does for a row.
    /// </summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var parts = new List<string?> { Vendor, Product, State, IdentityDisplay, PresenceDisplay };
        foreach (var signal in Signals)
        {
            parts.Add(signal.Category);
            parts.Add(signal.Detector);
            parts.Add(signal.Version);
            parts.Add(signal.Component?.Ecosystem);
            parts.Add(signal.Component?.Name);
            parts.Add(signal.Model?.Id);
        }

        return string.Join(' ', parts.Where(static part => !string.IsNullOrEmpty(part))).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public double MaxConfidence => Signals.Count == 0 ? 0 : Signals.Max(s => s.Confidence ?? 0);

    public string ConfidenceDisplay => $"{MaxConfidence:P0}";

    public string DetectorsDisplay => Distinct(Signals.Select(s => s.Detector));

    public string CategoriesDisplay => Distinct(Signals.Select(s => s.Category));

    public string SourcesDisplay => Distinct(Signals.Select(s => s.Source));

    public DateTimeOffset? FirstSeen => Signals.Select(s => s.FirstSeen).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Min();

    public DateTimeOffset? LastSeen => Signals.Select(s => s.LastSeen).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Max();

    public string FirstSeenDisplay => FirstSeen is { } f && f != default ? f.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public string LastSeenDisplay => LastSeen is { } l && l != default ? l.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public int EvidenceCount => Signals.Sum(s => s.Evidence.Count);

    public string EvidenceSummary => EvidenceCount == 0
        ? "No evidence recorded by DefenseClaw for this component — confidence alone, nothing to inspect."
        : $"{EvidenceCount} evidence entr{(EvidenceCount == 1 ? "y" : "ies")} — hashed fingerprints only " +
          "(type, quality, match kind); DefenseClaw does not retain literal file paths.";

    public string HeaderDisplay =>
        $"{Vendor} · {Product}  —  {(HasState ? State + ", " : string.Empty)}{ConfidenceDisplay} confidence, {Signals.Count} signal{(Signals.Count == 1 ? string.Empty : "s")}";

    /// <summary>Tone key for the confidence badge: green from 80 %, amber from 50 %, otherwise neutral (a low score is not an alarm).</summary>
    public string ConfidenceKey => MaxConfidence >= 0.8 ? "Ok" : MaxConfidence >= 0.5 ? "Warn" : "Neutral";

    public string SignalCountDisplay => $"{Signals.Count} signal{(Signals.Count == 1 ? string.Empty : "s")}";

    /// <summary>What a screen reader announces for the card.</summary>
    public override string ToString() => HeaderDisplay;

    private static string Distinct(IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }
}

/// <summary>One scan summary event, read from the <c>ai.discovery</c> audit bucket.</summary>
public sealed record DiscoveryScanEvent(
    DateTimeOffset Timestamp,
    string? Source,
    long? SignalsTotal,
    long? ActiveSignals,
    long? NewSignals,
    long? ChangedSignals,
    long? GoneSignals,
    long? DurationMs,
    string? Result)
{
    /// <summary>Files the scan read; a process-list check reads none.</summary>
    public long? FilesScanned { get; init; }

    /// <summary>
    /// True for a scan's completion event, which carries the scan's totals. The bucket also holds one event per component the scanner
    /// found (<c>ai_component.discovered</c>) or lost (<c>ai_component.removed</c>), which carry none of them.
    /// </summary>
    public bool IsSummary => SignalsTotal is not null || ActiveSignals is not null;

    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Summary =>
        $"{SignalsTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"} signals " +
        $"({ActiveSignals?.ToString(CultureInfo.InvariantCulture) ?? "?"} active, " +
        $"+{NewSignals?.ToString(CultureInfo.InvariantCulture) ?? "0"}/-{GoneSignals?.ToString(CultureInfo.InvariantCulture) ?? "0"}) " +
        $"via {Source ?? "unknown"} in {DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "?"} ms — {Result ?? "?"}";

    public override string ToString() => $"Scan at {TimestampDisplay}: {Summary}";
}

/// <summary>One row of the connector-level discovery table (agent_discovery.json).</summary>
public sealed record AgentDiscoveryRow(
    string Name,
    bool Installed,
    bool Configured,
    bool Active,
    string? Version,
    string? ConfigPath,
    string? BinaryPath,
    string? Error)
{
    /// <summary>
    /// An installed agent DefenseClaw is not set up for, that is offered on Windows: the row's Add (CUST-210). Decided where the row is built
    /// (<see cref="AiDiscoveryPanelViewModel.AddConnectorCommand"/>), since it depends on the Setup catalog.
    /// </summary>
    public bool CanAdd { get; init; }

    /// <summary>The screen-reader name of the Add button.</summary>
    public string AddAutomationName => $"Add {Name}";

    public string StatusDisplay =>
        (Installed, Configured, Active) switch
        {
            (_, _, true) => "Active",
            (true, _, _) => "Installed",
            (_, true, _) => "Configured only",
            _ => "Not detected",
        };

    public override string ToString() =>
        $"Connector {Name}: {StatusDisplay}{(string.IsNullOrEmpty(Version) ? string.Empty : ", version " + Version)}";
}

/// <summary>The currently selected/pinned connector (agent_selection.json), if any.</summary>
public sealed record AgentSelectionRow(
    string Connector,
    string? Executable,
    string? RawVersion,
    DateTimeOffset? SelectedAt,
    DateTimeOffset? ExpiresAt,
    string? Source)
{
    public string SelectedAtDisplay => SelectedAt is { } dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public string ExpiresAtDisplay => ExpiresAt is { } dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public override string ToString() => $"Selected connector {Connector}, selected {SelectedAtDisplay}, expires {ExpiresAtDisplay}";
}

/// <summary>
/// One row of the "sources" section: what this panel merged, where it came from, and when
/// it was last written — so a viewer never has to guess how fresh a card is.
/// </summary>
public sealed record DiscoverySourceInfo(string Label, string Detail, DateTimeOffset? LastUpdated, bool Available)
{
    public string LastUpdatedDisplay => LastUpdated is { } dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    /// <summary>Tone key: a source that could not be read is amber, one that answered is neutral.</summary>
    public string AvailabilityKey => Available ? "Ok" : "Warn";

    public string AvailabilityText => Available ? "Read" : "Not available";

    public override string ToString() =>
        $"{Label}: {Detail}. {(Available ? "Read" : "Not available")}, updated {LastUpdatedDisplay}";
}

/// <summary>
/// View-model for the AI Discovery panel: per-component transparency cards (vendor,
/// product, confidence, detector, source, evidence, first/last seen) plus a "sources"
/// section showing exactly which local files and audit history fed them.
/// <para>
/// The point of this panel is honesty about a known product gap: signature-based
/// confidence can both false-positive (a signature matches without the product actually
/// being installed) and miss real usage (a running tool with no matching signature). It
/// never asserts more than the data supports — evidence is shown as hashed fingerprints
/// (no literal file paths, per <c>ai_discovery.store_raw_local_paths</c>) and marked
/// explicitly when a component has none recorded.
/// </para>
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel : PanelViewModelBase, IAcceptsNavigation
{
    private const string AuditBucket = "ai.discovery";
    private readonly List<DiscoveryComponentCard> _allCards = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunningDiscover))]
    private bool _isRunningDiscover;

    [ObservableProperty]
    private string? _lastRunSummary;

    /// <summary>The Products view has nothing to list at all (see <see cref="EmptyTitle"/> for why).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasNoComponents;

    /// <summary>The view on screen has rows, and the search or the filters hide all of them.</summary>
    [ObservableProperty]
    private bool _showNoMatch;

    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    private string _emptyDetail = string.Empty;

    public AiDiscoveryPanelViewModel(AppServices services)
        : base(services)
    {
        CardsView = CollectionViewSource.GetDefaultView(_allCards);
        CardsView.Filter = FilterCard;
        ModelsView = CollectionViewSource.GetDefaultView(_allModels);
        ModelsView.Filter = FilterModel;
        Review = new DiscoverActionReview(services);

        // The old code toggled IsRunningDiscover around its own run; the shared review dialog owns the
        // run now, so mirror its state to keep NotRunningDiscover meaningful for any binding.
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoverActionReview.IsRunning))
            {
                IsRunningDiscover = Review.IsRunning;
            }
        };
    }

    /// <summary>The shared confirm-and-run dialog every change on this panel goes through.</summary>
    public DiscoverActionReview Review { get; }

    public override string Title => "AI Discovery";

    public override string Description =>
        "Discovered agents, AI components and local models, with the state, confidence, detector and evidence behind each one.";

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool NotRunningDiscover => !IsRunningDiscover;

    public ICollectionView CardsView { get; }

    public ObservableCollection<DiscoveryScanEvent> ScanHistory { get; } = new();

    public ObservableCollection<AgentDiscoveryRow> ConnectorDiscovery { get; } = new();

    public ObservableCollection<AgentSelectionRow> ConnectorSelections { get; } = new();

    public ObservableCollection<DiscoverySourceInfo> Sources { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        LastRunSummary = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// One-shot catch-up when the panel comes back on screen after its data has gone stale. Nothing runs
    /// on a timer: the gateway status call costs a CLI process (about a second), so it happens here, on
    /// Refresh, and after a change — never in the background.
    /// </summary>
    protected override void OnActivated()
    {
        if (_loadRunning || (_loadedAt is { } at && DefenseClaw.Core.Time.WallClock.Elapsed(at) < StaleAfter))
        {
            return;
        }

        _ = LoadSafelyAsync();
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the message is shown in the banner.
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"AI Discovery catch-up failed: {ex}");
            ErrorMessage = $"Could not refresh AI Discovery: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Offers to run <c>defenseclaw agent discovery scan</c> — the command that updates what the
    /// component cards show, and only after an explicit confirm.
    /// <para>
    /// This used to run <c>agent discover</c>, which is a different thing: it is the
    /// connector-level detection (is Claude Code / Codex installed, in <c>agent_discovery.json</c>),
    /// it returns that file's 24-hour cache without scanning when the cache is fresh, and it emits
    /// its own OTel report through the sidecar by default. It never touched
    /// <c>ai_discovery_state.json</c> or <c>inventory.db</c>, which is where the cards come from.
    /// <c>agent discovery scan</c> asks the running sidecar for one immediate AI discovery scan
    /// (<c>POST /api/v1/ai-usage/scan</c>) — the scan it already runs on a schedule, whose result
    /// the sidecar records in both files (the CLI's own help: "Trigger one immediate AI discovery
    /// scan via the sidecar"; it answers HTTP 503 when <c>ai_discovery</c> is disabled).
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeInstallation))]
    private void RunScan()
    {
        var argv = new[] { "agent", "discovery", "scan" };
        Review.Open(
            "Run an AI discovery scan?",
            "It asks the running DefenseClaw gateway (sidecar) to scan this machine for AI tools right now: the " +
            "same scan it already runs on its own schedule. The result is written to ai_discovery_state.json and " +
            "inventory.db, which is what the cards on this page read, and the sidecar records it in the audit " +
            "trail as an ai.discovery event like any scheduled scan (plus whatever telemetry export you have " +
            "configured for those events).\n\n" +
            "It needs the gateway running with AI discovery turned on; otherwise the command fails (HTTP 503 or " +
            "\"sidecar unavailable\") and nothing changes. It does not refresh the connector table under Sources.",
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Ask the gateway for one immediate AI discovery scan.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(3)),
            },
            result => AfterRunAsync(result, argv),
            primaryText: "Run scan");
    }

    /// <summary>
    /// Offers to run <c>defenseclaw agent discover --refresh --no-emit-otel</c>, which re-detects
    /// which agent CLIs are installed and rewrites <c>agent_discovery.json</c> — the connector
    /// table under "Sources". Without <c>--refresh</c> the CLI serves a cache up to 24 hours old,
    /// and it sends an OTel report through the sidecar unless told not to; a refresh from here is
    /// meant to be a local re-read, so it does neither. Flags checked against
    /// <c>defenseclaw agent discover --help</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeInstallation))]
    private void RefreshConnectors()
    {
        var argv = new[] { "agent", "discover", "--refresh", "--no-emit-otel" };
        Review.Open(
            "Re-detect installed agents?",
            "It re-checks which agent CLIs (Claude Code, Codex, …) are installed on this machine and rewrites " +
            "agent_discovery.json, the \"Connector discovery\" table under Sources. Without --refresh the CLI " +
            "would hand back a cached result up to 24 hours old. --no-emit-otel keeps it from sending a discovery " +
            "report through the sidecar.\n\n" +
            "It does not change the AI component cards; use \"Run AI discovery scan\" for those.",
            new[] { new DiscoverStep(argv, "Re-detect which agent CLIs are installed.", CommandTier.StateChanging) },
            result => AfterRunAsync(result, argv),
            primaryText: "Re-detect");
    }

    /// <summary>Summarizes the finished run on the panel and re-reads everything it may have changed.</summary>
    private async Task AfterRunAsync(DiscoverReviewResult result, IReadOnlyList<string> argv)
    {
        var command = DiscoverCli.CommandLine(argv);
        var last = result.Invocations.Count > 0 ? result.Invocations[^1] : null;

        LastRunSummary = last is null
            ? $"{command} did not start."
            : last.FailureReason is { Length: > 0 } reason
                ? $"{command} did not complete: {reason}"
                : $"{command} finished at {last.FinishedAt?.ToLocalTime():HH:mm:ss} " +
                  $"(exit {last.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}). See Activity for full output.";

        // A gateway restart or config write may have happened; pick the new config up before re-reading.
        Services.ReloadConfig();

        for (var i = 0; i < 50 && _loadRunning; i++)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value)
    {
        CardsView.Refresh();
        RefreshModels();
        UpdateEmptyState();
    }

    private bool FilterCard(object obj) => obj is DiscoveryComponentCard card && card.Matches(SearchText);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        // One read at a time: Initialize, the activation catch-up, Refresh and a finished action can all ask.
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            try
            {
                await LoadFromDiskAsync(cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                IsLoading = false;
            }

            // The live parts cost a CLI process and two REST calls (the runtime section, and the gateway's AI usage report, which adds the
            // newer model fields to what the files list); they run together, after the cards are up.
            await Task.WhenAll(LoadLiveStatusAsync(cancellationToken), LoadRuntimeAsync(cancellationToken), LoadUsageAsync(cancellationToken))
                .ConfigureAwait(true);
        }
        finally
        {
            _loadRunning = false;
        }
    }

    /// <summary>
    /// Everything the panel shows that comes from files on this machine: the signals (state file, else inventory.db), their confidence
    /// bands, the product cards and the model rows built from them, the scan history, the connector tables and the coverage card's
    /// configured half. It starts no process and makes no request; the gateway's live answers are layered on by <see cref="LoadAsync"/>.
    /// </summary>
    internal async Task LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        Sources.Clear();
        _liveStatus = null;

        var (loaded, signalSource) = await LoadSignalsAsync(cancellationToken).ConfigureAwait(true);
        var (signals, bandsSource) = await ApplyConfidenceBandsAsync(loaded, cancellationToken).ConfigureAwait(true);

        _loadedAt = DateTimeOffset.Now;
        ApplySignals(signals);
        Sources.Add(signalSource);
        if (bandsSource is not null)
        {
            Sources.Add(bandsSource);
        }

        _signalSourceAvailable = signalSource.Available;
        _signalCacheUpdatedAt = signalSource.LastUpdated;

        await LoadScanHistoryAsync(cancellationToken).ConfigureAwait(true);
        await LoadAgentDiscoveryAsync(cancellationToken).ConfigureAwait(true);
        await LoadAgentSelectionAsync(cancellationToken).ConfigureAwait(true);

        // What is configured comes from the in-memory config (no I/O), so the coverage card is
        // useful immediately; the gateway's live answer refines it below.
        BuildCoverage(live: null, liveProblem: null);
        RefreshHeader();
        UpdateEmptyState();
    }

    private static string Plural(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    /// <summary>
    /// Primary source: the persisted signal cache (<c>ai_discovery_state.json</c>), which
    /// carries first/last-seen and hashed evidence per signal. Falls back to the
    /// <c>ai_signals</c> rows of the latest full scan in <c>inventory.db</c> — the same data,
    /// DB-shaped, with slightly thinner evidence — when the cache file is missing or unreadable.
    /// The table holds every scan ever recorded (millions of rows), so "the signals" always means
    /// one scan's worth, chosen by <see cref="DefenseClaw.Core.Inventory.InventoryReader"/>.
    /// </summary>
    internal async Task<(IReadOnlyList<DiscoverySignalRecord> Signals, DiscoverySourceInfo Source)> LoadSignalsAsync(
        CancellationToken cancellationToken)
    {
        var path = Services.Paths.AiDiscoveryStatePath;
        var options = ReadOptions;
        try
        {
            if (File.Exists(path))
            {
                var raw = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;

                var signals = new List<DiscoverySignalRecord>();
                if (root.TryGetProperty("signals", out var signalsElement) && signalsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in signalsElement.EnumerateObject())
                    {
                        // A value that is not an object is not a signal: reading properties off it throws
                        // InvalidOperationException, which the catch below does not handle and which would leave the
                        // panel empty with no explanation.
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            signals.Add(DiscoverySignalParser.FromState(property.Value, options));
                        }
                    }
                }

                var updatedAt = GetTimestamp(root, "updated_at");
                var source = new DiscoverySourceInfo(
                    "Signal cache — ai_discovery_state.json",
                    $"{signals.Count} signals",
                    updatedAt,
                    Available: true);

                return (signals, source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to the inventory.db fallback below.
            return await LoadSignalsFromInventoryAsync(cancellationToken, $"ai_discovery_state.json unreadable ({ex.Message}); falling back to inventory.db.")
                .ConfigureAwait(true);
        }

        return await LoadSignalsFromInventoryAsync(cancellationToken, "ai_discovery_state.json not found; falling back to inventory.db.")
            .ConfigureAwait(true);
    }

    private async Task<(IReadOnlyList<DiscoverySignalRecord>, DiscoverySourceInfo)> LoadSignalsFromInventoryAsync(
        CancellationToken cancellationToken, string note)
    {
        try
        {
            if (!Services.Inventory.Exists)
            {
                return (Array.Empty<DiscoverySignalRecord>(),
                    new DiscoverySourceInfo("Signal cache", $"{note} inventory.db not found either — nothing to show.", null, Available: false));
            }

            // The signals of the latest full scan. This used to browse ai_signals with no ORDER BY,
            // which returns the OLDEST 5,000 rows (rowid order — the first days of the install) out of
            // millions.
            var latest = await Services.Inventory.GetLatestSignalsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            if (latest is null)
            {
                return (Array.Empty<DiscoverySignalRecord>(),
                    new DiscoverySourceInfo(
                        "Signal cache",
                        $"{note} inventory.db does not have the scan tables this fallback reads (ai_scans, and a scan_id on ai_signals) — nothing to show.",
                        null,
                        Available: false));
            }

            var options = ReadOptions;
            var signals = latest.Rows.Rows.Select(row => DiscoverySignalParser.FromDbRow(row, options)).ToList();
            var scan = latest.Scan;
            var detail = scan is null
                ? $"{note} inventory.db has not recorded a scan yet."
                : $"{note} {signals.Count} signal{(signals.Count == 1 ? string.Empty : "s")} from the " +
                  (scan.IsFullScan ? "latest full scan" : "newest scan (no completed scheduled or startup scan is recorded)") +
                  $": {scan.Source}, {scan.Result}.";

            return (signals, new DiscoverySourceInfo(
                "Signal cache — inventory.db (ai_signals, latest scan, fallback)",
                detail,
                scan?.ScannedAt,
                Available: signals.Count > 0));
        }
        catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
        {
            return (Array.Empty<DiscoverySignalRecord>(),
                new DiscoverySourceInfo("Signal cache", $"{note} inventory.db read failed: {ex.Message}", null, Available: false));
        }
    }

    private async Task LoadScanHistoryAsync(CancellationToken cancellationToken)
    {
        ScanHistory.Clear();
        try
        {
            if (!Services.Audit.Exists)
            {
                Sources.Add(new DiscoverySourceInfo("Audit trail — ai.discovery bucket", "audit.db not found.", null, Available: false));
                return;
            }

            // The bucket holds a completion event per scan (about one a minute, with the totals) and one event per component
            // found or lost, which carry no totals: a window of 100 events is read, and the scans among them are the history.
            var events = await Services.Audit.ListAsync(
                new AuditQuery { Bucket = AuditBucket, Limit = 100 }, cancellationToken).ConfigureAwait(true);

            foreach (var scan in events
                .Select(static evt => new DiscoveryScanEvent(
                    evt.Timestamp,
                    evt.StructuredString("defenseclaw.ai.discovery.source"),
                    GetLong(evt, "defenseclaw.ai.discovery.signals_total"),
                    GetLong(evt, "defenseclaw.ai.discovery.active_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.new_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.changed_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.gone_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.duration_ms"),
                    evt.StructuredString("defenseclaw.ai.discovery.result"))
                {
                    FilesScanned = GetLong(evt, "defenseclaw.ai.discovery.files_scanned"),
                })
                .Where(static scan => scan.IsSummary)
                .Take(25))
            {
                ScanHistory.Add(scan);
            }

            Sources.Add(new DiscoverySourceInfo(
                $"Audit trail — {AuditBucket} bucket",
                $"{ScanHistory.Count} recent scan event{(ScanHistory.Count == 1 ? string.Empty : "s")}",
                ScanHistory.Count > 0 ? ScanHistory[0].Timestamp : null,
                Available: ScanHistory.Count > 0));
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Sources.Add(new DiscoverySourceInfo($"Audit trail — {AuditBucket} bucket", $"Could not read audit.db: {ex.Message}", null, Available: false));
        }
    }

    internal async Task LoadAgentDiscoveryAsync(CancellationToken cancellationToken)
    {
        ConnectorDiscovery.Clear();
        var path = Services.Paths.AgentDiscoveryStatePath;

        try
        {
            if (!File.Exists(path))
            {
                Sources.Add(new DiscoverySourceInfo("Connector discovery — agent_discovery.json", "File not found.", null, Available: false));
                return;
            }

            var raw = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in agents.EnumerateObject())
                {
                    var value = property.Value;
                    ConnectorDiscovery.Add(new AgentDiscoveryRow(
                        GetString(value, "name") ?? property.Name,
                        GetBool(value, "installed"),
                        GetBool(value, "configured"),
                        GetBool(value, "active"),
                        NullIfEmpty(GetString(value, "version")),
                        NullIfEmpty(GetString(value, "config_path")),
                        NullIfEmpty(GetString(value, "binary_path")),
                        NullIfEmpty(GetString(value, "error")))
                    {
                        CanAdd = CanAddConnector(
                            GetString(value, "name") ?? property.Name,
                            GetBool(value, "installed"),
                            GetBool(value, "active")),
                    });
                }
            }

            var scannedAt = GetTimestamp(root, "scanned_at");
            var installedCount = ConnectorDiscovery.Count(a => a.Installed);
            Sources.Add(new DiscoverySourceInfo(
                "Connector discovery — agent_discovery.json",
                $"{ConnectorDiscovery.Count} known connectors, {installedCount} installed",
                scannedAt,
                Available: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Sources.Add(new DiscoverySourceInfo("Connector discovery — agent_discovery.json", $"Could not read: {ex.Message}", null, Available: false));
        }
    }

    private async Task LoadAgentSelectionAsync(CancellationToken cancellationToken)
    {
        _selectionsAll.Clear();
        var selectionPath = FindAgentSelectionPath();

        try
        {
            if (selectionPath is null || !File.Exists(selectionPath))
            {
                Sources.Add(new DiscoverySourceInfo("Connector selection — agent_selection.json", "File not found.", null, Available: false));
                return;
            }

            var raw = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(selectionPath, cancellationToken).ConfigureAwait(true);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.TryGetProperty("selections", out var selections) && selections.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in selections.EnumerateObject())
                {
                    var value = property.Value;
                    _selectionsAll.Add(new AgentSelectionRow(
                        GetString(value, "connector") ?? property.Name,
                        NullIfEmpty(GetString(value, "executable")),
                        NullIfEmpty(GetString(value, "raw_version")),
                        GetTimestamp(value, "selected_at"),
                        GetTimestamp(value, "expires_at"),
                        NullIfEmpty(GetString(value, "source"))));
                }
            }

            var updatedAt = GetTimestamp(root, "updated_at");
            Sources.Add(new DiscoverySourceInfo(
                "Connector selection — agent_selection.json",
                _selectionsAll.Count == 0
                    ? "No connector currently selected."
                    : $"{_selectionsAll.Count} selection{(_selectionsAll.Count == 1 ? string.Empty : "s")}: " +
                      string.Join(", ", _selectionsAll.Select(s => s.Connector)),
                updatedAt,
                Available: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Sources.Add(new DiscoverySourceInfo("Connector selection — agent_selection.json", $"Could not read: {ex.Message}", null, Available: false));
        }
        finally
        {
            ApplyScopeToSelections();
        }
    }

    // The pinned-connector table is connector-tagged data, so the shared connector scope narrows it. The "Connector discovery" table is not
    // narrowed: it is the roster of what is installed (and the Add buttons), the very list a scope is chosen from.
    private readonly List<AgentSelectionRow> _selectionsAll = new();

    /// <summary>The shared connector scope changed: the pinned-connector table is listed again under it.</summary>
    protected override void OnConnectorScopeChanged() => ApplyScopeToSelections();

    private void ApplyScopeToSelections()
    {
        var scope = Services.ConnectorScope;
        var wanted = _selectionsAll.Where(row => scope.Allows(row.Connector)).ToList();
        if (ConnectorSelections.SequenceEqual(wanted))
        {
            return;
        }

        ConnectorSelections.Clear();
        foreach (var row in wanted)
        {
            ConnectorSelections.Add(row);
        }
    }

    /// <summary>
    /// <c>agent_selection.json</c> lives next to <c>agent_discovery.json</c> in the data
    /// directory; <see cref="DefenseClaw.Core.Paths.DefenseClawPaths"/> does not expose it
    /// by name, so this resolves it the same way the paths type resolves its siblings.
    /// </summary>
    private string FindAgentSelectionPath() =>
        Path.Combine(Path.GetDirectoryName(Services.Paths.AgentDiscoveryStatePath) ?? Services.Paths.DataDirectory, "agent_selection.json");

    /// <summary>
    /// The product cards: the signals that are not an identified local model (those are the Models view's rows), grouped by vendor and
    /// product. Most actionable state first - a product with a new or changed signal ahead of one that is only seen, and one that is gone
    /// last - then the strongest detection, then by name.
    /// </summary>
    internal static IEnumerable<DiscoveryComponentCard> BuildCards(IReadOnlyList<DiscoverySignalRecord> signals, DateTimeOffset now) =>
        signals
            .Where(static s => !DiscoveryModelRow.IsModelSignal(s))
            .GroupBy(s => (Vendor: string.IsNullOrWhiteSpace(s.Vendor) ? "(unknown vendor)" : s.Vendor,
                           Product: string.IsNullOrWhiteSpace(s.Product) ? "(unknown product)" : s.Product),
                StringTupleComparer.Instance)
            .Select(g => new DiscoveryComponentCard { Vendor = g.Key.Vendor, Product = g.Key.Product, Signals = g.ToList(), Now = now })
            .OrderBy(c => c.StateWeight)
            .ThenByDescending(c => c.MaxConfidence)
            .ThenBy(c => c.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Product, StringComparer.OrdinalIgnoreCase);

    private static long? GetLong(AuditEvent evt, string key) =>
        evt.StructuredJson.TryGetValue(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTimestamp(JsonElement element, string property) =>
        GetString(element, property) is { } raw && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class StringTupleComparer : IEqualityComparer<(string Vendor, string Product)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Vendor, string Product) x, (string Vendor, string Product) y) =>
            string.Equals(x.Vendor, y.Vendor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Vendor, string Product) obj) =>
            HashCode.Combine(obj.Vendor.ToUpperInvariant(), obj.Product.ToUpperInvariant());
    }
}
