using System.Runtime.ExceptionServices;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Runs a test body on a dedicated single-threaded-apartment thread. Anything that creates a WPF
/// <see cref="System.Windows.Threading.DispatcherObject"/> (a <c>DispatcherTimer</c>, a control, a
/// clipboard call) must be built and used on one STA thread, and xunit's worker threads are MTA. A
/// hand-rolled thread keeps this suite free of an STA-attribute package.
/// <para>
/// The body's exception is captured and rethrown on the calling thread with its original stack, so a
/// failed assertion inside <see cref="Run(System.Action)"/> fails the test exactly as it would inline.
/// </para>
/// </summary>
internal static class StaThread
{
    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _ = Run<object?>(() =>
        {
            body();
            return null;
        });
    }

    public static T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        T result = default!;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        failure?.Throw();
        return result;
    }

    /// <summary>Runs an async body to completion on the STA thread (no synchronization context is installed).</summary>
    public static void Run(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Run(() => body().GetAwaiter().GetResult());
    }
}
