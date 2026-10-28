using System.Net;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Agents;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The AI BOM's IDE Plugins and Agent identities tabs (CUST-376): rows, columns, the enabled / disabled / unknown / AI chips, search, the detail pane,
/// the connector scope (identities narrow, IDE plugins are exempt), every way the gateway can fail or not have the routes, the runtime gate, and the
/// panel's read of both gateway routes. Fixtures are synthetic; the gateway is an in-memory handler behind the real client, and no process starts.
/// </summary>
public sealed class InventoryBomExtrasTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private static CorrelationRead<IdePluginPage> Ide(string fixture) => IdeInventoryReader.ParseText(AgentsScene.Read(fixture));

    private static CorrelationRead<AgentDirectoryPage> Identities(string fixture = "agents-identities.synthetic.json") => CorrelationReader.ParseDirectoryText(AgentsScene.Read(fixture));

    private static InventoryBomBrowser Loaded()
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.ShowIdePlugins(Ide("ide-plugins.synthetic.json"));
        browser.ShowIdentities(Identities());
        return browser;
    }

    private static string[] InventoryBomColumnsOf(string kindWord)
    {
        Assert.True(InventoryBomKinds.TryParse(kindWord, out var kind));
        return InventoryBomBrowser.ColumnsFor(kind, withConnector: false).Select(c => c.Header).ToArray();
    }

    private static string[] Plugins(InventoryBomBrowser browser) => browser.Rows.Select(r => r.Cells[0]).ToArray();

    private static string Field(InventoryBomBrowser browser, string label) => browser.DetailFields.Single(f => f.Key == label).Value;

    // ------------------------------------------------------------------ the IDE Plugins tab

    [Fact]
    public void The_ide_tab_lists_each_plugin_with_the_cli_columns_sorted_for_reading()
    {
        var browser = Loaded();

        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        Assert.Equal(["Plugin", "IDE", "Version", "Enabled", "AI", "User"], browser.Columns.Select(c => c.Header).ToArray());
        Assert.Equal(
            [
                "example.snippets",
                "example.themes",
                "com.example.completion (Example Completion)",
                "example.assistant (Example Assistant)",
                "example.formatter",
                "example.linter",
            ],
            Plugins(browser));
        var assistant = browser.Rows.Single(r => r.Cells[0].StartsWith("example.assistant", StringComparison.Ordinal));
        Assert.Equal(["example.assistant (Example Assistant)", "vscode 1.99.0", "2.3.1", "yes", "yes", "operator"], assistant.Cells.ToArray());
        Assert.Equal(6, browser.IdePluginsCount);
        Assert.Equal("6 shown", browser.ResultCaption);
        Assert.False(browser.ShowEmpty);
        Assert.False(browser.ShowGenerate);
    }

    [Theory]
    [InlineData("all", 6)]
    [InlineData("enabled", 3)]
    [InlineData("disabled", 1)]
    [InlineData("unknown", 2)]
    [InlineData("ai", 2)]
    public void The_chips_narrow_the_plugins_by_enabled_state_and_ai_flag(string chip, int expected)
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        browser.StatusFilter = chip;

        Assert.Equal(expected, browser.Rows.Count);
        Assert.True(browser.HasStatusFilter);
        Assert.True(browser.IsIdeTab);
    }

    [Fact]
    public void Search_matches_any_column_and_an_empty_result_says_so()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        browser.SearchText = "pycharm";
        Assert.Equal(["com.example.completion (Example Completion)"], Plugins(browser));

        browser.SearchText = "no-such-plugin";
        Assert.Empty(browser.Rows);
        Assert.Contains("Nothing matches", browser.EmptyTitle, StringComparison.Ordinal);

        browser.SearchText = string.Empty;
        browser.StatusFilter = "disabled";
        browser.SearchText = "themes";
        Assert.Empty(browser.Rows);
        Assert.Contains("Nothing matches", browser.EmptyTitle, StringComparison.Ordinal);

        // Without a search, a chip that hides every row says so.
        browser.SearchText = string.Empty;
        browser.SetConnector(null);
        browser.ShowIdePlugins(IdeInventoryReader.ParseText("""{"plugins": [{"plugin_id": "example.only", "enabled": "enabled"}]}"""));
        browser.StatusFilter = "disabled";
        Assert.Equal("No items match the current filter", browser.EmptyTitle);
    }

    [Fact]
    public void The_detail_pane_shows_the_plugin_and_its_ide_and_the_selection_survives_a_reread()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdeTab;
        browser.SelectedRow = browser.Rows.Single(r => r.Cells[0].StartsWith("example.assistant", StringComparison.Ordinal));

        Assert.Equal("IDE PLUGIN: Example Assistant", browser.DetailHeading);
        Assert.Equal("example.assistant", Field(browser, "Plugin ID"));
        Assert.Equal("example", Field(browser, "Publisher"));
        Assert.Equal("vscode 1.99.0", Field(browser, "IDE"));
        Assert.Equal("enabled", Field(browser, "Enabled"));
        Assert.Equal("yes", Field(browser, "AI extension"));
        Assert.Equal("sig-example-assistant", Field(browser, "AI signature"));
        Assert.Equal("fp-1", Field(browser, "Fingerprint"));
        Assert.True(browser.HasDetail);

        browser.ShowIdePlugins(Ide("ide-plugins.synthetic.json"));

        Assert.Equal("fp-1", browser.SelectedRow?.Extra?.Key);
    }

    [Fact]
    public void Ide_plugins_belong_to_no_connector_so_the_connector_scope_never_hides_them()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        browser.SetConnector("codex");

        Assert.Equal(6, browser.Rows.Count);
        Assert.Equal(6, browser.IdePluginsCount);
    }

    // ------------------------------------------------------------------ the Agent identities tab

    [Fact]
    public void The_identities_tab_lists_the_directory_and_the_connector_scope_narrows_it()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdentitiesTab;

        Assert.Equal(["Agent ID", "Connector", "User", "Sessions", "Last seen", "State"], browser.Columns.Select(c => c.Header).ToArray());
        Assert.Equal(["agt-0000000000000001", "agt-0000000000000002", "agt-0000000000000003"], browser.Rows.Select(r => r.Cells[0]).ToArray());
        Assert.Equal(3, browser.IdentitiesCount);
        Assert.Equal("retired account", browser.Rows[2].Cells[5]);
        Assert.Equal("observed", browser.Rows[0].Cells[5]);
        Assert.Equal("14", browser.Rows[0].Cells[3]);

        browser.SetConnector("claudecode");
        Assert.Equal(["agt-0000000000000002"], browser.Rows.Select(r => r.Cells[0]).ToArray());
        Assert.Equal(1, browser.IdentitiesCount);

        browser.SetConnector("CODEX");
        Assert.Equal(2, browser.Rows.Count);

        browser.SetConnector("gemini");
        Assert.Empty(browser.Rows);
        Assert.Equal("No agent identities for gemini", browser.EmptyTitle);
    }

    [Fact]
    public void An_identity_is_openable_in_the_agents_panel_only_where_that_panel_exists()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        var requests = 0;
        browser.OpenAgentsRequested += (_, _) => requests++;
        browser.SelectedRow = browser.Rows[0];

        Assert.False(browser.CanOpenAgents);
        browser.OpenInAgentsCommand.Execute(null);
        Assert.Equal(0, requests);

        browser.AgentsLinkAvailable = true;
        Assert.True(browser.CanOpenAgents);
        browser.OpenInAgentsCommand.Execute(null);
        Assert.Equal(1, requests);
        Assert.Equal("AGENT IDENTITY: agt-0000000000000001", browser.DetailHeading);
        Assert.Equal("14", Field(browser, "Sessions seen"));

        // A plugin has no agent to open.
        browser.ActiveTab = InventoryBomBrowser.IdeTab;
        browser.SelectedRow = browser.Rows[0];
        Assert.False(browser.CanOpenAgents);
    }

    // ------------------------------------------------------------------ what the tabs say when there is nothing

    [Fact]
    public void Before_the_first_read_the_tabs_say_they_are_reading_and_offer_no_scan()
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.BeginExtrasLoad();

        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        Assert.Equal(InventoryBomExtraState.Loading, browser.IdeState);
        Assert.Equal("Reading from the gateway", browser.EmptyTitle);
        Assert.True(browser.ShowEmpty);
        Assert.False(browser.ShowGenerate);
        Assert.Null(browser.IdePluginsCount);
    }

    [Fact]
    public void A_gateway_without_the_routes_is_not_available_on_this_host_and_never_an_error()
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        var unsupported = new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Unsupported, null, "x");
        browser.ShowIdePlugins(unsupported);
        browser.ShowIdentities(new CorrelationRead<AgentDirectoryPage>(CorrelationReadStatus.Unsupported, null, "x"));

        browser.ActiveTab = InventoryBomBrowser.IdeTab;
        Assert.Equal(InventoryBomExtraState.Unsupported, browser.IdeState);
        Assert.Equal("Not available on this host", browser.EmptyTitle);
        Assert.Contains("managed", browser.EmptyDetail, StringComparison.Ordinal);

        browser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        Assert.Equal("Not available on this host", browser.EmptyTitle);
        Assert.Contains("managed", browser.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_off_and_the_inventory_off_are_named_and_a_directory_that_is_not_kept_says_so()
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        browser.ShowIdePlugins(Ide("ide-plugins.disabled.synthetic.json"));
        Assert.Equal("AI discovery is off", browser.EmptyTitle);
        Assert.Equal("AI discovery is disabled on this gateway.", browser.EmptyDetail);

        browser.ShowIdePlugins(Ide("ide-plugins.off.synthetic.json"));
        Assert.Equal("The IDE plugin inventory is turned off", browser.EmptyTitle);
        Assert.Contains("ai_discovery.ide_inventory", browser.EmptyDetail, StringComparison.Ordinal);

        browser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        browser.ShowIdentities(Identities("agents-identities.disabled.synthetic.json"));
        Assert.Equal("Agent identities are not recorded on this host", browser.EmptyTitle);
    }

    [Fact]
    public void A_failed_read_is_not_an_empty_list_and_keeps_the_last_good_rows_marked_stale()
    {
        var browser = Loaded();
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        browser.ShowIdePlugins(new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Unreachable, null, "The gateway is not answering."));

        Assert.Equal(6, browser.Rows.Count);
        Assert.Contains("Showing the last read", browser.ResultCaption, StringComparison.Ordinal);
        Assert.Contains("not answering", browser.ResultCaption, StringComparison.Ordinal);

        var fresh = new InventoryBomBrowser();
        fresh.SetExtrasAvailable(true);
        fresh.ActiveTab = InventoryBomBrowser.IdeTab;
        fresh.ShowIdePlugins(new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Unauthorized, null, "The gateway refused this app's API token."));
        Assert.Equal(InventoryBomExtraState.Unavailable, fresh.IdeState);
        Assert.Equal("IDE plugins could not be read", fresh.EmptyTitle);
        Assert.Contains("This is not an empty list.", fresh.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_inventory_is_said_to_be_empty_not_unavailable()
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.ShowIdePlugins(IdeInventoryReader.ParseText("""{"plugins": [], "enabled": true, "scope": "all"}"""));
        browser.ActiveTab = InventoryBomBrowser.IdeTab;

        Assert.Equal(InventoryBomExtraState.Ready, browser.IdeState);
        Assert.Equal("No IDE plugins found yet", browser.EmptyTitle);
    }

    // ------------------------------------------------------------------ the gate and the other tabs

    [Fact]
    public void Where_the_runtime_lacks_the_routes_the_tabs_are_hidden_and_a_tab_that_was_open_goes_back_to_the_summary()
    {
        var browser = new InventoryBomBrowser();

        Assert.False(browser.ShowIdePluginsTab);
        Assert.False(browser.ShowIdentitiesTab);
        Assert.Null(browser.IdePluginsCount);

        browser.SetExtrasAvailable(true);
        browser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        browser.SetExtrasAvailable(false);

        Assert.Equal(InventoryBomBrowser.SummaryTab, browser.ActiveTab);
        Assert.False(browser.ShowIdePluginsTab);
    }

    [Fact]
    public void The_summary_lists_the_two_inventories_once_a_bom_is_on_screen_and_the_other_tabs_are_unchanged()
    {
        var parsed = InventoryBomSnapshot.Parse(PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));
        Assert.True(parsed.Succeeded, parsed.Message);
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.Load(parsed.Snapshot!, []);
        var without = browser.Rows.Select(r => r.Cells[0]).ToArray();
        Assert.DoesNotContain("IDE plugins", without);

        browser.ShowIdePlugins(Ide("ide-plugins.synthetic.json"));
        browser.ShowIdentities(Identities());

        var summary = browser.Rows.ToDictionary(r => r.Cells[0], r => r.Cells[1]);
        Assert.Equal("6 (2 AI, 1 disabled)", summary["IDE plugins"]);
        Assert.Equal("3", summary["Agent identities"]);

        // A tab of the scan is as it was, and its Generate offer is too.
        browser.ActiveTab = "skills";
        Assert.False(browser.IsExtrasTab);
        Assert.False(browser.IsIdeTab);
        Assert.Equal(InventoryBomColumnsOf("skills"), browser.Columns.Select(c => c.Header).ToArray());
    }

    // ------------------------------------------------------------------ the panel's read

    private sealed record Scene(InventoryPanelViewModel Vm, AgentsScene.Gateway Handler, AppServices Services);

    private async Task<Scene> OpenAsync(string runtimeSet)
    {
        var handler = new AgentsScene.Gateway();
        var client = new GatewayClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") },
            () => new SecretValue("fixture-bearer-token-0123456789"),
            ownsHttpClient: true);
        _disposables.Add(client);
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For(runtimeSet));
        _disposables.Add(services);
        _ = await services.Runtime.RefreshAsync();
        var vm = new InventoryPanelViewModel(services)
        {
            ExtrasFetch = (path, max, ct) => client.GetBoundedJsonAsync(path, max, true, ct),
        };
        return new Scene(vm, handler, services);
    }

    private static void ServeIdePlugins(AgentsScene.Gateway handler, string fixture = "ide-plugins.synthetic.json") =>
        handler.Override = r => r.RequestUri!.AbsolutePath.EndsWith("ide-plugins", StringComparison.Ordinal) ? AgentsScene.Json(AgentsScene.Read(fixture)) : null;

    [Fact]
    public async Task Showing_the_bom_reads_both_gateway_routes_with_plain_gets_and_no_scan_or_process()
    {
        var scene = await OpenAsync("95159fd");
        ServeIdePlugins(scene.Handler);

        Assert.True(scene.Vm.BomBrowser.ShowIdePluginsTab);
        Assert.True(scene.Vm.BomBrowser.AgentsLinkAvailable);

        await scene.Vm.LoadBomExtrasAsync(CancellationToken.None);

        Assert.All(scene.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(
            ["api/v1/agents/identities?limit=1000", "api/v1/ai-usage/ide-plugins?limit=1000"],
            scene.Handler.Paths.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(6, scene.Vm.BomBrowser.IdePluginsCount);
        Assert.Equal(3, scene.Vm.BomBrowser.IdentitiesCount);
        Assert.False(scene.Vm.Review.IsOpen);
    }

    [Fact]
    public async Task Switching_to_the_bom_view_starts_the_read()
    {
        var scene = await OpenAsync("95159fd");
        ServeIdePlugins(scene.Handler);

        scene.Vm.ActiveView = InventoryPanelViewModel.ViewBom;
        for (var i = 0; i < 200 && scene.Vm.BomBrowser.IdePluginsCount is null; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(6, scene.Vm.BomBrowser.IdePluginsCount);
    }

    [Fact]
    public async Task On_0_8_10_nothing_is_offered_and_no_request_is_made()
    {
        var scene = await OpenAsync("0.8.10");
        ServeIdePlugins(scene.Handler);

        Assert.False(scene.Vm.BomBrowser.ShowIdePluginsTab);
        Assert.False(scene.Vm.BomBrowser.ShowIdentitiesTab);
        Assert.False(scene.Vm.BomBrowser.AgentsLinkAvailable);

        await scene.Vm.LoadBomExtrasAsync(CancellationToken.None);

        Assert.Empty(scene.Handler.Requests);
        Assert.Null(scene.Vm.BomBrowser.IdePluginsCount);
    }

    [Fact]
    public async Task A_managed_host_that_registers_neither_route_shows_not_available_on_this_host()
    {
        var scene = await OpenAsync("95159fd");
        scene.Handler.Override = _ => AgentsScene.Json("""{"error":"not found"}""", HttpStatusCode.NotFound);

        await scene.Vm.LoadBomExtrasAsync(CancellationToken.None);

        Assert.Equal(InventoryBomExtraState.Unsupported, scene.Vm.BomBrowser.IdeState);
        Assert.Equal(InventoryBomExtraState.Unsupported, scene.Vm.BomBrowser.IdentitiesState);
        Assert.True(scene.Vm.BomBrowser.ShowIdePluginsTab);
        scene.Vm.BomBrowser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        Assert.Equal("Not available on this host", scene.Vm.BomBrowser.EmptyTitle);
        Assert.False(scene.Vm.HasBomWarning);
    }

    [Fact]
    public async Task Opening_an_identity_in_the_agents_panel_asks_the_shell_to_show_it()
    {
        var scene = await OpenAsync("95159fd");
        ServeIdePlugins(scene.Handler);
        var requested = new List<string>();
        object? payload = null;
        scene.Services.Navigation.Requested += (_, e) =>
        {
            requested.Add(e.Request.PanelId);
            payload = e.Request.Payload;
        };

        await scene.Vm.LoadBomExtrasAsync(CancellationToken.None);
        scene.Vm.BomBrowser.ActiveTab = InventoryBomBrowser.IdentitiesTab;
        scene.Vm.BomBrowser.SelectedRow = scene.Vm.BomBrowser.Rows[0];
        scene.Vm.BomBrowser.OpenInAgentsCommand.Execute(null);

        Assert.Equal(["agents"], requested);
        Assert.Equal(new DefenseClaw.App.Services.AgentFocus("agt-0000000000000001"), payload);
    }
}
