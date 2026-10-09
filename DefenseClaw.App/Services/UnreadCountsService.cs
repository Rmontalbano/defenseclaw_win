using System.Diagnostics;
using System.Windows;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>What <see cref="UnreadCountsService.Changed"/> reports.</summary>
internal sealed class UnreadChangedEventArgs : EventArgs
{
    public UnreadChangedEventArgs(IReadOnlyDictionary<string, int> counts)
    {
        Counts = counts;
    }

    /// <summary>The count of each tracked panel (<see cref="UnreadCountsService.TrackedPanels"/>), 0 for the panel on screen; none above <see cref="UnreadCountsService.CountLimit"/>.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }
}

/// <summary>
/// "New since last visit" for the stream panels (CUST-265): how many audit rows, Activity entries and AI Discovery components arrived since the
/// operator last had that panel on screen, for the sidebar's capsules. The installed TUI does the same on its tab strip (<c>tui_state.py</c>
/// <c>panel_seen_counts</c>, <c>_panel_unread_count</c>: <c>max(0, total - seen)</c>, capped at 99, not shown on the active panel).
/// <para>
/// <b>Not the TUI's count.</b> The TUI remembers <em>how many</em> items a panel had when it was opened and compares that with the length of the
/// panel's current list. That stops working the moment a list is capped (its Audit list is the newest 500: once the database has more, the count
/// never moves and the tab never lights), trimmed or cleared, and for AI Discovery it reads a member the snapshot does not have, so it is always
/// zero. This remembers <em>where</em> the operator looked - a position that means the same thing tomorrow - and counts what is past it. The
/// markers are <see cref="SeenSettings"/>: the newest <c>rowid</c> for Audit, a UTC instant for Activity and for AI Discovery. They move when the
/// panel is shown <em>and</em> when it is left (the TUI moves them only on arrival, so what scrolled past while the panel was open comes back
/// as "new" the moment it is left), and survive a restart.
/// </para>
/// <para>
/// <b>Where each count comes from - nothing here is a poll of its own.</b>
/// <list type="bullet">
///   <item><description><b>Activity</b>: the runner's in-memory list (<see cref="CliRunner.Activity"/>, at most its capacity) counted past the marker;
///   recounted when a run starts (<see cref="CliRunner.InvocationStarted"/>, an event that exists anyway) and on the alert tick. No I/O.</description></item>
///   <item><description><b>AI Discovery</b>: handed over by a reader that already has the state file in hand - the Overview's agents card, on its slow
///   cadence (<see cref="ReportAiDiscovery"/>) - and compared with the marker. Nothing is read for it, so it is as fresh as the last time the
///   Overview or the AI Discovery panel read the file, and says nothing when neither has. A component is new when it was <em>first seen</em> after the
///   marker (the scanner's own <c>state: new</c> lasts one scan).</description></item>
///   <item><description><b>Audit</b>: nothing reads <c>audit.db</c> while its panel is off screen, so this is the one count that costs a statement. It
///   rides the monitor's 30 s alert tick (<see cref="IGatewaySnapshotSource.AlertCadenceElapsed"/>, the tick the alert counts use) and asks
///   <see cref="AuditHeadReader"/> only when the shared <see cref="AuditChangeProbe"/> says the file has changed since the last answer: one
///   statement of two index probes, bounded at 100 rows, at most once per tick - and none at all on an idle database, where the whole cost is the probe's
///   one <c>PRAGMA data_version</c>. The panel on screen is not counted (it shows its own rows, live).</description></item>
///   <item><description><b>Logs</b> is not tracked: <c>gateway.log</c> and <c>watchdog.log</c> lines carry no id or time (the 0.8.x sidecar writes bare
///   lines), nothing reads either file until the Logs panel has been opened once and then the tails only buffer, and the Verdicts and Events streams
///   are audit rows the Audit count already includes. An honest number would need a read the app does not make today, and a session-long counter of
///   routine gateway chatter would sit at 99+.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Idle costs nothing.</b> No timer, no thread, no watcher of its own. It runs only while something listens to <see cref="Changed"/> (the dashboard
/// window) <em>and</em> the window can be seen (<see cref="SetInteractive"/>: not in the tray, not minimized): then it is attached to the alert tick and to
/// the runner's start event, and reads once when it attaches. In the tray, with monitoring paused (no tick), or with nothing subscribed, it does no work
/// at all; the last counts stay readable from <see cref="Current"/>. The markers are written to the settings file from a pool thread, coalesced, and only
/// when one changed.
/// </para>
/// <para>
/// <b>A panel never looked at has no marker</b>, and the first look at it sets one rather than flagging its whole history: the first time Audit's head
/// is read, or AI Discovery's file is, the marker becomes that moment. (Activity's list is the session's own, so with no marker everything in it is new.) A
/// head that is <em>below</em> the marker - the table was emptied, or <c>VACUUM</c> renumbered it - also starts again from the head.
/// </para>
/// </summary>
internal sealed class UnreadCountsService : IDisposable
{
    /// <summary>The Audit panel's id.</summary>
    public const string AuditId = "audit";

    /// <summary>The Activity panel's id.</summary>
    public const string ActivityId = "activity";

    /// <summary>The AI Discovery panel's id.</summary>
    public const string AiDiscoveryId = "ai-discovery";

    /// <summary>The most a capsule shows; more reads "99+" (<see cref="UnreadPresentation"/>). The TUI's cap.</summary>
    public const int Cap = 99;

    /// <summary>What is counted up to: one past the cap, which is enough to know there are more without counting on.</summary>
    public const int CountLimit = Cap + 1;

    private static readonly string[] Tracked = { AuditId, ActivityId, AiDiscoveryId };

    private readonly AppSettingsStore _settings;
    private readonly AuditHeadReader _audit;
    private readonly CliRunner _cli;
    private readonly IGatewaySnapshotSource _tick;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<Action> _post;

    /// <summary>Guards the state below and the subscriber list; never held across an <c>await</c>, a callback or a write.</summary>
    private readonly object _lock = new();

    /// <summary>Serializes the writes of the markers to the settings file; taken alone, never inside <see cref="_lock"/>.</summary>
    private readonly object _writeGate = new();

    /// <summary>Serializes passes: one read at a time, however many ticks and callers ask.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly Dictionary<string, long> _markers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Bumped whenever a panel's marker moves; a read that started under an older one is for a question that has since been answered, and is dropped.</summary>
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    /// <summary>
    /// How many visits (a panel shown or left) are still moving a panel's marker. While there is one the panel's capsule stays hidden and its count is
    /// not read: the marker is about to jump to the head, and a count made against the old one would flash rows the operator has just seen.
    /// </summary>
    private readonly Dictionary<string, int> _visiting = new(StringComparer.Ordinal);

    private EventHandler<UnreadChangedEventArgs>? _changed;
    private CancellationTokenSource? _attached;
    private IReadOnlyDictionary<string, int> _published;
    private AiDiscoveryHead? _aiHead;
    private long _aiDisplayedTicks;
    private string? _active;
    private bool _interactive = true;
    private bool _disposed;
    private string? _lastTracedFailure;

    /// <summary>The newest Audit marker request; only the answer to the latest one is applied.</summary>
    private int _auditAdvance;

    private Task _persisting = Task.CompletedTask;
    private bool _persistPending;
    private long _passes;

    /// <param name="settings">Where the markers live (<c>seen</c>).</param>
    /// <param name="audit">Reads the audit database's head; shares the app's change probe.</param>
    /// <param name="cli">The runner whose in-memory list Activity is counted from.</param>
    /// <param name="tick">The monitor: its alert tick is the only clock this has.</param>
    /// <param name="now">The instant a visit to Activity is marked at, and the one AI Discovery's marker starts from at the first sight of its file; the system's UTC clock when null. A test passes its own.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    public UnreadCountsService(
        AppSettingsStore settings,
        AuditHeadReader audit,
        CliRunner cli,
        IGatewaySnapshotSource tick,
        Func<DateTimeOffset>? now = null,
        Action<Action>? post = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _post = post ?? PostToDispatcher;

        foreach (var id in Tracked)
        {
            _counts[id] = 0;
            _generation[id] = 0;
            _visiting[id] = 0;
        }

        // What the last run left. Only the panels this build tracks are taken; a marker for one it does not stays in the file untouched.
        foreach (var (id, marker) in _settings.Current.Seen.Markers)
        {
            if (IsTracked(id))
            {
                _markers[id] = marker;
            }
        }

        _published = Snapshot();
    }

    /// <summary>The panels that have a capsule, in sidebar order.</summary>
    public static IReadOnlyList<string> TrackedPanels => Tracked;

    /// <summary>True for a panel that has a "new since last visit" count.</summary>
    public static bool IsTracked(string? panelId) => panelId is not null && Array.IndexOf(Tracked, panelId) >= 0;

    /// <summary>
    /// Raised on the UI thread when what the sidebar shows changes. The first subscriber starts the service (it reads once at once, and attaches to the
    /// alert tick and the runner's start event) and the last one to leave stops it. See the type documentation for when it is running.
    /// </summary>
    public event EventHandler<UnreadChangedEventArgs>? Changed
    {
        add
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _changed += value;
                UpdateRunning();
            }
        }

        remove
        {
            lock (_lock)
            {
                _changed -= value;
                UpdateRunning();
            }
        }
    }

    /// <summary>The counts as the sidebar shows them now (0 for the panel on screen). Readable from any thread, subscribed or not.</summary>
    public IReadOnlyDictionary<string, int> Current
    {
        get
        {
            lock (_lock)
            {
                return _published;
            }
        }
    }

    /// <summary>What the sidebar shows for <paramref name="panelId"/> now.</summary>
    public int CountOf(string panelId) => Current.TryGetValue(panelId, out var count) ? count : 0;

    /// <summary>True while the service is attached to the alert tick and the runner's start event: something listens, and the window can be seen.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _attached is not null;
            }
        }
    }

    /// <summary>How many passes have run (the catch-up when it attaches, and one per tick it was not too busy for); what the idle-cost tests count.</summary>
    public long PassCount => Interlocked.Read(ref _passes);

    /// <summary>True while a pass is reading; a tick that finds one running is skipped, not queued. What a test waits on before it ticks and expects a pass.</summary>
    internal bool IsPassRunning => _refreshGate.CurrentCount == 0;

    /// <summary>The marker of <paramref name="panelId"/> as held now; false when the panel has not been looked at.</summary>
    public bool TryGetMarker(string panelId, out long marker)
    {
        lock (_lock)
        {
            return _markers.TryGetValue(panelId, out marker);
        }
    }

    /// <summary>The most recent write of the markers to the settings file; completes when it has been made. What a test awaits before it opens the file as "the next run".</summary>
    internal Task Persisted
    {
        get
        {
            lock (_lock)
            {
                return _persisting;
            }
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Whether the dashboard window can be seen (shown and not minimized). While it cannot, nothing is attached and nothing is read: the capsules are
    /// on a window nobody is looking at. Coming back reads once, like every panel's catch-up. <see cref="PanelCatalog"/> is the one caller.
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        lock (_lock)
        {
            if (_interactive == interactive)
            {
                return;
            }

            _interactive = interactive;
            UpdateRunning();
        }
    }

    /// <summary>Attaches when there is a subscriber and the window can be seen, detaches when either stops being true. Callers hold <c>_lock</c>.</summary>
    private void UpdateRunning()
    {
        var shouldRun = _changed is not null && _interactive && !_disposed;
        if (shouldRun && _attached is null)
        {
            _attached = new CancellationTokenSource();
            _tick.AlertCadenceElapsed += OnTick;
            _cli.InvocationStarted += OnInvocationStarted;
            StartRefresh(_attached.Token);
        }
        else if (!shouldRun && _attached is not null)
        {
            _tick.AlertCadenceElapsed -= OnTick;
            _cli.InvocationStarted -= OnInvocationStarted;
            var attached = _attached;
            _attached = null;
            attached.Cancel();
            attached.Dispose();
        }
    }

    private void OnTick(object? sender, GatewaySnapshotEventArgs e)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_attached is null)
            {
                return;
            }

            token = _attached.Token;
        }

        // A pass still reading (a database the gateway holds) is not piled on: the next tick finds it done.
        if (IsPassRunning)
        {
            return;
        }

        StartRefresh(token);
    }

    /// <summary>A run started: Activity's count is a walk over the runner's in-memory list. Raised on whatever thread started the run.</summary>
    private void OnInvocationStarted(object? sender, CliInvocation invocation)
    {
        try
        {
            lock (_lock)
            {
                if (_attached is null)
                {
                    return;
                }
            }

            RecountActivity();
            Publish();
        }
#pragma warning disable CA1031 // A count that could not be made must not become a fault in the runner that raised the event.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"unread counts: the activity count failed: {ex}");
        }
    }

    /// <summary>Fire and forget, with the faults in the trace: the tick and the first read have no caller to tell.</summary>
    private void StartRefresh(CancellationToken token) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The window went away (or was hidden) while this waited or ran: nothing to publish.
            }
#pragma warning disable CA1031 // A background pass that throws has nobody to tell; it must not become an unobserved task exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"unread counts: a pass failed: {ex}");
            }
        });

    // ------------------------------------------------------------------ visits

    /// <summary>
    /// The panel came on screen. Its count goes to zero at once (the capsule is not shown on the panel being looked at) and its marker moves to what is
    /// there now. The returned task completes when the marker has: Audit's head is one read of the database, memoized under the change probe. Never faults.
    /// </summary>
    public Task PanelShown(string panelId)
    {
        if (!IsTracked(panelId) || _disposed)
        {
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            _active = panelId;
            _counts[panelId] = 0;
            _generation[panelId]++;
            _visiting[panelId]++;
        }

        Publish();
        return AdvanceAsync(panelId);
    }

    /// <summary>
    /// The panel left the screen, or the window it was on was hidden. Its marker moves to what is there now - what scrolled past while the panel
    /// was open was seen - and the count starts again from zero. The returned task completes when the marker has. Never faults.
    /// </summary>
    public Task PanelLeft(string panelId)
    {
        if (!IsTracked(panelId) || _disposed)
        {
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            // Another panel's arrival may already have been announced: only the panel that is current is cleared.
            if (string.Equals(_active, panelId, StringComparison.Ordinal))
            {
                _active = null;
            }

            _generation[panelId]++;
            _visiting[panelId]++;
        }

        return AdvanceAsync(panelId);
    }

    /// <summary>Moves <paramref name="panelId"/>'s marker to where its content is now, recounts it (zero, by construction) and publishes.</summary>
    private async Task AdvanceAsync(string panelId)
    {
        try
        {
            switch (panelId)
            {
                case AuditId:
                    await AdvanceAuditAsync().ConfigureAwait(false);
                    break;

                case ActivityId:
                    if (SetMarker(ActivityId, _now().UtcTicks))
                    {
                        SchedulePersist();
                    }

                    RecountActivity();
                    break;

                case AiDiscoveryId:
                    AdvanceAiDiscovery();
                    break;
            }
        }
#pragma warning disable CA1031 // Moving a marker is housekeeping: a failure leaves it where it was and is not the navigation's problem.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"unread counts: could not move the {panelId} marker: {ex}");
        }
        finally
        {
            // The visit is over: the capsule may show again (the panel on screen stays hidden), with what is counted past the marker now.
            lock (_lock)
            {
                _visiting[panelId]--;
            }

            Publish();
        }
    }

    /// <summary>
    /// Audit's marker is the database's head, read now. Two visits in a row each ask; the answer to the later question is the one applied, so a read that
    /// finishes second cannot put the marker back. A database with no table yet has no head to mark, and a read that failed leaves the marker alone.
    /// </summary>
    private async Task AdvanceAuditAsync()
    {
        var ticket = Interlocked.Increment(ref _auditAdvance);

        AuditHead head;
        try
        {
            head = await _audit.ReadAsync(long.MaxValue, 0).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A locked or unreadable database: the marker stays, and the next visit or tick tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TraceFailure(ex);
            return;
        }

        if (head.Status != AuditHeadStatus.Ok || ticket != Volatile.Read(ref _auditAdvance))
        {
            return;
        }

        if (SetMarker(AuditId, head.HeadRowId))
        {
            SchedulePersist();
        }

        SetCount(AuditId, 0);
    }

    /// <summary>AI Discovery's marker is the time of the newest data its panel has shown; nothing shown yet (or never visited) leaves it where it is.</summary>
    private void AdvanceAiDiscovery()
    {
        long shown;
        lock (_lock)
        {
            shown = _aiDisplayedTicks;
        }

        if (shown <= 0)
        {
            return;
        }

        // Forward only: a marker that was set at the first sight of the data is not taken back to an older load.
        var existing = TryGetMarker(AiDiscoveryId, out var held) ? held : 0;
        if (shown > existing && SetMarker(AiDiscoveryId, shown))
        {
            SchedulePersist();
        }

        RecountAiDiscovery();
    }

    /// <summary>
    /// The AI Discovery panel has put data on screen that was read at <paramref name="asOf"/>. What it shows is what the operator has seen, so while the
    /// panel is the one on screen the marker follows it; when it is left the marker stays where the last load put it - a component discovered after
    /// that was never shown, and is still new.
    /// </summary>
    public void NoteAiDiscoveryDisplayed(DateTimeOffset asOf)
    {
        bool onScreen;
        lock (_lock)
        {
            _aiDisplayedTicks = Math.Max(_aiDisplayedTicks, asOf.UtcTicks);
            onScreen = string.Equals(_active, AiDiscoveryId, StringComparison.Ordinal);
        }

        if (onScreen)
        {
            AdvanceAiDiscovery();
            Publish();
        }
    }

    /// <summary>
    /// A reader that has just read the AI discovery state file hands over when each component in it was first seen (<c>AiDiscoveryNovelty.Parse</c>).
    /// Nothing is read for this: it is the state file's text the Overview's agents card already holds, on its slow cadence. The first report of a
    /// session with no marker sets one (this moment) instead of calling everything in the file new.
    /// </summary>
    public void ReportAiDiscovery(AiDiscoveryHead head)
    {
        ArgumentNullException.ThrowIfNull(head);

        var baseline = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _aiHead = head;
            if (!_markers.ContainsKey(AiDiscoveryId))
            {
                _markers[AiDiscoveryId] = _now().UtcTicks;
                _generation[AiDiscoveryId]++;
                baseline = true;
            }
        }

        if (baseline)
        {
            SchedulePersist();
        }

        RecountAiDiscovery();
        Publish();
    }

    // ------------------------------------------------------------------ counting

    /// <summary>
    /// Counts everything now: Activity from the runner's list, AI Discovery from what was last reported, Audit from the database if its probe says the
    /// file changed. What the first subscriber's catch-up and every tick run; also what a caller that wants the numbers current awaits. A read that fails
    /// keeps the last good count (it never zeroes a badge, which would read as "all clear") and is traced once.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Interlocked.Increment(ref _passes);
            RecountActivity();
            RecountAiDiscovery();
            await RecountAuditAsync(cancellationToken).ConfigureAwait(false);

            // Inside the gate, so a pass that is no longer running has also said what it found.
            Publish();
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>Activity: the entries of the runner's list that started after the marker; with no marker, all of them (the list is this session's own).</summary>
    private void RecountActivity()
    {
        long marker;
        lock (_lock)
        {
            marker = _markers.TryGetValue(ActivityId, out var held) ? held : 0;
        }

        var count = 0;
        foreach (var invocation in _cli.Activity)
        {
            if (invocation.StartedAt.UtcTicks > marker)
            {
                count++;
            }
        }

        SetCount(ActivityId, count);
    }

    /// <summary>AI Discovery: the components reported first seen after the marker. Before any report there is nothing to say, which is not "nothing new".</summary>
    private void RecountAiDiscovery()
    {
        AiDiscoveryHead? head;
        long marker;
        lock (_lock)
        {
            head = _aiHead;
            marker = _markers.TryGetValue(AiDiscoveryId, out var held) ? held : long.MaxValue;
        }

        if (head is not null)
        {
            SetCount(AiDiscoveryId, head.CountAfter(marker));
        }
    }

    /// <summary>
    /// Audit: the rows past the marker, up to <see cref="CountLimit"/>. Skipped for the panel on screen, which shows its own rows live. With no marker the
    /// head becomes it (the first look at the database), and so does a head below it (the table started again).
    /// </summary>
    private async Task RecountAuditAsync(CancellationToken cancellationToken)
    {
        long marker;
        bool hasMarker;
        int generation;
        lock (_lock)
        {
            if (string.Equals(_active, AuditId, StringComparison.Ordinal) || _visiting[AuditId] > 0)
            {
                return;
            }

            hasMarker = _markers.TryGetValue(AuditId, out marker);
            generation = _generation[AuditId];
        }

        AuditHead read;
        try
        {
            read = await _audit.ReadAsync(hasMarker ? marker : long.MaxValue, hasMarker ? CountLimit : 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A database that cannot be read this time (locked, timed out, corrupt) keeps the last count, and is not "no news".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TraceFailure(ex);
            return;
        }

        if (read.Status != AuditHeadStatus.Ok)
        {
            SetCount(AuditId, 0);
            return;
        }

        lock (_lock)
        {
            // A visit moved the marker (or put the panel on screen) while this read ran: it answered a question that has been answered.
            if (generation != _generation[AuditId] || string.Equals(_active, AuditId, StringComparison.Ordinal) || _visiting[AuditId] > 0)
            {
                return;
            }
        }

        if (!hasMarker || read.HeadRowId < marker)
        {
            if (SetMarker(AuditId, read.HeadRowId))
            {
                SchedulePersist();
            }

            SetCount(AuditId, 0);
            return;
        }

        SetCount(AuditId, read.Newer);
    }

    private void SetCount(string panelId, int count)
    {
        lock (_lock)
        {
            _counts[panelId] = Math.Clamp(count, 0, CountLimit);
        }
    }

    /// <summary>Moves a marker. False when it is already there. A moved marker invalidates any read in flight for the panel.</summary>
    private bool SetMarker(string panelId, long value)
    {
        lock (_lock)
        {
            if (_markers.TryGetValue(panelId, out var held) && held == value)
            {
                return false;
            }

            _markers[panelId] = value;
            _generation[panelId]++;
            return true;
        }
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>The counts the sidebar shows: each tracked panel's, with the panel on screen at zero. Callers hold <c>_lock</c>.</summary>
    private IReadOnlyDictionary<string, int> Snapshot()
    {
        var shown = new Dictionary<string, int>(Tracked.Length, StringComparer.Ordinal);
        foreach (var id in Tracked)
        {
            shown[id] = string.Equals(id, _active, StringComparison.Ordinal) || _visiting[id] > 0 ? 0 : _counts[id];
        }

        return shown;
    }

    /// <summary>Raises <see cref="Changed"/> on the UI thread if what the sidebar shows is not what it was told last. Quiet otherwise.</summary>
    private void Publish()
    {
        UnreadChangedEventArgs? args = null;
        lock (_lock)
        {
            var next = Snapshot();
            if (next.Count == _published.Count && next.All(pair => _published.TryGetValue(pair.Key, out var before) && before == pair.Value))
            {
                return;
            }

            _published = next;
            if (_changed is not null)
            {
                args = new UnreadChangedEventArgs(next);
            }
        }

        if (args is not null)
        {
            _post(() => Raise(args));
        }
    }

    /// <summary>On the UI thread. Reads the subscribers now, so one that left while this was queued is not called.</summary>
    private void Raise(UnreadChangedEventArgs args)
    {
        EventHandler<UnreadChangedEventArgs>? handlers;
        lock (_lock)
        {
            handlers = _changed;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<UnreadChangedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // One misbehaving subscriber must not silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"unread counts: a Changed subscriber threw: {ex}");
            }
        }
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// Writes the markers to the settings file from a pool thread - the file is written synchronously and a scanner can hold it for a moment, which is
    /// no business of the UI thread's - and coalesced: a marker that moves while a write is pending is carried by that write or by the one it schedules.
    /// </summary>
    private void SchedulePersist()
    {
        lock (_lock)
        {
            if (_persistPending || _disposed)
            {
                return;
            }

            _persistPending = true;
            _persisting = Task.Run(() =>
            {
                lock (_lock)
                {
                    // From here a marker that moves schedules another write; this one snapshots what is there.
                    _persistPending = false;
                }

                PersistNow();
            });
        }
    }

    private void PersistNow()
    {
        try
        {
            lock (_writeGate)
            {
                Dictionary<string, long> mine;
                lock (_lock)
                {
                    mine = new Dictionary<string, long>(_markers, StringComparer.Ordinal);
                }

                // The store's own section is the base, so a marker this build does not track (a newer build's) is carried through untouched.
                _ = _settings.Update(settings =>
                {
                    var seen = settings.Seen;
                    foreach (var (id, marker) in mine)
                    {
                        seen = seen.With(id, marker);
                    }

                    return seen == settings.Seen ? settings : settings with { Seen = seen };
                });
            }
        }
#pragma warning disable CA1031 // A marker that could not be saved is held in memory and goes out with the next write; it is not a fault anyone can act on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"unread counts: the markers could not be saved: {ex.Message}");
        }
    }

    /// <summary>
    /// The application's dispatcher, the way <see cref="AlertCountsService"/> does it: in place on the UI thread (and when there is no WPF application,
    /// as in a headless run), queued otherwise, and nothing once shutdown has begun.
    /// </summary>
    private static void PostToDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post: nothing is left to update.
        }
    }

    /// <summary>The same failure every 30 s is traced once.</summary>
    private void TraceFailure(Exception ex)
    {
        var key = $"{ex.GetType().FullName}|{ex.Message}";
        lock (_lock)
        {
            if (key == _lastTracedFailure)
            {
                return;
            }

            _lastTracedFailure = key;
        }

        Trace.TraceWarning($"unread counts: the audit count could not be read: {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose()
    {
        Task pending;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changed = null;
            UpdateRunning();
            pending = _persisting;
        }

        // The exit path: give a write that is already under way a moment to finish. Bounded - the markers are a convenience, and the file is written
        // atomically, so a write cut short leaves the previous one.
        try
        {
            _ = pending.Wait(TimeSpan.FromSeconds(2));
        }
#pragma warning disable CA1031 // Nothing on the way out is worth a fault.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
