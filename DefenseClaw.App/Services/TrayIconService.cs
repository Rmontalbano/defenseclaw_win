using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;
using DefenseClaw.Core.Install;
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
    private MenuItem? _gatewayItem;
    private MenuItem? _autostartItem;
    private int _lastCriticalCount;
    private AppGatewayState _lastState = AppGatewayState.Unknown;
    private bool _gatewayActionRunning;
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

        // Gateway control goes through CliRunner, so the invocation — exact argv, output,
        // exit code — lands in the Activity panel like every other mutation this app makes.
        _gatewayItem = new MenuItem { Header = "Start Gateway" };
        _gatewayItem.Click += async (_, _) => await ToggleGatewayAsync();
        _ = menu.Items.Add(_gatewayItem);

        _autostartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = AutostartManager.IsEnabled,
        };
        _autostartItem.Click += (_, _) =>
        {
            var on = AutostartManager.Toggle();
            _autostartItem.IsChecked = on;
            Notify("Start with Windows", on
                ? "DefenseClaw will start minimized to the tray when you sign in."
                : "Autostart removed.", NotificationIcon.Info);
        };
        _ = menu.Items.Add(_autostartItem);

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

    /// <summary>Runs `defenseclaw-gateway start` or `stop` depending on the current state.</summary>
    private async Task ToggleGatewayAsync()
    {
        if (_gatewayActionRunning)
        {
            return;
        }

        var stopping = _services.Monitor.Current.IsRunning;
        _gatewayActionRunning = true;
        try
        {
            var invocation = await _services.Cli.RunGatewayAsync(new[] { stopping ? "stop" : "start" });
            Notify(
                stopping ? "Stop Gateway" : "Start Gateway",
                invocation.ExitCode == 0
                    ? $"defenseclaw-gateway {(stopping ? "stop" : "start")} succeeded."
                    : $"Exit code {invocation.ExitCode} — see the Activity panel for output.",
                invocation.ExitCode == 0 ? NotificationIcon.Info : NotificationIcon.Warning);
            _ = await _services.Monitor.RefreshAsync();
        }
        catch (Exception ex)
        {
            Notify("Gateway control failed", ex.Message, NotificationIcon.Error);
        }
        finally
        {
            _gatewayActionRunning = false;
        }
    }

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

        // Toast on new CRITICALs and on losing the gateway — not on every poll.
        if (snapshot.CriticalAlertCount > _lastCriticalCount)
        {
            Notify(
                "Critical finding",
                $"{snapshot.CriticalAlertCount - _lastCriticalCount} new CRITICAL alert(s) — open Alerts for detail.",
                NotificationIcon.Error);
        }
        _lastCriticalCount = snapshot.CriticalAlertCount;

        if (_lastState == AppGatewayState.Running && snapshot.State is AppGatewayState.GatewayStopped or AppGatewayState.Degraded)
        {
            Notify("Gateway lost", snapshot.Detail, NotificationIcon.Warning);
        }
        _lastState = snapshot.State;

        if (_gatewayItem is not null)
        {
            _gatewayItem.Header = snapshot.IsRunning ? "Stop Gateway" : "Start Gateway";
            _gatewayItem.IsEnabled = snapshot.Install is not (null or InstallState.NotInstalled);
        }
    }
}
