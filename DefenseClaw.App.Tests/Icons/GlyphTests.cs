using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;

namespace DefenseClaw.App.Tests.Icons;

/// <summary>
/// The status and severity glyphs (CUST-196): what each tone key means to them, that their shape set follows the style
/// (Default a dot, Linear status circles and priority bars, TUI flat terminal shapes) and their colours follow the mode,
/// live, and that what they draw is what the style promises. Every test that builds one runs on the shared UI thread.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GlyphTests
{
    // ------------------------------------------------------------------ the tone vocabulary

    [Theory]
    [InlineData("Critical", GlyphTone.Critical)]
    [InlineData("Bad", GlyphTone.Critical)]
    [InlineData("High", GlyphTone.High)]
    [InlineData("Warn", GlyphTone.High)]
    [InlineData("Medium", GlyphTone.Medium)]
    [InlineData("Ok", GlyphTone.Ok)]
    [InlineData("Low", GlyphTone.Low)]
    [InlineData("Info", GlyphTone.Neutral)]
    [InlineData("Neutral", GlyphTone.Neutral)]
    [InlineData("critical", GlyphTone.Critical)]
    [InlineData(" HIGH ", GlyphTone.High)]
    [InlineData("", GlyphTone.Neutral)]
    [InlineData(null, GlyphTone.Neutral)]
    [InlineData("nonsense", GlyphTone.Neutral)]
    public void The_tone_keys_of_the_design_system_fold_to_what_a_glyph_tells_apart(string? key, GlyphTone expected)
    {
        Assert.Equal(expected, DcGlyph.ParseTone(key));
    }

    // ------------------------------------------------------------------ the shape set follows the style, live

    [Fact]
    public void A_glyph_takes_its_shape_set_from_the_style_and_follows_a_live_switch()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var status = new DcStatusGlyph { Tone = "Ok" };
            var severity = new DcSeverityGlyph { Tone = "High" };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { status, severity } } };
            try
            {
                Assert.Equal(DcGlyphFamilies.Dot, status.Family);
                Assert.Equal(DcGlyphFamilies.Dot, severity.Family);

                fixture.Service.SetStyle(AppearanceStyle.Linear);
                Assert.Equal(DcGlyphFamilies.Linear, status.Family);
                Assert.Equal(DcGlyphFamilies.Linear, severity.Family);

                fixture.Service.SetStyle(AppearanceStyle.Tui);
                Assert.Equal(DcGlyphFamilies.Terminal, status.Family);
                Assert.Equal(DcGlyphFamilies.Terminal, severity.Family);

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(DcGlyphFamilies.Dot, status.Family);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Under_Default_a_status_glyph_is_the_ten_pixel_dot_and_a_severity_glyph_takes_no_room()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var status = new DcStatusGlyph { Tone = "Ok" };
            var large = new DcStatusGlyph { Tone = "Ok", GlyphSize = 20, DotSize = 12 };
            var inBadge = new DcStatusGlyph { Tone = "Ok", ShowDot = false, GlyphSize = 12, Gap = 5 };
            var severity = new DcSeverityGlyph { Tone = "High", GlyphSize = 12, Gap = 5 };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { status, large, inBadge, severity } } };
            try
            {
                Measure(window);

                // What the DcToneDot / DcToneDotLarge they replace measured.
                Assert.Equal(new Size(10, 10), status.DesiredSize);
                Assert.Equal(new Size(12, 12), large.DesiredSize);

                // A glyph added inside a badge that already says it in words draws nothing, and reserves nothing.
                Assert.Equal(new Size(0, 0), inBadge.DesiredSize);
                Assert.Equal(Visibility.Collapsed, severity.Visibility);
                Assert.Equal(new Size(0, 0), severity.DesiredSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("Linear")]
    [InlineData("Tui")]
    public void Under_Linear_and_TUI_both_glyphs_draw_and_the_gap_and_the_slot_are_honoured(string styleName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var status = new DcStatusGlyph { Tone = "Ok" };
            var inBadge = new DcStatusGlyph { Tone = "Ok", ShowDot = false, GlyphSize = 12, Gap = 5 };
            var severity = new DcSeverityGlyph { Tone = "High", GlyphSize = 12, Gap = 5 };
            var inSlot = new DcStatusGlyph { Tone = "Ok", SlotHeight = 18 };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { status, inBadge, severity, inSlot } } };
            try
            {
                Measure(window);

                Assert.Equal(new Size(16, 16), status.DesiredSize);
                Assert.Equal(new Size(17, 12), inBadge.DesiredSize);
                Assert.Equal(Visibility.Visible, severity.Visibility);
                Assert.Equal(new Size(17, 12), severity.DesiredSize);

                // Centred on a text line: the box is as tall as the line, not as tall as the shape.
                Assert.Equal(new Size(16, 18), inSlot.DesiredSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_severity_glyph_comes_back_when_the_style_changes_from_Default_to_Linear()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var severity = new DcSeverityGlyph { Tone = "Critical" };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { severity } } };
            try
            {
                Measure(window);
                Assert.Equal(Visibility.Collapsed, severity.Visibility);

                fixture.Service.SetStyle(AppearanceStyle.Linear);
                Measure(window);
                Assert.Equal(Visibility.Visible, severity.Visibility);
                Assert.Equal(new Size(16, 16), severity.DesiredSize);

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(Visibility.Collapsed, severity.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_glyph_is_a_mark_by_default_and_a_template_can_still_align_it()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var plain = new DcStatusGlyph();
            Assert.False(plain.IsHitTestVisible);
            Assert.Equal(VerticalAlignment.Center, plain.VerticalAlignment);
            Assert.Equal(HorizontalAlignment.Left, plain.HorizontalAlignment);

            // The defaults are metadata, not values set in the constructor: a value a DataTemplate gives an element ranks below
            // one its constructor set, which once left every glyph in a row's own column centred when the row said Top.
            var list = (ItemsControl)System.Windows.Markup.XamlReader.Parse("""
                <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:ctl="clr-namespace:DefenseClaw.App.Views.Controls;assembly=DefenseClaw.App">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <ctl:DcStatusGlyph Tone="Ok" SlotHeight="18" VerticalAlignment="Top" />
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                    <ItemsControl.ItemsSource>
                        <x:Array xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Type="{x:Type x:String}">
                            <x:String>one</x:String>
                        </x:Array>
                    </ItemsControl.ItemsSource>
                </ItemsControl>
                """);
            using var host = new OffscreenHost(list, 200, 100);

            var glyph = VisualTree.Find<DcStatusGlyph>(list)!;
            Assert.Equal(VerticalAlignment.Top, glyph.VerticalAlignment);
        });
    }

    // ------------------------------------------------------------------ colours follow the mode, live

    [Fact]
    public void A_glyph_is_drawn_in_the_tone_token_and_repaints_when_the_mode_flips()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var glyph = new DcStatusGlyph { Tone = "Bad" };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { glyph } } };
            try
            {
                var dark = AppearanceFixture.ColorOf("DcToneCriticalBrush");
                Assert.Equal(dark, ((SolidColorBrush)glyph.BrushOf(GlyphTone.Critical)!).Color);

                fixture.Service.SetMode(AppearanceMode.Light);

                var light = AppearanceFixture.ColorOf("DcToneCriticalBrush");
                Assert.NotEqual(dark, light);
                Assert.Equal(light, ((SolidColorBrush)glyph.BrushOf(GlyphTone.Critical)!).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ what is actually drawn

    [Fact]
    public void Linear_status_circle_for_ok_is_a_green_disc_with_the_check_cut_out_of_it()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        var (disc, check, corner, tone, window) = UiThread.Run(() =>
        {
            var glyph = new DcStatusGlyph { Tone = "Ok", GlyphSize = 64, VerticalAlignment = VerticalAlignment.Top };
            using var host = new OffscreenHost(glyph, 64, 64);
            var bitmap = Render(host);
            return (
                PixelAt(bitmap, 10, 32),
                PixelAt(bitmap, 37, 33),
                PixelAt(bitmap, 2, 2),
                AppearanceFixture.ColorOf("DcToneOkBrush"),
                AppearanceFixture.ColorOf("DcWindowBackgroundBrush"));
        });

        AssertColor(tone, disc);

        // A true cut-out: the page shows through the check, whatever the glyph sits on.
        AssertColor(window, check);
        AssertColor(window, corner);
    }

    [Fact]
    public void Terminal_status_for_a_failure_is_a_flat_square_in_the_tone_colour()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark));

        var (center, corner, tone, window) = UiThread.Run(() =>
        {
            var glyph = new DcStatusGlyph { Tone = "Bad", GlyphSize = 64, VerticalAlignment = VerticalAlignment.Top };
            using var host = new OffscreenHost(glyph, 64, 64);
            var bitmap = Render(host);
            return (
                PixelAt(bitmap, 32, 32),
                PixelAt(bitmap, 6, 6),
                AppearanceFixture.ColorOf("DcToneCriticalBrush"),
                AppearanceFixture.ColorOf("DcWindowBackgroundBrush"));
        });

        AssertColor(tone, center);
        AssertColor(window, corner);
    }

    [Fact]
    public void Linear_priority_glyph_for_low_lights_one_bar_and_dims_the_other_two()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        var (lit, dim, tone, window) = UiThread.Run(() =>
        {
            var glyph = new DcSeverityGlyph { Tone = "Low", GlyphSize = 64, VerticalAlignment = VerticalAlignment.Top };
            using var host = new OffscreenHost(glyph, 64, 64);
            var bitmap = Render(host);

            // Bar 1 (x 2.4..5.0, height 4.6) is lit; bar 3 (x 11.2..13.8, height 11.8) is dimmed to 30%.
            return (
                PixelAt(bitmap, 14, 50),
                PixelAt(bitmap, 50, 50),
                AppearanceFixture.ColorOf("DcToneNeutralBrush"),
                AppearanceFixture.ColorOf("DcWindowBackgroundBrush"));
        });

        AssertColor(tone, lit);
        Assert.NotEqual(Tuple.Create(tone.R, tone.G, tone.B), Tuple.Create(dim.R, dim.G, dim.B));
        Assert.NotEqual(Tuple.Create(window.R, window.G, window.B), Tuple.Create(dim.R, dim.G, dim.B));

        // 30% of the tone over the window, give or take rounding.
        AssertColor(Blend(tone, window, 0.3), dim, tolerance: 6);
    }

    [Fact]
    public void Linear_priority_glyph_for_critical_is_a_square_with_an_exclamation_mark_cut_out()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        var (body, mark, outside, tone, window) = UiThread.Run(() =>
        {
            var glyph = new DcSeverityGlyph { Tone = "Critical", GlyphSize = 64, VerticalAlignment = VerticalAlignment.Top };
            using var host = new OffscreenHost(glyph, 64, 64);
            var bitmap = Render(host);
            return (
                PixelAt(bitmap, 14, 32),
                PixelAt(bitmap, 32, 26),
                PixelAt(bitmap, 2, 2),
                AppearanceFixture.ColorOf("DcToneCriticalBrush"),
                AppearanceFixture.ColorOf("DcWindowBackgroundBrush"));
        });

        AssertColor(tone, body);
        AssertColor(window, mark);
        AssertColor(window, outside);
    }

    // ------------------------------------------------------------------ decorative

    [Fact]
    public void Glyphs_and_the_decorative_icon_are_invisible_to_UI_Automation()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            // A glyph never has a peer of its own: the words beside it carry the meaning.
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(new DcStatusGlyph { Tone = "Ok" }));
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(new DcSeverityGlyph { Tone = "High" }));

            // The icon has one, so its glyph text is not exposed as a Text element beside the label - and it is neither a
            // control nor content, and holds no children.
            var icon = new DcSymbolIcon(Wpf.Ui.Controls.SymbolRegular.Shield24);
            var peer = UIElementAutomationPeer.CreatePeerForElement(icon);
            Assert.NotNull(peer);
            Assert.False(peer!.IsControlElement());
            Assert.False(peer.IsContentElement());
            Assert.Empty(peer.GetChildren() ?? new List<AutomationPeer>());
        });
    }

    // ------------------------------------------------------------------ helpers

    private static void Measure(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(400, 400));
        content.Arrange(new Rect(0, 0, 400, 400));
        content.UpdateLayout();
    }

    private static BitmapSource Render(OffscreenHost host)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-glyph-{Guid.NewGuid():N}.png");
        try
        {
            host.RenderPng(path);
            using var stream = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static Color Blend(Color top, Color behind, double alpha) => Color.FromRgb(
        (byte)Math.Round((top.R * alpha) + (behind.R * (1 - alpha))),
        (byte)Math.Round((top.G * alpha) + (behind.G * (1 - alpha))),
        (byte)Math.Round((top.B * alpha) + (behind.B * (1 - alpha))));

    private static void AssertColor(Color expected, Color actual, int tolerance = 3)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected.R:X2}{expected.G:X2}{expected.B:X2} but the pixel is {actual.R:X2}{actual.G:X2}{actual.B:X2}");
    }
}
