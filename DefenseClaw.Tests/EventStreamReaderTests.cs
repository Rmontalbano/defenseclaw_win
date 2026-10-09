using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Logs panel's Verdicts and Events streams against a database built from the real DDL and synthetic rows only. The plan is
/// asserted as well as the rows: on the live 7.8 GB database a wrong plan is seconds (a HIGH/CRITICAL filter on
/// <c>telemetry.ingest</c> read the whole 156 k-row bucket in 36 s), on a few rows it is invisible.
/// </summary>
public sealed class EventStreamReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private EventStreamReader Reader(int limit = EventStreamReader.DefaultLimit) => new(_database.Path, limit);

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
        string? signal = "logs")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
            VALUES ($id, $timestamp, $action, '', 'gateway', $details, $severity, $bucket, $eventName, $connector, 'sidecar', $signal, $payload)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$eventName", eventName);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$signal", (object?)signal ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private void Seed()
    {
        Add("verdict", 1, "guardrail.evaluation", "guardrail.evaluated", payload: "{\"defenseclaw.guardrail.decision\":\"block\",\"defenseclaw.guardrail.reason\":\"matched a rule\"}");
        Add("enforce", 2, "enforcement.action", "enforcement.applied");
        Add("scan", 3, "asset.scan", "scan.completed", "HIGH");
        Add("finding", 4, "security.finding", "finding.observed", "HIGH");
        Add("telemetry", 5, "telemetry.ingest", "span.received");
        Add("tool", 6, "tool.activity", "tool.invocation.completed");
        Add("health-error", 7, "platform.health", "sink.failed", "HIGH");
        Add("health-info", 8, "platform.health", "sink.ok", "INFO");
        Add("diag-crit", 9, "diagnostic", "panic", "CRITICAL");
        Add("telemetry-error", 10, "telemetry.ingest", "span.dropped", "HIGH");
        Add("not-logs", 11, "guardrail.evaluation", "guardrail.evaluated", signal: "metrics");
    }

    // ---- Buckets, in SQL ----

    [Fact]
    public async Task Verdicts_are_the_four_decision_buckets_and_the_errors_of_the_small_ones()
    {
        Seed();

        var result = await Reader().ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(EventStreamStatus.Ok, result.Status);
        Assert.Equal(new[] { "diag-crit", "health-error", "finding", "scan", "enforce", "verdict" }, result.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Events_hide_telemetry_unless_asked_and_keep_every_other_bucket()
    {
        Seed();

        var hidden = await Reader().ReadAsync(EventStreamKind.Events);
        var shown = await Reader().ReadAsync(EventStreamKind.Events, includeTelemetry: true);

        Assert.DoesNotContain(hidden.Rows, r => r.Bucket == "telemetry.ingest");
        Assert.Equal(new[] { "diag-crit", "health-info", "health-error", "tool", "finding", "scan", "enforce", "verdict" }, hidden.Rows.Select(r => r.Id));
        Assert.Equal(2, shown.Rows.Count(r => r.Bucket == "telemetry.ingest"));
        Assert.Equal("telemetry-error", shown.Rows[0].Id);
    }

    [Fact]
    public async Task Rows_without_a_bucket_are_not_canonical_events()
    {
        Add("old", 1, "guardrail.evaluation", "x");
        using (var connection = _database.OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO audit_events (id, timestamp, action, severity, signal) VALUES ('legacy', '2026-10-01T13:00:00Z', 'scan', 'HIGH', 'logs')";
            _ = command.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync(EventStreamKind.Events);

        Assert.Equal(new[] { "old" }, result.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task The_newest_rows_win_the_limit_and_come_back_newest_first()
    {
        for (var i = 0; i < 30; i++)
        {
            Add($"v{i:00}", i, "guardrail.evaluation", "e");
        }

        var verdicts = await Reader(limit: 5).ReadAsync(EventStreamKind.Verdicts);
        var events = await Reader(limit: 5).ReadAsync(EventStreamKind.Events);

        Assert.Equal(new[] { "v29", "v28", "v27", "v26", "v25" }, verdicts.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "v29", "v28", "v27", "v26", "v25" }, events.Rows.Select(r => r.Id));
    }

    // ---- Projection ----

    [Fact]
    public async Task A_row_shows_its_decision_its_reason_and_its_kind()
    {
        Seed();

        var row = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows.Single(r => r.Id == "verdict");

        Assert.Equal("block", row.Action);
        Assert.Equal("verdict", row.EventType);
        Assert.Equal("guardrail.evaluated block — matched a rule", row.Message);
        Assert.Equal("claudecode", row.Connector);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 1, 0, TimeSpan.Zero), row.Timestamp);
        Assert.Contains("\"body\"", row.RawJson, StringComparison.Ordinal);
        Assert.Contains("defenseclaw.guardrail.decision", row.RawJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("guardrail.evaluation", "guardrail.judge.completed", "HIGH", "judge")]
    [InlineData("guardrail.evaluation", "guardrail.evaluated", "INFO", "verdict")]
    [InlineData("enforcement.action", "x", "INFO", "verdict")]
    [InlineData("security.finding", "finding.observed", "HIGH", "scan_finding")]
    [InlineData("asset.scan", "scan.completed", "INFO", "scan")]
    [InlineData("compliance.activity", "x", "INFO", "activity")]
    [InlineData("platform.health", "x", "HIGH", "error")]
    [InlineData("diagnostic", "x", "LOW", "diagnostic")]
    [InlineData("telemetry.ingest", "x", "CRITICAL", "error")]
    [InlineData("tool.activity", "x", "HIGH", "lifecycle")]
    public void An_events_kind_follows_its_bucket(string bucket, string eventName, string severity, string expected) =>
        Assert.Equal(expected, EventStreamReader.TypeOf(bucket, eventName, AuditSeverityExtensions.Parse(severity)));

    [Fact]
    public async Task Without_a_decision_in_the_payload_the_rows_own_action_and_details_are_shown()
    {
        Add("plain", 1, "enforcement.action", "enforcement.applied", action: "install-blocked", details: "type=skill reason=scanner-error");

        var row = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows.Single();

        Assert.Equal("install-blocked", row.Action);
        Assert.Equal("enforcement.applied install-blocked — type=skill reason=scanner-error", row.Message);
    }

    [Fact]
    public async Task A_payload_that_is_not_json_is_shown_as_text()
    {
        Add("odd", 1, "asset.scan", "scan.completed", payload: "not json at all");

        var row = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows.Single();

        Assert.Contains("not json at all", row.RawJson, StringComparison.Ordinal);
        Assert.False(row.PayloadOmitted);
    }

    // ---- Redaction and bounds ----

    [Fact]
    public async Task Credentials_are_masked_in_the_message_the_details_and_the_raw_body()
    {
        Add(
            "secret",
            1,
            "guardrail.evaluation",
            "guardrail.evaluated",
            details: "forwarded Authorization: Bearer LEAK-DETAILS-1",
            payload: "{\"headers\":{\"authorization\":\"Bearer LEAK-PAYLOAD-2\"},\"api_key\":\"LEAK-KEY-3\",\"note\":\"password=LEAK-PW-4 end\"}");

        var row = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows.Single();

        foreach (var leaked in new[] { "LEAK-DETAILS-1", "LEAK-PAYLOAD-2", "LEAK-KEY-3", "LEAK-PW-4" })
        {
            Assert.DoesNotContain(leaked, row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(leaked, row.RawJson, StringComparison.Ordinal);
        }

        Assert.Contains("[redacted]", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_over_64_KiB_is_not_read_and_says_so()
    {
        var big = "{\"k\":\"" + new string('a', EventStreamReader.PayloadByteLimit) + "\"}";
        Add("big", 1, "asset.scan", "scan.completed", details: "kept", payload: big);
        Add("edge", 2, "asset.scan", "scan.completed", payload: "{\"k\":\"" + new string('b', EventStreamReader.PayloadByteLimit - 8) + "\"}");

        var rows = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows;

        var omitted = rows.Single(r => r.Id == "big");
        Assert.True(omitted.PayloadOmitted);
        Assert.Contains("\"payload_omitted\": true", omitted.RawJson, StringComparison.Ordinal);
        Assert.True(omitted.RawJson.Length < 1024);
        Assert.Contains("kept", omitted.Message, StringComparison.Ordinal);

        var edge = rows.Single(r => r.Id == "edge");
        Assert.False(edge.PayloadOmitted);
        Assert.True(edge.RawJson.Length > EventStreamReader.PayloadByteLimit - 100);
    }

    [Fact]
    public async Task Details_are_cut_to_the_display_limit()
    {
        Add("long", 1, "asset.scan", "scan.completed", details: new string('d', 20_000));

        var row = (await Reader().ReadAsync(EventStreamKind.Verdicts)).Rows.Single();

        Assert.True(row.Message.Length <= 4096);
    }

    // ---- Database states ----

    [Fact]
    public async Task A_missing_database_is_no_database()
    {
        var reader = new EventStreamReader(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dcw-none", "audit.db"));

        var result = await reader.ReadAsync(EventStreamKind.Events);

        Assert.Equal(EventStreamStatus.NoDatabase, result.Status);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_database_without_canonical_columns_is_legacy()
    {
        using var directory = new TempDirectory("dcw-legacy");
        var path = directory.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE audit_events (id TEXT, timestamp TEXT, action TEXT, severity TEXT)";
            _ = command.ExecuteNonQuery();
        }

        SqlitePools.Release(directory.Path);
        var result = await new EventStreamReader(path).ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(EventStreamStatus.LegacySchema, result.Status);
    }

    [Fact]
    public async Task A_cancelled_read_throws_and_does_not_start()
    {
        Seed();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var reader = Reader();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(EventStreamKind.Verdicts, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task The_reader_counts_its_reads()
    {
        var reader = Reader();
        Assert.Equal(0, reader.ReadCount);

        _ = await reader.ReadAsync(EventStreamKind.Events);
        _ = await reader.ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(2, reader.ReadCount);
    }

    // ---- Plans ----

    [Fact]
    public async Task Events_walk_the_timestamp_index_with_no_sort()
    {
        Seed();

        foreach (var includeTelemetry in new[] { false, true })
        {
            var plan = await Reader().ExplainAsync(EventStreamKind.Events, includeTelemetry);

            Assert.Contains(plan, line => line.Contains("idx_audit_timestamp", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Verdict_arms_are_searches_of_the_bucket_index_and_never_scan_the_table()
    {
        Seed();

        var plan = await Reader().ExplainAsync(EventStreamKind.Verdicts);

        Assert.Equal(6, plan.Count(line => line.Contains("SEARCH", StringComparison.Ordinal) && line.Contains("idx_audit_bucket_timestamp (bucket=?)", StringComparison.Ordinal)));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN audit_events", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("idx_audit_severity_timestamp", StringComparison.Ordinal));
    }
}
