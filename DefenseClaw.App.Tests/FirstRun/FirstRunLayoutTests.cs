using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.FirstRun;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Install;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run window and the Overview's "not configured" rows as real views (CUST-210), at the window's 940 x 620 DIP minimum in the two looks
/// the review compares (Cisco Light, Default Dark). The window is built but never shown: its content is lifted into the offscreen host. A PNG of
/// each is written when <c>DC_RENDER_DIR</c> names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class FirstRunLayoutTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public FirstRunLayoutTests()
    {
        _ = _temp.WriteFile(
            "ai_discovery_state.json",
            "{\"signals\":{\"a\":{\"name\":\"Codex\",\"category\":\"active_process\",\"state\":\"new\",\"supported_connector\":\"codex\"}," +
            "\"b\":{\"name\":\"Cursor\",\"category\":\"active_process\",\"state\":\"seen\",\"supported_connector\":\"cursor\"}}}");
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    public static IEnumerable<object[]> Looks() => new[]
    {
        new object[] { "Cisco", "Light" },
        new object[] { "Default", "Dark" },
    };

    private static AppearanceSettings Look(string style, string mode) =>
        new(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode));

    private static GatewaySnapshot Installed() => new()
    {
        State = AppGatewayState.NotInitialized,
        Install = InstallState.InstalledNotInitialized,
        CliPath = @"C:\synthetic\defenseclaw.exe",
    };

    private static GatewaySnapshot NotInstalled() => new() { State = AppGatewayState.NotInstalled, Install = InstallState.NotInstalled };

    /// <summary>The window's content in the offscreen host, with its model. Built and disposed on the UI thread.</summary>
    private sealed class WindowScene : IDisposable
    {
        public WindowScene(AppServices services, GatewaySnapshot snapshot, double width = 940, double height = 620)
        {
            ViewModel = UiThread.Run(() => new FirstRunViewModel(services, snapshot: snapshot));
            (Host, Content) = UiThread.Run(() =>
            {
                var window = new FirstRunWindow(ViewModel);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = ViewModel;

                // The title bar needs a real window to attach to; the 32 DIP strip it occupies is kept.
                var grid = (Grid)content;
                var bar = grid.Children.OfType<Wpf.Ui.Controls.TitleBar>().Single();
                grid.Children.Remove(bar);
                grid.RowDefinitions[0].Height = new GridLength(32);
                return (new OffscreenHost(content, width, height), content);
            });
        }

        public FirstRunViewModel ViewModel { get; }

        public OffscreenHost Host { get; }

        public FrameworkElement Content { get; }

        public void Dispose() => UiThread.Run(() =>
        {
            Host.Dispose();
            ViewModel.Dispose();
        });
    }

    private static bool Inside<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element); parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
        {
            if (parent is T)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Presses Detect, waits for the scan to be read, and lays out again.</summary>
    private static void Detect(WindowScene scene)
    {
        UiThread.Run(() => scene.ViewModel.DetectCommand.Execute(null));
        UiThread.WaitFor(() => scene.ViewModel.HasDetected, "detection reads the scan and lists what it found");
        UiThread.Run(scene.Host.Relayout);
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_form_before_and_after_detection_is_rendered_and_nothing_is_clipped(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));
        using var scene = new WindowScene(_services, Installed());

        UiThread.Run(() => RenderTo.Png(scene.Host, $"firstrun-{style}-{mode}-940x620-before-detect"));

        Detect(scene);

        UiThread.Run(() =>
        {
            RenderTo.Png(scene.Host, $"firstrun-{style}-{mode}-940x620-detected");

            Assert.Equal(new[] { "codex", "cursor" }, scene.ViewModel.Detected.Select(d => d.Id).ToArray());
            var scroll = VisualTree.Descendants<ScrollViewer>(scene.Content).First(s => s.Name == "PageScroll");
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "the page needs no horizontal scrolling");
        });
    }

    [Fact]
    public void Not_installed_shows_the_installer_card_and_hides_the_form()
    {
        using var scene = new WindowScene(_services, NotInstalled());

        UiThread.Run(() =>
        {
            var headers = VisualTree.Descendants<DcCardHeader>(scene.Content).Where(h => h.IsVisible).Select(h => h.Content as string).ToArray();

            Assert.Contains("Install the DefenseClaw runtime", headers);
            Assert.DoesNotContain("Policy and enforcement", headers);
            Assert.DoesNotContain("Agent and connectors", headers);
            RenderTo.Png(scene.Host, "firstrun-not-installed-940x620");
        });
    }

    [Fact]
    public void Every_control_has_an_accessible_name_and_no_text_is_smaller_than_twelve()
    {
        using var scene = new WindowScene(_services, Installed());
        Detect(scene);

        UiThread.Run(() =>
        {
            var controls = VisualTree.Descendants<Control>(scene.Content)
                .Where(c => c.IsVisible && c is (ButtonBase or ComboBox or Wpf.Ui.Controls.ToggleSwitch) and not (RepeatButton or Thumb))

                // The parts of a switch or a combo box are its template's; the control carries the name.
                .Where(c => !(c is ToggleButton and not CheckBox && (Inside<Wpf.Ui.Controls.ToggleSwitch>(c) || Inside<ComboBox>(c))))
                .ToList();
            Assert.True(controls.Count >= 8, $"only {controls.Count} controls found; the window did not build");

            foreach (var control in controls)
            {
                var name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
                Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} has no accessible name");
            }

            // Words, not icons: an icon is one private-use glyph in a text block of its own.
            foreach (var text in VisualTree.Descendants<TextBlock>(scene.Content).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && !t.Text.All(c => c is >= '\uE000' and <= '\uF8FF')))
            {
                Assert.True(text.FontSize >= 12, $"'{text.Text}' is {text.FontSize} px");
            }
        });
    }

    [Fact]
    public void The_review_overlay_is_part_of_the_window_and_opens_over_the_form_with_the_exact_commands()
    {
        using var scene = new WindowScene(_services, Installed());

        UiThread.Run(() =>
        {
            var overlay = VisualTree.Descendants<DiscoverReviewOverlay>(scene.Content).Single();
            var scrim = (UIElement)overlay.FindName("Scrim");
            Assert.False(scrim.IsVisible);

            scene.ViewModel.RunCommand.Execute(null);
            scene.Host.Relayout();

            Assert.True(scene.ViewModel.Review.IsOpen);
            Assert.True(scrim.IsVisible);
            RenderTo.Png(scene.Host, "firstrun-review-940x620");
        });
    }

    // ---- the Overview's not-configured rows ----

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_connectors_table_lists_not_configured_rows_with_an_add_button_and_is_rendered(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));
        using var data = OverviewScene.Create(seedAudit: false);
        PanelShell? shell = null;
        OverviewPanel? view = null;
        OverviewPanelViewModel? vm = null;

        UiThread.Run(() =>
        {
            data.Publish(OverviewScene.Snapshot());
            shell = new PanelShell(data.Services, 940, 620);
            view = shell.Show<OverviewPanel>();
            vm = (OverviewPanelViewModel)shell.ViewModel;
            vm.Apply(OverviewScene.Snapshot());
            _ = vm.RefreshAgentsAsync(CancellationToken.None);
        });

        try
        {
            UiThread.WaitFor(() => vm!.HasUnconfiguredRows, "the scan's detected connectors reach the table");

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();

                var table = VisualTree.Descendants<ListBox>(view!).First(l => System.Windows.Automation.AutomationProperties.GetName(l) == "Connectors");
                var adds = VisualTree.Descendants<Wpf.Ui.Controls.Button>(table).Where(b => Equals(b.Content, "Add") && b.IsVisible).ToList();

                Assert.True(adds.Count >= 2, $"{adds.Count} Add buttons are visible");
                Assert.All(adds, b => Assert.False(string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(b)?.GetName())));
                Assert.Contains(VisualTree.Descendants<TextBlock>(table), t => t.IsVisible && t.Text == "not configured");

                // The table is far down the page; render the page scrolled to it.
                var scroller = VisualTree.Descendants<ScrollViewer>(view!).First();
                var origin = table.TranslatePoint(new Point(0, 0), scroller);
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + origin.Y - 80);
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, $"overview-connectors-{style}-{mode}-940x620");
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
