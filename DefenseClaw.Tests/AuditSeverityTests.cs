using System.Globalization;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace DefenseClaw.Tests;

/// <summary>
/// Everything about how <see cref="AuditReader"/> reads severity, over synthetic databases built from the real DDL
/// (retention triggers and indexes included): the fast, index-only answers must be the <em>same</em> answers the old
/// <c>UPPER(severity)</c> statements gave, for every window, in every spelling, and the plans must really be the
/// index-friendly ones. The "old" statements are re-run here verbatim as the oracle.
/// <para>
/// The fixture holds 600 rows over three hours with timestamps written the way Go writes them (fractions with trailing
/// zeros trimmed, or none) and 21 severity spellings: every case variant, a padded one, a dotless-i one that only
/// <c>ToUpperInvariant</c> would fold to a match, an unknown one, an empty one and NULL. A second database adds rows
/// in timestamp formats the retention trigger understands but text ordering does not (an offset, a space instead of
/// <c>T</c>), which is where the text-bound counts stop being trustworthy and the reconcile must reject them.
/// </para>
/// </summary>
public sealed class AuditSeverityTests : IClassFixture<AuditSeverityFixture>
{
    private readonly AuditSeverityFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AuditSeverityTests(AuditSeverityFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static readonly AuditSeverity[] Minimums =
    {
        AuditSeverity.Info, AuditSeverity.Low, AuditSeverity.Warn, AuditSeverity.Medium, AuditSeverity.High, AuditSeverity.Critical,
    };

    // ---------------------------------------------------------------- the old statements, as the oracle

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(path));
        connection.Open();
        return connection;
    }

    private static Dictionary<AuditSeverity, int> OldTiles(string path, DateTimeOffset? from)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test: the reader's pre-optimisation reference statement, built from literals and a bound $from, run read-only on a temp database
        command.CommandText = "SELECT UPPER(e.severity), COUNT(*) FROM audit_events e"
            + (from is null ? string.Empty : " WHERE e.retention_timestamp_unix_nano >= $from")
            + " GROUP BY UPPER(e.severity)";
        if (from is { } f)
        {
            command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(f));
        }

        var tiles = new Dictionary<AuditSeverity, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var severity = AuditSeverityExtensions.Parse(reader.IsDBNull(0) ? null : reader.GetString(0));
            tiles[severity] = tiles.GetValueOrDefault(severity) + reader.GetInt32(1);
        }

        return tiles;
    }

    private static string OldSeverityClause(AuditSeverity minimum, SqliteCommand command)
    {
        var allowed = AuditSeverityExtensions.AtOrAbove(minimum);
        var names = new List<string>();
        for (var i = 0; i < allowed.Count; i++)
        {
            names.Add("$s" + i.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(names[i], allowed[i]);
        }

        return $"UPPER(e.severity) IN ({string.Join(", ", names)})";
    }

    private static int OldCount(string path, AuditSeverity minimum, DateTimeOffset? from)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        var where = OldSeverityClause(minimum, command);
        if (from is { } f)
        {
            where += " AND e.retention_timestamp_unix_nano >= $from";
            command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(f));
        }

        // nosemgrep: csharp-sqli -- test: the reference statement's WHERE is OldSeverityClause plus a literal bound on $from; every value is a bound parameter
        command.CommandText = "SELECT COUNT(*) FROM audit_events e WHERE " + where;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> OldPage(string path, AuditSeverity minimum, DateTimeOffset? from, int limit)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        var where = OldSeverityClause(minimum, command);
        if (from is { } f)
        {
            where += " AND e.retention_timestamp_unix_nano >= $from";
            command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(f));
        }

        // nosemgrep: csharp-sqli -- test: the reference statement's WHERE is OldSeverityClause plus a literal bound on $from; limit is an int formatted invariantly; every value is a bound parameter
        command.CommandText = "SELECT e.id FROM audit_events e WHERE " + where
            + " ORDER BY e.retention_timestamp_unix_nano DESC, e.id DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture);
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static string Describe(IReadOnlyDictionary<AuditSeverity, int> tiles) =>
        string.Join(",", tiles.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"));

    // ---------------------------------------------------------------- the windows that matter

    /// <summary>
    /// Windows chosen to break a text bound: none, a whole second, a mid-second, and, around real rows, one tick before,
    /// exactly at and one tick after a row's timestamp (so the row sits inside the second the window starts in).
    /// </summary>
    public static IEnumerable<object?[]> WindowIndexes()
    {
        for (var i = 0; i < AuditSeverityFixture.WindowCount; i++)
        {
            yield return new object?[] { i };
        }
    }

    // ---------------------------------------------------------------- tiles

    [Theory]
    [MemberData(nameof(WindowIndexes))]
    public async Task The_tiles_equal_the_old_UPPER_grouping_for_every_window(int index)
    {
        var from = _fixture.Windows[index];
        var reader = new AuditReader(_fixture.CanonicalPath);

        var tiles = await reader.CountBySeverityAsync(new AuditQuery { From = from });

        Assert.Equal(Describe(OldTiles(_fixture.CanonicalPath, from)), Describe(tiles));

        // Not merely "right": the index-only answer reconciled, so the exact statement never had to step in. (If the
        // second the window starts in were not counted exactly the reconcile would fail, and this would read 1.)
        Assert.Equal(0, reader.SpellingCountFallbacks);
    }

    [Fact]
    public async Task The_tiles_still_split_nothing_and_lose_nothing_over_the_dotless_i_and_padded_spellings()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        var tiles = await reader.CountBySeverityAsync(new AuditQuery());

        Assert.Equal(_fixture.RowCount, tiles.Values.Sum());
        Assert.True(tiles.ContainsKey(AuditSeverity.Unknown), "ERROR, '' and NULL are the Unknown tile");
        Assert.Equal(Describe(OldTiles(_fixture.CanonicalPath, null)), Describe(tiles));
    }

    [Fact]
    public async Task The_tiles_for_a_window_use_the_covering_severity_index_and_read_no_table_row_or_temp_tree()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        // A window that starts part-way through a second: that second is counted exactly, in one more (non-covering) search.
        var plan = await reader.ExplainAsync(new AuditQuery { From = _fixture.Windows[6] }, AuditQueryShape.CountBySeverity);
        _output.WriteLine(string.Join(Environment.NewLine, plan));

        Assert.Contains(plan, line => line.Contains("COVERING INDEX idx_audit_severity_timestamp (severity=? AND timestamp>?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        AssertNoTableScan(plan);
    }

    [Fact]
    public async Task All_time_tiles_use_the_covering_severity_index_too()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        var plan = await reader.ExplainAsync(new AuditQuery(), AuditQueryShape.CountBySeverity);

        Assert.Contains(plan, line => line.Contains("COVERING INDEX idx_audit_severity_timestamp (severity=?)", StringComparison.Ordinal));
        AssertNoTableScan(plan);
    }

    [Fact]
    public async Task Tiles_with_another_filter_keep_the_exact_UPPER_grouping()
    {
        // A bucket is not in the covering index, so a per-spelling count would need a row fetch per row: the exact
        // statement is the right one, and it still answers the same.
        var reader = new AuditReader(_fixture.CanonicalPath);
        var query = new AuditQuery { From = _fixture.Windows[2], Bucket = "security.finding" };

        var plan = await reader.ExplainAsync(query, AuditQueryShape.CountBySeverity);
        var tiles = await reader.CountBySeverityAsync(query);

        Assert.Contains(plan, line => line.Contains("TEMP B-TREE FOR GROUP BY", StringComparison.Ordinal));
        Assert.Equal(_fixture.ExpectedTilesInBucket(_fixture.Windows[2], "security.finding"), Describe(tiles));
    }

    // ---------------------------------------------------------------- counts under a severity filter

    [Theory]
    [MemberData(nameof(WindowIndexes))]
    public async Task A_severity_filtered_count_equals_the_old_UPPER_count_for_every_minimum_and_window(int index)
    {
        var from = _fixture.Windows[index];
        var reader = new AuditReader(_fixture.CanonicalPath);

        foreach (var minimum in Minimums)
        {
            var count = await reader.CountAsync(new AuditQuery { MinimumSeverity = minimum, From = from });

            Assert.True(
                OldCount(_fixture.CanonicalPath, minimum, from) == count,
                $"{minimum} from {from:O}: old {OldCount(_fixture.CanonicalPath, minimum, from)}, new {count}");
        }

        Assert.Equal(0, reader.SpellingCountFallbacks);
    }

    [Fact]
    public async Task A_severity_filtered_window_count_is_answered_from_the_covering_severity_index()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        var plan = await reader.ExplainAsync(
            new AuditQuery { MinimumSeverity = AuditSeverity.High, From = _fixture.Windows[2] },
            AuditQueryShape.Count);

        Assert.Contains(plan, line => line.Contains("COVERING INDEX idx_audit_severity_timestamp", StringComparison.Ordinal));
        AssertNoTableScan(plan);
    }

    [Fact]
    public async Task An_all_time_severity_filtered_count_equals_the_old_count_and_stays_on_the_index()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        foreach (var minimum in Minimums)
        {
            Assert.Equal(OldCount(_fixture.CanonicalPath, minimum, null), await reader.CountAsync(new AuditQuery { MinimumSeverity = minimum }));
        }

        var plan = await reader.ExplainAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Critical }, AuditQueryShape.Count);
        Assert.Contains(plan, line => line.Contains("COVERING INDEX idx_audit_severity_timestamp", StringComparison.Ordinal));
        AssertNoTableScan(plan);
    }

    [Fact]
    public async Task A_severity_filtered_count_with_a_bucket_uses_the_row_filter_and_agrees_with_the_old_count()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);
        var from = _fixture.Windows[2];

        var count = await reader.CountAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Medium, Bucket = "security.finding", From = from });

        Assert.Equal(_fixture.ExpectedCount(AuditSeverity.Medium, "security.finding", from), count);
    }

    // ---------------------------------------------------------------- pages under a severity filter

    [Theory]
    [MemberData(nameof(WindowIndexes))]
    public async Task A_severity_filtered_page_is_the_old_page_whichever_way_it_is_read(int index)
    {
        var from = _fixture.Windows[index];

        // Threshold 1: every non-empty set is "common", so the retention index is walked and the severity filtered.
        // Default: every set in a 600-row table is "rare", so the severity index is sought and the matches sorted.
        var walking = new AuditReader(_fixture.CanonicalPath, commonRowThreshold: 1);
        var seeking = new AuditReader(_fixture.CanonicalPath);

        foreach (var minimum in Minimums)
        {
            var expected = OldPage(_fixture.CanonicalPath, minimum, from, limit: 40);
            var query = new AuditQuery { MinimumSeverity = minimum, From = from, Limit = 40 };

            Assert.Equal(expected, (await walking.QueryAsync(query)).Events.Select(e => e.Id));
            Assert.Equal(expected, (await seeking.QueryAsync(query)).Events.Select(e => e.Id));
        }
    }

    [Fact]
    public async Task Paging_through_a_severity_filter_visits_exactly_the_matching_rows_once()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);
        var expected = OldPage(_fixture.CanonicalPath, AuditSeverity.Warn, null, limit: 10_000);

        var seen = new List<string>();
        AuditCursor? cursor = null;
        for (var guard = 0; guard < 500; guard++)
        {
            var page = await reader.QueryAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Warn, Limit = 17, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Id));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task A_common_severity_set_walks_the_retention_index_and_a_rare_one_is_sought_on_the_severity_index()
    {
        var walking = new AuditReader(_fixture.CanonicalPath, commonRowThreshold: 1);
        var seeking = new AuditReader(_fixture.CanonicalPath);
        var query = new AuditQuery { MinimumSeverity = AuditSeverity.High, Limit = 50 };

        var walk = await walking.ExplainAsync(query, AuditQueryShape.Page);
        var seek = await seeking.ExplainAsync(query, AuditQueryShape.Page);
        _output.WriteLine("common: " + string.Join(" | ", walk));
        _output.WriteLine("rare:   " + string.Join(" | ", seek));

        // The point of the walk: the newest page of a common set needs no sort and never reads idx_audit_severity_timestamp
        // (measured live: 28 s and 4.1 GB for "INFO and above" when the planner seeks it and sorts every match).
        Assert.Contains(walk, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(walk, line => line.Contains("idx_audit_severity_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(walk, line => line.Contains("TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));

        Assert.Contains(seek, line => line.Contains("idx_audit_severity_timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_minimum_above_everything_stored_matches_nothing_without_a_scan()
    {
        // Only INFO-and-lower spellings exist in this database.
        using var database = new TestAuditDatabase();
        database.InsertEvent("only-1", AuditSeverityFixture.Base, "scan", "INFO");
        database.InsertEvent("only-2", AuditSeverityFixture.Base.AddMinutes(1), "scan", "low");
        var reader = new AuditReader(database.Path);

        Assert.Empty((await reader.QueryAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Critical })).Events);
        Assert.Equal(0, await reader.CountAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Critical }));
        Assert.Equal(0, await reader.CountAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Critical, From = AuditSeverityFixture.Base }));
    }

    [Fact]
    public async Task Only_ascii_letters_fold_so_a_dotless_i_never_satisfies_a_minimum()
    {
        // SQLite's UPPER() leaves "hıgh" alone, so the old filter never matched it; string.ToUpperInvariant would
        // turn it into HIGH. The new one must select the same rows as the old one did.
        var reader = new AuditReader(_fixture.CanonicalPath);
        var page = await reader.QueryAsync(new AuditQuery { MinimumSeverity = AuditSeverity.High, Limit = 5000 });

        Assert.DoesNotContain(page.Events, e => e.Severity == "hıgh");
        Assert.Contains(page.Events, e => e.Severity == "High");
        Assert.Contains(page.Events, e => e.Severity == "high");
        Assert.DoesNotContain(page.Events, e => e.Severity == " HIGH ");
    }

    // ---------------------------------------------------------------- a database the text bound cannot be trusted on

    [Fact]
    public void The_fixture_really_holds_timestamps_the_text_bound_gets_wrong()
    {
        // Proof that the legacy database exercises the reconcile: counting by text alone (what the index can do) is not the
        // answer the retention column gives, while on the canonical database it is.
        var from = _fixture.Windows[2]!.Value;
        var textBound = from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        Assert.Equal(_fixture.TruthCount(_fixture.CanonicalPath, from), _fixture.NaiveTextCount(_fixture.CanonicalPath, textBound));
        Assert.NotEqual(_fixture.TruthCount(_fixture.LegacyPath, from), _fixture.NaiveTextCount(_fixture.LegacyPath, textBound));
    }

    [Theory]
    [MemberData(nameof(WindowIndexes))]
    public async Task On_a_database_with_legacy_timestamp_formats_every_answer_still_equals_the_old_one(int index)
    {
        var from = _fixture.Windows[index];
        var reader = new AuditReader(_fixture.LegacyPath);

        var tiles = await reader.CountBySeverityAsync(new AuditQuery { From = from });
        Assert.Equal(Describe(OldTiles(_fixture.LegacyPath, from)), Describe(tiles));
        if (from is not null && from > AuditSeverityFixture.Base.AddMinutes(1) && from < AuditSeverityFixture.Base.AddHours(3))
        {
            // Rows written with an offset or a space sort by their text where the window is by instant: the counts do
            // not add up, and the exact statement gave the answer above.
            Assert.True(reader.SpellingCountFallbacks >= 1, $"window {from:O} was answered from text order on a database it is wrong for");
        }

        foreach (var minimum in Minimums)
        {
            Assert.Equal(OldCount(_fixture.LegacyPath, minimum, from), await reader.CountAsync(new AuditQuery { MinimumSeverity = minimum, From = from }));
            Assert.Equal(
                OldPage(_fixture.LegacyPath, minimum, from, 30),
                (await reader.QueryAsync(new AuditQuery { MinimumSeverity = minimum, From = from, Limit = 30 })).Events.Select(e => e.Id));
        }
    }

    // ---------------------------------------------------------------- the distinct lists (D2-4)

    [Fact]
    public async Task The_distinct_lists_equal_SELECT_DISTINCT_and_omit_NULL_and_empty()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);

        foreach (var (column, list) in new (string, Func<Task<IReadOnlyList<string>>>)[]
        {
            ("bucket", () => reader.ListBucketsAsync()),
            ("connector", () => reader.ListConnectorsAsync()),
            ("action", () => reader.ListActionsAsync()),
        })
        {
            using var connection = OpenReadOnly(_fixture.CanonicalPath);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {column} FROM audit_events WHERE {column} IS NOT NULL AND {column} <> '' ORDER BY {column}";
            var expected = new List<string>();
            using (var rows = command.ExecuteReader())
            {
                while (rows.Read())
                {
                    expected.Add(rows.GetString(0));
                }
            }

            var actual = await list();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain(string.Empty, actual);
        }
    }

    [Theory]
    [InlineData("bucket", "idx_audit_bucket_timestamp")]
    [InlineData("connector", "idx_audit_connector")]
    [InlineData("action", "idx_audit_action")]
    [InlineData("severity", "idx_audit_severity_timestamp")]
    public void The_distinct_scan_is_a_series_of_index_seeks_never_a_scan_of_the_table(string column, string index)
    {
        using var connection = OpenReadOnly(_fixture.CanonicalPath);
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test: the EXPLAIN prefix and the reader's own BuildDistinctSql, with column taken from this theory's InlineData literals
        command.CommandText = "EXPLAIN QUERY PLAN " + AuditReader.BuildDistinctSql(column, skipEmpty: column != "severity", limited: false);

        var plan = new List<string>();
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                plan.Add(rows.GetString(3));
            }
        }

        _output.WriteLine(column + ": " + string.Join(" | ", plan));
        Assert.Contains(plan, line => line.Contains("COVERING INDEX " + index, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN audit_events", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- cancelling a running statement (D2-3)

    [Fact]
    public async Task Cancelling_the_token_stops_a_statement_that_would_otherwise_never_finish()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var run = ReaderOffload.Run(
            async () =>
            {
                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                using var interrupt = ReaderOffload.InterruptOnCancel(connection, cts.Token);

                await using var command = connection.CreateCommand();
                // Endless: only an interrupt ends it.
                command.CommandText = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c";
                started.SetResult();
                return await command.ExecuteScalarAsync();
            },
            cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_token_that_can_never_be_cancelled_registers_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        Assert.Equal(default, ReaderOffload.InterruptOnCancel(connection, CancellationToken.None));
    }

    [Fact]
    public async Task A_search_cancelled_at_any_moment_ends_normally_or_as_a_cancellation_and_never_as_a_sqlite_error()
    {
        using var database = new TestAuditDatabase();
        AuditSeverityFixture.Fill(database.Path, 12_000, legacyFormats: false, details: new string('x', 200));
        var reader = new AuditReader(database.Path);

        var completed = 0;
        var cancelled = 0;
        for (var i = 0; i < 24; i++)
        {
            using var cts = new CancellationTokenSource();
            var run = reader.QueryAsync(new AuditQuery { SearchText = "no-such-text-zq", Limit = 100 }, cts.Token);
            await Task.Delay(i % 8);
            cts.Cancel();

            try
            {
                _ = await run;
                completed++;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
        }

        _output.WriteLine($"24 searches over 12,000 rows, cancelled 0-7 ms in: {completed} finished, {cancelled} cancelled.");
        Assert.Equal(24, completed + cancelled);

        // The reader is not left broken: the pooled connections the cancelled statements used answer normally.
        Assert.Equal(12_000, await reader.CountAsync(new AuditQuery()));
        SqlitePools.Release(database.Path);
    }

    [Fact]
    public async Task The_call_counters_count_calls_even_when_they_are_cancelled()
    {
        var reader = new AuditReader(_fixture.CanonicalPath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        _ = await reader.QueryAsync(new AuditQuery { Limit = 1 });
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.QueryAsync(new AuditQuery(), cancelled.Token));
        _ = await reader.CountAsync(new AuditQuery());
        _ = await reader.CountBySeverityAsync(new AuditQuery());

        Assert.Equal(2, reader.PageQueryCount);
        Assert.Equal(2, reader.CountQueryCount);
    }

    // ---------------------------------------------------------------- a row with no id (D3-15)

    [Fact]
    public async Task A_row_with_a_null_id_is_shown_with_an_empty_id_instead_of_failing_the_page()
    {
        using var database = new TestAuditDatabase();
        database.InsertEvent("real-1", AuditSeverityFixture.Base.AddMinutes(1), "scan", "INFO");
        database.InsertEvent("real-2", AuditSeverityFixture.Base.AddMinutes(3), "scan", "HIGH");
        InsertRaw(database, "NULL", "'2026-07-28T12:02:00.0000000Z'", "'scan'", "'INFO'");
        var reader = new AuditReader(database.Path);

        var page = await reader.QueryAsync(new AuditQuery());

        Assert.Equal(new[] { "real-2", string.Empty, "real-1" }, page.Events.Select(e => e.Id));
        Assert.Equal(3, await reader.CountAsync(new AuditQuery()));
        Assert.Equal(3, (await reader.CountBySeverityAsync(new AuditQuery())).Values.Sum());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Paging_across_a_null_id_row_visits_every_row_exactly_once(int pageSize, bool ascending)
    {
        using var database = new TestAuditDatabase();
        for (var i = 1; i <= 4; i++)
        {
            database.InsertEvent("real-" + i, AuditSeverityFixture.Base.AddMinutes(i * 2), "scan", "INFO");
        }

        // One with a timestamp of its own, and one sharing a timestamp with a real row (a tie the id must break).
        InsertRaw(database, "NULL", "'2026-07-28T12:03:00.0000000Z'", "'scan'", "'INFO'");
        InsertRaw(database, "NULL", "'2026-07-28T12:04:00.0000000Z'", "'scan'", "'INFO'");
        var reader = new AuditReader(database.Path);

        var seen = new List<string>();
        AuditCursor? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = await reader.QueryAsync(new AuditQuery { Limit = pageSize, Ascending = ascending, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Id));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        // Every row exactly once, in one total order: the null-id rows sort below every id, so they come first among
        // their ties ascending (real-1, 12:03, 12:04 null, real-2 ...) and last descending.
        var ascendingOrder = new[] { "real-1", string.Empty, string.Empty, "real-2", "real-3", "real-4" };
        // Enumerable.Reverse spelled out: on newer compilers an array's .Reverse() binds to the in-place span overload.
        Assert.Equal(ascending ? ascendingOrder : Enumerable.Reverse(ascendingOrder), seen);
    }

    [Fact]
    public async Task Odd_shapes_of_data_never_fail_a_page()
    {
        using var database = new TestAuditDatabase();
        database.InsertEvent("normal", AuditSeverityFixture.Base, "scan", "INFO", details: "fine");
        InsertRaw(database, "'garbage-ts'", "'not a timestamp'", "'scan'", "'INFO'");
        InsertRaw(database, "'empty-ts'", "''", "'scan'", "'INFO'");
        InsertRaw(database, "'int-ts'", "1786000000", "'scan'", "'INFO'");
        InsertRaw(database, "'blob-details'", "'2026-07-28T12:30:00Z'", "'scan'", "'INFO'", details: "x'DEADBEEF'");
        InsertRaw(database, "'int-action'", "'2026-07-28T12:31:00Z'", "42", "'INFO'");
        InsertRaw(database, "NULL", "'2026-07-28T12:32:00Z'", "'scan'", "NULL");
        var reader = new AuditReader(database.Path);

        var page = await reader.QueryAsync(new AuditQuery { Limit = 100 });

        Assert.Equal(7, page.Events.Count);
        Assert.Equal(7, await reader.CountAsync(new AuditQuery()));
        Assert.Equal(7, (await reader.CountBySeverityAsync(new AuditQuery())).Values.Sum());
    }

    private static void InsertRaw(TestAuditDatabase database, string id, string timestamp, string action, string severity, string details = "NULL")
    {
        using var connection = database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText =
            // nosemgrep: csharp-sqli -- test: the arguments are SQL literal tokens (quoted text or NULL) written by the calling test, so odd stored values can be inserted; temp database
            $"INSERT INTO audit_events (id, timestamp, action, actor, severity, details) VALUES ({id}, {timestamp}, {action}, 'audit_logger', {severity}, {details})";
        _ = command.ExecuteNonQuery();
    }

    private static void AssertNoTableScan(IReadOnlyList<string> plan) =>
        Assert.DoesNotContain(
            plan,
            line => (line.StartsWith("SCAN e", StringComparison.Ordinal) || line.StartsWith("SCAN audit_events", StringComparison.Ordinal))
                && !line.Contains("USING COVERING INDEX", StringComparison.Ordinal));
}

/// <summary>The two synthetic databases <see cref="AuditSeverityTests"/> share (building one costs a second of DDL).</summary>
public sealed class AuditSeverityFixture : IDisposable
{
    public static readonly DateTimeOffset Base = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How many entries <see cref="Windows"/> holds.</summary>
    public const int WindowCount = 14;

    private static readonly string?[] Spellings =
    {
        "INFO", "INFO", "INFO", "INFO", "info", "Info", "LOW", "MEDIUM", "medium", "HIGH", "High", "high", "CRITICAL", "critical",
        "WARN", "warning", "ERROR", "", null, " HIGH ", "hıgh", "FATAL",
    };

    private readonly TestAuditDatabase _canonical = new();
    private readonly TestAuditDatabase _legacy = new();

    public AuditSeverityFixture()
    {
        CanonicalPath = _canonical.Path;
        LegacyPath = _legacy.Path;
        Fill(CanonicalPath, RowCount, legacyFormats: false);
        Fill(LegacyPath, RowCount, legacyFormats: true);

        Windows = BuildWindows();
    }

    public int RowCount => 600;

    public string CanonicalPath { get; }

    public string LegacyPath { get; }

    /// <summary>Window starts: see <see cref="AuditSeverityTests.WindowIndexes"/>. Index 0 is "no window" (null).</summary>
    public DateTimeOffset?[] Windows { get; }

    public void Dispose()
    {
        _canonical.Dispose();
        _legacy.Dispose();
    }

    /// <summary>Text timestamp the way Go writes it: RFC3339 UTC, fraction with its trailing zeros trimmed, none at all when zero.</summary>
    public static string GoTimestamp(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var ticks = utc.Ticks % TimeSpan.TicksPerSecond;
        return ticks == 0 ? text + "Z" : text + "." + ticks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0') + "Z";
    }

    /// <summary>
    /// Fills a database created from the real DDL. <paramref name="legacyFormats"/> adds rows whose timestamp the retention
    /// trigger parses but that do not sort as <c>yyyy-MM-ddTHH:mm:ss</c> text (a UTC offset; a space where the T goes).
    /// </summary>
    public static void Fill(string path, int rows, bool legacyFormats, string? details = null)
    {
        var random = new Random(20260728);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, actor, severity, bucket, connector, details)
            VALUES ($id, $timestamp, $action, 'audit_logger', $severity, $bucket, $connector, $details)
            """;
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var timestamp = insert.Parameters.Add("$timestamp", SqliteType.Text);
        var action = insert.Parameters.Add("$action", SqliteType.Text);
        var severity = insert.Parameters.Add("$severity", SqliteType.Text);
        var bucket = insert.Parameters.Add("$bucket", SqliteType.Text);
        var connector = insert.Parameters.Add("$connector", SqliteType.Text);
        var detailsParameter = insert.Parameters.Add("$details", SqliteType.Text);

        string?[] buckets = { "security.finding", "guardrail.evaluation", "platform.health", "", null };
        string?[] connectors = { "claudecode", "codex", null, "" };
        string[] actions = { "scan", "hook_decision", "tool_invocation", "config.change" };

        var previous = Base;
        for (var i = 0; i < rows; i++)
        {
            DateTimeOffset when;
            if (i > 0 && i % 8 == 0)
            {
                when = previous;
            }
            else
            {
                when = Base.AddTicks(random.NextInt64(0, TimeSpan.TicksPerHour * 3));
                if (i % 10 == 0)
                {
                    when = new DateTimeOffset(when.UtcDateTime.Ticks - when.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
                }
            }

            previous = when;

            id.Value = "r-" + i.ToString("D5", CultureInfo.InvariantCulture);
            timestamp.Value = LegacyText(when, i, legacyFormats);
            action.Value = actions[i % actions.Length];
            severity.Value = (object?)Spellings[random.Next(Spellings.Length)] ?? DBNull.Value;
            bucket.Value = (object?)buckets[random.Next(buckets.Length)] ?? DBNull.Value;
            connector.Value = (object?)connectors[random.Next(connectors.Length)] ?? DBNull.Value;
            detailsParameter.Value = (object?)details ?? "row " + i.ToString(CultureInfo.InvariantCulture);
            _ = insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string LegacyText(DateTimeOffset when, int i, bool legacyFormats)
    {
        if (!legacyFormats || i % 5 != 0)
        {
            return GoTimestamp(when);
        }

        var utc = when.UtcDateTime;
        return (i / 5 % 3) switch
        {
            // The same instant written with a +02:00 offset: text order is two hours off.
            0 => utc.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "+02:00",

            // A space where the T goes, and a zone suffix (an older gateway's time.Time.String()).
            1 => utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + " +0000 UTC",

            // A space and no zone at all: read as UTC.
            _ => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
    }

    private DateTimeOffset?[] BuildWindows()
    {
        // Real timestamps to sit the window edges on: the retention key is the truth.
        var stamps = new List<DateTimeOffset>();
        using (var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(CanonicalPath)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT retention_timestamp_unix_nano FROM audit_events ORDER BY retention_timestamp_unix_nano";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                stamps.Add(AuditReader.FromUnixNanos(reader.GetInt64(0)));
            }
        }

        var wholeSecond = new DateTimeOffset(Base.Ticks + TimeSpan.TicksPerHour, TimeSpan.Zero);
        var fractional = stamps.First(s => s.UtcDateTime.Ticks % TimeSpan.TicksPerSecond != 0 && s > Base.AddMinutes(30));
        var onWholeSecond = stamps.First(s => s.UtcDateTime.Ticks % TimeSpan.TicksPerSecond == 0 && s > Base.AddMinutes(45));

        return new DateTimeOffset?[]
        {
            null,
            Base.AddYears(-1),
            Base.AddMinutes(30),
            wholeSecond,
            wholeSecond.AddMilliseconds(500),
            fractional.AddTicks(-1),
            fractional,
            fractional.AddTicks(1),
            onWholeSecond.AddTicks(-1),
            onWholeSecond,
            onWholeSecond.AddTicks(1),
            stamps[^1],
            stamps[^1].AddTicks(1),
            Base.AddYears(1),
        };
    }

    // ---------------------------------------------------------------- oracles for the "what does the fixture do to a text bound" check

    public int TruthCount(string path, DateTimeOffset from)
    {
        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE retention_timestamp_unix_nano >= $from";
        command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(from));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int NaiveTextCount(string path, string textBound)
    {
        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE timestamp >= $bound";
        command.Parameters.AddWithValue("$bound", textBound);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The per-severity tiles of one bucket in a window, described like the reader's, from the old statement.</summary>
    public string ExpectedTilesInBucket(DateTimeOffset? from, string bucket)
    {
        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(CanonicalPath));
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test: the pre-optimisation reference statement, built from literals with $bucket and $from bound, run read-only on a temp database
        command.CommandText = "SELECT UPPER(e.severity), COUNT(*) FROM audit_events e WHERE e.bucket = $bucket"
            + (from is null ? string.Empty : " AND e.retention_timestamp_unix_nano >= $from")
            + " GROUP BY UPPER(e.severity)";
        command.Parameters.AddWithValue("$bucket", bucket);
        if (from is { } f)
        {
            command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(f));
        }

        var tiles = new Dictionary<AuditSeverity, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var severity = AuditSeverityExtensions.Parse(reader.IsDBNull(0) ? null : reader.GetString(0));
            tiles[severity] = tiles.GetValueOrDefault(severity) + reader.GetInt32(1);
        }

        return string.Join(",", tiles.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"));
    }

    public int ExpectedCount(AuditSeverity minimum, string bucket, DateTimeOffset? from)
    {
        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(CanonicalPath));
        connection.Open();
        using var command = connection.CreateCommand();
        var allowed = AuditSeverityExtensions.AtOrAbove(minimum);
        var names = allowed.Select((_, i) => "$s" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        for (var i = 0; i < allowed.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], allowed[i]);
        }

        // nosemgrep: csharp-sqli -- test: names are the $s0..$sN placeholders built above from the loop index; severities, $bucket and $from are bound; temp database
        command.CommandText = $"SELECT COUNT(*) FROM audit_events e WHERE UPPER(e.severity) IN ({string.Join(", ", names)}) AND e.bucket = $bucket"
            + (from is null ? string.Empty : " AND e.retention_timestamp_unix_nano >= $from");
        command.Parameters.AddWithValue("$bucket", bucket);
        if (from is { } f)
        {
            command.Parameters.AddWithValue("$from", AuditReader.ToUnixNanos(f));
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
