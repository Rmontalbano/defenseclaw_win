using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using DefenseClaw.App.Services.Settings;

namespace DefenseClaw.App.Services.Updates;

/// <summary>What <see cref="UpdateWatcher.NewVersionAvailable"/> reports: the release to tell the operator about, once.</summary>
internal sealed class UpdateAnnouncedEventArgs : EventArgs
{
    public UpdateAnnouncedEventArgs(string version, string? releaseUrl)
    {
        Version = version;
        ReleaseUrl = releaseUrl;
    }

    /// <summary>The release version without a leading <c>v</c> (<c>0.8.11</c>).</summary>
    public string Version { get; }

    /// <summary>The release page, already vetted by <see cref="UpdateChecker.TrustedRepoUrl"/>; null when the release carried none that passed.</summary>
    public string? ReleaseUrl { get; }

    /// <summary>The sentence for the tray toast, the same words as the banner's title.</summary>
    public string Text => UpdateWatcher.Announcement(Version);
}

/// <summary>
/// Knows whether a newer DefenseClaw runtime is out, without the operator opening the Updates window (the Mac's background
/// update check, <c>AppState.checkForUpdates</c>): it asks at launch and every <see cref="Interval"/> after, and the shell shows
/// the answer as a banner and, once per release, a tray toast.
/// <para>
/// <b>It only asks; it never upgrades.</b> Every check goes through <see cref="UpdateChecker.CheckAsync"/> and keeps what that
/// enforces — the 24 h cache file (so the six-hourly tick is mostly a file read, and GitHub is asked about once a day), the
/// rate-limit handling with its stale-cache fallback, and the link vetting. The watcher never forces a refresh by itself.
/// The upgrade stays the reviewed, hash-verified flow in the Updates window.
/// </para>
/// <para>
/// <b>Off the startup path.</b> <see cref="Start"/> returns at once; the loop runs on the pool. Its first act is to wait (bounded
/// by <see cref="StartupPollWait"/>) for the gateway monitor's first poll, because that poll carries the installed version: asked
/// before it, the checker would run <c>defenseclaw --version-json</c> for it. A tray-only autostart therefore pays nothing for
/// this at launch beyond one object, and nothing at all until its loop gets going.
/// </para>
/// <para>
/// <b>A failed lookup keeps the previous answer.</b> <see cref="Latest"/> only ever moves to a result that compared two versions;
/// "GitHub was unreachable", "rate limited" and "could not read the installed version" leave it (and so the banner) as it was. A
/// failed check is retried sooner than the normal interval (<see cref="NextDelay"/>: 5, 15, 45 … minutes, capped at the interval),
/// since an autostart at sign-in often runs before the network is up.
/// </para>
/// <para>
/// <b>Persisted.</b> The time of the last check that got an answer, the version the operator dismissed and the version last
/// announced with a toast live in the <c>updates</c> settings section. The launch check always runs (a stale throttle must not
/// hide an update, as on the Mac); the stamp sets when the next periodic one is due, so the cadence is the same across restarts.
/// </para>
/// <para>
/// <b>Threads.</b> Checks run on the pool. <see cref="Changed"/> and <see cref="NewVersionAvailable"/> are raised on the UI
/// thread; everything readable is safe from any thread.
/// </para>
/// </summary>
internal sealed class UpdateWatcher : IDisposable
{
    /// <summary>How often the release is re-checked while the app runs (the Mac's "every 6 h").</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>The wait after the first failed check; each further failure triples it, up to <see cref="Interval"/>.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(5);

    /// <summary>The shortest wait between two periodic checks, however the stamp reads (a stamp in the past must not make a busy loop).</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromMinutes(1);

    /// <summary>How long the launch check waits for the monitor's first poll before going ahead without the gateway's version.</summary>
    public static readonly TimeSpan StartupPollWait = TimeSpan.FromSeconds(20);

    private readonly AppSettingsStore _settings;
    private readonly IGatewaySnapshotSource _source;
    private readonly Func<bool, CancellationToken, Task<UpdateCheckResult>> _check;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Action> _post;
    private readonly Action? _onDispose;

    /// <summary>Guards the state below; never held across an <c>await</c>, a callback or a settings write.</summary>
    private readonly object _lock = new();

    private readonly CancellationTokenSource _stop = new();
    private UpdateCheckResult _latest = UpdateCheckResult.NotCheckedYet;
    private Task<UpdateCheckResult>? _inFlight;
    private BannerState _published = BannerState.None;
    private string? _lastInstalled;
    private string? _lastFailure;
    private int _failures;
    private bool _started;
    private bool _disposed;

    /// <param name="settings">Where the last-check stamp and the dismissed/announced versions are kept.</param>
    /// <param name="source">The gateway monitor: the first poll (which carries the installed version) and any later change of it.</param>
    /// <param name="check">One release check: <c>UpdateChecker.CheckAsync</c>, or a fake. Its argument is "force a live fetch"; the watcher always passes false.</param>
    /// <param name="onDispose">Releases whatever <paramref name="check"/> owns.</param>
    /// <param name="time">The wall clock for the stamp; the system's when null.</param>
    /// <param name="delay">Waits (the loop's sleeps); <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null. A test passes one it releases by hand.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    public UpdateWatcher(
        AppSettingsStore settings,
        IGatewaySnapshotSource source,
        Func<bool, CancellationToken, Task<UpdateCheckResult>> check,
        Action? onDispose = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _onDispose = onDispose;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _post = post ?? PostToDispatcher;
    }

    /// <summary>
    /// The watcher for the running app: the live <see cref="UpdateChecker"/> (its HTTP client is made on the first check, not here,
    /// so composing the app costs nothing), over the app's settings and monitor. Nothing runs until <see cref="Start"/>.
    /// </summary>
    internal static UpdateWatcher Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var live = new LiveCheck(services);
        return new UpdateWatcher(services.Settings, services.Monitor, live.CheckAsync, live.Dispose);
    }

    /// <summary>
    /// Raised on the UI thread when what the banner shows changes: an update turned up or went away, a newer one replaced it, the
    /// operator dismissed it. Read <see cref="ShowBanner"/> and friends in the handler.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised on the UI thread, once per release version, when a check finds a release newer than the installed one that the
    /// operator has neither dismissed nor been told about before (the tray toast). Remembered across restarts.
    /// </summary>
    public event EventHandler<UpdateAnnouncedEventArgs>? NewVersionAvailable;

    /// <summary>
    /// The last check that compared two versions (<see cref="UpdateCheckResult.NotCheckedYet"/> until one has). A failed lookup does
    /// not replace it.
    /// </summary>
    public UpdateCheckResult Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>True when <see cref="Latest"/> says a newer release is published — whether or not the operator dismissed it.</summary>
    public bool UpdateAvailable => AvailableVersion is not null;

    /// <summary>The newer release's version without a leading <c>v</c> (<c>0.8.11</c>), or null when none is known.</summary>
    public string? AvailableVersion
    {
        get
        {
            lock (_lock)
            {
                return _latest.IsUpdateAvailable ? DisplayVersion(_latest.LatestVersion) : null;
            }
        }
    }

    /// <summary>True when the operator dismissed <see cref="AvailableVersion"/> (a newer release is not dismissed).</summary>
    public bool IsDismissed => AvailableVersion is { } version && SameVersion(version, _settings.Current.Updates.DismissedVersion);

    /// <summary>What the banner follows: a newer release is out and the operator has not dismissed it.</summary>
    public bool ShowBanner => UpdateAvailable && !IsDismissed;

    /// <summary>The release page for <see cref="Latest"/>, or null when it has none that is in this repository (<see cref="UpdateChecker.TrustedRepoUrl"/>).</summary>
    public string? ReleaseUrl => UpdateChecker.TrustedRepoUrl(Latest.HtmlUrl);

    /// <summary>The installed version <see cref="Latest"/> was compared against, or null when unknown.</summary>
    public string? InstalledVersion => Latest.InstalledVersion;

    /// <summary>True when the most recent check could not get a fresh answer (so <see cref="Latest"/> may be older than the last attempt).</summary>
    public bool LastCheckFailed
    {
        get
        {
            lock (_lock)
            {
                return _lastFailure is not null;
            }
        }
    }

    /// <summary>Why the most recent check failed, or null when it did not.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_lock)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>
    /// Begins watching: one background task that waits for the gateway's first poll, checks, and then checks every
    /// <see cref="Interval"/> (sooner after a failure). Returns at once and does no work on the caller's thread; a second call does
    /// nothing. Also starts following the monitor, so an upgrade that changes the installed version re-evaluates the banner.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _lastInstalled = Normalized(_source.Current.BinaryVersion);
        }

        _source.StateChanged += OnMonitorStateChanged;
        _settings.Changed += OnSettingsChanged;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>
    /// Checks now, through the same path and the same cache as the periodic check, and publishes the result. A check already running
    /// is joined, not repeated. Never throws for a failed check (<see cref="Latest"/> is kept and <see cref="LastFailure"/> says why);
    /// only <paramref name="cancellationToken"/> cancels the wait, not the check itself.
    /// </summary>
    /// <param name="forceRefresh">Skip the 24 h cache and ask GitHub — for an explicit "check again" only; nothing automatic passes it.</param>
    public Task<UpdateCheckResult> CheckNowAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.FromResult(_latest);
            }

            // Task.Run so the check's own completion, which clears _inFlight under this lock, can only run after the assignment.
            _inFlight ??= Task.Run(() => RunCheckAsync(forceRefresh));
            return _inFlight.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The operator dismisses the banner for the release it names: remembered across restarts, and a newer release shows it again.
    /// Does nothing when no update is available.
    /// </summary>
    public void Dismiss()
    {
        if (AvailableVersion is not { } version)
        {
            return;
        }

        _ = _settings.Update(s => s with { Updates = s.Updates with { DismissedVersion = version } });
        PublishIfChanged();
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

        _source.StateChanged -= OnMonitorStateChanged;
        _settings.Changed -= OnSettingsChanged;
        Changed = null;
        NewVersionAvailable = null;
        _stop.Cancel();
        _onDispose?.Invoke();
    }

    // ------------------------------------------------------------------ the pure parts

    /// <summary>The banner's title and the toast's text: what is announced for <paramref name="version"/>.</summary>
    public static string Announcement(string version) => $"DefenseClaw {version} is available";

    /// <summary>A release tag as it is shown: trimmed, without a leading <c>v</c> (<c>v0.8.11</c> is <c>0.8.11</c>); null for a blank one.</summary>
    internal static string? DisplayVersion(string? tag)
    {
        var trimmed = tag?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if ((trimmed[0] is 'v' or 'V') && trimmed.Length > 1)
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    /// <summary>True when two versions are the same release however each is spelled (<c>v0.8.11</c>, <c>0.8.11</c>); blank is never the same as anything.</summary>
    internal static bool SameVersion(string? a, string? b) =>
        DisplayVersion(a) is { } left && DisplayVersion(b) is { } right && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How long to wait before the next check. After <paramref name="failures"/> failures in a row: <see cref="FirstRetry"/>, three
    /// times that, nine times … never more than <see cref="Interval"/>. Otherwise what is left of <see cref="Interval"/> since the
    /// persisted last-check stamp, kept between <see cref="MinimumWait"/> and <see cref="Interval"/> (a stamp in the future, from a
    /// clock that was set back, therefore waits one interval, not more).
    /// </summary>
    internal static TimeSpan NextDelay(DateTimeOffset now, long lastCheckUnix, int failures)
    {
        if (failures > 0)
        {
            var retry = FirstRetry;
            for (var i = 1; i < failures && retry < Interval; i++)
            {
                retry *= 3;
            }

            return retry < Interval ? retry : Interval;
        }

        if (lastCheckUnix <= 0 || lastCheckUnix > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return Interval;
        }

        var remaining = DateTimeOffset.FromUnixTimeSeconds(lastCheckUnix) + Interval - now;
        return remaining < MinimumWait ? MinimumWait : remaining > Interval ? Interval : remaining;
    }

    /// <summary>
    /// Opens a release page in the browser — but only one <see cref="UpdateChecker.TrustedRepoUrl"/> accepts (https, github.com, this
    /// repository): the link came from GitHub's JSON or a cache file, and <c>UseShellExecute</c> hands whatever it is to the shell.
    /// False when it was refused or nothing could open it.
    /// </summary>
    /// <param name="launch">Opens the address; the shell's default handler when null. A test passes a recorder.</param>
    internal static bool TryOpenReleasePage(string? url, Action<string>? launch = null)
    {
        if (UpdateChecker.TrustedRepoUrl(url) is not { } trusted)
        {
            return false;
        }

        try
        {
            (launch ?? LaunchInBrowser)(trusted);
            return true;
        }
        catch (Win32Exception)
        {
            // No handler registered for the URL, or the shell prompt was dismissed.
            return false;
        }
        catch (InvalidOperationException)
        {
            // No default browser associated with http(s) on this machine.
            return false;
        }
    }

    private static void LaunchInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    // ------------------------------------------------------------------ the loop

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await WaitForFirstPollAsync(token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                _ = await CheckNowAsync(forceRefresh: false, token).ConfigureAwait(false);

                TimeSpan wait;
                lock (_lock)
                {
                    wait = NextDelay(_time.GetUtcNow(), _settings.Current.Updates.LastCheckUnix, _failures);
                }

                await _delay(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
#pragma warning disable CA1031 // The loop has nobody above it: a fault here must end the loop quietly, not become an unobserved task exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"update watcher: the loop stopped: {ex}");
        }
    }

    /// <summary>
    /// Returns once the monitor has published a real poll (its snapshot then carries the installed version, so the checker need not
    /// run the CLI for it) or <see cref="StartupPollWait"/> has passed, whichever is first.
    /// </summary>
    private async Task WaitForFirstPollAsync(CancellationToken token)
    {
        static bool Polled(GatewaySnapshot snapshot) => snapshot.PolledAt != DateTimeOffset.MinValue;

        if (Polled(_source.Current))
        {
            return;
        }

        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPolled(object? sender, GatewaySnapshotEventArgs e)
        {
            if (Polled(e.Snapshot))
            {
                _ = polled.TrySetResult();
            }
        }

        _source.StateChanged += OnPolled;
        try
        {
            if (Polled(_source.Current))
            {
                return;
            }

            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timeout = _delay(StartupPollWait, giveUp.Token);
            _ = await Task.WhenAny(polled.Task, timeout).ConfigureAwait(false);

            await giveUp.CancelAsync().ConfigureAwait(false);
            try
            {
                await timeout.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // The wait was ended on purpose.
            }
        }
        finally
        {
            _source.StateChanged -= OnPolled;
        }
    }

    // ------------------------------------------------------------------ one check

    private async Task<UpdateCheckResult> RunCheckAsync(bool forceRefresh)
    {
        try
        {
            UpdateCheckResult result;
            try
            {
                result = await _check(forceRefresh, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Disposed mid-check: nothing to publish to.
                return Latest;
            }
#pragma warning disable CA1031 // A check that throws is a failed check; the previous answer stays.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning($"update watcher: the check threw {ex.GetType().Name}: {ex.Message}");
                result = new UpdateCheckResult
                {
                    State = UpdateCheckState.CheckFailed,
                    ErrorMessage = $"The update check failed ({ex.GetType().Name}).",
                    Detail = ex.Message,
                    CheckedAt = _time.GetUtcNow(),
                };
            }

            Apply(result);
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

    /// <summary>
    /// Takes a result in. A version comparison replaces <see cref="Latest"/>; a failed lookup does not. Only a result the source
    /// really answered (not one served from a stale cache after a failed fetch) counts as a check for the stamp and ends a streak of failures.
    /// </summary>
    private void Apply(UpdateCheckResult result)
    {
        var compared = result.State is UpdateCheckState.UpToDate or UpdateCheckState.UpdateAvailable;
        var answered = compared && result.ErrorMessage is null;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (compared)
            {
                _latest = result;
            }

            _lastFailure = answered ? null : (result.ErrorMessage ?? result.Detail);
            _failures = answered ? 0 : _failures + 1;
        }

        if (answered)
        {
            var stamp = _time.GetUtcNow().ToUnixTimeSeconds();
            _ = _settings.Update(s => s with { Updates = s.Updates with { LastCheckUnix = stamp } });
        }

        var announcement = TakeAnnouncement();
        PublishIfChanged();

        if (announcement is not null)
        {
            _post(() => RaiseAnnounced(announcement));
        }
    }

    /// <summary>
    /// The toast to show for the current answer, or null: only a release that is newer, not dismissed and not announced before. Taking
    /// it records the version as announced, so it is returned once for a release however many checks see it (and across restarts).
    /// </summary>
    private UpdateAnnouncedEventArgs? TakeAnnouncement()
    {
        if (AvailableVersion is not { } version)
        {
            return null;
        }

        var updates = _settings.Current.Updates;
        if (SameVersion(version, updates.DismissedVersion) || SameVersion(version, updates.NotifiedVersion))
        {
            return null;
        }

        _ = _settings.Update(s => s with { Updates = s.Updates with { NotifiedVersion = version } });
        return new UpdateAnnouncedEventArgs(version, ReleaseUrl);
    }

    // ------------------------------------------------------------------ following the world

    /// <summary>
    /// The installed version moved (an upgrade finished and the gateway came back as the new build): ask again, from the cache, so
    /// the banner for the release that was just installed goes away without waiting for the next tick. On the UI thread, so it only
    /// compares two strings and hands the rest to the pool.
    /// </summary>
    private void OnMonitorStateChanged(object? sender, GatewaySnapshotEventArgs e)
    {
        var installed = Normalized(e.Snapshot.BinaryVersion);
        if (installed is null)
        {
            return;
        }

        bool moved;
        lock (_lock)
        {
            moved = _lastInstalled is not null && !SameVersion(_lastInstalled, installed);
            _lastInstalled = installed;
        }

        if (moved)
        {
            _ = RecheckAfterUpgradeAsync();
        }
    }

    /// <summary>
    /// The check for a moved version. It must not join a check already running: that one publishes its banner a moment before it
    /// clears <see cref="_inFlight"/>, so a version change arriving in between would get the old answer back and the banner for the
    /// release just installed would stay until the next tick. Wait for it, then ask.
    /// </summary>
    private async Task RecheckAfterUpgradeAsync()
    {
        Task<UpdateCheckResult>? running;
        lock (_lock)
        {
            running = _inFlight;
        }

        if (running is not null)
        {
            _ = await running.ConfigureAwait(false);
        }

        _ = await CheckNowAsync().ConfigureAwait(false);
    }

    /// <summary>Another writer changed the updates section (a future settings page resetting the dismissal): the banner follows.</summary>
    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (e.Affects(AppSettingsSections.Updates))
        {
            PublishIfChanged();
        }
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>What the banner renders, so <see cref="Changed"/> is raised for a difference in it and for nothing else.</summary>
    private sealed record BannerState(bool Show, string? Version, string? Url, string? Installed)
    {
        public static readonly BannerState None = new(false, null, null, null);
    }

    private BannerState CaptureBannerState()
    {
        if (!ShowBanner || AvailableVersion is not { } version)
        {
            return BannerState.None;
        }

        return new BannerState(true, version, ReleaseUrl, InstalledVersion);
    }

    private void PublishIfChanged()
    {
        var state = CaptureBannerState();
        lock (_lock)
        {
            if (_disposed || state == _published)
            {
                return;
            }

            _published = state;
        }

        _post(RaiseChanged);
    }

    /// <summary>On the UI thread.</summary>
    private void RaiseChanged()
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
#pragma warning disable CA1031 // One misbehaving subscriber must not silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"update watcher: a Changed subscriber threw: {ex}");
            }
        }
    }

    /// <summary>On the UI thread.</summary>
    private void RaiseAnnounced(UpdateAnnouncedEventArgs args)
    {
        var handlers = NewVersionAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<UpdateAnnouncedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // One misbehaving subscriber must not silence the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"update watcher: a NewVersionAvailable subscriber threw: {ex}");
            }
        }
    }

    private static string? Normalized(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();

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

    // ------------------------------------------------------------------ the live check

    /// <summary>
    /// <see cref="UpdateChecker"/> over its own HTTP client, made the first time a check runs. The 24 h cache file, the rate-limit
    /// handling and the link vetting are the checker's; this only owns its lifetime.
    /// </summary>
    private sealed class LiveCheck : IDisposable
    {
        private readonly AppServices _services;
        private readonly object _gate = new();
        private UpdateChecker? _checker;
        private bool _disposed;

        public LiveCheck(AppServices services) => _services = services;

        public Task<UpdateCheckResult> CheckAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Checker().CheckAsync(forceRefresh, cancellationToken);

        private UpdateChecker Checker()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _checker ??= new UpdateChecker(_services, UpdateChecker.CreateHttpClient(), ownsHttpClient: true);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _checker?.Dispose();
                _checker = null;
            }
        }
    }
}
