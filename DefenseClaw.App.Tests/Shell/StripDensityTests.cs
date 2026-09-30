using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The status strip's density thresholds follow the UI font (D4-11). They were measured in Segoe UI (the sentence needs ~220 DIPs,
/// the connector chip steps aside below 900), and TUI's monospace makes the same sentence ~270: at the 940 DIP minimum it was
/// cut to an ellipsis with 151 DIPs to draw in. The thresholds now rise by what the sentence gains in the font on screen.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StripDensityTests
{
    private const string Sentence = "Gateway responding on 127.0.0.1:18970.";

    [Fact]
    public void Under_Default_the_thresholds_are_the_ones_they_always_were()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        var extra = UiThread.Run(() => MainWindow.DetailSentenceExtra(SystemFonts.MessageFontFamily));

        Assert.Equal(0, extra);
        Assert.False(MainWindow.ShowsVersionChip(1039, extra));
        Assert.True(MainWindow.ShowsVersionChip(1040, extra));
        Assert.False(MainWindow.ShowsConnectorChip(899, extra));
        Assert.True(MainWindow.ShowsConnectorChip(900, extra));
    }

    [Theory]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    public void Under_TUI_the_extra_is_what_the_monospace_sentence_really_gains_and_the_connector_chip_gives_way_at_the_minimum(string style, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        var (extra, mono, system) = UiThread.Run(() =>
        {
            var font = (FontFamily)Application.Current.FindResource(AppearanceTokens.UiFontFamily);
            return (MainWindow.DetailSentenceExtra(font), Measure(font), Measure(SystemFonts.MessageFontFamily));
        });

        // Measured independently, with FormattedText at the caption size (12), not the same code path.
        Assert.InRange(extra, (mono - system) - 1.5, (mono - system) + 1.5);
        Assert.True(extra > 20, $"TUI's sentence should be clearly wider than Segoe UI's (extra {extra:0.0})");

        // The window's minimum is 940: the connector chip (and the version chip) step aside there so the sentence has room.
        Assert.False(MainWindow.ShowsConnectorChip(940, extra));
        Assert.False(MainWindow.ShowsVersionChip(940, extra));

        // Nothing is lost for good: a wide enough window shows both.
        Assert.True(MainWindow.ShowsConnectorChip(1200, extra));
        Assert.True(MainWindow.ShowsVersionChip(1200, extra));
    }

    [Fact]
    public void A_font_narrower_than_the_system_font_never_lowers_the_thresholds_below_the_measured_ones()
    {
        var extra = UiThread.Run(() => MainWindow.DetailSentenceExtra(new FontFamily("Arial Narrow, Segoe UI")));

        Assert.True(extra >= 0);
    }

    private static double Measure(FontFamily family) =>
        new FormattedText(
            Sentence,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            12,
            Brushes.Black,
            1.0).WidthIncludingTrailingWhitespace;
}
