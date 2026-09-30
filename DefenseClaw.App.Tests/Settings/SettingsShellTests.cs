using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.Navigation;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// How Settings sits in the shell (CUST-203): a footer panel in the catalog, the panel "Reopen on the last panel" brings the dashboard back to,
/// the sidebar's pinned entry, and what the window's close button does with "Closing the window keeps DefenseClaw in the tray" off.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsShellTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SettingsShellTests() => _services = TestServices.Create(_temp);

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private void Remember(bool on, string? panel = null) =>
        Assert.True(_services.Settings.Update(s => s with { Startup = s.Startup with { RememberLastPanel = on, LastPanelId = panel } }));

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public void Settings_is_registered_in_the_footer_with_its_own_view_and_view_model()
    {
        var catalog = new PanelCatalog(_services);

        var settings = catalog.ById("settings");

        Assert.NotNull(settings);
        Assert.Equal("Settings", settings.Title);
        Assert.Equal(PanelCatalog.FooterGroup, settings.Group);
        Assert.Equal(SymbolRegular.Settings24, settings.Icon);
        Assert.Equal(typeof(SettingsPanel), settings.ViewType);
        Assert.Equal("Setup", DcSections.OfPanelId("settings"));

        UiThread.Run(() =>
        {
            var view = Assert.IsType<SettingsPanel>(catalog.GetPage(typeof(SettingsPanel)));
            var model = Assert.IsType<SettingsPanelViewModel>(view.DataContext);
            Assert.Equal(settings.Title, model.Title);
        });
    }

    [Fact]
    public async Task The_page_reaches_the_trays_reset_through_the_catalogs_hooks()
    {
        var catalog = new PanelCatalog(_services);
        var calls = 0;
        catalog.Hooks.ResetSeenAlertHistory = () =>
        {
            calls++;
            return Task.CompletedTask;
        };

        var model = UiThread.Run(() => Assert.IsType<SettingsPanelViewModel>(((SettingsPanel)catalog.GetPage(typeof(SettingsPanel))!).DataContext));
        await UiThread.Run(() => model.ResetSeenAlertsCommand.ExecuteAsync(null));

        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------ the panel to open on

    [Fact]
    public void Without_the_setting_the_dashboard_opens_on_the_first_panel_whatever_was_remembered()
    {
        var catalog = new PanelCatalog(_services);
        Remember(on: false, panel: "alerts");

        Assert.Equal("overview", catalog.InitialPanel.Id);
    }

    [Fact]
    public void With_the_setting_it_reopens_on_the_remembered_panel_even_the_settings_page()
    {
        var catalog = new PanelCatalog(_services);

        Remember(on: true, panel: "audit");
        Assert.Equal("audit", catalog.InitialPanel.Id);

        Remember(on: true, panel: "SETTINGS");
        Assert.Equal("settings", catalog.InitialPanel.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-panel-that-was-removed")]
    public void With_the_setting_but_nothing_usable_remembered_it_opens_on_the_first_panel(string? remembered)
    {
        var catalog = new PanelCatalog(_services);
        Remember(on: true, panel: remembered);

        Assert.Equal("overview", catalog.InitialPanel.Id);
    }

    [Fact]
    public void A_navigation_request_waiting_for_the_window_wins_over_the_remembered_panel()
    {
        var catalog = new PanelCatalog(_services);
        Remember(on: true, panel: "audit");

        _services.Navigation.Request("settings");

        Assert.Equal("settings", catalog.InitialPanel.Id);
    }

    [Fact]
    public void A_panel_that_comes_on_screen_is_written_down_only_while_the_setting_is_on_and_only_when_it_changes()
    {
        var log = new List<string>();
        var catalog = new PanelCatalog(
            _services,
            new[]
            {
                new PanelDescriptor("probe-a", "Probe A", "Monitor", SymbolRegular.Home24, typeof(ProbeAView), s => new ProbeViewModel(s, "A", log)),
                new PanelDescriptor("probe-b", "Probe B", "Monitor", SymbolRegular.Home24, typeof(ProbeBView), s => new ProbeViewModel(s, "B", log)),
            });
        var writes = 0;
        _services.Settings.Changed += (_, _) => writes++;

        UiThread.Run(() =>
        {
            // Off: a panel coming up leaves the file alone.
            using var off = Host(catalog, typeof(ProbeAView));
            Assert.Equal(0, writes);
            Assert.Null(_services.Settings.Current.Startup.LastPanelId);
        });

        Remember(on: true);
        writes = 0;

        UiThread.Run(() =>
        {
            // On: each panel visit is remembered ...
            var hostA = Host(catalog, typeof(ProbeAView));
            Assert.Equal("probe-a", _services.Settings.Current.Startup.LastPanelId);
            Assert.Equal(1, writes);

            // ... the window going to the tray and back is not a new visit ...
            catalog.SetWindowInteractive(false);
            catalog.SetWindowInteractive(true);
            Assert.Equal(1, writes);

            // ... and the next panel replaces it, and is what a restart reads.
            hostA.Dispose();
            using var hostB = Host(catalog, typeof(ProbeBView));
            Assert.Equal("probe-b", _services.Settings.Current.Startup.LastPanelId);
            Assert.Equal(2, writes);
        });

        Assert.Equal("probe-b", AppSettingsStore.OpenFresh(_services.Settings.FilePath).Current.Startup.LastPanelId);
    }

    private static OffscreenHost Host(PanelCatalog catalog, Type viewType) =>
        new((FrameworkElement)catalog.GetPage(viewType)!, 100, 100);

    // ------------------------------------------------------------------ the sidebar's footer entry

    /// <summary>A sidebar built the way <c>MainWindow.BuildNavigation</c> builds it: the panels as entries, and Settings pinned in the footer.</summary>
    private NavigationView BuildSidebar(PanelCatalog catalog, out Dictionary<string, DcNavigationItem> items)
    {
        var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed };
        navigation.Resources[typeof(SymbolIcon)] = new Style(typeof(SymbolIcon), (Style)Application.Current.FindResource("DcNavIcon"));
        items = new Dictionary<string, DcNavigationItem>(StringComparer.Ordinal);

        foreach (var panel in catalog.SidebarOrder)
        {
            var item = new DcNavigationItem { Content = panel.Title, Icon = new SymbolIcon { Symbol = panel.Icon }, TargetPageType = panel.ViewType, Height = 34 };
            items[panel.Id] = item;
            _ = navigation.MenuItems.Add(item);
        }

        foreach (var panel in catalog.FooterPanels)
        {
            var item = new DcNavigationItem { Content = panel.Title, Icon = new SymbolIcon { Symbol = panel.Icon }, TargetPageType = panel.ViewType, Height = 34 };
            items[panel.Id] = item;
            _ = navigation.FooterMenuItems.Add(item);
        }

        navigation.SetPageProviderService(catalog);
        return navigation;
    }

    [Fact]
    public void The_footer_entry_navigates_to_the_settings_page_like_any_other_entry()
    {
        var catalog = new PanelCatalog(_services);

        UiThread.Run(() =>
        {
            var navigation = BuildSidebar(catalog, out var items);
            using var host = new OffscreenHost(navigation, 940, 620);

            Assert.True(navigation.Navigate(typeof(SettingsPanel)));
            host.Relayout();

            // The page is on screen and active, and its entry is the selected one, in the footer.
            var page = VisualTree.Find<SettingsPanel>(navigation);
            Assert.NotNull(page);
            Assert.True(page.IsVisible);
            Assert.True(((SettingsPanelViewModel)page.DataContext).IsActive);
            Assert.Contains(items["settings"], navigation.FooterMenuItems.Cast<object>());
            Assert.DoesNotContain(items["settings"], navigation.MenuItems.Cast<object>());
            Assert.True(items["settings"].IsActive);
            Assert.False(items["overview"].IsActive);

            // And leaving it for another panel deactivates it (the page stops listening to the store).
            Assert.True(navigation.Navigate(typeof(OverviewPanel)));
            host.Relayout();
            Assert.False(((SettingsPanelViewModel)page.DataContext).IsActive);
            Assert.False(items["settings"].IsActive);
        });
    }

    [Fact]
    public void The_footer_entry_stays_pinned_below_the_groups_in_a_short_window_and_is_rendered_for_review()
    {
        var catalog = new PanelCatalog(_services);

        UiThread.Run(() =>
        {
            var navigation = BuildSidebar(catalog, out var items);
            using var host = new OffscreenHost(navigation, 260, 420);

            // More entries than a 420 DIP pane can show: the groups scroll, the footer does not.
            var footer = items["settings"];
            var last = items["setup"];
            host.Relayout();

            var footerBottom = footer.TranslatePoint(new Point(0, footer.ActualHeight), navigation).Y;
            Assert.True(footer.IsVisible);
            Assert.InRange(footerBottom, 300, 420.5);
            Assert.True(last.TranslatePoint(new Point(0, 0), navigation).Y > footerBottom || !IsWithinViewport(last, navigation),
                "the last group entry and the footer entry are on top of each other");

            RenderTo.Png(host, "settings-sidebar-footer");
        });
    }

    private static bool IsWithinViewport(FrameworkElement element, FrameworkElement container)
    {
        var top = element.TranslatePoint(new Point(0, 0), container).Y;
        return top >= 0 && top + element.ActualHeight <= container.ActualHeight;
    }

    // ------------------------------------------------------------------ the close button

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    /// <summary>What WPF does when the close button is pressed: asks the window's <c>OnClosing</c>. Called directly: a window that was never shown cannot be asked any other way.</summary>
    private static bool CloseIsCancelled(MainWindow window)
    {
        var arguments = new CancelEventArgs();
        typeof(MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { arguments });
        return arguments.Cancel;
    }

    private static void HintAlreadyShown(MainWindow window) =>
        typeof(MainWindow).GetField("_minimizeHintShown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);

    [Fact]
    public void By_default_the_close_button_hides_the_window_and_asks_for_no_exit()
    {
        UiThread.Run(() =>
        {
            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            HintAlreadyShown(window);
            var exits = 0;
            window.ExitRequested += (_, _) => exits++;
            try
            {
                Assert.True(_services.Settings.Current.Startup.CloseToTray);

                Assert.True(CloseIsCancelled(window));
                Assert.Equal(0, exits);
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }

    [Fact]
    public void With_close_to_tray_off_the_close_button_asks_for_the_normal_exit_and_the_window_stays_until_it_says_yes()
    {
        Assert.True(_services.Settings.Update(s => s with { Startup = s.Startup with { CloseToTray = false } }));

        UiThread.Run(() =>
        {
            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            var exits = 0;
            window.ExitRequested += (_, _) => exits++;
            try
            {
                // Not hidden, not closed: the exit path (its upgrade and unsaved-config questions) decides.
                Assert.True(CloseIsCancelled(window));
                Assert.Equal(1, exits);
                Assert.Contains(window, Application.Current.Windows.OfType<MainWindow>());

                // Asked twice, it asks twice: a declined exit leaves the window as it was.
                Assert.True(CloseIsCancelled(window));
                Assert.Equal(2, exits);

                // The exit that goes ahead is the one that allows the close.
                window.AllowClose();
                Assert.False(CloseIsCancelled(window));
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }

    [Fact]
    public void The_setting_is_read_at_the_moment_of_the_close_not_when_the_window_was_built()
    {
        UiThread.Run(() =>
        {
            var window = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray());
            HintAlreadyShown(window);
            var exits = 0;
            window.ExitRequested += (_, _) => exits++;
            try
            {
                Assert.True(CloseIsCancelled(window));
                Assert.Equal(0, exits);

                Assert.True(_services.Settings.Update(s => s with { Startup = s.Startup with { CloseToTray = false } }));
                Assert.True(CloseIsCancelled(window));
                Assert.Equal(1, exits);

                Assert.True(_services.Settings.Update(s => s with { Startup = s.Startup with { CloseToTray = true } }));
                Assert.True(CloseIsCancelled(window));
                Assert.Equal(1, exits);
            }
            finally
            {
                window.AllowClose();
                window.Close();
            }
        });
    }

    [Fact]
    public void The_close_button_is_named_for_what_it_will_do()
    {
        Assert.Equal("Close to tray", MainWindow.CloseButtonName(closeToTray: true));
        Assert.Equal("Exit DefenseClaw", MainWindow.CloseButtonName(closeToTray: false));
    }

    [Fact]
    public void A_window_wires_the_trays_reset_and_gives_its_settings_subscription_back_when_it_really_closes()
    {
        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(_services);
            Assert.Null(catalog.Hooks.ResetSeenAlertHistory);

            var before = SettingsSubscribers();
            var window = new MainWindow(_services, catalog, UnbuiltTray());
            Assert.NotNull(catalog.Hooks.ResetSeenAlertHistory);
            Assert.True(SettingsSubscribers() > before);

            window.AllowClose();
            window.Close();
            Assert.Equal(before, SettingsSubscribers());
        });
    }

    [Fact]
    public void A_window_that_fails_to_build_gives_its_settings_subscription_back_too()
    {
        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(_services);
            var before = SettingsSubscribers();

            MainWindow.ConstructionProbe = () => throw new InvalidOperationException("XAML fault at the very end");
            try
            {
                _ = Assert.Throws<InvalidOperationException>(() => new MainWindow(_services, catalog, UnbuiltTray()));
            }
            finally
            {
                MainWindow.ConstructionProbe = null;
            }

            Assert.Equal(before, SettingsSubscribers());
        });
    }

    private int SettingsSubscribers()
    {
        var field = typeof(AppSettingsStore).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppSettingsStore.Changed is not a field-like event any more; update this helper.");
        return (field.GetValue(_services.Settings) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
