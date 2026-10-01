using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Live security findings: the <b>unacknowledged queue</b> read from <c>audit.db</c> (<see cref="AlertQueueReader"/>, the
/// Mac's one definition of "unacknowledged findings": up to the newest 500, the same number as the sidebar badge and the
/// tray), newest first; or, for a database that predates the queue's schema (or none), the last
/// <see cref="GatewayMonitor.AlertLimit"/> entries from <c>/alerts</c>, with the audit database as a fallback when the
/// gateway cannot serve those either.
/// <para>
/// <b>Which source.</b> The queue is primary because it is what the badge counts and what acknowledging empties, and because
/// it needs no gateway. The queue read gives ids, severity, action, target and connector; the rows' full details (rule,
/// title, evidence, attributes) are fetched by id for the rows this panel has not shown yet
/// (<see cref="AuditReader.GetByIdsAsync"/>), so a refresh that changes three findings reads three rows. It is read when the
/// panel comes on screen, when <see cref="AlertCountsService"/> reports a change (its 30 s cadence), on Refresh and after an
/// acknowledge or dismiss. While the queue is the source the gateway's own list is not consulted; if the queue cannot be used
/// (<see cref="AlertQueueStatus.LegacySchema"/>, no database) everything below about <c>/alerts</c> applies as it always did.
/// </para>
/// <para>
/// <b>Deep links.</b> <see cref="Accept"/> takes an <see cref="AlertsFilter"/>: a notification's click or a status chip opens
/// this panel already narrowed to a severity and above.
/// </para>
/// <para>
/// <b>Why the filters are not optional.</b> On a development box the alert stream is
/// dominated by the agent that is operating the box — every <c>$env:</c> reference trips
/// <c>CMD-ENV-DUMP</c> at HIGH. So this panel ships three affordances that make that
/// stream readable: severity toggles, a substring filter across rule id / title / action /
/// evidence, and a collapse-repeats switch that folds identical signature+action pairs
/// into one row with a count.
/// </para>
/// <para>
/// <b>What re-projects, and when.</b> The health poll runs every five seconds but
/// <c>/alerts</c> is only re-read every thirty, and <see cref="GatewayMonitor"/> hands back
/// the same <see cref="GatewaySnapshot.RecentAlerts"/> instance whenever a fresh answer shows
/// the same findings. So a poll that brings that same list re-projects nothing — it only
/// refreshes the "refreshed …" note — and a poll that brings a different list projects only
/// the alerts it has not seen (an alert id names an immutable audit row, so an already
/// projected row is reused, which also skips its JSON flattening). The bound list is then
/// merged by alert key rather than cleared and refilled, so scroll position and the selected
/// row survive. None of this runs while the panel is not on screen: it listens to
/// <see cref="GatewayMonitor.PollCompleted"/> and runs its relative-time clock only between
/// <see cref="OnActivated"/> and <see cref="OnDeactivated"/>.
/// </para>
/// <para>
/// <b>What "refreshed" means.</b> The health poll's <see cref="GatewaySnapshot.PolledAt"/>
/// says nothing about the alert list, which is re-read on its own slower cadence. The note
/// above the list therefore prints <see cref="GatewaySnapshot.AlertsFetchedAt"/> — when
/// <c>/alerts</c> last answered — and the Refresh button forces that read
/// (<see cref="GatewayMonitor.RefreshAlertsNowAsync"/>) instead of re-running a health poll
/// that would skip it. While the list came from the audit fallback, the note keeps saying
/// so on every poll, not only on the poll that loaded it.
/// </para>
/// <para>
/// <b>Filters act on the normalized severity.</b> A finding's stored severity can be a
/// spelling the toggles do not have (<c>WARN</c>, <c>FATAL</c>, <c>MODERATE</c>);
/// <see cref="AlertItem.SeverityKey"/> folds those into the bucket the row is coloured as,
/// and the toggles filter on that same key, so a row is always hidden by exactly the toggle
/// its chip looks like.
/// </para>
/// <para>
/// <b>Acknowledge / dismiss.</b> <c>defenseclaw alerts acknowledge|dismiss --severity X</c>
/// acts on the <i>whole severity class</i> in DefenseClaw, not on the handful of rows this list
/// loaded, so the panel never runs it blind: the operator picks the action, the panel runs the
/// same command with <c>--dry-run</c> (which the CLI documents as changing nothing) and shows what
/// would match, and only a preview that exited 0 with matches enables the real run, which is
/// gated on the preview's exit code and passes <c>--yes</c> (the CLI's own broad-selector
/// confirmation) instead of piping a <c>y</c> to a prompt. <b>What is confirmed is what was previewed.</b>
/// A severity selector matches whatever is active <i>when it runs</i>, and the confirm can come minutes
/// after the preview, so both commands carry <c>--before</c> with the moment the preview started (an alert
/// that arrives later is not selected), and a preview that lists twenty ids or fewer (all the CLI prints)
/// is applied as those exact ids, repeated <c>--id</c>. When the apply reports a different count from the
/// preview, the result says so. Afterwards the alert list is re-read and
/// then filtered against the read-only <c>alert_acknowledgement_projection</c> table in audit.db, so
/// an alert the gateway still serves after it was acknowledged is hidden rather than left on
/// screen looking untouched.
/// </para>
/// </summary>
public sealed partial class AlertsPanelViewModel : PanelViewModelBase, IAcceptsNavigation
{
    /// <summary>How many findings to pull from audit.db when /alerts cannot answer.</summary>
    private const int FallbackLimit = 100;

    private readonly List<AlertItem> _all = new();
    private readonly DispatcherTimer _clock;
    private int _loadingFallback;
    private DateTimeOffset _lastFallbackLoad = DateTimeOffset.MinValue;

    /// <summary>
    /// Why <c>/alerts</c> is not being served, as of the last snapshot applied; null while it
    /// is. The fallback load reads it when it finishes, so a load that outlives the outage
    /// does not decorate a note that no longer describes one.
    /// </summary>
    private string? _alertsUnavailableReason;

    /// <summary>
    /// The empty-state text the current <i>source</i> calls for. What the overlay actually
    /// shows also depends on whether the filters are hiding loaded alerts; see
    /// <see cref="ShowEmptyText"/>.
    /// </summary>
    private string _sourceEmptyTitle = "No alerts yet";

    private string _sourceEmptyDetail = "The first alert poll has not completed.";

    /// <summary>
    /// The monitor's alert list that <see cref="_all"/> was last projected from; null when
    /// <see cref="_all"/> came from audit.db or has not been filled. A poll whose
    /// <see cref="GatewaySnapshot.RecentAlerts"/> is this very instance has nothing to project.
    /// </summary>
    private IReadOnlyList<GatewayAlert>? _appliedAlerts;

    /// <summary>
    /// True while <see cref="_all"/> holds audit.db rows (the queue, or the fallback when the gateway serves no alerts). The
    /// sources project the same alert to slightly different rows (the audit path has no tags or confidence), so rows
    /// are never reused, and the bound list never merged, across a switch of source.
    /// </summary>
    private bool _allFromAudit;

    /// <summary>
    /// True while the audit queue is the source of <see cref="_all"/> (see the type documentation): the gateway's own list is then
    /// not consulted, and <see cref="Apply"/> leaves the rows alone. Set by the first queue read that succeeds, cleared when a read
    /// finds the queue unusable. UI thread only.
    /// </summary>
    private bool _queueActive;

    /// <summary>When the queue was last read, and whether its window was full (more findings waiting than it holds); for the source note.</summary>
    private DateTimeOffset _queueReadAt = DateTimeOffset.MinValue;

    private bool _queueHasMore;

    /// <summary>Serializes queue reads: one statement and one hydration at a time, however many callers ask.</summary>
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    /// <summary>True while a block of severity toggles is being set at once (see <see cref="SetSeverityFloor"/>): the list is filtered once afterwards, not once per toggle.</summary>
    private bool _settingToggles;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _collapseRepeats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AlertItem? _selectedAlert;

    [ObservableProperty]
    private string _sourceNote = "Waiting for the first alert poll…";

    [ObservableProperty]
    private string _countSummary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyTitle = "No alerts yet";

    [ObservableProperty]
    private string _emptyDetail = "The first alert poll has not completed.";

    // ---- Acknowledge / dismiss review ------------------------------------------------------

    private const string AcknowledgeVerb = "acknowledge";
    private const string DismissVerb = "dismiss";

    /// <summary>Alert ids per projection lookup (the list is at most a few hundred; SQLite allows far more parameters).</summary>
    private const int MaxAckLookup = 500;

    /// <summary>First line of the CLI's dry-run: <c>Preview: 25 alert(s) matched; digest=sha256:v1:...</c>.</summary>
    private static readonly Regex PreviewMatchedPattern = new(
        @"Preview:\s*(?<n>\d+)\s+alert\(s\)\s+matched",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Alert ids that <c>alert_acknowledgement_projection</c> says were acknowledged or dismissed.
    /// Grows only (an acknowledgement is not undone from here), so a lookup that finishes after
    /// the list has moved on cannot make a later list wrong. UI thread only.
    /// </summary>
    private readonly HashSet<string> _acknowledgedKeys = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _ackGate = new(1, 1);
    private string _reviewVerb = AcknowledgeVerb;
    private CancellationTokenSource? _previewCts;
    private int _previewVersion;
    private bool _openingReview;

    /// <summary>
    /// The command the confirm button runs, fixed by the preview that enabled it (and not rebuilt from the dialog at
    /// confirm time): the same selector plus <c>--before</c>, or - when the preview listed every match - the exact ids.
    /// </summary>
    private string[]? _applyArgv;

    /// <summary>How many ids a dry run prints before it says "... and N more" (the CLI's own cap).</summary>
    private const int PreviewedIdLimit = 20;

    /// <summary>One line the dry run prints per matched alert: <c>  &lt;id&gt; version=0</c>.</summary>
    private static readonly Regex PreviewedIdPattern = new(
        @"^\s+(?<id>\S+)\s+version=\S+\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>What the mutation prints when it is done: <c>Acknowledged 24 alert(s).</c> / <c>Dismissed 24 alert(s) from the active list.</c></summary>
    private static readonly Regex AppliedPattern = new(
        @"(?:Acknowledged|Dismissed)\s+(?<n>\d+)\s+alert\(s\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// How the two review commands reach the CLI. Null in the running app (<see cref="AppServices.Cli"/>); a test answers
    /// with canned invocations so what the dialog asks for, and what it says about the answer, can be checked without a
    /// process.
    /// </summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>>? RunCli { get; set; }

    /// <summary>What replaces the gateway re-read that follows a successful apply, when a test does not want a network call.</summary>
    internal Func<Task>? AfterApply { get; set; }

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken) =>
        RunCli is { } run ? run(argv, cancellationToken) : Services.Cli.RunAsync(argv, cancellationToken: cancellationToken);

    /// <summary>Alerts in the loaded list that are not acknowledged - what the toggles and text filter act on.</summary>
    private int _poolCount;

    [ObservableProperty]
    private bool _isReviewOpen;

    [ObservableProperty]
    private string _reviewHeading = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Acknowledge";

    [ObservableProperty]
    private SeverityChoice? _reviewSeverity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReview))]
    private bool _isPreviewing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReview))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReview))]
    private bool _previewSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReview))]
    private int _previewMatched;

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    /// <summary>The dry-run's own output, verbatim (matched count, digest, the first ids).</summary>
    [ObservableProperty]
    private string _previewOutput = string.Empty;

    [ObservableProperty]
    private bool _hasPreviewOutput;

    [ObservableProperty]
    private string _reviewError = string.Empty;

    [ObservableProperty]
    private bool _hasReviewError;

    /// <summary>The exact command the confirm button runs, as shown (mono, selectable) in the dialog.</summary>
    [ObservableProperty]
    private string _confirmCommandText = string.Empty;

    [ObservableProperty]
    private string _tierText = "State-changing";

    /// <summary>Medium (state-changing) or Bad (destructive) - the tone key of the tier badge.</summary>
    [ObservableProperty]
    private string _tierKey = "Medium";

    /// <summary>True for <c>dismiss</c>: the confirm button turns danger-styled and the tier badge says so.</summary>
    [ObservableProperty]
    private bool _isDestructive;

    [ObservableProperty]
    private bool _isNotDestructive = true;

    /// <summary>Result of the last acknowledge/dismiss (success). Two-way with the InfoBar's close button.</summary>
    [ObservableProperty]
    private bool _showActionSuccess;

    [ObservableProperty]
    private bool _showActionError;

    [ObservableProperty]
    private string _actionBannerText = string.Empty;

    /// <summary>Loaded alerts hidden because audit.db records them as acknowledged or dismissed.</summary>
    [ObservableProperty]
    private int _hiddenAcknowledgedCount;

    public AlertsPanelViewModel(AppServices services)
        : base(services)
    {
        foreach (var severity in new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO" })
        {
            var filter = new SeverityFilter(severity);

            // Only the on/off switch re-filters. The chip also carries a count that this panel
            // rewrites on every pass, and reacting to that would re-run the pass forever.
            filter.PropertyChanged += (_, e) =>
            {
                if (!_settingToggles && string.Equals(e.PropertyName, nameof(SeverityFilter.IsEnabled), StringComparison.Ordinal))
                {
                    ApplyFilters();
                }
            };
            SeverityFilters.Add(filter);
        }

        // "all" is last on purpose, and never the default: it reaches every active alert.
        foreach (var value in new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO", "ERROR" })
        {
            ReviewSeverities.Add(new SeverityChoice(value, value));
        }

        ReviewSeverities.Add(new SeverityChoice("all", "All severities (every active alert)"));

        // Relative timestamps go stale silently, which is the worst way for a monitoring
        // panel to lie. One timer re-stamps the rows - but only while someone can read them;
        // OnActivated starts it and re-stamps once so a returning operator never sees old text.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => RestampTimes();
    }

    public override string Title => "Alerts";

    public override string Description =>
        "Unacknowledged security findings, newest first, with severity and signature filters.";

    /// <summary>The filtered, optionally collapsed view bound to the list.</summary>
    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ObservableCollection<SeverityFilter> SeverityFilters { get; } = new();

    /// <summary>What <c>--severity</c> the review dialog can target (the CLI's own set, plus "all").</summary>
    public ObservableCollection<SeverityChoice> ReviewSeverities { get; } = new();

    /// <summary>Drives the detail pane's placeholder; no converter needed for the inverse.</summary>
    public bool HasSelection => SelectedAlert is not null;

    /// <summary>
    /// The confirm button is live only for a preview that ran to exit 0, matched something, and is
    /// not being redone or applied right now - the follow-up is gated on the previous exit code.
    /// </summary>
    public bool CanConfirmReview => PreviewSucceeded && PreviewMatched > 0 && !IsPreviewing && !IsApplying;

    public bool HasHiddenAcknowledged => HiddenAcknowledgedCount > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The gateway's list first, so the panel has something to show at once; the queue replaces it when it has been read.
        Apply(Services.Monitor.Current);
        await RefreshQueueAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Catch-up first, then listen: <c>Apply</c> compares against what is already projected,
    /// so returning to an unchanged alert list re-projects nothing, and a list that moved on
    /// while the panel was away is rebuilt before it is seen.
    /// </summary>
    protected override void OnActivated()
    {
        Apply(Services.Monitor.Current);
        RestampTimes();

        Services.Monitor.PollCompleted += OnPollCompleted;

        // The counts service runs while anything listens (the tray always does) and says when the queue changed; a finding that
        // arrives while this panel is open shows up on its next tick.
        Services.AlertCounts.Changed += OnAlertCountsChanged;
        _clock.Start();
        _ = RefreshQueueAsync();
    }

    protected override void OnDeactivated()
    {
        Services.Monitor.PollCompleted -= OnPollCompleted;
        Services.AlertCounts.Changed -= OnAlertCountsChanged;
        _clock.Stop();
        CancelDetail();
    }

    private void OnAlertCountsChanged(object? sender, AlertCountsChangedEventArgs e) => _ = RefreshQueueAsync();

    /// <summary>
    /// A deep link (<see cref="IAcceptsNavigation"/>): an <see cref="AlertsFilter"/> opens the panel on a severity and above and/or a
    /// kind (<see cref="AlertKinds"/>: "all", "blocks", "audit", "scans", "egress"), with the text filter cleared (the point of the link is
    /// to show those findings). A link that names a kind and no severity shows every severity of it (the Mac's <c>.all</c> /
    /// <c>.blocks</c> requests reset the severity filter); a link that names neither changes nothing. The toggles are state, so a link
    /// that lands before the first read is honoured when the rows arrive. A payload of another type, or a kind this panel does not
    /// know, is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is not AlertsFilter filter)
        {
            return;
        }

        var kind = AlertKinds.Normalize(filter.Kind);
        if (filter.SeverityFloor is null && kind is null)
        {
            return;
        }

        if (kind is not null)
        {
            KindFilter = kind;
        }

        FilterText = string.Empty;

        // No floor means "every severity": Unknown is below them all.
        SetSeverityFloor(filter.SeverityFloor ?? AuditSeverity.Unknown);
    }

    /// <summary>
    /// Turns on every severity toggle at or above <paramref name="floor"/> and off those below it, then filters once.
    /// The rows are the same either way; only what is showing changes.
    /// </summary>
    private void SetSeverityFloor(AuditSeverity floor)
    {
        _settingToggles = true;
        try
        {
            foreach (var filter in SeverityFilters)
            {
                filter.IsEnabled = AuditSeverityExtensions.Parse(filter.Severity) >= floor;
            }
        }
        finally
        {
            _settingToggles = false;
        }

        ApplyFilters();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();

    partial void OnCollapseRepeatsChanged(bool value) => ApplyFilters();

    /// <summary>
    /// Forces a real <c>/alerts</c> read. <see cref="GatewayMonitor.RefreshAsync"/> would only
    /// re-run the health poll, which skips <c>/alerts</c> until its 30 s throttle expires — the
    /// button would then relabel a list that was never re-fetched. The fallback's own 30 s
    /// throttle is reset too, so a gateway that is still down re-reads audit.db now.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        // What the inspector read for rows is read again, too: Refresh means "look now".
        ResetDetails();

        // The queue first: when it serves, the gateway's list is not what is on screen and there is nothing to re-read there.
        await RefreshQueueAsync();
        if (_queueActive)
        {
            return;
        }

        var snapshot = await Services.Monitor.RefreshAlertsNowAsync();
        _lastFallbackLoad = DateTimeOffset.MinValue;
        Apply(snapshot);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = string.Empty;
        CollapseRepeats = false;
        KindFilter = AlertKinds.All;
        foreach (var filter in SeverityFilters)
        {
            filter.IsEnabled = true;
        }
    }

    /// <summary>Every row selected in the table (the table is Extended-select; <see cref="SelectedAlert"/> is the first of them, which drives the detail pane).</summary>
    private IReadOnlyList<AlertItem> _selectedMany = Array.Empty<AlertItem>();

    /// <summary>The rows the table has selected, for the row menu: Copy details acts on all of them, and Acknowledge / Dismiss open on the worst of their severities.</summary>
    public IReadOnlyList<AlertItem> SelectedAlerts => _selectedMany;

    /// <summary>Called by the view whenever the table's selection changes.</summary>
    public void NoteSelection(IEnumerable<AlertItem> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        _selectedMany = selected.ToList();
        OpenAcknowledgeSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The rows a menu action applies to: the table's selection, or the one row the detail pane shows when the view has not reported a selection.</summary>
    public IReadOnlyList<AlertItem> ActionRows =>
        _selectedMany.Count > 0 ? _selectedMany : SelectedAlert is { } one ? new[] { one } : Array.Empty<AlertItem>();

    /// <summary>"time [SEVERITY] action target - details" for each row, one per line (the Mac's Copy Details).</summary>
    public static string CopyText(IEnumerable<AlertItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join(Environment.NewLine, rows.Select(r => r.CopyLine));
    }

    /// <summary>Closes the detail pane (Esc, or its own close button).</summary>
    [RelayCommand]
    private void ClearSelection() => SelectedAlert = null;

    partial void OnHiddenAcknowledgedCountChanged(int value) => OnPropertyChanged(nameof(HasHiddenAcknowledged));

    partial void OnReviewSeverityChanged(SeverityChoice? value)
    {
        // Picking another severity in the open dialog is a different command, so it gets its own
        // preview. Setting the default while the dialog is being opened is not a pick.
        if (!_openingReview && IsReviewOpen && value is not null)
        {
            _ = PreviewAsync();
        }
    }

    [RelayCommand]
    private Task OpenAcknowledgeAsync() => OpenReviewAsync(AcknowledgeVerb);

    [RelayCommand]
    private Task OpenDismissAsync() => OpenReviewAsync(DismissVerb);

    [RelayCommand]
    private void CancelReview()
    {
        CancelPreview();
        IsPreviewing = false;
        IsReviewOpen = false;
    }

    [RelayCommand]
    private void DismissActionBanner()
    {
        ShowActionSuccess = false;
        ShowActionError = false;
    }

    private async Task OpenReviewAsync(string verb)
    {
        _reviewVerb = verb;
        _openingReview = true;
        try
        {
            ReviewHeading = string.Equals(verb, DismissVerb, StringComparison.Ordinal) ? "Dismiss alerts" : "Acknowledge alerts";
            ReviewSeverity = DefaultReviewSeverity();
            SetReviewError(string.Empty);
            IsReviewOpen = true;
        }
        finally
        {
            _openingReview = false;
        }

        await PreviewAsync();
    }

    /// <summary>
    /// The severity the dialog opens on: the selected alert's, else the most severe class that has
    /// anything loaded. Never "all" - a broad selector is something the operator has to choose.
    /// </summary>
    private SeverityChoice DefaultReviewSeverity()
    {
        // Several rows selected in the table (the row menu opens this review for the selection): the worst of them.
        var wanted = _selectedMany.Count > 1
            ? _selectedMany.OrderByDescending(a => a.SeverityRank).First().Severity
            : SelectedAlert?.Severity;
        if (wanted is not null &&
            ReviewSeverities.FirstOrDefault(c => string.Equals(c.Value, wanted, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        foreach (var filter in SeverityFilters)
        {
            if (filter.Count > 0 &&
                ReviewSeverities.FirstOrDefault(c => string.Equals(c.Value, filter.Severity, StringComparison.Ordinal)) is { } loaded)
            {
                return loaded;
            }
        }

        return ReviewSeverities[0];
    }

    /// <summary>
    /// Runs the dry-run for the chosen action and severity and reports what it would touch. The
    /// dry-run is safe to run unreviewed on the strength of the CLI's own help ("Preview the exact
    /// matched IDs without mutating them"); the real command below is what needs the confirmation,
    /// and its tier comes from <see cref="CommandTiers"/> (dismiss is destructive, acknowledge is
    /// state-changing). A preview superseded by another pick, or by closing the dialog, is
    /// cancelled and its result discarded.
    /// </summary>
    private async Task PreviewAsync()
    {
        if (ReviewSeverity is not { } choice)
        {
            return;
        }

        CancelPreview();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var version = Volatile.Read(ref _previewVersion);

        var verb = _reviewVerb;

        // The moment this preview is taken. A severity selector matches whatever is active when it runs, so a bare
        // "--severity HIGH" applied minutes later also takes the alerts that arrived after the operator read the count.
        // "--before" pins both commands to the alerts that existed now (millisecond precision: a whole second would drop
        // the ones from the last instant).
        var before = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var previewArgv = new[] { "alerts", verb, "--severity", choice.Value, "--before", before, "--dry-run" };
        var applyArgv = new[] { "alerts", verb, "--severity", choice.Value, "--before", before, "--yes" };
        _applyArgv = null;

        ConfirmCommandText = "defenseclaw " + string.Join(' ', applyArgv);
        var tier = CommandTiers.Classify(applyArgv);
        IsDestructive = tier == CommandTier.Destructive;
        IsNotDestructive = !IsDestructive;
        // Same words and tone as the shared command review, so a tier reads identically everywhere.
        TierText = CommandReview.LabelFor(tier);
        TierKey = CommandReview.ToneFor(tier);
        ConfirmButtonText = string.Equals(verb, DismissVerb, StringComparison.Ordinal)
            ? (choice.IsAll ? "Dismiss all alerts" : $"Dismiss all {choice.Value}")
            : (choice.IsAll ? "Acknowledge all alerts" : $"Acknowledge all {choice.Value}");

        IsPreviewing = true;
        PreviewSucceeded = false;
        PreviewMatched = 0;
        PreviewOutput = string.Empty;
        HasPreviewOutput = false;
        SetReviewError(string.Empty);
        PreviewSummary = "Previewing: running the same command with --dry-run, which changes nothing…";

        try
        {
            var invocation = await RunCliAsync(previewArgv, cts.Token).ConfigureAwait(true);
            if (version != Volatile.Read(ref _previewVersion))
            {
                return;
            }

            var stdout = StreamText(invocation, CliStream.StandardOutput, maxLines: 30);
            PreviewOutput = stdout;
            HasPreviewOutput = stdout.Length > 0;

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                PreviewSummary = "The preview did not complete, so nothing can be applied.";
                SetReviewError($"The dry run did not complete: {reason}.");
                return;
            }

            if (invocation.ExitCode != 0)
            {
                // The follow-up is gated on this exit code: no green preview, no apply button.
                PreviewSummary = "The preview failed, so nothing can be applied.";
                SetReviewError(ErrorTail(invocation, "The dry run failed"));
                return;
            }

            var match = PreviewMatchedPattern.Match(stdout);
            if (!match.Success ||
                !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var matched))
            {
                PreviewSummary = "The preview finished, but its output was not in the expected form, so nothing can be applied.";
                SetReviewError("Expected a line like 'Preview: N alert(s) matched'. The full output is shown above and in Activity.");
                return;
            }

            // Twenty ids or fewer, all of them printed: apply exactly those (the CLI takes --id alone, not with a selector).
            // More than that, only the selector plus the preview's moment can name the set.
            var ids = PreviewedIds(invocation);
            var exact = matched is > 0 and <= PreviewedIdLimit && ids.Count == matched;
            if (exact)
            {
                applyArgv = new[] { "alerts", verb }
                    .Concat(ids.SelectMany(id => new[] { "--id", id }))
                    .Append("--yes")
                    .ToArray();
                ConfirmCommandText = "defenseclaw " + string.Join(' ', applyArgv);
            }

            _applyArgv = applyArgv;
            PreviewMatched = matched;
            PreviewSucceeded = true;
            PreviewSummary = DescribePreview(matched, choice, verb, exact);
        }
        catch (CliNotFoundException ex)
        {
            PreviewSummary = "The DefenseClaw CLI was not found, so nothing can be applied.";
            SetReviewError(ex.Message);
        }
        finally
        {
            if (version == Volatile.Read(ref _previewVersion))
            {
                IsPreviewing = false;
            }

            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Runs the real command - only reachable for a preview that exited 0 and matched something -
    /// then re-reads the alert list and the acknowledgement projection. On failure the dialog
    /// stays open with the CLI's own error and the full output is in Activity.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmReviewAsync()
    {
        if (!CanConfirmReview || ReviewSeverity is not { } choice)
        {
            return;
        }

        var verb = _reviewVerb;

        // The command the preview fixed - never one rebuilt now, which would select whatever is active now.
        if (_applyArgv is not { } argv)
        {
            return;
        }

        var previewed = PreviewMatched;

        IsApplying = true;
        SetReviewError(string.Empty);

        try
        {
            var invocation = await RunCliAsync(argv, CancellationToken.None).ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                SetReviewError($"'defenseclaw alerts {verb}' did not complete: {reason}. Check Activity, then run the preview again.");
                return;
            }

            if (invocation.ExitCode != 0)
            {
                SetReviewError(ErrorTail(invocation, $"'defenseclaw alerts {verb}' failed"));
                return;
            }

            IsReviewOpen = false;
            var output = StreamText(invocation, CliStream.StandardOutput, maxLines: 30);
            var done = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            var result = string.IsNullOrWhiteSpace(done)
                ? $"Ran 'defenseclaw alerts {verb} --severity {choice.Value}'."
                : done;
            ShowActionResult(result + DifferenceNote(output, previewed), isError: false);

            // The sidebar badge and the tray follow the acknowledge at once, not on the next 30 s tick.
            await RefreshCountsAsync();
            await (AfterApply?.Invoke() ?? ReloadAfterDispositionAsync());
        }
        catch (CliNotFoundException ex)
        {
            SetReviewError(ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Re-reads the unacknowledged count now (<see cref="AlertCountsService.RefreshAsync"/>); it records its own failures, so only a cancellation can end it early.</summary>
    private async Task RefreshCountsAsync()
    {
        try
        {
            await Services.AlertCounts.RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: nobody is left to show the count to.
        }
    }

    /// <summary>
    /// Re-fetches the alert list now (not on the 30 s throttle) and then hides whatever
    /// <c>alert_acknowledgement_projection</c> still marks as acknowledged, in case the gateway
    /// keeps serving it.
    /// </summary>
    private async Task ReloadAfterDispositionAsync()
    {
        // The queue excludes what was just acknowledged by definition, so when it is the source that is all there is to do.
        await RefreshQueueAsync();
        if (_queueActive)
        {
            return;
        }

        var snapshot = await Services.Monitor.RefreshAlertsNowAsync();
        _lastFallbackLoad = DateTimeOffset.MinValue;
        Apply(snapshot);
        await RefreshAcknowledgedAsync();
    }

    private void CancelPreview()
    {
        _ = Interlocked.Increment(ref _previewVersion);
        var cts = _previewCts;
        _previewCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The preview finished in the instant it was superseded; there is nothing to stop.
        }
    }

    private void SetReviewError(string message)
    {
        ReviewError = message;
        HasReviewError = message.Length > 0;
    }

    private void ShowActionResult(string message, bool isError)
    {
        ActionBannerText = message;
        ShowActionSuccess = !isError;
        ShowActionError = isError;
    }

    private string DescribePreview(int matched, SeverityChoice choice, string verb, bool exactIds)
    {
        var scope = choice.IsAll ? "active alert" : $"active {choice.Value} alert";
        if (matched == 0)
        {
            return $"No {scope} matches, so there is nothing to {verb}.";
        }

        var doing = string.Equals(verb, DismissVerb, StringComparison.Ordinal) ? "Dismissing" : "Acknowledging";
        var plural = matched == 1 ? string.Empty : "s";
        var count = matched.ToString("N0", CultureInfo.CurrentCulture);
        if (exactIds)
        {
            // Every match is listed below, and the command names each of them.
            return $"{count} {scope}{plural} match. {doing} applies to exactly the {(matched == 1 ? "alert" : "alerts")} listed below, and to nothing that arrives after this preview.";
        }

        var everything = choice.IsAll ? "every active alert of every severity" : $"the whole {choice.Value} severity class";
        return
            $"{count} {scope}{plural} match. {doing} applies to {everything} " +
            $"in DefenseClaw as of this preview, not only the {_all.Count.ToString("N0", CultureInfo.CurrentCulture)} loaded in this list; " +
            "an alert that arrives after it is left alone.";
    }

    /// <summary>The ids a dry run printed, in the order it printed them (at most <see cref="PreviewedIdLimit"/>).</summary>
    private static List<string> PreviewedIds(CliInvocation invocation)
    {
        var ids = new List<string>();
        foreach (var line in invocation.OutputLines)
        {
            if (line.Stream == CliStream.StandardOutput && PreviewedIdPattern.Match(line.Text) is { Success: true } match)
            {
                ids.Add(match.Groups["id"].Value);
            }
        }

        return ids;
    }

    /// <summary>
    /// A sentence for the result banner when the mutation did not act on the number of alerts the preview showed, else
    /// nothing. The command prints its own <c>Preview: N alert(s) matched</c> just before it applies and
    /// <c>Acknowledged N alert(s).</c> after, so what it matched and what it applied are both known; neither is what
    /// the operator confirmed unless it equals the preview.
    /// </summary>
    private static string DifferenceNote(string output, int previewed)
    {
        int? applied = null;
        if (AppliedPattern.Match(output) is { Success: true } a
            && int.TryParse(a.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appliedCount))
        {
            applied = appliedCount;
        }

        int? matchedNow = null;
        if (PreviewMatchedPattern.Match(output) is { Success: true } m
            && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var matchedCount))
        {
            matchedNow = matchedCount;
        }

        var actual = applied ?? matchedNow;
        if (actual is null || (actual == previewed && (matchedNow is null || matchedNow == previewed)))
        {
            return string.Empty;
        }

        var shown = previewed.ToString("N0", CultureInfo.CurrentCulture);
        var ran = actual.Value.ToString("N0", CultureInfo.CurrentCulture);
        return $" The preview showed {shown} alert(s) but {ran} were {(applied is null ? "matched" : "applied")}: the list changed between the preview and the apply.";
    }

    /// <summary>One stream of an invocation's transcript as text, capped so a chatty run cannot flood a dialog.</summary>
    private static string StreamText(CliInvocation invocation, CliStream stream, int maxLines)
    {
        var lines = invocation.OutputLines
            .Where(l => l.Stream == stream && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => l.Text.TrimEnd())
            .ToList();

        return lines.Count <= maxLines
            ? string.Join('\n', lines)
            : string.Join('\n', lines.Take(maxLines)) + $"\n… and {lines.Count - maxLines} more line(s) - see Activity";
    }

    /// <summary>The CLI's own error, in its own words: the last few stderr lines (click prints <c>Error: ...</c>).</summary>
    private static string ErrorTail(CliInvocation invocation, string prefix)
    {
        var tail = invocation.OutputLines
            .Where(l => l.Stream == CliStream.StandardError && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => l.Text.Trim())
            .TakeLast(3)
            .ToList();

        if (tail.Count == 0)
        {
            tail = invocation.OutputLines
                .Where(l => l.Stream == CliStream.StandardOutput && !string.IsNullOrWhiteSpace(l.Text))
                .Select(l => l.Text.Trim())
                .TakeLast(2)
                .ToList();
        }

        var code = invocation.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return tail.Count == 0
            ? $"{prefix} (exit {code}) and printed nothing. See Activity."
            : $"{prefix} (exit {code}): {string.Join(" ", tail)}";
    }

    /// <summary>
    /// Reads which of the loaded alerts audit.db already records as acknowledged or dismissed
    /// (<c>alert_acknowledgement_projection</c>, opened read-only) and folds them into the hidden
    /// set. Best-effort: an unreadable database or a build without the table leaves the list exactly
    /// as the gateway served it.
    /// </summary>
    private async Task RefreshAcknowledgedAsync()
    {
        if (!Services.Audit.Exists || _all.Count == 0)
        {
            return;
        }

        var ids = _all.Select(a => a.Key).Distinct(StringComparer.Ordinal).Take(MaxAckLookup).ToArray();
        var path = Services.Paths.AuditDatabasePath;

        await _ackGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var found = await Task.Run(() => ReadAcknowledgedIds(path, ids)).ConfigureAwait(true);

            var changed = false;
            foreach (var id in found)
            {
                changed |= _acknowledgedKeys.Add(id);
            }

            if (changed)
            {
                ApplyFilters();
            }
        }
#pragma warning disable CA1031 // The projection is a nicety; a locked DB or an older schema must not fault the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            // Leave the list as the gateway served it.
        }
#pragma warning restore CA1031
        finally
        {
            _ = _ackGate.Release();
        }
    }

    private static List<string> ReadAcknowledgedIds(string databasePath, IReadOnlyList<string> ids)
    {
        var found = new List<string>();
        if (ids.Count == 0)
        {
            return found;
        }

        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(databasePath));
        connection.Open();

        using var command = connection.CreateCommand();

        // Only the parameter names are concatenated; every id goes in as a bound value.
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "@p" + i.ToString(CultureInfo.InvariantCulture);
            _ = command.Parameters.AddWithValue(names[i], ids[i]);
        }

        command.CommandText =
            "SELECT alert_id FROM alert_acknowledgement_projection WHERE alert_id IN (" + string.Join(',', names) + ")";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                found.Add(reader.GetString(0));
            }
        }

        return found;
    }

    /// <summary>
    /// Every poll, not just StateChanged: the fallback's 30 s reload cadence and the
    /// "refreshed …" note both ride the poll, and StateChanged is silent on an idle box.
    /// <c>Apply</c> decides what, if anything, actually needs rebuilding.
    /// </summary>
    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// Brings the master list in line with one snapshot. <c>NotConnected</c> and
    /// <c>Unauthorized</c> are informational: the panel says why the stream is quiet and
    /// falls back to the audit database rather than showing an error.
    /// </summary>
    internal void Apply(GatewaySnapshot snapshot)
    {
        // The audit queue is the source: the gateway's answer says nothing about it (and the queue does not need the gateway).
        if (_queueActive)
        {
            return;
        }

        if (snapshot.AlertsUnavailable is { Length: > 0 } reason)
        {
            _alertsUnavailableReason = reason;

            // Rebuilt on every poll, so the audit suffix has to be part of the rebuild: the
            // fallback only reloads every 30 s, and a note that dropped its suffix for the
            // five polls in between claimed the list below had no source.
            SourceNote = WithFallbackNote(reason);

            // The master list no longer mirrors /alerts, so the next answer must re-project.
            _appliedAlerts = null;

            if (Services.Audit.Exists && DefenseClaw.Core.Time.WallClock.Elapsed(_lastFallbackLoad) > TimeSpan.FromSeconds(30))
            {
                _ = LoadFallbackAsync();
            }
            else if (_all.Count == 0)
            {
                SetEmpty("No alerts to show", reason);
                ApplyFilters();
            }

            return;
        }

        _alertsUnavailableReason = null;

        // The Initial snapshot: no poll has completed, so there is no answer to describe (its
        // AlertsUnavailable is null, which would otherwise read as "the gateway reported none").
        if (snapshot.PolledAt == DateTimeOffset.MinValue)
        {
            SourceNote = "Waiting for the first alert poll…";
            SetEmpty("No alerts yet", "The first alert poll has not completed.");
            return;
        }

        // AlertsFetchedAt, not PolledAt: the health poll runs every few seconds but /alerts is
        // re-read on its own slower cadence, so PolledAt would say "just now" about a list
        // that is up to 30 s old. Null means /alerts has never answered.
        var fetched = snapshot.AlertsFetchedAt is { } at ? Relative(at) : "never";
        SourceNote = $"GET /alerts · newest {GatewayMonitor.AlertLimit} · fetched {fetched}";
        SetEmpty(
            "No alerts in the last poll",
            "The gateway is answering and reported no findings in the most recent window.");

        // The same list instance means the same findings (see GatewayMonitor), and the
        // filters re-run themselves when they change - nothing else can alter the result.
        if (ReferenceEquals(_appliedAlerts, snapshot.RecentAlerts))
        {
            return;
        }

        ProjectGatewayAlerts(snapshot.RecentAlerts);
        ApplyFilters();

        // One lookup per new list (this branch is skipped for the same list instance above), off
        // the UI thread; it only ever removes rows the gateway should not have served.
        _ = RefreshAcknowledgedAsync();
    }

    /// <summary>
    /// Rebuilds the master list from a /alerts answer, reusing the row for any alert already
    /// projected: an alert id is an immutable audit row, and the row is where the cost is
    /// (the attribute bag is sorted, flattened and pretty-printed twice). Only alerts new
    /// since the last projection pay for it. An alert without an id gets a fresh identity per
    /// projection and is never reused.
    /// </summary>
    private void ProjectGatewayAlerts(IReadOnlyList<GatewayAlert> alerts)
    {
        Dictionary<string, AlertItem>? reusable = null;
        if (_allFromAudit)
        {
            // Different source, different row shape; the bound list starts over too.
            if (Alerts.Count > 0)
            {
                Alerts.Clear();
            }
        }
        else if (_all.Count > 0)
        {
            reusable = new Dictionary<string, AlertItem>(_all.Count, StringComparer.Ordinal);
            foreach (var item in _all)
            {
                _ = reusable.TryAdd(item.Key, item);
            }
        }

        var projected = new List<AlertItem>(alerts.Count);
        foreach (var alert in alerts)
        {
            if (alert.Id.Length > 0 && reusable is not null && reusable.Remove(alert.Id, out var existing))
            {
                projected.Add(existing);
            }
            else
            {
                projected.Add(AlertItem.FromGateway(alert));
            }
        }

        _all.Clear();
        _all.AddRange(projected);
        _allFromAudit = false;
        _appliedAlerts = alerts;
    }

    /// <summary>
    /// Fallback source. <c>security.finding</c> rows carry the same dotted attribute keys
    /// as the REST payload, so the same item shape renders either way.
    /// </summary>
    private async Task LoadFallbackAsync()
    {
        if (Interlocked.Exchange(ref _loadingFallback, 1) == 1)
        {
            return;
        }

        try
        {
            var query = new AuditQuery
            {
                Buckets = new[] { "security.finding" },
                Limit = FallbackLimit,
            };

            var page = await Services.Audit.QueryAsync(query, CancellationToken.None);
            _lastFallbackLoad = DateTimeOffset.UtcNow;

            // The queue became the source while this ran: its rows are the list now, and these are not what it holds.
            if (_queueActive)
            {
                return;
            }

            if (!_allFromAudit && Alerts.Count > 0)
            {
                // Different source, different row shape; see _allFromAudit.
                Alerts.Clear();
            }

            _all.Clear();
            foreach (var row in page.Events)
            {
                _all.Add(AlertItem.FromAudit(row));
            }

            _allFromAudit = true;
            _appliedAlerts = null;

            // Rebuilt from the reason rather than appended to whatever the note is now: a poll
            // may have replaced it while the query ran, and the gateway may even have come
            // back (then the next poll re-projects /alerts and the note is not this one's).
            if (_alertsUnavailableReason is { } reason)
            {
                SourceNote = WithFallbackNote(reason);
            }

            SetEmpty(
                "No findings recorded",
                "The gateway is not serving alerts and audit.db holds no security.finding rows yet.");
            ApplyFilters();
            _ = RefreshAcknowledgedAsync();
        }
#pragma warning disable CA1031 // The fallback is a nicety; a locked DB must not fault the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            SetEmpty("Alerts unavailable", ex.Message);
            ApplyFilters();
        }
#pragma warning restore CA1031
        finally
        {
            Interlocked.Exchange(ref _loadingFallback, 0);
        }
    }

    // ---- The audit queue (the primary source) ------------------------------------------------------------

    /// <summary>
    /// Reads the unacknowledged queue and, when it is usable, makes it the list. When it is not (a database from before the
    /// queue's schema, no database) the gateway's list stays or comes back; a read that fails while the queue is already the
    /// source keeps the rows and says so in the note. Never throws: it runs fire-and-forget from events and a timer. Reads are
    /// taken one at a time, and each caller returns after a read that started after its call (so after an acknowledge, the list
    /// it gets is the one without the acknowledged rows).
    /// </summary>
    private async Task RefreshQueueAsync()
    {
        await _queueGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ReadQueueAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A queue that cannot be read this time (locked, timed out) must not fault the panel; the next tick reads again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"alerts: the unacknowledged queue could not be read: {ex.GetType().Name}: {ex.Message}");
            if (_queueActive)
            {
                SourceNote = QueueNote() + $" · the last refresh failed: {ex.Message}".ReplaceLineEndings(" ");
            }
        }
        finally
        {
            _ = _queueGate.Release();
        }
    }

    private async Task ReadQueueAsync()
    {
        var result = await Services.AlertQueue.ReadAsync(AlertQueueReader.DefaultWindowLimit).ConfigureAwait(true);
        if (result.Status != AlertQueueStatus.Ok)
        {
            LeaveQueue();
            return;
        }

        var window = result.Counts.Newest;

        // Rows already shown are reused; only the findings this panel has not shown yet are read in full.
        var shown = new Dictionary<string, AlertItem>(StringComparer.Ordinal);
        if (_queueActive)
        {
            foreach (var item in _all)
            {
                _ = shown.TryAdd(item.Key, item);
            }
        }

        var missing = window
            .Where(item => item.Id.Length > 0 && !shown.ContainsKey(item.Id))
            .Select(item => item.Id)
            .ToArray();
        var details = missing.Length == 0
            ? Array.Empty<AuditEvent>()
            : await Services.Audit.GetByIdsAsync(missing).ConfigureAwait(true);

        // A row flattens and pretty-prints its attribute bag, and a first load builds hundreds: not on the UI thread.
        var rows = await Task.Run(() => BuildQueueRows(window, shown, details)).ConfigureAwait(true);

        // The gateway's egress decisions that deserve a row (blocked, or LLM-shaped) join the findings, newest first.
        var egress = await ReadEgressRowsAsync(shown).ConfigureAwait(true);
        if (egress.Count > 0)
        {
            rows = rows.Concat(egress).OrderByDescending(row => row.Timestamp).ToList();
        }

        if (!_queueActive)
        {
            // The gateway's rows (or none) give way to the queue's, which have a different shape: the bound list starts over.
            _queueActive = true;
            if (Alerts.Count > 0)
            {
                Alerts.Clear();
            }
        }

        _all.Clear();
        _all.AddRange(rows);
        _allFromAudit = true;
        _appliedAlerts = null;
        _alertsUnavailableReason = null;
        _queueReadAt = DateTimeOffset.UtcNow;
        _queueHasMore = result.Counts.HasMore;

        SourceNote = QueueNote();
        SetEmpty("Nothing to acknowledge", "No unacknowledged findings are waiting in the audit database.");
        ApplyFilters();
    }

    /// <summary>The queue, newest first, as rows: a row already shown as it is, a new one from its full audit row, one whose row has gone (retention) from what the queue knows.</summary>
    private static List<AlertItem> BuildQueueRows(
        IReadOnlyList<AlertQueueItem> window,
        Dictionary<string, AlertItem> shown,
        IReadOnlyList<AuditEvent> details)
    {
        Dictionary<string, AuditEvent>? byId = null;
        if (details.Count > 0)
        {
            byId = new Dictionary<string, AuditEvent>(details.Count, StringComparer.Ordinal);
            foreach (var detail in details)
            {
                _ = byId.TryAdd(detail.Id, detail);
            }
        }

        var rows = new List<AlertItem>(window.Count);
        foreach (var item in window)
        {
            if (item.Id.Length > 0 && shown.TryGetValue(item.Id, out var existing))
            {
                rows.Add(existing);
            }
            else if (item.Id.Length > 0 && byId is not null && byId.TryGetValue(item.Id, out var detail))
            {
                rows.Add(AlertItem.FromAudit(detail));
            }
            else
            {
                rows.Add(AlertItem.FromQueue(item));
            }
        }

        return rows;
    }

    /// <summary>
    /// The queue cannot serve (no database, or one from before the queue's schema): back to the gateway's list, which is read
    /// from the monitor's current snapshot at once. Does nothing while the gateway's list is already what is showing.
    /// </summary>
    private void LeaveQueue()
    {
        if (!_queueActive)
        {
            return;
        }

        _queueActive = false;
        _all.Clear();
        _allFromAudit = false;
        _appliedAlerts = null;
        Alerts.Clear();
        ApplyFilters();
        Apply(Services.Monitor.Current);
    }

    /// <summary>"Unacknowledged findings · 441 · read 12s ago", or, when the queue's window was full, that the newest 500 are shown and more are waiting.</summary>
    private string QueueNote()
    {
        var egress = _all.Count(item => string.Equals(item.Kind, AlertKinds.Egress, StringComparison.Ordinal));
        var count = (_all.Count - egress).ToString("N0", CultureInfo.CurrentCulture);
        var what = _queueHasMore ? $"newest {count} (more are waiting)" : count;
        var egressNote = egress > 0
            ? $" · {egress.ToString("N0", CultureInfo.CurrentCulture)} egress"
            : _egressProblem.Length > 0 ? $" · {_egressProblem}" : string.Empty;
        return $"Unacknowledged findings · {what}{egressNote} · read {Relative(_queueReadAt)}";
    }

    /// <summary>
    /// The source note plus, while <see cref="_all"/> holds audit.db rows, the statement that
    /// the list below came from there. The one place the suffix is built, so every poll and
    /// the fallback load agree on it.
    /// </summary>
    private string WithFallbackNote(string reason) =>
        _allFromAudit ? $"{reason} · showing {_all.Count} finding(s) from audit.db instead" : reason;

    /// <summary>Records what the source says about an empty list, then shows the right text.</summary>
    private void SetEmpty(string title, string detail)
    {
        _sourceEmptyTitle = title;
        _sourceEmptyDetail = detail;
        ShowEmptyText();
    }

    /// <summary>
    /// "No alerts" and "alerts exist but the filters hide them" are different states with
    /// different remedies; the source's own message ("the gateway reported no findings")
    /// would be false in the second. Runs after every filter pass and every
    /// <see cref="SetEmpty"/>, so neither ordering leaves the wrong text up.
    /// </summary>
    private void ShowEmptyText()
    {
        if (_poolCount > 0 && Alerts.Count == 0)
        {
            EmptyTitle = "No alerts match the current filters";
            EmptyDetail =
                $"{_poolCount} alert(s) are loaded but hidden by the severity, kind or text filter. " +
                "Use Clear to show them again.";
            return;
        }

        if (_all.Count > 0 && _poolCount == 0)
        {
            // Everything loaded is acknowledged or dismissed: a good outcome, not an error.
            EmptyTitle = "Every loaded alert is acknowledged";
            EmptyDetail =
                $"{_all.Count} loaded alert(s) are recorded as acknowledged or dismissed in audit.db and are hidden. " +
                "New findings appear here as they arrive.";
            return;
        }

        EmptyTitle = _sourceEmptyTitle;
        EmptyDetail = _sourceEmptyDetail;
    }

    /// <summary>
    /// Severity toggles, then substring match, then optional collapse. Collapsing keys on
    /// signature+action, which is exactly the axis the agent-generated noise repeats on.
    /// <para>
    /// The toggles compare <see cref="AlertItem.SeverityKey"/>, not the stored spelling: the
    /// toggles only spell five severities, and <c>WARN</c> / <c>WARNING</c> / <c>MODERATE</c> /
    /// <c>FATAL</c> rows (which the chip colours as Medium / Critical) would otherwise match
    /// none of them and vanish the moment any toggle was switched off, with no toggle able to
    /// bring them back. Anything the key does not recognise is Info, so the five keys
    /// partition every alert.
    /// </para>
    /// </summary>
    private void ApplyFilters()
    {
        var anyToggleOff = SeverityFilters.Any(f => !f.IsEnabled);
        var allowed = SeverityFilters
            .Where(f => f.IsEnabled)
            .Select(f => f.SeverityKey)
            .ToHashSet(StringComparer.Ordinal);

        var needle = FilterText.Trim();
        var selectedKey = SelectedAlert?.Key;
        RefreshTileState();

        // Acknowledged / dismissed alerts (per audit.db's projection) are not part of the pool the
        // toggles and the text filter act on, whatever the source still serves. Counted, not silent.
        var pool = _acknowledgedKeys.Count == 0
            ? _all
            : _all.Where(item => !_acknowledgedKeys.Contains(item.Key)).ToList();
        _poolCount = pool.Count;
        HiddenAcknowledgedCount = _all.Count - pool.Count;
        UpdateSeverityCounts(pool);

        IEnumerable<AlertItem> query = pool;

        if (anyToggleOff)
        {
            query = query.Where(item => allowed.Contains(item.SeverityKey));
        }

        if (!string.Equals(KindFilter, AlertKinds.All, StringComparison.Ordinal))
        {
            query = query.Where(KindAllows);
        }

        if (needle.Length > 0)
        {
            query = query.Where(item => item.Matches(needle));
        }

        var filtered = query.ToList();
        var rows = CollapseRepeats ? Collapse(filtered) : filtered;

        // Merged by alert key, not cleared and refilled: the ListView keeps the containers,
        // the scroll offset and the selected row for every alert that is still listed. Within
        // one source a key names one immutable finding, so a matched row is always current
        // - the only field that varies under a key is a collapsed group's repeat count, and
        // that is copied across in place.
        SyncCollection(
            Alerts,
            rows,
            static alert => alert.Key,
            static (_, _) => true,
            static (existing, wanted) => existing.RepeatCount = wanted.RepeatCount);

        IsEmpty = Alerts.Count == 0;
        ShowEmptyText();

        var summary = _all.Count == 0
            ? string.Empty
            : CollapseRepeats
                ? $"{Alerts.Count} group(s) · {filtered.Count} of {pool.Count} alerts"
                : $"{Alerts.Count} of {pool.Count} alerts";
        CountSummary = HiddenAcknowledgedCount > 0
            ? $"{summary} · {HiddenAcknowledgedCount} acknowledged hidden"
            : summary;

        SelectedAlert = selectedKey is null
            ? null
            : Alerts.FirstOrDefault(a => string.Equals(a.Key, selectedKey, StringComparison.Ordinal));
    }

    /// <summary>Writes each severity chip's count from the un-filtered pool (a chip that is off still says how many it hides).</summary>
    private void UpdateSeverityCounts(IReadOnlyList<AlertItem> pool)
    {
        foreach (var filter in SeverityFilters)
        {
            filter.Count = pool.Count(item => string.Equals(item.SeverityKey, filter.SeverityKey, StringComparison.Ordinal));
        }

        OnPropertyChanged(nameof(ShowInfoFilter));
    }

    private static List<AlertItem> Collapse(IReadOnlyList<AlertItem> items)
    {
        var groups = new List<AlertItem>();
        var index = new Dictionary<string, AlertItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (index.TryGetValue(item.GroupKey, out var existing))
            {
                existing.RepeatCount++;
                continue;
            }

            // The newest occurrence represents the group, and counts itself.
            var clone = item.CloneForGroup();
            index[item.GroupKey] = clone;
            groups.Add(clone);
        }

        return groups;
    }

    /// <summary>
    /// Re-stamps every row, not just the listed ones: rows are now reused across polls, so a
    /// row a filter is hiding would otherwise carry its old text back into view. (Collapsed
    /// group rows are not in <see cref="_all"/>, hence both passes.)
    /// </summary>
    private void RestampTimes()
    {
        if (_queueActive)
        {
            SourceNote = QueueNote();
        }

        foreach (var item in _all)
        {
            item.Restamp();
        }

        foreach (var item in Alerts)
        {
            item.Restamp();
        }
    }

    internal static string Relative(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
        {
            return "never";
        }

        var delta = DateTimeOffset.UtcNow - value;
        return delta switch
        {
            { TotalSeconds: < 5 } => "just now",
            { TotalSeconds: < 60 } => $"{(int)delta.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            _ => $"{(int)delta.TotalDays}d ago",
        };
    }
}

/// <summary>
/// One severity chip above the list: an on/off filter that carries the tone of its severity and
/// says how many loaded alerts it stands for. Its own state change is only <see cref="IsEnabled"/>
/// (<see cref="Count"/> is rewritten by the panel on every pass and must not re-trigger a pass).
/// </summary>
public sealed partial class SeverityFilter : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(TileCaption))]
    private bool _isEnabled = true;

    /// <summary>How many loaded, un-acknowledged alerts have this severity (regardless of the toggle).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private int _count;

    public SeverityFilter(string severity)
    {
        Severity = severity;
    }

    /// <summary>Stored spelling, e.g. <c>HIGH</c>.</summary>
    public string Severity { get; }

    public string SeverityKey => AlertItem.KeyFor(Severity);

    /// <summary>"HIGH: 25 alerts, shown" - the chip's state in words, for screen readers and tooltips.</summary>
    public string AutomationName =>
        $"{Severity} severity filter: {Count.ToString(CultureInfo.CurrentCulture)} alert{(Count == 1 ? string.Empty : "s")}, " +
        (IsEnabled ? "shown" : "hidden");

    public override string ToString() => AutomationName;
}

/// <summary>
/// One <c>--severity</c> choice in the acknowledge/dismiss review: the value the CLI takes and the
/// words the dialog shows for it.
/// </summary>
public sealed record SeverityChoice(string Value, string Label)
{
    /// <summary>True for <c>all</c>, the one choice that reaches every active alert.</summary>
    public bool IsAll => string.Equals(Value, "all", StringComparison.Ordinal);

    public override string ToString() => Label;
}

/// <summary>One key/value row in the detail pane.</summary>
public sealed record AlertField(string Name, string Value)
{
    /// <summary>"name: value" - what a screen reader says instead of the record's member dump.</summary>
    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>
/// One alert row. Built from either <c>/alerts</c> or a <c>security.finding</c> audit row —
/// both carry the same dotted attribute names, so the view never has to know which.
/// </summary>
public sealed partial class AlertItem : ObservableObject
{
    [ObservableProperty]
    private string _relativeTime = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeated))]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    private int _repeatCount = 1;

    private AlertItem(string key, DateTimeOffset timestamp)
    {
        Key = key;
        Timestamp = timestamp;
        RelativeTime = AlertsPanelViewModel.Relative(timestamp);
    }

    /// <summary>Stable identity, used to keep the selection across refreshes.</summary>
    public string Key { get; }

    public DateTimeOffset Timestamp { get; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>The table's Time cell: the clock time for today's alerts, the date and minute for older ones (the full stamp is in <see cref="TimestampText"/>).</summary>
    public string TableTimeText
    {
        get
        {
            var local = Timestamp.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        }
    }

    public string Severity { get; private init; } = "INFO";

    /// <summary>Critical / High / Medium / Low / Info — the view's colour key.</summary>
    public string SeverityKey => KeyFor(Severity);

    /// <summary>Critical 4 ... Info 0: what the Severity column sorts by (the words sort alphabetically, which is no order).</summary>
    public int SeverityRank => SeverityKey switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0,
    };

    public string RuleId { get; private init; } = string.Empty;

    /// <summary>The run (agent session) the alert came from, as far as the source says; empty when it does not.</summary>
    public string RunId { get; private init; } = string.Empty;

    /// <summary>One line for the clipboard: "Sep 30 11:35:57 [HIGH] scan-finding target - details".</summary>
    public string CopyLine
    {
        get
        {
            var line = $"{TimestampText} [{Severity}] {Action} {TargetRef}".TrimEnd();
            return Headline.Length > 0 ? $"{line} - {Headline}" : line;
        }
    }

    public string Headline { get; private init; } = string.Empty;

    public string Action { get; private init; } = string.Empty;

    public string TargetRef { get; private init; } = string.Empty;

    public string Evidence { get; private init; } = string.Empty;

    public string Scanner { get; private init; } = string.Empty;

    public string Source { get; private init; } = string.Empty;

    public string Tags { get; private init; } = string.Empty;

    public string ConfidenceText { get; private init; } = string.Empty;

    /// <summary>Full attribute bag, pretty-printed for the detail pane.</summary>
    public string StructuredText { get; private init; } = string.Empty;

    public IReadOnlyList<AlertField> Fields { get; private init; } = Array.Empty<AlertField>();

    /// <summary>Collapse axis: the same signature firing on the same action.</summary>
    public string GroupKey => $"{RuleId}|{Action}";

    public bool HasEvidence => Evidence.Length > 0;

    public bool IsRepeated => RepeatCount > 1;

    public string RepeatText => "x" + RepeatCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>
    /// What a screen reader announces for the row (UI Automation falls back to
    /// <c>ToString()</c> for an item with no explicit name): severity, rule, title, target, how
    /// often it repeated and when it happened.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string> { $"{Severity} alert", RuleId, Headline, TargetRef };
        if (IsRepeated)
        {
            parts.Add($"repeated {RepeatCount.ToString(CultureInfo.CurrentCulture)} times");
        }

        parts.Add(RelativeTime);
        return string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public static AlertItem FromGateway(GatewayAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var structured = alert.Structured is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(alert.Structured, StringComparer.Ordinal);

        return new AlertItem(alert.Id.Length > 0 ? alert.Id : Guid.NewGuid().ToString("n"), alert.Timestamp)
        {
            Severity = Normalize(alert.Severity),
            Kind = KindOf(alert.Action ?? string.Empty, alert.Scanner ?? string.Empty),
            RawTarget = alert.Target ?? string.Empty,
            RuleId = alert.RuleId ?? string.Empty,
            RunId = alert.RunId ?? string.Empty,
            Headline = alert.Title ?? alert.Details ?? alert.Action ?? "(finding)",
            Action = alert.Action ?? string.Empty,
            TargetRef = alert.TargetRef ?? alert.Target ?? string.Empty,
            Evidence = alert.EvidenceSummary ?? string.Empty,
            Scanner = alert.Scanner ?? string.Empty,
            Source = alert.Actor ?? string.Empty,
            Tags = string.Join(", ", alert.Tags.Concat(alert.DataAxes)),
            ConfidenceText = alert.Confidence is { } confidence
                ? confidence.ToString("P0", CultureInfo.CurrentCulture)
                : string.Empty,
            StructuredText = Pretty(structured),
            Fields = ToFields(structured),
        };
    }

    public static AlertItem FromAudit(AuditEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var structured = row.StructuredJson;

        return new AlertItem(row.Id, row.Timestamp)
        {
            Severity = Normalize(row.Severity),
            Kind = KindOf(row.Action, row.StructuredString(GatewayAlert.Keys.Scanner) ?? string.Empty),
            RawTarget = row.Target ?? string.Empty,
            RuleId = row.StructuredString(GatewayAlert.Keys.RuleId) ?? string.Empty,
            RunId = row.RunId ?? string.Empty,
            Headline = row.StructuredString(GatewayAlert.Keys.Title) ?? row.Details ?? row.Action,
            Action = row.Action,
            TargetRef = row.StructuredString(GatewayAlert.Keys.TargetRef) ?? row.Target ?? string.Empty,
            Evidence = row.StructuredString(GatewayAlert.Keys.EvidenceSummary) ?? string.Empty,
            Scanner = row.StructuredString(GatewayAlert.Keys.Scanner) ?? string.Empty,
            Source = row.Actor ?? row.Source ?? string.Empty,
            Tags = string.Empty,
            ConfidenceText = string.Empty,
            StructuredText = Pretty(structured),
            Fields = ToFields(structured),
        };
    }

    /// <summary>
    /// A row from what the alert queue itself knows (id, severity, action, target, connector), for a finding whose full audit row
    /// could not be read (it was removed by retention between the two reads). The details it cannot show are empty, not invented.
    /// </summary>
    public static AlertItem FromQueue(AlertQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new AlertItem(item.Id.Length > 0 ? item.Id : Guid.NewGuid().ToString("n"), item.Timestamp)
        {
            Severity = item.Severity.ToStoredValue(),
            Kind = KindOf(item.Action, string.Empty),
            RawTarget = item.Target ?? string.Empty,
            Headline = item.Action.Length > 0 ? item.Action : "(finding)",
            Action = item.Action,
            TargetRef = item.Target ?? string.Empty,
            Source = item.Connector ?? string.Empty,
            StructuredText = "{}",
        };
    }

    /// <summary>Copy used as a collapsed group's representative row.</summary>
    public AlertItem CloneForGroup() => new(Key, Timestamp)
    {
        Severity = Severity,
        Kind = Kind,
        RawTarget = RawTarget,
        RuleId = RuleId,
        RunId = RunId,
        Headline = Headline,
        Action = Action,
        TargetRef = TargetRef,
        Evidence = Evidence,
        Scanner = Scanner,
        Source = Source,
        Tags = Tags,
        ConfidenceText = ConfidenceText,
        StructuredText = StructuredText,
        Fields = Fields,
    };

    public static string KeyFor(string? severity) => severity?.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" or "FATAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" or "MODERATE" or "WARN" or "WARNING" => "Medium",
        "LOW" => "Low",
        _ => "Info",
    };

    public bool Matches(string needle) =>
        Contains(RuleId, needle) ||
        Contains(Kind, needle) ||
        Contains(Headline, needle) ||
        Contains(Action, needle) ||
        Contains(TargetRef, needle) ||
        Contains(Evidence, needle) ||
        Contains(Scanner, needle) ||
        Contains(Tags, needle);

    public void Restamp() => RelativeTime = AlertsPanelViewModel.Relative(Timestamp);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? severity) =>
        string.IsNullOrWhiteSpace(severity) ? "INFO" : severity.Trim().ToUpperInvariant();

    private static IReadOnlyList<AlertField> ToFields(IReadOnlyDictionary<string, JsonElement> structured)
    {
        var fields = new List<AlertField>(structured.Count);
        foreach (var (name, value) in structured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            fields.Add(new AlertField(Shorten(name), Flatten(value)));
        }

        return fields;
    }

    /// <summary>Drops the <c>defenseclaw.</c> prefix every attribute name carries.</summary>
    private static string Shorten(string name) =>
        name.StartsWith("defenseclaw.", StringComparison.Ordinal) ? name["defenseclaw.".Length..] : name;

    private static string Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Flatten)),
        _ => value.ToString(),
    };

    private static string Pretty(IReadOnlyDictionary<string, JsonElement> structured)
    {
        if (structured.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder("{\n");
        foreach (var (name, value) in structured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append("  \"").Append(name).Append("\": ").Append(value.ToString()).Append(",\n");
        }

        builder.Length -= 2;
        return builder.Append("\n}").ToString();
    }
}
