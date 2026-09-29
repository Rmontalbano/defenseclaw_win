using System.Globalization;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// inventory.db has no published schema contract, so the reader discovers it at runtime.
/// These tests use a synthetic database shaped like the live one (ai_signals, ai_scans,
/// and an ai_components_v view) without copying any real inventory data.
/// </summary>
public class InventoryReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new("dcw-inventory");
    private readonly string _path;
    private readonly InventoryReader _reader;

    public InventoryReaderTests()
    {
        var path = _directory.File("inventory.db");
        _path = path;

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ai_signals (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    name TEXT,
                    confidence REAL,
                    last_seen TEXT
                );
                CREATE TABLE ai_scans (id TEXT PRIMARY KEY, started_at TEXT);
                CREATE VIEW ai_components_v AS SELECT id, name, confidence FROM ai_signals;

                INSERT INTO ai_signals VALUES ('sig-1', 'sdk', 'anthropic-sdk', 0.9, '2026-07-28T12:00:00Z');
                INSERT INTO ai_signals VALUES ('sig-2', 'agent', 'claude-code', 0.75, '2026-07-28T12:05:00Z');
                INSERT INTO ai_signals VALUES ('sig-3', 'sdk', NULL, NULL, NULL);
                INSERT INTO ai_scans VALUES ('scan-1', '2026-07-28T12:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        _reader = new InventoryReader(path);
    }

    [Fact]
    public async Task Discovers_tables_and_views_at_runtime()
    {
        var tables = await _reader.ListTablesAsync();

        Assert.Equal(new[] { "ai_components_v", "ai_scans", "ai_signals" }, tables.Select(t => t.Name));
        Assert.True(tables.Single(t => t.Name == "ai_components_v").IsView);
        Assert.False(tables.Single(t => t.Name == "ai_signals").IsView);
    }

    [Fact]
    public async Task Discovers_columns()
    {
        var columns = await _reader.ListColumnsAsync("ai_signals");

        Assert.Equal(new[] { "id", "kind", "name", "confidence", "last_seen" }, columns.Select(c => c.Name));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.True(columns[1].NotNull);
        Assert.Equal("REAL", columns[3].DeclaredType);
    }

    [Fact]
    public async Task Browses_rows_generically()
    {
        var result = await _reader.BrowseAsync("ai_signals", limit: 2, orderByColumn: "id");

        Assert.Equal(new[] { "id", "kind", "name", "confidence", "last_seen" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("sig-1", result.Rows[0]["id"]);
        Assert.Equal("anthropic-sdk", result.Rows[0]["name"]);
    }

    [Fact]
    public async Task Nulls_come_back_as_null()
    {
        var result = await _reader.BrowseAsync("ai_signals", orderByColumn: "id", descending: true, limit: 1);

        Assert.Null(result.Rows[0]["name"]);
        Assert.Null(result.Rows[0]["confidence"]);
    }

    [Fact]
    public async Task Paging_uses_limit_and_offset()
    {
        var page = await _reader.BrowseAsync("ai_signals", limit: 1, offset: 1, orderByColumn: "id");

        Assert.Single(page.Rows);
        Assert.Equal("sig-2", page.Rows[0]["id"]);
    }

    [Fact]
    public async Task Views_are_browsable_too()
    {
        var result = await _reader.BrowseAsync("ai_components_v");

        Assert.Equal(new[] { "id", "name", "confidence" }, result.Columns);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public async Task Counts_rows()
    {
        Assert.Equal((int?)3, await _reader.CountAsync("ai_signals"));
        Assert.Equal((int?)1, await _reader.CountAsync("ai_scans"));
    }

    [Fact]
    public async Task Table_names_are_matched_case_insensitively()
    {
        Assert.Equal((int?)3, await _reader.CountAsync("AI_SIGNALS"));
    }

    // ---- Row counts must stay cheap on the live 4.5 GB database (4.9 M-row ai_signals, a 14-20 s aggregate view). ----

    [Fact]
    public async Task Small_tables_get_an_exact_count()
    {
        var count = await _reader.CountRowsAsync("ai_signals");

        Assert.Equal(new InventoryRowCount(3, InventoryCountKind.Exact), count);
        Assert.True(count.IsExact);
        Assert.Equal("3", count.ToDisplayString());
    }

    [Fact]
    public async Task Views_are_never_counted()
    {
        // COUNT(*) on a view evaluates its whole query; for ai_components_v that is 14 s on the
        // live database. Not counting is the only safe generic answer.
        var count = await _reader.CountRowsAsync("ai_components_v");

        Assert.Equal(InventoryRowCount.NotCounted, count);
        Assert.False(count.IsExact);
        Assert.Null(await _reader.CountAsync("ai_components_v"));
    }

    [Fact]
    public async Task Sparse_rowids_do_not_inflate_a_small_tables_count()
    {
        // Three rows but a rowid span of a million: the capped COUNT(*) sees three, so the count
        // is exact rather than the rowid-span upper bound.
        Execute("CREATE TABLE sparse (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO sparse VALUES (1, 'a'), (2, 'b'), (1000000, 'c');");

        Assert.Equal(new InventoryRowCount(3, InventoryCountKind.Exact), await _reader.CountRowsAsync("sparse"));
    }

    [Fact]
    public async Task Tables_above_the_limit_get_a_labelled_estimate_not_a_full_count()
    {
        var rows = InventoryReader.ExactCountRowLimit + 10_000;
        Execute("CREATE TABLE big (id INTEGER PRIMARY KEY, v TEXT); " + Fill("big", rows));

        var count = await _reader.CountRowsAsync("big");

        Assert.Equal(InventoryCountKind.Approximate, count.Kind);
        Assert.Equal(rows, count.Count);
        Assert.False(count.IsExact);
        Assert.Equal("~260,000", count.ToDisplayString());

        // The int-returning API must not present an estimate as if it were exact.
        Assert.Null(await _reader.CountAsync("big"));
    }

    [Fact]
    public async Task Without_rowid_tables_above_the_limit_report_a_lower_bound()
    {
        // No rowid to take a span from, so the honest answer is "at least the cap".
        Execute("CREATE TABLE wr (k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID; " + Fill("wr", InventoryReader.ExactCountRowLimit + 1));

        var count = await _reader.CountRowsAsync("wr");

        Assert.Equal(new InventoryRowCount(InventoryReader.ExactCountRowLimit, InventoryCountKind.AtLeast), count);
        Assert.Equal("250,000+", count.ToDisplayString());
    }

    [Fact]
    public void Count_display_strings_label_their_precision()
    {
        Assert.Equal("1,234", new InventoryRowCount(1234, InventoryCountKind.Exact).ToDisplayString());
        Assert.Equal("~4,903,572", new InventoryRowCount(4903572, InventoryCountKind.Approximate).ToDisplayString());
        Assert.Equal("250,000+", new InventoryRowCount(250000, InventoryCountKind.AtLeast).ToDisplayString());
        Assert.Equal("n/a", InventoryRowCount.NotCounted.ToDisplayString());
    }

    [Fact]
    public async Task Counting_an_unknown_table_is_rejected_not_interpolated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.CountRowsAsync("ai_signals; DROP TABLE ai_scans"));
    }

    [Fact]
    public async Task Unknown_tables_are_rejected_rather_than_interpolated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.CountAsync("ai_signals; DROP TABLE ai_scans"));
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.BrowseAsync("no_such_table"));

        // The injection attempt did nothing.
        Assert.Equal((int?)1, await _reader.CountAsync("ai_scans"));
    }

    [Fact]
    public async Task Unknown_order_by_columns_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _reader.BrowseAsync("ai_signals", orderByColumn: "id; DROP TABLE ai_scans"));
    }

    [Fact]
    public void Connection_string_is_read_only()
    {
        Assert.False(new InventoryReader(@"C:\nope\inventory.db").Exists);
    }

    // ---- Scan-scoped reads: a layout without scan columns is "unsupported" (null), never an exception. ----

    [Fact]
    public async Task Scan_scoped_reads_report_an_unsupported_layout_as_null()
    {
        // This fixture's ai_scans has (id, started_at) and its ai_signals has no scan_id — the shape
        // of a release the rollup was not written for. The panel needs to tell that apart from
        // "no scans yet", so it is null, not an empty result and not a "no such column" error.
        Assert.Null(await _reader.FindLatestFullScanAsync());
        Assert.Null(await _reader.GetLatestComponentsAsync());
        Assert.Null(await _reader.GetLatestSignalsAsync());
    }

    // ---- Timestamps: Go's time.Time.String(), which DateTimeOffset.Parse rejects. ----

    [Theory]
    [InlineData("2026-09-29 03:25:46.6124924 +0000 UTC", "2026-09-29T03:25:46.6124924Z")]
    [InlineData("2026-07-01 12:00:00 +0000 UTC", "2026-07-01T12:00:00Z")]
    [InlineData("2026-07-28 21:33:39.258790812 +0000 UTC", "2026-07-28T21:33:39.2587908Z")]
    [InlineData("2026-07-28 21:33:39.5 -0700 PDT", "2026-07-29T04:33:39.5Z")]
    [InlineData("2026-07-28 12:00:00 +0000 UTC m=+12.5", "2026-07-28T12:00:00Z")]
    [InlineData("2026-07-28 12:00:00", "2026-07-28T12:00:00Z")]
    [InlineData("2026-07-28T12:00:00Z", "2026-07-28T12:00:00Z")]
    [InlineData("2026-07-28T12:00:00.1234567+02:00", "2026-07-28T10:00:00.1234567Z")]
    public void Timestamps_in_the_shapes_the_sidecar_writes_parse_to_utc(string raw, string expectedUtc)
    {
        Assert.True(InventoryTimestamps.TryParse(raw, out var parsed));

        var expected = DateTimeOffset.Parse(
            expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.Equal(expected, parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a timestamp")]
    [InlineData("2026-13-45 99:99:99 +0000 UTC")]
    public void Anything_else_is_not_a_timestamp(string raw)
    {
        Assert.False(InventoryTimestamps.TryParse(raw, out _));
    }

    [Fact]
    public void A_null_timestamp_is_not_a_timestamp()
    {
        Assert.False(InventoryTimestamps.TryParse(null, out _));
    }

    /// <summary>Runs setup SQL through a separate, writable connection (the reader under test is read-only).</summary>
    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Bulk-inserts <paramref name="rows"/> rows (1..rows, 'x') into a two-column table.</summary>
    private static string Fill(string table, long rows) =>
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < "
        + rows.ToString(CultureInfo.InvariantCulture)
        + ") INSERT INTO " + table + " SELECT i, 'x' FROM n;";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}

/// <summary>
/// The scan-scoped reads (<see cref="InventoryReader.GetLatestComponentsAsync"/> and friends) against
/// a database with the <em>real</em> 0.8.10 layout — <c>ai_scans</c>, <c>ai_signals</c>,
/// <c>ai_confidence_snapshots</c>, their indexes and the <c>ai_components_v</c> view, copied from
/// <c>sqlite_master</c> of a live install — filled with synthetic rows. The hazard they exist for is
/// invisible on a handful of rows (the view is fine there) and a 14-20 s, history-inflated stall on
/// the live 4.9 M-row table, so the assertions are on <em>which scan</em> feeds each number and on the
/// query plan.
/// </summary>
public class InventoryScanReaderTests : IDisposable
{
    private const string Schema = """
        CREATE TABLE ai_scans (
            scan_id TEXT PRIMARY KEY,
            scanned_at DATETIME NOT NULL,
            duration_ms INTEGER NOT NULL,
            source TEXT NOT NULL,
            privacy_mode TEXT NOT NULL,
            result TEXT NOT NULL,
            total_signals INTEGER NOT NULL,
            active_signals INTEGER NOT NULL,
            files_scanned INTEGER NOT NULL
        );
        CREATE INDEX idx_ai_scans_scanned_at ON ai_scans(scanned_at);
        CREATE TABLE ai_signals (
            scan_id TEXT NOT NULL REFERENCES ai_scans(scan_id) ON DELETE CASCADE,
            fingerprint TEXT NOT NULL,
            signal_id TEXT NOT NULL,
            signature_id TEXT NOT NULL,
            name TEXT NOT NULL,
            vendor TEXT NOT NULL,
            product TEXT NOT NULL,
            category TEXT NOT NULL,
            detector TEXT NOT NULL,
            state TEXT NOT NULL,
            confidence REAL NOT NULL,
            component_ecosystem TEXT,
            component_name TEXT,
            component_framework TEXT,
            component_version TEXT,
            last_seen DATETIME NOT NULL,
            last_active_at DATETIME,
            evidence_json TEXT,
            runtime_json TEXT, model_json TEXT,
            PRIMARY KEY (scan_id, fingerprint)
        );
        CREATE INDEX idx_ai_signals_component ON ai_signals(component_ecosystem, component_name);
        CREATE INDEX idx_ai_signals_signature_id ON ai_signals(signature_id);
        CREATE TABLE ai_confidence_snapshots (
            scan_id TEXT NOT NULL REFERENCES ai_scans(scan_id) ON DELETE CASCADE,
            ecosystem TEXT NOT NULL,
            name TEXT NOT NULL,
            identity_score REAL NOT NULL,
            identity_band TEXT NOT NULL,
            presence_score REAL NOT NULL,
            presence_band TEXT NOT NULL,
            policy_version INTEGER NOT NULL,
            detectors TEXT,
            factors_json TEXT,
            PRIMARY KEY (scan_id, ecosystem, name)
        );
        CREATE VIEW ai_components_v AS
            SELECT
                s.component_ecosystem AS ecosystem,
                s.component_name      AS name,
                MAX(s.component_framework)         AS framework,
                MAX(s.component_version)           AS version,
                MAX(s.vendor)                      AS vendor,
                COUNT(*)                           AS install_count,
                MAX(s.last_seen)                   AS last_seen,
                MAX(s.last_active_at)              AS last_active_at,
                MAX(c.identity_score)              AS identity_score,
                MAX(c.identity_band)               AS identity_band,
                MAX(c.presence_score)              AS presence_score,
                MAX(c.presence_band)               AS presence_band,
                MAX(c.policy_version)              AS policy_version
            FROM ai_signals s
            LEFT JOIN ai_confidence_snapshots c
                ON LOWER(c.ecosystem) = LOWER(s.component_ecosystem)
                AND LOWER(c.name)     = LOWER(s.component_name)
                AND c.scan_id         = s.scan_id
            WHERE s.component_ecosystem IS NOT NULL
                AND s.component_name      IS NOT NULL
            GROUP BY LOWER(s.component_ecosystem), LOWER(s.component_name);
        """;

    private readonly TempDirectory _directory = new("dcw-inventory-scans");

    /// <summary>
    /// Four scans, newest last. The newest <em>clean full</em> scan is <c>scan-2</c> (a
    /// <c>startup</c> pass): <c>scan-3</c> is newer but <c>partial</c>, and <c>scan-4</c> is newer
    /// still but a lightweight <c>process</c> pass. <c>npm/openai</c> is seen 3, 2, 1 and 5 times by
    /// them, so the all-history view reports 11 while the chosen scan saw it twice.
    /// </summary>
    private InventoryReader MultiScanDatabase()
    {
        var path = _directory.File("multi.db");
        using (var seed = new Seeder(path))
        {
            seed.Scan("scan-1", "2026-07-01 12:00:00 +0000 UTC", "scheduled", "ok");
            seed.Scan("scan-2", "2026-07-02 12:00:00.1234567 +0000 UTC", "startup", "ok");
            seed.Scan("scan-3", "2026-07-03 12:00:00.5 +0000 UTC", "scheduled", "partial");
            seed.Scan("scan-4", "2026-07-03 12:01:00.25 +0000 UTC", "process", "ok");

            for (var i = 0; i < 3; i++)
            {
                seed.Signal("scan-1", "a" + i.ToString(CultureInfo.InvariantCulture), "npm", "openai", "node", "3.0.0", "OpenAI");
            }

            seed.Signal("scan-2", "b1", "npm", "openai", "node", "4.0.0", "OpenAI", "2026-07-02 11:59:00 +0000 UTC");
            seed.Signal("scan-2", "b2", "npm", "openai", null, "4.1.0", "OpenAI", "2026-07-02 12:00:00.1234567 +0000 UTC");
            seed.Signal("scan-2", "b3", "pypi", "anthropic", null, "0.30.0", "Anthropic");
            seed.Signal("scan-2", "b4"); // a signal with no component block: not part of the rollup

            seed.Signal("scan-3", "c1", "npm", "openai", "node", "5.0.0", "OpenAI");

            for (var i = 0; i < 5; i++)
            {
                seed.Signal("scan-4", "d" + i.ToString(CultureInfo.InvariantCulture), "npm", "openai", "node", "6.0.0", "OpenAI");
            }

            // Stored 'NPM'/'OpenAI' against signals 'npm'/'openai': the view joins on LOWER().
            seed.Snapshot("scan-2", "NPM", "OpenAI", 0.95, "very_high", 0.9, "high");
            seed.Snapshot("scan-4", "npm", "openai", 0.5, "medium", 0.5, "medium");
        }

        return new InventoryReader(path);
    }

    [Fact]
    public async Task Components_are_rolled_up_from_the_chosen_scan_not_from_all_history()
    {
        var reader = MultiScanDatabase();

        var result = await reader.GetLatestComponentsAsync();

        Assert.NotNull(result);
        var rows = result!.Rows.Rows.OrderBy(row => Text(row["name"]), StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "anthropic", "openai" }, rows.Select(row => Text(row["name"])));

        var openai = rows[1];
        Assert.Equal("npm", Text(openai["ecosystem"]));
        Assert.Equal(2L, Convert.ToInt64(openai["install_count"], CultureInfo.InvariantCulture));
        Assert.Equal("OpenAI", Text(openai["vendor"]));
        Assert.Equal("4.1.0", Text(openai["version"]));
        Assert.Equal("node", Text(openai["framework"]));
        Assert.Equal(0.95, Convert.ToDouble(openai["identity_score"], CultureInfo.InvariantCulture), 3);
        Assert.Equal("very_high", Text(openai["identity_band"]));
        Assert.Equal("high", Text(openai["presence_band"]));

        var anthropic = rows[0];
        Assert.Equal(1L, Convert.ToInt64(anthropic["install_count"], CultureInfo.InvariantCulture));
        Assert.Null(anthropic["identity_score"]);

        // The view this replaces adds up every scan: the number the panel used to show.
        var view = await reader.BrowseAsync("ai_components_v");
        var inflated = view.Rows.Single(row => Text(row["name"]) == "openai");
        Assert.Equal(11L, Convert.ToInt64(inflated["install_count"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_newest_clean_full_scan_wins_over_newer_partial_and_process_scans()
    {
        var reader = MultiScanDatabase();

        var scan = await reader.FindLatestFullScanAsync();

        Assert.NotNull(scan);
        Assert.Equal("scan-2", scan!.ScanId);
        Assert.Equal("startup", scan.Source); // startup counts as a full pass
        Assert.Equal("ok", scan.Result);
        Assert.Equal(InventoryScanBasis.NewestFullScan, scan.Basis);
        Assert.True(scan.IsFullScan);
        Assert.Equal("2026-07-02 12:00:00.1234567 +0000 UTC", scan.ScannedAtRaw);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), scan.ScannedAt);

        var components = await reader.GetLatestComponentsAsync();
        Assert.Equal("scan-2", components!.Scan!.ScanId);
    }

    [Fact]
    public async Task With_no_clean_full_scan_the_newest_scan_of_any_kind_is_used_and_says_so()
    {
        var path = _directory.File("no-full.db");
        using (var seed = new Seeder(path))
        {
            seed.Scan("old", "2026-07-01 12:00:00 +0000 UTC", "process", "ok");
            seed.Scan("partial-a", "2026-07-03 12:00:00 +0000 UTC", "scheduled", "partial");

            // One microsecond newer, and stored without the fraction the other lacks: text order
            // still has to be time order ("…00 +0000" sorts before "…00.000001 +0000").
            seed.Scan("partial-b", "2026-07-03 12:00:00.000001 +0000 UTC", "scheduled", "partial");
            seed.Signal("partial-b", "p1", "npm", "openai", "node", "1.0.0", "OpenAI");
        }

        var reader = new InventoryReader(path);

        var scan = await reader.FindLatestFullScanAsync();

        Assert.NotNull(scan);
        Assert.Equal("partial-b", scan!.ScanId);
        Assert.Equal(InventoryScanBasis.NewestAnyScan, scan.Basis);
        Assert.False(scan.IsFullScan);

        var components = await reader.GetLatestComponentsAsync();
        Assert.Equal("partial-b", components!.Scan!.ScanId);
        Assert.Single(components.Rows.Rows);
    }

    [Fact]
    public async Task A_database_with_the_layout_but_no_scans_yet_is_empty_not_unsupported()
    {
        var path = _directory.File("empty.db");
        using (var seed = new Seeder(path))
        {
            // Schema only.
        }

        var reader = new InventoryReader(path);

        Assert.Null(await reader.FindLatestFullScanAsync());

        var components = await reader.GetLatestComponentsAsync();
        Assert.NotNull(components);
        Assert.Null(components!.Scan);
        Assert.Empty(components.Rows.Rows);

        var signals = await reader.GetLatestSignalsAsync();
        Assert.NotNull(signals);
        Assert.Null(signals!.Scan);
        Assert.Empty(signals.Rows.Rows);
    }

    [Fact]
    public async Task The_rollup_equals_the_view_when_the_database_holds_one_scan()
    {
        // The rollup is the view's SELECT with one extra WHERE term. With a single scan there is
        // no history to leave out, so the two must agree column for column and value for value —
        // if DefenseClaw ever changes the view and this query is not updated, this fails.
        var path = _directory.File("single.db");
        using (var seed = new Seeder(path))
        {
            seed.Scan("only", "2026-07-02 12:00:00 +0000 UTC", "scheduled", "ok");
            seed.Signal("only", "x1", "npm", "openai", "node", "4.0.0", "OpenAI");
            seed.Signal("only", "x2", "npm", "openai", null, "4.1.0", "OpenAI");
            seed.Signal("only", "x3", "pypi", "anthropic", null, "0.30.0", "Anthropic");
            seed.Signal("only", "x4", "npm", "@scope/pkg", "deno", "1.0.0", "Acme");
            seed.Signal("only", "x5");
            seed.Snapshot("only", "NPM", "OpenAI", 0.95, "very_high", 0.9, "high");
            seed.Snapshot("only", "PyPI", "Anthropic", 0.6, "medium", 0.4, "low");
        }

        var reader = new InventoryReader(path);

        var rollup = (await reader.GetLatestComponentsAsync())!.Rows;
        var view = await reader.BrowseAsync("ai_components_v");

        Assert.Equal(3, view.Rows.Count);
        Assert.Equal(view.Columns, rollup.Columns);
        Assert.Equal(Render(view), Render(rollup));
    }

    [Fact]
    public async Task The_rollup_seeks_the_scan_in_the_primary_key_index_of_both_tables()
    {
        // Without the scan filter both tables are scanned in full (4.9 M rows live). The filter
        // must stay a seek on (scan_id, ...) — a plain "SCAN s" here is the 14-20 s stall back.
        var reader = MultiScanDatabase();

        var plan = await reader.ExplainComponentsRollupAsync("scan-2");

        Assert.Contains(plan, line =>
            line.StartsWith("SEARCH s ", StringComparison.Ordinal) && line.Contains("scan_id=?", StringComparison.Ordinal));
        Assert.Contains(plan, line =>
            line.StartsWith("SEARCH c ", StringComparison.Ordinal) && line.Contains("scan_id=?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Latest_signals_are_the_chosen_scans_rows_not_the_oldest_rows_in_the_table()
    {
        var reader = MultiScanDatabase();

        var signals = await reader.GetLatestSignalsAsync();

        Assert.NotNull(signals);
        Assert.Equal("scan-2", signals!.Scan!.ScanId);
        Assert.Equal(
            new[] { "b1", "b2", "b3", "b4" },
            signals.Rows.Rows.Select(row => Text(row["fingerprint"])).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(signals.Rows.Rows, row => Assert.Equal("scan-2", Text(row["scan_id"])));

        // What the AI Discovery fallback used to read: no ORDER BY, so the first rows by rowid.
        var oldest = await reader.BrowseAsync("ai_signals", limit: 2);
        Assert.All(oldest.Rows, row => Assert.Equal("scan-1", Text(row["scan_id"])));

        var limited = await reader.GetLatestSignalsAsync(limit: 2);
        Assert.Equal(2, limited!.Rows.Rows.Count);
    }

    [Fact]
    public async Task Every_query_method_returns_a_pending_task_instead_of_running_the_query_on_the_callers_thread()
    {
        // Microsoft.Data.Sqlite's "async" calls complete synchronously, so without the reader's own hop
        // to the pool each of these would run on the caller's thread. Deterministic, no sleeps: an
        // EXCLUSIVE lock on this rollback-journal database makes every read wait in SQLite's busy
        // handler (10 s). An inline method would only return after that, already failed; an offloaded
        // one returns at once with a task that cannot finish until the lock is released below.
        var reader = MultiScanDatabase();
        using var lockHolder = new SqliteConnection($"Data Source={_directory.File("multi.db")}");
        lockHolder.Open();
        RunSql(lockHolder, "BEGIN EXCLUSIVE");

        var calls = new (string Name, Func<Task> Call)[]
        {
            (nameof(InventoryReader.ListTablesAsync), () => reader.ListTablesAsync()),
            (nameof(InventoryReader.ListColumnsAsync), () => reader.ListColumnsAsync("ai_signals")),
            (nameof(InventoryReader.CountAsync), () => reader.CountAsync("ai_signals")),
            (nameof(InventoryReader.CountRowsAsync), () => reader.CountRowsAsync("ai_signals")),
            (nameof(InventoryReader.FindLatestFullScanAsync), () => reader.FindLatestFullScanAsync()),
            (nameof(InventoryReader.GetLatestComponentsAsync), () => reader.GetLatestComponentsAsync()),
            (nameof(InventoryReader.GetLatestSignalsAsync), () => reader.GetLatestSignalsAsync()),
            (nameof(InventoryReader.ExplainComponentsRollupAsync), () => reader.ExplainComponentsRollupAsync("scan-2")),
            (nameof(InventoryReader.BrowseAsync), () => reader.BrowseAsync("ai_signals", orderByColumn: "fingerprint")),
        };

        var started = new List<Task>();
        foreach (var (name, call) in calls)
        {
            var task = call();
            Assert.False(task.IsCompleted, $"{name} ran its query on the caller's thread (it returned only after the lock timed out).");
            started.Add(task);
        }

        RunSql(lockHolder, "ROLLBACK");

        await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_never_starts_the_query()
    {
        var reader = MultiScanDatabase();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CountRowsAsync("ai_signals", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetLatestComponentsAsync(cancelled.Token));
    }

    private static void RunSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>One line per row (every column, in order), sorted, so two result sets compare regardless of row order.</summary>
    private static IEnumerable<string> Render(InventoryRows rows) =>
        rows.Rows
            .Select(row => string.Join("|", rows.Columns.Select(column => Text(row[column]))))
            .OrderBy(line => line, StringComparer.Ordinal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }

    /// <summary>A writable handle that creates the real layout and inserts synthetic scans, signals and snapshots.</summary>
    private sealed class Seeder : IDisposable
    {
        private readonly SqliteConnection _connection;

        public Seeder(string path)
        {
            _connection = new SqliteConnection($"Data Source={path}");
            _connection.Open();
            Run(Schema);
        }

        public void Scan(string scanId, string scannedAt, string source, string result) =>
            Run(
                "INSERT INTO ai_scans VALUES ($id, $at, 100, $source, 'enhanced', $result, 1, 1, 0)",
                ("$id", scanId), ("$at", scannedAt), ("$source", source), ("$result", result));

        public void Signal(
            string scanId,
            string fingerprint,
            string? ecosystem = null,
            string? name = null,
            string? framework = null,
            string? version = null,
            string vendor = "Acme",
            string lastSeen = "2026-07-01 12:00:00 +0000 UTC") =>
            Run(
                """
                INSERT INTO ai_signals
                    (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector,
                     state, confidence, component_ecosystem, component_name, component_framework,
                     component_version, last_seen)
                VALUES
                    ($scan, $fingerprint, $signal, 'sig', $label, $vendor, 'Product', 'sdk', 'package_manifest',
                     'seen', 0.9, $ecosystem, $name, $framework, $version, $seen)
                """,
                ("$scan", scanId), ("$fingerprint", fingerprint), ("$signal", "sig-" + fingerprint),
                ("$label", name ?? "Thing"), ("$vendor", vendor), ("$ecosystem", ecosystem), ("$name", name),
                ("$framework", framework), ("$version", version), ("$seen", lastSeen));

        public void Snapshot(
            string scanId, string ecosystem, string name,
            double identityScore, string identityBand, double presenceScore, string presenceBand) =>
            Run(
                """
                INSERT INTO ai_confidence_snapshots
                    (scan_id, ecosystem, name, identity_score, identity_band, presence_score, presence_band, policy_version)
                VALUES ($scan, $ecosystem, $name, $identityScore, $identityBand, $presenceScore, $presenceBand, 1)
                """,
                ("$scan", scanId), ("$ecosystem", ecosystem), ("$name", name), ("$identityScore", identityScore),
                ("$identityBand", identityBand), ("$presenceScore", presenceScore), ("$presenceBand", presenceBand));

        public void Dispose() => _connection.Dispose();

        private void Run(string sql, params (string Name, object? Value)[] arguments)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in arguments)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }
}
