using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// The sentences for the rule ids DefenseClaw 1.0.9 added to hook rows (rule labels), what the shell-command limit rule says about the call from the row's own fields,
/// the runtime's <c>surface</c> key and the Cowork mark it gives a runtime-only row. Synthetic rows in the shape the upstream source writes.
/// </summary>
public sealed class RuleIdLabelsTests
{
    [Theory]
    [InlineData("ACTION-OPERAND-BOUND", "more than 1024 operands")]
    [InlineData("ACTION-SHARE-BODY-UNREAD", "network share could not be read")]
    [InlineData("TOOL-PATH-UNVERIFIED", "Windows file path")]
    [InlineData("tamper.agent_hook_config_disable", "turns off the agent's DefenseClaw hooks")]
    [InlineData("tamper.agent_hook_config_write", "writes to an agent's hook settings file")]
    [InlineData("ACTION-COMPOUND-LIMIT", "too long or too complex")]
    public void Each_new_rule_id_has_a_plain_sentence(string id, string fragment)
    {
        Assert.Contains(fragment, RuleIdLabels.Sentence(id), StringComparison.Ordinal);
        Assert.Equal($"{id} ({RuleIdLabels.Sentence(id)})", RuleIdLabels.WithLabel(id));
        Assert.True(RuleIdLabels.Has(id.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("CMD-X")]
    [InlineData("persistence.shell_profile_write")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_id_is_shown_raw(string? id)
    {
        Assert.Null(RuleIdLabels.Sentence(id));
        Assert.Equal(id ?? string.Empty, id is null ? string.Empty : RuleIdLabels.WithLabel(id));
        Assert.False(RuleIdLabels.Has(id));
    }

    [Fact]
    public void The_judge_unavailable_id_keeps_its_own_label()
    {
        Assert.Equal(JudgeEscalationFinding.WithLabel("JUDGE-UNAVAILABLE-ESCALATED"), RuleIdLabels.WithLabel("JUDGE-UNAVAILABLE-ESCALATED"));
        Assert.Equal(JudgeEscalationFinding.Label, RuleIdLabels.Sentence("llm-judge:JUDGE-UNAVAILABLE-ESCALATED"));
    }

    [Fact]
    public void A_text_names_the_ids_it_holds_and_is_labelled_in_place()
    {
        var text = "ACTION-COMPOUND-LIMIT, CMD-X, tamper.agent_hook_config_write";
        Assert.True(RuleIdLabels.Mentions(text));
        var labelled = RuleIdLabels.Replace(text);
        Assert.Contains("ACTION-COMPOUND-LIMIT (A shell command too long or too complex for the policy check.)", labelled, StringComparison.Ordinal);
        Assert.Contains(", CMD-X,", labelled, StringComparison.Ordinal);
        Assert.Contains("tamper.agent_hook_config_write (A shell command that writes", labelled, StringComparison.Ordinal);
        Assert.False(RuleIdLabels.Mentions("ACTION-COMPOUND-LIMIT-2"));
        Assert.Equal("CMD-X", RuleIdLabels.Replace("CMD-X"));
    }

    [Fact]
    public void The_runtimes_rule_value_gives_its_id_and_a_new_sentence_never_covers_the_judge_id()
    {
        Assert.Equal("ACTION-COMPOUND-LIMIT", RuleIdLabels.IdOfRuleFact("ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit"));
        Assert.Equal(RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"), RuleIdLabels.NewSentence("ACTION-COMPOUND-LIMIT"));
        Assert.Null(RuleIdLabels.NewSentence("CMD-X"));
        Assert.Null(RuleIdLabels.NewSentence("JUDGE-UNAVAILABLE-ESCALATED"));
        Assert.Null(RuleIdLabels.NewSentence("llm-judge:JUDGE-UNAVAILABLE-ESCALATED"));
        Assert.Equal(RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"), Assert.Single(RuleIdLabels.NewSentencesIn("ACTION-COMPOUND-LIMIT: Shell command exceeds, ACTION-COMPOUND-LIMIT, JUDGE-UNAVAILABLE-ESCALATED")));
        Assert.Empty(RuleIdLabels.NewSentencesIn("CMD-X"));
    }

    // ------------------------------------------------------------------ the call outcome

    [Theory]
    [InlineData("alert", "connector=codex result=ok action=alert raw_action=alert mode=action would_block=false")]
    [InlineData(null, "connector=codex result=ok action=alert mode=action would_block=false")]
    [InlineData("alert", null)]
    public void An_alert_row_says_the_call_ran_from_its_own_fields(string? decision, string? details)
    {
        var outcome = RuleIdLabels.CompoundLimitOutcome(decision, details);
        Assert.Equal("The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", outcome);
    }

    [Theory]
    [InlineData("allow", "connector=codex action=allow raw_action=alert mode=observe would_block=false")]
    [InlineData(null, "connector=codex action=allowed raw_action=alert mode=observe")]
    public void An_observe_mode_alert_row_says_the_call_ran_without_the_strict_pack_clause(string? decision, string? details) =>
        Assert.Equal("The call ran; DefenseClaw recorded it as an alert (observe mode).", RuleIdLabels.CompoundLimitOutcome(decision, details));

    [Theory]
    [InlineData("blocked", null)]
    [InlineData(null, "connector=codex action=block raw_action=block mode=action would_block=true")]
    [InlineData(null, "connector=codex action=deny")]
    public void A_blocked_row_says_it_was_blocked(string? decision, string? details) =>
        Assert.Equal("The call was blocked.", RuleIdLabels.CompoundLimitOutcome(decision, details));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "connector=codex mode=action")]
    [InlineData("allow", "connector=codex action=allow mode=observe")]
    [InlineData("would block (observe mode)", "connector=codex action=allow raw_action=block mode=observe would_block=true")]
    public void A_row_without_these_fields_gets_only_the_label(string? decision, string? details) =>
        Assert.Null(RuleIdLabels.CompoundLimitOutcome(decision, details));

    // ------------------------------------------------------------------ surface

    private const string Row =
        "{\"id\":\"a-1\",\"timestamp\":\"2026-11-10T10:00:00Z\",\"severity\":\"HIGH\",\"action\":\"connector-hook\",\"target\":\"claudecode:PreToolUse\"," +
        "\"actor\":\"gateway\",\"connector\":\"claudecode\",\"details\":\"x\"@@}";

    [Fact]
    public void A_surface_key_is_read_as_a_fact_and_marks_a_claudecode_row_as_cowork()
    {
        var row = RuntimeAlertSnapshotParser.Parse("[" + Row.Replace("@@", ",\"surface\":\"cowork\"") + "]").Rows.Single();
        Assert.Equal("cowork", row.Facts.Single(f => f.Name == "surface").Value);
        Assert.Equal("claudecode:PreToolUse", row.Target);
        Assert.NotNull(CoworkRow.FromRuntimeSurface(row.Connector, row.Facts.Single(f => f.Name == "surface").Value));
    }

    [Theory]
    [InlineData("claudecode", "cowork", true)]
    [InlineData("claudecode", "COWORK", true)]
    [InlineData("claudecode", " cowork ", true)]
    [InlineData("claudecode", "", false)]
    [InlineData("claudecode", null, false)]
    [InlineData("claudecode", "desktop", false)]
    [InlineData("codex", "cowork", false)]
    [InlineData(null, "cowork", false)]
    public void Only_a_claudecode_row_with_the_cowork_surface_is_marked(string? connector, string? surface, bool marked) =>
        Assert.Equal(marked, CoworkRow.FromRuntimeSurface(connector, surface) is not null);

    [Fact]
    public void A_1_0_8_row_without_surface_has_no_such_fact()
    {
        var row = RuntimeAlertSnapshotParser.Parse("[" + Row.Replace("@@", string.Empty) + "]").Rows.Single();
        Assert.DoesNotContain(row.Facts, f => f.Name == "surface");
        var empty = RuntimeAlertSnapshotParser.Parse("[" + Row.Replace("@@", ",\"surface\":\"\"") + "]").Rows.Single();
        Assert.DoesNotContain(empty.Facts, f => f.Name == "surface");
    }

    [Theory]
    [InlineData("claudecode:PreToolUse", "cowork:PreToolUse")]
    [InlineData("Claudecode:PostToolUse", "cowork:PostToolUse")]
    [InlineData("claudecode:", "")]
    [InlineData("PreToolUse", "")]
    [InlineData(null, "")]
    public void The_stored_target_is_shown_in_the_cowork_spelling_of_the_tui(string? stored, string expected) =>
        Assert.Equal(expected, CoworkRow.RetargetStored(stored));

    // ------------------------------------------------------------------ the protected-file card's copy

    [Fact]
    public void The_windows_coverage_sentence_is_unchanged_and_the_shell_floor_sentence_says_the_three_facts()
    {
        Assert.Equal("Shell startup files and autostart entries are watched on Linux and macOS only.", ProtectedFileWatchFacts.WindowsCoverage);

        var text = ProtectedFileWatchFacts.ShellFloorCoverage;
        Assert.Contains("On Windows too", text, StringComparison.Ordinal);
        Assert.Contains(@"~\.ssh\authorized_keys", text, StringComparison.Ordinal);
        Assert.Contains("at least an alert in every rule pack, permissive included", text, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw's own files are blocked in every pack", text, StringComparison.Ordinal);
        Assert.Contains("In action mode", text, StringComparison.Ordinal);
        Assert.Contains("the strict pack blocks it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("observe", text, StringComparison.OrdinalIgnoreCase);
    }
}
