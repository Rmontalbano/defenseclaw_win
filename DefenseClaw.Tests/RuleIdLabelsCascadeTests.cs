using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// The rule-id sentences of the guardrail cascade era (DefenseClaw 1.0.10): the over-limit rule on every surface and the alignment check. The 1.0.9 era's text is
/// pinned as it was: a runtime without the cascade never reads the new words, and the runtime's own rule title is never replaced.
/// </summary>
public sealed class RuleIdLabelsCascadeTests
{
    private const string OldLimit = "A shell command too long or too complex for the policy check.";
    private const string NewLimit =
        "An input too large or too complex for the policy check to read in full (a command, a tool's output or a model reply); DefenseClaw judged it at the rule pack's unknown tier.";
    private const string Drift = "The alignment check (a question to the LLM judge) found that this tool call does not serve the task the user gave.";

    [Fact]
    public void The_cascade_era_words_the_limit_for_every_surface_and_adds_the_alignment_check()
    {
        Assert.Equal(NewLimit, RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT", cascadeEra: true));
        Assert.Equal(NewLimit, RuleIdLabels.NewSentence("action-compound-limit", cascadeEra: true));
        Assert.Equal(Drift, RuleIdLabels.Sentence("ALIGNMENT-DRIFT", cascadeEra: true));
        Assert.Equal(Drift, RuleIdLabels.Sentence(" alignment-drift ", cascadeEra: true));
    }

    [Fact]
    public void The_operand_bound_sentence_and_the_other_ids_are_the_same_in_both_eras()
    {
        foreach (var id in new[] { "ACTION-OPERAND-BOUND", "ACTION-SHARE-BODY-UNREAD", "TOOL-PATH-UNVERIFIED", "tamper.agent_hook_config_write", "CMD-X" })
        {
            Assert.Equal(RuleIdLabels.Sentence(id), RuleIdLabels.Sentence(id, cascadeEra: true));
        }

        Assert.Contains("more than 1024 operands", RuleIdLabels.Sentence("ACTION-OPERAND-BOUND", cascadeEra: true), StringComparison.Ordinal);
        Assert.Null(RuleIdLabels.Sentence("CMD-X", cascadeEra: true));
    }

    [Fact]
    public void An_older_runtime_reads_the_old_words_and_never_the_alignment_id()
    {
        Assert.Equal(OldLimit, RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"));
        Assert.Equal(OldLimit, RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT", cascadeEra: false));
        Assert.Null(RuleIdLabels.Sentence("ALIGNMENT-DRIFT"));
        Assert.Null(RuleIdLabels.Sentence("ALIGNMENT-DRIFT", cascadeEra: false));
        Assert.False(RuleIdLabels.Has("ALIGNMENT-DRIFT"));
        Assert.Empty(RuleIdLabels.NewSentencesIn("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)"));
        Assert.Empty(RuleIdLabels.NewSentencesIn("ALIGNMENT-DRIFT", cascadeEra: false));
        Assert.Equal("ALIGNMENT-DRIFT", RuleIdLabels.Replace("ALIGNMENT-DRIFT"));
    }

    [Fact]
    public void A_free_text_names_the_alignment_id_only_in_the_cascade_era()
    {
        var text = "ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check), ACTION-COMPOUND-LIMIT";
        Assert.Equal(new[] { Drift, NewLimit }, RuleIdLabels.NewSentencesIn(text, cascadeEra: true));
        Assert.Equal(new[] { OldLimit }, RuleIdLabels.NewSentencesIn(text, cascadeEra: false));
        Assert.Equal(new[] { Drift }, RuleIdLabels.NewSentencesIn("llm-judge:ALIGNMENT-DRIFT", cascadeEra: true));
        Assert.Empty(RuleIdLabels.NewSentencesIn("ALIGNMENT-DRIFT-2", cascadeEra: true));
        Assert.Equal(new[] { Drift, NewLimit }, RuleIdLabels.NewSentencesOf(new[] { "ALIGNMENT-DRIFT", "ACTION-COMPOUND-LIMIT", "ALIGNMENT-DRIFT", "CMD-X" }, cascadeEra: true));
        Assert.True(RuleIdLabels.MentionsAlignmentDrift("rule=ALIGNMENT-DRIFT"));
        Assert.True(RuleIdLabels.IsAlignmentDrift("alignment-drift"));
        Assert.False(RuleIdLabels.IsAlignmentDrift("ALIGNMENT-DRIFT-2"));
    }

    [Theory]
    [InlineData("blocked", "mode=action", "It was blocked.", "The call was blocked.")]
    [InlineData(null, "action=block raw_action=block mode=action would_block=true", "It was blocked.", "The call was blocked.")]
    [InlineData(null, "action=alert raw_action=alert mode=observe", "It went through; DefenseClaw recorded it as an alert (observe mode).", "The call ran; DefenseClaw recorded it as an alert (observe mode).")]
    [InlineData("alert", "mode=action", "It went through; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", "The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).")]
    [InlineData("allow", "raw_action=alert mode=action", "It went through; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", "The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).")]
    public void The_limit_outcome_is_worded_for_every_surface_in_the_cascade_era_and_as_before_otherwise(string? decision, string details, string cascade, string before)
    {
        Assert.Equal(cascade, RuleIdLabels.CompoundLimitOutcome(decision, details, cascadeEra: true));
        Assert.Equal(before, RuleIdLabels.CompoundLimitOutcome(decision, details, cascadeEra: false));
        Assert.Equal(before, RuleIdLabels.CompoundLimitOutcome(decision, details));
    }

    [Fact]
    public void A_limit_row_that_says_neither_has_no_outcome_in_either_era()
    {
        Assert.Null(RuleIdLabels.CompoundLimitOutcome(null, "connector=codex action=allow raw_action=allow", cascadeEra: true));
        Assert.Null(RuleIdLabels.CompoundLimitOutcome(null, null, cascadeEra: true));
    }

    [Theory]
    [InlineData("allow", null, "Measured only: the call was not changed (the alignment lane records without acting in shadow mode, and observe mode never acts).")]
    [InlineData(null, "connector=codex action=allow raw_action=allow mode=observe", "Measured only: the call was not changed (the alignment lane records without acting in shadow mode, and observe mode never acts).")]
    [InlineData(null, "connector=codex mode=shadow would_block=false", "Measured only: the call was not changed (the alignment lane records without acting in shadow mode, and observe mode never acts).")]
    [InlineData("alert", null, "The call ran; DefenseClaw recorded an alert.")]
    [InlineData(null, "action=alert raw_action=alert", "The call ran; DefenseClaw recorded an alert.")]
    [InlineData("blocked", null, "The call was blocked.")]
    [InlineData(null, "action=deny", "The call was blocked.")]
    public void The_alignment_outcome_comes_from_the_rows_own_fields(string? decision, string? details, string expected) =>
        Assert.Equal(expected, RuleIdLabels.AlignmentDriftOutcome(decision, details));

    [Fact]
    public void An_alignment_row_with_no_decision_says_nothing_about_the_outcome()
    {
        Assert.Null(RuleIdLabels.AlignmentDriftOutcome(null, null));
        Assert.Null(RuleIdLabels.AlignmentDriftOutcome(null, "connector=codex mode=action"));
        Assert.Null(RuleIdLabels.AlignmentDriftOutcome("would block (observe mode)", null));
    }
}
