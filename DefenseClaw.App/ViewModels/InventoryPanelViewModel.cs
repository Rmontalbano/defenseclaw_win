using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Inventory;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One row of the components rollup for the latest full scan (the <c>ai_components_v</c> columns,
/// scoped to one scan — see <see cref="InventoryReader.GetLatestComponentsAsync"/>), plus its raw
/// column values.
/// </summary>
public sealed class InventoryComponentRow
{
    public required string Ecosystem { get; init; }

    public required string Name { get; init; }

    public string? Framework { get; init; }

    public string? Version { get; init; }

    public string? Vendor { get; init; }

    public int? InstallCount { get; init; }

    public DateTimeOffset? LastSeen { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    public double? IdentityScore { get; init; }

    public string? IdentityBand { get; init; }

    public double? PresenceScore { get; init; }

    public string? PresenceBand { get; init; }

    /// <summary>Every column the view returned, verbatim — feeds the detail pane.</summary>
    public required IReadOnlyDictionary<string, object?> Raw { get; init; }

    /// <summary>Non-null display fallback so the grid never shows a blank vendor cell.</summary>
    public string VendorDisplay => string.IsNullOrWhiteSpace(Vendor) ? "(unknown vendor)" : Vendor;

    public string EcosystemDisplay => string.IsNullOrWhiteSpace(Ecosystem) ? "(unknown)" : Ecosystem;

    public string InstallCountDisplay => InstallCount is { } count ? count.ToString(CultureInfo.InvariantCulture) : "—";

    public string IdentityDisplay => IdentityScore is { } score
        ? IdentityBand is { Length: > 0 } band ? $"{score:P0} ({band})" : $"{score:P0}"
        : "—";

    public string PresenceDisplay => PresenceScore is { } score
        ? PresenceBand is { Length: > 0 } band ? $"{score:P0} ({band})" : $"{score:P0}"
        : "—";

    public string LastSeenDisplay => LastSeen is { } seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>What a screen reader announces for the row (the default would be the type name).</summary>
    public override string ToString() =>
        $"{Name}, {VendorDisplay}, {EcosystemDisplay}" +
        (string.IsNullOrWhiteSpace(Version) ? string.Empty : $", version {Version}") +
        $", {InstallCountDisplay} install{(InstallCount == 1 ? string.Empty : "s")}";
}

/// <summary>One row of a raw detail/key-value listing (component detail pane, table browser row inspector).</summary>
public sealed record InventoryKeyValueRow(string Key, string Value)
{
    public override string ToString() => $"{Key}: {Value}";
}

/// <summary>A discovered table or view, with its row count, for the table-browser expander.</summary>
public sealed partial class InventoryTableSummary : ObservableObject
{
    public InventoryTableSummary(string name, bool isView)
    {
        Name = name;
        IsView = isView;
    }

    public string Name { get; }

    public bool IsView { get; }

    public string Kind => IsView ? "view" : "table";

    /// <summary>
    /// <c>null</c> until the count has finished (the row shows "…"). A view, and a database
    /// error, both end as <see cref="InventoryRowCount.NotCounted"/> ("n/a") so a count that will
    /// never arrive is not indistinguishable from one that has not arrived yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountDisplay))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private InventoryRowCount? _rowCount;

    /// <summary><c>1,234</c>, <c>~4,907,787</c> (an estimate from the rowid span), <c>250,000+</c>, <c>n/a</c>, or "…" while pending.</summary>
    public string RowCountDisplay => RowCount is { } count ? count.ToDisplayString() : "…";

    /// <summary>The line under the table picker.</summary>
    public string Summary => IsView
        ? "view · row count not computed (a view is evaluated on every read) · first 200 rows once loaded"
        : $"table · {RowCountDisplay} rows (first 200 shown)";

    public override string ToString() => $"{Kind} {Name}, {RowCountDisplay} rows";
}

/// <summary>
/// View-model for the Inventory panel: the AI components/SDK rollup from
/// <c>inventory.db</c>, with search, vendor/ecosystem grouping, a raw detail pane, and a
/// generic table browser for every other table the reader discovers.
/// <para>
/// <c>inventory.db</c> has no published schema contract (see
/// <see cref="DefenseClaw.Core.Inventory.InventoryReader"/>), so the rollup is the happy path and
/// the panel falls back to the generic browser — which works against any table — when the
/// database lacks the layout the rollup is built from.
/// </para>
/// <para>
/// <b>The rollup is not <c>ai_components_v</c>.</b> That view aggregates every signal of every scan
/// ever recorded: on the live 4.5 GB database it takes 14-20 s and reports a component the latest
/// scan saw twice as <c>install_count</c> 102,128. The grid is fed by
/// <see cref="InventoryReader.GetLatestComponentsAsync"/> instead — the same SELECT for one scan
/// (~0.2 ms) — and <see cref="ScanNote"/> says which scan that was. The view stays reachable in the
/// table browser, but only after an explicit "load anyway" behind a warning.
/// </para>
/// <para>
/// <b>Threading.</b> Every <see cref="InventoryReader"/> query runs on the thread pool (see its
/// type documentation), so the awaits here return the dispatcher to WPF immediately: the table
/// counts (a capped scan of a multi-million-row table, seconds when the file is cold) and the
/// deliberate 15-20 s view load cost the UI nothing.
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel : PanelViewModelBase
{
    public const string GroupByVendor = "Vendor";
    public const string GroupByType = "Type";

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly List<InventoryComponentRow> _allComponents = new();

    private bool _loadRunning;
    private DateTimeOffset? _loadedAt;

    /// <summary>The load the table browser is currently waiting on; cancelled when the selection changes.</summary>
    private CancellationTokenSource? _browseCts;

    /// <summary>
    /// Bumped for every browse request. A load only publishes its rows or clears
    /// <see cref="IsTableLoading"/> while it is still the newest — the query cannot be interrupted
    /// mid-statement, so an earlier, slower table can finish after a later one and must not win.
    /// UI thread only.
    /// </summary>
    private int _browseSequence;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowComponentsGrid))]
    private bool _usingFallbackView;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _groupBy = GroupByVendor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoComponentSelected))]
    private InventoryComponentRow? _selectedComponent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTableSelected))]
    private InventoryTableSummary? _selectedTable;

    [ObservableProperty]
    private DataView? _browsedRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTableBrowserError))]
    private string? _tableBrowserError;

    [ObservableProperty]
    private bool _isTableLoading;

    /// <summary>Which scan the grid's numbers come from (time, source, result), or why there is none. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanNote))]
    private string? _scanNote;

    /// <summary>
    /// A warning about the selected table or view: today, that <c>ai_components_v</c> aggregates all
    /// history. Shown above the browser and kept while the view loads.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTableBrowserNote))]
    private string? _tableBrowserNote;

    /// <summary>A view is selected but not loaded: browsing one costs its whole definition, so it waits for a click.</summary>
    [ObservableProperty]
    private bool _isViewLoadPending;

    /// <summary>True once loading has failed with a message worth showing in a banner.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasScanNote => !string.IsNullOrEmpty(ScanNote);

    public bool HasTableBrowserNote => !string.IsNullOrEmpty(TableBrowserNote);

    /// <summary>Inverse of <see cref="UsingFallbackView"/> — whether the rollup grid should show.</summary>
    public bool ShowComponentsGrid => !UsingFallbackView;

    public bool NoComponentSelected => SelectedComponent is null;

    public bool NoTableSelected => SelectedTable is null;

    public bool HasTableBrowserError => !string.IsNullOrEmpty(TableBrowserError);

    public InventoryPanelViewModel(AppServices services)
        : base(services)
    {
        ComponentsView = CollectionViewSource.GetDefaultView(_allComponents);
        ComponentsView.Filter = FilterComponent;
        ApplyGrouping();
        Review = new DiscoverActionReview(services);
        BuildBomConnectors();
    }

    /// <summary>The shared confirm-and-run dialog (used by "Generate AI BOM").</summary>
    public DiscoverActionReview Review { get; }

    public override string Title => "Inventory";

    public override string Description =>
        "AI components and SDKs from the latest full scan in inventory.db, with search and grouping.";

    /// <summary>Filtered, grouped view over the loaded rollup; the grid binds to this.</summary>
    public ICollectionView ComponentsView { get; }

    public ObservableCollection<InventoryTableSummary> Tables { get; } = new();

    public ObservableCollection<InventoryKeyValueRow> DetailRows { get; } = new();

    public IReadOnlyList<string> GroupByOptions { get; } = new[] { GroupByVendor, GroupByType };

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(CancellationToken.None).ConfigureAwait(true);

    /// <summary>
    /// One-shot catch-up when the panel returns to the screen after its data has gone stale (no timer, no
    /// CLI: it re-reads inventory.db). The first visit is covered by <see cref="InitializeAsync"/>.
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
            await LoadAsync(CancellationToken.None, refreshTables: false).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the message is shown in the banner.
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Inventory catch-up failed: {ex}");
            ErrorMessage = $"Could not refresh the inventory: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    partial void OnSearchTextChanged(string value)
    {
        ComponentsView.Refresh();
        UpdateEmptyState();
    }

    partial void OnGroupByChanged(string value)
    {
        ApplyGrouping();
        ComponentsView.Refresh();
    }

    partial void OnSelectedComponentChanged(InventoryComponentRow? value)
    {
        DetailRows.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var (key, raw) in value.Raw.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            DetailRows.Add(new InventoryKeyValueRow(key, FormatRawValue(raw)));
        }
    }

    /// <summary>
    /// A new table (or none) was picked: whatever the browser was loading for the previous pick is
    /// stale. Cancels it and bumps <see cref="_browseSequence"/> so that, even though the query
    /// itself cannot be stopped once SQLite is inside a statement, its result is dropped instead of
    /// overwriting <see cref="BrowsedRows"/> or clearing <see cref="IsTableLoading"/> under the
    /// newer pick. A view is not loaded here — see <see cref="LoadSelectedViewAsync"/>.
    /// </summary>
    partial void OnSelectedTableChanged(InventoryTableSummary? value)
    {
        var sequence = ++_browseSequence;
        CancelBrowse();

        BrowsedRows = null;
        TableBrowserError = null;
        TableBrowserNote = null;
        IsViewLoadPending = false;
        IsTableLoading = false;

        if (value is null)
        {
            return;
        }

        if (value.IsView)
        {
            TableBrowserNote = DescribeViewCost(value.Name);
            IsViewLoadPending = true;
            return;
        }

        _ = LoadBrowsedTableAsync(value.Name, sequence);
    }

    /// <summary>
    /// The explicit "load anyway" for a selected view. Browsing <c>ai_components_v</c> aggregates
    /// every scan ever recorded (14-20 s on the live 4.5 GB database), so it never happens just
    /// because the picker moved over it.
    /// </summary>
    [RelayCommand]
    private async Task LoadSelectedViewAsync()
    {
        var view = SelectedTable;
        if (view is null || !view.IsView)
        {
            return;
        }

        IsViewLoadPending = false;
        var sequence = ++_browseSequence;
        CancelBrowse();
        await LoadBrowsedTableAsync(view.Name, sequence).ConfigureAwait(true);
    }

    private static string DescribeViewCost(string name) =>
        string.Equals(name, "ai_components_v", StringComparison.OrdinalIgnoreCase)
            ? "Aggregates all history. ai_components_v adds up every signal of every scan ever recorded, so " +
              "it takes 15-20 s on a large inventory.db (measured on a 4.5 GB file) and its install counts are " +
              "inflated history totals. The components grid above already shows the latest scan instead — load " +
              "this view only if you specifically want the all-history rollup."
            : "A view is computed from its definition on every read, and this inventory.db can be many " +
              "gigabytes, so this may take a while. Load it only if you need it.";

    private void CancelBrowse()
    {
        var pending = _browseCts;
        _browseCts = null;
        pending?.Cancel();
    }

    private async Task LoadAsync(CancellationToken cancellationToken, bool refreshTables = true)
    {
        // One read at a time: Initialize, the activation catch-up, Refresh and a finished BOM run can all ask.
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            if (!Services.Inventory.Exists)
            {
                // Normal until AI discovery has run once, so this is an empty state, not a warning.
                DatabaseMissing = true;
                _allComponents.Clear();
                Tables.Clear();
                ScanNote = null;
                UsingFallbackView = false;
                ComponentsView.Refresh();
                StatusMessage = $"No inventory.db yet · as of {DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}";
                _loadedAt = DateTimeOffset.Now;
                HasLoaded = true;
                UpdateEmptyState();
                return;
            }

            DatabaseMissing = false;

            // The table list and its counts are the expensive part (a capped COUNT on a multi-million-row
            // table); the activation catch-up only refreshes the rollup, and Refresh does everything.
            IReadOnlyList<InventoryTable>? tables = null;
            if (refreshTables || Tables.Count == 0)
            {
                tables = await Services.Inventory.ListTablesAsync(cancellationToken).ConfigureAwait(true);

                Tables.Clear();
                foreach (var table in tables)
                {
                    Tables.Add(new InventoryTableSummary(table.Name, table.IsView));
                }
            }

            // The rollup for the latest full scan — not a browse of ai_components_v, which is 14-20 s
            // and history-inflated. null = this database lacks the layout it is built from.
            var rollup = await Services.Inventory.GetLatestComponentsAsync(cancellationToken).ConfigureAwait(true);

            _allComponents.Clear();
            if (rollup is null)
            {
                UsingFallbackView = true;
                ScanNote = null;
            }
            else
            {
                UsingFallbackView = false;
                foreach (var row in rollup.Rows.Rows)
                {
                    _allComponents.Add(MapComponentRow(row));
                }

                ScanNote = DescribeScan(rollup.Scan);
            }

            ComponentsView.Refresh();
            _loadedAt = DateTimeOffset.Now;
            var asOf = _loadedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusMessage = rollup is null
                ? $"Component rollup unavailable · as of {asOf}"
                : $"{BuildStatusMessage(rollup.Scan)} · as of {asOf}";
            HasLoaded = true;
            UpdateEmptyState();

            // Fire off row counts for the table browser without blocking the main load —
            // a capped COUNT on a multi-million-row table (ai_signals on this box) is not free.
            if (tables is not null)
            {
                _ = LoadTableCountsAsync(tables.Select(t => t.Name).ToArray(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not read inventory.db: {ex.Message}";
            HasLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shell went away mid-load; there is nobody to tell.
        }
        finally
        {
            IsLoading = false;
            _loadRunning = false;
        }
    }

    /// <summary>
    /// Fills each table's row count as it arrives. A table above the exact-count cap shows an
    /// estimate labelled as one (<c>~4,907,787</c>), a view shows <c>n/a</c> without being queried,
    /// and a count that fails shows <c>n/a</c> too — the table itself still browses.
    /// </summary>
    private async Task LoadTableCountsAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken)
    {
        foreach (var name in tableNames)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            InventoryRowCount count;
            try
            {
                count = await Services.Inventory.CountRowsAsync(name, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
            {
                count = InventoryRowCount.NotCounted;
            }

            var summary = Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (summary is not null)
            {
                summary.RowCount = count;
            }
        }
    }

    /// <summary>
    /// Loads the first 200 rows of <paramref name="tableName"/> into the browser grid for the browse
    /// request numbered <paramref name="sequence"/>. If a newer request has been issued by the time the
    /// query returns, the rows and every state change are dropped.
    /// </summary>
    private async Task LoadBrowsedTableAsync(string tableName, int sequence)
    {
        var cts = new CancellationTokenSource();
        _browseCts = cts;
        var token = cts.Token;

        IsTableLoading = true;
        TableBrowserError = null;

        try
        {
            var rows = await Services.Inventory.BrowseAsync(tableName, limit: 200, cancellationToken: token)
                .ConfigureAwait(true);

            if (sequence != _browseSequence)
            {
                return;
            }

            var table = new DataTable(tableName);
            foreach (var column in rows.Columns)
            {
                table.Columns.Add(column, typeof(string));
            }

            foreach (var row in rows.Rows)
            {
                var dataRow = table.NewRow();
                foreach (var column in rows.Columns)
                {
                    dataRow[column] = FormatRawValue(row[column]);
                }

                table.Rows.Add(dataRow);
            }

            BrowsedRows = table.DefaultView;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; that request owns the state now.
        }
        catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
        {
            if (sequence == _browseSequence)
            {
                TableBrowserError = $"Could not browse '{tableName}': {ex.Message}";
                BrowsedRows = null;
            }
        }
        finally
        {
            if (sequence == _browseSequence)
            {
                IsTableLoading = false;
            }

            if (ReferenceEquals(_browseCts, cts))
            {
                _browseCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// One line on where the grid's numbers come from. The scan is named (time, source, result) because
    /// the alternative — the all-history view — gives very different, inflated, install counts.
    /// </summary>
    private static string DescribeScan(InventoryScan? scan)
    {
        if (scan is null)
        {
            return "inventory.db has not recorded a scan yet, so there is nothing to roll up.";
        }

        var stamp = scan.ScannedAt is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : scan.ScannedAtRaw;
        var what = $"{stamp} ({scan.Source}, {scan.Result})";

        return scan.IsFullScan
            ? $"Showing the latest full scan: {what}. Install counts are what that scan found — the ai_components_v " +
              "view in the table browser adds up every scan ever recorded, which inflates them."
            : $"No completed scheduled or startup scan is recorded, so this is the newest scan of any kind: {what}. " +
              "Install counts are what that scan found, not history totals.";
    }

    private bool FilterComponent(object obj)
    {
        if (obj is not InventoryComponentRow row)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return Contains(row.Name) || Contains(row.Vendor) || Contains(row.Framework) || Contains(row.Ecosystem);

        bool Contains(string? value) =>
            value is not null && value.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The column the operator sorted the table by (a property of <see cref="InventoryComponentRow"/>); null: by name within the groups.</summary>
    private string? _sortMember;

    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>
    /// Sorts the rows <i>within</i> their groups by <paramref name="member"/> (a null member puts them back in name order). The
    /// grid's own header sort would replace the view's sort descriptions - the one that keeps the groups in alphabetical order
    /// among them - so the panel routes a header click here instead.
    /// </summary>
    public void SortBy(string? member, ListSortDirection direction)
    {
        _sortMember = string.IsNullOrEmpty(member) ? null : member;
        _sortDirection = direction;
        ApplySort();
    }

    private void ApplyGrouping()
    {
        ComponentsView.GroupDescriptions.Clear();
        var property = GroupBy == GroupByType
            ? nameof(InventoryComponentRow.EcosystemDisplay)
            : nameof(InventoryComponentRow.VendorDisplay);
        ComponentsView.GroupDescriptions.Add(new PropertyGroupDescription(property));
        ApplySort();
    }

    private void ApplySort()
    {
        var property = GroupBy == GroupByType
            ? nameof(InventoryComponentRow.EcosystemDisplay)
            : nameof(InventoryComponentRow.VendorDisplay);

        var name = nameof(InventoryComponentRow.Name);
        var groupDirection = _sortMember == property ? _sortDirection : ListSortDirection.Ascending;

        using (ComponentsView.DeferRefresh())
        {
            ComponentsView.SortDescriptions.Clear();
            ComponentsView.SortDescriptions.Add(new SortDescription(property, groupDirection));
            if (_sortMember is { } member && member != property && member != name)
            {
                ComponentsView.SortDescriptions.Add(new SortDescription(member, _sortDirection));
                ComponentsView.SortDescriptions.Add(new SortDescription(name, ListSortDirection.Ascending));
            }
            else
            {
                ComponentsView.SortDescriptions.Add(new SortDescription(name, _sortMember == name ? _sortDirection : ListSortDirection.Ascending));
            }
        }
    }

    private string BuildStatusMessage(InventoryScan? scan)
    {
        if (_allComponents.Count == 0)
        {
            return scan is null ? "No scans recorded yet." : "The latest scan found no components.";
        }

        var vendors = _allComponents.Select(c => c.VendorDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{_allComponents.Count} component{(_allComponents.Count == 1 ? string.Empty : "s")} across " +
               $"{vendors} vendor{(vendors == 1 ? string.Empty : "s")}.";
    }

    private static InventoryComponentRow MapComponentRow(IReadOnlyDictionary<string, object?> raw)
    {
        return new InventoryComponentRow
        {
            Ecosystem = AsString(raw, "ecosystem") ?? string.Empty,
            Name = AsString(raw, "name") ?? string.Empty,
            Framework = AsString(raw, "framework"),
            Version = AsString(raw, "version"),
            Vendor = AsString(raw, "vendor"),
            InstallCount = AsInt(raw, "install_count"),
            LastSeen = AsTimestamp(raw, "last_seen"),
            LastActiveAt = AsTimestamp(raw, "last_active_at"),
            IdentityScore = AsDouble(raw, "identity_score"),
            IdentityBand = AsString(raw, "identity_band"),
            PresenceScore = AsDouble(raw, "presence_score"),
            PresenceBand = AsString(raw, "presence_band"),
            Raw = raw,
        };
    }

    private static string? AsString(IReadOnlyDictionary<string, object?> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static int? AsInt(IReadOnlyDictionary<string, object?> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : null;

    private static double? AsDouble(IReadOnlyDictionary<string, object?> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is not null
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : null;

    private static DateTimeOffset? AsTimestamp(IReadOnlyDictionary<string, object?> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is not null &&
        TryParseFlexibleTimestamp(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// inventory.db stores timestamps as Go's default <c>time.Time</c> string form
    /// (e.g. <c>2026-07-28 21:33:39.2587908 +0000 UTC</c>), which <see cref="DateTimeOffset"/>
    /// cannot parse directly because of the trailing zone-name token. Strips that token and
    /// retries before giving up.
    /// </summary>
    internal static bool TryParseFlexibleTimestamp(string? raw, out DateTimeOffset result)
    {
        result = default;

        // Only a string that starts like a date (yyyy-MM-dd) is a candidate: DateTimeOffset.TryParse
        // alone turns version-like cells such as "1-0" or "2.1" into dates in the table browser.
        if (string.IsNullOrWhiteSpace(raw) || !StartsWithIsoDate(raw.AsSpan().TrimStart()))
        {
            return false;
        }

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles, out result))
        {
            return true;
        }

        var trimmed = raw.Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0 && trimmed[(lastSpace + 1)..].All(char.IsLetter))
        {
            return DateTimeOffset.TryParse(trimmed[..lastSpace], CultureInfo.InvariantCulture, styles, out result);
        }

        return false;
    }

    private static bool StartsWithIsoDate(ReadOnlySpan<char> s) =>
        s.Length >= 10
        && char.IsAsciiDigit(s[0]) && char.IsAsciiDigit(s[1]) && char.IsAsciiDigit(s[2]) && char.IsAsciiDigit(s[3])
        && s[4] == '-' && char.IsAsciiDigit(s[5]) && char.IsAsciiDigit(s[6])
        && s[7] == '-' && char.IsAsciiDigit(s[8]) && char.IsAsciiDigit(s[9]);

    private static string FormatRawValue(object? value) => value switch
    {
        null or DBNull => "—",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        string s when TryParseFlexibleTimestamp(s, out var dto) =>
            dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—",
    };
}
