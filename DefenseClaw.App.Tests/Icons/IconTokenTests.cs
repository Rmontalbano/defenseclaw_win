using System.Windows;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App.Tests.Icons;

/// <summary>
/// The icon colour tokens (CUST-196): what each style makes of the default and muted icon, the tint palette and the
/// section mappings, and that every one of them is legible as a shape (WCAG non-text contrast, 3:1) on the window and on a
/// card in every style and mode. The vocabulary itself (all five dictionaries define exactly the same keys) is held by
/// <see cref="AppearanceTokensTests"/>, which reads the same <see cref="AppearanceTokens.All"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class IconTokenTests
{
    private static readonly string[] Tones = { "Critical", "High", "Medium", "Low", "Ok", "Neutral" };

    public static IEnumerable<object[]> Looks()
    {
        foreach (var style in new[] { "Default", "Linear", "Tui", "Cisco" })
        {
            foreach (var mode in new[] { "Dark", "Light" })
            {
                yield return new object[] { style, mode };
            }
        }
    }

    // ------------------------------------------------------------------ the tokens

    [Fact]
    public void The_tint_palette_names_ten_colours_and_the_sections_six_mappings()
    {
        Assert.Equal(
            new[] { "Indigo", "Blue", "Violet", "Teal", "Green", "Amber", "Orange", "Red", "Pink", "Gray" },
            Palette);
        Assert.Equal(new[] { "Overview", "Observe", "Govern", "Discover", "Setup", "Updates" }, DcSections.All);

        foreach (var tint in Palette)
        {
            Assert.Contains($"DcTint{tint}Brush", AppearanceTokens.All);
        }

        foreach (var section in DcSections.All)
        {
            Assert.Contains($"DcSection{section}Brush", AppearanceTokens.All);
            Assert.Contains($"DcNavIcon{section}Brush", AppearanceTokens.All);
        }
    }

    private static readonly string[] Palette = { "Indigo", "Blue", "Violet", "Teal", "Green", "Amber", "Orange", "Red", "Pink", "Gray" };

    [Theory]
    [InlineData("Default", "Dark", "Dot")]
    [InlineData("Linear", "Dark", "Linear")]
    [InlineData("Linear", "Light", "Linear")]
    [InlineData("Tui", "Dark", "Terminal")]
    [InlineData("Tui", "Light", "Terminal")]
    [InlineData("Cisco", "Dark", "Dot")]
    [InlineData("Cisco", "Light", "Dot")]
    public void Each_style_names_the_glyph_shapes_it_draws(string style, string mode, string family)
    {
        var tokens = IconLook.Dictionary(style, mode);

        Assert.Equal(family, UiThread.Run(() => (string)tokens[AppearanceTokens.GlyphFamily]));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Linear_tints_every_section_with_its_own_colour_and_wears_it_in_the_sidebar(string mode)
    {
        var look = IconLook.Load("Linear", mode);

        var sections = DcSections.All.Select(s => look.Of($"DcSection{s}Brush")).ToArray();
        Assert.Equal(sections.Length, sections.Distinct().Count());

        foreach (var section in DcSections.All)
        {
            // Sidebar and page headers wear the same tint as the card headers: Linear is the coloured style.
            Assert.Equal(look.Of($"DcSection{section}Brush"), look.Of($"DcNavIcon{section}Brush"));
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Linear_tints_are_in_the_family_of_its_own_tones(string mode)
    {
        // Blue, green, orange and red are the tone colours themselves, so a tinted icon and a status badge agree.
        var look = IconLook.Load("Linear", mode);

        Assert.Equal(look.Of(AppearanceTokens.ToneMedium), look.Of(AppearanceTokens.TintBlue));
        Assert.Equal(look.Of(AppearanceTokens.ToneOk), look.Of(AppearanceTokens.TintGreen));
        Assert.Equal(look.Of(AppearanceTokens.ToneHigh), look.Of(AppearanceTokens.TintOrange));
        Assert.Equal(look.Of(AppearanceTokens.ToneCritical), look.Of(AppearanceTokens.TintRed));
    }

    [Fact]
    public void Default_keeps_the_sidebar_in_the_secondary_text_colour_and_tints_only_card_headers()
    {
        var look = IconLook.Load("Default", "Dark");

        foreach (var section in DcSections.All)
        {
            Assert.Equal(look.Of(AppearanceTokens.Icon), look.Of($"DcNavIcon{section}Brush"));
            Assert.NotEqual(look.Of(AppearanceTokens.Icon), look.Of($"DcSection{section}Brush"));
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void TUI_draws_icons_in_the_text_or_the_accent_colour_only(string mode)
    {
        var look = IconLook.Load("Tui", mode);
        var accent = look.Of(AppearanceTokens.Accent);

        foreach (var section in DcSections.All)
        {
            Assert.Equal(accent, look.Of($"DcSection{section}Brush"));
            Assert.Equal(look.Of(AppearanceTokens.Icon), look.Of($"DcNavIcon{section}Brush"));
        }

        foreach (var tint in Palette.Where(t => t != "Gray"))
        {
            Assert.Equal(accent, look.Of($"DcTint{tint}Brush"));
        }

        Assert.Equal(look.Of(AppearanceTokens.Icon), look.Of(AppearanceTokens.TintGray));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Cisco_draws_card_headers_in_Cisco_blue_the_sidebar_in_the_accent_and_statuses_in_their_tone(string mode)
    {
        var look = IconLook.Load("Cisco", mode);

        // The Mac: every card-header icon one blue, every sidebar icon the control blue (the accent).
        var brand = look.Of(AppearanceTokens.SectionOverview);
        foreach (var section in DcSections.All)
        {
            Assert.Equal(brand, look.Of($"DcSection{section}Brush"));
            Assert.Equal(look.Of(AppearanceTokens.Accent), look.Of($"DcNavIcon{section}Brush"));
        }

        // Cisco blue is a blue (hue 190-215) in both modes.
        var hue = Hue(brand);
        Assert.InRange(hue, 190, 215);

        // The tints that mean a status are the tone colours themselves, so an icon and a badge agree; the rest are Cisco blue.
        Assert.Equal(look.Of(AppearanceTokens.ToneOk), look.Of(AppearanceTokens.TintGreen));
        Assert.Equal(look.Of(AppearanceTokens.ToneMedium), look.Of(AppearanceTokens.TintAmber));
        Assert.Equal(look.Of(AppearanceTokens.ToneHigh), look.Of(AppearanceTokens.TintOrange));
        Assert.Equal(look.Of(AppearanceTokens.ToneCritical), look.Of(AppearanceTokens.TintRed));
        foreach (var tint in new[] { "Indigo", "Blue", "Violet", "Teal", "Pink" })
        {
            Assert.Equal(brand, look.Of($"DcTint{tint}Brush"));
        }

        Assert.Equal(look.Of(AppearanceTokens.Icon), look.Of(AppearanceTokens.TintGray));
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    // ------------------------------------------------------------------ non-text contrast

    [Theory]
    [MemberData(nameof(Looks))]
    public void Every_icon_colour_and_tone_reads_at_3_to_1_on_the_window_and_on_a_card(string style, string mode)
    {
        var look = IconLook.Load(style, mode);
        var failures = new List<string>();

        foreach (var (surfaceName, surface) in new[] { ("window", look.Window), ("surface", look.Surface) })
        {
            foreach (var token in AppearanceTokens.IconColours)
            {
                Check(failures, token, look.Composite(look.Of(token), surface), surface, surfaceName);
            }

            // The colours the status and severity glyphs are drawn in. Default's Medium is the Windows accent, whatever
            // colour this machine has, so only the fixed ones are held for Default (the same rule the tone test uses).
            foreach (var tone in Tones.Where(t => !(style == "Default" && t == "Medium")))
            {
                var token = $"DcTone{tone}Brush";
                Check(failures, token, look.Composite(look.Of(token), surface), surface, surfaceName);
            }
        }

        Assert.True(failures.Count == 0, $"{style} {mode}: " + string.Join("; ", failures));
    }

    private static void Check(List<string> failures, string token, Color foreground, Color background, string surface)
    {
        var ratio = AppearanceContrastTests.Ratio(foreground, background);
        if (ratio < 3.0)
        {
            failures.Add($"{token} on {surface} is {ratio:0.00}:1, needs 3.0");
        }
    }

    /// <summary>One style in one mode, read the way <see cref="AppearanceContrastTests"/> reads it.</summary>
    private sealed class IconLook
    {
        private readonly IReadOnlyDictionary<string, Color> _colors;

        private IconLook(IReadOnlyDictionary<string, Color> colors) => _colors = colors;

        public Color Window => Composite(Of(AppearanceTokens.WindowBackground), Colors.Black);

        public Color Surface => Composite(Of(AppearanceTokens.Surface), Window);

        public Color Of(string token) => _colors[token];

        public Color Composite(Color top, Color behind)
        {
            var a = top.A / 255.0;
            byte Mix(byte t, byte b) => (byte)Math.Round((t * a) + (b * (1 - a)));
            return System.Windows.Media.Color.FromRgb(Mix(top.R, behind.R), Mix(top.G, behind.G), Mix(top.B, behind.B));
        }

        public static ResourceDictionary Dictionary(string styleName, string modeName) => UiThread.Run(() =>
            new ResourceDictionary { Source = AppearanceCatalog.TokenSource(Enum.Parse<AppearanceStyle>(styleName), modeName == "Dark") });

        public static IconLook Load(string styleName, string modeName)
        {
            var style = Enum.Parse<AppearanceStyle>(styleName);
            var dark = modeName == "Dark";

            return UiThread.Run(() =>
            {
                if (style == AppearanceStyle.Default)
                {
                    // Default's tokens copy WPF-UI's colours as they are when loaded: put WPF-UI in the mode first, with a fixed
                    // accent so the result does not depend on this machine's colour.
                    var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
                    ApplicationThemeManager.Apply(theme, updateAccent: false);
                    ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4), theme);
                }

                var tokens = new ResourceDictionary { Source = AppearanceCatalog.TokenSource(style, dark) };
                var cached = AppearanceTokens.Brushes.ToDictionary(key => key, key => ((SolidColorBrush)tokens[key]).Color);

                if (style == AppearanceStyle.Default)
                {
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: false);
                }

                return new IconLook(cached);
            });
        }
    }
}
