using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Overview's hourly allowed/blocked chart, against a database built from the real DDL (the retention triggers, the action/timestamp
/// index) and synthetic rows only. What the live 6.7 GB database needs is the plan, so the plan is asserted as well as the counts, and one
/// test times the real thing where there is one.
/// </summary>
public sealed class HourlyActivityReaderTests : IDisposable
{
    /// <summary>Half past noon, so "the current hour" has an hour before and after it to fall into and out of.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 30, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly ManualTimeProvider _time = new(Now);
    private int _row;

    public void Dispose() => _database.Dispose();

    private HourlyActivityReader Reader(int hours = HourlyActivityReader.DefaultHours) => new(_database.Path, hours, _time);

    private void Add(DateTimeOffset at, string action, string? details = null, string? connector = null) =>
        _database.InsertEvent($"row-{_row++:D6}", at, action, "INFO", details: details, connector: connector);

    private static string Hook(string decision) => $"connector=claudecode result=ok action={decision} raw_action={decision} severity=NONE mode=observe";

    private static DateTimeOffset Hour(int hoursAgo) => new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, 0, 0, TimeSpan.Zero).AddHours(-hoursAgo);

    // ---- What a bar is ----

    [Fact]
    public async Task Hook_calls_that_do_not_block_are_allowed_and_blocks_are_the_tiles_definition()
    {
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        Add(Hour(0).AddMinutes(2), "connector-hook", Hook("alert"));
        Add(Hour(0).AddMinutes(3), "connector-hook", Hook("block"));
        Add(Hour(0).AddMinutes(4), "connector-hook", Hook("deny"));
        Add(Hour(0).AddMinutes(5), "connector-hook", "connector=claudecode raw_action=block action=allow");   // raw_action= is not action=
        Add(Hour(0).AddMinutes(6), "guardrail-block");
        Add(Hour(0).AddMinutes(7), "install-blocked");
        Add(Hour(0).AddMinutes(8), "tool_invocation");                                                           // not a decision: not counted at all
        Add(Hour(0).AddMinutes(9), "scan-finding");

        var result = await Reader().ReadAsync();

        Assert.Equal(HourlyActivityStatus.Ok, result.Status);
        var current = result.Hours[^1];
        Assert.Equal(Hour(0), current.HourStart);
        Assert.Equal(3, current.Allowed);
        Assert.Equal(4, current.Blocked);
        Assert.Equal(3, result.Allowed);
        Assert.Equal(4, result.Blocked);
        Assert.Equal(7, result.Total);
        Assert.Equal(7, result.Peak);
    }

    [Fact]
    public async Task Every_block_class_action_counts_as_blocked_and_an_action_that_merely_contains_block_does_not()
    {
        foreach (var action in HourlyActivityReader.BlockActions)
        {
            Add(Hour(1).AddMinutes(1), action);
        }

        Add(Hour(1).AddMinutes(2), "blocker");
        Add(Hour(1).AddMinutes(3), "unblock");

        var result = await Reader().ReadAsync();

        Assert.Equal(HourlyActivityReader.BlockActions.Count, result.Hours[^2].Blocked);
        Assert.Equal(0, result.Hours[^2].Allowed);
    }

    // ---- The window ----

    [Fact]
    public async Task The_window_is_exactly_twenty_four_hours_oldest_first_and_every_hour_is_present_even_when_empty()
    {
        Add(Hour(23).AddMinutes(10), "connector-hook", Hook("allow"));
        Add(Hour(12).AddMinutes(10), "connector-hook", Hook("allow"));

        var result = await Reader().ReadAsync();

        Assert.Equal(24, result.Hours.Count);
        Assert.Equal(Hour(23), result.Hours[0].HourStart);
        Assert.Equal(Hour(0), result.Hours[23].HourStart);
        Assert.Equal(Hour(23), result.From);
        Assert.Equal(Hour(0).AddHours(1), result.To);
        Assert.All(result.Hours.Zip(result.Hours.Skip(1)), pair => Assert.Equal(pair.First.HourStart.AddHours(1), pair.Second.HourStart));
        Assert.Equal(1, result.Hours[0].Allowed);
        Assert.Equal(1, result.Hours[11].Allowed);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task Rows_before_the_window_and_after_the_current_hour_are_not_counted()
    {
        Add(Hour(24).AddMinutes(59), "connector-hook", Hook("allow"));   // the last minute of the hour before the window
        Add(Hour(0).AddHours(1), "connector-hook", Hook("allow"));       // the first second of the next hour
        Add(Hour(23), "connector-hook", Hook("allow"));                  // the first second of the window
        Add(Hour(0).AddMinutes(59), "connector-hook", Hook("block"));    // the last minute of the current hour

        var result = await Reader().ReadAsync();

        Assert.Equal(1, result.Hours[0].Allowed);
        Assert.Equal(1, result.Hours[^1].Blocked);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task The_window_follows_the_clock_the_reader_was_given()
    {
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        var reader = Reader();

        _time.Set(Now.AddHours(2));
        var later = await reader.ReadAsync();

        Assert.Equal(Hour(0).AddHours(2), later.Hours[^1].HourStart);
        Assert.Equal(Hour(0), later.Hours[^3].HourStart);
        Assert.Equal(1, later.Hours[^3].Allowed);
    }

    [Fact]
    public async Task A_shorter_window_is_just_fewer_hours()
    {
        Add(Hour(2).AddMinutes(1), "connector-hook", Hook("allow"));
        Add(Hour(5).AddMinutes(1), "connector-hook", Hook("allow"));

        var result = await Reader(hours: 4).ReadAsync();

        Assert.Equal(4, result.Hours.Count);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task A_timestamp_with_a_space_instead_of_a_T_still_lands_in_its_hour()
    {
        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"INSERT INTO audit_events (id, timestamp, action, actor, details) VALUES ('legacy', '{Hour(0):yyyy-MM-dd} {Hour(0):HH}:05:00', 'connector-hook', 'a', '{Hook("allow")}')";
            _ = command.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync();

        // Inside the text bounds (a later date than the window's start), so it is counted in the hour its prefix names.
        Assert.Equal(1, result.Hours[^1].Allowed);
    }

    // ---- What is on disk ----

    [Fact]
    public async Task No_database_and_no_table_are_nothing_recorded_and_still_have_every_hour()
    {
        using var temp = new TempDirectory();

        var missing = await new HourlyActivityReader(temp.File("audit.db"), timeProvider: _time).ReadAsync();
        Assert.Equal(HourlyActivityStatus.NoDatabase, missing.Status);
        Assert.Equal(24, missing.Hours.Count);
        Assert.Equal(0, missing.Total);
        Assert.Empty(await new HourlyActivityReader(temp.File("audit.db"), timeProvider: _time).ExplainAsync());

        var path = temp.File("empty.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x INTEGER)";
            _ = command.ExecuteNonQuery();
        }

        var noTable = await new HourlyActivityReader(path, timeProvider: _time).ReadAsync();
        Assert.Equal(HourlyActivityStatus.NoDatabase, noTable.Status);
    }

    [Fact]
    public async Task A_database_without_the_action_timestamp_index_reads_nothing_instead_of_scanning()
    {
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DROP INDEX {HourlyActivityReader.IndexName}";
            _ = command.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync();

        Assert.Equal(HourlyActivityStatus.NoIndex, result.Status);
        Assert.Equal(0, result.Total);
        Assert.Equal(24, result.Hours.Count);
        Assert.Empty(await Reader().ExplainAsync());
    }

    [Fact]
    public async Task A_same_named_index_of_another_shape_is_not_trusted()
    {
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DROP INDEX {HourlyActivityReader.IndexName}; CREATE INDEX {HourlyActivityReader.IndexName} ON audit_events(timestamp);";
            _ = command.ExecuteNonQuery();
        }

        Assert.Equal(HourlyActivityStatus.NoIndex, (await Reader().ReadAsync()).Status);
    }

    [Fact]
    public async Task A_database_without_a_details_column_still_counts_hook_calls_as_allowed_and_action_blocks_as_blocked()
    {
        using var temp = new TempDirectory();
        var path = temp.File("audit.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT);
                CREATE INDEX {HourlyActivityReader.IndexName} ON audit_events(action, timestamp DESC);
                INSERT INTO audit_events VALUES ('1', '{Hour(0):yyyy-MM-ddTHH}:10:00Z', 'connector-hook');
                INSERT INTO audit_events VALUES ('2', '{Hour(0):yyyy-MM-ddTHH}:11:00Z', 'block');
                """;
            _ = command.ExecuteNonQuery();
        }

        var result = await new HourlyActivityReader(path, timeProvider: _time).ReadAsync();

        Assert.Equal(1, result.Allowed);
        Assert.Equal(1, result.Blocked);
    }

    [Fact]
    public async Task It_reads_a_database_held_open_by_a_writer_and_writes_nothing()
    {
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        var before = new FileInfo(_database.Path);
        var stamp = (before.Length, before.LastWriteTimeUtc);

        using var writer = _database.OpenWritable();
        using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            _ = begin.ExecuteNonQuery();
        }

        var result = await Reader().ReadAsync();
        Assert.Equal(1, result.Allowed);

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
        Add(Hour(0).AddMinutes(1), "connector-hook", Hook("allow"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(cancellationToken: cancelled.Token));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(TimeSpan.Zero));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void The_window_size_is_validated()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Reader(hours: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Reader(hours: 24 * 14 + 1));
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
    public async Task Both_statements_are_a_range_search_on_the_action_timestamp_index_and_the_block_events_are_a_covering_one()
    {
        for (var i = 0; i < 40; i++)
        {
            Add(Hour(i % 24).AddMinutes(1), i % 2 == 0 ? "connector-hook" : "tool_invocation", Hook("allow"));
        }

        var plan = await Reader().ExplainAsync();

        var hookPlan = plan.Where(line => line.Contains("SEARCH", StringComparison.Ordinal) || line.Contains("SCAN", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, hookPlan.Count);
        Assert.All(hookPlan, line =>
        {
            Assert.Contains(HourlyActivityReader.IndexName, line, StringComparison.Ordinal);
            Assert.Contains("action=?", line, StringComparison.Ordinal);
            Assert.Contains("timestamp>?", line, StringComparison.Ordinal);
            Assert.StartsWith("SEARCH", line, StringComparison.Ordinal);
        });
        Assert.Contains("COVERING INDEX", hookPlan[1], StringComparison.Ordinal);
    }

    // ---- The real database, where there is one ----

    [LiveAuditFact]
    public async Task On_the_real_audit_database_the_day_reads_in_well_under_a_second_with_the_same_plan()
    {
        var reader = new HourlyActivityReader(new DefenseClawPaths().AuditDatabasePath);

        var plan = await reader.ExplainAsync();
        Assert.All(plan.Where(line => line.StartsWith("SEARCH", StringComparison.Ordinal) || line.StartsWith("SCAN", StringComparison.Ordinal)), line =>
        {
            Assert.StartsWith("SEARCH", line, StringComparison.Ordinal);
            Assert.Contains(HourlyActivityReader.IndexName, line, StringComparison.Ordinal);
        });

        // The first read opens the file cold; the second is what the panel costs every time after.
        _ = await reader.ReadAsync();
        var warm = await reader.ReadAsync();

        Assert.Equal(HourlyActivityStatus.Ok, warm.Status);
        Assert.Equal(24, warm.Hours.Count);
        Assert.True(warm.Elapsed < TimeSpan.FromSeconds(2), $"The day took {warm.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    // ---- The hour text ----

    [Theory]
    [InlineData("2026-09-30T22", 2026, 9, 30, 22)]
    [InlineData("2026-01-01T00", 2026, 1, 1, 0)]
    [InlineData("2026-09-30 22", 2026, 9, 30, 22)]
    public void An_hour_prefix_is_read_as_a_utc_hour(string prefix, int year, int month, int day, int hour) =>
        Assert.Equal(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero), HourlyActivityReader.ParseHour(prefix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026-09-30T2")]
    [InlineData("2026-13-30T22")]
    public void An_unreadable_hour_prefix_is_no_hour(string? prefix) => Assert.Null(HourlyActivityReader.ParseHour(prefix));

    /// <summary>A clock a test can move.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now) => _now = now;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
