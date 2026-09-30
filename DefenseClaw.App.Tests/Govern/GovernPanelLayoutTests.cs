using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Panels.Govern;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The four Govern list panels (Tools, MCPs, Plugins, Skills) as real views inside the shell stand-in, over the
/// <c>*-list.*.json</c> fixtures. At the window's 940 x 620 DIP minimum the page is ~711 x 538; with the shell's gateway
/// banner open (94 DIPs, the tray app's everyday state) it is ~711 x 444. The toolbar, forms and banners used to take
/// fixed rows above the list and leave it whatever remained: 105 DIPs for Tools, 11 with the banner open. Now the whole
/// page scrolls and the list keeps at least 250 DIPs of its own.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class GovernPanelLayoutTests
{
    private const double MinimumList = 250;

    public static IEnumerable<object[]> PanelsAtTheSmallSizes() =>
        from panel in new[] { "Tools", "Mcps", "Plugins", "Skills" }
        from size in new[] { (Width: 940, Height: 620), (Width: 940, Height: 526) } // 526: 620 less the 94 DIP gateway banner
        select new object[] { panel, size.Width, size.Height };

    [Theory]
    [MemberData(nameof(PanelsAtTheSmallSizes))]
    public void The_list_keeps_at_least_250_DIPs_once_the_page_is_scrolled_to_it(string panel, int width, int height)
    {
        using var scene = Scene.Open(panel, width, height);

        UiThread.Run(() =>
        {
            var list = scene.List;
            if (panel != "Plugins")
            {
                // The virtualizing lists have a height of their own; Plugins' rows are plain page content.
                Assert.True(list.ActualHeight >= MinimumList, $"the {panel} list is {list.ActualHeight:0} DIPs tall");
            }

            scene.ScrollListToTop();
            var visible = scene.VisibleHeight(list);
            var wanted = Math.Min(MinimumList, list.ActualHeight);
            Assert.True(visible >= wanted - 1, $"{visible:0.##} DIPs of the {panel} list ({list.ActualHeight:0.##} tall) are on screen at {width} x {height}");

            RenderTo.Png(scene.Host, $"govern-{panel.ToLowerInvariant()}-{width}x{height}");
        });
    }

    [Theory]
    [InlineData("Tools")]
    [InlineData("Mcps")]
    [InlineData("Skills")]
    public void On_a_tall_window_the_list_fills_the_page(string panel)
    {
        using var scene = Scene.Open(panel, 1400, 900);

        UiThread.Run(() =>
        {
            var list = scene.List;
            var viewport = scene.PageScroll!.ViewportHeight;

            // Nothing to scroll to: the list took the room that is left, and the page has no scrollbar of its own.
            Assert.True(list.ActualHeight > 400, $"the {panel} list is {list.ActualHeight:0} DIPs tall in a {viewport:0} DIP viewport");
            Assert.True(scene.PageScroll.ScrollableHeight < 1, $"the page scrolls by {scene.PageScroll.ScrollableHeight:0} DIPs");

            RenderTo.Png(scene.Host, $"govern-{panel.ToLowerInvariant()}-1400x900");
        });
    }

    [Theory]
    [InlineData("Tools")]
    [InlineData("Mcps")]
    [InlineData("Skills")]
    public void A_long_list_is_still_virtualized_inside_the_scrolling_page(string panel)
    {
        using var scene = Scene.Open(panel, 940, 620, copies: 100);

        UiThread.Run(() =>
        {
            var total = scene.ViewModel.Rows.Count;
            var realized = VisualTree.Descendants<GovernRowView>(scene.List).Count();
            Assert.True(total >= 100);
            Assert.InRange(realized, 1, 40);

            // The list is a scroller inside a scrolling page: a wheel notch it cannot use goes on to the page.
            Assert.True(NestedScroll.GetForwardWheel(scene.List));
        });
    }

    [Fact]
    public void The_result_card_of_a_read_only_command_sits_under_the_list_and_scrolls_into_view()
    {
        using var scene = Scene.Open("Skills", 940, 620);

        UiThread.Run(() =>
        {
            scene.ViewModel.OutputTitle = "skill info: pdf-tools";
            scene.ViewModel.OutputText = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}: some output of the command"));
            scene.ViewModel.IsOutputOpen = true;
            scene.Host.Relayout();

            var card = VisualTree.Find<Border>(scene.Shell.Page!, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Command result")!;
            Assert.True(card.IsVisible);

            // It did not take the list's room: the list is as tall as it was, and the card comes after it.
            Assert.True(scene.List.ActualHeight >= MinimumList, $"the list is {scene.List.ActualHeight:0} DIPs tall with the result open");
            Assert.True(scene.Top(card) >= scene.Top(scene.List) + scene.List.ActualHeight - 1, "the result card is not under the list");

            // And the operator did not have to hunt for it: the page scrolled to show it.
            Assert.True(scene.VisibleHeight(card) >= 100, $"only {scene.VisibleHeight(card):0} DIPs of the result card are on screen");

            RenderTo.Png(scene.Host, "govern-skills-result-940x620");
        });
    }

    [Fact]
    public void Manage_a_tool_by_name_is_one_closed_line_until_there_are_no_rules()
    {
        using var scene = Scene.Open("Tools", 940, 620);

        UiThread.Run(() =>
        {
            var manage = VisualTree.Find<Wpf.Ui.Controls.CardExpander>(scene.Shell.Page!, e => System.Windows.Automation.AutomationProperties.GetName(e) == "Manage a tool by name")!;
            Assert.False(manage.IsExpanded);
            Assert.True(manage.ActualHeight < 80, $"the closed card is {manage.ActualHeight:0} DIPs tall");

            // With no rules at all it is the way forward, so it opens by itself.
            scene.ViewModel.Rows.Clear();
            scene.ViewModel.State = GovernState.Empty;
            scene.Host.Relayout();
            Assert.True(manage.IsExpanded);
        });
    }

    // ------------------------------------------------------------------ the scene

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(string panel, int width, int height, int copies)
        {
            Panel = panel;
            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = panel switch
                {
                    "Tools" => (FrameworkElement)shell.Show<ToolsPanel>(),
                    "Mcps" => shell.Show<McpsPanel>(),
                    "Plugins" => shell.Show<PluginsPanel>(),
                    "Skills" => shell.Show<SkillsPanel>(),
                    _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
                };
                return shell;
            });
            ViewModel = UiThread.Run(() => (GovernPanelViewModelBase)Shell.ViewModel);

            // The isolated services have no CLI, so the panel's own read ends at "CLI not found"; settle that, then put the
            // fixture's rows in front of it (parsed by the real parser).
            UiThread.WaitFor(() => ViewModel.State != GovernState.Loading, "first read finished");
            UiThread.Run(() =>
            {
                var fixture = panel switch
                {
                    "Tools" => "tool-list.groups.json",
                    "Mcps" => "mcp-list.multi-connector.json",
                    "Plugins" => "plugin-list.multi-connector.json",
                    _ => "skill-list.multi-connector.json",
                };
                var rows = ViewModel.ParseRows(PayloadFixtures.Read(fixture));
                for (var i = 0; i < copies; i++)
                {
                    foreach (var row in rows.Where(r => !r.IsArtifact))
                    {
                        ViewModel.Rows.Add(row);
                    }
                }

                foreach (var row in rows.Where(r => r.IsArtifact))
                {
                    ViewModel.ArtifactRows.Add(row);
                }

                ViewModel.State = GovernState.Loaded;
                Host.Relayout();
            });
        }

        public string Panel { get; }

        public PanelShell Shell { get; }

        public GovernPanelViewModelBase ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public static Scene Open(string panel, int width, int height, int copies = 1) => new(panel, width, height, copies);

        /// <summary>The list of rows: the ItemsControl named after the panel's contents.</summary>
        public ItemsControl List
        {
            get
            {
                var name = Panel switch { "Tools" => "Tool rules", "Mcps" => "MCP servers", "Plugins" => "Plugins", _ => "Skills" };
                return VisualTree.Find<ItemsControl>(Shell.Page!, c => System.Windows.Automation.AutomationProperties.GetName(c) == name)
                    ?? throw new InvalidOperationException($"The {name} list was not built.");
            }
        }

        /// <summary>The page scroller (named "... page"), or null in a layout that has none.</summary>
        public ScrollViewer? PageScroll =>
            VisualTree.Find<ScrollViewer>(Shell.Page!, sv => System.Windows.Automation.AutomationProperties.GetName(sv) is { } name && name.EndsWith(" page", StringComparison.Ordinal));

        /// <summary>Top of the element inside the scrolled content (the page scroller's, when there is one).</summary>
        public double Top(FrameworkElement element)
        {
            var scroll = PageScroll;
            return scroll is null
                ? element.TranslatePoint(new Point(0, 0), Shell.Page!).Y
                : element.TranslatePoint(new Point(0, 0), scroll).Y + scroll.VerticalOffset;
        }

        /// <summary>Scrolls the page so the list starts at the top of the viewport.</summary>
        public void ScrollListToTop()
        {
            if (PageScroll is { } scroll)
            {
                scroll.ScrollToVerticalOffset(Top(List));
                Host.Relayout();
            }
        }

        /// <summary>
        /// How many DIPs of the element are on screen: its bounds cut down by every scroller it sits in (their viewports) and
        /// by the page itself.
        /// </summary>
        public double VisibleHeight(FrameworkElement element)
        {
            var page = Shell.Page!;
            var rect = Bounds(element, page);
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null && !ReferenceEquals(parent, page); parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer { IsVisible: true } scroll)
                {
                    rect.Intersect(Bounds(scroll, page));
                }
            }

            rect.Intersect(new Rect(0, 0, page.ActualWidth, page.ActualHeight));
            return rect.IsEmpty ? 0 : rect.Height;
        }

        private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
            element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
