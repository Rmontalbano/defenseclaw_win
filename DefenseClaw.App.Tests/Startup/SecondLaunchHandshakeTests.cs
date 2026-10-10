using DefenseClaw.App.Services;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The second launch's decision (CUST-251): a mutex squatter or an unresponsive instance gets a message, a listening instance gets a quiet exit.
/// Pure logic with fakes - no mutex, no event, no App, no window.
/// </summary>
public sealed class SecondLaunchHandshakeTests
{
    [Fact]
    public void An_instance_that_acknowledges_means_a_quiet_exit()
    {
        var handshake = new SecondLaunchHandshake(() => true, _ => true);

        var outcome = handshake.Run();

        Assert.Equal(SecondLaunchOutcome.Acknowledged, outcome);
        Assert.Null(SecondLaunchHandshake.MessageFor(outcome));
    }

    [Fact]
    public void A_holder_nobody_can_signal_is_reported_and_never_waited_for()
    {
        var waited = false;
        var handshake = new SecondLaunchHandshake(() => false, _ => { waited = true; return true; });

        var outcome = handshake.Run();

        Assert.Equal(SecondLaunchOutcome.NoChannel, outcome);
        Assert.False(waited);
        var message = SecondLaunchHandshake.MessageFor(outcome);
        Assert.NotNull(message);
        Assert.Contains("single-instance lock", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signalled_holder_that_never_answers_is_reported_with_the_wait_it_was_given()
    {
        TimeSpan? asked = null;
        var handshake = new SecondLaunchHandshake(() => true, timeout => { asked = timeout; return false; });

        var outcome = handshake.Run(TimeSpan.FromMilliseconds(250));

        Assert.Equal(SecondLaunchOutcome.NotAcknowledged, outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(250), asked);
        var message = SecondLaunchHandshake.MessageFor(outcome);
        Assert.NotNull(message);
        Assert.Contains("did not answer", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_wait_is_used_when_none_is_given()
    {
        TimeSpan? asked = null;
        _ = new SecondLaunchHandshake(() => true, timeout => { asked = timeout; return true; }).Run();

        Assert.Equal(SecondLaunchHandshake.DefaultTimeout, asked);
    }

    [Fact]
    public void The_two_failure_messages_differ_and_both_say_what_to_do()
    {
        var noChannel = SecondLaunchHandshake.MessageFor(SecondLaunchOutcome.NoChannel);
        var silent = SecondLaunchHandshake.MessageFor(SecondLaunchOutcome.NotAcknowledged);

        Assert.NotEqual(noChannel, silent);
        Assert.Contains("Task Manager", noChannel, StringComparison.Ordinal);
        Assert.Contains("Task Manager", silent, StringComparison.Ordinal);
    }
}
