using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>How much of the grid's width a column asks for (the grid shares its width by these, each with a floor).</summary>
public enum InventoryBomColumnSize
{
    /// <summary>A flag or a count: Enabled, Findings, Files.</summary>
    Narrow,

    /// <summary>A word or a short value: a verdict, a version, a connector, a model id.</summary>
    Medium,

    /// <summary>A name or a path: an id, a source, a workspace, a command.</summary>
    Wide,

    /// <summary>The one column of a two-column table that takes what the other leaves.</summary>
    Fill,
}

/// <summary>One column of the browser's grid. A verdict column draws its cell as a state pill.</summary>
public sealed record InventoryBomColumn(string Header, InventoryBomColumnSize Size, bool IsVerdict = false);

/// <summary>
/// One row of the browser's grid: a skill, plugin, MCP server, agent, tool, model provider or memory store (<see cref="Entity"/>), or a
/// line of the Summary tab (a metric and its value, no entity). <see cref="Cells"/> are in the order of <see cref="InventoryBomBrowser.Columns"/>.
/// </summary>
public sealed class InventoryBomRowItem
{
    private readonly string _haystack;

    public InventoryBomRowItem(IReadOnlyList<string> cells, InventoryBomEntity? entity, string verdict = "")
    {
        Cells = cells;
        Entity = entity;
        Verdict = verdict;
        _haystack = string.Join('\n', cells) + (entity is null ? string.Empty : "\n" + entity.Id + "\n" + entity.Title);
    }

    public IReadOnlyList<string> Cells { get; }

    /// <summary>The inventory row this is, or null for a Summary line.</summary>
    public InventoryBomEntity? Entity { get; }

    /// <summary>The policy verdict the Verdict column draws as a pill; empty for a row with none.</summary>
    public string Verdict { get; }

    public bool HasVerdict => Verdict.Length > 0;

    /// <summary>The pill's tone key: blocked and rejected are bad, a warning warns, clean and allowed are fine, anything else (unscanned, discovery-only) is quiet.</summary>
    public string VerdictTone => Verdict switch
    {
        "blocked" or "rejected" => "Bad",
        "warning" => "Warn",
        "clean" or "allowed" => "Ok",
        _ => "Neutral",
    };

    /// <summary>True when the search text (already trimmed) is in any cell, the id or the title.</summary>
    public bool Matches(string search) => _haystack.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a screen reader announces for the row (the default would be the type name).</summary>
    public override string ToString() => string.Join(", ", Cells.Where(c => c.Length > 0));
}

/// <summary>One category in the scope strip (the TUI's <c>InventoryScopeChip</c>): on means the next scan covers it.</summary>
public sealed partial class InventoryBomScopeChip : ObservableObject
{
    private readonly Action<InventoryBomScopeChip> _changed;

    public InventoryBomScopeChip(InventoryBomKind kind, Action<InventoryBomScopeChip> changed)
    {
        Kind = kind;
        _changed = changed;
    }

    public InventoryBomKind Kind { get; }

    /// <summary>The <c>--only</c> word: <c>skills</c>, <c>mcp</c>, <c>models</c>.</summary>
    public string Label => Kind.OnlyName();

    /// <summary>What a screen reader says: "Scan skills".</summary>
    public string AutomationName => "Scan " + Kind.Plural();

    [ObservableProperty]
    private bool _isActive = true;

    partial void OnIsActiveChanged(bool value) => _changed(this);
}

/// <summary>
/// The Inventory page's AI bill-of-materials browser: <c>aibom scan --json</c> as the TUI's Inventory panel shows it
/// (<c>tui/panels/inventory.py</c>, <c>tui/services/inventory_state.py</c> in 0.8.10) - a Summary and one table per kind with a detail pane -
/// over an <see cref="InventoryBomSnapshot"/>. It holds no I/O: the panel's view-model runs the scan and hands the parsed snapshot to
/// <see cref="Load"/>.
/// <para>
/// <b>Tabs.</b> Summary, Skills, Plugins, MCPs, Agents, Models and Memory (the TUI's sub-tabs), with Tools between Agents and Models when some
/// connector lists a tool. Each tab's count is its rows under the shared connector scope; the status chips of Skills (eligible, warning, blocked)
/// and Plugins (loaded, disabled, blocked) narrow its list, as the TUI's 2 / 3 / 4 keys do. A Connector column leads every table when the scan
/// covered several connectors.
/// </para>
/// <para>
/// <b>Summary.</b> The TUI's <c>summary_table_rows</c>, label for label: version, generated, source, home, config, the counts, then errors, unsupported
/// capabilities and the policy verdict and scan coverage tallies when there are some. Under "All" with several connectors the counts and tallies
/// are the connectors' sums (the TUI merges only the counts) and the source names every connector; narrowed to one connector it is that connector's
/// own summary. The detail pane of the Summary tab is its coverage notes: the connectors that were skipped, the commands that failed, what a
/// connector cannot inventory, rows left out by the bound.
/// </para>
/// <para>
/// <b>Detail.</b> The pane shows the TUI's fields for the selected row in its order and labels, then "Also in this scan": the other members the row
/// carried, which is how a newer runtime's additions appear. A value the row did not have shows as "—".
/// </para>
/// <para>
/// <b>Scope chips</b> are the TUI's <c>--only</c> categories. Every category is on until some are turned off; <see cref="OnlyArgument"/> is empty for
/// "all" and otherwise the comma-separated words in tab order. A scan that was limited this way says so for the tabs it did not cover.
/// </para>
/// </summary>
public sealed partial class InventoryBomBrowser : ObservableObject
{
    public const string SummaryTab = "summary";

    private const string NoValue = "—";

    private readonly List<InventoryBomRowItem> _all = new();
    private readonly InventoryBomScopeChip[] _chips;
    private IReadOnlyList<InventoryBomRow> _connectorLines = Array.Empty<InventoryBomRow>();
    private IReadOnlyList<InventoryBomKind> _scanned = Array.Empty<InventoryBomKind>();
    private string? _scope;
    private bool _settingChips;
    private bool _rebuilding;

    public InventoryBomBrowser()
    {
        _chips = InventoryBomKinds.All.Select(kind => new InventoryBomScopeChip(kind, OnChipToggled)).ToArray();
        ScopeChips = new ObservableCollection<InventoryBomScopeChip>(_chips);
        Rebuild();
    }

    /// <summary>The scan being browsed; null until one has been loaded.</summary>
    public InventoryBomSnapshot? Snapshot { get; private set; }

    public bool HasSnapshot => Snapshot is not null;

    // ------------------------------------------------------------------ view state

    /// <summary>Which tab shows: <see cref="SummaryTab"/> or a kind's word (<c>skills</c>, <c>agents</c>, <c>models</c>, ...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummary), nameof(IsSkillsTab), nameof(IsPluginsTab), nameof(HasStatusFilter), nameof(HasFilterRow))]
    private string _activeTab = SummaryTab;

    /// <summary>The Skills / Plugins status chip: <c>all</c>, <c>eligible</c>, <c>warning</c>, <c>blocked</c>, <c>loaded</c> or <c>disabled</c>.</summary>
    [ObservableProperty]
    private string _statusFilter = "all";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasDetail), nameof(ShowDetailEmpty))]
    private InventoryBomRowItem? _selectedRow;

    [ObservableProperty]
    private IReadOnlyList<InventoryBomColumn> _columns = Array.Empty<InventoryBomColumn>();

    [ObservableProperty]
    private string _detailHeading = "Details";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmpty))]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyDetail))]
    private string _emptyDetail = string.Empty;

    [ObservableProperty]
    private string _caption = "No AI BOM yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterRow))]
    private string _resultCaption = string.Empty;

    [ObservableProperty]
    private bool _showConnectorColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGenerate))]
    private bool _showEmpty = true;

    [ObservableProperty]
    private string _detailEmptyText = "Select a row to see its fields.";

    [ObservableProperty]
    private bool _hasMore;

    public bool IsSummary => ActiveTab == SummaryTab;

    public bool IsSkillsTab => ActiveTab == InventoryBomKind.Skills.OnlyName();

    public bool IsPluginsTab => ActiveTab == InventoryBomKind.Plugins.OnlyName();

    /// <summary>The tab has status chips (the TUI's Skills and Plugins).</summary>
    public bool HasStatusFilter => IsSkillsTab || IsPluginsTab;

    /// <summary>The row under the tabs has something in it: the status chips of Skills and Plugins, or the count of what is listed.</summary>
    public bool HasFilterRow => HasStatusFilter || ResultCaption.Length > 0;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The detail pane has something to list: a selected row's fields, or the Summary tab's notes.</summary>
    public bool HasDetail => IsSummary ? HasSnapshot : HasSelection;

    public bool ShowDetailEmpty => !HasDetail;

    public bool HasEmpty => EmptyTitle.Length > 0;

    public bool HasEmptyDetail => EmptyDetail.Length > 0;

    /// <summary>The empty state offers "Generate AI BOM…": there is no scan on screen to look at.</summary>
    public bool ShowGenerate => ShowEmpty && !HasSnapshot;

    /// <summary>The rows of the active tab after the connector scope, the status chip and the search.</summary>
    public BatchObservableCollection<InventoryBomRowItem> Rows { get; } = new();

    /// <summary>The detail pane's fields: the TUI's for the selected row; on the Summary tab, the coverage notes.</summary>
    public ObservableCollection<InventoryKeyValueRow> DetailFields { get; } = new();

    /// <summary>The selected row's other members (see the type's remarks), empty when it carried none.</summary>
    public ObservableCollection<InventoryKeyValueRow> DetailMore { get; } = new();

    // ------------------------------------------------------------------ tab counts (null: not shown)

    public int? SkillsCount => CountOf(InventoryBomKind.Skills);

    public int? PluginsCount => CountOf(InventoryBomKind.Plugins);

    public int? McpCount => CountOf(InventoryBomKind.Mcp);

    public int? AgentsCount => CountOf(InventoryBomKind.Agents);

    public int? ToolsCount => CountOf(InventoryBomKind.Tools);

    public int? ModelsCount => CountOf(InventoryBomKind.Models);

    public int? MemoryCount => CountOf(InventoryBomKind.Memory);

    /// <summary>The Tools tab (not one of 0.8.10's TUI) is offered when some connector listed a tool.</summary>
    public bool ShowToolsTab => Snapshot is { } snapshot && snapshot.Connectors.Any(c => c.Entities(InventoryBomKind.Tools).Count > 0);

    private int? CountOf(InventoryBomKind kind)
    {
        if (Snapshot is not { } snapshot)
        {
            return null;
        }

        var connectors = snapshot.Connectors.Where(InScope).ToList();
        return connectors.Count == 0 || !Collected(connectors, kind) ? null : connectors.Sum(c => c.Entities(kind).Count);
    }

    // ------------------------------------------------------------------ the --only scope

    /// <summary>The categories of the next scan, one chip each (the TUI's <c>scope_state</c>).</summary>
    public ObservableCollection<InventoryBomScopeChip> ScopeChips { get; }

    /// <summary>The value of <c>aibom scan --only</c> for the chips as set: empty when every category is on (the CLI's default).</summary>
    public string OnlyArgument => AllChipsActive
        ? string.Empty
        : string.Join(',', _chips.Where(c => c.IsActive).Select(c => c.Kind.OnlyName()));

    /// <summary>The TUI's label for the scope: "Scope (all)", "Scope (fast)" or "Scope".</summary>
    public string ScopeLabel => AllChipsActive
        ? "Scope (all)"
        : IsFastScope ? "Scope (fast)" : "Scope";

    /// <summary>The categories the next scan covers, in tab order.</summary>
    public IReadOnlyList<InventoryBomKind> ScopeKinds => AllChipsActive
        ? InventoryBomKinds.All
        : _chips.Where(c => c.IsActive).Select(c => c.Kind).ToList();

    private bool AllChipsActive => _chips.All(c => c.IsActive);

    private bool IsFastScope
    {
        get
        {
            var fast = InventoryBomKinds.FastScan;
            return _chips.All(c => c.IsActive == fast.Contains(c.Kind));
        }
    }

    /// <summary>The TUI's "Fast" button: skills, plugins and MCP servers only.</summary>
    [RelayCommand]
    private void ShowFastScope() => SetCategories(InventoryBomKinds.FastScan);

    /// <summary>The TUI's "All scope" button: every category.</summary>
    [RelayCommand]
    private void ShowAllScope() => SetCategories(InventoryBomKinds.All);

    /// <summary>Turns exactly <paramref name="kinds"/> on (and the rest off).</summary>
    public void SetCategories(IReadOnlyList<InventoryBomKind> kinds)
    {
        _settingChips = true;
        try
        {
            foreach (var chip in _chips)
            {
                chip.IsActive = kinds.Contains(chip.Kind);
            }
        }
        finally
        {
            _settingChips = false;
        }

        RaiseScopeChanged();
    }

    private void OnChipToggled(InventoryBomScopeChip chip)
    {
        if (_settingChips)
        {
            return;
        }

        // A scan of nothing is not a scan: turning off the last category goes back to all of them (the TUI's empty scope).
        if (_chips.All(c => !c.IsActive))
        {
            SetCategories(InventoryBomKinds.All);
            return;
        }

        RaiseScopeChanged();
    }

    private void RaiseScopeChanged()
    {
        OnPropertyChanged(nameof(OnlyArgument));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(ScopeKinds));
    }

    // ------------------------------------------------------------------ loading and re-projecting

    /// <summary>Shows a scan. The tab goes back to Summary and the status chip, search and selection are cleared; the scope chips stay as they were.</summary>
    /// <param name="snapshot">What the scan printed.</param>
    /// <param name="scanned">The categories the scan was limited to with <c>--only</c>; empty for all of them.</param>
    public void Load(InventoryBomSnapshot snapshot, IReadOnlyList<InventoryBomKind> scanned)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Snapshot = snapshot;
        _scanned = scanned ?? Array.Empty<InventoryBomKind>();
        _connectorLines = Array.Empty<InventoryBomRow>();
        _rebuilding = true;
        try
        {
            ActiveTab = SummaryTab;
            StatusFilter = "all";
            SelectedRow = null;
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ShowGenerate));
        Rebuild();
    }

    /// <summary>Forgets the scan (a new one is starting, or it failed).</summary>
    public void Clear()
    {
        Snapshot = null;
        _scanned = Array.Empty<InventoryBomKind>();
        _connectorLines = Array.Empty<InventoryBomRow>();
        _rebuilding = true;
        try
        {
            ActiveTab = SummaryTab;
            StatusFilter = "all";
            SelectedRow = null;
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ShowGenerate));
        Rebuild();
    }

    /// <summary>Narrows the browser to one connector (the shared connector scope), or to all of them for null.</summary>
    public void SetConnector(string? connector)
    {
        var normalized = string.IsNullOrWhiteSpace(connector) ? null : connector.Trim();
        if (string.Equals(_scope, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _scope = normalized;
        Rebuild();
    }

    /// <summary>The per-connector count lines of <c>aibom scan</c> (the Summary tab lists them under "All" when there are several).</summary>
    public void SetConnectorLines(IReadOnlyList<InventoryBomRow> lines)
    {
        _connectorLines = lines ?? Array.Empty<InventoryBomRow>();
        if (IsSummary && HasSnapshot)
        {
            Rebuild();
        }
    }

    partial void OnActiveTabChanged(string value)
    {
        if (_rebuilding)
        {
            return;
        }

        StatusFilter = "all";
        SelectedRow = null;
        Rebuild();
    }

    partial void OnStatusFilterChanged(string value)
    {
        if (!_rebuilding)
        {
            Rebuild();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_rebuilding)
        {
            Rebuild();
        }
    }

    partial void OnSelectedRowChanged(InventoryBomRowItem? value) => ShowDetail();

    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    private bool InScope(InventoryBomConnector connector) =>
        _scope is null || string.Equals(connector.Name.Trim(), _scope, StringComparison.OrdinalIgnoreCase);

    private InventoryBomKind? ActiveKind =>
        InventoryBomKinds.TryParse(ActiveTab, out var kind) && ActiveTab != SummaryTab ? kind : null;

    /// <summary>Whether a scan covered the kind: its summary does not say it was left out, and the scan was not limited to other categories.</summary>
    private bool Collected(IReadOnlyList<InventoryBomConnector> connectors, InventoryBomKind kind) =>
        (_scanned.Count == 0 || _scanned.Contains(kind)) && connectors.All(c => c.Count(kind) is not { Collected: false });

    private void Rebuild()
    {
        var selected = SelectedRow;
        _all.Clear();

        var snapshot = Snapshot;
        var connectors = snapshot is null ? new List<InventoryBomConnector>() : snapshot.Connectors.Where(InScope).ToList();
        ShowConnectorColumn = snapshot is { HasSeveralConnectors: true };

        string emptyTitle = string.Empty;
        string emptyDetail = string.Empty;
        string resultCaption = string.Empty;

        if (snapshot is null)
        {
            SetColumns(Array.Empty<InventoryBomColumn>());
            emptyTitle = "No AI BOM yet";
            emptyDetail = "Generate AI BOM… runs defenseclaw aibom scan --json and lists the skills, plugins, MCP servers, agents, tools, models and " +
                          "memory of your connectors with their policy verdicts. Every run records a scan event in audit.db, so it asks first.";
        }
        else if (connectors.Count == 0)
        {
            SetColumns(Array.Empty<InventoryBomColumn>());
            emptyTitle = $"No inventory for {_scope}";
            emptyDetail = "The AI BOM on screen does not include that connector. Choose All connectors to see the whole of it, or generate it again for that connector.";
        }
        else if (ActiveKind is not { } kind)
        {
            SetColumns(new[]
            {
                new InventoryBomColumn("Metric", InventoryBomColumnSize.Wide),
                new InventoryBomColumn("Value", InventoryBomColumnSize.Fill),
            });
            foreach (var (label, value) in SummaryRows(connectors))
            {
                _all.Add(new InventoryBomRowItem(new[] { label, value }, null));
            }
        }
        else
        {
            SetColumns(ColumnsFor(kind, ShowConnectorColumn));
            foreach (var connector in connectors)
            {
                foreach (var entity in connector.Entities(kind))
                {
                    var cells = ShowConnectorColumn ? new[] { entity.Connector }.Concat(entity.Cells).ToArray() : entity.Cells.ToArray();
                    _all.Add(new InventoryBomRowItem(cells, entity, entity.Verdict));
                }
            }

            if (!Collected(connectors, kind))
            {
                emptyTitle = $"{kind.Label()} were not collected";
                emptyDetail = _scanned.Count > 0 && !_scanned.Contains(kind)
                    ? $"This scan was limited to {string.Join(", ", _scanned.Select(k => k.OnlyName()))} (--only), so it did not look at {kind.Plural()}. An empty list here would mean “not looked at”, not “none”."
                    : $"The runtime says this scan did not collect {kind.Plural()}.";
                _all.Clear();
            }
        }

        var shown = Filtered(connectors);
        Rows.ReplaceAll(shown);

        if (snapshot is not null && connectors.Count > 0 && shown.Count == 0 && emptyTitle.Length == 0)
        {
            (emptyTitle, emptyDetail) = ActiveKind is { } k ? NoRows(k, connectors) : ("Nothing to summarize", string.Empty);
        }

        if (ActiveKind is { } active && snapshot is not null && connectors.Count > 0)
        {
            var dropped = connectors.Sum(c => c.Dropped(active));
            resultCaption = dropped > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{shown.Count:N0} shown · {dropped:N0} more not kept ({InventoryBomSnapshot.MaxRowsPerCategory:N0} per connector)")
                : string.Create(CultureInfo.CurrentCulture, $"{shown.Count:N0} shown");
        }

        EmptyTitle = emptyTitle;
        EmptyDetail = emptyDetail;
        ShowEmpty = emptyTitle.Length > 0;
        ResultCaption = resultCaption;
        Caption = CaptionFor(snapshot, connectors);

        // The rows are made again on every change (a search, a scope, a chip), so a selected row is found again by the inventory row behind it.
        var kept = selected?.Entity is { } behind ? shown.FirstOrDefault(r => ReferenceEquals(r.Entity, behind)) : null;
        if (!ReferenceEquals(SelectedRow, kept))
        {
            SelectedRow = kept;
        }

        RaiseCounts();
        ShowDetail();
    }

    /// <summary>
    /// Changes the grid's columns. The rows on screen hold cells for the old ones, so they are taken out first: the grid builds its new columns over an
    /// empty list and never reads a cell past the end of a row.
    /// </summary>
    private void SetColumns(IReadOnlyList<InventoryBomColumn> columns)
    {
        if (Columns.SequenceEqual(columns))
        {
            return;
        }

        Rows.ReplaceAll(Array.Empty<InventoryBomRowItem>());
        Columns = columns;
    }

    private List<InventoryBomRowItem> Filtered(IReadOnlyList<InventoryBomConnector> connectors)
    {
        IEnumerable<InventoryBomRowItem> rows = _all;
        if (ActiveKind is not null && StatusFilter != "all")
        {
            rows = rows.Where(r => r.Entity is { } e && PassesStatus(e));
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            rows = rows.Where(r => r.Matches(search));
        }

        return rows.ToList();
    }

    /// <summary>The TUI's Skills (eligible, warning, blocked) and Plugins (loaded, disabled, blocked) filters.</summary>
    private bool PassesStatus(InventoryBomEntity entity) => StatusFilter switch
    {
        "eligible" => entity.Eligible,
        "warning" => entity.Verdict == "warning",
        "blocked" => entity.Verdict == "blocked",
        "loaded" => entity.Status == "loaded",
        "disabled" => entity.Status == "disabled",
        _ => true,
    };

    private (string Title, string Detail) NoRows(InventoryBomKind kind, IReadOnlyList<InventoryBomConnector> connectors)
    {
        if (_all.Count > 0)
        {
            return SearchText.Trim().Length > 0
                ? ($"Nothing matches “{SearchText.Trim()}”", "Clear or change the search text.")
                : ("No items match the current filter", "Choose All to list every row.");
        }

        // The TUI says "No agents found."; the connector's own reason for an empty list, where it gave one, is the useful part.
        var reasons = connectors
            .SelectMany(c => c.Limitations.Where(l => string.Equals(l.Category, kind.OnlyName(), StringComparison.OrdinalIgnoreCase) || string.Equals(l.Category, kind.JsonKey(), StringComparison.OrdinalIgnoreCase)))
            .Select(l => ShowConnectorColumn ? $"{l.Connector}: {l.Reason}" : l.Reason)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ($"No {kind.Plural()} found", string.Join(" ", reasons));
    }

    private string CaptionFor(InventoryBomSnapshot? snapshot, IReadOnlyList<InventoryBomConnector> connectors)
    {
        if (snapshot is null)
        {
            return "No AI BOM yet";
        }

        if (connectors.Count == 0)
        {
            return "No inventory for this connector";
        }

        var items = connectors.Sum(c => c.TotalItems);
        var names = connectors.Count == 1 ? connectors[0].Name : string.Create(CultureInfo.InvariantCulture, $"{connectors.Count} connectors");
        var caption = string.Create(CultureInfo.CurrentCulture, $"{names} · {items:N0} item{(items == 1 ? string.Empty : "s")}");

        // When the inventory was built, in the operator's time: the generated_at the TUI prints as it is.
        return DateTimeOffset.TryParse(connectors[0].GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? caption + " · " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : caption;
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(SkillsCount));
        OnPropertyChanged(nameof(PluginsCount));
        OnPropertyChanged(nameof(McpCount));
        OnPropertyChanged(nameof(AgentsCount));
        OnPropertyChanged(nameof(ToolsCount));
        OnPropertyChanged(nameof(ModelsCount));
        OnPropertyChanged(nameof(MemoryCount));
        OnPropertyChanged(nameof(ShowToolsTab));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowDetailEmpty));
    }

    // ------------------------------------------------------------------ the detail pane

    private void ShowDetail()
    {
        DetailFields.Clear();
        DetailMore.Clear();

        if (IsSummary)
        {
            DetailHeading = "Coverage notes";
            DetailEmptyText = "Generate an AI BOM to see its coverage notes.";
            if (Snapshot is { } snapshot)
            {
                foreach (var (label, value) in Notes(snapshot))
                {
                    DetailFields.Add(new InventoryKeyValueRow(label, value));
                }
            }
        }
        else if (SelectedRow?.Entity is { } entity)
        {
            DetailHeading = entity.Title;
            foreach (var field in entity.Fields)
            {
                DetailFields.Add(new InventoryKeyValueRow(field.Label, field.Value.Length == 0 ? NoValue : field.Value));
            }

            foreach (var field in entity.More)
            {
                DetailMore.Add(new InventoryKeyValueRow(field.Label, field.Value));
            }
        }
        else
        {
            DetailHeading = "Details";
            DetailEmptyText = "Select a row to see its fields.";
        }

        HasMore = DetailMore.Count > 0;
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowDetailEmpty));
    }

    // ------------------------------------------------------------------ columns

    /// <summary>The kind's table: the TUI's columns, with the Connector column in front when the scan covered several connectors.</summary>
    internal static IReadOnlyList<InventoryBomColumn> ColumnsFor(InventoryBomKind kind, bool withConnector)
    {
        var columns = new List<InventoryBomColumn>();
        if (withConnector)
        {
            columns.Add(new InventoryBomColumn("Connector", InventoryBomColumnSize.Medium));
        }

        foreach (var header in kind.Columns())
        {
            columns.Add(new InventoryBomColumn(header, SizeOf(header), IsVerdict: header == "Verdict"));
        }

        return columns;
    }

    private static InventoryBomColumnSize SizeOf(string header) => header switch
    {
        "Enabled" or "Default" or "Findings" or "Files" or "Chunks" or "Severity" => InventoryBomColumnSize.Narrow,
        "ID" or "Name" or "Source" or "Origin" or "Model" or "Default Model" or "Workspace" or "Command/URL" => InventoryBomColumnSize.Wide,
        _ => InventoryBomColumnSize.Medium,
    };

    // ------------------------------------------------------------------ the Summary tab

    /// <summary>
    /// The TUI's <c>summary_table_rows</c> over <paramref name="connectors"/> (the one the scope names, or all of them): a row whose value is empty is
    /// left out, as there.
    /// </summary>
    private List<(string Label, string Value)> SummaryRows(IReadOnlyList<InventoryBomConnector> connectors)
    {
        var rows = new List<(string, string)>();
        var first = connectors[0];
        var several = connectors.Count > 1;

        rows.Add(("AIBOM version", string.Join(", ", connectors.Select(c => c.Version).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal))));
        rows.Add(("Generated", first.GeneratedAt));
        if (several)
        {
            rows.Add(("Source", $"All {connectors.Count} connectors: {string.Join(", ", connectors.Select(c => OverviewPanelViewModel.FriendlyConnectorName(c.Name)))}"));
            rows.Add(("Home / Config", "per connector (choose one in the connector scope)"));
        }
        else
        {
            var friendly = OverviewPanelViewModel.FriendlyConnectorName(first.Name);
            rows.Add(("Source", first.Name.Length > 0 && !string.Equals(friendly, first.Name, StringComparison.OrdinalIgnoreCase) ? $"{friendly} ({first.Name})" : friendly));
            rows.Add(("Home", first.Home));
            rows.Add(("Config", first.Config));
        }

        rows.Add(("Total items", connectors.Sum(c => c.TotalItems).ToString(CultureInfo.InvariantCulture)));
        rows.Add(("Skills", CountText(connectors, InventoryBomKind.Skills)));
        rows.Add(("Plugins", CountText(connectors, InventoryBomKind.Plugins)));
        rows.Add(("MCPs", CountText(connectors, InventoryBomKind.Mcp)));
        rows.Add(("Agents", CountText(connectors, InventoryBomKind.Agents)));

        // The newer runtime counts rule files; 0.8.10's summary has no such entry, so it gets no such row.
        if (connectors.Any(c => c.Rules is not null))
        {
            rows.Add(("Rules", connectors.Sum(c => c.Rules ?? 0).ToString(CultureInfo.InvariantCulture)));
        }

        rows.Add(("Tools", CountText(connectors, InventoryBomKind.Tools)));
        rows.Add(("Models", CountText(connectors, InventoryBomKind.Models)));
        rows.Add(("Memory", CountText(connectors, InventoryBomKind.Memory)));

        var errors = connectors.Sum(c => c.ErrorCount);
        if (errors != 0)
        {
            rows.Add(("Errors", errors.ToString(CultureInfo.InvariantCulture)));
        }

        var limitations = connectors.Sum(c => c.LimitationCount);
        if (limitations != 0)
        {
            rows.Add(("Unsupported capabilities", $"{limitations.ToString(CultureInfo.InvariantCulture)} (informational)"));
        }

        foreach (var (kind, noun) in new[] { (InventoryBomKind.Skills, "Skill"), (InventoryBomKind.Plugins, "Plugin"), (InventoryBomKind.Mcp, "MCP") })
        {
            rows.Add(($"{noun} policy verdicts", VerdictSummary(Sum(connectors.Select(c => c.Verdicts(kind))))));
        }

        foreach (var (kind, noun) in new[] { (InventoryBomKind.Skills, "Skill"), (InventoryBomKind.Plugins, "Plugin"), (InventoryBomKind.Mcp, "MCP") })
        {
            rows.Add(($"{noun} scan coverage", ScanSummary(Sum(connectors.Select(c => c.Coverage(kind))))));
        }

        // Under All, one line of counts per connector (aibom scan's own per-connector summary).
        if (several && _connectorLines.Count > 1)
        {
            foreach (var line in _connectorLines)
            {
                rows.Add((line.Connector, line.Summary));
            }
        }

        return rows.Where(r => r.Item2.Length > 0).ToList();
    }

    /// <summary>A kind's count as the TUI writes it: "2 (1 eligible)", "3 (2 loaded, 1 disabled)"; "not collected" for a kind the scan did not look at.</summary>
    private string CountText(IReadOnlyList<InventoryBomConnector> connectors, InventoryBomKind kind)
    {
        if (!Collected(connectors, kind))
        {
            return "not collected";
        }

        var counts = connectors.Select(c => c.Count(kind)).ToList();
        var total = counts.Sum(c => c?.Count ?? 0);
        var text = total.ToString(CultureInfo.InvariantCulture);
        switch (kind)
        {
            case InventoryBomKind.Skills:
                var eligible = counts.Sum(c => c?.Eligible ?? 0);
                return eligible != 0 ? $"{text} ({eligible.ToString(CultureInfo.InvariantCulture)} eligible)" : text;
            case InventoryBomKind.Plugins:
                var loaded = counts.Sum(c => c?.Loaded ?? 0);
                var disabled = counts.Sum(c => c?.Disabled ?? 0);
                return loaded != 0 || disabled != 0
                    ? $"{text} ({loaded.ToString(CultureInfo.InvariantCulture)} loaded, {disabled.ToString(CultureInfo.InvariantCulture)} disabled)"
                    : text;
            default:
                return text;
        }
    }

    private static Dictionary<string, long> Sum(IEnumerable<IReadOnlyDictionary<string, long>> maps)
    {
        var total = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            foreach (var (key, value) in map)
            {
                total[key] = total.GetValueOrDefault(key) + value;
            }
        }

        return total;
    }

    /// <summary>The TUI's <c>_verdict_summary</c>: "1 blocked  2 clean", in that fixed order, a zero left out. The newer runtime's "discovery-only" is last.</summary>
    private static string VerdictSummary(IReadOnlyDictionary<string, long> tally)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "blocked", "rejected", "allowed", "warning", "clean", "unscanned", "discovery-only" })
        {
            if (tally.GetValueOrDefault(key) != 0)
            {
                parts.Add($"{tally[key].ToString(CultureInfo.InvariantCulture)} {key}");
            }
        }

        return string.Join("  ", parts);
    }

    /// <summary>The TUI's <c>_scan_summary</c>: "2 scanned  0 unscanned  1 findings"; nothing when there is no coverage to report.</summary>
    private static string ScanSummary(IReadOnlyDictionary<string, long> coverage)
    {
        if (coverage.Count == 0)
        {
            return string.Empty;
        }

        var scanned = coverage.GetValueOrDefault("scanned");
        var unscanned = coverage.GetValueOrDefault("unscanned");
        var findings = coverage.GetValueOrDefault("total_findings");
        if (scanned == 0 && unscanned == 0 && findings == 0)
        {
            return string.Empty;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scanned} scanned  {unscanned} unscanned  {findings} findings");
    }

    // ------------------------------------------------------------------ the coverage notes (the Summary's detail)

    /// <summary>
    /// What the scan could not do, in the Summary tab's detail pane: a connector the output named but that could not be used, the commands that
    /// failed, what a connector cannot inventory, the categories an <c>--only</c> scan left out and rows the bound left out. Every note names its
    /// connector; "Nothing to report" when there is none.
    /// </summary>
    private List<(string Label, string Value)> Notes(InventoryBomSnapshot snapshot)
    {
        var notes = new List<(string, string)>();

        foreach (var skipped in snapshot.Skipped)
        {
            notes.Add(("Skipped", $"{skipped.Name}: {skipped.Reason}"));
        }

        foreach (var connector in snapshot.Connectors.Where(InScope))
        {
            foreach (var error in connector.Errors)
            {
                notes.Add(($"{connector.Name}: command failed", error.Command.Length > 0 ? $"{error.Command} - {error.Message}" : error.Message));
            }

            if (connector.ErrorCount > connector.Errors.Count)
            {
                notes.Add(($"{connector.Name}: command failed", $"{(connector.ErrorCount - connector.Errors.Count).ToString(CultureInfo.InvariantCulture)} more not listed"));
            }

            foreach (var limitation in connector.Limitations)
            {
                var label = string.Equals(limitation.Status, "unsupported", StringComparison.OrdinalIgnoreCase)
                    ? "Not supported"
                    : string.Equals(limitation.Status, "unverified", StringComparison.OrdinalIgnoreCase) ? "Partly checked" : limitation.Status;
                notes.Add(($"{connector.Name}: {label}", $"{limitation.Category} - {limitation.Reason}"));
            }

            foreach (var kind in InventoryBomKinds.All)
            {
                if (connector.Dropped(kind) > 0)
                {
                    notes.Add((
                        $"{connector.Name}: {kind.Label()}",
                        string.Create(CultureInfo.CurrentCulture, $"showing the first {connector.Entities(kind).Count:N0} of {connector.Entities(kind).Count + connector.Dropped(kind):N0}")));
                }
            }
        }

        if (_scanned.Count > 0)
        {
            notes.Add(("Scope", $"This scan was limited to {string.Join(", ", _scanned.Select(k => k.OnlyName()))} (--only); the other categories were not looked at."));
        }

        if (notes.Count == 0)
        {
            notes.Add(("Nothing to report", "Every connector was read and every category was inventoried."));
        }

        return notes;
    }
}
