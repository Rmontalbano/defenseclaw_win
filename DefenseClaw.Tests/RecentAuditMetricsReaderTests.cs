using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The two numbers behind the tray flyout's "Hook Calls" and "Blocks" rows, against a database built from the real DDL (the retention
/// triggers, the timestamp indexes) and synthetic rows only. What the live 6.9 GB database needs is the plan, so the plan is asserted as well
/// as the counts, and one test times the real thing where there is one.
/// </summary>
public sealed class RecentAuditMetricsReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private int _minute;

    public void Dispose() => _database.Dispose();

    private RecentAuditMetricsReader Reader(int window = RecentAuditMetricsReader.DefaultWindow) => new(_database.Path, window);

    /// <summary>Adds a row one minute after the previous one, so insertion order is time order.</summary>
    private void Add(string action, string? details = null) =>
        _database.InsertEvent($"row-{_minute:D5}", Base.AddMinutes(_minute++), action, "INFO", details: details);

    private static string Hook(string decision) => $"connector=claudecode result=ok action={decision} raw_action={decision} severity=NONE mode=observe";

    // ---- The definitions (AuditStore.overviewHookCallCount / overviewBlockCount) ----

    [Fact]
    public async Task Hook_calls_are_the_connector_hook_rows_and_nothing_else()
    {
        Add("connector-hook", Hook("allow"));
        Add("connector-hook", Hook("allow"));
        Add("connector-hook", Hook("alert"));
        Add("tool_invocation");
        Add("scan-finding");
        Add("Connector-Hook", Hook("allow"));   // the Mac compares exactly
        Add("connector-hook-result");

        var result = await Reader().ReadAsync();

        Assert.Equal(RecentAuditMetricsStatus.Ok, result.Status);
        Assert.Equal(3, result.HookCalls);
        Assert.Equal(7, result.Window);
    }

    [Fact]
    public async Task Blocks_are_counted_across_every_row_and_hook_calls_only_across_hook_rows()
    {
        Add("connector-hook", Hook("allow"));
        Add("connector-hook", Hook("block"));
        Add("connector-hook", Hook("deny"));
        Add("guardrail-block");
        Add("quarantine");
        Add("tool_invocation");

        var result = await Reader().ReadAsync();

        Assert.Equal(3, result.HookCalls);
        Assert.Equal(4, result.Blocks);
    }

    // ---- The window ----

    [Fact]
    public async Task Only_the_newest_rows_count_and_ties_break_by_rowid_like_the_mac_orders_them()
    {
        // Oldest first: three blocks, then two hook calls. A window of two sees only the hook calls.
        Add("block");
        Add("block");
        Add("block");
        Add("connector-hook", Hook("allow"));
        Add("connector-hook", Hook("allow"));

        var result = await Reader(window: 2).ReadAsync();

        Assert.Equal(2, result.Window);
        Assert.Equal(2, result.HookCalls);
        Assert.Equal(0, result.Blocks);

        // Same timestamp: the later rowid is the newer row.
        _database.InsertEvent("tie-a", Base.AddDays(1), "block", "INFO");
        _database.InsertEvent("tie-b", Base.AddDays(1), "connector-hook", "INFO", details: Hook("allow"));

        var tied = await Reader(window: 1).ReadAsync();

        Assert.Equal(1, tied.HookCalls);
        Assert.Equal(0, tied.Blocks);
    }

    [Fact]
    public async Task The_default_window_is_five_hundred_rows()
    {
        Assert.Equal(500, RecentAuditMetricsReader.DefaultWindow);

        // 520 rows, the oldest 20 of which are blocks: they are outside the window.
        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 520)
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity)
                SELECT 'bulk-' || i,
                       strftime('%Y-%m-%dT%H:%M:%S', '2026-09-30 00:00:00', '+' || i || ' seconds') || '.0000000Z',
                       CASE WHEN i <= 20 THEN 'block' WHEN i % 2 = 0 THEN 'connector-hook' ELSE 'scan' END,
                       '',
                       'audit_logger',
                       NULL,
                       'INFO'
                FROM n
                """;
            _ = command.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync();

        Assert.Equal(500, result.Window);
        Assert.Equal(0, result.Blocks);
        Assert.Equal(250, result.HookCalls);
    }

    [Fact]
    public async Task A_huge_details_value_costs_nothing_and_the_decision_near_its_start_still_counts()
    {
        Add("connector-hook", "connector=claudecode action=block " + new string('x', 200_000));

        var result = await Reader().ReadAsync();

        Assert.Equal(1, result.HookCalls);
        Assert.Equal(1, result.Blocks);
    }

    // ---- What is on disk ----

    [Fact]
    public async Task No_database_and_no_table_are_nothing_recorded()
    {
        using var temp = new TempDirectory();

        var missing = await new RecentAuditMetricsReader(temp.File("audit.db")).ReadAsync();
        Assert.Equal(RecentAuditMetricsStatus.NoDatabase, missing.Status);
        Assert.Equal(0, missing.HookCalls);
        Assert.Empty(await new RecentAuditMetricsReader(temp.File("audit.db")).ExplainAsync());

        var path = temp.File("empty.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x INTEGER)";
            _ = command.ExecuteNonQuery();
        }

        var noTable = await new RecentAuditMetricsReader(path).ReadAsync();
        Assert.Equal(RecentAuditMetricsStatus.NoDatabase, noTable.Status);
    }

    [Fact]
    public async Task A_database_without_a_details_column_still_counts_hook_calls_and_action_blocks()
    {
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT);
                INSERT INTO audit_events VALUES ('1', '2026-07-22T12:00:00Z', 'connector-hook');
                INSERT INTO audit_events VALUES ('2', '2026-07-22T12:01:00Z', 'block');
                """;
            _ = command.ExecuteNonQuery();
        }

        var result = await new RecentAuditMetricsReader(path).ReadAsync();

        Assert.Equal(1, result.HookCalls);
        Assert.Equal(1, result.Blocks);
    }

    [Fact]
    public async Task It_reads_a_database_held_open_by_a_writer_and_writes_nothing()
    {
        Add("connector-hook", Hook("allow"));
        var before = new FileInfo(_database.Path);
        var stamp = (before.Length, before.LastWriteTimeUtc);

        // A writer with an open transaction, as the gateway is: the read-only reader sees the committed rows and is not blocked.
        using var writer = _database.OpenWritable();
        using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            _ = begin.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync();
        Assert.Equal(1, result.HookCalls);

        using (var rollback = writer.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            _ = rollback.ExecuteNonQuery();
        }

        var after = new FileInfo(_database.Path);
        Assert.Equal(stamp, (after.Length, after.LastWriteTimeUtc));
    }

    // ---- Stoppable ----

    [Fact]
    public async Task A_cancelled_token_stops_the_read_and_a_non_positive_timeout_is_refused()
    {
        Add("connector-hook", Hook("allow"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(cancellationToken: cancelled.Token));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(TimeSpan.Zero));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Reads_are_counted_when_they_are_made()
    {
        var reader = Reader();
        Assert.Equal(0, reader.ReadCount);

        _ = await reader.ReadAsync();
        _ = await reader.ReadAsync();

        Assert.Equal(2, reader.ReadCount);
    }

    // ---- The plan ----

    [Fact]
    public async Task The_window_is_one_backward_walk_of_the_timestamp_index_with_no_sort()
    {
        for (var i = 0; i < 30; i++)
        {
            Add(i % 2 == 0 ? "connector-hook" : "scan", Hook("allow"));
        }

        var plan = await Reader().ExplainAsync();

        // SQLite walks idx_audit_timestamp (timestamp, rowid) newest first and stops at the limit: nothing is sorted, however large the table.
        Assert.Contains(plan, line => line.Contains("idx_audit_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    // ---- The real database, where there is one ----

    [LiveAuditFact]
    public async Task On_the_real_audit_database_the_window_reads_in_milliseconds_with_the_same_plan()
    {
        var path = new DefenseClawPaths().AuditDatabasePath;
        var reader = new RecentAuditMetricsReader(path);

        var plan = await reader.ExplainAsync();
        Assert.Contains(plan, line => line.Contains("idx_audit_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));

        // The first read opens the file cold; the second is what the flyout costs every time after.
        _ = await reader.ReadAsync();
        var warm = await reader.ReadAsync();

        Assert.Equal(RecentAuditMetricsStatus.Ok, warm.Status);
        Assert.True(warm.Window <= RecentAuditMetricsReader.DefaultWindow);
        Assert.True(warm.Elapsed < TimeSpan.FromSeconds(1), $"The window took {warm.Elapsed.TotalMilliseconds:0.0} ms.");
    }
}

/// <summary>The Mac's two tests on a row - "is this a block", "what does its <c>action=</c> say" - which need no database.</summary>
public sealed class RecentAuditMetricsRulesTests
{
    [Theory]
    [InlineData("block", null, true)]
    [InlineData("Block", null, true)]
    [InlineData("guardrail-block", null, true)]
    [InlineData("deny", null, true)]
    [InlineData("QUARANTINE", null, true)]
    [InlineData("connector-hook", "connector=claudecode action=block raw_action=block", true)]
    [InlineData("connector-hook", "connector=claudecode action=deny", true)]
    [InlineData("connector-hook", "connector=claudecode action=DENY", true)]
    [InlineData("connector-hook", "connector=claudecode action=\"block\"", true)]
    [InlineData("connector-hook", "connector=claudecode,action=block;mode=observe", true)]
    [InlineData("tool_invocation", "action=block", true)]                                   // any row whose details decide "block"
    [InlineData("connector-hook", "connector=claudecode action=allow", false)]
    [InlineData("connector-hook", "connector=claudecode action=alert", false)]
    [InlineData("connector-hook", "connector=claudecode action=warn", false)]
    [InlineData("connector-hook", "connector=claudecode raw_action=block action=allow", false)]    // raw_action= is not action=
    [InlineData("connector-hook", "connector=claudecode action=allow action=block", false)]         // the first action= token decides
    [InlineData("connector-hook", "blocked by policy", false)]
    [InlineData("scan", null, false)]
    [InlineData("blocker", null, false)]
    [InlineData("quarantine-lifted", null, false)]
    public void A_block_is_judged_by_its_action_and_by_the_first_action_token_of_its_details(string action, string? details, bool expected) =>
        Assert.Equal(expected, RecentAuditMetricsReader.IsBlock(action, details));

    [Theory]
    [InlineData("a=1 action=block b=2", "action", "block")]
    [InlineData("action='deny'", "action", "deny")]
    [InlineData("action=`x`", "action", "x")]
    [InlineData("x=1;action=allow", "action", "allow")]
    [InlineData("x=1,action=allow", "action", "allow")]
    [InlineData("raw_action=block", "action", "")]
    [InlineData("action", "action", "")]
    [InlineData("", "action", "")]
    [InlineData(null, "action", "")]
    [InlineData("connector=claudecode", "connector", "claudecode")]
    public void The_detail_value_is_the_first_token_with_that_key(string? details, string key, string expected) =>
        Assert.Equal(expected, RecentAuditMetricsReader.DetailValue(key, details));
}

/// <summary>A fact that reads the developer's own <c>audit.db</c> (read-only) and is skipped where there is none, such as a CI runner.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveAuditFactAttribute : FactAttribute
{
    public LiveAuditFactAttribute()
    {
        if (!File.Exists(new DefenseClawPaths().AuditDatabasePath))
        {
            Skip = "There is no audit.db on this machine.";
        }
    }
}
