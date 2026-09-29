using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The four Govern panels turn <c>list --json</c> output into rows. The payloads are shaped like 0.8.10's
/// (synthetic names, no real machine paths): a bare array, per-connector groups, and a lone group object.
/// </summary>
public sealed class GovernPanelParsingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public GovernPanelParsingTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ skills

    private const string SkillsBareArray = """
        [
          {
            "name": "pdf-tools", "description": "Read and edit PDFs", "source": "user", "status": "active",
            "eligible": true, "disabled": false, "bundled": false, "homepage": "https://example.test/pdf",
            "scan": {
              "target": "C:\\skills\\pdf-tools", "clean": false, "max_severity": "HIGH", "total_findings": 3,
              "severity_counts": {"critical": 0, "high": 2, "medium": 1, "low": 0, "info": 0}
            },
            "actions": {"file": "none", "runtime": "enable", "install": "none"},
            "verdict": "warning"
          },
          {"name": "shipped-with-connector", "source": "bundled", "status": "active", "eligible": true, "bundled": true},
          {"name": "blocked-skill", "status": "blocked", "actions": {"install": "block"}, "verdict": "blocked"},
          {"description": "an item with no name is skipped"}
        ]
        """;

    [Fact]
    public void A_skill_row_carries_its_state_scan_fields_and_verbs()
    {
        var vm = new SkillsPanelViewModel(_services);

        var rows = vm.ParseRows(SkillsBareArray);

        Assert.Equal(new[] { "pdf-tools", "shipped-with-connector", "blocked-skill" }, rows.Select(r => r.Name).ToArray());
        var pdf = rows[0];
        Assert.Equal("skill", pdf.Noun);
        Assert.Equal("Active", pdf.StateLabel);
        Assert.Equal("Ok", pdf.StateTone);
        Assert.Equal("HIGH · 3 findings", pdf.ScanLabel);
        Assert.Equal("High", pdf.ScanTone);
        Assert.True(pdf.NeedsAttention);
        Assert.Equal("Read and edit PDFs", pdf.Description);
        Assert.Contains(pdf.Fields, f => f is { Label: "Findings", Value: "high 2 · medium 1" });
        Assert.Contains(pdf.Fields, f => f is { Label: "Homepage", Value: "https://example.test/pdf" });
        Assert.Equal(
            GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Block | GovernVerbs.Allow | GovernVerbs.Disable | GovernVerbs.Quarantine,
            pdf.Verbs);
        Assert.Contains("\"pdf-tools\"", pdf.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundled_skill_can_be_inspected_but_not_changed()
    {
        var vm = new SkillsPanelViewModel(_services);

        var bundled = vm.ParseRows(SkillsBareArray)[1];

        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName, bundled.Verbs);
        Assert.False(bundled.CanBlock);
        Assert.False(bundled.CanQuarantine);
    }

    [Fact]
    public void A_blocked_skill_offers_unblock_and_allow_rather_than_block()
    {
        var vm = new SkillsPanelViewModel(_services);

        var blocked = vm.ParseRows(SkillsBareArray)[2];

        Assert.True(blocked.IsBlocked);
        Assert.Equal("Blocked", blocked.StateLabel);
        Assert.True(blocked.CanUnblock);
        Assert.True(blocked.CanAllow);
        Assert.False(blocked.CanBlock);
    }

    [Fact]
    public void Skills_listed_per_connector_keep_the_connector_they_were_listed_under()
    {
        var vm = new SkillsPanelViewModel(_services);

        var rows = vm.ParseRows("""
            [
              {"connector": "claudecode", "skills": [{"name": "a"}, {"name": "b"}]},
              {"connector": "codex", "skills": [{"name": "a"}, {"name": "c", "connector": "override"}]}
            ]
            """);

        Assert.Equal(
            new[] { ("a", "claudecode"), ("b", "claudecode"), ("a", "codex"), ("c", "override") },
            rows.Select(r => (r.Name, r.Connector!)).ToArray());
    }

    [Fact]
    public void The_same_skill_twice_under_one_connector_is_listed_once()
    {
        var vm = new SkillsPanelViewModel(_services);

        var rows = vm.ParseRows("""{"connector": "codex", "skills": [{"name": "a"}, {"name": "a"}]}""");

        Assert.Single(rows);
    }

    [Fact]
    public void A_bare_skill_with_no_connector_anywhere_has_none()
    {
        var vm = new SkillsPanelViewModel(_services);

        var row = Assert.Single(vm.ParseRows("""[{"name": "a"}]"""));

        Assert.Null(row.Connector);
        Assert.Equal(string.Empty, row.ConnectorLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_output_is_a_format_error_not_an_empty_list(string stdout)
    {
        var vm = new SkillsPanelViewModel(_services);

        Assert.Throws<FormatException>(() => vm.ParseRows(stdout));
    }

    [Fact]
    public void Output_that_is_not_json_or_not_a_list_is_an_error_the_panel_can_report()
    {
        var vm = new SkillsPanelViewModel(_services);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => vm.ParseRows("Traceback (most recent call last):"));
        Assert.Throws<FormatException>(() => vm.ParseRows("42"));
    }

    // ------------------------------------------------------------------ MCP servers

    [Fact]
    public void An_mcp_server_row_joins_its_launch_command_and_offers_unset_but_not_info()
    {
        var vm = new McpsPanelViewModel(_services);

        var rows = vm.ParseRows("""
            {"connector": "claudecode", "mcp_servers": [
              {"name": "docs", "transport": "stdio", "command": "npx", "args": ["-y", "@example/docs-mcp"], "severity": "MEDIUM"},
              {"name": "remote", "transport": "sse", "url": "https://mcp.example.test/sse", "actions": {"install": "allow"}, "verdict": "allowed"}
            ]}
            """);

        var docs = rows[0];
        Assert.Equal("mcp", docs.Noun);
        Assert.Equal("claudecode", docs.Connector);
        Assert.Contains(docs.Fields, f => f is { Label: "Command", Value: "npx -y @example/docs-mcp" });
        Assert.Equal("Configured", docs.StateLabel);
        Assert.Equal("MEDIUM", docs.ScanLabel);
        Assert.True(docs.NeedsAttention);
        Assert.Equal(GovernVerbs.CopyName | GovernVerbs.Block | GovernVerbs.Allow | GovernVerbs.Unset, docs.Verbs);
        Assert.False(docs.CanInfo);

        var remote = rows[1];
        Assert.True(remote.IsAllowed);
        Assert.Contains(remote.Fields, f => f is { Label: "URL", Value: "https://mcp.example.test/sse" });
        Assert.Equal(GovernVerbs.CopyName | GovernVerbs.Block | GovernVerbs.Unblock | GovernVerbs.Unset, remote.Verbs);
    }

    // ------------------------------------------------------------------ plugins

    private const string PluginsPayload = """
        [
          {
            "id": "code-review", "name": "Code Review", "description": "Reviews diffs", "version": "1.2.0",
            "origin": "marketplace", "source": "npm", "status": "active", "enabled": true, "connector": "claudecode",
            "scan": {"target": "C:\\plugins\\code-review", "clean": false, "max_severity": "MEDIUM", "total_findings": 2},
            "actions": {"file": "none", "runtime": "enable", "install": "allow"},
            "verdict": "allowed"
          },
          {"id": "marketplaces", "name": "marketplaces", "connector": "claudecode", "status": "quarantined", "actions": {"file": "quarantine"}, "verdict": "quarantined"},
          {"id": "cache", "connector": "claudecode", "status": "quarantined"},
          {"id": "known_marketplaces.json.lock", "connector": "claudecode", "status": "quarantined"},
          {"id": "installed_plugins.json", "connector": "claudecode"},
          {"id": ".hidden-dir", "connector": "claudecode"},
          {"id": "Cache", "connector": "ClaudeCode"},
          {"id": "cache-warmer", "connector": "claudecode"},
          {"id": "marketplaces", "connector": "codex"},
          {"id": "notes.json"}
        ]
        """;

    private static readonly string[] ExpectedArtifacts =
    {
        "marketplaces", "cache", "known_marketplaces.json.lock", "installed_plugins.json", ".hidden-dir", "Cache",
    };

    [Fact]
    public void Claude_codes_own_plugin_store_entries_are_flagged_as_listing_artifacts()
    {
        var vm = new PluginsPanelViewModel(_services);

        var rows = vm.ParseRows(PluginsPayload);

        var artifacts = rows.Where(r => r.IsArtifact && r.Connector!.Equals("claudecode", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name).ToArray();
        Assert.Equal(ExpectedArtifacts, artifacts);
    }

    [Fact]
    public void Look_alikes_that_are_not_in_claude_codes_store_stay_real_plugins()
    {
        var vm = new PluginsPanelViewModel(_services);

        var real = vm.ParseRows(PluginsPayload).Where(r => !r.IsArtifact).Select(r => (r.Name, r.Connector)).ToArray();

        Assert.Equal(
            new (string, string?)[]
            {
                ("code-review", "claudecode"),
                ("cache-warmer", "claudecode"),
                ("marketplaces", "codex"),
                ("notes.json", null),
            },
            real);
    }

    [Fact]
    public void An_artifact_is_offered_only_info_and_copy_and_says_it_is_not_a_plugin()
    {
        var vm = new PluginsPanelViewModel(_services);

        var artifact = vm.ParseRows(PluginsPayload).First(r => r.Name == "marketplaces" && r.Connector == "claudecode");

        Assert.True(artifact.IsArtifact);
        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName, artifact.Verbs);
        Assert.True(artifact.HasArtifactNote);
        Assert.Contains("Not a plugin", artifact.ArtifactNote, StringComparison.Ordinal);
        Assert.Contains("listing artifact", artifact.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_plugin_reads_scan_max_severity_and_offers_remove_on_top_of_the_standard_verbs()
    {
        var vm = new PluginsPanelViewModel(_services);

        var plugin = vm.ParseRows(PluginsPayload)[0];

        Assert.Equal("code-review", plugin.Name);
        Assert.Equal("Code Review", plugin.Title);
        Assert.Equal("MEDIUM · 2 findings", plugin.ScanLabel);
        Assert.Equal("Medium", plugin.ScanTone);
        Assert.True(plugin.NeedsAttention);
        Assert.True(plugin.IsAllowed);
        Assert.Contains(plugin.Fields, f => f is { Label: "Scan target", Value: "C:\\plugins\\code-review" });
        Assert.Equal(
            GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Block | GovernVerbs.Unblock | GovernVerbs.Disable | GovernVerbs.Quarantine | GovernVerbs.Remove,
            plugin.Verbs);
        Assert.Contains("version: 1.2.0", plugin.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_falls_back_from_id_to_name_and_is_skipped_with_neither()
    {
        var vm = new PluginsPanelViewModel(_services);

        var rows = vm.ParseRows("""[{"name": "only-a-name"}, {"description": "neither"}]""");

        var row = Assert.Single(rows);
        Assert.Equal("only-a-name", row.Name);
    }

    // ------------------------------------------------------------------ tools

    private const string ToolsPayload = """
        [
          {"connector": "claudecode", "tools": [
            {"name": "delete_file", "connector": "claudecode", "scope": "connector", "status": "block", "reason": "destructive", "updated_at": "2026-09-01T10:00:00Z"},
            {"name": "shell_exec", "connector": "claudecode", "scope": "global", "status": "allow", "reason": "-", "updated_at": "2026-09-02T10:00:00Z"}
          ]},
          {"connector": "codex", "tools": [
            {"name": "shell_exec", "connector": "codex", "scope": "global", "status": "allow", "reason": "-", "updated_at": "2026-09-02T10:00:00Z"}
          ]},
          {"connector": null, "scope": "source", "tools": [
            {"name": "docs-server/read_file", "scope": "source", "status": "block", "reason": "audit only"}
          ]}
        ]
        """;

    [Fact]
    public void Tool_rules_are_read_from_status_block_and_allow_and_scoped_by_their_scope_field()
    {
        var vm = new ToolsPanelViewModel(_services);

        var rows = vm.ParseRows(ToolsPayload);

        Assert.Equal(3, rows.Count);

        var connectorRule = rows[0];
        Assert.Equal("delete_file", connectorRule.Name);
        Assert.Equal("connector", connectorRule.RuleScope);
        Assert.Equal("claudecode", connectorRule.Connector);
        Assert.True(connectorRule.IsBlocked);
        Assert.Equal("Blocked", connectorRule.StateLabel);
        Assert.Equal("Bad", connectorRule.StateTone);
        Assert.Equal("destructive", connectorRule.Reason);
        Assert.Contains(connectorRule.Fields, f => f is { Label: "Applies to", Value: "connector claudecode" });
        Assert.Equal("Status", connectorRule.InfoLabel);

        var globalRule = rows[1];
        Assert.Equal("global", globalRule.RuleScope);
        Assert.Null(globalRule.Connector);
        Assert.True(globalRule.IsAllowed);
        Assert.Null(globalRule.Reason);
        Assert.Equal("all connectors (fallback)", globalRule.ConnectorLabel);

        var sourceRule = rows[2];
        Assert.Equal("source", sourceRule.RuleScope);
        Assert.Equal("docs-server", sourceRule.SourceScope);
        Assert.Equal("read_file", sourceRule.Name);
        Assert.True(sourceRule.IsBlocked);
        Assert.NotNull(sourceRule.Description);
    }

    [Fact]
    public void A_global_rule_the_cli_repeats_under_every_connector_is_listed_once()
    {
        var vm = new ToolsPanelViewModel(_services);

        var rows = vm.ParseRows(ToolsPayload);

        Assert.Single(rows, r => r.Name == "shell_exec");
    }

    [Fact]
    public void A_tool_row_offers_the_opposite_decision_and_a_source_rule_offers_neither()
    {
        var vm = new ToolsPanelViewModel(_services);
        var rows = vm.ParseRows(ToolsPayload);

        Assert.True(rows[0].CanAllow);
        Assert.False(rows[0].CanBlock);
        Assert.True(rows[0].CanUnblock);

        Assert.True(rows[1].CanBlock);
        Assert.False(rows[1].CanAllow);

        Assert.False(rows[2].CanBlock);
        Assert.False(rows[2].CanAllow);
        Assert.True(rows[2].CanUnblock);
    }

    [Fact]
    public void A_lone_group_object_and_a_missing_scope_are_read_too()
    {
        var vm = new ToolsPanelViewModel(_services);

        var rows = vm.ParseRows("""{"connector": "codex", "tools": [{"name": "t", "connector": "codex", "status": "block"}]}""");

        var row = Assert.Single(rows);
        Assert.Equal("global", row.RuleScope);
        Assert.Null(row.Connector);
        Assert.True(row.IsBlocked);
    }

    [Fact]
    public void A_status_that_is_neither_block_nor_allow_is_shown_as_is()
    {
        var vm = new ToolsPanelViewModel(_services);

        var row = Assert.Single(vm.ParseRows("""[{"name": "t", "scope": "global", "status": "none"}]"""));

        Assert.False(row.IsBlocked);
        Assert.False(row.IsAllowed);
        Assert.Equal("None", row.StateLabel);
    }
}
