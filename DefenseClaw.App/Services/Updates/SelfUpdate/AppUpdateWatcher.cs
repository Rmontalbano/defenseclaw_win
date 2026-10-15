using System.Diagnostics;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>
/// Knows whether a newer DefenseClaw for Windows is out: it checks shortly after launch and every six hours, and the shell shows the
/// answer as a banner and one toast per release. The twin of <see cref="UpdateWatcher"/> for this app's own repository; the two do not
/// share state. <b>It only asks.</b> Installing is the reviewed flow behind the banner's Update now.
/// <para>
/// A development build (<c>-dev</c>) never offers (<see cref="AppUpdateState.DevBuild"/>). "Later" hides the banner for this run only;
/// "Skip this version" is remembered (<see cref="SelfUpdateStore"/>) and a newer release shows the banner again.
/// </para>
/// </summary>
internal sealed class AppUpdateWatcher : IDisposable
{
    /// <summary>The wait before the launch check, so it never competes with the window and the tray for the first seconds.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly Func<bool, CancellationToken, Task<AppUpdateCheck>> _check;
    private readonly SelfUpdateStore _store;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Action> _post;
    private readonly Action? _onDispose;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private AppUpdateCheck _latest = AppUpdateCheck.NotChecked;
    private Task<AppUpdateCheck>? _inFlight;
    private string? _snoozed;
    private int _failures;
    private bool _started;
    private bool _disposed;
    private (bool Show, string? Version) _published = (false, null);

    public AppUpdateWatcher(
        Func<bool, CancellationToken, Task<AppUpdateCheck>> check,
        SelfUpdateStore store,
        Action? onDispose = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onDispose = onDispose;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _post = post ?? UpdateWatcher.PostToDispatcher;
    }

    /// <summary>Raised on the UI thread when the banner's content changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread once per release (remembered across runs): the tray toast.</summary>
    public event EventHandler<string>? Announced;

    /// <summary>Raised on the UI thread after every check (also a manual one), whatever it found: the Settings status follows it.</summary>
    public event EventHandler? Checked;

    public AppUpdateCheck Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>A newer installable release is known, skipped or not.</summary>
    public SelfUpdateRelease? Available => Latest is { IsUpdateAvailable: true, Release: { } release } ? release : null;

    public bool IsSkipped => Available is { } release && IsSameVersion(_store.Current.SkippedVersion, release.Version);

    /// <summary>The banner shows: an update is available, not skipped, not postponed for this run.</summary>
    public bool ShowBanner => Available is { } release && !IsSkipped && !string.Equals(Volatile.Read(ref _snoozed), release.Version.ToString(), StringComparison.Ordinal);

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Checks now. A manual check asks GitHub whatever the throttle says. A check already running is joined.</summary>
    public Task<AppUpdateCheck> CheckNowAsync(bool manual, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.FromResult(_latest);
            }

            _inFlight ??= Task.Run(() => RunCheckAsync(manual));
            return _inFlight.WaitAsync(cancellationToken);
        }
    }

    /// <summary>"Later": hides the banner until the app is restarted.</summary>
    public void Later()
    {
        if (Available is { } release)
        {
            Volatile.Write(ref _snoozed, release.Version.ToString());
            PublishIfChanged();
        }
    }

    /// <summary>"Skip this version": remembered; a newer release offers again.</summary>
    public void Skip()
    {
        if (Available is { } release)
        {
            var version = release.Version.ToString();
            _ = _store.Update(s => s with { SkippedVersion = version });
            PublishIfChanged();
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
        }

        Changed = null;
        Announced = null;
        Checked = null;
        _stop.Cancel();
        _onDispose?.Invoke();
    }

    private static bool IsSameVersion(string? stored, AppVersion version) => AppVersion.TryParse(stored, out var parsed) && parsed.CompareTo(version) == 0;

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await _delay(StartupDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                _ = await CheckNowAsync(manual: false, token).ConfigureAwait(false);
                var wait = UpdateWatcher.NextDelay(_time.GetUtcNow(), _store.Current.LastCheckUnix, _failures);
                await _delay(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
#pragma warning disable CA1031 // Nobody is above the loop: a fault ends it quietly.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"app update watcher: the loop stopped: {ex}");
        }
    }

    private async Task<AppUpdateCheck> RunCheckAsync(bool manual)
    {
        try
        {
            AppUpdateCheck result;
            try
            {
                result = await _check(manual, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return Latest;
            }
#pragma warning disable CA1031 // A check that throws is a failed check; the previous answer stays.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning($"app update watcher: the check threw {ex.GetType().Name}: {ex.Message}");
                result = new AppUpdateCheck { State = AppUpdateState.Failed, Message = $"The update check failed ({ex.GetType().Name}).", LiveFailed = true };
            }

            var announce = Apply(result);
            PublishIfChanged();
            _post(() => Checked?.Invoke(this, EventArgs.Empty));
            if (announce is not null)
            {
                _post(() => Announced?.Invoke(this, announce));
            }

            return result;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = null;
            }
        }
    }

    /// <summary>Takes a result in. A failed look keeps the last good answer when it had one; returns the version to announce, if any.</summary>
    private string? Apply(AppUpdateCheck result)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return null;
            }

            var keep = result.State == AppUpdateState.Failed && result.Release is null && _latest.State is not AppUpdateState.Unknown and not AppUpdateState.Failed;
            _latest = keep ? _latest with { Message = result.Message, LiveFailed = true } : result;
            _failures = result.LiveFailed || result.State == AppUpdateState.Failed ? _failures + 1 : 0;
        }

        if (!ShowBanner || Available is not { } release)
        {
            return null;
        }

        var version = release.Version.ToString();
        if (IsSameVersion(_store.Current.NotifiedVersion, release.Version))
        {
            return null;
        }

        _ = _store.Update(s => s with { NotifiedVersion = version });
        return version;
    }

    private void PublishIfChanged()
    {
        var now = (ShowBanner, Available?.Version.ToString());
        lock (_lock)
        {
            if (_disposed || now == _published)
            {
                return;
            }

            _published = now;
        }

        _post(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
