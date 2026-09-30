using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>
/// The style's UI font reaches panel content (D4-3). A window's font is inherited by everything in it, but WPF-UI's navigation
/// frame hosts a page in a content presenter that does not pass it on, so Linear's and TUI's fonts used to reach the chrome
/// and the separate windows and never a panel. <c>PanelCatalog</c> now gives each panel view a live reference to the token.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PanelFontTests
{
    private const string TuiMono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";
    private const string LinearUi = "Inter, Segoe UI Variable Text, Segoe UI";

    [Fact]
    public void A_panels_text_wears_the_TUI_font_and_gives_it_back_when_the_style_returns_to_Default()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark));

        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 940, 620);
                _ = shell.Show<AlertsPanel>();

                var title = PanelTitle(shell.Page!);
                Assert.Equal(TuiMono, title.FontFamily.Source);

                fixture.Service.SetStyle(AppearanceStyle.Linear);
                Assert.Equal(LinearUi, title.FontFamily.Source);

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(SystemFont(), title.FontFamily.Source);

                // ... and it is live again on the way back, with no reload of the panel.
                fixture.Service.SetStyle(AppearanceStyle.Tui);
                Assert.Equal(TuiMono, title.FontFamily.Source);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    [Fact]
    public void A_panel_built_while_a_style_is_on_screen_already_wears_its_font_and_so_do_the_ones_cached_before_a_switch()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 940, 620);
                _ = shell.Show<AlertsPanel>();
                Assert.Equal(SystemFont(), PanelTitle(shell.Page!).FontFamily.Source);

                // A panel the operator has not opened yet, and one already built: both follow a switch made in between.
                fixture.Service.SetStyle(AppearanceStyle.Tui);
                var logs = shell.Show<LogsPanel>();
                Assert.Equal(TuiMono, logs.FontFamily.Source);
                var alerts = shell.Show<AlertsPanel>();
                Assert.Equal(TuiMono, PanelTitle(alerts).FontFamily.Source);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    [Fact]
    public void Under_a_high_contrast_theme_the_panels_keep_the_system_font_whatever_style_was_chosen()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        using var fixture = new AppearanceFixture(
            new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark),
            configureSystem: system => system.IsHighContrast = true);

        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 940, 620);
                _ = shell.Show<AlertsPanel>();

                Assert.Equal(AppearanceStyle.Default, fixture.Service.EffectiveStyle);
                Assert.Equal(SystemFont(), PanelTitle(shell.Page!).FontFamily.Source);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    private static TextBlock PanelTitle(FrameworkElement page) =>
        VisualTree.Descendants<TextBlock>(page).First(t => t.Text == "Alerts");

    /// <summary>What text resolves to with nothing setting a font: the system message font.</summary>
    private static string SystemFont() => new TextBlock().FontFamily.Source;
}
