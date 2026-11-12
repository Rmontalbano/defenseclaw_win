using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The hook-row readers of DefenseClaw 1.0.7 and 1.0.8 (CUST-449), over synthetic text in the shapes the upstream source writes: a hook's own refusal, the
/// judge-unavailable finding, <c>judge_fallback</c> on a hook row, Claude Cowork rows, the outage rule's wording per runtime, and the two block counts that must
/// not count a refusal no policy decided.
/// </summary>
public sealed class HookRowLabelsTests : IDisposable
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string Tail = "; see logs/hook-failures.jsonl";

    private const string CallRefused =
        "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: " +
        "\"gateway unreachable: dial tcp 127.0.0.1:18970: connect refused\"" + Tail;

    private const string ResultRefused =
        "the tool had already run; the hook blocked its result (fail-closed, PostToolUse), not the call: \"gateway timeout\"" + Tail;

    private const string EventRefused =
        "hook blocked its UserPromptSubmit event itself (fail-closed), not a tool call: \"gateway unreachable\"" + Tail;

    private const string Unchecked = "unchecked hook call during gateway outage: \"gateway unreachable\"" + Tail;

    private const string SessionId = "00000000-0000-4000-8000-000000000001";

    private readonly TestAuditDatabase _database = new();
    private int _row;

    public void Dispose() => _database.Dispose();

    private static RuntimeSnapshot Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    private static IReadOnlyDictionary<string, JsonElement> Structured(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ HookFailureRow

    [Fact]
    public void A_call_the_hook_refused_reads_its_event_and_reason()
    {
        var refusal = HookFailureRow.Parse("guardrail-block", CallRefused)!;

        Assert.Equal(HookFailureKind.CallRefused, refusal.Kind);
        Assert.Equal("PreToolUse", refusal.Event);
        Assert.Equal("gateway unreachable: dial tcp 127.0.0.1:18970: connect refused", refusal.Reason);
        Assert.True(refusal.IsRefusal);
        Assert.Contains("Hook blocked the call itself", refusal.Headline, StringComparison.Ordinal);
        Assert.Contains("hook_fail_mode is closed", refusal.Explanation, StringComparison.Ordinal);
        Assert.Contains(HookFailureRow.FailuresLog, refusal.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_the_hook_refused_and_a_refused_event_are_told_apart()
    {
        var result = HookFailureRow.Parse("guardrail-block", ResultRefused)!;
        Assert.Equal(HookFailureKind.ResultRefused, result.Kind);
        Assert.Equal("PostToolUse", result.Event);
        Assert.Equal("gateway timeout", result.Reason);
        Assert.Contains("tool's result", result.Headline, StringComparison.Ordinal);

        var prompt = HookFailureRow.Parse("guardrail-block", EventRefused)!;
        Assert.Equal(HookFailureKind.EventRefused, prompt.Kind);
        Assert.Equal("UserPromptSubmit", prompt.Event);
        Assert.Equal("gateway unreachable", prompt.Reason);
        Assert.Contains("UserPromptSubmit", prompt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_that_ran_unchecked_is_not_a_refusal()
    {
        var unchecked_ = HookFailureRow.Parse("guardrail-warn", Unchecked)!;

        Assert.Equal(HookFailureKind.RanUnchecked, unchecked_.Kind);
        Assert.False(unchecked_.IsRefusal);
        Assert.Equal("gateway unreachable", unchecked_.Reason);
        Assert.False(HookFailureRow.IsRefusal("guardrail-warn", Unchecked));
        Assert.True(HookFailureRow.IsRefusal("guardrail-block", CallRefused));
    }

    [Fact]
    public void The_reason_is_unquoted_the_way_Go_wrote_it()
    {
        var details = """hook blocked the call itself (fail-closed, PreToolUse): x: "said \"no\" café"; see logs/hook-failures.jsonl""";

        Assert.Equal("said \"no\" café", HookFailureRow.Parse("guardrail-block", details)!.Reason);
    }

    [Fact]
    public void A_policy_block_and_a_warning_that_is_not_a_hook_outage_are_not_refusals()
    {
        Assert.Null(HookFailureRow.Parse("guardrail-block", "rule=CMD-X action=block"));
        Assert.Null(HookFailureRow.Parse("guardrail-warn", "rule=CMD-X severity=HIGH"));
        Assert.Null(HookFailureRow.Parse("guardrail-warn", "unchecked hook call during gateway outage: \"gateway unreachable\""));
        Assert.Null(HookFailureRow.Parse("guardrail-warn", CallRefused));
        Assert.Null(HookFailureRow.Parse("connector-hook", CallRefused));
        Assert.Null(HookFailureRow.Parse("guardrail-block", null));
        Assert.Null(HookFailureRow.Parse(null, CallRefused));
        Assert.False(HookFailureRow.IsRefusal("guardrail-block", "rule=CMD-X action=block"));
    }

    // ------------------------------------------------------------------ JudgeEscalationFinding

    [Theory]
    [InlineData("JUDGE-UNAVAILABLE-ESCALATED", true)]
    [InlineData("judge-unavailable-escalated", true)]
    [InlineData("llm-judge:JUDGE-UNAVAILABLE-ESCALATED", true)]
    [InlineData(" LLM-JUDGE:judge-unavailable-escalated ", true)]
    [InlineData("JUDGE-INJ-INSTRUCT", false)]
    [InlineData("llm-judge:JUDGE-INJ-INSTRUCT", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_escalation_id_is_known_in_any_case_with_or_without_the_lane_prefix(string? id, bool expected) =>
        Assert.Equal(expected, JudgeEscalationFinding.Is(id));

    [Fact]
    public void Any_in_and_with_label_follow_the_same_rule()
    {
        Assert.True(JudgeEscalationFinding.AnyIn(new[] { "CMD-X", "llm-judge:JUDGE-UNAVAILABLE-ESCALATED" }));
        Assert.False(JudgeEscalationFinding.AnyIn(new[] { "CMD-X" }));
        Assert.False(JudgeEscalationFinding.AnyIn(null));
        Assert.Contains(JudgeEscalationFinding.Label, JudgeEscalationFinding.WithLabel("JUDGE-UNAVAILABLE-ESCALATED"), StringComparison.Ordinal);
        Assert.Equal("CMD-X", JudgeEscalationFinding.WithLabel("CMD-X"));
    }

    // ------------------------------------------------------------------ HookJudgeFallback

    [Fact]
    public void The_fallback_is_read_from_the_envelope_first()
    {
        var structured = Structured("""{"schema":"s","connector":"claudecode","extra":{"judge_fallback":"timeout"}}""");

        Assert.Equal("timeout", HookJudgeFallback.Read(structured, null));
        Assert.Equal("timeout", HookJudgeFallback.Read(structured, "connector=claudecode judge_fallback=error"));
        Assert.Equal("timed out", HookJudgeFallback.Short("timeout"));
        Assert.Contains("rules decided", HookJudgeFallback.Describe("timeout"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fallback_is_read_from_the_details_text_when_there_is_no_envelope()
    {
        const string details = "connector=claudecode result=ok action=allow severity=LOW mode=action reason=x would_block=false elapsed_ms=812 judge_fallback=budget details_json=\"{}\"";

        Assert.Equal("budget", HookJudgeFallback.Read(null, details));
        Assert.Equal("budget", HookJudgeFallback.Read(Structured("""{"connector":"claudecode"}"""), details));
    }

    [Fact]
    public void A_fallback_inside_the_quoted_envelope_copy_or_nowhere_is_nothing()
    {
        Assert.Equal(string.Empty, HookJudgeFallback.Read(null, "connector=claudecode action=allow details_json=\"judge_fallback=error\""));
        Assert.Equal(string.Empty, HookJudgeFallback.Read(null, "connector=claudecode action=allow"));
        Assert.Equal(string.Empty, HookJudgeFallback.Read(null, null));
        Assert.Equal(string.Empty, HookJudgeFallback.Read(Structured("""{"extra":{}}"""), null));
        Assert.Equal(string.Empty, HookJudgeFallback.Read(null, "judge_fallback=bad<value>"));
    }

    // ------------------------------------------------------------------ CoworkRow

    [Fact]
    public void A_cowork_row_is_read_from_the_envelopes_extra()
    {
        var structured = Structured(
            "{\"connector\":\"claudecode\",\"extra\":{\"surface\":\"cowork\",\"cowork_session_id\":\"" + SessionId +
            "\",\"prompt_id\":\"prompt-1\",\"cowork_tool_class\":\"shell\"}}");

        var call = CoworkRow.Read("claudecode", structured, null)!;

        Assert.Equal(SessionId, call.SessionId);
        Assert.Equal("prompt-1", call.PromptId);
        Assert.Equal("shell", call.ToolClass);
        Assert.Equal("shell (Cowork VM)", call.ToolClassLabel);
        Assert.Contains(CoworkRow.Facts(call), f => f.Key == "Cowork session" && f.Value == SessionId);
        Assert.Contains(CoworkRow.Facts(call), f => f.Key == "Surface" && f.Value.StartsWith(CoworkRow.Marker, StringComparison.Ordinal));
        Assert.Equal("cowork:PreToolUse", CoworkRow.Target("PreToolUse"));
    }

    [Fact]
    public void A_cowork_row_is_read_from_a_top_level_surface()
    {
        var call = CoworkRow.Read("claudecode", Structured("""{"surface":"cowork"}"""), null)!;

        Assert.Equal(string.Empty, call.SessionId);
        Assert.Single(CoworkRow.Facts(call));
    }

    [Fact]
    public void A_cowork_row_is_read_from_the_details_tail_and_from_a_decision_records_surface()
    {
        var details = "connector=claudecode result=ok action=allow cowork_session_id=" + SessionId + " cowork_tool_class=fetch prompt_id=prompt-2 surface=cowork";

        var call = CoworkRow.Read("claudecode", null, details)!;
        Assert.Equal(SessionId, call.SessionId);
        Assert.Equal("fetch", call.ToolClass);
        Assert.Equal("prompt-2", call.PromptId);

        Assert.NotNull(CoworkRow.Read("claudecode", null, null, "cowork"));
        Assert.Null(CoworkRow.Read("claudecode", null, "connector=claudecode surface=terminal"));
        Assert.Null(CoworkRow.Read("claudecode", Structured("""{"extra":{"surface":"terminal"}}"""), null));
    }

    [Fact]
    public void Free_text_in_the_details_that_mentions_the_surface_is_not_a_cowork_row()
    {
        // The pair is only read from the closing run of key=value tokens, and only beside a Cowork key.
        Assert.Null(CoworkRow.Read("claudecode", null, "connector=claudecode surface=cowork was what the user typed in the prompt"));
        Assert.Null(CoworkRow.Read("claudecode", null, "connector=claudecode surface=cowork"));
        Assert.Null(CoworkRow.Read("claudecode", null, "tool said: surface=cowork cowork_tool_class=shell then more words"));
        Assert.NotNull(CoworkRow.Read("claudecode", null, "free text first cowork_tool_class=shell surface=cowork"));
        Assert.Equal("surface=cowork", CoworkRow.KeyValueTail("some words surface=cowork"));
        Assert.Equal(string.Empty, CoworkRow.KeyValueTail("no pairs at the end"));
    }

    [Fact]
    public void The_sql_predicate_keeps_its_connector_term_and_the_surface_part_has_none()
    {
        Assert.Contains("e.connector = 'claudecode'", CoworkRow.SqlPredicate, StringComparison.Ordinal);
        Assert.Contains(CoworkRow.SqlSurfacePredicate, CoworkRow.SqlPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("e.connector", CoworkRow.SqlSurfacePredicate, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_connector_is_never_cowork_and_bad_ids_are_dropped()
    {
        var structured = Structured("""{"extra":{"surface":"cowork","cowork_session_id":"not an id!","prompt_id":"ok-1","cowork_tool_class":"Shell!"}}""");

        Assert.Null(CoworkRow.Read("codex", structured, "surface=cowork"));
        Assert.Null(CoworkRow.Read(null, structured, null, "cowork"));

        var call = CoworkRow.Read("claudecode", structured, null)!;
        Assert.Equal(string.Empty, call.SessionId);
        Assert.Equal(string.Empty, call.ToolClass);
        Assert.Equal("ok-1", call.PromptId);
    }

    // ------------------------------------------------------------------ the outage rule's wording

    [Fact]
    public void The_outage_rule_gains_the_judge_sentence_only_on_a_runtime_that_has_it()
    {
        Assert.Equal(HookFailModeRule.SavedWinsOutageRule, HookFailModeRule.SavedWinsOutageRuleFor(Probe("1.0.6").Capabilities));
        Assert.Equal(HookFailModeRule.SavedWinsOutageRule, HookFailModeRule.SavedWinsOutageRuleFor(null));
        Assert.Contains(HookFailModeRule.JudgeEscalationRule, HookFailModeRule.SavedWinsOutageRuleFor(Probe("1.0.7").Capabilities), StringComparison.Ordinal);
        Assert.Contains(HookFailModeRule.JudgeEscalationRule, HookFailModeRule.SavedWinsOutageRuleFor(Probe("1.0.8").Capabilities), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the two block counts

    private void Add(DateTimeOffset at, string action, string? details) =>
        _database.InsertEvent($"row-{_row++:D6}", at, action, "INFO", details: details);

    [Fact]
    public async Task The_hourly_chart_counts_a_policy_block_and_a_null_details_block_but_not_a_hook_refusal()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.Zero);
        var hour = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        Add(hour.AddMinutes(1), "guardrail-block", "rule=CMD-X action=block");
        Add(hour.AddMinutes(2), "guardrail-block", null);
        Add(hour.AddMinutes(3), "guardrail-block", CallRefused);
        Add(hour.AddMinutes(4), "guardrail-block", ResultRefused);
        Add(hour.AddMinutes(5), "guardrail-block", EventRefused);
        Add(hour.AddMinutes(6), "deny", CallRefused);

        var result = await new HourlyActivityReader(_database.Path, HourlyActivityReader.DefaultHours, new FixedTime(now)).ReadAsync();

        Assert.Equal(HourlyActivityStatus.Ok, result.Status);

        // The policy block, the NULL-details block and the plain deny; the three refusals are no policy's decision.
        Assert.Equal(3, result.Blocked);
    }

    [Theory]
    [InlineData("guardrail-block", "rule=CMD-X action=block", true)]
    [InlineData("guardrail-block", null, true)]
    [InlineData("guardrail-block", CallRefused, false)]
    [InlineData("guardrail-block", ResultRefused, false)]
    [InlineData("guardrail-block", EventRefused, false)]
    [InlineData("block", "rule=CMD-X", true)]
    [InlineData("guardrail-warn", Unchecked, false)]
    public void The_tray_block_test_leaves_out_a_hook_refusal(string action, string? details, bool expected) =>
        Assert.Equal(expected, RecentAuditMetricsReader.IsBlock(action, details));

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
