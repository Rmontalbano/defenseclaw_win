using System.Diagnostics;
using System.Windows;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Services;

/// <summary>What a "Start gateway" button does with the current state: drawn or not, pressable or not, and its tooltip.</summary>
/// <param name="Visible">The gateway is down and a start makes sense to offer (stopped, degraded, or not yet known); false while it runs, holds a WSL relay, or is not installed.</param>
/// <param name="Enabled">The gate (<see cref="GatewayControl.Availability(GatewayAction, GatewaySnapshot, InstallationGuard)"/>) lets the start through.</param>
/// <param name="Tip">What the start does, or why it is off (the installation's sentence first).</param>
internal readonly record struct GatewayStartOffer(bool Visible, bool Enabled, string Tip)
{
    public static GatewayStartOffer Hidden { get; } = new(false, false, string.Empty);
}

/// <summary>A result line for a toast: the tray shows it, other surfaces ignore it (the Activity panel has the run).</summary>
internal sealed record GatewayLifecycleNotice(string Title, string Message, ToastLevel Level);

/// <summary>
/// The one implementation of "start / stop / restart the gateway from the shell": the availability gate, the review the operator must confirm, the
/// run through the CLI runner (so it is in Activity), the monitor poll that follows. The tray menu, the command palette and every "Start gateway"
/// button (the dashboard's degraded banner, the Overview's attention row, the tray flyout) call this and nothing else, so they cannot disagree
/// about when it is offered, what it reviews or what it runs. Nothing runs without the review confirming.
/// </summary>
internal sealed class GatewayLifecycle
{
    private readonly Func<GatewaySnapshot> _snapshot;
    private readonly InstallationGuard _installation;
    private readonly Func<IReadOnlyList<string>, Task<CliInvocation>> _run;
    private readonly Func<Task> _refresh;
    private readonly Action _markUserStopped;
    private int _running;

    /// <param name="snapshot">The gateway state the gate is judged against (the monitor's current one).</param>
    /// <param name="installation">Whether the installation may be changed; its sentence comes before any other reason.</param>
    /// <param name="run">Runs <c>defenseclaw-gateway</c> with an argv: <c>CliRunner.RunGatewayAsync</c>, or a fake. Never a real start in a test.</param>
    /// <param name="refresh">One monitor poll, so the headline follows the run.</param>
    /// <param name="markUserStopped">Remembers that the operator stopped the gateway, which vetoes the automatic start for the session.</param>
    public GatewayLifecycle(
        Func<GatewaySnapshot> snapshot,
        InstallationGuard installation,
        Func<IReadOnlyList<string>, Task<CliInvocation>> run,
        Func<Task> refresh,
        Action markUserStopped)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _markUserStopped = markUserStopped ?? throw new ArgumentNullException(nameof(markUserStopped));
    }

    /// <summary>The service for the running app: the app's monitor, installation guard and runner.</summary>
    internal static GatewayLifecycle Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new GatewayLifecycle(
            () => services.Monitor.Current,
            services.Installation,
            argv => services.Cli.RunGatewayAsync(argv),
            async () => _ = await services.Monitor.RefreshAsync().ConfigureAwait(true),
            () => services.GatewayAutoStart.MarkUserStopped());
    }

    /// <summary>Test seam: shows the review and says whether the operator confirmed it (default: the gateway-action dialog window).</summary>
    internal Func<Window?, GatewayAction, bool> Confirmer { get; set; } = static (owner, action) => GatewayActionDialog.Confirm(owner, action);

    /// <summary>Raised with the result line of a run or a refusal; the tray turns it into a toast.</summary>
    public event EventHandler<GatewayLifecycleNotice>? Notice;

    /// <summary>Raised when the answer to <see cref="IsRunning"/> changes (a run began or ended); a button re-reads its offer.</summary>
    public event EventHandler? OfferChanged;

    /// <summary>True while a confirmed action is running; a second request is ignored.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>The gateway state the gate is judged against: the monitor's current snapshot.</summary>
    public GatewaySnapshot Snapshot => _snapshot();

    /// <summary>The offer for <see cref="Snapshot"/>.</summary>
    public GatewayStartOffer StartOffer() => StartOffer(_snapshot(), _installation);

    /// <summary>
    /// The offer for <paramref name="snapshot"/>: drawn while the gateway is stopped, degraded or not yet known (the states
    /// <see cref="GatewayControl.Availability(GatewayAction, GatewaySnapshot)"/> lets a start through for), not while it runs, not for a WSL relay
    /// (a native start would run a second gateway; the WSL banner says what to do) and not when there is no install to start. Pressable when the gate
    /// says so, with the installation's sentence as the tooltip before any other reason.
    /// </summary>
    public static GatewayStartOffer StartOffer(GatewaySnapshot snapshot, InstallationGuard installation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(installation);

        if (snapshot.State is not (AppGatewayState.Unknown or AppGatewayState.GatewayStopped or AppGatewayState.Degraded)
            || snapshot.WslGatewayDetected)
        {
            return GatewayStartOffer.Hidden;
        }

        var (allowed, reason) = GatewayControl.Availability(GatewayAction.Start, snapshot, installation);
        return new GatewayStartOffer(
            Visible: true,
            Enabled: allowed,
            Tip: allowed ? GatewayControl.Summary(GatewayAction.Start) + " Asks for confirmation first." : reason ?? "Not available right now.");
    }

    /// <summary>
    /// Start, stop or restart: the gate, the review, the run, the poll. Returns without doing anything when the gate says no (the reason is raised as
    /// a notice), when the operator declines the review, or when another action is running.
    /// </summary>
    /// <param name="action">Start, stop or restart.</param>
    /// <param name="owner">The window to centre the review over; the visible dashboard when null, else the screen.</param>
    public async Task RunAsync(GatewayAction action, Window? owner = null)
    {
        var title = GatewayControl.Title(action);

        var (allowed, reason) = GatewayControl.Availability(action, _snapshot(), _installation);
        if (!allowed)
        {
            RaiseNotice(title, reason ?? "Not available right now.", ToastLevel.Info);
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        OfferChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var argv = GatewayControl.Argv(action);

            if (CommandTiers.Classify(argv) != CommandTier.ReadOnly && !Confirmer(owner ?? VisibleDashboard(), action))
            {
                return;
            }

            // A confirmed Stop is the operator's word for the rest of the session: no automatic start follows it.
            if (action == GatewayAction.Stop)
            {
                _markUserStopped();
            }

            var invocation = await _run(argv).ConfigureAwait(true);
            RaiseNotice(
                title,
                invocation.ExitCode == 0
                    ? GatewayControl.SucceededText(action)
                    : invocation.ExitCode is { } code
                        ? $"Exit code {code} — see the Activity panel for output."
                        : $"{invocation.FailureReason ?? "The command did not finish"} — see the Activity panel.",
                invocation.ExitCode == 0 ? ToastLevel.Info : ToastLevel.Warning);
            await _refresh().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Any failure of the run or the poll is reported as a notice; this is called from button handlers.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Gateway {GatewayControl.Verb(action)} failed: {ex.Message}");
            RaiseNotice("Gateway control failed", ex.Message, ToastLevel.Error);
        }
#pragma warning restore CA1031
        finally
        {
            _ = Interlocked.Exchange(ref _running, 0);
            OfferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseNotice(string title, string message, ToastLevel level) =>
        Notice?.Invoke(this, new GatewayLifecycleNotice(title, message, level));

    /// <summary>The dashboard when it is showing: a review opened from the tray flyout or a banner is centred over it. Null (the screen) otherwise.</summary>
    private static Window? VisibleDashboard() =>
        Application.Current is { } app && app.Dispatcher.CheckAccess()
            ? app.Windows.OfType<MainWindow>().FirstOrDefault(static w => w.IsVisible && w.WindowState != WindowState.Minimized)
            : null;
}
