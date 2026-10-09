using System.Diagnostics;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// How many commands this app has in flight right now: what the status strip's Running chip reads. The TUI's strip says "running" while its
/// executor has a command going; this is the same for the one runner every command of this app goes through.
/// <para>
/// <b>What it sees.</b> Everything <see cref="CliRunner"/> runs, whoever asked and whatever it is - a scan, a read, a gateway verb, a wizard step,
/// a probe a panel makes on opening (the runner raises <see cref="CliRunner.InvocationStarted"/> and, exactly once per invocation however it ends,
/// <see cref="CliRunner.InvocationCompleted"/>). That is the difference from <see cref="ScanActivity"/>, which counts only the CLI's scans for the
/// tray's shield and is built the same way. A hand-off (a command the app only copies to a console, <see cref="CliRunner.RecordHandOff"/>) and a
/// refusal (<see cref="CliRunner.RecordRefusal"/>) are born finished and never count. What the app does not see, it cannot show: a command typed
/// in a terminal, or work the gateway does by itself.
/// </para>
/// <para>
/// <b>Cost.</b> No timer and no polling: two event subscriptions and a set of the ids in flight (empty almost always). <see cref="Changed"/> is
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

    /// <summary>The ids of the commands in flight.</summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>The count <see cref="Changed"/> last announced.</summary>
    private int _announced;

    private bool _disposed;

    /// <param name="cli">The runner whose invocations are watched.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    public CommandActivity(CliRunner cli, Action<Action>? post = null)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _post = post ?? UiPost.ToDispatcher;

        // Subscribed first, then seeded: a command that starts in between is in the set once (it is a set), and one that finishes in between is
        // skipped by the seed (FinishedAt is set before InvocationCompleted is raised) or removed by its completion.
        _cli.InvocationStarted += OnStarted;
        _cli.InvocationCompleted += OnCompleted;
        Seed();
    }

    /// <summary>Raised on the UI thread when <see cref="Count"/> is not what was last announced (the first command began, a second joined it, the last ended).</summary>
    public event EventHandler? Changed;

    /// <summary>How many commands are in flight. Readable from any thread.</summary>
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

            bool added;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                added = _inFlight.Add(invocation.Id);
            }

            if (added)
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
            lock (_lock)
            {
                removed = _inFlight.Remove(invocation.Id);
            }

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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inFlight.Clear();
        }

        _cli.InvocationStarted -= OnStarted;
        _cli.InvocationCompleted -= OnCompleted;
        Changed = null;
    }

    /// <summary>Picks up the commands already running when this was created.</summary>
    private void Seed()
    {
        foreach (var invocation in _cli.Activity)
        {
            if (invocation.IsRunning)
            {
                lock (_lock)
                {
                    _ = _inFlight.Add(invocation.Id);
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
