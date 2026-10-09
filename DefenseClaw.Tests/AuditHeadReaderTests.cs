using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Audit sidebar badge's reader (CUST-265): the newest rowid and a bounded count of the rows above a marker. Against a database built
/// from the real DDL with invented rows. What matters is that the marker is the order of insertion (a row committed late counts), that
/// the count is bounded, that the plan never scans the table, and that an unchanged database costs no statement at all.
/// </summary>
public sealed class AuditHeadReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly List<AuditChangeProbe> _probes = new();
    private int _rows;

    public void Dispose()
    {
        foreach (var probe in _probes)
        {
            probe.Dispose();
        }

        _database.Dispose();
    }

    /// <summary>A reader that, like the app's, shares a probe with the other readers of the file.</summary>
    private AuditHeadReader Reader(bool withProbe = true)
    {
        AuditChangeProbe? probe = null;
        if (withProbe)
        {
            probe = new AuditChangeProbe(_database.Path);
            _probes.Add(probe);
        }

        return new AuditHeadReader(_database.Path, probe, TestTimeouts.Ceiling);
    }

    private void Add(int count = 1)
    {
        using var connection = _database.OpenWritable();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO audit_events (id, timestamp, action, actor, severity) VALUES ($id, $ts, 'hook_decision', 'audit_logger', 'INFO')";
            command.Parameters.AddWithValue("$id", "row-" + _rows);
            command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(Base.AddSeconds(_rows)));
            _ = command.ExecuteNonQuery();
            _rows++;
        }

        transaction.Commit();
    }

    private void Exec(string sql)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ what it reads

    [Fact]
    public async Task An_empty_table_has_head_zero_and_nothing_newer()
    {
        var head = await Reader().ReadAsync(0, 100);

        Assert.Equal(AuditHeadStatus.Ok, head.Status);
        Assert.Equal(0, head.HeadRowId);
        Assert.Equal(0, head.Newer);
    }

    [Fact]
    public async Task The_head_is_the_newest_rowid_and_the_count_is_the_rows_above_the_marker()
    {
        Add(10);
        var reader = Reader();

        var all = await reader.ReadAsync(0, 100);
        Assert.Equal(10, all.HeadRowId);
        Assert.Equal(10, all.Newer);

        var some = await reader.ReadAsync(7, 100);
        Assert.Equal(10, some.HeadRowId);
        Assert.Equal(3, some.Newer);

        var none = await reader.ReadAsync(10, 100);
        Assert.Equal(10, none.HeadRowId);
        Assert.Equal(0, none.Newer);

        // A marker past the head (a table that was emptied and started again) counts nothing, and the head says so.
        var beyond = await reader.ReadAsync(50, 100);
        Assert.Equal(10, beyond.HeadRowId);
        Assert.Equal(0, beyond.Newer);
    }

    [Fact]
    public async Task The_count_stops_at_the_limit_asked_for_so_its_cost_does_not_depend_on_how_far_behind_the_marker_is()
    {
        Add(150);
        var reader = Reader();

        Assert.Equal(100, (await reader.ReadAsync(0, 100)).Newer);
        Assert.Equal(100, (await reader.ReadAsync(40, 100)).Newer);
        Assert.Equal(60, (await reader.ReadAsync(90, 100)).Newer);
        Assert.Equal(150, (await reader.ReadAsync(0, 100)).HeadRowId);

        // A head-only read: nothing is above long.MaxValue and a limit of 0 counts nothing.
        var headOnly = await reader.ReadAsync(long.MaxValue, 0);
        Assert.Equal(150, headOnly.HeadRowId);
        Assert.Equal(0, headOnly.Newer);
        Assert.Equal(0, (await reader.ReadAsync(0, 0)).Newer);
    }

    [Fact]
    public async Task A_negative_limit_is_refused_because_SQLite_would_read_it_as_no_limit()
    {
        Add(3);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(0, -1));
    }

    [Fact]
    public async Task A_row_committed_late_with_an_old_timestamp_still_counts_because_the_marker_is_the_order_of_insertion()
    {
        Add(5);
        var reader = Reader();
        var marker = (await reader.ReadAsync(long.MaxValue, 0)).HeadRowId;

        // The gateway stamps a scan finding when the scan began and commits it later: its timestamp is older than every row already there.
        _database.InsertEvent("late-finding", Base.AddDays(-1), "scan-finding", "HIGH", "security.finding", "claudecode", eventName: "finding.observed");

        var after = await reader.ReadAsync(marker, 100);

        Assert.Equal(marker + 1, after.HeadRowId);
        Assert.Equal(1, after.Newer);
    }

    [Fact]
    public async Task Retention_deleting_the_oldest_rows_changes_nothing_above_the_marker()
    {
        Add(20);
        var reader = Reader();
        var marker = (await reader.ReadAsync(long.MaxValue, 0)).HeadRowId;
        Add(4);

        Exec("DELETE FROM audit_events WHERE rowid <= 12");
        var after = await reader.ReadAsync(marker, 100);

        Assert.Equal(24, after.HeadRowId);
        Assert.Equal(4, after.Newer);
    }

    [Fact]
    public async Task A_table_that_was_emptied_and_started_again_has_a_head_below_the_old_marker()
    {
        Add(30);
        var reader = Reader();
        var marker = (await reader.ReadAsync(long.MaxValue, 0)).HeadRowId;
        Assert.Equal(30, marker);

        // Rowids restart: a rowid table with no AUTOINCREMENT hands out max + 1, which is 1 again once the table is empty. (VACUUM
        // renumbering shows the same way: a head below the marker.) Whoever holds the marker must start again from the head.
        Exec("DELETE FROM audit_events");
        Add(3);

        var after = await reader.ReadAsync(marker, 100);
        Assert.Equal(3, after.HeadRowId);
        Assert.True(after.HeadRowId < marker);
        Assert.Equal(0, after.Newer);
    }

    // ------------------------------------------------------------------ no database

    [Fact]
    public async Task No_file_is_no_database_and_nothing_is_created()
    {
        var path = Path.Combine(Path.GetTempPath(), "dcw-head-missing-" + Guid.NewGuid().ToString("n"), "audit.db");
        var reader = new AuditHeadReader(path, null, TestTimeouts.Ceiling);

        var head = await reader.ReadAsync(0, 100);

        Assert.Equal(AuditHeadStatus.NoDatabase, head.Status);
        Assert.Equal(0, head.HeadRowId);
        Assert.Equal(0, head.Newer);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(0, reader.StatementCount);
    }

    [Fact]
    public async Task A_database_with_no_audit_events_table_is_no_database_not_an_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dcw-head-notable-" + Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "audit.db");
        try
        {
            using (var connection = new SqliteConnection(SqlitePools.WritableConnectionString(path)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE something_else (id TEXT PRIMARY KEY)";
                _ = command.ExecuteNonQuery();
            }

            var head = await new AuditHeadReader(path, null, TestTimeouts.Ceiling).ReadAsync(0, 100);

            Assert.Equal(AuditHeadStatus.NoDatabase, head.Status);
            Assert.Equal(0, head.HeadRowId);
        }
        finally
        {
            SqlitePools.Release(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------ cost

    [Fact]
    public async Task A_read_of_an_unchanged_database_runs_no_statement_and_a_commit_makes_the_next_one_run()
    {
        Add(5);
        var reader = Reader();

        var first = await reader.ReadAsync(2, 100);
        Assert.Equal(1, reader.StatementCount);
        Assert.Equal(0, reader.UnchangedReads);

        for (var i = 0; i < 25; i++)
        {
            Assert.Same(first, await reader.ReadAsync(2, 100));
        }

        Assert.Equal(1, reader.StatementCount);
        Assert.Equal(25, reader.UnchangedReads);
        Assert.Equal(26, reader.ReadCount);

        Add(1);
        var changed = await reader.ReadAsync(2, 100);
        Assert.Equal(2, reader.StatementCount);
        Assert.Equal(4, changed.Newer);

        Assert.Same(changed, await reader.ReadAsync(2, 100));
        Assert.Equal(2, reader.StatementCount);
    }

    [Fact]
    public async Task Another_marker_is_another_question_and_is_remembered_beside_the_first()
    {
        Add(8);
        var reader = Reader();

        _ = await reader.ReadAsync(0, 100);
        _ = await reader.ReadAsync(5, 100);
        Assert.Equal(2, reader.StatementCount);

        _ = await reader.ReadAsync(0, 100);
        _ = await reader.ReadAsync(5, 100);
        Assert.Equal(2, reader.StatementCount);
        Assert.Equal(2, reader.UnchangedReads);
    }

    [Fact]
    public async Task Without_a_probe_nothing_is_remembered_and_every_read_runs()
    {
        Add(3);
        var reader = Reader(withProbe: false);

        _ = await reader.ReadAsync(0, 100);
        _ = await reader.ReadAsync(0, 100);
        _ = await reader.ReadAsync(0, 100);

        Assert.Equal(3, reader.StatementCount);
        Assert.Equal(0, reader.UnchangedReads);
    }

    [Fact]
    public async Task The_plan_is_a_search_on_the_rowid_and_never_a_scan_of_the_table()
    {
        Add(5);

        var plan = await Reader().ExplainAsync();

        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN audit_events", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan, line => line.Contains("SEARCH audit_events", StringComparison.OrdinalIgnoreCase) && line.Contains("rowid>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task It_only_reads_the_file_is_left_exactly_as_it_was()
    {
        Add(10);
        SqlitePools.Release(_database.Path);
        var before = new FileInfo(_database.Path);
        var length = before.Length;
        var written = before.LastWriteTimeUtc;
        var reader = Reader();

        for (var i = 0; i < 5; i++)
        {
            _ = await reader.ReadAsync(i, 100);
        }

        var after = new FileInfo(_database.Path);
        Assert.Equal(length, after.Length);
        Assert.Equal(written, after.LastWriteTimeUtc);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_read()
    {
        Add(3);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(0, 100, cancellationToken: cancelled.Token));
    }
}
