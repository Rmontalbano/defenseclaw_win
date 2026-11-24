using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// Hook-failure rows whose details end <c>; see logs/hook-failures.dropped</c> (the hook could not finish its usual hook-failures.jsonl record; written since
/// DefenseClaw 1.0.9): read like the .jsonl ones, told apart by the file they name, and never counted as a policy block. The .jsonl rows are unchanged.
/// </summary>
public sealed class HookFailureDroppedTailTests
{
    private const string Jsonl = "; see logs/hook-failures.jsonl";
    private const string Dropped = "; see logs/hook-failures.dropped";
    private const string Body = "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: \"gateway unreachable\"";

    [Fact]
    public void A_dropped_tail_row_reads_like_the_jsonl_one_and_says_which_file()
    {
        var jsonl = HookFailureRow.Parse("guardrail-block", Body + Jsonl)!;
        var dropped = HookFailureRow.Parse("guardrail-block", Body + Dropped)!;

        Assert.False(jsonl.Dropped);
        Assert.True(dropped.Dropped);
        Assert.Equal(jsonl.Kind, dropped.Kind);
        Assert.Equal("PreToolUse", dropped.Event);
        Assert.Equal("gateway unreachable", dropped.Reason);
        Assert.Equal(jsonl.Headline, dropped.Headline);
        Assert.True(dropped.IsRefusal);
    }

    [Fact]
    public void The_explanation_names_the_dropped_file_and_the_jsonl_text_is_byte_for_byte_what_it_was()
    {
        var jsonl = HookFailureRow.Parse("guardrail-block", Body + Jsonl)!;
        Assert.EndsWith(" Logged in logs\\hook-failures.jsonl.", jsonl.Explanation, StringComparison.Ordinal);
        Assert.Equal(
            "The gateway did not answer and this connector's hook_fail_mode is closed, so the hook blocked it. No gateway policy decided it. Logged in logs\\hook-failures.jsonl.",
            jsonl.Explanation);

        var dropped = HookFailureRow.Parse("guardrail-block", Body + Dropped)!;
        Assert.EndsWith(" The usual record could not be written, so the call is logged in logs\\hook-failures.dropped.", dropped.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("The gateway did not answer", dropped.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("guardrail-warn", "unchecked hook call during gateway outage: \"gateway unreachable\"", HookFailureKind.RanUnchecked)]
    [InlineData("guardrail-block", "the tool had already run; the hook blocked its result (fail-closed, PostToolUse), not the call: \"timeout\"", HookFailureKind.ResultRefused)]
    [InlineData("guardrail-block", "hook blocked its UserPromptSubmit event itself (fail-closed), not a tool call: \"down\"", HookFailureKind.EventRefused)]
    public void Every_kind_is_read_on_both_tails(string action, string body, HookFailureKind kind)
    {
        foreach (var tail in new[] { Jsonl, Dropped })
        {
            var row = HookFailureRow.Parse(action, body + tail)!;
            Assert.Equal(kind, row.Kind);
            Assert.False(string.IsNullOrEmpty(row.Reason));
            Assert.Equal(tail == Dropped, row.Dropped);
        }
    }

    [Fact]
    public void A_dropped_tail_refusal_is_no_policy_block_and_a_look_alike_still_is()
    {
        Assert.True(HookFailureRow.IsRefusal("guardrail-block", Body + Dropped));
        Assert.False(RecentAuditMetricsReader.IsBlock("guardrail-block", Body + Dropped));
        Assert.False(RecentAuditMetricsReader.IsBlock("guardrail-block", Body + Jsonl));

        // No fail-closed mark and a different tail: a policy block.
        Assert.False(HookFailureRow.IsRefusal("guardrail-block", "policy decided this; see logs/hook-failures.other"));
        Assert.True(RecentAuditMetricsReader.IsBlock("guardrail-block", "policy decided this; see logs/hook-failures.other"));
        Assert.Null(HookFailureRow.Parse("guardrail-block", Body));
    }

    [Fact]
    public void The_refusal_sql_names_both_tails()
    {
        Assert.Contains("LIKE '%; see logs/hook-failures.jsonl'", HookFailureRow.RefusalSql, StringComparison.Ordinal);
        Assert.Contains("LIKE '%; see logs/hook-failures.dropped'", HookFailureRow.RefusalSql, StringComparison.Ordinal);
        Assert.StartsWith("(action = 'guardrail-block' AND ", HookFailureRow.RefusalSql, StringComparison.Ordinal);
    }
}
