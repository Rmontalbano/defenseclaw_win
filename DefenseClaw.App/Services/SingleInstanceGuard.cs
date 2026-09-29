using System.Threading;

namespace DefenseClaw.App.Services;

/// <summary>
/// Enforces one running shell per user session and lets a second launch raise the first
/// window instead of opening a rival tray icon.
/// <para>
/// A named mutex answers "am I first"; a named auto-reset event carries the "come to the
/// front" nudge. Both live in the <c>Local\</c> namespace, so a second desktop session
/// (RDP, fast user switching) gets its own instance — which is what an operator expects
/// from a per-user tray app.
/// </para>
/// <para>
/// <b>Elevation.</b> Objects created by an elevated process get a default ACL that a
/// non-elevated process of the same user cannot open: <c>new Mutex(...)</c> and
/// <c>OpenExisting</c> both throw <see cref="UnauthorizedAccessException"/>. That is not a
/// fault, it is proof that another instance exists — so it is read as "another instance owns
/// it" rather than allowed to escape <see cref="Acquire"/>, where (before the shell is ready)
/// it would end the process with a crash log for something that is not a crash. The second
/// launch then tries to nudge the first through the event; if that too is denied there is
/// nothing more it can do and it simply exits — the first instance's tray icon is still there.
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\DefenseClaw.App.SingleInstance";
    private const string ActivateEventName = @"Local\DefenseClaw.App.Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, EventWaitHandle? activate, bool isFirstInstance)
    {
        _mutex = mutex;
        _activate = activate;
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>False when another instance already owns the mutex.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when a later launch asks us to surface.</summary>
    public event EventHandler? ActivationRequested;

    public static SingleInstanceGuard Acquire()
    {
        Mutex? mutex;
        bool first;

        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out first);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and belongs to a process we may not open — an elevated first
            // instance. Someone else is running; this launch is the loser.
            return new SingleInstanceGuard(mutex: null, OpenActivateEvent(), isFirstInstance: false);
        }

        if (!first)
        {
            return new SingleInstanceGuard(mutex, OpenActivateEvent(), isFirstInstance: false);
        }

        // The first instance creates the event. If that is somehow denied (a stale event left
        // by an elevated process that already released the mutex) it keeps running without the
        // raise-the-window nudge, which is better than not running at all.
        EventWaitHandle? activate;
        try
        {
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (UnauthorizedAccessException)
        {
            activate = null;
        }

        return new SingleInstanceGuard(mutex, activate, isFirstInstance: true);
    }

    /// <summary>
    /// Opens the event the first instance listens on. Null when it does not exist (the first
    /// instance is already gone) or may not be opened (it is elevated and this process is not).
    /// It opens rather than creates: creating here would conjure an event nobody listens on.
    /// </summary>
    private static EventWaitHandle? OpenActivateEvent()
    {
        try
        {
            return EventWaitHandle.OpenExisting(ActivateEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>Starts watching for activation requests. Only meaningful on the first instance.</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || _activate is null || _listener is not null)
        {
            return;
        }

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "DefenseClaw single-instance listener",
        };

        _listener.Start();
    }

    /// <summary>
    /// Asks the already-running instance to show its window. Called by the loser. Does nothing
    /// when the event could not be opened — see the type documentation.
    /// </summary>
    public void SignalFirstInstance()
    {
        try
        {
            _ = _activate?.Set();
        }
        catch (ObjectDisposedException)
        {
            // The first instance exited between our mutex check and this signal.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();

        // Nudge the listener out of its blocking wait so the thread can retire. Only the
        // instance that is listening has a thread to wake: a losing launch setting the event
        // here would be heard by the first instance as a second, spurious activation request
        // right behind the real one SignalFirstInstance just sent.
        if (_listener is not null)
        {
            try
            {
                _ = _activate?.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (IsFirstInstance && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned; nothing to release.
            }
        }

        _activate?.Dispose();
        _mutex?.Dispose();
        _stop.Dispose();
    }

    private void ListenLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!_activate!.WaitOne())
                {
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (AbandonedMutexException)
            {
                continue;
            }

            if (_stop.IsCancellationRequested)
            {
                return;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
