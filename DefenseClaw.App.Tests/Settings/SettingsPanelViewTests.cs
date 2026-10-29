using System.Windows;
using System.Windows.Automation;
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
/// The Settings page as a real view in the shell stand-in (<see cref="PanelShell"/>), one area at a time (CUST-386): General, Monitoring,
/// Notifications and Connection, picked from the segmented control beside the title. Each area is checked at the Mac's 980 x 692 minimum, at the
/// window's own 940 x 620 minimum and at 1468 x 875: it scrolls instead of clipping, its content keeps to a readable width, every control has an
/// accessible name, no text is under 12 px, and the tab order is the order on the page. A PNG of each area in light and dark is written when the
/// <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
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

    /// <summary>The four areas, by name (a public test method cannot take an enum the test assembly does not own as cleanly).</summary>
    public static IEnumerable<object[]> Areas() => Enum.GetNames<SettingsArea>().Select(area => new object[] { area });

    /// <summary>Every area at both review sizes in light and dark, the Default style.</summary>
    public static IEnumerable<object[]> Renders() =>
        from area in Enum.GetNames<SettingsArea>()
        from size in new[] { "980x692", "1468x875" }
        from mode in new[] { "Light", "Dark" }
        select new object[] { area, size, mode };

    /// <summary>Every style in both modes, by name (the enums are internal, and a public test method cannot take them).</summary>
    public static IEnumerable<object[]> Looks() =>
        from style in new[] { "Default", "Linear", "Tui", "Cisco" }
        from mode in new[] { "Light", "Dark" }
        select new object[] { style, mode };

    private static AppearanceSettings Look(string style, string mode) =>
        new(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode));

    /// <summary>The headers of each area's cards, top to bottom: the Mac's sections, as Windows has them.</summary>
    private static readonly Dictionary<SettingsArea, string[]> Cards = new()
    {
        [SettingsArea.General] = new[] { "General", "Appearance", "Updates — DefenseClaw for Windows (this app)", "Updates — DefenseClaw runtime (CLI and gateway)" },
        [SettingsArea.Monitoring] = new[] { "Refresh cadence" },
        [SettingsArea.Notifications] = new[] { "Desktop notifications", "Seen-alert history" },
        [SettingsArea.Connection] = new[] { "Gateway", "Installation", "Files", "defenseclaw CLI", "Audit archive", "Companion app files" },
    };

    /// <summary>
    /// The page in the stand-in shell on <paramref name="area"/>, with the navigation pane fully open (its animation must land before the page is
    /// measured) and the page's own first look at the machine done (the file list and the CLI answer arrive asynchronously). UI thread only.
    /// </summary>
    private PanelShell Open(double width, double height, SettingsArea area, out SettingsPanel page)
    {
        var shell = new PanelShell(_services, width, height);
        page = shell.Show<SettingsPanel>();

        var model = (SettingsPanelViewModel)page.DataContext;
        Assert.True(model.IsActive, "the catalog activates a panel that is on screen");
        model.SelectedArea = area;

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

    private static (int Width, int Height) Size(string size)
    {
        var parts = size.Split('x');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ScrollViewer ScrollerOf(SettingsPanel page, SettingsArea area) => (ScrollViewer)page.FindName(area + "Area");

    private static string[] VisibleCardHeaders(DependencyObject root) =>
        VisualTree.Descendants<DcCardHeader>(root).Where(h => h.IsVisible).Select(h => (string)h.Content).ToArray();

    /// <summary>The cards <paramref name="area"/> shows: Appearance only where the app has an appearance service to drive (a fixture, or the app).</summary>
    private static string[] ExpectedCards(SettingsArea area, SettingsPanelViewModel model) =>
        Cards[area].Where(card => card != "Appearance" || model.HasAppearance).ToArray();

    // ------------------------------------------------------------------ the areas

    [Fact]
    public void The_area_picker_sits_beside_the_title_and_shows_one_area_at_a_time()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.General, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;
            var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(page));
            var picker = Assert.Single(VisualTree.Descendants<DcSegmented>(toolbar));

            Assert.Equal("Settings area", AutomationProperties.GetName(picker));
            Assert.Equal(new[] { "General", "Monitoring", "Notifications", "Connection" }, picker.Items.OfType<DcSegment>().Select(s => (string)s.Content).ToArray());
            Assert.Equal(SettingsArea.General, picker.SelectedValue);

            // Picking a segment is the view-model's area, and only that area's scroller is on screen.
            picker.SelectedIndex = 3;
            shell.Host.Relayout();
            Assert.Equal(SettingsArea.Connection, model.SelectedArea);
            foreach (var area in Enum.GetValues<SettingsArea>())
            {
                Assert.Equal(area == SettingsArea.Connection, ScrollerOf(page, area).IsVisible);
            }

            Assert.Equal(Cards[SettingsArea.Connection], VisibleCardHeaders(page));

            // And the other way: a link that changes the area moves the picker.
            model.Accept(new SettingsAreaLink(SettingsArea.Monitoring));
            shell.Host.Relayout();
            Assert.Equal(1, picker.SelectedIndex);
            Assert.Equal(Cards[SettingsArea.Monitoring], VisibleCardHeaders(page));
        });
    }

    [Theory]
    [MemberData(nameof(Areas))]
    public void At_the_minimum_windows_each_area_shows_its_cards_and_nothing_is_clipped_sideways(string name)
    {
        var area = Enum.Parse<SettingsArea>(name);

        UiThread.Run(() =>
        {
            foreach (var (width, height) in new[] { (980, 692), (940, 620) })
            {
                using var shell = Open(width, height, area, out var page);
                var scroller = ScrollerOf(page, area);

                Assert.True(scroller.IsVisible);
                Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"{area} at {width}x{height}: content is {scroller.ExtentWidth} wide in a {scroller.ViewportWidth} viewport");
                Assert.Equal(ExpectedCards(area, (SettingsPanelViewModel)page.DataContext), VisibleCardHeaders(page));
            }
        });
    }

    [Theory]
    [MemberData(nameof(Areas))]
    public void On_a_wide_window_each_area_keeps_to_a_readable_width(string name)
    {
        var area = Enum.Parse<SettingsArea>(name);

        UiThread.Run(() =>
        {
            using var shell = Open(1468, 875, area, out var page);
            var scroller = ScrollerOf(page, area);

            var card = VisualTree.Descendants<DcCardHeader>(page).First(h => h.IsVisible);
            var cardWidth = VisualTree.Descendants<Border>(page)
                .First(b => b.Child is StackPanel panel && panel.Children.Contains(card))
                .ActualWidth;

            Assert.InRange(cardWidth, 600, 880.5);
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5);
        });
    }

    [Theory]
    [MemberData(nameof(Renders))]
    public void Each_area_is_rendered_at_both_review_sizes_in_light_and_dark(string name, string size, string mode)
    {
        var area = Enum.Parse<SettingsArea>(name);
        var (width, height) = Size(size);
        using var fixture = new AppearanceFixture(Look("Default", mode));

        UiThread.Run(() =>
        {
            using var shell = Open(width, height, area, out var page);
            var scroller = ScrollerOf(page, area);

            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"{area} {size} {mode}: content overflows sideways");
            RenderTo.Png(shell.Host, $"settings-{name.ToLowerInvariant()}-{size}-{mode.ToLowerInvariant()}");
        });
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void In_every_style_and_mode_each_area_lays_out_and_no_text_is_lost_against_its_card(string style, string mode)
    {
        using var fixture = new AppearanceFixture(Look(style, mode));

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.General, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;
            var cardFill = ((SolidColorBrush)Application.Current.FindResource("DcSurfaceBrush")).Color;

            foreach (var area in Enum.GetValues<SettingsArea>())
            {
                model.SelectedArea = area;
                shell.Host.Relayout();
                var scroller = ScrollerOf(page, area);
                Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"{style} {mode} {area}: content overflows sideways");

                // Nothing is invisible against its card: every text block resolves to a brush that is not the card's own fill (a picker's chosen
                // segment is on its accent thumb, not on the card, so it is not asked).
                Assert.Equal(Cards[area], VisibleCardHeaders(scroller));
                var onThumbs = VisualTree.Descendants<DcSegment>(scroller).SelectMany(VisualTree.Descendants<TextBlock>).ToHashSet();
                foreach (var text in VisualTree.Descendants<TextBlock>(scroller).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && !onThumbs.Contains(t)))
                {
                    if (text.Foreground is SolidColorBrush brush && brush.Color.A > 0)
                    {
                        Assert.NotEqual(cardFill, brush.Color);
                    }
                }
            }

            model.SelectedArea = SettingsArea.Connection;
            shell.Host.Relayout();
            RenderTo.Png(shell.Host, $"settings-connection-980x692-{style}-{mode}");
        });
    }

    /// <summary>
    /// Each area alone fits a tall window without scrolling, and is rendered whole for review. Per area rather than the one long page: the page-wide
    /// height constant had to grow with every card anyone added (it was 4200 DIPs by CUST-372); an area's own budget only moves when that area does.
    /// </summary>
    [Theory]
    [InlineData("General", 1400)]
    [InlineData("Monitoring", 700)]
    [InlineData("Notifications", 800)]
    [InlineData("Connection", 2600)]
    public void Each_area_fits_a_tall_window_without_scrolling_and_is_rendered_whole(string name, double tallWindow)
    {
        var area = Enum.Parse<SettingsArea>(name);
        using var fixture = new AppearanceFixture(Look("Default", "Light"));

        UiThread.Run(() =>
        {
            using var shell = Open(1200, tallWindow, area, out var page);
            var scroller = ScrollerOf(page, area);

            Assert.True(scroller.ScrollableHeight < 1, $"{scroller.ScrollableHeight} DIPs of {area} are still below the fold at {tallWindow} DIPs");

            RenderTo.Png(shell.Host, $"settings-{name.ToLowerInvariant()}-whole-area");
        });
    }

    [Fact]
    public void The_installation_link_lands_on_Connection_with_the_installation_block_in_view()
    {
        _services.Navigation.Request("settings", new SettingsAreaLink(SettingsArea.Connection, SettingsAreaLink.InstallationSection));
        using var fixture = new AppearanceFixture(Look("Default", "Dark"));

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.General, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;

            // The request waiting for the panel was taken when it came on screen; Open's own pick of General came after it, so ask again, as a
            // second click on Review Installation would.
            model.Accept(new SettingsAreaLink(SettingsArea.Connection, SettingsAreaLink.InstallationSection));
            for (var i = 0; i < 5; i++)
            {
                UiThread.Settle();
                shell.Host.Relayout();
            }

            Assert.Equal(SettingsArea.Connection, model.SelectedArea);
            var scroller = ScrollerOf(page, SettingsArea.Connection);
            var card = (FrameworkElement)page.FindName("InstallationCard");
            var top = card.TranslatePoint(new Point(0, 0), scroller).Y;
            Assert.InRange(top, -1, scroller.ViewportHeight - 40);

            RenderTo.Png(shell.Host, "settings-connection-installation-link");
        });
    }

    [Fact]
    public void A_long_path_wraps_inside_its_card_and_says_itself_whole_on_hover()
    {
        var deep = Path.Combine(_temp.Path, new string('d', 48), "a-folder-whose-name-goes-on-for-a-long-while-" + new string('x', 40), "and-on");
        using var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(deep),
            claudeSettingsPath: _temp.File("claude-settings-long.json"),
            settingsPath: _temp.File("settings-long.json"),
            readerTimeouts: TestServices.ReaderTimeouts);
        using var fixture = new AppearanceFixture(Look("Default", "Light"));

        UiThread.Run(() =>
        {
            using var shell = new PanelShell(services, 980, 692);
            var page = shell.Show<SettingsPanel>();
            var model = (SettingsPanelViewModel)page.DataContext;
            model.SelectedArea = SettingsArea.Connection;
            shell.Host.Relayout();

            var scroller = ScrollerOf(page, SettingsArea.Connection);
            Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"content is {scroller.ExtentWidth} wide in a {scroller.ViewportWidth} viewport");

            var data = VisualTree.Descendants<System.Windows.Controls.TextBox>(page).First(t => AutomationProperties.GetName(t) == "Data");
            Assert.Equal(deep, data.Text);
            Assert.Equal(deep, data.ToolTip);
            Assert.True(data.ActualHeight > data.FontSize * 2, "a path longer than the card is wide wraps onto more lines");

            data.BringIntoView(new Rect(0, -60, data.ActualWidth, 260));
            shell.Host.Relayout();
            RenderTo.Png(shell.Host, "settings-connection-long-path");
        });
    }

    [Fact]
    public void The_developer_selector_shows_only_the_fields_of_the_chosen_runtime_and_nothing_while_it_is_off()
    {
        using var fixture = new AppearanceFixture(Look("Default", "Light"));

        UiThread.Run(() =>
        {
            using var shell = Open(1200, 3000, SettingsArea.Connection, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;

            bool Visible(string name) => VisualTree.Descendants<FrameworkElement>(page)
                .Any(e => e.IsVisible && AutomationProperties.GetName(e) == name);

            Assert.True(Visible("Use a different DefenseClaw runtime"));
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
            RenderTo.Png(shell.Host, "settings-installation-developer-cli");

            model.SelectedRuntimeKind = model.RuntimeKinds[2];
            model.RuntimeProblem = "The gateway address must be on this PC.";
            shell.Host.Relayout();

            Assert.True(Visible("Docker container name"));
            Assert.True(Visible("Host data folder"));
            Assert.False(Visible("Side-by-side defenseclaw.exe path"));
            RenderTo.Png(shell.Host, "settings-installation-developer-container");
        });
    }

    // ------------------------------------------------------------------ accessibility

    [Theory]
    [MemberData(nameof(Areas))]
    public void Every_control_has_an_accessible_name_and_no_text_is_smaller_than_twelve(string name)
    {
        var area = Enum.Parse<SettingsArea>(name);

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, area, out var page);

            var controls = VisualTree.Descendants<Control>(page)
                .Where(c => c.IsVisible && (c is ButtonBase or Slider or TextBoxBase or ToggleButton or Selector))
                .Where(c => c is not (RepeatButton or Thumb or ScrollBar))
                .ToList();
            Assert.True(controls.Count >= 3, $"only {controls.Count} controls found in {area}; the area did not build");

            foreach (var control in controls)
            {
                var accessible = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
                Assert.False(string.IsNullOrWhiteSpace(accessible), $"{area}: {control.GetType().Name} at {Position(control, page)} has no accessible name");
            }

            // Words, not icons: an icon is one private-use glyph in a text block of its own.
            foreach (var text in VisualTree.Descendants<TextBlock>(page).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && !t.Text.All(c => c is >= '' and <= '')))
            {
                Assert.True(text.FontSize >= 12, $"{area}: '{text.Text}' is {text.FontSize} px");
            }
        });
    }

    [Theory]
    [MemberData(nameof(Areas))]
    public void The_switches_the_slider_and_the_buttons_carry_help_text_saying_what_they_do(string name)
    {
        var area = Enum.Parse<SettingsArea>(name);

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, area, out var page);

            var described = VisualTree.Descendants<Control>(ScrollerOf(page, area))
                .Where(c => c.IsVisible && (c is ToggleSwitch or Slider or Wpf.Ui.Controls.Button))
                .Where(c => c is not (RepeatButton or Thumb))
                .ToList();

            // The icon-only buttons (copy, open folder) are named for what they do and say it in their tooltip; everything else has help text.
            foreach (var control in described)
            {
                var help = AutomationProperties.GetHelpText(control);
                var tip = control.ToolTip as string;
                Assert.True(
                    !string.IsNullOrWhiteSpace(help) || !string.IsNullOrWhiteSpace(tip),
                    $"{area}: {control.GetType().Name} '{AutomationProperties.GetName(control)}' has neither help text nor a tooltip");
            }
        });
    }

    [Theory]
    [InlineData("General", typeof(ToggleSwitch), "Start with Windows")]
    [InlineData("Monitoring", typeof(Slider), "Health poll interval in seconds")]
    [InlineData("Notifications", typeof(ToggleSwitch), "Notify on CRITICAL findings")]
    [InlineData("Connection", typeof(System.Windows.Controls.TextBox), "Gateway endpoint")]
    public void The_tab_order_is_the_order_on_the_area_top_to_bottom(string name, Type firstType, string firstName)
    {
        var area = Enum.Parse<SettingsArea>(name);

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, area, out var page);

            var stops = VisualTree.Descendants<Control>(page)
                .Where(c => c.IsVisible && c.IsEnabled && c.Focusable && c.IsTabStop && (c is ButtonBase or Slider or TextBoxBase))
                .Where(c => c is not (RepeatButton or Thumb))
                .ToList();
            Assert.True(stops.Count >= 2);

            var previous = double.MinValue;
            foreach (var stop in stops)
            {
                var y = Position(stop, page).Y;
                Assert.True(y >= previous - 12, $"{area}: {stop.GetType().Name} at y={y} comes after one at y={previous}: the tab order runs up the page");
                previous = Math.Max(previous, y);
            }

            // The page toolbar's Refresh comes first, as on every page; the stop after it is the area's first setting.
            Assert.Equal("Refresh", AutomationProperties.GetName(stops[0]));
            Assert.IsAssignableFrom(firstType, stops[1]);
            Assert.Equal(firstName, AutomationProperties.GetName(stops[1]));
        });
    }

    private static Point Position(UIElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo);

    // ------------------------------------------------------------------ the page says what the store holds

    [Fact]
    public void The_switches_show_the_stores_values_each_in_its_area_and_flipping_one_writes_the_store()
    {
        Assert.True(_services.Settings.Update(s => s with { Notifications = s.Notifications with { High = false } }));

        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.Notifications, out var page);
            var model = (SettingsPanelViewModel)page.DataContext;

            // Ten switches in all, as on the long page, each in its area: start with Windows, close to tray and reopen on the last panel (General);
            // pause (Monitoring); the three notifications (Notifications); start the gateway automatically, run gateway as administrator and a
            // different runtime (Connection).
            var perArea = new Dictionary<SettingsArea, int>();
            foreach (var area in Enum.GetValues<SettingsArea>())
            {
                model.SelectedArea = area;
                shell.Host.Relayout();
                perArea[area] = VisualTree.Descendants<ToggleSwitch>(ScrollerOf(page, area)).Count(s => s.IsVisible);
            }

            Assert.Equal(3, perArea[SettingsArea.General]);
            Assert.Equal(1, perArea[SettingsArea.Monitoring]);
            Assert.Equal(3, perArea[SettingsArea.Notifications]);
            Assert.Equal(3, perArea[SettingsArea.Connection]);

            model.SelectedArea = SettingsArea.Notifications;
            shell.Host.Relayout();
            var switches = VisualTree.Descendants<ToggleSwitch>(page).Where(s => s.IsVisible).ToList();
            var high = switches.Single(s => AutomationProperties.GetName(s) == "Notify on HIGH findings");
            Assert.False(high.IsChecked);

            var critical = switches.Single(s => AutomationProperties.GetName(s) == "Notify on CRITICAL findings");
            Assert.True(critical.IsChecked);
            critical.IsChecked = false;

            Assert.False(_services.Settings.Current.Notifications.Critical);
            Assert.False(model.NotifyCritical);
        });
    }

    [Fact]
    public void The_token_is_never_on_the_page_only_its_standing()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.Connection, out var page);

            var texts = VisualTree.Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("not found", texts);
            Assert.DoesNotContain(texts, t => t.Contains("tok-", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void The_two_update_cards_name_the_product_each_one_updates()
    {
        UiThread.Run(() =>
        {
            using var shell = Open(980, 692, SettingsArea.General, out var page);
            var headers = VisibleCardHeaders(page);

            Assert.Contains("Updates — DefenseClaw for Windows (this app)", headers);
            Assert.Contains("Updates — DefenseClaw runtime (CLI and gateway)", headers);

            var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page).Where(b => b.IsVisible).ToList();
            var labels = buttons.Select(b => b.Content as string).ToList();
            Assert.Contains("Check for Updates…", labels);
            Assert.Contains("Review update…", labels);
            Assert.Contains("Check now", labels);
            Assert.Contains("Open Updates…", labels);

            // The app card is bound to the companion's own self-update view-model (it was not, while that property was internal): its version
            // and status show, and its buttons are its commands.
            var model = (SettingsPanelViewModel)page.DataContext;
            var texts = VisualTree.Descendants<TextBlock>(page).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains(model.AppUpdate.RunningText, texts);
            Assert.Contains(model.AppUpdate.StatusText, texts);
            Assert.Same(model.AppUpdate.CheckNowCommand, buttons.Single(b => b.Content as string == "Check for Updates…").Command);
            Assert.Same(model.AppUpdate.UpdateNowCommand, buttons.Single(b => b.Content as string == "Review update…").Command);
            Assert.Same(model.CheckNowCommand, buttons.Single(b => b.Content as string == "Check now").Command);
        });
    }
}
