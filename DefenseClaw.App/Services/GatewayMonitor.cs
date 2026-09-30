using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.Services;

/// <summary>
/// The shell's single notion of "how is DefenseClaw doing right now".
/// <para>
/// Deliberately coarser than <see cref="InstallState"/>: the UI cares about which banner
/// and which tray colour to show, not about the detection mechanics.
/// </para>
/// </summary>
public enum AppGatewayState
{
    /// <summary>Before the first poll completes.</summary>
    Unknown = 0,

    /// <summary>Native sidecar answering <c>/health</c> cleanly.</summary>
    Running,

    /// <summary>Initialized, but nothing is listening on the API port.</summary>
    GatewayStopped,

    /// <summary>
    /// Something answered but not cleanly — a 5xx, a malformed body, or an unauthorized
    /// <c>/health</c>. Reads fall back to SQLite and the log tail.
    /// </summary>
    Degraded,

    /// <summary>The API port is owned by <c>wslrelay.exe</c>: a WSL gateway, not the native one.</summary>
    WslGatewayDetected,

    /// <summary>No <c>defenseclaw.exe</c> on PATH or in the installer's bin directory.</summary>
    NotInstalled,

    /// <summary>Binaries present, <c>~/.defenseclaw/config.yaml</c> absent.</summary>
    NotInitialized,
}

/// <summary>
/// Everything the UI is allowed to know about the current gateway state, captured at one
/// instant. Immutable: hand it across threads freely.
/// </summary>
public sealed record GatewaySnapshot
{
    /// <summary>The command the not-initialized banner offers. The app never runs it.</summary>
    public const string InitCommand = "defenseclaw init";

    /// <summary>The command the gateway-stopped banner offers. The app never runs it.</summary>
    public const string StartGatewayCommand = "defenseclaw-gateway start";

    public static readonly GatewaySnapshot Initial = new()
    {
        Detail = "Waiting for the first gateway poll…",
    };

    public AppGatewayState State { get; init; } = AppGatewayState.Unknown;

    /// <summary>Human-readable explanation, straight from <see cref="InstallStatus.Detail"/>.</summary>
    public string Detail { get; init; } = string.Empty;

    public InstallState? Install { get; init; }

    public bool WslGatewayDetected { get; init; }

    public PortOwner? PortOwner { get; init; }

    /// <summary>
    /// The full <c>/health</c> payload. <b>Deliberately not part of
    /// <see cref="RendersSameAs"/></b>: it carries uptime and the per-connector request
    /// counters, which change on nearly every poll of a box with a busy agent, and nothing
    /// that subscribes to <see cref="GatewayMonitor.StateChanged"/> renders them. The one
    /// consumer that does (the Overview panel) reads it from
    /// <see cref="GatewayMonitor.PollCompleted"/> while it is on screen.
    /// </summary>
    public GatewayHealth? Health { get; init; }

    /// <summary>Raw outcome of the last <c>/health</c> call.</summary>
    public GatewayStatus? HealthStatus { get; init; }

    /// <summary>e.g. <c>0.8.7</c>, from <c>/health</c> provenance.</summary>
    public string? BinaryVersion { get; init; }

    public int ApiPort { get; init; }

    public string? CliPath { get; init; }

    /// <summary>Most recent alerts, refreshed on the slower alert cadence.</summary>
    public IReadOnlyList<GatewayAlert> RecentAlerts { get; init; } = Array.Empty<GatewayAlert>();

    public int AlertCount { get; init; }

    public int CriticalAlertCount { get; init; }

    /// <summary>
    /// Why the alert count is missing, when it is. <c>NotConnected</c> and
    /// <c>Unauthorized</c> are informational states, never errors.
    /// </summary>
    public string? AlertsUnavailable { get; init; }

    /// <summary>
    /// When the last <i>successful</i> <c>/alerts</c> fetch completed; null if there has never
    /// been one (or the monitored port changed since). This is not <see cref="PolledAt"/>:
    /// <c>/alerts</c> rides its own 30 s cadence (<see cref="GatewayMonitor.AlertInterval"/>)
    /// and only <see cref="GatewayMonitor.RefreshAlertsNowAsync"/> bypasses it, so
    /// <see cref="PolledAt"/> ticks every five seconds while the list it sits next to may be
    /// nearly half a minute old. A surface that says how fresh the alert list is must use this.
    /// <para>
    /// It survives a later failed fetch — the list itself does not (it is emptied and
    /// <see cref="AlertsUnavailable"/> says why) — so a consumer can still say how old the last
    /// good data is. <b>Deliberately not part of <see cref="RendersSameAs"/></b>, for the same
    /// reason as <see cref="PolledAt"/>: it moves on every alert fetch, an idle box would raise
    /// <see cref="GatewayMonitor.StateChanged"/> every 30 s for it, and no always-alive
    /// subscriber (tray, shell, flyout) renders it. Whatever prints it subscribes to
    /// <see cref="GatewayMonitor.PollCompleted"/>, or uses the snapshot returned by
    /// <see cref="GatewayMonitor.RefreshAlertsNowAsync"/>.
    /// </para>
    /// </summary>
    public DateTimeOffset? AlertsFetchedAt { get; init; }

    /// <summary>Connectors named by config.yaml, unioned with the ones <c>/health</c> reports.</summary>
    public IReadOnlyList<string> ActiveConnectors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When this snapshot was taken. Changes on every poll, so it is excluded from
    /// <see cref="RendersSameAs"/>; a surface that prints it subscribes to
    /// <see cref="GatewayMonitor.PollCompleted"/>.
    /// </summary>
    public DateTimeOffset PolledAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Consecutive unreachable polls; drives the poll backoff. Not rendered anywhere.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Set when the <c>env</c> block of Claude Code's settings.json overrides the hook fail
    /// mode the gateway believes it is running. Null when the two agree, or when there was
    /// nothing to compare. The hook obeys the env var, so a non-null value here is the
    /// effective behavior regardless of what config.yaml or <c>/status</c> claim.
    /// </summary>
    public FailModeDrift? FailModeDrift { get; init; }

    /// <summary>True while the gateway cannot serve reads and panels must fall back.</summary>
    public bool IsDegraded =>
        State is AppGatewayState.GatewayStopped or AppGatewayState.Degraded or AppGatewayState.NotInstalled;

    public bool IsRunning => State == AppGatewayState.Running;

    /// <summary>True when the last alert poll saw a CRITICAL severity.</summary>
    public bool HasCriticalAlert => CriticalAlertCount > 0;

    public string ConnectorSummary =>
        ActiveConnectors.Count == 0 ? "none" : string.Join(", ", ActiveConnectors);

    public string StateLabel => State switch
    {
        AppGatewayState.Running => "Running",
        AppGatewayState.GatewayStopped => "Gateway stopped",
        AppGatewayState.Degraded => "Degraded",
        AppGatewayState.WslGatewayDetected => "WSL gateway detected",
        AppGatewayState.NotInstalled => "Not installed",
        AppGatewayState.NotInitialized => "Not initialized",
        _ => "Checking…",
    };

    /// <summary>
    /// The gate behind <see cref="GatewayMonitor.StateChanged"/>: true when
    /// <paramref name="other"/> would make every StateChanged subscriber render exactly what
    /// it already shows.
    /// <para>
    /// <b>Compared</b> — everything the tray, the shell, the flyout, the Alerts panel and the
    /// Setup panel read: <see cref="State"/>, <see cref="Detail"/>, <see cref="Install"/>,
    /// <see cref="HealthStatus"/>, <see cref="WslGatewayDetected"/>, <see cref="PortOwner"/>,
    /// <see cref="ApiPort"/>, <see cref="CliPath"/>, <see cref="BinaryVersion"/>,
    /// <see cref="AlertCount"/>, <see cref="CriticalAlertCount"/>,
    /// <see cref="AlertsUnavailable"/>, <see cref="FailModeDrift"/>, the ordered
    /// <see cref="ActiveConnectors"/> and the alert list itself (see
    /// <see cref="AlertsEquivalent"/>). The tray's toasts key off <see cref="State"/> and the
    /// ids of the CRITICAL alerts in <see cref="RecentAlerts"/> (it announces each id once, so
    /// a new CRITICAL that merely displaces an old one from the window still counts), and it
    /// treats <see cref="AlertsUnavailable"/> as "no list to read"; all three are in this list,
    /// so no toast-worthy transition can be swallowed.
    /// </para>
    /// <para>
    /// <b>Not compared</b> — <see cref="PolledAt"/>, <see cref="AlertsFetchedAt"/> and
    /// <see cref="ConsecutiveFailures"/> (change every poll or every alert fetch, and the
    /// failure count only steers the backoff), and
    /// <see cref="Health"/> (uptime, connector counters and last-activity stamps change on
    /// nearly every poll of a busy box, and only the Overview panel renders them). Anything
    /// that does render one of those must subscribe to
    /// <see cref="GatewayMonitor.PollCompleted"/> instead. The direction of the risk is
    /// deliberate: a field added here later that is forgotten in this method yields a
    /// stale-until-something-else-changes surface, so a new snapshot field that any
    /// StateChanged subscriber reads must be added below.
    /// </para>
    /// </summary>
    public bool RendersSameAs(GatewaySnapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return State == other.State &&
               Install == other.Install &&
               HealthStatus == other.HealthStatus &&
               WslGatewayDetected == other.WslGatewayDetected &&
               ApiPort == other.ApiPort &&
               AlertCount == other.AlertCount &&
               CriticalAlertCount == other.CriticalAlertCount &&
               string.Equals(Detail, other.Detail, StringComparison.Ordinal) &&
               string.Equals(BinaryVersion, other.BinaryVersion, StringComparison.Ordinal) &&
               string.Equals(CliPath, other.CliPath, StringComparison.Ordinal) &&
               string.Equals(AlertsUnavailable, other.AlertsUnavailable, StringComparison.Ordinal) &&
               EqualityComparer<PortOwner?>.Default.Equals(PortOwner, other.PortOwner) &&
               EqualityComparer<FailModeDrift?>.Default.Equals(FailModeDrift, other.FailModeDrift) &&
               ActiveConnectors.SequenceEqual(other.ActiveConnectors, StringComparer.Ordinal) &&
               AlertsEquivalent(RecentAlerts, other.RecentAlerts);
    }

    /// <summary>
    /// True when two alert lists show the same findings in the same order. Compares the
    /// fields the list, the tiles and the tray render (id, timestamp, severity, action,
    /// target, details, rule id, title) rather than the structured attribute bag: a finding
    /// with a given id is an immutable audit row, so the bag cannot differ when those match.
    /// A reference-equal pair short-circuits, which is the common case because
    /// <see cref="GatewayMonitor"/> keeps the previous list when a fresh <c>/alerts</c>
    /// answer is equivalent to it.
    /// </summary>
    internal static bool AlertsEquivalent(IReadOnlyList<GatewayAlert> left, IReadOnlyList<GatewayAlert> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!SameAlert(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAlert(GatewayAlert a, GatewayAlert b) =>
        ReferenceEquals(a, b) ||
        (a.Timestamp == b.Timestamp &&
         string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
         string.Equals(a.Severity, b.Severity, StringComparison.Ordinal) &&
         string.Equals(a.Action, b.Action, StringComparison.Ordinal) &&
         string.Equals(a.Target, b.Target, StringComparison.Ordinal) &&
         string.Equals(a.Details, b.Details, StringComparison.Ordinal) &&
         string.Equals(a.RuleId, b.RuleId, StringComparison.Ordinal) &&
         string.Equals(a.Title, b.Title, StringComparison.Ordinal));
}

public sealed class GatewaySnapshotEventArgs : EventArgs
{
    public GatewaySnapshotEventArgs(GatewaySnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public GatewaySnapshot Snapshot { get; }
}

/// <summary>
/// Polls the sidecar and turns four separate Core probes — install detection, port
/// ownership, <c>/health</c> and <c>/alerts</c> — into one <see cref="GatewaySnapshot"/>
/// and two events: <see cref="StateChanged"/> and <see cref="PollCompleted"/>.
/// <para>
/// <b>Every piece of gateway-derived UI state flows through here.</b> Panels and the tray
/// must subscribe to this rather than calling <c>GetHealthAsync</c> on their own timers:
/// one poller keeps the audit trail (and DefenseClaw's own request counters) honest.
/// </para>
/// <para>
/// Cadence: <see cref="FastInterval"/> normally, dropping to <see cref="SlowInterval"/>
/// after <see cref="FailuresBeforeBackoff"/> consecutive unreachable polls — a stopped
/// gateway should not cost a connection attempt every five seconds all day. Alerts are on
/// their own <see cref="AlertInterval"/> because <c>/alerts</c> is far more expensive
/// than <c>/health</c>. The cadence is the same whether or not the dashboard is open: the
/// tray's toasts (a new CRITICAL, the gateway going away) depend on it.
/// </para>
/// <para>
/// <b>Two events, two audiences.</b> <see cref="StateChanged"/> is the low-volume one — it
/// fires only when the new snapshot differs from the last published one in a way
/// <see cref="GatewaySnapshot.RendersSameAs"/> counts, so an idle box produces close to
/// none. It serves the surfaces that are always alive: the tray, the shell, the flyout.
/// <see cref="PollCompleted"/> fires after every poll and exists for the volatile detail
/// (uptime, connector counters, "polled at"); it costs nothing while nothing is subscribed,
/// so a panel subscribes only while it is on screen.
/// </para>
/// <para>
/// <b>One poll at a time.</b> The loop, <see cref="RefreshAsync"/> and
/// <see cref="RefreshAlertsNowAsync"/> all go through <c>_pollGate</c>. <c>PollAsync</c>
/// keeps its bookkeeping (alert and status throttles, cached alerts, cached hook contract) in
/// plain fields, and two interleaved polls could pair one poll's alert list with the other's
/// "unavailable" reason.
/// </para>
/// <para>
/// <b>One endpoint per poll.</b> A poll reads <see cref="AppServices.Endpoint"/> once and uses
/// that port and that client for install detection, <c>/health</c>, <c>/alerts</c> and
/// <c>/status</c> alike. <c>gateway.api_port</c> can change while the app runs; sampling the
/// port and the client separately would inspect one port and probe another. When the port
/// changes between polls, everything cached from the old gateway (alerts, hook contract,
/// throttles) is dropped — it described a different process.
/// </para>
/// <para>
/// <b>One <c>/health</c> GET per poll.</b> The install detector performs the probe and hands
/// the full result back on <see cref="InstallStatus.Health"/>; the monitor only fetches one
/// itself in the states where detection stops before probing (not installed, not
/// initialized), which is when a WSL relay can still be answering.
/// </para>
/// <para>
/// <b>The token goes to the gateway only.</b> <c>/alerts</c> and <c>/status</c> are polled, with
/// the bearer token, only when the port's owner is <c>defenseclaw-gateway</c> from the install
/// directory (<see cref="InstallStatus.OwnerTrust"/>) and <c>/health</c> parsed. A WSL relay or
/// any other listener is read through <c>/health</c> alone, and the alerts note says why. See
/// <see cref="GatewayPeerVerifier"/>.
/// </para>
/// <para>
/// <b>A poll never dies silently.</b> A poll that throws is traced (rate-limited) and counted;
/// after <see cref="PollFaultsBeforeSurface"/> in a row — or at once, for a manual refresh — a
/// snapshot is published whose <see cref="GatewaySnapshot.Detail"/> names the fault, so the
/// dashboard and tray say the monitor is failing instead of freezing on the last good state. A
/// subscriber that throws costs only its own delivery.
/// </para>
/// <para>
/// <b>Gates run on the monotonic clock</b> (<see cref="MonotonicStamp"/>): a wall-clock step
/// must not stop <c>/alerts</c> — and with it the tray's CRITICAL toasts — for the size of the
/// step. Wall time (<see cref="GatewaySnapshot.PolledAt"/>, <see cref="GatewaySnapshot.AlertsFetchedAt"/>)
/// is for display only.
/// </para>
/// </summary>
public sealed class GatewayMonitor : IDisposable
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AlertInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cadence for <c>/status</c>. It is authenticated and carries the hook contract rather
    /// than liveness, so it rides the same slow lane as alerts instead of the 5s health tick.
    /// </summary>
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(30);

    /// <summary>Unreachable polls tolerated before backing off to <see cref="SlowInterval"/>.</summary>
    public const int FailuresBeforeBackoff = 3;

    /// <summary>
    /// Polls in a row that may throw before a snapshot saying so is published. One is a blip
    /// (a file locked for a moment); three is a defect the operator should be told about.
    /// </summary>
    public const int PollFaultsBeforeSurface = 3;

    /// <summary>The same fault is traced at most this often; a different one is traced at once.</summary>
    private static readonly TimeSpan FaultTraceInterval = TimeSpan.FromMinutes(1);

    /// <summary>Matches the TUI's alert page size.</summary>
    public const int AlertLimit = 25;

    /// <summary>The connector whose hook Claude Code's settings.json can override.</summary>
    public const string ClaudeCodeConnector = "claudecode";

    private readonly AppServices _services;
    private readonly TimeProvider _time;
    private readonly InstallStateDetector? _detector;
    private readonly GatewayPeerVerifier _peer;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Guards <see cref="_current"/>, which other threads read through <see cref="Current"/>.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Serializes <c>PollAsync</c> + <see cref="Publish"/>: the background loop and
    /// <see cref="RefreshAsync"/> take turns. Every poll-bookkeeping field below, and
    /// <see cref="_lastPublished"/>, is only touched while holding it. Deliberately never
    /// disposed — without <c>AvailableWaitHandle</c> a <see cref="SemaphoreSlim"/> owns no
    /// unmanaged resource, and disposing it under a pending waiter would turn a clean
    /// shutdown into an <see cref="ObjectDisposedException"/>.
    /// </summary>
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private SynchronizationContext? _uiContext;
    private Task? _loop;
    private int _consecutiveFailures;

    /// <summary>When <c>/alerts</c> and <c>/status</c> were last requested; the gates behind their 30 s cadence. Monotonic.</summary>
    private MonotonicStamp _lastAlertPoll = MonotonicStamp.Never;
    private MonotonicStamp _lastStatusPoll = MonotonicStamp.Never;

    /// <summary>Polls in a row that threw before producing a snapshot. Only touched while holding <c>_pollGate</c>.</summary>
    private int _consecutivePollFaults;

    /// <summary>What was last traced and when, so a fault that repeats every five seconds is not logged every five seconds.</summary>
    private readonly object _faultTraceGate = new();
    private string? _lastTracedFault;
    private MonotonicStamp _lastFaultTraceAt = MonotonicStamp.Never;

    /// <summary>Completion time of the last successful <c>/alerts</c> fetch (wall clock, for display); see <see cref="GatewaySnapshot.AlertsFetchedAt"/>.</summary>
    private DateTimeOffset? _lastAlertsFetchedAt;

    /// <summary>Port the previous poll used; 0 before the first. See <c>ResetForNewEndpoint</c>.</summary>
    private int _lastPolledPort;

    /// <summary>Last known claudecode hook contract from <c>/status</c>; sticky across blips.</summary>
    private ConnectorMode? _lastClaudeCodeMode;
    private IReadOnlyList<GatewayAlert> _lastAlerts = Array.Empty<GatewayAlert>();
    private string? _lastAlertsUnavailable = "Alerts have not been polled yet.";
    private GatewaySnapshot _current = GatewaySnapshot.Initial;

    /// <summary>
    /// The snapshot the last <see cref="StateChanged"/> was raised for; null until the first
    /// poll, which therefore always publishes. Compared against, never handed out.
    /// </summary>
    private GatewaySnapshot? _lastPublished;

    /// <summary>
    /// What the last read of Claude Code's settings.json returned, and the file stamp it was
    /// read at. See <see cref="ReadClaudeSettings"/>.
    /// </summary>
    private ClaudeSettingsSnapshot? _settingsCache;
    private (DateTime WrittenUtc, long Length) _settingsStamp;

    private bool _disposed;

    /// <param name="services">The composition this monitor polls through.</param>
    /// <param name="timeProvider">Clock for the cadence gates and the snapshot stamps; tests pass a manual one.</param>
    /// <param name="detector">Overrides <see cref="AppServices.InstallDetector"/>; tests hand in one over a fake port owner.</param>
    internal GatewayMonitor(AppServices services, TimeProvider? timeProvider = null, InstallStateDetector? detector = null)
    {
        _services = services;
        _time = timeProvider ?? TimeProvider.System;
        _detector = detector;
        _peer = new GatewayPeerVerifier(services.Paths, services.PortInspector);
    }

    /// <summary>How many handlers are attached to <see cref="PollCompleted"/> right now; what the idle-cost tests read.</summary>
    internal int PollCompletedSubscriberCount => PollCompleted?.GetInvocationList().Length ?? 0;

    /// <summary>
    /// Raised on the UI thread, once per <i>material</i> change: the first poll, and after
    /// that only when <see cref="GatewaySnapshot.RendersSameAs"/> says the new snapshot
    /// differs from the last one published. An idle, healthy box raises it almost never.
    /// <para>
    /// Delivery is queued, and the delegate is read when the queued call runs, so a
    /// subscriber that unsubscribed in the meantime is not called.
    /// </para>
    /// </summary>
    public event EventHandler<GatewaySnapshotEventArgs>? StateChanged;

    /// <summary>
    /// Raised on the UI thread after <i>every</i> completed poll, changed or not, carrying
    /// the freshest snapshot — including the <see cref="GatewaySnapshot.Health"/> detail and
    /// <see cref="GatewaySnapshot.PolledAt"/> that <see cref="StateChanged"/> ignores. When
    /// both fire for one poll, <see cref="StateChanged"/> runs first.
    /// <para>
    /// <b>Subscribe only while something that needs it is on screen.</b> While no handler is
    /// attached nothing is queued to the UI thread at all; every subscriber costs one
    /// dispatcher hop per poll. A subscriber that comes and goes (a panel) reads
    /// <see cref="Current"/> when it attaches, so it never has to wait a poll to be current.
    /// </para>
    /// </summary>
    public event EventHandler<GatewaySnapshotEventArgs>? PollCompleted;

    /// <summary>The most recent snapshot. Never null.</summary>
    public GatewaySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Starts the poll loop, capturing the calling thread as the event thread.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _uiContext = SynchronizationContext.Current;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>
    /// Polls once, out of band, and publishes the result. Queues behind a poll already in
    /// flight rather than racing it, so the answer is never older than the call — and the
    /// 30 s alert and status throttles hold no matter how often this is clicked. (That means
    /// the alert list in the returned snapshot may be up to <see cref="AlertInterval"/> old;
    /// <see cref="GatewaySnapshot.AlertsFetchedAt"/> says exactly how old, and
    /// <see cref="RefreshAlertsNowAsync"/> is the call that refuses to serve a cached list.)
    /// As a manual action it also forgets the cached executable lookups, so an install that
    /// just finished is noticed now rather than when the lookup cache expires.
    /// </summary>
    public Task<GatewaySnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        ManualPollAsync(forceAlerts: false, cancellationToken);

    /// <summary>
    /// Polls once, out of band, with <c>/alerts</c> forced: the 30 s alert throttle is skipped
    /// so the snapshot's list was fetched by this call (when the gateway is reachable at all —
    /// with it down, or not installed, there is nothing to fetch and the snapshot says why via
    /// <see cref="GatewaySnapshot.AlertsUnavailable"/>). Everything else about it is
    /// <see cref="RefreshAsync"/>: it queues behind a poll in flight, publishes the result to
    /// <see cref="Current"/> and both events, and leaves the <c>/status</c> throttle alone.
    /// <para>
    /// The returned <see cref="GatewaySnapshot.AlertsFetchedAt"/> is the completion time of the
    /// fetch this call made — the honest "refreshed at" for an Alerts refresh button. A forced
    /// fetch restarts the 30 s alert cadence, so the background loop does not fetch again
    /// right behind it.
    /// </para>
    /// </summary>
    public Task<GatewaySnapshot> RefreshAlertsNowAsync(CancellationToken cancellationToken = default) =>
        ManualPollAsync(forceAlerts: true, cancellationToken);

    /// <summary>
    /// One background-loop poll: exactly what <see cref="RunAsync"/> does each round, minus the
    /// wait. A fault is counted rather than shown until <see cref="PollFaultsBeforeSurface"/> of
    /// them ran in a row. Internal so a test can drive the loop's cadence without sleeping.
    /// </summary>
    internal Task<GatewaySnapshot> PollOnceAsync(CancellationToken cancellationToken = default) =>
        PollAndPublishAsync(forceAlerts: false, surfaceFaultNow: false, cancellationToken);

    /// <summary>How many times <c>/alerts</c> was requested; the tests' view of the 30 s cadence gate.</summary>
    internal int AlertsFetchCount => Volatile.Read(ref _alertsFetchCount);

    /// <summary>How many times <c>/status</c> was requested; the tests' view of its cadence gate.</summary>
    internal int StatusFetchCount => Volatile.Read(ref _statusFetchCount);

    private int _alertsFetchCount;
    private int _statusFetchCount;

    private Task<GatewaySnapshot> ManualPollAsync(bool forceAlerts, CancellationToken cancellationToken)
    {
        _services.Paths.InvalidateExecutableCache();
        return PollAndPublishAsync(forceAlerts, surfaceFaultNow: true, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _ = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A poll loop that dies takes the whole UI's state with it.
            catch (Exception ex)
            {
                // PollAndPublishAsync already records a poll's own faults; what reaches here is
                // the gate or the plumbing around it. Still traced, never swallowed silently.
                TraceFault("poll loop", ex);
                Interlocked.Increment(ref _consecutiveFailures);
            }
#pragma warning restore CA1031

            var interval = _consecutiveFailures >= FailuresBeforeBackoff ? SlowInterval : FastInterval;
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The only way a poll starts, from the loop or from a manual refresh. Polling
    /// and publishing are one critical section so snapshots are published in the order they
    /// were taken: released between the two, a slow poll could overwrite a newer one.
    /// <para>
    /// <b>Never throws for a poll's own fault</b> (only for cancellation): a poll that threw is
    /// recorded, and once <see cref="PollFaultsBeforeSurface"/> of them ran in a row — or at once,
    /// for a manual refresh, so the relay command that asked gets an answer instead of an
    /// exception — a snapshot carrying "The gateway poll is failing: …" is published. Below that
    /// the previous snapshot stands and is what this returns.
    /// </para>
    /// </summary>
    private async Task<GatewaySnapshot> PollAndPublishAsync(bool forceAlerts, bool surfaceFaultNow, CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GatewaySnapshot? snapshot;
            try
            {
                snapshot = await PollAsync(forceAlerts, cancellationToken).ConfigureAwait(false);
                _consecutivePollFaults = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A poll that throws must be recorded and shown, not allowed to end the loop or the refresh command.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                snapshot = RecordPollFault(ex, surfaceFaultNow);
            }

            if (snapshot is null)
            {
                return Current;
            }

            try
            {
                Publish(snapshot);
            }
#pragma warning disable CA1031 // Publish stores the snapshot before it raises anything; a fault after that must not look like a failed poll.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                TraceFault("publish", ex);
            }

            return snapshot;
        }
        finally
        {
            _ = _pollGate.Release();
        }
    }

    /// <summary>
    /// Counts a poll that threw and traces it (rate-limited). Returns the snapshot to publish —
    /// the last one, with <see cref="GatewaySnapshot.Detail"/> naming the fault — once it is time
    /// to tell the operator, or null while it is still only a blip. Callers must hold <c>_pollGate</c>.
    /// </summary>
    private GatewaySnapshot? RecordPollFault(Exception ex, bool surfaceNow)
    {
        _consecutivePollFaults++;
        TraceFault("poll", ex);
        Interlocked.Increment(ref _consecutiveFailures);

        if (!surfaceNow && _consecutivePollFaults < PollFaultsBeforeSurface)
        {
            return null;
        }

        var current = Current;
        return current with
        {
            // Before the first good poll there is no state to keep; "Checking…" forever is the
            // one thing this must not say.
            State = current.State == AppGatewayState.Unknown ? AppGatewayState.Degraded : current.State,
            Detail = $"The gateway poll is failing: {DescribeFault(ex)}",
            PolledAt = _time.GetUtcNow(),
            ConsecutiveFailures = Volatile.Read(ref _consecutiveFailures),
        };
    }

    /// <summary>
    /// <c>Type: message</c>, on one line, capped, with the gateway token masked should an
    /// exception message ever quote it. The text ends up on the dashboard and in the tray.
    /// </summary>
    private string DescribeFault(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        if (_services.Token.Token is { IsEmpty: false } token)
        {
            text = token.Scrub(text);
        }

        text = text.ReplaceLineEndings(" ");
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>
    /// Writes <paramref name="ex"/> to the trace, but a fault that repeats verbatim every poll
    /// only once a minute; a different fault is written at once.
    /// </summary>
    private void TraceFault(string what, Exception ex)
    {
        var key = $"{what}|{ex.GetType().FullName}|{ex.Message}";
        lock (_faultTraceGate)
        {
            if (key == _lastTracedFault && !_lastFaultTraceAt.HasElapsed(FaultTraceInterval, _time))
            {
                return;
            }

            _lastTracedFault = key;
            _lastFaultTraceAt = MonotonicStamp.Now(_time);
        }

        Trace.TraceError($"gateway monitor: {what} failed: {ex}");
    }

    /// <summary>
    /// Takes one snapshot. Callers must hold <c>_pollGate</c>. <paramref name="forceAlerts"/>
    /// skips the <see cref="AlertInterval"/> throttle for this poll (only meaningful while the
    /// gateway answers; otherwise there is nothing to fetch).
    /// </summary>
    private async Task<GatewaySnapshot> PollAsync(bool forceAlerts, CancellationToken cancellationToken)
    {
        // Read once: the port and the client bound to it must come from the same instant, and
        // stay the same for the whole poll even if a config reload swaps them underneath.
        var endpoint = _services.Endpoint;
        var config = _services.Config.Config;

        ResetForNewEndpoint(endpoint.Port);

        var status = await (_detector ?? _services.InstallDetector)
            .DetectAsync(endpoint.Port, endpoint.Client, cancellationToken)
            .ConfigureAwait(false);

        // Detection already probed /health whenever it got that far. Only the states where it
        // stops early (not installed / not initialized) leave nothing to reuse, and there the
        // probe is still wanted: it drives the backoff counter and reveals a WSL relay.
        var health = status.Health
            ?? await endpoint.Client.GetHealthAsync(cancellationToken).ConfigureAwait(false);

        if (health.Status == GatewayStatus.Unreachable)
        {
            Interlocked.Increment(ref _consecutiveFailures);
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        var state = Classify(status, health.Status);

        if (state is AppGatewayState.Running or AppGatewayState.Degraded or AppGatewayState.WslGatewayDetected)
        {
            // The authenticated endpoints are polled only for the gateway itself: its owner
            // checked out AND /health parsed. Anything else that answers on the port — a WSL
            // relay, another server, a gateway from outside the install directory — is read
            // through /health alone, so the bearer token never leaves for it.
            if (status.OwnerTrust == PortOwnerTrust.Gateway && health.IsOk)
            {
                if (forceAlerts || _lastAlertPoll.HasElapsed(AlertInterval, _time))
                {
                    await RefreshAlertsAsync(endpoint.Client, cancellationToken).ConfigureAwait(false);
                }

                if (_lastStatusPoll.HasElapsed(StatusInterval, _time))
                {
                    await RefreshClaudeCodeModeAsync(endpoint.Client, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                _lastAlerts = Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable = DescribeUnverifiedPeer(status, health);
                _lastClaudeCodeMode = null;
            }
        }
        else if (state is AppGatewayState.GatewayStopped or AppGatewayState.NotInstalled or AppGatewayState.NotInitialized)
        {
            _lastAlerts = Array.Empty<GatewayAlert>();
            _lastAlertsUnavailable = "The gateway is not answering; alerts are unavailable.";

            // Nothing is serving /status, so the runtime hook contract is unknowable and a
            // stale cached one would be a lie. config.yaml takes over as the comparison side.
            _lastClaudeCodeMode = null;
        }

        var alerts = _lastAlerts;

        // Checked every poll rather than trusted: the installer re-plants the env override
        // silently and mid-session, and catching that is the entire point of the check. The
        // check is a file stamp; the read-and-parse only happens when the stamp moved.
        var claudeSettings = ReadClaudeSettings();
        config.Guardrail.Connectors.TryGetValue(ClaudeCodeConnector, out var configuredClaudeCode);
        var drift = FailModeDrift.Evaluate(claudeSettings, _lastClaudeCodeMode, configuredClaudeCode);

        return new GatewaySnapshot
        {
            State = state,
            Detail = status.Detail,
            Install = status.State,
            WslGatewayDetected = status.WslGatewayDetected,
            PortOwner = status.PortOwner,
            Health = health.Value,
            HealthStatus = health.Status,
            BinaryVersion = status.BinaryVersion ?? health.Value?.Provenance?.BinaryVersion,
            ApiPort = status.Port,
            CliPath = status.CliPath,
            RecentAlerts = alerts,
            AlertCount = alerts.Count,
            CriticalAlertCount = alerts.Count(IsCritical),
            AlertsUnavailable = _lastAlertsUnavailable,
            AlertsFetchedAt = _lastAlertsFetchedAt,
            ActiveConnectors = ResolveConnectors(health.Value),
            FailModeDrift = drift,
            PolledAt = _time.GetUtcNow(),
            ConsecutiveFailures = Volatile.Read(ref _consecutiveFailures),
        };
    }

    /// <summary>
    /// Why <c>/alerts</c> is not being read when something is answering on the port: the answer is
    /// not the gateway, or is not a healthy one. Never blames the token — none was sent.
    /// </summary>
    private string DescribeUnverifiedPeer(InstallStatus status, GatewayResult<GatewayHealth> health)
    {
        if (status.OwnerTrust == PortOwnerTrust.Gateway)
        {
            return $"Alerts are unavailable: the gateway's /health did not answer as expected ({health.Status}), " +
                   "so no credentials were sent.";
        }

        return $"Alerts are unavailable: {_peer.DescribeUntrusted(status.PortOwner)}, " +
               "so the app reads only /health from it and sends it no credentials.";
    }

    /// <summary>
    /// Drops everything cached from the previous gateway when the monitored port changed
    /// since the last poll: the alert list, the hook contract and the fetch throttles all
    /// describe a different process, and pairing them with the new port's <c>/health</c>
    /// would be a lie until the 30 s cadence caught up. The very first poll (port 0 →
    /// anything) is not a change. Callers must hold <c>_pollGate</c>.
    /// </summary>
    private void ResetForNewEndpoint(int port)
    {
        var previous = _lastPolledPort;
        _lastPolledPort = port;

        if (previous == 0 || previous == port)
        {
            return;
        }

        _lastAlertPoll = MonotonicStamp.Never;
        _lastStatusPoll = MonotonicStamp.Never;
        _lastAlerts = Array.Empty<GatewayAlert>();
        _lastAlertsUnavailable = "Alerts have not been polled yet.";
        _lastAlertsFetchedAt = null;
        _lastClaudeCodeMode = null;
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    private async Task RefreshAlertsAsync(GatewayClient gateway, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _alertsFetchCount);

        GatewayResult<IReadOnlyList<GatewayAlert>> result;
        try
        {
            result = await gateway
                .GetAlertsAsync(AlertLimit, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The snapshot must still be published: a throwing fetch is "alerts unavailable", not a frozen dashboard.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            TraceFault("alerts fetch", ex);
            _lastAlertPoll = MonotonicStamp.Now(_time);
            _lastAlerts = Array.Empty<GatewayAlert>();
            _lastAlertsUnavailable = $"Alerts could not be read: {DescribeFault(ex)}";
            return;
        }

        var completedAt = _time.GetUtcNow();
        _lastAlertPoll = MonotonicStamp.Now(_time);

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                // A fresh list that shows the same findings keeps the previous instance, so
                // "did the alerts change" is a reference comparison for every consumer that
                // keys off RecentAlerts (the Alerts panel re-projects only on a new one).
                var fresh = result.Value ?? Array.Empty<GatewayAlert>();
                if (!GatewaySnapshot.AlertsEquivalent(_lastAlerts, fresh))
                {
                    _lastAlerts = fresh;
                }

                _lastAlertsUnavailable = null;
                _lastAlertsFetchedAt = completedAt;
                break;

            // Sidecar alive, subsystem unwired. Normal on this install — informational,
            // never an error.
            case GatewayStatus.NotConnected:
                _lastAlerts = Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable = "The alert subsystem is not connected on this install.";
                break;

            case GatewayStatus.Unauthorized:
                _lastAlerts = Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable =
                    $"/alerts needs a bearer token; none was found via {_services.Token.VariableName}." +
                    (_services.Token.Note is { } note ? $" {note}" : string.Empty);
                break;

            default:
                _lastAlerts = Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable = result.ErrorMessage ?? "Alerts could not be read.";
                break;
        }
    }

    /// <summary>
    /// Reads the claudecode hook contract from <c>/status</c>, the only surface that states
    /// what the running hook will actually do on a gateway failure.
    /// <para>
    /// Deliberately forgiving about which entry counts. <c>connector_modes</c> is preferred
    /// and matched by name; failing that the primary <c>connector_mode</c> is accepted when
    /// it names claudecode, and also when it is the only mode present — on a
    /// single-connector install 0.8.x omits the connector name, and refusing to read it
    /// there would silently disable the check on exactly the boxes it was written for.
    /// </para>
    /// <para>
    /// Any non-Ok result keeps the previous cached value: <c>/status</c> is authenticated,
    /// and a transient 401 or timeout must not flap the drift banner on and off.
    /// </para>
    /// </summary>
    private async Task RefreshClaudeCodeModeAsync(GatewayClient gateway, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _statusFetchCount);

        GatewayResult<GatewayStatusResponse> result;
        try
        {
            result = await gateway.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Same as the alerts fetch: the cached contract stands and the poll still publishes.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            TraceFault("status fetch", ex);
            _lastStatusPoll = MonotonicStamp.Now(_time);
            return;
        }

        _lastStatusPoll = MonotonicStamp.Now(_time);

        if (!result.IsOk || result.Value is not { } status)
        {
            return;
        }

        foreach (var mode in status.ConnectorModes)
        {
            if (string.Equals(mode.Connector, ClaudeCodeConnector, StringComparison.OrdinalIgnoreCase))
            {
                _lastClaudeCodeMode = mode;
                return;
            }
        }

        if (status.ConnectorMode is { } primary &&
            (string.Equals(primary.Connector, ClaudeCodeConnector, StringComparison.OrdinalIgnoreCase) ||
             status.ConnectorModes.Count <= 1))
        {
            _lastClaudeCodeMode = primary;
        }
    }

    /// <summary>
    /// Collapses install detection plus the <c>/health</c> outcome into the state the UI
    /// renders. WSL coexistence outranks everything: a healthy-looking answer from a WSL
    /// relay is exactly the case the banner exists to catch.
    /// </summary>
    private static AppGatewayState Classify(InstallStatus status, GatewayStatus healthStatus)
    {
        if (status.WslGatewayDetected)
        {
            return AppGatewayState.WslGatewayDetected;
        }

        return status.State switch
        {
            InstallState.NotInstalled => AppGatewayState.NotInstalled,
            InstallState.InstalledNotInitialized => AppGatewayState.NotInitialized,
            InstallState.GatewayStopped => AppGatewayState.GatewayStopped,
            // A clean /health from a process that is demonstrably not the gateway (or not the
            // installed one) is not "Running": it is something else answering on the port.
            InstallState.Running when healthStatus == GatewayStatus.Ok && status.OwnerTrust != PortOwnerTrust.Other =>
                AppGatewayState.Running,
            InstallState.Running => AppGatewayState.Degraded,
            _ => AppGatewayState.Unknown,
        };
    }

    private static bool IsCritical(GatewayAlert alert) =>
        string.Equals(alert.Severity, "CRITICAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Config is the source of truth for "which connector did the operator configure";
    /// <c>/health</c> adds the ones actually running. Both matter, so the flyout shows the
    /// union in config-first order.
    /// </summary>
    private IReadOnlyList<string> ResolveConnectors(GatewayHealth? health)
    {
        var names = new Collection<string>();
        var config = _services.Config.Config;

        Add(config.Claw.Mode);
        Add(config.Guardrail.Connector);
        foreach (var key in config.Guardrail.Connectors.Keys)
        {
            Add(key);
        }

        if (health is not null)
        {
            Add(health.Connector?.Name);
            foreach (var connector in health.Connectors)
            {
                Add(connector.Name);
            }
        }

        return names;

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var trimmed = name.Trim();
            if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }
    }

    /// <summary>
    /// Returns what Claude Code's settings.json says about the fail mode, re-reading and
    /// re-parsing it only when the file's last-write time or length moved since the last
    /// clean read. The file is typically a few KB to a few tens of KB and was being read and
    /// parsed every five seconds to learn that nothing had changed.
    /// <para>
    /// The stamp is taken <i>before</i> the read, so an edit landing between the two is
    /// cached under the older stamp and picked up on the next poll — never missed. Only a
    /// clean read of an existing file is cached: an absent file, a locked or half-written
    /// one (the installer mid-rewrite) and any error path go through
    /// <see cref="ClaudeSettingsReader.Read"/> every time, exactly as before. Callers must
    /// hold <c>_pollGate</c>.
    /// </para>
    /// </summary>
    private ClaudeSettingsSnapshot ReadClaudeSettings()
    {
        var reader = _services.ClaudeSettings;

        (DateTime WrittenUtc, long Length) stamp;
        try
        {
            var info = new FileInfo(reader.SettingsPath);
            if (!info.Exists)
            {
                _settingsCache = null;
                return reader.Read();
            }

            stamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settingsCache = null;
            return reader.Read();
        }

        if (_settingsCache is { } cached && _settingsStamp == stamp)
        {
            return cached;
        }

        var fresh = reader.Read();
        if (fresh is { Exists: true, ReadError: null })
        {
            _settingsCache = fresh;
            _settingsStamp = stamp;
        }
        else
        {
            _settingsCache = null;
        }

        return fresh;
    }

    /// <summary>
    /// Stores <paramref name="snapshot"/> as <see cref="Current"/> unconditionally — so
    /// <c>RefreshAsync</c> callers and late subscribers always see the freshest data — and
    /// then raises <see cref="StateChanged"/> only if it differs materially from the last
    /// published one, and <see cref="PollCompleted"/> always. Callers must hold
    /// <c>_pollGate</c>.
    /// </summary>
    private void Publish(GatewaySnapshot snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
        }

        var changed = _lastPublished is null || !snapshot.RendersSameAs(_lastPublished);
        if (changed)
        {
            _lastPublished = snapshot;
        }

        // Nobody listening for this poll: queue nothing. This is the hidden-in-the-tray case
        // once the panels have unsubscribed, and it is what makes an idle poll cost only the
        // poll itself.
        if ((!changed || StateChanged is null) && PollCompleted is null)
        {
            return;
        }

        var context = _uiContext;
        if (context is null || context == SynchronizationContext.Current)
        {
            Raise(snapshot, changed);
            return;
        }

        context.Post(_ => Raise(snapshot, changed), null);
    }

    /// <summary>
    /// Runs on the UI thread. Reads both delegates here rather than at publish time, so a
    /// panel that deactivated while the call was queued is not called into.
    /// <para>
    /// Each subscriber is called on its own: one that throws is traced and skipped, and neither
    /// the ones after it nor <see cref="PollCompleted"/> are cheated of this poll. (A plain
    /// <c>Invoke</c> stops at the first throw — and since <c>_lastPublished</c> was set before
    /// this ran, the material change it announced would never have been announced again.)
    /// </para>
    /// </summary>
    private void Raise(GatewaySnapshot snapshot, bool changed)
    {
        var args = new GatewaySnapshotEventArgs(snapshot);

        if (changed)
        {
            InvokeEach(StateChanged, args, nameof(StateChanged));
        }

        InvokeEach(PollCompleted, args, nameof(PollCompleted));
    }

    private void InvokeEach(EventHandler<GatewaySnapshotEventArgs>? handlers, GatewaySnapshotEventArgs args, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<GatewaySnapshotEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // A misbehaving subscriber must not silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                TraceFault($"{eventName} subscriber {handler.Method.DeclaringType?.Name}.{handler.Method.Name}", ex);
            }
        }
    }
}
