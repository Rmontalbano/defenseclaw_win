using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>One row of the <c>ai_components_v</c> rollup, plus its raw column values.</summary>
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
}

/// <summary>One row of a raw detail/key-value listing (component detail pane, table browser row inspector).</summary>
public sealed record InventoryKeyValueRow(string Key, string Value);

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

    [ObservableProperty]
    private int? _rowCount;

    public string RowCountDisplay => RowCount is { } count ? count.ToString(CultureInfo.InvariantCulture) : "…";
}

/// <summary>
/// View-model for the Inventory panel: the AI components/SDK rollup from
/// <c>inventory.db</c>, with search, vendor/ecosystem grouping, a raw detail pane, and a
/// generic table browser for every other table the reader discovers.
/// <para>
/// <c>inventory.db</c> has no published schema contract (see
/// <see cref="DefenseClaw.Core.Inventory.InventoryReader"/>), so this treats
/// <c>ai_components_v</c> as the happy path and falls back to the generic browser — which
/// works against any table — when that view is absent.
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel : PanelViewModelBase
{
    public const string GroupByVendor = "Vendor";
    public const string GroupByType = "Type";

    private readonly List<InventoryComponentRow> _allComponents = new();

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

    /// <summary>True once loading has failed with a message worth showing in a banner.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

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
    }

    public override string Title => "Inventory";

    public override string Description =>
        "AI components and SDK rollup discovered in inventory.db, with search and grouping.";

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

    partial void OnSearchTextChanged(string value) => ComponentsView.Refresh();

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

    partial void OnSelectedTableChanged(InventoryTableSummary? value)
    {
        if (value is null)
        {
            BrowsedRows = null;
            return;
        }

        _ = LoadBrowsedTableAsync(value.Name, CancellationToken.None);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            if (!Services.Inventory.Exists)
            {
                ErrorMessage = $"inventory.db not found at {Services.Inventory.DatabasePath}. " +
                    "It is created the first time AI discovery runs — nothing to show yet.";
                _allComponents.Clear();
                Tables.Clear();
                ComponentsView.Refresh();
                return;
            }

            var tables = await Services.Inventory.ListTablesAsync(cancellationToken).ConfigureAwait(true);

            Tables.Clear();
            foreach (var table in tables)
            {
                Tables.Add(new InventoryTableSummary(table.Name, table.IsView));
            }

            var componentsTable = tables.FirstOrDefault(t =>
                string.Equals(t.Name, "ai_components_v", StringComparison.OrdinalIgnoreCase));

            _allComponents.Clear();
            if (componentsTable is not null)
            {
                UsingFallbackView = false;
                var rows = await Services.Inventory.BrowseAsync(
                    componentsTable.Name, limit: 5000, cancellationToken: cancellationToken).ConfigureAwait(true);

                foreach (var row in rows.Rows)
                {
                    _allComponents.Add(MapComponentRow(row));
                }
            }
            else
            {
                UsingFallbackView = true;
            }

            ComponentsView.Refresh();
            StatusMessage = BuildStatusMessage();

            // Fire off row counts for the table browser without blocking the main load —
            // COUNT(*) on a multi-million-row table (ai_signals on this box) is not free.
            _ = LoadTableCountsAsync(tables.Select(t => t.Name).ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not read inventory.db: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadTableCountsAsync(IReadOnlyList<string> tableNames, CancellationToken cancellationToken)
    {
        foreach (var name in tableNames)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var count = await Services.Inventory.CountAsync(name, cancellationToken).ConfigureAwait(true);
                var summary = Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (summary is not null)
                {
                    summary.RowCount = count;
                }
            }
            catch (SqliteException)
            {
                // Leave the row count as "…" — the table itself still browses fine.
            }
        }
    }

    private async Task LoadBrowsedTableAsync(string tableName, CancellationToken cancellationToken)
    {
        IsTableLoading = true;
        TableBrowserError = null;

        try
        {
            var rows = await Services.Inventory.BrowseAsync(tableName, limit: 200, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

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
        catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
        {
            TableBrowserError = $"Could not browse '{tableName}': {ex.Message}";
            BrowsedRows = null;
        }
        finally
        {
            IsTableLoading = false;
        }
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

    private void ApplyGrouping()
    {
        ComponentsView.GroupDescriptions.Clear();
        var property = GroupBy == GroupByType
            ? nameof(InventoryComponentRow.EcosystemDisplay)
            : nameof(InventoryComponentRow.VendorDisplay);
        ComponentsView.GroupDescriptions.Add(new PropertyGroupDescription(property));

        ComponentsView.SortDescriptions.Clear();
        ComponentsView.SortDescriptions.Add(new SortDescription(property, ListSortDirection.Ascending));
        ComponentsView.SortDescriptions.Add(new SortDescription(nameof(InventoryComponentRow.Name), ListSortDirection.Ascending));
    }

    private string BuildStatusMessage()
    {
        var vendors = _allComponents.Select(c => c.VendorDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return _allComponents.Count == 0
            ? "No components recorded yet."
            : $"{_allComponents.Count} component{(_allComponents.Count == 1 ? string.Empty : "s")} across " +
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
    private static bool TryParseFlexibleTimestamp(string? raw, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
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

    private static string FormatRawValue(object? value) => value switch
    {
        null or DBNull => "—",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        string s when TryParseFlexibleTimestamp(s, out var dto) =>
            dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—",
    };
}
