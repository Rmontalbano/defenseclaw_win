using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>The Audit inspector's rule-id sentences and the shell-command limit outcome, from synthetic hook rows. A row with no known id is left as it was.</summary>
public sealed class RuleLabelAuditTests
{
    private static AuditEvent Hook(string details, string? structured) => new()
    {
        Id = "a-1",
        Timestamp = DateTimeOffset.UtcNow,
        RawTimestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = "connector-hook",
        Target = "PreToolUse",
        Actor = "gateway",
        Details = details,
        Severity = "CRITICAL",
        Connector = "codex",
        StructuredJsonRaw = structured,
    };

    private static IReadOnlyList<DetailPair> Pairs(AuditEvent row, params DetailPair[] parsed) =>
        AuditHookRows.Pairs(parsed, row, HookRowGate.All, null, null);

    private static IReadOnlyList<DetailPair> PairsOn(HookRowGate gate, AuditEvent row, params DetailPair[] parsed) =>
        AuditHookRows.Pairs(parsed, row, gate, null, null);

    [Fact]
    public void The_structured_rule_ids_get_a_meaning_line_and_the_limit_row_says_the_call_ran()
    {
        var row = Hook(
            "connector=codex result=ok action=alert raw_action=alert severity=CRITICAL mode=action would_block=false",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\",\"CMD-X\"]}");

        var pairs = Pairs(row);
        Assert.DoesNotContain(pairs, p => p.Label == "Rule IDs");
        var meaning = Assert.Single(pairs, p => p.Label == "Rule meaning");
        Assert.Equal("A shell command too long or too complex for the policy check.", meaning.Value);
        Assert.Contains(pairs, p => p.Label == "Call outcome" && p.Value.StartsWith("The call ran; DefenseClaw recorded it as an alert", StringComparison.Ordinal));
        Assert.DoesNotContain(pairs, p => p.Label == "Rule note");
    }

    [Fact]
    public void A_rule_value_in_the_parsed_details_keeps_the_runtimes_title_and_a_blocked_row_says_so()
    {
        var row = Hook("connector=codex action=block raw_action=block mode=action would_block=true", "{\"connector\":\"codex\"}");

        var pairs = Pairs(row, new DetailPair("Rule Id", "ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit"), new DetailPair("Mode", "action"));
        Assert.Equal(new DetailPair("Rule Id", "ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit"), pairs[0]);
        Assert.Equal(new DetailPair("Mode", "action"), pairs[1]);
        Assert.Contains(pairs, p => p.Label == "Rule meaning" && p.Value.StartsWith("A shell command too long", StringComparison.Ordinal));
        Assert.Contains(pairs, p => p.Label == "Call outcome" && p.Value == "The call was blocked.");
    }

    [Fact]
    public void A_limit_row_without_action_fields_gets_only_the_meaning_and_a_judge_row_keeps_its_note()
    {
        var plain = Hook("no pairs here", "{\"rule_ids\":[\"tamper.agent_hook_config_write\"]}");
        var pairs = Pairs(plain);
        Assert.Contains(pairs, p => p.Label == "Rule meaning" && p.Value.StartsWith("A shell command that writes", StringComparison.Ordinal));
        Assert.DoesNotContain(pairs, p => p.Label is "Call outcome" or "Rule note");

        var limitOnly = Pairs(Hook("no pairs here", "{\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\"]}"));
        Assert.DoesNotContain(limitOnly, p => p.Label == "Call outcome");

        var judge = Pairs(Hook("x", "{\"rule_ids\":[\"JUDGE-UNAVAILABLE-ESCALATED\"]}"));
        Assert.Contains(judge, p => p.Label == "Rule note" && p.Value == JudgeEscalationFinding.Explanation);
        Assert.Contains(judge, p => p.Label == "Rule IDs" && p.Value.Contains(JudgeEscalationFinding.Label, StringComparison.Ordinal));
        Assert.DoesNotContain(judge, p => p.Label == "Rule meaning");
    }

    [Theory]
    [InlineData("ACTION-COMPOUND-LIMIT", "connector=codex result=ok action=alert raw_action=alert mode=action would_block=false")]
    [InlineData("ACTION-COMPOUND-LIMIT", "connector=codex action=block raw_action=block mode=action would_block=true")]
    [InlineData("tamper.agent_hook_config_write", "connector=claudecode action=block")]
    [InlineData("tamper.agent_hook_config_disable", "connector=claudecode action=block")]
    [InlineData("ACTION-OPERAND-BOUND", "connector=codex action=alert raw_action=alert mode=action")]
    public void A_1_0_8_runtime_row_for_a_rule_id_added_in_1_0_9_is_returned_as_it_was(string id, string details)
    {
        // b8cb8e2 labelled only the judge-unavailable id, so a 1.0.8 row with any other id came back exactly as parsed.
        var gate108 = new HookRowGate(true, true, true, true);
        var row = Hook(details, "{\"connector\":\"codex\",\"rule_ids\":[\"" + id + "\"]}");
        var parsed = new[] { new DetailPair("Rule IDs", id), new DetailPair("Rule Id", id + ": A title"), new DetailPair("Mode", "action") };

        Assert.Equal(parsed, PairsOn(gate108, row, parsed));
        Assert.Equal(Array.Empty<DetailPair>(), PairsOn(gate108, row));
    }

    [Fact]
    public void A_1_0_8_runtime_still_labels_the_judge_unavailable_id_in_place()
    {
        var gate108 = new HookRowGate(true, true, true, true);
        var row = Hook("x", "{\"rule_ids\":[\"JUDGE-UNAVAILABLE-ESCALATED\"]}");

        var pairs = PairsOn(gate108, row, new DetailPair("Rule IDs", "JUDGE-UNAVAILABLE-ESCALATED, CMD-X"));
        Assert.StartsWith("JUDGE-UNAVAILABLE-ESCALATED (", pairs[0].Value, StringComparison.Ordinal);
        Assert.EndsWith(", CMD-X", pairs[0].Value, StringComparison.Ordinal);
        Assert.Contains(pairs, p => p.Label == "Rule note");
        Assert.DoesNotContain(pairs, p => p.Label is "Rule meaning" or "Call outcome");
    }

    [Fact]
    public void A_row_with_no_known_id_is_returned_as_it_was()
    {
        var row = Hook("connector=codex action=allow", "{\"rule_ids\":[\"CMD-X\"]}");
        var parsed = new[] { new DetailPair("Rule Id", "CMD-X") };

        Assert.Equal(parsed, Pairs(row, parsed));
    }
}
