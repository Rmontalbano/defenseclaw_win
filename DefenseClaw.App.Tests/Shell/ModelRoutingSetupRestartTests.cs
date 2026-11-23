using DefenseClaw.App.Services;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// Whether the review says <c>setup routing</c> and <c>setup tokenomics</c> restart the gateway: the <c>setup</c> group's result callback
/// (cmd_setup.py@1.0.10 <c>_auto_restart_sidecar_after_setup</c>) restarts only after a change to a restart key.
/// </summary>
public class ModelRoutingSetupRestartTests
{
    [Theory]
    [InlineData("setup tokenomics")]
    [InlineData("setup tokenomics --enable")]
    [InlineData("setup tokenomics --disable")]
    [InlineData("setup tokenomics --status")]
    [InlineData("setup tokenomics --seed-pricing")]
    [InlineData("setup tokenomics --status --json")]
    public void Setup_tokenomics_writes_hot_keys_and_never_restarts(string commandLine)
    {
        Assert.False(CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
    }

    [Theory]
    [InlineData("setup routing")]
    [InlineData("setup routing --status")]
    [InlineData("setup routing --status --yes")]
    [InlineData("setup routing --yes")]
    public void Setup_routing_that_only_prints_the_status_restarts_nothing(string commandLine)
    {
        Assert.False(CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
    }

    [Theory]
    [InlineData("setup routing --enable")]
    [InlineData("setup routing --enable --yes")]
    [InlineData("setup routing --disable")]
    [InlineData("setup routing --disable --yes")]
    public void Setup_routing_enable_and_disable_write_a_restart_key_and_keep_the_restart_answer(string commandLine)
    {
        Assert.True(CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
    }

    [Fact]
    public void Other_setup_commands_are_unchanged()
    {
        Assert.True(CommandReview.RestartsGatewayFor(["setup", "guardrail"]));
        Assert.True(CommandReview.RestartsGatewayFor(["setup", "claude-code"]));
        Assert.False(CommandReview.RestartsGatewayFor(["setup", "decision-model", "--show"]));
        Assert.False(CommandReview.RestartsGatewayFor(["setup", "routing", "--enable", "--no-restart"]));
        Assert.False(CommandReview.RestartsGatewayFor(["routing", "status"]));
    }
}
