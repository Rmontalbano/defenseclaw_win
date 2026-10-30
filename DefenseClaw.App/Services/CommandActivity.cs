using System.Diagnostics;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// How many commands this app has had in flight for longer than a short grace period: what the status strip's "N commands running" chip reads. The
/// TUI's strip says "running" while its executor has a command going; this is the same for the one runner every command of this app goes through,
/// minus the blink (see <b>Grace</b>).
/// <para>
/// <b>What it sees.</b> Everything <see cref="CliRunner"/> runs, whoever asked and whatever it is - a scan, a read, a gateway verb, a wizard step,
/// a probe a panel makes on opening (the runner raises <see cref="CliRunner.InvocationStarted"/> and, exactly once per invocation however it ends,
/// <see cref="CliRunner.InvocationCompleted"/>). That is the difference from <see cref="ScanActivity"/>, which counts only the CLI's scans for the
/// tray's shield and is built the same way. A hand-off (a command the app only copies to a console, <see cref="CliRunner.RecordHandOff"/>) and a
/// refusal (<see cref="CliRunner.RecordRefusal"/>) are born finished and never count. What the app does not see, it cannot show: a command typed
/// in a terminal, or work the gateway does by itself.
/// </para>
/// <para>
/// <b>Grace.</b> The runner does not say who asked (a page's own quick read of the CLI looks the same as a wizard step), so a command counts only once
/// it has been running for <see cref="Grace"/>: a read that finishes inside it never shows, and a chip that would flash on every page change stays
/// quiet, while a scan, a doctor run or a wizard step - anything the operator would wait for - shows. A zero grace counts a command from the moment
/// it starts (what the tests that drive the count by hand use).
/// </para>
/// <para>
/// <b>Cost.</b> No polling: two event subscriptions, a set of the ids in flight (empty almost always) and, for each command, one one-shot timer that
/// is disposed when the command ends or the grace passes. <see cref="Changed"/> is
/// raised on the UI thread when the number of commands in flight differs from what was last announced; an idle app does nothing here, and a start
/// and an end that cancel out before the UI thread looks announce nothing. A handler never throws into the runner.
/// </para>
/// </summary>
internal sealed class CommandActivity : IDisposable
{
    private readonly CliRunner _cli;
    private readonly Action<Action> _post;

    /// <summary>Guards the set and the flags below; never held across a callback.</summary>
    private readonly object _lock = new();

    /// <summary>The ids of the commands in flight that have outlasted the grace: what <see cref="Count"/> counts.</summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>The ids of the commands in flight that are still inside the grace, with the timer that promotes each.</summary>
    private readonly Dictionary<string, ITimer> _pending = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    /// <summary>The count <see cref="Changed"/> last announced.</summary>
    private int _announced;

    private bool _disposed;

    /// <param name="cli">The runner whose invocations are watched.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    /// <param name="time">The clock the grace is timed on; the system's when null.</param>
    /// <param name="grace">How long a command runs before it counts; zero (the default) counts it at once.</param>
    public CommandActivity(CliRunner cli, Action<Action>? post = null, TimeProvider? time = null, TimeSpan grace = default)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _post = post ?? UiPost.ToDispatcher;
        _time = time ?? TimeProvider.System;
        Grace = grace < TimeSpan.Zero ? TimeSpan.Zero : grace;

        // Subscribed first, then seeded: a command that starts in between is in the set once (it is a set), and one that finishes in between is
        // skipped by the seed (FinishedAt is set before InvocationCompleted is raised) or removed by its completion.
        _cli.InvocationStarted += OnStarted;
        _cli.InvocationCompleted += OnCompleted;
        Seed();
    }

    /// <summary>Raised on the UI thread when <see cref="Count"/> is not what was last announced (the first command began, a second joined it, the last ended).</summary>
    public event EventHandler? Changed;

    /// <summary>How long a command must have been running before it counts.</summary>
    public TimeSpan Grace { get; }

    /// <summary>How many commands have been in flight for longer than <see cref="Grace"/>. Readable from any thread.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>The runner's event, raised on the thread that started the command. Never throws into the runner.</summary>
    internal void OnStarted(object? sender, CliInvocation invocation)
    {
        try
        {
            // A hand-off or a refusal is recorded already finished: nothing is running, and its completion would only undo this.
            if (!invocation.IsRunning)
            {
                return;
            }

            var counted = false;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (Grace == TimeSpan.Zero)
                {
                    counted = _inFlight.Add(invocation.Id);
                }
                else if (!_inFlight.Contains(invocation.Id) && !_pending.ContainsKey(invocation.Id))
                {
                    var id = invocation.Id;
                    _pending[id] = _time.CreateTimer(_ => Promote(id), null, Grace, Timeout.InfiniteTimeSpan);
                }
            }

            if (counted)
            {
                _post(RaiseIfChanged);
            }
        }
#pragma warning disable CA1031 // A status chip must never be the reason a command fails to start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"command activity: ignoring a start: {ex.Message}");
        }
    }

    /// <summary>The runner's event, raised once per invocation on a pool thread. Never throws into the runner.</summary>
    internal void OnCompleted(object? sender, CliInvocation invocation)
    {
        try
        {
            // An id never seen starting (finished before the seed, or never running) is simply not here.
            bool removed;
            ITimer? timer;
            lock (_lock)
            {
                removed = _inFlight.Remove(invocation.Id);
                _ = _pending.Remove(invocation.Id, out timer);
            }

            timer?.Dispose();

            if (removed)
            {
                _post(RaiseIfChanged);
            }
        }
#pragma warning disable CA1031 // A status chip must never be the reason a command fails to finish.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"command activity: ignoring a completion: {ex.Message}");
        }
    }

    /// <summary>The grace of a command ran out: it counts now, if it is still going.</summary>
    private void Promote(string id)
    {
        ITimer? timer = null;
        bool counted;
        lock (_lock)
        {
            if (_disposed || !_pending.Remove(id, out timer))
            {
                return;
            }

            counted = _inFlight.Add(id);
        }

        timer?.Dispose();
        if (counted)
        {
            _post(RaiseIfChanged);
        }
    }

    public void Dispose()
    {
        ITimer[] timers;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inFlight.Clear();
            timers = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        _cli.InvocationStarted -= OnStarted;
        _cli.InvocationCompleted -= OnCompleted;
        Changed = null;
    }

    /// <summary>Picks up the commands already running when this was created; one already past the grace counts at once.</summary>
    private void Seed()
    {
        foreach (var invocation in _cli.Activity)
        {
            if (!invocation.IsRunning)
            {
                continue;
            }

            lock (_lock)
            {
                var left = Grace - (_time.GetUtcNow() - invocation.StartedAt);
                if (left <= TimeSpan.Zero)
                {
                    _ = _inFlight.Add(invocation.Id);
                }
                else
                {
                    var id = invocation.Id;
                    _pending[id] = _time.CreateTimer(_ => Promote(id), null, left, Timeout.InfiniteTimeSpan);
                }
            }
        }

        lock (_lock)
        {
            _announced = _inFlight.Count;
        }
    }

    /// <summary>On the UI thread: tells the subscriber, unless it was already told what is true now.</summary>
    private void RaiseIfChanged()
    {
        EventHandler? handler;
        lock (_lock)
        {
            var count = _inFlight.Count;
            if (_disposed || count == _announced)
            {
                return;
            }

            _announced = count;
            handler = Changed;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // The subscriber is the status strip; its fault is traced, not thrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"command activity: a Changed subscriber threw: {ex}");
        }
    }
}
