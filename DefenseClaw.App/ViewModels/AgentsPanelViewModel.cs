using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>What the directory half of the Agents panel is showing.</summary>
public enum AgentsState
{
    /// <summary>The first read is on its way.</summary>
    Loading,

    /// <summary>The gateway has no agent directory or correlation route (HTTP 404 or 405): a runtime without the ledger.</summary>
    Unsupported,

    /// <summary>The first read failed, so there is no directory at all. Not an empty one.</summary>
    Unavailable,

    /// <summary>The gateway says it keeps no directory (a managed host).</summary>
    Disabled,

    /// <summary>The directory was read and holds no agent identities.</summary>
    Empty,

    /// <summary>A directory with agents is on screen.</summary>
    Ready,
}

/// <summary>
/// The Agents panel: the agents DefenseClaw has seen, per connector, and for the one picked a tree of its subagents, an ordered timeline, and what
/// the gateway enforced - read from the gateway's own correlation ledger, with no Docker, Grafana or Loki behind it.
/// <para>
/// <b>Where the data comes from.</b> The directory is <c>GET /api/v1/agents/identities</c> (one identity per harness install and user, newest first).
/// The ledger's agents are per session, so an identity is opened through the session it last reported: <c>GET /api/v1/correlation/graph?session_id=</c>
/// names the agents in it, and the walk then reads each agent's graph (<c>?agent_id=</c>) and follows <c>parent_of</c> / <c>delegated_by</c> edges both
/// ways. <c>conflicts</c> adds the deliveries that contradicted each other. <c>timeline</c> and <c>explain</c> are not read: the gateway builds them
/// from the same graph query, so the ordered timeline is built here from the graph pages, and one request serves the tree, the timeline and the counts.
/// An agent id typed into the lookup box opens a tree directly, for an agent the directory does not list (a subagent, or a runtime with no directory).
/// </para>
/// <para>
/// <b>Nothing is hidden, nothing is estimated.</b> Every edge shows its method (reported, trace exact, derived, inferred) and its status (active,
/// unresolved, conflicted, superseded, rejected) on the tree whatever they are. The ledger stores identifiers and the names of records, not
/// payloads: tokens, cost, and whether a guardrail verdict was an enforced block or an observe-mode would-block are not in it, so those tiles say
/// "Not reported" (see <see cref="AgentKpis"/>). The panel is read-only: it has no button that changes anything.
/// </para>
/// <para>
/// <b>Stale, not replaced.</b> A directory read that fails after one succeeded keeps the list, marked stale with the reason. A 404 or 405 is different:
/// it is the version, and clears the list.
/// </para>
/// </summary>
public sealed partial class AgentsPanelViewModel : PanelViewModelBase
{
    public const string UnsupportedTitle = "The agent view is not supported by this gateway";

    public const string UnsupportedDetail =
        "The selected gateway does not serve the agent directory or the correlation ledger (/api/v1/agents/identities, /api/v1/correlation/*). " +
        "Install a DefenseClaw 1.0 build to use this view. This is a limit of the version, not a problem with this PC.";

    public const string UnavailableTitle = "Agent directory unavailable";

    public const string UnavailableDetail = "The directory could not be read, so nothing can be said about which agents exist. This is not an empty directory.";

    public const string DisabledTitle = "This gateway keeps no agent directory";

    public const string DisabledDetail = "The gateway reported that agent identities are not recorded on this host. An agent id can still be opened with the lookup box.";

    public const string EmptyTitle = "No agents seen yet";

    public const string EmptyDetail = "The directory is empty: no agent has reported through a connector yet. An agent id can still be opened with the lookup box.";

    public const string LoadingTitle = "Reading the agent directory";

    /// <summary>The directory is re-read when the panel comes back after this long.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>Directory page size: the gateway's own default is 1000; a screenful of rows is plenty per page.</summary>
    internal const int DirectoryPageSize = 200;

    /// <summary>Events per graph page (the gateway's largest is 500).</summary>
    internal const int GraphPageSize = CorrelationRoutes.DefaultLimit;

    /// <summary>The most timeline lines shown; the rest stay in the ledger and the caption says so.</summary>
    internal const int MaxTimelineRows = 1500;

    private AgentDirectoryPage? _directory;
    private AgentLedger? _ledger;
    private MonotonicStamp _readStamp;
    private CancellationTokenSource? _openCts;
    private bool _loadRunning;
    private bool _syncingSelection;
    private IReadOnlyList<TimelineEntry> _scopeEntries = [];
    private string _focusAgentId = string.Empty;
    private int _openVersion;
    private int _listCapped;

    public AgentsPanelViewModel(AppServices services)
        : base(services)
    {
        AgentsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Agents);
        AgentsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AgentDirectoryRowVm.Connector)));
        AgentsView.SortDescriptions.Add(new SortDescription(nameof(AgentDirectoryRowVm.Connector), ListSortDirection.Ascending));
        AgentsView.SortDescriptions.Add(new SortDescription(nameof(AgentDirectoryRowVm.LastSeen), ListSortDirection.Descending));
        AgentsView.Filter = o => o is AgentDirectoryRowVm row && row.Matches(FilterText.Trim());
    }

    public override string Title => "Agents";

    public override string Description =>
        "The agents DefenseClaw has seen, per connector: their subagents, what they did in order, and what the gateway blocked - read from the gateway's own correlation ledger.";

    /// <summary>Test seam: the gateway read, so a test feeds exact answers and opens no socket.</summary>
    internal Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>>? Fetch { get; set; }

    private CorrelationService Service => new(Fetch ?? ((path, max, ct) => Services.Gateway.GetBoundedJsonAsync(path, max, true, ct)));

    // ---- Collections -----------------------------------------------------------------------------------------------------

    /// <summary>Every identity the directory returned.</summary>
    public ObservableCollection<AgentDirectoryRowVm> Agents { get; } = new();

    /// <summary>The directory grouped by connector, newest first, narrowed by the filter box.</summary>
    public ListCollectionView AgentsView { get; }

    /// <summary>The tops of the selected agent's trees (one for a session with a single main agent).</summary>
    public ObservableCollection<AgentTreeNodeVm> TreeRoots { get; } = new();

    /// <summary>The ordered timeline of the focused agent, with a header line per turn.</summary>
    public ObservableCollection<TimelineRowVm> Timeline { get; } = new();

    /// <summary>The tiles above the timeline.</summary>
    public ObservableCollection<KpiTile> Kpis { get; } = new();

    /// <summary>Lines about what could not be read or was cut (a descendant that failed, a walk that hit its limit).</summary>
    public ObservableCollection<string> Notes { get; } = new();

    // ---- State -----------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStateCard), nameof(ShowDirectory), nameof(StateTitle), nameof(StateDetail), nameof(CaptionText))]
    private AgentsState _state = AgentsState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDetail))]
    private string _unavailableReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaleBanner), nameof(CaptionText))]
    private bool _isStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaleBanner))]
    private string _staleReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpinner))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpinner))]
    private bool _isOpening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(ShowNoMatch))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _lookupText = string.Empty;

    [ObservableProperty]
    private string _lookupProblem = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowPickHint), nameof(SelectedTitle), nameof(SelectedSubtitle))]
    private AgentDirectoryRowVm? _selectedAgent;

    /// <summary>Why the selected agent has no tree (no session reported, a failed read), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenMessage))]
    private string _openMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    private string _conflictsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowTimeline), nameof(HasSelection), nameof(ShowPickHint), nameof(SelectedTitle))]
    private bool _hasLedger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineCaption))]
    private bool _showMetrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineCaption))]
    private bool _includeSubagents = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineCaption), nameof(CanLoadMore))]
    private bool _isPaging;

    [ObservableProperty]
    private string _openedLabel = string.Empty;

    public bool ShowSpinner => IsLoading || IsOpening;

    public bool HasSelection => SelectedAgent is not null || HasLedger;

    public bool ShowPickHint => !HasSelection;

    public bool HasOpenMessage => OpenMessage.Length > 0;

    public bool HasConflicts => ConflictsText.Length > 0;

    public bool ShowStateCard => State != AgentsState.Ready;

    public bool ShowDirectory => State == AgentsState.Ready;

    public bool ShowNoMatch => State == AgentsState.Ready && AgentsView.IsEmpty && FilterText.Trim().Length > 0;

    public bool ShowTree => HasLedger && TreeRoots.Count > 0;

    public bool ShowTimeline => HasLedger;

    public string StateTitle => State switch
    {
        AgentsState.Loading => LoadingTitle,
        AgentsState.Unsupported => UnsupportedTitle,
        AgentsState.Unavailable => UnavailableTitle,
        AgentsState.Disabled => DisabledTitle,
        AgentsState.Empty => EmptyTitle,
        _ => string.Empty,
    };

    public string StateDetail => State switch
    {
        AgentsState.Unsupported => UnsupportedDetail,
        AgentsState.Unavailable => UnavailableReason.Length > 0 ? UnavailableDetail + " " + UnavailableReason : UnavailableDetail,
        AgentsState.Disabled => DisabledDetail,
        AgentsState.Empty => EmptyDetail,
        _ => string.Empty,
    };

    public string StaleBanner => IsStale ? "The agents below are from the last successful read. " + StaleReason : string.Empty;

    public string SelectedTitle => SelectedAgent is { } row
        ? $"{row.Connector} agent {row.ShortId}"
        : OpenedLabel.Length > 0 ? OpenedLabel : "No agent selected";

    public string SelectedSubtitle => SelectedAgent is { } row
        ? string.Join(" · ", new[] { row.UserName.Length > 0 ? row.UserName : null, row.LastSeenText, row.SessionsText, row.Retired ? row.RetiredText : null }.Where(s => s is not null))
        : string.Empty;

    public string CaptionText
    {
        get
        {
            var text = State switch
            {
                AgentsState.Ready when _directory is { } d =>
                    d.Total > d.Rows.Count
                        ? $"{d.Rows.Count.ToString(CultureInfo.InvariantCulture)} of {d.Total.ToString(CultureInfo.InvariantCulture)} agents, per connector"
                        : $"{d.Rows.Count.ToString(CultureInfo.InvariantCulture)} {(d.Rows.Count == 1 ? "agent" : "agents")}, per connector",
                AgentsState.Loading => "Reading the agent directory",
                _ => StateTitle,
            };
            return IsStale ? text + " (stale)" : text;
        }
    }

    public string TimelineCaption
    {
        get
        {
            if (!HasLedger)
            {
                return string.Empty;
            }

            var shown = Timeline.Count(r => r.IsEntry);
            var text = $"{shown.ToString(CultureInfo.InvariantCulture)} {(shown == 1 ? "entry" : "entries")} in time order, grouped by turn";
            if (_scopeEntries.Count(e => !ShowMetrics && e.IsMetric) is > 0 and var hidden)
            {
                text += $"; {hidden.ToString(CultureInfo.InvariantCulture)} metric points hidden";
            }

            if (_listCapped > 0)
            {
                text += $"; only the first {MaxTimelineRows.ToString(CultureInfo.InvariantCulture)} of {_listCapped.ToString(CultureInfo.InvariantCulture)} loaded entries are listed";
            }

            if (CanLoadMore)
            {
                text += "; more events are available";
            }

            return IsPaging ? text + " (loading more)" : text;
        }
    }

    /// <summary>True when an agent in the focused scope has events the gateway has not sent yet.</summary>
    public bool CanLoadMore => !IsPaging && _ledger is { } ledger && ScopeIds().Any(ledger.HasMore);

    public string KpiCaption
    {
        get
        {
            if (!HasLedger)
            {
                return string.Empty;
            }

            return CanLoadMore
                ? "Counts cover the events loaded so far; load more to count the rest."
                : "Counts cover every event the gateway returned for these agents.";
        }
    }

    // ---- Lifecycle -------------------------------------------------------------------------------------------------------

    public override Task InitializeAsync(CancellationToken cancellationToken = default) => LoadDirectoryAsync();

    protected override void OnActivated()
    {
        if (!_loadRunning && (_directory is null || IsStale || _readStamp.HasElapsed(StaleAfter)))
        {
            _ = LoadDirectorySafelyAsync();
        }
    }

    partial void OnFilterTextChanged(string value) => AgentsView.Refresh();

    partial void OnShowMetricsChanged(bool value) => RebuildTimeline();

    partial void OnIncludeSubagentsChanged(bool value) => RebuildTimeline();

    partial void OnSelectedAgentChanged(AgentDirectoryRowVm? value)
    {
        if (_syncingSelection || value is null)
        {
            return;
        }

        _ = OpenIdentitySafelyAsync(value);
    }

    /// <summary>F5, the toolbar's Refresh and the shell's Refresh: read the directory again (and the open agent, when one is).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDirectoryAsync().ConfigureAwait(true);
        if (SelectedAgent is { } row)
        {
            await OpenIdentityAsync(row).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task OpenLookupAsync()
    {
        var anchor = CorrelationAnchor.ForAgent(LookupText.Trim());
        if (anchor.Problem() is { } problem)
        {
            LookupProblem = problem;
            return Task.CompletedTask;
        }

        LookupProblem = string.Empty;
        return OpenAgentIdAsync(anchor.Value);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_ledger is not { } ledger || IsPaging)
        {
            return;
        }

        var version = _openVersion;
        var token = _openCts?.Token ?? CancellationToken.None;
        IsPaging = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            foreach (var id in ScopeIds().Where(ledger.HasMore).ToArray())
            {
                if (ledger.NextCursor(id) is not { } cursor)
                {
                    continue;
                }

                var anchor = id.StartsWith("session:", StringComparison.Ordinal) ? CorrelationAnchor.ForSession(id["session:".Length..]) : CorrelationAnchor.ForAgent(id);
                var read = await Service.ReadGraphAsync(anchor, GraphPageSize, cursor.Time, cursor.Id, token).ConfigureAwait(true);
                if (version != _openVersion)
                {
                    return;
                }

                if (read.IsOk && read.Value is { } page)
                {
                    _ = ledger.AddPage(id, page);
                }
                else
                {
                    Notes.Add($"More events for {AgentTree.Short(id)} could not be read: {read.Message}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsPaging = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }

        RebuildTimeline();
    }

    // ---- Directory -------------------------------------------------------------------------------------------------------

    private async Task LoadDirectorySafelyAsync()
    {
        try
        {
            await LoadDirectoryAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the failure is shown as a stale list.
        catch (Exception ex)
        {
            Trace.TraceError($"Agents panel catch-up failed: {ex}");
            ApplyDirectory(new CorrelationRead<AgentDirectoryPage>(CorrelationReadStatus.Failed, null, "The read failed unexpectedly (" + ex.GetType().Name + ")."));
        }
#pragma warning restore CA1031
    }

    internal async Task LoadDirectoryAsync()
    {
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        IsLoading = true;
        try
        {
            CorrelationRead<AgentDirectoryPage> read;
            try
            {
                read = await Service.ReadDirectoryAsync(DirectoryPageSize, CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // Any failure to read is a state of the panel, never an unhandled exception.
            catch (Exception ex)
            {
                Trace.TraceError($"Agent directory read failed: {ex}");
                read = new CorrelationRead<AgentDirectoryPage>(CorrelationReadStatus.Failed, null, "The read failed unexpectedly (" + ex.GetType().Name + ").");
            }
#pragma warning restore CA1031

            ApplyDirectory(read);
        }
        finally
        {
            _loadRunning = false;
            IsLoading = false;
        }
    }

    internal void ApplyDirectory(CorrelationRead<AgentDirectoryPage> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read.IsOk && read.Value is { } page)
        {
            _directory = page;
            _readStamp = MonotonicStamp.Now();
            IsStale = false;
            StaleReason = string.Empty;
            UnavailableReason = string.Empty;
            SyncRows(page);
            State = !page.Enabled ? AgentsState.Disabled : page.Rows.Count == 0 ? AgentsState.Empty : AgentsState.Ready;
            OnPropertyChanged(nameof(CaptionText));
            OnPropertyChanged(nameof(ShowNoMatch));
            return;
        }

        if (read.Status == CorrelationReadStatus.Unsupported)
        {
            // The version has no such route: a fact about the runtime, so nothing from before it is kept.
            _directory = null;
            Agents.Clear();
            IsStale = false;
            StaleReason = string.Empty;
            State = AgentsState.Unsupported;
            return;
        }

        if (_directory is null)
        {
            UnavailableReason = read.Message;
            State = AgentsState.Unavailable;
            return;
        }

        IsStale = true;
        StaleReason = read.Message;
    }

    private void SyncRows(AgentDirectoryPage page)
    {
        var keep = SelectedAgent?.AgentId;
        _syncingSelection = true;
        try
        {
            Agents.Clear();
            foreach (var row in page.Rows)
            {
                Agents.Add(new AgentDirectoryRowVm(row));
            }

            SelectedAgent = keep is null ? null : Agents.FirstOrDefault(r => r.AgentId == keep);
        }
        finally
        {
            _syncingSelection = false;
        }

        AgentsView.Refresh();
    }

    // ---- One agent -------------------------------------------------------------------------------------------------------

    private async Task OpenIdentitySafelyAsync(AgentDirectoryRowVm row)
    {
        try
        {
            await OpenIdentityAsync(row).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed open is a message on the panel, never an unhandled exception.
        catch (Exception ex)
        {
            Trace.TraceError($"Agents panel open failed: {ex}");
            OpenMessage = "The agent could not be opened (" + ex.GetType().Name + ").";
        }
#pragma warning restore CA1031
    }

    internal Task OpenIdentityAsync(AgentDirectoryRowVm row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.LastSessionId.Length == 0)
        {
            ClearLedger();
            OpenMessage = "This agent has not reported a session yet, so the ledger has nothing to open it by. Open a specific agent id with the lookup box.";
            return Task.CompletedTask;
        }

        var anchor = CorrelationAnchor.ForSession(row.LastSessionId);
        if (anchor.Problem() is { } problem)
        {
            ClearLedger();
            OpenMessage = "The session this agent last reported cannot be queried: " + problem;
            return Task.CompletedTask;
        }

        return OpenAsync(row.LastSessionId, [], string.Empty);
    }

    internal Task OpenAgentIdAsync(string agentId)
    {
        _syncingSelection = true;
        try
        {
            SelectedAgent = null;
        }
        finally
        {
            _syncingSelection = false;
        }

        return OpenAsync(null, [agentId], "Agent " + AgentTree.Short(agentId));
    }

    private async Task OpenAsync(string? sessionId, IReadOnlyList<string> startAgents, string label)
    {
        _openCts?.Cancel();
        _openCts?.Dispose();
        var cts = new CancellationTokenSource();
        _openCts = cts;
        var version = ++_openVersion;
        OpenedLabel = label;
        OnPropertyChanged(nameof(SelectedTitle));
        ClearLedger();
        IsOpening = true;
        try
        {
            var ledger = new AgentLedger();
            AgentLoadOutcome outcome;
            try
            {
                outcome = await Service.LoadAsync(ledger, sessionId, startAgents, GraphPageSize, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (version != _openVersion)
            {
                return;
            }

            if (!outcome.IsOk)
            {
                OpenMessage = outcome.Status == CorrelationReadStatus.Unsupported
                    ? "This gateway does not serve the correlation ledger, so this agent cannot be opened. " + UnsupportedDetail
                    : "The agent could not be read: " + outcome.Message;
                return;
            }

            Adopt(ledger, outcome);
            await LoadConflictsAsync(ledger, version, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (version == _openVersion)
            {
                IsOpening = false;
            }
        }
    }

    private void Adopt(AgentLedger ledger, AgentLoadOutcome outcome)
    {
        _ledger = ledger;
        OpenMessage = string.Empty;
        Notes.Clear();
        foreach (var note in outcome.Notes)
        {
            Notes.Add(note);
        }

        if (outcome.Capped)
        {
            Notes.Add($"The walk stopped at {AgentLedger.MaxAgents.ToString(CultureInfo.InvariantCulture)} agents; further subagents are not shown.");
        }

        if (ledger.Capped)
        {
            Notes.Add("The ledger view is full; later events are not shown.");
        }

        if (ledger.Skipped > 0)
        {
            Notes.Add($"{ledger.Skipped.ToString(CultureInfo.InvariantCulture)} entries from the gateway could not be read and were skipped.");
        }

        var agents = ledger.AgentIds;
        TreeRoots.Clear();
        var roots = AgentTree.RootsOf(agents, ledger.Relationships);
        var truncated = false;
        foreach (var root in roots)
        {
            var tree = AgentTree.Build(root, ledger.Relationships);
            truncated |= tree.Truncated;
            TreeRoots.Add(new AgentTreeNodeVm(tree.Root));
        }

        if (truncated)
        {
            Notes.Add($"A tree was cut at {AgentTree.MaxNodes.ToString(CultureInfo.InvariantCulture)} agents or {AgentTree.MaxDepth.ToString(CultureInfo.InvariantCulture)} levels.");
        }

        if (agents.Count == 0)
        {
            Notes.Add("No agent ids were reported in these events, so there is no tree; the timeline shows the session as a whole.");
        }

        HasLedger = true;
        OnPropertyChanged(nameof(ShowTree));
        _focusAgentId = TreeRoots.Count > 0 ? TreeRoots[0].AgentId : string.Empty;
        if (TreeRoots.Count > 0)
        {
            TreeRoots[0].IsSelected = true;
        }

        RebuildTimeline();
    }

    /// <summary>The conflicted edges are already in the tree; the deliveries that contradicted each other come from <c>conflicts</c>, per top of tree.</summary>
    private async Task LoadConflictsAsync(AgentLedger ledger, int version, CancellationToken token)
    {
        var conflicted = ledger.Relationships.Count(r => r.Status == RelationshipStatus.Conflicted);
        var receipts = 0;
        var failure = string.Empty;
        foreach (var root in TreeRoots.Take(3).Select(r => r.AgentId))
        {
            var read = await Service.ReadConflictsAsync(CorrelationAnchor.ForAgent(root), GraphPageSize, token).ConfigureAwait(true);
            if (version != _openVersion)
            {
                return;
            }

            if (read.IsOk && read.Value is { } page)
            {
                receipts += page.Receipts.Count;
            }
            else
            {
                failure = read.Message;
                break;
            }
        }

        if (failure.Length > 0)
        {
            ConflictsText = "Conflicts could not be read: " + failure;
            return;
        }

        var relationshipText = conflicted == 1 ? "1 relationship is conflicted" : $"{conflicted.ToString(CultureInfo.InvariantCulture)} relationships are conflicted";
        var receiptText = receipts == 1 ? "1 delivery contradicted an earlier one" : $"{receipts.ToString(CultureInfo.InvariantCulture)} deliveries contradicted an earlier one";
        ConflictsText = conflicted == 0 && receipts == 0
            ? "No conflicts reported for the agents loaded."
            : $"Conflicts: {relationshipText}; {receiptText}.";
    }

    private void ClearLedger()
    {
        _ledger = null;
        HasLedger = false;
        _focusAgentId = string.Empty;
        _scopeEntries = [];
        TreeRoots.Clear();
        Timeline.Clear();
        Kpis.Clear();
        Notes.Clear();
        ConflictsText = string.Empty;
        OpenMessage = string.Empty;
        OnPropertyChanged(nameof(ShowTree));
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(KpiCaption));
    }

    /// <summary>The tree node picked: the timeline and the counts follow it.</summary>
    internal void FocusAgent(string agentId)
    {
        _focusAgentId = agentId;
        RebuildTimeline();
    }

    /// <summary>The slice keys the focused scope reads: the focused agent, and (when asked) every agent under it; else the session, when there are no agents.</summary>
    private IReadOnlyList<string> ScopeIds()
    {
        if (_ledger is null)
        {
            return [];
        }

        if (_focusAgentId.Length == 0)
        {
            return SessionScopeKey is { Length: > 0 } key && _ledger.Has(key) ? [key] : [];
        }

        var node = TreeRoots.Select(r => r.Find(_focusAgentId)).FirstOrDefault(n => n is not null);
        if (node is null)
        {
            return [_focusAgentId];
        }

        return IncludeSubagents ? node.SubtreeIds().Distinct(StringComparer.Ordinal).ToArray() : [node.AgentId];
    }

    private string? SessionScopeKey => SelectedAgent is { LastSessionId.Length: > 0 } row ? AgentLedger.SessionKey(row.LastSessionId) : null;

    private void RebuildTimeline()
    {
        Timeline.Clear();
        Kpis.Clear();
        if (_ledger is not { } ledger)
        {
            return;
        }

        var scope = ScopeIds();
        var entries = ledger.Timeline(scope);
        _scopeEntries = entries;
        var visible = ShowMetrics ? entries : entries.Where(e => !e.IsMetric).ToArray();
        var shown = 0;
        foreach (var group in TimelineGrouping.ByTurn(visible))
        {
            if (shown >= MaxTimelineRows)
            {
                break;
            }

            var take = group.Entries.Take(MaxTimelineRows - shown).ToArray();
            Timeline.Add(TimelineRowVm.Header(group, take.Length));
            foreach (var entry in take)
            {
                Timeline.Add(TimelineRowVm.ForEntry(entry));
                shown++;
            }
        }

        _listCapped = visible.Count > shown ? visible.Count : 0;
        BuildKpis(AgentKpis.From(entries));
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(KpiCaption));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void BuildKpis(AgentKpis k)
    {
        Kpis.Add(new KpiTile("Enforced blocks", AgentKpis.Count(k.EnforcedBlocks), k.EnforcedBlocks > 0 ? "High" : "Neutral", "Blocks the gateway names as applied (tool call blocked, block applied).", true));
        Kpis.Add(new KpiTile("Observe-mode would-blocks", AgentKpis.NotReported, "Neutral", AgentKpis.WouldBlockNote, false));
        Kpis.Add(new KpiTile("Guardrail evaluations", AgentKpis.Count(k.GuardrailEvaluations), "Neutral", "Distinct guardrail and hook decisions recorded.", true));
        Kpis.Add(new KpiTile(
            "Approvals",
            $"{AgentKpis.Count(k.ApprovalsRequested)} asked · {AgentKpis.Count(k.ApprovalsResolved)} settled",
            "Neutral",
            "Approval requests and their resolutions.",
            true));
        Kpis.Add(new KpiTile("Model calls", AgentKpis.Count(k.ModelCalls), "Neutral", "Distinct model request and response events.", true));
        Kpis.Add(new KpiTile("Tool calls", AgentKpis.Count(k.ToolCalls), "Neutral", "Distinct tool call and result events.", true));
        Kpis.Add(new KpiTile("Tokens", AgentKpis.NotReported, "Neutral", AgentKpis.UsageNote, false));
        Kpis.Add(new KpiTile("Cost", AgentKpis.NotReported, "Neutral", AgentKpis.UsageNote, false));
    }

    /// <summary>Esc clears the filter box when it has text. False when there was nothing to clear.</summary>
    public bool HandleEscape()
    {
        if (FilterText.Length == 0)
        {
            return false;
        }

        FilterText = string.Empty;
        return true;
    }
}
