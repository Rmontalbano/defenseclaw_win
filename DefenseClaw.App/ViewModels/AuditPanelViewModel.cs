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
/// <para>
/// <b>Latest wins.</b> A filter change starts a new <em>generation</em> of loads: everything still
/// running or waiting for an older generation is cancelled, and a scan that is already inside SQLite is
/// interrupted (the reader hands its token to <c>sqlite3_interrupt</c>). A text search is an unindexed scan
/// of the whole window, 1.4 s for 24 h and 17 s for 7 days on a real database, and it used to run to the
/// end for every debounced keystroke, one behind the other; now only the last value's search finishes, and
/// a burst of changes made together (<see cref="ResetFiltersCommand"/> changes up to seven properties)
/// is one load, not seven.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel : PanelViewModelBase
{
    /// <summary>Rows per keyset page.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// The most rows <see cref="Rows"/> ever holds: twenty pages. The list is newest-first and
    /// "Load more" only appends older rows, so past this the panel stops paging and says so
    /// (<see cref="IsRowCapReached"/>) rather than dropping the newest rows off the top or growing
    /// for as long as the operator keeps clicking. Each row carries its flattened columns and a
    /// detail field list, so an unbounded list is unbounded memory - the Logs panel caps its list
    /// at 5,000 single-line rows and Activity at its 200-entry ring; this is the same idea sized
    /// for a heavier row.
    /// </summary>
    public const int MaxRows = 20 * PageSize;

    private const string AnyBucket = "All buckets";
    private const string AnyAction = "Any action";

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _generationLock = new();
    private CancellationTokenSource _generationSource = new();
    private CancellationToken _generationToken;
    private AuditCursor? _cursor;

    /// <summary>While above zero, filter changes only note that a reload is due (see <see cref="ResetFilters"/>).</summary>
    private int _reloadDeferrals;

    private bool _reloadPending;

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

    /// <summary>
    /// True when more rows match than <see cref="MaxRows"/> lets the list hold, so the operator
    /// is looking at the newest <see cref="MaxRows"/> and "Load more" is gone. Cleared by any
    /// fresh load. See <see cref="RowCapNotice"/> for what the view says about it.
    /// </summary>
    [ObservableProperty]
    private bool _isRowCapReached;

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
        _generationToken = _generationSource.Token;
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

    /// <summary>
    /// The most recent load a filter change started (a finished one once it has settled). Tests await it: a filter change
    /// starts its load without being awaited, and the reader's call counters are what show how many queries it cost.
    /// </summary>
    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The gate loads queue behind; a test holds it to line a burst of changes up before any of them can query.</summary>
    internal SemaphoreSlim LoadGate => _loadGate;

    /// <summary>What the list footer says while <see cref="IsRowCapReached"/>.</summary>
    public string RowCapNotice =>
        $"Showing the newest {MaxRows.ToString("N0", CultureInfo.CurrentCulture)} matching events. " +
        "Narrow the time range or add a filter to reach older ones.";

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

    /// <summary>Closes the detail pane (Esc, or its own close button).</summary>
    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    private IReadOnlyList<AuditRow> _selectedMany = Array.Empty<AuditRow>();

    /// <summary>Every row the table has selected (it is Extended-select; <see cref="SelectedRow"/> is the first, which drives the detail pane).</summary>
    public IReadOnlyList<AuditRow> SelectedRows => _selectedMany;

    /// <summary>Called by the view whenever the table's selection changes.</summary>
    public void NoteSelection(IEnumerable<AuditRow> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        _selectedMany = selected.ToList();
    }

    /// <summary>The rows a menu action applies to: the table's selection, or the one row the detail pane shows when the view has not reported a selection.</summary>
    public IReadOnlyList<AuditRow> ActionRows =>
        _selectedMany.Count > 0 ? _selectedMany : SelectedRow is { } one ? new[] { one } : Array.Empty<AuditRow>();

    /// <summary>"time action target [SEVERITY] details" for each row, one per line (the Mac's Copy Details).</summary>
    public static string CopyDetailsText(IEnumerable<AuditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join(Environment.NewLine, rows.Select(r => r.CopyLine));
    }

    /// <summary>The pretty-printed <c>structured_json</c> of each row, one after the other (the Mac's Copy Structured JSON).</summary>
    public static string CopyStructuredJsonText(IEnumerable<AuditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join(Environment.NewLine, rows.Select(r => r.StructuredJson));
    }

    /// <summary>
    /// "Show same target": narrows the list to events about the row's target by putting it in the search box (the search
    /// matches the target column, among others), which reloads like any other search. A row with no target changes nothing.
    /// </summary>
    public void ShowSameTarget(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Target.Length > 0)
        {
            SearchText = row.Target;
        }
    }

    /// <summary>
    /// Puts every filter back and reloads <em>once</em>. Each of the seven properties reloads when it changes, so from a
    /// fully filtered state this used to start up to seven page queries and seven counts on a multi-gigabyte database, the
    /// first six of them for a filter set that never existed on screen.
    /// </summary>
    [RelayCommand]
    private void ResetFilters()
    {
        _reloadDeferrals++;
        try
        {
            SelectedBucket = AnyBucket;
            SelectedSeverity = SeverityOption.Any;
            SelectedConnector = Connectors[0];
            SelectedRange = TimeRangeOption.Day;
            SelectedActionOption = AnyAction;
            ActionFilter = string.Empty;
            SearchText = string.Empty;
        }
        finally
        {
            _reloadDeferrals--;
        }

        if (_reloadDeferrals == 0 && _reloadPending)
        {
            _reloadPending = false;
            Reload();
        }
    }

    private void Reload()
    {
        if (_reloadDeferrals > 0)
        {
            _reloadPending = true;
            return;
        }

        LastLoad = LoadAsync(append: false, CancellationToken.None);
    }

    /// <summary>
    /// Starts a new generation of loads: cancels the previous one (whatever it was doing, including a scan already inside
    /// SQLite) and returns the token of the new one.
    /// </summary>
    private CancellationToken StartGeneration()
    {
        CancellationTokenSource previous;
        var next = new CancellationTokenSource();
        lock (_generationLock)
        {
            previous = _generationSource;
            _generationSource = next;
            _generationToken = next.Token;
        }

        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
        }

        return next.Token;
    }

    private CancellationToken CurrentGeneration()
    {
        lock (_generationLock)
        {
            return _generationToken;
        }
    }

    private bool IsCurrentGeneration(CancellationToken generation)
    {
        lock (_generationLock)
        {
            return generation == _generationToken;
        }
    }

    private async Task LoadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Three loose index scans (a millisecond each), started together rather than one after the other.
            var bucketsRead = Services.Audit.ListBucketsAsync(cancellationToken);
            var connectorsRead = Services.Audit.ListConnectorsAsync(cancellationToken);
            var actionsRead = Services.Audit.ListActionsAsync(cancellationToken);
            await Task.WhenAll(bucketsRead, connectorsRead, actionsRead);
            var buckets = await bucketsRead;
            var connectors = await connectorsRead;
            var actions = await actionsRead;

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

        if (append && (_cursor is null || Rows.Count >= MaxRows))
        {
            // Nothing anchored to page from (a fresh load would duplicate the first page), or the
            // list is already full: either way there is no next page to add.
            HasMore = false;
            return;
        }

        // A fresh load starts a new generation and so cancels whatever the previous one is doing; "Load more" belongs to
        // the generation whose rows it extends, so a newer fresh load cancels it too.
        var generation = append ? CurrentGeneration() : StartGeneration();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(generation, cancellationToken);
        var token = linked.Token;

        // Filter changes arrive in bursts (a combo box can fire twice, a search box types five letters); the gate
        // serializes them so the reader never has two overlapping connections open on the same page. Only the newest
        // generation gets through it to a query: an older one is cancelled while it waits.
        try
        {
            await _loadGate.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            IsLoading = true;

            // Inside the gate, so a burst of filter changes cannot load the lists twice. The
            // note a failed read sets is cleared by a successful query below, as before. They are read beside
            // the page and its count, not ahead of them: the first Audit visit used to sit through three
            // serial DISTINCT scans before the first row was requested.
            Task filterOptions = _filterOptionsLoaded ? Task.CompletedTask : LoadFilterOptionsAsync(token);

            var query = BuildQuery(append ? _cursor : null);
            var platformOnly = SelectedConnector.PlatformOnly;
            var pageRead = Services.Audit.QueryAsync(query, token);

            // A SQL COUNT cannot express the platform-only refinement, so that view reports what is actually on screen
            // rather than a number that would not match it. Every other view counts beside the page, not after it.
            var totalRead = platformOnly
                ? null
                : Services.Audit.CountAsync(query with { After = null, Limit = PageSize }, token);

            await Task.WhenAll(filterOptions, pageRead, totalRead ?? Task.CompletedTask);

            // A result that arrives for a generation that has since been replaced is dropped whole: it must not clear
            // the rows of the load that replaced it.
            token.ThrowIfCancellationRequested();

            var page = await pageRead;
            if (!append)
            {
                Rows.Clear();
            }

            var truncated = false;
            foreach (var row in page.Events)
            {
                if (platformOnly && row.Connector is not null)
                {
                    continue;
                }

                if (Rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }

                Rows.Add(new AuditRow(row));
            }

            // At the cap with anything left over - unread rows in this page, or another page
            // behind it - the list is a window onto the newest MaxRows, not the whole result.
            var capReached = Rows.Count >= MaxRows && (truncated || page.HasMore);
            IsRowCapReached = capReached;

            _cursor = page.NextCursor;
            HasMore = page.HasMore && !capReached;

            ResultSummary = totalRead is null
                ? $"{Rows.Count.ToString("N0", CultureInfo.CurrentCulture)} platform row(s) loaded · {SelectedRange.Label}"
                : $"{Rows.Count.ToString("N0", CultureInfo.CurrentCulture)} of " +
                  $"{(await totalRead).ToString("N0", CultureInfo.CurrentCulture)} matching events · {SelectedRange.Label}";

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseded: a newer load owns the list, the flag and the summary. The scan it was in the middle of was
            // interrupted rather than left to run to the end.
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
            // The flag is the newest load's to clear: an older one finishing must not hide the ring while it still runs.
            if (IsCurrentGeneration(generation))
            {
                IsLoading = false;
            }

            _loadGate.Release();
        }
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
public sealed record AuditDetailField(string Name, string Value)
{
    /// <summary>"name: value" - what a screen reader says instead of the record's member dump.</summary>
    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>
/// One <c>audit_events</c> row, flattened for display. <c>structured_json</c> - often several
/// kilobytes - is only pretty-printed when <see cref="StructuredJson"/> is first read, which is
/// the detail pane showing this row, so building a page of 100 rows never parses a payload nobody
/// opens.
/// </summary>
public sealed class AuditRow
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private readonly Lazy<string> _structuredJson;

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
        SeverityRank = source.SeverityLevel switch
        {
            AuditSeverity.Critical => 4,
            AuditSeverity.High => 3,
            AuditSeverity.Medium or AuditSeverity.Warn => 2,
            AuditSeverity.Low => 1,
            AuditSeverity.Info => 0,
            _ => -1,
        };
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
        var rawStructured = source.StructuredJsonRaw;
        _structuredJson = new Lazy<string>(() => PrettyJson(rawStructured), LazyThreadSafetyMode.ExecutionAndPublication);
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

    /// <summary>
    /// Pretty-printed <c>structured_json</c>; the raw text when it will not parse. Computed on first
    /// read and kept - the detail pane's binding is the only reader - so a row nobody selects
    /// never pays for the parse and the indented re-serialize.
    /// </summary>
    public string StructuredJson => _structuredJson.Value;

    /// <summary>True once <see cref="StructuredJson"/> has been read (and so pretty-printed).</summary>
    internal bool IsStructuredJsonMaterialized => _structuredJson.IsValueCreated;

    public IReadOnlyList<AuditDetailField> Fields { get; }

    public string Summary => Details.Length > 0 ? Details : EventName;

    /// <summary>Critical 4 ... Info 0, unknown -1: what the Severity column sorts by (the words sort alphabetically, which is no order).</summary>
    public int SeverityRank { get; }

    /// <summary>One line for the clipboard: "Sep 30 11:35 hook_decision target [HIGH] details".</summary>
    public string CopyLine => $"{TimestampText} {Action} {Target} [{Severity}] {Summary}".Replace("  ", " ", StringComparison.Ordinal).Trim();

    /// <summary>
    /// What a screen reader announces for the row (UI Automation falls back to
    /// <c>ToString()</c> for a list item with no explicit name), instead of the type name: severity,
    /// action, what it was about, which connector, and when.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string> { $"{Severity} {Action}", Summary, $"connector {Connector}", TimestampText };
        return string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static IReadOnlyList<AuditDetailField> BuildFields(AuditEvent source)
    {
        var fields = new List<AuditDetailField>();

        // "id TEXT PRIMARY KEY" admits NULL, and the reader hands such a row an empty id: say so rather than omit the field.
        Add("id", string.IsNullOrEmpty(source.Id) ? "(none - this row's id is NULL)" : source.Id);
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
