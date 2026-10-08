using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// What an operator sees when a catalog read was partial (CUST-283): the rows, the "Incomplete discovery" banner with the CLI's own
/// diagnostic, the toolbar's change buttons and the row menu's change items off with the reason as their tooltip, and Info and Copy
/// name still on. A PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CatalogSafetyViewTests
{
    private const string Diagnostic = "error: MCP discovery source is unreadable for connector='claudecode': C:\\fixture-home\\.claude\\settings.json";

    [Fact]
    public void A_partial_mcp_read_shows_the_banner_and_turns_the_change_controls_off_with_the_reason()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1400, 900);
            s.Show<McpsPanel>();
            return s;
        });
        try
        {
            var vm = (McpsPanelViewModel)UiThread.Run(() => shell.ViewModel);
            UiThread.WaitFor(() => vm.State != GovernState.Loading && !vm.IsBusy, "first read finished");

            var json = PayloadFixtures.Read("mcp-list.multi-connector.json");
            vm.RunCli = (argv, _) =>
            {
                var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
                InvocationFactory.Append(invocation, json);
                InvocationFactory.Append(invocation, Diagnostic, CliStream.StandardError);
                InvocationFactory.Finish(invocation, 1);
                return Task.FromResult(invocation);
            };
            UiThread.Run(() => vm.RefreshCommand.Execute(null));
            UiThread.WaitFor(() => vm.HasPartialDiscovery && !vm.IsBusy, "partial read applied");

            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                var page = shell.Page!;

                var banner = VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(page).Single(b => b.Title == "Incomplete discovery");
                Assert.True(banner.IsOpen);
                Assert.Contains("settings.json", banner.Message, StringComparison.Ordinal);

                var add = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add MCP server…");
                Assert.False(add.IsEnabled);
                Assert.Equal(vm.DataUntrustedReason, add.ToolTip);

                var refresh = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Refresh");
                Assert.True(refresh.IsEnabled);

                var grid = (DataGrid)page.FindName("RowGrid");
                grid.UpdateLayout();
                var row = VisualTree.Descendants<DataGridRow>(grid).First(r => r.Item is GovernRow { CanBlock: true });
                Assert.True(VisualTree.Descendants<DcRowMenuButton>(row).Single().OpenMenu());
                try
                {
                    var menu = ContextMenuService.GetContextMenu(row)!;
                    var items = menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToList();

                    foreach (var header in new[] { "Block…", "Allow…", "Scan…" }.Where(h => items.Any(i => (string)i.Header == h)))
                    {
                        var item = items.Single(i => (string)i.Header == header);
                        Assert.False(item.IsEnabled, $"{header} is on for a partial list");
                        Assert.Equal(vm.DataUntrustedReason, item.ToolTip);
                    }

                    Assert.All(items.Where(i => Equals(i.CommandParameter, "Info")), i => Assert.True(i.IsEnabled));
                    Assert.True(items.Single(i => Equals(i.CommandParameter, "CopyName")).IsEnabled);

                    RenderTo.Png(shell.Host, "catalog-safety-mcps-partial");
                }
                finally
                {
                    ContextMenuService.GetContextMenu(row)!.IsOpen = false;
                }
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void The_same_menu_items_come_back_on_when_a_complete_read_replaces_the_partial_one()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1400, 900);
            s.Show<SkillsPanel>();
            return s;
        });
        try
        {
            var vm = (SkillsPanelViewModel)UiThread.Run(() => shell.ViewModel);
            UiThread.WaitFor(() => vm.State != GovernState.Loading && !vm.IsBusy, "first read finished");
            vm.ScannerFinder = _ => Task.FromResult<string?>(@"C:\scanners\skill-scanner.exe");

            var json = PayloadFixtures.Read("skill-list.multi-connector.json");
            var partial = true;
            vm.RunCli = (argv, _) =>
            {
                var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
                InvocationFactory.Append(invocation, json);
                if (partial)
                {
                    InvocationFactory.Append(invocation, Diagnostic, CliStream.StandardError);
                }

                InvocationFactory.Finish(invocation, partial ? 1 : 0);
                return Task.FromResult(invocation);
            };

            UiThread.Run(() => vm.RefreshCommand.Execute(null));
            UiThread.WaitFor(() => vm.HasPartialDiscovery && !vm.IsBusy, "partial read applied");
            UiThread.Run(() => shell.Host.Relayout());

            partial = false;
            UiThread.Run(() => vm.RefreshCommand.Execute(null));
            UiThread.WaitFor(() => !vm.HasPartialDiscovery && !vm.IsBusy && vm.IsDataTrusted, "complete read applied");

            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                var page = shell.Page!;
                var banner = VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(page).Single(b => b.Title == "Incomplete discovery");
                Assert.False(banner.IsOpen);

                var install = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Install skill…");
                Assert.True(install.IsEnabled);
                Assert.Equal("Install skill…", install.ToolTip);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
