using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// The four Govern panels read <c>defenseclaw {skill,mcp,plugin,tool} list --json</c>. The payloads in
/// <c>Fixtures/CliPayloads</c> are synthetic (made-up names, paths under C:\Users\example) but follow the code that
/// prints them, cited per test below; file:line refer to the <c>defenseclaw</c> 0.8.10 wheel
/// (<c>runtime\python\Lib\site-packages\defenseclaw</c> of the installer's managed Python).
/// <para>
/// Live captures on the dev machine confirmed the empty shapes (<c>skill list</c> → <c>[]</c>, <c>mcp list</c> → <c>[]</c>,
/// <c>tool list</c> → <c>[{"connector": …, "tools": []}]</c>) and the plugin shape (three Claude Code store entries).
/// A populated skill or MCP list could not be captured (this machine has none), so those follow the source alone.
/// </para>
/// </summary>
public sealed class GovernPayloadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public GovernPayloadTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static string[] Names(IEnumerable<GovernRow> rows) => rows.Select(r => r.Name).ToArray();

    private static GovernRow Row(IEnumerable<GovernRow> rows, string name) => Assert.Single(rows, r => r.Name == name);

    // ------------------------------------------------------------------ skills

    // Emitter: commands/cmd_skill.py:1035-1065 (list_skills: bare array for one connector and no flag, per-connector
    // {"connector","skills"} groups for several, one such object under --connector) and 1152-1185
    // (_skill_list_json_items: name, description, source, status, eligible, disabled, bundled, [connector], [homepage],
    // [scan], [actions], verdict). scan is 670-679 (target, clean, max_severity "CLEAN" when there are no findings,
    // total_findings, severity_counts of five buckets); actions is models.py:125-133 (ActionState.to_dict omits empty
    // fields); verdict is commands/__init__.py:154-180; status is cmd_skill.py:941-948 (disabled, blocked, active, inactive).
    [Fact]
    public void A_skill_list_for_one_connector_is_a_bare_array_and_every_item_is_read()
    {
        var vm = new SkillsPanelViewModel(_services);

        var rows = vm.ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        Assert.Equal(
            new[] { "pdf-tools", "notes-helper", "risky-skill", "draft-skill", "paused-skill", "removed-skill", "old-scan-only", "résumé-助手" },
            Names(rows));
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
        Assert.All(rows, r => Assert.Null(r.Connector));
    }

    [Fact]
    public void An_allowed_skill_with_findings_shows_both_its_decision_and_its_scan()
    {
        var pdf = Row(new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");

        Assert.Equal("Allowed", pdf.StateLabel);
        Assert.Equal("Ok", pdf.StateTone);
        Assert.Equal("HIGH · 3 findings", pdf.ScanLabel);
        Assert.Equal("High", pdf.ScanTone);
        Assert.True(pdf.NeedsAttention);
        Assert.Equal("install: allow", pdf.ActionsText);
        Assert.Contains(pdf.Fields, f => f is { Label: "Findings", Value: "high 2 · medium 1" });
        Assert.Contains(pdf.Fields, f => f is { Label: "Homepage", Value: "https://example.test/pdf-tools" });
        Assert.Equal(
            GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Unblock | GovernVerbs.Disable | GovernVerbs.Quarantine,
            pdf.Verbs);
    }

    [Fact]
    public void A_clean_scan_is_labelled_clean_and_a_critical_one_needs_attention()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        var clean = Row(rows, "notes-helper");
        Assert.Equal("Active", clean.StateLabel);
        Assert.Equal("Ok", clean.StateTone);
        Assert.Equal("Scan clean", clean.ScanLabel);
        Assert.Equal("Ok", clean.ScanTone);
        Assert.False(clean.NeedsAttention);
        Assert.DoesNotContain(clean.Fields, f => f.Label == "Findings");

        var risky = Row(rows, "risky-skill");
        Assert.Equal("CRITICAL · 1 finding", risky.ScanLabel);
        Assert.Equal("Critical", risky.ScanTone);
        Assert.True(risky.NeedsAttention);
    }

    [Fact]
    public void A_skill_with_no_marker_file_is_inactive_unscanned_and_says_it_is_not_eligible()
    {
        var draft = Row(new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "draft-skill");

        Assert.Equal("Inactive", draft.StateLabel);
        Assert.Equal("Neutral", draft.StateTone);
        Assert.Equal("Not scanned", draft.ScanLabel);
        Assert.Null(draft.Description);
        Assert.Contains("eligible: no", draft.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_disabled_skill_offers_enable_and_a_quarantined_phantom_offers_restore()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        var paused = Row(rows, "paused-skill");
        Assert.Equal("Disabled", paused.StateLabel);
        Assert.Equal("Warn", paused.StateTone);
        Assert.True(paused.CanEnable);
        Assert.False(paused.CanDisable);

        // cmd_skill.py:1088-1150: a quarantined copy is absent from the folder walk, so the CLI adds an "enforcement" row for it.
        var removed = Row(rows, "removed-skill");
        Assert.Equal("Quarantined", removed.StateLabel);
        Assert.Equal("Bad", removed.StateTone);
        Assert.True(removed.IsBlocked);
        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Restore | GovernVerbs.Unblock, removed.Verbs);
        Assert.Contains("source: enforcement", removed.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scan_history_only_skill_keeps_its_scan_severity()
    {
        var old = Row(new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "old-scan-only");

        Assert.Equal("MEDIUM · 1 finding", old.ScanLabel);
        Assert.True(old.NeedsAttention);
        Assert.Contains("source: scan-history", old.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ascii_names_and_descriptions_survive_and_are_searchable()
    {
        var unicode = Row(new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "résumé-助手");

        Assert.Equal("Génère un résumé — 日本語 ✓", unicode.Description);
        Assert.Contains("résumé-助手", unicode.SearchText, StringComparison.Ordinal);
        Assert.Contains("日本語", unicode.RawJson, StringComparison.Ordinal);
    }

    // cmd_skill.py:1035-1048: with more than one connector, [{"connector": c, "skills": […]}], items carry "connector".
    [Fact]
    public void A_multi_connector_skill_list_keeps_the_same_name_once_per_connector()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.multi-connector.json"));

        Assert.Equal(
            new[] { ("pdf-tools", "claudecode"), ("shared-skill", "claudecode"), ("shared-skill", "codex"), ("skill-installer", "codex") },
            rows.Select(r => (r.Name, r.Connector!)).ToArray());
        Assert.True(rows.Single(r => r is { Name: "shared-skill", Connector: "claudecode" }).IsBlocked);
        Assert.False(rows.Single(r => r is { Name: "shared-skill", Connector: "codex" }).IsBlocked);

        // A skill that ships with the connector (Codex's .system container, skill_discovery.py:48-90) can be inspected only.
        var bundled = Row(rows, "skill-installer");
        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan, bundled.Verbs);
    }

    // cmd_skill.py:1059-1063: --connector prints one {"connector","skills"} object, even when empty.
    [Fact]
    public void A_skill_list_under_a_connector_flag_is_one_group_object()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.connector-flag.json"));

        Assert.Equal(new[] { "skill-installer", "team-helper" }, Names(rows));
        Assert.All(rows, r => Assert.Equal("codex", r.Connector));
        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan, Row(rows, "skill-installer").Verbs);
    }

    [Theory]
    [InlineData("skill-list.empty.json")]
    [InlineData("skill-list.connector-flag-empty.json")]
    public void An_empty_skill_list_is_an_empty_result_not_an_error(string fixture)
    {
        // Live capture: `defenseclaw skill list --json` printed [] on the dev machine. ParseRows returning no rows is what
        // GovernPanelViewModelBase.LoadOnceAsync turns into GovernState.Empty (a normal state), not Error.
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read(fixture));

        Assert.Empty(rows);
    }

    [Fact]
    public void Skill_items_with_nulls_unknown_members_and_junk_entries_are_read_or_skipped_without_failing()
    {
        // Not something 0.8.10 prints: the tolerance the panel keeps for a later release and for a hand-edited list.
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.tolerant.json"));

        Assert.Equal(new[] { "all-optionals-null", "fields-a-later-release-adds", "scan-with-only-a-severity" }, Names(rows));

        var nulls = Row(rows, "all-optionals-null");
        Assert.Equal("Unknown", nulls.StateLabel);
        Assert.Equal("Not scanned", nulls.ScanLabel);
        Assert.Null(nulls.Description);
        Assert.Equal(new[] { "Name", "Scan" }, nulls.Fields.Select(f => f.Label).ToArray());

        var future = Row(rows, "fields-a-later-release-adds");
        Assert.Equal("Scan clean", future.ScanLabel);
        Assert.Contains("\"metrics\"", future.RawJson, StringComparison.Ordinal);

        Assert.Equal("HIGH", Row(rows, "scan-with-only-a-severity").ScanLabel);
    }

    // ------------------------------------------------------------------ MCP servers

    // Emitter: commands/cmd_mcp.py:123-178 (list_mcps) and 234-273 (_mcp_list_json_items: name, transport, [connector],
    // [command], [args], [url], [severity], [actions], verdict; env is never printed). Transport is
    // connector_paths.py:163-183 (explicit, else "http" for a URL, else "stdio"). Severity is the bare max_severity string
    // of the scan map (cmd_mcp.py:373-427), "CLEAN" without findings. Unlike skills, items carry "connector" even in the
    // bare single-connector array (the list is built with connector=connectors[0]).
    [Fact]
    public void An_mcp_list_for_one_connector_is_a_bare_array_whose_items_name_their_connector()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json"));

        Assert.Equal(
            new[] { "docs-search", "remote-tools", "streaming-tools", "sse-server", "bare-server", "données-ü" },
            Names(rows));
        Assert.All(rows, r => Assert.Equal("claudecode", r.Connector));
    }

    [Fact]
    public void An_mcp_stdio_server_shows_its_launch_command_and_a_clean_scan()
    {
        var docs = Row(new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json")), "docs-search");

        Assert.Equal("Configured", docs.StateLabel);
        Assert.Equal("Neutral", docs.StateTone);
        Assert.Equal("Scan clean", docs.ScanLabel);
        Assert.Equal("Ok", docs.ScanTone);
        Assert.False(docs.NeedsAttention);
        Assert.Contains(docs.Fields, f => f is { Label: "Transport", Value: "stdio" });
        Assert.Contains(docs.Fields, f => f is { Label: "Command", Value: @"npx -y @example/docs-mcp --root C:\Users\example\docs" });
        Assert.Equal(GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Allow | GovernVerbs.Unset, docs.Verbs);
    }

    [Fact]
    public void An_mcp_remote_server_reads_its_url_transport_severity_and_block_decision()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json"));

        var blocked = Row(rows, "remote-tools");
        Assert.Equal("Blocked", blocked.StateLabel);
        Assert.Equal("Bad", blocked.StateTone);
        Assert.Equal("HIGH", blocked.ScanLabel);
        Assert.True(blocked.NeedsAttention);
        Assert.Contains(blocked.Fields, f => f is { Label: "URL", Value: "https://mcp.example.test/v1" });
        Assert.Equal(GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Allow | GovernVerbs.Unblock | GovernVerbs.Unset, blocked.Verbs);

        var streaming = Row(rows, "streaming-tools");
        Assert.Contains(streaming.Fields, f => f is { Label: "Transport", Value: "streamable-http" });
        Assert.Equal("MEDIUM", streaming.ScanLabel);

        var sse = Row(rows, "sse-server");
        Assert.True(sse.IsAllowed);
        Assert.Equal(GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Unblock | GovernVerbs.Unset, sse.Verbs);
    }

    [Fact]
    public void An_mcp_server_with_a_command_and_no_args_and_one_with_unicode_args_are_read()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json"));

        Assert.Contains(Row(rows, "bare-server").Fields, f => f is { Label: "Command", Value: "example-server" });
        Assert.Contains(Row(rows, "données-ü").Fields, f => f is { Label: "Command", Value: "python -m srv --label café ☕" });
    }

    // cmd_mcp.py:141-158: several connectors → [{"connector", "mcp_servers": […]}]; a connector with none gets an empty list.
    [Fact]
    public void A_multi_connector_mcp_list_is_flattened_and_an_empty_group_adds_nothing()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.multi-connector.json"));

        Assert.Equal(
            new[] { ("docs-search", "claudecode"), ("docs-search", "codex"), ("remote-tools", "codex") },
            rows.Select(r => (r.Name, r.Connector!)).ToArray());
    }

    [Fact]
    public void An_mcp_list_under_a_connector_flag_is_one_group_object()
    {
        var row = Assert.Single(new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.connector-flag.json")));

        Assert.Equal("docs-search", row.Name);
        Assert.Equal("claudecode", row.Connector);
    }

    [Theory]
    [InlineData("mcp-list.empty.json")]
    [InlineData("mcp-list.connector-flag-empty.json")]
    public void An_empty_mcp_list_is_an_empty_result_not_an_error(string fixture)
    {
        // Live capture: `defenseclaw mcp list --json` printed [] on the dev machine.
        Assert.Empty(new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read(fixture)));
    }

    [Fact]
    public void Mcp_items_with_nulls_odd_args_and_junk_entries_are_read_or_skipped_without_failing()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.tolerant.json"));

        Assert.Equal(new[] { "nulls-everywhere", "args-that-are-not-all-strings", "empty-args" }, Names(rows));

        var nulls = Row(rows, "nulls-everywhere");
        Assert.Equal("Configured", nulls.StateLabel);
        Assert.Null(nulls.Connector);
        Assert.Equal(new[] { "Name", "Scan" }, nulls.Fields.Select(f => f.Label).ToArray());

        // A non-string argument is shown as its JSON rather than dropped or thrown on.
        Assert.Contains(
            Row(rows, "args-that-are-not-all-strings").Fields,
            f => f is { Label: "Command", Value: "example-server --port 8080 true null [\"nested\"]" });
        Assert.Contains(Row(rows, "empty-args").Fields, f => f is { Label: "Command", Value: "example-server" });
    }

    // ------------------------------------------------------------------ plugins

    // Emitter: commands/cmd_plugin.py:1426-1464 (list_plugins: always a bare array for one connector, with or without
    // --connector; groups only for several) and 1655-1685 (_plugin_list_json_items: id, name, description, version, origin,
    // source, status, enabled, [connector], [scan], [actions], verdict). status is 1619-1630: quarantined, blocked, disabled
    // or "enabled" (a skill's healthy status is "active", a plugin's is "enabled"). The first three items below are the
    // shape captured live from this machine's Claude Code plugin store, with the paths replaced.
    [Fact]
    public void A_claude_code_plugin_list_reads_every_item_and_flags_the_stores_own_folders()
    {
        var rows = new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.claudecode.json"));

        Assert.Equal(
            new[] { "marketplaces", "cache", "known_marketplaces.json.lock", "code-review", "release-notes", "switched-off", "managed-by-defenseclaw" },
            Names(rows));
        Assert.Equal(
            new[] { "marketplaces", "cache", "known_marketplaces.json.lock" },
            Names(rows.Where(r => r.IsArtifact)));
    }

    [Fact]
    public void An_enabled_plugin_is_shown_with_the_healthy_tone_and_a_scanless_one_says_not_scanned()
    {
        var plugin = Row(new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.claudecode.json")), "code-review");

        Assert.Equal("Code Review", plugin.Title);
        Assert.Equal("Enabled", plugin.StateLabel);
        Assert.Equal("Ok", plugin.StateTone);
        Assert.Equal("Not scanned", plugin.ScanLabel);
        Assert.False(plugin.IsArtifact);
        Assert.Equal(
            GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Allow | GovernVerbs.Disable | GovernVerbs.Quarantine | GovernVerbs.Remove,
            plugin.Verbs);
        Assert.Contains("version: 1.4.2", plugin.MetaLine, StringComparison.Ordinal);
        Assert.Contains("enabled: yes", plugin.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_plugin_and_a_disabled_plugin_with_a_low_finding_are_read()
    {
        var rows = new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.claudecode.json"));

        Assert.Equal("Scan clean", Row(rows, "release-notes").ScanLabel);

        var off = Row(rows, "switched-off");
        Assert.Equal("Disabled", off.StateLabel);
        Assert.Equal("Warn", off.StateTone);
        Assert.Equal("LOW · 1 finding", off.ScanLabel);
        Assert.Equal("Low", off.ScanTone);
        Assert.True(off.NeedsAttention);
        Assert.True(off.CanEnable);
    }

    [Fact]
    public void The_live_captured_store_entries_are_quarantined_artifacts_with_only_info_and_copy()
    {
        var rows = new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.claudecode.json"));

        var marketplaces = Row(rows, "marketplaces");
        Assert.True(marketplaces.IsArtifact);
        Assert.Equal("Quarantined", marketplaces.StateLabel);
        Assert.Equal("HIGH · 23 findings", marketplaces.ScanLabel);
        Assert.Equal("file: quarantine · runtime: disable · install: block", marketplaces.ActionsText);
        Assert.Equal(GovernVerbs.Info | GovernVerbs.CopyName, marketplaces.Verbs);
    }

    [Fact]
    public void A_multi_connector_plugin_list_is_flattened_and_an_empty_group_adds_nothing()
    {
        var rows = new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.multi-connector.json"));

        Assert.Equal(
            new[] { ("code-review", "claudecode"), ("code-review", "codex") },
            rows.Select(r => (r.Name, r.Connector!)).ToArray());
        Assert.All(rows, r => Assert.False(r.IsArtifact));
    }

    [Fact]
    public void An_empty_plugin_list_is_an_empty_result_not_an_error()
    {
        Assert.Empty(new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.empty.json")));
    }

    // ------------------------------------------------------------------ tools

    // Emitter: commands/cmd_tool.py:469-553 (list_tools: always an array of {"connector","tools"} groups, plus a trailing
    // {"connector": null, "scope": "source", …} group; one {"connector","tools"} object under --connector), 601-618
    // (_tool_rows_json: name, connector, scope, status = the install action or "none", reason, updated_at as isoformat or
    // null) and 621-629 (a global row is listed under every connector's group with that connector filled in).
    [Fact]
    public void Tool_groups_give_one_row_per_rule_and_list_a_global_rule_once()
    {
        var rows = new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.groups.json"));

        Assert.Equal(new[] { "delete_file", "shell_exec", "runtime-only", "read_file" }, Names(rows));

        var delete = Row(rows, "delete_file");
        Assert.Equal("connector", delete.RuleScope);
        Assert.Equal("claudecode", delete.Connector);
        Assert.Equal("Blocked", delete.StateLabel);
        Assert.Equal("destructive", delete.Reason);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", delete.UpdatedText);

        var shell = Row(rows, "shell_exec");
        Assert.Equal("global", shell.RuleScope);
        Assert.Null(shell.Connector);
        Assert.True(shell.IsAllowed);
        Assert.Null(shell.Reason);
    }

    [Fact]
    public void A_tool_rule_with_no_install_action_and_no_timestamp_is_shown_as_none()
    {
        var none = Row(new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.groups.json")), "runtime-only");

        Assert.Equal("None", none.StateLabel);
        Assert.False(none.IsBlocked);
        Assert.False(none.IsAllowed);
        Assert.Null(none.UpdatedText);
        Assert.Null(none.Reason);
    }

    [Fact]
    public void The_source_group_with_a_null_connector_is_read_as_an_audit_only_rule()
    {
        var source = Row(new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.groups.json")), "read_file");

        Assert.Equal("source", source.RuleScope);
        Assert.Equal("docs-server", source.SourceScope);
        Assert.Null(source.Connector);
        Assert.True(source.IsBlocked);
        Assert.False(source.CanBlock);
        Assert.False(source.CanAllow);
        Assert.True(source.CanUnblock);
    }

    [Fact]
    public void A_tool_list_under_a_connector_flag_is_one_group_object_with_the_global_rule_bound_to_no_connector()
    {
        var rows = new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.connector-flag.json"));

        Assert.Equal(new[] { "delete_file", "shell_exec" }, Names(rows));
        Assert.Equal("claudecode", Row(rows, "delete_file").Connector);
        Assert.Null(Row(rows, "shell_exec").Connector);
    }

    [Fact]
    public void A_tool_list_with_an_empty_group_is_an_empty_result_not_an_error()
    {
        // Live capture: `defenseclaw tool list --json` printed exactly this on the dev machine.
        Assert.Empty(new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.empty.json")));
    }

    // ------------------------------------------------------------------ the one non-JSON answer

    // commands/__init__.py:83-119 (resolve_list_connectors), used by skill, mcp, plugin and tool list before they look at
    // --json: with no connector configured it prints one sentence and exits 0.
    public static TheoryData<string> NoConnectorAnswers { get; } = new()
    {
        "no connector configured — run 'defenseclaw setup <connector>'\n",
        "no connector configured — run 'defenseclaw setup <connector>'\r\n",
        "  No connector configured - run 'defenseclaw setup <connector>'",
    };

    [Theory]
    [MemberData(nameof(NoConnectorAnswers))]
    public void No_connector_configured_is_reported_as_that_and_not_as_unparseable_json(string stdout)
    {
        foreach (var vm in new GovernPanelViewModelBase[]
                 {
                     new SkillsPanelViewModel(_services),
                     new McpsPanelViewModel(_services),
                     new PluginsPanelViewModel(_services),
                     new ToolsPanelViewModel(_services),
                 })
        {
            var error = Assert.Throws<FormatException>(() => vm.ParseRows(stdout));
            Assert.Contains("No connector is configured", error.Message, StringComparison.Ordinal);
            Assert.Contains("defenseclaw setup", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_that_sentence_is_taken_for_the_no_connector_answer()
    {
        Assert.True(GovernJson.IsNoConnectorMessage("no connector configured — run 'defenseclaw setup <connector>'"));
        Assert.False(GovernJson.IsNoConnectorMessage("[]"));
        Assert.False(GovernJson.IsNoConnectorMessage("Traceback (most recent call last):"));
        Assert.False(GovernJson.IsNoConnectorMessage(string.Empty));
    }
}
