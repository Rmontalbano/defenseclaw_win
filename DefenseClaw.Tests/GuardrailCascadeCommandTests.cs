using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.Tests;

/// <summary>
/// The command side of the cascade (runtime 1.0.10): <c>guardrail cascade explain</c> is a read by its exact path (cmd_guardrail.py@1.0.10:6751-6788
/// shells to <c>defenseclaw-gateway policy cascade</c>, which loads config only), the Policies panel's one door lets exactly that argv through, and
/// the severity matrix floors a proven CRITICAL at <c>admin.critical_floor</c> only when asked to (so older runtimes' text is unchanged).
/// </summary>
public sealed class GuardrailCascadeCommandTests
{
    [Theory]
    [InlineData("guardrail", "cascade", "explain", "--connector", "codex", "--json")]
    [InlineData("guardrail", "cascade", "explain", "--json")]
    [InlineData("guardrail", "cascade", "explain")]
    [InlineData("guardrail", "cascade", "explain", "--profile", "strict-team", "--json")]

    // Replaying a decision from a file or the gateway's record only prints; it writes nothing.
    [InlineData("guardrail", "cascade", "explain", "--input", "decision.json", "--json")]
    [InlineData("guardrail", "cascade", "explain", "--last", "1", "--json")]
    public void Cascade_explain_is_read_only_by_its_exact_path(params string[] argv)
    {
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv));
    }

    [Theory]
    [InlineData("guardrail", "cascade")]
    [InlineData("guardrail", "cascade", "--json")]
    [InlineData("guardrail", "cascade", "explain", "now")]
    [InlineData("guardrail", "cascade", "explain", "--reveal")]
    [InlineData("guardrail", "cascade", "set", "unknown", "block")]
    [InlineData("guardrail", "cascade", "enable")]
    [InlineData("guardrail", "cascades", "explain")]
    public void Nothing_near_it_inherits_the_exemption(params string[] argv)
    {
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(argv));
    }

    [Fact]
    public void Cascade_explain_is_not_on_the_list_of_commands_that_run_with_no_review()
    {
        Assert.False(CommandTiers.IsUnreviewedRead(new[] { "guardrail", "cascade", "explain" }));
        Assert.DoesNotContain("guardrail cascade explain", CommandTiers.UnreviewedReadPaths);
    }

    // ---- the Policies panel's door -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_argv_is_built_from_constants_and_a_connector_name_of_the_usual_form()
    {
        Assert.Equal(new[] { "guardrail", "cascade", "explain", "--json" }, PolicyActionGuard.CascadeExplain(null));
        Assert.Equal(new[] { "guardrail", "cascade", "explain", "--connector", "codex", "--json" }, PolicyActionGuard.CascadeExplain("codex"));
        Assert.Equal(new[] { "guardrail", "cascade", "explain", "--connector", "claude-code_2", "--json" }, PolicyActionGuard.CascadeExplain("claude-code_2"));
        Assert.True(PolicyActionGuard.IsAllowedRead(PolicyActionGuard.CascadeExplain("codex")));
        Assert.True(PolicyActionGuard.IsAllowedRead(PolicyActionGuard.CascadeExplain(null)));
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(PolicyActionGuard.CascadeExplain("codex")));
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("--json")]
    [InlineData("a b")]
    [InlineData("codex\n")]
    [InlineData("-x")]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("..\\x")]
    public void A_connector_name_of_another_form_is_left_out_so_the_global_scope_is_asked(string connector)
    {
        Assert.Equal(new[] { "guardrail", "cascade", "explain", "--json" }, PolicyActionGuard.CascadeExplain(connector));
    }

    [Fact]
    public void The_one_door_lets_only_those_shapes_through()
    {
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain", "--input", "x", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain", "--connector", "Codex", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain", "--connector", "codex", "--json", "--reveal" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain", "--profile", "p", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "set", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "cascade", "explain", "--connector", "--json", "--json" }));
    }

    // ---- the severity matrix ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_a_floor_the_matrix_and_the_summary_are_exactly_what_they_were()
    {
        var before = PolicyLevels.MatrixLines("action", "none", "none", "off");

        Assert.Equal(before, PolicyLevels.MatrixLines("action", "none", "none", "off", null));
        Assert.Equal("  CRITICAL  allow", before[0]);
        Assert.Equal(
            PolicyLevels.PostureSummary("global", "action", "none", "none", "off"),
            PolicyLevels.PostureSummary("global", "action", "none", "none", "off", null));
        Assert.Equal(PolicyLevels.SeverityActions("none", "none", "off"), PolicyLevels.SeverityActions("none", "none", "off", null));
    }

    [Fact]
    public void A_critical_that_would_be_allowed_ends_at_alert_with_the_keys_name()
    {
        var lines = PolicyLevels.MatrixLines("action", "none", "none", "off", "alert");

        Assert.Equal("  CRITICAL  alert (admin.critical_floor)", lines[0]);
        Assert.Equal("  HIGH      allow", lines[1]);
        Assert.Equal(("CRITICAL", "alert"), PolicyLevels.SeverityActions("none", "none", "off", "alert")[0]);
    }

    [Fact]
    public void A_floor_of_log_is_the_explicit_opt_out_and_changes_nothing()
    {
        Assert.Equal(PolicyLevels.MatrixLines("action", "none", "none", "off"), PolicyLevels.MatrixLines("action", "none", "none", "off", "log"));
    }

    [Theory]
    [InlineData("confirm", "  CRITICAL  ask a human (admin.critical_floor)")]
    [InlineData("block", "  CRITICAL  block (admin.critical_floor)")]
    public void A_stricter_floor_raises_critical_to_what_it_names(string floor, string expected)
    {
        Assert.Equal(expected, PolicyLevels.MatrixLines("action", "none", "alert", "off", floor)[0]);
    }

    [Fact]
    public void The_floor_never_lowers_and_a_row_it_leaves_alone_has_no_note()
    {
        var lines = PolicyLevels.MatrixLines("action", "CRITICAL", "MEDIUM", "off", "alert");

        Assert.Equal("  CRITICAL  block", lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains("admin.critical_floor", StringComparison.Ordinal));
        Assert.Equal(PolicyLevels.MatrixLines("action", "CRITICAL", "MEDIUM", "off"), lines);
    }

    [Fact]
    public void In_observe_mode_a_raised_row_that_would_block_is_still_a_log()
    {
        Assert.Equal("  CRITICAL  log (would block) (admin.critical_floor)", PolicyLevels.MatrixLines("observe", "none", "none", "off", "block")[0]);
    }

    [Fact]
    public void The_posture_context_carries_whether_the_cascade_is_set_and_its_floor()
    {
        var set = PostureCascade.Of(GuardrailCascade.Parse("guardrail:\n  cascade:\n    admin:\n      critical_floor: confirm\n"));
        var none = PostureCascade.Of(GuardrailCascade.Default);

        Assert.True(set.Set);
        Assert.Equal("confirm", set.CriticalFloor);
        Assert.False(none.Set);
        Assert.Equal("alert", none.CriticalFloor);
    }
}
