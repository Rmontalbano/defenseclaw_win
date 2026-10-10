using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The dashboard, in the Mac's order (CUST-209): What Needs Attention, the Services | Scanners | Enforcement hero row, Quick Actions,
/// Configuration, Connectors, Observability destinations, Activity (last 24 h) and Doctor beside the discovered AI agents. This file holds
/// the gateway-derived cards and the two clocks; each of the newer cards is its own partial: <c>.Enforcement</c> (the four tiles),
/// <c>.Actions</c> (Quick Actions and their review), <c>.Configuration</c> (rows and the one <c>status --json</c> read),
/// <c>.Scope</c> (the connector table as the scope selector), <c>.Observability</c>, <c>.Activity</c> (the hourly chart), <c>.Agents</c> and <c>.Services</c> (the TUI's nine service cards).
/// <para>
/// <b>Two clocks.</b> Everything gateway-derived is re-derived on every poll; the audit
/// counts, the enforcement lists and the scanner-path probes are far more expensive, so they
/// refresh on a <see cref="DataRefreshInterval"/> floor and on demand.
/// </para>
/// <para>
/// <b>Only while it is being looked at.</b> The panel listens to
/// <see cref="GatewayMonitor.PollCompleted"/> — not StateChanged, because what it prints
/// (uptime, connector counters, last-activity ages) is exactly what StateChanged ignores —
/// and only between <see cref="OnActivated"/> and <see cref="OnDeactivated"/>. Hidden in the
/// tray it does no work at all; on the way back it re-derives everything from
/// <c>Monitor.Current</c> once, and refreshes the expensive data if it is past its floor.
/// </para>
/// <para>
/// <b>Rows are merged, not rebuilt.</b> Each poll produces the desired rows and
/// <see cref="PanelViewModelBase.SyncCollection{T}"/> reconciles the bound collections with
/// them by key. A row whose content did not change keeps its visuals — the scroll position,
/// and a selection inside the copyable remediation text, used to be destroyed every five
/// seconds by a clear-and-refill — and the two connector fields that tick under a stable
/// row (counters, last-activity age) are updated in place.
/// </para>
/// <para>
/// <b>Disabled is not broken.</b> On a healthy standalone box <c>/health</c> reports
/// <c>application_protection: disabled</c> with <c>last_error: "application protection
/// disabled"</c>, and <c>gateway: disabled</c> because there is no OpenClaw fleet. Both
/// are normal. Only states that actually read as failures get the critical colour, and
/// <c>last_error</c> is shown as a note rather than treated as a failure signal.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel : PanelViewModelBase
{
    /// <summary>Floor between audit/enforcement refreshes driven by the poll loop.</summary>
    public static readonly TimeSpan DataRefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>Window the severity tiles count over.</summary>
    private static readonly TimeSpan CountWindow = TimeSpan.FromHours(24);

    private int _refreshing;
    private DateTimeOffset _lastDataRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// Where the scanners were last found. <c>DefenseClawPaths.FindExecutable</c> keeps only a
    /// short-lived cache (60 s for hits, 10 s for misses), and the Scanners box used to do two
    /// lookups on every poll. They are re-probed on the <see cref="DataRefreshInterval"/> floor
    /// and on a manual refresh instead; an installed-or-not answer does not change between polls.
    /// </summary>
    private string? _skillScannerPath;
    private string? _mcpScannerPath;
    private DateTimeOffset _scannerPathsProbedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// False until the first PATH lookup has answered. The lookup never runs on the UI thread (a dead network
    /// entry on PATH stalls it for ~40 s), so until it lands the Scanners rows say "checking" rather than
    /// claiming a scanner is missing.
    /// </summary>
    private bool _scannerPathsResolved;

    /// <summary>
    /// Runtime enforcement posture per connector, from <c>/status</c>. Kept here rather
    /// than in <see cref="GatewaySnapshot"/> because the monitor only polls <c>/health</c>
    /// and <c>/alerts</c>; this rides the slow refresh, never its own timer.
    /// </summary>
    private IReadOnlyDictionary<string, ConnectorMode> _connectorModes =
        new Dictionary<string, ConnectorMode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True once a <c>/status</c> read has failed and no later one has succeeded. The runtime
    /// hook contract is then unknowable, so <see cref="_connectorModes"/> is emptied rather than
    /// left holding the last answer, and a connector row that can only show config.yaml's
    /// stated intent says so instead of presenting it as what the hook is doing.
    /// </summary>
    private bool _statusUnavailable;

    /// <summary>
    /// The "unverified" note above the Services, Scanners and Connectors boxes (<see cref="HealthTrustPresentation"/>): empty when the
    /// <c>/health</c> answer is from the verified gateway, so the boxes show nothing extra then.
    /// </summary>
    [ObservableProperty]
    private string _healthTrustNote = string.Empty;

    [ObservableProperty]
    private string _gatewayHeadline = "Checking…";

    [ObservableProperty]
    private string _gatewayDetail = string.Empty;

    [ObservableProperty]
    private string _gatewayStateKey = "Neutral";

    [ObservableProperty]
    private string _uptimeText = "—";

    [ObservableProperty]
    private string _versionText = "—";

    [ObservableProperty]
    private string _dataDirectoryText = string.Empty;

    /// <summary>Where <see cref="DataDirectoryText"/> came from: the default, or the <c>DEFENSECLAW_HOME</c> override the CLI honours.</summary>
    [ObservableProperty]
    private string _dataDirectorySourceText = string.Empty;

    [ObservableProperty]
    private string _enforcementSummary = "Not read yet.";

    [ObservableProperty]
    private string _enforcementNote = string.Empty;

    [ObservableProperty]
    private string _auditSummary = "Reading audit.db…";

    [ObservableProperty]
    private string _auditNote = string.Empty;

    [ObservableProperty]
    private string _lastUpdatedText = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    // ---- Doctor card ---------------------------------------------------------------------
    // Read from <data dir>\doctor_cache.json, which `defenseclaw doctor` rewrites on every run
    // (see DoctorCacheReader). The panel never runs doctor on its own: the file is read once on
    // activation, on Refresh and after a run the operator started, and its age text is re-derived
    // (no I/O) on each poll so "as of" and STALE cannot go silently out of date.

    /// <summary>Results older than this are shown as STALE (the DefenseClaw TUI uses the same window).</summary>
    public static readonly TimeSpan DoctorStaleAfter = TimeSpan.FromMinutes(15);

    private DoctorCacheSnapshot? _doctorSnapshot;
    private CancellationTokenSource? _doctorCts;

    /// <summary>True when a cache file exists and parsed; drives the counts + failing-checks half of the card.</summary>
    [ObservableProperty]
    private bool _doctorHasData;

    /// <summary>No cache yet (doctor was never run on this install) - a normal state, not a fault.</summary>
    [ObservableProperty]
    private bool _doctorIsEmpty = true;

    [ObservableProperty]
    private string _doctorVerdict = string.Empty;

    [ObservableProperty]
    private string _doctorSummary = string.Empty;

    [ObservableProperty]
    private string _doctorAsOfText = string.Empty;

    /// <summary>Ok / Warn / Bad / Neutral - tone of the verdict line.</summary>
    [ObservableProperty]
    private string _doctorStateKey = "Neutral";

    [ObservableProperty]
    private bool _doctorIsStale;

    [ObservableProperty]
    private string _doctorProblemsNote = string.Empty;

    [ObservableProperty]
    private bool _doctorHasProblems;

    /// <summary>Set when the cache file exists but could not be read/parsed: distinct from "never run".</summary>
    [ObservableProperty]
    private string _doctorReadError = string.Empty;

    [ObservableProperty]
    private bool _doctorHasReadError;

    [ObservableProperty]
    private bool _isDoctorRunning;

    [ObservableProperty]
    private string _doctorRunMessage = string.Empty;

    [ObservableProperty]
    private bool _doctorHasRunMessage;

    /// <summary>
    /// Cancelled when the panel leaves the screen: the token every read the panel starts on its own (the audit windows, the agents file, the
    /// <c>status --json</c> run) is given, so nothing it began keeps a statement or a process running for a panel nobody is looking at.
    /// </summary>
    private CancellationTokenSource? _activation;

    public OverviewPanelViewModel(AppServices services)
        : base(services)
    {
        // Reading the cached snapshot is not I/O; the poll that produced it already ran. No
        // subscriptions here: OnActivated attaches them, and OnDeactivated lets go.
        DataDirectoryText = Services.Paths.DataDirectory;
        DataDirectorySourceText = Services.Paths.DataDirectoryOrigin.Description;

        // Readers only hold a connection string: nothing is opened until a read is asked for. The totals reader is the app's shared one.
        _metricsReader = Services.HookTotals;
        _hourlyReader = new HourlyActivityReader(Services.Paths.AuditDatabasePath);
        Review = new DiscoverActionReview(Services);
        BuildEnforcementCards();

        Apply(Services.Monitor.Current);
    }

    /// <summary>
    /// The snapshot the panel last applied: what every card that is not re-derived from a read is showing. The roster, the gateway buttons
    /// and the guardrail mode are read from this, not from the monitor, so the panel is consistent with itself between a poll and the next.
    /// </summary>
    private GatewaySnapshot _snapshot = GatewaySnapshot.Initial;

    /// <summary>The token to give a read the panel starts: cancelled when it leaves the screen. <see cref="CancellationToken.None"/> before the first activation.</summary>
    private CancellationToken ActiveToken => _activation?.Token ?? CancellationToken.None;

    /// <summary>The argv the Run doctor button hands the CLI: plain <c>doctor</c>, never <c>--fix</c>.</summary>
    internal static readonly string[] DoctorArgv = { "doctor" };

    /// <summary>What the Run doctor button executes: <c>defenseclaw doctor</c>, never <c>--fix</c>.</summary>
    public string DoctorCommandText => CommandReview.CommandLine(CommandReview.DefaultExecutable, DoctorArgv);

    /// <summary>Failing then warning checks from the cache (all of them, capped for the card).</summary>
    public ObservableCollection<DoctorCheckRow> DoctorChecks { get; } = new();

    /// <summary>PASS / FAIL / WARN / SKIP counts from the cache.</summary>
    public ObservableCollection<CountTile> DoctorTiles { get; } = new();

    public override string Title => "Overview";

    public override string Description =>
        "Service health, scanners, enforcement posture and connector state at a glance.";

    /// <summary>Derived: the short list of things an operator should actually look at.</summary>
    public ObservableCollection<AttentionRow> Attention { get; } = new();

    /// <summary>The TUI's nine service cards, in its order (<c>.Services</c>).</summary>
    public ObservableCollection<ServiceRow> ServiceRows { get; } = new();

    public ObservableCollection<ScannerRow> ScannerRows { get; } = new();

    public ObservableCollection<ConnectorRow> ConnectorRows { get; } = new();

    /// <summary>Severity counts over <see cref="CountWindow"/>, from the audit DB.</summary>
    public ObservableCollection<CountTile> SeverityTiles { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Apply(Services.Monitor.Current);

        // The doctor card first: it is one small file read, so the card is populated before the
        // slower audit / enforcement / status reads below finish.
        await ReloadDoctorCacheAsync(cancellationToken);
        await RefreshDataAsync(cancellationToken);
    }

    /// <summary>
    /// Everything the slow cadence reads for the new cards, after the original audit and enforcement reads: the tiles' audit window, the
    /// day's hourly decisions, the agents file, and <c>status --json</c> when it is due (<paramref name="forceStatus"/> forces it: Refresh).
    /// Each is independent and none throws past here, so one slow or failing read never blanks the others.
    /// </summary>
    private async Task RefreshCardsAsync(bool forceStatus, CancellationToken cancellationToken)
    {
        await RefreshMetricsAsync(force: true, cancellationToken).ConfigureAwait(true);
        await RefreshHourlyAsync(cancellationToken).ConfigureAwait(true);
        await RefreshAgentsAsync(cancellationToken).ConfigureAwait(true);
        RefreshBomCoverage();
        await RefreshStatusAsync(forceStatus, cancellationToken).ConfigureAwait(true);

        // The compiled observability plan has its own age limit (and is read again when config.yaml changes), so this is a comparison unless it is due.
        await RefreshObservabilityPlanAsync(forceStatus, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Attaches to the poll and config-reload notifications, then catches up once: the panel
    /// may have been off screen for hours, so everything gateway-derived is re-derived from
    /// the monitor's current snapshot (a no-op merge if nothing moved) and the expensive data
    /// is refreshed if it is past its floor. A refresh already in flight — the first visit
    /// overlaps <see cref="InitializeAsync"/> — makes that call a no-op via the
    /// <c>_refreshing</c> guard.
    /// </summary>
    protected override void OnActivated()
    {
        _activation?.Dispose();
        _activation = new CancellationTokenSource();

        Services.Monitor.PollCompleted += OnPollCompleted;
        Services.ConfigReloaded += OnConfigReloaded;

        // A queued gateway restart is app-wide (CUST-267): a save or a run on another panel queues one, a restart anywhere applies it.
        Services.RestartQueue.Changed += OnRestartQueueChanged;

        // The scope and the unacknowledged-findings count are shared with the rest of the app (the sidebar badge, the tray): the panel
        // follows them only while it is on screen. Subscribing to the counts is what keeps their 30 s read going, which the badge
        // already does; it is one query of a few milliseconds.
        Services.ConnectorScope.Changed += OnScopeChanged;
        Services.AlertCounts.Changed += OnAlertCountsChanged;

        Apply(Services.Monitor.Current);
        ApplyScope();
        RefreshDataIfDue();
        RefreshBomCoverage();

        // A config.yaml change while the panel was away: the plan on screen was compiled from the old one (a comparison unless it is stale).
        _ = RefreshObservabilityPlanAsync(force: false, ActiveToken);

        // One file read per activation: a doctor run from a terminal (or the TUI) while the panel
        // was away rewrote the cache, and this is the only time it is picked up without a Refresh.
        _ = ReloadDoctorCacheAsync(CancellationToken.None);

        // The audit window is a few milliseconds and the tiles say how old it is: catch up now rather than at the next poll.
        _ = RefreshMetricsAsync(force: false, ActiveToken);
    }

    protected override void OnDeactivated()
    {
        Services.Monitor.PollCompleted -= OnPollCompleted;
        Services.ConfigReloaded -= OnConfigReloaded;
        Services.RestartQueue.Changed -= OnRestartQueueChanged;
        Services.ConnectorScope.Changed -= OnScopeChanged;
        Services.AlertCounts.Changed -= OnAlertCountsChanged;

        // Stop what the panel started for itself: a running audit statement ends at once, and a status run's process tree is killed. A
        // diagnostic the operator asked for is not stopped: they may well have gone to Activity to watch it.
        try
        {
            _activation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a re-activation that raced this; nothing left to cancel.
        }
    }

    /// <summary>Also what F5 invokes: an <see cref="IAsyncRelayCommand"/> that disables itself while it runs.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var snapshot = await Services.Monitor.RefreshAsync();

        // "Refresh" means look again, including whether the scanners are installed and what `status --json` says.
        _scannerPathsProbedAt = DateTimeOffset.MinValue;
        Apply(snapshot);
        _lastDataRefresh = DateTimeOffset.MinValue;
        _forceStatus = true;
        await ReloadDoctorCacheAsync(CancellationToken.None);
        await RefreshDataAsync(ActiveToken);
    }

    /// <summary>Set by Refresh so the next data refresh re-reads <c>status --json</c> whatever its age.</summary>
    private bool _forceStatus;

    /// <summary>
    /// Runs <c>defenseclaw doctor</c> (10-30 s of live probes; it writes only its own results
    /// cache), then re-reads that cache. Plain <c>doctor</c> is on the explicit allow-list of known reads
    /// (<see cref="CommandReview.MayRunUnreviewed(IReadOnlyList{string})"/>: the list <em>and</em> <see cref="CommandTiers"/>
    /// agree), so it runs with no <see cref="CommandReview"/> in front of it. This panel offers no
    /// other command to run (the fixes on its attention rows are text to copy, never run), so it has no
    /// review surface of its own; if <c>doctor</c> ever came off the list this action refuses
    /// rather than silently running an unreviewed command. <c>doctor --fix</c> is
    /// deliberately not offered here.
    /// </summary>
    [RelayCommand]
    private async Task RunDoctorAsync()
    {
        var argv = DoctorArgv;
        if (!CommandReview.MayRunUnreviewed(argv))
        {
            SetDoctorRunMessage("Doctor is not on the list of commands known to be read-only, so it will not run without a review step.");
            return;
        }

        // The command disables itself while it runs, so there is never a previous source to replace.
        var cts = new CancellationTokenSource();
        _doctorCts = cts;

        IsDoctorRunning = true;
        SetDoctorRunMessage("Running 'defenseclaw doctor' - it probes every configured service and usually takes 10-30 seconds. The previous results stay visible until it finishes.");

        try
        {
            var invocation = await Services.Cli.RunAsync(argv, cancellationToken: cts.Token).ConfigureAwait(true);
            var finished = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                // Cancelled or timed out: the CLI never reached the point where it writes the cache.
                SetDoctorRunMessage($"Doctor did not finish: {reason}. The results below are from the previous run.");
            }
            else if (invocation.ExitCode == 0)
            {
                SetDoctorRunMessage($"Doctor finished at {finished}: every check passed.");
            }
            else
            {
                // Exit 1 means "found failures", which is the tool working, not the tool failing.
                SetDoctorRunMessage(
                    $"Doctor finished at {finished} (exit {(invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "?")}). " +
                    "It reported failures or warnings - see the list below, or Activity for the full output.");
            }
        }
        catch (CliNotFoundException ex)
        {
            SetDoctorRunMessage($"'defenseclaw' was not found: {ex.Message}");
        }
        catch (SecretInArgumentException ex)
        {
            SetDoctorRunMessage(ex.Message);
        }
        finally
        {
            IsDoctorRunning = false;
            _doctorCts = null;
            cts.Dispose();
        }

        await ReloadDoctorCacheAsync(CancellationToken.None);
    }

    /// <summary>Stops a doctor run in flight; the run's whole process tree is killed by the runner.</summary>
    [RelayCommand]
    private void CancelDoctor()
    {
        try
        {
            _doctorCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished in the instant the button was pressed; nothing left to cancel.
        }
    }

    private void SetDoctorRunMessage(string message)
    {
        DoctorRunMessage = message;
        DoctorHasRunMessage = message.Length > 0;
    }

    /// <summary>
    /// Reads and parses <c>doctor_cache.json</c>. A missing file is "never run" (normal on a fresh
    /// install), an unreadable or malformed one is a separate, visible state - the card never
    /// shows stale or partial numbers as if they were results.
    /// </summary>
    internal async Task ReloadDoctorCacheAsync(CancellationToken cancellationToken)
    {
        var path = Services.Paths.DoctorCachePath;
        try
        {
            if (!File.Exists(path))
            {
                _doctorSnapshot = null;
                DoctorReadError = string.Empty;
                DoctorHasReadError = false;
            }
            else
            {
                var json = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
                _doctorSnapshot = DoctorCacheReader.Parse(json);
                DoctorReadError = string.Empty;
                DoctorHasReadError = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _doctorSnapshot = null;
            DoctorReadError = $"{path} could not be read: {ex.Message}";
            DoctorHasReadError = true;
        }

        ApplyDoctorState();
        BuildAttention(_snapshot);
    }

    /// <summary>
    /// Derives everything the card shows from the parsed cache and the clock. No I/O, so it is
    /// safe to call on every poll: that is what keeps "as of 14:02 - 9 m ago" and the STALE
    /// badge honest between reads.
    /// </summary>
    private void ApplyDoctorState()
    {
        var snapshot = _doctorSnapshot;
        if (snapshot is null)
        {
            DoctorHasData = false;
            DoctorIsEmpty = !DoctorHasReadError;
            DoctorVerdict = string.Empty;
            DoctorSummary = string.Empty;
            DoctorAsOfText = string.Empty;
            DoctorIsStale = false;
            DoctorStateKey = "Neutral";
            DoctorHasProblems = false;
            DoctorProblemsNote = string.Empty;
            SyncTiles(DoctorTiles, Array.Empty<CountTile>());
            SyncByEquality(DoctorChecks, Array.Empty<DoctorCheckRow>(), static row => row.Label);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stale = snapshot.IsStale(now, DoctorStaleAfter);

        // The Mac's liveHealthContradicts: a cached fail/warn for a subsystem /health says is running is a stale result, not a failure.
        var health = _snapshot.Health;
        var staleFailures = snapshot.Checks.Count(c => c.Status == "fail" && DoctorReconciliation.LiveHealthContradicts(c, health));
        var staleWarnings = snapshot.Checks.Count(c => c.Status == "warn" && DoctorReconciliation.LiveHealthContradicts(c, health));
        var failed = Math.Max(snapshot.Failed - staleFailures, 0);
        var warned = Math.Max(snapshot.Warned - staleWarnings, 0);
        var staleCount = staleFailures + staleWarnings;

        DoctorHasData = true;
        DoctorIsEmpty = false;
        DoctorIsStale = stale;
        DoctorAsOfText = snapshot.CapturedAt is { } at
            ? $"as of {FormatCapturedAt(at)} - {Relative(at)}"
            : "capture time unknown";

        DoctorSummary =
            $"{snapshot.Passed.ToString("N0", CultureInfo.CurrentCulture)} pass · " +
            $"{failed.ToString("N0", CultureInfo.CurrentCulture)} fail · " +
            $"{warned.ToString("N0", CultureInfo.CurrentCulture)} warn · " +
            (staleCount > 0 ? $"{staleCount.ToString("N0", CultureInfo.CurrentCulture)} stale · " : string.Empty) +
            $"{snapshot.Skipped.ToString("N0", CultureInfo.CurrentCulture)} skip";

        if (failed > 0)
        {
            DoctorVerdict = failed == 1 ? "1 check failed" : $"{failed} checks failed";
            DoctorStateKey = "Bad";
        }
        else if (warned > 0)
        {
            DoctorVerdict = warned == 1 ? "1 warning" : $"{warned} warnings";
            DoctorStateKey = "Warn";
        }
        else if (staleCount > 0)
        {
            DoctorVerdict = staleCount == 1 ? "1 stale result" : $"{staleCount} stale results";
            DoctorStateKey = "Neutral";
        }
        else if (snapshot.Passed > 0)
        {
            DoctorVerdict = "All checks passed";

            // An old all-green is not evidence of anything current: neutral, not green.
            DoctorStateKey = stale ? "Neutral" : "Ok";
        }
        else
        {
            DoctorVerdict = "No checks recorded";
            DoctorStateKey = "Neutral";
        }

        SyncTiles(DoctorTiles, new[]
        {
            new CountTile { Label = "PASS", Value = snapshot.Passed.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = snapshot.Passed > 0 ? "Ok" : "Info" },
            new CountTile { Label = "FAIL", Value = failed.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = failed > 0 ? "Critical" : "Info" },
            new CountTile { Label = "WARN", Value = warned.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = warned > 0 ? "High" : "Info" },
            new CountTile { Label = "SKIP", Value = snapshot.Skipped.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = "Info" },
        });

        var problems = snapshot.Problems()
            .Select(c => DoctorReconciliation.LiveHealthContradicts(c, health)
                ? c with { IsStale = true, Detail = c.Detail.Length > 0 ? c.Detail + " (live state OK)" : string.Empty }
                : c)
            .ToList();
        var shown = problems.Take(MaxDoctorProblemsShown).ToList();
        DoctorHasProblems = shown.Count > 0;
        DoctorProblemsNote = problems.Count > shown.Count
            ? $"and {problems.Count - shown.Count} more - run 'defenseclaw doctor' in a terminal for the full list."
            : string.Empty;
        SyncByEquality(DoctorChecks, shown, static row => row.Label);
    }

    /// <summary>Failing/warning checks shown on the card before "and N more".</summary>
    private const int MaxDoctorProblemsShown = 8;

    private static string FormatCapturedAt(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The reload originates on the config watcher's thread, but AppServices marshals
    /// <c>ConfigReloaded</c> onto the Dispatcher before raising it, so — as with
    /// <see cref="OnPollCompleted"/> — this is the UI thread and <see cref="Attention"/>
    /// can be merged directly. Only attached while active; a reload that lands while the
    /// panel is away is picked up by the catch-up in <see cref="OnActivated"/>.
    /// </summary>
    private void OnConfigReloaded(object? sender, EventArgs e)
    {
        Apply(Services.Monitor.Current);

        // The observability plan is compiled from config.yaml: the reader sees the new document and runs the command again.
        _ = RefreshObservabilityPlanAsync(force: false, ActiveToken);
    }

    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e)
    {
        // GatewayMonitor posts on the UI SynchronizationContext, so this is already the
        // UI thread and the collections below can be mutated directly.
        Apply(e.Snapshot);
        RefreshDataIfDue();

        // The hook-call and block counts are a few milliseconds: they follow the poll, gated to MetricsRefreshInterval.
        _ = RefreshMetricsAsync(force: false, ActiveToken);
    }

    private void RefreshDataIfDue()
    {
        if (DefenseClaw.Core.Time.WallClock.Elapsed(_lastDataRefresh) >= DataRefreshInterval)
        {
            _ = RefreshDataAsync(ActiveToken);
        }
    }

    /// <summary>Renders everything derivable from one snapshot plus config.yaml.</summary>
    internal void Apply(GatewaySnapshot snapshot)
    {
        _snapshot = snapshot;
        var health = snapshot.Health;

        GatewayHeadline = snapshot.StateLabel;
        GatewayDetail = snapshot.Detail;
        GatewayStateKey = snapshot.State switch
        {
            AppGatewayState.Running => "Ok",
            AppGatewayState.Degraded or AppGatewayState.WslGatewayDetected => "Warn",
            AppGatewayState.Unknown => "Neutral",
            _ => "Bad",
        };

        UptimeText = health is { UptimeMs: > 0 } ? HealthTrustPresentation.Mark(FormatDuration(health.Uptime), snapshot) : "—";
        HealthTrustNote = HealthTrustPresentation.NoteFor(snapshot);
        VersionText = string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? "—"
            : snapshot.PeerUnverified
                ? $"DefenseClaw {snapshot.BinaryVersion} (unverified)"
                : $"DefenseClaw {snapshot.BinaryVersion}";
        LastUpdatedText = snapshot.PolledAt == DateTimeOffset.MinValue
            ? string.Empty
            : $"Polled {snapshot.PolledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";

        BuildAttention(snapshot);
        BuildServices(health);
        BuildScanners(snapshot, health);
        BuildConnectors(snapshot, health);
        BuildObservability(health);
        ApplyGatewayActions(snapshot);

        // Nothing below does I/O: each re-derives a card from what the panel already holds, so the tiles' ages, the roster-driven rows and
        // the Configuration card follow the poll without a read of their own.
        RenderEnforcementCards();
        BuildConfiguration();

        // No I/O: re-derives the doctor card's "as of" text and STALE flag from the clock.
        ApplyDoctorState();
    }

    /// <summary>
    /// The whole point of the panel: four independent signals collapsed into one list,
    /// with an explicit "nothing needs attention" row so an empty box never reads as a
    /// rendering failure.
    /// </summary>
    private void BuildAttention(GatewaySnapshot snapshot)
    {
        var rows = new List<AttentionRow>();

        if (Services.ConfigLoadError is { Length: > 0 } configError)
        {
            rows.Add(new AttentionRow
            {
                Title = "config.yaml could not be read",
                Detail = configError,
                SeverityKey = "Critical",
            });
        }

        switch (snapshot.State)
        {
            case AppGatewayState.WslGatewayDetected:
                rows.Add(new AttentionRow
                {
                    Title = "A WSL gateway owns the API port",
                    Detail = snapshot.PortOwner is { } owner
                        ? $"Port {snapshot.ApiPort} is held by {owner.ProcessName} (pid {owner.Pid}). Everything shown " +
                          "describes the WSL instance, not the native install."
                        : $"Port {snapshot.ApiPort} is relayed from WSL, not served by the native install.",
                    SeverityKey = "High",
                });
                break;

            case AppGatewayState.GatewayStopped:
                rows.Add(new AttentionRow
                {
                    Title = "The gateway is not answering",
                    Detail = snapshot.Detail + " Audit, Logs and Activity still work from disk.",
                    SeverityKey = "High",
                    Command = GatewaySnapshot.StartGatewayCommand,
                });
                break;

            case AppGatewayState.Degraded:
                rows.Add(new AttentionRow
                {
                    Title = "The gateway answered, but not cleanly",
                    Detail = snapshot.Detail,
                    SeverityKey = "Medium",
                });
                break;

            case AppGatewayState.NotInstalled:
                rows.Add(new AttentionRow
                {
                    Title = "DefenseClaw was not found",
                    Detail = snapshot.Detail,
                    SeverityKey = "Critical",
                });
                break;

            case AppGatewayState.NotInitialized:
                rows.Add(new AttentionRow
                {
                    Title = "Installed, but never initialized",
                    Detail = snapshot.Detail,
                    SeverityKey = "Medium",
                    Command = GatewaySnapshot.InitCommand,
                });
                break;

            default:
                break;
        }

        // A saved change the running gateway does not have yet (CUST-267), right after what the gateway itself is doing: it is what Restart now is for.
        AppendRestartPending(rows);

        // Observe + fail-closed: the recurring bad default. config.yaml is the operator's
        // stated intent; /status carries what the running hook contract actually does, and
        // the two disagreeing is itself worth surfacing.
        var configured = Services.Config.Config.Guardrail.Connectors;
        foreach (var (name, settings) in configured)
        {
            if (settings.HasFailModeMismatch)
            {
                rows.Add(new AttentionRow
                {
                    Title = $"{name}: observe mode with a fail-closed hook",
                    Detail = "The connector only observes, but a hook failure blocks the agent anyway. " +
                             "Pair observe with hook_fail_mode: open, or move to enforce.",
                    SeverityKey = "High",
                });
            }
        }

        // The third opinion, and the one that wins: Claude Code's own settings.json can
        // hold an env override the hook obeys over both config.yaml and /status.
        if (snapshot.FailModeDrift is { } drift)
        {
            rows.Add(new AttentionRow
            {
                Title = "claudecode: settings.json overrides the hook fail mode",
                Detail = $"{drift.SettingsPath} sets {ClaudeSettingsReader.FailModeVariableName}={drift.EnvFailMode}, " +
                         $"but {drift.GatewaySource} says hook_fail_mode: {drift.GatewayFailMode}. The hook obeys the " +
                         "env var, so this is the effective behavior — the Setup installer is known to re-plant " +
                         "fail-closed on upgrade." +
                         (drift.IsDangerousPairing
                             ? " With the connector in observe mode, a gateway outage blocks the agent it is only " +
                               "meant to watch."
                             : string.Empty),
                SeverityKey = drift.IsDangerousPairing ? "Critical" : "High",
                Command = drift.RemediationCommand,
            });
        }

        // The Mac's remaining rules (CUST-213): detected-but-unconfigured agents, guardrail, doctor, missing keys, drift, zero requests.
        AppendParityAttention(rows, snapshot);

        if (snapshot.CriticalAlertCount > 0)
        {
            rows.Add(new AttentionRow
            {
                Title = $"{snapshot.CriticalAlertCount} CRITICAL alert{(snapshot.CriticalAlertCount == 1 ? string.Empty : "s")} in the last poll",
                Detail = "Open the Alerts panel for the matched rule, evidence and target.",
                SeverityKey = "Critical",
            });
        }

        var high = snapshot.RecentAlerts.Count(a => string.Equals(a.Severity, "HIGH", StringComparison.OrdinalIgnoreCase));
        if (high > 0)
        {
            // The row's tone is the finding's own severity: HIGH is the High (amber) tone everywhere else in the
            // app (Alerts, Audit, the doctor WARN tile). The detail below is what says these are usually benign;
            // painting the row Medium (blue) understated it (CUST-180).
            rows.Add(new AttentionRow
            {
                Title = $"{high} HIGH finding{(high == 1 ? string.Empty : "s")} in the last {GatewayMonitor.AlertLimit} alerts",
                Detail = "On a development box these are usually the agent's own commands tripping hook rules.",
                SeverityKey = "High",
            });
        }

        // The gateway's own admission that it cannot keep its event history (for instance sqlite_write_failed). It used to sit in /health
        // unread; if audit.db is not being written, what every other card here counts is going stale.
        if (snapshot.Health is { } reported && FindFailure(reported) is { } historyFailure)
        {
            rows.Add(new AttentionRow
            {
                Title = "The gateway cannot keep its event history",
                Detail = $"/health reports event_history_failure: {historyFailure}. Events may not be reaching audit.db; the Observability card lists the destinations.",
                SeverityKey = "High",
            });
        }

        // NotConnected/Unauthorized are informational, never errors.
        if (snapshot.AlertsUnavailable is { Length: > 0 } alertsReason && !snapshot.IsDegraded)
        {
            rows.Add(new AttentionRow
            {
                Title = "Alerts are not being served",
                Detail = alertsReason,
                SeverityKey = "Info",
            });
        }

        if (rows.Count == 0)
        {
            if (snapshot.State == AppGatewayState.Unknown)
            {
                // Before the first poll nothing has been checked, so "the gateway is healthy" would
                // be an assertion with no evidence behind it. Only a Running snapshot reaches the
                // reassurance below: every other state adds its own row above.
                rows.Add(new AttentionRow
                {
                    Title = "Checking the gateway…",
                    Detail = snapshot.Detail.Length > 0
                        ? snapshot.Detail
                        : "The gateway state has not been determined yet.",
                    SeverityKey = "Info",
                });
            }
            else
            {
                rows.Add(new AttentionRow
                {
                    Title = "Nothing needs attention",
                    Detail = "The gateway is healthy, no CRITICAL alerts in the last poll, no fail-mode mismatch, " +
                             "and settings.json agrees with the gateway on the hook fail mode.",
                    SeverityKey = "Ok",
                });
            }
        }

        SyncByEquality(Attention, rows, static row => row.Title);
        RenderVisibleAttention();

        // Every input of the reviewed buttons (config.yaml, the doctor cache, the snapshot) changes with a rebuild of this list: they follow it.
        ApplyQuickActions();
        RefreshRestartPendingActions();
    }

    /// <summary>How many rows "What needs attention" shows before "Show all": the Mac's top three.</summary>
    internal const int AttentionRowsShown = 3;

    /// <summary>The rows on screen: the first <see cref="AttentionRowsShown"/> of <see cref="Attention"/>, or all of them once expanded.</summary>
    public ObservableCollection<AttentionRow> VisibleAttention { get; } = new();

    [ObservableProperty]
    private bool _showAllAttention;

    /// <summary>"Show all 5" or "Show fewer"; empty when everything already fits.</summary>
    [ObservableProperty]
    private string _attentionMoreText = string.Empty;

    [ObservableProperty]
    private bool _hasAttentionOverflow;

    [RelayCommand]
    private void ToggleAttention()
    {
        ShowAllAttention = !ShowAllAttention;
        RenderVisibleAttention();
    }

    private void RenderVisibleAttention()
    {
        var overflow = Attention.Count > AttentionRowsShown;
        HasAttentionOverflow = overflow;
        if (!overflow)
        {
            ShowAllAttention = false;
        }

        AttentionMoreText = !overflow
            ? string.Empty
            : ShowAllAttention ? "Show fewer" : $"Show all {Attention.Count.ToString(CultureInfo.CurrentCulture)}";

        var wanted = ShowAllAttention ? Attention.ToList() : Attention.Take(AttentionRowsShown).ToList();
        SyncByEquality(VisibleAttention, wanted, static row => row.Title);
    }

    /// <summary>
    /// Refreshes the two scanner executables' locations at most once per <see cref="DataRefreshInterval"/>;
    /// see <see cref="_skillScannerPath"/> for why it is not once per poll. Runs on the UI thread, so it only
    /// <i>reads</i> the last known answer; the lookup itself is started on the pool and applied when it returns
    /// (<see cref="RefreshScannerPathsAsync"/>).
    /// </summary>
    private void EnsureScannerPathsProbed()
    {
        if (DefenseClaw.Core.Time.WallClock.Elapsed(_scannerPathsProbedAt) < DataRefreshInterval)
        {
            return;
        }

        var paths = Services.Paths;
        var skillKnown = paths.TryGetKnownExecutable("skill-scanner", out var skill);
        var mcpKnown = paths.TryGetKnownExecutable("mcp-scanner", out var mcp);
        if (skillKnown && mcpKnown)
        {
            _skillScannerPath = skill;
            _mcpScannerPath = mcp;
            _scannerPathsResolved = true;
        }

        _scannerPathsProbedAt = DateTimeOffset.UtcNow;

        // Off the UI thread (a test, a tool: no window to keep responsive) there is no single thread to marshal the answer back to. With
        // no synchronization context, or one that posts to other threads (xunit's does), a continuation would rebuild ScannerRows on
        // another thread while the caller is still in Apply: a race on the collection. So the answer is taken here, on the calling
        // thread. On the UI thread, where the lookup must never wait (a dead PATH entry stalls it), it runs on the pool and is applied
        // back on that thread.
        if (SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
        {
            _skillScannerPath = paths.FindExecutable("skill-scanner");
            _mcpScannerPath = paths.FindExecutable("mcp-scanner");
            _scannerPathsResolved = true;
            return;
        }

        _ = RefreshScannerPathsAsync();
    }

    /// <summary>
    /// Waits (off the UI thread) for the current answer for both scanners and, if it differs from what the rows
    /// show, re-renders them. The continuation resumes on the UI thread, so the row collections are touched only there.
    /// </summary>
    private async Task RefreshScannerPathsAsync()
    {
        try
        {
            var skillTask = Services.Paths.FindExecutableAsync("skill-scanner");
            var mcpTask = Services.Paths.FindExecutableAsync("mcp-scanner");
            var skill = await skillTask.ConfigureAwait(true);
            var mcp = await mcpTask.ConfigureAwait(true);

            if (_scannerPathsResolved &&
                string.Equals(skill, _skillScannerPath, StringComparison.Ordinal) &&
                string.Equals(mcp, _mcpScannerPath, StringComparison.Ordinal))
            {
                return;
            }

            _skillScannerPath = skill;
            _mcpScannerPath = mcp;
            _scannerPathsResolved = true;

            var snapshot = Services.Monitor.Current;
            BuildScanners(snapshot, snapshot.Health);
        }
        catch (Exception ex)
        {
            // Reached only if a filesystem probe threw; the rows keep whatever they show.
            Trace.TraceWarning($"Looking up the scanner executables failed: {ex.Message}");
        }
    }

    private void BuildScanners(GatewaySnapshot snapshot, GatewayHealth? health)
    {
        EnsureScannerPathsProbed();

        var rows = new List<ScannerRow>
        {
            ExecutableRow("skill-scanner", _skillScannerPath, _scannerPathsResolved),
            ExecutableRow("mcp-scanner", _mcpScannerPath, _scannerPathsResolved),
            new()
            {
                Name = "codeguard",
                StateText = "built-in",
                StateKey = "Ok",
                Detail = "Ships inside the gateway binary; nothing to install.",
            },
        };

        if (health?.Watcher is { } watcher)
        {
            var skillDirs = DetailNumber(watcher, "skill_dirs");
            var pluginDirs = DetailNumber(watcher, "plugin_dirs");
            rows.Add(new ScannerRow
            {
                Name = "watcher",
                StateText = watcher.State ?? "unknown",
                StateKey = ClassifyState(watcher),
                Detail = $"{skillDirs?.ToString(CultureInfo.CurrentCulture) ?? "?"} skill dirs · " +
                         $"{pluginDirs?.ToString(CultureInfo.CurrentCulture) ?? "?"} plugin dirs · " +
                         $"take action: skill={Flag(watcher, "skill_take_action")} " +
                         $"mcp={Flag(watcher, "mcp_take_action")} plugin={Flag(watcher, "plugin_take_action")}",
            });
        }

        if (health?.AiDiscovery is { } discovery)
        {
            var signals = DetailNumber(discovery, "active_signals");
            var lastScan = discovery.DetailString("last_scan");
            rows.Add(new ScannerRow
            {
                Name = "ai discovery",
                StateText = discovery.State ?? "unknown",
                StateKey = ClassifyState(discovery),
                Detail = $"mode {discovery.DetailString("mode") ?? "?"} · " +
                         $"{signals?.ToString(CultureInfo.CurrentCulture) ?? "?"} active signals" +
                         (string.IsNullOrWhiteSpace(lastScan) ? string.Empty : $" · last scan {lastScan}"),
            });
        }

        if (snapshot.CliPath is { Length: > 0 } cli)
        {
            rows.Add(new ScannerRow
            {
                Name = "defenseclaw CLI",
                StateText = "found",
                StateKey = "Ok",
                Detail = cli,
            });
        }

        // Scoped to one connector the Mac puts that connector's policy first: its mode and rule pack.
        if (Services.ConnectorScope.Current is { } scope)
        {
            Services.Config.Config.Guardrail.Connectors.TryGetValue(scope, out var settings);
            rows.Insert(0, new ScannerRow
            {
                Name = "policy",
                StateText = ModeOf(scope) ?? "—",
                StateKey = "Medium",
                Detail = $"{scope.ToLowerInvariant()} · rule pack {RulePackName(settings?.RulePackDir)}",
            });
        }

        SyncByEquality(ScannerRows, rows, static row => row.Name);

        // The attention list has a row for a missing skill scanner: it follows this lookup (Apply builds the list before the lookup has answered).
        RefreshScannerNotice();
    }

    /// <summary>
    /// Connector posture, unioned from config.yaml (stated intent) and <c>/health</c>
    /// (what is actually running and its counters).
    /// </summary>
    private void BuildConnectors(GatewaySnapshot snapshot, GatewayHealth? health)
    {
        var configured = Services.Config.Config.Guardrail.Connectors;
        var live = new Dictionary<string, ConnectorStatus>(StringComparer.OrdinalIgnoreCase);

        if (health?.Connector is { Name: { Length: > 0 } primaryName } primary)
        {
            live[primaryName] = primary;
        }

        if (health is not null)
        {
            foreach (var connector in health.Connectors)
            {
                if (connector.Name is { Length: > 0 } name)
                {
                    live[name] = connector;
                }
            }
        }

        var names = new List<string>();
        foreach (var name in snapshot.ActiveConnectors.Concat(live.Keys).Concat(configured.Keys))
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        var alertCounts = Services.AlertCounts.Current.ByConnector;
        var rows = new List<ConnectorRow>();
        foreach (var name in names)
        {
            configured.TryGetValue(name, out var settings);
            live.TryGetValue(name, out var status);
            _connectorModes.TryGetValue(name, out var runtime);
            var reported = _status.Connector(name);

            // config.yaml is stated intent; /status is what the running hook contract does.
            // They can disagree — say so rather than picking a winner. With /status down there
            // is no runtime side at all, and config.yaml's value is labelled as what it is.
            // `defenseclaw status --json` (when it has been read) says what the hook EFFECTIVELY obeys, which outranks both.
            var mode = runtime?.GuardrailMode ?? ConfigOnly(settings?.Mode);
            var effectiveFailMode = reported?.FailMode?.Effective;
            var failMode = effectiveFailMode is { Length: > 0 } ? effectiveFailMode : runtime?.HookFailMode ?? ConfigOnly(settings?.HookFailMode);
            var mismatch = (settings?.HasFailModeMismatch ?? false) || (runtime?.HasFailModeMismatch ?? false) ||
                           (Equal(ModeOf(name), "observe") && Equal(effectiveFailMode, "closed"));

            var drift = runtime is not null && settings is not null &&
                        (!Equal(runtime.GuardrailMode, settings.Mode) ||
                         !Equal(runtime.HookFailMode, settings.HookFailMode))
                ? $"config.yaml says mode {settings.Mode ?? "—"} / fail-mode {settings.HookFailMode ?? "—"}"
                : string.Empty;
            if (reported?.FailMode is { HasDrift: true, Drift.Count: > 0 } failDrift)
            {
                var sources = "fail-mode drift: " + string.Join(", ", failDrift.Drift);
                drift = drift.Length > 0 ? drift + " · " + sources : sources;
            }

            rows.Add(new ConnectorRow
            {
                Name = name,
                Friendly = reported?.Friendly ?? string.Empty,
                RulePack = RulePackName(settings?.RulePackDir),
                Alerts = alertCounts.TryGetValue(name, out var tally) ? tally.Total : 0,
                Calls = status?.Requests ?? 0,
                Blocks = (status?.ToolBlocks ?? 0) + (status?.SubprocessBlocks ?? 0),
                LastActivityShort = status?.LastActivityAt is { } seen ? Relative(seen) : "never",
                StateText = status?.State ?? (settings is null ? "not configured" : "not running"),
                StateKey = status is null ? "Neutral" : ClassifyText(status.State),
                Mode = mode,
                FailMode = failMode,
                Source = status?.Source ?? "config.yaml",
                Counters = status is null
                    ? string.Empty
                    : $"{status.Requests.ToString("N0", CultureInfo.CurrentCulture)} requests · " +
                      $"{status.Errors.ToString("N0", CultureInfo.CurrentCulture)} errors · " +
                      $"{status.ToolInspections.ToString("N0", CultureInfo.CurrentCulture)} inspections · " +
                      $"{status.ToolBlocks.ToString("N0", CultureInfo.CurrentCulture)} tool blocks · " +
                      $"{status.SubprocessBlocks.ToString("N0", CultureInfo.CurrentCulture)} subprocess blocks",
                Surface = status is null
                    ? string.Empty
                    : $"tool inspection: {status.ToolInspectionMode ?? "—"} · subprocess: {status.SubprocessPolicy ?? "—"}" +
                      (runtime?.EnforcementSurface is { Length: > 0 } surface ? $" · surface: {surface}" : string.Empty),
                Drift = drift,
                LastActivity = status?.LastActivityAt is { } activity
                    ? $"last activity {Relative(activity)}"
                    : string.Empty,
                HasWarning = mismatch,
                WarningText = mismatch
                    ? "observe mode paired with a fail-closed hook — a hook failure blocks the agent it is only meant to watch"
                    : string.Empty,
            });
        }

        // Detected by the AI-discovery scan but not configured: the Mac's "○ not configured" rows with an Add (CUST-210).
        rows.AddRange(BuildUnconfiguredRows(names));

        // Structure is compared, the fields that tick are not: counters move on nearly
        // every poll of a busy box and "last activity 5s ago" is a different string every
        // time, and replacing the row for either would rebuild the whole card. They are
        // copied onto the row that stays.
        //
        // The table's selection is the operator's scope, and a list box clears its selection when the selected item leaves the collection, which
        // a replaced row does for a moment. Nothing in that sweep may reach the scope: the selection is put back by name afterwards.
        var selectedName = _selectionName;
        _syncingSelection = true;
        try
        {
            SyncCollection(
                ConnectorRows,
                rows,
                static row => row.Name,
                static (existing, wanted) => existing.SameStructureAs(wanted),
                static (existing, wanted) => existing.RefreshLiveFieldsFrom(wanted));

            SelectedConnector = selectedName is null
                ? null
                : ConnectorRows.FirstOrDefault(r => string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// The expensive half: two SQLite aggregates and the two enforcement listings. Guarded
    /// so overlapping polls cannot pile up connections onto a database someone else owns.
    /// </summary>
    private async Task RefreshDataAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await RefreshAuditCountsAsync(cancellationToken);
            await RefreshEnforcementAsync(cancellationToken);
            await RefreshConnectorModesAsync(cancellationToken);

            var forceStatus = _forceStatus;
            _forceStatus = false;
            await RefreshCardsAsync(forceStatus, cancellationToken);
            _lastDataRefresh = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // The panel left the screen mid-refresh: the statements were stopped, and the next activation starts over (the floor is not stamped).
        }
        finally
        {
            IsRefreshing = false;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RefreshAuditCountsAsync(CancellationToken cancellationToken)
    {
        if (!Services.Audit.Exists)
        {
            AuditSummary = "No audit database yet";
            AuditNote = $"{Services.Paths.AuditDatabasePath} has not been created. It appears after the first event.";
            SyncTiles(SeverityTiles, Array.Empty<CountTile>());
            return;
        }

        var window = new AuditQuery { From = DateTimeOffset.UtcNow - CountWindow };

        try
        {
            var counts = await Services.Audit.CountBySeverityAsync(window, cancellationToken);
            var total = await Services.Audit.CountAsync(window, cancellationToken);

            var tiles = new List<CountTile>();
            foreach (var severity in AuditSeverityExtensions.Ladder.Reverse())
            {
                counts.TryGetValue(severity, out var count);
                if (count == 0 && severity is AuditSeverity.Warn or AuditSeverity.Low)
                {
                    // Levels this install never writes would be four empty tiles of noise.
                    continue;
                }

                tiles.Add(new CountTile
                {
                    Label = severity.ToStoredValue(),
                    Value = count.ToString("N0", CultureInfo.CurrentCulture),
                    SeverityKey = SeverityKey(severity),
                });
            }

            SyncTiles(SeverityTiles, tiles);
            AuditSummary = $"{total.ToString("N0", CultureInfo.CurrentCulture)} audit events in the last 24 hours";
            AuditNote = Services.Paths.AuditDatabasePath;
        }
#pragma warning disable CA1031 // A locked or half-written DB must degrade the tile, not the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            AuditSummary = "Audit counts unavailable";
            AuditNote = ex.Message;
            SyncTiles(SeverityTiles, Array.Empty<CountTile>());
        }
#pragma warning restore CA1031
    }

    private async Task RefreshEnforcementAsync(CancellationToken cancellationToken)
    {
        var blocked = await Services.Gateway.GetEnforceBlockedAsync(cancellationToken);
        var allowed = await Services.Gateway.GetEnforceAllowedAsync(cancellationToken);

        if (blocked.IsOk || allowed.IsOk)
        {
            var blockedItems = blocked.ValueOr(Array.Empty<EnforcementEntry>());
            var allowedItems = allowed.ValueOr(Array.Empty<EnforcementEntry>());

            // The Enforcement card's tiles are the four buttons (RenderEnforcementCards); these two lists are the gateway's explicit entries,
            // kept as the card's footer, each with the kinds it holds ("2 blocked (1 skill · 1 mcp)").
            EnforcementSummary = blockedItems.Count == 0 && allowedItems.Count == 0
                ? "No explicit block or allow entries"
                : $"{blockedItems.Count} blocked{Parenthesized(Kinds(blockedItems))} · {allowedItems.Count} allowed{Parenthesized(Kinds(allowedItems))}";
            EnforcementNote = "Entries come from /enforce/blocked and /enforce/allowed. Changes go through the CLI.";
            return;
        }

        EnforcementSummary = "Enforcement lists unavailable";
        EnforcementNote = DescribeUnavailable(blocked.Status, blocked.ErrorMessage);
    }

    /// <summary>
    /// One <c>/status</c> read per slow refresh. <c>/health</c> carries counters but not
    /// enforcement posture, and posture is the field an operator is most often wrong about.
    /// </summary>
    private async Task RefreshConnectorModesAsync(CancellationToken cancellationToken)
    {
        var result = await Services.Gateway.GetStatusAsync(cancellationToken);
        if (!result.IsOk || result.Value is not { } status)
        {
            // Not "keep the last answer": a stopped gateway or a 401 leaves the previous
            // mode / fail-mode on screen as if it were still what the hook does, which is the
            // one thing this box exists to get right. Rebuild only when that changes what is
            // shown, so a gateway that stays down costs nothing per refresh.
            var changed = !_statusUnavailable || _connectorModes.Count > 0;
            _statusUnavailable = true;
            _connectorModes = new Dictionary<string, ConnectorMode>(StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                BuildConnectors(Services.Monitor.Current, Services.Monitor.Current.Health);
            }

            return;
        }

        _statusUnavailable = false;
        var modes = new Dictionary<string, ConnectorMode>(StringComparer.OrdinalIgnoreCase);
        if (status.ConnectorMode is { Connector: { Length: > 0 } primary } primaryMode)
        {
            modes[primary] = primaryMode;
        }

        foreach (var mode in status.ConnectorModes)
        {
            if (mode.Connector is { Length: > 0 } name)
            {
                modes[name] = mode;
            }
        }

        _connectorModes = modes;
        BuildConnectors(Services.Monitor.Current, status.Health ?? Services.Monitor.Current.Health);
    }

    /// <summary>
    /// The text for a mode field that has no runtime value. Normally that is just config.yaml's
    /// value (or a dash); after a failed <c>/status</c> read it says the value is only what
    /// config.yaml states, or that nothing is known.
    /// </summary>
    private string ConfigOnly(string? configured) => (configured, _statusUnavailable) switch
    {
        (null, true) => "unknown",
        (null, false) => "—",
        ({ } value, true) => $"{value} (config.yaml)",
        ({ } value, false) => value,
    };

    private static bool Equal(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private string DescribeUnavailable(GatewayStatus status, string? message) => status switch
    {
        GatewayStatus.NotConnected => "The enforcement subsystem is not connected on this install — informational, not a fault.",
        GatewayStatus.Unauthorized =>
            $"/enforce needs a bearer token; none was found via {Services.Token.VariableName}.",
        GatewayStatus.Unreachable => "The gateway is not answering, so enforcement lists cannot be read.",
        _ => message ?? "The gateway did not return an enforcement list.",
    };

    private static string Parenthesized(string kinds) => kinds.Length == 0 ? string.Empty : $" ({kinds})";

    private static string Kinds(IReadOnlyList<EnforcementEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var kinds = entries
            .Select(e => e.Kind ?? "entry")
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} {g.Key}");

        return string.Join(" · ", kinds);
    }

    private static ScannerRow ExecutableRow(string name, string? path, bool resolved) => !resolved
        ? new ScannerRow
        {
            Name = name,
            StateText = "checking",
            StateKey = "Neutral",
            Detail = "Looking on PATH and in the installer's bin directory…",
        }
        : new ScannerRow
        {
            Name = name,
            StateText = path is null ? "not found" : "installed",
            StateKey = path is null ? "Warn" : "Ok",
            Detail = path ?? "Not on PATH and not in the installer's bin directory.",
        };

    /// <summary>
    /// Liveness only. <c>disabled</c> is a deliberate configuration on a standalone box,
    /// so it stays neutral; <c>last_error</c> is never consulted here because 0.8.7 fills
    /// it with "application protection disabled" on a perfectly healthy install.
    /// </summary>
    private static string ClassifyState(ServiceState state) => ClassifyText(state.State);

    private static string ClassifyText(string? state) => state?.Trim().ToUpperInvariant() switch
    {
        "RUNNING" or "HEALTHY" or "OK" or "ACTIVE" => "Ok",
        "DISABLED" or "INACTIVE" or "IDLE" or "NOT_CONFIGURED" => "Neutral",
        "DEGRADED" or "STARTING" or "PENDING" or "PAUSED" => "Warn",
        "FAILED" or "ERROR" or "STOPPED" or "CRASHED" => "Bad",
        null or "" => "Neutral",
        _ => "Neutral",
    };

    private static string Flag(ServiceState state, string key) =>
        state.DetailBool(key) switch { true => "on", false => "off", _ => "?" };

    private static long? DetailNumber(ServiceState state, string key) =>
        state.Details is { ValueKind: JsonValueKind.Object } details &&
        details.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static string SeverityKey(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => "Critical",
        AuditSeverity.High => "High",
        AuditSeverity.Medium => "Medium",
        AuditSeverity.Warn => "Medium",
        AuditSeverity.Low => "Low",
        AuditSeverity.Info => "Info",
        _ => "Neutral",
    };

    private static string FormatDuration(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m";

    private static string Relative(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value;
        return delta switch
        {
            { TotalSeconds: < 0 } => "just now",
            { TotalSeconds: < 60 } => $"{(int)delta.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            _ => $"{(int)delta.TotalDays}d ago",
        };
    }

    /// <summary>
    /// Merges a list of immutable rows into its bound collection by key: a row whose content
    /// is unchanged (record equality) keeps its visuals, a changed one is swapped in place,
    /// and only rows that appeared or vanished are inserted or removed. See
    /// <see cref="PanelViewModelBase.SyncCollection{T}"/>. The row types are records precisely
    /// so that "unchanged" is a comparison the compiler writes and cannot forget a field of.
    /// </summary>
    private static void SyncByEquality<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, string> keyOf)
        where T : class, IEquatable<T> =>
        SyncCollection(target, desired, keyOf, static (existing, wanted) => existing.Equals(wanted));

    private static void SyncTiles(ObservableCollection<CountTile> target, IReadOnlyList<CountTile> tiles) =>
        SyncByEquality(target, tiles, static tile => tile.Label);
}

/// <summary>
/// One line in "What Needs Attention". A record so the panel can tell an unchanged row from a
/// changed one by value (see <c>SyncByEquality</c>); rows are immutable once built.
/// </summary>
public sealed record AttentionRow
{
    public required string Title { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>Critical / High / Medium / Low / Info / Ok — drives the accent colour.</summary>
    public string SeverityKey { get; init; } = "Info";

    /// <summary>Suggested command. Displayed and copyable; the app never runs it.</summary>
    public string? Command { get; init; }

    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

    /// <summary>
    /// The row is the queued gateway restart (CUST-267) and carries its two buttons, Restart now (a reviewed <c>defenseclaw-gateway restart</c>) and
    /// Clear. The one row that is not text to read or a command to copy.
    /// </summary>
    public bool OffersRestart { get; init; }

    /// <summary>
    /// The Mac's mono bracket tag for the row: <c>[!]</c> critical, <c>[*]</c> a warning (High, Medium), <c>[OK]</c> all clear, <c>[&gt;]</c> for
    /// information. Drawn in the row's tone beside the severity word, which stays: a bracket is a shape, not a reading.
    /// </summary>
    public string Tag => SeverityKey switch
    {
        "Critical" or "Bad" => "[!]",
        "High" or "Warn" or "Medium" => "[*]",
        "Ok" => "[OK]",
        _ => "[>]",
    };

    /// <summary>
    /// What a screen reader announces for the row (UI Automation falls back to
    /// <c>ToString()</c> for an item with no explicit name). Without this it read the record's
    /// generated dump - including the raw PowerShell in <see cref="Command"/>. The command itself
    /// is reachable in the row's own text box; here it is only mentioned.
    /// </summary>
    public override string ToString()
    {
        var text = $"{SeverityKey}: {Title}. {Detail}".TrimEnd();
        if (OffersRestart)
        {
            text += " Restart now and Clear are available.";
        }

        return HasCommand ? text + " A suggested command is available." : text;
    }
}

/// <summary>One of the nine cards of the Services box. A record for the same reason as <see cref="AttentionRow"/>.</summary>
public sealed record ServiceRow
{
    /// <summary>The TUI's key for the card (<c>gateway</c>, <c>agent</c>, <c>watcher</c>, <c>guardrail</c>, <c>api</c>, <c>sinks</c>, <c>telemetry</c>, <c>ai_discovery</c>, <c>sandbox</c>): the row's identity across polls.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string StateText { get; init; }

    /// <summary>Ok / Warn / Bad / Neutral. Disabled subsystems are Neutral, never Bad.</summary>
    public string StateKey { get; init; } = "Neutral";

    /// <summary>Enforcement posture, shown separately from liveness.</summary>
    public string Posture { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string SinceText { get; init; } = string.Empty;

    public bool HasPosture => Posture.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// What the Services box prints after the state word, in one flowing line: the detail, the enforcement posture and when the state began
    /// (<c>127.0.0.1:18970 · since Sep 30 11:19</c>). The Mac prints the detail alone; the posture and the time are this app's additions.
    /// </summary>
    public string Summary => string.Join(" · ", new[] { Detail, Posture, SinceText }.Where(static part => part.Length > 0));

    public bool HasSummary => Summary.Length > 0;

    /// <summary>The screen-reader sentence for the row; see <see cref="AttentionRow.ToString"/>.</summary>
    public override string ToString() => JoinSentences(Name, StateText, SinceText, Posture, Detail);

    internal static string JoinSentences(params string[] parts) =>
        string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>One row in the Scanners box. A record for the same reason as <see cref="AttentionRow"/>.</summary>
public sealed record ScannerRow
{
    public required string Name { get; init; }

    public required string StateText { get; init; }

    public string StateKey { get; init; } = "Neutral";

    public string Detail { get; init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The screen-reader sentence for the row; see <see cref="AttentionRow.ToString"/>.</summary>
    public override string ToString() => ServiceRow.JoinSentences(Name, StateText, Detail);
}

/// <summary>
/// One connector card. Immutable except for the two fields that legitimately change under a
/// stable row and would otherwise force the whole card to be rebuilt on every poll:
/// <see cref="Counters"/> (request/error counts move whenever the agent works) and
/// <see cref="LastActivity"/> (an age, so a different string every few seconds). Those two
/// notify; <see cref="SameStructureAs"/> compares everything else, and
/// <see cref="RefreshLiveFieldsFrom"/> carries the two across onto the row that stays.
/// </summary>
public sealed partial class ConnectorRow : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>The connector's friendly name from <c>status --json</c> (<c>Claude Code</c>); empty until that has been read.</summary>
    public string Friendly { get; init; } = string.Empty;

    /// <summary>The table's first column, the Mac's: <c>Claude Code (claudecode)</c>, or just the name when there is no friendly one.</summary>
    public string DisplayName => Friendly.Length > 0 && !string.Equals(Friendly, Name, StringComparison.Ordinal)
        ? IsUnconfigured ? Friendly : $"{Friendly} ({Name})"
        : Name;

    /// <summary>The rule pack the connector runs: the last segment of its <c>rule_pack_dir</c>, or <c>default</c>.</summary>
    public string RulePack { get; init; } = "default";

    public required string StateText { get; init; }

    public string StateKey { get; init; } = "Neutral";

    public string Mode { get; init; } = "—";

    public string FailMode { get; init; } = "—";

    public string Source { get; init; } = string.Empty;

    public string Surface { get; init; } = string.Empty;

    /// <summary>Set when config.yaml and the running hook contract disagree.</summary>
    public string Drift { get; init; } = string.Empty;

    public bool HasDrift => Drift.Length > 0;

    public bool HasWarning { get; init; }

    public string WarningText { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounters))]
    private string _counters = string.Empty;

    [ObservableProperty]
    private string _lastActivity = string.Empty;

    /// <summary>Requests the gateway has served for this connector since it started (the table's Calls).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallsText))]
    private long _calls;

    /// <summary>Tool and subprocess blocks since the gateway started (the table's Blocks).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlocksText))]
    [NotifyPropertyChangedFor(nameof(BlocksKey))]
    private long _blocks;

    /// <summary>Unacknowledged findings attributed to this connector (the table's Alerts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlertsText))]
    [NotifyPropertyChangedFor(nameof(AlertsKey))]
    private int _alerts;

    /// <summary>The table's Last Activity: <c>4h ago</c>, or <c>never</c>.</summary>
    [ObservableProperty]
    private string _lastActivityShort = "never";

    public string CallsText => Calls.ToString("N0", CultureInfo.CurrentCulture);

    public string BlocksText => Blocks.ToString("N0", CultureInfo.CurrentCulture);

    public string AlertsText => Alerts.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Red when something was blocked, grey when not: the Mac's colouring.</summary>
    public string BlocksKey => Blocks > 0 ? "Bad" : "Neutral";

    /// <summary>Amber when findings wait, grey when none do.</summary>
    public string AlertsKey => Alerts > 0 ? "High" : "Neutral";

    public bool HasCounters => Counters.Length > 0;

    /// <summary>Drift and the fail-mode warning, the lines that go under the row when there is one.</summary>
    public bool HasNotes => HasDrift || HasWarning;

    /// <summary>
    /// True when every field except the two live ones matches, i.e. when keeping this row
    /// and refreshing those two shows exactly what <paramref name="other"/> would.
    /// </summary>
    internal bool SameStructureAs(ConnectorRow other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        string.Equals(Friendly, other.Friendly, StringComparison.Ordinal) &&
        string.Equals(RulePack, other.RulePack, StringComparison.Ordinal) &&
        string.Equals(StateText, other.StateText, StringComparison.Ordinal) &&
        string.Equals(StateKey, other.StateKey, StringComparison.Ordinal) &&
        string.Equals(Mode, other.Mode, StringComparison.Ordinal) &&
        string.Equals(FailMode, other.FailMode, StringComparison.Ordinal) &&
        string.Equals(Source, other.Source, StringComparison.Ordinal) &&
        string.Equals(Surface, other.Surface, StringComparison.Ordinal) &&
        string.Equals(Drift, other.Drift, StringComparison.Ordinal) &&
        string.Equals(WarningText, other.WarningText, StringComparison.Ordinal) &&
        HasWarning == other.HasWarning &&
        IsUnconfigured == other.IsUnconfigured &&
        string.Equals(AddCaution, other.AddCaution, StringComparison.Ordinal);

    /// <summary>Copies the two ticking fields; each setter notifies only if the value moved.</summary>
    internal void RefreshLiveFieldsFrom(ConnectorRow fresh)
    {
        Counters = fresh.Counters;
        LastActivity = fresh.LastActivity;
        Calls = fresh.Calls;
        Blocks = fresh.Blocks;
        Alerts = fresh.Alerts;
        LastActivityShort = fresh.LastActivityShort;
    }

    /// <summary>
    /// The screen-reader sentence for the card: name, state, posture, then the live counters. A
    /// class (not a record), so without this the item was announced as its type name.
    /// </summary>
    public override string ToString() => ServiceRow.JoinSentences(
        $"Connector {Name}",
        StateText,
        $"mode {Mode}",
        $"fail-mode {FailMode}",
        HasWarning ? WarningText : string.Empty,
        Counters);
}

/// <summary>A number tile: severity counts and the enforcement rollup. A record for the same reason as <see cref="AttentionRow"/>.</summary>
public sealed record CountTile
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    public string SeverityKey { get; init; } = "Info";

    public string Caption { get; init; } = string.Empty;

    public bool HasCaption => Caption.Length > 0;

    /// <summary>
    /// "CountTile { Label = CRITICAL, Value = 412, ... }" is what a screen reader used to say.
    /// Now it is "CRITICAL: 412" (plus the caption when there is one).
    /// </summary>
    public override string ToString() => HasCaption ? $"{Label}: {Value}. {Caption}" : $"{Label}: {Value}";
}

/// <summary>
/// One failing or warning line from <c>doctor_cache.json</c>, shown on the Overview doctor card.
/// A record for the same reason as <see cref="AttentionRow"/>: an unchanged row keeps its visuals.
/// </summary>
public sealed record DoctorCheckRow
{
    public required string Label { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary><c>pass</c> / <c>fail</c> / <c>warn</c> / <c>skip</c>, lower-case as the CLI writes it.</summary>
    public string Status { get; init; } = "skip";

    /// <summary>
    /// True when this cached fail/warn names a subsystem that the live <c>/health</c> now reports running
    /// (<see cref="DoctorReconciliation"/>): the result is out of date, not the service. Shown as STALE, not as a failure.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>Bad / Warn / Ok / Neutral - the tone key for the badge.</summary>
    public string StatusKey => IsStale ? "Neutral" : Status switch
    {
        "fail" => "Bad",
        "warn" => "Warn",
        "pass" => "Ok",
        _ => "Neutral",
    };

    /// <summary>FAIL / WARN / PASS / SKIP, or STALE for a result the live state contradicts.</summary>
    public string StatusText => IsStale ? "STALE" : Status.ToUpperInvariant();

    public bool HasDetail => Detail.Length > 0;

    public override string ToString() =>
        HasDetail ? $"{StatusText}: {Label}. {Detail}" : $"{StatusText}: {Label}";
}

/// <summary>
/// <c>doctor_cache.json</c> after parsing: the four counts the CLI recorded, when it recorded
/// them, and every check line. The counts are the CLI's own and are used as written - a check
/// with no label is counted but never listed, so they can exceed the number of listed checks.
/// </summary>
public sealed record DoctorCacheSnapshot(
    int Passed,
    int Failed,
    int Warned,
    int Skipped,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<DoctorCheckRow> Checks)
{
    /// <summary>
    /// True when the results are older than <paramref name="window"/>, or carry no capture time
    /// at all (which cannot be told apart from very old, so it is treated as stale).
    /// </summary>
    public bool IsStale(DateTimeOffset now, TimeSpan window) =>
        CapturedAt is not { } at || now - at > window;

    /// <summary>Failing checks first, then warnings - the TUI's ordering - each in file order.</summary>
    public IReadOnlyList<DoctorCheckRow> Problems() =>
        Checks.Where(c => c.Status == "fail").Concat(Checks.Where(c => c.Status == "warn")).ToList();
}

/// <summary>
/// Parses <c>&lt;data dir&gt;\doctor_cache.json</c>, the file <c>defenseclaw doctor</c> rewrites
/// atomically at the end of every run. Schema (from <c>cmd_doctor.py: _write_doctor_cache</c>,
/// confirmed against the live file): <c>{"passed","failed","warned","skipped": int,
/// "checks": [{"status": pass|fail|warn|skip, "label", "detail"}], "captured_at": "...Z"}</c>.
/// Tolerant by design: every field is optional, unknown fields and statuses are ignored, and only
/// a document that is not a JSON object at all is an error.
/// </summary>
internal static class DoctorCacheReader
{
    /// <exception cref="JsonException">Not valid JSON, or not a JSON object.</exception>
    internal static DoctorCacheSnapshot Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("doctor_cache.json is not a JSON object.");
        }

        var checks = new List<DoctorCheckRow>();
        if (root.TryGetProperty("checks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var label = ReadString(item, "label");
                if (label.Length == 0)
                {
                    continue;
                }

                var status = ReadString(item, "status").ToLowerInvariant();
                checks.Add(new DoctorCheckRow
                {
                    Label = label,
                    Detail = ReadString(item, "detail"),
                    Status = status is "pass" or "fail" or "warn" or "skip" ? status : "skip",
                });
            }
        }

        DateTimeOffset? capturedAt = null;
        if (DateTimeOffset.TryParse(
                ReadString(root, "captured_at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            capturedAt = parsed;
        }

        return new DoctorCacheSnapshot(
            ReadCount(root, "passed"),
            ReadCount(root, "failed"),
            ReadCount(root, "warned"),
            ReadCount(root, "skipped"),
            capturedAt,
            checks);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadCount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) &&
        number >= 0
            ? number
            : 0;
}
