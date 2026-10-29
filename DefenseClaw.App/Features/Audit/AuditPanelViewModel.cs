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
/// <para>
/// <b>A refresh of an unchanged database changes nothing on screen.</b> The reader remembers its last answers under the shared change
/// probe (<see cref="AuditReader"/>), so a Refresh that finds the database where the last read left it costs one probe: no query, no row
/// decoded. Whether the reader re-read or not, a fresh page whose rows are the ones already listed (same ids in the same order, same
/// total) leaves the list, the selection and the scroll position alone - the Mac's <c>appliedCanonicalIDs</c> comparison. The window
/// ("last 24 hours") starts on a whole minute for the same reason: two refreshes inside a minute ask for the same rows.
/// </para>
/// <para>
/// <b>A row that is too large is unavailable, not missing and not an empty list.</b> The reader leaves a <c>details</c> or
/// <c>structured_json</c> over its limit in the database and lists the row with the reason (<see cref="AuditEvent.Oversized"/>); the row is
/// shown, its detail text and its JSON say why they are missing, and the filter strip says how many ("1 event too large to display"). A
/// first page that is nothing but such rows is therefore a list of them with that note, never the "No matching events" state.
/// </para>
/// <para>
/// <b>Actionable only, and live.</b> The panel opens on the events the 0.8.10 TUI's Audit panel shows and stays current while it is on screen
/// (CUST-262): see <c>AuditPanelViewModel.Actionable.cs</c> and <c>AuditPanelViewModel.Live.cs</c>.
/// </para>
/// <para>
/// <b>Search tokens, hook rows, Current state, the Connector column.</b> The search box takes <c>connector:codex</c>, <c>severity:high</c>, <c>run:</c>,
/// <c>trace:</c>, <c>id:</c>, <c>actor:</c>, <c>type:</c>, <c>target:</c> ... as filters of the list's own query; a connector's hook call reads
/// <c>claudecode · preToolUse</c> / <c>allow · 320ms</c> (<see cref="AuditRow.TargetText"/>, <see cref="AuditRow.DetailsText"/>); selecting an event about a
/// skill, MCP server, plugin or tool shows its current state; and a Connector column joins the table while more than one connector is active (CUST-261): see
/// <c>AuditPanelViewModel.Search.cs</c>.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel : PanelViewModelBase, IAcceptsNavigation
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

    /// <summary>
    /// What the list was last built from by a fresh load (not "Load more"): the page, its total, whether the platform-only refinement was
    /// applied and the name of the time range (the summary line says it). A fresh load that reads the same thing leaves the list alone. Null
    /// whenever the list is not a known result (while it is being rebuilt, after an error, after switching source).
    /// </summary>
    private ShownSnapshot? _shown;

    /// <summary>
    /// True once "Load more" has added pages to the list since the last fresh load. A refresh then still starts over from the newest page
    /// (that is what Refresh and the row-cap notice promise), so an extended list is never "the same as shown".
    /// </summary>
    private bool _extended;

    private sealed record ShownSnapshot(AuditPage Page, int? Total, bool PlatformOnly, string RangeLabel, bool Actionable);

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

    /// <summary>The clock the time window is read from; a test fixes it so a refresh can never straddle the minute the window starts on.</summary>
    internal TimeProvider TimeSource { get; set; } = TimeProvider.System;

    /// <summary>What the list footer says while <see cref="IsRowCapReached"/>.</summary>
    public string RowCapNotice =>
        $"Showing the newest {MaxRows.ToString("N0", CultureInfo.CurrentCulture)} matching events. " +
        "Narrow the time range or add a filter to reach older ones.";

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshArchiveSetting();
        if (!Services.Audit.Exists)
        {
            ShowNoLiveDatabase();
            return;
        }

        // The filter lists load inside LoadAsync, the first time it finds the database.
        await LoadAsync(append: false, cancellationToken);
    }

    private void ShowNoLiveDatabase()
    {
        IsEmpty = true;
        EmptyTitle = "No audit database yet";
        EmptyDetail = $"{Services.Paths.AuditDatabasePath} appears after DefenseClaw records its first event.";
        ResultSummary = string.Empty;
    }

    partial void OnSelectedBucketChanged(string value) => Reload();

    partial void OnSelectedSeverityChanged(SeverityOption value) => Reload();

    partial void OnSelectedConnectorChanged(ConnectorOption value)
    {
        PublishConnectorScope(value);
        Reload();
    }

    // ---- The shared connector scope ------------------------------------------------------------------------------------
    // The scope (Services.ConnectorScope) is the one source of truth for WHICH connector; this panel's combo is that scope plus the
    // platform-row refinement the Mac's chip has no room for ("+ platform rows", "platform only"). Choosing a connector here sets the
    // shared scope; a scope change from the chip, Ctrl+Shift+M or Overview picks the matching option (keeping "+ platform rows" if it is
    // already that connector). "Platform only" belongs to no connector, so it leaves the scope alone and stays until a connector is
    // chosen. With one connector there is no chip and the scope refuses to narrow: the combo then keeps its own choice.

    private bool _applyingScope;

    private void PublishConnectorScope(ConnectorOption option)
    {
        if (_applyingScope || option.PlatformOnly)
        {
            return;
        }

        _ = Services.ConnectorScope.Set(option.Connector);
    }

    /// <summary>The shared scope changed: select the combo option that says so.</summary>
    protected override void OnConnectorScopeChanged()
    {
        var scope = Services.ConnectorScope.Current;
        var selected = SelectedConnector;
        ConnectorOption wanted;

        if (scope is null)
        {
            // Reset to All: a named choice follows it back; "platform only" is not a connector and stays.
            if (selected.PlatformOnly || selected.Connector is null)
            {
                return;
            }

            wanted = Connectors[0];
        }
        else if (string.Equals(selected.Connector, scope, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        else
        {
            wanted = Connectors.FirstOrDefault(c => !c.IncludeNull && !c.PlatformOnly && string.Equals(c.Connector, scope, StringComparison.OrdinalIgnoreCase))
                     ?? ConnectorOption.Named(scope);
            if (!Connectors.Contains(wanted))
            {
                // A connector the audit log has no rows for yet is still one the operator can scope to.
                Connectors.Insert(1, wanted);
            }
        }

        _applyingScope = true;
        try
        {
            SelectedConnector = wanted;
        }
        finally
        {
            _applyingScope = false;
        }
    }

    partial void OnSelectedRangeChanged(TimeRangeOption value) => Reload();

    partial void OnActionFilterChanged(string value) => Reload();

    partial void OnSearchTextChanged(string value) => Reload();

    partial void OnSelectedRowChanged(AuditRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        StartCorrelation(value);
        StartCurrentState(value);
    }

    /// <summary>The action dropdown is a helper that fills the substring box, not a second filter.</summary>
    partial void OnSelectedActionOptionChanged(string value) =>
        ActionFilter = string.Equals(value, AnyAction, StringComparison.Ordinal) ? string.Empty : value;

    [RelayCommand]
    private Task RefreshAsync() => IsArchive ? LastLoad = EnterArchiveAsync() : LoadAsync(append: false, CancellationToken.None);

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
            // As plain text: a target that reads like a token (type:abc) would otherwise be searched for as one.
            SearchText = SearchQuery.AsFreeText(row.Target);
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
            SelectedRange = DefaultRange;
            SelectedActionOption = AnyAction;
            ActionFilter = string.Empty;
            SearchText = string.Empty;
            ActivePreset = PresetAll;
            RunFilter = string.Empty;
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
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(FiltersHeader));
        RaiseActionableState();
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

    private async Task LoadFilterOptionsAsync(AuditReader reader, CancellationToken cancellationToken)
    {
        try
        {
            // Three loose index scans (a millisecond each), started together rather than one after the other.
            var bucketsRead = reader.ListBucketsAsync(cancellationToken);
            var connectorsRead = reader.ListConnectorsAsync(cancellationToken);
            var actionsRead = reader.ListActionsAsync(cancellationToken);
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
                // A connector the shared scope already added (before the database was read) is not listed twice.
                if (!Connectors.Any(c => !c.IncludeNull && string.Equals(c.Connector, connector, StringComparison.OrdinalIgnoreCase)))
                {
                    Connectors.Add(ConnectorOption.Named(connector));
                }

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
        _loadRequested = true;

        // The reader of the source on screen. An archive that failed its check has none: that is its error state, already shown.
        var reader = ActiveReader;
        if (reader is null)
        {
            return;
        }

        if (!reader.Exists)
        {
            if (IsArchive)
            {
                ShowArchiveError("The archive file is no longer there.");
            }

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
            Task filterOptions = _filterOptionsLoaded ? Task.CompletedTask : LoadFilterOptionsAsync(reader, token);

            var query = BuildQuery(append ? _cursor : null);
            var platformOnly = SelectedConnector.PlatformOnly;
            var actionable = IsActionableApplied;

            // The live refresh's baseline: the change probe's stamp and the time, taken before anything is read, so a commit that lands during the
            // read makes the next poll look once more and never skips one (see the Live partial). The archive is a file that does not change.
            var started = TimeSource.GetUtcNow();
            var baseline = IsArchive ? AuditStamp.Unknown : await Services.AuditChanges.SampleAsync(token);

            // The actionable view reads the same indexed pages, up to twenty to fill one, and keeps the rows the TUI's rule keeps (CUST-262).
            var pageRead = actionable ? reader.QueryActionableAsync(query, token) : reader.QueryAsync(query, token);

            // A SQL COUNT cannot express the platform-only refinement, so that view reports what is actually on screen
            // rather than a number that would not match it. Every other view counts beside the page, not after it.
            // The preset terms (blocks, scans, credentials) match inside the details text, which no index serves: counting them
            // scans the whole window (15 s for a day of blocks and over a minute for a week on a 6.7 GB database) while the page
            // itself, which stops at 100 matches, takes milliseconds. So those views do not count either. Nor does the actionable view:
            // the total counts every event of the window, and the list holds the actionable ones.
            var countable = !platformOnly && !actionable && query.ActionAnyOf is not { Count: > 0 };
            var totalRead = countable
                ? reader.CountAsync(query with { After = null, Limit = PageSize }, token)
                : null;

            await Task.WhenAll(filterOptions, pageRead, totalRead ?? Task.CompletedTask);

            // A result that arrives for a generation that has since been replaced is dropped whole: it must not clear
            // the rows of the load that replaced it.
            token.ThrowIfCancellationRequested();

            var page = await pageRead;
            int? total = totalRead is null ? null : await totalRead;

            // A fresh load that read what the list is already built from (the reader answered from its last result, or re-read and found the
            // same rows) changes nothing: the rows, the selection and the scroll position stay, and only the note is brought up to date.
            if (!append && SameAsShown(page, total, platformOnly, SelectedRange.Label, actionable))
            {
                // The same rows; the events the actionable view leaves out may still have grown in number (new low-signal rows arrived).
                if (HiddenCount != page.Hidden)
                {
                    HiddenCount = page.Hidden;
                    ResultSummary = Summarize(total, platformOnly, actionable, SelectedRange.Label);
                }

                StatusNote = OversizedNote();
                RememberLoad(page, append: false, IsRowCapReached);
                NoteLiveBaseline(baseline, started);
                return;
            }

            if (!append)
            {
                _shown = null;
                Rows.Clear();
                HiddenCount = 0;
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
            HiddenCount += page.Hidden;
            RememberLoad(page, append, capReached);

            ResultSummary = Summarize(total, platformOnly, actionable, SelectedRange.Label);

            IsEmpty = Rows.Count == 0;
            if (IsEmpty)
            {
                SetEmptyText(actionable);
            }

            // The note a successful read leaves: how many of the listed events had a value too large to load (and so cleared a stale error).
            StatusNote = OversizedNote();
            _extended = append;
            if (!append)
            {
                _shown = new ShownSnapshot(page, total, platformOnly, SelectedRange.Label, actionable);
                NoteLiveBaseline(baseline, started);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseded: a newer load owns the list, the flag and the summary. The scan it was in the middle of was
            // interrupted rather than left to run to the end.
        }
#pragma warning disable CA1031 // The DB is owned by the gateway; a busy file must degrade, not crash.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            if (IsArchive)
            {
                // A failure for a source the operator has since left is not the archive's to report.
                if (IsCurrentGeneration(generation))
                {
                    ShowArchiveError(AuditArchive.Describe(ex));
                }
            }
            else
            {
                StatusNote = $"audit.db could not be read: {ex.Message}";
                IsEmpty = Rows.Count == 0;
                EmptyTitle = "Audit database unavailable";
                EmptyDetail = ex.Message;
            }
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

    /// <summary>
    /// The query the list, its "Load more", the total, the live refresh and the export all read with: the filter bar, the preset and the run, narrowed by the
    /// search box (CUST-261) - its <c>field:value</c> tokens as database filters, the free words as the text search it always was.
    /// </summary>
    private AuditQuery BuildQuery(AuditCursor? after) => new AuditQuery
    {
        Bucket = string.Equals(SelectedBucket, AnyBucket, StringComparison.Ordinal) ? null : SelectedBucket,
        MinimumSeverity = PresetMinimumSeverity(ActivePreset, SelectedSeverity.Value),
        Connector = SelectedConnector.Connector,
        IncludeNullConnector = SelectedConnector.IncludeNull,
        ActionContains = string.IsNullOrWhiteSpace(ActionFilter) ? null : ActionFilter.Trim(),
        ActionAnyOf = PresetActionTerms(ActivePreset),
        RunId = string.IsNullOrWhiteSpace(RunFilter) ? null : RunFilter.Trim(),
        From = SelectedRange.Since is { } window ? WindowStart(window) : null,
        Limit = PageSize,
        After = after,
    }.WithSearch(ActiveSearch);

    /// <summary>
    /// Where a window of <paramref name="window"/> ending now starts: the whole minute it falls in. A window that starts at a different
    /// instant on every call is a different question on every call, so the reader could never recognise a Refresh as a repeat; the
    /// minute is invisible in a window of an hour or more, and it lets two refreshes inside it share an answer.
    /// </summary>
    private DateTimeOffset WindowStart(TimeSpan window)
    {
        var start = TimeSource.GetUtcNow() - window;
        return new DateTimeOffset(start.UtcTicks - (start.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
    }

    /// <summary>
    /// True when <paramref name="page"/> and <paramref name="total"/> are what the list is already built from: the same rows in the same
    /// order (the reader's remembered object, or a new read of the same ids - audit rows do not change), the same total, the same
    /// platform refinement and the same range name. Then a refresh has nothing to show.
    /// </summary>
    private bool SameAsShown(AuditPage page, int? total, bool platformOnly, string rangeLabel, bool actionable)
    {
        if (_extended || _shown is not { } shown || shown.PlatformOnly != platformOnly || shown.Total != total || shown.Actionable != actionable
            || !string.Equals(shown.RangeLabel, rangeLabel, StringComparison.Ordinal))
        {
            return false;
        }

        if (ReferenceEquals(shown.Page, page))
        {
            return true;
        }

        var before = shown.Page;
        if (before.HasMore != page.HasMore || before.NextCursor != page.NextCursor || before.Events.Count != page.Events.Count)
        {
            return false;
        }

        for (var i = 0; i < page.Events.Count; i++)
        {
            if (!string.Equals(before.Events[i].Id, page.Events[i].Id, StringComparison.Ordinal)
                || before.Events[i].Oversized.Count != page.Events[i].Oversized.Count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// "1 event too large to display" / "3 events too large to display" for the listed rows that have a value left in the database
    /// (<see cref="AuditRow.IsOversized"/>), else empty. Words, not a count alone, because this is what stands where a failed read's note
    /// would: the list is not empty and not broken, some of it is unavailable, and that is said.
    /// </summary>
    private string OversizedNote()
    {
        var count = Rows.Count(static row => row.IsOversized);
        return count switch
        {
            0 => string.Empty,
            1 => "1 event too large to display",
            _ => $"{count.ToString("N0", CultureInfo.CurrentCulture)} events too large to display",
        };
    }
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

    /// <summary>No lower bound: what "Same run" widens to, since a run can be older than the default window.</summary>
    public static TimeRangeOption AllTime { get; } = new("All time", null);

    public static IReadOnlyList<TimeRangeOption> All { get; } = new[]
    {
        new TimeRangeOption("Last hour", TimeSpan.FromHours(1)),
        Day,
        new TimeRangeOption("Last 7 days", TimeSpan.FromDays(7)),
        new TimeRangeOption("Last 30 days", TimeSpan.FromDays(30)),
        AllTime,
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
    private readonly Lazy<IReadOnlyList<DetailPair>> _detailPairs;

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
        Oversized = source.Oversized;
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
        TimestampNanos = source.TimestampNanos > 0
            ? source.TimestampNanos
            : source.Timestamp == DateTimeOffset.MinValue ? 0 : (source.Timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L;
        _detailPairs = new Lazy<IReadOnlyList<DetailPair>>(
            () => ParseDetailPairs(Details),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var rawStructured = source.StructuredJsonRaw;
        var structuredOversized = source.Oversized.FirstOrDefault(static o => string.Equals(o.Column, "structured_json", StringComparison.Ordinal));
        _structuredJson = new Lazy<string>(
            () => structuredOversized is null ? PrettyJson(rawStructured) : structuredOversized.Placeholder(CodeBoxColumns),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Fields = BuildFields(source);
    }

    /// <summary>
    /// The values of this event the reader left in the database because they are over its limit (<see cref="AuditEvent.Oversized"/>): which
    /// column, how big, what the limit was. Empty for a complete row. The row is listed all the same, with the reason where the value would be.
    /// </summary>
    public IReadOnlyList<OversizedValue> Oversized { get; }

    /// <summary>True when part of this event is too large to display (<see cref="Oversized"/>).</summary>
    public bool IsOversized => Oversized.Count > 0;

    /// <summary>"Too large to display: details is 3.2 MB, over the 256 KB limit"; empty for a complete row.</summary>
    public string OversizedNotice => IsOversized ? "Too large to display: " + OversizedValue.Describe(Oversized) : string.Empty;

    /// <summary>
    /// How many characters wide the inspector's JSON box is, give or take: it scrolls sideways instead of wrapping, so the placeholder for a
    /// value the reader did not load is broken into lines this long (<see cref="OversizedValue.Placeholder"/>) and none is cut off at the edge.
    /// </summary>
    private const int CodeBoxColumns = 40;

    /// <summary>What stands where a value the reader did not load would be in a field, which wraps by itself: the reason on one line, in the parentheses the "no structured payload" placeholder uses.</summary>
    private static string UnavailableText(OversizedValue value) => value.Placeholder(int.MaxValue);

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

    /// <summary>The Connector column's cell: the connector, or a dash for a platform row that belongs to none (the TUI's <c>—</c>).</summary>
    public string ConnectorCell => IsPlatform ? "—" : Connector;

    /// <summary>True for the audit row of a connector's hook call (<c>connector-hook</c>), which the table and the inspector read from its details (CUST-261).</summary>
    public bool IsHook => StructuredDetailParser.IsHook(Action);

    /// <summary>
    /// The Target cell. A hook call's row says only the phase (<c>preToolUse</c>), so the cell says who made the call too: <c>claudecode · preToolUse</c>
    /// (the connector from the details, else the row's own). Every other row shows its target.
    /// </summary>
    public string TargetText => _targetText ??= IsHook ? StructuredDetailParser.HookTarget(Target, Details, IsPlatform ? null : Connector) : Target;

    /// <summary>
    /// The Details cell. A hook call's details are a wall of <c>key=value</c> that cut off after <c>connector=claudecod</c>, so the cell reads the decision,
    /// the severity when it says something and how long it took: <c>allow · 320ms</c>. Every other row shows <see cref="Summary"/>.
    /// </summary>
    public string DetailsText => _detailsText ??= IsHook && StructuredDetailParser.HookSummary(Details) is { Length: > 0 } hook ? hook : Summary;

    /// <summary>The inspector's title: <c>claudecode preToolUse</c> for a hook call (every one would otherwise be titled <c>connector-hook</c>), the action for any other row.</summary>
    public string Title => _title ??= (IsHook ? StructuredDetailParser.HookTitle(Target, Details, IsPlatform ? null : Connector) : null) ?? Action;

    private string? _targetText;
    private string? _detailsText;
    private string? _title;

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

    /// <summary>The sort key (Unix nanoseconds); the centre of the "same target" correlation window. 0 when the timestamp is unreadable.</summary>
    internal long TimestampNanos { get; }

    /// <summary>"Same target" has something to match on.</summary>
    public bool HasTarget => Target.Length > 0;

    /// <summary>"Same run" has something to match on.</summary>
    public bool HasRun => RunId.Length > 0;

    /// <summary>
    /// The <c>key=value</c> pairs of <see cref="Details"/> (<c>&lt;redacted len= sha=&gt;</c> placeholders read as "redacted · 29 bytes ·
    /// sha:..."), or, when the details are prose, only the useful metadata keys found in it. Empty when neither applies. Parsed
    /// on first read: the inspector is the only reader.
    /// </summary>
    public IReadOnlyList<DetailPair> DetailPairs => _detailPairs.Value;

    /// <summary>The inspector shows the "Parsed details" section.</summary>
    public bool HasDetailPairs => DetailPairs.Count > 0;

    private static IReadOnlyList<DetailPair> ParseDetailPairs(string details)
    {
        if (details.Length == 0)
        {
            return Array.Empty<DetailPair>();
        }

        // The TUI's reading of a record (CUST-261): its key order and labels, a redaction as its size and digest, yes / no flags, and the two fields that
        // are noise on every passing hook call (severity=NONE, would_block=false while observing) left out. Prose gives only its known metadata keys.
        var rows = StructuredDetailParser.InspectorRows(details);
        return rows.Count > 0 ? rows : StructuredDetailParser.SafeMetadataPairs(details);
    }

    /// <summary>
    /// Pretty-printed <c>structured_json</c>; the raw text when it will not parse. Computed on first
    /// read and kept - the detail pane's binding is the only reader - so a row nobody selects
    /// never pays for the parse and the indented re-serialize.
    /// </summary>
    public string StructuredJson => _structuredJson.Value;

    /// <summary>True once <see cref="StructuredJson"/> has been read (and so pretty-printed).</summary>
    internal bool IsStructuredJsonMaterialized => _structuredJson.IsValueCreated;

    public IReadOnlyList<AuditDetailField> Fields { get; }

    /// <summary>The one-line text of the row: its details; when those are too large to display, the reason (so the row says why it has none); otherwise the event name.</summary>
    public string Summary => Details.Length > 0 ? Details : IsOversized ? OversizedNotice : EventName;

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
        var parts = new List<string> { $"{Severity} {Action}", DetailsText, $"connector {Connector}", TimestampText };
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
        Add(
            "details",
            source.Details ?? (source.Oversized.FirstOrDefault(static o => string.Equals(o.Column, "details", StringComparison.Ordinal)) is { } over
                ? UnavailableText(over)
                : null));

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

    internal static string KeyFor(AuditSeverity severity) => severity switch
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
