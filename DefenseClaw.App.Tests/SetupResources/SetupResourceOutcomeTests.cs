using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// CUST-270 and the Activity footer (CUST-264): the Setup editors read a list every time their window opens, and each read is an Activity entry. The
/// footer claims "config reloaded" only on evidence, and a list that was only looked at is not evidence: the first-verb classifier calls every
/// <c>setup</c> command a change, so the footer asks the guard, which knows these reads by their whole shape.
/// </summary>
public sealed class SetupResourceOutcomeTests
{
    private static readonly DateTimeOffset T0 = new(2030, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static CliInvocation Finished(int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor("defenseclaw", argv, T0);
        InvocationFactory.Finish(invocation, exit, T0.AddSeconds(3));
        return invocation;
    }

    [Theory]
    [InlineData("setup", "observability", "list", "--json")]
    [InlineData("setup", "webhook", "list", "--json")]
    [InlineData("setup", "trusted-paths", "list", "--json")]
    [InlineData("setup", "webhook", "show", "--json", "--", "example-slack")]
    public void A_list_the_editor_read_reloaded_nothing_and_the_footer_does_not_say_it_did(params string[] argv)
    {
        var outcome = CommandOutcome.Of(Finished(0, argv));

        Assert.False(outcome.ConfigReloaded);
        Assert.False(outcome.GatewayRestarted);
        Assert.DoesNotContain("config reloaded", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup", "observability", "enable", "--", "example-otlp")]
    [InlineData("setup", "observability", "disable", "--", "example-otlp")]
    [InlineData("setup", "webhook", "enable", "--", "example-slack")]
    [InlineData("setup", "webhook", "remove", "--yes", "--", "example-slack")]
    [InlineData("setup", "observability", "remove", "--yes", "--", "example-otlp")]
    [InlineData("setup", "trusted-paths", "remove", "--", @"C:\Users\synthetic\.synthetic-tools\bin")]
    public void A_change_the_editor_made_still_says_the_config_was_reloaded(params string[] argv)
    {
        var outcome = CommandOutcome.Of(Finished(0, argv));

        Assert.True(outcome.ConfigReloaded);
        Assert.StartsWith("config reloaded", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_at_a_webhook_named_like_a_verb_is_still_only_a_look_and_a_removal_of_it_is_still_a_change()
    {
        // The name comes after the end of the options, so it cannot turn one into the other.
        Assert.False(CommandOutcome.Of(Finished(0, "setup", "webhook", "show", "--json", "--", "remove")).ConfigReloaded);
        Assert.True(CommandOutcome.Of(Finished(0, "setup", "webhook", "remove", "--yes", "--", "list")).ConfigReloaded);
    }
}
