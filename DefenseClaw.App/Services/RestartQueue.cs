using System.Diagnostics;
using System.Globalization;
using System.Windows;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>One change that is saved and not yet in the running gateway: why, and when it was queued (UTC).</summary>
internal sealed record RestartQueueEntry(string Reason, DateTimeOffset QueuedAt);

/// <summary>
/// The app-wide queue of gateway restarts that are waiting (CUST-267), reached as <c>Services.RestartQueue</c>. The gateway reads config.yaml when
/// it starts, so a change saved without restarting it - a config-editor save, a wizard, guardrail or discovery run told <c>--no-restart</c> - is
/// not in effect until it does; the TUI's Setup page keeps a queue of the reasons (<c>setup_state.py: RestartQueue</c>: reason, queued_at,
/// last_started_at) and so does this. Three surfaces show the same queue - the Setup hub's banner, the Setup readiness row "Restart Pending", and an
/// Overview attention row - each with "Restart now" (a reviewed <c>defenseclaw-gateway restart</c>) and "Clear".
/// <para>
/// <b>What queues.</b> <see cref="Queue"/>, called by the config editor after a save that changed a value
/// (<see cref="RestartQueueRules.ConfigSaveReason"/>), and the runner: the service listens to every finished command
/// (<see cref="CliRunner.InvocationCompleted"/>), so a wizard, the guardrail and discovery controls, the palette and a Rerun are all covered with
/// no call of their own at the place that runs them. A successful run with a standalone <c>--no-restart</c> that is not a read queues a line
/// (<see cref="RestartQueueRules.PendingReasonFor"/>, which asks <see cref="CommandReview.RestartsGatewayFor"/> - the rule the review states
/// before the run - and does not make a second one).
/// </para>
/// <para>
/// <b>What clears it.</b> A restart, by any route. Seen from the gateway: each poll the monitor completes carries the start time <c>/health</c>
/// reports, and a start at or after the moment a line was queued applies that line (the gateway read config.yaml when it started), so the tray,
/// the palette, another app and a terminal all clear it; so does a different binary version while the queue was pending. Seen from this app's own
/// runs: a command <see cref="CommandOutcome"/> calls "gateway restarted" clears what was queued before it began, without waiting a poll. And
/// <see cref="Clear"/>, the operator's. <b>Not stored: the TUI's <c>last_started_at</c></b>. It compares the gateway's start with a baseline taken
/// when the line was queued, which is the monitor's last poll and so up to one interval old: a restart just before the save would clear a line
/// the restart never saw. Comparing the start with each line's own queued-at has no baseline to be stale, and its failure is the safe one (a
/// banner that stays until Clear, never a change taken for applied). (The TUI's own start-time clear never fires for what it queues:
/// <c>queue_restart</c> is called without <c>last_started_at</c>.)
/// </para>
/// <para>
/// <b>Not persisted.</b> The TUI keeps its queue in the Setup model (<c>SetupPanelModel.restart_queue = RestartQueue()</c>) and writes it nowhere,
/// so it does not survive the TUI; this one lives as long as the app process (a tray app: across panel switches and closing the window to the
/// tray) and is empty at the next start.
/// </para>
/// <para>
/// <b>No timer.</b> The monitor's poll is the clock: the service listens to <see cref="IGatewaySnapshotSource.PollCompleted"/> only while a line
/// is queued, so with nothing queued the poll costs it nothing and the monitor queues no delivery for it. <see cref="Changed"/> is raised on the UI
/// thread; every reader is safe on any thread.
/// </para>
/// </summary>
internal sealed class RestartQueue : IDisposable
{
    /// <summary>The most lines the queue holds; the oldest goes when a new reason arrives past it (a restart applies them all, and this is text for a banner).</summary>
    public const int MaxEntries = 20;

    /// <summary>What the TUI puts between reasons.</summary>
    public const string Separator = "; ";

    private readonly IGatewaySnapshotSource _source;
    private readonly CliRunner? _cli;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private readonly List<RestartQueueEntry> _entries = new();
    private string? _versionWhenQueued;
    private bool _watching;
    private bool _disposed;

    /// <param name="source">The gateway monitor, or a fake: the current snapshot, and the poll it is told about while something is queued.</param>
    /// <param name="cli">The runner whose finished commands queue and clear; null for a queue that only <see cref="Queue"/> and the monitor drive.</param>
    /// <param name="time">The clock for the queued-at times; the system's when null.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null.</param>
    public RestartQueue(IGatewaySnapshotSource source, CliRunner? cli = null, TimeProvider? time = null, Action<Action>? post = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _cli = cli;
        _time = time ?? TimeProvider.System;
        _post = post ?? PostToDispatcher;

        if (_cli is not null)
        {
            _cli.InvocationCompleted += OnInvocationCompleted;
        }
    }

    /// <summary>Raised on the UI thread when the lines changed: one was added, some were applied by a restart, or the operator cleared them.</summary>
    public event EventHandler? Changed;

    /// <summary>The queued lines, oldest first (a copy).</summary>
    public IReadOnlyList<RestartQueueEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>True while at least one line is queued: the TUI's <c>pending</c>.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    /// <summary>The reasons in one line, oldest first, joined with <c>; </c> (the TUI's <c>reason</c>); empty when nothing is queued.</summary>
    public string Reason
    {
        get
        {
            lock (_gate)
            {
                return string.Join(Separator, _entries.Select(static e => e.Reason));
            }
        }
    }

    /// <summary>When the oldest line still queued was queued (the TUI's <c>queued_at</c>); null when nothing is. A line queued again counts from the later time.</summary>
    public DateTimeOffset? QueuedAt
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count == 0 ? null : _entries.Min(static e => e.QueuedAt);
            }
        }
    }

    /// <summary>
    /// Queues a reason: a change is saved and the running gateway does not have it. A reason already queued is not repeated (the TUI's
    /// <c>with_reason</c>) but is queued <i>again</i> - its time moves to now, because a restart that came before this save did not apply it. A
    /// blank reason queues nothing. Returns true when a line was added.
    /// </summary>
    public bool Queue(string? reason)
    {
        var text = RestartQueueRules.Tidy(reason);
        if (text.Length == 0)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        bool added;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var existing = _entries.FindIndex(e => string.Equals(e.Reason, text, StringComparison.Ordinal));
            if (existing >= 0)
            {
                _entries[existing] = _entries[existing] with { QueuedAt = now };
                added = false;
            }
            else
            {
                if (_entries.Count == 0)
                {
                    _versionWhenQueued = VersionOf(_source.Current);
                    Watch();
                }
                else if (_entries.Count >= MaxEntries)
                {
                    _entries.RemoveAt(0);
                }

                _entries.Add(new RestartQueueEntry(text, now));
                added = true;
            }
        }

        if (added)
        {
            RaiseChanged();
        }

        return added;
    }

    /// <summary>The operator's Clear: forgets every line without restarting anything (what is saved stays saved and applies at the next restart). Returns true when something was queued.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return false;
            }

            _entries.Clear();
            Reset();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// What a finished command did to the queue: a restart (<see cref="CommandOutcome.GatewayRestarted"/>) applied every line queued before it began;
    /// a successful <c>--no-restart</c> change queued a line (<see cref="RestartQueueRules.PendingReasonFor"/>). Called for every command the runner
    /// finishes; true when the queue changed. A command that never started (refused, handed to a terminal) or is not DefenseClaw's is nothing.
    /// </summary>
    public bool Note(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (invocation.IsRunning || CommandRerun.ToolOf(invocation) is not { } tool)
        {
            return false;
        }

        if (CommandOutcome.Of(invocation).GatewayRestarted)
        {
            // The restart happened somewhere in the run, so only a line queued before the run began is certainly applied by it; one queued while
            // it ran is the monitor's to settle, from the start time the restarted gateway reports.
            return RemoveQueuedAtOrBefore(invocation.StartedAt);
        }

        var succeeded = invocation.ExitCode == 0 && invocation.FailureReason is null;
        return RestartQueueRules.PendingReasonFor(tool, invocation.Argv, succeeded) is { } reason && Queue(reason);
    }

    /// <summary>
    /// What a completed poll tells the queue: the gateway's start time applies every line queued at or before it, and a different binary version
    /// than the one running when the queue began applies them all. Called from the monitor's poll while something is queued; true when the queue changed.
    /// </summary>
    public bool Observe(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var start = RestartQueueRules.GatewayStart(snapshot);
        var version = VersionOf(snapshot);
        bool changed;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return false;
            }

            var before = _entries.Count;
            if (version is not null && _versionWhenQueued is not null && !string.Equals(version, _versionWhenQueued, StringComparison.OrdinalIgnoreCase))
            {
                // The binary was replaced: a new process, which read the configuration. (A running gateway reports its version in /health.)
                _entries.Clear();
            }
            else if (start is { } started)
            {
                _ = _entries.RemoveAll(e => e.QueuedAt <= started);
            }

            if (_entries.Count == 0)
            {
                Reset();
            }
            else
            {
                // A version that was not known when the queue began is learned as soon as a poll has one.
                _versionWhenQueued ??= version;
            }

            changed = _entries.Count != before;
        }

        if (changed)
        {
            RaiseChanged();
        }

        return changed;
    }

    private bool RemoveQueuedAtOrBefore(DateTimeOffset moment)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.QueuedAt <= moment) == 0)
            {
                return false;
            }

            if (_entries.Count == 0)
            {
                Reset();
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Back to nothing queued: no version, and no listening to the monitor. Holds <see cref="_gate"/>.</summary>
    private void Reset()
    {
        _versionWhenQueued = null;
        Unwatch();
    }

    private void Watch()
    {
        if (!_watching)
        {
            _source.PollCompleted += OnPollCompleted;
            _watching = true;
        }
    }

    private void Unwatch()
    {
        if (_watching)
        {
            _source.PollCompleted -= OnPollCompleted;
            _watching = false;
        }
    }

    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e) => _ = Observe(e.Snapshot);

    /// <summary>
    /// The runner raises this on a pool thread inside its own completion path, so nothing may escape: a queue that fails to note a run costs a
    /// banner, and an exception here would be the runner's.
    /// </summary>
    private void OnInvocationCompleted(object? sender, CliInvocation invocation)
    {
        try
        {
            _ = Note(invocation);
        }
#pragma warning disable CA1031 // See the method summary.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"restart queue: could not note a finished command: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The version the queue may decide on: the trusted one (<see cref="GatewaySnapshot.TrustedBinaryVersion"/>), so a version claimed by an
    /// unverified port owner never applies or keeps lines (CUST-341). Null when unknown or unverified.
    /// </summary>
    private static string? VersionOf(GatewaySnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.TrustedBinaryVersion) ? null : snapshot.TrustedBinaryVersion.Trim();

    private void RaiseChanged()
    {
        if (_disposed)
        {
            return;
        }

        _post(RaiseNow);
    }

    /// <summary>Each handler on its own: one that throws is traced and does not stop the rest (or the caller that queued).</summary>
    private void RaiseNow()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A panel that cannot redraw must not keep the others from hearing of the change.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"restart queue: a Changed handler ({handler.Method.DeclaringType?.Name}) failed: {ex}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
            Reset();
        }

        if (_cli is not null)
        {
            _cli.InvocationCompleted -= OnInvocationCompleted;
        }
    }

    /// <summary>
    /// The application's dispatcher, the way <c>AppServices.RaiseConfigReloaded</c> does it: in place on the UI thread (and when there is no WPF
    /// application, as in a headless run), queued otherwise, and nothing once shutdown has begun.
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
}

/// <summary>The words every surface of the queue uses, so the banner, the readiness row and the Overview row cannot disagree.</summary>
internal static class RestartQueueText
{
    /// <summary>The banner's and the Overview row's heading.</summary>
    public const string Title = "Gateway restart pending";

    /// <summary>What a queued restart means for the gateway that is running.</summary>
    public const string Consequence = "The running gateway keeps its previous settings until it restarts.";

    /// <summary>The queue's one-line account: the reasons, when the first was queued, and what it means. Empty while nothing is queued.</summary>
    public static string Detail(RestartQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var reason = queue.Reason.TrimEnd('.', ' ');
        if (reason.Length == 0)
        {
            return string.Empty;
        }

        var at = queue.QueuedAt is { } queued ? $" Queued {QueuedAtText(queued)}." : string.Empty;
        return $"{reason}.{at} {Consequence}";
    }

    /// <summary>When a line was queued, in the viewer's own clock: the time today, the date and time before.</summary>
    public static string QueuedAtText(DateTimeOffset queued)
    {
        var local = queued.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
    }
}
