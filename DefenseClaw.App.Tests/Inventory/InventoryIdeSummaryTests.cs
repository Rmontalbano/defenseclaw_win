using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Agents;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The IDE plugins line of the AI BOM as the Mac and the CLI word it (CUST-389, Mac v1.1.32): the Summary reads "N (X AI, Y disabled, Z users)",
/// and a scan that stopped at its bound reads "N+" in the summary and on the tab. The gateway page is the synthetic fixture with its
/// <c>partial</c> flag and users changed; no process starts.
/// </summary>
public sealed class InventoryIdeSummaryTests
{
    private const string FixtureName = "ide-plugins.synthetic.json";

    /// <summary>The synthetic page (six plugins, all of user "operator", not partial) with the changes a test asks for.</summary>
    internal static string Page(bool partial = false, string? user = null, bool secondUser = false)
    {
        var json = AgentsScene.Read(FixtureName);
        Assert.Contains("\"partial\": false", json, StringComparison.Ordinal);
        if (partial)
        {
            json = json.Replace("\"partial\": false", "\"partial\": true", StringComparison.Ordinal);
        }

        if (user is not null)
        {
            json = json.Replace("\"user\": \"operator\"", $"\"user\": \"{user}\"", StringComparison.Ordinal);
        }

        if (secondUser)
        {
            const string Row = "\"fingerprint\": \"fp-4\", \"user\": \"operator\"";
            Assert.Contains(Row, json, StringComparison.Ordinal);
            json = json.Replace(Row, "\"fingerprint\": \"fp-4\", \"user\": \"operator-b\"", StringComparison.Ordinal);
        }

        return json;
    }

    /// <summary>The synthetic scan every BOM test uses: the Summary tab lists the two gateway inventories only once a BOM is on screen.</summary>
    internal static InventoryBomSnapshot Snapshot()
    {
        var parsed = InventoryBomSnapshot.Parse(PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));
        Assert.True(parsed.Succeeded, parsed.Message);
        return parsed.Snapshot!;
    }

    private static InventoryBomBrowser Browse(string pageJson)
    {
        var browser = new InventoryBomBrowser();
        browser.SetExtrasAvailable(true);
        browser.Load(Snapshot(), []);
        browser.ShowIdePlugins(IdeInventoryReader.ParseText(pageJson));
        return browser;
    }

    private static string Summary(InventoryBomBrowser browser)
    {
        browser.ActiveTab = InventoryBomBrowser.SummaryTab;
        return browser.Rows.Single(r => r.Cells[0] == "IDE plugins").Cells[1];
    }

    [Fact]
    public void The_summary_counts_the_distinct_users_of_the_plugins()
    {
        Assert.Equal("6 (2 AI, 1 disabled, 1 user)", Summary(Browse(Page())));
        Assert.Equal("6 (2 AI, 1 disabled, 2 users)", Summary(Browse(Page(secondUser: true))));
    }

    [Fact]
    public void A_plugin_with_no_user_adds_none_and_no_users_reads_zero_users()
    {
        Assert.Equal("6 (2 AI, 1 disabled, 0 users)", Summary(Browse(Page(user: string.Empty))));
    }

    [Fact]
    public void Users_are_counted_as_the_gateway_spelled_them()
    {
        // The CLI's set of strings: two spellings are two users.
        var json = Page().Replace("\"fingerprint\": \"fp-4\", \"user\": \"operator\"", "\"fingerprint\": \"fp-4\", \"user\": \"Operator\"", StringComparison.Ordinal);

        Assert.Equal("6 (2 AI, 1 disabled, 2 users)", Summary(Browse(json)));
    }

    [Fact]
    public void A_scan_that_stopped_at_its_bound_reads_N_plus_in_the_summary_and_for_the_tab()
    {
        var browser = Browse(Page(partial: true, secondUser: true));

        Assert.True(browser.IdePluginsCountIsAtLeast);
        Assert.Equal(6, browser.IdePluginsCount);
        Assert.Equal("6+ (2 AI, 1 disabled, 2 users)", Summary(browser));
    }

    [Fact]
    public void A_complete_scan_has_no_plus()
    {
        var browser = Browse(Page());

        Assert.False(browser.IdePluginsCountIsAtLeast);
        Assert.Equal(6, browser.IdePluginsCount);
    }

    [Fact]
    public void Nothing_read_yet_or_a_gateway_without_the_route_has_no_count_and_no_plus()
    {
        var unread = new InventoryBomBrowser();
        Assert.False(unread.IdePluginsCountIsAtLeast);
        Assert.Null(unread.IdePluginsCount);

        var off = new InventoryBomBrowser();
        off.SetExtrasAvailable(false);
        off.ShowIdePlugins(IdeInventoryReader.ParseText(Page(partial: true)));
        Assert.False(off.IdePluginsCountIsAtLeast);
        Assert.Null(off.IdePluginsCount);
    }
}

/// <summary>The tab's "(N+)" in the real segmented control and in the Inventory panel, in both looks at the two window sizes. A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder.</summary>
[Collection(UiCollection.Name)]
public sealed class InventoryIdeTabViewTests
{
    [Fact]
    public void A_segment_whose_count_is_a_lower_bound_shows_a_plus_and_says_so_in_its_name()
    {
        UiThread.Run(() =>
        {
            var segment = new DcSegment { Content = "IDE Plugins", Count = 50 };

            Assert.Equal("(50)", segment.CountText);
            Assert.Equal("IDE Plugins (50)", AutomationProperties.GetName(segment));

            segment.CountIsAtLeast = true;
            Assert.Equal("(50+)", segment.CountText);
            Assert.Equal("IDE Plugins (50+)", AutomationProperties.GetName(segment));

            segment.Count = null;
            Assert.Equal(string.Empty, segment.CountText);
            Assert.Equal("IDE Plugins", AutomationProperties.GetName(segment));

            segment.Count = 7;
            segment.CountIsAtLeast = false;
            Assert.Equal("(7)", segment.CountText);
        });
    }

    [Theory]
    [InlineData(980, 692, "Light")]
    [InlineData(980, 692, "Dark")]
    [InlineData(1468, 875, "Light")]
    [InlineData(1468, 875, "Dark")]
    public async Task The_panels_ide_tab_reads_N_plus_for_a_partial_inventory(int width, int height, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await services.Runtime.RefreshAsync();

        var handler = new AgentsScene.Gateway
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("ide-plugins", StringComparison.Ordinal)
                ? AgentsScene.Json(InventoryIdeSummaryTests.Page(partial: true, secondUser: true))
                : null,
        };
        using var client = new GatewayClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") },
            () => new SecretValue("fixture-bearer-token-0123456789"),
            ownsHttpClient: true);

        var shell = UiThread.Run(() =>
        {
            var shell = new PanelShell(services, width, height);
            _ = shell.Show<InventoryPanel>();
            return shell;
        });
        try
        {
            var vm = UiThread.Run(() => (InventoryPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => vm.HasLoaded && !vm.IsLoading, "inventory loaded");
            UiThread.Run(() =>
            {
                vm.ExtrasFetch = (path, max, ct) => client.GetBoundedJsonAsync(path, max, true, ct);
                vm.BomBrowser.Load(InventoryIdeSummaryTests.Snapshot(), []);
                vm.ActiveView = InventoryPanelViewModel.ViewBom;
            });
            UiThread.WaitFor(() => vm.BomBrowser.IdePluginsCount is not null, "IDE plugins read");

            UiThread.Run(() =>
            {
                vm.BomBrowser.ActiveTab = InventoryBomBrowser.IdeTab;
                shell.Host.Relayout();

                var tabs = (DcSegmented)shell.Page!.FindName("BomTabs");
                var ide = tabs.Items.Cast<DcSegment>().Single(t => (string)t.Value! == "ide-plugins");
                Assert.True(ide.IsVisible);
                Assert.Equal("(6+)", ide.CountText);
                Assert.Equal("IDE Plugins (6+)", AutomationProperties.GetName(ide));
                var shown = VisualTree.Descendants<TextBlock>(ide).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Equal(new[] { "IDE Plugins", "(6+)" }, shown);

                // The other tabs are as they were: no plus on a count that is exact.
                var identities = tabs.Items.Cast<DcSegment>().Single(t => (string)t.Value! == "identities");
                Assert.Equal("(3)", identities.CountText);

                RenderTo.Png(shell.Host, $"ide-plugins-tab-{width}x{height}-{mode.ToLowerInvariant()}");

                vm.BomBrowser.ActiveTab = InventoryBomBrowser.SummaryTab;
                shell.Host.Relayout();
                Assert.Contains(vm.BomBrowser.Rows, r => r.Cells[0] == "IDE plugins" && r.Cells[1] == "6+ (2 AI, 1 disabled, 2 users)");
                RenderTo.Png(shell.Host, $"ide-plugins-summary-{width}x{height}-{mode.ToLowerInvariant()}");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
