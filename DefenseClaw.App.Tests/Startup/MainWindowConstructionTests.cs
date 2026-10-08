using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// <see cref="DashboardHost"/> retries a factory that threw, so a <see cref="MainWindow"/> constructor that fails after it
/// has subscribed to the monitor, the catalog and the config-reload event leaked the half-built window and its
/// view-model once per retry. The real window is built here (never shown), made to fail at the very end of construction -
/// when every subscription is in place - and the subscriber counts of the app-lifetime objects must come back to where
/// they were. The tray is an uninitialised instance: the constructor only stores it, and a real one would put an icon in
/// the notification area.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class MainWindowConstructionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    [Fact]
    public void A_construction_that_fails_after_subscribing_gives_every_subscription_back_and_closes_the_window()
    {
        using var services = TestServices.Create(_temp);

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);
            var monitorBefore = Subscribers(services.Monitor, "StateChanged");
            var reloadBefore = Subscribers(services, "ConfigReloaded");
            var faultBefore = Subscribers(catalog, "PanelFaulted");
            var runtimeBefore = Subscribers(services.Runtime, "Changed");
            var windowsBefore = Application.Current.Windows.Count;

            MainWindow.ConstructionProbe = () => throw new InvalidOperationException("XAML fault at the very end");
            try
            {
                var failure = Assert.Throws<InvalidOperationException>(() => new MainWindow(services, catalog, UnbuiltTray()));
                Assert.Equal("XAML fault at the very end", failure.Message);
            }
            finally
            {
                MainWindow.ConstructionProbe = null;
            }

            Assert.Equal(monitorBefore, Subscribers(services.Monitor, "StateChanged"));
            Assert.Equal(reloadBefore, Subscribers(services, "ConfigReloaded"));
            Assert.Equal(faultBefore, Subscribers(catalog, "PanelFaulted"));
            Assert.Equal(runtimeBefore, Subscribers(services.Runtime, "Changed"));
            Assert.Equal(windowsBefore, Application.Current.Windows.Count);
        });
    }

    [Fact]
    public void The_probe_is_what_holds_the_subscriptions_up_a_construction_that_succeeds_keeps_them()
    {
        // The other half of the claim: without the failure the same construction really does subscribe, so the
        // "back to baseline" above is the cleanup's doing and not a subscription the window never made.
        using var services = TestServices.Create(_temp);

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);
            var monitorBefore = Subscribers(services.Monitor, "StateChanged");
            var reloadBefore = Subscribers(services, "ConfigReloaded");
            var faultBefore = Subscribers(catalog, "PanelFaulted");
            var runtimeBefore = Subscribers(services.Runtime, "Changed");

            var window = new MainWindow(services, catalog, UnbuiltTray());
            try
            {
                Assert.True(Subscribers(services.Monitor, "StateChanged") > monitorBefore);
                Assert.True(Subscribers(services, "ConfigReloaded") > reloadBefore);
                Assert.True(Subscribers(catalog, "PanelFaulted") > faultBefore);
                Assert.True(Subscribers(services.Runtime, "Changed") > runtimeBefore);
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }
}
