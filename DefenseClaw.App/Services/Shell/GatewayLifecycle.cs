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
    private readonly IGatewayAdministrator? _administrator;
    private int _running;

    /// <param name="administrator">
    /// "Run gateway as administrator" (null in a composition without it): while its mode is on, start and restart go through the helper after a
    /// review of what will run elevated, and stop tries the ordinary graceful stop first. The ordinary path is untouched when it is off.
    /// </param>
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
        Action markUserStopped,
        IGatewayAdministrator? administrator = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _markUserStopped = markUserStopped ?? throw new ArgumentNullException(nameof(markUserStopped));
        _administrator = administrator;
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
            () => services.GatewayAutoStart.MarkUserStopped(),
            services.GatewayAdmin);
    }

    /// <summary>Test seam: shows the review and says whether the operator confirmed it (default: the gateway-action dialog window).</summary>
    internal Func<Window?, GatewayAction, bool> Confirmer { get; set; } = static (owner, action) => GatewayActionDialog.Confirm(owner, action);

    /// <summary>Test seam: the review of what will run elevated (default: the same window, around the shared review control).</summary>
    internal Func<Window?, CommandReview, bool> ReviewConfirmer { get; set; } = static (owner, review) => GatewayActionDialog.Confirm(owner, review);

    /// <summary>Test seam: the wait between looks at whether an ordinary stop worked (default: a real second).</summary>
    internal Func<TimeSpan, Task> Delay { get; set; } = static span => Task.Delay(span);

    /// <summary>How many times, a second apart, an ordinary stop is waited on before it is called failed and a stop as administrator is offered.</summary>
    internal int StopSettleAttempts { get; set; } = 8;

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
        var administrator = _administrator is { IsActive: true } active ? active : null;
        var title = administrator is null ? GatewayControl.Title(action) : GatewayControl.AdministratorTitle(action);

        var (allowed, reason) = GatewayControl.Availability(action, _snapshot(), _installation);
        if (!allowed)
        {
            RaiseNotice(title, reason ?? "Not available right now.", ToastLevel.Info);
            return;
        }

        // The ordinary path with files that an elevated gateway left owned by Administrators: 1.x would refuse them, so the app says why first.
        if (administrator is null && _administrator?.NormalPathBlockedReason(action) is { } blocked)
        {
            RaiseNotice(title, blocked, ToastLevel.Info);
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        OfferChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (administrator is not null)
            {
                await RunAsAdministratorAsync(action, owner, administrator, title).ConfigureAwait(true);
                return;
            }

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

    // ---- administrator mode ---------------------------------------------------------------------------------------

    /// <summary>
    /// "Prepare administrator copy": the same review and step runner as a start, for the one step that puts the helper and a copy of the
    /// installed gateway in Program Files. Starts and stops nothing.
    /// </summary>
    public Task PrepareAdministratorCopyAsync(Window? owner = null) =>
        RunStandalonePlanAsync("Prepare administrator copy", static (admin, ct) => admin.PlanPrepareAsync(ct), owner);

    /// <summary>"Repair ownership": the reviewed step that gives files an elevated gateway made back to the user.</summary>
    public Task RepairOwnershipAsync(Window? owner = null) =>
        RunStandalonePlanAsync("Repair ownership", static (admin, ct) => admin.PlanRepairOwnershipAsync(ct), owner);

    private async Task RunStandalonePlanAsync(string title, Func<IGatewayAdministrator, CancellationToken, Task<AdminPlan>> planner, Window? owner)
    {
        if (_administrator is null)
        {
            RaiseNotice(title, "Run gateway as administrator is not available here.", ToastLevel.Info);
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        OfferChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var plan = await planner(_administrator, CancellationToken.None).ConfigureAwait(true);
            if (await RunPlanAsync(plan, title, owner, _administrator).ConfigureAwait(true))
            {
                await _refresh().ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Called from button handlers: any failure is a notice.
        catch (Exception ex)
        {
            Trace.TraceWarning($"{title} failed: {ex.Message}");
            RaiseNotice(title, ex.Message, ToastLevel.Error);
        }
#pragma warning restore CA1031
        finally
        {
            _ = Interlocked.Exchange(ref _running, 0);
            OfferChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunAsAdministratorAsync(GatewayAction action, Window? owner, IGatewayAdministrator administrator, string title)
    {
        if (action == GatewayAction.Stop)
        {
            await StopWithFallbackAsync(owner, administrator, title).ConfigureAwait(true);
            return;
        }

        var plan = await administrator.PlanAsync(action, CancellationToken.None).ConfigureAwait(true);
        if (await RunPlanAsync(plan, title, owner, administrator).ConfigureAwait(true))
        {
            await _refresh().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Stop in administrator mode: the ordinary graceful stop first (the same review, the same run), a short wait for the gateway to go; only
    /// if it is still there is a stop as administrator planned, reviewed and asked for. A gateway the ordinary stop can reach never costs a prompt.
    /// </summary>
    private async Task StopWithFallbackAsync(Window? owner, IGatewayAdministrator administrator, string title)
    {
        if (!Confirmer(owner ?? VisibleDashboard(), GatewayAction.Stop))
        {
            return;
        }

        _markUserStopped();
        var invocation = await _run(GatewayControl.Argv(GatewayAction.Stop)).ConfigureAwait(true);
        await _refresh().ConfigureAwait(true);

        for (var attempt = 0; _snapshot().IsRunning && attempt < StopSettleAttempts; attempt++)
        {
            await Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            await _refresh().ConfigureAwait(true);
        }

        if (!_snapshot().IsRunning)
        {
            RaiseNotice(
                GatewayControl.Title(GatewayAction.Stop),
                invocation.ExitCode == 0 ? GatewayControl.SucceededText(GatewayAction.Stop) : "The gateway is no longer running.",
                ToastLevel.Info);
            return;
        }

        RaiseNotice(title, "The ordinary stop did not stop the gateway (it may be running with administrator rights). Asking to stop it as administrator.", ToastLevel.Info);
        var plan = await administrator.PlanAsync(GatewayAction.Stop, CancellationToken.None).ConfigureAwait(true);
        if (await RunPlanAsync(plan, title, owner, administrator).ConfigureAwait(true))
        {
            await _refresh().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The review and the steps of one plan. Nothing runs unless the plan was not refused, the installation may be changed, and the operator
    /// confirmed the review; each step is one prompt, and a step that did not succeed ends the run. True when every step succeeded.
    /// </summary>
    private async Task<bool> RunPlanAsync(AdminPlan plan, string title, Window? owner, IGatewayAdministrator administrator)
    {
        if (plan.Refusal is { } refusal)
        {
            RaiseNotice(title, refusal, ToastLevel.Info);
            return false;
        }

        var review = plan.Review!.GuardedBy(_installation);
        if (review.BlockedReason is { } blocked)
        {
            RaiseNotice(title, blocked, ToastLevel.Info);
            return false;
        }

        if (!ReviewConfirmer(owner ?? VisibleDashboard(), review))
        {
            return false;
        }

        var last = string.Empty;
        foreach (var step in plan.Steps)
        {
            var outcome = await administrator.ExecuteAsync(plan, step, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Succeeded)
            {
                RaiseNotice(title, outcome.Message, outcome.Cancelled ? ToastLevel.Info : ToastLevel.Warning);
                await _refresh().ConfigureAwait(true);
                return false;
            }

            last = outcome.Message;
        }

        RaiseNotice(title, last, ToastLevel.Info);
        return true;
    }

    private void RaiseNotice(string title, string message, ToastLevel level) =>
        Notice?.Invoke(this, new GatewayLifecycleNotice(title, message, level));

    /// <summary>The dashboard when it is showing: a review opened from the tray flyout or a banner is centred over it. Null (the screen) otherwise.</summary>
    private static Window? VisibleDashboard() =>
        Application.Current is { } app && app.Dispatcher.CheckAccess()
            ? app.Windows.OfType<MainWindow>().FirstOrDefault(static w => w.IsVisible && w.WindowState != WindowState.Minimized)
            : null;
}
