using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Browser over <c>audit_events</c>: filter bar, virtualized row list, keyset paging and a
/// detail pane that pretty-prints <c>structured_json</c>.
/// <para>
/// <b>Paging is keyset, never OFFSET.</b> Every page carries an
/// <see cref="AuditCursor"/> built from <c>retention_timestamp_unix_nano</c> plus the row
/// id, so "Load more" stays O(1) against a table the gateway is still writing to and rows
/// cannot be skipped or repeated when new events land mid-scroll.
/// </para>
/// <para>
/// <b>Platform rows.</b> A large minority of rows have <c>connector IS NULL</c> — they are
/// platform-scoped, not connector-scoped. <see cref="AuditQuery.IncludeNullConnector"/>
/// folds them in beside a named connector; "platform only" has no SQL predicate in Core,
/// so it is applied as a refinement over the keyset stream (the cursor still comes from
/// the last raw row, which keeps paging correct).
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel : PanelViewModelBase
{
    /// <summary>Rows per keyset page.</summary>
    public const int PageSize = 100;

    private const string AnyBucket = "All buckets";
    private const string AnyAction = "Any action";

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private AuditCursor? _cursor;

    /// <summary>
    /// True once the bucket / connector / action lists have been read from audit.db. They are
    /// loaded by the first <see cref="LoadAsync"/> that finds the database, not by
    /// <see cref="InitializeAsync"/>: that runs once, on the first visit, and if audit.db did
    /// not exist yet (it appears with the first event) the lists used to stay at their
    /// "All ..." placeholders for good even after the database showed up and Refresh worked.
    /// A failed read leaves this false, so the next load retries.
    /// </summary>
    private bool _filterOptionsLoaded;

    [ObservableProperty]
    private string _selectedBucket = AnyBucket;

    [ObservableProperty]
    private SeverityOption _selectedSeverity = SeverityOption.Any;

    [ObservableProperty]
    private ConnectorOption _selectedConnector = ConnectorOption.All;

    [ObservableProperty]
    private TimeRangeOption _selectedRange = TimeRangeOption.Day;

    [ObservableProperty]
    private string _actionFilter = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedActionOption = AnyAction;

    [ObservableProperty]
    private AuditRow? _selectedRow;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private string _resultSummary = "Loading…";

    [ObservableProperty]
    private string _statusNote = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyTitle = "No matching events";

    [ObservableProperty]
    private string _emptyDetail = "Widen the time range or clear a filter.";

    public AuditPanelViewModel(AppServices services)
        : base(services)
    {
        Buckets.Add(AnyBucket);
        Actions.Add(AnyAction);
        Connectors.Add(ConnectorOption.All);

        foreach (var option in SeverityOption.All)
        {
            Severities.Add(option);
        }

        foreach (var option in TimeRangeOption.All)
        {
            Ranges.Add(option);
        }
    }

    public override string Title => "Audit";

    public override string Description =>
        "Browse audit_events by bucket, severity, connector and time. Read-only, keyset paged.";

    public ObservableCollection<AuditRow> Rows { get; } = new();

    public ObservableCollection<string> Buckets { get; } = new();

    public ObservableCollection<string> Actions { get; } = new();

    public ObservableCollection<ConnectorOption> Connectors { get; } = new();

    public ObservableCollection<SeverityOption> Severities { get; } = new();

    public ObservableCollection<TimeRangeOption> Ranges { get; } = new();

    /// <summary>Drives the detail pane's placeholder without an inverse-boolean converter.</summary>
    public bool HasSelection => SelectedRow is not null;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Services.Audit.Exists)
        {
            IsEmpty = true;
            EmptyTitle = "No audit database yet";
            EmptyDetail = $"{Services.Paths.AuditDatabasePath} appears after DefenseClaw records its first event.";
            ResultSummary = string.Empty;
            return;
        }

        // The filter lists load inside LoadAsync, the first time it finds the database.
        await LoadAsync(append: false, cancellationToken);
    }

    partial void OnSelectedBucketChanged(string value) => Reload();

    partial void OnSelectedSeverityChanged(SeverityOption value) => Reload();

    partial void OnSelectedConnectorChanged(ConnectorOption value) => Reload();

    partial void OnSelectedRangeChanged(TimeRangeOption value) => Reload();

    partial void OnActionFilterChanged(string value) => Reload();

    partial void OnSearchTextChanged(string value) => Reload();

    partial void OnSelectedRowChanged(AuditRow? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>The action dropdown is a helper that fills the substring box, not a second filter.</summary>
    partial void OnSelectedActionOptionChanged(string value) =>
        ActionFilter = string.Equals(value, AnyAction, StringComparison.Ordinal) ? string.Empty : value;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(append: false, CancellationToken.None);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(append: true, CancellationToken.None);

    [RelayCommand]
    private void ResetFilters()
    {
        SelectedBucket = AnyBucket;
        SelectedSeverity = SeverityOption.Any;
        SelectedConnector = Connectors[0];
        SelectedRange = TimeRangeOption.Day;
        SelectedActionOption = AnyAction;
        ActionFilter = string.Empty;
        SearchText = string.Empty;
    }

    private void Reload() => _ = LoadAsync(append: false, CancellationToken.None);

    private async Task LoadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buckets = await Services.Audit.ListBucketsAsync(cancellationToken);
            var connectors = await Services.Audit.ListConnectorsAsync(cancellationToken);
            var actions = await Services.Audit.ListActionsAsync(cancellationToken);

            foreach (var bucket in buckets)
            {
                Buckets.Add(bucket);
            }

            foreach (var connector in connectors)
            {
                Connectors.Add(ConnectorOption.Named(connector));
                Connectors.Add(ConnectorOption.WithPlatform(connector));
            }

            // ListConnectorsAsync deliberately omits NULL, so the platform bucket is added here.
            Connectors.Add(ConnectorOption.PlatformOnlyOption);

            foreach (var action in actions)
            {
                Actions.Add(action);
            }

            // All three reads succeeded before anything was added, so a retry never duplicates.
            _filterOptionsLoaded = true;
        }
#pragma warning disable CA1031 // Losing the dropdowns must not lose the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            StatusNote = $"Filter options could not be read: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private async Task LoadAsync(bool append, CancellationToken cancellationToken)
    {
        if (!Services.Audit.Exists)
        {
            return;
        }

        if (append && _cursor is null)
        {
            // Nothing anchored to page from; a fresh load would duplicate the first page.
            HasMore = false;
            return;
        }

        // Filter changes arrive in bursts (a combo box can fire twice); the gate serializes
        // them so the reader never has two overlapping connections open on the same page.
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;

            // Inside the gate, so a burst of filter changes cannot load the lists twice. The
            // note a failed read sets is cleared by a successful query below, as before.
            if (!_filterOptionsLoaded)
            {
                await LoadFilterOptionsAsync(cancellationToken);
            }

            var query = BuildQuery(append ? _cursor : null);
            var page = await Services.Audit.QueryAsync(query, cancellationToken);

            if (!append)
            {
                Rows.Clear();
            }

            foreach (var row in page.Events)
            {
                if (SelectedConnector.PlatformOnly && row.Connector is not null)
                {
                    continue;
                }

                Rows.Add(new AuditRow(row));
            }

            _cursor = page.NextCursor;
            HasMore = page.HasMore;

            await UpdateSummaryAsync(query, cancellationToken);

            IsEmpty = Rows.Count == 0;
            if (IsEmpty)
            {
                EmptyTitle = "No matching events";
                EmptyDetail = SelectedConnector.PlatformOnly
                    ? "No platform-scoped rows in this window. Platform rows are the ones with no connector attribution."
                    : "Widen the time range, lower the minimum severity, or clear the action filter.";
            }

            StatusNote = string.Empty;
        }
#pragma warning disable CA1031 // The DB is owned by the gateway; a busy file must degrade, not crash.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            StatusNote = $"audit.db could not be read: {ex.Message}";
            IsEmpty = Rows.Count == 0;
            EmptyTitle = "Audit database unavailable";
            EmptyDetail = ex.Message;
        }
#pragma warning restore CA1031
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    private async Task UpdateSummaryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        if (SelectedConnector.PlatformOnly)
        {
            // A SQL COUNT cannot express the platform-only refinement, so report what is
            // actually on screen rather than a number that would not match it.
            ResultSummary = $"{Rows.Count.ToString("N0", CultureInfo.CurrentCulture)} platform row(s) loaded · {SelectedRange.Label}";
            return;
        }

        var total = await Services.Audit.CountAsync(query with { After = null, Limit = PageSize }, cancellationToken);
        ResultSummary =
            $"{Rows.Count.ToString("N0", CultureInfo.CurrentCulture)} of " +
            $"{total.ToString("N0", CultureInfo.CurrentCulture)} matching events · {SelectedRange.Label}";
    }

    private AuditQuery BuildQuery(AuditCursor? after) => new()
    {
        Bucket = string.Equals(SelectedBucket, AnyBucket, StringComparison.Ordinal) ? null : SelectedBucket,
        MinimumSeverity = SelectedSeverity.Value,
        Connector = SelectedConnector.Connector,
        IncludeNullConnector = SelectedConnector.IncludeNull,
        ActionContains = string.IsNullOrWhiteSpace(ActionFilter) ? null : ActionFilter.Trim(),
        SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        From = SelectedRange.Since is { } window ? DateTimeOffset.UtcNow - window : null,
        Limit = PageSize,
        After = after,
    };
}

/// <summary>Minimum-severity choice for the filter bar.</summary>
public sealed class SeverityOption
{
    private SeverityOption(string label, AuditSeverity? value)
    {
        Label = label;
        Value = value;
    }

    public static SeverityOption Any { get; } = new("Any severity", null);

    /// <summary>Any, then the ladder from CRITICAL down — the order an operator scans.</summary>
    public static IReadOnlyList<SeverityOption> All { get; } = BuildAll();

    public string Label { get; }

    public AuditSeverity? Value { get; }

    public override string ToString() => Label;

    private static IReadOnlyList<SeverityOption> BuildAll()
    {
        var options = new List<SeverityOption> { Any };
        foreach (var severity in AuditSeverityExtensions.Ladder.Reverse())
        {
            options.Add(new SeverityOption($"{severity.ToStoredValue()} and above", severity));
        }

        return options;
    }
}

/// <summary>
/// Connector choice. Three shapes, because the column is nullable: a named connector, a
/// named connector plus platform rows, and platform rows alone.
/// </summary>
public sealed class ConnectorOption
{
    private ConnectorOption(string label, string? connector, bool includeNull, bool platformOnly)
    {
        Label = label;
        Connector = connector;
        IncludeNull = includeNull;
        PlatformOnly = platformOnly;
    }

    public static ConnectorOption All { get; } = new("All connectors", null, false, false);

    public static ConnectorOption PlatformOnlyOption { get; } =
        new("Platform only (no connector)", null, false, true);

    public string Label { get; }

    public string? Connector { get; }

    /// <summary>Maps to <see cref="AuditQuery.IncludeNullConnector"/>.</summary>
    public bool IncludeNull { get; }

    /// <summary>Refined over the keyset stream; Core has no <c>connector IS NULL</c> predicate.</summary>
    public bool PlatformOnly { get; }

    public static ConnectorOption Named(string name) => new(name, name, false, false);

    public static ConnectorOption WithPlatform(string name) => new($"{name} + platform rows", name, true, false);

    public override string ToString() => Label;
}

/// <summary>Time-range preset for the filter bar.</summary>
public sealed class TimeRangeOption
{
    private TimeRangeOption(string label, TimeSpan? since)
    {
        Label = label;
        Since = since;
    }

    public static TimeRangeOption Day { get; } = new("Last 24 hours", TimeSpan.FromHours(24));

    public static IReadOnlyList<TimeRangeOption> All { get; } = new[]
    {
        new TimeRangeOption("Last hour", TimeSpan.FromHours(1)),
        Day,
        new TimeRangeOption("Last 7 days", TimeSpan.FromDays(7)),
        new TimeRangeOption("Last 30 days", TimeSpan.FromDays(30)),
        new TimeRangeOption("All time", null),
    };

    public string Label { get; }

    public TimeSpan? Since { get; }

    public override string ToString() => Label;
}

/// <summary>One key/value row in the audit detail pane.</summary>
public sealed record AuditDetailField(string Name, string Value);

/// <summary>
/// One <c>audit_events</c> row, flattened for display. <c>structured_json</c> is parsed
/// lazily by Core, so building a row stays cheap even at a page of 100.
/// </summary>
public sealed class AuditRow
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public AuditRow(AuditEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        Timestamp = source.Timestamp;
        // Most browsing happens inside the last day, where repeating the date in every row
        // costs column width the summary needs. The full value stays on the tooltip.
        var local = source.Timestamp.ToLocalTime();
        TimestampText = source.Timestamp == DateTimeOffset.MinValue
            ? source.RawTimestamp
            : local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        RelativeTime = Relative(source.Timestamp);
        Severity = string.IsNullOrWhiteSpace(source.Severity) ? "—" : source.Severity;
        SeverityKey = KeyFor(source.SeverityLevel);
        Bucket = source.Bucket ?? "—";
        Action = source.Action;
        Connector = source.Connector ?? "platform";
        IsPlatform = source.Connector is null;
        EventName = source.EventName ?? string.Empty;
        Details = source.Details ?? string.Empty;
        Target = source.Target ?? string.Empty;
        Actor = source.Actor ?? string.Empty;
        ToolName = source.ToolName ?? string.Empty;
        AgentName = source.AgentName ?? string.Empty;
        Source = source.Source ?? string.Empty;
        Signal = source.Signal ?? string.Empty;
        SessionId = source.SessionId ?? string.Empty;
        RunId = source.RunId ?? string.Empty;
        RequestId = source.RequestId ?? string.Empty;
        TraceId = source.TraceId ?? string.Empty;
        BinaryVersion = source.BinaryVersion ?? string.Empty;
        RawTimestamp = source.RawTimestamp;
        StructuredJson = PrettyJson(source.StructuredJsonRaw);
        Fields = BuildFields(source);
    }

    public string Id { get; }

    public DateTimeOffset Timestamp { get; }

    public string TimestampText { get; }

    public string RelativeTime { get; }

    public string Severity { get; }

    /// <summary>Critical / High / Medium / Low / Info — the view's colour key.</summary>
    public string SeverityKey { get; }

    public string Bucket { get; }

    public string Action { get; }

    public string Connector { get; }

    public bool IsPlatform { get; }

    public string EventName { get; }

    public string Details { get; }

    public string Target { get; }

    public string Actor { get; }

    public string ToolName { get; }

    public string AgentName { get; }

    public string Source { get; }

    public string Signal { get; }

    public string SessionId { get; }

    public string RunId { get; }

    public string RequestId { get; }

    public string TraceId { get; }

    public string BinaryVersion { get; }

    public string RawTimestamp { get; }

    /// <summary>Pretty-printed <c>structured_json</c>; the raw text when it will not parse.</summary>
    public string StructuredJson { get; }

    public IReadOnlyList<AuditDetailField> Fields { get; }

    public string Summary => Details.Length > 0 ? Details : EventName;

    private static IReadOnlyList<AuditDetailField> BuildFields(AuditEvent source)
    {
        var fields = new List<AuditDetailField>();

        Add("id", source.Id);
        Add("timestamp", source.RawTimestamp);
        Add("bucket", source.Bucket);
        Add("action", source.Action);
        Add("event_name", source.EventName);
        Add("severity", source.Severity);
        Add("connector", source.Connector ?? "(platform / none)");
        Add("actor", source.Actor);
        Add("target", source.Target);
        Add("tool_name", source.ToolName);
        Add("agent_name", source.AgentName);
        Add("source", source.Source);
        Add("signal", source.Signal);
        Add("session_id", source.SessionId);
        Add("run_id", source.RunId);
        Add("request_id", source.RequestId);
        Add("trace_id", source.TraceId);
        Add("binary_version", source.BinaryVersion);
        Add("details", source.Details);

        return fields;

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(new AuditDetailField(name, value));
            }
        }
    }

    private static string PrettyJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(no structured payload on this row)";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            // One malformed row must still be readable.
            return raw;
        }
    }

    private static string KeyFor(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => "Critical",
        AuditSeverity.High => "High",
        AuditSeverity.Medium or AuditSeverity.Warn => "Medium",
        AuditSeverity.Low => "Low",
        AuditSeverity.Info => "Info",
        _ => "Neutral",
    };

    private static string Relative(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var delta = DateTimeOffset.UtcNow - value;
        return delta switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            _ => $"{(int)delta.TotalDays}d ago",
        };
    }
}
