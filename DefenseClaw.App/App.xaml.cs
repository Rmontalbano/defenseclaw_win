using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App;

/// <summary>
/// Application entry point and composition root driver.
/// <para>
/// Order matters here: last-resort exception handlers, then the single-instance check,
/// then services (which resolves the gateway token and hands it to
/// <c>CliRunner.RegisterSecret</c> before anything can shell out), then theming, then the
/// tray, then the window, then the poll loop.
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
/// window and the poll loop all exist. Before that point the object graph is half-built:
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
    private MainWindow? _window;
    private DateTimeOffset _lastFaultDialogUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// True once the tray, the window and the poll loop exist. Gates whether a dispatcher
    /// fault is survivable; see the type doc for the reasoning.
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
            // Hand the request to the instance that already owns the tray icon.
            _instanceGuard.SignalFirstInstance();
            _instanceGuard.Dispose();
            _instanceGuard = null;
            Shutdown();
            return;
        }

        _instanceGuard.ActivationRequested += OnActivationRequested;
        _instanceGuard.StartListening();

        _services = AppServices.Initialize();
        _catalog = new PanelCatalog(_services);

        ApplyTheme();

        _tray = new TrayIconService(_services);
        _tray.OpenDashboardRequested += (_, _) => _window?.ShowAndActivate();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _window = new MainWindow(_services, _catalog, _tray);
        MainWindow = _window;

        // Autostart launches with --minimized: the tray is the app until the user asks for more.
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }

        // Started last, on the UI thread, so StateChanged is raised where the bindings live.
        _services.Monitor.Start();

        // Last statement, deliberately: from here on a dispatcher fault costs a panel
        // interaction rather than the session, because the tray can still drive everything.
        _shellReady = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
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
    /// .NET 9's <see cref="System.Windows.ThemeMode"/> drives the framework's own Fluent
    /// styles; WPF-UI's theme manager drives its control library. Both follow the OS, and
    /// both have to be told to.
    /// </summary>
    private void ApplyTheme()
    {
        ThemeMode = ThemeMode.System;
        ApplicationThemeManager.ApplySystemTheme();
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        // Raised on the guard's listener thread.
        _ = Dispatcher.BeginInvoke(() => _window?.ShowAndActivate());
    }

    private void ExitApplication()
    {
        _window?.AllowClose();
        _window?.Close();
        Shutdown();
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
            if (_window is { IsVisible: true })
            {
                _window.Hide();
            }

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
    /// <b>Bounds.</b> One file per process, named for its start time and pid. The newest
    /// <see cref="MaxFiles"/> files are kept and the rest are deleted on first write, so a
    /// machine that crash-loops cannot accumulate logs forever. Within a session, appends
    /// stop after <see cref="MaxFileBytes"/>: a fault that repeats on every dispatcher pass
    /// would otherwise fill the disk faster than anyone could notice. Both limits are
    /// deliberately generous enough that a real crash is never truncated below usefulness.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> Every entry point is wrapped: this is called from handlers that
    /// run while the process is already failing, and on threads with no dispatcher above
    /// them. An IO error here must be silent — the alternative is a second fault inside the
    /// handler for the first one.
    /// </para>
    /// </summary>
    private static class CrashLog
    {
        /// <summary>Newest files kept, including the one this process is writing.</summary>
        private const int MaxFiles = 10;

        /// <summary>Per-session append budget; entries past it are suppressed with a marker.</summary>
        private const long MaxFileBytes = 1024 * 1024;

        private static readonly object Gate = new();
        private static string? _filePath;
        private static bool _capped;

        /// <summary>The directory holding the logs. Created lazily on the first fault.</summary>
        public static string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DefenseClaw.App",
            "logs");

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
        /// Deletes all but the newest <c>MaxFiles - 1</c> files, leaving room for this
        /// session's. Best effort in every respect: a file another process (or a viewer)
        /// holds open simply survives one more round.
        /// </summary>
        private static void Prune()
        {
            try
            {
                var existing = Directory.GetFiles(DirectoryPath, "crash-*.log");
                if (existing.Length < MaxFiles)
                {
                    return;
                }

                var stale = existing
                    .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                    .Skip(MaxFiles - 1);

                foreach (var path in stale)
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
            catch (Exception)
            {
                // Pruning is housekeeping. Never let it stop the entry from being written.
            }
        }
    }
}
