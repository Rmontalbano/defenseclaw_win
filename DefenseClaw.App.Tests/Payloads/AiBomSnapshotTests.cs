using System.Text;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// <c>defenseclaw aibom scan --json</c> as the Inventory page keeps it (<see cref="InventoryBomSnapshot"/>): every kind's rows with the cells and
/// detail fields of the TUI's Inventory panel (<c>tui/services/inventory_state.py</c> in the 0.8.10 wheel, <c>from_mapping</c>, <c>data_table_rows</c> and
/// <c>detail_info</c>), one connector's summary, and the bounds. Nothing here ran the command (a run writes an audit record): the fixtures are
/// synthetic, written from the code that prints the output (<c>claw_inventory.py</c> and <c>cmd_aibom.py</c> of 0.8.10; of source commit 95159fd for the
/// <c>95159fd</c> ones), with made-up names, paths under <c>C:\Users\example</c> and <c>*.example.test</c> hosts.
/// </summary>
public sealed class AiBomSnapshotTests
{
    private static InventoryBomSnapshot Read(string fixture, string? requested = null)
    {
        var result = InventoryBomSnapshot.Parse(PayloadFixtures.Read(fixture), requested);
        Assert.True(result.Succeeded, result.Message);
        return result.Snapshot!;
    }

    private static InventoryBomSnapshot ReadText(string json)
    {
        var result = InventoryBomSnapshot.Parse(json);
        Assert.True(result.Succeeded, result.Message);
        return result.Snapshot!;
    }

    private static InventoryBomEntity Row(InventoryBomConnector connector, InventoryBomKind kind, string id) =>
        connector.Entities(kind).Single(e => e.Id == id);

    private static IEnumerable<string> Labels(InventoryBomEntity entity) => entity.Fields.Select(f => f.Label);

    private static string Field(InventoryBomEntity entity, string label) => entity.Fields.Single(f => f.Label == label).Value;

    private static string More(InventoryBomEntity entity, string label) => entity.More.Single(f => f.Label == label).Value;

    // ------------------------------------------------------------------ the shape

    [Fact]
    public void A_bare_object_is_one_connector_and_every_kind_has_its_rows()
    {
        var snapshot = Read("aibom-scan.openclaw.synthetic.json");

        var connector = Assert.Single(snapshot.Connectors);
        Assert.Empty(snapshot.Skipped);
        Assert.False(snapshot.HasSeveralConnectors);
        Assert.Equal("openclaw", connector.Name);
        Assert.Equal(
            new[] { 2, 2, 2, 3, 1, 5, 1 },
            InventoryBomKinds.All.Select(kind => connector.Entities(kind).Count).ToArray());
        Assert.All(InventoryBomKinds.All, kind => Assert.All(connector.Entities(kind), row => Assert.Equal("openclaw", row.Connector)));
    }

    [Fact]
    public void A_list_is_one_connector_per_entry_in_the_order_printed_and_an_empty_list_is_no_connector()
    {
        var several = Read("aibom-scan.multi-connector.json");
        Assert.Equal(new[] { "claudecode", "codex" }, several.Connectors.Select(c => c.Name).ToArray());
        Assert.True(several.HasSeveralConnectors);
        Assert.Same(several.Connectors[1], several.Find(" CODEX "));
        Assert.Null(several.Find("nope"));
        Assert.Null(several.Find(null));

        // '[]' is what a scan of no connector prints: nothing was inventoried, and nothing failed either.
        var none = Read("aibom-scan.no-connectors.json");
        Assert.Empty(none.Connectors);
        Assert.Empty(none.Skipped);
    }

    [Fact]
    public void The_json_may_follow_a_banner_line()
    {
        var snapshot = ReadText("Scanning claudecode inventory …\n" + PayloadFixtures.Read("aibom-scan.claudecode.json"));

        Assert.Equal("claudecode", Assert.Single(snapshot.Connectors).Name);
    }

    [Fact]
    public void An_object_with_no_connector_name_takes_the_one_that_was_requested_and_otherwise_is_numbered()
    {
        const string json = """{"skills": [{"id": "a"}], "summary": {"skills": {"count": 1}}}""";

        Assert.Equal("claudecode", Assert.Single(InventoryBomSnapshot.Parse(json, "claudecode").Snapshot!.Connectors).Name);
        Assert.Equal("connector 1", Assert.Single(InventoryBomSnapshot.Parse(json).Snapshot!.Connectors).Name);
    }

    // ------------------------------------------------------------------ the connector's summary (the Summary tab)

    [Fact]
    public void The_summary_carries_what_the_tuis_summary_tab_shows()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal("3", connector.Version);
        Assert.Equal("2030-01-15T10:00:05.123456+00:00", connector.GeneratedAt);
        Assert.Equal(@"C:\Users\example\.openclaw", connector.Home);
        Assert.Equal(@"C:\Users\example\.openclaw\openclaw.json", connector.Config);
        Assert.Equal(16, connector.TotalItems);

        Assert.Equal(new InventoryBomCount(2, Eligible: 1), connector.Count(InventoryBomKind.Skills));
        Assert.Equal(new InventoryBomCount(2, Loaded: 1, Disabled: 1), connector.Count(InventoryBomKind.Plugins));
        Assert.Equal(5, connector.Count(InventoryBomKind.Models)!.Count);
        Assert.Equal(0, connector.ErrorCount);
        Assert.Equal(0, connector.LimitationCount);

        // policy_skills / scan_skills, and the two the TUI leaves out (policy_mcp / scan_mcp).
        Assert.Equal(1, connector.Verdicts(InventoryBomKind.Skills)["warning"]);
        Assert.Equal(1, connector.Verdicts(InventoryBomKind.Skills)["clean"]);
        Assert.Equal(2, connector.Verdicts(InventoryBomKind.Plugins)["unscanned"]);
        Assert.Equal(1, connector.Verdicts(InventoryBomKind.Mcp)["clean"]);
        Assert.Equal(2, connector.Coverage(InventoryBomKind.Skills)["scanned"]);
        Assert.Equal(2, connector.Coverage(InventoryBomKind.Skills)["total_findings"]);
        Assert.Empty(connector.Verdicts(InventoryBomKind.Agents));
    }

    [Fact]
    public void The_config_is_the_first_config_file_and_the_home_falls_back_to_the_claw_home()
    {
        const string json = """
            {"connector": "x", "claw_home": "C:\\h", "openclaw_config": "C:\\old.json",
             "connector_config_files": ["C:\\new.json", "C:\\second.json"]}
            """;
        const string legacy = """{"connector": "x", "claw_home": "C:\\h", "openclaw_config": "C:\\old.json"}""";

        var connector = ReadText(json).Connectors[0];
        Assert.Equal(@"C:\new.json", connector.Config);
        Assert.Equal(@"C:\h", connector.Home);
        Assert.Equal(@"C:\old.json", ReadText(legacy).Connectors[0].Config);
    }

    [Fact]
    public void A_connector_without_a_summary_is_counted_from_its_lists()
    {
        const string json = """{"connector": "x", "skills": [{"id": "a"}, {"id": "b"}], "mcp": [{"id": "c"}], "errors": [{"command": "x:y", "error": "boom"}]}""";

        var connector = ReadText(json).Connectors[0];

        Assert.Equal(2, connector.Count(InventoryBomKind.Skills)!.Count);
        Assert.Equal(1, connector.Count(InventoryBomKind.Mcp)!.Count);
        Assert.Null(connector.Count(InventoryBomKind.Agents));
        Assert.Equal(3, connector.TotalItems);
        Assert.Equal(1, connector.ErrorCount);
    }

    // ------------------------------------------------------------------ Agents, Models, Memory: the TUI's cells and detail fields

    [Fact]
    public void Agents_have_the_tuis_columns_and_detail_fields()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Source", "Model", "Workspace", "Default" }, InventoryBomKinds.Columns(InventoryBomKind.Agents));
        var main = Row(connector, InventoryBomKind.Agents, "main");
        Assert.Equal(
            new[] { "main", "", "example/model-large", @"C:\Users\example\.openclaw\workspace", "yes" },
            main.Cells);
        Assert.Equal("AGENT: main", main.Title);
        Assert.Equal(new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" }, Labels(main));
        Assert.Equal("example/model-large", Field(main, "Model"));
        Assert.Equal("true", Field(main, "Default"));
        Assert.Equal("0", Field(main, "Max Concurrent"));

        var helper = Row(connector, InventoryBomKind.Agents, "helper");
        Assert.Equal("no", helper.Cells[4]);
        Assert.Equal("false", Field(helper, "Default"));

        // The defaults row: its source, the subagent ceiling the TUI shows as Max Concurrent.
        var defaults = Row(connector, InventoryBomKind.Agents, "_defaults");
        Assert.Equal("agents.defaults", Field(defaults, "Source"));
        Assert.Equal("4", Field(defaults, "Max Concurrent"));
    }

    [Fact]
    public void Models_have_the_tuis_columns_and_detail_fields_and_list_fallbacks_and_allowed_only_when_there_are_some()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Source", "Default Model", "Status" }, InventoryBomKinds.Columns(InventoryBomKind.Models));
        var config = Row(connector, InventoryBomKind.Models, "_config");
        Assert.Equal(new[] { "_config", "models status", "example/model-large", "" }, config.Cells);
        Assert.Equal("MODEL: _config", config.Title);
        Assert.Equal(new[] { "Source", "Default Model", "Status", "Config", "Fallbacks", "Allowed" }, Labels(config));
        Assert.Equal("example/model-small", Field(config, "Fallbacks"));
        Assert.Equal("example/model-large, example/model-small", Field(config, "Allowed"));
        Assert.Equal(@"C:\Users\example\.openclaw\openclaw.json", Field(config, "Config"));

        var auth = Row(connector, InventoryBomKind.Models, "example-alt");
        Assert.Equal(new[] { "example-alt", "auth", "", "missing" }, auth.Cells);
        Assert.Equal(new[] { "Source", "Default Model", "Status", "Config" }, Labels(auth));
    }

    [Fact]
    public void Memory_has_the_tuis_columns_and_detail_fields()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Backend", "Provider", "Files", "Chunks", "Workspace" }, InventoryBomKinds.Columns(InventoryBomKind.Memory));
        var main = Row(connector, InventoryBomKind.Memory, "main");
        Assert.Equal(
            new[] { "main", "sqlite", "example-embed", "12", "340", @"C:\Users\example\.openclaw\workspace" },
            main.Cells);
        Assert.Equal("MEMORY: main", main.Title);
        Assert.Equal(
            new[] { "Backend", "Provider", "Workspace", "DB Path", "Files", "Chunks", "FTS Available", "Vector Enabled", "Sources" },
            Labels(main));
        Assert.Equal(@"C:\Users\example\.openclaw\memory\main.sqlite", Field(main, "DB Path"));
        Assert.Equal("true", Field(main, "FTS Available"));
        Assert.Equal("false", Field(main, "Vector Enabled"));
        Assert.Equal("memory, sessions", Field(main, "Sources"));
    }

    // ------------------------------------------------------------------ Skills, Plugins, MCPs, Tools

    [Fact]
    public void Skills_put_the_description_first_and_filter_on_eligibility_and_verdict()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Verdict", "Enabled", "Severity", "Findings", "Source" }, InventoryBomKinds.Columns(InventoryBomKind.Skills));
        var weather = Row(connector, InventoryBomKind.Skills, "weather");
        Assert.Equal(new[] { "weather", "clean", "yes", "CLEAN", "0", "openclaw-bundled" }, weather.Cells);
        Assert.Equal("SKILL: weather", weather.Title);
        Assert.Equal(
            new[] { "Description", "Source", "Eligible", "Enabled", "Bundled", "Verdict", "Detail", "Scan Findings", "Scan Severity", "Scan Target" },
            Labels(weather));
        Assert.Equal("Get the current weather", Field(weather, "Description"));
        Assert.True(weather.Eligible);
        Assert.Equal("clean", weather.Verdict);

        // A skill with no description has no Description field at all.
        var issues = Row(connector, InventoryBomKind.Skills, "gh-issues");
        Assert.Equal("Source", issues.Fields[0].Label);
        Assert.False(issues.Eligible);
        Assert.Equal("warning", issues.Verdict);
        Assert.Equal("2", Field(issues, "Scan Findings"));
        Assert.Equal("MEDIUM", Field(issues, "Scan Severity"));
    }

    [Fact]
    public void Plugins_are_named_by_their_name_or_else_their_id()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "Name", "Version", "Origin", "Status", "Verdict", "Findings", "Severity" }, InventoryBomKinds.Columns(InventoryBomKind.Plugins));
        var search = Row(connector, InventoryBomKind.Plugins, "example-search");
        Assert.Equal(new[] { "Example Search", "2.1.0", "bundled", "loaded", "unscanned", "0", "" }, search.Cells);
        Assert.Equal("PLUGIN: Example Search", search.Title);
        Assert.Equal(
            new[] { "ID", "Version", "Origin", "Status", "Enabled", "Verdict", "Detail", "Scan Findings", "Scan Severity", "Scan Target" },
            Labels(search));
        Assert.Equal("example-search", Field(search, "ID"));
        Assert.Equal("loaded", search.Status);

        var voice = Row(connector, InventoryBomKind.Plugins, "example-voice");
        Assert.Equal("example-voice", voice.Cells[0]);
        Assert.Equal("PLUGIN: example-voice", voice.Title);
        Assert.Equal("false", Field(voice, "Enabled"));
        Assert.Equal("disabled", voice.Status);
    }

    [Fact]
    public void An_mcp_server_shows_its_command_or_else_its_url()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Source", "Transport", "Command/URL" }, InventoryBomKinds.Columns(InventoryBomKind.Mcp));
        var docs = Row(connector, InventoryBomKind.Mcp, "docs");
        Assert.Equal(new[] { "docs", "openclaw mcp list", "", "npx" }, docs.Cells);
        Assert.Equal("MCP: docs", docs.Title);
        Assert.Equal(new[] { "Source", "Transport", "Command", "URL" }, Labels(docs));

        var remote = Row(connector, InventoryBomKind.Mcp, "remote");
        Assert.Equal(new[] { "remote", "openclaw mcp list", "sse", "https://mcp.example.test/v1" }, remote.Cells);
        Assert.Equal("", Field(remote, "Command"));
        Assert.Equal("https://mcp.example.test/v1", Field(remote, "URL"));
    }

    [Fact]
    public void A_tool_has_the_newer_tuis_columns_whatever_runtime_listed_it()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        Assert.Equal(new[] { "ID", "Name", "Kind", "Source" }, InventoryBomKinds.Columns(InventoryBomKind.Tools));
        var search = Row(connector, InventoryBomKind.Tools, "web_search");
        Assert.Equal(new[] { "web_search", "web_search", "", "plugin:example-search" }, search.Cells);
        Assert.Equal("TOOL: web_search", search.Title);
        Assert.Equal(new[] { "ID", "Kind", "Source", "Description" }, Labels(search));
    }

    // ------------------------------------------------------------------ extra members, by presence

    [Fact]
    public void A_row_shows_the_members_the_tui_leaves_out_and_only_those_it_has()
    {
        var connector = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];

        // 0.8.10's OpenClaw rows: members the TUI's detail does not list.
        Assert.Equal("W", More(Row(connector, InventoryBomKind.Skills, "weather"), "Emoji"));
        var issues = Row(connector, InventoryBomKind.Skills, "gh-issues");
        Assert.Equal("gh", More(issues, "Missing Bins"));
        Assert.Equal("EXAMPLE_GITHUB_TOKEN", More(issues, "Missing Env"));
        Assert.Equal("web_search", More(Row(connector, InventoryBomKind.Plugins, "example-search"), "Tool Names"));
        Assert.Equal("2", More(Row(connector, InventoryBomKind.Agents, "main"), "Bindings"));
        Assert.Equal("example/model-small", More(Row(connector, InventoryBomKind.Agents, "_defaults"), "Fallbacks"));
        var model = Row(connector, InventoryBomKind.Models, "example/model-large");
        Assert.Equal("Example Large", More(model, "Name"));
        Assert.Equal("false", More(model, "Local"));
        Assert.Equal("200000", More(model, "Context Window"));

        // An MCP row's own verdict and scan, which the TUI's MCP detail has no field for.
        var docs = Row(connector, InventoryBomKind.Mcp, "docs");
        Assert.Equal("-y @example/docs-mcp", More(docs, "Args"));
        Assert.Equal("EXAMPLE_DOCS_TOKEN", More(docs, "Env Keys"));
        Assert.Equal("clean", More(docs, "Policy Verdict"));

        // Never the provenance stamp, never a member that is already a field, never an empty one.
        Assert.All(InventoryBomKinds.All, kind => Assert.All(connector.Entities(kind), row =>
        {
            Assert.DoesNotContain(row.More, f => f.Label == "Provenance");
            Assert.DoesNotContain(row.More, f => f.Label is "Id" or "Source" or "Eligible" or "Default Model");
            Assert.DoesNotContain(row.More, f => f.Value.Length == 0);
        }));

        // A row with nothing extra has no extras.
        Assert.Empty(Row(connector, InventoryBomKind.Memory, "main").More);
        Assert.Equal("Bindings", Assert.Single(Row(connector, InventoryBomKind.Agents, "helper").More).Label);
    }

    [Fact]
    public void A_filesystem_connectors_own_members_are_shown_where_the_tui_shows_blanks()
    {
        var connector = Read("aibom-scan.claudecode.json").Connectors[0];

        // Claude Code's memory row has no backend, provider or counts; it has a kind, an entry count and a source.
        var memory = Row(connector, InventoryBomKind.Memory, "memory");
        Assert.Equal(new[] { "memory", "", "", "0", "0", "" }, memory.Cells);
        Assert.Equal("filesystem", More(memory, "Kind"));
        Assert.Equal("4", More(memory, "Entry Count"));
        Assert.Equal(@"C:\Users\example\.claude\memory", More(memory, "Source"));

        var provider = Row(connector, InventoryBomKind.Models, "anthropic");
        Assert.Equal("https://api.anthropic.com", More(provider, "Base Url"));
        Assert.Equal("true", More(provider, "Api Key Present"));
        Assert.Equal("env:ANTHROPIC_BASE_URL", Field(provider, "Source"));
    }

    [Fact]
    public void A_member_the_mapping_reads_as_a_number_or_a_list_but_is_not_one_is_not_hidden()
    {
        // The newer runtime's built-in memory row lists its files, where the TUI counts them.
        const string json = """
            {"connector": "hermes", "memory": [{"id": "builtin", "files": ["C:\\m\\MEMORY.md", "C:\\m\\USER.md"], "chunks": "7", "sources": "notes"}]}
            """;

        var memory = ReadText(json).Connectors[0].Entities(InventoryBomKind.Memory).Single();

        Assert.Equal("0", Field(memory, "Files"));
        Assert.Equal(@"C:\m\MEMORY.md, C:\m\USER.md", More(memory, "Files"));
        Assert.Equal("7", Field(memory, "Chunks"));
        Assert.DoesNotContain(memory.More, f => f.Label == "Chunks");
        Assert.Equal("notes", More(memory, "Sources"));
        Assert.DoesNotContain(memory.Fields, f => f.Label == "Sources");
    }

    // ------------------------------------------------------------------ the newer runtime, by presence

    [Fact]
    public void A_pinned_runtimes_tools_and_plugins_show_their_newer_members_and_0_8_10_has_none_of_them()
    {
        var snapshot = Read("aibom-scan.95159fd.synthetic.json");
        var hermes = snapshot.Connectors[0];
        var opencode = snapshot.Connectors[1];

        // A tool's kind and description are its TUI columns and fields.
        var deploy = Row(opencode, InventoryBomKind.Tools, "deploy");
        Assert.Equal(new[] { "deploy", "deploy", "config-command", @"C:\Users\example\proj\opencode.json" }, deploy.Cells);
        Assert.Equal("Deploy the app", Field(deploy, "Description"));
        Assert.Equal("config-command", Field(deploy, "Kind"));

        // A plugin row that has source_kind and enabled, and no origin or status, is shown as 'plugin list' shows it.
        var notes = Row(hermes, InventoryBomKind.Plugins, "user/notes");
        Assert.Equal(new[] { "notes", "1.2.0", "user", "enabled", "", "0", "" }, notes.Cells);
        Assert.Equal("enabled", notes.Status);
        Assert.Equal("disabled", Row(hermes, InventoryBomKind.Plugins, "bundled/search").Cells[3]);
        Assert.Equal("standalone", More(notes, "Kind"));
        Assert.DoesNotContain(notes.More, f => f.Label == "Source Kind");

        // A skill the runtime calls discovery-only, and that it does not scan.
        var helper = Row(hermes, InventoryBomKind.Skills, "notes-helper");
        Assert.Equal("discovery-only", helper.Verdict);
        Assert.Equal("false", More(helper, "Scan Eligible"));

        // Summary: a rule count, and the verdict tallies the newer runtime adds to.
        Assert.Equal(1, hermes.Rules);
        Assert.Equal(0, opencode.Rules);
        Assert.Equal("4", hermes.Version);
        Assert.Equal(1, hermes.Verdicts(InventoryBomKind.Skills)["discovery-only"]);
        Assert.Equal(1, opencode.Verdicts(InventoryBomKind.Mcp)["allowed"]);
        Assert.Equal("on the allow list", More(Row(opencode, InventoryBomKind.Mcp, "docs-search"), "Policy Detail"));

        // 0.8.10 prints none of them: no rule count, no kind on a tool, no collected flag.
        var old = Read("aibom-scan.openclaw.synthetic.json").Connectors[0];
        Assert.Null(old.Rules);
        Assert.Equal(string.Empty, Field(Row(old, InventoryBomKind.Tools, "web_search"), "Kind"));
        Assert.All(InventoryBomKinds.All, kind => Assert.True(old.Count(kind)!.Collected));
    }

    [Fact]
    public void A_category_an_only_run_did_not_collect_says_so_where_the_runtime_marks_it()
    {
        var connector = Read("aibom-scan.95159fd.only.synthetic.json").Connectors[0];

        Assert.True(connector.Count(InventoryBomKind.Skills)!.Collected);
        Assert.True(connector.Count(InventoryBomKind.Plugins)!.Collected);
        foreach (var kind in new[] { InventoryBomKind.Mcp, InventoryBomKind.Agents, InventoryBomKind.Tools, InventoryBomKind.Models, InventoryBomKind.Memory })
        {
            Assert.False(connector.Count(kind)!.Collected, kind.ToString());
            Assert.Empty(connector.Entities(kind));
        }
    }

    // ------------------------------------------------------------------ what a connector could not do

    [Fact]
    public void Unsupported_capabilities_are_kept_with_their_reasons_and_are_not_errors()
    {
        var connector = Read("aibom-scan.multi-connector.json").Connectors[0];

        Assert.Equal(4, connector.LimitationCount);
        Assert.Equal(0, connector.ErrorCount);
        var agents = connector.Limitations.Single(l => l.Category == "agents");
        Assert.Equal(("claudecode", "unsupported", "agents are not a first-class concept on this connector"), (agents.Connector, agents.Status, agents.Reason));
    }

    [Fact]
    public void A_connector_whose_commands_failed_is_kept_and_each_failure_is_named()
    {
        var snapshot = Read("aibom-scan.partial.synthetic.json");
        var codex = snapshot.Find("codex")!;

        Assert.Equal(2, codex.ErrorCount);
        Assert.Equal("codex:mcp", codex.Errors[0].Command);
        Assert.Contains("permission denied", codex.Errors[0].Message, StringComparison.Ordinal);
        Assert.Equal("codex:plugins", codex.Errors[1].Command);
        Assert.Equal("summarize", Assert.Single(codex.Entities(InventoryBomKind.Skills)).Id);
    }

    [Fact]
    public void A_connector_that_cannot_be_used_is_skipped_and_named_and_the_others_are_kept()
    {
        var snapshot = Read("aibom-scan.partial.synthetic.json");

        Assert.Equal(new[] { "claudecode", "codex" }, snapshot.Connectors.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "entry 2", "entry 3", "entry 4" }, snapshot.Skipped.Select(s => s.Name).ToArray());
        Assert.Equal("it is text, not an inventory object", snapshot.Skipped[0].Reason);
        Assert.Equal("it is not an inventory (no connector, summary or category list)", snapshot.Skipped[1].Reason);
        Assert.Equal("it is null, not an inventory object", snapshot.Skipped[2].Reason);
    }

    [Fact]
    public void A_skipped_entry_that_names_its_connector_is_named_by_it()
    {
        // An inventory-looking object whose connector member is there but whose rows are not an array is still readable (no rows); one that is
        // not an object at all is not.
        var snapshot = ReadText("""[{"connector": "ghost", "skills": "none"}, 7]""");

        Assert.Equal("ghost", Assert.Single(snapshot.Connectors).Name);
        Assert.Empty(snapshot.Connectors[0].Entities(InventoryBomKind.Skills));
        Assert.Equal("entry 2", Assert.Single(snapshot.Skipped).Name);
    }

    [Fact]
    public void Only_the_first_skipped_connectors_are_named()
    {
        var entries = string.Join(',', Enumerable.Repeat("1", InventoryBomSnapshot.MaxSkipped + 10));

        var snapshot = ReadText("[" + entries + "]");

        Assert.Empty(snapshot.Connectors);
        Assert.Equal(InventoryBomSnapshot.MaxSkipped, snapshot.Skipped.Count);
    }

    // ------------------------------------------------------------------ the bounds

    [Fact]
    public void Output_over_the_cap_is_too_large_and_nothing_is_parsed_or_kept()
    {
        // 1.3 MB past the cap (and a few bytes of framing): 5.2 MB in all.
        var padding = new string('x', InventoryBomSnapshot.MaxOutputBytes + 1_300_000);
        var big = """{"connector": "claudecode", "skills": [{"id": "a", "description": """ + '"' + padding + "\"}]}";

        var result = InventoryBomSnapshot.Parse(big);

        Assert.Equal(InventoryBomParseStatus.TooLarge, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Snapshot);
        // The wording every other oversized value uses (OversizedValue.Reason).
        Assert.Equal("aibom scan output is 5.2 MB, over the 4 MB limit", result.Message);
    }

    [Fact]
    public void The_size_is_counted_in_bytes_not_characters_and_exactly_the_cap_is_still_parsed()
    {
        // Each 'é' is two bytes: half the cap in characters is already the whole of it.
        var twoByte = new string('é', (InventoryBomSnapshot.MaxOutputBytes / 2) + 1);
        Assert.Equal(InventoryBomParseStatus.TooLarge, InventoryBomSnapshot.Parse(twoByte).Status);

        const string head = "{\"connector\": \"x\", \"note\": \"";
        const string tail = "\"}";
        var fill = new string('y', InventoryBomSnapshot.MaxOutputBytes - head.Length - tail.Length);
        var exact = head + fill + tail;
        Assert.Equal(InventoryBomSnapshot.MaxOutputBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.True(InventoryBomSnapshot.Parse(exact).Succeeded);
    }

    [Fact]
    public void A_kinds_rows_are_capped_per_connector_and_the_summary_still_says_how_many_there_were()
    {
        var total = InventoryBomSnapshot.MaxRowsPerCategory + 120;
        var rows = string.Join(',', Enumerable.Range(1, total).Select(i => $"{{\"id\": \"skill-{i}\", \"source\": \"s\"}}"));
        var json = $"{{\"connector\": \"claudecode\", \"skills\": [{rows}], \"summary\": {{\"total_items\": {total}, \"skills\": {{\"count\": {total}, \"eligible\": 0}}}}}}";

        var connector = ReadText(json).Connectors[0];

        Assert.Equal(InventoryBomSnapshot.MaxRowsPerCategory, connector.Entities(InventoryBomKind.Skills).Count);
        Assert.Equal("skill-1", connector.Entities(InventoryBomKind.Skills)[0].Id);
        Assert.Equal(120, connector.Dropped(InventoryBomKind.Skills));
        Assert.Equal(0, connector.Dropped(InventoryBomKind.Plugins));
        Assert.Equal(total, connector.Count(InventoryBomKind.Skills)!.Count);
        Assert.Equal(total, connector.TotalItems);
    }

    [Fact]
    public void A_value_longer_than_the_limit_is_cut_with_an_ellipsis_and_extra_members_are_capped()
    {
        var members = string.Join(',', Enumerable.Range(1, InventoryBomSnapshot.MaxMoreFields + 10).Select(i => $"\"extra_{i}\": \"v{i}\""));
        var json = $"{{\"connector\": \"x\", \"skills\": [{{\"id\": \"a\", \"source\": \"{new string('p', 2000)}\", {members}}}]}}";

        var skill = ReadText(json).Connectors[0].Entities(InventoryBomKind.Skills).Single();

        Assert.Equal(InventoryBomSnapshot.MaxValueCharacters, skill.Cells[5].Length);
        Assert.EndsWith("…", skill.Cells[5], StringComparison.Ordinal);
        Assert.Equal(InventoryBomSnapshot.MaxMoreFields, skill.More.Count);
        Assert.Equal("Extra 1", skill.More[0].Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{ not json")]
    [InlineData("[1, 2")]
    public void Output_that_is_not_the_json_is_reported_and_never_throws(string stdout)
    {
        var result = InventoryBomSnapshot.Parse(stdout);

        Assert.Equal(InventoryBomParseStatus.NotJson, result.Status);
        Assert.Null(result.Snapshot);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Null_output_and_absurdly_nested_output_are_reported_not_thrown()
    {
        Assert.Equal(InventoryBomParseStatus.NotJson, InventoryBomSnapshot.Parse(null).Status);

        var nested = new string('[', 5000) + new string(']', 5000);
        Assert.Equal(InventoryBomParseStatus.NotJson, InventoryBomSnapshot.Parse(nested).Status);
    }

    // ------------------------------------------------------------------ names from outside

    [Fact]
    public void Names_and_descriptions_are_shown_with_their_control_and_bidirectional_characters_spelled_out()
    {
        const string json = "{\"connector\": \"x\", \"skills\": [{\"id\": \"evil\\u202Epdf\", \"description\": \"line one\\nline two\", \"source\": \"a\\tb\"}]}";

        var skill = ReadText(json).Connectors[0].Entities(InventoryBomKind.Skills).Single();

        Assert.Equal("evil\\u202Epdf", skill.Id);
        Assert.Equal("SKILL: evil\\u202Epdf", skill.Title);
        Assert.Equal("line one\\nline two", Field(skill, "Description"));
        Assert.Equal("a\\tb", skill.Cells[5]);
    }

    [Fact]
    public void Credentials_in_a_command_line_a_url_or_an_extra_member_are_masked_before_the_text_is_cut()
    {
        const string json = """
            {"connector": "x", "mcp": [
              {"id": "a", "command": "run", "args": ["--password", "hunter22"], "note": "token=hunter22 and more"},
              {"id": "b", "url": "https://mcp.example.test/v1?token=hunter22&x=1", "description": "Reset your password to continue"}
            ]}
            """;

        var connector = ReadText(json).Connectors[0];
        var a = Row(connector, InventoryBomKind.Mcp, "a");
        var b = Row(connector, InventoryBomKind.Mcp, "b");

        Assert.Equal("--password [redacted]", More(a, "Args"));
        Assert.Equal("token=[redacted] and more", More(a, "Note"));
        // The masking is DisplayRedaction's: a value runs to the next space, comma, semicolon or quote, so it takes the rest of the query with it.
        Assert.Equal("https://mcp.example.test/v1?token=[redacted]", Field(b, "URL"));
        Assert.Equal("https://mcp.example.test/v1?token=[redacted]", b.Cells[3]);
        // A sentence is prose: the word is not an assignment.
        Assert.Equal("Reset your password to continue", More(b, "Description"));
        Assert.DoesNotContain("hunter22", string.Concat(a.Fields.Concat(a.More).Select(f => f.Value)), StringComparison.Ordinal);
    }
}
