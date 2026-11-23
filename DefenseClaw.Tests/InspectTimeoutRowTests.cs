using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// The inspection timeout rows of DefenseClaw 1.0.10 (inspect-tool-timeout, inspect-request-timeout, inspect-response-timeout, inspect-tool-response-timeout):
/// the reason, the label, the actionable rule (a scan timeout is a real miss, a canceled caller keeps today's rule) and the target type (never a governed tool).
/// </summary>
public sealed class InspectTimeoutRowTests
{
    private const string Scan = "verdict=none reason=scan_timeout elapsed_ms=2001 budget_ms=2000 request_id=req-1";
    private const string Canceled = "verdict=none reason=caller_canceled elapsed_ms=40 budget_ms=2000";

    private static AuditEvent Row(string action, string target, string details, string severity = "MEDIUM") => new()
    {
        Id = "t-1",
        Timestamp = DateTimeOffset.UtcNow,
        RawTimestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = action,
        Target = target,
        Actor = "gateway",
        Details = details,
        Severity = severity,
        Bucket = "guardrail.evaluation",
    };

    [Theory]
    [InlineData("inspect-tool-timeout")]
    [InlineData("inspect-request-timeout")]
    [InlineData("inspect-response-timeout")]
    [InlineData("inspect-tool-response-timeout")]
    [InlineData("Inspect-Tool-Timeout")]
    public void Each_of_the_four_actions_is_read_with_its_reason(string action)
    {
        var scan = InspectTimeoutRow.Parse(action, Scan)!;
        Assert.True(scan.IsScanTimeout);
        Assert.False(scan.IsCallerCanceled);
        Assert.Equal("Inspection timed out: the call was decided without this check", scan.Label);

        var canceled = InspectTimeoutRow.Parse(action, Canceled)!;
        Assert.True(canceled.IsCallerCanceled);
        Assert.Equal("Inspection timed out: the call was decided without this check (the caller canceled it)", canceled.Label);
    }

    [Theory]
    [InlineData("inspect-tool")]
    [InlineData("inspect-tool-timeouts")]
    [InlineData("guardrail-inspection")]
    [InlineData("tool-timeout")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_action_is_not_a_timeout_row(string? action) => Assert.Null(InspectTimeoutRow.Parse(action, Scan));

    [Fact]
    public void A_row_with_no_or_an_odd_reason_is_still_a_timeout_and_the_reason_is_bounded()
    {
        var none = InspectTimeoutRow.Parse("inspect-request-timeout", null)!;
        Assert.Equal(string.Empty, none.Reason);
        Assert.False(none.IsScanTimeout);

        var odd = InspectTimeoutRow.Parse("inspect-request-timeout", "reason=" + new string('z', 300))!;
        Assert.Equal(32, odd.Reason.Length);
    }

    [Fact]
    public void A_scan_timeout_is_actionable_and_a_canceled_caller_keeps_todays_rule()
    {
        Assert.True(ActionableRule.IsActionable(Row("inspect-tool-timeout", "shell", Scan)));
        Assert.True(ActionableRule.IsActionable("MEDIUM", "x", "inspect-tool-timeout", "shell", Scan));

        // MEDIUM with no alarming word: hidden, as before.
        Assert.False(ActionableRule.IsActionable(Row("inspect-tool-timeout", "shell", Canceled)));
        Assert.False(ActionableRule.IsActionable("MEDIUM", "x", "inspect-tool-timeout", "shell", Canceled));
        Assert.False(ActionableRule.IsActionable(Row("inspect-request-timeout", "pre-request", Canceled)));
    }

    [Theory]
    [InlineData("inspect-tool-timeout")]
    [InlineData("inspect-tool-response-timeout")]
    [InlineData("inspect-request-timeout")]
    [InlineData("inspect-response-timeout")]
    [InlineData("INSPECT-TOOL-TIMEOUT")]
    public void The_four_actions_have_no_target_type(string action) => Assert.Equal(string.Empty, AuditTargetType.FromAction(action));

    [Fact]
    public void Other_actions_keep_their_type_and_the_sql_agrees_with_the_code()
    {
        Assert.Equal("tool", AuditTargetType.FromAction("tool_invocation"));
        Assert.Equal("scan", AuditTargetType.FromAction("scan-finding"));
        Assert.Equal(string.Empty, AuditTargetType.FromAction("guardrail-inspection"));

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var action in new[] { "inspect-tool-timeout", "inspect-tool-response-timeout", "Inspect-Request-Timeout", "tool_invocation", "skill-block", "x" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {AuditTargetType.SqlCase("$action")}";
            command.Parameters.AddWithValue("$action", action);
            Assert.Equal(AuditTargetType.FromAction(action), (string)command.ExecuteScalar()!);
        }
    }

    [Fact]
    public void The_shared_labels_carry_the_timeout_text()
    {
        var note = Assert.Single(GuardrailRowNotes.For("inspect-tool-timeout", "shell", Canceled));
        Assert.Equal("Inspection timeout", note.Key);
        Assert.Contains("the caller canceled it", note.Value, StringComparison.Ordinal);
        Assert.Equal(InspectTimeoutRow.Parse("inspect-tool-timeout", Scan)!.Label, GuardrailRowNotes.Headline("inspect-tool-timeout", "shell", Scan));
    }
}
