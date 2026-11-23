using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// One row of each 1.0.10 guardrail kind (shadow measurements, inspection timeouts, the cascade's degraded row) and a 1.0.9-and-later hook refusal on the
/// <c>.dropped</c> tail, written into a database built from the real DDL with the bucket, event name, severity, target and details the runtime writes. Held to:
/// the Events stream and the Audit actionable page leave the shadow rows out and keep the scan timeout; the block and alert counters count none of them.
/// </summary>
public sealed class GuardrailRowCountersTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Dropped =
        "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: \"gateway unreachable\"; see logs/hook-failures.dropped";

    private readonly TestAuditDatabase _database = new();

    public GuardrailRowCountersTests()
    {
        Add("shadow-effects", 1, "guardrail-inspection", "INFO", "guardrail.evaluation", "legacy.audit.guardrail.inspection", "codex", ShadowMeasurementRowTests.Effects);
        Add("shadow-secret", 2, "guardrail-inspection", "INFO", "guardrail.evaluation", "legacy.audit.guardrail.inspection", "secret-flow:shadow", ShadowMeasurementRowTests.SecretFlow);
        Add("timeout-scan", 3, "inspect-tool-timeout", "MEDIUM", "guardrail.evaluation", "legacy.audit.inspect.tool.timeout", "shell", "verdict=none reason=scan_timeout elapsed_ms=2001 budget_ms=2000");
        Add("timeout-canceled", 4, "inspect-request-timeout", "MEDIUM", "guardrail.evaluation", "legacy.audit.inspect.request.timeout", "pre-request", "verdict=none reason=caller_canceled elapsed_ms=40 budget_ms=2000");
        Add("cascade", 5, "guardrail-degraded", "HIGH", "platform.health", "subsystem.degraded", string.Empty, "guardrail degraded: guardrail.cascade is invalid: unknown layer; the guardrail decides with the built-in order", actor: "guardrail");
        Add("dropped", 6, "guardrail-block", "MEDIUM", "guardrail.evaluation", "legacy.audit.guardrail.block", "codex:PreToolUse", Dropped);
        Add("quiet", 7, "hook_decision", "INFO", "guardrail.evaluation", "legacy.audit.hook.decision", string.Empty, "quiet and allowed");
    }

    public void Dispose() => _database.Dispose();

    private void Add(string id, int minute, string action, string severity, string bucket, string eventName, string target, string details, string actor = "gateway")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
            VALUES ($id, $timestamp, $action, $target, $actor, $details, $severity, $bucket, $eventName, 'codex', 'sidecar', 'logs', NULL)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$details", details);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$eventName", eventName);
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task The_events_stream_leaves_the_shadow_rows_out_of_the_actionable_view_and_keeps_a_scan_timeout()
    {
        var rows = (await new EventStreamReader(_database.Path).ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

        Assert.False(rows["shadow-effects"].IsActionable);
        Assert.False(rows["shadow-secret"].IsActionable);
        Assert.True(rows["timeout-scan"].IsActionable);
        Assert.False(rows["timeout-canceled"].IsActionable);
        Assert.True(rows["cascade"].IsActionable);
        Assert.True(rows["dropped"].IsActionable);
        Assert.False(rows["quiet"].IsActionable);
    }

    [Fact]
    public async Task The_events_stream_carries_the_rows_own_action_target_and_details_for_the_labels()
    {
        var rows = (await new EventStreamReader(_database.Path).ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

        Assert.Equal("guardrail-inspection", rows["shadow-secret"].RowAction);
        Assert.Equal("secret-flow:shadow", rows["shadow-secret"].RowTarget);
        // The stream redacts a program name in details as it does for every row; the would= token the label reads survives.
        Assert.Contains("would=block", rows["shadow-secret"].RowDetails, StringComparison.Ordinal);
        Assert.StartsWith("lane=effects-shadow ", rows["shadow-effects"].RowDetails, StringComparison.Ordinal);
        Assert.Contains("would=block", rows["shadow-effects"].RowDetails, StringComparison.Ordinal);
        Assert.Contains("reason=scan_timeout", rows["timeout-scan"].RowDetails, StringComparison.Ordinal);
        Assert.Contains("guardrail.cascade is invalid", rows["cascade"].RowDetails, StringComparison.Ordinal);

        // A row with no label keeps no extra text.
        Assert.Equal(string.Empty, rows["quiet"].RowDetails);
        Assert.Equal(string.Empty, rows["quiet"].RowTarget);
    }

    [Fact]
    public async Task The_audit_actionable_page_shows_the_scan_timeout_the_cascade_and_the_refusal_and_not_the_shadow_rows()
    {
        var page = await new AuditReader(_database.Path).QueryActionableAsync(new AuditQuery { Limit = 50 });

        Assert.Equal(new[] { "dropped", "cascade", "timeout-scan" }, page.Events.Select(e => e.Id));
        Assert.Equal(4, page.Hidden);
        var hidden = page.HiddenRows.Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "quiet", "shadow-effects", "shadow-secret", "timeout-canceled" }, hidden);
    }

    [Fact]
    public async Task No_shadow_row_is_a_block_and_a_dropped_tail_refusal_is_not_a_policy_block()
    {
        var result = await new RecentAuditMetricsReader(_database.Path).ReadAsync();

        Assert.Equal(RecentAuditMetricsStatus.Ok, result.Status);
        Assert.Equal(0, result.Blocks);
        Assert.Equal(0, result.HookCalls);
    }

    [Fact]
    public async Task No_row_of_these_kinds_is_an_alert()
    {
        var result = await new AlertQueueReader(_database.Path, readTimeout: TestTimeouts.Ceiling).ReadAsync();

        Assert.Equal(AlertQueueStatus.Ok, result.Status);
        Assert.Equal(0, result.Counts.Total);
        Assert.Empty(result.Counts.Newest);
    }
}
