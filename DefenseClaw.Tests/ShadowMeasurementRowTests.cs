using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// The default-on shadow measurement rows of DefenseClaw 1.0.10 (action guardrail-inspection): which rows they are, what the label says, and that
/// <see cref="ActionableRule"/> leaves them out of the actionable view even when their text says <c>would=block</c>. Synthetic text in the shapes the runtime writes.
/// </summary>
public sealed class ShadowMeasurementRowTests
{
    internal const string Effects =
        "lane=effects-shadow mode=shadow tier=t2 would=block sets=net program=curl kind=exfil rules=R1 reason=policy cost_us=120 effects=3 dropped=1";

    internal const string SecretFlow =
        "rule=SF-1 mode=shadow kind=token program=curl host=example.test sets=S1 source_digest=abc123 would=block connector=codex";

    private static AuditEvent Row(string action, string? target, string? details, string severity = "INFO") => new()
    {
        Id = "x-1",
        Timestamp = DateTimeOffset.UtcNow,
        RawTimestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = action,
        Target = target,
        Actor = "gateway",
        Details = details,
        Severity = severity,
        Bucket = "guardrail.evaluation",
    };

    [Fact]
    public void An_effects_lane_row_and_a_secret_flow_row_are_shadow_measurements()
    {
        Assert.True(ShadowMeasurementRow.IsShadowMeasurement("guardrail-inspection", "codex", Effects));
        Assert.True(ShadowMeasurementRow.IsShadowMeasurement("Guardrail-Inspection", "secret-flow:shadow", SecretFlow));
        Assert.True(ShadowMeasurementRow.IsShadowMeasurement("guardrail-inspection", "codex", "lane=effects-shadow"));
    }

    [Theory]
    [InlineData("guardrail-inspection", "codex", "lane=effects-shadowX mode=shadow would=block")]
    [InlineData("guardrail-inspection", "codex", "x lane=effects-shadow mode=shadow would=block")]
    [InlineData("guardrail-inspection", "secret-flow:shadowed", "rule=SF-1 would=block")]
    [InlineData("guardrail-inspection", "codex", "rule=SF-1 mode=shadow would=block")]
    [InlineData("guardrail-block", "codex", "lane=effects-shadow mode=shadow would=block")]
    [InlineData("guardrail-block", "secret-flow:shadow", "would=block")]
    [InlineData("", "secret-flow:shadow", "would=block")]
    [InlineData(null, null, null)]
    public void Any_other_row_is_not_one(string? action, string? target, string? details) =>
        Assert.False(ShadowMeasurementRow.IsShadowMeasurement(action, target, details));

    [Fact]
    public void The_would_token_is_read_as_a_whole_word_and_bounded()
    {
        Assert.Equal("block", ShadowMeasurementRow.Would(Effects));
        Assert.Equal("block", ShadowMeasurementRow.Would(SecretFlow));
        Assert.Equal(string.Empty, ShadowMeasurementRow.Would("lane=effects-shadow mode=shadow wouldx=block"));
        Assert.Equal(string.Empty, ShadowMeasurementRow.Would(null));
        Assert.Equal(32, ShadowMeasurementRow.Would("lane=effects-shadow would=" + new string('a', 200)).Length);
    }

    [Fact]
    public void The_label_says_nothing_was_changed_and_names_the_lane()
    {
        Assert.Equal("Shadow measurement (no action taken)", ShadowMeasurementRow.Headline);
        Assert.Equal(
            "What the effects lane would have done (would=block). The call was not changed.",
            ShadowMeasurementRow.Describe("guardrail-inspection", "codex", Effects));
        Assert.Equal(
            "What secret flow tracking would have done (would=block). The call was not changed.",
            ShadowMeasurementRow.Describe("guardrail-inspection", "secret-flow:shadow", SecretFlow));
        Assert.Equal(
            "What the effects lane would have done. The call was not changed.",
            ShadowMeasurementRow.Describe("guardrail-inspection", "codex", "lane=effects-shadow mode=shadow"));
        Assert.Null(ShadowMeasurementRow.Describe("guardrail-block", "codex", Effects));
    }

    [Fact]
    public void A_shadow_row_is_never_actionable_even_when_its_text_or_severity_would_make_it_so()
    {
        Assert.False(ActionableRule.IsActionable(Row("guardrail-inspection", "codex", Effects)));
        Assert.False(ActionableRule.IsActionable(Row("guardrail-inspection", "secret-flow:shadow", SecretFlow)));
        Assert.False(ActionableRule.IsActionable(Row("guardrail-inspection", "codex", Effects, "HIGH")));
        Assert.False(ActionableRule.IsActionable("INFO", "would=block", "guardrail-inspection", "codex", Effects));
        Assert.False(ActionableRule.IsActionable("CRITICAL", "x", "guardrail-inspection", "secret-flow:shadow", SecretFlow));
    }

    [Fact]
    public void A_row_that_only_looks_like_one_stays_actionable_under_the_old_rule()
    {
        // lane=effects-shadowX is not the lane, and a real block row that says would=block in its details is still a block.
        Assert.True(ActionableRule.IsActionable(Row("guardrail-inspection", "codex", "lane=effects-shadowX mode=shadow would=block")));
        Assert.True(ActionableRule.IsActionable(Row("guardrail-block", "codex", "lane=effects-shadow would=block")));
        Assert.True(ActionableRule.IsActionable(Row("guardrail-inspection", "codex", "x would=block")));
        Assert.True(ActionableRule.IsActionable("INFO", "would=block", "guardrail-block", "codex", "would=block"));
    }

    [Fact]
    public void The_shared_labels_name_the_row_and_leave_others_alone()
    {
        var notes = GuardrailRowNotes.For("guardrail-inspection", "codex", Effects);
        var note = Assert.Single(notes);
        Assert.Equal("Shadow measurement", note.Key);
        Assert.Equal(ShadowMeasurementRow.Describe("guardrail-inspection", "codex", Effects), note.Value);
        Assert.Equal("Shadow measurement (no action taken)", GuardrailRowNotes.Headline("guardrail-inspection", "secret-flow:shadow", SecretFlow));

        Assert.Empty(GuardrailRowNotes.For("connector-hook", "PreToolUse", "action=allow"));
        Assert.Null(GuardrailRowNotes.Headline("guardrail-block", "x", "would=block"));
    }
}
