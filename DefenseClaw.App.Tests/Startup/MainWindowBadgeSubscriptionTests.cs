using System.Runtime.CompilerServices;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The sidebar badges listen to the alert counts through the window's view-model, so a window that is abandoned half-built, or
/// closed for good, hands that subscription back: the counts service is idle again (no read on the monitor's tick for nobody), and
/// a window built afterwards starts from the current count rather than from a subscription the old one leaked.
/// Real windows, never shown; the tray is an uninitialised instance, as in <see cref="MainWindowConstructionTests"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class MainWindowBadgeSubscriptionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    [Fact]
    public void A_construction_that_fails_after_the_badge_subscription_was_made_gives_it_back()
    {
        using var services = TestServices.Create(_temp);

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);
            Assert.False(services.AlertCounts.IsRunning);

            MainWindow.ConstructionProbe = () => throw new InvalidOperationException("XAML fault at the very end");
            try
            {
                _ = Assert.Throws<InvalidOperationException>(() => new MainWindow(services, catalog, UnbuiltTray()));
            }
            finally
            {
                MainWindow.ConstructionProbe = null;
            }

            Assert.False(services.AlertCounts.IsRunning);
        });
    }

    [Fact]
    public void A_window_holds_the_subscription_while_it_lives_and_gives_it_back_when_it_really_closes()
    {
        using var services = TestServices.Create(_temp);

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);

            var window = new MainWindow(services, catalog, UnbuiltTray());
            Assert.True(services.AlertCounts.IsRunning);

            window.AllowClose();
            window.Close();
            Assert.False(services.AlertCounts.IsRunning);

            // Rebuilt, as the dashboard can be: the same again, nothing left over from the first.
            var again = new MainWindow(services, catalog, UnbuiltTray());
            Assert.True(services.AlertCounts.IsRunning);
            again.AllowClose();
            again.Close();
            Assert.False(services.AlertCounts.IsRunning);
            Assert.Empty(Application.Current.Windows.OfType<MainWindow>());
        });
    }
}
