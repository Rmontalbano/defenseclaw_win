using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What the review says about a gateway restart for the two setup commands whose answer is not "yes, always": <c>setup decision-model</c> (every write
/// is hot-reloaded, cmd_setup.py@1.0.8:1576) and <c>setup cowork</c> (restarts only when it has to set up the Claude Code connector first,
/// cmd_setup_cowork.py@1.0.8:56-79,225-235).
/// </summary>
public class SetupRestartPredictionTests
{
    [Theory]
    [InlineData("setup decision-model --enable")]
    [InlineData("setup decision-model --show")]
    [InlineData("setup decision-model --model gpt-x --yes")]
    [InlineData("setup decision-model --disable")]
    public void Setup_decision_model_never_restarts_the_gateway(string commandLine)
    {
        Assert.False(CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
    }

    [Fact]
    public void Other_setup_verbs_still_restart_the_gateway_as_before()
    {
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "setup", "claude-code" }));
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "setup", "guardrail" }));
        Assert.False(CommandReview.RestartsGatewayFor(new[] { "setup", "guardrail", "--no-restart" }));
    }

    [Fact]
    public void Setup_cowork_keeps_the_restart_badge_but_says_may()
    {
        var argv = new[] { "setup", "cowork" };

        Assert.True(CommandReview.RestartsGatewayFor(argv));
        var notice = CommandReview.RestartNoticeFor(argv);
        Assert.Equal(CommandReview.CoworkRestartNotice, notice);
        Assert.StartsWith("This may restart the DefenseClaw gateway", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandReview.RestartSentence, notice, StringComparison.Ordinal);
        Assert.Contains("Claude Code connector is not set up yet", notice, StringComparison.Ordinal);
        Assert.Contains("nothing restarts", notice, StringComparison.Ordinal);
        Assert.False(CommandReview.RestartsGatewayFor(argv.Append("--no-restart").ToArray()));
    }

    [Theory]
    [InlineData("setup claude-code")]
    [InlineData("setup guardrail")]
    [InlineData("guardrail enable")]
    [InlineData("setup llm --provider x")]
    public void Every_other_restarting_command_keeps_the_definite_sentence(string commandLine)
    {
        var argv = commandLine.Split(' ');

        Assert.True(CommandReview.RestartsGatewayFor(argv));
        Assert.Equal(CommandReview.RestartNotice, CommandReview.RestartNoticeFor(argv));
        Assert.StartsWith(CommandReview.RestartSentence, CommandReview.RestartNoticeFor(argv), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wizard_restart_paragraph_for_cowork_is_the_conditional_one()
    {
        var definition = WizardSamples.ClaudeCode();
        var values = WizardSamples.StartingValues(definition);

        var warning = WizardReview.RestartWarning(definition, values, new[] { "setup", "cowork" });

        Assert.StartsWith(CommandReview.CoworkRestartNotice, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_restarting_command_in_a_wizard_keeps_the_definite_paragraph()
    {
        var definition = WizardSamples.Llm();
        var values = WizardSamples.StartingValues(definition);

        Assert.Equal(CommandReview.RestartNotice, WizardReview.RestartWarning(definition, values, new[] { "setup", "llm", "--provider", "x" }));
    }
}
