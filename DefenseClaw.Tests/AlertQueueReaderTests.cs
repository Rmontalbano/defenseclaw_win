using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The alert queue against a database built from the real DDL (retention triggers, the bucket and severity indexes, the
/// <c>alert_acknowledgement_projection</c> table) and synthetic rows only. The live database is 6.7 GB; what matters
/// there is the plan, so the plan is asserted as well as the rows.
/// </summary>
public sealed class AlertQueueReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private AlertQueueReader Reader(int window = AlertQueueReader.DefaultWindowLimit) => new(_database.Path, window);

    private void Finding(string id, int minute, string severity, string connector = "claudecode", string action = "scan-finding") =>
        _database.InsertEvent(id, Base.AddMinutes(minute), action, severity, "security.finding", connector, eventName: "finding.observed");

    private void Acknowledge(string alertId)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_acknowledgement_projection
                (alert_id, disposition, actor, disposition_at, projection_version, source, source_event_id, updated_at)
            VALUES ($id, 'acknowledged', 'test', '2026-09-30T12:00:00Z', 1, 'modern', 'src', '2026-09-30T12:00:00Z')
            """;
        command.Parameters.AddWithValue("$id", alertId);
        _ = command.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    // ---- The definition (AuditStore.alertQueueEvents) ----

    [Fact]
    public async Task Only_findings_above_info_that_are_neither_dismissed_nor_acknowledged_are_queued()
    {
        // The Mac's own fixture (AlertQueueProjectionTests), plus the cases its SQL and Severity.parse imply.
        _database.InsertEvent("active", Base.AddMinutes(1), "scan", "HIGH");                                   // bucket NULL: a pre-bucket row
        Finding("observed", 2, "HIGH");
        _database.InsertEvent("acked", Base.AddMinutes(3), "scan", "HIGH");
        Acknowledge("acked");
        _database.InsertEvent("telemetry", Base.AddMinutes(4), "scan", "HIGH", "telemetry", eventName: "span.received");
        _database.InsertEvent("warning", Base.AddMinutes(5), "scan", "WARNING");                               // not in the Mac's severity list
        _database.InsertEvent("dismissed", Base.AddMinutes(6), "dismiss-alert", "HIGH");
        _database.InsertEvent("info", Base.AddMinutes(7), "scan", "INFO");
        _database.InsertEvent("error", Base.AddMinutes(8), "scan", "ERROR");
        _database.InsertEvent("wrong-event", Base.AddMinutes(9), "scan-finding", "CRITICAL", "security.finding", eventName: "finding.suppressed");
        _database.InsertEvent("no-severity", Base.AddMinutes(10), "scan", null);
        _database.InsertEvent("wrong-bucket", Base.AddMinutes(11), "scan-finding", "CRITICAL", "guardrail.evaluation", eventName: "finding.observed");

        var result = await Reader().ReadAsync();

        Assert.Equal(AlertQueueStatus.Ok, result.Status);
        Assert.Equal(new[] { "observed", "active" }, result.Counts.Newest.Select(i => i.Id));
        Assert.Equal(2, result.Counts.Total);
    }

    [Theory]
    [InlineData("high", AuditSeverity.High)]
    [InlineData("Critical", AuditSeverity.Critical)]
    [InlineData("medium", AuditSeverity.Medium)]
    [InlineData("low", AuditSeverity.Low)]
    public async Task Severity_is_matched_without_case(string stored, AuditSeverity expected)
    {
        Finding("mixed-case", 1, stored);

        var result = await Reader().ReadAsync();

        Assert.Equal(expected, Assert.Single(result.Counts.Newest).Severity);
    }

    [Fact]
    public async Task Any_action_starting_with_dismiss_is_not_an_alert_whatever_its_case()
    {
        Finding("kept", 1, "HIGH", action: "scan-finding");
        Finding("d1", 2, "HIGH", action: "dismiss-alert");
        Finding("d2", 3, "HIGH", action: "Dismissed");
        Finding("d3", 4, "HIGH", action: "dismiss");
        Finding("not-a-prefix", 5, "HIGH", action: "alert-dismiss");

        var result = await Reader().ReadAsync();

        Assert.Equal(new[] { "not-a-prefix", "kept" }, result.Counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public async Task Acknowledging_an_alert_removes_it_and_a_later_read_sees_that()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "CRITICAL");
        var reader = Reader();

        Assert.Equal(2, (await reader.ReadAsync()).Counts.Total);

        Acknowledge("b");

        var after = (await reader.ReadAsync()).Counts;
        Assert.Equal(1, after.Total);
        Assert.Equal(1, after.High);
        Assert.Equal(0, after.Critical);
    }

    [Fact]
    public async Task A_database_without_the_acknowledgement_table_reads_as_nothing_acknowledged()
    {
        Finding("a", 1, "HIGH");
        Exec("DROP TABLE alert_acknowledgement_projection");

        var result = await Reader().ReadAsync();

        Assert.Equal(AlertQueueStatus.Ok, result.Status);
        Assert.Equal(1, result.Counts.Total);
    }

    [Fact]
    public async Task An_acknowledgement_table_without_alert_id_is_ignored_not_a_failure()
    {
        Finding("a", 1, "HIGH");
        Exec("DROP TABLE alert_acknowledgement_projection");
        Exec("CREATE TABLE alert_acknowledgement_projection (something_else TEXT)");

        var result = await Reader().ReadAsync();

        Assert.Equal(1, result.Counts.Total);
    }

    // ---- Counts, order, window ----

    [Fact]
    public async Task Counts_are_per_severity_and_the_total_is_their_sum()
    {
        Finding("c1", 1, "CRITICAL");
        Finding("h1", 2, "HIGH");
        Finding("h2", 3, "HIGH");
        Finding("m1", 4, "MEDIUM");
        Finding("m2", 5, "MEDIUM");
        Finding("m3", 6, "MEDIUM");
        Finding("l1", 7, "LOW");

        var counts = (await Reader().ReadAsync()).Counts;

        Assert.Equal(1, counts.Critical);
        Assert.Equal(2, counts.High);
        Assert.Equal(3, counts.Medium);
        Assert.Equal(1, counts.Low);
        Assert.Equal(7, counts.Total);
        Assert.False(counts.HasMore);
        Assert.Equal(AlertCountsSource.Database, counts.Source);
    }

    [Fact]
    public async Task The_two_buckets_merge_into_one_newest_first_list()
    {
        // Interleaved: findings (bucket security.finding) and pre-bucket rows (bucket NULL) must sort as one list.
        Finding("f1", 1, "HIGH");
        _database.InsertEvent("n2", Base.AddMinutes(2), "scan", "LOW");
        Finding("f3", 3, "MEDIUM");
        _database.InsertEvent("n4", Base.AddMinutes(4), "scan", "CRITICAL");
        Finding("f5", 5, "LOW");

        var result = await Reader().ReadAsync();

        Assert.Equal(new[] { "f5", "n4", "f3", "n2", "f1" }, result.Counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public async Task Rows_sharing_a_timestamp_are_ordered_newest_rowid_first()
    {
        Finding("tie-1", 1, "HIGH");
        Finding("tie-2", 1, "HIGH");
        Finding("tie-3", 1, "HIGH");

        var result = await Reader().ReadAsync();

        Assert.Equal(new[] { "tie-3", "tie-2", "tie-1" }, result.Counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public async Task The_window_is_the_newest_rows_and_more_is_reported_when_it_overflows()
    {
        for (var i = 1; i <= 7; i++)
        {
            Finding($"f{i}", i, i % 2 == 0 ? "HIGH" : "LOW");
        }

        var counts = (await Reader(window: 5).ReadAsync()).Counts;

        // f3..f7 are the newest five: f4 and f6 are HIGH, f3, f5 and f7 are LOW.
        Assert.Equal(5, counts.Total);
        Assert.Equal(2, counts.High);
        Assert.Equal(3, counts.Low);
        Assert.True(counts.HasMore);
        Assert.Equal(new[] { "f7", "f6", "f5", "f4", "f3" }, counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public async Task A_window_that_just_fits_is_not_more()
    {
        for (var i = 1; i <= 5; i++)
        {
            Finding($"f{i}", i, "HIGH");
        }

        var counts = (await Reader(window: 5).ReadAsync()).Counts;

        Assert.Equal(5, counts.Total);
        Assert.False(counts.HasMore);
    }

    [Fact]
    public async Task The_window_is_taken_after_the_filters_so_hidden_rows_do_not_use_it_up()
    {
        // Ten newer rows that are not alerts (INFO, acknowledged) must not push the real ones out of a window of 3.
        Finding("keep1", 1, "HIGH");
        Finding("keep2", 2, "HIGH");
        Finding("keep3", 3, "HIGH");
        for (var i = 10; i < 15; i++)
        {
            Finding($"info{i}", i, "INFO");
        }

        for (var i = 20; i < 25; i++)
        {
            Finding($"acked{i}", i, "CRITICAL");
            Acknowledge($"acked{i}");
        }

        var counts = (await Reader(window: 3).ReadAsync()).Counts;

        Assert.Equal(new[] { "keep3", "keep2", "keep1" }, counts.Newest.Select(i => i.Id));
        Assert.False(counts.HasMore);
    }

    [Fact]
    public async Task Newest_is_limited_but_the_tallies_cover_the_whole_window()
    {
        for (var i = 1; i <= 8; i++)
        {
            Finding($"f{i}", i, "HIGH");
        }

        var counts = (await Reader().ReadAsync(newestLimit: 3)).Counts;

        Assert.Equal(8, counts.Total);
        Assert.Equal(new[] { "f8", "f7", "f6" }, counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public async Task An_empty_queue_is_zero_counts_and_ok()
    {
        _database.InsertEvent("noise", Base, "hook_decision", "INFO", "guardrail.evaluation", eventName: "evt");

        var result = await Reader().ReadAsync();

        Assert.Equal(AlertQueueStatus.Ok, result.Status);
        Assert.Equal(0, result.Counts.Total);
        Assert.Empty(result.Counts.Newest);
        Assert.False(result.Counts.HasMore);
    }

    // ---- What an item carries ----

    [Fact]
    public async Task An_item_carries_id_severity_action_target_connector_and_time()
    {
        _database.InsertEvent("full", Base.AddMinutes(5).AddTicks(1234567), "scan-finding", "CRITICAL", "security.finding", "  codex  ", eventName: "finding.observed");
        Exec("UPDATE audit_events SET target = '/work/app/src/secret.py' WHERE id = 'full'");

        var item = Assert.Single((await Reader().ReadAsync()).Counts.Newest);

        Assert.Equal("full", item.Id);
        Assert.Equal(AuditSeverity.Critical, item.Severity);
        Assert.Equal("scan-finding", item.Action);
        Assert.Equal("/work/app/src/secret.py", item.Target);
        Assert.Equal("codex", item.Connector);
        Assert.Equal(Base.AddMinutes(5).AddTicks(1234567), item.Timestamp);
    }

    [Fact]
    public async Task An_empty_target_and_a_missing_connector_are_null()
    {
        _database.InsertEvent("bare", Base, "scan-finding", "HIGH", "security.finding", null, eventName: "finding.observed");

        var item = Assert.Single((await Reader().ReadAsync()).Counts.Newest);

        Assert.Null(item.Target);
        Assert.Null(item.Connector);
    }

    [Fact]
    public async Task A_long_target_is_cut_at_the_limit_in_the_query_not_after_it_is_read()
    {
        Finding("long", 1, "HIGH");
        Exec($"UPDATE audit_events SET target = '{new string('x', 5000)}' WHERE id = 'long'");

        var item = Assert.Single((await Reader().ReadAsync()).Counts.Newest);

        Assert.Equal(AlertQueueReader.TargetLimit, item.Target!.Length);
    }

    [Fact]
    public async Task A_row_with_a_null_id_is_still_counted_with_an_empty_id()
    {
        // "id TEXT PRIMARY KEY" admits NULL in SQLite; AuditReader shows such a row with an empty id, and so does the queue.
        Exec("INSERT INTO audit_events (id, timestamp, action, severity, bucket, event_name) VALUES (NULL, '2026-09-30T12:00:00Z', 'scan-finding', 'HIGH', 'security.finding', 'finding.observed')");

        var item = Assert.Single((await Reader().ReadAsync()).Counts.Newest);

        Assert.Equal(string.Empty, item.Id);
    }

    [Fact]
    public async Task An_unparseable_timestamp_is_the_minimum_not_a_failure()
    {
        Exec("INSERT INTO audit_events (id, timestamp, action, severity, bucket, event_name) VALUES ('odd', 'yesterday-ish', 'scan-finding', 'HIGH', 'security.finding', 'finding.observed')");

        var item = Assert.Single((await Reader().ReadAsync()).Counts.Newest);

        Assert.Equal(DateTimeOffset.MinValue, item.Timestamp);
    }

    [Fact]
    public async Task Counts_keep_a_breakdown_per_connector()
    {
        Finding("a", 1, "HIGH", "claudecode");
        Finding("b", 2, "CRITICAL", "claudecode");
        Finding("c", 3, "LOW", "codex");
        _database.InsertEvent("d", Base.AddMinutes(4), "scan", "MEDIUM");

        var counts = (await Reader().ReadAsync()).Counts;

        Assert.Equal(new SeverityTally(1, 1, 0, 0), counts.ByConnector["claudecode"]);
        Assert.Equal(new SeverityTally(0, 0, 0, 1), counts.ByConnector["codex"]);
        Assert.Equal(new SeverityTally(0, 0, 1, 0), counts.ByConnector[string.Empty]);
    }

    // ---- Schemas and files ----

    [Fact]
    public async Task A_missing_file_is_no_database_and_nothing_is_created()
    {
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");

        var result = await new AlertQueueReader(path).ReadAsync();

        Assert.Equal(AlertQueueStatus.NoDatabase, result.Status);
        Assert.Same(AlertCounts.Empty, result.Counts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_database_with_no_audit_table_is_no_database()
    {
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x INTEGER)";
            _ = command.ExecuteNonQuery();
        }

        var result = await new AlertQueueReader(path).ReadAsync();

        Assert.Equal(AlertQueueStatus.NoDatabase, result.Status);
    }

    [Fact]
    public async Task A_pre_v8_schema_asks_for_the_gateway_fallback()
    {
        // The Mac's own legacy fixture: no bucket, no event_name, no projection table.
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT, target TEXT, actor TEXT,
                    details TEXT, severity TEXT, run_id TEXT, structured_json TEXT, connector TEXT);
                INSERT INTO audit_events (id, timestamp, action, severity) VALUES ('legacy', '2026-07-22T12:00:00Z', 'scan-finding', 'HIGH');
                """;
            _ = command.ExecuteNonQuery();
        }

        var result = await new AlertQueueReader(path).ReadAsync();

        Assert.Equal(AlertQueueStatus.LegacySchema, result.Status);
        Assert.Same(AlertCounts.Empty, result.Counts);
        Assert.Empty(await new AlertQueueReader(path).ExplainAsync());
    }

    [Fact]
    public async Task A_database_missing_optional_columns_still_reads()
    {
        // No connector, no target: the queue lists them as NULL rather than refusing the database.
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT, severity TEXT, bucket TEXT, event_name TEXT);
                INSERT INTO audit_events VALUES ('x', '2026-07-22T12:00:00Z', 'scan-finding', 'HIGH', 'security.finding', 'finding.observed');
                """;
            _ = command.ExecuteNonQuery();
        }

        var item = Assert.Single((await new AlertQueueReader(path).ReadAsync()).Counts.Newest);

        Assert.Equal("x", item.Id);
        Assert.Null(item.Connector);
        Assert.Null(item.Target);
    }

    [Fact]
    public async Task The_database_is_opened_read_only_and_a_read_only_file_reads_fine()
    {
        Finding("a", 1, "HIGH");
        SqliteConnection.ClearAllPools();
        var before = File.GetLastWriteTimeUtc(_database.Path);
        var length = new FileInfo(_database.Path).Length;
        File.SetAttributes(_database.Path, FileAttributes.ReadOnly);
        try
        {
            var result = await Reader().ReadAsync();

            Assert.Equal(1, result.Counts.Total);
        }
        finally
        {
            File.SetAttributes(_database.Path, FileAttributes.Normal);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.GetLastWriteTimeUtc(_database.Path));
        Assert.Equal(length, new FileInfo(_database.Path).Length);
    }

    [Fact]
    public async Task A_read_does_not_block_a_writer_and_sees_what_was_committed()
    {
        Finding("a", 1, "HIGH");
        var reader = Reader();
        _ = await reader.ReadAsync();

        Finding("b", 2, "CRITICAL");

        Assert.Equal(2, (await reader.ReadAsync()).Counts.Total);
    }

    // ---- Cancellation and timeout ----

    [Fact]
    public async Task A_cancelled_token_ends_the_read_and_never_starts_the_query()
    {
        Finding("a", 1, "HIGH");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_timeout_that_is_not_positive_is_refused(int seconds)
    {
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(timeout: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task A_read_that_outlasts_its_timeout_is_stopped_with_a_timeout_not_a_cancellation()
    {
        // 80k INFO "findings": the arm has to look at every one of them to find none above INFO, which is far longer than 1 ms.
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = """
                    CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT, target TEXT, severity TEXT, connector TEXT, bucket TEXT, event_name TEXT);
                    CREATE INDEX idx_audit_bucket_timestamp ON audit_events(bucket, timestamp);
                    CREATE INDEX idx_audit_event_name_timestamp ON audit_events(event_name, timestamp);
                    """;
                _ = ddl.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO audit_events VALUES ($id, $ts, 'scan-finding', 'target', 'INFO', 'claudecode', 'security.finding', 'finding.observed')";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var ts = insert.Parameters.Add("$ts", SqliteType.Text);
            for (var i = 0; i < 80_000; i++)
            {
                id.Value = "id-" + i;
                ts.Value = "2026-09-30T12:00:00." + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture) + "Z";
                _ = insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var reader = new AlertQueueReader(path);
        _ = await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(timeout: TimeSpan.FromMilliseconds(1)));

        // The same reader, with room, answers: the interrupted connection left nothing behind.
        var result = await reader.ReadAsync(timeout: Timeout.InfiniteTimeSpan);
        Assert.Equal(0, result.Counts.Total);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Reads_are_counted_so_an_idle_service_can_be_shown_idle()
    {
        var reader = Reader();
        Assert.Equal(0, reader.ReadCount);

        _ = await reader.ReadAsync();
        _ = await reader.ReadAsync();

        Assert.Equal(2, reader.ReadCount);
    }

    // ---- The plan (the live database is 6.7 GB: 55 ms for the obvious single statement, 3 ms for this one) ----

    [Fact]
    public async Task The_statement_is_two_index_searches_and_never_a_table_scan()
    {
        for (var i = 1; i <= 30; i++)
        {
            Finding($"f{i}", i, i % 3 == 0 ? "HIGH" : "INFO");
            _database.InsertEvent($"n{i}", Base.AddMinutes(i), "scan", "LOW");
        }

        var plan = await Reader().ExplainAsync();

        // Arm one walks the finding rows on the event-name or bucket index (the planner has no statistics and takes
        // whichever it likes; both are (key, timestamp) and ordered the way the arm sorts). Arm two is bucket IS NULL.
        Assert.Matches(new Regex(@"SEARCH \w+ USING INDEX idx_audit_(event_name|bucket)_timestamp \((event_name|bucket)=\?\)"), string.Join("\n", plan));
        Assert.Contains(plan, line => line.Contains("idx_audit_bucket_timestamp (bucket=?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN e", StringComparison.Ordinal) || line.StartsWith("SCAN audit_events", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("MULTI-INDEX OR", StringComparison.Ordinal));

        // The only sorts are the merge's, over a co-routine's <= window rows: each follows a "SCAN (subquery-n)" line.
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan[i].Contains("TEMP B-TREE", StringComparison.Ordinal))
            {
                Assert.StartsWith("SCAN (subquery", plan[i - 1], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task The_acknowledgement_check_is_a_key_lookup_on_the_projection_table()
    {
        Finding("f", 1, "HIGH");

        var plan = await Reader().ExplainAsync();

        Assert.Contains(plan, line => line.Contains("alert_acknowledgement_projection_1 (alert_id=?)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_rows_come_back_when_the_statement_is_written_the_way_the_mac_writes_it()
    {
        // The equivalence the optimisation rests on: two arms merged == the Mac's one statement with an OR.
        var random = new Random(42);
        var severities = new[] { "INFO", "LOW", "MEDIUM", "HIGH", "CRITICAL", "WARNING", "ERROR", "high" };
        for (var i = 0; i < 160; i++)
        {
            var minute = random.Next(0, 120);
            var severity = severities[random.Next(severities.Length)];
            switch (random.Next(4))
            {
                case 0:
                    _database.InsertEvent($"r{i}", Base.AddMinutes(minute), "scan-finding", severity, "security.finding", "claudecode", eventName: "finding.observed");
                    break;
                case 1:
                    _database.InsertEvent($"r{i}", Base.AddMinutes(minute), "scan", severity);
                    break;
                case 2:
                    _database.InsertEvent($"r{i}", Base.AddMinutes(minute), random.Next(5) == 0 ? "dismiss-alert" : "scan", severity, "telemetry.ingest", eventName: "finding.observed");
                    break;
                default:
                    _database.InsertEvent($"r{i}", Base.AddMinutes(minute), "scan-finding", severity, "security.finding", "codex", eventName: "finding.suppressed");
                    break;
            }

            if (random.Next(10) == 0)
            {
                Acknowledge($"r{i}");
            }
        }

        var window = 25;
        var counts = (await Reader(window).ReadAsync(newestLimit: window)).Counts;

        List<string> expected;
        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id FROM audit_events
                WHERE UPPER(severity) IN ('CRITICAL','HIGH','MEDIUM','LOW')
                  AND action NOT LIKE 'dismiss%'
                  AND (bucket IS NULL OR (bucket = 'security.finding' AND event_name = 'finding.observed'))
                  AND NOT EXISTS (SELECT 1 FROM alert_acknowledgement_projection AS projection WHERE projection.alert_id = audit_events.id)
                ORDER BY timestamp DESC, rowid DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", window);
            expected = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                expected.Add(reader.GetString(0));
            }
        }

        Assert.Equal(expected, counts.Newest.Select(i => i.Id));
        Assert.Equal(window, counts.Total);
        Assert.True(counts.HasMore);
    }
}
