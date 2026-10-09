using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Audit panel stays current while it is on screen (CUST-262): the 0.8.10 TUI's Audit panel refreshes live, and a new block event now appears here
/// within one poll interval without the operator pressing Refresh, the scroll position moving, or the selection being lost.
/// <para>
/// <b>Idle costs one trivial statement.</b> Every <see cref="LivePollInterval"/> the panel asks <see cref="AppServices.AuditChanges"/> for a stamp
/// (<c>PRAGMA data_version</c> on the probe's kept read-only connection, tens of microseconds) and compares it with the one taken before the list was last
/// read. Nothing moved: that was the whole poll - no page, no count, no row decoded - and only the "Updated hh:mm:ss" caption changes. Something did:
/// the recent rows are read, below.
/// </para>
/// <para>
/// <b>A change reads the recent rows, not the list again.</b> The query is the list's own (the same bucket, connector, severity, search and preset) narrowed
/// to the last <see cref="LateCommitWindow"/>, not to "rows newer than the newest I have", because the gateway stamps a row when it is made and can commit
/// it later: on a real database 5% of the rows are committed behind a newer one, up to two minutes behind, and most of them are scan findings - the rows an
/// operator wants most. So a text search or a preset, whose terms live in <c>details</c> and are an unindexed scan of their window, scans minutes of
/// rows here, not a day of them. A row the list does not know is inserted where the query's own order puts it (newest first, so nearly always at the top);
/// the rows already listed stay the very objects they were, so the selection, the open inspector and (the view keeps the scroll offset by the height
/// inserted above it) the scroll position are untouched. When more arrived than <see cref="LiveWindowLimit"/> rows hold, the list is read again from the
/// newest page, as Refresh does.
/// </para>
/// <para>
/// <b>Only the loaded part of the window.</b> A row older than the last one a load read belongs to "Load more", which will read it when asked; the live refresh
/// leaves it alone, so a row is never listed twice. And a low-signal row the actionable view leaves out is remembered by name until it leaves the window, so
/// the count of them is each row once, not once a poll.
/// </para>
/// <para>
/// <b>Only while it is on screen, and never over the operator's own work.</b> The timer runs between <see cref="PanelViewModelBase.OnActivated"/> and
/// <see cref="PanelViewModelBase.OnDeactivated"/>; on coming back one catch-up poll runs at once. A tick that finds a load running (a filter changed, Load
/// more) does nothing, and a load that starts while a poll is reading cancels it (the poll belongs to the generation whose list it extends).
/// The archive is a file that does not change and is never polled.
/// </para>
/// <para>
/// <b>A failure keeps what is on screen.</b> A locked or unreadable database leaves the rows as they were and says so beside the caption - "Live refresh
/// paused" - and the next attempts back off (5, 10, 20, 40, then every 60 seconds); the first poll that works clears the note.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel
{
    /// <summary>How often the panel looks for a change while it is on screen. The look is one probe sample.</summary>
    public static readonly TimeSpan DefaultLivePollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far back a poll reads. A row carries the time the gateway made it, which can be well before the time it was committed (a scan stamps its
    /// findings when it began: up to 112 s on a real database); one that arrives later than this is not added by the live refresh, and Refresh reads it.
    /// </summary>
    public static readonly TimeSpan LateCommitWindow = TimeSpan.FromMinutes(5);

    /// <summary>The most rows one poll reads. More than this in <see cref="LateCommitWindow"/> is a storm; the list is read again from the newest page.</summary>
    public const int LiveWindowLimit = 2000;

    private static readonly TimeSpan MaxLiveBackoff = TimeSpan.FromSeconds(60);

    private DispatcherTimer? _liveTimer;
    private CancellationTokenSource? _liveCts;

    /// <summary>The probe's stamp taken before the list was last read or extended; a poll that samples the same one has nothing to do.</summary>
    private AuditStamp _liveStamp;

    /// <summary>When that read started: the live query reads from <see cref="LateCommitWindow"/> before it.</summary>
    private DateTimeOffset _liveHorizon;

    /// <summary>
    /// The last row the loads have read, or null when they reached the end of the window: rows older than this are for "Load more", not for a poll. A
    /// cursor, so a row at the same instant is told apart by its id.
    /// </summary>
    private AuditCursor? _liveFloor;

    /// <summary>The low-signal rows the actionable view has counted and left out, by (instant, id): a poll that reads one again must not count it again. Pruned to the window.</summary>
    private readonly HashSet<(long Nanos, string Id)> _liveHidden = new();

    private bool _liveBusy;
    private int _liveFailures;

    /// <summary>The <see cref="TimeProvider.GetTimestamp"/> before which a poll does nothing (a failure's back-off).</summary>
    private long _liveNotBefore;

    /// <summary>"Updated 14:03:22": when the list was last known to be current. Empty until something has been read, and while the archive is on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveStatus))]
    private string _liveStatusText = string.Empty;

    /// <summary>Why the live refresh is paused, or empty. The rows stay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveNote))]
    private string _liveNote = string.Empty;

    public bool HasLiveStatus => LiveStatusText.Length > 0;

    public bool HasLiveNote => LiveNote.Length > 0;

    private TimeSpan _livePollInterval = DefaultLivePollInterval;

    /// <summary>The interval between looks (and the first back-off); a test shortens it so a timer-driven refresh can be watched.</summary>
    internal TimeSpan LivePollInterval
    {
        get => _livePollInterval;
        set
        {
            _livePollInterval = value;
            if (_liveTimer is not null)
            {
                _liveTimer.Interval = value;
            }
        }
    }

    /// <summary>False in a test that drives <see cref="PollLiveAsync"/> itself and must not have a timer tick behind its back.</summary>
    internal bool LiveTimerEnabled { get; set; } = true;

    /// <summary>The most recent poll (a finished one once it has settled); tests await it.</summary>
    internal Task LastLivePoll { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Raised on the UI thread when a live refresh has finished putting new rows into <see cref="Rows"/> - after the last insert, once. The view uses it
    /// to scroll by the height that was inserted above the row the operator is reading, so what they are reading does not move.
    /// </summary>
    internal event EventHandler? LiveRowsInserted;

    /// <summary>True while the poll timer is running: the panel is on screen and has a dispatcher to tick on.</summary>
    internal bool IsLiveTimerRunning => _liveTimer?.IsEnabled == true;

    /// <summary>
    /// What a poll samples the database with. The probe in the running app; a test replaces it to make the sample fail or say it cannot tell. Loads
    /// always use the real probe.
    /// </summary>
    internal Func<CancellationToken, Task<AuditStamp>>? LiveStampSource { get; set; }

    protected override void OnDeactivated() => StopLive();

    /// <summary>
    /// The panel came on screen: start the timer (when there is a dispatcher to tick on) and catch up once with whatever happened while it was away. The
    /// very first visit has nothing to catch up on - the initial load is on its way - and is left to it.
    /// </summary>
    private void StartLive()
    {
        var previous = Interlocked.Exchange(ref _liveCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        if (_liveTimer is null && LiveTimerEnabled && Application.Current?.Dispatcher is { } dispatcher)
        {
            _liveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = LivePollInterval };
            _liveTimer.Tick += (_, _) => LastLivePoll = PollLiveAsync();
        }

        _liveTimer?.Start();

        if (_shown is not null)
        {
            LastLivePoll = PollLiveAsync();
        }
    }

    /// <summary>The panel left the screen: no timer, and a poll in flight is cancelled (it may finish its statement; it adds nothing).</summary>
    private void StopLive()
    {
        _liveTimer?.Stop();
        var cts = Interlocked.Exchange(ref _liveCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    /// <summary>Forgets what the live refresh knew of the source that was on screen (a switch between Live and Archive).</summary>
    private void ForgetLive()
    {
        _liveStamp = AuditStamp.Unknown;
        _liveHorizon = default;
        _liveFloor = null;
        _liveHidden.Clear();
        _liveFailures = 0;
        _liveNotBefore = 0;
        LiveStatusText = string.Empty;
        LiveNote = string.Empty;
    }

    /// <summary>
    /// A list was just built from a fresh read (not "Load more"): it is current as of <paramref name="baseline"/>, the stamp taken before the read, and
    /// as of <paramref name="started"/>, when the read began. The archive has no live state.
    /// </summary>
    private void NoteLiveBaseline(AuditStamp baseline, DateTimeOffset started)
    {
        if (IsArchive)
        {
            return;
        }

        _liveStamp = baseline;
        _liveHorizon = started;
        NoteLiveChecked();
    }

    /// <summary>
    /// A load read <paramref name="page"/>: what it left out is now counted (so a poll must not count it again), and the list is loaded as far back as its
    /// last row - or as far as the cap, when the list stopped there. <paramref name="append"/> is "Load more", which reads further back and adds to what was read.
    /// </summary>
    private void RememberLoad(AuditPage page, bool append, bool capReached)
    {
        if (IsArchive)
        {
            return;
        }

        if (!append)
        {
            _liveHidden.Clear();
        }

        foreach (var hidden in page.HiddenRows)
        {
            _ = _liveHidden.Add((hidden.TimestampNanos, hidden.Id));
        }

        _liveFloor = capReached && Rows.Count > 0 ? CursorOf(Rows[^1]) : page.HasMore ? page.NextCursor : null;
    }

    private static AuditCursor CursorOf(AuditRow row) => new(row.TimestampNanos, row.Id);

    /// <summary>A poll found the list current (or made it so): the failures are over, and the caption says when.</summary>
    private void NoteLiveChecked()
    {
        _liveFailures = 0;
        _liveNotBefore = 0;
        LiveNote = string.Empty;
        LiveStatusText = "Updated " + TimeSource.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private Task<AuditStamp> SampleLiveAsync(CancellationToken cancellationToken) =>
        LiveStampSource is { } source ? source(cancellationToken) : Services.AuditChanges.SampleAsync(cancellationToken);

    /// <summary>
    /// One look for a change: the timer's tick, and the catch-up when the panel comes back. See the type documentation. Never throws: a failure is a note
    /// beside the caption and a longer wait.
    /// </summary>
    internal async Task PollLiveAsync()
    {
        if (!IsActive || IsArchive || _liveBusy || IsLoading)
        {
            return;
        }

        var reader = Services.Audit;
        if (!reader.Exists || TimeSource.GetTimestamp() < _liveNotBefore)
        {
            return;
        }

        _liveBusy = true;
        try
        {
            var stop = _liveCts?.Token ?? CancellationToken.None;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(CurrentGeneration(), stop);
            var token = linked.Token;

            // No list yet (the database did not exist when the panel opened, or the first read failed): read it, as Refresh would.
            if (_shown is null)
            {
                await LoadAsync(append: false, CancellationToken.None);
                if (_shown is null)
                {
                    throw new IOException("audit.db could not be read");
                }

                return;
            }

            var stamp = await SampleLiveAsync(token);
            if (!stamp.IsKnown)
            {
                throw new IOException("audit.db could not be sampled");
            }

            if (stamp.Matches(_liveStamp))
            {
                // Idle: the probe was the whole cost.
                NoteLiveChecked();
                return;
            }

            var started = TimeSource.GetUtcNow();
            bool merged;
            await _loadGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (IsLoading || _shown is null)
                {
                    // The operator's own load took the list while this waited: it is current as of that read.
                    return;
                }

                merged = await MergeLiveAsync(reader, token);
                if (merged)
                {
                    _liveStamp = stamp;
                    _liveHorizon = started;
                }
            }
            finally
            {
                _ = _loadGate.Release();
            }

            if (!merged)
            {
                // A storm: more arrived than a poll will merge. Read the list again from the newest page.
                await LoadAsync(append: false, CancellationToken.None);
                if (_shown is null)
                {
                    throw new IOException("audit.db could not be read");
                }

                return;
            }

            NoteLiveChecked();
        }
        catch (OperationCanceledException)
        {
            // The panel left the screen, or a filter changed: the list belongs to the next read.
        }
#pragma warning disable CA1031 // A database the gateway holds, or that cannot be read for a moment, degrades to a note and a longer wait.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
#pragma warning restore CA1031
        {
            BackOff(ex);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    /// <summary>A poll failed: the rows stay, the caption says so, and the next attempt waits twice as long as the last (at most a minute).</summary>
    private void BackOff(Exception failure)
    {
        _liveFailures++;
        var wait = TimeSpan.FromTicks(Math.Min(LivePollInterval.Ticks << Math.Min(_liveFailures - 1, 8), MaxLiveBackoff.Ticks));
        _liveNotBefore = TimeSource.GetTimestamp() + (long)(wait.TotalSeconds * TimeSource.TimestampFrequency);

        var reason = failure.Message.ReplaceLineEndings(" ").Trim();
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        LiveNote = $"Live refresh paused ({reason}). The list is as of its last update; trying again in {seconds} s.";
    }

    /// <summary>
    /// Reads the recent rows and puts the ones the list does not know into it. Returns false when there are more of them than a poll will merge - the caller
    /// reads the list again. Runs on the UI thread, inside the load gate; it changes the list only after its last await.
    /// </summary>
    private async Task<bool> MergeLiveAsync(AuditReader reader, CancellationToken token)
    {
        var shown = _shown!;
        var from = LiveFrom();
        var page = await reader.QueryAsync(BuildQuery(null) with { From = from, Limit = LiveWindowLimit }, token);

        // A filter changed while the page was read: the list is another question's, and this answer is not for it.
        token.ThrowIfCancellationRequested();
        if (page.HasMore)
        {
            return false;
        }

        var known = new HashSet<(long, string)>(Rows.Count);
        foreach (var row in Rows)
        {
            _ = known.Add((row.TimestampNanos, row.Id));
        }

        var fresh = new List<AuditRow>();
        var hidden = 0;
        foreach (var audit in page.Events)
        {
            var key = (NanosOf(audit), audit.Id);

            // Older than the last row a load read: "Load more" will read it. Listed already: nothing to do. Another connector's, in the platform-only view: not this list's.
            if (!IsInLoadedRange(key) || known.Contains(key) || (shown.PlatformOnly && audit.Connector is not null))
            {
                continue;
            }

            if (shown.Actionable && !ActionableRule.IsActionable(audit))
            {
                // Counted once: a low-signal row read again by the next poll is already in the set.
                if (_liveHidden.Add(key))
                {
                    hidden++;
                }

                continue;
            }

            fresh.Add(new AuditRow(audit));
        }

        var fromNanos = ToNanos(from);
        _ = _liveHidden.RemoveWhere(key => key.Nanos < fromNanos);

        if (fresh.Count == 0 && hidden == 0)
        {
            return true;
        }

        // Newest first, where the query's own order (retention nanos, then id, both descending) puts each: nearly always the top.
        fresh.Sort(static (a, b) => Compare(b, a));
        var at = 0;
        foreach (var row in fresh)
        {
            while (at < Rows.Count && Compare(Rows[at], row) > 0)
            {
                at++;
            }

            Rows.Insert(at, row);
            at++;
        }

        // The list is a window onto the newest MaxRows: what the new rows push past the end goes, and the window says so.
        var pushedOut = false;
        while (Rows.Count > MaxRows)
        {
            Rows.RemoveAt(Rows.Count - 1);
            pushedOut = true;
        }

        if (pushedOut)
        {
            IsRowCapReached = true;
            HasMore = false;
            _liveFloor = CursorOf(Rows[^1]);
        }

        HiddenCount += hidden;
        var total = shown.Total is { } counted ? counted + fresh.Count : (int?)null;
        _shown = shown with { Total = total };
        _extended = true;
        ResultSummary = Summarize(total, shown.PlatformOnly, shown.Actionable, shown.RangeLabel);
        IsEmpty = Rows.Count == 0;
        if (IsEmpty)
        {
            SetEmptyText(shown.Actionable);
        }

        StatusNote = OversizedNote();
        if (fresh.Count > 0)
        {
            LiveRowsInserted?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>True when a row is newer than the last one the loads read (or the loads reached the end of the window): the live refresh's to list, not "Load more"'s.</summary>
    private bool IsInLoadedRange((long Nanos, string Id) key) =>
        _liveFloor is not { } floor
        || key.Nanos > floor.TimestampNanos
        || (key.Nanos == floor.TimestampNanos && string.CompareOrdinal(key.Id, floor.Id) > 0);

    /// <summary>Unix nanoseconds, as the reader's sort key counts them (the retention column).</summary>
    private static long ToNanos(DateTimeOffset at) => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L;

    /// <summary>The instant of an event as its row will carry it (<see cref="AuditRow.TimestampNanos"/>): the reader's sort key, or the parsed time when the key is missing.</summary>
    private static long NanosOf(AuditEvent audit) =>
        audit.TimestampNanos > 0 ? audit.TimestampNanos : audit.Timestamp == DateTimeOffset.MinValue ? 0 : ToNanos(audit.Timestamp);

    /// <summary>The list's order: newer first; the same instant, the greater id first. Greater means "sorts earlier".</summary>
    private static int Compare(AuditRow a, AuditRow b)
    {
        var byTime = a.TimestampNanos.CompareTo(b.TimestampNanos);
        return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>Where the live query starts: <see cref="LateCommitWindow"/> before the last look, and never before the window the list was asked for.</summary>
    private DateTimeOffset LiveFrom()
    {
        var from = _liveHorizon - LateCommitWindow;
        if (SelectedRange.Since is { } since)
        {
            var window = WindowStart(since);
            if (window > from)
            {
                from = window;
            }
        }

        return from;
    }
}
