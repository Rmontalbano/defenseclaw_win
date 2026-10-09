using System.Text;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="InventoryBomSnapshot"/>, the parsed and bounded output of <c>defenseclaw aibom scan --json</c>, held to the TUI's reading of it
/// (<c>tui/services/inventory_state.py</c> of the 0.8.10 wheel: <c>from_mapping</c>, <c>data_table_rows</c>, <c>detail_info</c>). The JSON is synthetic and
/// shaped by the code that prints it (<c>claw_inventory.py</c>, <c>cmd_aibom.py</c>); the App suite runs the same type over whole fixture files
/// (<c>AiBomSnapshotTests</c>). Names and paths are made up.
/// </summary>
public sealed class InventoryBomSnapshotTests
{
    private const string OpenClaw = """
        {
          "version": 3, "generated_at": "2030-01-15T10:00:05+00:00", "connector": "openclaw", "claw_mode": "openclaw", "live": true,
          "claw_home": "C:\\Users\\example\\.openclaw", "openclaw_config": "C:\\Users\\example\\.openclaw\\openclaw.json",
          "skills": [], "plugins": [], "mcp": [], "tools": [],
          "agents": [
            {"id": "main", "model": "example/model-large", "workspace": "C:\\w", "is_default": true, "bindings": 2},
            {"id": "_defaults", "source": "agents.defaults", "model": "example/model-large", "fallbacks": ["example/model-small"], "subagents_max_concurrent": 4}
          ],
          "model_providers": [
            {"id": "_config", "source": "models status", "default_model": "example/model-large", "fallbacks": ["example/model-small"], "allowed": ["a", "b"], "config_path": "C:\\c.json"},
            {"id": "example", "source": "auth", "status": "missing"}
          ],
          "memory": [
            {"id": "main", "backend": "sqlite", "files": 12, "chunks": 340, "db_path": "C:\\m.sqlite", "provider": "example-embed",
             "sources": ["memory", "sessions"], "workspace": "C:\\w", "fts_available": true, "vector_enabled": false}
          ],
          "errors": [], "limitations": [],
          "summary": {"total_items": 5, "skills": {"count": 0, "eligible": 0}, "plugins": {"count": 0, "loaded": 0, "disabled": 0}, "mcp": {"count": 0},
                      "agents": {"count": 2}, "tools": {"count": 0}, "model_providers": {"count": 2}, "memory": {"count": 1}, "errors": 0, "limitations": 0}
        }
        """;

    private static InventoryBomSnapshot Read(string json)
    {
        var result = InventoryBomSnapshot.Parse(json);
        Assert.True(result.Succeeded, result.Message);
        return result.Snapshot!;
    }

    private static InventoryBomEntity Row(InventoryBomSnapshot snapshot, InventoryBomKind kind, string id) =>
        snapshot.Connectors[0].Entities(kind).Single(e => e.Id == id);

    private static string[] Labels(InventoryBomEntity entity) => entity.Fields.Select(f => f.Label).ToArray();

    [Fact]
    public void Agents_have_the_tuis_cells_and_detail_fields()
    {
        var main = Row(Read(OpenClaw), InventoryBomKind.Agents, "main");

        Assert.Equal(new[] { "ID", "Source", "Model", "Workspace", "Default" }, InventoryBomKind.Agents.Columns());
        Assert.Equal(new[] { "main", "", "example/model-large", @"C:\w", "yes" }, main.Cells);
        Assert.Equal("AGENT: main", main.Title);
        Assert.Equal(new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" }, Labels(main));
        Assert.Equal("true", main.Fields[2].Value);
        Assert.Equal("0", main.Fields[4].Value);

        var defaults = Row(Read(OpenClaw), InventoryBomKind.Agents, "_defaults");
        Assert.Equal("4", defaults.Fields[4].Value);
        Assert.Equal("no", defaults.Cells[4]);
    }

    [Fact]
    public void Models_have_the_tuis_cells_and_list_fallbacks_and_allowed_only_when_there_are_some()
    {
        var snapshot = Read(OpenClaw);
        var config = Row(snapshot, InventoryBomKind.Models, "_config");
        var auth = Row(snapshot, InventoryBomKind.Models, "example");

        Assert.Equal(new[] { "ID", "Source", "Default Model", "Status" }, InventoryBomKind.Models.Columns());
        Assert.Equal(new[] { "_config", "models status", "example/model-large", "" }, config.Cells);
        Assert.Equal(new[] { "Source", "Default Model", "Status", "Config", "Fallbacks", "Allowed" }, Labels(config));
        Assert.Equal("a, b", config.Fields[5].Value);
        Assert.Equal(new[] { "Source", "Default Model", "Status", "Config" }, Labels(auth));
        Assert.Equal("missing", auth.Cells[3]);
    }

    [Fact]
    public void Memory_has_the_tuis_cells_and_detail_fields_in_order()
    {
        var main = Row(Read(OpenClaw), InventoryBomKind.Memory, "main");

        Assert.Equal(new[] { "ID", "Backend", "Provider", "Files", "Chunks", "Workspace" }, InventoryBomKind.Memory.Columns());
        Assert.Equal(new[] { "main", "sqlite", "example-embed", "12", "340", @"C:\w" }, main.Cells);
        Assert.Equal(
            new[] { "Backend", "Provider", "Workspace", "DB Path", "Files", "Chunks", "FTS Available", "Vector Enabled", "Sources" },
            Labels(main));
        Assert.Equal(new[] { "true", "false", "memory, sessions" }, main.Fields.Skip(6).Select(f => f.Value).ToArray());
    }

    [Fact]
    public void The_summary_is_read_per_connector_with_the_counts_the_tui_shows()
    {
        var connector = Read(OpenClaw).Connectors[0];

        Assert.Equal("openclaw", connector.Name);
        Assert.Equal("3", connector.Version);
        Assert.Equal(@"C:\Users\example\.openclaw", connector.Home);
        Assert.Equal(@"C:\Users\example\.openclaw\openclaw.json", connector.Config);
        Assert.Equal(5, connector.TotalItems);
        Assert.Equal(2, connector.Count(InventoryBomKind.Agents)!.Count);
        Assert.Equal(2, connector.Count(InventoryBomKind.Models)!.Count);
        Assert.Null(connector.Rules);
    }

    [Fact]
    public void A_bare_object_is_one_connector_a_list_is_one_each_and_an_empty_list_is_none()
    {
        Assert.Single(Read(OpenClaw).Connectors);
        Assert.Equal(new[] { "a", "b" }, Read("""[{"connector": "a"}, {"connector": "b", "skills": []}]""").Connectors.Select(c => c.Name).ToArray());

        var none = Read("[]");
        Assert.Empty(none.Connectors);
        Assert.Empty(none.Skipped);
    }

    [Fact]
    public void Output_over_the_cap_is_too_large_and_is_never_parsed()
    {
        var padding = new string('x', InventoryBomSnapshot.MaxOutputBytes + 1_300_000);

        var result = InventoryBomSnapshot.Parse("{\"connector\": \"x\", \"note\": \"" + padding + "\"}");

        Assert.Equal(InventoryBomParseStatus.TooLarge, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal("aibom scan output is 5.2 MB, over the 4 MB limit", result.Message);
    }

    [Fact]
    public void The_cap_is_in_bytes_and_exactly_the_cap_is_still_read()
    {
        Assert.Equal(InventoryBomParseStatus.TooLarge, InventoryBomSnapshot.Parse(new string('é', (InventoryBomSnapshot.MaxOutputBytes / 2) + 1)).Status);

        const string head = "{\"connector\": \"x\", \"note\": \"";
        const string tail = "\"}";
        var exact = head + new string('y', InventoryBomSnapshot.MaxOutputBytes - head.Length - tail.Length) + tail;
        Assert.Equal(InventoryBomSnapshot.MaxOutputBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.True(InventoryBomSnapshot.Parse(exact).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{ not json")]
    [InlineData("[1, 2")]
    public void Output_that_is_not_the_json_is_reported_and_never_throws(string stdout)
    {
        var result = InventoryBomSnapshot.Parse(stdout);

        Assert.Equal(InventoryBomParseStatus.NotJson, result.Status);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Rows_are_capped_per_kind_and_connector_and_the_summary_still_says_how_many_there_were()
    {
        var total = InventoryBomSnapshot.MaxRowsPerCategory + 7;
        var rows = string.Join(',', Enumerable.Range(1, total).Select(i => $"{{\"id\": \"a{i}\"}}"));

        var connector = Read($"{{\"connector\": \"x\", \"agents\": [{rows}], \"summary\": {{\"agents\": {{\"count\": {total}}}}}}}").Connectors[0];

        Assert.Equal(InventoryBomSnapshot.MaxRowsPerCategory, connector.Entities(InventoryBomKind.Agents).Count);
        Assert.Equal(7, connector.Dropped(InventoryBomKind.Agents));
        Assert.Equal(total, connector.Count(InventoryBomKind.Agents)!.Count);
    }

    [Fact]
    public void A_connector_that_cannot_be_used_is_skipped_and_named_and_one_whose_commands_failed_is_kept_with_them_named()
    {
        const string json = """
            [7, {"unrelated": true}, null,
             {"connector": "codex", "skills": [{"id": "summarize"}],
              "errors": [{"command": "codex:mcp", "error": "could not read the config"}], "summary": {"errors": 1}}]
            """;

        var snapshot = Read(json);

        Assert.Equal("codex", Assert.Single(snapshot.Connectors).Name);
        Assert.Equal(new[] { "entry 1", "entry 2", "entry 3" }, snapshot.Skipped.Select(s => s.Name).ToArray());
        Assert.Equal("it is a number, not an inventory object", snapshot.Skipped[0].Reason);
        Assert.Equal("it is not an inventory (no connector, summary or category list)", snapshot.Skipped[1].Reason);
        Assert.Equal("it is null, not an inventory object", snapshot.Skipped[2].Reason);

        var codex = snapshot.Connectors[0];
        Assert.Equal(1, codex.ErrorCount);
        Assert.Equal(("codex:mcp", "could not read the config"), (codex.Errors[0].Command, codex.Errors[0].Message));
        Assert.Equal("summarize", Assert.Single(codex.Entities(InventoryBomKind.Skills)).Id);
    }

    [Fact]
    public void A_newer_runtimes_members_are_read_by_presence_and_a_row_without_them_gets_none()
    {
        const string json = """
            {"connector": "hermes", "version": 4,
             "plugins": [{"id": "user/notes", "name": "notes", "kind": "standalone", "source_kind": "user", "enabled": true}],
             "tools": [{"id": "deploy", "name": "deploy", "kind": "config-command", "description": "Deploy the app", "source": "C:\\p.json"}, {"id": "bare"}],
             "rules": [{"id": "SOUL.md"}],
             "summary": {"total_items": 4, "plugins": {"count": 1}, "tools": {"count": 2}, "rules": {"count": 1}, "mcp": {"count": 0, "collected": false}, "agents": {"count": 0}}}
            """;

        var connector = Read(json).Connectors[0];
        var notes = connector.Entities(InventoryBomKind.Plugins)[0];
        var deploy = connector.Entities(InventoryBomKind.Tools)[0];
        var bare = connector.Entities(InventoryBomKind.Tools)[1];

        Assert.Equal(new[] { "notes", "", "user", "enabled", "", "0", "" }, notes.Cells);
        Assert.Equal("Kind", Assert.Single(notes.More).Label);
        Assert.Equal(new[] { "deploy", "deploy", "config-command", @"C:\p.json" }, deploy.Cells);
        Assert.Equal("Deploy the app", deploy.Fields.Single(f => f.Label == "Description").Value);
        Assert.Equal(new[] { "bare", "bare", "", "" }, bare.Cells);
        Assert.Equal(1, connector.Rules);
        Assert.False(connector.Count(InventoryBomKind.Mcp)!.Collected);
        Assert.True(connector.Count(InventoryBomKind.Agents)!.Collected);
        Assert.True(connector.Count(InventoryBomKind.Plugins)!.Collected);
    }

    [Fact]
    public void Names_are_spelled_out_and_credentials_in_free_text_are_masked()
    {
        const string json = """
            {"connector": "x",
             "skills": [{"id": "evil\u202Epdf", "description": "line one\nline two"}],
             "mcp": [{"id": "a", "command": "run", "args": ["--password", "hunter22"], "url": "https://mcp.example.test/v1?token=hunter22"}]}
            """;

        var connector = Read(json).Connectors[0];
        var skill = connector.Entities(InventoryBomKind.Skills).Single();
        var mcp = connector.Entities(InventoryBomKind.Mcp).Single();

        Assert.Equal("evil\\u202Epdf", skill.Id);
        Assert.Equal("line one\\nline two", skill.Fields[0].Value);
        Assert.Equal("--password [redacted]", mcp.More.Single(f => f.Label == "Args").Value);
        Assert.Equal("https://mcp.example.test/v1?token=[redacted]", mcp.Fields.Single(f => f.Label == "URL").Value);
        Assert.Equal("run", mcp.Cells[3]);
        Assert.DoesNotContain("hunter22", string.Concat(mcp.Fields.Concat(mcp.More).Select(f => f.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_kinds_know_their_words()
    {
        Assert.Equal("model_providers", InventoryBomKind.Models.JsonKey());
        Assert.Equal("models", InventoryBomKind.Models.OnlyName());
        Assert.Equal("MCPs", InventoryBomKind.Mcp.Label());
        Assert.Equal("MCP servers", InventoryBomKind.Mcp.Plural());
        Assert.Equal(new[] { InventoryBomKind.Skills, InventoryBomKind.Plugins, InventoryBomKind.Mcp }, InventoryBomKinds.FastScan);

        Assert.True(InventoryBomKinds.TryParse(" Models ", out var models));
        Assert.Equal(InventoryBomKind.Models, models);
        Assert.True(InventoryBomKinds.TryParse("model_providers", out _));
        Assert.False(InventoryBomKinds.TryParse("rules", out _));
    }
}
