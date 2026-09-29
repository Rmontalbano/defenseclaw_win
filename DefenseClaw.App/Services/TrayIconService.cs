using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Drawing = System.Drawing;

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
    /// <summary>
    /// Cap on remembered CRITICAL alert ids. The gateway is asked for the newest
    /// <see cref="GatewayMonitor.AlertLimit"/> (25) alerts, so anything near that size already
    /// covers a full window turning over; the headroom is for churn between poll cycles.
    /// </summary>
    private const int MaxSeenAlerts = 512;

    private readonly AppServices _services;
    private readonly TaskbarIcon _icon;
    private readonly TrayFlyoutViewModel _flyoutViewModel;
    private TrayFlyoutWindow? _flyout;
    private ShieldState? _currentShield;

    /// <summary>
    /// The icon instance currently assigned to <see cref="TaskbarIcon.Icon"/> — a private icon
    /// from <see cref="ShieldIconFactory.CreateIcon"/> that nobody else holds. Tracked only so
    /// <see cref="Dispose"/> can release the last one defensively. While it is still assigned it
    /// must not be disposed or shared: the library drives the native tray icon from it (reading
    /// <c>Icon.Handle</c>, which throws <see cref="ObjectDisposedException"/> once disposed — the
    /// original crash). Outgoing icons are NOT disposed here; see <see cref="Apply"/>.
    /// </summary>
    private Drawing.Icon? _ownedIcon;
    private MenuItem? _gatewayItem;
    private MenuItem? _restartItem;
    private MenuItem? _autostartItem;

    /// <summary>
    /// Ids of CRITICAL alerts this tray has already accounted for, so a toast fires once per
    /// finding. Bounded (<see cref="MaxSeenAlerts"/>, oldest evicted first via
    /// <see cref="_seenAlertOrder"/>) — a long-lived tray process must not grow it forever.
    /// UI thread only, like everything in <see cref="Apply"/>.
    /// </summary>
    private readonly HashSet<string> _seenCriticalAlerts = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenAlertOrder = new();

    /// <summary>
    /// False until the first snapshot carrying a successfully fetched alert list has been
    /// absorbed. That first list is history — whatever CRITICALs the gateway already held
    /// when the app launched — and seeds <see cref="_seenCriticalAlerts"/> without a toast.
    /// </summary>
    private bool _alertsSeeded;
    private AppGatewayState _lastState = AppGatewayState.Unknown;
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
            _ownedIcon?.Dispose();
            _ownedIcon = null;
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

        // The checkmark is re-read every time the menu opens: Task Manager's Startup tab and
        // Settings can disable (or re-enable) the entry while the app runs, and a menu built
        // once at launch would keep claiming whatever was true then.
        menu.Opened += (_, _) =>
        {
            if (_autostartItem is not null)
            {
                _autostartItem.IsChecked = AutostartManager.IsEnabled;
            }
        };

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
        var on = AutostartManager.Toggle();

        if (_autostartItem is not null)
        {
            _autostartItem.IsChecked = on;
        }

        Notify("Start with Windows", on
            ? "DefenseClaw will start minimized to the tray when you sign in."
            : "Autostart removed.", NotificationIcon.Info);
        return on;
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

        var (allowed, reason) = GatewayControl.Availability(action, _services.Monitor.Current);
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
    /// Pushes a gateway snapshot onto the tray: shield colour, tooltip, toasts, menu text.
    /// <para>
    /// Icon ownership: every assignment to <see cref="TaskbarIcon.Icon"/> gets a brand-new
    /// <see cref="Drawing.Icon"/> from <see cref="ShieldIconFactory.CreateIcon"/>, never a shared
    /// or previously used one. <c>H.NotifyIcon.Wpf</c> 2.3.2 <b>disposes the icon that was assigned
    /// before</b> whenever <c>TaskbarIcon.Icon</c> is reassigned (proved with a harness), so
    /// re-assigning an instance that has ever been handed to the library throws
    /// <c>ObjectDisposedException ('Icon')</c> from <c>TaskbarIcon.UpdateIcon</c> — the crash seen
    /// 25 times in the field on Running → Stopped → Running flaps. The outgoing icon is therefore
    /// deliberately left for the library to dispose; disposing it here as well would be harmless
    /// (<see cref="Drawing.Icon.Dispose()"/> is idempotent) but redundant, and would start to
    /// matter only if a future library version stopped disposing — revisit on any upgrade of the
    /// pinned H.NotifyIcon.Wpf 2.3.2. The construction-time icon goes through this same path
    /// (<c>_currentShield</c> is null on the first call), so there is no separate initial
    /// assignment to keep in sync.
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

        var shield = ShieldIconFactory.StateFor(snapshot);
        if (_currentShield != shield)
        {
            _currentShield = shield;

            var fresh = ShieldIconFactory.CreateIcon(shield);
            _icon.Icon = fresh;
            _ownedIcon = fresh;
        }

        // Tray tooltips are truncated hard by the shell, so lead with the state.
        var alerts = snapshot.AlertsUnavailable is { Length: > 0 }
            ? string.Empty
            : $"\n{snapshot.AlertCount} recent alert{(snapshot.AlertCount == 1 ? string.Empty : "s")}";

        _icon.ToolTipText = $"DefenseClaw — {snapshot.StateLabel}{alerts}";

        // Toast on new CRITICALs and on losing the gateway — not on every poll.
        var newCritical = CountNewCriticalAlerts(snapshot);
        if (newCritical > 0)
        {
            Notify(
                "Critical finding",
                $"{newCritical} new CRITICAL alert{(newCritical == 1 ? string.Empty : "s")} — open Alerts for detail.",
                NotificationIcon.Error);
        }

        // Gateway lost / recovered are edges, not levels: one toast per transition. While the operator's
        // own start / stop / restart is running the state is expected to bounce, and that action has
        // its own toast, so neither edge is announced then. (The recovery that lands after the
        // command returns — the gateway finishing its start — still is.)
        if (!_gatewayActionRunning)
        {
            if (_lastState == AppGatewayState.Running && snapshot.State is AppGatewayState.GatewayStopped or AppGatewayState.Degraded)
            {
                Notify("Gateway lost", snapshot.Detail, NotificationIcon.Warning);
            }
            else if (snapshot.State == AppGatewayState.Running && _lastState is AppGatewayState.GatewayStopped or AppGatewayState.Degraded)
            {
                Notify("Gateway recovered", "The DefenseClaw gateway is answering again.", NotificationIcon.Info);
            }
        }

        _lastState = snapshot.State;

        // Menu availability shares GatewayControl's rules with the command palette; the reason is
        // the item's tooltip so a greyed-out entry says why.
        if (_gatewayItem is not null)
        {
            var action = snapshot.IsRunning ? GatewayAction.Stop : GatewayAction.Start;
            var (allowed, reason) = GatewayControl.Availability(action, snapshot);
            _gatewayItem.Header = snapshot.IsRunning ? "Stop Gateway" : "Start Gateway";
            _gatewayItem.IsEnabled = allowed;
            _gatewayItem.ToolTip = allowed ? GatewayControl.Summary(action) : reason;
        }

        if (_restartItem is not null)
        {
            var (allowed, reason) = GatewayControl.Availability(GatewayAction.Restart, snapshot);
            _restartItem.IsEnabled = allowed;
            _restartItem.ToolTip = allowed ? GatewayControl.Summary(GatewayAction.Restart) : reason;
        }
    }

    /// <summary>
    /// How many CRITICAL alerts in <paramref name="snapshot"/> this tray has not seen before —
    /// the number the "new CRITICAL alert(s)" toast reports.
    /// <para>
    /// This is identity-based, not count-based. The snapshot carries only the newest
    /// <see cref="GatewayMonitor.AlertLimit"/> alerts, so comparing this snapshot's CRITICAL
    /// <i>count</i> with the last one's is wrong both ways: a gateway restart, a sleep/resume
    /// blip or the app launching sees the count go 0 → N and re-announces old findings as new,
    /// and a new CRITICAL that pushes an older one out of the window leaves the count unchanged
    /// and never toasts. Each CRITICAL is instead remembered by id (bounded, see
    /// <see cref="MaxSeenAlerts"/>) and announced exactly once.
    /// </para>
    /// <para>
    /// The first snapshot with a successfully fetched list only <i>seeds</i> that memory and
    /// returns 0: opening the app must not toast whatever history the gateway already holds.
    /// A snapshot whose alert list is missing (<see cref="GatewaySnapshot.AlertsUnavailable"/>
    /// set, or nothing ever fetched) says nothing about what the gateway holds and is ignored
    /// entirely — it neither seeds nor forgets.
    /// </para>
    /// </summary>
    private int CountNewCriticalAlerts(GatewaySnapshot snapshot)
    {
        if (snapshot.AlertsFetchedAt is null || snapshot.AlertsUnavailable is { Length: > 0 })
        {
            return 0;
        }

        var unseen = 0;
        foreach (var alert in snapshot.RecentAlerts)
        {
            if (string.Equals(alert.Severity, "CRITICAL", StringComparison.OrdinalIgnoreCase) &&
                RememberAlert(AlertKey(alert)))
            {
                unseen++;
            }
        }

        var announce = _alertsSeeded ? unseen : 0;
        _alertsSeeded = true;
        return announce;
    }

    /// <summary>
    /// The alert's id; a composite of its other fields for a finding the gateway sent without
    /// one, so two id-less alerts are still told apart and the same one is still recognized.
    /// </summary>
    private static string AlertKey(GatewayAlert alert) =>
        alert.Id.Length > 0
            ? alert.Id
            : $"{alert.Timestamp.UtcTicks}|{alert.Action}|{alert.Target}|{alert.Title}";

    /// <summary>Records <paramref name="key"/>; true when it had not been recorded yet.</summary>
    private bool RememberAlert(string key)
    {
        if (!_seenCriticalAlerts.Add(key))
        {
            return false;
        }

        _seenAlertOrder.Enqueue(key);
        while (_seenAlertOrder.Count > MaxSeenAlerts)
        {
            _ = _seenCriticalAlerts.Remove(_seenAlertOrder.Dequeue());
        }

        return true;
    }
}
