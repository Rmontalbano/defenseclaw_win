using System.Diagnostics;
using System.Windows;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Services;

/// <summary>What <see cref="AlertCountsService.Changed"/> reports.</summary>
internal sealed class AlertCountsChangedEventArgs : EventArgs
{
    public AlertCountsChangedEventArgs(AlertCounts counts, AlertCounts previous, string? unavailable)
    {
        Counts = counts;
        Previous = previous;
        Unavailable = unavailable;
    }

    /// <summary>The counts now. The same object as <see cref="AlertCountsService.Current"/> at the moment of the change.</summary>
    public AlertCounts Counts { get; }

    /// <summary>The counts before.</summary>
    public AlertCounts Previous { get; }

    /// <summary>Why the counts could not be refreshed (they are then the last good ones), or null when they are current. See <see cref="AlertCountsService.Unavailable"/>.</summary>
    public string? Unavailable { get; }
}

/// <summary>
/// The app's one answer to "how many unacknowledged findings are there" (<see cref="AlertQueueReader"/>'s definition, the Mac's),
/// kept fresh for whoever is watching: the sidebar badge, the status-strip chip, the tray, the Alerts panel.
/// <para>
/// <b>Cadence.</b> While something is subscribed to <see cref="Changed"/>, the counts are re-read on the monitor's alert tick
/// (<see cref="GatewayMonitor.AlertCadenceElapsed"/>, every 30 s, the same as <c>/alerts</c>), once when the first subscriber attaches,
/// and whenever <see cref="RefreshAsync"/> is called — after an acknowledge, so the badge drops at once instead of half a minute
/// later. <b>While nothing is subscribed it does no work at all</b>: it is not attached to the monitor, nothing is scheduled, no
/// query runs; the last counts stay readable from <see cref="Current"/>. The last subscriber leaving also stops a read in flight.
/// </para>
/// <para>
/// <b>Change, not churn.</b> <see cref="Changed"/> is raised only when what a badge shows differs (<see cref="AlertCounts.SameAs"/>),
/// or the first data arrives, or the counts turn unavailable or recover. It is raised on the UI thread. A read that fails (the
/// database locked past its timeout, say) keeps the last good counts, sets <see cref="Unavailable"/> and is traced — it does not
/// zero the badge, which would read as "all clear".
/// </para>
/// <para>
/// <b>Old databases.</b> A database with no <c>bucket</c> column cannot tell findings from telemetry
/// (<see cref="AlertQueueStatus.LegacySchema"/>); the counts then come from the gateway's own <c>/alerts</c> list, the one the monitor
/// already fetched (<see cref="AlertCountsSource.Gateway"/>: at most its page size, so <see cref="AlertCounts.HasMore"/> is usually
/// set) — no extra request. If the gateway is not answering either, the counts are empty and <see cref="Unavailable"/> says why.
/// </para>
/// <para>
/// Scope them to a connector with <c>Current.TallyFor(connectorScope.Allows)</c> and re-read on <see cref="ConnectorScope.Changed"/> as well.
/// </para>
/// </summary>
internal sealed class AlertCountsService : IDisposable
{
    private readonly AlertQueueReader _reader;
    private readonly IGatewaySnapshotSource _source;
    private readonly int _gatewayPageSize;
    private readonly int _newestLimit;
    private readonly Action<Action> _post;

    /// <summary>Guards the state below and the subscriber list; never held across an <c>await</c> or a callback.</summary>
    private readonly object _lock = new();

    /// <summary>Serializes reads: one statement at a time, however many callers ask. See <see cref="RefreshAsync"/>.</summary>
    private readonly SemaphoreSlim _readGate = new(1, 1);

    private EventHandler<AlertCountsChangedEventArgs>? _changed;
    private CancellationTokenSource? _attached;
    private AlertCounts _current = AlertCounts.Empty;
    private string? _unavailable;
    private bool _hasData;
    private string? _lastTracedFailure;
    private bool _disposed;

    /// <summary>Refresh requests made / the highest request number a started read covers; see <see cref="RefreshAsync"/>.</summary>
    private long _requested;
    private long _covered;

    /// <param name="reader">The queue reader over <c>audit.db</c>.</param>
    /// <param name="source">The monitor: its alert tick, and its snapshot for the old-database fallback.</param>
    /// <param name="newestLimit">How many rows <see cref="AlertCounts.Newest"/> keeps.</param>
    /// <param name="gatewayPageSize">The page size the monitor requests <c>/alerts</c> with; what "the fallback list was full" is measured against.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    public AlertCountsService(
        AlertQueueReader reader,
        IGatewaySnapshotSource source,
        int newestLimit = AlertCounts.DefaultNewestLimit,
        int gatewayPageSize = GatewayMonitor.AlertLimit,
        Action<Action>? post = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _newestLimit = newestLimit;
        _gatewayPageSize = gatewayPageSize;
        _post = post ?? PostToDispatcher;
    }

    /// <summary>
    /// Raised on the UI thread when the counts change (see the type documentation). The first subscriber starts the service —
    /// it reads once at once and attaches to the monitor's tick — and the last one to leave stops it.
    /// </summary>
    public event EventHandler<AlertCountsChangedEventArgs>? Changed
    {
        add
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                var first = _changed is null;
                _changed += value;
                if (first)
                {
                    Attach();
                }
            }
        }

        remove
        {
            lock (_lock)
            {
                _changed -= value;
                if (_changed is null && _attached is not null)
                {
                    Detach();
                }
            }
        }
    }

    /// <summary>The last counts read; <see cref="AlertCounts.Empty"/> until <see cref="HasData"/>. Readable from any thread, subscribed or not.</summary>
    public AlertCounts Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>True once a read has completed (so zero counts mean "nothing waiting", not "not read yet").</summary>
    public bool HasData
    {
        get
        {
            lock (_lock)
            {
                return _hasData;
            }
        }
    }

    /// <summary>Why the last refresh could not produce counts (<see cref="Current"/> is then the last good ones, or empty), or null when they are current.</summary>
    public string? Unavailable
    {
        get
        {
            lock (_lock)
            {
                return _unavailable;
            }
        }
    }

    /// <summary>True while the service is running: there is a subscriber, and it is attached to the monitor's tick.</summary>
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

    /// <summary>
    /// Reads the queue now, whether or not anything is subscribed, and publishes the result: what to call after an acknowledge so
    /// the badge follows at once. Calls made while a read runs are folded together — the next read covers them all — so ten
    /// acknowledgements in a burst cost one more query, and every caller's task completes only after a read that started after its call.
    /// A failure is recorded in <see cref="Unavailable"/>, not thrown; only the caller's own cancellation throws.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var ticket = Interlocked.Increment(ref _requested);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A read that started after this call was made, and finished, already reflects it.
            if (Volatile.Read(ref _covered) >= ticket)
            {
                return;
            }

            // Everything requested so far is covered by the read about to start — but only once it has finished: one cut short
            // by the last subscriber leaving (it throws) covers nobody, and the next caller in line reads for itself.
            var covers = Volatile.Read(ref _requested);
            await ReadAndPublishAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _covered, covers);
        }
        finally
        {
            _ = _readGate.Release();
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
            _changed = null;
            if (_attached is not null)
            {
                Detach();
            }
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>First subscriber: listen for the tick and read once now. Callers hold <c>_lock</c>.</summary>
    private void Attach()
    {
        _attached = new CancellationTokenSource();
        _source.AlertCadenceElapsed += OnAlertCadence;
        StartRefresh(_attached.Token);
    }

    /// <summary>Last subscriber gone: stop listening and stop a read in flight. Callers hold <c>_lock</c>.</summary>
    private void Detach()
    {
        _source.AlertCadenceElapsed -= OnAlertCadence;
        var attached = _attached;
        _attached = null;
        attached?.Cancel();
        attached?.Dispose();
    }

    private void OnAlertCadence(object? sender, GatewaySnapshotEventArgs e)
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

        StartRefresh(token);
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
                // The last subscriber left while this waited or ran: nothing to publish.
            }
#pragma warning disable CA1031 // A background refresh that throws has nobody to tell; it must not become an unobserved task exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"alert counts: refresh failed: {ex}");
            }
        });

    // ------------------------------------------------------------------ reading

    private async Task ReadAndPublishAsync(CancellationToken cancellationToken)
    {
        AlertCounts counts;
        string? unavailable = null;

        try
        {
            var result = await _reader.ReadAsync(_newestLimit, timeout: null, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case AlertQueueStatus.LegacySchema:
                    (counts, unavailable) = FromGateway(_source.Current);
                    break;

                case AlertQueueStatus.NoDatabase:
                    counts = AlertCounts.Empty;
                    break;

                default:
                    counts = result.Counts;
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A database that cannot be read this time (locked, timed out, corrupt) is "unavailable", not a crash, and not "zero alerts".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TraceFailure(ex);
            Publish(counts: null, $"The alert queue could not be read: {ex.Message}".ReplaceLineEndings(" "), cancellationToken);
            return;
        }

        Publish(counts, unavailable, cancellationToken);
    }

    /// <summary>The fallback for a database with no bucket column: the gateway's list, if the gateway is answering.</summary>
    private (AlertCounts Counts, string? Unavailable) FromGateway(GatewaySnapshot snapshot) =>
        snapshot.AlertsUnavailable is { } why
            ? (AlertCounts.Empty, why)
            : (AlertCounts.FromGateway(snapshot.RecentAlerts, _gatewayPageSize, _newestLimit), null);

    /// <summary>
    /// Stores the result and raises <see cref="Changed"/> if it differs. A null <paramref name="counts"/> is a failed read: the last
    /// good counts stay and only <see cref="Unavailable"/> changes. A result that arrives after the last subscriber left is dropped.
    /// </summary>
    private void Publish(AlertCounts? counts, string? unavailable, CancellationToken cancellationToken)
    {
        AlertCountsChangedEventArgs? args = null;

        lock (_lock)
        {
            // A read started for a subscriber that has since left must not revive anything. (An on-demand read, which has no
            // subscriber lifetime, carries its caller's token and is not cancelled by that.)
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var previous = _current;
            var next = counts ?? previous;

            var firstData = counts is not null && !_hasData;
            var changed = firstData ||
                          !next.SameAs(previous) ||
                          !string.Equals(unavailable, _unavailable, StringComparison.Ordinal);

            _current = next;
            _hasData = _hasData || counts is not null;
            _unavailable = unavailable;
            if (counts is not null)
            {
                _lastTracedFailure = null;
            }

            if (changed && _changed is not null)
            {
                args = new AlertCountsChangedEventArgs(next, previous, unavailable);
            }
        }

        if (args is not null)
        {
            _post(() => Raise(args));
        }
    }

    /// <summary>On the UI thread. Reads the subscribers now, so one that left while this was queued is not called.</summary>
    private void Raise(AlertCountsChangedEventArgs args)
    {
        EventHandler<AlertCountsChangedEventArgs>? handlers;
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
                ((EventHandler<AlertCountsChangedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // One misbehaving subscriber must not silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"alert counts: a Changed subscriber threw: {ex}");
            }
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

        Trace.TraceWarning($"alert counts: the alert queue could not be read: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// The application's dispatcher, the way <c>AppServices.RaiseConfigReloaded</c> does it: in place on the UI thread (and when there
    /// is no WPF application, as in a headless run), queued otherwise, and nothing once shutdown has begun.
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
