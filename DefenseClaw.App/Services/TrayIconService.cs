using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DefenseClaw.App.Services;

/// <summary>
/// Owns the tray icon, its context menu and the left-click flyout.
/// <para>
/// The tray is the app's primary surface — the Windows answer to the macOS menu-bar
/// companion — so this service outlives every window: closing the dashboard only hides
/// it, and the process exits solely through <see cref="ExitRequested"/>.
/// </para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppServices _services;
    private readonly TaskbarIcon _icon;
    private readonly TrayFlyoutViewModel _flyoutViewModel;
    private TrayFlyoutWindow? _flyout;
    private ShieldState? _currentShield;
    private bool _disposed;

    public TrayIconService(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _flyoutViewModel = new TrayFlyoutViewModel(
            services,
            () =>
            {
                HideFlyout();
                OnOpenDashboard();
            },
            () =>
            {
                HideFlyout();
                OnExitRequested();
            });

        _icon = new TaskbarIcon
        {
            ToolTipText = "DefenseClaw",
            ContextMenu = BuildContextMenu(),
            // The context menu binds through this element's DataContext.
            DataContext = _flyoutViewModel,
        };

        _icon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _services.Monitor.StateChanged += OnStateChanged;

        Apply(_services.Monitor.Current);
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Raised when the user picks "Open Dashboard" or clicks the flyout button.</summary>
    public event EventHandler? OpenDashboardRequested;

    /// <summary>Raised only by the tray's Exit item — the one real way out of the app.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Shows a tray balloon. Used for the close-to-tray hint and for stubs.</summary>
    public void Notify(string title, string message, NotificationIcon icon = NotificationIcon.Info)
    {
        if (_disposed)
        {
            return;
        }

        _icon.ShowNotification(title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Monitor.StateChanged -= OnStateChanged;
        _icon.TrayLeftMouseUp -= OnTrayLeftMouseUp;

        _flyout?.ForceClose();
        _flyoutViewModel.Dispose();
        _icon.Dispose();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open Dashboard", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => OnOpenDashboard();
        _ = menu.Items.Add(open);

        // Deliberately enabled but inert: wiring this to `defenseclaw-gateway start` is a
        // later milestone, and every mutation this app makes has to land in the Activity
        // panel with its exact argv. A menu item that silently shells out would not.
        var start = new MenuItem { Header = "Start Gateway" };
        start.Click += (_, _) => Notify(
            "Start Gateway",
            "Gateway control is wired in a later milestone. For now, run 'defenseclaw-gateway start' yourself.",
            NotificationIcon.Info);
        _ = menu.Items.Add(start);

        _ = menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => OnExitRequested();
        _ = menu.Items.Add(exit);

        return menu;
    }

    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => ToggleFlyout();

    private void ToggleFlyout()
    {
        _flyout ??= new TrayFlyoutWindow { DataContext = _flyoutViewModel };

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        _flyout.ShowNearCursor();
    }

    private void HideFlyout() => _flyout?.Hide();

    private void OnOpenDashboard() => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    private void Apply(GatewaySnapshot snapshot)
    {
        var shield = ShieldIconFactory.StateFor(snapshot);
        if (_currentShield != shield)
        {
            _currentShield = shield;
            _icon.Icon = ShieldIconFactory.Get(shield);
        }

        // Tray tooltips are truncated hard by the shell, so lead with the state.
        var alerts = snapshot.AlertsUnavailable is { Length: > 0 }
            ? string.Empty
            : $"\n{snapshot.AlertCount} recent alert{(snapshot.AlertCount == 1 ? string.Empty : "s")}";

        _icon.ToolTipText = $"DefenseClaw — {snapshot.StateLabel}{alerts}";
    }
}
