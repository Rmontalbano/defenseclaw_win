using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// What the shared <see cref="UiThread"/> costs to keep running, measured in CPU cycles the thread itself spent
/// (<c>QueryThreadCycleTime</c>), so a hidden animation shows up as a rate rather than as noise in a process-wide CPU
/// figure. The same method the performance review used: an idle dispatcher measures 0.0-0.3 Mcycles/s, one WPF
/// animation clock left ticking measures 50-100.
/// </summary>
internal static class UiThreadCost
{
    private const uint ThreadQueryLimitedInformation = 0x0800;

    private static readonly Lazy<IntPtr> Handle = new(() =>
    {
        var id = UiThread.Run(GetCurrentThreadId);
        return OpenThread(ThreadQueryLimitedInformation, false, id);
    });

    /// <summary>
    /// Mcycles per second the UI thread spends over <paramref name="window"/>. Takes the lowest of <paramref name="attempts"/>
    /// windows: a stray timer or a queued layout from a neighbouring test can only add to one window, while a clock that is
    /// really still running adds to every one of them. It stops early once a window is below <paramref name="settledBelow"/>: a
    /// quiet window already proves there is nothing running, and a busy one is measured again.
    /// </summary>
    public static double Measure(TimeSpan window, int attempts = 3, double settledBelow = double.MinValue)
    {
        var best = double.MaxValue;
        for (var i = 0; i < attempts && best >= settledBelow; i++)
        {
            _ = QueryThreadCycleTime(Handle.Value, out var before);
            var watch = Stopwatch.StartNew();
            Thread.Sleep(window);
            _ = QueryThreadCycleTime(Handle.Value, out var after);
            best = Math.Min(best, (after - before) / 1e6 / watch.Elapsed.TotalSeconds);
        }

        return best;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
