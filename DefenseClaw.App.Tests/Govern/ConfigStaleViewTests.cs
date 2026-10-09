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
/// What an operator sees when <c>config.yaml</c> or <c>.env</c> changed after a catalog was read (CUST-312): the rows still there, the toolbar's change
/// buttons and the row menu's change items off with the reason as their tooltip, Refresh and Info and Copy name still on, and everything back after
/// a fresh read. The tooltips are the same as for a partial or old read (CUST-283), so no new surface is drawn. A PNG is written when
/// <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigStaleViewTests
{
    [Fact]
    public void A_config_change_after_a_complete_read_turns_the_toolbar_and_the_row_menu_off_with_the_reason_and_a_refresh_turns_them_on()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var files = new ConfigEdits(services);
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
                InvocationFactory.Finish(invocation, 0);
                return Task.FromResult(invocation);
            };
            UiThread.Run(() =>
            {
                vm.SetActive(true); // on screen: it listens for the reload
                vm.RefreshCommand.Execute(null);
            });
            UiThread.WaitFor(() => vm.IsDataTrusted && vm.Rows.Count > 0 && !vm.IsBusy, "complete read applied");
            var rows = UiThread.Run(() => vm.Rows.Select(r => r.Key).ToArray());

            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                var add = VisualTree.Descendants<Wpf.Ui.Controls.Button>(shell.Page!)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add MCP server…");
                Assert.True(add.IsEnabled);
                Assert.Equal("Add MCP server…", add.ToolTip);
            });

            // The edit, and the watcher's notification of it (raised from this thread, delivered on the UI thread).
            files.EditConfig();
            files.Reload();

            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                var page = shell.Page!;
                Assert.False(vm.IsDataTrusted);
                Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DataUntrustedReason);

                var add = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add MCP server…");
                Assert.False(add.IsEnabled);
                Assert.Equal(CatalogTrust.ConfigChangedReason(), add.ToolTip);

                var refresh = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Refresh");
                Assert.True(refresh.IsEnabled);

                // The rows are still there.
                var grid = (DataGrid)page.FindName("RowGrid");
                grid.UpdateLayout();
                Assert.Equal(rows, vm.Rows.Select(r => r.Key).ToArray());
                var row = VisualTree.Descendants<DataGridRow>(grid).First(r => r.Item is GovernRow { CanBlock: true });
                Assert.True(VisualTree.Descendants<DcRowMenuButton>(row).Single().OpenMenu());
                try
                {
                    var menu = ContextMenuService.GetContextMenu(row)!;
                    var items = menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToList();

                    foreach (var header in new[] { "Block…", "Allow…", "Scan…" }.Where(h => items.Any(i => (string)i.Header == h)))
                    {
                        var item = items.Single(i => (string)i.Header == header);
                        Assert.False(item.IsEnabled, $"{header} is on for a list read before the config changed");
                        Assert.Equal(CatalogTrust.ConfigChangedReason(), item.ToolTip);
                    }

                    Assert.All(items.Where(i => Equals(i.CommandParameter, "Info")), i => Assert.True(i.IsEnabled));
                    Assert.True(items.Single(i => Equals(i.CommandParameter, "CopyName")).IsEnabled);

                    RenderTo.Png(shell.Host, "config-stale-mcps");
                }
                finally
                {
                    ContextMenuService.GetContextMenu(row)!.IsOpen = false;
                }
            });

            // A fresh complete read gives it all back.
            UiThread.Run(() => vm.RefreshCommand.Execute(null));
            UiThread.WaitFor(() => vm.IsDataTrusted && !vm.IsBusy, "fresh read applied");
            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                var add = VisualTree.Descendants<Wpf.Ui.Controls.Button>(shell.Page!)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add MCP server…");
                Assert.True(add.IsEnabled);
                Assert.Equal("Add MCP server…", add.ToolTip);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                ((McpsPanelViewModel)shell.ViewModel).SetActive(false);
                shell.Dispose();
            });
        }
    }
}
