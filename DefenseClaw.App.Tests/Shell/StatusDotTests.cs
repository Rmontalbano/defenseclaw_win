using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The status dot takes its shape from the style (D4-10). <c>DcToneDot</c> / <c>DcToneDotLarge</c> were Ellipses, round in a look
/// where everything else is square; they are Borders now, with <c>DcDotRadius</c> for a corner radius: a circle in Default and
/// Linear, a square in TUI.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StatusDotTests
{
    [Theory]
    [InlineData("DcToneDot", 10)]
    [InlineData("DcToneDotLarge", 12)]
    public void A_dot_is_a_circle_under_Default_and_Linear_and_a_square_under_TUI(string styleKey, int size)
    {
        foreach (var (style, mode, round) in new[]
                 {
                     (AppearanceStyle.Default, AppearanceMode.Dark, true),
                     (AppearanceStyle.Linear, AppearanceMode.Light, true),
                     (AppearanceStyle.Tui, AppearanceMode.Dark, false),
                     (AppearanceStyle.Tui, AppearanceMode.Light, false),
                 })
        {
            using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

            UiThread.Run(() =>
            {
                var dot = new Border { Style = (Style)Application.Current.FindResource(styleKey), Tag = "Ok" };
                using var host = new OffscreenHost(new Grid { Children = { dot } }, 40, 40);

                Assert.Equal(size, dot.ActualWidth);
                Assert.Equal((CornerRadius)Application.Current.FindResource(AppearanceTokens.DotRadius), dot.CornerRadius);

                // The corner pixel of the dot: painted for a square, clear for a circle. The dot is centred in a 40 x 40 host.
                var pixels = Picture(host.Content, 40, 40);
                var left = (40 - size) / 2;
                var top = (40 - size) / 2;
                var cornerAlpha = pixels[(((top * 40) + left) * 4) + 3];
                var centreAlpha = pixels[((((top + (size / 2)) * 40) + left + (size / 2)) * 4) + 3];

                Assert.Equal(0xFF, centreAlpha);
                Assert.Equal(round ? 0 : 0xFF, cornerAlpha);
            });
        }
    }

    [Fact]
    public void The_dot_keeps_its_tone_colour()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var critical = new Border { Style = (Style)Application.Current.FindResource("DcToneDot"), Tag = "Critical" };
            var neutral = new Border { Style = (Style)Application.Current.FindResource("DcToneDot") };
            using var host = new OffscreenHost(new StackPanel { Children = { critical, neutral } }, 40, 40);

            Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.ToneCritical), ((SolidColorBrush)critical.Background).Color);
            Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.ToneNeutral), ((SolidColorBrush)neutral.Background).Color);
        });
    }

    private static byte[] Picture(FrameworkElement root, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
