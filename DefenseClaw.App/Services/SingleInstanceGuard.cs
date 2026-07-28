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
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\DefenseClaw.App.SingleInstance";
    private const string ActivateEventName = @"Local\DefenseClaw.App.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate, bool isFirstInstance)
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
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstanceGuard(mutex, activate, createdNew);
    }

    /// <summary>Starts watching for activation requests. Only meaningful on the first instance.</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || _listener is not null)
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

    /// <summary>Asks the already-running instance to show its window. Called by the loser.</summary>
    public void SignalFirstInstance()
    {
        try
        {
            _ = _activate.Set();
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

        // Nudge the listener out of its blocking wait so the thread can retire.
        try
        {
            _ = _activate.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        if (IsFirstInstance)
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

        _activate.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }

    private void ListenLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!_activate.WaitOne())
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
