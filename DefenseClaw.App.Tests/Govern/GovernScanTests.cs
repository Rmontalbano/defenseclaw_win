using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// Scan (one row, or every skill) and Install skill (CUST-212). A scan runs the scanner and writes scan results and an audit
/// event, so it is reviewed like every other mutation: these tests only read the review's argv and never confirm it, so no
/// process starts.
/// </summary>
public sealed class GovernScanTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public GovernScanTests()
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

    private static GovernRow Only(GovernPanelViewModelBase vm, string json) => Assert.Single(vm.ParseRows(json));

    // ------------------------------------------------------------------ one row

    [Fact]
    public void Scanning_a_skill_reviews_noun_scan_connector_then_the_name_after_a_double_dash()
    {
        var vm = new SkillsPanelViewModel(_services);
        var row = Only(vm, """{"connector": "claudecode", "skills": [{"name": "pdf-tools"}]}""");

        Act(vm, row, GovernVerbs.Scan);

        Assert.True(vm.IsConfirmOpen);
        Assert.Equal("defenseclaw skill scan --connector claudecode -- pdf-tools", vm.ConfirmCommandText);
        Assert.Equal("Changes state", vm.ConfirmTierText);
        Assert.False(vm.IsConfirmDestructive);
        Assert.Contains("records the results", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Scanning_an_mcp_server_and_a_plugin_use_their_own_noun()
    {
        var mcps = new McpsPanelViewModel(_services);
        Act(mcps, Only(mcps, """{"connector": "codex", "mcp_servers": [{"name": "github"}]}"""), GovernVerbs.Scan);
        Assert.Equal("defenseclaw mcp scan --connector codex -- github", mcps.ConfirmCommandText);

        var plugins = new PluginsPanelViewModel(_services);
        Act(plugins, Only(plugins, """[{"id": "demo", "name": "demo", "connector": "codex"}]"""), GovernVerbs.Scan);
        Assert.Equal("defenseclaw plugin scan --connector codex -- demo", plugins.ConfirmCommandText);
    }

    [Fact]
    public void A_scan_of_a_name_that_looks_like_an_option_is_a_name_never_an_option()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Only(vm, """[{"name": "--all"}]"""), GovernVerbs.Scan);

        Assert.Equal("defenseclaw skill scan -- --all", vm.ConfirmCommandText);
        Assert.Equal("Changes state", vm.ConfirmTierText);
    }

    [Fact]
    public void A_skill_called_all_is_not_scanned_by_name_because_the_CLI_would_scan_every_skill()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Only(vm, """[{"name": "all"}]"""), GovernVerbs.Scan);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.IsResultOpen);
    }

    [Fact]
    public void Skills_mcps_and_plugins_offer_Scan_but_tools_and_listing_artifacts_do_not()
    {
        Assert.True(Only(new SkillsPanelViewModel(_services), """[{"name": "a"}]""").CanScan);
        Assert.True(Only(new SkillsPanelViewModel(_services), """[{"name": "b", "bundled": true}]""").CanScan);
        Assert.True(Only(new McpsPanelViewModel(_services), """[{"name": "a"}]""").CanScan);

        var plugins = new PluginsPanelViewModel(_services);
        Assert.True(Only(plugins, """[{"id": "demo", "connector": "codex"}]""").CanScan);
        Assert.False(Only(plugins, """[{"id": "marketplaces", "connector": "claudecode"}]""").CanScan);
    }

    // ------------------------------------------------------------------ Scan all skills

    [Fact]
    public void Scan_all_skills_is_the_Overview_quick_actions_command_and_is_reviewed()
    {
        var vm = new SkillsPanelViewModel(_services);
        Assert.True(vm.HasScanAll);

        vm.ScanAllCommand.Execute(null);

        Assert.True(vm.IsConfirmOpen);
        Assert.Equal("defenseclaw skill scan --all", vm.ConfirmCommandText);
        Assert.Equal(string.Join(' ', OverviewPanelViewModel.ScanSkillsArgv), string.Join(' ', vm.ConfirmReview!.Steps.Single().Argv));
        Assert.Equal("Changes state", vm.ConfirmTierText);
    }

    [Fact]
    public void Scan_all_follows_the_toolbar_connector()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.SelectedConnector = "codex";

        vm.ScanAllCommand.Execute(null);

        Assert.Equal("defenseclaw skill scan --all --connector codex", vm.ConfirmCommandText);
        Assert.Contains("“codex”", vm.ConfirmHeading, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_skills_panel_has_a_bulk_scan()
    {
        Assert.False(new McpsPanelViewModel(_services).HasScanAll);
        Assert.False(new PluginsPanelViewModel(_services).HasScanAll);
    }

    // ------------------------------------------------------------------ the scanner is not installed

    [Fact]
    public async Task A_missing_scanner_turns_Scan_off_and_says_why_without_opening_a_review()
    {
        var vm = new SkillsPanelViewModel(_services) { ScannerFinder = _ => Task.FromResult<string?>(null) };
        var row = Only(vm, """[{"name": "pdf-tools"}]""");
        Assert.True(vm.CanScan);

        await vm.InitializeAsync();

        Assert.False(vm.CanScan);
        Assert.True(vm.HasScanUnavailableReason);
        Assert.Contains("skill-scanner", vm.ScanUnavailableReason, StringComparison.Ordinal);

        Act(vm, row, GovernVerbs.Scan);
        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.IsResultOpen);

        vm.ScanAllCommand.Execute(null);
        Assert.False(vm.IsConfirmOpen);
    }

    [Fact]
    public async Task A_found_scanner_leaves_Scan_on_and_the_mcp_panel_looks_for_its_own()
    {
        var asked = new List<string>();
        var vm = new McpsPanelViewModel(_services)
        {
            ScannerFinder = name =>
            {
                asked.Add(name);
                return Task.FromResult<string?>(@"C:\scanners\mcp-scanner.exe");
            },
        };

        await vm.InitializeAsync();

        Assert.Equal(new[] { "mcp-scanner" }, asked);
        Assert.True(vm.CanScan);
        Assert.False(vm.HasScanUnavailableReason);
    }

    [Fact]
    public async Task Plugins_have_no_scanner_to_look_for()
    {
        var asked = 0;
        var vm = new PluginsPanelViewModel(_services) { ScannerFinder = _ => { asked++; return Task.FromResult<string?>(null); } };

        await vm.InitializeAsync();

        Assert.Equal(0, asked);
        Assert.True(vm.CanScan);
    }

    // ------------------------------------------------------------------ skill install

    [Fact]
    public void Installing_a_skill_puts_the_name_after_the_double_dash_and_force_raises_the_tier()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.SelectedConnector = "codex";
        vm.InstallName = "  pdf-tools  ";
        vm.InstallForce = true;
        vm.InstallApplyActionPolicy = true;

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.Equal("defenseclaw skill install --force --action --connector codex -- pdf-tools", vm.ConfirmCommandText);
        Assert.True(vm.IsConfirmDestructive);
        Assert.Contains("Force overwrites", vm.ConfirmNote, StringComparison.Ordinal);
        Assert.Contains("skill_actions", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_a_skill_without_options_is_a_plain_state_change_for_every_connector()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.InstallName = "pdf-tools";

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.Equal(new[] { "skill", "install", "--", "pdf-tools" }, vm.ConfirmReview!.Steps.Single().Argv);
        Assert.False(vm.IsConfirmDestructive);
        Assert.Contains("ALL configured connectors", vm.ConfirmHeading, StringComparison.Ordinal);
        Assert.Contains("only reports findings", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\skills\\x")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("https://example.com/x.tgz")]
    [InlineData("clawhub://x@1.0")]
    [InlineData("-rf")]
    public void A_target_that_is_not_a_ClawHub_name_shows_an_error_and_opens_no_review(string input)
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.InstallName = input;

        vm.SubmitInstallFormCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.HasInstallFormError);
    }

    [Fact]
    public void Escape_closes_the_review_first_and_then_the_install_form()
    {
        var vm = new SkillsPanelViewModel(_services);
        vm.ToggleInstallFormCommand.Execute(null);
        vm.InstallName = "x";
        vm.SubmitInstallFormCommand.Execute(null);
        Assert.True(vm.IsConfirmOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.IsInstallFormOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsInstallFormOpen);
        Assert.False(vm.HandleEscape());
    }
}

/// <summary>
/// The new Scan actions in real views (Skills with the install form open, Plugins), in Default Dark and Linear Light at the
/// window minimum. A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GovernScanViewTests
{
    [Theory]
    [InlineData("Default", "Dark", "default-dark")]
    [InlineData("Linear", "Light", "linear-light")]
    public void Skills_and_plugins_show_the_scan_actions(string styleName, string modeName, string name)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        var skills = UiThread.Run(() =>
        {
            var shell = new PanelShell(services, 940, 620);
            shell.Show<SkillsPanel>();
            return shell;
        });
        try
        {
            var vm = (SkillsPanelViewModel)UiThread.Run(() => skills.ViewModel);
            UiThread.WaitFor(() => vm.State != GovernState.Loading && !vm.IsBusy, "first read finished");
            Fill(vm, "skill-list.multi-connector.json");
            UiThread.Run(() => vm.ToggleInstallFormCommand.Execute(null));

            foreach (var installed in new[] { true, false })
            {
                UiThread.Run(() =>
                {
                    vm.ScannerFinder = _ => Task.FromResult<string?>(installed ? @"C:\scanners\skill-scanner.exe" : null);
                    vm.RefreshCommand.Execute(null);
                });
                UiThread.WaitFor(() => !vm.IsBusy, "refresh finished");
                Fill(vm, "skill-list.multi-connector.json");
                UiThread.WaitFor(() => vm.HasScanUnavailableReason == !installed, "scanner lookup answered");
                UiThread.Run(() =>
                {
                    skills.Host.Relayout();
                    var scan = VisualTree.Descendants<Wpf.Ui.Controls.Button>(skills.Page!)
                        .Where(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Scan", StringComparison.Ordinal))
                        .ToList();
                    Assert.Contains(scan, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Scan all skills…");
                    Assert.Equal(installed, Assert.Single(scan).IsEnabled);

                    // Scan on a row lives in its "..." menu (dense table), enabled by the same rule.
                    Assert.Equal(installed, RowScanItem(skills.Page!).IsEnabled);
                    RenderTo.Png(skills.Host, $"skills-scan-install-{name}-{(installed ? "scanner-found" : "scanner-missing")}");

                    // The same page with the form closed: the rows and their Scan buttons.
                    vm.ToggleInstallFormCommand.Execute(null);
                    skills.Host.Relayout();
                    RenderTo.Png(skills.Host, $"skills-rows-{name}-{(installed ? "scanner-found" : "scanner-missing")}");
                    vm.ToggleInstallFormCommand.Execute(null);
                });
            }
        }
        finally
        {
            UiThread.Run(skills.Dispose);
        }

        var plugins = UiThread.Run(() =>
        {
            var shell = new PanelShell(services, 940, 620);
            shell.Show<PluginsPanel>();
            return shell;
        });
        try
        {
            var vm = (PluginsPanelViewModel)UiThread.Run(() => plugins.ViewModel);
            Fill(vm, "plugin-list.multi-connector.json");
            UiThread.Run(() =>
            {
                plugins.Host.Relayout();
                Assert.True(RowScanItem(plugins.Page!).IsEnabled);
                RenderTo.Png(plugins.Host, $"plugins-scan-{name}");
            });
        }
        finally
        {
            UiThread.Run(plugins.Dispose);
        }
    }

    /// <summary>The "Scan…" item of the first scannable row's menu, opened from the row's "..." button and closed again.</summary>
    private static System.Windows.Controls.MenuItem RowScanItem(System.Windows.FrameworkElement page)
    {
        var row = VisualTree.Descendants<System.Windows.Controls.DataGridRow>(page).First(r => r.Item is GovernRow { CanScan: true });
        var button = VisualTree.Descendants<DefenseClaw.App.Views.Controls.DcRowMenuButton>(row).Single();
        Assert.True(button.OpenMenu());
        try
        {
            var menu = System.Windows.Controls.ContextMenuService.GetContextMenu(row)!;
            var item = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => (string)i.Header == "Scan…");
            Assert.StartsWith("Scan ", System.Windows.Automation.AutomationProperties.GetName(item), StringComparison.Ordinal);
            return item;
        }
        finally
        {
            System.Windows.Controls.ContextMenuService.GetContextMenu(row)!.IsOpen = false;
        }
    }

    private static void Fill(GovernPanelViewModelBase vm, string fixture)
    {
        UiThread.WaitFor(() => vm.State != GovernState.Loading, "first read finished");
        UiThread.Run(() =>
        {
            vm.Trust.MarkComplete(); // the stand-in for a successful read: the Scan actions in these tests are meant to be on
            vm.Rows.Clear();
            foreach (var row in vm.ParseRows(PayloadFixtures.Read(fixture)).Where(r => !r.IsArtifact))
            {
                vm.Rows.Add(row);
            }

            vm.State = GovernState.Loaded;
        });
    }
}
