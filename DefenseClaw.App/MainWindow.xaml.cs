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

    /// <summary>
    /// One line per panel whose initialization failed, newest fault per panel wins; what
    /// <c>PanelFaultBar</c> shows. Cleared when the operator dismisses the bar. UI thread only.
    /// </summary>
    private readonly List<(string PanelId, string Line)> _panelFaults = new();
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
        // stay crisp; cached per state because StateChanged also fires for changes that leave
        // the shield colour alone (a new detail line, a connector appearing).
        ApplyShieldIcon(ShieldIconFactory.StateFor(services.Monitor.Current));
        services.Monitor.StateChanged += (_, e) => ApplyShieldIcon(ShieldIconFactory.StateFor(e.Snapshot));

        // Panels only run while someone can see them. Hiding to the tray is caught by
        // IsVisibleChanged; a minimized window still reports itself visible, so the window
        // state is watched as well. Both feed the one flag the catalog understands.
        IsVisibleChanged += (_, _) => PublishInteractivity();
        StateChanged += (_, _) => PublishInteractivity();

        // A panel whose InitializeAsync throws would otherwise sit on "Loading…" forever with
        // nothing saying why. Subscribed before the sidebar exists (OnLoaded builds it), so no
        // fault can be missed. Dismissing the bar forgets the faults it listed: the next one
        // shows on its own instead of resurrecting ones the operator already saw.
        _catalog.PanelFaulted += OnPanelFaulted;
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            ?.AddValueChanged(PanelFaultBar, OnPanelFaultBarOpenChanged);

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Adds the faulted panel to the banner. Normally already on the UI thread (see
    /// <see cref="PanelCatalog.PanelFaulted"/>); marshals anyway so a future caller that is
    /// not cannot turn a panel fault into a cross-thread exception.
    /// </summary>
    private void OnPanelFaulted(object? sender, PanelFaultEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnPanelFaulted(sender, e));
            return;
        }

        var reason = e.Exception.Message.ReplaceLineEndings(" ").Trim();
        if (reason.Length > 300)
        {
            reason = string.Concat(reason.AsSpan(0, 300), "…");
        }

        var line = $"{e.Panel.Title} — {e.Exception.GetType().Name}: {reason}";
        var existing = _panelFaults.FindIndex(fault => string.Equals(fault.PanelId, e.Panel.Id, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _panelFaults[existing] = (e.Panel.Id, line);
        }
        else
        {
            _panelFaults.Add((e.Panel.Id, line));
        }

        PanelFaultBar.Message =
            string.Join(Environment.NewLine, _panelFaults.Select(fault => fault.Line)) +
            Environment.NewLine +
            "The affected panel may stay empty or on “Loading…”; the rest of the dashboard is unaffected.";
        PanelFaultBar.IsOpen = true;
    }

    private void OnPanelFaultBarOpenChanged(object? sender, EventArgs e)
    {
        if (!PanelFaultBar.IsOpen)
        {
            _panelFaults.Clear();
        }
    }

    /// <summary>
    /// Whether the operator can actually see this window: shown, and not minimized to the
    /// taskbar. The panels' pause/resume hangs off this — see the activation contract on
    /// <see cref="PanelViewModelBase"/>. The tray, its toasts and the shell status strip do
    /// not, and keep running.
    /// </summary>
    private void PublishInteractivity() =>
        _catalog.SetWindowInteractive(IsVisible && WindowState != WindowState.Minimized);

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

        _catalog.PanelFaulted -= OnPanelFaulted;
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
