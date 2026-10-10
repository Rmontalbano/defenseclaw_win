using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Time;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The left-click flyout, after the Mac app's menu-bar popover (<c>MenuBarPopover.swift</c>), top to bottom: the gateway header (shield, name,
/// "Gateway up · 43m up · 2 connectors", state pill); one line per connector (dot, name, mode, "N calls · N blocks"); three metric rows —
/// Hook Calls, Blocks, Findings — each a thin bar and a button that opens the dashboard on the panel that explains the number; the newest
/// unacknowledged findings; and the footer (Open Dashboard, Settings, Pause / Resume, Exit) with the version / endpoint / last-polled facts as one
/// caption line.
/// <para>
/// <b>It never changes anything on its own.</b> Where the Mac has "Ack All", this has "Review acknowledge…", which opens Alerts, where the
/// reviewed acknowledge flow already lives. The only state it touches is the app's own: Pause / Resume monitoring
/// (<see cref="GatewayMonitor.SetPaused"/>), which is local to this app and leaves the gateway alone.
/// </para>
/// <para>
/// <b>Three subscriptions, three lifetimes.</b> <see cref="GatewayMonitor.StateChanged"/> — everything that can change materially — is held for
/// the life of the process, so the flyout is right the instant it opens. Everything that costs something is held <b>only while the flyout is on
/// screen and monitoring is not paused</b> (<see cref="SetVisible"/>, driven by the window): <see cref="GatewayMonitor.PollCompleted"/> (one
/// dispatcher hop per poll; it carries the uptime and the per-connector counters), <see cref="AlertCountsService.Changed"/> (which keeps the
/// Findings row and the recent list fresh and, as the only subscriber, is what starts that service), and the read-only audit query behind Hook Calls and
/// Blocks (<see cref="RecentAuditMetricsReader"/>: the newest 500 rows by an index walk, a few milliseconds, cancelled the moment the flyout hides).
/// While monitoring is paused none of them runs: the flyout shows the last known numbers and says when they are from.
/// </para>
/// </summary>
public sealed partial class TrayFlyoutViewModel : ObservableObject, IDisposable
{
    /// <summary>How many of the newest unacknowledged findings the flyout lists (the Mac's <c>prefix(5)</c>).</summary>
    public const int RecentFindingLimit = 5;

    /// <summary>The audit counts are re-read at most this often while the flyout stays open; it is read once when it opens.</summary>
    private static readonly TimeSpan MetricsRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>A read that takes longer than this is abandoned: on the live 6.9 GB database it takes a few milliseconds (5 s; see <see cref="AppServices.ReaderTimeouts"/>).</summary>
    private TimeSpan MetricsTimeout => _services.ReaderTimeouts.RecentAuditMetrics;

    private readonly AppServices _services;
    private readonly Action _openDashboard;
    private readonly Action _exit;
    private readonly RecentAuditMetricsReader _metricsReader;
    private readonly TimeProvider _time;

    private bool _visible;
    private bool _trackingPolls;
    private bool _disposed;

    /// <summary>Cancelled when the flyout hides or monitoring is paused; non-null exactly while the live feeds run. See <see cref="SyncLive"/>.</summary>
    private CancellationTokenSource? _live;
    private Task _lastRefresh = Task.CompletedTask;

    private RecentAuditMetrics? _metrics;
    private string? _metricsFailure;
    private bool _metricsInFlight;

    /// <summary>The token of the newest audit read. A read that was cancelled (the flyout hid, monitoring paused) finishes some time later and must not clear the in-flight flag of the one that replaced it.</summary>
    private CancellationToken _metricsToken;
    private MonotonicStamp _lastMetricsRead = MonotonicStamp.Never;

    /// <summary>What the last alert-count result said, kept to word the empty list: whether there is data, and why there is none.</summary>
    private bool _alertsHaveData;
    private string? _alertsUnavailable;

    /// <summary>When a live read last completed (wall clock, for "Updated 12s ago"); null until one has.</summary>
    private DateTimeOffset? _updatedAt;

    [ObservableProperty]
    private string _stateLabel = "Checking…";

    /// <summary>The state in a sentence, for the pill's tooltip.</summary>
    [ObservableProperty]
    private string _stateDetail = string.Empty;

    /// <summary>
    /// Design-system tone key for the state pill (<c>Ok / Warn / Bad / Neutral</c>). A key rather than a brush so the dot and the pill follow a live
    /// light/dark or style switch.
    /// </summary>
    [ObservableProperty]
    private string _stateTone = "Neutral";

    /// <summary>The line under "DefenseClaw": "Gateway up · 43m up · 2 connectors", "Gateway offline", "Monitoring paused".</summary>
    [ObservableProperty]
    private string _headerCaption = string.Empty;

    /// <summary>"v0.8.10 · 127.0.0.1:18970 · polled 21:04:12": version, endpoint and last poll as the one caption line they are.</summary>
    [ObservableProperty]
    private string _factsLine = string.Empty;

    [ObservableProperty]
    private string _lastPolled = "never";

    /// <summary>
    /// The one line about fail-mode drift the operator sees before opening the dashboard. Deliberately a single sentence: the full explanation and
    /// the copyable remediation live in the Overview panel's attention list.
    /// </summary>
    [ObservableProperty]
    private string _failModeNote = string.Empty;

    [ObservableProperty]
    private bool _hasFailModeNote;

    /// <summary>"Updated just now" / "Updating…" / "Monitoring paused · counts as of 3m ago".</summary>
    [ObservableProperty]
    private string _updatedText = string.Empty;

    /// <summary>What the list says when there is nothing to list: "No unacknowledged findings", or why the count is not known.</summary>
    [ObservableProperty]
    private string _findingsEmptyText = "Reading findings…";

    [ObservableProperty]
    private bool _hasConnectors;

    /// <summary>True when the gateway has been asked and names no connector; not before the first poll, when nothing is known yet.</summary>
    [ObservableProperty]
    private bool _showNoConnectors;

    [ObservableProperty]
    private bool _hasFindings;

    /// <summary>True while monitoring is paused (the app's <c>monitoring.paused</c> setting). The Pause / Resume button and the header follow it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    [NotifyPropertyChangedFor(nameof(PauseSymbol))]
    [NotifyPropertyChangedFor(nameof(PauseAutomationName))]
    private bool _isPaused;

    public TrayFlyoutViewModel(AppServices services, Action openDashboard, Action exit)
        : this(services, openDashboard, exit, metricsReader: null, timeProvider: null)
    {
    }

    /// <param name="services">The composition the flyout reads.</param>
    /// <param name="openDashboard">Shows the dashboard (Open Dashboard).</param>
    /// <param name="exit">Quits the app for real.</param>
    /// <param name="metricsReader">The audit-count reader; one over <c>audit.db</c> when null. A test hands in one over a scratch database.</param>
    /// <param name="timeProvider">The clock for the refresh gate and "Updated …"; the system one when null.</param>
    internal TrayFlyoutViewModel(
        AppServices services,
        Action openDashboard,
        Action exit,
        RecentAuditMetricsReader? metricsReader,
        TimeProvider? timeProvider)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _openDashboard = openDashboard ?? throw new ArgumentNullException(nameof(openDashboard));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _metricsReader = metricsReader ?? new RecentAuditMetricsReader(services.Paths.AuditDatabasePath);
        _time = timeProvider ?? TimeProvider.System;

        Metrics = new[]
        {
            new FlyoutMetricRow("Hook Calls", "Accent", "Logs", OpenHookCallsCommand),
            new FlyoutMetricRow("Blocks", "Bad", "Audit", OpenBlocksCommand),
            new FlyoutMetricRow("Findings", "Ok", "Alerts", OpenFindingsCommand),
        };

        _services.Monitor.StateChanged += OnStateChanged;

        // What is known without asking anyone: the last snapshot and the last alert counts (which may be from the dashboard's own reads).
        ApplyAlerts(_services.AlertCounts.Current, _services.AlertCounts.HasData, _services.AlertCounts.Unavailable);
        RenderMetrics();
        Apply(_services.Monitor.Current);
    }

    /// <summary>
    /// Raised when the flyout should go away because something in it opened the dashboard; the window hides itself. (Open Dashboard and Exit hide it
    /// through the actions they were given, as they always have.)
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>One connector per line: dot, name, mode, and what the gateway says it has done.</summary>
    public ObservableCollection<FlyoutConnectorRow> Connectors { get; } = new();

    /// <summary>Hook Calls, Blocks, Findings: in that order.</summary>
    public IReadOnlyList<FlyoutMetricRow> Metrics { get; }

    /// <summary>The newest unacknowledged findings, at most <see cref="RecentFindingLimit"/>, newest first.</summary>
    public ObservableCollection<FlyoutFindingRow> RecentFindings { get; } = new();

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public string PauseAutomationName => IsPaused ? "Resume monitoring" : "Pause monitoring";

    public SymbolRegular PauseSymbol => IsPaused ? SymbolRegular.Play24 : SymbolRegular.Pause24;

    /// <summary>True while <see cref="GatewayMonitor.PollCompleted"/> is subscribed, i.e. while the flyout is showing.</summary>
    internal bool IsTrackingPolls => _trackingPolls;

    /// <summary>True while the live feeds (alert counts, audit query) run: the flyout is on screen and monitoring is not paused.</summary>
    internal bool IsLive => _live is not null;

    /// <summary>The audit reader, for a test to count its reads.</summary>
    internal RecentAuditMetricsReader MetricsReader => _metricsReader;

    /// <summary>Completes when the read the flyout started last (on showing, on a resume) has finished; for tests.</summary>
    internal Task WhenRefreshedAsync() => _lastRefresh;

    /// <summary>
    /// Called by the flyout window (UI thread) when it is shown or hidden. Showing subscribes to <see cref="GatewayMonitor.PollCompleted"/>, re-reads
    /// <see cref="GatewayMonitor.Current"/> — which also catches up with the polls that went by while hidden — and, unless monitoring is paused, starts the
    /// live feeds; hiding drops every one of them (and stops a read in flight) so a flyout nobody is looking at costs nothing.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        _visible = visible;

        if (visible)
        {
            if (!_trackingPolls)
            {
                _trackingPolls = true;
                _services.Monitor.PollCompleted += OnPollCompleted;
            }

            Apply(_services.Monitor.Current);
        }
        else if (_trackingPolls)
        {
            _trackingPolls = false;
            _services.Monitor.PollCompleted -= OnPollCompleted;
        }

        SyncLive();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trackingPolls = false;
        _visible = false;
        StopLive();
        _services.Monitor.StateChanged -= OnStateChanged;
        _services.Monitor.PollCompleted -= OnPollCompleted;
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void OpenDashboard() => _openDashboard();

    /// <summary>The gear: the dashboard, on its Settings page.</summary>
    [RelayCommand]
    private void OpenSettings() => Navigate("settings", null);

    [RelayCommand]
    private void Exit() => _exit();

    [RelayCommand]
    private void OpenHookCalls() => Navigate("logs", new LogsPreset("hooks"));

    [RelayCommand]
    private void OpenBlocks() => Navigate("audit", new AuditPreset("blocks"));

    [RelayCommand]
    private void OpenFindings() => Navigate("alerts", new AlertsFilter(Kind: AlertsFilter.KindAll));

    /// <summary>
    /// "Review acknowledge…": the Mac's "Ack All" turned into a review. The flyout never acknowledges anything; Alerts is where the operator sees the
    /// findings, and where the acknowledge goes through the reviewed command flow.
    /// </summary>
    [RelayCommand]
    private void ReviewAcknowledge() => Navigate("alerts", new AlertsFilter());

    /// <summary>
    /// Pauses monitoring, or resumes it: the app-local switch (<see cref="GatewayMonitor.SetPaused"/>), never the gateway. The flyout follows at once
    /// rather than waiting for the snapshot the monitor publishes: pausing stops the live feeds, resuming starts them and the monitor polls immediately.
    /// </summary>
    [RelayCommand]
    private void TogglePause()
    {
        var wasPaused = _services.Monitor.IsPaused;
        var saved = _services.Monitor.SetPaused(!wasPaused);

        Apply(_services.Monitor.Current);

        if (!saved && _services.Monitor.IsPaused == wasPaused)
        {
            UpdatedText = "Could not change monitoring: the settings file is unreadable.";
        }
    }

    private void Navigate(string panelId, object? payload)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _services.Navigation.Request(panelId, payload);
    }

    // ------------------------------------------------------------------ the snapshot

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// While visible: the poll stream, for what StateChanged does not carry — the uptime, the per-connector counters, "polled at" — and the pace of the
    /// audit re-read.
    /// </summary>
    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e)
    {
        Apply(e.Snapshot);

        if (_live is { } live && !_metricsInFlight && _lastMetricsRead.HasElapsed(MetricsRefreshInterval, _time))
        {
            _lastRefresh = RefreshMetricsAsync(live.Token);
        }
    }

    private static string FormatPolledAt(DateTimeOffset polledAt) =>
        polledAt == DateTimeOffset.MinValue
            ? "never"
            : polledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Shows <paramref name="snapshot"/>: the header, the connectors, the facts line. Internal so a test can hand it a snapshot it built.</summary>
    internal void Apply(GatewaySnapshot snapshot)
    {
        // The pause flag is the setting, which is ahead of the snapshot the monitor publishes for it; what is shown follows the flag.
        var paused = _services.Monitor.IsPaused;
        var view = snapshot.IsPaused == paused ? snapshot : snapshot with { IsPaused = paused };

        StateLabel = paused ? "Paused" : view.StateLabel;
        StateDetail = paused
            ? "Monitoring is paused: nothing is polling the gateway and no audit queries run. The gateway itself is not affected."
            : view.Detail;
        StateTone = GatewayPresentation.StateTone(view);
        HeaderCaption = paused ? GatewaySnapshot.PausedLabel : Caption(view);

        LastPolled = FormatPolledAt(view.PolledAt);
        var version = string.IsNullOrWhiteSpace(view.BinaryVersion)
            ? "version unknown"
            : view.PeerUnverified ? $"v{view.BinaryVersion} (unverified)" : $"v{view.BinaryVersion}";
        var port = view.ApiPort > 0 ? view.ApiPort : _services.ApiPort;
        FactsLine = $"{version} · 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)} · polled {LastPolled}";

        if (view.FailModeDrift is { } drift)
        {
            FailModeNote =
                $"⚠ settings.json forces fail-{drift.EnvFailMode}; {drift.GatewaySource} says " +
                $"{drift.GatewayFailMode} — open the dashboard";
            HasFailModeNote = true;
        }
        else
        {
            FailModeNote = string.Empty;
            HasFailModeNote = false;
        }

        ApplyConnectors(view);

        IsPaused = paused;
        SyncLive();
        RenderFindingsEmpty();
        RenderUpdated();
    }

    /// <summary>
    /// The Mac's "Gateway up · 4d up · 2 connector(s)" while the gateway answers; "Gateway offline" when it does not; otherwise the sentence the status
    /// strip shows for the state (not installed, WSL relay, waiting for the first poll).
    /// </summary>
    private static string Caption(GatewaySnapshot snapshot)
    {
        if (snapshot.IsRunning)
        {
            var parts = new List<string> { "Gateway up" };
            if (snapshot.Health is { UptimeMs: > 0 } health)
            {
                parts.Add(TrayFlyoutText.Uptime(health.UptimeMs));
            }

            var count = snapshot.ActiveConnectors.Count;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} connector{(count == 1 ? string.Empty : "s")}"));
            return string.Join(" · ", parts);
        }

        if (snapshot.State == AppGatewayState.GatewayStopped)
        {
            return "Gateway offline";
        }

        return string.IsNullOrWhiteSpace(snapshot.Detail) ? snapshot.StateLabel : snapshot.Detail;
    }

    private void ApplyConnectors(GatewaySnapshot snapshot)
    {
        var names = snapshot.ActiveConnectors;

        // The same connectors in the same order (the normal poll): the rows stay and only their numbers move, so nothing under the pointer is rebuilt.
        var same = Connectors.Count == names.Count;
        for (var i = 0; same && i < names.Count; i++)
        {
            same = string.Equals(Connectors[i].Name, names[i], StringComparison.OrdinalIgnoreCase);
        }

        if (!same)
        {
            Connectors.Clear();
            foreach (var name in names)
            {
                Connectors.Add(new FlyoutConnectorRow(name));
            }
        }

        var configured = _services.Config.Config.Guardrail.Connectors;
        for (var i = 0; i < names.Count; i++)
        {
            var row = Connectors[i];
            var live = FindLive(snapshot.Health, row.Name);
            var mode = configured.TryGetValue(row.Name, out var settings) && !string.IsNullOrWhiteSpace(settings.Mode)
                ? settings.Mode.Trim()
                : string.Empty;

            row.Mode = mode;
            if (live is not null)
            {
                row.Tone = TrayFlyoutText.ConnectorTone(live.State);
                row.Counts = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{live.Requests:N0} calls · {live.ToolBlocks + live.SubprocessBlocks:N0} blocks");
            }
            else
            {
                // Say what is missing rather than a zero that would read as "nothing happened".
                row.Tone = "Neutral";
                row.Counts = snapshot.Health is null ? "no live counters" : "not running";
            }

            row.AutomationName = mode.Length > 0 ? $"{row.Name}, {mode}, {row.Counts}" : $"{row.Name}, {row.Counts}";
        }

        HasConnectors = names.Count > 0;
        ShowNoConnectors = names.Count == 0 && snapshot.State != AppGatewayState.Unknown;
    }

    private static ConnectorStatus? FindLive(GatewayHealth? health, string name)
    {
        if (health is null)
        {
            return null;
        }

        foreach (var connector in health.Connectors)
        {
            if (string.Equals(connector.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return connector;
            }
        }

        // A gateway that reports only the singular connector object.
        return health.Connector is { } primary && string.Equals(primary.Name, name, StringComparison.OrdinalIgnoreCase) ? primary : null;
    }

    // ------------------------------------------------------------------ the live feeds

    /// <summary>Starts or stops the live feeds so they run exactly while the flyout is visible and monitoring is not paused.</summary>
    private void SyncLive()
    {
        var wanted = _visible && !_disposed && !_services.Monitor.IsPaused;
        if (wanted && _live is null)
        {
            StartLive();
        }
        else if (!wanted && _live is not null)
        {
            StopLive();
        }
    }

    private void StartLive()
    {
        var live = new CancellationTokenSource();
        _live = live;

        // Subscribing to the counts starts that service, which reads once at once; if the dashboard already has it running, ask it for a fresh read
        // instead, so the flyout never opens on numbers from half a minute ago.
        var alreadyRunning = _services.AlertCounts.IsRunning;
        _services.AlertCounts.Changed += OnAlertCountsChanged;
        ApplyAlerts(_services.AlertCounts.Current, _services.AlertCounts.HasData, _services.AlertCounts.Unavailable);

        _lastRefresh = RefreshLiveAsync(live.Token, refreshAlerts: alreadyRunning);
    }

    private void StopLive()
    {
        if (_live is not { } live)
        {
            return;
        }

        _live = null;
        _services.AlertCounts.Changed -= OnAlertCountsChanged;

        // The read in flight is abandoned (its result is dropped), so it no longer blocks the next session's read; see _metricsToken.
        _metricsInFlight = false;
        live.Cancel();
        live.Dispose();
    }

    private async Task RefreshLiveAsync(CancellationToken token, bool refreshAlerts)
    {
        var metrics = RefreshMetricsAsync(token);
        var alerts = refreshAlerts ? RefreshAlertsAsync(token) : Task.CompletedTask;
        await Task.WhenAll(metrics, alerts).ConfigureAwait(true);
    }

    private async Task RefreshAlertsAsync(CancellationToken token)
    {
        try
        {
            await _services.AlertCounts.RefreshAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            ApplyAlerts(_services.AlertCounts.Current, _services.AlertCounts.HasData, _services.AlertCounts.Unavailable);
            MarkUpdated();
        }
    }

    private void OnAlertCountsChanged(object? sender, AlertCountsChangedEventArgs e)
    {
        ApplyAlerts(e.Counts, _services.AlertCounts.HasData, e.Unavailable);
        MarkUpdated();
    }

    private async Task RefreshMetricsAsync(CancellationToken token)
    {
        if (_metricsInFlight)
        {
            return;
        }

        _metricsInFlight = true;
        _metricsToken = token;
        _lastMetricsRead = MonotonicStamp.Now(_time);

        try
        {
            var result = await _metricsReader.ReadAsync(MetricsTimeout, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _metrics = result;
            _metricsFailure = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // A database that cannot be read this time (locked, timed out) is "unavailable" on the flyout, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _metricsFailure = ex.Message;
            Trace.TraceWarning($"tray flyout: the audit counts could not be read: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_metricsToken == token)
            {
                _metricsInFlight = false;
            }
        }

        RenderMetrics();
        MarkUpdated();
    }

    private void MarkUpdated()
    {
        _updatedAt = _time.GetUtcNow();
        RenderUpdated();
    }

    // ------------------------------------------------------------------ rendering the numbers

    /// <summary>Hook Calls and Blocks from the last audit read (or "—" before there is one).</summary>
    private void RenderMetrics()
    {
        var window = RecentAuditMetricsReader.DefaultWindow.ToString(CultureInfo.InvariantCulture);
        var hooks = Metrics[0];
        var blocks = Metrics[1];

        if (_metrics is { } metrics)
        {
            hooks.Value = metrics.HookCalls.ToString("N0", CultureInfo.CurrentCulture);
            hooks.ValueTone = metrics.HookCalls > 0 ? "Primary" : "Neutral";
            hooks.Detail = $"latest {window} audit events";
            hooks.SetProgress(TrayFlyoutText.ActivityProgress(metrics.HookCalls), metrics.HookCalls);

            blocks.Value = metrics.Blocks.ToString("N0", CultureInfo.CurrentCulture);
            blocks.ValueTone = metrics.Blocks > 0 ? "Bad" : "Neutral";
            blocks.Detail = $"latest {window} decisions · {TrayFlyoutText.BlockRate(metrics.Blocks, metrics.HookCalls)}";
            blocks.SetProgress(TrayFlyoutText.BlockProgress(metrics.Blocks, metrics.HookCalls), metrics.Blocks);
        }
        else
        {
            var why = _metricsFailure is null ? $"latest {window} audit events" : "audit database unavailable";
            hooks.Value = "—";
            hooks.ValueTone = "Neutral";
            hooks.Detail = why;
            hooks.SetProgress(0, 0);

            blocks.Value = "—";
            blocks.ValueTone = "Neutral";
            blocks.Detail = _metricsFailure is null ? $"latest {window} decisions" : "audit database unavailable";
            blocks.SetProgress(0, 0);
        }

        hooks.Describe();
        blocks.Describe();
    }

    /// <summary>The Findings row and the recent list, from the alert counts (the same definition as the sidebar badge and the tray).</summary>
    private void ApplyAlerts(AlertCounts counts, bool hasData, string? unavailable)
    {
        _alertsHaveData = hasData;
        _alertsUnavailable = unavailable;

        var row = Metrics[2];
        var total = counts.Total;
        if (!hasData)
        {
            row.Value = "—";
            row.ValueTone = "Neutral";
            row.BarTone = "Ok";
            row.Detail = unavailable is null ? "unacknowledged" : "unavailable";
            row.SetProgress(0, 0);
        }
        else
        {
            // A count at the window's cap is "500+", as the badge says it.
            row.Value = counts.HasMore
                ? total.ToString("N0", CultureInfo.CurrentCulture) + "+"
                : total.ToString("N0", CultureInfo.CurrentCulture);
            row.ValueTone = total > 0 ? "Warn" : "Neutral";
            row.BarTone = total > 0 ? "Warn" : "Ok";
            row.Detail = unavailable is null ? "unacknowledged" : "unacknowledged (last read)";
            row.SetProgress(TrayFlyoutText.FindingsProgress(total), total);
        }

        row.Describe();
        RenderFindingsEmpty();
        SyncFindings(counts);
    }

    /// <summary>What the findings list says when it is empty: nothing waiting, not read yet (or not read because monitoring is paused), or unreadable.</summary>
    private void RenderFindingsEmpty() =>
        FindingsEmptyText = _alertsHaveData
            ? "No unacknowledged findings"
            : _alertsUnavailable is not null
                ? "Unacknowledged findings are unavailable"
                : _services.Monitor.IsPaused ? "Monitoring is paused; findings have not been read" : "Reading findings…";

    /// <summary>Replaces the list only when what it shows changed (an id, or how long ago it reads), so a poll does not rebuild rows under the pointer.</summary>
    private void SyncFindings(AlertCounts counts)
    {
        var now = _time.GetUtcNow();
        var wanted = counts.Newest.Take(RecentFindingLimit).ToList();

        var same = RecentFindings.Count == wanted.Count;
        for (var i = 0; same && i < wanted.Count; i++)
        {
            same = string.Equals(RecentFindings[i].Id, wanted[i].Id, StringComparison.Ordinal) &&
                   string.Equals(RecentFindings[i].When, TrayFlyoutText.Relative(wanted[i].Timestamp, now), StringComparison.Ordinal);
        }

        if (!same)
        {
            RecentFindings.Clear();
            foreach (var item in wanted)
            {
                RecentFindings.Add(new FlyoutFindingRow(
                    item.Id,
                    string.IsNullOrWhiteSpace(item.Action) ? "finding" : item.Action,
                    TrayFlyoutText.SeverityTone(item.Severity),
                    TrayFlyoutText.SeverityWord(item.Severity),
                    TrayFlyoutText.Relative(item.Timestamp, now),
                    item.Target ?? string.Empty,
                    OpenFindingsCommand));
            }
        }

        HasFindings = RecentFindings.Count > 0;
    }

    /// <summary>"Updated 12s ago" while live; while paused, how old the numbers are.</summary>
    private void RenderUpdated()
    {
        var now = _time.GetUtcNow();
        if (IsPaused || _services.Monitor.IsPaused)
        {
            UpdatedText = _updatedAt is { } at
                ? $"{GatewaySnapshot.PausedLabel} · counts as of {TrayFlyoutText.Relative(at, now)}"
                : $"{GatewaySnapshot.PausedLabel} · no counts read";
            return;
        }

        UpdatedText = _updatedAt is { } updated ? $"Updated {TrayFlyoutText.Relative(updated, now)}" : _live is null ? string.Empty : "Updating…";
    }
}
