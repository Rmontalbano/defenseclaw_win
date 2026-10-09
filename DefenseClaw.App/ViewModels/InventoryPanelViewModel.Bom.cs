using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>One connector's line in the generated AI BOM summary.</summary>
public sealed record InventoryBomRow(string Connector, string Summary)
{
    public override string ToString() => $"{Connector}: {Summary}";
}

/// <summary>
/// Inventory: empty-state logic and the optional "Generate AI BOM" action.
/// <para>
/// <b>AI BOM.</b> <c>defenseclaw aibom scan --json [--only C,...] [--connector C]</c> (flags from its help) inventories the
/// active connectors' own skills, plugins, MCP servers, agents, tools, models and memory with policy
/// verdicts. The inventory is only read, but every run records a scan event in audit.db and posts to the
/// gateway (and fails closed if the gateway is down), so it is a state-changing command: it goes through the
/// review dialog and never runs on its own. Its JSON shape was never captured from a live run (that writes the audit
/// record); it is taken from the code that prints it, see <see cref="InventoryBomSnapshot"/>.
/// </para>
/// <para>
/// <b>Browsing it.</b> What a run printed is kept, parsed and bounded, in <see cref="BomBrowser"/> (the TUI's Inventory panel: a Summary and
/// one table per kind with a detail pane) until the next run. The run keeps its whole output (<see cref="DiscoverStep.RetainFullOutput"/>);
/// output the runner had to cut, or that is over the parser's cap, is not parsed at all and the status line says "Too large to display" with what
/// to do about it. The per-connector count lines (<see cref="ParseBom"/>, which reads counts even from output with no summary) are the Summary
/// tab's "by connector" rows.
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel
{
    public const string AllConnectorsLabel = "All active connectors";

    /// <summary>The page shows the AI components from inventory.db (the page's own subject).</summary>
    public const string ViewComponents = "components";

    /// <summary>The page shows the AI bill of materials a scan printed.</summary>
    public const string ViewBom = "bom";

    private static readonly Regex ConnectorNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private string? _lastBomJson;
    private bool _lastBomTruncated;

    /// <summary>The AI BOM a scan printed, as the TUI browses it: tabs, tables, detail and the <c>--only</c> scope chips.</summary>
    public InventoryBomBrowser BomBrowser { get; } = new();

    /// <summary><see cref="ViewComponents"/> or <see cref="ViewBom"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBomView), nameof(IsComponentsView), nameof(ToolbarCaption), nameof(SearchPlaceholder), nameof(SearchAutomationName), nameof(SearchHelp))]
    private string _activeView = ViewComponents;

    public bool IsBomView => ActiveView == ViewBom;

    public bool IsComponentsView => !IsBomView;

    /// <summary>The toolbar's caption: which scan the components come from, or which AI BOM is on screen.</summary>
    public string ToolbarCaption => IsBomView ? BomBrowser.Caption : StatusMessage ?? string.Empty;

    public string SearchPlaceholder => IsBomView ? "Search the AI BOM…" : "Search name, vendor, framework…";

    public string SearchAutomationName => IsBomView ? "Search the AI BOM" : "Search components";

    public string SearchHelp => IsBomView
        ? "Matches any column of the rows in the table. Shortcut Ctrl+F; Esc clears it."
        : "Matches name, vendor, framework and ecosystem. Shortcut Ctrl+F; Esc clears it.";

    /// <summary>
    /// A note about the last AI BOM that is not an error: connectors it printed that could not be read (skipped, and named), and the inventory
    /// commands that failed on the connectors that were. Empty when there is nothing to say.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBomWarning))]
    private string? _bomWarning;

    public bool HasBomWarning => !string.IsNullOrWhiteSpace(BomWarning);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ToolbarCaption));

    private void OnBomBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InventoryBomBrowser.Caption):
                OnPropertyChanged(nameof(ToolbarCaption));
                break;

            case nameof(InventoryBomBrowser.HasSnapshot):
                OnPropertyChanged(nameof(BomFailed));
                break;
        }
    }

    /// <summary>inventory.db does not exist yet: the normal state until AI discovery has run once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabase))]
    private bool _databaseMissing;

    /// <summary>False until the first read finishes, so the grid does not flash "no components" while it is still looking.</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private bool _showEmptyState;

    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    private string _emptyDetail = string.Empty;

    [ObservableProperty]
    private string? _selectedBomConnector;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBom), nameof(BomFailed))]
    private string? _bomStatus;

    [ObservableProperty]
    private bool _canSaveBom;

    public bool HasDatabase => !DatabaseMissing;

    public bool HasBom => !string.IsNullOrWhiteSpace(BomStatus);

    /// <summary>
    /// The last run left nothing to browse - it did not finish, its output was too large or was not JSON, or it listed no connector - so its
    /// status line is what the AI BOM page has to say.
    /// </summary>
    public bool BomFailed => HasBom && !BomBrowser.HasSnapshot;

    public ObservableCollection<string> BomConnectors { get; } = new();

    public ObservableCollection<InventoryBomRow> BomRows { get; } = new();

    /// <summary>Esc closes the review dialog. True when it consumed the key.</summary>
    public bool HandleEscape() => Review.IsOpen && Review.HandleEscape();

    /// <summary>
    /// Says why the components grid is empty, when it is: no database yet, no rollup for this database's
    /// layout, a scan that found nothing, or a search that hides everything. Never a scary state for a
    /// normal install.
    /// </summary>
    private void UpdateEmptyState()
    {
        if (!HasLoaded)
        {
            ShowEmptyState = false;
            return;
        }

        if (DatabaseMissing)
        {
            EmptyTitle = "No inventory yet";
            EmptyDetail = "inventory.db is created the first time AI discovery runs. Turn on AI Discovery and run a scan, then refresh this page.";
            ShowEmptyState = true;
            return;
        }

        if (UsingFallbackView)
        {
            EmptyTitle = "No component rollup available";
            EmptyDetail = "Use the table browser below to inspect the raw tables this install's inventory.db does have.";
            ShowEmptyState = true;
            return;
        }

        if (_allComponents.Count == 0)
        {
            EmptyTitle = "No components in the latest scan";
            EmptyDetail = (string.IsNullOrWhiteSpace(ScanNote) ? string.Empty : ScanNote + " ") +
                "Nothing found is not the same as nothing looked at: AI Discovery lists exactly what its scans examined.";
            ShowEmptyState = true;
            return;
        }

        var shown = ComponentsView.Cast<object>().Count();
        if (shown == 0)
        {
            EmptyTitle = $"Nothing matches “{SearchText}”";
            EmptyDetail = "Clear or change the search text.";
            ShowEmptyState = true;
            return;
        }

        ShowEmptyState = false;
    }

    // The BOM's connector picker and the shared connector scope are one choice (the scope is the source of truth): picking here narrows the
    // whole app, and a scope change selects it here and re-lists the rows of the last BOM under it.

    private readonly List<InventoryBomRow> _bomAll = new();
    private bool _followingScope;

    partial void OnSelectedBomConnectorChanged(string? value)
    {
        // A null is the combo losing its selection while the list is rebuilt, not a pick.
        if (_followingScope || value is null)
        {
            return;
        }

        var connector = !string.IsNullOrWhiteSpace(value) && !string.Equals(value, AllConnectorsLabel, StringComparison.Ordinal) ? value : null;
        _ = Services.ConnectorScope.Set(connector);
    }

    protected override void OnConnectorScopeChanged() => FollowScope(Services.ConnectorScope.Current);

    private void FollowScope(string? scope)
    {
        var wanted = scope is null
            ? AllConnectorsLabel
            : BomConnectors.FirstOrDefault(c => string.Equals(c, scope, StringComparison.OrdinalIgnoreCase));

        if (wanted is null)
        {
            // A live connector config.yaml does not name is still one the operator can scope to.
            if (ConnectorNamePattern.IsMatch(scope!))
            {
                BomConnectors.Add(scope!);
                wanted = scope;
            }
            else
            {
                wanted = AllConnectorsLabel;
            }
        }

        _followingScope = true;
        try
        {
            SelectedBomConnector = wanted;
        }
        finally
        {
            _followingScope = false;
        }

        ApplyBomScope();
    }

    /// <summary>The last BOM's connector lines, narrowed to the shared scope (a line is one connector's; All shows every line), and the browser with them.</summary>
    private void ApplyBomScope()
    {
        var scope = Services.ConnectorScope;
        BomRows.Clear();
        foreach (var row in _bomAll)
        {
            if (scope.Allows(row.Connector))
            {
                BomRows.Add(row);
            }
        }

        BomBrowser.SetConnector(scope.Current);
        BomBrowser.SetConnectorLines(BomRows.ToList());
    }

    /// <summary>The connectors <c>aibom scan --connector</c> can be narrowed to: the ones named in config.yaml.</summary>
    private void BuildBomConnectors()
    {
        BomConnectors.Clear();
        BomConnectors.Add(AllConnectorsLabel);

        var guardrail = Services.Config.Config.Guardrail;
        AddConnector(guardrail.Connector);
        foreach (var key in guardrail.Connectors.Keys)
        {
            AddConnector(key);
        }

        AddConnector(Services.ConnectorScope.Current);

        FollowScope(Services.ConnectorScope.Current);

        void AddConnector(string? name)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && ConnectorNamePattern.IsMatch(trimmed) &&
                !BomConnectors.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                BomConnectors.Add(trimmed);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeInstallation))]
    private void GenerateAiBom()
    {
        var connector = SelectedBomConnector;
        var scoped = !string.IsNullOrWhiteSpace(connector) &&
            !string.Equals(connector, AllConnectorsLabel, StringComparison.Ordinal) &&
            ConnectorNamePattern.IsMatch(connector);

        // The TUI's order: --only (the scope chips), then --connector.
        var only = BomBrowser.OnlyArgument;
        var scanned = only.Length == 0 ? Array.Empty<InventoryBomKind>() : BomBrowser.ScopeKinds.ToArray();

        var argv = new List<string> { "aibom", "scan", "--json" };
        if (only.Length > 0)
        {
            argv.Add("--only");
            argv.Add(only);
        }

        if (scoped)
        {
            argv.Add("--connector");
            argv.Add(connector!);
        }

        var what = only.Length == 0
            ? "the skills, plugins, MCP servers, agents, tools, models and memory"
            : "only the " + string.Join(", ", scanned.Select(k => k.Plural()));
        Review.Open(
            "Generate an AI BOM?",
            $"Inventories {what} of " +
            (scoped ? $"the {connector} connector" : "every active connector") +
            ", with their policy verdicts. The inventory itself is only read, but every run records a scan event " +
            "in audit.db (it raises Overview's Scans count) and posts an observability event to the gateway, and it " +
            "fails if the gateway is down. This reads the connectors' own configuration; it is not the machine-wide " +
            "scan on the AI Discovery page.",
            new[]
            {
                // The whole output is kept (up to the runner's full-output ceiling): a BOM is far longer than an ordinary command's 2,000 lines,
                // and a transcript cut at its start is not an inventory.
                new DiscoverStep(
                    argv,
                    "Build the AI bill of materials as JSON.",
                    CommandTier.StateChanging,
                    CliRunner.ExtendedTimeout,
                    RetainFullOutput: true),
            },
            result => AfterBomAsync(result, scoped ? connector : null, scanned),
            primaryText: "Generate");
    }

    private async Task AfterBomAsync(DiscoverReviewResult result, string? connector, IReadOnlyList<InventoryBomKind> scanned)
    {
        BomRows.Clear();
        _bomAll.Clear();
        _lastBomJson = null;
        _lastBomTruncated = false;
        CanSaveBom = false;
        BomWarning = null;
        BomStatus = null;
        BomBrowser.Clear();

        // Whatever the run came to, its answer is on the AI BOM page: the status line is there.
        ActiveView = ViewBom;

        var stamp = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (!result.Succeeded || result.Invocations.Count == 0)
        {
            BomStatus = $"The AI BOM run did not finish ({stamp}). The dialog and the Activity panel have its output.";
            return;
        }

        var invocation = result.Invocations[^1];
        var scope = connector ?? "every active connector";

        // The runner keeps the END of an output longer than it retains and says so; what is left starts in the middle of the document, so it is not
        // parsed (a fragment could pass for a smaller inventory).
        if (invocation.IsOutputTruncated)
        {
            _lastBomTruncated = true;
            BomStatus = $"Too large to display: the AI BOM output ran past the {invocation.RetainedLineLimit:N0} lines this app keeps of one command's output " +
                        $"({invocation.DroppedOutputLineCount:N0} were dropped), so none of it is shown here ({stamp}). Narrow the scan - one connector, " +
                        "or fewer categories under Scope - and generate it again, or run 'defenseclaw aibom scan --json' in a terminal to capture all of it.";
            return;
        }

        var stdout = DiscoverCli.Stdout(invocation);
        var (parsed, lines) = await Task.Run(() =>
        {
            var snapshot = InventoryBomSnapshot.Parse(stdout, connector);
            return (snapshot, snapshot.Succeeded ? ParseBomLines(stdout, connector) : new List<InventoryBomRow>());
        }).ConfigureAwait(true);

        switch (parsed.Status)
        {
            case InventoryBomParseStatus.TooLarge:
                BomStatus = $"Too large to display: {parsed.Message} ({stamp}). Narrow the scan - one connector, or fewer categories under Scope - and " +
                            "generate it again, or run 'defenseclaw aibom scan --json' in a terminal to capture all of it.";
                return;

            case InventoryBomParseStatus.NotJson:
                BomStatus = $"AI BOM ran ({stamp}) but its output was not the JSON this page expected ({parsed.Message}). See the Activity panel.";
                return;
        }

        var bom = parsed.Snapshot!;
        if (bom.Connectors.Count == 0)
        {
            // '[]' is what a scan of no connector prints (cmd_aibom.py:111-118): nothing was inventoried.
            BomStatus = bom.Skipped.Count == 0
                ? $"AI BOM ran ({stamp}) but listed no connector, so there is nothing to summarize. Is a connector configured? The Activity panel has its output."
                : $"AI BOM ran ({stamp}) but no connector in its output could be read: {SkippedText(bom)}. The Activity panel has its output.";
            return;
        }

        foreach (var row in lines)
        {
            _bomAll.Add(row);
        }

        BomBrowser.Load(bom, scanned);
        ApplyBomScope();

        // The Overview's coverage rows (CUST-329) show the last good run; a run that failed leaves the earlier one there, with its time.
        Services.InventoryBom.Publish(bom, scanned, DateTimeOffset.Now);

        _lastBomJson = stdout;
        CanSaveBom = !string.IsNullOrWhiteSpace(stdout);
        BomWarning = WarningFor(bom);
        BomStatus = $"AI BOM generated {stamp} for {scope}. The full JSON is in the Activity panel, and you can save it.";
    }

    /// <summary>
    /// What to tell the operator about connectors of the scan that did not come through whole: the ones that could not be read at all
    /// (skipped, each named with the reason) and the ones whose inventory commands failed (kept, each failed command named). Null for a clean scan.
    /// </summary>
    internal static string? WarningFor(InventoryBomSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.Skipped.Count > 0)
        {
            lines.Add($"Skipped {snapshot.Skipped.Count} connector{(snapshot.Skipped.Count == 1 ? string.Empty : "s")} the output named but this page could not read: {SkippedText(snapshot)}.");
        }

        foreach (var connector in snapshot.Connectors.Where(c => c.ErrorCount > 0))
        {
            var commands = connector.Errors.Select(e => e.Command).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            lines.Add(
                $"{connector.Name}: {connector.ErrorCount} inventory command{(connector.ErrorCount == 1 ? string.Empty : "s")} failed" +
                (commands.Count > 0 ? $" ({string.Join(", ", commands)})" : string.Empty) +
                "; what it did list is shown. The Summary tab has the reasons.");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string SkippedText(InventoryBomSnapshot snapshot) =>
        string.Join("; ", snapshot.Skipped.Select(s => $"{s.Name} ({s.Reason})"));

    /// <summary><see cref="ParseBom"/> for output the snapshot has already read: the same JSON, so it cannot fail, but a count line is never worth an exception.</summary>
    private static List<InventoryBomRow> ParseBomLines(string stdout, string? connector)
    {
        try
        {
            return ParseBom(stdout, connector);
        }
        catch (JsonException)
        {
            return new List<InventoryBomRow>();
        }
    }

    /// <summary>The seven inventory categories, in the order the CLI lists them (claw_inventory.py:724-747, _build_summary).</summary>
    private static readonly string[] BomCategories =
        { "skills", "plugins", "mcp", "agents", "tools", "model_providers", "memory" };

    /// <summary>
    /// A connector's category counts from <c>aibom scan --json</c>: the bare object for one connector, a list
    /// for several (<c>[]</c> when no connector is active; cmd_aibom.py:111-118). Each connector object is the inventory
    /// dict of claw_inventory.py:104-174 / 2091-2177: seven category arrays (skills, plugins, mcp, agents, tools,
    /// model_providers, memory), <c>errors</c> and <c>limitations</c> arrays, and a <c>summary</c> that holds
    /// <c>total_items</c>, one <c>{"count": n, …}</c> object per category, <c>errors</c> and <c>limitations</c> as
    /// numbers, plus the <c>policy_*</c> / <c>scan_*</c> objects policy enrichment adds (claw_inventory.py:724-747).
    /// The counts are read from the summary, and from the category array when the summary lacks one; the many
    /// other top-level arrays (<c>connector_skill_dirs</c>, <c>connector_config_files</c>, …) are paths, not components,
    /// and are never counted.
    /// </summary>
    internal static List<InventoryBomRow> ParseBom(string json, string? requestedConnector)
    {
        var rows = new List<InventoryBomRow>();
        using var document = JsonDocument.Parse(DiscoverCli.TrimToJson(json));
        var root = document.RootElement;

        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var connector = FirstString(item, "connector", "connector_name", "name") ?? requestedConnector ?? "active connector";
            var counts = new List<(string Name, long Count)>();

            var summary = item.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
                ? summaryElement
                : (JsonElement?)null;

            foreach (var category in BomCategories)
            {
                if ((summary is { } s ? SummaryCount(s, category) : null) is { } fromSummary)
                {
                    counts.Add((category, fromSummary));
                }
                else if (item.TryGetProperty(category, out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    counts.Add((category, array.GetArrayLength()));
                }
            }

            // Only worth a word when there is something: a failed category command, or a category this connector cannot inventory.
            if (counts.Count > 0)
            {
                foreach (var name in new[] { "errors", "limitations" })
                {
                    var count = (summary is { } s2 ? SummaryCount(s2, name) : null)
                        ?? (item.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : (long?)null);
                    if (count is > 0)
                    {
                        counts.Add((name, count.Value));
                    }
                }
            }

            rows.Add(new InventoryBomRow(
                connector,
                counts.Count == 0
                    ? "inventory generated; the category counts could not be read from this output (see Activity)"
                    : string.Join(
                        " · ",
                        counts.Select(c => $"{c.Name.Replace('_', ' ')} {c.Count.ToString(CultureInfo.InvariantCulture)}"))));
        }

        return rows;
    }

    /// <summary>A summary entry as a count: a bare number, or the <c>count</c> of a category object.</summary>
    private static long? SummaryCount(JsonElement summary, string name)
    {
        if (!summary.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            value = value.TryGetProperty("count", out var count) ? count : default;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>Writes the generated BOM JSON to a file the operator chooses.</summary>
    [RelayCommand]
    private void SaveBom()
    {
        if (_lastBomJson is null || _lastBomTruncated)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save AI BOM",
            FileName = $"aibom-{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.json",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _lastBomJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            BomStatus = $"Saved the AI BOM to {dialog.FileName}. It lists your connectors' skills, plugins and MCP servers, so share it with care.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BomStatus = $"Could not save the AI BOM: {ex.Message}";
        }
    }
}
