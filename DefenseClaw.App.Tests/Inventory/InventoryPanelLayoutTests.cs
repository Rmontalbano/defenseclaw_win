using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The Inventory panel as a real view over a synthetic <c>inventory.db</c>, in the shell stand-in (<see cref="PanelShell"/>)
/// at the window's 940 x 620 DIP minimum - where the page has ~663 x 498 DIPs of its own - and at 1400 x 900. At the minimum
/// the toolbar, the scan note, the components grid and the table browser together are taller than that, which used to clip
/// the table browser with no way to reach it.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class InventoryPanelLayoutTests
{
    // ------------------------------------------------------------------ the minimum window

    [Fact]
    public void At_the_minimum_window_size_the_page_scrolls_and_the_table_browser_can_be_reached()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            // The panel host really is about 711 x 538 DIPs at the minimum window size (the page then has 24 DIPs around it).
            Assert.InRange(scene.Shell.PageSize.Width, 690, 730);
            Assert.InRange(scene.Shell.PageSize.Height, 520, 555);

            Assert.True(scene.PageScroll.ScrollableHeight > 100, $"the page only scrolls {scene.PageScroll.ScrollableHeight} DIPs");
            Assert.Equal(ScrollBarVisibility.Auto, scene.PageScroll.VerticalScrollBarVisibility);

            // Nothing is clipped by the panel: after scrolling to the end the browser's header (and its bottom edge) is on screen.
            scene.PageScroll.ScrollToEnd();
            scene.Host.Relayout();
            Assert.True(scene.IsWithinViewport(scene.TableBrowser), "the table browser is out of reach");

            scene.Render("inventory-940x620-scrolled-to-end");
        });
    }

    [Fact]
    public void Opening_the_table_browser_scrolls_it_into_view_and_its_grid_stays_bounded()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            scene.TableBrowser.IsExpanded = true;
            scene.ViewModel.SelectedTable = scene.ViewModel.Tables.First(t => t.Name == "ai_signals");
        });
        UiThread.WaitFor(() => scene.ViewModel.BrowsedRows is not null, "table rows loaded");

        UiThread.Run(() =>
        {
            scene.Host.Relayout();

            Assert.True(scene.PageScroll.VerticalOffset > 0, "opening the browser should scroll to it");
            Assert.True(scene.IsTopVisible(scene.TableBrowser), "the browser's header should be on screen once it is opened");

            // 320 DIPs of grid, never the height of its 200 rows.
            Assert.InRange(scene.BrowserGrid.ActualHeight, 100, 321);
            Assert.InRange(VisualTree.Descendants<DataGridRow>(scene.BrowserGrid).Count(), 1, 40);

            scene.PageScroll.ScrollToEnd();
            scene.Host.Relayout();
            scene.Render("inventory-940x620-browser-open");
            Assert.True(scene.IsWithinViewport(scene.BrowserGrid), "the browser's grid should be fully reachable");
        });
    }

    [Fact]
    public void The_components_grid_keeps_a_usable_height_at_the_minimum_size()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            Assert.True(scene.ComponentsGrid.ActualHeight >= 280, $"the grid is {scene.ComponentsGrid.ActualHeight} DIPs tall");
            scene.Render("inventory-940x620");
        });
    }

    // ------------------------------------------------------------------ a tall window

    [Fact]
    public void A_tall_window_fits_the_grid_and_the_table_browser_header_without_scrolling()
    {
        using var scene = Scene.Open(1400, 900, components: 60);

        UiThread.Run(() =>
        {
            Assert.True(scene.PageScroll.ScrollableHeight < 12, $"the page scrolls {scene.PageScroll.ScrollableHeight} DIPs on a tall window");
            Assert.True(scene.IsWithinViewport(scene.TableBrowser), "the table browser's header should be on screen");

            // The grid takes what the toolbar leaves it, well beyond its 300 DIP floor.
            Assert.True(scene.ComponentsGrid.ActualHeight > 380, $"the grid is only {scene.ComponentsGrid.ActualHeight} DIPs tall");
            scene.Render("inventory-1400x900");
        });
    }

    [Fact]
    public void Growing_the_window_gives_the_grid_the_extra_height()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            var small = scene.ComponentsGrid.ActualHeight;

            scene.Host.Resize(1400, 900);

            Assert.True(scene.ComponentsGrid.ActualHeight > small + 150, $"{small} -> {scene.ComponentsGrid.ActualHeight}");
        });
    }

    // ------------------------------------------------------------------ virtualization

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void The_components_grid_realizes_only_the_rows_in_view(int width, int height)
    {
        using var scene = Scene.Open(width, height, components: 600);

        UiThread.Run(() =>
        {
            Assert.Equal(600, scene.ViewModel.ComponentsView.Cast<object>().Count());

            var rows = VisualTree.Descendants<DataGridRow>(scene.ComponentsGrid).Count();
            Assert.InRange(rows, 1, 60);

            // WPF turns virtualization off for a grouped list (and for a DataGrid whose scroll viewer does not scroll by
            // content) unless it is asked for; the rows above only stay few because both were asked for.
            Assert.True(ScrollViewer.GetCanContentScroll(scene.ComponentsGrid));
            Assert.True(VirtualizingPanel.GetIsVirtualizingWhenGrouping(scene.ComponentsGrid));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(scene.ComponentsGrid));

            // Scrolling down realizes new rows and lets the old ones go: the count stays about the same.
            var scroll = VisualTree.Find<ScrollViewer>(scene.ComponentsGrid)!;
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            scene.Host.Relayout();
            Assert.InRange(VisualTree.Descendants<DataGridRow>(scene.ComponentsGrid).Count(), 1, 60);
        });
    }

    [Fact]
    public void Both_grids_pass_the_mouse_wheel_to_the_page_when_they_cannot_scroll_further()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            Assert.True(NestedScroll.GetForwardWheel(scene.ComponentsGrid));
            Assert.True(NestedScroll.GetForwardWheel(scene.BrowserGrid));
        });
    }

    // ------------------------------------------------------------------ columns

    [Fact]
    public void At_the_minimum_width_the_six_leading_columns_all_fit_and_hold_their_minimum_width()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            var columns = scene.ComponentsGrid.Columns;
            var widths = scene.ColumnWidths();
            Assert.Equal(9, columns.Count);
            AssertAtLeastTheirMinimum(scene, widths);

            // Vendor, Name, Ecosystem, Framework, Version, Installs: what the grid is read for, without scrolling sideways.
            var visibleWidth = VisualTree.Find<ScrollViewer>(scene.ComponentsGrid)!.ViewportWidth;
            var leading = columns.Take(6).Sum(column => widths[(string)column.Header]);
            Assert.True(leading <= visibleWidth + 0.5, $"six leading columns need {leading} DIPs but the grid shows {visibleWidth}");
            Assert.True(leading >= visibleWidth - 4, $"six leading columns use only {leading} of the {visibleWidth} DIPs the grid shows");

            // The other three are reached by scrolling, not squeezed.
            Assert.All(columns.Skip(6), column => Assert.True(column.MinWidth >= 112));
        });
    }

    [Fact]
    public void A_very_wide_window_shares_the_grids_width_between_its_columns()
    {
        using var scene = Scene.Open(2000, 1000, components: 60);

        UiThread.Run(() =>
        {
            var widths = scene.ColumnWidths();
            var visibleWidth = VisualTree.Find<ScrollViewer>(scene.ComponentsGrid)!.ViewportWidth;

            // Star widths: nothing is left over at the right edge, and the wider columns take the wider shares.
            Assert.Equal(visibleWidth, widths.Values.Sum(), 3.0);
            Assert.True(widths["Name"] > 140 + 40, $"Name is {widths["Name"]} (min 140)");
            Assert.True(widths["Name"] > widths["Version"] * 2);
            AssertAtLeastTheirMinimum(scene, widths);
        });
    }

    [Fact]
    public void The_columns_fill_the_grid_even_though_its_rows_arrive_after_the_first_layout()
    {
        // The order the running app has: the panel is laid out on screen first (its grid empty), the database read finishes
        // after. A DataGrid left to its own star sizing keeps the widths it worked out for no rows.
        using var temp = new TempDirectory();
        InventoryFixture.Create(temp.File("inventory.db"), 60);
        SqliteConnection.ClearAllPools();
        using var services = TestServices.Create(temp);

        var viewModel = UiThread.Run(() => new InventoryPanelViewModel(services));
        OffscreenHost? host = null;
        try
        {
            var panel = UiThread.Run(() =>
            {
                var view = new InventoryPanel { DataContext = viewModel };
                host = new OffscreenHost(view, 1800, 800);
                Assert.Empty(viewModel.ComponentsView.Cast<object>());
                return view;
            });

            _ = UiThread.Run(() => viewModel.InitializeAsync());
            UiThread.WaitFor(() => viewModel.HasLoaded && !viewModel.IsLoading && viewModel.Tables.Count > 0, "inventory loaded");

            UiThread.Run(() =>
            {
                host!.Relayout();
                var grid = (Wpf.Ui.Controls.DataGrid)panel.FindName("ComponentsGrid");
                var visibleWidth = VisualTree.Find<ScrollViewer>(grid)!.ViewportWidth;
                var widths = VisualTree.Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid)
                    .Where(header => header.Column is not null)
                    .Sum(header => header.ActualWidth);

                Assert.Equal(visibleWidth, widths, 3.0);
            });
        }
        finally
        {
            if (host is not null)
            {
                UiThread.Run(host.Dispose);
            }

            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void A_window_too_narrow_for_every_column_scrolls_sideways_rather_than_squeezing_them()
    {
        using var scene = Scene.Open(1400, 900, components: 60);

        UiThread.Run(() =>
        {
            var widths = scene.ColumnWidths();
            var scroll = VisualTree.Find<ScrollViewer>(scene.ComponentsGrid)!;

            AssertAtLeastTheirMinimum(scene, widths);
            Assert.True(scroll.ScrollableWidth > 0, "the three trailing columns should be reached by scrolling sideways");
            Assert.True(widths.Values.Sum() >= scroll.ViewportWidth - 1);

            // The leading six use the whole width, so the name column is well above its floor on a window this size.
            var leading = scene.ComponentsGrid.Columns.Take(6).Sum(column => widths[(string)column.Header]);
            Assert.Equal(scroll.ViewportWidth, leading, 3.0);
            Assert.True(widths["Name"] > 140 + 40, $"Name is {widths["Name"]} (min 140)");
            foreach (var trailing in new[] { "Identity", "Presence", "Last seen" })
            {
                var column = scene.ComponentsGrid.Columns.Single(c => Equals(c.Header, trailing));
                Assert.Equal(column.MinWidth, widths[trailing], 0.5);
            }
        });
    }

    [Fact]
    public void Cell_text_is_trimmed_with_an_ellipsis_and_carries_its_full_value_as_a_tooltip()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            var longName = VisualTree.Descendants<TextBlock>(scene.ComponentsGrid)
                .FirstOrDefault(t => t.Text.StartsWith("@northwind-internal", StringComparison.Ordinal));
            Assert.NotNull(longName);

            Assert.Equal(TextTrimming.CharacterEllipsis, longName!.TextTrimming);
            Assert.Equal(longName.Text, longName.ToolTip);

            // ... and it really is cut off at this width, so the tooltip has something to add.
            var typeface = new System.Windows.Media.Typeface(longName.FontFamily, longName.FontStyle, longName.FontWeight, longName.FontStretch);
            var full = new System.Windows.Media.FormattedText(
                longName.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                longName.FontSize,
                System.Windows.Media.Brushes.Black,
                1.0);
            Assert.True(full.Width > longName.ActualWidth, $"{full.Width} fits in {longName.ActualWidth}");
        });
    }

    /// <summary>Every column is at least as wide as its <c>MinWidth</c> - none has been squeezed below the width that keeps its header readable.</summary>
    private static void AssertAtLeastTheirMinimum(Scene scene, IReadOnlyDictionary<string, double> widths)
    {
        foreach (var column in scene.ComponentsGrid.Columns)
        {
            var header = (string)column.Header;
            Assert.True(widths[header] >= column.MinWidth - 0.5, $"{header}: {widths[header]} < min {column.MinWidth}");
        }
    }

    // ------------------------------------------------------------------ narrow and wide arrangement

    [Fact]
    public void A_narrow_page_puts_the_detail_pane_under_the_grid_and_a_wide_one_beside_it()
    {
        using var scene = Scene.Open(940, 620, components: 60);

        UiThread.Run(() =>
        {
            Assert.Equal("Narrow", scene.MasterDetail.Tag);
            var gridCard = (FrameworkElement)((FrameworkElement)scene.ComponentsGrid.Parent).Parent;
            var below = scene.DetailCard.TranslatePoint(new Point(0, 0), scene.MasterDetail);
            Assert.True(below.Y >= gridCard.ActualHeight, "the detail pane should sit under the grid");
            Assert.Equal(0, below.X, 0.5);
            Assert.Equal(scene.MasterDetail.ActualWidth, scene.DetailCard.ActualWidth, 0.5);
            Assert.Equal(scene.MasterDetail.ActualWidth, gridCard.ActualWidth, 0.5);

            scene.Host.Resize(1400, 900);

            Assert.Equal("Wide", scene.MasterDetail.Tag);
            var beside = scene.DetailCard.TranslatePoint(new Point(0, 0), scene.MasterDetail);
            Assert.Equal(0, beside.Y, 0.5);
            Assert.True(beside.X > gridCard.ActualWidth, "the detail pane should sit beside the grid");
            Assert.True(scene.DetailCard.ActualWidth >= 260);
        });
    }

    // ------------------------------------------------------------------ untouched behaviour

    [Fact]
    public void The_review_dialog_still_covers_the_whole_page_from_the_first_row()
    {
        using var scene = Scene.Open(940, 620, components: 10);

        UiThread.Run(() =>
        {
            var overlay = VisualTree.Find<DiscoverReviewOverlay>(scene.Shell.Page!)!;

            Assert.Equal(0, Grid.GetRow(overlay));
            Assert.Equal(5, Grid.GetRowSpan(overlay));
            Assert.Same(scene.ViewModel.Review, overlay.DataContext);
        });
    }

    [Fact]
    public void The_page_is_named_and_the_grids_keep_their_names()
    {
        using var scene = Scene.Open(940, 620, components: 10);

        UiThread.Run(() =>
        {
            Assert.Equal("Inventory page", System.Windows.Automation.AutomationProperties.GetName(scene.PageScroll));
            Assert.Equal("AI components in the latest scan", System.Windows.Automation.AutomationProperties.GetName(scene.ComponentsGrid));
            Assert.Equal("Rows of the selected table", System.Windows.Automation.AutomationProperties.GetName(scene.BrowserGrid));
        });
    }

    // ------------------------------------------------------------------ scene

    /// <summary>The shell stand-in with an Inventory panel over a synthetic inventory.db, loaded and laid out.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height, int components)
        {
            InventoryFixture.Create(_temp.File("inventory.db"), components);
            SqliteConnection.ClearAllPools();

            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = shell.Show<InventoryPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (InventoryPanelViewModel)Shell.ViewModel);

            UiThread.WaitFor(() => ViewModel.HasLoaded && !ViewModel.IsLoading && ViewModel.Tables.Count > 0, "inventory loaded");
            UiThread.WaitFor(() => ViewModel.Tables.All(t => t.RowCount is not null), "table counts loaded");
            UiThread.Run(() => Host.Relayout());
        }

        public PanelShell Shell { get; }

        public InventoryPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public static Scene Open(int width, int height, int components) => new(width, height, components);

        private T Named<T>(string name)
            where T : class =>
            Shell.Page!.FindName(name) as T ?? throw new InvalidOperationException($"{name} not found in the Inventory panel.");

        public ScrollViewer PageScroll => Named<ScrollViewer>("PageScroll");

        public Wpf.Ui.Controls.DataGrid ComponentsGrid => Named<Wpf.Ui.Controls.DataGrid>("ComponentsGrid");

        public Wpf.Ui.Controls.DataGrid BrowserGrid => Named<Wpf.Ui.Controls.DataGrid>("BrowserGrid");

        public Wpf.Ui.Controls.CardExpander TableBrowser => Named<Wpf.Ui.Controls.CardExpander>("TableBrowser");

        public Grid MasterDetail => Named<Grid>("MasterDetail");

        public Border DetailCard => Named<Border>("DetailCard");

        /// <summary>True when <paramref name="element"/>'s bottom edge is inside the page's scroll viewport: the whole of it is reachable.</summary>
        public bool IsWithinViewport(FrameworkElement element)
        {
            var bottom = element.TranslatePoint(new Point(0, element.ActualHeight), PageScroll).Y;
            return bottom > 0 && bottom <= PageScroll.ViewportHeight + 1;
        }

        /// <summary>True when the top edge of <paramref name="element"/> is inside the viewport.</summary>
        public bool IsTopVisible(FrameworkElement element)
        {
            var top = element.TranslatePoint(new Point(0, 0), PageScroll).Y;
            return top >= -1 && top < PageScroll.ViewportHeight - 1;
        }

        /// <summary>
        /// The rendered width of each components column, by header text. Read from the header cells: a star column's
        /// <c>ActualWidth</c> on the column object is not refreshed by a layout pass the way the cells are.
        /// </summary>
        public Dictionary<string, double> ColumnWidths() =>
            VisualTree.Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(ComponentsGrid)
                .Where(header => header.Column is not null)
                .ToDictionary(header => (string)header.Column.Header, header => header.ActualWidth);

        public void Render(string name) => RenderTo.Png(Host, name);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }
    }
}
