using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// What a Govern row action asks the operator to confirm. Every mutation shows its exact argv first
/// (<see cref="GovernPanelViewModelBase.ConfirmCommandText"/>); nothing runs here, because the confirm step is
/// the only thing that would start a process, and these tests never press it.
/// </summary>
public sealed class GovernPanelArgvTests : IDisposable
{
    private const string AllConnectors = "All configured connectors";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public GovernPanelArgvTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static void Act(GovernPanelViewModelBase vm, GovernRow row, GovernVerbs verb) =>
        ((IGovernRowHost)vm).OnRowAction(row, verb);

    private static GovernRow Skill(SkillsPanelViewModel vm, string json) => Assert.Single(vm.ParseRows(json));

    // ------------------------------------------------------------------ verbs and the target after "--"

    [Theory]
    [InlineData(GovernVerbs.Block, "block")]
    [InlineData(GovernVerbs.Allow, "allow")]
    [InlineData(GovernVerbs.Unblock, "unblock")]
    [InlineData(GovernVerbs.Disable, "disable")]
    [InlineData(GovernVerbs.Enable, "enable")]
    [InlineData(GovernVerbs.Quarantine, "quarantine")]
    [InlineData(GovernVerbs.Restore, "restore")]
    public void Each_verb_becomes_noun_verb_scope_then_the_target_after_a_double_dash(GovernVerbs verb, string word)
    {
        var vm = new SkillsPanelViewModel(_services);
        var row = Skill(vm, """{"connector": "claudecode", "skills": [{"name": "pdf-tools"}]}""");

        Act(vm, row, verb);

        Assert.True(vm.IsConfirmOpen);
        Assert.Equal($"defenseclaw skill {word} --connector claudecode -- pdf-tools", vm.ConfirmCommandText);
    }

    [Fact]
    public void A_target_that_starts_with_a_dash_is_a_name_never_an_option()
    {
        var vm = new SkillsPanelViewModel(_services);
        var row = Skill(vm, """[{"name": "--dangerous"}]""");

        Act(vm, row, GovernVerbs.Block);

        Assert.Equal("defenseclaw skill block -- --dangerous", vm.ConfirmCommandText);
    }

    [Fact]
    public void A_target_that_spells_a_flag_or_a_verb_does_not_change_how_the_command_is_reviewed()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Skill(vm, """[{"name": "--help"}]"""), GovernVerbs.Quarantine);
        Assert.Equal("defenseclaw skill quarantine -- --help", vm.ConfirmCommandText);
        Assert.True(vm.IsConfirmDestructive);

        vm.ConfirmNoCommand.Execute(null);
        Act(vm, Skill(vm, """[{"name": "list"}]"""), GovernVerbs.Block);
        Assert.Equal("defenseclaw skill block -- list", vm.ConfirmCommandText);
        Assert.False(vm.IsConfirmDestructive);
        Assert.Equal("Changes state", vm.ConfirmTierText);
    }

    [Fact]
    public void A_target_with_a_space_is_quoted_in_the_review_but_stays_one_argument()
    {
        var vm = new SkillsPanelViewModel(_services);
        var row = Skill(vm, """[{"name": "my skill"}]""");

        Act(vm, row, GovernVerbs.Allow);

        Assert.Equal("defenseclaw skill allow -- \"my skill\"", vm.ConfirmCommandText);
    }

    [Fact]
    public void A_reason_is_sent_only_with_the_verbs_whose_help_lists_it()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.Reason = "not vetted";
        var row = Skill(vm, """{"connector": "codex", "skills": [{"name": "x"}]}""");

        Act(vm, row, GovernVerbs.Block);
        Assert.Equal("defenseclaw skill block --reason \"not vetted\" --connector codex -- x", vm.ConfirmCommandText);

        vm.ConfirmNoCommand.Execute(null);
        Act(vm, row, GovernVerbs.Quarantine);
        Assert.Equal("defenseclaw skill quarantine --reason \"not vetted\" --connector codex -- x", vm.ConfirmCommandText);

        foreach (var verb in new[] { GovernVerbs.Unblock, GovernVerbs.Enable, GovernVerbs.Restore })
        {
            vm.ConfirmNoCommand.Execute(null);
            Act(vm, row, verb);
            Assert.DoesNotContain("--reason", vm.ConfirmCommandText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_blank_reason_sends_no_reason_flag()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.Reason = "   ";

        Act(vm, Skill(vm, """{"connector": "codex", "skills": [{"name": "x"}]}"""), GovernVerbs.Block);

        Assert.DoesNotContain("--reason", vm.ConfirmCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_carries_the_heading_and_the_tier()
    {
        var vm = new SkillsPanelViewModel(_services);
        var row = Skill(vm, """{"connector": "codex", "skills": [{"name": "x"}]}""");

        Act(vm, row, GovernVerbs.Block);

        Assert.Contains("Block skill “x” for connector “codex”?", vm.ConfirmHeading, StringComparison.Ordinal);
        Assert.Equal("Changes state", vm.ConfirmTierText);
        Assert.False(vm.IsConfirmDestructive);
    }

    [Theory]
    [InlineData(GovernVerbs.Quarantine)]
    [InlineData(GovernVerbs.Remove)]
    public void Verbs_that_remove_or_move_files_are_reviewed_as_destructive(GovernVerbs verb)
    {
        var vm = new PluginsPanelViewModel(_services);
        var row = Assert.Single(vm.ParseRows("""[{"id": "p", "connector": "claudecode"}]"""));

        Act(vm, row, verb);

        Assert.True(vm.IsConfirmOpen);
        Assert.True(vm.IsConfirmDestructive);
        Assert.Equal("Destructive", vm.ConfirmTierText);
    }

    [Fact]
    public void Nothing_is_confirmed_until_the_operator_says_so_and_declining_clears_the_review()
    {
        var vm = new SkillsPanelViewModel(_services);
        Act(vm, Skill(vm, """[{"name": "x"}]"""), GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);

        vm.ConfirmNoCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
    }

    [Fact]
    public void A_verb_the_panel_does_not_map_opens_no_review()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Skill(vm, """[{"name": "x"}]"""), GovernVerbs.None);

        Assert.False(vm.IsConfirmOpen);
    }

    // ------------------------------------------------------------------ row connector vs toolbar scope

    [Fact]
    public void A_row_that_names_its_connector_is_acted_on_there_whatever_the_toolbar_says()
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.SelectedConnector = "codex";
        var row = Assert.Single(vm.ParseRows("""[{"id": "p", "connector": "claudecode"}]"""));

        Act(vm, row, GovernVerbs.Block);

        Assert.Equal("defenseclaw plugin block --connector claudecode -- p", vm.ConfirmCommandText);
        Assert.Contains("connector “claudecode”", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_connector_takes_the_toolbars()
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.SelectedConnector = "codex";
        var row = Assert.Single(vm.ParseRows("""[{"id": "p"}]"""));

        Act(vm, row, GovernVerbs.Block);

        Assert.Equal("defenseclaw plugin block --connector codex -- p", vm.ConfirmCommandText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(AllConnectors)]
    public void A_row_with_no_connector_under_the_all_connectors_toolbar_targets_all_of_them(string? toolbar)
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.SelectedConnector = toolbar;
        var row = Assert.Single(vm.ParseRows("""[{"id": "p"}]"""));

        Act(vm, row, GovernVerbs.Block);

        Assert.Equal("defenseclaw plugin block -- p", vm.ConfirmCommandText);
        Assert.Contains("ALL configured connectors", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ tools: the rule decides, not the toolbar

    private const string ToolRules = """
        [
          {"connector": "claudecode", "tools": [
            {"name": "delete_file", "connector": "claudecode", "scope": "connector", "status": "block"},
            {"name": "shell_exec", "connector": "claudecode", "scope": "global", "status": "allow"}
          ]},
          {"connector": null, "scope": "source", "tools": [
            {"name": "docs-server/read_file", "scope": "source", "status": "block"}
          ]}
        ]
        """;

    private static GovernRow Rule(ToolsPanelViewModel vm, string name) => vm.ParseRows(ToolRules).Single(r => r.Name == name);

    [Fact]
    public void A_connector_rule_is_changed_with_its_own_connector_even_when_the_toolbar_names_another()
    {
        var vm = new ToolsPanelViewModel(_services);
        vm.SelectedConnector = "codex";

        Act(vm, Rule(vm, "delete_file"), GovernVerbs.Unblock);

        Assert.Equal("defenseclaw tool unblock --connector claudecode -- delete_file", vm.ConfirmCommandText);
    }

    [Fact]
    public void A_global_rule_carries_no_scope_flag_even_when_the_toolbar_is_scoped()
    {
        var vm = new ToolsPanelViewModel(_services);
        vm.SelectedConnector = "codex";

        Act(vm, Rule(vm, "shell_exec"), GovernVerbs.Block);

        Assert.Equal("defenseclaw tool block -- shell_exec", vm.ConfirmCommandText);
        Assert.Contains("ALL configured connectors", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_global_rule_says_it_clears_every_connectors_override_too()
    {
        var vm = new ToolsPanelViewModel(_services);

        Act(vm, Rule(vm, "shell_exec"), GovernVerbs.Unblock);

        Assert.Equal("defenseclaw tool unblock -- shell_exec", vm.ConfirmCommandText);
        Assert.Contains("every connector-specific override", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_rule_is_changed_with_source_and_the_bare_tool_name()
    {
        var vm = new ToolsPanelViewModel(_services);
        vm.SelectedConnector = "codex";

        Act(vm, Rule(vm, "read_file"), GovernVerbs.Unblock);

        Assert.Equal("defenseclaw tool unblock --source docs-server -- read_file", vm.ConfirmCommandText);
        Assert.Contains("audit only", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void Managing_a_tool_by_name_follows_the_toolbar_and_sends_the_bare_name_after_the_dashes()
    {
        var vm = new ToolsPanelViewModel(_services);
        vm.ManageToolName = "  delete_file  ";

        vm.ManageBlockCommand.Execute(null);
        Assert.Equal("defenseclaw tool block -- delete_file", vm.ConfirmCommandText);

        vm.ConfirmNoCommand.Execute(null);
        vm.SelectedConnector = "codex";
        vm.ManageAllowCommand.Execute(null);
        Assert.Equal("defenseclaw tool allow --connector codex -- delete_file", vm.ConfirmCommandText);

        vm.ConfirmNoCommand.Execute(null);
        vm.ManageUnblockCommand.Execute(null);
        Assert.Equal("defenseclaw tool unblock --connector codex -- delete_file", vm.ConfirmCommandText);
    }

    [Theory]
    [InlineData("", "Enter a tool name first")]
    [InlineData("   ", "Enter a tool name first")]
    [InlineData("@codex/delete_file", "bare tool name")]
    [InlineData("codex/delete_file", "bare tool name")]
    public void Managing_a_tool_by_name_refuses_an_empty_name_or_a_connector_prefix(string typed, string expectedError)
    {
        var vm = new ToolsPanelViewModel(_services);
        vm.ManageToolName = typed;

        vm.ManageBlockCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.HasManageError);
        Assert.Contains(expectedError, vm.ManageError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ mcp: unset and the add form

    [Fact]
    public void Removing_an_mcp_server_from_the_config_is_reviewed_as_destructive()
    {
        var vm = new McpsPanelViewModel(_services);
        var row = Assert.Single(vm.ParseRows("""{"connector": "claudecode", "mcp_servers": [{"name": "docs"}]}"""));

        Act(vm, row, GovernVerbs.Unset);

        Assert.Equal("defenseclaw mcp unset --connector claudecode -- docs", vm.ConfirmCommandText);
        Assert.True(vm.IsConfirmDestructive);
    }

    [Fact]
    public void The_add_form_builds_set_with_the_name_last_and_the_toolbar_scope()
    {
        var vm = new McpsPanelViewModel(_services);
        vm.SelectedConnector = "codex";
        vm.SetName = "docs";
        vm.SetCommand = "npx";
        vm.SetArgs = "-y\n@example/docs-mcp";
        vm.SetTransport = "stdio";
        vm.SetEnv = "LOG_LEVEL=debug";

        vm.SubmitSetFormCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SetFormError);
        Assert.True(vm.IsConfirmOpen);
        Assert.Equal(
            "defenseclaw mcp set --command npx --args [\"-y\",\"@example/docs-mcp\"] --transport stdio --env LOG_LEVEL=debug --connector codex -- docs",
            vm.ConfirmCommandText);
        Assert.Contains("Environment values are part of this command line", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_add_form_for_a_remote_server_sends_the_url_and_can_skip_the_scan()
    {
        var vm = new McpsPanelViewModel(_services);
        vm.SetName = "remote";
        vm.SetUrl = "https://mcp.example.test/sse";
        vm.SetSkipScan = true;

        vm.SubmitSetFormCommand.Execute(null);

        Assert.Equal(
            "defenseclaw mcp set --url https://mcp.example.test/sse --skip-scan -- remote",
            vm.ConfirmCommandText);
        Assert.Contains("no check at all", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void An_argument_containing_a_comma_or_space_stays_one_array_element()
    {
        var vm = new McpsPanelViewModel(_services);
        vm.SetName = "n";
        vm.SetCommand = "tool";
        vm.SetArgs = "--flag=a,b";

        vm.SubmitSetFormCommand.Execute(null);

        Assert.Contains("--args [\"--flag=a,b\"]", vm.ConfirmCommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "npx", "", "", "", "", "Enter a server name")]
    [InlineData("n", "", "", "", "", "", "either a command")]
    [InlineData("n", "npx", "https://h.example.test", "", "", "", "not both")]
    [InlineData("n", "", "ftp://h.example.test", "", "", "", "http:// or https://")]
    [InlineData("n", "", "not a url", "", "", "", "http:// or https://")]
    [InlineData("n", "", "https://h.example.test", "stdio", "", "", "stdio transport")]
    [InlineData("n", "npx", "", "sse", "", "", "sse transport")]
    [InlineData("n", "npx", "", "", "[not json", "", "not valid JSON")]
    [InlineData("n", "", "https://h.example.test", "", "-y", "", "Arguments belong to a command")]
    [InlineData("n", "npx", "", "", "", "no-equals-sign", "is not KEY=VAL")]
    [InlineData("n", "npx", "", "", "", "1BAD=x", "is not KEY=VAL")]
    [InlineData("n", "npx", "", "", "", "=novalue", "is not KEY=VAL")]
    public void The_add_form_refuses_what_the_cli_would_reject_without_opening_a_review(
        string name, string command, string url, string transport, string args, string env, string expectedError)
    {
        var vm = new McpsPanelViewModel(_services);
        vm.SetName = name;
        vm.SetCommand = command;
        vm.SetUrl = url;
        vm.SetArgs = args;
        vm.SetEnv = env;
        if (transport.Length > 0)
        {
            vm.SetTransport = transport;
        }

        vm.SubmitSetFormCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.HasSetFormError);
        Assert.Contains(expectedError, vm.SetFormError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ plugins: install

    [Fact]
    public void Installing_a_plugin_puts_the_source_after_the_double_dash_and_force_raises_the_tier()
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.SelectedConnector = "codex";
        vm.InstallNameOrPath = "  clawhub://example/pkg  ";
        vm.InstallForce = true;
        vm.InstallApplyActionPolicy = true;

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.Equal(
            "defenseclaw plugin install --force --action --connector codex -- clawhub://example/pkg",
            vm.ConfirmCommandText);
        Assert.True(vm.IsConfirmDestructive);
        Assert.Contains("Force overwrites", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_a_plugin_without_force_is_a_state_change_and_without_a_toolbar_scope_targets_every_connector()
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.InstallNameOrPath = "./local-plugin";

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.Equal("defenseclaw plugin install -- ./local-plugin", vm.ConfirmCommandText);
        Assert.False(vm.IsConfirmDestructive);
        Assert.Contains("ALL configured connectors", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_with_no_source_shows_an_error_and_opens_no_review()
    {
        var vm = new PluginsPanelViewModel(_services);
        vm.InstallNameOrPath = "   ";

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.HasInstallFormError);
    }

    [Fact]
    public void Escape_closes_the_review_first_and_then_the_panels_own_form()
    {
        var vm = new McpsPanelViewModel(_services);
        vm.ToggleSetFormCommand.Execute(null);
        Assert.True(vm.IsSetFormOpen);
        vm.SetName = "n";
        vm.SetCommand = "npx";
        vm.SubmitSetFormCommand.Execute(null);
        Assert.True(vm.IsConfirmOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.IsSetFormOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsSetFormOpen);
        Assert.False(vm.HandleEscape());
    }
}

public class GovernRowTests
{
    private sealed class RecordingHost : IGovernRowHost
    {
        public List<(GovernRow Row, GovernVerbs Verb)> Calls { get; } = new();

        public void OnRowAction(GovernRow row, GovernVerbs verb) => Calls.Add((row, verb));
    }

    private static GovernRow Row(RecordingHost host, string name = "x", string? connector = null, string? scope = null, string? source = null) => new(host)
    {
        Noun = "skill",
        Name = name,
        Connector = connector,
        RuleScope = scope,
        SourceScope = source,
    };

    [Fact]
    public void The_key_separates_the_same_name_under_different_scopes()
    {
        var host = new RecordingHost();

        Assert.Equal("|claudecode||x", Row(host, connector: "claudecode").Key);
        Assert.NotEqual(Row(host, connector: "claudecode").Key, Row(host, connector: "codex").Key);
        Assert.NotEqual(Row(host, scope: "global").Key, Row(host, scope: "source", source: "s").Key);
    }

    [Fact]
    public void The_action_command_maps_a_verb_name_to_the_host_ignoring_case_and_nonsense()
    {
        var host = new RecordingHost();
        var row = Row(host);

        row.ActionCommand.Execute("Block");
        row.ActionCommand.Execute("quarantine");
        row.ActionCommand.Execute("None");
        row.ActionCommand.Execute("bogus");
        row.ActionCommand.Execute(null);

        Assert.Equal(new[] { GovernVerbs.Block, GovernVerbs.Quarantine }, host.Calls.Select(c => c.Verb).ToArray());
        Assert.All(host.Calls, c => Assert.Same(row, c.Row));
    }

    [Fact]
    public void A_row_requires_a_host()
    {
        Assert.Throws<ArgumentNullException>(() => new GovernRow(null!) { Noun = "skill", Name = "x" });
    }

    [Fact]
    public void Search_text_is_lower_case_and_covers_the_visible_facts()
    {
        var host = new RecordingHost();
        var row = new GovernRow(host)
        {
            Noun = "plugin",
            Name = "Code-Review",
            Title = "Code Review",
            Connector = "ClaudeCode",
            StateLabel = "Blocked",
            Reason = "Too Risky",
        };

        Assert.Contains("code-review", row.SearchText, StringComparison.Ordinal);
        Assert.Contains("claudecode", row.SearchText, StringComparison.Ordinal);
        Assert.Contains("blocked", row.SearchText, StringComparison.Ordinal);
        Assert.Contains("too risky", row.SearchText, StringComparison.Ordinal);
        Assert.Equal(row.SearchText, row.SearchText.ToLowerInvariant());
    }

    [Fact]
    public void The_spoken_name_names_the_item_its_connector_and_state()
    {
        var host = new RecordingHost();
        var row = new GovernRow(host)
        {
            Noun = "skill",
            Name = "pdf-tools",
            Connector = "codex",
            StateLabel = "Blocked",
            ScanLabel = "HIGH · 3 findings",
        };

        Assert.Equal("skill pdf-tools, connector codex, Blocked, HIGH · 3 findings", row.AutomationName);
        Assert.Equal(row.AutomationName, row.ToString());
    }

    [Fact]
    public void The_verbs_flags_drive_which_buttons_the_row_shows()
    {
        var host = new RecordingHost();
        var row = new GovernRow(host)
        {
            Noun = "skill",
            Name = "x",
            Verbs = GovernVerbs.Info | GovernVerbs.Block | GovernVerbs.Remove,
        };

        Assert.True(row.CanInfo);
        Assert.True(row.CanBlock);
        Assert.True(row.CanRemove);
        Assert.False(row.CanAllow);
        Assert.False(row.CanUnblock);
        Assert.False(row.CanDisable);
        Assert.False(row.CanEnable);
        Assert.False(row.CanQuarantine);
        Assert.False(row.CanRestore);
        Assert.False(row.CanUnset);
    }
}
