using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// The Settings page as a real view in the shell stand-in (<see cref="PanelShell"/>) at the window's 940 x 620 DIP minimum and at 1400 x 900,
/// in every style and both modes: it scrolls instead of clipping, its content keeps to a readable width, every control has an accessible name,
/// no text is under 12 px, and the tab order is the order on the page. A PNG of each look is written when the <c>DC_RENDER_DIR</c> environment
/// variable names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPanelViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SettingsPanelViewTests() => _services = TestServices.Create(_temp, "config_version: 8\ngateway:\n  api_port: 18970\n");

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    /// <summary>Every style in both modes, by name (the enums are internal, and a public test method cannot take them).</summary>
    public static IEnumerable<object[]> Looks() =>
        from style in new[] { "Default", "Linear", "Tui", "Cisco" }
        from mode in new[] { "Light", "Dark" }
        select new object[] { style, mode };

    private static AppearanceSettings Look(string style, string mode) =>
        new(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode));

    /// <summary>
    /// The page in the stand-in shell, with the navigation pane fully open (its animation must land before the page is measured) and the
    /// page's own first look at the machine done (the file list and the CLI answer arrive asynchronously). UI thread only.
    /// </summary>
    private PanelShell Open(double width, double height, out SettingsPanel page)
    {
        var shell = new PanelShell(_services, width, height);
        page = shell.Show<SettingsPanel>();

        var model = (SettingsPanelViewModel)page.DataContext;
        Assert.True(model.IsActive, "the catalog activates a panel that is on screen");

        var look = model.RefreshMachineFactsAsync();
        var deadline = Environment.TickCount64 + 5_000;
        while (!look.IsCompleted && Environment.TickCount64 < deadline)
        {
            UiThread.Settle();
            Thread.Sleep(10);
        }

        Assert.True(look.IsCompleted, "the page did not finish looking at the machine");

        for (var attempt = 0; attempt < 100 && shell.PageSize.Width > width - 225; attempt++)
        {
            shell.Host.Relayout();
            Thread.Sleep(20);
        }

        shell.Host.Relayout();
        return shell;
    }

    private static ScrollViewer ScrollerOf(SettingsPanel page) => VisualTree.Descendants<ScrollViewer>(page).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);

    // ------------------------------------------------------------------ layout

    [Fact]
    public void At_the_minimum_window_the_page_scrolls_its_cards_and_nothing_is_clipped_sideways()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);
            var scroller = ScrollerOf(page);

            Assert.True(scroller.ScrollableHeight > 100, $"eight cards fit in {scroller.ViewportHeight} DIPs: nothing scrolls");
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"content is {scroller.ExtentWidth} wide in a {scroller.ViewportWidth} viewport");

            var headers = VisualTree.Descendants<DcCardHeader>(page).Select(h => h.Content as string).ToArray();
            Assert.Equal(new[] { "Monitoring", "Notifications", "Startup", "Connection", "Files", "defenseclaw CLI", "Updates", "Audit archive", "Advanced" }, headers);

            RenderTo.Png(shell.Host, "settings-940x620");
        });
    }

    [Fact]
    public void On_a_wide_window_the_content_keeps_to_a_readable_width_beside_the_header()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(1400, 900, out var page);
            var scroller = ScrollerOf(page);

            var card = VisualTree.Descendants<DcCardHeader>(page).First();
            var cardWidth = VisualTree.Descendants<Border>(page)
                .First(b => b.Child is StackPanel panel && panel.Children.Contains(card))
                .ActualWidth;

            Assert.InRange(cardWidth, 600, 880.5);
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5);

            RenderTo.Png(shell.Host, "settings-1400x900");
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void In_every_style_and_mode_at_the_minimum_window_it_lays_out_the_same_and_renders(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));

        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);
            var scroller = ScrollerOf(page);

            Assert.True(scroller.ScrollableHeight > 100);
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"{style} {mode}: content overflows sideways");

            // Nothing is invisible against its card: every text block resolves to a brush that is not the card's own fill.
            var cardFill = ((SolidColorBrush)Application.Current.FindResource("DcSurfaceBrush")).Color;
            foreach (var text in VisualTree.Descendants<TextBlock>(page).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)))
            {
                if (text.Foreground is SolidColorBrush brush && brush.Color.A > 0)
                {
                    Assert.NotEqual(cardFill, brush.Color);
                }
            }

            RenderTo.Png(shell.Host, $"settings-940x620-{style}-{mode}");
        });
    }

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Cisco", "Light")]
    public void The_two_looks_the_review_compares_are_rendered_at_both_sizes(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));

        UiThread.Run(() =>
        {
            using (var small = Open(940, 620, out _))
            {
                RenderTo.Png(small.Host, $"settings-{style}-{mode}-940x620");
            }

            using var large = Open(1400, 900, out _);
            RenderTo.Png(large.Host, $"settings-{style}-{mode}-1400x900");
        });
    }

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Cisco", "Light")]
    public void The_whole_page_fits_a_tall_window_without_scrolling_and_is_rendered_for_review(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));

        UiThread.Run(() =>
        {
            using var shell = Open(1200, 3000, out var page);
            var scroller = ScrollerOf(page);

            Assert.True(scroller.ScrollableHeight < 1, $"{scroller.ScrollableHeight} DIPs of the page are still below the fold at 3000 DIPs");

            RenderTo.Png(shell.Host, $"settings-{style}-{mode}-whole-page");
        });
    }

    [Fact]
    public void The_Advanced_card_shows_only_the_fields_of_the_chosen_runtime_and_nothing_while_the_selector_is_off()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(1200, 3000, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;

            bool Visible(string name) => VisualTree.Descendants<FrameworkElement>(page)
                .Any(e => e.IsVisible && System.Windows.Automation.AutomationProperties.GetName(e) == name);

            Assert.False(Visible("Runtime to use"));
            Assert.False(Visible("Side-by-side defenseclaw.exe path"));
            Assert.False(Visible("Docker container name"));

            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[1];
            shell.Host.Relayout();

            Assert.True(Visible("Runtime to use"));
            Assert.True(Visible("Side-by-side defenseclaw.exe path"));
            Assert.True(Visible("DEFENSECLAW_HOME folder"));
            Assert.True(Visible("Gateway address"));
            Assert.False(Visible("Docker container name"));
            RenderTo.Png(shell.Host, "settings-advanced-cli");

            model.SelectedRuntimeKind = model.RuntimeKinds[2];
            model.RuntimeProblem = "The gateway address must be on this PC.";
            shell.Host.Relayout();

            Assert.True(Visible("Docker container name"));
            Assert.True(Visible("Host data folder"));
            Assert.False(Visible("Side-by-side defenseclaw.exe path"));
            RenderTo.Png(shell.Host, "settings-advanced-container");
        });
    }

    // ------------------------------------------------------------------ accessibility

    [Fact]
    public void Every_control_has_an_accessible_name_and_no_text_is_smaller_than_twelve()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);

            var controls = VisualTree.Descendants<Control>(page)
                .Where(c => c.IsVisible && (c is ButtonBase or Slider or TextBoxBase or ToggleButton))
                .Where(c => c is not (RepeatButton or Thumb or ScrollBar))
                .ToList();
            Assert.True(controls.Count >= 25, $"only {controls.Count} controls found; the page did not build");

            foreach (var control in controls)
            {
                var name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
                Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} at {Position(control, page)} has no accessible name");
            }

            // Words, not icons: an icon is one private-use glyph in a text block of its own.
            foreach (var text in VisualTree.Descendants<TextBlock>(page).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && !t.Text.All(c => c is >= '' and <= '')))
            {
                Assert.True(text.FontSize >= 12, $"'{text.Text}' is {text.FontSize} px");
            }
        });
    }

    [Fact]
    public void The_switches_the_slider_and_the_buttons_carry_help_text_saying_what_they_do()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);

            var described = VisualTree.Descendants<Control>(page)
                .Where(c => c.IsVisible && (c is ToggleSwitch or Slider or Wpf.Ui.Controls.Button))
                .Where(c => c is not (RepeatButton or Thumb))
                .ToList();

            // The icon-only buttons (copy, open folder) are named for what they do and say it in their tooltip; everything else has help text.
            foreach (var control in described)
            {
                var help = System.Windows.Automation.AutomationProperties.GetHelpText(control);
                var tip = control.ToolTip as string;
                Assert.True(
                    !string.IsNullOrWhiteSpace(help) || !string.IsNullOrWhiteSpace(tip),
                    $"{control.GetType().Name} '{System.Windows.Automation.AutomationProperties.GetName(control)}' has neither help text nor a tooltip");
            }
        });
    }

    [Fact]
    public void The_tab_order_is_the_order_on_the_page_top_to_bottom()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);

            var stops = VisualTree.Descendants<Control>(page)
                .Where(c => c.IsVisible && c.IsEnabled && c.Focusable && c.IsTabStop && (c is ButtonBase or Slider or TextBoxBase))
                .Where(c => c is not (RepeatButton or Thumb))
                .ToList();
            Assert.True(stops.Count >= 25);

            var previous = double.MinValue;
            foreach (var stop in stops)
            {
                var y = Position(stop, page).Y;
                Assert.True(y >= previous - 12, $"{stop.GetType().Name} at y={y} comes after one at y={previous}: the tab order runs up the page");
                previous = Math.Max(previous, y);
            }

            // The page toolbar's Refresh comes first, as on every page; the stop after it is the first setting.
            Assert.Equal("Refresh", System.Windows.Automation.AutomationProperties.GetName(stops[0]));
            Assert.IsType<Slider>(stops[1]);
        });
    }

    private static Point Position(UIElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo);

    // ------------------------------------------------------------------ the page says what the store holds

    [Fact]
    public void The_switches_show_the_stores_values_and_flipping_one_writes_the_store()
    {
        Assert.True(_services.Settings.Update(s => s with { Notifications = s.Notifications with { High = false } }));

        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);
            var switches = VisualTree.Descendants<ToggleSwitch>(page).ToList();

            // Pause, three notifications, start with Windows, close to tray, start the gateway automatically, reopen on the last panel, a different runtime.
            Assert.Equal(9, switches.Count);
            var high = switches.Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Notify on HIGH findings");
            Assert.False(high.IsChecked);

            var critical = switches.Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Notify on CRITICAL findings");
            Assert.True(critical.IsChecked);
            critical.IsChecked = false;

            Assert.False(_services.Settings.Current.Notifications.Critical);
            Assert.False(((SettingsPanelViewModel)page.DataContext).NotifyCritical);
        });
    }

    [Fact]
    public void The_token_is_never_on_the_page_only_its_standing()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(940, 620, out var page);

            var texts = VisualTree.Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("not found", texts);
            Assert.DoesNotContain(texts, t => t.Contains("tok-", StringComparison.OrdinalIgnoreCase));
        });
    }
}
