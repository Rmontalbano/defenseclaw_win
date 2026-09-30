using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>
/// The Cisco style (CUST-207): the fourth look, built to match DefenseClaw for Mac. Its values are held here against the two
/// references the brief names - the Mac source (<c>DesignSystem\CiscoTheme.swift</c>) for dark, and the live Mac capture for light -
/// and the things the brief says must not move (Default, Linear and TUI keep their metrics) are pinned next to them. Contrast
/// (text, tones, the tinted fills, the destructive button, icons) is held for every look by <see cref="AppearanceContrastTests"/>
/// and <c>Icons\IconTokenTests</c>, which list Cisco in both modes.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppearanceCiscoTests
{
    private static ResourceDictionary Load(string style, string mode) => UiThread.Run(() =>
        new ResourceDictionary { Source = AppearanceCatalog.TokenSource(Enum.Parse<AppearanceStyle>(style), mode == "Dark") });

    private static string Hex(ResourceDictionary d, string token) => UiThread.Run(() =>
    {
        var c = ((SolidColorBrush)d[token]).Color;
        return c.A == 0xFF ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    });

    // ------------------------------------------------------------------ the anchors

    [Fact]
    public void Cisco_is_the_fourth_style_and_has_its_own_name_and_dictionaries()
    {
        Assert.Equal(new[] { AppearanceStyle.Default, AppearanceStyle.Linear, AppearanceStyle.Tui, AppearanceStyle.Cisco }, AppearanceCatalog.Styles);
        Assert.Equal("Cisco", AppearanceCatalog.Name(AppearanceStyle.Cisco));
        Assert.False(string.IsNullOrWhiteSpace(AppearanceCatalog.Description(AppearanceStyle.Cisco)));
        Assert.EndsWith("/Themes/Styles/Cisco.Dark.xaml", AppearanceCatalog.TokenSource(AppearanceStyle.Cisco, dark: true).AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/Themes/Styles/Cisco.Light.xaml", AppearanceCatalog.TokenSource(AppearanceStyle.Cisco, dark: false).AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Dark_holds_the_source_values_the_brief_gave_and_no_dark_capture_moves_them()
    {
        var d = Load("Cisco", "Dark");

        // DesignSystem\CiscoTheme.swift:41-82 and OverviewView.swift:402-406 - there is no dark capture to calibrate against.
        Assert.Equal("#1C1C1E", Hex(d, AppearanceTokens.WindowBackground));
        Assert.Equal("#111B2C", Hex(d, AppearanceTokens.Surface));
        Assert.Equal("#16233A", Hex(d, AppearanceTokens.Raised));
        Assert.Equal("#04AEED", Hex(d, AppearanceTokens.Accent));
        Assert.Equal("#2A2A2C", Hex(d, AppearanceTokens.Zebra));

        // The Mac's severity ramp: macOS's dark system colours.
        Assert.Equal("#FF453A", Hex(d, AppearanceTokens.ToneCritical));
        Assert.Equal("#FF9F0A", Hex(d, AppearanceTokens.ToneHigh));
        Assert.Equal("#FFD60A", Hex(d, AppearanceTokens.ToneMedium));
        Assert.Equal("#64D2FF", Hex(d, AppearanceTokens.ToneLow));
        Assert.Equal("#32D74B", Hex(d, AppearanceTokens.ToneOk));
    }

    [Fact]
    public void Light_holds_the_source_values_the_capture_confirmed_and_the_capture_values_where_it_differed()
    {
        var d = Load("Cisco", "Light");

        // Source and capture agree (sampled from 10-overview-top-light.png): the window, a card, a stat tile.
        Assert.Equal("#FFFFFF", Hex(d, AppearanceTokens.WindowBackground));
        Assert.Equal("#F5F7FA", Hex(d, AppearanceTokens.Surface));
        Assert.Equal("#FFFFFF", Hex(d, AppearanceTokens.Raised));

        // The zebra stays the source's #EFEFEF (the Overview's Configuration rows) although the capture's tables are #F4F5F5: that
        // stripe cannot be seen on a card (#F5F7FA), where this app hosts its grids.
        Assert.Equal("#EFEFEF", Hex(d, AppearanceTokens.Zebra));

        // Calibrated from the capture: a stat tile's hairline (Cisco blue at 11%), a button's grey fill.
        Assert.Equal("#DBEDF6", Hex(d, AppearanceTokens.Border));
        Assert.Equal("#E3E4E7", Hex(d, AppearanceTokens.Subtle));

        // The control accent is the capture's blue (the selected sidebar row and the segmented control, #0070F5), not the source's
        // #049FD9, which holds only 3.0:1 under a white label; Cisco blue stays on the card-header icons, darkened to 3:1.
        Assert.Equal("#0070F5", Hex(d, AppearanceTokens.Accent));
        Assert.Equal("#0291CC", Hex(d, AppearanceTokens.SectionOverview));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Cisco_holds_the_Mac_metrics_through_the_existing_radius_and_padding_tokens(string mode)
    {
        var d = Load("Cisco", mode);

        Assert.Equal(new CornerRadius(12), UiThread.Run(() => (CornerRadius)d[AppearanceTokens.RadiusL]));   // a card
        Assert.Equal(new CornerRadius(10), UiThread.Run(() => (CornerRadius)d[AppearanceTokens.RadiusM]));   // a stat tile
        Assert.Equal(new Thickness(14), UiThread.Run(() => (Thickness)d[AppearanceTokens.CardPadding]));
        Assert.Equal(13.0, UiThread.Run(() => (double)d[AppearanceTokens.FontSize]));
        Assert.Equal("Dot", UiThread.Run(() => (string)d[AppearanceTokens.GlyphFamily]));
    }

    [Fact]
    public void Default_Linear_and_TUI_keep_the_metrics_they_had()
    {
        foreach (var (style, mode, radiusL, padding) in new[]
        {
            ("Default", "Dark", 8.0, 16.0),
            ("Linear", "Dark", 8.0, 12.0),
            ("Linear", "Light", 8.0, 12.0),
            ("Tui", "Dark", 0.0, 12.0),
            ("Tui", "Light", 0.0, 12.0),
        })
        {
            var d = Load(style, mode);
            Assert.Equal(new CornerRadius(radiusL), UiThread.Run(() => (CornerRadius)d[AppearanceTokens.RadiusL]));
            Assert.Equal(new Thickness(padding), UiThread.Run(() => (Thickness)d[AppearanceTokens.CardPadding]));
        }
    }

    // ------------------------------------------------------------------ live

    [Theory]
    [InlineData("Light", "#FFFFFF", "#F5F7FA")]
    [InlineData("Dark", "#1C1C1E", "#111B2C")]
    public void Choosing_Cisco_re_skins_a_live_card_and_reaches_WPF_UI_solid(string modeName, string window, string surface)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var card = new Border { Style = (Style)Application.Current.FindResource("DcCard") };
            var badge = new Border { Style = (Style)Application.Current.FindResource("DcBadge"), Tag = "Low" };
            var medium = new Border { Style = (Style)Application.Current.FindResource("DcBadge"), Tag = "Medium" };
            var host = new StackPanel { Children = { card, badge, medium } };
            var window0 = new Window { Content = host };
            try
            {
                fixture.Service.SetMode(Enum.Parse<AppearanceMode>(modeName));
                fixture.Service.SetStyle(AppearanceStyle.Cisco);

                Assert.Equal(new CornerRadius(12), card.CornerRadius);
                Assert.Equal(new Thickness(14), card.Padding);
                Assert.Equal(Hex(surface), ((SolidColorBrush)card.Background).Color);

                // Solid: WPF-UI's own window and card brushes are the style's, not Mica's or its grey.
                Assert.Equal(Hex(window), AppearanceFixture.ColorOf("ApplicationBackgroundBrush"));
                Assert.Equal(Hex(window), AppearanceFixture.ColorOf("DcWindowBackgroundBrush"));
                Assert.Equal(Hex(surface), AppearanceFixture.ColorOf("CardBackgroundFillColorDefaultBrush"));
                Assert.Equal(AppearanceStyle.Cisco, fixture.Service.EffectiveStyle);

                // Low and Medium are two different tones here (cyan and yellow), each a badge of its own colours.
                var low = ((SolidColorBrush)(Brush)badge.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color;
                var yellow = ((SolidColorBrush)(Brush)medium.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color;
                Assert.Equal(AppearanceFixture.ColorOf("DcToneLowBrush"), low);
                Assert.Equal(AppearanceFixture.ColorOf("DcToneMediumBrush"), yellow);
                Assert.NotEqual(low, yellow);
            }
            finally
            {
                window0.Close();
            }
        });
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    [Fact]
    public void Information_takes_the_accent_in_Cisco_because_Medium_is_yellow_there_and_Linears_stays_blue()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var accent = AppearanceFixture.ColorOf("DcAccentBrush");
            Assert.Equal(accent, AppearanceFixture.ColorOf("SystemFillColorAttentionBrush"));
            Assert.Equal(accent, AppearanceFixture.ColorOf("InfoBarInformationalSeverityIconBackground"));
            Assert.Equal(accent, AppearanceFixture.ColorOf("InfoBadgeAttentionSeverityBackgroundBrush"));

            // The tinted fill is the accent, translucent - not Medium's yellow.
            var fill = AppearanceFixture.ColorOf("InfoBarInformationalSeverityBackgroundBrush");
            Assert.Equal((accent.R, accent.G, accent.B), (fill.R, fill.G, fill.B));
            Assert.InRange(fill.A, 0x10, 0x40);

            // ...while WPF-UI's warning (caution) still is High's orange.
            Assert.Equal(AppearanceFixture.ColorOf("DcToneHighBrush"), AppearanceFixture.ColorOf("SystemFillColorCautionBrush"));

            fixture.Service.SetStyle(AppearanceStyle.Linear);
            Assert.Equal(AppearanceFixture.ColorOf("DcToneMediumBrush"), AppearanceFixture.ColorOf("SystemFillColorAttentionBrush"));
            Assert.Equal(AppearanceFixture.ColorOf("DcToneMediumBrush"), AppearanceFixture.ColorOf("InfoBarInformationalSeverityIconBackground"));
        });
    }

    [Theory]
    [InlineData("Light", "#FFFFFF", "#F5F7FA", "#0070F5", "#1D1D1F")]
    [InlineData("Dark", "#1C1C1E", "#111B2C", "#04AEED", "#F5F5F7")]
    public void The_swatch_shows_the_window_card_accent_and_text_of_the_mode_on_screen(string modeName, string window, string surface, string accent, string text)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(modeName)));

        var swatch = UiThread.Run(() => fixture.Service.Swatch(AppearanceStyle.Cisco));

        Assert.Equal((Hex(window), Hex(surface), Hex(accent), Hex(text)), swatch);
    }

    // ------------------------------------------------------------------ Low and the zebra are consumed

    [Theory]
    [InlineData("DcValue", "Foreground", "DcToneLowBrush")]
    [InlineData("DcBadge", "Background", "DcToneLowSubtleBrush")]
    [InlineData("DcBadge", "TextElement.Foreground", "DcToneLowBrush")]
    [InlineData("DcToneText", "Foreground", "DcToneLowBrush")]
    [InlineData("DcToneDot", "Background", "DcToneLowBrush")]
    [InlineData("DcToneBar", "Background", "DcToneLowBrush")]
    [InlineData("DcToneTile", "Background", "DcToneLowSubtleBrush")]
    public void Every_tone_style_paints_a_Low_tag_with_the_Low_tone(string styleKey, string property, string brushKey)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var document = XDocument.Load(ThemePath());
        var style = document.Descendants(presentation + "Style").Single(s => (string?)s.Attribute(xaml + "Key") == styleKey);
        var trigger = style.Descendants(presentation + "Trigger").Single(t => (string?)t.Attribute("Property") == "Tag" && (string?)t.Attribute("Value") == "Low");
        var setter = trigger.Elements(presentation + "Setter").Single(s => (string?)s.Attribute("Property") == property);

        Assert.Equal($"{{DynamicResource {brushKey}}}", (string?)setter.Attribute("Value"));
    }

    private static string ThemePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "Themes", "DefenseClaw.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Themes\\DefenseClaw.xaml was not found above the test output directory.");
    }

    // ------------------------------------------------------------------ persistence

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    public void The_choice_is_saved_as_cisco_and_comes_back(string mode)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "DefenseClaw.App", "settings.json");
        var settings = new AppearanceSettings(AppearanceStyle.Cisco, Enum.Parse<AppearanceMode>(mode));

        new FileAppearanceSettingsStore(path).Save(settings);

        Assert.Equal("cisco", (string)JsonNode.Parse(File.ReadAllText(path))!["appearance"]!["style"]!);
        Assert.Equal(settings, new FileAppearanceSettingsStore(path).Load());
    }
}
