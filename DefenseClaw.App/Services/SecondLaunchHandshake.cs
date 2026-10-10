namespace DefenseClaw.App.Services;

/// <summary>What a launch that lost the single-instance race learned from asking the running one to surface.</summary>
public enum SecondLaunchOutcome
{
    /// <summary>The running instance acknowledged: it is DefenseClaw, it is listening, and it is bringing its window up. Exit quietly.</summary>
    Acknowledged,

    /// <summary>There was no way to reach an instance (no activation event could be opened): the mutex is held, but nothing here can talk to its holder.</summary>
    NoChannel,

    /// <summary>The request was sent and nothing answered within the timeout.</summary>
    NotAcknowledged,
}

/// <summary>
/// The second launch's decision, apart from the process, the mutex and the message box so it can be tested without any of them.
/// <para>
/// A named mutex is first come, first served: any process of the user's can create <c>DefenseClaw.App.SingleInstance</c> and every later
/// launch would then exit without a word (nothing is wrong with them, and nothing says why). The first instance therefore acknowledges
/// each activation request; a launch that cannot reach one, or hears nothing back, says so instead of exiting silently. An elevated first
/// instance also lands in <see cref="SecondLaunchOutcome.NoChannel"/> (its events cannot be opened from a normal session), which the text
/// covers: the same advice, close it from Task Manager, fixes both.
/// </para>
/// </summary>
public sealed class SecondLaunchHandshake
{
    /// <summary>How long a second launch waits for the running instance to answer. Generous: a busy machine, not a hung one, answers within it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<bool> _signal;
    private readonly Func<TimeSpan, bool> _waitForAcknowledgement;

    /// <param name="signal">Asks the running instance to surface; false when there was no channel to send it on.</param>
    /// <param name="waitForAcknowledgement">Waits up to the given time for its answer; true when it came.</param>
    public SecondLaunchHandshake(Func<bool> signal, Func<TimeSpan, bool> waitForAcknowledgement)
    {
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _waitForAcknowledgement = waitForAcknowledgement ?? throw new ArgumentNullException(nameof(waitForAcknowledgement));
    }

    /// <summary>Signals, waits, and says which of the three things happened. Never throws for an unreachable instance.</summary>
    public SecondLaunchOutcome Run(TimeSpan? timeout = null)
    {
        if (!_signal())
        {
            return SecondLaunchOutcome.NoChannel;
        }

        return _waitForAcknowledgement(timeout ?? DefaultTimeout)
            ? SecondLaunchOutcome.Acknowledged
            : SecondLaunchOutcome.NotAcknowledged;
    }

    /// <summary>The sentence for the message box, or null when the launch should exit quietly.</summary>
    public static string? MessageFor(SecondLaunchOutcome outcome) => outcome switch
    {
        SecondLaunchOutcome.Acknowledged => null,
        SecondLaunchOutcome.NoChannel =>
            "DefenseClaw for Windows did not start because something already holds its single-instance lock, and this launch could not reach it. " +
            "If DefenseClaw is running with administrator rights, close it from its tray icon (or Task Manager) and start it again. " +
            "If it is not running, another program is holding the lock; end that program, then start DefenseClaw again.",
        _ =>
            "DefenseClaw for Windows did not start because a process already holds its single-instance lock and did not answer when asked to show its window. " +
            "That is either a DefenseClaw that is not responding or a different program using the same name. " +
            "End the DefenseClaw process in Task Manager (or the other program), then start DefenseClaw again.",
    };
}
