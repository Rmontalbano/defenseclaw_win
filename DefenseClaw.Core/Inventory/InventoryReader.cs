using System.Globalization;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Inventory;

/// <summary>A table or view discovered in the database.</summary>
public sealed record InventoryTable(string Name, string Type)
{
    public bool IsView => string.Equals(Type, "view", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One column, as reported by <c>PRAGMA table_info</c>.</summary>
public sealed record InventoryColumn(int Ordinal, string Name, string DeclaredType, bool NotNull, bool IsPrimaryKey);

/// <summary>A generic result set: ordered column names plus rows of boxed values.</summary>
public sealed record InventoryRows(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    /// <summary>No columns, no rows.</summary>
    public static InventoryRows Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<IReadOnlyDictionary<string, object?>>());
}

/// <summary>How much an <see cref="InventoryRowCount"/> can be trusted.</summary>
public enum InventoryCountKind
{
    /// <summary>A real <c>COUNT(*)</c>.</summary>
    Exact,

    /// <summary>
    /// An upper bound taken from the rowid span (<c>MAX(rowid) - MIN(rowid) + 1</c>). Exact
    /// unless rows have been deleted from the middle of the table; always at least the limit
    /// the exact count was capped at.
    /// </summary>
    Approximate,

    /// <summary>
    /// The table holds more than the exact-count limit and no rowid span was available (a
    /// <c>WITHOUT ROWID</c> table). <see cref="InventoryRowCount.Count"/> is that limit.
    /// </summary>
    AtLeast,

    /// <summary>Deliberately not counted — a view, whose row count costs a full evaluation.</summary>
    NotCounted,
}

/// <summary>A row count together with how it was obtained; see <see cref="InventoryCountKind"/>.</summary>
public readonly record struct InventoryRowCount(long Count, InventoryCountKind Kind)
{
    /// <summary>The "nothing to show" value for a view.</summary>
    public static InventoryRowCount NotCounted { get; } = new(0, InventoryCountKind.NotCounted);

    public bool IsExact => Kind == InventoryCountKind.Exact;

    /// <summary><c>1,234</c>, <c>~4,903,572</c>, <c>250,000+</c>, or <c>n/a</c>.</summary>
    public string ToDisplayString() => Kind switch
    {
        InventoryCountKind.Exact => Count.ToString("N0", CultureInfo.InvariantCulture),
        InventoryCountKind.Approximate => "~" + Count.ToString("N0", CultureInfo.InvariantCulture),
        InventoryCountKind.AtLeast => Count.ToString("N0", CultureInfo.InvariantCulture) + "+",
        _ => "n/a",
    };
}

/// <summary>
/// Read-only browser over <c>~/.defenseclaw/inventory.db</c>. Its schema is not pinned by
/// any published contract and changes between 0.8.x releases, so this discovers the
/// schema at runtime and offers generic browsing rather than baking in table shapes.
/// On the live 0.8.7 install it holds <c>ai_signals</c>, <c>ai_scans</c>,
/// <c>ai_confidence_snapshots</c>, <c>schema_version</c> and the <c>ai_components_v</c> view.
/// <para>
/// <b>Size and cost, measured on a live 0.8.10 install (4.47 GB main file).</b> The database is
/// almost entirely <c>ai_signals</c>: the table is 3.5 GB / 4.9 M rows (~690 B each, no
/// overflow pages) and its three indexes add 0.8 GB — 4.35 GB, 97% of the file.
/// <c>ai_confidence_snapshots</c> is 108 MB / 120 k rows and <c>ai_scans</c> 8.7 MB / 40 k
/// rows. It grows with every scan (a scan is recorded about once a minute and writes ~190
/// signal rows) and nothing prunes it. Two things follow for a reader that opens it on the UI
/// path:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>COUNT(*)</c> on <c>ai_signals</c> costs ~0.2 s warm and several seconds cold, and
/// <c>MAX(rowid)</c>/<c>MIN(rowid)</c> are O(log n) (0.1 ms) — but only as <em>separate</em>
/// statements; <c>SELECT MAX(rowid), MIN(rowid)</c> defeats the min/max optimisation and scans an
/// index (0.4 s warm, 3.6 s cold). <see cref="CountRowsAsync"/> is built on that.
/// </description></item>
/// <item><description>
/// The <c>ai_components_v</c> view is far worse: it aggregates <em>every</em> signal ever
/// recorded with a <c>LOWER()</c> join, so both <c>SELECT COUNT(*)</c> and
/// <c>SELECT * … LIMIT 200</c> take 14–20 s (the aggregate must finish before the first row).
/// The generic reader cannot make a view cheaper, so it never counts one
/// (<see cref="InventoryCountKind.NotCounted"/>); browsing one is the caller's decision and
/// costs what the view costs.
/// </description></item>
/// <item><description>
/// The view is also <em>wrong</em> for a dashboard, not just slow: it groups every signal of
/// every scan, so a component the latest scan saw twice reports <c>install_count</c> 102,128 (the
/// number of times any scan ever saw it). The same SELECT with <c>WHERE s.scan_id = &lt;one
/// scan&gt;</c> is a primary-key seek (0.4 ms, 2 ms for the whole read including the schema check;
/// 15 s for the view on the same database). <see cref="GetLatestComponentsAsync"/> is that
/// query, for the scan <see cref="FindLatestFullScanAsync"/> picks, and
/// <see cref="GetLatestSignalsAsync"/> is the matching read of the raw signals. Prefer those to
/// browsing <c>ai_components_v</c> / <c>ai_signals</c>: an un-ordered
/// <c>SELECT * FROM ai_signals LIMIT 5000</c> is the <em>oldest</em> 5,000 rows (rowid order),
/// from the first days the install ran.
/// </description></item>
/// <item><description>
/// <b>Threading: every query runs on the thread pool.</b> Microsoft.Data.Sqlite's <c>async</c>
/// methods complete synchronously (SQLite has no asynchronous I/O), so an <c>async</c> method that
/// awaits them never yields and runs entirely on its caller's thread — and this reader's callers are
/// view-models awaiting from the dispatcher, so a query froze the window for its whole duration
/// (15 s for the view aggregate, seconds for a cold count). Each public method therefore starts its
/// body with <see cref="ReaderOffload"/> (<c>Task.Run</c>) and returns a task that is really
/// pending; callers need no <c>Task.Run</c> of their own. Cancellation is observed between
/// statements and rows, never inside one (the 14-20 s view aggregate is a single
/// <c>sqlite3_step</c>), so cancelling a load abandons its result rather than stopping the query.
/// </description></item>
/// </list>
/// </summary>
public sealed class InventoryReader
{
    /// <summary>
    /// Tables with up to this many rows get an exact <c>COUNT(*)</c>; larger ones get an
    /// estimate. The exact count is capped at this many rows at the SQL level, so its cost is
    /// bounded (~50 ms on the live <c>ai_signals</c>) no matter how big the table is.
    /// </summary>
    public const long ExactCountRowLimit = 250_000;

    /// <summary>SQLITE_ERROR — the primary code behind "no such column/table" style failures.</summary>
    private const int SqliteErrorGeneric = 1;

    /// <summary>Default cap on the raw signals <see cref="GetLatestSignalsAsync"/> returns (a scan holds ~200).</summary>
    public const int DefaultSignalLimit = 5000;

    /// <summary>
    /// The <c>ai_scans.source</c> values that mean a complete inventory pass. Measured on the live
    /// 0.8.10 install (40,181 scans over 62 days): <c>process</c> is the lightweight per-minute
    /// pass (<c>files_scanned</c> = 0, 33,365 scans), <c>scheduled</c> is the file walk
    /// (<c>files_scanned</c> ≈ 1,000, about every five minutes; 6,800 scans, 7% of them
    /// <c>result = 'partial'</c>) and <c>startup</c> is the same walk when the sidecar starts
    /// (16 scans). Interpolated into SQL, so these must stay compile-time constants.
    /// </summary>
    private const string FullScanSources = "'scheduled', 'startup'";

    private const string ScanSelect = "SELECT scan_id, scanned_at, source, result FROM ai_scans";

    /// <summary>
    /// The columns each query in this file needs beyond what the generic browser assumes. Checked
    /// with <c>PRAGMA table_info</c> before any scan-scoped query runs, so a layout that lacks them
    /// (an older or newer release) is reported as "unsupported" instead of throwing.
    /// </summary>
    private static readonly string[] ScanColumns = { "scan_id", "scanned_at", "source", "result" };

    private static readonly string[] SignalRollupColumns =
    {
        "scan_id", "component_ecosystem", "component_name", "component_framework",
        "component_version", "vendor", "last_seen", "last_active_at",
    };

    private static readonly string[] SnapshotRollupColumns =
    {
        "scan_id", "ecosystem", "name", "identity_score", "identity_band",
        "presence_score", "presence_band", "policy_version",
    };

    /// <summary>
    /// <c>ai_components_v</c> for one scan. The SELECT list, aggregates, LEFT JOIN and GROUP BY are
    /// the view's own definition, copied from <c>sqlite_master</c> of a live 0.8.10 install; the
    /// one addition is <c>s.scan_id = $scan_id</c> in the WHERE clause, which turns a scan of every
    /// signal ever recorded (4.9 M rows, 14-20 s) into a seek on
    /// <c>sqlite_autoindex_ai_signals_1 (scan_id, fingerprint)</c> plus one on
    /// <c>ai_confidence_snapshots (scan_id, ecosystem, name)</c> (0.4 ms). A test builds a database
    /// with exactly one scan and requires this query to equal the view row for row, so the two
    /// cannot drift apart. <c>ecosystem</c> is a bare column under <c>GROUP BY LOWER(...)</c>,
    /// exactly as in the view — SQLite takes it from an arbitrary row of the group.
    /// </summary>
    private const string ComponentsRollupSql = """
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
        WHERE s.scan_id = $scan_id
            AND s.component_ecosystem IS NOT NULL
            AND s.component_name      IS NOT NULL
        GROUP BY LOWER(s.component_ecosystem), LOWER(s.component_name)
        """;

    private readonly string _connectionString;

    public InventoryReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;

        // Same read-only, explicit-busy-timeout policy as audit.db; see AuditReader.
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>Discovers tables and views, excluding SQLite internals.</summary>
    public Task<IReadOnlyList<InventoryTable>> ListTablesAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ListTablesCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<InventoryTable>> ListTablesCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        var tables = new List<InventoryTable>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new InventoryTable(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    public Task<IReadOnlyList<InventoryColumn>> ListColumnsAsync(string table, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ListColumnsCoreAsync(table, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<InventoryColumn>> ListColumnsCoreAsync(string table, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTableNameAsync(connection, table, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- quoted identifier: resolved is a name read back from sqlite_master (ResolveTableNameAsync throws for anything else), then Quote()d
        command.CommandText = $"PRAGMA table_info({Quote(resolved)})";

        var columns = new List<InventoryColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new InventoryColumn(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(5) != 0));
        }

        return columns;
    }

    /// <summary>
    /// The exact row count, or <c>null</c> when an exact count is not cheap: a table above
    /// <see cref="ExactCountRowLimit"/> rows, or any view. <c>null</c> is never "zero" — it
    /// means "not counted". Use <see cref="CountRowsAsync"/> to get the estimate and its
    /// precision (<c>~4,903,572</c>) instead of nothing.
    /// <para>
    /// This used to run an unbounded <c>SELECT COUNT(*)</c>, which on the live database is
    /// 0.2 s (warm) to several seconds (cold) for <c>ai_signals</c> and 14 s for the
    /// <c>ai_components_v</c> view — per panel open.
    /// </para>
    /// </summary>
    public async Task<int?> CountAsync(string table, CancellationToken cancellationToken = default)
    {
        var count = await CountRowsAsync(table, cancellationToken).ConfigureAwait(false);
        return count.IsExact ? (int)Math.Min(count.Count, int.MaxValue) : null;
    }

    /// <summary>
    /// A row count whose cost is bounded regardless of table size, with the precision it was
    /// obtained at (<see cref="InventoryRowCount.Kind"/>):
    /// <list type="number">
    /// <item><description>Views are never counted — <see cref="InventoryRowCount.NotCounted"/>.</description></item>
    /// <item><description>
    /// Otherwise <c>SELECT COUNT(*) FROM (SELECT 1 FROM t LIMIT limit+1)</c>: an exact count that
    /// stops after <see cref="ExactCountRowLimit"/> + 1 rows (≤ ~50 ms).
    /// </description></item>
    /// <item><description>
    /// If that hit the cap, the table is big: report <c>MAX(rowid) - MIN(rowid) + 1</c> as
    /// <see cref="InventoryCountKind.Approximate"/> (two O(log n) probes, ~0.1 ms; an upper
    /// bound, exact when nothing was deleted — on the live <c>ai_signals</c> it equals the
    /// real count to within the rows a scan added while measuring). A <c>WITHOUT ROWID</c>
    /// table has no rowid; that falls back to <see cref="InventoryCountKind.AtLeast"/>.
    /// </description></item>
    /// </list>
    /// <c>sqlite_stat1</c> is deliberately not used: it is only written by <c>ANALYZE</c>, which the
    /// gateway never runs (the live file has no <c>sqlite_stat1</c>), and it goes stale anyway.
    /// </summary>
    public Task<InventoryRowCount> CountRowsAsync(string table, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => CountRowsCoreAsync(table, cancellationToken), cancellationToken);

    private async Task<InventoryRowCount> CountRowsCoreAsync(string table, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveObjectAsync(connection, table, cancellationToken).ConfigureAwait(false);
        if (resolved.IsView)
        {
            return InventoryRowCount.NotCounted;
        }

        var quoted = Quote(resolved.Name);
        var capped = await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM (SELECT 1 FROM {quoted} LIMIT {(ExactCountRowLimit + 1).ToString(CultureInfo.InvariantCulture)})",
            cancellationToken).ConfigureAwait(false) ?? 0;

        if (capped <= ExactCountRowLimit)
        {
            return new InventoryRowCount(capped, InventoryCountKind.Exact);
        }

        try
        {
            // Two statements on purpose: "SELECT MAX(rowid), MIN(rowid)" is a scan, these are seeks.
            var max = await ScalarAsync(connection, $"SELECT MAX(rowid) FROM {quoted}", cancellationToken).ConfigureAwait(false);
            var min = await ScalarAsync(connection, $"SELECT MIN(rowid) FROM {quoted}", cancellationToken).ConfigureAwait(false);
            if (max is { } high && min is { } low)
            {
                long span;
                try
                {
                    span = checked(high - low + 1);
                }
                catch (OverflowException)
                {
                    span = long.MaxValue;
                }

                // The rowid span can only overshoot the true count; it can never undershoot the cap.
                return new InventoryRowCount(Math.Max(span, capped), InventoryCountKind.Approximate);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteErrorGeneric)
        {
            // "no such column: rowid" — a WITHOUT ROWID table. Fall through to the lower bound.
        }

        return new InventoryRowCount(ExactCountRowLimit, InventoryCountKind.AtLeast);
    }

    /// <summary>
    /// The scan every "what does this machine have <em>now</em>" read is scoped to: the newest scan
    /// of a full inventory pass (<c>source</c> <c>scheduled</c> or <c>startup</c>) that finished
    /// <c>result = 'ok'</c> — <see cref="InventoryScanBasis.NewestFullScan"/> — or, when none exists,
    /// the newest scan of any kind (<see cref="InventoryScanBasis.NewestAnyScan"/>).
    /// <para>
    /// <c>null</c> when this database has no <c>ai_scans</c> table with the columns needed (an older
    /// or newer layout) <em>or</em> has not recorded a scan yet; use <see cref="GetLatestComponentsAsync"/>
    /// when the two need telling apart. Newest is decided by <c>scanned_at</c>, which the sidecar
    /// writes as Go's fixed-shape UTC string, so text order is time order and
    /// <c>idx_ai_scans_scanned_at</c> serves it: a backwards walk that stops at the first match
    /// (<c>0.1 ms</c> — the newest full scan is never more than a few scans back; the no-match
    /// fallback reads the newest one only).
    /// </para>
    /// <para>
    /// On the live 0.8.10 install every kind persists the sidecar's whole signal set (about 190
    /// rows), so the <c>process</c> and <c>scheduled</c> rollups agree today. The full-scan
    /// preference guards the layouts where a lightweight pass persists only what it touched,
    /// and a <c>partial</c> file walk only what it reached, either of which would make a dashboard
    /// of that scan look like components had disappeared.
    /// </para>
    /// </summary>
    public Task<InventoryScan?> FindLatestFullScanAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => FindLatestFullScanCoreAsync(cancellationToken), cancellationToken);

    private async Task<InventoryScan?> FindLatestFullScanCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasColumnsAsync(connection, "ai_scans", ScanColumns, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await ChooseScanAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The components rollup — what <c>ai_components_v</c> is meant to show — computed from the
    /// scan <see cref="FindLatestFullScanAsync"/> chooses instead of from every scan ever
    /// recorded (see <c>ComponentsRollupSql</c> for how it mirrors the view). Costs ~2 ms where
    /// browsing the view costs 14-20 s, and reports what the latest scan saw
    /// (<c>install_count</c> 2) rather than how often every scan combined saw it (102,128).
    /// <para>
    /// <c>null</c> means the database does not have the layout the rollup is built from
    /// (<c>ai_scans</c>, <c>ai_signals</c> and <c>ai_confidence_snapshots</c> with the columns the
    /// view reads); a database that has it but no scans yet returns
    /// <see cref="InventoryScanRows.Scan"/> = <c>null</c> and no rows.
    /// </para>
    /// </summary>
    public Task<InventoryScanRows?> GetLatestComponentsAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => GetLatestComponentsCoreAsync(cancellationToken), cancellationToken);

    private async Task<InventoryScanRows?> GetLatestComponentsCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasColumnsAsync(connection, "ai_scans", ScanColumns, cancellationToken).ConfigureAwait(false) ||
            !await HasColumnsAsync(connection, "ai_signals", SignalRollupColumns, cancellationToken).ConfigureAwait(false) ||
            !await HasColumnsAsync(connection, "ai_confidence_snapshots", SnapshotRollupColumns, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var scan = await ChooseScanAsync(connection, cancellationToken).ConfigureAwait(false);
        if (scan is null)
        {
            return new InventoryScanRows(null, InventoryRows.Empty);
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: ComponentsRollupSql is a const string; $scan_id is bound
        command.CommandText = ComponentsRollupSql;
        command.Parameters.AddWithValue("$scan_id", scan.ScanId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return new InventoryScanRows(scan, await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Every <c>ai_signals</c> row of the scan <see cref="FindLatestFullScanAsync"/> chooses — the
    /// current picture, not <c>SELECT * FROM ai_signals LIMIT n</c>, which has no ORDER BY and so
    /// returns the <em>oldest</em> n rows. A primary-key seek on <c>(scan_id, fingerprint)</c>:
    /// ~190 rows in ~0.1 ms. <c>null</c> when the layout lacks <c>ai_scans</c> or
    /// <c>ai_signals.scan_id</c>; an empty result with a <c>null</c> scan when nothing has been
    /// recorded yet.
    /// </summary>
    public Task<InventoryScanRows?> GetLatestSignalsAsync(
        int limit = DefaultSignalLimit,
        CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => GetLatestSignalsCoreAsync(limit, cancellationToken), cancellationToken);

    private async Task<InventoryScanRows?> GetLatestSignalsCoreAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasColumnsAsync(connection, "ai_scans", ScanColumns, cancellationToken).ConfigureAwait(false) ||
            !await HasColumnsAsync(connection, "ai_signals", new[] { "scan_id" }, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var scan = await ChooseScanAsync(connection, cancellationToken).ConfigureAwait(false);
        if (scan is null)
        {
            return new InventoryScanRows(null, InventoryRows.Empty);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ai_signals WHERE scan_id = $scan_id LIMIT $limit";
        command.Parameters.AddWithValue("$scan_id", scan.ScanId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return new InventoryScanRows(scan, await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines of the exact SQL <see cref="GetLatestComponentsAsync"/>
    /// runs for <paramref name="scanId"/>. Exists so a test can assert the scan filter is still a
    /// primary-key seek on both tables (<c>SEARCH s USING INDEX … (scan_id=?)</c>) — if it ever
    /// degrades to <c>SCAN s</c> the panel is back to a multi-second read of every signal and that
    /// is otherwise invisible on a ten-row fixture.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainComponentsRollupAsync(string scanId, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainComponentsRollupCoreAsync(scanId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> ExplainComponentsRollupCoreAsync(string scanId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(scanId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: the EXPLAIN prefix and the const ComponentsRollupSql; $scan_id is bound
        command.CommandText = "EXPLAIN QUERY PLAN " + ComponentsRollupSql;
        command.Parameters.AddWithValue("$scan_id", scanId);

        // Columns: id, parent, notused, detail.
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    /// <summary>
    /// Reads a page of rows. <paramref name="table"/> is validated against the discovered
    /// schema and quoted — it is never concatenated straight into SQL.
    /// <para>
    /// Cost is the caller's to manage: <c>LIMIT/OFFSET</c> on a base table is cheap at the
    /// front (2.8 ms for the first 200 <c>ai_signals</c> rows) and linear in <c>OFFSET</c>;
    /// <paramref name="orderByColumn"/> on an unindexed column of a multi-million-row table is a
    /// full sort; and any read of <c>ai_components_v</c> costs the full aggregate (14–20 s on the
    /// live database) whatever the limit.
    /// </para>
    /// </summary>
    public Task<InventoryRows> BrowseAsync(
        string table,
        int limit = 200,
        int offset = 0,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(
            () => BrowseCoreAsync(table, limit, offset, orderByColumn, descending, cancellationToken),
            cancellationToken);

    private async Task<InventoryRows> BrowseCoreAsync(
        string table,
        int limit,
        int offset,
        string? orderByColumn,
        bool descending,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTableNameAsync(connection, table, cancellationToken).ConfigureAwait(false);

        var orderBy = string.Empty;
        if (!string.IsNullOrWhiteSpace(orderByColumn))
        {
            // The Core variant: already on the pool thread, no second hop.
            var columns = await ListColumnsCoreAsync(resolved, cancellationToken).ConfigureAwait(false);
            var match = columns.FirstOrDefault(c => string.Equals(c.Name, orderByColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown column '{orderByColumn}' on '{resolved}'.", nameof(orderByColumn));
            orderBy = $" ORDER BY {Quote(match.Name)} {(descending ? "DESC" : "ASC")}";
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- quoted identifier: the table and the ORDER BY column are names read back from sqlite_master / PRAGMA table_info (anything else throws), Quote()d; the direction is ASC or DESC; $limit and $offset are bound
        command.CommandText = $"SELECT * FROM {Quote(resolved)}{orderBy} LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drains <paramref name="reader"/> into the generic column-name → boxed-value shape.</summary>
    private static async Task<InventoryRows> ReadRowsAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var names = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            names.Add(reader.GetName(i));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(names.Count, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new InventoryRows(names, rows);
    }

    /// <summary>
    /// Picks the scan per <see cref="FindLatestFullScanAsync"/>: newest clean full-pass scan, else
    /// newest scan of any kind, else <c>null</c>. The caller has already checked the columns.
    /// </summary>
    private static async Task<InventoryScan?> ChooseScanAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var full = await ReadScanAsync(
            connection,
            $"{ScanSelect} WHERE source IN ({FullScanSources}) AND result = 'ok' ORDER BY scanned_at DESC LIMIT 1",
            InventoryScanBasis.NewestFullScan,
            cancellationToken).ConfigureAwait(false);

        return full ?? await ReadScanAsync(
            connection,
            $"{ScanSelect} ORDER BY scanned_at DESC LIMIT 1",
            InventoryScanBasis.NewestAnyScan,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<InventoryScan?> ReadScanAsync(
        SqliteConnection connection,
        string sql,
        InventoryScanBasis basis,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: sql is only ever the const ScanSelect and FullScanSources composed in ChooseScanAsync; nothing is interpolated from data
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var raw = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
        return new InventoryScan(
            reader.GetString(0),
            raw,
            InventoryTimestamps.TryParse(raw, out var scannedAt) ? (DateTimeOffset?)scannedAt : null,
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            basis);
    }

    /// <summary>
    /// True when <paramref name="table"/> exists (as a table or view) and has every column in
    /// <paramref name="required"/>. <c>PRAGMA table_info</c> yields no rows for a missing object.
    /// </summary>
    private static async Task<bool> HasColumnsAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyCollection<string> required,
        CancellationToken cancellationToken)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- quoted identifier: table is one of the literals ai_scans, ai_signals or ai_confidence_snapshots at every call site, and is Quote()d anyway
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            present.Add(reader.GetString(1));
        }

        return required.All(present.Contains);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- quoted identifier: sql is only built in CountRowsCoreAsync, from a table name resolved against sqlite_master and Quote()d plus the const ExactCountRowLimit
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Maps a caller-supplied name onto a real object in sqlite_master. Anything that does
    /// not match exactly (case-insensitively) is rejected, which is what keeps
    /// interpolating the name into SQL safe.
    /// </summary>
    private static async Task<string> ResolveTableNameAsync(SqliteConnection connection, string table, CancellationToken cancellationToken) =>
        (await ResolveObjectAsync(connection, table, cancellationToken).ConfigureAwait(false)).Name;

    /// <summary>Same validation as <see cref="ResolveTableNameAsync"/>, keeping the object's type.</summary>
    private static async Task<InventoryTable> ResolveObjectAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' AND name = $name COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", table);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new InventoryTable(reader.GetString(0), reader.GetString(1));
        }

        throw new ArgumentException($"Unknown table or view '{table}'.", nameof(table));
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
