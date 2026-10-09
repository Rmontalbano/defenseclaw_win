using System.Security.Cryptography;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// "Two refreshes with no database change: the second does no row decoding." Every reader of audit.db remembers its last answers under
/// the shared change probe's stamp; a call that finds the stamp where the last read left it hands back the remembered answer without a
/// connection, a statement or a row decoded. The counters (<c>RowsDecoded</c>, <c>UnchangedReads</c>) are what make that assertable
/// rather than a matter of latency. Synthetic databases from the real DDL only.
/// </summary>
public sealed class UnchangedSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly AuditChangeProbe _probe;
    private int _minute;

    public UnchangedSnapshotTests()
    {
        _probe = new AuditChangeProbe(_database.Path);
    }

    public void Dispose()
    {
        _probe.Dispose();
        _database.Dispose();
    }

    /// <summary>One more event, newer than every one before it.</summary>
    private void Add(string id, string action = "hook_decision", string severity = "INFO", string bucket = "guardrail.evaluation", string? connector = "claudecode") =>
        _database.InsertEvent(id, Base.AddMinutes(++_minute), action, severity, bucket, connector, eventName: "evt");

    /// <summary>Many events in one transaction (one commit, so it is quick).</summary>
    private void Bulk(int count)
    {
        using var connection = _database.OpenWritable();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, connector, event_name)
            VALUES ($id, $timestamp, 'hook_decision', '', 'audit_logger', 'INFO', 'guardrail.evaluation', 'claudecode', 'evt')
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        for (var i = 0; i < count; i++)
        {
            id.Value = "bulk-" + (++_minute).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            timestamp.Value = TestAuditDatabase.FormatTimestamp(Base.AddSeconds(_minute));
            _ = command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private AuditReader Reader(AuditChangeProbe? probe = null, TimeProvider? time = null) =>
        new(_database.Path, probe: probe ?? _probe, timeProvider: time);

    // ------------------------------------------------------------------ the Audit panel's reader

    [Fact]
    public async Task A_second_page_read_of_an_unchanged_database_decodes_no_rows()
    {
        for (var i = 0; i < 30; i++)
        {
            Add("e" + i);
        }

        var reader = Reader();
        var query = new AuditQuery { Limit = 10 };

        var first = await reader.QueryAsync(query);
        var decoded = reader.RowsDecoded;
        var second = await reader.QueryAsync(query);

        // The page query fetches one row past the page to answer HasMore, so eleven rows were decoded the first time.
        Assert.Equal(11, decoded);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(1, reader.UnchangedReads);
        Assert.Same(first, second);
        Assert.Equal(2, reader.PageQueryCount);
        Assert.Equal(10, second.Events.Count);
    }

    [Fact]
    public async Task A_query_built_again_with_the_same_filters_finds_the_same_answer()
    {
        for (var i = 0; i < 12; i++)
        {
            Add("e" + i, action: i % 2 == 0 ? "hook_decision" : "scan-finding");
        }

        var reader = Reader();

        // Separately built lists, as a panel builds them for every load: a record would compare them by reference.
        AuditQuery Build() => new()
        {
            Buckets = new[] { "guardrail.evaluation" },
            ActionAnyOf = new[] { "hook", "scan" },
            MinimumSeverity = AuditSeverity.Info,
            Connector = "claudecode",
            SearchText = "evt",
            Limit = 5,
        };

        var first = await reader.QueryAsync(Build());
        var second = await reader.QueryAsync(Build());

        Assert.Same(first, second);
        Assert.Equal(1, reader.UnchangedReads);
    }

    [Fact]
    public async Task A_commit_makes_the_next_read_run_again_and_see_the_new_row()
    {
        for (var i = 0; i < 5; i++)
        {
            Add("e" + i);
        }

        var reader = Reader();
        var query = new AuditQuery { Limit = 10 };
        var first = await reader.QueryAsync(query);
        var decoded = reader.RowsDecoded;

        Add("newest");
        var second = await reader.QueryAsync(query);
        var third = await reader.QueryAsync(query);

        Assert.NotSame(first, second);
        Assert.Equal("newest", second.Events[0].Id);
        Assert.Equal(first.Events.Count + 1, second.Events.Count);
        Assert.True(reader.RowsDecoded > decoded);
        Assert.Same(second, third);
        Assert.Equal(1, reader.UnchangedReads);
    }

    [Fact]
    public async Task Several_queries_are_remembered_side_by_side()
    {
        for (var i = 0; i < 8; i++)
        {
            Add("e" + i, bucket: i % 2 == 0 ? "guardrail.evaluation" : "asset.scan");
        }

        var reader = Reader();
        var scans = new AuditQuery { Bucket = "asset.scan" };
        var guardrails = new AuditQuery { Bucket = "guardrail.evaluation" };

        var firstScans = await reader.QueryAsync(scans);
        var firstGuardrails = await reader.QueryAsync(guardrails);
        var secondScans = await reader.QueryAsync(scans);
        var secondGuardrails = await reader.QueryAsync(guardrails);

        Assert.Same(firstScans, secondScans);
        Assert.Same(firstGuardrails, secondGuardrails);
        Assert.NotSame(firstScans, firstGuardrails);
        Assert.Equal(2, reader.UnchangedReads);
    }

    [Fact]
    public async Task The_total_is_remembered_the_same_way_whatever_the_page_size_or_cursor()
    {
        for (var i = 0; i < 9; i++)
        {
            Add("e" + i);
        }

        var reader = Reader();

        var first = await reader.CountAsync(new AuditQuery { Limit = 100 });
        var second = await reader.CountAsync(new AuditQuery { Limit = 7, After = new AuditCursor(1, "x") });

        Assert.Equal(9, first);
        Assert.Equal(first, second);
        Assert.Equal(1, reader.UnchangedReads);

        Add("tenth");
        Assert.Equal(10, await reader.CountAsync(new AuditQuery { Limit = 100 }));
    }

    [Fact]
    public async Task Without_a_probe_every_read_is_a_read()
    {
        for (var i = 0; i < 6; i++)
        {
            Add("e" + i);
        }

        var reader = new AuditReader(_database.Path);
        var query = new AuditQuery { Limit = 10 };

        var first = await reader.QueryAsync(query);
        var second = await reader.QueryAsync(query);

        Assert.NotSame(first, second);
        Assert.Equal(0, reader.UnchangedReads);
        Assert.Equal(12, reader.RowsDecoded);
    }

    [Fact]
    public async Task A_database_the_probe_cannot_read_is_never_remembered()
    {
        for (var i = 0; i < 3; i++)
        {
            Add("e" + i);
        }

        // A probe on the wrong path answers unknown, which matches nothing: the reader then reads every time, as it always did.
        using var wrong = new AuditChangeProbe(_database.Path + ".absent");
        var reader = Reader(wrong);

        _ = await reader.QueryAsync(new AuditQuery());
        _ = await reader.QueryAsync(new AuditQuery());

        Assert.Equal(0, reader.UnchangedReads);
        Assert.Equal(6, reader.RowsDecoded);
    }

    [Fact]
    public async Task A_query_with_a_payload_limit_of_its_own_is_never_remembered()
    {
        for (var i = 0; i < 3; i++)
        {
            Add("e" + i);
        }

        var reader = Reader();
        var export = new AuditQuery { PayloadLimitBytes = AuditQuery.NoPayloadLimit };

        _ = await reader.QueryAsync(export);
        _ = await reader.QueryAsync(export);

        Assert.Equal(0, reader.UnchangedReads);
        Assert.Equal(6, reader.RowsDecoded);
    }

    [Fact]
    public async Task A_result_with_more_rows_than_a_few_screens_is_read_once_and_not_kept()
    {
        Bulk(400);
        var reader = Reader();
        var big = new AuditQuery { Limit = 300 };

        var first = await reader.QueryAsync(big);
        var second = await reader.QueryAsync(big);

        Assert.Equal(300, first.Events.Count);
        Assert.NotSame(first, second);
        Assert.Equal(0, reader.UnchangedReads);
    }

    [Fact]
    public async Task A_page_of_very_large_values_is_read_once_and_not_kept()
    {
        // Twenty rows of 250,000 characters each are under the payload limit one by one and five million characters together.
        var details = new string('d', 250_000);
        for (var i = 0; i < 20; i++)
        {
            _database.InsertEvent("big-" + i, Base.AddMinutes(++_minute), "hook_decision", "INFO", "guardrail.evaluation", "claudecode", details: details);
        }

        var reader = Reader();
        var first = await reader.QueryAsync(new AuditQuery());
        var second = await reader.QueryAsync(new AuditQuery());

        Assert.Equal(20, first.Events.Count);
        Assert.All(first.Events, e => Assert.Empty(e.Oversized));
        Assert.NotSame(first, second);
        Assert.Equal(0, reader.UnchangedReads);
    }

    [Fact]
    public async Task A_remembered_answer_is_not_trusted_for_ever()
    {
        for (var i = 0; i < 4; i++)
        {
            Add("e" + i);
        }

        var clock = new ManualClock();
        var reader = Reader(time: clock);
        var query = new AuditQuery();

        _ = await reader.QueryAsync(query);
        clock.Advance(TimeSpan.FromMinutes(4));
        _ = await reader.QueryAsync(query);
        Assert.Equal(1, reader.UnchangedReads);

        // The probe is exact, so this is only the backstop: past the age the database is simply read again.
        clock.Advance(TimeSpan.FromMinutes(2));
        _ = await reader.QueryAsync(query);

        Assert.Equal(1, reader.UnchangedReads);
        Assert.Equal(8, reader.RowsDecoded);
    }

    [Fact]
    public async Task A_cancelled_read_remembers_nothing_and_a_failed_one_does_not_poison_the_next()
    {
        Add("e");
        var reader = Reader();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.QueryAsync(new AuditQuery(), cancelled.Token));
        var page = await reader.QueryAsync(new AuditQuery());

        Assert.Single(page.Events);
        Assert.Equal(0, reader.UnchangedReads);
    }

    [Fact]
    public async Task An_archive_is_remembered_without_being_given_a_probe_and_nothing_is_created_or_written()
    {
        for (var i = 0; i < 6; i++)
        {
            Add("e" + i);
        }

        SqlitePools.Release(_database.Path);
        var directory = Path.GetDirectoryName(_database.Path)!;
        var files = Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var hash = Hash(_database.Path);
        var reader = AuditArchive.OpenReader(_database.Path);
        var query = new AuditQuery { Limit = 4 };

        var first = await reader.QueryAsync(query);
        var second = await reader.QueryAsync(query);
        var total = await reader.CountAsync(query);
        var total2 = await reader.CountAsync(query);

        Assert.True(reader.IsImmutable);
        Assert.Same(first, second);
        Assert.Equal(total, total2);
        Assert.Equal(2, reader.UnchangedReads);
        Assert.Equal(files, Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.Equal(hash, Hash(_database.Path));
    }

    // ------------------------------------------------------------------ the alert queue

    private void Finding(string id, string severity = "HIGH", string connector = "claudecode") =>
        _database.InsertEvent(id, Base.AddMinutes(++_minute), "scan-finding", severity, "security.finding", connector, eventName: "finding.observed");

    [Fact]
    public async Task A_second_queue_read_of_an_unchanged_database_returns_the_same_counts_and_decodes_nothing()
    {
        for (var i = 0; i < 12; i++)
        {
            Finding("f" + i);
        }

        var reader = new AlertQueueReader(_database.Path, probe: _probe);

        var first = await reader.ReadAsync(newestLimit: 5);
        var decoded = reader.RowsDecoded;
        var second = await reader.ReadAsync(newestLimit: 5);

        Assert.Equal(12, decoded);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(1, reader.UnchangedReads);
        Assert.Same(first.Counts, second.Counts);
        Assert.Equal(AlertQueueStatus.Ok, second.Status);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task A_second_limit_for_the_same_database_state_is_cut_from_the_rows_already_read()
    {
        for (var i = 0; i < 12; i++)
        {
            Finding("f" + i);
        }

        var reader = new AlertQueueReader(_database.Path, probe: _probe);

        // The badge reads with 3, the panel with 500: one statement serves both.
        var badge = await reader.ReadAsync(newestLimit: 3);
        var panel = await reader.ReadAsync(newestLimit: 500);
        var panelAgain = await reader.ReadAsync(newestLimit: 500);

        Assert.Equal(3, badge.Counts.Newest.Count);
        Assert.Equal(12, panel.Counts.Newest.Count);
        Assert.Equal(12, reader.RowsDecoded);
        Assert.Equal(2, reader.UnchangedReads);
        Assert.Same(panel.Counts, panelAgain.Counts);
        Assert.Equal(badge.Counts.Total, panel.Counts.Total);
    }

    [Fact]
    public async Task A_row_that_is_not_in_the_queue_still_moves_the_probe_and_the_queue_is_read_again()
    {
        Finding("f1");
        var reader = new AlertQueueReader(_database.Path, probe: _probe);
        var first = await reader.ReadAsync();

        // Telemetry, not a finding: the queue is the same, but the database changed and the probe cannot know which rows matter.
        Add("telemetry", bucket: "telemetry.ingest");
        var second = await reader.ReadAsync();

        Assert.NotSame(first.Counts, second.Counts);
        Assert.True(first.Counts.SameAs(second.Counts));
        Assert.Equal(2, reader.RowsDecoded);
        Assert.Equal(0, reader.UnchangedReads);

        Finding("f2");
        var third = await reader.ReadAsync();
        Assert.Equal(2, third.Counts.Total);
    }

    [Fact]
    public async Task A_database_that_predates_the_queue_is_remembered_as_such()
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

        using var probe = new AuditChangeProbe(path);
        var reader = new AlertQueueReader(path, probe: probe);

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();

        Assert.Equal(AlertQueueStatus.LegacySchema, first.Status);
        Assert.Equal(AlertQueueStatus.LegacySchema, second.Status);
        Assert.Equal(1, reader.UnchangedReads);
    }

    // ------------------------------------------------------------------ the mutation history

    private void Change(string id, string action = "config.change.applied", string bucket = "compliance.activity") =>
        _database.InsertEvent(id, Base.AddMinutes(++_minute), action, "INFO", bucket, null, details: "details of " + id, structuredJson: "{\"k\":\"" + id + "\"}", actor: "gateway_api");

    [Fact]
    public async Task A_second_mutation_read_of_an_unchanged_database_returns_the_same_items_and_decodes_nothing()
    {
        for (var i = 0; i < 6; i++)
        {
            Change("c" + i);
        }

        var reader = new MutationReader(_database.Path, _probe);

        var first = await reader.ReadAsync();
        var decoded = reader.RowsDecoded;
        var second = await reader.ReadAsync();

        Assert.Equal(6, decoded);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(1, reader.UnchangedReads);
        Assert.Same(first.Items, second.Items);
        Assert.Equal(first.HasMore, second.HasMore);

        Change("newer");
        var third = await reader.ReadAsync();
        Assert.Equal("newer", third.Items[0].Id);
        Assert.Equal(1, reader.UnchangedReads);
    }

    [Fact]
    public async Task Mutation_reads_with_different_limits_do_not_share_an_answer()
    {
        for (var i = 0; i < 6; i++)
        {
            Change("c" + i);
        }

        var reader = new MutationReader(_database.Path, _probe);

        var few = await reader.ReadAsync(limit: 2);
        var many = await reader.ReadAsync(limit: 50);

        Assert.Equal(2, few.Items.Count);
        Assert.Equal(6, many.Items.Count);
        Assert.True(few.HasMore);
        Assert.False(many.HasMore);
        Assert.Equal(0, reader.UnchangedReads);
    }

    // ------------------------------------------------------------------ the Logs streams

    private void Stream(string id, string bucket = "guardrail.evaluation", string eventName = "guardrail.evaluated", string? payload = null)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
            VALUES ($id, $timestamp, 'act', '', 'gateway', 'details', 'INFO', $bucket, $eventName, 'claudecode', 'sidecar', 'logs', $payload)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(++_minute)));
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$eventName", eventName);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_second_stream_read_of_an_unchanged_database_projects_no_rows()
    {
        for (var i = 0; i < 7; i++)
        {
            Stream("s" + i, payload: "{\"defenseclaw.guardrail.decision\":\"block\"}");
        }

        var reader = new EventStreamReader(_database.Path, probe: _probe);

        var first = await reader.ReadAsync(EventStreamKind.Verdicts);
        var decoded = reader.RowsDecoded;
        var second = await reader.ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(7, decoded);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(1, reader.UnchangedReads);
        Assert.Same(first.Rows, second.Rows);
        Assert.Equal(2, reader.ReadCount);

        Stream("newer");
        var third = await reader.ReadAsync(EventStreamKind.Verdicts);
        Assert.Equal("newer", third.Rows[0].Id);
    }

    [Fact]
    public async Task A_stream_read_after_the_database_moved_projects_only_the_rows_it_has_not_seen()
    {
        for (var i = 0; i < 6; i++)
        {
            Stream("s" + i, payload: "{\"defenseclaw.guardrail.decision\":\"block\"}");
        }

        var reader = new EventStreamReader(_database.Path, probe: _probe);
        var first = await reader.ReadAsync(EventStreamKind.Verdicts);
        Assert.Equal(6, reader.RowsDecoded);

        // A row the Verdicts stream does not list: the database moved (the probe knows), so the statement runs again - and finds the six it knew.
        Stream("t1", bucket: "telemetry.ingest", eventName: "span.received");
        var second = await reader.ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(6, reader.RowsDecoded);
        Assert.Equal(0, reader.UnchangedReads);
        Assert.NotSame(first.Rows, second.Rows);
        Assert.True(first.Rows.Zip(second.Rows).All(pair => ReferenceEquals(pair.First, pair.Second)), "the very same rows, not copies");

        // One new verdict: one row projected, the six kept.
        Stream("s6", payload: "{\"defenseclaw.guardrail.decision\":\"allow\"}");
        var third = await reader.ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(7, reader.RowsDecoded);
        Assert.Equal(7, third.Rows.Count);
        Assert.Equal("s6", third.Rows[0].Id);
        Assert.Equal("allow", third.Rows[0].Action);
        Assert.Same(first.Rows[0], third.Rows[1]);
    }

    [Fact]
    public async Task Each_stream_has_its_own_answer_and_telemetry_only_matters_to_events()
    {
        Stream("v");
        Stream("t", bucket: "telemetry.ingest", eventName: "span.received");
        var reader = new EventStreamReader(_database.Path, probe: _probe);

        var verdicts = await reader.ReadAsync(EventStreamKind.Verdicts);
        var verdictsAgain = await reader.ReadAsync(EventStreamKind.Verdicts, includeTelemetry: true);
        var events = await reader.ReadAsync(EventStreamKind.Events);
        var withTelemetry = await reader.ReadAsync(EventStreamKind.Events, includeTelemetry: true);
        var eventsAgain = await reader.ReadAsync(EventStreamKind.Events);

        // Verdicts ignore the telemetry switch, so the second Verdicts read is the first one's answer.
        Assert.Same(verdicts.Rows, verdictsAgain.Rows);
        Assert.NotSame(events.Rows, withTelemetry.Rows);
        Assert.Single(events.Rows);
        Assert.Equal(2, withTelemetry.Rows.Count);
        Assert.Same(events.Rows, eventsAgain.Rows);
        Assert.Equal(2, reader.UnchangedReads);
    }

    // ------------------------------------------------------------------ the egress feed

    private void Egress(string id)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, bucket, connector, event_name, source)
            VALUES ($id, $timestamp, 'network-egress', '', 'gateway', 'INFO', $structured, 'network.egress', 'claudecode', 'network.egress', 'gateway')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(++_minute)));
        command.Parameters.AddWithValue("$structured", "{\"defenseclaw.network.decision\":\"block\",\"defenseclaw.network.target_ref\":\"api.example.test\"}");
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_second_egress_read_of_an_unchanged_database_decodes_no_rows()
    {
        for (var i = 0; i < 5; i++)
        {
            Egress("x" + i);
        }

        var reader = new NetworkEgressReader(_database.Path, _probe);

        var first = await reader.ReadRecentAsync();
        var decoded = reader.RowsDecoded;
        var second = await reader.ReadRecentAsync();

        Assert.Equal(5, decoded);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(1, reader.UnchangedReads);
        Assert.Same(first, second);
        Assert.Equal(2, reader.ReadCount);

        Egress("newer");
        var third = await reader.ReadRecentAsync();
        Assert.Equal("audit:newer", third[0].Id);
        Assert.Equal(1, reader.UnchangedReads);
    }

    // ------------------------------------------------------------------ one probe, every reader

    [Fact]
    public async Task One_shared_probe_serves_every_reader_over_one_connection_and_each_notices_a_commit()
    {
        Add("e1");
        Finding("f1");
        Change("c1");
        Stream("s1");
        var audit = Reader();
        var queue = new AlertQueueReader(_database.Path, probe: _probe);
        var changes = new MutationReader(_database.Path, _probe);
        var streams = new EventStreamReader(_database.Path, probe: _probe);

        async Task ReadAll()
        {
            _ = await audit.QueryAsync(new AuditQuery());
            _ = await queue.ReadAsync();
            _ = await changes.ReadAsync();
            _ = await streams.ReadAsync(EventStreamKind.Events);
        }

        await ReadAll();
        await ReadAll();
        Assert.Equal(4, audit.UnchangedReads + queue.UnchangedReads + changes.UnchangedReads + streams.UnchangedReads);
        Assert.Equal(1, _probe.OpenCount);

        Add("e2");
        await ReadAll();

        // Each reader kept its own last stamp over the one connection, so every one of them saw the commit.
        Assert.Equal(4, audit.UnchangedReads + queue.UnchangedReads + changes.UnchangedReads + streams.UnchangedReads);
        Assert.Equal(1, _probe.OpenCount);
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }
}
