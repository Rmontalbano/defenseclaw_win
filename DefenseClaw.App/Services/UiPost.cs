using System.Windows;

namespace DefenseClaw.App.Services;

/// <summary>
/// "Run this on the UI thread", for the objects that hear of things on a pool thread: the application's dispatcher, the way
/// <see cref="ScanActivity"/> and <see cref="AlertCountsService"/> do it. A test passes its own <c>Action&lt;Action&gt;</c> instead.
/// </summary>
internal static class UiPost
{
    /// <summary>
    /// Runs <paramref name="action"/> in place on the UI thread (and when there is no WPF application, as in a headless run), queues it
    /// otherwise, and drops it once shutdown has begun. Never throws for a dispatcher that is going away.
    /// </summary>
    public static void ToDispatcher(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

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
