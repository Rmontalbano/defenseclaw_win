using System.Windows;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.ConfigEditor;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>
/// WCAG contrast for every style in both modes, computed from the values the app actually loads (the token dictionaries,
/// or for Default WPF-UI's own resolved colours), so a palette tweak that costs legibility fails here rather than in a
/// screenshot. Translucent colours - Default's card fill is 5% white, its secondary text 77% - are composited over what
/// they sit on before the ratio is taken.
/// <para>
/// The bar: primary and secondary text on the window and on the surface at 4.5:1 (WCAG AA for body text); every tone's
/// foreground on the surface at 3:1 (AA for non-text UI and large text); the same text on a zebra row. Linear, TUI and
/// Cisco, whose values this app owns, are held to more: the primary button's label on the accent (4.5), the focus ring against the window and surface
/// (3), each tone on its own tinted fill (3), and the YAML editor's token colours against every surface (4.5).
/// Tertiary text is deliberately not held to a bar - it is hint text, and the Dc styles do not use it for anything that
/// carries meaning (see the note on DcCaption).
/// </para>
/// <para>
/// The icon colours (default and muted icon, the tint palette, both section mappings) and the tone colours the status and
/// severity glyphs are drawn in are held to the non-text bar, 3:1 on the window and on the surface, in
/// <c>Icons\IconTokenTests</c>, which reads the same values the same way and reuses <see cref="Ratio"/>.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppearanceContrastTests
{
    private readonly ITestOutputHelper _output;

    public AppearanceContrastTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string[] Tones = { "Critical", "High", "Medium", "Low", "Ok", "Neutral" };

    // ------------------------------------------------------------------ the contract, per style and mode

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void Text_and_tones_meet_contrast_in_every_style_and_mode(string styleName, string modeName)
    {
        var look = Look.Load(styleName, modeName);
        var failures = new List<string>();

        foreach (var (surfaceName, surface) in new[] { ("window", look.Window), ("surface", look.Surface) })
        {
            Check(failures, $"primary text on {surfaceName}", look.Composite(look.TextPrimary, surface), surface, 4.5);
            Check(failures, $"secondary text on {surfaceName}", look.Composite(look.TextSecondary, surface), surface, 4.5);

            // The alternate row of a striped table, over whatever the rows sit on: the text in it still reads.
            var zebra = look.Composite(look.Zebra, surface);
            Check(failures, $"primary text on a zebra row over the {surfaceName}", look.Composite(look.TextPrimary, zebra), zebra, 4.5);
            Check(failures, $"secondary text on a zebra row over the {surfaceName}", look.Composite(look.TextSecondary, zebra), zebra, 4.5);
        }

        foreach (var tone in Tones)
        {
            // Default's Medium is the Windows accent itself (whatever colour this machine has), so only the tones
            // that are fixed colours are held to the bar for Default; Linear, TUI and Cisco own theirs, Medium included.
            if (styleName == "Default" && tone == "Medium")
            {
                continue;
            }

            Check(failures, $"{tone} tone on surface", look.Composite(look.Tone(tone), look.Surface), look.Surface, 3.0);
            Check(failures, $"{tone} tone on window", look.Composite(look.Tone(tone), look.Window), look.Window, 3.0);
        }

        Assert.True(failures.Count == 0, $"{styleName} {modeName}: " + string.Join("; ", failures));
    }

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void The_destructive_buttons_label_holds_4_5_to_1_on_its_fill_at_rest_and_on_hover(string styleName, string modeName)
    {
        // WPF-UI's own Danger button is a fixed #F44336 under the theme's text colour: 3.68 (Default dark), 3.65 (Linear light),
        // 3.22 (TUI dark). The tokens are chosen per style; the button reads them (Shell\DangerButtonTests holds that half).
        var look = Look.Load(styleName, modeName);
        var failures = new List<string>();

        Check(failures, "on-danger label on the danger fill", look.OnDanger, look.Danger, 4.5);
        Check(failures, "on-danger label on the hover fill", look.OnDanger, look.DangerHover, 4.5);

        Assert.True(failures.Count == 0, $"{styleName} {modeName}: " + string.Join("; ", failures));
    }

    [Theory]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void Linear_TUI_and_Cisco_also_hold_accent_focus_and_tinted_tones(string styleName, string modeName)
    {
        var look = Look.Load(styleName, modeName);
        var failures = new List<string>();

        Check(failures, "on-accent label on accent", look.OnAccent, look.Accent, 4.5);
        Check(failures, "on-accent label on accent (hover)", look.OnAccent, look.AccentHover, 3.0);
        Check(failures, "accent as text (hover step) on surface", look.AccentHover, look.Surface, 4.5);

        foreach (var (name, background) in new[] { ("window", look.Window), ("surface", look.Surface) })
        {
            Check(failures, $"focus ring on {name}", look.FocusRing, background, 3.0);
        }

        foreach (var tone in Tones)
        {
            var tinted = look.Composite(look.ToneSubtle(tone), look.Surface);
            Check(failures, $"{tone} tone on its tinted fill", look.Composite(look.Tone(tone), tinted), tinted, 3.0);
        }

        Assert.True(failures.Count == 0, $"{styleName} {modeName}: " + string.Join("; ", failures));
    }

    [Theory]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void The_YAML_editor_palette_reads_on_every_surface_it_can_sit_on(string styleName, string modeName)
    {
        var look = Look.Load(styleName, modeName);
        var palette = YamlHighlighting.PaletteFor(Enum.Parse<AppearanceStyle>(styleName), modeName == "Dark");
        var failures = new List<string>();

        foreach (var (token, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            foreach (var (surfaceName, surface) in new[] { ("window", look.Window), ("surface", look.Surface), ("inset", look.Inset) })
            {
                Check(failures, $"{token} {hex} on {surfaceName}", color, surface, 4.5);
            }
        }

        Assert.True(failures.Count == 0, $"{styleName} {modeName}: " + string.Join("; ", failures));
    }

    [Fact]
    public void Default_light_YAML_palette_is_still_readable_on_white()
    {
        // The pre-existing promise of the light palette (see YamlHighlighting): every hue at 4.5:1 on white.
        var failures = new List<string>();
        foreach (var (token, hex) in YamlHighlighting.PaletteFor(AppearanceStyle.Default, dark: false))
        {
            Check(failures, $"{token} {hex} on white", (Color)ColorConverter.ConvertFromString(hex), Colors.White, 4.5);
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // ------------------------------------------------------------------ a readable table of everything, for the humans

    [Fact]
    public void The_full_contrast_table_is_written_to_the_test_output()
    {
        foreach (var style in new[] { "Default", "Linear", "Tui", "Cisco" })
        {
            foreach (var mode in new[] { "Dark", "Light" })
            {
                var look = Look.Load(style, mode);
                var row = new List<string>
                {
                    $"text1/window {Ratio(look.Composite(look.TextPrimary, look.Window), look.Window):0.0}",
                    $"text1/surface {Ratio(look.Composite(look.TextPrimary, look.Surface), look.Surface):0.0}",
                    $"text2/window {Ratio(look.Composite(look.TextSecondary, look.Window), look.Window):0.0}",
                    $"text2/surface {Ratio(look.Composite(look.TextSecondary, look.Surface), look.Surface):0.0}",
                };
                row.AddRange(Tones.Select(t => $"{t} {Ratio(look.Composite(look.Tone(t), look.Surface), look.Surface):0.0}"));
                _output.WriteLine($"{style,-8}{mode,-6} " + string.Join(" | ", row));
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void Check(List<string> failures, string what, Color foreground, Color background, double minimum)
    {
        var ratio = Ratio(foreground, background);
        if (ratio < minimum)
        {
            failures.Add($"{what} is {ratio:0.00}:1, needs {minimum:0.0}");
        }
    }

    /// <summary>WCAG 2.x contrast ratio of two opaque colours.</summary>
    internal static double Ratio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>One style in one mode, read the way the app reads it.</summary>
    internal sealed class Look
    {
        private readonly Func<string, Color> _color;

        private Look(Func<string, Color> color) => _color = color;

        public Color Window => Composite(_color(AppearanceTokens.WindowBackground), Colors.Black);

        public Color Surface => Composite(_color(AppearanceTokens.Surface), Window);

        public Color Inset => Composite(_color(AppearanceTokens.Inset), Window);

        public Color Zebra => _color(AppearanceTokens.Zebra);

        public Color Selected => _color(AppearanceTokens.Selected);

        public Color Subtle => _color(AppearanceTokens.Subtle);

        public Color TextPrimary => _color(AppearanceTokens.TextPrimary);

        public Color TextSecondary => _color(AppearanceTokens.TextSecondary);

        public Color Accent => _color(AppearanceTokens.Accent);

        public Color AccentHover => _color(AppearanceTokens.AccentHover);

        public Color OnAccent => _color(AppearanceTokens.OnAccent);

        public Color FocusRing => _color(AppearanceTokens.FocusRing);

        public Color Danger => _color(AppearanceTokens.Danger);

        public Color DangerHover => _color(AppearanceTokens.DangerHover);

        public Color OnDanger => _color(AppearanceTokens.OnDanger);

        public Color Tone(string tone) => _color($"DcTone{tone}Brush");

        public Color ToneSubtle(string tone) => _color($"DcTone{tone}SubtleBrush");

        /// <summary>Composites a possibly translucent colour over an opaque one.</summary>
        public Color Composite(Color top, Color behind)
        {
            var a = top.A / 255.0;
            byte Mix(byte t, byte b) => (byte)Math.Round((t * a) + (b * (1 - a)));
            return Color.FromRgb(Mix(top.R, behind.R), Mix(top.G, behind.G), Mix(top.B, behind.B));
        }

        public static Look Load(string styleName, string modeName)
        {
            var style = Enum.Parse<AppearanceStyle>(styleName);
            var dark = modeName == "Dark";

            if (style == AppearanceStyle.Default)
            {
                // Default's tokens copy WPF-UI's colours as they are when loaded, so WPF-UI has to be in the mode first,
                // with a fixed accent so the result does not depend on this machine's colour.
                return UiThread.Run(() =>
                {
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light, updateAccent: false);
                    Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(Color.FromRgb(0x00, 0x78, 0xD4), dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
                    var tokens = new ResourceDictionary { Source = AppearanceCatalog.TokenSource(style, dark) };

                    // Read now: the colours are values, not references, so the caller can use them after WPF-UI moves on.
                    var cached = AppearanceTokens.Brushes.ToDictionary(key => key, key => ((SolidColorBrush)tokens[key]).Color);
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark, updateAccent: false);
                    return new Look(key => cached[key]);
                });
            }

            return UiThread.Run(() =>
            {
                var tokens = new ResourceDictionary { Source = AppearanceCatalog.TokenSource(style, dark) };
                var cached = AppearanceTokens.Brushes.ToDictionary(key => key, key => ((SolidColorBrush)tokens[key]).Color);
                return new Look(key => cached[key]);
            });
        }
    }
}
