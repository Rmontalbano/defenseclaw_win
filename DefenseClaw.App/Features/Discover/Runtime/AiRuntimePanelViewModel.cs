using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>What the Runtime panel is showing. Orthogonal to it: <see cref="AiRuntimePanelViewModel.IsStale"/>.</summary>
public enum AiRuntimeState
{
    /// <summary>The first read is on its way and nothing has been read.</summary>
    Loading,

    /// <summary>The gateway has no runtime route (HTTP 404 or 405): a runtime without the planes.</summary>
    Unsupported,

    /// <summary>The first read failed, so there is no snapshot at all. Not a clean-host result.</summary>
    Unavailable,

    /// <summary>The gateway answered that the planes are turned off.</summary>
    Disabled,

    /// <summary>The planes are on and none has completed a poll yet.</summary>
    NoPoll,

    /// <summary>A snapshot with planes, counts and findings (possibly none) is on screen.</summary>
    Ready,
}

/// <summary>
/// The Runtime panel (the macOS companion's "AI Discovery Runtime"): what the three runtime planes can and cannot see on this PC, and what they
/// found. It needs a DefenseClaw runtime that has <c>agent discovery runtime</c> (<c>PanelDescriptor.Requires = AiRuntime</c>); on 0.8.10 it is not offered
/// and the AI Discovery panel's runtime section keeps saying "not supported".
/// <para>
/// <b>Coverage before findings.</b> A detector that reports nothing because it was never able to look reads, on a dashboard, exactly like a host
/// with nothing to find. So the plane strip comes first, every plane that is not fully up states its whole reason as text, the unattributed
/// share warns at half, and an empty list is always shown beside "No findings is not a clean host". Nothing here is ever drawn as clean
/// while a plane is idle, blind, partial or off, or while the snapshot on screen is stale (see <see cref="AiRuntimeCoverage"/>).
/// </para>
/// <para>
/// <b>Stale, not replaced.</b> A read that fails keeps the last good snapshot and marks it STALE with the reason: swapping a stale-but-true
/// report for an empty one would read as a clean host rather than as a lost connection. A 404 or 405 is different - it is the version, not a
/// fault - and clears the snapshot. Poll now, Enable and Disable are off while the snapshot is stale, the gateway does not serve the planes,
/// or the runtime does not list the command (<see cref="DefenseClaw.Core.Runtime.RuntimeCapabilities.HasAiRuntimeCommand"/>).
/// A snapshot read before <c>config.yaml</c> or <c>.env</c> changed is stale the same way (CUST-312; <see cref="CatalogTrust"/> does it for the
/// other panels' lists): the panel keeps the disk signature the read began under (<see cref="ConfigDiskSignature"/>: a stat, never the
/// content), compares it when <c>AppServices.ConfigReloaded</c> is raised while the panel is on screen, when the panel comes back, and right
/// before a change starts or is confirmed, and marks the snapshot stale with <see cref="ConfigMovedReason"/>. A read that began after the
/// change is not stale.
/// </para>
/// <para>
/// <b>Reads and changes.</b> The snapshot is one authenticated <c>GET /api/v1/ai-usage/runtime</c> through the gateway client; every change is a
/// reviewed CLI run (<see cref="Review"/>, then Activity); the prerequisites card reads <c>permissions --json</c> and never <c>--grant</c>.
/// </para>
/// </summary>
public sealed partial class AiRuntimePanelViewModel : PanelViewModelBase
{
    public const string LoadingTitle = "Reading runtime coverage";

    public const string UnsupportedTitle = "Runtime planes are not supported by this gateway";

    public const string UnsupportedDetail =
        "The selected gateway does not serve the runtime API (GET /api/v1/ai-usage/runtime). A matching version label alone is not enough: " +
        "install a DefenseClaw build that has the runtime planes. This is a limit of the version, not a problem with this PC - and it means " +
        "nothing is watching for shadow AI network traffic or unattributed agent actions through the planes.";

    public const string UnavailableTitle = "Runtime coverage unavailable";

    public const string UnavailableDetail = "No snapshot has been received, so nothing can be said about what the planes can see. This is not a clean-host result.";

    public const string DisabledTitle = "Runtime planes are disabled";

    public const string DisabledDetail =
        "Nothing is watching for shadow AI network traffic or unattributed agent actions through the planes. " +
        "Enable them with: defenseclaw agent discovery runtime enable";

    public const string NoPollTitle = "No poll yet";

    public const string NoPollDetail = "The runtime planes are enabled but none has completed a poll. Poll now, or wait for the next interval. This is not a clean-host result.";

    public const string NoFindingsTitle = "No findings";

    /// <summary>Planes whose snapshot is older than this are re-read when the panel comes back on screen.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>Why the snapshot on screen is stale when config.yaml or .env changed after it was read: the twin of the gateway-away reason.</summary>
    public const string ConfigMovedReason =
        "The config.yaml or .env file changed after this snapshot was read, so the coverage above may no longer be true.";

    private AiRuntimeSnapshot? _snapshot;
    private DateTimeOffset? _readAt;
    private MonotonicStamp _readStamp;

    /// <summary>What config.yaml and .env looked like when the read in flight began; taken per read, kept with the snapshot it produces.</summary>
    private ConfigDiskSignature? _pendingSignature;

    /// <summary>What config.yaml and .env looked like when the snapshot on screen was read.</summary>
    private ConfigDiskSignature? _readSignature;
    private bool _loadRunning;
    private bool _reloadRequested;
    private bool _syncing;
    private string? _selectedId;
    private bool _gatewayReachable = true;

    public AiRuntimePanelViewModel(AppServices services)
        : base(services)
    {
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };

        // The review dialog being open or running takes the action buttons away (one change at a time); the panel follows it.
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiscoverActionReview.IsOpen) or nameof(DiscoverActionReview.IsRunning))
            {
                RaiseActionState();
            }
        };
    }

    public override string Title => "Runtime";

    public override string Description =>
        "What the AI Discovery runtime planes can and cannot see on this PC - running AI processes, their network peers and agent actions - and what they found.";

    /// <summary>The confirm-and-run overlay every change on this panel goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>The planes, in the gateway's order.</summary>
    public ObservableCollection<AiRuntimePlaneRow> Planes { get; } = new();

    /// <summary>The findings the filter lets through, worst first.</summary>
    public ObservableCollection<AiRuntimeFindingRow> Rows { get; } = new();

    /// <summary>Lines about coverage that are not a plane's own reason (a degraded line, a plane the gateway did not list, unreadable entries).</summary>
    public ObservableCollection<string> OtherGaps { get; } = new();

    /// <summary>Test seam: reads the snapshot instead of asking the gateway client, so a test feeds exact answers and opens no socket.</summary>
    internal Func<CancellationToken, Task<GatewayResult<JsonDocument>>>? ReadSnapshot { get; set; }

    // ---- State -----------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStateCard), nameof(ShowCoverage), nameof(ShowFindings), nameof(CaptionText), nameof(StateTitle), nameof(StateDetail), nameof(ShowEnableInState))]
    private AiRuntimeState _state = AiRuntimeState.Loading;

    /// <summary>The last read failed, so what is on screen is the last good snapshot and nothing newer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(StaleBanner), nameof(CoverageBadgeText), nameof(CoverageTone), nameof(CoverageHeadline))]
    private bool _isStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaleBanner), nameof(StateDetail))]
    private string _staleReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpinner))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(ShowNoFindings), nameof(ShowNoMatch), nameof(ShowFindingsTable))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AiRuntimeFindingRow? _selectedFinding;

    [ObservableProperty]
    private string _lastRunSummary = string.Empty;

    public bool HasSelection => SelectedFinding is not null;

    public bool ShowSpinner => IsLoading;

    /// <summary>A card in place of the plane strip and findings: loading, unsupported, unavailable, disabled, no poll yet.</summary>
    public bool ShowStateCard => State != AiRuntimeState.Ready;

    /// <summary>The coverage card: shown once a snapshot with planes is on screen (a disabled runtime has none).</summary>
    public bool ShowCoverage => State is AiRuntimeState.Ready or AiRuntimeState.NoPoll;

    public bool ShowFindings => State == AiRuntimeState.Ready;

    public bool ShowFindingsTable => ShowFindings && Rows.Count > 0;

    /// <summary>Ready, nothing to list and no filter hiding anything: the "No findings is not a clean host" state.</summary>
    public bool ShowNoFindings => ShowFindings && _snapshot is { } s && s.Findings.Count == 0;

    /// <summary>Findings exist and the filter hides all of them.</summary>
    public bool ShowNoMatch => ShowFindings && _snapshot is { } s && s.Findings.Count > 0 && Rows.Count == 0;

    public bool ShowEnableInState => State == AiRuntimeState.Disabled;

    /// <summary>What the "No findings" state says: always the caveat, and what the planes were actually doing.</summary>
    public string NoFindingsDetail =>
        _snapshot is { } snapshot
            ? AiRuntimeCoverage.NoFindingsCaveat + " " + snapshot.Coverage.Headline
            : AiRuntimeCoverage.NoFindingsCaveat;

    public string StateTitle => State switch
    {
        AiRuntimeState.Loading => LoadingTitle,
        AiRuntimeState.Unsupported => UnsupportedTitle,
        AiRuntimeState.Unavailable => UnavailableTitle,
        AiRuntimeState.Disabled => DisabledTitle,
        AiRuntimeState.NoPoll => NoPollTitle,
        _ => string.Empty,
    };

    public string StateDetail => State switch
    {
        AiRuntimeState.Loading => "Asking the gateway for the runtime planes' last snapshot.",
        AiRuntimeState.Unsupported => UnsupportedDetail,
        AiRuntimeState.Unavailable => (_unavailableReason.Length > 0 ? _unavailableReason + " " : string.Empty) + UnavailableDetail,
        AiRuntimeState.Disabled => DisabledDetail,
        AiRuntimeState.NoPoll => NoPollDetail,
        _ => string.Empty,
    };

    private string _unavailableReason = string.Empty;

    /// <summary>The banner above everything while the snapshot on screen is not the latest.</summary>
    public string StaleBanner =>
        IsStale
            ? "STALE — the last successful snapshot" +
              (_snapshot?.ScannedAt is { } at ? $" (polled {Time(at)})" : string.Empty) +
              " is shown. " + (StaleReason.Length > 0 ? StaleReason + " " : string.Empty) +
              "Nothing below is newer than that, and no change can be made until a read succeeds."
            : string.Empty;

    // ---- Coverage --------------------------------------------------------------------------------------------------------

    // What the planes add up to, as of the snapshot. The three properties below never show the calm version while the snapshot is stale.
    private string _coverageBadge = "Unknown";
    private string _coverageToneCore = "Neutral";
    private string _coverageHeadlineCore = string.Empty;

    public string CoverageBadgeText => IsStale ? "Stale" : _coverageBadge;

    /// <summary>Ok only when every plane is up, nothing is degraded and the snapshot is current; Warn when anything is partial or stale; Neutral when there is nothing to judge yet.</summary>
    public string CoverageTone => IsStale ? "Warn" : _coverageToneCore;

    public string CoverageHeadline =>
        IsStale && _coverageHeadlineCore.Length > 0 ? "As of the last successful poll, which may no longer be true: " + _coverageHeadlineCore : _coverageHeadlineCore;

    [ObservableProperty]
    private string _countsText = string.Empty;

    [ObservableProperty]
    private string _hostPlaneText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnattributedWarning))]
    private string _unattributedWarning = string.Empty;

    public bool HasUnattributedWarning => UnattributedWarning.Length > 0;

    public bool HasHostPlaneText => HostPlaneText.Length > 0;

    public bool HasOtherGaps => OtherGaps.Count > 0;

    public bool HasNoPlanes => Planes.Count == 0;

    /// <summary>"6 findings", "2 of 6 findings", and what was not kept when the snapshot had more than the panel lists.</summary>
    public string FindingsCaption =>
        _snapshot is { FindingsNotShown: > 0 } snapshot
            ? FindingsCount + $"; {snapshot.FindingsNotShown.ToString("N0", CultureInfo.InvariantCulture)} more were not kept (the worst {AiRuntimeReader.MaxFindings.ToString("N0", CultureInfo.InvariantCulture)} are listed)"
            : FindingsCount;

    /// <summary>"6 findings" or, while the filter hides some, "2 of 6 findings".</summary>
    private string FindingsCount
    {
        get
        {
            if (_snapshot is not { } snapshot)
            {
                return string.Empty;
            }

            var total = snapshot.Findings.Count;
            var noun = total == 1 ? "finding" : "findings";
            return Rows.Count == total
                ? $"{total.ToString("N0", CultureInfo.InvariantCulture)} {noun}"
                : $"{Rows.Count.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} {noun}";
        }
    }

    /// <summary>What the toolbar says beside the title: the count, what the planes add up to (DEGRADED, partial coverage, all planes up), the poll time, then the counts.</summary>
    public string CaptionText
    {
        get
        {
            var text = State switch
            {
                AiRuntimeState.Loading => "Reading runtime coverage…",
                AiRuntimeState.Unsupported => "Not supported by this gateway",
                AiRuntimeState.Unavailable => "Runtime coverage unavailable",
                AiRuntimeState.Disabled => "Disabled",
                AiRuntimeState.NoPoll => "No poll yet",
                _ => ReadyCaption(),
            };

            return IsStale ? "STALE · " + text : text;
        }
    }

    private string ReadyCaption()
    {
        if (_snapshot is not { } snapshot)
        {
            return string.Empty;
        }

        // The verdict sits right after the count, not at the end: a narrow window cuts the caption short, and what it must never cut is
        // "partial coverage" or "DEGRADED". The counts are the part that can go.
        var parts = new List<string>
        {
            FindingsCount,
            snapshot.Degraded ? "DEGRADED" : snapshot.Coverage.IsComplete ? "all planes up" : "partial coverage",
        };
        if (snapshot.ScannedAt is { } at)
        {
            parts.Add("polled " + Time(at));
        }

        parts.Add(snapshot.CoverageSummary);
        return string.Join(" · ", parts);
    }

    /// <summary>"The snapshot was polled 10:04:30; this panel read it 10:05:02."</summary>
    public string ReadText
    {
        get
        {
            if (_snapshot is null)
            {
                return string.Empty;
            }

            var polled = _snapshot.ScannedAt is { } at ? $"polled {Time(at)}" : _snapshot.Polled ? "polled (time unreadable)" : "not polled yet";
            return _readAt is { } read ? $"Snapshot {polled}; read by this app at {Time(read)}." : $"Snapshot {polled}.";
        }
    }

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ---- Lifecycle -------------------------------------------------------------------------------------------------------

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(true);
        await LoadPrerequisitesAsync().ConfigureAwait(true);
        await ReadPlaneCAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Catch-up first, then listen. The snapshot is re-read when the panel comes back after it has aged, and whenever the gateway comes back;
    /// nothing runs on a timer.
    /// </summary>
    protected override void OnActivated()
    {
        Services.Monitor.StateChanged += OnGatewayStateChanged;
        Services.Runtime.Changed += OnRuntimeChanged;
        Services.ConfigReloaded += OnConfigReloaded;

        // A change to config.yaml or .env while the panel was away was not heard: compare the files with what the snapshot was read under.
        _ = NoteConfigMoved();
        RaiseActionState();

        if (!_loadRunning && (_snapshot is null || IsStale || _readStamp.HasElapsed(StaleAfter)))
        {
            _ = LoadSafelyAsync();
        }
    }

    protected override void OnDeactivated()
    {
        Services.Monitor.StateChanged -= OnGatewayStateChanged;
        Services.Runtime.Changed -= OnRuntimeChanged;
        Services.ConfigReloaded -= OnConfigReloaded;
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => RaiseActionState();

    /// <summary>config.yaml or .env were rewritten (the existing watcher raises this for both): the snapshot on screen is stale if that postdates its read. It is kept either way.</summary>
    private void OnConfigReloaded(object? sender, EventArgs e) => NoteConfigMoved();

    /// <summary>
    /// Compares config.yaml and .env with what the snapshot on screen was read under (a stat of each, never their content) and, when they
    /// differ, marks it stale with <see cref="ConfigMovedReason"/>. Looks only when asked - a reload notification, the panel coming back,
    /// a change about to start or be confirmed - never on a timer. A read that began after the change finds nothing different.
    /// </summary>
    /// <returns>True when the snapshot is stale (now or already).</returns>
    internal bool NoteConfigMoved()
    {
        if (_snapshot is not null && !IsStale && _readSignature is { } read && ConfigDiskSignature.Capture(Services.Paths) != read)
        {
            MarkStale(ConfigMovedReason);
        }

        return IsStale;
    }

    private void OnGatewayStateChanged(object? sender, GatewaySnapshotEventArgs e) => NoteGatewayState(e.Snapshot.State);

    /// <summary>
    /// The gateway went away or came back. Away: what is on screen is stale from now on, and the reason says why. Back: read again.
    /// <c>Unknown</c> (before the first poll) and <c>Degraded</c> (it answered, not cleanly) still get a read: the read decides.
    /// </summary>
    internal void NoteGatewayState(AppGatewayState state)
    {
        var reachable = state is not (AppGatewayState.GatewayStopped or AppGatewayState.WslGatewayDetected or AppGatewayState.NotInstalled or AppGatewayState.NotInitialized);
        var wasReachable = _gatewayReachable;
        _gatewayReachable = reachable;

        if (!reachable && _snapshot is not null)
        {
            MarkStale("The gateway is not running, so the coverage above may no longer be true.");
        }
        else if (reachable && !wasReachable)
        {
            _ = LoadSafelyAsync();
        }
    }

    /// <summary>F5, the toolbar's Refresh and the shell's Refresh: read the snapshot again.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the failure is shown as a stale snapshot.
        catch (Exception ex)
        {
            Trace.TraceError($"Runtime panel catch-up failed: {ex}");
            Apply(new AiRuntimeRead(AiRuntimeReadStatus.Failed, null, "The read failed unexpectedly (" + ex.GetType().Name + ")."));
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Reads the snapshot. One read at a time: a request that arrives while one is running is remembered and served once the first ends, so
    /// "read again after the poll" is never lost to a read that started a moment earlier.
    /// </summary>
    internal async Task LoadAsync()
    {
        if (_loadRunning)
        {
            _reloadRequested = true;
            return;
        }

        _loadRunning = true;
        IsLoading = true;
        RaiseActionState();
        try
        {
            do
            {
                _reloadRequested = false;
                var read = await ReadOnceAsync().ConfigureAwait(true);
                Apply(read);
            }
            while (_reloadRequested);
        }
        finally
        {
            _loadRunning = false;
            IsLoading = false;
            RaiseActionState();
        }
    }

    private async Task<AiRuntimeRead> ReadOnceAsync()
    {
        // What config.yaml and .env look like as this read starts: the snapshot it produces was read under that.
        _pendingSignature = ConfigDiskSignature.Capture(Services.Paths);

        try
        {
            var result = ReadSnapshot is { } read
                ? await read(CancellationToken.None).ConfigureAwait(true)
                : await Services.Gateway.GetRawJsonAsync(AiRuntimeReader.Route, requiresAuth: true, CancellationToken.None).ConfigureAwait(true);

            // Parsing is linear in the answer and runs off the UI thread; the document is disposed with it.
            return await Task.Run(() =>
            {
                try
                {
                    return AiRuntimeReader.FromGateway(result);
                }
                finally
                {
                    result.Value?.Dispose();
                }
            }).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Any failure to read is a state of the panel, never an unhandled exception.
        catch (Exception ex)
        {
            Trace.TraceError($"Runtime snapshot read failed: {ex}");
            return new AiRuntimeRead(AiRuntimeReadStatus.Failed, null, "The read failed unexpectedly (" + ex.GetType().Name + ").");
        }
#pragma warning restore CA1031
    }

    /// <summary>Takes a read's outcome into the panel. See the type documentation for what is kept and what is cleared.</summary>
    internal void Apply(AiRuntimeRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        switch (read.Status)
        {
            case AiRuntimeReadStatus.Ok when read.Snapshot is { } snapshot:
                _snapshot = snapshot;
                _readAt = DateTimeOffset.UtcNow;
                _readStamp = MonotonicStamp.Now();
                _gatewayReachable = true;
                _unavailableReason = string.Empty;
                IsStale = false;
                StaleReason = string.Empty;
                _readSignature = _pendingSignature ?? ConfigDiskSignature.Capture(Services.Paths);
                _pendingSignature = null;
                Project(snapshot);

                // The files moved while this read was running: nothing says which side of the edit it saw.
                _ = NoteConfigMoved();
                break;

            case AiRuntimeReadStatus.Unsupported:
                // The version has no such route: that is a fact about the runtime, so nothing from before it is kept.
                _snapshot = null;
                _readAt = null;
                _readSignature = null;
                _pendingSignature = null;
                _unavailableReason = string.Empty;
                IsStale = false;
                StaleReason = string.Empty;
                ClearProjection();
                SetState(AiRuntimeState.Unsupported);
                break;

            default:
                _pendingSignature = null;
                if (_snapshot is null)
                {
                    _unavailableReason = read.Message;
                    ClearProjection();
                    SetState(AiRuntimeState.Unavailable);
                }
                else
                {
                    // Keep the last good snapshot. Replacing a stale-but-true report with an empty one would read as a clean host.
                    MarkStale(read.Message);
                }

                break;
        }

        RaiseActionState();
    }

    private void MarkStale(string reason)
    {
        StaleReason = reason;
        IsStale = true;
        OnPropertyChanged(nameof(CaptionText));
        RaiseActionState();
    }

    private void SetState(AiRuntimeState state)
    {
        if (State == state)
        {
            // The properties that follow the state still depend on what was just applied.
            OnPropertyChanged(nameof(CaptionText));
            OnPropertyChanged(nameof(StateDetail));
            return;
        }

        State = state;
    }

    // ---- Projection ------------------------------------------------------------------------------------------------------

    private void Project(AiRuntimeSnapshot snapshot)
    {
        var coverage = snapshot.Coverage;

        Planes.Clear();
        foreach (var plane in snapshot.Planes)
        {
            Planes.Add(new AiRuntimePlaneRow(plane, IsStale));
        }

        OtherGaps.Clear();
        foreach (var gap in coverage.Gaps.Where(g => !snapshot.PlanesNotUp.Any(p => p.Summary == g) && g != snapshot.UnattributedWarning))
        {
            OtherGaps.Add(gap);
        }

        _coverageHeadlineCore = coverage.Headline;
        (_coverageBadge, _coverageToneCore) = snapshot.Planes.Count == 0
            ? ("Unknown", "Neutral")
            : coverage.IsComplete ? ("All planes up", "Ok") : ("Partial coverage", "Warn");
        CountsText = snapshot.CoverageSummary;
        HostPlaneText = snapshot.HostPlaneObservations > 0 || snapshot.HostPlaneGated > 0 || snapshot.Planes.Any(p => p.Id == "c" && p.Running)
            ? snapshot.HostPlaneSummary
            : string.Empty;
        UnattributedWarning = snapshot.UnattributedWarning;

        SetState(!snapshot.Enabled ? AiRuntimeState.Disabled : !snapshot.Polled ? AiRuntimeState.NoPoll : AiRuntimeState.Ready);
        ApplyRows();

        RaiseCoverage();
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(StaleBanner));
        RebuildPlaneC(snapshot);
    }

    private void ClearProjection()
    {
        Planes.Clear();
        OtherGaps.Clear();
        _coverageHeadlineCore = string.Empty;
        (_coverageBadge, _coverageToneCore) = ("Unknown", "Neutral");
        CountsText = string.Empty;
        HostPlaneText = string.Empty;
        UnattributedWarning = string.Empty;
        _selectedId = null;
        ApplyRows();
        RaiseCoverage();
        RebuildPlaneC(null);
    }

    private void RaiseCoverage()
    {
        OnPropertyChanged(nameof(CoverageBadgeText));
        OnPropertyChanged(nameof(CoverageTone));
        OnPropertyChanged(nameof(CoverageHeadline));
        OnPropertyChanged(nameof(HasHostPlaneText));
        OnPropertyChanged(nameof(HasOtherGaps));
        OnPropertyChanged(nameof(HasNoPlanes));
        OnPropertyChanged(nameof(ReadText));
    }

    partial void OnFilterTextChanged(string value) => ApplyRows();

    /// <summary>The chips follow the snapshot's freshness: a stale "up" is not green.</summary>
    partial void OnIsStaleChanged(bool value)
    {
        foreach (var plane in Planes)
        {
            plane.IsStale = value;
        }
    }

    partial void OnSelectedFindingChanged(AiRuntimeFindingRow? value)
    {
        if (!_syncing)
        {
            _selectedId = value?.Id;
        }
    }

    /// <summary>
    /// Brings <see cref="Rows"/> in line with the snapshot and the filter without rebuilding it: a row that did not change keeps its visuals.
    /// The selection follows the finding's id across a refresh and clears when the finding is gone (or filtered out), so the inspector never
    /// keeps describing a process that has exited.
    /// </summary>
    private void ApplyRows()
    {
        IReadOnlyList<AiRuntimeFindingRow> wanted = _snapshot is { Enabled: true, Polled: true } snapshot
            ? snapshot.Findings.Where(f => f.Matches(FilterText)).Select(f => new AiRuntimeFindingRow(f)).ToArray()
            : [];

        var keep = _selectedId;
        _syncing = true;
        try
        {
            SyncCollection(Rows, wanted, row => row.Id, (existing, desired) => string.Equals(existing.Signature, desired.Signature, StringComparison.Ordinal));
        }
        finally
        {
            _syncing = false;
        }

        var selected = keep is null ? null : Rows.FirstOrDefault(row => string.Equals(row.Id, keep, StringComparison.Ordinal));
        if (!ReferenceEquals(SelectedFinding, selected))
        {
            SelectedFinding = selected;
        }
        else if (selected is null)
        {
            _selectedId = null;
        }

        OnPropertyChanged(nameof(FindingsCaption));
        OnPropertyChanged(nameof(ShowNoFindings));
        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(ShowFindingsTable));
        OnPropertyChanged(nameof(NoFindingsDetail));
        OnPropertyChanged(nameof(CaptionText));
    }

    /// <summary>Esc: closes the review if it is open, else the inspector. True when it consumed the key.</summary>
    public bool HandleEscape()
    {
        if (Review.IsOpen)
        {
            return Review.HandleEscape();
        }

        if (HasSelection)
        {
            ClearSelection();
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void ClearSelection() => SelectedFinding = null;

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;
}
