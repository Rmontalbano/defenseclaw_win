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
    /// Polls <paramref name="condition"/> (evaluated on the UI thread) from the calling thread until it holds. The
    /// UI thread stays free between polls, so async view-model work that resumes on its dispatcher can finish.
    /// </summary>
    public static void WaitFor(Func<bool> condition, string what, int timeoutMilliseconds = 20_000)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
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
