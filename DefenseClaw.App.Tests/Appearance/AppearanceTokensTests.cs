using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>
/// The token dictionaries themselves: every style defines the whole vocabulary and nothing else, with the right kind of
/// value for each token, and Linear / TUI hold the values the brief gave them.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppearanceTokensTests
{
    public static IEnumerable<object[]> Dictionaries()
    {
        yield return new object[] { "Default", "Dark" };
        yield return new object[] { "Linear", "Dark" };
        yield return new object[] { "Linear", "Light" };
        yield return new object[] { "Tui", "Dark" };
        yield return new object[] { "Tui", "Light" };
    }

    private static ResourceDictionary Load(string style, string mode) => UiThread.Run(() =>
        new ResourceDictionary { Source = AppearanceCatalog.TokenSource(Enum.Parse<AppearanceStyle>(style), mode == "Dark") });

    private static string Hex(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return c.A == 0xFF ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ------------------------------------------------------------------ shape of every dictionary

    [Theory]
    [MemberData(nameof(Dictionaries))]
    public void Each_style_defines_the_whole_token_vocabulary_and_nothing_else(string style, string mode)
    {
        var dictionary = Load(style, mode);
        var defined = dictionary.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet(StringComparer.Ordinal);
        var expected = AppearanceTokens.All.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(expected.Except(defined));
        Assert.Empty(defined.Except(expected));
        Assert.Empty(dictionary.MergedDictionaries);
    }

    [Theory]
    [MemberData(nameof(Dictionaries))]
    public void Each_token_is_the_kind_of_value_its_consumers_expect(string style, string mode)
    {
        var dictionary = Load(style, mode);

        UiThread.Run(() =>
        {
            foreach (var key in AppearanceTokens.Brushes)
            {
                Assert.IsType<SolidColorBrush>(dictionary[key]);
            }

            foreach (var key in new[] { AppearanceTokens.UiFontFamily, AppearanceTokens.MonoFontFamily })
            {
                Assert.IsType<FontFamily>(dictionary[key]);
            }

            foreach (var key in new[] { AppearanceTokens.RadiusS, AppearanceTokens.RadiusM, AppearanceTokens.RadiusL, AppearanceTokens.ControlRadius, AppearanceTokens.BarRadius })
            {
                Assert.IsType<CornerRadius>(dictionary[key]);
            }

            foreach (var key in new[] { AppearanceTokens.FontSize, AppearanceTokens.FocusRadius, AppearanceTokens.FocusRadiusInner, AppearanceTokens.FocusRadiusInset, AppearanceTokens.FocusRadiusInsetInner })
            {
                Assert.IsType<double>(dictionary[key]);
            }

            foreach (var key in new[] { AppearanceTokens.BorderThickness, AppearanceTokens.CardPadding, AppearanceTokens.CardPaddingCompact, AppearanceTokens.CardPaddingFlush })
            {
                Assert.IsType<Thickness>(dictionary[key]);
            }

            Assert.IsType<DropShadowEffect>(dictionary[AppearanceTokens.FlyoutShadow]);
        });
    }

    [Theory]
    [InlineData("Linear")]
    [InlineData("Tui")]
    public void The_solid_styles_have_no_shadow_and_a_one_pixel_border(string style)
    {
        foreach (var mode in new[] { "Dark", "Light" })
        {
            var dictionary = Load(style, mode);

            Assert.Equal(0.0, ((DropShadowEffect)dictionary[AppearanceTokens.FlyoutShadow]).Opacity);
            Assert.Equal(new Thickness(1), (Thickness)dictionary[AppearanceTokens.BorderThickness]);
        }
    }

    // ------------------------------------------------------------------ the brief's values

    public static IEnumerable<object[]> LinearValues()
    {
        // token, dark, light: exactly the brief, unchanged (the contrast tests found nothing to adjust).
        (string Token, string Dark, string Light)[] rows =
        {
            (AppearanceTokens.WindowBackground, "#0F1011", "#FCFCFD"),
            (AppearanceTokens.Surface, "#151619", "#FFFFFF"),
            (AppearanceTokens.Raised, "#1B1C1F", "#F4F5F8"),
            (AppearanceTokens.Border, "#23252A", "#E4E5E9"),
            (AppearanceTokens.TextPrimary, "#F7F8F8", "#1C1D1F"),
            (AppearanceTokens.TextSecondary, "#8A8F98", "#62666D"),
            (AppearanceTokens.TextTertiary, "#62666D", "#8A8F98"),
            (AppearanceTokens.Accent, "#5E6AD2", "#5E6AD2"),
            (AppearanceTokens.AccentHover, "#6E79E0", "#4C57C4"),
            (AppearanceTokens.OnAccent, "#FFFFFF", "#FFFFFF"),
            (AppearanceTokens.ToneCritical, "#EB5757", "#D93F3F"),
            (AppearanceTokens.ToneHigh, "#F2994A", "#C7641A"),
            (AppearanceTokens.ToneMedium, "#4EA7FC", "#2B7FD6"),
            (AppearanceTokens.ToneOk, "#4CB782", "#2E8B5F"),
            (AppearanceTokens.ToneNeutral, "#8A8F98", "#62666D"),
        };

        return rows.Select(r => new object[] { r.Token, r.Dark, r.Light });
    }

    [Theory]
    [MemberData(nameof(LinearValues))]
    public void Linear_holds_the_briefs_colours(string token, string dark, string light)
    {
        Assert.Equal(dark, UiThread.Run(() => Hex((Brush)Load("Linear", "Dark")[token])));
        Assert.Equal(light, UiThread.Run(() => Hex((Brush)Load("Linear", "Light")[token])));
    }

    [Fact]
    public void Linear_holds_the_briefs_type_shape_and_density()
    {
        foreach (var mode in new[] { "Dark", "Light" })
        {
            var d = Load("Linear", mode);

            Assert.Equal("Inter, Segoe UI Variable Text, Segoe UI", ((FontFamily)d[AppearanceTokens.UiFontFamily]).Source);
            Assert.Equal("JetBrains Mono, Cascadia Mono, Consolas", ((FontFamily)d[AppearanceTokens.MonoFontFamily]).Source);
            Assert.Equal(new CornerRadius(6), (CornerRadius)d[AppearanceTokens.ControlRadius]);
            Assert.Equal(new CornerRadius(8), (CornerRadius)d[AppearanceTokens.RadiusL]);

            // "Card padding ~25% tighter than Default": 16 / 12 / 4 become 12 / 9 / 3.
            Assert.Equal(16 * 0.75, ((Thickness)d[AppearanceTokens.CardPadding]).Left);
            Assert.Equal(12 * 0.75, ((Thickness)d[AppearanceTokens.CardPaddingCompact]).Left);
            Assert.Equal(4 * 0.75, ((Thickness)d[AppearanceTokens.CardPaddingFlush]).Left);
        }
    }

    [Fact]
    public void TUI_is_monospace_everywhere_at_13_and_square_everywhere()
    {
        const string mono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

        foreach (var mode in new[] { "Dark", "Light" })
        {
            var d = Load("Tui", mode);

            Assert.Equal(mono, ((FontFamily)d[AppearanceTokens.UiFontFamily]).Source);
            Assert.Equal(mono, ((FontFamily)d[AppearanceTokens.MonoFontFamily]).Source);
            Assert.Equal(13.0, (double)d[AppearanceTokens.FontSize]);

            foreach (var key in new[] { AppearanceTokens.RadiusS, AppearanceTokens.RadiusM, AppearanceTokens.RadiusL, AppearanceTokens.ControlRadius, AppearanceTokens.BarRadius })
            {
                Assert.Equal(new CornerRadius(0), (CornerRadius)d[key]);
            }

            foreach (var key in new[] { AppearanceTokens.FocusRadius, AppearanceTokens.FocusRadiusInner, AppearanceTokens.FocusRadiusInset, AppearanceTokens.FocusRadiusInsetInner })
            {
                Assert.Equal(0.0, (double)d[key]);
            }
        }
    }

    [Fact]
    public void TUI_dark_is_the_CLIs_own_theme_token_for_token()
    {
        // From defenseclaw/tui/theme.py in the installed CLI (ThemeTokens), see Tui.Dark.xaml.
        var d = Load("Tui", "Dark");

        Assert.Equal("#070A12", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.WindowBackground])));   // surface_base
        Assert.Equal("#0D1220", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Surface])));            // surface_panel
        Assert.Equal("#121A2B", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Raised])));             // surface_raised
        Assert.Equal("#18233A", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Subtle])));             // surface_hover
        Assert.Equal("#203251", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Selected])));           // surface_selected
        Assert.Equal("#27324A", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Border])));             // border_muted
        Assert.Equal("#38BDF8", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.FocusRing])));          // border_active
        Assert.Equal("#E6F1FF", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.TextPrimary])));        // text_primary
        Assert.Equal("#9FB2CC", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.TextSecondary])));      // text_secondary
        Assert.Equal("#64748B", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.TextTertiary])));       // text_muted
        Assert.Equal("#22D3EE", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.Accent])));             // accent_cyan
        Assert.Equal("#F87171", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.ToneCritical])));       // accent_red
        Assert.Equal("#FB923C", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.ToneHigh])));           // accent_orange
        Assert.Equal("#60A5FA", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.ToneMedium])));         // accent_blue
        Assert.Equal("#34D399", UiThread.Run(() => Hex((Brush)d[AppearanceTokens.ToneOk])));             // accent_green
    }

    // ------------------------------------------------------------------ tones keep their meaning in every style

    [Theory]
    [MemberData(nameof(Dictionaries))]
    public void Severity_tones_keep_their_hue_family_in_every_style(string style, string mode)
    {
        if (style == "Default")
        {
            // Default's tones are WPF-UI's own; only the Linear and TUI values are ours to check.
            return;
        }

        var d = Load(style, mode);
        (double Hue, double Saturation) Of(string key) => HueSaturation(((SolidColorBrush)d[key]).Color);

        // Critical is red, High orange/amber, Medium blue, Ok green, Neutral grey - by hue, whatever the shade.
        Assert.True(Of(AppearanceTokens.ToneCritical).Hue is < 12 or > 348, "Critical is red");
        Assert.InRange(Of(AppearanceTokens.ToneHigh).Hue, 18, 45);
        Assert.InRange(Of(AppearanceTokens.ToneMedium).Hue, 200, 235);
        Assert.InRange(Of(AppearanceTokens.ToneOk).Hue, 140, 165);
        Assert.True(Of(AppearanceTokens.ToneNeutral).Saturation < 0.35, "Neutral stays grey");
        Assert.NotEqual(Of(AppearanceTokens.ToneCritical).Hue, Of(AppearanceTokens.ToneHigh).Hue);
    }

    private static (double Hue, double Saturation) HueSaturation(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta == 0)
        {
            return (0, 0);
        }

        double hue;
        if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        var lightness = (max + min) / 2;
        var saturation = delta / (1 - Math.Abs((2 * lightness) - 1));
        return (hue, saturation);
    }
}
