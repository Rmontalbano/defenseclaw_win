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
/// The dashboard: the TUI's boxes — What Needs Attention, Services, Scanners,
/// Enforcement, Connectors — plus severity tiles counted straight out of the audit DB.
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

    public OverviewPanelViewModel(AppServices services)
        : base(services)
    {
        // Reading the cached snapshot is not I/O; the poll that produced it already ran. No
        // subscriptions here: OnActivated attaches them, and OnDeactivated lets go.
        DataDirectoryText = Services.Paths.DataDirectory;
        DataDirectorySourceText = Services.Paths.DataDirectoryOrigin.Description;
        Apply(Services.Monitor.Current);
    }

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

    /// <summary>One row per subsystem block in <c>/health</c>.</summary>
    public ObservableCollection<ServiceRow> ServiceRows { get; } = new();

    public ObservableCollection<ScannerRow> ScannerRows { get; } = new();

    public ObservableCollection<ConnectorRow> ConnectorRows { get; } = new();

    /// <summary>Severity counts over <see cref="CountWindow"/>, from the audit DB.</summary>
    public ObservableCollection<CountTile> SeverityTiles { get; } = new();

    public ObservableCollection<CountTile> EnforcementTiles { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Apply(Services.Monitor.Current);

        // The doctor card first: it is one small file read, so the card is populated before the
        // slower audit / enforcement / status reads below finish.
        await ReloadDoctorCacheAsync(cancellationToken);
        await RefreshDataAsync(cancellationToken);
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
        Services.Monitor.PollCompleted += OnPollCompleted;
        Services.ConfigReloaded += OnConfigReloaded;

        Apply(Services.Monitor.Current);
        RefreshDataIfDue();

        // One file read per activation: a doctor run from a terminal (or the TUI) while the panel
        // was away rewrote the cache, and this is the only time it is picked up without a Refresh.
        _ = ReloadDoctorCacheAsync(CancellationToken.None);
    }

    protected override void OnDeactivated()
    {
        Services.Monitor.PollCompleted -= OnPollCompleted;
        Services.ConfigReloaded -= OnConfigReloaded;
    }

    /// <summary>Also what F5 invokes: an <see cref="IAsyncRelayCommand"/> that disables itself while it runs.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var snapshot = await Services.Monitor.RefreshAsync();

        // "Refresh" means look again, including whether the scanners are installed.
        _scannerPathsProbedAt = DateTimeOffset.MinValue;
        Apply(snapshot);
        _lastDataRefresh = DateTimeOffset.MinValue;
        await ReloadDoctorCacheAsync(CancellationToken.None);
        await RefreshDataAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs <c>defenseclaw doctor</c> (10-30 s of live probes; it writes only its own results
    /// cache), then re-reads that cache. The tier comes from <see cref="CommandTiers"/> rather than
    /// being assumed: plain <c>doctor</c> classifies as read-only, so — like every read-only command
    /// in the app — it runs with no <see cref="CommandReview"/> in front of it. This panel offers no
    /// other command to run (the fixes on its attention rows are text to copy, never run), so it has no
    /// review surface of its own; if <c>doctor</c> ever stopped being read-only this action refuses
    /// rather than silently running an unreviewed state-changing command. <c>doctor --fix</c> is
    /// deliberately not offered here.
    /// </summary>
    [RelayCommand]
    private async Task RunDoctorAsync()
    {
        var argv = DoctorArgv;
        if (CommandTiers.Classify(argv) != CommandTier.ReadOnly)
        {
            SetDoctorRunMessage("Doctor is no longer classified read-only, so it will not run without a review step.");
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
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
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

        DoctorHasData = true;
        DoctorIsEmpty = false;
        DoctorIsStale = stale;
        DoctorAsOfText = snapshot.CapturedAt is { } at
            ? $"as of {FormatCapturedAt(at)} - {Relative(at)}"
            : "capture time unknown";

        DoctorSummary =
            $"{snapshot.Passed.ToString("N0", CultureInfo.CurrentCulture)} pass · " +
            $"{snapshot.Failed.ToString("N0", CultureInfo.CurrentCulture)} fail · " +
            $"{snapshot.Warned.ToString("N0", CultureInfo.CurrentCulture)} warn · " +
            $"{snapshot.Skipped.ToString("N0", CultureInfo.CurrentCulture)} skip";

        if (snapshot.Failed > 0)
        {
            DoctorVerdict = snapshot.Failed == 1 ? "1 check failed" : $"{snapshot.Failed} checks failed";
            DoctorStateKey = "Bad";
        }
        else if (snapshot.Warned > 0)
        {
            DoctorVerdict = snapshot.Warned == 1 ? "1 warning" : $"{snapshot.Warned} warnings";
            DoctorStateKey = "Warn";
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
            new CountTile { Label = "FAIL", Value = snapshot.Failed.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = snapshot.Failed > 0 ? "Critical" : "Info" },
            new CountTile { Label = "WARN", Value = snapshot.Warned.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = snapshot.Warned > 0 ? "High" : "Info" },
            new CountTile { Label = "SKIP", Value = snapshot.Skipped.ToString("N0", CultureInfo.CurrentCulture), SeverityKey = "Info" },
        });

        var problems = snapshot.Problems();
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
    private void OnConfigReloaded(object? sender, EventArgs e) => Apply(Services.Monitor.Current);

    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e)
    {
        // GatewayMonitor posts on the UI SynchronizationContext, so this is already the
        // UI thread and the collections below can be mutated directly.
        Apply(e.Snapshot);
        RefreshDataIfDue();
    }

    private void RefreshDataIfDue()
    {
        if (DefenseClaw.Core.Time.WallClock.Elapsed(_lastDataRefresh) >= DataRefreshInterval)
        {
            _ = RefreshDataAsync(CancellationToken.None);
        }
    }

    /// <summary>Renders everything derivable from one snapshot plus config.yaml.</summary>
    internal void Apply(GatewaySnapshot snapshot)
    {
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

        UptimeText = health is { UptimeMs: > 0 } ? FormatDuration(health.Uptime) : "—";
        VersionText = string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? "—"
            : $"DefenseClaw {snapshot.BinaryVersion}";
        LastUpdatedText = snapshot.PolledAt == DateTimeOffset.MinValue
            ? string.Empty
            : $"Polled {snapshot.PolledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";

        BuildAttention(snapshot);
        BuildServices(health);
        BuildScanners(snapshot, health);
        BuildConnectors(snapshot, health);

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
    }

    /// <summary>
    /// One row per <c>/health</c> subsystem. <c>disabled</c> is rendered neutral with its
    /// own explanation — a standalone install has two of them by design.
    /// </summary>
    private void BuildServices(GatewayHealth? health)
    {
        if (health is null)
        {
            SyncByEquality(ServiceRows, Array.Empty<ServiceRow>(), static row => row.Name);
            return;
        }

        var rows = new List<ServiceRow>();
        foreach (var (name, state) in health.Services())
        {
            rows.Add(new ServiceRow
            {
                Name = FriendlyServiceName(name),
                StateText = state.State ?? "unknown",
                StateKey = ClassifyState(state),
                Posture = PostureFor(name, state),
                Detail = DescribeService(name, state),
                SinceText = state.Since is { } since ? $"since {since.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}" : string.Empty,
            });
        }

        SyncByEquality(ServiceRows, rows, static row => row.Name);
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

        SyncByEquality(ScannerRows, rows, static row => row.Name);
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

        var rows = new List<ConnectorRow>();
        foreach (var name in names)
        {
            configured.TryGetValue(name, out var settings);
            live.TryGetValue(name, out var status);
            _connectorModes.TryGetValue(name, out var runtime);

            // config.yaml is stated intent; /status is what the running hook contract does.
            // They can disagree — say so rather than picking a winner. With /status down there
            // is no runtime side at all, and config.yaml's value is labelled as what it is.
            var mode = runtime?.GuardrailMode ?? ConfigOnly(settings?.Mode);
            var failMode = runtime?.HookFailMode ?? ConfigOnly(settings?.HookFailMode);
            var mismatch = (settings?.HasFailModeMismatch ?? false) || (runtime?.HasFailModeMismatch ?? false);

            var drift = runtime is not null && settings is not null &&
                        (!Equal(runtime.GuardrailMode, settings.Mode) ||
                         !Equal(runtime.HookFailMode, settings.HookFailMode))
                ? $"config.yaml says mode {settings.Mode ?? "—"} / fail-mode {settings.HookFailMode ?? "—"}"
                : string.Empty;

            rows.Add(new ConnectorRow
            {
                Name = name,
                StateText = status?.State ?? (settings is null ? "configured elsewhere" : "not running"),
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

        // Structure is compared, the two fields that tick are not: counters move on nearly
        // every poll of a busy box and "last activity 5s ago" is a different string every
        // time, and replacing the row for either would rebuild the whole card. They are
        // copied onto the row that stays.
        SyncCollection(
            ConnectorRows,
            rows,
            static row => row.Name,
            static (existing, wanted) => existing.SameStructureAs(wanted),
            static (existing, wanted) => existing.RefreshLiveFieldsFrom(wanted));
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
            _lastDataRefresh = DateTimeOffset.UtcNow;
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

            SyncTiles(EnforcementTiles, new[]
            {
                new CountTile
                {
                    Label = "Blocked",
                    Value = blockedItems.Count.ToString("N0", CultureInfo.CurrentCulture),
                    SeverityKey = blockedItems.Count > 0 ? "High" : "Ok",
                    Caption = Kinds(blockedItems),
                },
                new CountTile
                {
                    Label = "Allowed",
                    Value = allowedItems.Count.ToString("N0", CultureInfo.CurrentCulture),
                    SeverityKey = "Info",
                    Caption = Kinds(allowedItems),
                },
            });

            EnforcementSummary = blockedItems.Count == 0 && allowedItems.Count == 0
                ? "No explicit block or allow entries"
                : $"{blockedItems.Count} blocked · {allowedItems.Count} allowed";
            EnforcementNote = "Entries come from /enforce/blocked and /enforce/allowed. Changes go through the CLI.";
            return;
        }

        SyncTiles(EnforcementTiles, Array.Empty<CountTile>());
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

    /// <summary>
    /// Enforcement posture, kept separate from liveness on purpose: <c>guardrail</c> runs
    /// with <c>enforcement_enabled: false</c> in observe mode, and collapsing the two
    /// would paint a correctly configured box as broken.
    /// </summary>
    private static string PostureFor(string name, ServiceState state)
    {
        if (string.Equals(name, "guardrail", StringComparison.Ordinal))
        {
            var mode = state.DetailString("policy_mode") ?? state.DetailString("mode") ?? "unknown";
            var enforcing = state.DetailBool("enforcement_enabled");
            var surface = state.DetailString("enforcement_surface");
            var posture = enforcing == true ? "enforcing" : "observing (enforcement off)";
            return surface is { Length: > 0 }
                ? $"{mode} · {posture} · {surface}"
                : $"{mode} · {posture}";
        }

        if (string.Equals(name, "application_protection", StringComparison.Ordinal))
        {
            var enabled = state.DetailBool("enabled");
            var assetMode = state.DetailString("asset_policy_mode");
            return enabled == true
                ? $"enabled · asset policy {assetMode ?? "?"}"
                : "not enabled";
        }

        return string.Empty;
    }

    private static string DescribeService(string name, ServiceState state)
    {
        var summary = state.DetailString("summary")
            ?? state.DetailString("hint")
            ?? state.DetailString("addr")
            ?? state.DetailString("path");

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        if (string.Equals(name, "telemetry", StringComparison.Ordinal))
        {
            var destinations = DetailNumber(state, "destination_count");
            var retention = DetailNumber(state, "retention_days");
            return $"{destinations?.ToString(CultureInfo.CurrentCulture) ?? "?"} destination(s) · " +
                   $"retention {retention?.ToString(CultureInfo.CurrentCulture) ?? "?"} days";
        }

        // Shown, but never used to decide the colour: on 0.8.7 a disabled subsystem writes
        // its own name into last_error.
        return state.LastError is { Length: > 0 } lastError && !state.IsDisabled
            ? lastError
            : string.Empty;
    }

    private static string FriendlyServiceName(string key) => key switch
    {
        "api" => "api",
        "ai_discovery" => "ai discovery",
        "application_protection" => "application protection",
        "gateway" => "fleet uplink",
        _ => key,
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
    /// What a screen reader announces for the row (UI Automation falls back to
    /// <c>ToString()</c> for an item with no explicit name). Without this it read the record's
    /// generated dump - including the raw PowerShell in <see cref="Command"/>. The command itself
    /// is reachable in the row's own text box; here it is only mentioned.
    /// </summary>
    public override string ToString()
    {
        var text = $"{SeverityKey}: {Title}. {Detail}".TrimEnd();
        return HasCommand ? text + " A suggested command is available." : text;
    }
}

/// <summary>One subsystem row in the Services box. A record for the same reason as <see cref="AttentionRow"/>.</summary>
public sealed record ServiceRow
{
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

    public bool HasCounters => Counters.Length > 0;

    /// <summary>
    /// True when every field except the two live ones matches, i.e. when keeping this row
    /// and refreshing those two shows exactly what <paramref name="other"/> would.
    /// </summary>
    internal bool SameStructureAs(ConnectorRow other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        string.Equals(StateText, other.StateText, StringComparison.Ordinal) &&
        string.Equals(StateKey, other.StateKey, StringComparison.Ordinal) &&
        string.Equals(Mode, other.Mode, StringComparison.Ordinal) &&
        string.Equals(FailMode, other.FailMode, StringComparison.Ordinal) &&
        string.Equals(Source, other.Source, StringComparison.Ordinal) &&
        string.Equals(Surface, other.Surface, StringComparison.Ordinal) &&
        string.Equals(Drift, other.Drift, StringComparison.Ordinal) &&
        string.Equals(WarningText, other.WarningText, StringComparison.Ordinal) &&
        HasWarning == other.HasWarning;

    /// <summary>Copies the two ticking fields; each setter notifies only if the value moved.</summary>
    internal void RefreshLiveFieldsFrom(ConnectorRow fresh)
    {
        Counters = fresh.Counters;
        LastActivity = fresh.LastActivity;
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

    /// <summary>Bad / Warn / Ok / Neutral - the tone key for the badge.</summary>
    public string StatusKey => Status switch
    {
        "fail" => "Bad",
        "warn" => "Warn",
        "pass" => "Ok",
        _ => "Neutral",
    };

    /// <summary>FAIL / WARN / PASS / SKIP.</summary>
    public string StatusText => Status.ToUpperInvariant();

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
