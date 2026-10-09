using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The strip in the real <c>MainWindow</c> (CUST-273): hosted over the shell's own view-model, and given back - every subscription it took - when the
/// window really closes or fails to build. The window is built, never shown (its constructor needs a tray icon, which these tests do not make:
/// <see cref="UnbuiltTray"/>), like every other test of it. Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StatusStripWindowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public StatusStripWindowTests()
    {
        _services = TestServices.Create(_temp, "gateway:\n  api_port: 18970\n");
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private (int Facts, int Scope, int Settings, int Started, int Completed, int Config) Counts() => (
        Subscribers(_services.StatusFacts, "Changed"),
        Subscribers(_services.ConnectorScope, "Changed"),
        Subscribers(_services.Settings, "Changed"),
        Subscribers(_services.Cli, "InvocationStarted"),
        Subscribers(_services.Cli, "InvocationCompleted"),
        Subscribers(_services, "ConfigReloaded"));

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in LogicalDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void The_window_hosts_the_strip_control_over_the_shells_own_strip_view_model()
    {
        UiThread.Run(() =>
        {
            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            try
            {
                var control = LogicalDescendants<StatusStripControl>(window).Single();
                var shell = Assert.IsType<MainWindowViewModel>(window.DataContext);

                Assert.Same(control, window.FindName("StatusStrip"));
                Assert.Same(shell.Strip, control.DataContext);

                // The chips the strip used to have as slots of the window are the strip's now: nothing of the old density machinery is left to bind to.
                Assert.Null(window.FindName("VersionSlot"));
                Assert.Null(window.FindName("ConnectorSlot"));
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }

    [Fact]
    public void A_window_that_is_not_on_screen_keeps_the_strips_look_off_and_no_timer_runs()
    {
        UiThread.Run(() =>
        {
            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            try
            {
                var shell = (MainWindowViewModel)window.DataContext;

                // What the window does when it is shown, hidden or minimized, asked directly: a window that was never shown cannot be asked any other way.
                typeof(MainWindow).GetMethod("PublishInteractivity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

                Assert.False(window.IsVisible);
                Assert.False(shell.Strip.IsActive);
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }

    [Fact]
    public void A_window_listens_for_what_the_strip_shows_while_it_lives_and_stops_when_it_really_closes()
    {
        UiThread.Run(() =>
        {
            var before = Counts();

            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            var during = Counts();
            Assert.True(during.Facts > before.Facts, "the strip listens for the facts the panels hand over");
            Assert.True(during.Scope > before.Scope, "the strip listens for the connector filter");
            Assert.True(during.Settings > before.Settings, "the strip listens for the health pulse and the pause");
            Assert.True(during.Started > before.Started, "the strip counts the commands that start");
            Assert.True(during.Completed > before.Completed, "the strip counts the commands that end");
            Assert.True(during.Config > before.Config, "the strip follows config.yaml for the policy fallback");

            window.AllowClose();
            window.Close();

            Assert.Equal(before, Counts());
        });
    }

    [Fact]
    public void A_window_that_fails_to_build_gives_the_strips_subscriptions_back_too()
    {
        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(_services);
            var before = Counts();

            MainWindow.ConstructionProbe = () => throw new InvalidOperationException("XAML fault at the very end");
            try
            {
                _ = Assert.Throws<InvalidOperationException>(() => new MainWindow(_services, catalog, UnbuiltTray()));
            }
            finally
            {
                MainWindow.ConstructionProbe = null;
            }

            Assert.Equal(before, Counts());
        });
    }

    [Fact]
    public void Windows_built_and_closed_again_and_again_leave_nothing_behind()
    {
        UiThread.Run(() =>
        {
            var before = Counts();
            for (var i = 0; i < 4; i++)
            {
                var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
                window.AllowClose();
                window.Close();
            }

            Assert.Equal(before, Counts());
        });
    }
}
