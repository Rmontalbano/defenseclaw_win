using System.Collections.ObjectModel;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;

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

    /// <summary>Connectors named by config.yaml, unioned with the ones <c>/health</c> reports.</summary>
    public IReadOnlyList<string> ActiveConnectors { get; init; } = Array.Empty<string>();

    public DateTimeOffset PolledAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Consecutive unreachable polls; drives the poll backoff.</summary>
    public int ConsecutiveFailures { get; init; }

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
/// and one <see cref="StateChanged"/> event.
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
/// than <c>/health</c>.
/// </para>
/// </summary>
public sealed class GatewayMonitor : IDisposable
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AlertInterval = TimeSpan.FromSeconds(30);

    /// <summary>Unreachable polls tolerated before backing off to <see cref="SlowInterval"/>.</summary>
    public const int FailuresBeforeBackoff = 3;

    /// <summary>Matches the TUI's alert page size.</summary>
    public const int AlertLimit = 25;

    private readonly AppServices _services;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private SynchronizationContext? _uiContext;
    private Task? _loop;
    private int _consecutiveFailures;
    private DateTimeOffset _lastAlertPoll = DateTimeOffset.MinValue;
    private IReadOnlyList<GatewayAlert> _lastAlerts = Array.Empty<GatewayAlert>();
    private string? _lastAlertsUnavailable = "Alerts have not been polled yet.";
    private GatewaySnapshot _current = GatewaySnapshot.Initial;
    private bool _disposed;

    internal GatewayMonitor(AppServices services)
    {
        _services = services;
    }

    /// <summary>Raised on the UI thread after every completed poll.</summary>
    public event EventHandler<GatewaySnapshotEventArgs>? StateChanged;

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

    /// <summary>Polls once, out of band, and publishes the result.</summary>
    public async Task<GatewaySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await PollAsync(cancellationToken).ConfigureAwait(false);
        Publish(snapshot);
        return snapshot;
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
                var snapshot = await PollAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A poll loop that dies takes the whole UI's state with it.
            catch (Exception)
            {
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

    private async Task<GatewaySnapshot> PollAsync(CancellationToken cancellationToken)
    {
        var config = _services.Config.Config;
        var status = await _services.InstallDetector
            .DetectAsync(config, cancellationToken)
            .ConfigureAwait(false);

        var health = await _services.Gateway.GetHealthAsync(cancellationToken).ConfigureAwait(false);

        if (health.Status == GatewayStatus.Unreachable)
        {
            Interlocked.Increment(ref _consecutiveFailures);
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        var state = Classify(status, health.Status);

        if (state is AppGatewayState.Running or AppGatewayState.Degraded or AppGatewayState.WslGatewayDetected &&
            DateTimeOffset.UtcNow - _lastAlertPoll >= AlertInterval)
        {
            await RefreshAlertsAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (state is AppGatewayState.GatewayStopped or AppGatewayState.NotInstalled or AppGatewayState.NotInitialized)
        {
            _lastAlerts = Array.Empty<GatewayAlert>();
            _lastAlertsUnavailable = "The gateway is not answering; alerts are unavailable.";
        }

        var alerts = _lastAlerts;

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
            ActiveConnectors = ResolveConnectors(health.Value),
            PolledAt = DateTimeOffset.UtcNow,
            ConsecutiveFailures = Volatile.Read(ref _consecutiveFailures),
        };
    }

    private async Task RefreshAlertsAsync(CancellationToken cancellationToken)
    {
        var result = await _services.Gateway
            .GetAlertsAsync(AlertLimit, cancellationToken)
            .ConfigureAwait(false);

        _lastAlertPoll = DateTimeOffset.UtcNow;

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                _lastAlerts = result.Value ?? Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable = null;
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
                    $"/alerts needs a bearer token; none was found via {_services.Token.VariableName}.";
                break;

            default:
                _lastAlerts = Array.Empty<GatewayAlert>();
                _lastAlertsUnavailable = result.ErrorMessage ?? "Alerts could not be read.";
                break;
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
            InstallState.Running when healthStatus == GatewayStatus.Ok => AppGatewayState.Running,
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

    private void Publish(GatewaySnapshot snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
        }

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        var context = _uiContext;
        if (context is null || context == SynchronizationContext.Current)
        {
            handler(this, new GatewaySnapshotEventArgs(snapshot));
            return;
        }

        context.Post(_ => handler(this, new GatewaySnapshotEventArgs(snapshot)), null);
    }
}
