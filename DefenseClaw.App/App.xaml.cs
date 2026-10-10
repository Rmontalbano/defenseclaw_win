using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using H.NotifyIcon.Core;
using Microsoft.Win32;

namespace DefenseClaw.App;

/// <summary>
/// Application entry point and composition root driver.
/// <para>
/// Order matters here: last-resort exception handlers, then the single-instance check,
/// then the config read (started on a pool thread) overlapped with theming,
/// then services (which resolves the gateway token and hands it to
/// <c>CliRunner.RegisterSecret</c> before anything can shell out), then the tray, then — unless
/// the launch is <c>--minimized</c> — the window, then the poll loop.
/// </para>
/// <para>
/// <b>The dashboard window is built on demand</b> (<see cref="DashboardHost"/>): an autostart
/// launch is tray-only, so it never constructs the window at all until the operator asks for it.
/// Nothing in this class may assume it exists — go through <see cref="_dashboard"/>.
/// </para>
/// <para>
/// <b>Why the handlers are wired first.</b> This app runs with
/// <c>ShutdownMode="OnExplicitShutdown"</c> and autostarts with <c>--minimized</c>, so for
/// most of its life there is no window at all. An unhandled exception therefore has no
/// visible consequence beyond the tray shield disappearing — the operator's monitoring
/// stops and nothing says so. Every fault must leave a trace on disk before anything else
/// is allowed to happen, including the faults thrown while the composition root itself is
/// still being built. See <see cref="CrashLog"/> for the file and its bounds.
/// </para>
/// <para>
/// <b>Continue-or-die criterion.</b> <see cref="DispatcherUnhandledException"/> is marked
/// handled only once <see cref="_shellReady"/> is true — that is, once the tray icon, the
/// window (unless the launch is <c>--minimized</c>) and the poll loop all exist. Before that
/// point the object graph is half-built:
/// there may be no tray icon to quit from and no monitoring to preserve, so swallowing
/// would produce an invisible process with no user-reachable control surface. Those faults
/// are logged and allowed to terminate the process. After that point the tray is a
/// complete control surface on its own (Open dashboard / Start-Stop gateway / Exit) and
/// the gateway poll loop lives on its own thread, so a faulted UI callback costs a panel
/// interaction, not the session.
/// </para>
/// <para>
/// <b>No zombie tray icons.</b> A swallowed dispatcher exception fired somewhere in the
/// middle of a UI callback, so the dashboard's visual tree may be half-updated — bound
/// collections partly rebuilt, a panel mid-navigation. Continuing to display it would be
/// dishonest. The handler therefore hides the window and says so in the dialog: the tray
/// stays live and authoritative, and reopening from the shield re-shows and re-lays-out
/// the window rather than leaving the user staring at a broken one. The tray icon is never
/// left as the only survivor of a dead window — either both are alive, or the process is
/// gone and the crash log explains why.
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Minimum spacing between fault dialogs. A faulting binding or a wedged panel callback
    /// can raise the same exception on every dispatcher pass; one dialog per fault would
    /// stack modal windows faster than the user can dismiss them. Logging is never
    /// throttled — only the dialog is.
    /// </summary>
    private static readonly TimeSpan FaultDialogInterval = TimeSpan.FromSeconds(30);

    private SingleInstanceGuard? _instanceGuard;
    private AppServices? _services;
    private PanelCatalog? _catalog;
    private TrayIconService? _tray;
    private DashboardHost? _dashboard;
    private DateTimeOffset _lastFaultDialogUtc = DateTimeOffset.MinValue;

    /// <summary>Owns the look (style and light/dark); created before anything can paint. See <see cref="ApplyTheme"/>.</summary>
    private AppearanceService? _appearance;

    /// <summary>
    /// True once the tray, the window (when the launch shows one) and the poll loop exist.
    /// Gates whether a dispatcher fault is survivable; see the type doc for the reasoning.
    /// </summary>
    private bool _shellReady;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Wired before base.OnStartup and before any service is constructed: a fault thrown
        // while building the composition root is exactly the fault most worth a log entry,
        // and it is the one a handler installed later would miss.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsFirstInstance)
        {
            // Hand the request to the instance that already owns the tray icon, and say so when nothing answers (a squatter on the mutex
            // name, a hung instance, an elevated one this session cannot reach) instead of exiting without a word.
            var guard = _instanceGuard;
            var outcome = new SecondLaunchHandshake(guard.SignalFirstInstance, guard.WaitForAcknowledgement).Run();
            guard.Dispose();
            _instanceGuard = null;
            if (SecondLaunchHandshake.MessageFor(outcome) is { } message)
            {
                _ = MessageBox.Show(message, "DefenseClaw for Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        _instanceGuard.ActivationRequested += OnActivationRequested;
        _instanceGuard.StartListening();

        // Reading config.yaml (a cold YAML parse) and the token ladder is file I/O the UI thread
        // would otherwise do before it can start on anything else. Started on a pool thread
        // here, it overlaps theming, which needs neither; the services below join the read.
        AppServices.BeginInitialize();

        ApplyTheme();

        _services = AppServices.Initialize();
        _catalog = new PanelCatalog(_services);

        _dashboard = new DashboardHost(CreateDashboard);

        // A deep link (ShellNavigation) means the operator wants to see that panel: bring the dashboard up, building it if this
        // is a tray-only session. A window that already exists selects the panel itself; a new one opens on it.
        _services.Navigation.Requested += (_, _) => ShowDashboard();

        _tray = new TrayIconService(_services);
        _tray.OpenDashboardRequested += (_, _) => ShowDashboard();
        _tray.ExitRequested += (_, _) => ExitApplication();

        // A copy that failed (another program holds the clipboard) or was cut short says so, wherever it was started from.
        Views.Controls.DcClipboard.Notice += message => _tray?.Notify("Clipboard", message, NotificationIcon.Warning);

        // Autostart launches with --minimized: the tray is the app until the user asks for more,
        // and the dashboard window is not even built until then.
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            ShowDashboard();
        }

        // Started last, on the UI thread, so StateChanged is raised where the bindings live.
        _services.Monitor.Start();

        // What the connected runtime can do (a probe of --version-json and --help screens, off the UI thread, repeated only when the CLI file changes).
        _services.Runtime.Start();

        // One tray toast per release ("DefenseClaw X is available"; the banner in the dashboard carries the rest). Then the background
        // update check: Start returns at once and the work runs on the pool, after the monitor's first poll, so nothing is added to the launch.
        _services.UpdateWatcher.NewVersionAvailable += (_, e) => _tray?.Notify("DefenseClaw update", e.Text + ". Open the dashboard to review it.", NotificationIcon.Info);
        _services.UpdateWatcher.Start();

        // Opt-in automatic gateway start: decides once, on the monitor's first settled snapshot (off unless Settings turned it on).
        _services.GatewayAutoStart.NoticeRaised += (_, e) => _tray?.Notify(e.Title, e.Body, e.IsFailure ? NotificationIcon.Warning : NotificationIcon.Info);
        _services.GatewayAutoStart.Start();

        // Last statement, deliberately: from here on a dispatcher fault costs a panel
        // interaction rather than the session, because the tray can still drive everything.
        _shellReady = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Tray first so the shield leaves the notification area immediately; the CLI shutdown
        // below is bounded but not instant, and a ghost icon for a few seconds looks like a hang.
        _tray?.Dispose();

        // Then the CLI children: cancel and kill the process tree of anything still running
        // (a wizard mutation, `agent discover`, a gateway start/stop) so nothing is orphaned
        // writing to ~/.defenseclaw after the app is gone. The upgrade installer/resolver is
        // exempt inside the runner — killing Setup mid-install can corrupt the install — and
        // the wait is bounded, so a stuck child cannot hold the exit open.
        _services?.Cli.Shutdown();

        _services?.Dispose();

        if (_instanceGuard is not null)
        {
            _instanceGuard.ActivationRequested -= OnActivationRequested;
            _instanceGuard.Dispose();
        }

        // The exception handlers are deliberately left wired through shutdown: teardown
        // order bugs surface here, and a log entry is the only evidence of them.
        base.OnExit(e);
    }

    /// <summary>
    /// Applies the saved look (style and light/dark) before the tray flyout or any window exists.
    /// <para>
    /// .NET 9's <see cref="System.Windows.ThemeMode"/> drives the framework's own Fluent styles and
    /// WPF-UI's theme manager its control library; <see cref="AppearanceService"/> sets both from
    /// one decision, and swaps the token dictionary the <c>Dc*</c> styles read, so the operator's
    /// choice (Appearance button, palette, Ctrl+Shift+L) takes effect live. With nothing saved
    /// the answer is Default + follow the system, which is what this method did before there was
    /// a choice.
    /// </para>
    /// </summary>
    private void ApplyTheme()
    {
        _appearance = new AppearanceService(this, new FileAppearanceSettingsStore(), new WindowsSystemThemeSource());
        _appearance.Initialize();

        // The service follows the OS itself (in mode System), but only if told when it changes.
        // MainWindow no longer runs WPF-UI's SystemThemeWatcher - it would force the system theme
        // over an explicit choice - and an autostarted "--minimized" session may go a whole day
        // without building that window anyway, while the tray flyout, which reads the same
        // DynamicResource brushes, would keep yesterday's light/dark. This is the OS's own signal.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// Lets the appearance service compare against the OS. Raised on a system thread for every
    /// "General" preference change (there are many, most irrelevant), so it hops to the dispatcher;
    /// the service compares first, so an irrelevant change costs a few property reads, not a
    /// dictionary swap.
    /// </summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() => _appearance?.OnSystemPreferenceChanged());
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        // Raised on the guard's listener thread.
        _ = Dispatcher.BeginInvoke(ShowDashboard);
    }

    /// <summary>
    /// Shows the dashboard, building it first if this is the first request — the tray's "Open
    /// Dashboard", its flyout button, a second launch, or the launch itself when it is not
    /// <c>--minimized</c>. UI thread only.
    /// </summary>
    private void ShowDashboard()
    {
        // No host yet: a request that lands while OnStartup is still composing the shell is
        // dropped, exactly as it was when the window was built inline. Shutting down: WPF refuses
        // to build a Window once Application.Shutdown has started.
        if (_dashboard is null || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _dashboard.Show();
    }

    /// <summary>
    /// The factory <see cref="DashboardHost"/> calls the first time the dashboard is wanted.
    /// </summary>
    private IDashboardWindow CreateDashboard()
    {
        var window = new MainWindow(_services!, _catalog!, _tray!);

        // The close button with "Closing the window keeps DefenseClaw in the tray" turned off (Settings → Startup): the same Exit the
        // tray's menu runs, prompts included. If it declines, the window is still there.
        window.ExitRequested += (_, _) => ExitApplication();

        // Application.MainWindow is what the Updates and wizard windows read to find their owner,
        // and what WPF-UI re-applies the window backdrop to on a theme change. WPF would hand it
        // to whichever Window is built first, which is no longer this one.
        MainWindow = window;
        return window;
    }

    /// <summary>
    /// The tray's Exit. <c>async void</c> because it is an event handler, which means nothing awaits it: an exception
    /// that escaped here would surface as a dispatcher fault - the dashboard hidden behind a dialog - with the exit
    /// silently abandoned. So nothing escapes: a fault is logged and said in a tray toast, and so is an exit that
    /// cannot go ahead because the config editor is busy (see <see cref="ClearEditorForExitAsync"/>).
    /// </summary>
    private async void ExitApplication()
    {
        try
        {
            // An in-app upgrade survives CliRunner.Shutdown by design, so quitting would not kill it —
            // but it would leave it running with nobody watching, and the resolver-script channel
            // writes to stdout pipes that close with this process. Ask; default to staying.
            if (_services?.Cli.HasShutdownSurvivingRun == true)
            {
                var answer = MessageBox.Show(
                    "A DefenseClaw upgrade is still running.\n\n" +
                    "Exiting now leaves the installer running in the background with no progress shown, " +
                    "and an upgrade run through the resolver script may be interrupted when its output " +
                    "pipe closes. Waiting for the Updates window to report the result is safer.\n\n" +
                    "Exit anyway?",
                    "DefenseClaw — upgrade in progress",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // Unsaved config-editor edits: not clear means the operator cancelled, the save failed or the editor is
            // busy, so stay running. Last check, because it closes the editor itself once the answer allows the exit.
            var clearance = await ClearEditorForExitAsync(Views.ConfigEditor.ConfigEditorWindow.CloseForExitAsync);
            if (clearance.Notice is { } notice)
            {
                _tray?.Notify("DefenseClaw", notice, NotificationIcon.Warning);
            }

            if (!clearance.MayExit)
            {
                return;
            }

            _dashboard?.CloseForExit();
            Shutdown();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExitApplication", ex);
            try
            {
                _tray?.Notify("DefenseClaw", ExitFailedText(ex), NotificationIcon.Error);
            }
            catch (Exception)
            {
                // The toast is a courtesy; the crash log above has the fault.
            }
        }
    }

    /// <summary>What the config editor said about the exit: whether it may go ahead, and a toast when the operator has not already been told why not.</summary>
    internal readonly record struct ExitClearance(bool MayExit, string? Notice);

    private static string ExitFailedText(Exception ex) => $"Could not exit: {ex.Message}";

    /// <summary>
    /// The config-editor half of the tray Exit, separate from the window so it can be tested. <paramref name="closeEditor"/> is
    /// <c>ConfigEditorWindow.CloseForExitAsync</c>.
    /// <para>
    /// <see cref="Views.ConfigEditor.ConfigEditorExitResult.Declined"/> needs no toast: the operator just answered the
    /// question, or the editor is showing why the save failed. <see cref="Views.ConfigEditor.ConfigEditorExitResult.Busy"/>
    /// does - the click asked nothing and changed nothing, and looked like it did nothing at all. A close that throws (a
    /// faulted save the editor is still holding) abandons the exit with the reason, and is logged.
    /// </para>
    /// </summary>
    internal static async Task<ExitClearance> ClearEditorForExitAsync(Func<Task<Views.ConfigEditor.ConfigEditorExitResult>> closeEditor)
    {
        ArgumentNullException.ThrowIfNull(closeEditor);

        try
        {
            return await closeEditor().ConfigureAwait(true) switch
            {
                Views.ConfigEditor.ConfigEditorExitResult.Closed => new ExitClearance(true, null),
                Views.ConfigEditor.ConfigEditorExitResult.Busy => new ExitClearance(
                    false,
                    "Could not exit: the config editor is still saving or asking about unsaved changes. Finish there, then choose Exit again."),
                _ => new ExitClearance(false, null),
            };
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExitApplication (config editor close)", ex);
            return new ExitClearance(false, ExitFailedText(ex));
        }
    }

    /// <summary>
    /// Last-resort handler for faults raised inside a dispatcher callback — a binding, a
    /// command, a <c>BeginInvoke</c> continuation posted from a background thread.
    /// <para>
    /// Logs unconditionally, then decides whether the app can honestly continue (see the
    /// type doc). When it can, the window is hidden rather than left half-rendered and one
    /// dialog explains where the log is and that the tray is still watching.
    /// </para>
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            CrashLog.Write("DispatcherUnhandledException", e.Exception);

            if (!_shellReady)
            {
                // Startup fault. Handled stays false: the process dies, Windows Error
                // Reporting records it, and the crash log written above says why. A
                // half-built app with no tray is worse than no app.
                return;
            }

            e.Handled = true;

            // The callback that faulted may have left the visual tree inconsistent. Hide
            // rather than repaint; reopening from the tray gives a freshly laid-out window.
            // (Nothing to hide if the dashboard was never built.)
            _dashboard?.HideIfVisible();

            ShowFaultDialog(e.Exception);
        }
        catch (Exception)
        {
            // A handler that throws re-enters this same path and takes the process with it.
            // Nothing here is important enough to be worth that, so everything is swallowed.
        }
    }

    /// <summary>
    /// Faults from discarded tasks — the log-tail loop, the gateway poll loop, every
    /// fire-and-forget <c>_ = SomethingAsync()</c> in the panels.
    /// <para>
    /// These are observed and logged, never fatal. .NET does not terminate on unobserved
    /// task exceptions by default, but <c>ThrowUnobservedTaskExceptions</c> can flip that,
    /// and a tray app that dies at a GC boundary for a failed background poll would be
    /// indefensible — so <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/> is
    /// called first, before anything that could itself fail.
    /// </para>
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            e.SetObserved();
            CrashLog.Write("UnobservedTaskException", e.Exception);
        }
        catch (Exception)
        {
            // See OnDispatcherUnhandledException: a last-resort handler never throws.
        }
    }

    /// <summary>
    /// The process is already lost — a fault on a thread-pool or background thread with no
    /// dispatcher above it. Nothing can be swallowed here (the CLR is on its way to
    /// terminating regardless), so this exists purely to get the exception onto disk before
    /// the process disappears from the notification area without explanation.
    /// </summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var source = e.IsTerminating
                ? "AppDomain.UnhandledException (terminating)"
                : "AppDomain.UnhandledException";

            CrashLog.Write(source, e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        }
        catch (Exception)
        {
            // See OnDispatcherUnhandledException: a last-resort handler never throws.
        }
    }

    /// <summary>
    /// One honest dialog: what broke, that monitoring continues, and where to read the
    /// detail. Rate-limited by <see cref="FaultDialogInterval"/> so a repeating fault
    /// cannot stack modal windows.
    /// </summary>
    private void ShowFaultDialog(Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFaultDialogUtc < FaultDialogInterval)
        {
            return;
        }

        _lastFaultDialogUtc = now;

        var location = CrashLog.CurrentFilePath ?? CrashLog.DirectoryPath;
        var message =
            "The dashboard hit an unexpected error and was closed to the tray." +
            Environment.NewLine + Environment.NewLine +
            "Monitoring is still running. Reopen the dashboard from the shield in the " +
            "notification area, or right-click the shield and pick Exit to quit." +
            Environment.NewLine + Environment.NewLine +
            $"{exception.GetType().Name}: {exception.Message}" +
            Environment.NewLine + Environment.NewLine +
            "The full details were written to:" + Environment.NewLine + location;

        _ = MessageBox.Show(
            message,
            "DefenseClaw hit an unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Bounded, append-only crash log under
    /// <c>%LOCALAPPDATA%\DefenseClaw.App\logs</c> — alongside the existing
    /// <c>updates</c> and <c>upgrades</c> directories this app already owns there.
    /// <para>
    /// <b>Bounds.</b> One file per process, named for its start time and pid. Files older than
    /// <see cref="MaxAge"/> are deleted on first write, so a machine that crash-loops cannot
    /// accumulate logs forever - and a burst of new ones (a test or probe harness that faults on
    /// every run) cannot push out the real history, which a plain newest-N cap did: ten harness
    /// crashes in five minutes evicted every genuine one. <see cref="MaxFiles"/> remains only as a
    /// disk-safety ceiling for a genuine crash loop. Within a session, appends stop after
    /// <see cref="MaxFileBytes"/>: a fault that repeats on every dispatcher pass would otherwise
    /// fill the disk faster than anyone could notice. The limits are deliberately generous enough
    /// that a real crash is never truncated below usefulness.
    /// </para>
    /// <para>
    /// <b>Where.</b> <see cref="DirectoryEnvironmentVariable"/> redirects the directory (a harness that has to drive
    /// the app sets it so its faults never land among the real ones); <see cref="UseDirectory"/> does the same for a
    /// test, and restores the state afterwards.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> Every entry point is wrapped: this is called from handlers that
    /// run while the process is already failing, and on threads with no dispatcher above
    /// them. An IO error here must be silent — the alternative is a second fault inside the
    /// handler for the first one.
    /// </para>
    /// </summary>
    internal static class CrashLog
    {
        /// <summary>Environment variable that names the directory the logs go to instead of the default.</summary>
        internal const string DirectoryEnvironmentVariable = "DEFENSECLAW_APP_LOG_DIR";

        /// <summary>How long a crash file is kept.</summary>
        internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        /// <summary>Disk-safety ceiling on files kept, including the one this process is writing; age normally prunes long before it.</summary>
        internal const int MaxFiles = 100;

        /// <summary>Per-session append budget; entries past it are suppressed with a marker.</summary>
        private const long MaxFileBytes = 1024 * 1024;

        private static readonly object Gate = new();
        private static string? _filePath;
        private static bool _capped;
        private static string? _directoryOverride;

        private static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DefenseClaw.App",
            "logs");

        /// <summary>The directory holding the logs. Created lazily on the first fault.</summary>
        public static string DirectoryPath =>
            _directoryOverride
            ?? (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : DefaultDirectory);

        /// <summary>
        /// Test seam: points the log at <paramref name="directory"/> with a fresh session (no file yet, not capped)
        /// until the returned scope is disposed, which puts back what was there. The state is process-wide, so tests
        /// that use it must not run in parallel with each other.
        /// </summary>
        internal static IDisposable UseDirectory(string directory)
        {
            ArgumentException.ThrowIfNullOrEmpty(directory);

            lock (Gate)
            {
                var scope = new Scope(_directoryOverride, _filePath, _capped);
                _directoryOverride = directory;
                _filePath = null;
                _capped = false;
                return scope;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly string? _previousDirectory;
            private readonly string? _previousFile;
            private readonly bool _previousCapped;

            public Scope(string? directory, string? file, bool capped)
            {
                _previousDirectory = directory;
                _previousFile = file;
                _previousCapped = capped;
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    _directoryOverride = _previousDirectory;
                    _filePath = _previousFile;
                    _capped = _previousCapped;
                }
            }
        }

        /// <summary>This session's file, or null if nothing has faulted (or the write failed).</summary>
        public static string? CurrentFilePath
        {
            get
            {
                lock (Gate)
                {
                    return _filePath;
                }
            }
        }

        /// <summary>
        /// Appends one entry. <paramref name="fallbackText"/> covers the
        /// <see cref="AppDomain.UnhandledException"/> case, where the thrown object is not
        /// required to be an <see cref="Exception"/> at all.
        /// </summary>
        public static void Write(string source, Exception? exception, string? fallbackText = null)
        {
            try
            {
                lock (Gate)
                {
                    if (_capped)
                    {
                        return;
                    }

                    var path = EnsureFile();
                    if (path is null)
                    {
                        return;
                    }

                    var info = new FileInfo(path);
                    if (info.Exists && info.Length >= MaxFileBytes)
                    {
                        _capped = true;
                        File.AppendAllText(
                            path,
                            $"--- capped at {MaxFileBytes:N0} bytes; further entries in this session are not recorded ---{Environment.NewLine}");
                        return;
                    }

                    var builder = new StringBuilder();
                    _ = builder.Append("=== ")
                        .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                        .Append("  ")
                        .Append(source)
                        .Append("  thread ")
                        .Append(Environment.CurrentManagedThreadId)
                        .AppendLine(" ===");

                    // ToString() carries the message, the stack and every inner exception,
                    // which is the whole point of writing this file at all.
                    _ = builder.AppendLine(exception?.ToString() ?? fallbackText ?? "(no exception object)");
                    _ = builder.AppendLine();

                    File.AppendAllText(path, builder.ToString());
                }
            }
            catch (Exception)
            {
                // Deliberately silent; see the type doc.
            }
        }

        /// <summary>Resolves this session's file, creating the directory and pruning once.</summary>
        private static string? EnsureFile()
        {
            if (_filePath is not null)
            {
                return _filePath;
            }

            _ = Directory.CreateDirectory(DirectoryPath);
            Prune();

            var path = Path.Combine(
                DirectoryPath,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            File.AppendAllText(
                path,
                $"DefenseClaw.App {version} · pid {Environment.ProcessId} · started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}" +
                Environment.NewLine + Environment.NewLine);

            _filePath = path;
            return _filePath;
        }

        /// <summary>
        /// Deletes crash files older than <see cref="MaxAge"/>, then, if more than <c>MaxFiles - 1</c> remain, all but the
        /// newest of those - leaving room for this session's. Best effort in every respect: a file another process (or a
        /// viewer) holds open simply survives one more round.
        /// </summary>
        private static void Prune()
        {
            try
            {
                var cutoff = DateTime.UtcNow - MaxAge;
                var kept = new List<(string Path, DateTime WrittenUtc)>();

                foreach (var path in Directory.GetFiles(DirectoryPath, "crash-*.log"))
                {
                    DateTime writtenUtc;
                    try
                    {
                        writtenUtc = new FileInfo(path).LastWriteTimeUtc;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (writtenUtc < cutoff)
                    {
                        TryDelete(path);
                    }
                    else
                    {
                        kept.Add((path, writtenUtc));
                    }
                }

                foreach (var stale in kept.OrderByDescending(file => file.WrittenUtc).Skip(MaxFiles - 1))
                {
                    TryDelete(stale.Path);
                }
            }
            catch (Exception)
            {
                // Pruning is housekeeping. Never let it stop the entry from being written.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Locked or already gone; the next fault tries again.
            }
        }
    }
}
