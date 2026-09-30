using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using Wpf.Ui.Controls;
using UiButton = Wpf.Ui.Controls.Button;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The destructive button reads the style's tokens (D4-6). WPF-UI paints <c>Appearance="Danger"</c> with a fixed #F44336 and no
/// label colour of its own, which left the label at 3.2-3.7:1 in most looks; a style trigger in Themes\DefenseClaw.xaml now
/// gives it <c>DcDanger</c> / <c>DcDangerHover</c> / <c>DcOnDanger</c>, whose contrast is held in AppearanceContrastTests. This
/// is the other half: that a real button actually wears them, wherever it sits, in every look.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DangerButtonTests
{
    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    public void A_Danger_button_wears_the_danger_tokens_in_every_look(string style, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        UiThread.Run(() =>
        {
            var plain = new UiButton { Content = "Run", Appearance = ControlAppearance.Danger };
            var inActionBar = new UiButton { Content = "Delete", Appearance = ControlAppearance.Danger };
            var inToolbar = new UiButton { Content = "Remove", Appearance = ControlAppearance.Danger };
            var primary = new UiButton { Content = "Save", Appearance = ControlAppearance.Primary };

            var actionBar = new StackPanel { Style = (Style)Application.Current.FindResource("DcActionBar"), Children = { inActionBar } };
            var toolbar = new WrapPanel { Style = (Style)Application.Current.FindResource("DcToolbarPanel"), Children = { inToolbar } };
            using var host = new OffscreenHost(new StackPanel { Children = { plain, actionBar, toolbar, primary } }, 400, 300);

            foreach (var button in new[] { plain, inActionBar, inToolbar })
            {
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.OnDanger), ColorOf(button.Foreground));
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.Danger), ColorOf(button.Background));
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.DangerHover), ColorOf(button.MouseOverBackground));
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.DangerHover), ColorOf(button.PressedBackground));
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.OnDanger), ColorOf(button.PressedForeground));
            }

            // Only a Danger button: a primary one keeps its accent look.
            Assert.NotEqual(AppearanceFixture.ColorOf(AppearanceTokens.Danger), ColorOf(primary.Background));
        });
    }

    [Fact]
    public void A_Danger_button_follows_a_live_style_switch_and_a_disabled_one_keeps_WPF_UIs_disabled_label()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            // A real window: the application refreshes dynamic resources in its windows, not in a bare host.
            var danger = new UiButton { Content = "Delete", Appearance = ControlAppearance.Danger };
            var disabled = new UiButton { Content = "Delete", Appearance = ControlAppearance.Danger, IsEnabled = false };
            var window = new Window { Content = new StackPanel { Children = { danger, disabled } } };
            try
            {
                var linear = ColorOf(danger.Background);
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.Danger), linear);

                fixture.Service.SetStyle(AppearanceStyle.Tui);
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.Danger), ColorOf(danger.Background));
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.OnDanger), ColorOf(danger.Foreground));
                Assert.NotEqual(linear, ColorOf(danger.Background));

                // Disabled: the trigger stands aside and WPF-UI's own disabled treatment (label included) applies.
                Assert.NotEqual(AppearanceFixture.ColorOf(AppearanceTokens.OnDanger), ColorOf(disabled.Foreground));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Color ColorOf(Brush brush) =>
        brush is SolidColorBrush solid ? solid.Color : throw new InvalidOperationException($"Expected a solid brush, got {brush?.GetType().Name ?? "null"}.");
}
