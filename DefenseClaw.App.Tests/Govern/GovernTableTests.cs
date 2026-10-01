using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The Govern panels as dense tables (CUST-214): the Mac's columns, every row action in the row's "..." menu (destructive ones in
/// the danger tone, all still going through the review), virtualization kept, and a render of each in two looks and two sizes.
/// A PNG of each state is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GovernTableTests
{
    public static IEnumerable<object[]> Columns() => new[]
    {
        new object[] { "Skills", "Status|Name|Description|Scan|Connector|Source|Actions" },
        new object[] { "Plugins", "Status|Name|Version|Origin|Scan|Connector|Source|Actions" },
        new object[] { "Mcps", "Status|Name|Transport|Command or URL|Scan|Connector|Actions" },
        new object[] { "Tools", "Status|Tool|Applies to|Reason|Updated|Connector|Actions" },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void Each_panel_is_a_dense_zebra_table_with_the_mac_columns(string panel, string headers)
    {
        using var scene = Scene.Open(panel, 1400, 900);

        UiThread.Run(() =>
        {
            var grid = scene.Grid;
            Assert.Equal(headers.Split('|'), grid.Columns.Select(c => (string)c.Header).ToArray());
            Assert.Equal(2, grid.AlternationCount);
            Assert.Equal(DataGridGridLinesVisibility.None, grid.GridLinesVisibility);
            Assert.True(grid.CanUserSortColumns);
            Assert.All(grid.Columns.Take(grid.Columns.Count - 1), c => Assert.False(string.IsNullOrEmpty(c.SortMemberPath)));

            // Each row is named for a screen reader by its item, and carries the one menu.
            var row = VisualTree.Descendants<DataGridRow>(grid).First();
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(row)) && string.IsNullOrWhiteSpace(row.Item.ToString()));
            Assert.NotNull(ContextMenuService.GetContextMenu(row));
            Assert.NotNull(VisualTree.Descendants<DcRowMenuButton>(row).SingleOrDefault());

            // The pill has room to breathe inside the row: taller than its 16 DIP line, shorter than the 26 DIP row.
            var pill = VisualTree.Descendants<DcStatePill>(row).First();
            Assert.InRange(pill.ActualHeight, 21, 25);

            RenderTo.Png(scene.Host, $"govern-table-{panel.ToLowerInvariant()}");
        });
    }

    [Fact]
    public void The_row_menu_holds_every_verb_the_row_offers_with_the_destructive_ones_in_the_danger_tone()
    {
        using var scene = Scene.Open("Plugins", 1400, 900);

        UiThread.Run(() =>
        {
            var row = VisualTree.Descendants<DataGridRow>(scene.Grid).First(r => r.Item is GovernRow { CanRemove: true });
            var item = (GovernRow)row.Item;
            var button = VisualTree.Descendants<DcRowMenuButton>(row).Single();
            Assert.True(button.OpenMenu());
            try
            {
                var menu = ContextMenuService.GetContextMenu(row)!;
                var shown = menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToList();
                var headers = shown.Select(i => (string)i.Header).ToList();

                Assert.Contains(headers, h => h == "Info");
                Assert.Contains(headers, h => h == "Scan…");
                Assert.Contains(headers, h => h == "Remove…");
                Assert.All(shown, i => Assert.NotNull(i.Icon));
                Assert.All(shown, i => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(i)) && i.Header is null));

                // A verb the row does not offer is not in the menu.
                Assert.Equal(item.CanBlock, headers.Contains("Block…"));
                Assert.Equal(item.CanAllow, headers.Contains("Allow…"));

                var critical = Application.Current.FindResource("DcToneCriticalBrush");
                foreach (var danger in new[] { "Quarantine…", "Remove…" }.Where(headers.Contains))
                {
                    Assert.Same(critical, shown.Single(i => (string)i.Header == danger).Foreground);
                }

                Assert.NotSame(critical, shown.Single(i => (string)i.Header == "Info").Foreground);
            }
            finally
            {
                ContextMenuService.GetContextMenu(row)!.IsOpen = false;
            }
        });
    }

    [Fact]
    public void A_menu_item_is_the_rows_command_and_a_state_changing_one_opens_the_review_first()
    {
        using var scene = Scene.Open("Skills", 1400, 900);

        UiThread.Run(() =>
        {
            var row = VisualTree.Descendants<DataGridRow>(scene.Grid).First(r => r.Item is GovernRow { CanBlock: true });
            var item = (GovernRow)row.Item;
            Assert.True(VisualTree.Descendants<DcRowMenuButton>(row).Single().OpenMenu());
            try
            {
                var menu = ContextMenuService.GetContextMenu(row)!;
                var block = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Block…");
                Assert.Same(item.ActionCommand, block.Command);
                Assert.Equal("Block", block.CommandParameter);

                Assert.False(scene.ViewModel.IsConfirmOpen);
                block.Command.Execute(block.CommandParameter);
                Assert.True(scene.ViewModel.IsConfirmOpen);
                Assert.Contains("block", scene.ViewModel.ConfirmCommandText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                ContextMenuService.GetContextMenu(row)!.IsOpen = false;
            }
        });
    }

    [Fact]
    public void The_scan_item_follows_the_panels_idle_state()
    {
        using var scene = Scene.Open("Skills", 1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.ViewModel.Rows.Where(r => r.CanScan).ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(scene.ViewModel.CanScan, r.ScanEnabled));

            var changed = new List<string?>();
            rows[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            scene.ViewModel.IsBusy = true;
            Assert.Contains(nameof(GovernRow.ScanEnabled), changed);
            Assert.False(rows[0].ScanEnabled);
            scene.ViewModel.IsBusy = false;
        });
    }

    [Theory]
    [InlineData("CRITICAL · 19 findings", "19 CRITICAL findings")]
    [InlineData("HIGH · 1 finding", "1 HIGH finding")]
    [InlineData("Not scanned", "Not scanned")]
    [InlineData("Scan clean", "Scan clean")]
    [InlineData("HIGH", "HIGH")]
    public void The_scan_column_says_it_the_way_the_mac_does(string label, string expected)
    {
        var row = new GovernRow(new NullHost()) { Noun = "skill", Name = "x", ScanLabel = label };
        Assert.Equal(expected, row.ScanCellText);
    }

    [Fact]
    public void Plugins_keep_their_listing_artifacts_in_a_dimmed_table_of_their_own()
    {
        using var scene = Scene.Open("Plugins", 1400, 900, "plugin-list.claudecode.json");

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.ArtifactRows.Count > 0);
            var expander = VisualTree.Find<Expander>(scene.Shell.Page!, e => AutomationProperties.GetName(e) == "Entries the CLI lists that are not plugins")!;
            expander.IsExpanded = true;
            scene.Host.Relayout();

            var grid = VisualTree.Find<DataGrid>(expander, g => AutomationProperties.GetName(g) == "Listing artifacts")!;
            var row = VisualTree.Descendants<DataGridRow>(grid).First();
            Assert.True(((GovernRow)row.Item).IsArtifact);
            Assert.Equal(0.8, row.Opacity, 2);
            RenderTo.Png(scene.Host, "govern-table-plugins-artifacts");
        });
    }

    [Theory]
    [InlineData("Default", "Dark", "default-dark")]
    [InlineData("Cisco", "Light", "cisco-light")]
    public void Every_panel_renders_in_two_looks_and_two_sizes(string styleName, string modeName, string look)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));
        foreach (var panel in new[] { "Skills", "Plugins", "Mcps", "Tools" })
        {
            foreach (var (width, height) in new[] { (940, 620), (1400, 900) })
            {
                using var scene = Scene.Open(panel, width, height);
                UiThread.Run(() =>
                {
                    Assert.True(scene.Grid.ActualHeight >= 250, $"{panel} table is {scene.Grid.ActualHeight:0} DIPs tall at {width} x {height}");

                    // The columns fit the window minimum: no sideways scroll bar.
                    var scroller = VisualTree.Descendants<ScrollViewer>(scene.Grid).First();
                    Assert.True(scroller.ScrollableWidth < 1, $"{panel} table scrolls sideways by {scroller.ScrollableWidth:0} DIPs at {width} x {height}");

                    // Select the first row: its details open under it.
                    scene.Grid.SelectedIndex = 0;
                    scene.Host.Relayout();
                    RenderTo.Png(scene.Host, $"govern-{panel.ToLowerInvariant()}-{look}-{width}x{height}");
                });
            }
        }
    }

    private sealed class NullHost : IGovernRowHost
    {
        public void OnRowAction(GovernRow row, GovernVerbs verb)
        {
        }
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(string panel, int width, int height, string? fixtureName = null)
        {
            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = panel switch
                {
                    "Tools" => (FrameworkElement)shell.Show<ToolsPanel>(),
                    "Mcps" => shell.Show<McpsPanel>(),
                    "Plugins" => shell.Show<PluginsPanel>(),
                    _ => shell.Show<SkillsPanel>(),
                };
                return shell;
            });
            ViewModel = UiThread.Run(() => (GovernPanelViewModelBase)Shell.ViewModel);
            UiThread.WaitFor(() => ViewModel.State != GovernState.Loading, "first read finished");
            UiThread.Run(() =>
            {
                var fixture = fixtureName ?? panel switch
                {
                    "Tools" => "tool-list.groups.json",
                    "Mcps" => "mcp-list.multi-connector.json",
                    "Plugins" => "plugin-list.multi-connector.json",
                    _ => "skill-list.multi-connector.json",
                };
                foreach (var row in ViewModel.ParseRows(PayloadFixtures.Read(fixture)))
                {
                    (row.IsArtifact ? ViewModel.ArtifactRows : ViewModel.Rows).Add(row);
                }

                ViewModel.State = GovernState.Loaded;
                Host.Relayout();
            });
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    if (Grid is { ActualHeight: > 0, Items.Count: > 0 } grid && !VisualTree.Descendants<DataGridRow>(grid).Any())
                    {
                        // A virtualizing grid measured before its rows arrived can sit with none realized until something asks for
                        // one (seen once on CI under Cisco Light): ask, as a scroll into view would.
                        grid.ScrollIntoView(grid.Items[0]);
                        grid.UpdateLayout();
                    }

                    return Grid is { ActualHeight: > 0 } && VisualTree.Descendants<DataGridRow>(Grid).Any();
                },
                "table laid out",
                timeoutMilliseconds: 60_000); // a wait, not a bound: CI runners take ~10x longer than a desktop
        }

        public PanelShell Shell { get; }

        public GovernPanelViewModelBase ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public DataGrid Grid => (DataGrid)Shell.Page!.FindName("RowGrid");

        public static Scene Open(string panel, int width, int height, string? fixture = null) => new(panel, width, height, fixture);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
