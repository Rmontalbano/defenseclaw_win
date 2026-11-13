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

    [Fact]
    public void The_structured_rule_ids_are_labelled_and_the_limit_row_says_the_call_ran()
    {
        var row = Hook(
            "connector=codex result=ok action=alert raw_action=alert severity=CRITICAL mode=action would_block=false",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\",\"CMD-X\"]}");

        var pairs = Pairs(row);
        var ids = Assert.Single(pairs, p => p.Label == "Rule IDs");
        Assert.Contains("ACTION-COMPOUND-LIMIT (A shell command too long or too complex for the policy check.)", ids.Value, StringComparison.Ordinal);
        Assert.EndsWith(", CMD-X", ids.Value, StringComparison.Ordinal);
        Assert.Contains(pairs, p => p.Label == "Call outcome" && p.Value.StartsWith("The call ran; DefenseClaw recorded it as an alert", StringComparison.Ordinal));
        Assert.DoesNotContain(pairs, p => p.Label == "Rule note");
    }

    [Fact]
    public void A_rule_id_in_the_parsed_details_is_labelled_in_place_and_a_blocked_row_says_so()
    {
        var row = Hook("connector=codex action=block raw_action=block mode=action would_block=true", "{\"connector\":\"codex\"}");

        var pairs = Pairs(row, new DetailPair("Rule Id", "ACTION-COMPOUND-LIMIT"), new DetailPair("Mode", "action"));
        Assert.Equal("Rule Id", pairs[0].Label);
        Assert.StartsWith("ACTION-COMPOUND-LIMIT (A shell command", pairs[0].Value, StringComparison.Ordinal);
        Assert.Contains(pairs, p => p.Label == "Call outcome" && p.Value == "The call was blocked.");
    }

    [Fact]
    public void A_limit_row_without_action_fields_gets_only_the_label_and_a_judge_row_keeps_its_note()
    {
        var plain = Hook("no pairs here", "{\"rule_ids\":[\"tamper.agent_hook_config_write\"]}");
        var pairs = Pairs(plain);
        Assert.Contains(pairs, p => p.Label == "Rule IDs" && p.Value.Contains("tamper.agent_hook_config_write (A shell command that writes", StringComparison.Ordinal));
        Assert.DoesNotContain(pairs, p => p.Label is "Call outcome" or "Rule note");

        var limitOnly = Pairs(Hook("no pairs here", "{\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\"]}"));
        Assert.DoesNotContain(limitOnly, p => p.Label == "Call outcome");

        var judge = Pairs(Hook("x", "{\"rule_ids\":[\"JUDGE-UNAVAILABLE-ESCALATED\"]}"));
        Assert.Contains(judge, p => p.Label == "Rule note" && p.Value == JudgeEscalationFinding.Explanation);
        Assert.Contains(judge, p => p.Label == "Rule IDs" && p.Value.Contains(JudgeEscalationFinding.Label, StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_with_no_known_id_is_returned_as_it_was()
    {
        var row = Hook("connector=codex action=allow", "{\"rule_ids\":[\"CMD-X\"]}");
        var parsed = new[] { new DetailPair("Rule Id", "CMD-X") };

        Assert.Equal(parsed, Pairs(row, parsed));
    }
}
