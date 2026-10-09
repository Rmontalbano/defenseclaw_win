using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-262: the one "actionable" predicate the Audit panel's switch and the Logs panel's Events view share, held to the 0.8.10 TUI's rule
/// (<c>tui/panels/audit.py</c> <c>_is_low_signal_event</c> and <c>tui/panels/alerts.py</c> <c>_is_low_signal_alert</c>) as a table. The cases are the
/// TUI's own order of tests - an actionable severity, then a severity that is not on the low-signal list, then the words - spelled out one by one.
/// </summary>
public sealed class ActionableRuleTests
{
    // ---- The TUI's constants, verbatim ----

    [Fact]
    public void The_sets_and_the_words_are_the_TUIs_own()
    {
        Assert.Equal(new[] { "CRITICAL", "HIGH", "ERROR" }, ActionableRule.ActionableSeverities);
        Assert.Equal(new[] { "INFO", "LOW", "MEDIUM", "WARNING", string.Empty }, ActionableRule.LowSignalSeverities);
        Assert.Equal(
            new[] { "block", "blocked", "deny", "denied", "reject", "rejected", "quarantine", "fail", "failed", "failure", "error", "fatal", "panic" },
            ActionableRule.Tokens);
    }

    // ---- Severity alone ----

    [Theory]
    [InlineData("CRITICAL")]
    [InlineData("HIGH")]
    [InlineData("ERROR")]
    [InlineData("high")]
    [InlineData("  Critical  ")]
    [InlineData("error")]
    public void An_actionable_severity_is_shown_whatever_the_text_says(string severity)
    {
        Assert.True(ActionableRule.IsActionable(severity, "all quiet, allow, ok"));
        Assert.True(ActionableRule.IsActionable(severity, null));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("LOW")]
    [InlineData("MEDIUM")]
    [InlineData("WARNING")]
    [InlineData("info")]
    [InlineData(" warning ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_low_signal_severity_with_nothing_alarming_in_its_text_is_hidden(string? severity)
    {
        Assert.False(ActionableRule.IsActionable(severity, "hook_decision observe connector=claudecode action=allow"));
        Assert.False(ActionableRule.IsActionable(severity, string.Empty));
        Assert.False(ActionableRule.IsActionable(severity, null));
    }

    [Theory]
    [InlineData("FATAL")]
    [InlineData("WARN")]
    [InlineData("NONE")]
    [InlineData("DEBUG")]
    [InlineData("NOTICE")]
    [InlineData("whatever")]
    public void A_severity_the_rule_does_not_list_is_never_hidden(string severity)
    {
        // The TUI: "if severity not in LOW_SIGNAL_SEVERITIES: return False" (not low-signal) - before it looks at a single word.
        Assert.True(ActionableRule.IsActionable(severity, "nothing alarming here"));
        Assert.True(ActionableRule.IsActionable(severity, null));
    }

    // ---- The words ----

    public static TheoryData<string> TheTokens()
    {
        var data = new TheoryData<string>();
        foreach (var token in ActionableRule.Tokens)
        {
            data.Add(token);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TheTokens))]
    public void Each_word_makes_a_low_signal_row_actionable_in_any_case_and_anywhere_in_the_text(string token)
    {
        Assert.True(ActionableRule.IsActionable("INFO", token));
        Assert.True(ActionableRule.IsActionable("LOW", token.ToUpperInvariant()));
        Assert.True(ActionableRule.IsActionable("MEDIUM", $"the scan {token} for a reason"));
        Assert.True(ActionableRule.IsActionable("WARNING", $"prefix-{token}-suffix"));
        Assert.True(ActionableRule.IsActionable(string.Empty, char.ToUpperInvariant(token[0]) + token[1..]));
    }

    [Theory]
    [InlineData("observe connector=claudecode action=allow severity=NONE would_block=false elapsed=12ms", true)] // a hook row: "block" inside would_block
    [InlineData("unblocked", true)]                                                                              // a substring test, not a word test
    [InlineData("failover target selected", true)]                                                              // "fail" inside "failover"
    [InlineData("blockchain ledger", true)]
    [InlineData("denied by policy", true)]
    [InlineData("rejected", true)]
    [InlineData("PANIC: runtime error", true)]
    [InlineData("scan completed, 3 findings, allow", false)]
    [InlineData("allowed", false)]
    [InlineData("connected", false)]
    [InlineData("quarantin", false)]
    [InlineData("rejec", false)]
    [InlineData("", false)]
    public void The_words_are_a_substring_test_like_the_TUIs(string text, bool expected) =>
        Assert.Equal(expected, ActionableRule.IsActionable("INFO", text));

    [Fact]
    public void HasToken_is_the_words_alone()
    {
        Assert.True(ActionableRule.HasToken("a Block happened"));
        Assert.False(ActionableRule.HasToken("a quiet day"));
        Assert.False(ActionableRule.HasToken(null));
        Assert.False(ActionableRule.HasToken(string.Empty));
    }

    // ---- An audit row: the TUI's Audit panel text ----

    private static AuditEvent Row(
        string severity = "INFO",
        string action = "hook_decision",
        string? target = null,
        string? actor = "audit_logger",
        string? details = null,
        string? runId = null,
        string id = "evt-1",
        string? eventName = null,
        string? bucket = null,
        string? connector = null,
        string? toolName = null) => new()
    {
        Id = id,
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        RawTimestamp = "2026-10-01T12:00:00Z",
        Action = action,
        Severity = severity,
        Target = target,
        Actor = actor,
        Details = details,
        RunId = runId,
        EventName = eventName,
        Bucket = bucket,
        Connector = connector,
        ToolName = toolName,
    };

    [Fact]
    public void An_audit_row_is_judged_on_the_text_the_TUIs_audit_panel_searches()
    {
        Assert.False(ActionableRule.IsActionable(Row()));

        // Each column the TUI searches, on its own.
        Assert.True(ActionableRule.IsActionable(Row(action: "install-blocked")));
        Assert.True(ActionableRule.IsActionable(Row(target: "skill/denied-skill")));
        Assert.True(ActionableRule.IsActionable(Row(actor: "failure-injector")));
        Assert.True(ActionableRule.IsActionable(Row(details: "observe connector=claudecode would_block=false")));
        Assert.True(ActionableRule.IsActionable(Row(runId: "run-error")));
        Assert.True(ActionableRule.IsActionable(Row(id: "evt-fatal")));
    }

    [Fact]
    public void The_columns_the_TUIs_audit_panel_does_not_select_are_not_searched()
    {
        // event_name, bucket, connector and tool_name are not in the TUI's Event, so a word there does not make a low-signal row actionable.
        var row = Row(eventName: "guardrail.judge.failed", bucket: "enforcement.blocked", connector: "denied-connector", toolName: "reject_tool");

        Assert.False(ActionableRule.IsActionable(row));
    }

    [Theory]
    [InlineData("HIGH", true)]
    [InlineData("ERROR", true)]
    [InlineData("FATAL", true)]
    [InlineData("INFO", false)]
    [InlineData("MEDIUM", false)]
    public void An_audit_rows_severity_is_read_as_stored(string severity, bool expected) =>
        Assert.Equal(expected, ActionableRule.IsActionable(Row(severity: severity)));

    [Fact]
    public void A_row_with_no_severity_is_judged_on_its_words_alone()
    {
        Assert.False(ActionableRule.IsActionable(new AuditEvent { Id = "x", Timestamp = DateTimeOffset.UnixEpoch, RawTimestamp = "", Action = "scan" }));
        Assert.True(ActionableRule.IsActionable(new AuditEvent { Id = "x", Timestamp = DateTimeOffset.UnixEpoch, RawTimestamp = "", Action = "scan-failed" }));
    }

    [Fact]
    public void A_row_whose_details_were_too_large_to_load_is_shown_because_its_words_are_unknown()
    {
        var oversized = new AuditEvent
        {
            Id = "big",
            Timestamp = DateTimeOffset.UnixEpoch,
            RawTimestamp = string.Empty,
            Action = "hook_decision",
            Severity = "INFO",
            Oversized = new[] { new OversizedValue("details", 3_000_000, 262_144) },
        };
        Assert.True(ActionableRule.IsActionable(oversized));

        // Only the details matter to the rule: a structured_json too large to load does not change the verdict.
        var jsonOnly = new AuditEvent
        {
            Id = "big-json",
            Timestamp = DateTimeOffset.UnixEpoch,
            RawTimestamp = string.Empty,
            Action = "hook_decision",
            Severity = "INFO",
            Details = "quiet",
            Oversized = new[] { new OversizedValue("structured_json", 3_000_000, 262_144) },
        };
        Assert.False(ActionableRule.IsActionable(jsonOnly));
    }

    [Fact]
    public void Joining_the_columns_and_searching_each_one_agree()
    {
        // The TUI joins id, action, target, actor, severity, details and run id with spaces and searches the whole; the rule searches each where
        // it stands. No word has a space in it, so a word cannot straddle two columns and the two are the same test.
        foreach (var row in new[]
        {
            Row(),
            Row(action: "bloc", target: "ked"),
            Row(details: "pani", runId: "c"),
            Row(actor: "fail", details: "ed"),
            Row(details: "would_block=false"),
        })
        {
            var joined = string.Join(' ', row.Id, row.Action, row.Target, row.Actor, row.Severity, row.Details, row.RunId);
            Assert.Equal(ActionableRule.IsActionable(row.Severity, joined), ActionableRule.IsActionable(row));
        }
    }

    [Fact]
    public void Null_arguments_are_an_error_and_null_text_is_not()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ActionableRule.IsActionable(null!));
        Assert.False(ActionableRule.IsActionable("INFO", null));
    }
}
