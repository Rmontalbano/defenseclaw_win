using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>
/// Updating DefenseClaw for Windows itself, end to end: the background check (<see cref="Watcher"/>), the reviewed install
/// (<see cref="PrepareReviewAsync"/> then <see cref="InstallAsync"/>) and the clean-up on the next start (<see cref="CompletePendingUpdateAsync"/>).
/// <para>
/// Separate from <see cref="UpdateWatcher"/> / <see cref="UpdateChecker"/>, which look at DefenseClaw's own releases; this looks at
/// <c>Rmontalbano/defenseclaw_win</c>. <b>Never automatic</b>: the check offers, the operator reviews (version, size, SHA-256, the trust
/// sentence) and confirms, and only then are files downloaded and replaced.
/// </para>
/// <para>
/// The restart goes through <see cref="AppRestart"/> (the same exit checks, the same <c>--after-restart</c> wait) with an
/// <see cref="UpdateHandoff"/>: the old copy does not exit until the new one has said it started, and puts its own files back and stays up
/// when it does not.
/// </para>
/// </summary>
internal sealed class SelfUpdateService : IDisposable
{
    private readonly SelfUpdateInstaller? _installer;
    private readonly AppRestart _restart;
    private readonly AppVersion? _running;
    private readonly SelfUpdateStore _store;
    private readonly IUninstallKeyStore _uninstall;
    private readonly Func<IStartedSignal> _signalFactory;
    private readonly string _appExePath;
    private readonly Action? _onDispose;
    private readonly Func<TimeSpan, Task> _delay;
    private SwapTransaction? _swap;

    public SelfUpdateService(
        AppUpdateWatcher watcher,
        SelfUpdateStore store,
        SelfUpdateInstaller? installer,
        AppRestart restart,
        AppVersion? running,
        string appExePath,
        IUninstallKeyStore? uninstall = null,
        Func<IStartedSignal>? signalFactory = null,
        Action? onDispose = null,
        Func<TimeSpan, Task>? delay = null)
    {
        Watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _installer = installer;
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _running = running;
        _appExePath = appExePath;
        _uninstall = uninstall ?? new RegistryUninstallKeyStore();
        _signalFactory = signalFactory ?? (static () => new NamedStartedSignal());
        _onDispose = onDispose;
        _delay = delay ?? (static span => Task.Delay(span));
        ViewModel = new SelfUpdateViewModel(this);
    }

    public AppUpdateWatcher Watcher { get; }

    public SelfUpdateViewModel ViewModel { get; }

    public AppVersion? Running => _running;

    public SelfUpdateStore Store => _store;

    /// <summary>The Add/Remove Programs entry was brought up to date after the last self-update (for a toast and for tests).</summary>
    public bool UpdatedInstallerEntry { get; private set; }

    /// <summary>Raised (on the pool) with a sentence for a tray toast: the update took, or it did not.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>The real service for the running app. Nothing runs, and no HTTP client exists, until the first check.</summary>
    public static SelfUpdateService Create(AppRestart restart)
    {
        ArgumentNullException.ThrowIfNull(restart);

        var store = new SelfUpdateStore(SelfUpdateStore.DefaultPath());
        var running = AppVersion.Running();
        var exe = Environment.ProcessPath ?? string.Empty;
        var http = new Lazy<HttpClient>(() => AppUpdateChecker.CreateHttpClient(), LazyThreadSafetyMode.ExecutionAndPublication);
        var checker = new Lazy<AppUpdateChecker>(() => new AppUpdateChecker(store, running, http.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        var watcher = new AppUpdateWatcher(
            (manual, token) => checker.Value.CheckAsync(manual, token),
            store,
            onDispose: () =>
            {
                if (checker.IsValueCreated)
                {
                    checker.Value.Dispose();
                }
            });

        // Downloads stream with the headers read first and have their own size caps; a long timeout, cancelled by the operator.
        var downloads = new Lazy<HttpClient>(
            () =>
            {
                var client = AppUpdateChecker.CreateHttpClient(TimeSpan.FromMinutes(15));
                client.DefaultRequestHeaders.Accept.Clear();
                return client;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        var installer = exe.Length == 0
            ? null
            : new SelfUpdateInstaller(() => downloads.Value, exe, SelfUpdateStore.DefaultDataRoot(), blockedReason: () => restart.BlockedReason);
        return new SelfUpdateService(watcher, store, installer, restart, running, exe, onDispose: () =>
        {
            if (http.IsValueCreated)
            {
                http.Value.Dispose();
            }

            if (downloads.IsValueCreated)
            {
                downloads.Value.Dispose();
            }
        });
    }

    /// <summary>A service that checks nothing and installs nothing: what an isolated composition (a test) gets.</summary>
    public static SelfUpdateService CreateInert(AppRestart restart, string storePath) =>
        new(
            new AppUpdateWatcher((_, _) => Task.FromResult(AppUpdateCheck.NotChecked), new SelfUpdateStore(storePath + ".watcher")),
            new SelfUpdateStore(storePath),
            installer: null,
            restart,
            AppVersion.Running(),
            string.Empty,
            uninstall: new NoUninstallKeyStore());

    /// <summary>The current answer's release, but only when it is installable and not a development build.</summary>
    public SelfUpdateRelease? Offered => Watcher.Available;

    /// <summary>"Check for Updates…": asks GitHub now (ETag-conditional) and returns the answer.</summary>
    public Task<AppUpdateCheck> CheckManuallyAsync(CancellationToken cancellationToken = default) => Watcher.CheckNowAsync(manual: true, cancellationToken);

    /// <summary>
    /// For the review: the expected SHA-256 (from checksums.txt, cross-checked with GitHub's digest) and any reason the install cannot go ahead.
    /// Downloads only the small checksums file.
    /// </summary>
    public async Task<(string? Sha256, string? Problem, IReadOnlyList<string> Notes)> PrepareReviewAsync(SelfUpdateRelease release, CancellationToken cancellationToken)
    {
        if (_installer is null)
        {
            return (null, "Updating is not available in this build of the app.", []);
        }

        if (_installer.PreconditionRefusal() is { } refusal)
        {
            return (null, refusal, []);
        }

        var notes = new List<string>();
        if (_installer.IsDevelopmentFolder)
        {
            notes.Add("This app runs from a build folder (publish or bin); the update replaces that build.");
        }

        notes.Add("Administrator mode keeps its own copy of the helper under Program Files. This update replaces only the helper next to the app; the Program Files copy is refreshed by the next administrator-mode restart.");
        var (hash, problem) = await _installer.FetchExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);
        if (hash is not null && release.Zip?.Digest is null)
        {
            notes.Add("GitHub published no digest for this file; it is checked against checksums.txt only.");
        }

        return (hash, problem, notes);
    }

    /// <summary>
    /// Downloads, verifies, swaps and asks the shell to restart. Null when the restart was requested (the process is about to be replaced);
    /// otherwise why not, with the program files exactly as they were. A restart the operator then declines leaves the swap in place:
    /// <see cref="HasInstalledUpdate"/> is true and <see cref="RestartNow"/> finishes it.
    /// </summary>
    public async Task<string?> InstallAsync(SelfUpdateRelease release, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_installer is null || _running is not { } running)
        {
            return "Updating is not available in this build of the app.";
        }

        if (HasInstalledUpdate)
        {
            return "An update is already installed and waiting for the app to restart.";
        }

        var stage = await _installer.StageAsync(release, progress, cancellationToken).ConfigureAwait(false);
        if (stage.Staged is not { } staged)
        {
            return stage.Refusal ?? "The update could not be prepared.";
        }

        progress?.Report("Replacing the program files…");
        var (swap, refusal) = _installer.Swap(staged, running);
        if (swap is null)
        {
            return refusal ?? "The program files could not be replaced.";
        }

        _swap = swap;
        _restart.PendingHandoff = new UpdateHandoff
        {
            ExecutablePath = _appExePath,
            Signal = _signalFactory(),
            Rollback = () =>
            {
                var problems = swap.Rollback();
                _swap = null;
                return problems;
            },
        };

        progress?.Report("Restarting…");
        return RestartNow();
    }

    /// <summary>True between a successful swap and the restart that makes it take effect.</summary>
    public bool HasInstalledUpdate => _swap is not null;

    /// <summary>
    /// Asks for the restart (the shell's usual exit checks). A refusal (a state-changing run in flight) undoes the swap so the next start is
    /// not a surprise; the reason is returned. Null when the restart was raised.
    /// </summary>
    public string? RestartNow()
    {
        var reason = _restart.Request();
        if (reason is not null && _swap is { } swap)
        {
            _ = swap.Rollback();
            _swap = null;
            if (_restart.PendingHandoff is { } handoff)
            {
                handoff.Signal.Dispose();
                _restart.PendingHandoff = null;
            }
        }

        return reason;
    }

    /// <summary>
    /// Run once at start, off the UI thread. A marker for this version is a finished update: the <c>.old</c> files and the marker go (retried
    /// while the previous process finishes exiting), the Add/Remove Programs entry gets the new version, and a notice is raised. Leftovers
    /// without a marker are removed quietly.
    /// </summary>
    public async Task CompletePendingUpdateAsync()
    {
        if (_installer is null || _running is not { } running)
        {
            return;
        }

        PostUpdateResult result;
        try
        {
            result = _installer.CompletePendingUpdate(running);
            for (var attempt = 0; attempt < 10 && result.StillLocked.Count > 0; attempt++)
            {
                await _delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                result = _installer.CompletePendingUpdate(running);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"self-update: the clean-up after an update failed: {ex.Message}");
            return;
        }

        switch (result.Outcome)
        {
            case PostUpdateOutcome.Updated:
                UpdatedInstallerEntry = InstallerRegistration.UpdateDisplayVersion(_uninstall, _installer.AppDirectory, running);
                Notice?.Invoke(this, $"Updated to DefenseClaw for Windows {running}.");
                break;
            case PostUpdateOutcome.NotApplied:
                Notice?.Invoke(this, $"The update to {result.Version} was not applied; this is still {running}.");
                break;
        }
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        Watcher.Dispose();
        _onDispose?.Invoke();
    }

    private sealed class NoUninstallKeyStore : IUninstallKeyStore
    {
        public string? GetString(string subKey, string name) => null;

        public bool SetString(string subKey, string name, string value) => false;
    }
}
