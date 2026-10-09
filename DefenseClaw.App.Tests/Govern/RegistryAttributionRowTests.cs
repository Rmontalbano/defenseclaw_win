using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// Registry attribution on the Skills and MCPs rows (CUST-276): a skill or an MCP server a registry promoted into the allow policy carries the
/// <c>registry:&lt;id&gt;</c> badge and the "Open in Registries" action, which sends the Registries panel a <see cref="RegistryFocus"/>. The
/// attribution is the TUI's: the <c>asset_policy.skill.registry</c> / <c>asset_policy.mcp.registry</c> rules of config.yaml whose <c>reason</c> is
/// <c>registry:&lt;id&gt;</c>. Everything is synthetic: a config written into a scratch folder, the fixtures of <c>Fixtures/CliPayloads</c>, and a scripted CLI.
/// </summary>
public sealed class RegistryAttributionRowTests : IDisposable
{
    /// <summary>pdf-tools and notes-helper are skills the first registry promoted; docs-search and sse-server are MCP servers two registries promoted.</summary>
    private const string Config = """
        config_version: 8
        claw:
          mode: claudecode
        asset_policy:
          enabled: true
          skill:
            registry:
              - name: pdf-tools
                reason: registry:corp-skills
                url: https://registry.example.test/skills/pdf-tools.tar.gz
              - name: notes-helper
                reason: registry:corp-skills
              - name: risky-skill
                reason: approved by hand
          mcp:
            registry:
              - name: docs-search
                connector: claudecode
                reason: registry:corp-skills
                command: npx
              - name: sse-server
                reason: registry:team.catalog
        """;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public RegistryAttributionRowTests()
    {
        _services = TestServices.Create(_temp, Config);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static GovernRow Row(IEnumerable<GovernRow> rows, string name) => Assert.Single(rows, r => r.Name == name);

    private static CliInvocation Result(IReadOnlyList<string> argv, string stdout)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, stdout);
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    private static void Script(GovernPanelViewModelBase vm, string listJson, List<string>? calls = null) =>
        vm.RunCli = (argv, _) =>
        {
            calls?.Add(string.Join(' ', argv));
            return Task.FromResult(Result(argv, argv.Count >= 2 && argv[1] == "list" ? listJson : "{}"));
        };

    // ------------------------------------------------------------------ the badge

    [Fact]
    public void A_skill_a_registry_promoted_shows_the_registry_badge_and_offers_the_open_action()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        var pdf = Row(rows, "pdf-tools");
        Assert.Equal("corp-skills", pdf.RegistrySource);
        Assert.True(pdf.HasRegistry);
        Assert.Equal("registry:corp-skills", pdf.RegistryBadge);
        Assert.Equal("registry:corp-skills", pdf.RegistryFull);
        Assert.True(pdf.CanOpenInRegistries);
        Assert.True(pdf.Verbs.HasFlag(GovernVerbs.OpenInRegistries));
        Assert.Contains(pdf.Fields, f => f is { Label: "Registry", Value: "registry:corp-skills" });
        Assert.Contains("corp-skills", pdf.RegistryToolTip, StringComparison.Ordinal);
        Assert.Contains("Registries", pdf.RegistryToolTip, StringComparison.Ordinal);
        Assert.Contains("promoted by registry corp-skills", pdf.AutomationName, StringComparison.Ordinal);
        Assert.Contains("registry corp-skills", pdf.RegistryAutomationName, StringComparison.Ordinal);

        // The row keeps every verb it had on a machine with no registry rules; the action is the one addition.
        using var bare = new TempDirectory();
        using var bareServices = TestServices.Create(bare);
        var plain = Row(new SkillsPanelViewModel(bareServices).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");
        Assert.False(plain.HasRegistry);
        Assert.Equal(plain.Verbs | GovernVerbs.OpenInRegistries, pdf.Verbs);
        Assert.Equal(
            GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Unblock | GovernVerbs.Disable | GovernVerbs.Quarantine | GovernVerbs.OpenInRegistries,
            pdf.Verbs);
    }

    [Fact]
    public void A_skill_no_registry_promoted_is_exactly_the_row_it_always_was()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        // risky-skill has a rule, but an operator's: its reason is not registry:<id>.
        foreach (var name in new[] { "risky-skill", "draft-skill", "paused-skill", "résumé-助手" })
        {
            var row = Row(rows, name);
            Assert.Null(row.RegistrySource);
            Assert.False(row.HasRegistry);
            Assert.Equal(string.Empty, row.RegistryBadge);
            Assert.Equal(string.Empty, row.RegistryFull);
            Assert.False(row.CanOpenInRegistries);
            Assert.DoesNotContain(row.Fields, f => f.Label == "Registry");
            Assert.DoesNotContain("registry", row.AutomationName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("registry", row.SearchText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_mcp_server_a_registry_promoted_shows_the_badge_of_its_own_source()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json"));

        var docs = Row(rows, "docs-search");
        Assert.Equal("corp-skills", docs.RegistrySource);
        Assert.Equal("registry:corp-skills", docs.RegistryBadge);
        Assert.True(docs.CanOpenInRegistries);
        Assert.Contains(docs.Fields, f => f is { Label: "Registry", Value: "registry:corp-skills" });
        Assert.Equal(
            GovernVerbs.CopyName | GovernVerbs.Scan | GovernVerbs.Block | GovernVerbs.Allow | GovernVerbs.Unset | GovernVerbs.OpenInRegistries,
            docs.Verbs);

        var sse = Row(rows, "sse-server");
        Assert.Equal("team.catalog", sse.RegistrySource);
        Assert.Equal("registry:team.catalog", sse.RegistryBadge);

        var remote = Row(rows, "remote-tools");
        Assert.Null(remote.RegistrySource);
        Assert.False(remote.CanOpenInRegistries);
        Assert.DoesNotContain(remote.Fields, f => f.Label == "Registry");
    }

    [Fact]
    public void A_skill_and_an_mcp_server_with_the_same_name_are_attributed_by_their_own_kind()
    {
        // pdf-tools is promoted as a skill; an MCP server of that name is not.
        var mcps = new McpsPanelViewModel(_services).ParseRows("""[{"name": "pdf-tools", "transport": "stdio", "command": "x"}, {"name": "notes-helper", "transport": "sse", "url": "https://mcp.example.test/s"}]""");
        Assert.All(mcps, r => Assert.Null(r.RegistrySource));

        var skills = new SkillsPanelViewModel(_services).ParseRows("""[{"name": "docs-search"}, {"name": "sse-server"}]""");
        Assert.All(skills, r => Assert.Null(r.RegistrySource));
    }

    [Fact]
    public void Plugins_and_tools_are_never_attributed_to_a_registry()
    {
        // A registry only ever promotes skills and MCP servers: a plugin called pdf-tools is not one of them.
        var plugin = new PluginsPanelViewModel(_services).ParseRows("""[{"id": "pdf-tools", "name": "pdf-tools", "connector": "claudecode", "enabled": true}]""");
        Assert.All(plugin, r =>
        {
            Assert.Null(r.RegistrySource);
            Assert.False(r.CanOpenInRegistries);
        });

        var tools = new ToolsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("tool-list.groups.json"));
        Assert.NotEmpty(tools);
        Assert.All(tools, r => Assert.False(r.HasRegistry));
    }

    [Fact]
    public void A_long_source_id_is_cut_in_the_badge_as_the_tui_cuts_it_and_whole_everywhere_else()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, """
            asset_policy:
              skill:
                registry:
                  - name: pdf-tools
                    reason: registry:enterprise-skills-catalog-prod
            """);

        var pdf = Row(new SkillsPanelViewModel(services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");

        Assert.Equal("registry:enterprise-skil...", pdf.RegistryBadge);
        Assert.Equal("registry:enterprise-skills-catalog-prod", pdf.RegistryFull);
        Assert.Contains("“enterprise-skills-catalog-prod”", pdf.RegistryToolTip, StringComparison.Ordinal);
        Assert.Contains(pdf.Fields, f => f is { Label: "Registry", Value: "registry:enterprise-skills-catalog-prod" });
    }

    [Fact]
    public void A_source_id_with_control_characters_is_spelled_out_not_drawn()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, "asset_policy:\n  skill:\n    registry:\n      - name: pdf-tools\n        reason: \"registry:evil\\u202Eid\"\n");

        var pdf = Row(new SkillsPanelViewModel(services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");

        // The badge, the details and the tooltip show the escape; the id the navigation carries is the real one.
        Assert.Equal("registry:evil‮id", "registry:" + pdf.RegistrySource);
        Assert.Equal("registry:evil\\u202Eid", pdf.RegistryBadge);
        Assert.Equal("registry:evil\\u202Eid", pdf.RegistryFull);
        Assert.DoesNotContain('‮', pdf.RegistryToolTip);
        Assert.DoesNotContain('‮', pdf.AutomationName);
    }

    [Fact]
    public async Task The_filter_finds_the_promoted_rows_by_the_word_registry_or_by_their_source()
    {
        var vm = new SkillsPanelViewModel(_services);
        Script(vm, PayloadFixtures.Read("skill-list.single-connector.json"));

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(GovernState.Loaded, vm.State);

        vm.FilterText = "registry:corp";
        Assert.Equal(new[] { "pdf-tools", "notes-helper" }, vm.Rows.Select(r => r.Name).ToArray());

        vm.FilterText = "CORP-SKILLS";
        Assert.Equal(new[] { "pdf-tools", "notes-helper" }, vm.Rows.Select(r => r.Name).ToArray());

        vm.FilterText = "team.catalog";
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task A_list_read_after_the_attribution_changed_shows_the_new_badge()
    {
        var vm = new SkillsPanelViewModel(_services);
        Script(vm, PayloadFixtures.Read("skill-list.single-connector.json"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("corp-skills", Row(vm.Rows, "pdf-tools").RegistrySource);

        // A registry sync rewrote config.yaml: the next read is attributed from the document the app has loaded then.
        var files = new ConfigEdits(_services);
        File.WriteAllText(
            _services.Paths.ConfigFilePath,
            "asset_policy:\n  skill:\n    registry:\n      - name: pdf-tools\n        reason: registry:team.catalog\n      - name: draft-skill\n        reason: registry:team.catalog\n");
        files.Reload();
        await vm.RefreshCommand.ExecuteAsync(null);

        // Same JSON, same verbs: the row is replaced because its registry differs.
        Assert.Equal("team.catalog", Row(vm.Rows, "pdf-tools").RegistrySource);
        Assert.Equal("team.catalog", Row(vm.Rows, "draft-skill").RegistrySource);
        Assert.Null(Row(vm.Rows, "notes-helper").RegistrySource);
        Assert.False(Row(vm.Rows, "notes-helper").CanOpenInRegistries);
    }

    // ------------------------------------------------------------------ Open in Registries

    [Fact]
    public void The_open_action_navigates_to_the_registries_panel_with_the_entry_and_its_source_and_runs_nothing()
    {
        var vm = new SkillsPanelViewModel(_services);
        var calls = new List<string>();
        Script(vm, "[]", calls);
        var pdf = Row(vm.ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");

        pdf.ActionCommand.Execute("OpenInRegistries");

        var request = Assert.IsType<NavigationRequest>(_services.Navigation.Pending);
        Assert.Equal("registries", request.PanelId);
        Assert.Equal(new RegistryFocus("skill", "pdf-tools", "corp-skills"), request.Payload);
        Assert.Empty(calls);
        Assert.Empty(_services.Cli.Activity);
        Assert.False(vm.IsConfirmOpen);
    }

    [Fact]
    public void An_mcp_servers_action_names_the_mcp_kind_and_its_source()
    {
        var vm = new McpsPanelViewModel(_services);
        var sse = Row(vm.ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json")), "sse-server");

        sse.ActionCommand.Execute("OpenInRegistries");

        Assert.Equal(new RegistryFocus("mcp", "sse-server", "team.catalog"), _services.Navigation.Pending!.Payload);
    }

    [Fact]
    public void A_row_no_registry_promoted_cannot_be_opened_in_registries()
    {
        var vm = new SkillsPanelViewModel(_services);
        var risky = Row(vm.ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "risky-skill");

        risky.ActionCommand.Execute("OpenInRegistries");

        Assert.Null(_services.Navigation.Pending);
    }

    [Fact]
    public void The_open_action_is_a_navigation_so_no_gate_holds_it_back_while_every_change_stays_gated()
    {
        // A list that failed to read, a command running, and an installation that may not be changed: Block is refused, the link is not.
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var vm = new SkillsPanelViewModel(_services);
        var pdf = Row(vm.ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");
        vm.Trust.MarkFailed("could not read the list");
        vm.IsBusy = true;
        Assert.False(vm.IsDataTrusted);

        pdf.ActionCommand.Execute("OpenInRegistries");
        Assert.Equal(new RegistryFocus("skill", "pdf-tools", "corp-skills"), _services.Navigation.Pending!.Payload);

        // ... and what it must not disturb: a change on the same row is still refused for the same reasons.
        vm.IsBusy = false;
        pdf.ActionCommand.Execute("Block");
        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Equal(TestInstallations.ManagedReason, vm.ResultMessage);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public void The_menu_verb_comes_through_the_rows_command_by_name_and_an_unknown_name_is_ignored()
    {
        var vm = new SkillsPanelViewModel(_services);
        var pdf = Row(vm.ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")), "pdf-tools");

        pdf.ActionCommand.Execute("openinregistries"); // the verb is parsed without regard to case, like every other
        Assert.NotNull(_services.Navigation.Pending);
        _services.Navigation.Discard(_services.Navigation.Pending!);

        pdf.ActionCommand.Execute("OpenElsewhere");
        Assert.Null(_services.Navigation.Pending);
    }
}
