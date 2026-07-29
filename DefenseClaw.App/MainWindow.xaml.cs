using System.ComponentModel;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DefenseClaw.App;

/// <summary>
/// The dashboard window: title bar, status strip, banner region, sidebar.
/// <para>
/// Shell-owned. The sidebar is built from <see cref="PanelCatalog"/> at load time, so a
/// panel landing later never touches this file — and the close button minimizes to the
/// tray, because the tray is the app's real lifecycle.
/// </para>
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly PanelCatalog _catalog;
    private readonly TrayIconService _tray;
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;
    private bool _minimizeHintShown;

    public MainWindow(AppServices services, PanelCatalog catalog, TrayIconService tray)
    {
        ArgumentNullException.ThrowIfNull(services);

        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));

        InitializeComponent();

        _viewModel = new MainWindowViewModel(services);
        DataContext = _viewModel;

        // WPF-UI needs its own hook to repaint the window chrome when the OS theme flips;
        // Application.ThemeMode alone only covers the framework's Fluent dictionaries.
        SystemThemeWatcher.Watch(this);

        // Taskbar icon mirrors the tray shield, colour and all, so alt-tab tells the same
        // story as the notification area. Rendered at 256px so alt-tab and taskbar scaling
        // stay crisp; cached per state because StateChanged fires every poll.
        ApplyShieldIcon(ShieldIconFactory.StateFor(services.Monitor.Current));
        services.Monitor.StateChanged += (_, e) => ApplyShieldIcon(ShieldIconFactory.StateFor(e.Snapshot));

        Loaded += OnLoaded;
    }

    private static readonly Dictionary<ShieldState, System.Windows.Media.ImageSource> ShieldImageCache = new();
    private ShieldState? _currentIconState;

    private void ApplyShieldIcon(ShieldState state)
    {
        if (_currentIconState == state)
        {
            return;
        }

        _currentIconState = state;
        if (!ShieldImageCache.TryGetValue(state, out var image))
        {
            image = ShieldIconFactory.CreateImage(state, 256);
            ShieldImageCache[state] = image;
        }

        Icon = image;
    }

    /// <summary>Brings the window back from the tray. Used by the tray and by a second launch.</summary>
    public void ShowAndActivate()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        _ = Focus();
    }

    /// <summary>Lets the next close actually close. Only the tray's Exit item calls this.</summary>
    public void AllowClose()
    {
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Closing the dashboard is not quitting: the tray icon is the app.
            e.Cancel = true;
            Hide();

            if (!_minimizeHintShown)
            {
                _minimizeHintShown = true;
                _tray.Notify(
                    "DefenseClaw is still running",
                    "The dashboard closed to the tray and keeps watching the gateway. " +
                    "Right-click the shield and pick Exit to quit for real.");
            }

            return;
        }

        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        BuildNavigation();
    }

    /// <summary>
    /// Materializes the sidebar from the catalog: one non-clickable header per group, one
    /// item per panel, in catalog order.
    /// </summary>
    private void BuildNavigation()
    {
        foreach (var group in PanelCatalog.Groups)
        {
            _ = RootNavigation.MenuItems.Add(new NavigationViewItemHeader { Text = group });

            foreach (var panel in _catalog.InGroup(group))
            {
                _ = RootNavigation.MenuItems.Add(new NavigationViewItem
                {
                    Content = panel.Title,
                    Icon = new SymbolIcon { Symbol = panel.Icon },
                    TargetPageType = panel.ViewType,
                });
            }
        }

        RootNavigation.SetPageProviderService(_catalog);
        _ = RootNavigation.Navigate(_catalog.Default.ViewType);
    }
}
