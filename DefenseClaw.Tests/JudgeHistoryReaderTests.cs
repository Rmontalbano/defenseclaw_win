using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Paths;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The judge history reader against synthetic databases built from the gateway's columns: the forensic store and the legacy audit table
/// merged newest first with the forensic copy winning, both timestamp spellings, the load-more limit, a bounded body, and the three
/// ways a source can be absent or broken (missing, no table, unreadable) without a throw or a write.
/// </summary>
public sealed class JudgeHistoryReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-judge-" + Guid.NewGuid().ToString("N"));

    public JudgeHistoryReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File_(string name) => Path.Combine(_dir, name);

    private static void Exec(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        _ = command.ExecuteNonQuery();
    }

    private const string BodiesDdl =
        "CREATE TABLE judge_responses (id TEXT, timestamp DATETIME, kind TEXT, direction TEXT, model TEXT, action TEXT, severity TEXT, latency_ms INTEGER, " +
        "parse_error TEXT, raw_response TEXT, request_id TEXT, trace_id TEXT, run_id TEXT, input_hash TEXT, confidence REAL, fail_closed_applied INTEGER, " +
        "inspected_model TEXT, prompt_template_id TEXT, timestamp_unix_nano INTEGER)";

    private const string LegacyOldDdl =
        "CREATE TABLE judge_responses (id TEXT, timestamp DATETIME, kind TEXT, direction TEXT, model TEXT, action TEXT, severity TEXT, latency_ms INTEGER, raw TEXT)";

    private string Bodies()
    {
        var path = File_("judge_bodies.db");
        Exec(path, BodiesDdl);
        return path;
    }

    private string Legacy(string ddl = BodiesDdl)
    {
        var path = File_("audit.db");
        Exec(path, ddl);
        return path;
    }

    private static void Insert(string path, string id, string timestamp, string raw = "{}", long? nano = null, string action = "allow", double? confidence = null, int failClosed = 0)
    {
        var hasNano = nano is not null;
        Exec(
            path,
            "INSERT INTO judge_responses (id, timestamp, kind, direction, model, action, severity, latency_ms, raw_response, confidence, fail_closed_applied" +
            (hasNano ? ", timestamp_unix_nano" : string.Empty) + ") VALUES ($id, $ts, 'injection', 'prompt', 'judge-1', $action, 'LOW', 42, $raw, $conf, $fc" +
            (hasNano ? ", $nano" : string.Empty) + ")",
            ("$id", id), ("$ts", timestamp), ("$raw", raw), ("$action", action), ("$conf", confidence), ("$fc", failClosed), ("$nano", nano));
    }

    private static string[] Ids(JudgeHistoryResult result) => result.Rows.Select(r => r.Id).ToArray();

    [Fact]
    public async Task Both_databases_merge_newest_first_and_an_id_in_both_is_read_once_from_the_forensic_store()
    {
        var bodies = Bodies();
        var legacy = Legacy();
        Insert(bodies, "b-new", "2026-10-01T10:00:00Z", raw: "bodies-copy");
        Insert(bodies, "dup", "2026-09-30T10:00:00Z", raw: "bodies-copy");
        Insert(legacy, "dup", "2026-09-30T10:00:00Z", raw: "legacy-copy");
        Insert(legacy, "l-old", "2026-09-01T10:00:00Z");
        Insert(legacy, "l-mid", "2026-09-30T11:00:00Z");

        var result = await new JudgeHistoryReader(bodies, legacy).ReadAsync();

        Assert.Equal(JudgeHistoryStatus.Ok, result.Status);
        Assert.Equal(new[] { "b-new", "l-mid", "dup", "l-old" }, Ids(result));
        var dup = result.Rows.Single(r => r.Id == "dup");
        Assert.Equal("bodies-copy", dup.Raw);
        Assert.Equal(JudgeHistorySource.JudgeBodies, dup.Source);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Go_and_iso_timestamps_sort_together_by_the_instant_not_the_text()
    {
        var bodies = Bodies();
        // Same instants in different spellings and zones; the text order would be wrong for all of them.
        Insert(bodies, "iso-z", "2026-10-01T12:00:00.5Z");
        Insert(bodies, "go-utc", "2026-10-01 12:00:00.7 +0000 UTC");
        Insert(bodies, "go-west", "2026-10-01 05:00:01 -0700 MST");
        Insert(bodies, "no-zone", "2026-10-01T11:00:00");
        Insert(bodies, "junk", "not a time");

        var result = await new JudgeHistoryReader(bodies, null).ReadAsync();

        Assert.Equal(new[] { "go-west", "go-utc", "iso-z", "no-zone", "junk" }, Ids(result));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 1, TimeSpan.Zero), result.Rows[0].Timestamp);
        Assert.Equal(DateTimeOffset.MinValue, result.Rows[^1].Timestamp);
    }

    [Fact]
    public async Task The_unix_nano_column_orders_when_it_is_there()
    {
        var bodies = Bodies();
        Insert(bodies, "a", "2026-10-01T10:00:00Z", nano: 3_000_000_000_000_000_000);
        Insert(bodies, "b", "2026-10-02T10:00:00Z", nano: 2_000_000_000_000_000_000);

        var result = await new JudgeHistoryReader(bodies, null).ReadAsync();

        Assert.Equal(new[] { "a", "b" }, Ids(result));
    }

    [Fact]
    public async Task The_limit_bounds_the_list_and_says_when_there_is_more()
    {
        var bodies = Bodies();
        for (var i = 0; i < 25; i++)
        {
            Insert(bodies, $"r{i:00}", $"2026-10-01T10:{i:00}:00Z");
        }

        var first = await new JudgeHistoryReader(bodies, null).ReadAsync(JudgeHistoryReader.DefaultLimit);
        var all = await new JudgeHistoryReader(bodies, null).ReadAsync(40);

        Assert.Equal(20, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.Equal("r24", first.Rows[0].Id);
        Assert.Equal(25, all.Rows.Count);
        Assert.False(all.HasMore);
    }

    [Fact]
    public async Task A_legacy_table_with_a_raw_column_and_no_newer_columns_is_read_with_what_it_has()
    {
        var legacy = File_("audit.db");
        Exec(legacy, LegacyOldDdl);
        Exec(legacy, "INSERT INTO judge_responses (id, timestamp, kind, direction, model, action, severity, latency_ms, raw) VALUES ('x', '2026-10-01T10:00:00Z', 'pii', 'completion', 'm', 'block', 'HIGH', 7, 'old-raw')");

        var result = await new JudgeHistoryReader(File_("missing.db"), legacy).ReadAsync();

        var row = Assert.Single(result.Rows);
        Assert.Equal("old-raw", row.Raw);
        Assert.Equal("block", row.Action);
        Assert.Equal("7", row.LatencyMs);
        Assert.Null(row.Confidence);
        Assert.False(row.FailClosed);
        Assert.Equal(JudgeHistorySource.LegacyAudit, row.Source);
    }

    [Fact]
    public async Task Confidence_and_fail_closed_are_carried_and_a_zero_confidence_is_kept()
    {
        var bodies = Bodies();
        Insert(bodies, "zero", "2026-10-01T10:00:00Z", confidence: 0.0, failClosed: 1);
        Insert(bodies, "some", "2026-10-01T09:00:00Z", confidence: 0.87654);

        var result = await new JudgeHistoryReader(bodies, null).ReadAsync();

        Assert.Equal(0.0, result.Rows[0].Confidence);
        Assert.True(result.Rows[0].FailClosed);
        Assert.Equal(0.87654, result.Rows[1].Confidence);
        Assert.False(result.Rows[1].FailClosed);
    }

    [Fact]
    public async Task A_huge_body_is_cut_and_flagged()
    {
        var bodies = Bodies();
        Insert(bodies, "big", "2026-10-01T10:00:00Z", raw: new string('x', JudgeHistoryReader.RawLimit + 500));
        Insert(bodies, "fits", "2026-10-01T09:00:00Z", raw: new string('y', 100));

        var result = await new JudgeHistoryReader(bodies, null).ReadAsync();

        Assert.Equal(JudgeHistoryReader.RawLimit, result.Rows[0].Raw.Length);
        Assert.True(result.Rows[0].RawTruncated);
        Assert.Equal(100, result.Rows[1].Raw.Length);
        Assert.False(result.Rows[1].RawTruncated);
    }

    [Fact]
    public async Task No_files_is_guidance_not_an_error()
    {
        var result = await new JudgeHistoryReader(File_("nope.db"), File_("nope-audit.db")).ReadAsync();

        Assert.Equal(JudgeHistoryStatus.Missing, result.Status);
        Assert.Equal(JudgeHistoryReader.MissingGuidance, result.Message);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_database_with_no_table_yet_is_not_initialized_and_one_with_an_empty_table_is_just_empty()
    {
        var bodies = File_("judge_bodies.db");
        Exec(bodies, "CREATE TABLE other (x INTEGER)");

        var none = await new JudgeHistoryReader(bodies, null).ReadAsync();
        Assert.Equal(JudgeHistoryStatus.NotInitialized, none.Status);

        Exec(bodies, BodiesDdl);
        var empty = await new JudgeHistoryReader(bodies, null).ReadAsync();
        Assert.Equal(JudgeHistoryStatus.Ok, empty.Status);
        Assert.Empty(empty.Rows);
    }

    [Fact]
    public async Task A_corrupt_file_is_an_error_text_and_does_not_throw()
    {
        var bodies = File_("judge_bodies.db");
        await File.WriteAllTextAsync(bodies, new string('z', 4096));

        var result = await new JudgeHistoryReader(bodies, null).ReadAsync();

        Assert.Equal(JudgeHistoryStatus.Error, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_database_locked_past_the_timeout_is_an_error_and_the_rows_survive()
    {
        var bodies = Bodies();
        Insert(bodies, "a", "2026-10-01T10:00:00Z");
        // An exclusive writer holds the file: a read-only reader must wait, then report, not throw.
        using var writer = new SqliteConnection($"Data Source={bodies};Pooling=False");
        writer.Open();
        using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE";
            _ = begin.ExecuteNonQuery();
        }

        var result = await new JudgeHistoryReader(bodies, null, TimeSpan.FromSeconds(3)).ReadAsync();

        Assert.Equal(JudgeHistoryStatus.Error, result.Status);
        using (var rollback = writer.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            _ = rollback.ExecuteNonQuery();
        }

        // Rolled back, and the reader added nothing of its own: the one row is still the only content.
        var after = await new JudgeHistoryReader(bodies, null).ReadAsync();
        Assert.Equal(new[] { "a" }, Ids(after));
    }

    [Fact]
    public async Task The_same_file_named_twice_is_read_once()
    {
        var bodies = Bodies();
        Insert(bodies, "only", "2026-10-01T10:00:00Z");

        var result = await new JudgeHistoryReader(bodies, bodies.ToUpperInvariant()).ReadAsync();

        Assert.Equal(new[] { "only" }, Ids(result));
    }

    [Fact]
    public async Task A_caller_cancellation_is_an_operation_cancelled_not_a_result()
    {
        var bodies = Bodies();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JudgeHistoryReader(bodies, null).ReadAsync(20, cts.Token));
    }

    [Fact]
    public void Config_paths_win_over_the_defaults_relative_ones_are_read_from_the_data_directory_and_a_bad_config_names_nothing()
    {
        var paths = new DefenseClawPaths(dataDirectory: _dir);

        var configured = JudgeHistoryReader.ForConfig(paths, "observability:\n  local:\n    judge_bodies_path: custom/j.db\n    path: " + File_("elsewhere.db").Replace('\\', '/') + "\n");
        Assert.Equal(Path.GetFullPath(Path.Combine("custom", "j.db"), _dir), configured.JudgeBodiesPath);
        Assert.Equal(Path.GetFullPath(File_("elsewhere.db")), configured.LegacyAuditPath);

        var flat = JudgeHistoryReader.ForConfig(paths, "judge_bodies_db: flat.db\naudit_db: flat-audit.db\n");
        Assert.Equal(Path.Combine(_dir, "flat.db"), flat.JudgeBodiesPath);
        Assert.Equal(Path.Combine(_dir, "flat-audit.db"), flat.LegacyAuditPath);

        foreach (var yaml in new[] { null, "", "observability: [unclosed", "observability: 3" })
        {
            var fallback = JudgeHistoryReader.ForConfig(paths, yaml);
            Assert.Equal(paths.JudgeBodiesDatabasePath, fallback.JudgeBodiesPath);
            Assert.Equal(paths.AuditDatabasePath, fallback.LegacyAuditPath);
        }
    }

    [Theory]
    [InlineData("2026-10-01 12:00:00.123456789 +0000 UTC", 1_790_856_000_123_456_789)]
    [InlineData("2026-10-01T12:00:00.123456789Z", 1_790_856_000_123_456_789)]
    [InlineData("2026-10-01T14:00:00+02:00", 1_790_856_000_000_000_000)]
    [InlineData("2026-10-01 05:00:00 -0700 MST", 1_790_856_000_000_000_000)]
    public void Timestamps_read_to_the_nanosecond(string text, long expected) =>
        Assert.Equal(expected, JudgeHistoryReader.UnixNano(text));
}
