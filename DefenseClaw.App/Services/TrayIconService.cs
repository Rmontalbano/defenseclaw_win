using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
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

    /// <summary>
    /// Decides the shield and the tooltip, and owns the icon assigned to <see cref="TaskbarIcon.Icon"/> — a private icon
    /// from <see cref="ShieldIconFactory.CreateIcon(TrayShieldKey)"/> that nobody else holds. While it is still assigned it
    /// must not be disposed or shared: the library drives the native tray icon from it (reading
    /// <c>Icon.Handle</c>, which throws <see cref="ObjectDisposedException"/> once disposed — the
    /// original crash). The presenter disposes the outgoing one after the swap; see <see cref="TrayShieldPresenter"/>.
    /// </summary>
    private readonly TrayShieldPresenter _shield;

    /// <summary>Whether a scan this app started is running: the second of the shield's inputs the gateway snapshot does not carry. See <see cref="ScanActivity"/>.</summary>
    private readonly ScanActivity _scans;
    private MenuItem? _gatewayItem;
    private MenuItem? _restartItem;
    private MenuItem? _autostartItem;

    /// <summary>
    /// Decides the gateway offline / recovered toasts: only for a gateway this session has seen
    /// reachable, and only while the notification setting allows them. UI thread only.
    /// </summary>
    private readonly GatewayToastTracker _gatewayToasts = new();

    /// <summary>
    /// Watches the alert queue and raises the finding toasts (CRITICAL / HIGH as the settings allow, findings
    /// that arrived while the app was closed as one batch, the mark persisted). See <see cref="AlertNotifier"/>.
    /// </summary>
    private readonly AlertNotifier _alertNotifier;

    /// <summary>
    /// Where a click on the balloon currently showing goes (Alerts, on the severity the toast announced), or nowhere
    /// for a toast that goes nowhere. Armed by whichever toast was shown last, which is the one on screen; taken by
    /// <see cref="OnTrayBalloonTipClicked"/> so one click navigates once.
    /// </summary>
    private readonly BalloonClickTarget _balloonTarget = new();

    private bool _gatewayActionRunning;
    private bool _disposed;

    public TrayIconService(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // Before the menu is built, so the checkmark it shows is the repaired state: a Run
        // entry left pointing at an exe that has since moved is fixed here, once per launch.
        AutostartManager.RepairIfStale();

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

        // Both before anything that can call back into the shield: its inputs must exist when the first notification lands.
        _shield = new TrayShieldPresenter(
            ShieldIconFactory.CreateIcon,
            icon => _icon.Icon = icon,
            text => _icon.ToolTipText = text);

        // No cost while no scan runs: two events on the CLI runner, no timer.
        _scans = new ScanActivity(_services.Cli);
        _scans.Changed += OnScanActivityChanged;

        _icon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _icon.TrayBalloonTipClicked += OnTrayBalloonTipClicked;
        _services.Monitor.StateChanged += OnStateChanged;

        // The tray is the count's permanent reader (the tooltip and the number on the shield), so the counts service runs for the
        // life of the process.
        _services.AlertCounts.Changed += OnAlertCountsChanged;
        _alertNotifier = new AlertNotifier(_services, ShowAlertToast);

        Apply(_services.Monitor.Current);
        _icon.ForceCreate(enablesEfficiencyMode: false);

        // After the icon exists: the first look at the queue may toast what arrived while the app was closed.
        _alertNotifier.Start();
    }

    /// <summary>Raised when the user picks "Open Dashboard" or clicks the flyout button.</summary>
    public event EventHandler? OpenDashboardRequested;

    /// <summary>Raised only by the tray's Exit item — the one real way out of the app.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Shows a tray balloon. Used for the close-to-tray hint and for stubs. A click on it goes nowhere.</summary>
    public void Notify(string title, string message, NotificationIcon icon = NotificationIcon.Info) =>
        Notify(title, message, icon, onClick: null);

    /// <summary>
    /// Shows a tray balloon that, when clicked, asks the shell for <paramref name="onClick"/> (null: nothing happens). The
    /// balloon showing now is the last one shown, so a toast that goes nowhere also takes away the target of the one before it.
    /// </summary>
    private void Notify(string title, string message, NotificationIcon icon, NavigationRequest? onClick)
    {
        if (_disposed)
        {
            return;
        }

        _balloonTarget.Arm(onClick);
        _icon.ShowNotification(title, message, icon);
    }

    /// <summary>A finding toast: severity and target (see <see cref="AlertToast"/>), and a click opens Alerts on the severity it announced.</summary>
    private void ShowAlertToast(AlertToast toast) =>
        Notify(toast.Title, toast.Body, IconFor(toast.Level), new NavigationRequest("alerts", toast.Filter));

    private static NotificationIcon IconFor(ToastLevel level) => level switch
    {
        ToastLevel.Error => NotificationIcon.Error,
        ToastLevel.Warning => NotificationIcon.Warning,
        _ => NotificationIcon.Info,
    };

    /// <summary>
    /// "Reset seen-alert history" (the palette's entry, and the Settings page's button): forgets which findings have been
    /// announced, so what is outstanding now (CRITICAL and HIGH, as the settings allow) is announced once more as one toast.
    /// When there is nothing to announce (nothing outstanding, or the queue could not be read this time) the operator is told the
    /// history was cleared, so the command never seems to do nothing.
    /// </summary>
    public async Task ResetSeenAlertHistoryAsync()
    {
        if (_disposed)
        {
            return;
        }

        var announced = await _alertNotifier.ResetSeenHistoryAsync().ConfigureAwait(true);
        if (!announced)
        {
            Notify(
                "Seen-alert history reset",
                "Nothing to announce right now. New CRITICAL and HIGH findings will show as they arrive.");
        }
    }

    /// <summary>
    /// A click on the balloon that is showing: open the dashboard where that toast said to (Alerts, narrowed to the severity
    /// it announced). Raised by the native notification's user-click, which the library reports as
    /// <c>TrayBalloonTipClicked</c>; taken once, so a click never navigates twice.
    /// </summary>
    private void OnTrayBalloonTipClicked(object sender, RoutedEventArgs e)
    {
        if (!_disposed)
        {
            _ = _balloonTarget.Click(_services.Navigation);
        }
    }

    private void OnAlertCountsChanged(object? sender, AlertCountsChangedEventArgs e)
    {
        if (!_disposed)
        {
            RefreshShield(_services.Monitor.Current);
        }
    }

    private void OnScanActivityChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            RefreshShield(_services.Monitor.Current);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Monitor.StateChanged -= OnStateChanged;
        _services.AlertCounts.Changed -= OnAlertCountsChanged;
        _scans.Changed -= OnScanActivityChanged;
        _scans.Dispose();
        _alertNotifier.Dispose();
        _icon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
        _icon.TrayBalloonTipClicked -= OnTrayBalloonTipClicked;

        _flyout?.ForceClose();
        _flyoutViewModel.Dispose();

        try
        {
            _icon.Dispose();
        }
        finally
        {
            // Whether TaskbarIcon.Dispose releases its current Icon is not something this code
            // relies on: the last clone would leak a GDI handle if it doesn't, and Icon.Dispose is
            // idempotent (it only destroys a non-zero handle, then zeroes it), so a second
            // disposal after the library's is a no-op. This runs strictly after the tray icon is
            // torn down so the shell never sees the handle destroyed while still registered.
            _shield.Dispose();
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open Dashboard", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => OnOpenDashboard();
        _ = menu.Items.Add(open);

        // Gateway control goes through CliRunner, so the invocation — exact argv, output,
        // exit code — lands in the Activity panel like every other mutation this app makes. Each
        // verb is StateChanging (CommandTiers), so each opens the review dialog first.
        _gatewayItem = new MenuItem { Header = "Start Gateway" };
        _gatewayItem.Click += async (_, _) =>
            await RunGatewayActionAsync(_services.Monitor.Current.IsRunning ? GatewayAction.Stop : GatewayAction.Start);
        _ = menu.Items.Add(_gatewayItem);

        _restartItem = new MenuItem { Header = "Restart Gateway" };
        _restartItem.Click += async (_, _) => await RunGatewayActionAsync(GatewayAction.Restart);
        _ = menu.Items.Add(_restartItem);

        _ = menu.Items.Add(new Separator());

        _autostartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = AutostartManager.IsEnabled,
        };
        _autostartItem.Click += (_, _) => _ = ToggleAutostart();
        _ = menu.Items.Add(_autostartItem);

        // App-local: pausing stops this app's polling and audit reads; the gateway keeps running.
        var pauseItem = new MenuItem { Header = PauseHeader() };
        pauseItem.Click += (_, _) => _services.Monitor.SetPaused(!_services.Monitor.IsPaused);
        _ = menu.Items.Add(pauseItem);

        // The checkmark is re-read every time the menu opens: Task Manager's Startup tab and
        // Settings can disable (or re-enable) the entry while the app runs, and a menu built
        // once at launch would keep claiming whatever was true then. The pause label likewise
        // follows the flyout's own Pause/Resume button.
        menu.Opened += (_, _) =>
        {
            if (_autostartItem is not null)
            {
                _autostartItem.IsChecked = AutostartManager.IsEnabled;
            }

            pauseItem.Header = PauseHeader();
        };

        _ = menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => OnExitRequested();
        _ = menu.Items.Add(exit);

        return menu;
    }

    private string PauseHeader() => _services.Monitor.IsPaused ? "Resume monitoring" : "Pause monitoring";

    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => ToggleFlyout();

    private void ToggleFlyout()
    {
        _flyout ??= CreateFlyout();

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        // The flyout hides itself the moment it loses focus, and pressing the tray icon is
        // what takes the focus: by the time this mouse-UP arrives the flyout is already
        // hidden, IsVisible is false, and without this check the click that was meant to
        // dismiss it would open it again. See TrayFlyoutWindow.WasJustHiddenByFocusLoss.
        if (_flyout.WasJustHiddenByFocusLoss)
        {
            return;
        }

        _flyout.ShowNearCursor();
    }

    private TrayFlyoutWindow CreateFlyout()
    {
        var flyout = new TrayFlyoutWindow { DataContext = _flyoutViewModel };

        // WPF makes the first Window a process builds its Application.MainWindow, and the dashboard
        // is no longer built at startup — so this flyout can now be that first window. WPF-UI
        // re-applies the window backdrop to Application.MainWindow on every theme change, which is
        // right for the dashboard and wrong for this transparent, frameless popup. MainWindow is
        // meant to be the dashboard or nothing; the dashboard claims it when it is built.
        if (Application.Current is { } app && ReferenceEquals(app.MainWindow, flyout))
        {
            app.MainWindow = null;
        }

        return flyout;
    }

    private void HideFlyout() => _flyout?.Hide();

    private void OnOpenDashboard() => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// Flips Start-with-Windows, mirrors the result on the menu's checkmark and says so in a toast.
    /// Shared by the tray menu and the command palette so both behave and read the same.
    /// </summary>
    /// <returns>The new state: true when the app will now start at sign-in.</returns>
    public bool ToggleAutostart()
    {
        var result = AutostartManager.Toggle();

        // On a refusal (a policy-locked Run key) Enabled is the state as it was, so the checkmark the click just
        // flipped goes back to what is true.
        if (_autostartItem is not null)
        {
            _autostartItem.IsChecked = result.Enabled;
        }

        if (result.FailureMessage is { } failure)
        {
            Notify("Start with Windows", failure, NotificationIcon.Warning);
            return result.Enabled;
        }

        Notify("Start with Windows", result.Enabled
            ? "DefenseClaw will start minimized to the tray when you sign in."
            : "Autostart removed.", NotificationIcon.Info);
        return result.Enabled;
    }

    /// <summary>
    /// Runs <c>defenseclaw-gateway start | stop | restart</c> (verified against the CLI's help; see
    /// <see cref="GatewayControl"/>) after the operator has reviewed the exact command.
    /// <para>
    /// Every verb here is <c>StateChanging</c> in <see cref="CommandTiers"/>, so it always goes
    /// through <see cref="GatewayActionDialog"/> first — the tray menu and the command palette share
    /// this one path. The run itself is a <see cref="CliRunner"/> call, so the argv, output and exit
    /// code land in the Activity panel, and the result is toasted. The busy flag is held from the
    /// moment the review opens until the run and its follow-up poll finish, so a second click cannot
    /// stack a second dialog or a second command.
    /// </para>
    /// </summary>
    /// <param name="action">Start, stop or restart.</param>
    /// <param name="owner">The visible dashboard to centre the review over, or null.</param>
    public async Task RunGatewayActionAsync(GatewayAction action, Window? owner = null)
    {
        if (_disposed || _gatewayActionRunning)
        {
            return;
        }

        var title = GatewayControl.Title(action);

        var (allowed, reason) = GatewayControl.Availability(action, _services.Monitor.Current, _services.Installation);
        if (!allowed)
        {
            Notify(title, reason ?? "Not available right now.", NotificationIcon.Info);
            return;
        }

        var argv = GatewayControl.Argv(action);

        _gatewayActionRunning = true;
        try
        {
            if (CommandTiers.Classify(argv) != CommandTier.ReadOnly && !GatewayActionDialog.Confirm(owner, action))
            {
                return;
            }

            // A confirmed Stop is the operator's word for the rest of the session: no automatic start follows it.
            if (action == GatewayAction.Stop)
            {
                _services.GatewayAutoStart.MarkUserStopped();
            }

            var invocation = await _services.Cli.RunGatewayAsync(argv);
            Notify(
                title,
                invocation.ExitCode == 0
                    ? GatewayControl.SucceededText(action)
                    : invocation.ExitCode is { } code
                        ? $"Exit code {code} — see the Activity panel for output."
                        : $"{invocation.FailureReason ?? "The command did not finish"} — see the Activity panel.",
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

    /// <summary>
    /// Pushes a gateway snapshot onto the tray: shield, tooltip, toasts, menu text.
    /// <para>
    /// Icon ownership: every assignment to <see cref="TaskbarIcon.Icon"/> gets a brand-new
    /// <see cref="System.Drawing.Icon"/> from <see cref="ShieldIconFactory.CreateIcon(TrayShieldKey)"/>, never a shared
    /// or previously used one. <c>H.NotifyIcon.Wpf</c> 2.3.2 <b>disposes the icon that was assigned
    /// before</b> whenever <c>TaskbarIcon.Icon</c> is reassigned (proved with a harness), so
    /// re-assigning an instance that has ever been handed to the library throws
    /// <c>ObjectDisposedException ('Icon')</c> from <c>TaskbarIcon.UpdateIcon</c> — the crash seen
    /// 25 times in the field on Running → Stopped → Running flaps. The presenter therefore only ever
    /// assigns fresh ones, and disposes the outgoing icon itself once the swap is done: redundant
    /// with the library's own disposal (<see cref="System.Drawing.Icon.Dispose()"/> is idempotent) while
    /// it does that, and what keeps a handle from leaking if a future version of the pinned
    /// H.NotifyIcon.Wpf stops. The construction-time icon goes through this same path (the
    /// presenter has no icon showing on the first call), so there is no separate initial
    /// assignment to keep in sync.
    /// </para>
    /// <para>
    /// What the shield and the tooltip show also depends on the unacknowledged count and on whether a scan is running, which
    /// the snapshot does not carry; <see cref="RefreshShield"/> is this without the toasts and the menu, for when only those change.
    /// </para>
    /// </summary>
    private void Apply(GatewaySnapshot snapshot)
    {
        // A queued StateChanged delivery landing after Dispose must not resurrect an icon on a
        // disposed TaskbarIcon (and would leak the clone, since Dispose has already run).
        if (_disposed)
        {
            return;
        }

        RefreshShield(snapshot);

        // Findings toast from the alert queue (AlertNotifier), not from here; this is the gateway's own edges. Offline and
        // recovered are edges, not levels: one toast per transition, and only for a gateway this session has seen reachable.
        // While the operator's own start / stop / restart is running the state is expected to bounce, and that action has
        // its own toast, so neither edge is announced then. (The recovery that lands after the command returns — the
        // gateway finishing its start — still is.)
        if (_gatewayToasts.Observe(snapshot, _gatewayActionRunning, _services.Settings.Current.Notifications.Gateway) is { } toast)
        {
            Notify(toast.Title, toast.Body, IconFor(toast.Level));
        }

        // Menu availability shares GatewayControl's rules with the command palette; the reason is
        // the item's tooltip so a greyed-out entry says why.
        if (_gatewayItem is not null)
        {
            var action = snapshot.IsRunning ? GatewayAction.Stop : GatewayAction.Start;
            var (allowed, reason) = GatewayControl.Availability(action, snapshot, _services.Installation);
            _gatewayItem.Header = snapshot.IsRunning ? "Stop Gateway" : "Start Gateway";
            _gatewayItem.IsEnabled = allowed;
            _gatewayItem.ToolTip = allowed ? GatewayControl.Summary(action) : reason;
        }

        if (_restartItem is not null)
        {
            var (allowed, reason) = GatewayControl.Availability(GatewayAction.Restart, snapshot, _services.Installation);
            _restartItem.IsEnabled = allowed;
            _restartItem.ToolTip = allowed ? GatewayControl.Summary(GatewayAction.Restart) : reason;
        }
    }

    /// <summary>
    /// The shield and the tooltip: the Mac's menu-bar icon. Its inputs are the gateway snapshot (paused, offline, degraded, healthy), the
    /// unacknowledged-findings count (<see cref="AlertCountsService"/>, the same number as the sidebar badge, null until its first read)
    /// and whether a scan is running (<see cref="ScanActivity"/>); <see cref="ShieldIconFactory.StateFor(GatewaySnapshot, int?, bool)"/> has
    /// the precedence and <see cref="TrayShieldPresenter"/> replaces only what differs. Called whenever one of the three changes, from the
    /// notifications the app already raises for them: no timer.
    /// </summary>
    private void RefreshShield(GatewaySnapshot snapshot) =>
        _ = _shield.Update(
            snapshot,
            _services.AlertCounts.HasData ? _services.AlertCounts.Current : null,
            _scans.IsScanning);
}
