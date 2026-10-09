using System.Diagnostics;
using System.Globalization;
using System.Text;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Services;

/// <summary>How loud a toast is; the tray maps it to its balloon icon.</summary>
internal enum ToastLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One toast about findings. <b>Severity and target only</b>: nothing from a finding's payload, evidence or details ever
/// reaches one (the Mac's rule, <c>AppState.refreshAlerts</c>), because a toast is shown on the lock screen and kept in the
/// notification centre.
/// </summary>
/// <param name="Title">Short, for the toast's heading.</param>
/// <param name="Body">The sentence under it.</param>
/// <param name="Floor">The lowest severity the toast announces (CRITICAL when only those, HIGH when any HIGH is in): what clicking it opens Alerts on.</param>
/// <param name="Level">Error when a CRITICAL is among them, else warning.</param>
internal sealed record AlertToast(string Title, string Body, AuditSeverity Floor, ToastLevel Level)
{
    /// <summary>The navigation payload a click sends to the Alerts panel: this severity and above.</summary>
    public AlertsFilter Filter => new(Floor);
}

/// <summary>Why a batch of findings is being announced; it only changes the wording.</summary>
internal enum AlertToastPhase
{
    /// <summary>The first look after the app started: what arrived while it was closed.</summary>
    Launch,

    /// <summary>A look while running: what arrived since the last one.</summary>
    Live,

    /// <summary>After "Reset seen-alert history": everything outstanding, as if it were new.</summary>
    Backlog,
}

/// <summary>What <see cref="AlertToastPolicy.Decide"/> decided.</summary>
/// <param name="Toast">What to show, or null for nothing.</param>
/// <param name="HighWaterUnixNano">Where the persisted mark goes: the newest finding looked at, never lower than it was.</param>
internal readonly record struct AlertToastDecision(AlertToast? Toast, long HighWaterUnixNano);

/// <summary>
/// Decides what, if anything, to announce about the alert queue, from a window of it and the persisted notification settings.
/// Pure: no clock, no I/O, so every rule below is a unit test.
/// <para>
/// <b>The high-water mark</b> (<see cref="NotificationSettings.HighWaterUnixNano"/>) is the timestamp of the newest finding
/// already looked at. A finding is <i>new</i> when it is newer than the mark; the mark moves to the newest finding in the
/// window <i>whether or not it was announced</i>, so switching HIGH toasts on later does not announce yesterday's HIGHs,
/// and a restart repeats nothing. It is what makes findings that arrived while the app was closed announced once.
/// </para>
/// <para>
/// <b>Batched.</b> However many findings are new, there is one toast: a single one is told in full (severity and target), several
/// as a count. A burst of a hundred findings is one sentence, not a hundred balloons.
/// </para>
/// <para>
/// <b>A mark of zero</b> means nothing has ever been announced. At launch that is a fresh install, whose existing backlog is
/// history, not news, so the first look only sets the mark (no toast). <see cref="AlertToastPhase.Backlog"/> is the
/// exception on purpose: it is what the operator asks for by resetting the history.
/// </para>
/// </summary>
internal static class AlertToastPolicy
{
    /// <summary>The longest target a toast shows; the rest is cut with an ellipsis.</summary>
    public const int TargetLimit = 100;

    private const string ClosedSuffix = " while DefenseClaw was closed";
    private const string NoTarget = "Open Alerts for detail.";

    /// <summary>A timestamp as Unix nanoseconds (100 ns resolution, which is what a <see cref="DateTimeOffset"/> holds); zero for one that could not be read.</summary>
    public static long UnixNanos(DateTimeOffset at)
    {
        if (at == DateTimeOffset.MinValue)
        {
            return 0;
        }

        var nanos = (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
        return Math.Max(0, nanos);
    }

    /// <param name="window">The queue window (<see cref="AlertCounts.Newest"/> of a full read); any order.</param>
    /// <param name="settings">The toggles and the mark, as persisted.</param>
    /// <param name="phase">Why this look is being taken.</param>
    public static AlertToastDecision Decide(IReadOnlyList<AlertQueueItem> window, NotificationSettings settings, AlertToastPhase phase)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        var mark = settings.HighWaterUnixNano;
        var newest = mark;
        var critical = 0;
        var high = 0;
        AlertQueueItem? single = null;
        var singleNanos = 0L;

        foreach (var item in window)
        {
            var nanos = UnixNanos(item.Timestamp);
            if (nanos <= mark)
            {
                continue;
            }

            newest = Math.Max(newest, nanos);

            var wanted = item.Severity switch
            {
                AuditSeverity.Critical => settings.Critical,
                AuditSeverity.High => settings.High,
                _ => false,
            };
            if (!wanted)
            {
                continue;
            }

            if (item.Severity == AuditSeverity.Critical)
            {
                critical++;
            }
            else
            {
                high++;
            }

            if (single is null || nanos > singleNanos)
            {
                single = item;
                singleNanos = nanos;
            }
        }

        // A fresh install's first look: the backlog is history. Only the mark moves.
        if (phase == AlertToastPhase.Launch && mark == 0)
        {
            return new AlertToastDecision(null, newest);
        }

        if (critical + high == 0)
        {
            return new AlertToastDecision(null, newest);
        }

        var floor = high > 0 ? AuditSeverity.High : AuditSeverity.Critical;
        var level = critical > 0 ? ToastLevel.Error : ToastLevel.Warning;
        return new AlertToastDecision(Compose(phase, critical, high, single!, floor, level), newest);
    }

    private static AlertToast Compose(AlertToastPhase phase, int critical, int high, AlertQueueItem single, AuditSeverity floor, ToastLevel level)
    {
        switch (phase)
        {
            case AlertToastPhase.Launch:
                return new AlertToast("DefenseClaw", Counted(critical, high, "new ") + ClosedSuffix, floor, level);

            case AlertToastPhase.Backlog:
                return new AlertToast("Unacknowledged findings", Counted(critical, high, string.Empty) + " unacknowledged", floor, level);

            default:
                if (critical + high == 1)
                {
                    var severity = single.Severity.ToStoredValue();
                    return new AlertToast($"{severity} finding", TargetLine(single.Target), floor, level);
                }

                return new AlertToast("New findings", Counted(critical, high, "new ") + " — open Alerts for detail.", floor, level);
        }
    }

    /// <summary>"3 new CRITICAL findings", "1 new HIGH finding", "2 new CRITICAL and 5 new HIGH findings".</summary>
    private static string Counted(int critical, int high, string qualifier)
    {
        var parts = new List<string>(2);
        if (critical > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{critical} {qualifier}CRITICAL"));
        }

        if (high > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{high} {qualifier}HIGH"));
        }

        return string.Join(" and ", parts) + (critical + high == 1 ? " finding" : " findings");
    }

    /// <summary>"Target: C:\work\app.py", cut to <see cref="TargetLimit"/>; a line that sends the operator to Alerts when there is no target.</summary>
    internal static string TargetLine(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return NoTarget;
        }

        // One line, printable: a target is a path or a URL, and a toast is no place for a newline or a control character.
        var text = new StringBuilder(Math.Min(target.Length, TargetLimit + 1));
        var lastWasSpace = false;
        foreach (var ch in target)
        {
            var c = char.IsControl(ch) || char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ' && (lastWasSpace || text.Length == 0))
            {
                continue;
            }

            lastWasSpace = c == ' ';
            _ = text.Append(c);
            if (text.Length > TargetLimit)
            {
                break;
            }
        }

        var line = text.ToString().TrimEnd();
        if (line.Length == 0)
        {
            return NoTarget;
        }

        return line.Length > TargetLimit
            ? "Target: " + line[..(TargetLimit - 1)] + "…"
            : "Target: " + line;
    }
}

/// <summary>
/// Where a click on the balloon that is showing goes. A balloon is a native notification the app cannot attach a handler to: the
/// shell reports only "the balloon was clicked" (<c>TrayBalloonTipClicked</c>), for whichever one is on screen, which is the last
/// one shown. So each toast arms this with where it wants to go (or disarms it, for a toast that goes nowhere), and a click takes
/// the target once and asks the shell to go there.
/// </summary>
internal sealed class BalloonClickTarget
{
    private NavigationRequest? _target;

    /// <summary>The toast just shown: its target, or null for one that goes nowhere (which also drops the previous toast's).</summary>
    public void Arm(NavigationRequest? target) => Volatile.Write(ref _target, target);

    /// <summary>Whether a click would go anywhere now.</summary>
    public bool IsArmed => Volatile.Read(ref _target) is not null;

    /// <summary>A click: asks <paramref name="navigation"/> for the armed target, once. False when nothing was armed (a click on a toast that goes nowhere, or a second click).</summary>
    public bool Click(ShellNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        if (Interlocked.Exchange(ref _target, null) is not { } target)
        {
            return false;
        }

        navigation.Request(target);
        return true;
    }
}

/// <summary>A toast about the gateway going away or coming back.</summary>
internal sealed record GatewayToast(string Title, string Body, ToastLevel Level);

/// <summary>
/// Turns the gateway's state changes into "offline" and "recovered" toasts, but only for a gateway the app has actually seen
/// reachable (the Mac's <c>wasReachable</c>): a launch into an already-stopped gateway is not news, and neither is it coming
/// up, so neither toasts. The edges are: seen running, then stopped or degraded, then running again. The operator's own
/// start/stop/restart (<c>suppress</c>) keeps the state moving but says nothing, since that action has its own toast.
/// </summary>
internal sealed class GatewayToastTracker
{
    private enum Reach
    {
        Unknown,
        Reachable,
        Lost,
    }

    private Reach _reach = Reach.Unknown;

    /// <summary>True while a toast for the gateway coming back is owed: it was seen reachable, then lost.</summary>
    public bool IsLost => _reach == Reach.Lost;

    /// <param name="snapshot">The state just published.</param>
    /// <param name="suppress">True while the operator's own gateway action is running.</param>
    /// <param name="enabled">The "gateway" toggle in the notification settings, read now.</param>
    public GatewayToast? Observe(GatewaySnapshot snapshot, bool suppress, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        switch (snapshot.State)
        {
            case AppGatewayState.Running:
                {
                    var announce = _reach == Reach.Lost && enabled && !suppress;
                    _reach = Reach.Reachable;
                    return announce
                        ? new GatewayToast(
                            "DefenseClaw gateway recovered",
                            snapshot.ApiPort > 0
                                ? string.Create(CultureInfo.InvariantCulture, $"The gateway is reachable again on port {snapshot.ApiPort}.")
                                : "The gateway is reachable again.",
                            ToastLevel.Info)
                        : null;
                }

            case AppGatewayState.GatewayStopped or AppGatewayState.Degraded:
                {
                    if (_reach != Reach.Reachable)
                    {
                        return null;
                    }

                    _reach = Reach.Lost;
                    return enabled && !suppress
                        ? new GatewayToast(
                            "DefenseClaw gateway offline",
                            string.IsNullOrWhiteSpace(snapshot.Detail) ? "Lost contact with the gateway." : snapshot.Detail,
                            ToastLevel.Warning)
                        : null;
                }

            default:
                // Not installed, not initialized, a WSL relay, or no answer yet: none of these is the native gateway
                // coming or going, so the tracker stays where it was.
                return null;
        }
    }
}

/// <summary>
/// Watches the alert queue and announces new CRITICAL / HIGH findings through <c>show</c> (the tray's balloon), as the settings
/// allow, with the decisions made by <see cref="AlertToastPolicy"/> and the mark persisted in the settings store.
/// <para>
/// <b>When it looks.</b> On <see cref="AlertCountsService.Changed"/> (the service's 30 s cadence, and anything that refreshes
/// it), which includes the first read after start: that look is the <see cref="AlertToastPhase.Launch"/> one, so findings that
/// arrived while the app was closed are announced once, as one toast. Looks are taken one at a time.
/// </para>
/// <para>
/// <b>What it reads.</b> The full queue window from <see cref="AppServices.AlertQueue"/> (the counts service keeps only its newest
/// 50), so a batch is counted exactly up to the window's 500. A database that predates the queue schema falls back to the rows the
/// counts service already holds from the gateway's <c>/alerts</c> — no extra request.
/// </para>
/// </summary>
internal sealed class AlertNotifier : IDisposable
{
    private readonly AppServices _services;
    private readonly Action<AlertToast> _show;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _launchDone;
    private bool _disposed;

    /// <param name="services">The composition: the queue, the counts and the settings.</param>
    /// <param name="show">Shows a toast. Called on the thread the look resumes on (the UI thread when started from it); the tray's balloon needs that.</param>
    public AlertNotifier(AppServices services, Action<AlertToast> show)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _show = show ?? throw new ArgumentNullException(nameof(show));
    }

    /// <summary>Starts watching: subscribes to the counts (which starts them), and looks at once if they already have data.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _services.AlertCounts.Changed += OnCountsChanged;

        if (_services.AlertCounts.HasData)
        {
            StartLook();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _services.AlertCounts.Changed -= OnCountsChanged;
        }

        _gate.Dispose();
    }

    /// <summary>
    /// Why the last look failed ("TimeoutException: …"), or null when the last one worked or none has run. A failed look is only traced and
    /// answers false like "nothing to say", so a test that expected a toast can say why there was none (CUST-323).
    /// </summary>
    internal string? LastFailure { get; private set; }

    /// <summary>
    /// Takes one look now and announces what it finds. Returns true when a toast was shown, false when there was nothing to say
    /// (or the queue could not be read, which is traced and tried again at the next change).
    /// </summary>
    public Task<bool> LookAsync() => LookAsync(resetFirst: false);

    /// <summary>
    /// "Reset seen-alert history": the mark goes back to zero and the next look treats everything outstanding (CRITICAL and HIGH, as
    /// the toggles allow) as new — one batched toast. Returns true when that toast was shown, false when there was nothing
    /// outstanding to announce (the mark is reset either way).
    /// </summary>
    public Task<bool> ResetSeenHistoryAsync() => LookAsync(resetFirst: true);

    private void OnCountsChanged(object? sender, AlertCountsChangedEventArgs e) => StartLook();

    /// <summary>Fire and forget: <see cref="LookAsync(bool)"/> catches and traces everything itself, and resumes on the caller's context (the UI thread's, when <c>Changed</c> is raised there).</summary>
    private void StartLook() => _ = LookAsync(resetFirst: false);

    private async Task<bool> LookAsync(bool resetFirst)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (_disposed)
            {
                return false;
            }

            if (resetFirst)
            {
                SetMark(0, allowLower: true);
            }

            var window = await ReadWindowAsync().ConfigureAwait(true);
            if (window is null || _disposed)
            {
                return false;
            }

            LastFailure = null;

            var settings = _services.Settings.Current.Notifications;
            var phase = resetFirst ? AlertToastPhase.Backlog : _launchDone ? AlertToastPhase.Live : AlertToastPhase.Launch;
            var decision = AlertToastPolicy.Decide(window, settings, phase);

            // The mark first: a toast that cannot be repeated is better than one that is repeated after a crash.
            SetMark(decision.HighWaterUnixNano, allowLower: false);
            if (!resetFirst)
            {
                _launchDone = true;
            }

            if (decision.Toast is not { } toast)
            {
                return false;
            }

            _show(toast);
            return true;
        }
#pragma warning disable CA1031 // A look that fails must not take the tray down; the next change looks again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"notifications: looking at the alert queue failed: {ex.GetType().Name}: {ex.Message}");
            LastFailure = $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ");
            return false;
        }
        finally
        {
            try
            {
                _ = _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose ran while this look was reading (the tray is exiting): the gate is gone, nobody is waiting on it, and a look that
                // runs fire-and-forget has no caller for this to reach - it would only be an unobserved task exception.
            }
        }
    }

    /// <summary>The queue window to decide from, or null when it cannot be read right now.</summary>
    private async Task<IReadOnlyList<AlertQueueItem>?> ReadWindowAsync()
    {
        var counts = _services.AlertCounts.Current;

        // A database that predates the queue schema is counted from the gateway's own list, which the service already holds.
        if (counts.Source == AlertCountsSource.Gateway)
        {
            return counts.Newest;
        }

        var result = await _services.AlertQueue.ReadAsync(AlertQueueReader.DefaultWindowLimit).ConfigureAwait(true);
        return result.Status switch
        {
            AlertQueueStatus.Ok => result.Counts.Newest,
            AlertQueueStatus.NoDatabase => Array.Empty<AlertQueueItem>(),
            _ => counts.Newest,
        };
    }

    private void SetMark(long value, bool allowLower)
    {
        _ = _services.Settings.Update(s =>
            s.Notifications.HighWaterUnixNano == value || (!allowLower && s.Notifications.HighWaterUnixNano > value)
                ? s
                : s with { Notifications = s.Notifications with { HighWaterUnixNano = value } });
    }
}
