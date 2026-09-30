using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.ConfigEditor;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>Windows, the tray flyout's font, the YAML editor and the title-bar flyout following the look.</summary>
[Collection(UiCollection.Name)]
public sealed class AppearanceWindowsTests
{
    private const string TuiMono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

    // ------------------------------------------------------------------ windows

    [Fact]
    public void An_attached_window_wears_the_styles_font_and_gives_it_back_under_Default()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var systemFont = new Window().FontFamily.Source;
            var window = new Window();
            try
            {
                fixture.Service.Attach(window);
                Assert.Equal(TuiMono, window.FontFamily.Source);

                fixture.Service.SetStyle(AppearanceStyle.Linear);
                Assert.Equal("Inter, Segoe UI Variable Text, Segoe UI", window.FontFamily.Source);

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(systemFont, window.FontFamily.Source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_window_that_never_calls_Attach_is_attached_when_it_loads()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var window = new Window();
            try
            {
                var before = window.FontFamily.Source;
                Assert.NotEqual(TuiMono, before);

                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));

                Assert.Equal(TuiMono, window.FontFamily.Source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Attaching_twice_is_harmless_and_a_plain_window_keeps_its_own_background()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            // The tray flyout is a plain, transparent Window: the service styles its font, never its background.
            var window = new Window { Background = Brushes.Transparent };
            try
            {
                fixture.Service.Attach(window);
                fixture.Service.Attach(window);
                fixture.Service.SetStyle(AppearanceStyle.Tui);

                Assert.Same(Brushes.Transparent, window.Background);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_fluent_window_without_a_native_handle_is_attached_without_error_and_still_gets_the_font()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var window = new FluentWindow();
            try
            {
                fixture.Service.Attach(window);
                fixture.Service.SetStyle(AppearanceStyle.Tui);
                fixture.Service.SetMode(AppearanceMode.Dark);

                Assert.Equal(TuiMono, window.FontFamily.Source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_window_font_is_a_live_resource_reference_so_it_follows_a_later_switch_without_a_reattach()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var window = new Window();
            var text = new System.Windows.Controls.TextBlock { Text = "x" };
            window.Content = text;
            try
            {
                fixture.Service.Attach(window);
                Assert.StartsWith("Inter", text.FontFamily.Source, StringComparison.Ordinal);

                fixture.Service.SetStyle(AppearanceStyle.Tui);

                Assert.Equal(TuiMono, text.FontFamily.Source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ YAML

    [Fact]
    public void The_YAML_highlighting_follows_the_style_and_mode_and_caches_each_look()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        var defaultDark = UiThread.Run(YamlHighlighting.ForCurrent);
        Assert.Same(YamlHighlighting.Instance, defaultDark);

        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Linear));
        var linearDark = UiThread.Run(YamlHighlighting.ForCurrent);

        UiThread.Run(() => fixture.Service.SetMode(AppearanceMode.Light));
        var linearLight = UiThread.Run(YamlHighlighting.ForCurrent);

        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Tui));
        var tuiLight = UiThread.Run(YamlHighlighting.ForCurrent);

        Assert.NotSame(defaultDark, linearDark);
        Assert.NotSame(linearDark, linearLight);
        Assert.NotSame(linearLight, tuiLight);

        // Same look again: the same definition, not a rebuild.
        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Linear));
        Assert.Same(linearLight, UiThread.Run(YamlHighlighting.ForCurrent));
    }

    [Theory]
    [InlineData("Default", true, "#4FC1FF")]
    [InlineData("Default", false, "#0451A5")]
    [InlineData("Linear", true, "#9AA5F0")]
    [InlineData("Linear", false, "#4C57C4")]
    [InlineData("Tui", true, "#22D3EE")]
    [InlineData("Tui", false, "#0E7490")]
    public void Each_look_colours_YAML_keys_from_its_own_palette(string style, bool dark, string keyColour)
    {
        var palette = YamlHighlighting.PaletteFor(Enum.Parse<AppearanceStyle>(style), dark);

        Assert.Equal(keyColour, palette["@KEY@"]);
        Assert.Equal(8, palette.Count);
    }

    [Fact]
    public void The_original_YAML_palettes_are_unchanged()
    {
        Assert.Equal("#6A9955", YamlHighlighting.PaletteFor(AppearanceStyle.Default, dark: true)["@COMMENT@"]);
        Assert.Equal("#CE9178", YamlHighlighting.PaletteFor(AppearanceStyle.Default, dark: true)["@STRING@"]);
        Assert.Equal("#008000", YamlHighlighting.PaletteFor(AppearanceStyle.Default, dark: false)["@COMMENT@"]);
        Assert.Equal("#A31515", YamlHighlighting.PaletteFor(AppearanceStyle.Default, dark: false)["@STRING@"]);
    }

    // ------------------------------------------------------------------ the title-bar flyout

    private static IEnumerable<T> Logical<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Logical<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static AppearanceFlyout NewFlyout(FakeAppearance appearance) => UiThread.Run(() =>
    {
        var flyout = new AppearanceFlyout();
        flyout.Bind(appearance);
        return flyout;
    });

    [Fact]
    public void The_flyout_offers_four_styles_and_three_modes_with_the_current_ones_chosen()
    {
        var appearance = new FakeAppearance { Style = AppearanceStyle.Linear, Mode = AppearanceMode.Dark };
        var flyout = NewFlyout(appearance);

        UiThread.Run(() =>
        {
            var radios = Logical<RadioButton>(flyout).ToList();
            var styles = radios.Where(r => r.GroupName == "AppearanceStyle").ToList();
            var modes = radios.Where(r => r.GroupName == "AppearanceMode").ToList();

            Assert.Equal(new[] { "Default style", "Linear style", "TUI style", "Cisco style" }, styles.Select(r => System.Windows.Automation.AutomationProperties.GetName(r)).ToArray());
            Assert.Equal(new[] { "System mode", "Light mode", "Dark mode" }, modes.Select(r => System.Windows.Automation.AutomationProperties.GetName(r)).ToArray());
            Assert.Equal(new[] { false, true, false, false }, styles.Select(r => r.IsChecked == true).ToArray());
            Assert.Equal(new[] { false, false, true }, modes.Select(r => r.IsChecked == true).ToArray());
            Assert.All(radios, r => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(r))));
        });
    }

    [Fact]
    public void Choosing_a_radio_applies_at_once_and_the_radios_follow_the_service()
    {
        var appearance = new FakeAppearance { Style = AppearanceStyle.Default, Mode = AppearanceMode.System };
        var flyout = NewFlyout(appearance);

        UiThread.Run(() =>
        {
            var radios = Logical<RadioButton>(flyout).ToList();
            radios.First(r => System.Windows.Automation.AutomationProperties.GetName(r) == "TUI style").IsChecked = true;
            radios.First(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Light mode").IsChecked = true;

            Assert.Equal(new[] { "style:Tui", "mode:Light" }, appearance.Calls);

            // Something else (the palette, the shortcut) changes the look: the flyout, if open, shows it.
            appearance.Style = AppearanceStyle.Linear;
            appearance.Mode = AppearanceMode.Dark;
            flyout.Refresh();

            Assert.True(radios.First(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Linear style").IsChecked);
            Assert.True(radios.First(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Dark mode").IsChecked);
            Assert.Equal(2, appearance.Calls.Count);   // syncing the radios is not a choice: no extra calls
        });
    }

    [Fact]
    public void Escape_asks_the_shell_to_close_the_flyout()
    {
        var flyout = NewFlyout(new FakeAppearance());
        var closes = 0;
        flyout.CloseRequested += (_, _) => closes++;

        UiThread.Run(() =>
        {
            using var host = new OffscreenHost(flyout, 400, 500);
            var source = System.Windows.PresentationSource.FromVisual(flyout)!;
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            };

            flyout.RaiseEvent(args);

            Assert.True(args.Handled);
        });

        Assert.Equal(1, closes);
    }

    [Fact]
    public void The_flyout_paints_each_swatch_from_the_services_colours_and_binding_twice_does_not_double_up()
    {
        var appearance = new FakeAppearance();
        var flyout = NewFlyout(appearance);

        UiThread.Run(() =>
        {
            flyout.Bind(appearance);
            flyout.Bind(appearance);

            Assert.Equal(7, Logical<RadioButton>(flyout).Count());

            var swatches = Logical<Border>(flyout).Where(b => b.Width == 44).ToList();
            Assert.Equal(4, swatches.Count);
            Assert.All(swatches, s => Assert.Equal(Colors.Black, ((SolidColorBrush)s.Background).Color));

            appearance.SetStyle(AppearanceStyle.Tui);   // raises Changed; the refresh is queued on the dispatcher
        });

        UiThread.Run(UiThread.Settle);
        Assert.Single(appearance.Calls);
    }
}
