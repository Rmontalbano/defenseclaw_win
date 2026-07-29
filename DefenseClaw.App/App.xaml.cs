using System.Windows;
using DefenseClaw.App.Services;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App;

/// <summary>
/// Application entry point and composition root driver.
/// <para>
/// Order matters here: single-instance check, then services (which resolves the gateway
/// token and hands it to <c>CliRunner.RegisterSecret</c> before anything can shell out),
/// then theming, then the tray, then the window, then the poll loop.
/// </para>
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private AppServices? _services;
    private PanelCatalog? _catalog;
    private TrayIconService? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            // Hand the request to the instance that already owns the tray icon.
            _instanceGuard.SignalFirstInstance();
            _instanceGuard.Dispose();
            _instanceGuard = null;
            Shutdown();
            return;
        }

        _instanceGuard.ActivationRequested += OnActivationRequested;
        _instanceGuard.StartListening();

        _services = AppServices.Initialize();
        _catalog = new PanelCatalog(_services);

        ApplyTheme();

        _tray = new TrayIconService(_services);
        _tray.OpenDashboardRequested += (_, _) => _window?.ShowAndActivate();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _window = new MainWindow(_services, _catalog, _tray);
        MainWindow = _window;

        // Autostart launches with --minimized: the tray is the app until the user asks for more.
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }

        // Started last, on the UI thread, so StateChanged is raised where the bindings live.
        _services.Monitor.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();

        if (_instanceGuard is not null)
        {
            _instanceGuard.ActivationRequested -= OnActivationRequested;
            _instanceGuard.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// .NET 9's <see cref="System.Windows.ThemeMode"/> drives the framework's own Fluent
    /// styles; WPF-UI's theme manager drives its control library. Both follow the OS, and
    /// both have to be told to.
    /// </summary>
    private void ApplyTheme()
    {
        ThemeMode = ThemeMode.System;
        ApplicationThemeManager.ApplySystemTheme();
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        // Raised on the guard's listener thread.
        _ = Dispatcher.BeginInvoke(() => _window?.ShowAndActivate());
    }

    private void ExitApplication()
    {
        _window?.AllowClose();
        _window?.Close();
        Shutdown();
    }
}
