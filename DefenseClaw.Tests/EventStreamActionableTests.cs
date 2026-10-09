using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-262: the Events stream held to the 0.8.10 TUI's Alerts panel. Which buckets it reads (all seven the TUI does, <c>network.egress</c> among
/// them, and not the Verdicts' narrower set), what a row of each says (decision, target, reason) and whether the TUI's actionable view would show it
/// (<see cref="StreamEvent.IsActionable"/>, the shared <see cref="ActionableRule"/> over the TUI's Alerts text). A database built from the real DDL with
/// synthetic rows only.
/// </summary>
public sealed class EventStreamActionableTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private EventStreamReader Reader() => new(_database.Path);

    private void Add(
        string id,
        int minute,
        string bucket,
        string eventName,
        string severity = "INFO",
        string action = "act",
        string? details = null,
        string? payload = null,
        string? connector = "claudecode",
        string source = "sidecar")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
            VALUES ($id, $timestamp, $action, '', 'gateway', $details, $severity, $bucket, $eventName, $connector, $source, 'logs', $payload)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$eventName", eventName);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private async Task<StreamEvent> OnlyRow()
    {
        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows;
        return Assert.Single(rows);
    }

    // ---- Which buckets ----

    [Fact]
    public void The_alert_buckets_are_the_seven_the_TUIs_alerts_panel_reads()
    {
        // tui/panels/alerts.py: _V8_ALERT_BUCKETS.
        Assert.Equal(
            new[] { "security.finding", "guardrail.evaluation", "enforcement.action", "asset.scan", "network.egress", "platform.health", "diagnostic" },
            EventStreamReader.AlertBuckets);
    }

    [Fact]
    public async Task Events_read_every_bucket_the_TUIs_alerts_panel_reads_and_not_telemetry()
    {
        var minute = 0;
        foreach (var bucket in EventStreamReader.AlertBuckets)
        {
            Add($"row-{bucket}", ++minute, bucket, bucket + ".recorded");
        }

        Add("telemetry", ++minute, "telemetry.ingest", "span.received");
        Add("other", ++minute, "compliance.activity", "config.change.applied");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows;

        foreach (var bucket in EventStreamReader.AlertBuckets)
        {
            Assert.Contains(rows, r => r.Bucket == bucket);
        }

        // ... plus the Mac's "every canonical event", minus telemetry unless asked for.
        Assert.Contains(rows, r => r.Bucket == "compliance.activity");
        Assert.DoesNotContain(rows, r => r.Bucket == "telemetry.ingest");
        Assert.Equal(EventStreamReader.AlertBuckets.Count + 1, rows.Count);

        var withTelemetry = (await Reader().ReadAsync(EventStreamKind.Events, includeTelemetry: true)).Rows;
        Assert.Contains(withTelemetry, r => r.Bucket == "telemetry.ingest");
    }

    [Fact]
    public async Task The_verdicts_stream_leaves_the_egress_bucket_out_by_design()
    {
        // The Mac's Verdicts are the decisions; the TUI's alert buckets add the egress decisions, which live in Events.
        Add("egress", 1, "network.egress", "egress.decided");
        Add("verdict", 2, "guardrail.evaluation", "guardrail.evaluated");

        var verdicts = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows;
        var events = (await Reader().ReadAsync(EventStreamKind.Events)).Rows;

        Assert.DoesNotContain(verdicts, r => r.Bucket == "network.egress");
        Assert.Contains(events, r => r.Bucket == "network.egress");
    }

    [Fact]
    public async Task Only_rows_the_logs_signal_wrote_are_read()
    {
        Add("logs", 1, "network.egress", "egress.decided");
        using (var connection = _database.OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit_events SET signal = 'metrics' WHERE id = 'logs'";
            _ = command.ExecuteNonQuery();
        }

        Add("kept", 2, "network.egress", "egress.decided");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows;

        Assert.Equal(new[] { "kept" }, rows.Select(r => r.Id));
    }

    // ---- What an egress row says ----

    [Fact]
    public async Task An_egress_row_shows_the_decision_the_target_and_the_reason_the_TUI_shows()
    {
        Add(
            "egress",
            1,
            "network.egress",
            "egress.decided",
            payload: """{"defenseclaw.network.decision":"block","defenseclaw.network.target_ref":"api.example.test","defenseclaw.network.reason":"host is not on the allow list"}""");

        var row = await OnlyRow();

        Assert.Equal("block", row.Action);
        Assert.Equal("api.example.test", row.Target);
        Assert.Equal("egress", row.EventType);
        Assert.Equal("egress.decided block — host is not on the allow list", row.Message);
        Assert.True(row.IsActionable);
    }

    [Fact]
    public async Task An_egress_policy_outcome_stands_in_for_a_missing_decision_and_an_allowed_row_is_quiet()
    {
        Add(
            "egress",
            1,
            "network.egress",
            "egress.decided",
            payload: """{"defenseclaw.network.policy_outcome":"allow","defenseclaw.network.target_ref":"api.example.test"}""",
            details: "passthrough");

        var row = await OnlyRow();

        Assert.Equal("allow", row.Action);
        Assert.Equal("api.example.test", row.Target);
        Assert.False(row.IsActionable);
    }

    [Fact]
    public async Task A_scan_verdict_is_the_action_of_a_scan_row_that_has_no_other_decision()
    {
        Add("scan", 1, "asset.scan", "scan.completed", payload: """{"defenseclaw.scan.verdict":"allow","defenseclaw.scan.target_ref":"skills/example"}""");

        var row = await OnlyRow();

        Assert.Equal("allow", row.Action);
        Assert.Equal("skills/example", row.Target);
        Assert.False(row.IsActionable);
    }

    [Fact]
    public async Task A_decision_the_Mac_reads_keeps_its_place_ahead_of_the_ones_the_TUI_adds()
    {
        Add("both", 1, "guardrail.evaluation", "guardrail.evaluated", payload: """{"defenseclaw.guardrail.decision":"allow","defenseclaw.network.decision":"block"}""");

        var row = await OnlyRow();

        Assert.Equal("allow", row.Action);
    }

    [Theory]
    [InlineData("network.egress", "egress")]
    [InlineData("guardrail.evaluation", "verdict")]
    [InlineData("platform.health", "diagnostic")]
    public void The_kind_of_an_egress_row_is_egress_so_the_event_picker_finds_it(string bucket, string expected) =>
        Assert.Equal(expected, EventStreamReader.TypeOf(bucket, "x", AuditSeverity.Info));

    // ---- The TUI's actionable view ----

    [Theory]
    [InlineData("HIGH", "guardrail.evaluation", "guardrail.evaluated", null, null, true)]
    [InlineData("CRITICAL", "diagnostic", "boot.checked", null, null, true)]
    [InlineData("ERROR", "platform.health", "sink.checked", null, null, true)]
    [InlineData("FATAL", "diagnostic", "boot.checked", null, null, true)]
    [InlineData("INFO", "guardrail.evaluation", "guardrail.evaluated", "allow", null, false)]
    [InlineData("MEDIUM", "asset.scan", "scan.completed", "scan done, 3 findings", null, false)]
    [InlineData("LOW", "platform.health", "sink.ok", null, null, false)]
    [InlineData("INFO", "platform.health", "sink.failed", null, null, true)]                                          // the event name
    [InlineData("INFO", "enforcement.action", "enforcement.applied", "install-blocked", null, true)]                  // the details
    [InlineData("INFO", "guardrail.evaluation", "guardrail.evaluated", null, """{"defenseclaw.guardrail.decision":"deny"}""", true)]
    [InlineData("INFO", "guardrail.evaluation", "guardrail.evaluated", null, """{"defenseclaw.guardrail.reason":"request rejected by the judge"}""", true)]
    [InlineData("MEDIUM", "guardrail.evaluation", "guardrail.evaluated", null, """{"defenseclaw.guardrail.decision":"allow","defenseclaw.guardrail.reason":"clean"}""", false)]
    public async Task A_row_is_actionable_when_the_TUIs_alerts_panel_would_show_it(
        string severity, string bucket, string eventName, string? details, string? payload, bool expected)
    {
        Add("row", 1, bucket, eventName, severity, details: details, payload: payload);

        var row = await OnlyRow();

        Assert.Equal(expected, row.IsActionable);
    }

    [Fact]
    public async Task The_bucket_the_source_and_the_connector_are_part_of_the_text_the_TUI_searches()
    {
        // tui/panels/alerts.py _v8_alert_event: the details line is "bucket=... event_name=... source=... connector=...".
        Add("by-source", 1, "platform.health", "sink.checked", source: "retry-failed");
        Add("by-connector", 2, "platform.health", "sink.checked", connector: "denied-connector");
        Add("quiet", 3, "platform.health", "sink.checked");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.True(rows["by-source"].IsActionable);
        Assert.True(rows["by-connector"].IsActionable);
        Assert.False(rows["quiet"].IsActionable);
    }

    [Fact]
    public async Task The_severity_is_kept_as_stored_because_the_rule_reads_error_and_fatal_as_written()
    {
        Add("error", 1, "platform.health", "x", "ERROR");
        Add("fatal", 2, "platform.health", "x", "FATAL");
        Add("padded", 3, "platform.health", "x", " warning ");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.Equal("ERROR", rows["error"].SeverityText);
        Assert.Equal(AuditSeverity.Unknown, rows["error"].Severity);
        Assert.True(rows["error"].IsActionable);
        Assert.Equal("FATAL", rows["fatal"].SeverityText);
        Assert.Equal(AuditSeverity.Critical, rows["fatal"].Severity);
        Assert.Equal("warning", rows["padded"].SeverityText);
        Assert.False(rows["padded"].IsActionable);
    }

    [Fact]
    public async Task An_event_whose_payload_was_too_large_to_read_is_shown_because_its_decision_is_unknown()
    {
        var big = "{\"k\":\"" + new string('a', EventStreamReader.PayloadByteLimit) + "\"}";
        Add("big", 1, "network.egress", "egress.decided", payload: big);

        var row = await OnlyRow();

        Assert.True(row.PayloadOmitted);
        Assert.True(row.IsActionable);
    }

    [Fact]
    public async Task A_target_is_masked_like_every_other_shown_string()
    {
        Add("secret", 1, "network.egress", "egress.decided", payload: """{"defenseclaw.network.target_ref":"https://example.test/?token=LEAK-TARGET-1"}""");

        var row = await OnlyRow();

        Assert.DoesNotContain("LEAK-TARGET-1", row.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAK-TARGET-1", row.RawJson, StringComparison.Ordinal);
    }
}
