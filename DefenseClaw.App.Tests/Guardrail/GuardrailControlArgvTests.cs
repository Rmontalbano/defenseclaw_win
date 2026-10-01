using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Guardrail;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Guardrail;

/// <summary>The argv of each control, its tier, and whether it restarts the gateway (<c>CommandReview.RestartsGatewayFor</c>).</summary>
public class GuardrailControlArgvTests
{
    [Fact]
    public void Hilt_on_carries_the_severity_yes_scope_and_no_restart_in_the_order_the_cli_documents()
    {
        Assert.Equal(
            "guardrail hilt on --yes --min-severity HIGH --connector codex --no-restart",
            string.Join(' ', GuardrailControlArgv.Hilt(on: true, "high", "codex", restart: false)));
    }

    [Fact]
    public void Hilt_off_sends_no_severity_and_omits_scope_and_no_restart_when_there_are_none()
    {
        Assert.Equal("guardrail hilt off --yes", string.Join(' ', GuardrailControlArgv.Hilt(on: false, "HIGH", null, restart: true)));
    }

    [Fact]
    public void A_block_message_goes_after_a_double_dash_so_it_can_never_be_read_as_an_option_or_a_verb()
    {
        var argv = GuardrailControlArgv.BlockMessage("--clear list", "codex", restart: true);

        Assert.Equal(new[] { "guardrail", "block-message", "--yes", "--connector", "codex", "--", "--clear list" }, argv);
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(argv, CommandTier.StateChanging));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_message_is_clear_not_an_empty_argument(string? message)
    {
        Assert.Equal(new[] { "guardrail", "block-message", "--clear", "--yes" }, GuardrailControlArgv.BlockMessage(message, null, restart: true));
    }

    [Fact]
    public void Judge_add_and_remove_take_the_connector_as_the_argument_and_no_yes_because_the_cli_has_none()
    {
        Assert.Equal(
            "guardrail judge add hermes --enable --timeout 8 --no-restart",
            string.Join(' ', GuardrailControlArgv.JudgeAdd("hermes", alsoEnableJudge: true, 8, restart: false)));
        Assert.Equal("guardrail judge add all", string.Join(' ', GuardrailControlArgv.JudgeAdd("all", false, null, restart: true)));
        Assert.Equal("guardrail judge remove opencode", string.Join(' ', GuardrailControlArgv.JudgeRemove("opencode", restart: true)));
        Assert.DoesNotContain("--yes", GuardrailControlArgv.JudgeAdd("hermes", true, 5.5, true));
        Assert.Equal("5.5", GuardrailControlArgv.JudgeAdd("hermes", false, 5.5, true)[^1]);
    }

    [Fact]
    public void Tiers_of_the_verbs_are_never_read_only_and_judge_remove_is_destructive_by_the_classifier()
    {
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(GuardrailControlArgv.Hilt(true, "HIGH", null, true), CommandTier.StateChanging));
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(GuardrailControlArgv.JudgeAdd("hermes", false, null, true), CommandTier.StateChanging));
        Assert.Equal(CommandTier.Destructive, CommandReview.ResolveTier(GuardrailControlArgv.JudgeRemove("hermes", true), CommandTier.StateChanging));
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(GuardrailControlArgv.ReadJudge()));
    }

    [Theory]
    [InlineData("guardrail enable --yes", true)]
    [InlineData("guardrail enable --yes --no-restart", false)]
    [InlineData("guardrail disable --yes --connector codex", true)]
    [InlineData("guardrail fail-mode closed --yes", true)]
    [InlineData("guardrail fail-mode closed --yes --no-restart", false)]
    [InlineData("guardrail fail-mode", false)]
    [InlineData("guardrail hilt on --yes --min-severity HIGH", true)]
    [InlineData("guardrail hilt off --yes --connector codex --no-restart", false)]
    [InlineData("guardrail hilt", false)]
    [InlineData("guardrail hilt --connector codex", false)]
    [InlineData("guardrail block-message --yes -- hello", true)]
    [InlineData("guardrail block-message --clear --yes", true)]
    [InlineData("guardrail block-message --clear --yes --no-restart", false)]
    [InlineData("guardrail block-message", false)]
    [InlineData("guardrail judge add hermes", true)]
    [InlineData("guardrail judge add hermes --enable --timeout 8 --no-restart", false)]
    [InlineData("guardrail judge remove all", true)]
    [InlineData("guardrail judge list", false)]
    [InlineData("guardrail status", false)]
    [InlineData("guardrail status --connector codex", false)]
    [InlineData("guardrail list-packs", false)]
    [InlineData("guardrail frobnicate --yes", true)]
    public void Guardrail_verbs_restart_the_gateway_unless_they_only_read_or_say_no_restart(string argv, bool restarts)
    {
        Assert.Equal(restarts, CommandReview.RestartsGatewayFor(argv.Split(' ')));
    }

    [Fact]
    public void A_connector_named_like_the_no_restart_flag_does_not_turn_the_restart_off()
    {
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "guardrail", "hilt", "on", "--yes", "--connector", "--no-restart" }));
    }

    [Fact]
    public void A_message_that_spells_a_flag_after_the_double_dash_changes_nothing()
    {
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "guardrail", "block-message", "--yes", "--", "--no-restart" }));
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "guardrail", "block-message", "--yes", "--", "status" }));
    }

    [Fact]
    public void The_setup_behaviour_of_restarts_gateway_for_is_unchanged()
    {
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "setup", "guardrail" }));
        Assert.False(CommandReview.RestartsGatewayFor(new[] { "setup", "guardrail", "--no-restart" }));
        Assert.False(CommandReview.RestartsGatewayFor(new[] { "skill", "list" }));
        Assert.False(CommandReview.RestartsGatewayFor(Array.Empty<string>()));
    }
}
