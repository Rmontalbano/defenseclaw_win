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

/// <summary>One raw detection: a single detector's single hit for one product.</summary>
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
    IReadOnlyList<DiscoveryEvidenceItem> Evidence);

/// <summary>
/// One card: every signal for a given (vendor, product) pair, rolled up. This is the unit
/// the panel actually shows — a single product can be detected by several independent
/// detectors (process, config, mcp, package manifest, env var), each contributing its own
/// confidence and evidence.
/// </summary>
public sealed class DiscoveryComponentCard
{
    public required string Vendor { get; init; }

    public required string Product { get; init; }

    public required IReadOnlyList<DiscoverySignalRecord> Signals { get; init; }

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

    public string HeaderDisplay => $"{Vendor} · {Product}  —  {ConfidenceDisplay} confidence, {Signals.Count} signal{(Signals.Count == 1 ? string.Empty : "s")}";

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
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Summary =>
        $"{SignalsTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"} signals " +
        $"({ActiveSignals?.ToString(CultureInfo.InvariantCulture) ?? "?"} active, " +
        $"+{NewSignals?.ToString(CultureInfo.InvariantCulture) ?? "0"}/-{GoneSignals?.ToString(CultureInfo.InvariantCulture) ?? "0"}) " +
        $"via {Source ?? "unknown"} in {DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "?"} ms — {Result ?? "?"}";
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
    public string StatusDisplay =>
        (Installed, Configured, Active) switch
        {
            (_, _, true) => "Active",
            (true, _, _) => "Installed",
            (_, true, _) => "Configured only",
            _ => "Not detected",
        };
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
}

/// <summary>
/// One row of the "sources" section: what this panel merged, where it came from, and when
/// it was last written — so a viewer never has to guess how fresh a card is.
/// </summary>
public sealed record DiscoverySourceInfo(string Label, string Detail, DateTimeOffset? LastUpdated, bool Available)
{
    public string LastUpdatedDisplay => LastUpdated is { } dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";
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
public sealed partial class AiDiscoveryPanelViewModel : PanelViewModelBase
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

    [ObservableProperty]
    private bool _hasNoComponents;

    public AiDiscoveryPanelViewModel(AppServices services)
        : base(services)
    {
        CardsView = CollectionViewSource.GetDefaultView(_allCards);
        CardsView.Filter = FilterCard;
    }

    public override string Title => "AI Discovery";

    public override string Description =>
        "Discovered agents and AI components, with the confidence, detector and evidence behind each one.";

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
    private async Task RefreshAsync() => await LoadAsync(CancellationToken.None).ConfigureAwait(true);

    /// <summary>
    /// Offers to run <c>defenseclaw agent discover</c> — the one mutation this panel can
    /// trigger, and only after an explicit confirm. Every other action here is a read.
    /// </summary>
    [RelayCommand]
    private async Task RunDiscoverAsync()
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Run agent discovery?",
            Content = "This will run:\n\n    defenseclaw agent discover\n\n" +
                      "The exact command, its live output and its exit code are recorded in the Activity panel. " +
                      "Local caches are re-read afterwards to refresh this view.",
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = true,
        };

        var result = await dialog.ShowDialogAsync().ConfigureAwait(true);
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        IsRunningDiscover = true;
        LastRunSummary = "Running…";

        try
        {
            var invocation = await Services.Cli.RunAsync(new[] { "agent", "discover" }).ConfigureAwait(true);
            LastRunSummary = invocation.FailureReason is { Length: > 0 } reason
                ? $"Could not run: {reason}"
                : $"defenseclaw agent discover finished at {invocation.FinishedAt?.ToLocalTime():HH:mm:ss} " +
                  $"(exit {invocation.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}). See Activity for full output.";
        }
        catch (CliNotFoundException ex)
        {
            LastRunSummary = $"Could not run: {ex.Message}";
        }
        finally
        {
            IsRunningDiscover = false;
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value) => CardsView.Refresh();

    private bool FilterCard(object obj)
    {
        if (obj is not DiscoveryComponentCard card)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return Contains(card.Vendor) || Contains(card.Product) || Contains(card.CategoriesDisplay);

        bool Contains(string? value) => value is not null && value.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;
        Sources.Clear();

        try
        {
            var (signals, signalSource) = await LoadSignalsAsync(cancellationToken).ConfigureAwait(true);

            _allCards.Clear();
            foreach (var card in BuildCards(signals))
            {
                _allCards.Add(card);
            }

            CardsView.Refresh();
            Sources.Add(signalSource);

            HasNoComponents = _allCards.Count == 0;
            StatusMessage = _allCards.Count == 0
                ? "No AI components discovered yet."
                : $"{_allCards.Count} component{(_allCards.Count == 1 ? string.Empty : "s")} from {signals.Count} signal{(signals.Count == 1 ? string.Empty : "s")}.";

            await LoadScanHistoryAsync(cancellationToken).ConfigureAwait(true);
            await LoadAgentDiscoveryAsync(cancellationToken).ConfigureAwait(true);
            await LoadAgentSelectionAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Primary source: the persisted signal cache (<c>ai_discovery_state.json</c>), which
    /// carries first/last-seen and hashed evidence per signal. Falls back to the
    /// <c>ai_signals</c> table in <c>inventory.db</c> — the same data, DB-shaped, with
    /// slightly thinner evidence — when the cache file is missing or unreadable.
    /// </summary>
    private async Task<(IReadOnlyList<DiscoverySignalRecord> Signals, DiscoverySourceInfo Source)> LoadSignalsAsync(
        CancellationToken cancellationToken)
    {
        var path = Services.Paths.AiDiscoveryStatePath;
        try
        {
            if (File.Exists(path))
            {
                var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;

                var signals = new List<DiscoverySignalRecord>();
                if (root.TryGetProperty("signals", out var signalsElement) && signalsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in signalsElement.EnumerateObject())
                    {
                        signals.Add(MapSignalFromState(property.Value));
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

            var rows = await Services.Inventory.BrowseAsync("ai_signals", limit: 5000, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            var signals = rows.Rows.Select(MapSignalFromDbRow).ToList();
            return (signals, new DiscoverySourceInfo(
                "Signal cache — inventory.db (ai_signals table, fallback)",
                $"{note} {signals.Count} signals.",
                null,
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

            var events = await Services.Audit.ListAsync(
                new AuditQuery { Bucket = AuditBucket, Limit = 25 }, cancellationToken).ConfigureAwait(true);

            foreach (var evt in events)
            {
                ScanHistory.Add(new DiscoveryScanEvent(
                    evt.Timestamp,
                    evt.StructuredString("defenseclaw.ai.discovery.source"),
                    GetLong(evt, "defenseclaw.ai.discovery.signals_total"),
                    GetLong(evt, "defenseclaw.ai.discovery.active_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.new_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.changed_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.gone_signals"),
                    GetLong(evt, "defenseclaw.ai.discovery.duration_ms"),
                    evt.StructuredString("defenseclaw.ai.discovery.result")));
            }

            Sources.Add(new DiscoverySourceInfo(
                $"Audit trail — {AuditBucket} bucket",
                $"{events.Count} recent scan event{(events.Count == 1 ? string.Empty : "s")}",
                events.Count > 0 ? events[0].Timestamp : null,
                Available: events.Count > 0));
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Sources.Add(new DiscoverySourceInfo($"Audit trail — {AuditBucket} bucket", $"Could not read audit.db: {ex.Message}", null, Available: false));
        }
    }

    private async Task LoadAgentDiscoveryAsync(CancellationToken cancellationToken)
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

            var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
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
                        NullIfEmpty(GetString(value, "error"))));
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
        ConnectorSelections.Clear();
        var selectionPath = FindAgentSelectionPath();

        try
        {
            if (selectionPath is null || !File.Exists(selectionPath))
            {
                Sources.Add(new DiscoverySourceInfo("Connector selection — agent_selection.json", "File not found.", null, Available: false));
                return;
            }

            var raw = await File.ReadAllTextAsync(selectionPath, cancellationToken).ConfigureAwait(true);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.TryGetProperty("selections", out var selections) && selections.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in selections.EnumerateObject())
                {
                    var value = property.Value;
                    ConnectorSelections.Add(new AgentSelectionRow(
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
                ConnectorSelections.Count == 0
                    ? "No connector currently selected."
                    : $"{ConnectorSelections.Count} selection{(ConnectorSelections.Count == 1 ? string.Empty : "s")}: " +
                      string.Join(", ", ConnectorSelections.Select(s => s.Connector)),
                updatedAt,
                Available: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Sources.Add(new DiscoverySourceInfo("Connector selection — agent_selection.json", $"Could not read: {ex.Message}", null, Available: false));
        }
    }

    /// <summary>
    /// <c>agent_selection.json</c> lives next to <c>agent_discovery.json</c> in the data
    /// directory; <see cref="DefenseClaw.Core.Paths.DefenseClawPaths"/> does not expose it
    /// by name, so this resolves it the same way the paths type resolves its siblings.
    /// </summary>
    private string FindAgentSelectionPath() =>
        Path.Combine(Path.GetDirectoryName(Services.Paths.AgentDiscoveryStatePath) ?? Services.Paths.DataDirectory, "agent_selection.json");

    private static IEnumerable<DiscoveryComponentCard> BuildCards(IReadOnlyList<DiscoverySignalRecord> signals) =>
        signals
            .GroupBy(s => (Vendor: string.IsNullOrWhiteSpace(s.Vendor) ? "(unknown vendor)" : s.Vendor,
                           Product: string.IsNullOrWhiteSpace(s.Product) ? "(unknown product)" : s.Product),
                StringTupleComparer.Instance)
            .Select(g => new DiscoveryComponentCard { Vendor = g.Key.Vendor, Product = g.Key.Product, Signals = g.ToList() })
            .OrderByDescending(c => c.MaxConfidence)
            .ThenBy(c => c.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Product, StringComparer.OrdinalIgnoreCase);

    private static DiscoverySignalRecord MapSignalFromState(JsonElement element)
    {
        var evidence = new List<DiscoveryEvidenceItem>();
        if (element.TryGetProperty("evidence", out var evidenceArray) && evidenceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in evidenceArray.EnumerateArray())
            {
                evidence.Add(new DiscoveryEvidenceItem(
                    GetString(item, "type") ?? "unknown",
                    GetString(item, "basename"),
                    GetDouble(item, "quality"),
                    GetString(item, "match_kind")));
            }
        }

        return new DiscoverySignalRecord(
            GetString(element, "vendor") ?? string.Empty,
            GetString(element, "product") ?? string.Empty,
            GetString(element, "category"),
            GetString(element, "detector"),
            GetString(element, "source"),
            GetDouble(element, "confidence"),
            GetString(element, "state"),
            GetTimestamp(element, "first_seen"),
            GetTimestamp(element, "last_seen"),
            evidence);
    }

    private static DiscoverySignalRecord MapSignalFromDbRow(IReadOnlyDictionary<string, object?> row)
    {
        var evidence = new List<DiscoveryEvidenceItem>();
        if (row.TryGetValue("evidence_json", out var raw) && raw is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        evidence.Add(new DiscoveryEvidenceItem(
                            GetString(item, "type") ?? "unknown",
                            GetString(item, "basename"),
                            GetDouble(item, "quality"),
                            GetString(item, "match_kind")));
                    }
                }
            }
            catch (JsonException)
            {
                // Leave evidence empty rather than fail the whole row over one bad cell.
            }
        }

        return new DiscoverySignalRecord(
            AsString(row, "vendor") ?? string.Empty,
            AsString(row, "product") ?? string.Empty,
            AsString(row, "category"),
            AsString(row, "detector"),
            null,
            AsDouble(row, "confidence"),
            AsString(row, "state"),
            null,
            AsTimestamp(row, "last_seen"),
            evidence);
    }

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

    private static double? GetDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTimestamp(JsonElement element, string property) =>
        GetString(element, property) is { } raw && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? AsString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static double? AsDouble(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

    private static DateTimeOffset? AsTimestamp(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return parsed;
        }

        var trimmed = raw.Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0 && trimmed[(lastSpace + 1)..].All(char.IsLetter) &&
            DateTimeOffset.TryParse(trimmed[..lastSpace], CultureInfo.InvariantCulture, styles, out var withoutZone))
        {
            return withoutZone;
        }

        return null;
    }

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
