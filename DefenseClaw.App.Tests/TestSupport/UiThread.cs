using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// One long-lived STA thread with a running dispatcher and the app's real resource dictionaries, for
/// the few tests that must build actual views (layout, virtualization). <see cref="StaThread"/> cannot
/// serve them: a WPF <see cref="Application"/> may be created once per process and belongs to the thread
/// that made it, and xunit gives every test a different thread.
/// <para>
/// The dictionaries are merged in the order <c>App.xaml</c> merges them — WPF-UI's theme and control
/// styles first, then <c>Themes/DefenseClaw.xaml</c>, which is built on their brushes — so a view built here
/// resolves every <c>Dc*</c> and WPF-UI key exactly as the running app does.
/// </para>
/// </summary>
internal static class UiThread
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>The dispatcher of the shared UI thread (started on first use).</summary>
    public static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                return _dispatcher ??= Start();
            }
        }
    }

    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Dispatcher.Invoke(body);
    }

    public static T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Dispatcher.Invoke(body);
    }

    /// <summary>
    /// Gives the application a Fluent <see cref="ThemeMode"/> (Dark) until the returned scope is disposed, then takes it away
    /// again. The running app always has one (<c>AppearanceService</c> sets it from the operator's choice), and some views are
    /// styled on top of the framework's own Fluent styles: <c>AuditPanel</c>'s row style is <c>BasedOn</c>
    /// <c>{x:Type ListViewItem}</c>, which exists only under one, so without a mode that view cannot be built here at all.
    /// <para>
    /// A scope rather than a mode for the whole process, because a Fluent mode changes what a plain <see cref="Window"/> is:
    /// its style sets <c>AllowsTransparency</c> while the handle is being created, which WPF refuses
    /// (<c>WindowIconTests</c> builds exactly such a window). Tests run one at a time on this thread, so a scope cannot leak
    /// into another test's view.
    /// </para>
    /// </summary>
    public static IDisposable FluentTheme()
    {
        Run(() => Application.Current.ThemeMode = ThemeMode.Dark);
        return new FluentThemeScope();
    }

    private sealed class FluentThemeScope : IDisposable
    {
        public void Dispose() => Run(() => Application.Current.ThemeMode = ThemeMode.None);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> (evaluated on the UI thread) from the calling thread until it holds. The
    /// UI thread stays free between polls, so async view-model work that resumes on its dispatcher can finish.
    /// </summary>
    /// <param name="timeoutMilliseconds">
    /// How long to wait before calling it a hang. Null: <see cref="TestTimeouts.Ceiling"/>, a condition's bound on any machine this suite runs on
    /// (it used to be 20 s, which a CI runner with both test projects and xunit's parallel classes on a few cores does not always meet). A test
    /// that names a number is waiting for something it expects to be quick, and says so.
    /// </param>
    public static void WaitFor(Func<bool> condition, string what, int? timeoutMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = Environment.TickCount64 + (timeoutMilliseconds ?? (long)TestTimeouts.Ceiling.TotalMilliseconds);
        while (!Run(condition))
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Lets everything queued at or above <see cref="DispatcherPriority.ContextIdle"/> run — loaded
    /// handlers, data-binding updates, and the Background-priority coalesced scrolls the panels schedule —
    /// repeatedly, until a pass has nothing left to do. Must be called on the UI thread.
    /// </summary>
    public static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DefenseClaw.App;component/Themes/DefenseClaw.xaml", UriKind.Absolute),
            });
            app.Resources["BoolToVisibility"] = new System.Windows.Controls.BooleanToVisibilityConverter();

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "DefenseClaw.App.Tests UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
