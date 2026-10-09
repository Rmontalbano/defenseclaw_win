using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The AI BOM browser (<see cref="InventoryBomBrowser"/>) over synthetic <c>aibom scan --json</c> output (see <c>AiBomSnapshotTests</c> for where
/// the fixtures come from): the tabs and their counts, each tab's rows and the detail fields of a selected row, the Summary tab's rows and
/// coverage notes, the Connector column, the shared connector scope, the Skills and Plugins status chips, search, the <c>--only</c> scope
/// chips, and the bounds. Plain objects: no view, no process, no window.
/// </summary>
public sealed class InventoryBomBrowserTests
{
    private static InventoryBomBrowser Browse(string fixture, params InventoryBomKind[] scanned) => BrowseText(PayloadFixtures.Read(fixture), scanned);

    private static InventoryBomBrowser BrowseText(string json, params InventoryBomKind[] scanned)
    {
        var result = InventoryBomSnapshot.Parse(json);
        Assert.True(result.Succeeded, result.Message);
        var browser = new InventoryBomBrowser();
        browser.Load(result.Snapshot!, scanned);
        return browser;
    }

    private static string[][] Cells(InventoryBomBrowser browser) => browser.Rows.Select(r => r.Cells.ToArray()).ToArray();

    private static string[] Ids(InventoryBomBrowser browser) => browser.Rows.Select(r => r.Cells[0]).ToArray();

    private static (string Label, string Value)[] Summary(InventoryBomBrowser browser)
    {
        browser.ActiveTab = InventoryBomBrowser.SummaryTab;
        return browser.Rows.Select(r => (r.Cells[0], r.Cells[1])).ToArray();
    }

    private static string Value(InventoryBomBrowser browser, string metric) => Summary(browser).Single(r => r.Label == metric).Value;

    private static string[] Labels(IEnumerable<InventoryKeyValueRow> rows) => rows.Select(r => r.Key).ToArray();

    private static string Detail(InventoryBomBrowser browser, string label) => browser.DetailFields.Single(f => f.Key == label).Value;

    private static string[] Columns(InventoryBomBrowser browser) => browser.Columns.Select(c => c.Header).ToArray();

    // ------------------------------------------------------------------ before there is anything to browse

    [Fact]
    public void Before_a_scan_there_is_one_empty_state_that_offers_to_generate_one()
    {
        var browser = new InventoryBomBrowser();

        Assert.False(browser.HasSnapshot);
        Assert.True(browser.ShowEmpty);
        Assert.True(browser.ShowGenerate);
        Assert.Equal("No AI BOM yet", browser.EmptyTitle);
        Assert.Contains("asks first", browser.EmptyDetail, StringComparison.Ordinal);
        Assert.Empty(browser.Rows);
        Assert.Equal("No AI BOM yet", browser.Caption);
        Assert.Null(browser.SkillsCount);
        Assert.False(browser.ShowToolsTab);
        Assert.False(browser.HasDetail);
    }

    // ------------------------------------------------------------------ the tabs

    [Fact]
    public void A_scan_opens_on_the_summary_tab_with_a_count_for_each_other_tab()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");

        Assert.Equal("summary", browser.ActiveTab);
        Assert.True(browser.IsSummary);
        Assert.False(browser.ShowEmpty);
        Assert.Equal(
            new int?[] { 2, 2, 2, 3, 1, 5, 1 },
            new[] { browser.SkillsCount, browser.PluginsCount, browser.McpCount, browser.AgentsCount, browser.ToolsCount, browser.ModelsCount, browser.MemoryCount });
        Assert.True(browser.ShowToolsTab);
        Assert.Equal("openclaw · 16 items" + Suffix(browser.Caption), browser.Caption);
    }

    /// <summary>The caption ends in the time the inventory was built, in the operator's zone: whatever follows " · 16 items".</summary>
    private static string Suffix(string caption)
    {
        var at = caption.IndexOf(" · 16 items", StringComparison.Ordinal) + " · 16 items".Length;
        return caption[at..];
    }

    [Fact]
    public void The_tools_tab_is_there_only_when_some_connector_listed_a_tool()
    {
        var withTools = Browse("aibom-scan.claudecode.json");
        var without = Browse("aibom-scan.multi-connector.json");

        Assert.True(withTools.ShowToolsTab);
        Assert.False(without.ShowToolsTab);
    }

    [Fact]
    public void Changing_tab_changes_the_columns_and_the_rows_and_clears_the_selection_and_the_status_chip()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");

        browser.ActiveTab = "agents";
        browser.SelectedRow = browser.Rows[0];
        browser.StatusFilter = "blocked";
        Assert.Equal(new[] { "ID", "Source", "Model", "Workspace", "Default" }, Columns(browser));

        browser.ActiveTab = "models";

        Assert.Equal(new[] { "ID", "Source", "Default Model", "Status" }, Columns(browser));
        Assert.Null(browser.SelectedRow);
        Assert.Equal("all", browser.StatusFilter);
        Assert.Equal(5, browser.Rows.Count);
    }

    // ------------------------------------------------------------------ Agents, Models, Memory: rows and detail fields

    [Fact]
    public void The_agents_tab_lists_the_rows_with_the_tuis_cells_and_a_selected_row_shows_the_tuis_fields()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "agents";

        Assert.Equal(new[] { "main", "helper", "_defaults" }, Ids(browser));
        Assert.Equal(new[] { "main", "", "example/model-large", @"C:\Users\example\.openclaw\workspace", "yes" }, Cells(browser)[0]);
        Assert.Equal("3 shown", browser.ResultCaption);
        Assert.Equal("Details", browser.DetailHeading);
        Assert.False(browser.HasDetail);
        Assert.Equal("Select a row to see its fields.", browser.DetailEmptyText);

        browser.SelectedRow = browser.Rows[0];

        Assert.Equal("AGENT: main", browser.DetailHeading);
        Assert.Equal(new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" }, Labels(browser.DetailFields));
        Assert.Equal("example/model-large", Detail(browser, "Model"));
        Assert.Equal("true", Detail(browser, "Default"));
        // A value the row did not carry is a dash, not a blank.
        Assert.Equal("—", Detail(browser, "Source"));
        Assert.Equal("0", Detail(browser, "Max Concurrent"));

        // The members the TUI leaves out, under their own heading.
        Assert.True(browser.HasMore);
        Assert.Equal(new[] { "Bindings" }, Labels(browser.DetailMore));
        Assert.Equal("2", browser.DetailMore[0].Value);
    }

    [Fact]
    public void The_models_tab_lists_every_row_and_the_selected_ones_fields_in_the_tuis_order()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "models";

        Assert.Equal(new[] { "_config", "example", "example-alt", "example-search-provider", "example/model-large" }, Ids(browser));
        browser.SelectedRow = browser.Rows[0];

        Assert.Equal("MODEL: _config", browser.DetailHeading);
        Assert.Equal(new[] { "Source", "Default Model", "Status", "Config", "Fallbacks", "Allowed" }, Labels(browser.DetailFields));
        Assert.Equal("—", Detail(browser, "Status"));
        Assert.Equal("example/model-large, example/model-small", Detail(browser, "Allowed"));

        browser.SelectedRow = browser.Rows[4];
        Assert.Equal(new[] { "Name", "Available", "Local", "Input", "Context Window" }, Labels(browser.DetailMore));
    }

    [Fact]
    public void The_memory_tab_lists_the_stores_and_the_selected_ones_fields()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "memory";

        Assert.Equal(new[] { "ID", "Backend", "Provider", "Files", "Chunks", "Workspace" }, Columns(browser));
        var row = Assert.Single(browser.Rows);
        Assert.Equal(new[] { "main", "sqlite", "example-embed", "12", "340", @"C:\Users\example\.openclaw\workspace" }, row.Cells);

        browser.SelectedRow = row;

        Assert.Equal("MEMORY: main", browser.DetailHeading);
        Assert.Equal(
            new[] { "Backend", "Provider", "Workspace", "DB Path", "Files", "Chunks", "FTS Available", "Vector Enabled", "Sources" },
            Labels(browser.DetailFields));
        Assert.Equal("memory, sessions", Detail(browser, "Sources"));
        Assert.False(browser.HasMore);
        Assert.Empty(browser.DetailMore);
    }

    [Fact]
    public void Skills_plugins_mcps_and_tools_are_read_only_tabs_with_the_tuis_columns_and_fields()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");

        browser.ActiveTab = "skills";
        Assert.Equal(new[] { "ID", "Verdict", "Enabled", "Severity", "Findings", "Source" }, Columns(browser));
        Assert.True(browser.Columns[1].IsVerdict);
        Assert.Equal(new[] { "weather", "gh-issues" }, Ids(browser));
        browser.SelectedRow = browser.Rows[0];
        Assert.Equal("SKILL: weather", browser.DetailHeading);
        Assert.Equal("Description", browser.DetailFields[0].Key);
        Assert.Equal("clean", browser.SelectedRow!.Verdict);
        Assert.Equal("Ok", browser.SelectedRow.VerdictTone);

        browser.ActiveTab = "plugins";
        Assert.Equal(new[] { "Example Search", "example-voice" }, Ids(browser));

        browser.ActiveTab = "mcp";
        Assert.Equal(new[] { "docs", "remote" }, Ids(browser));
        Assert.Equal("npx", Cells(browser)[0][3]);

        browser.ActiveTab = "tools";
        Assert.Equal(new[] { "ID", "Name", "Kind", "Source" }, Columns(browser));
        Assert.Equal(new[] { "web_search" }, Ids(browser));
    }

    // ------------------------------------------------------------------ the Summary tab

    [Fact]
    public void The_summary_is_the_tuis_summary_rows_label_for_label()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");

        Assert.Equal(new[] { "Metric", "Value" }, Columns(browser));
        Assert.Equal(
            new[]
            {
                ("AIBOM version", "3"),
                ("Generated", "2030-01-15T10:00:05.123456+00:00"),
                ("Source", "OpenClaw"),
                ("Home", @"C:\Users\example\.openclaw"),
                ("Config", @"C:\Users\example\.openclaw\openclaw.json"),
                ("Total items", "16"),
                ("Skills", "2 (1 eligible)"),
                ("Plugins", "2 (1 loaded, 1 disabled)"),
                ("MCPs", "2"),
                ("Agents", "3"),
                ("Tools", "1"),
                ("Models", "5"),
                ("Memory", "1"),
                ("Skill policy verdicts", "1 warning  1 clean"),
                ("Plugin policy verdicts", "2 unscanned"),
                ("MCP policy verdicts", "1 clean  1 unscanned"),
                ("Skill scan coverage", "2 scanned  0 unscanned  2 findings"),
                ("Plugin scan coverage", "0 scanned  2 unscanned  0 findings"),
                ("MCP scan coverage", "1 scanned  1 unscanned  0 findings"),
            },
            Summary(browser));
    }

    [Fact]
    public void The_source_names_the_connector_as_the_tui_does_with_its_wire_name_when_that_differs()
    {
        var browser = Browse("aibom-scan.claudecode.json");

        Assert.Equal("Claude Code (claudecode)", Value(browser, "Source"));
        Assert.Equal(@"C:\Users\example\.claude", Value(browser, "Home"));
        // The first config file, not the connector's other ones.
        Assert.Equal(@"C:\Users\example\.claude\settings.json", Value(browser, "Config"));
        Assert.Equal("2 (1 eligible)", Value(browser, "Skills"));
        Assert.Equal("1 (1 loaded, 0 disabled)", Value(browser, "Plugins"));
    }

    [Fact]
    public void Errors_and_unsupported_capabilities_have_rows_only_when_there_are_some()
    {
        var clean = Summary(Browse("aibom-scan.openclaw.synthetic.json"));
        var codex = Browse("aibom-scan.multi-connector.json");
        codex.SetConnector("codex");
        var claude = Browse("aibom-scan.multi-connector.json");
        claude.SetConnector("claudecode");

        Assert.DoesNotContain(clean, r => r.Label is "Errors" or "Unsupported capabilities");
        Assert.Equal("1", Value(codex, "Errors"));
        Assert.DoesNotContain(Summary(codex), r => r.Label == "Unsupported capabilities");
        Assert.Equal("4 (informational)", Value(claude, "Unsupported capabilities"));
        Assert.DoesNotContain(Summary(claude), r => r.Label == "Errors");
    }

    [Fact]
    public void Under_all_with_several_connectors_the_summary_sums_them_and_names_them_all()
    {
        var browser = Browse("aibom-scan.multi-connector.json");
        browser.SetConnectorLines(InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.multi-connector.json"), null));

        Assert.Equal("All 2 connectors: Claude Code, Codex", Value(browser, "Source"));
        Assert.Equal("per connector (choose one in the connector scope)", Value(browser, "Home / Config"));
        Assert.DoesNotContain(Summary(browser), r => r.Label is "Home" or "Config");
        Assert.Equal("2", Value(browser, "Total items"));
        Assert.Equal("1 (1 eligible)", Value(browser, "Skills"));
        Assert.Equal("1", Value(browser, "Errors"));
        Assert.Equal("4 (informational)", Value(browser, "Unsupported capabilities"));

        // aibom scan's own count line for each connector closes the table.
        Assert.Equal(
            "skills 1 · plugins 0 · mcp 1 · agents 0 · tools 0 · model providers 0 · memory 0 · limitations 4",
            Value(browser, "claudecode"));
        Assert.Equal(
            "skills 0 · plugins 0 · mcp 0 · agents 0 · tools 0 · model providers 0 · memory 0 · errors 1",
            Value(browser, "codex"));
    }

    [Fact]
    public void Narrowed_to_one_connector_the_summary_is_that_connectors_own()
    {
        var browser = Browse("aibom-scan.multi-connector.json");
        browser.SetConnectorLines(InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.multi-connector.json"), null));

        browser.SetConnector("CODEX");

        // The TUI adds the wire name only where it differs from the friendly one ("Claude Code (claudecode)", but plain "Codex").
        Assert.Equal("Codex", Value(browser, "Source"));
        Assert.Equal(@"C:\Users\example\.codex", Value(browser, "Home"));
        Assert.Equal("0", Value(browser, "Skills"));
        // One connector is not a list of them.
        Assert.DoesNotContain(Summary(browser), r => r.Label is "claudecode" or "codex");
        Assert.StartsWith("codex · 0 items", browser.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_the_scan_was_not_asked_for_is_not_collected_and_not_none()
    {
        // 0.8.10 prints zeros for a category --only left out; the page knows what it asked for.
        var browser = Browse("aibom-scan.openclaw.synthetic.json", InventoryBomKind.Skills, InventoryBomKind.Plugins);

        Assert.Equal("2 (1 eligible)", Value(browser, "Skills"));
        Assert.Equal("not collected", Value(browser, "MCPs"));
        Assert.Equal("not collected", Value(browser, "Agents"));
        Assert.Equal("not collected", Value(browser, "Models"));
        Assert.Null(browser.AgentsCount);
        Assert.NotNull(browser.SkillsCount);

        browser.ActiveTab = "agents";
        Assert.True(browser.ShowEmpty);
        Assert.Equal("Agents were not collected", browser.EmptyTitle);
        Assert.Contains("limited to skills, plugins", browser.EmptyDetail, StringComparison.Ordinal);
        Assert.Empty(browser.Rows);

        browser.ActiveTab = "summary";
        Assert.Contains(browser.DetailFields, f => f.Key == "Scope" && f.Value.Contains("skills, plugins", StringComparison.Ordinal));
    }

    [Fact]
    public void A_runtime_that_marks_a_category_not_collected_is_believed_without_being_asked()
    {
        var browser = Browse("aibom-scan.95159fd.only.synthetic.json");

        Assert.Equal("not collected", Value(browser, "MCPs"));
        Assert.Equal("1 (1 eligible)", Value(browser, "Skills"));
        Assert.Null(browser.ModelsCount);
        browser.ActiveTab = "memory";
        Assert.Equal("Memory were not collected", browser.EmptyTitle);
    }

    [Fact]
    public void A_newer_runtimes_extra_summary_rows_appear_by_presence_and_0_8_10_gets_none()
    {
        var pinned = Browse("aibom-scan.95159fd.synthetic.json");
        var old = Browse("aibom-scan.openclaw.synthetic.json");

        // Rules are counted, and the verdict the runtime adds ('discovery-only') is tallied after the TUI's six.
        Assert.Equal("1", Value(pinned, "Rules"));
        Assert.Equal("1 discovery-only", Value(pinned, "Skill policy verdicts"));
        Assert.Equal("2 unscanned", Value(pinned, "Plugin policy verdicts"));
        Assert.Equal("4", Value(pinned, "AIBOM version"));
        Assert.DoesNotContain(Summary(old), r => r.Label == "Rules");
    }

    [Fact]
    public void The_summary_detail_names_what_a_connector_could_not_do_and_what_the_scan_skipped()
    {
        var browser = Browse("aibom-scan.partial.synthetic.json");

        Assert.Equal("Coverage notes", browser.DetailHeading);
        Assert.True(browser.HasDetail);
        var notes = browser.DetailFields.Select(f => f.Key + " | " + f.Value).ToArray();

        // Skipped connectors, each named with the reason.
        Assert.Contains("Skipped | entry 2: it is text, not an inventory object", notes);
        Assert.Contains("Skipped | entry 3: it is not an inventory (no connector, summary or category list)", notes);
        Assert.Contains("Skipped | entry 4: it is null, not an inventory object", notes);

        // The failed commands of the connector that was kept, and what a connector cannot inventory.
        Assert.Contains(notes, n => n.StartsWith("codex: command failed | codex:mcp - could not read ", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith("codex: command failed | codex:plugins - ambiguous plugin identity", StringComparison.Ordinal));
        Assert.Contains("claudecode: Not supported | tools - tool registry is owned by each plugin's manifest", notes);
        Assert.DoesNotContain(notes, n => n.StartsWith("Nothing to report", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_scan_has_nothing_to_report_and_an_unverified_note_is_partly_checked()
    {
        var clean = Browse("aibom-scan.openclaw.synthetic.json");
        Assert.Equal(("Nothing to report", "Every connector was read and every category was inventoried."), (clean.DetailFields[0].Key, clean.DetailFields[0].Value));

        var pinned = Browse("aibom-scan.95159fd.synthetic.json");
        Assert.Contains(pinned.DetailFields, f => f.Key == "hermes: Partly checked" && f.Value.StartsWith("skills - ", StringComparison.Ordinal));
        Assert.Contains(pinned.DetailFields, f => f.Key == "hermes: Not supported" && f.Value.StartsWith("tools - ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ several connectors

    [Fact]
    public void Several_connectors_put_a_connector_column_in_front_of_every_table_and_the_scope_narrows_the_rows()
    {
        var browser = Browse("aibom-scan.95159fd.synthetic.json");
        browser.ActiveTab = "agents";

        Assert.True(browser.ShowConnectorColumn);
        Assert.Equal(new[] { "Connector", "ID", "Source", "Model", "Workspace", "Default" }, Columns(browser));
        Assert.Equal(new[] { "opencode", "opencode" }, browser.Rows.Select(r => r.Cells[0]).ToArray());
        Assert.Equal(new[] { "reviewer", "build" }, browser.Rows.Select(r => r.Cells[1]).ToArray());

        // The tab counts follow the scope too: the TUI's badges match the visible rows.
        browser.ActiveTab = "plugins";
        Assert.Equal(new[] { "hermes", "hermes" }, browser.Rows.Select(r => r.Cells[0]).ToArray());
        Assert.Equal(2, browser.PluginsCount);
        browser.SetConnector("opencode");
        Assert.Equal(0, browser.PluginsCount);
        Assert.Empty(browser.Rows);
        browser.ActiveTab = "agents";
        Assert.Equal(2, browser.AgentsCount);
        browser.SetConnector(null);
        Assert.Equal(2, browser.AgentsCount);
    }

    [Fact]
    public void One_connector_has_no_connector_column_even_when_scoped_down_from_several()
    {
        var one = Browse("aibom-scan.openclaw.synthetic.json");
        one.ActiveTab = "agents";
        Assert.False(one.ShowConnectorColumn);
        Assert.Equal("ID", one.Columns[0].Header);

        // The TUI keeps the column for a merged scan whatever the filter says.
        var several = Browse("aibom-scan.multi-connector.json");
        several.SetConnector("claudecode");
        several.ActiveTab = "skills";
        Assert.True(several.ShowConnectorColumn);
        Assert.Equal(new[] { "claudecode" }, several.Rows.Select(r => r.Cells[0]).ToArray());
    }

    [Fact]
    public void A_scope_the_scan_does_not_include_says_so_and_a_return_to_all_brings_the_rows_back()
    {
        var browser = Browse("aibom-scan.claudecode.json");
        browser.ActiveTab = "skills";

        browser.SetConnector("codex");

        Assert.True(browser.ShowEmpty);
        Assert.Equal("No inventory for codex", browser.EmptyTitle);
        Assert.Empty(browser.Rows);
        Assert.Equal("No inventory for this connector", browser.Caption);
        Assert.Null(browser.SkillsCount);

        browser.SetConnector(null);
        Assert.False(browser.ShowEmpty);
        Assert.Equal(2, browser.Rows.Count);
    }

    // ------------------------------------------------------------------ empty tabs

    [Fact]
    public void An_empty_tab_says_none_found_and_gives_the_connectors_own_reason_when_it_has_one()
    {
        var browser = Browse("aibom-scan.multi-connector.json");

        browser.ActiveTab = "agents";
        Assert.True(browser.ShowEmpty);
        Assert.Equal("No agents found", browser.EmptyTitle);
        Assert.Equal("claudecode: agents are not a first-class concept on this connector", browser.EmptyDetail);

        browser.SetConnector("codex");
        Assert.Equal("No agents found", browser.EmptyTitle);
        Assert.Equal(string.Empty, browser.EmptyDetail);

        browser.ActiveTab = "plugins";
        Assert.Equal("No plugins found", browser.EmptyTitle);
    }

    // ------------------------------------------------------------------ status chips and search

    private const string Statuses = """
        {"connector": "x",
         "skills": [
           {"id": "ok", "eligible": true, "policy_verdict": "clean"},
           {"id": "warn", "eligible": false, "policy_verdict": "warning"},
           {"id": "bad", "eligible": true, "policy_verdict": "blocked"},
           {"id": "new"}],
         "plugins": [
           {"id": "p-loaded", "status": "loaded", "policy_verdict": "clean"},
           {"id": "p-off", "status": "disabled"},
           {"id": "p-bad", "status": "loaded", "policy_verdict": "blocked"}]}
        """;

    [Theory]
    [InlineData("skills", "all", new[] { "ok", "warn", "bad", "new" })]
    [InlineData("skills", "eligible", new[] { "ok", "bad" })]
    [InlineData("skills", "warning", new[] { "warn" })]
    [InlineData("skills", "blocked", new[] { "bad" })]
    [InlineData("plugins", "all", new[] { "p-loaded", "p-off", "p-bad" })]
    [InlineData("plugins", "loaded", new[] { "p-loaded", "p-bad" })]
    [InlineData("plugins", "disabled", new[] { "p-off" })]
    [InlineData("plugins", "blocked", new[] { "p-bad" })]
    public void The_skills_and_plugins_status_chips_narrow_the_list_as_the_tuis_filters_do(string tab, string chip, string[] expected)
    {
        var browser = BrowseText(Statuses);
        browser.ActiveTab = tab;
        Assert.True(browser.HasStatusFilter);

        browser.StatusFilter = chip;

        Assert.Equal(expected, Ids(browser));
        // The tab's badge is the connector's rows, not the filtered ones.
        Assert.Equal(tab == "skills" ? 4 : 3, tab == "skills" ? browser.SkillsCount : browser.PluginsCount);
    }

    [Fact]
    public void Only_skills_and_plugins_have_status_chips_and_a_chip_that_hides_everything_says_so()
    {
        var browser = BrowseText(Statuses);

        browser.ActiveTab = "mcp";
        Assert.False(browser.HasStatusFilter);
        browser.ActiveTab = "skills";
        browser.StatusFilter = "warning";
        browser.SearchText = "ok";

        Assert.Empty(browser.Rows);
        Assert.Equal("Nothing matches “ok”", browser.EmptyTitle);

        browser.SearchText = string.Empty;
        browser.StatusFilter = "loaded";
        Assert.Equal("No items match the current filter", browser.EmptyTitle);
        Assert.Equal("Choose All to list every row.", browser.EmptyDetail);
    }

    [Fact]
    public void Search_matches_any_column_the_id_and_the_connector_and_clears()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "agents";

        browser.SearchText = "model-small";
        Assert.Equal(new[] { "helper" }, Ids(browser));
        Assert.Equal("1 shown", browser.ResultCaption);

        browser.SearchText = "  WORKSPACE-HELPER ";
        Assert.Equal(new[] { "helper" }, Ids(browser));

        // Any column: both workspaces are under .openclaw, the defaults row has none.
        browser.SearchText = "openclaw";
        Assert.Equal(new[] { "main", "helper" }, Ids(browser));

        browser.SearchText = string.Empty;
        Assert.Equal(3, browser.Rows.Count);
    }

    [Fact]
    public void Search_matches_the_connector_when_the_scan_has_a_connector_column()
    {
        var browser = Browse("aibom-scan.95159fd.synthetic.json");
        browser.ActiveTab = "plugins";

        browser.SearchText = "hermes";
        Assert.Equal(2, browser.Rows.Count);
        browser.SearchText = "opencode";
        Assert.Empty(browser.Rows);
    }

    [Fact]
    public void A_selection_that_a_filter_hides_is_cleared_and_one_that_survives_is_kept()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "agents";
        browser.SelectedRow = browser.Rows[1];

        browser.SearchText = "helper";
        Assert.Equal("AGENT: helper", browser.DetailHeading);
        Assert.NotNull(browser.SelectedRow);

        browser.SearchText = "main";
        Assert.Null(browser.SelectedRow);
        Assert.False(browser.HasSelection);
    }

    // ------------------------------------------------------------------ newer runtime, by presence

    [Fact]
    public void A_pinned_runtimes_tools_show_their_kind_and_description_and_nothing_is_added_for_0_8_10()
    {
        var browser = Browse("aibom-scan.95159fd.synthetic.json");
        browser.ActiveTab = "tools";

        Assert.Equal(new[] { "Connector", "ID", "Name", "Kind", "Source" }, Columns(browser));
        Assert.Equal(new[] { "lint", "deploy" }, browser.Rows.Select(r => r.Cells[1]).ToArray());
        browser.SelectedRow = browser.Rows[1];
        Assert.Equal("TOOL: deploy", browser.DetailHeading);
        Assert.Equal(new[] { "ID", "Kind", "Source", "Description" }, Labels(browser.DetailFields));
        Assert.Equal("Deploy the app", Detail(browser, "Description"));

        // A plugin the newer runtime lists by source kind and enabled flag.
        browser.ActiveTab = "plugins";
        Assert.Equal(new[] { "hermes", "notes", "1.2.0", "user", "enabled", "", "0", "" }, browser.Rows[0].Cells);
        browser.SelectedRow = browser.Rows[0];
        Assert.Contains(browser.DetailMore, f => f.Key == "Kind" && f.Value == "standalone");

        // A skill the runtime calls discovery-only gets a quiet pill, and says why it is not scanned.
        browser.ActiveTab = "skills";
        Assert.Equal("discovery-only", browser.Rows[0].Verdict);
        Assert.Equal("Neutral", browser.Rows[0].VerdictTone);
    }

    // ------------------------------------------------------------------ the verdict pill

    [Theory]
    [InlineData("blocked", "Bad")]
    [InlineData("rejected", "Bad")]
    [InlineData("warning", "Warn")]
    [InlineData("clean", "Ok")]
    [InlineData("allowed", "Ok")]
    [InlineData("unscanned", "Neutral")]
    [InlineData("discovery-only", "Neutral")]
    [InlineData("something-new", "Neutral")]
    public void A_verdict_is_a_pill_in_the_tone_of_its_meaning(string verdict, string tone)
    {
        var row = new InventoryBomRowItem(new[] { "x" }, null, verdict);

        Assert.True(row.HasVerdict);
        Assert.Equal(tone, row.VerdictTone);
        Assert.False(new InventoryBomRowItem(new[] { "x" }, null).HasVerdict);
    }

    // ------------------------------------------------------------------ the --only scope chips

    [Fact]
    public void Every_category_is_on_until_some_are_turned_off_and_all_on_is_no_only_argument()
    {
        var browser = new InventoryBomBrowser();

        Assert.Equal(7, browser.ScopeChips.Count);
        Assert.All(browser.ScopeChips, chip => Assert.True(chip.IsActive));
        Assert.Equal(new[] { "skills", "plugins", "mcp", "agents", "tools", "models", "memory" }, browser.ScopeChips.Select(c => c.Label).ToArray());
        Assert.Equal(string.Empty, browser.OnlyArgument);
        Assert.Equal("Scope (all)", browser.ScopeLabel);
        Assert.Equal(InventoryBomKinds.All, browser.ScopeKinds);
    }

    [Fact]
    public void Turning_a_chip_off_leaves_it_out_of_the_only_argument_in_tab_order()
    {
        var browser = new InventoryBomBrowser();

        browser.ScopeChips.Single(c => c.Kind == InventoryBomKind.Tools).IsActive = false;
        browser.ScopeChips.Single(c => c.Kind == InventoryBomKind.Memory).IsActive = false;

        Assert.Equal("skills,plugins,mcp,agents,models", browser.OnlyArgument);
        Assert.Equal("Scope", browser.ScopeLabel);
        Assert.Equal(5, browser.ScopeKinds.Count);

        browser.ScopeChips.Single(c => c.Kind == InventoryBomKind.Tools).IsActive = true;
        browser.ScopeChips.Single(c => c.Kind == InventoryBomKind.Memory).IsActive = true;
        Assert.Equal(string.Empty, browser.OnlyArgument);
        Assert.Equal("Scope (all)", browser.ScopeLabel);
    }

    [Fact]
    public void Fast_is_skills_plugins_and_mcp_and_all_puts_every_category_back()
    {
        var browser = new InventoryBomBrowser();
        var changes = new List<string?>();
        browser.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        browser.ShowFastScopeCommand.Execute(null);

        Assert.Equal("skills,plugins,mcp", browser.OnlyArgument);
        Assert.Equal("Scope (fast)", browser.ScopeLabel);
        Assert.Equal(new[] { true, true, true, false, false, false, false }, browser.ScopeChips.Select(c => c.IsActive).ToArray());
        Assert.Contains(nameof(InventoryBomBrowser.OnlyArgument), changes);
        Assert.Contains(nameof(InventoryBomBrowser.ScopeLabel), changes);

        browser.ShowAllScopeCommand.Execute(null);
        Assert.Equal(string.Empty, browser.OnlyArgument);
        Assert.Equal("Scope (all)", browser.ScopeLabel);
    }

    [Fact]
    public void A_scan_of_nothing_is_not_a_scan_so_turning_off_the_last_category_turns_them_all_on()
    {
        var browser = new InventoryBomBrowser();
        browser.SetCategories(new[] { InventoryBomKind.Agents });
        Assert.Equal("agents", browser.OnlyArgument);

        browser.ScopeChips.Single(c => c.Kind == InventoryBomKind.Agents).IsActive = false;

        Assert.All(browser.ScopeChips, chip => Assert.True(chip.IsActive));
        Assert.Equal(string.Empty, browser.OnlyArgument);
    }

    [Fact]
    public void A_chip_says_what_it_does_to_a_screen_reader()
    {
        var chips = new InventoryBomBrowser().ScopeChips;

        Assert.Equal("Scan skills", chips[0].AutomationName);
        Assert.Equal("Scan MCP servers", chips[2].AutomationName);
        Assert.Equal("models", chips[5].Label);
        Assert.Equal("Scan model providers", chips[5].AutomationName);
    }

    // ------------------------------------------------------------------ the bounds

    [Fact]
    public void Rows_left_out_by_the_bound_are_counted_in_the_caption_the_tab_and_the_notes()
    {
        var total = InventoryBomSnapshot.MaxRowsPerCategory + 25;
        var rows = string.Join(',', Enumerable.Range(1, total).Select(i => $"{{\"id\": \"skill-{i}\"}}"));
        var browser = BrowseText($"{{\"connector\": \"claudecode\", \"skills\": [{rows}], \"summary\": {{\"total_items\": {total}, \"skills\": {{\"count\": {total}}}}}}}");

        Assert.Equal(total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), browser.Caption.Split(' ')[2]);
        Assert.Contains(browser.DetailFields, f => f.Key == "claudecode: Skills" && f.Value.StartsWith("showing the first ", StringComparison.Ordinal));
        Assert.Equal(total.ToString(System.Globalization.CultureInfo.InvariantCulture), Value(browser, "Skills"));

        browser.ActiveTab = "skills";
        Assert.Equal(InventoryBomSnapshot.MaxRowsPerCategory, browser.Rows.Count);
        Assert.Contains("25 more not kept", browser.ResultCaption, StringComparison.Ordinal);
        Assert.Equal(InventoryBomSnapshot.MaxRowsPerCategory, browser.SkillsCount);
    }

    [Fact]
    public void Clearing_forgets_the_scan_and_loading_another_starts_on_the_summary_again()
    {
        var browser = Browse("aibom-scan.openclaw.synthetic.json");
        browser.ActiveTab = "agents";
        browser.SelectedRow = browser.Rows[0];

        browser.Clear();

        Assert.False(browser.HasSnapshot);
        Assert.Equal("summary", browser.ActiveTab);
        Assert.Null(browser.SelectedRow);
        Assert.Empty(browser.Rows);
        Assert.True(browser.ShowGenerate);

        browser.Load(InventoryBomSnapshot.Parse(PayloadFixtures.Read("aibom-scan.claudecode.json")).Snapshot!, Array.Empty<InventoryBomKind>());
        Assert.Equal("summary", browser.ActiveTab);
        Assert.False(browser.ShowEmpty);
        Assert.Equal("claudecode", browser.Caption.Split(' ')[0]);
    }

    [Fact]
    public void Names_that_could_pass_for_other_text_are_spelled_out_in_the_rows_and_the_detail()
    {
        var browser = BrowseText("{\"connector\": \"x\", \"agents\": [{\"id\": \"evil\\u202Ename\", \"model\": \"m\\u200Bx\"}]}");
        browser.ActiveTab = "agents";

        Assert.Equal("evil\\u202Ename", browser.Rows[0].Cells[0]);
        browser.SelectedRow = browser.Rows[0];
        Assert.Equal("AGENT: evil\\u202Ename", browser.DetailHeading);
        Assert.Equal("m\\u200Bx", Detail(browser, "Model"));
    }
}
