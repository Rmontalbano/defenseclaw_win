using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>The query shapes <see cref="AuditReader.ExplainAsync"/> can describe.</summary>
public enum AuditQueryShape
{
    /// <summary>The keyset page behind <see cref="AuditReader.QueryAsync"/>.</summary>
    Page,

    /// <summary>The total behind <see cref="AuditReader.CountAsync"/>.</summary>
    Count,

    /// <summary>The per-severity tiles behind <see cref="AuditReader.CountBySeverityAsync"/>.</summary>
    CountBySeverity,
}

/// <summary>
/// Read-only, parameterized access to <c>~/.defenseclaw/audit.db</c>.
/// <para>
/// The database is actively written by the gateway in WAL mode, so every connection is
/// opened with <c>Mode=ReadOnly</c>: we are a guest in someone else's file and must never
/// take a write lock. Verified against a live 0.8.7 install — a read-only connection
/// against a WAL database with a hot -wal/-shm pair opens fine.
/// </para>
/// <para>
/// <b>Read-only really does cover the "gateway is down" case.</b> The performance/durability
/// eval (finding #15) worried that a read-only connection cannot create or recover the
/// <c>-shm</c> file, and proposed a <c>Mode=ReadWrite</c> fallback. Measured with SQLite
/// 3.50.4 on Windows against synthetic WAL databases (Python <c>sqlite3</c>, same engine):
/// <c>mode=ro</c> opened and read every one of — a cleanly closed database with no -wal/-shm
/// at all (SQLite creates empty ones; only the <em>main file</em> is read-only), a writer
/// killed with <c>os._exit</c> leaving a hot -wal plus its -shm, the same with the -shm
/// deleted, the same with the -shm overwritten with garbage, a db+wal copy taken with the
/// writer still open, a live writer holding an uncommitted transaction, and a db file with the
/// read-only attribute. The one transient failure seen in a 20 s open/close/checkpoint
/// hammer (3,650 writer cycles, 954 reader opens, busy timeout 0) was a single
/// <c>SQLITE_BUSY_RECOVERY</c>. The <c>ReadWrite</c> fallback is therefore not adopted: it
/// would fix nothing measured, and it would let this app checkpoint and delete the
/// gateway's -wal on close — a write to a file we are only a guest in.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite already retries <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> until the
/// command timeout, so "no busy timeout" was not accurate either — the default was an
/// implicit 30 s. <see cref="BuildReadOnlyConnectionString"/> now sets it explicitly to
/// <see cref="BusyTimeoutSeconds"/> so a wedged writer surfaces as a panel error in seconds
/// rather than hanging a refresh for half a minute. Persistent failures (permissions on the
/// directory, a corrupt file) are not transient and are deliberately not retried.
/// </para>
/// <para>
/// <b>Query shape (measured on a live 2.7 GB / 275k-row database).</b> Every time-window and
/// ordering predicate is written against the raw, indexed
/// <c>retention_timestamp_unix_nano</c> column so SQLite can use
/// <c>idx_retention_audit_events_timestamp(retention_timestamp_unix_nano, id)</c>. Wrapping
/// that column in <c>COALESCE(…, strftime(…))</c> — as the reader used to — makes every
/// predicate non-sargable: the 24 h COUNT(*) was a 1.6 s full scan, the newest-50 page was
/// a 1.7 s scan plus a temp B-tree sort, and the 24 h GROUP BY severity was a 1.7 s full
/// index scan. See <see cref="KeyMode"/> for how rows that lack the column are handled.
/// </para>
/// <para>
/// <b>Connector filter: index or walk, decided per query.</b> Measured on the live 3.1 GB /
/// 289,769-row database (245,761 rows <c>claudecode</c>, 44,008 NULL). The database has no
/// <c>sqlite_stat1</c> (the gateway never runs <c>ANALYZE</c>), so for <c>e.connector = $c</c> the
/// planner assumes a handful of rows and always picks <c>idx_audit_connector</c>, then sorts the
/// matches for the ORDER BY. That is instant for a rare connector (0 ms for one with no rows) and
/// ruinous for the dominant one: 3.5 s for the newest page of a 24 h window, 5.9 s with no window,
/// 3.3 s for the 24 h COUNT, because every one of its 245 k rows is fetched to be tested and
/// sorted. Writing <c>+e.connector = $c</c> makes the term unusable for an index, so the planner
/// walks <c>idx_retention_audit_events_timestamp</c> in order and filters: 0.6-0.9 ms for that
/// page. But the walk only stops early if matches are dense — for a connector with no rows it reads
/// the whole window (0.65 s for 24 h, 2.1 s for the newest page with no window). Neither form is
/// right for both, so <see cref="IsCommonAsync"/> asks the database first with a bounded,
/// covering-index probe (<c>SELECT COUNT(*) FROM (SELECT 1 … WHERE connector = $c LIMIT N)</c>,
/// 0.4 ms even at N = 10,000): at least N matches means walk (<c>+e.connector</c>), fewer means seek
/// (<c>e.connector</c>). N = <see cref="DefaultCommonRowThreshold"/> comes from timing both
/// forms across connector-sized sets on the live database (<c>tool_name</c>, which has the same
/// single-column index, standing in for mid-sized connectors): the newest-page crossover is about
/// 2-3 k matching rows and a 24 h COUNT breaks even near 15 k, so at 10 k neither form costs more
/// than about 0.4 s on either side of the line.
/// </para>
/// <para>
/// <b>Bucket filter: the same probe, the same threshold, but only where a walk pays.</b>
/// <c>bucket IN (…)</c> has the same planner blind spot — it seeks
/// <c>idx_audit_bucket_timestamp (bucket=?)</c> and sorts every matching row for the ORDER BY. The
/// twelve real buckets (8 to 156 k rows) are a ready-made sweep. The newest page: 1.3 s (24 h) /
/// 1.9 s (all time) for <c>telemetry.ingest</c>, 0.66 / 0.97 s for <c>guardrail.evaluation</c>,
/// 0.6 / 0.86 s for <c>tool.activity</c>, against 1-5 ms walking the retention index; about even at
/// 5 k rows and the index is better below that (a walk over-reads, 0.13 s against 0.05 s at 2.4 k;
/// 1.7 s against 0.001 s for a 76-row bucket). But an aggregate is a different trade
/// (<see cref="WalkPays"/>): with no lower bound a walk reads the whole table where the index reads
/// only the bucket, so all-time severity tiles are <em>slower</em> walking (1.4 s against 0.29 s for
/// <c>tool.activity</c>, 1.5 s against 0.77 s for <c>telemetry.ingest</c>) and an all-time COUNT is
/// answered from the covering index in milliseconds. The walk is therefore used for a common
/// connector/bucket only for the newest page, and for COUNT/tiles only when the query has a
/// <see cref="AuditQuery.From"/> bound (24 h COUNT/tiles, <c>telemetry.ingest</c>: 1.4 s and 1.2 s on
/// the index, 0.35-0.39 s walking).
/// </para>
/// <para>
/// <b>Severity: never <c>UPPER(severity)</c>.</b> The severity of a row is compared and grouped on the
/// raw column, so <c>idx_audit_severity_timestamp (severity, timestamp)</c> stays usable. Wrapping the
/// column in <c>UPPER()</c> — as the reader used to, to fold case — hid it from that index: the 24 h
/// severity tiles the Overview refreshes every 60 s fetched every one of the window's ~145 k rows from
/// the 5.6 GB table just to read one column (0.7-0.9 s, 614 MB), and the Audit panel's "minimum
/// severity" filter cost the same. The case folding still happens, but on the handful of distinct
/// spellings instead of on every row: <see cref="DetectHintsAsync"/> reads them with a loose index scan
/// (a recursive <c>MIN(severity) WHERE severity &gt; ?</c>, 0.1-2 ms however large the table is), and
/// <c>UPPER</c> is applied to that list in code exactly as SQLite would (ASCII only), so the predicate
/// selects the same rows as before.
/// </para>
/// <para>
/// <b>Severity filter: seek or walk, decided per query — again.</b> A plain
/// <c>severity IN (…)</c> for a <em>common</em> set is a disaster on this table: with no
/// <c>sqlite_stat1</c> the planner seeks <c>idx_audit_severity_timestamp</c> and sorts every match for
/// the ORDER BY (measured live: 28 s and 4.1 GB for the newest page of "INFO and above"). It is the
/// bucket/connector story once more, so it gets the same answer: <see cref="IsCommonAsync"/> counts the
/// qualifying spellings up to the threshold, and a common set is written <c>+e.severity IN (…)</c> so the
/// retention index is walked (0.1-30 ms) where <see cref="WalkPays"/>, while a rare one (CRITICAL: 1.5 k
/// of 490 k rows) is sought and sorted (20 ms).
/// </para>
/// <para>
/// <b>Severity counts come from the index alone.</b> The tiles (and the count under a severity filter
/// with nothing but a time window beside it) are answered by <see cref="BuildSpellingCountSql"/>: one
/// <c>COUNT(*)</c> per stored spelling, each a covering range on <c>(severity, timestamp)</c> with no
/// table row read (32-37 ms and 15 MB against 700-880 ms and 614 MB, identical counts). The index is on
/// the <em>text</em> timestamp while the window is defined on the retention nanos, so the range uses the
/// text of the window's first whole second as a bound and the one second it straddles is counted exactly
/// against the nanos column. Every result is reconciled against the plain window count taken in the same
/// statement (one snapshot): if the per-spelling counts do not add up — a legacy timestamp format that
/// does not sort as text, a NULL retention key — the answer is thrown away and the original
/// <c>GROUP BY UPPER(severity)</c> runs instead, so the fast path can only ever be faster, never different.
/// </para>
/// <para>
/// <b>Threading: every query runs on the thread pool, and can be stopped.</b> Microsoft.Data.Sqlite's
/// <c>async</c> methods complete synchronously (SQLite has no asynchronous I/O), so an <c>async</c>
/// method that awaits them never yields and runs entirely on its caller's thread. This reader's callers
/// are view-models awaiting from the dispatcher, so that froze the window for the length of every
/// query (0.3-5 s on the live database). Each public method therefore starts its body with
/// <see cref="ReaderOffload"/> (<c>Task.Run</c>) and returns a task that is really pending;
/// callers need no <c>Task.Run</c> of their own. The token is honoured between statements and rows
/// <em>and</em> inside one: every connection registers <see cref="ReaderOffload.InterruptOnCancel"/>, so
/// cancelling ends a running scan with <c>sqlite3_interrupt</c> and the call throws
/// <see cref="OperationCanceledException"/>. That matters for a text search — <c>LIKE '%x%'</c> has no
/// index and reads the whole window (1.4 s for 24 h, 17 s cold for 7 days on the live database) — which
/// the Audit panel abandons and restarts on every keystroke.
/// </para>
/// </summary>
public sealed class AuditReader
{
    /// <summary>
    /// How long a command waits out <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> before failing.
    /// Microsoft.Data.Sqlite's own default is 30 s; the panels are interactive, so 10 s is
    /// plenty for the sub-second WAL recovery lock the gateway can briefly hold.
    /// </summary>
    public const int BusyTimeoutSeconds = 10;

    /// <summary>
    /// A connector or bucket with at least this many rows is "common": filtering on it walks the
    /// retention index instead of seeking <c>idx_audit_connector</c> / <c>idx_audit_bucket_timestamp</c>
    /// (in the shapes where that pays — see <see cref="WalkPays"/>). See the type documentation for the
    /// measurements behind 10,000.
    /// </summary>
    public const int DefaultCommonRowThreshold = 10_000;

    /// <summary>
    /// A run filter walks the retention index (newest first, stopping after the page) when the run's page-th newest row is within
    /// this many rows of the table's newest, and otherwise seeks <c>idx_audit_run_id</c> and sorts the run. Measured live: the
    /// newest run holds a big share of the recent table, where the seek-and-sort took 4.7 s and the walk 0.2 ms; a big run that
    /// ended weeks ago is the opposite (the walk reads hundreds of thousands of rows to reach it, 60 s).
    /// </summary>
    public const int DefaultRunWalkBudget = 100_000;

    /// <summary>
    /// The most distinct severity spellings the reader will enumerate. The real table has five (INFO, LOW,
    /// MEDIUM, HIGH, CRITICAL); a database with more than this many is not one the gateway wrote, and the
    /// loose index scan that lists them is not worth trusting, so such a database is read with the original
    /// <c>UPPER(severity)</c> shapes.
    /// </summary>
    private const int MaxSeveritySpellings = 64;

    /// <summary>
    /// The raw, trigger-maintained, indexed sort/range column. Compare and order on this
    /// directly — never inside a function — or the index cannot be used.
    /// </summary>
    internal const string RetentionColumn = "e.retention_timestamp_unix_nano";

    /// <summary>
    /// Nanos derived from the text <c>timestamp</c> column, for a row a trigger somehow
    /// missed. Only ever used in the <see cref="KeyMode.NullSafe"/> fallback.
    /// </summary>
    private const string TextDerivedNanos =
        "CAST(strftime('%s', substr(e.timestamp, 1, 19) || 'Z') AS INTEGER) * 1000000000";

    /// <summary>
    /// The value shown to the UI and used as the keyset cursor: the indexed column with a
    /// text-derived fallback. Lives <b>only in the SELECT list</b> in the normal
    /// (<see cref="KeyMode.Indexed"/>) mode — it is never a WHERE or ORDER BY term there,
    /// because that is exactly what defeated the index.
    /// </summary>
    private const string SortKey = "COALESCE(" + RetentionColumn + ", " + TextDerivedNanos + ")";

    /// <summary>The id as the keyset cursor compares it: a NULL id (see <see cref="Map"/>) is the empty string.</summary>
    private const string IdKey = "COALESCE(e.id, '')";

    internal const string SelectColumns = """
        e.id, e.timestamp, e.action, e.target, e.actor, e.details, e.severity,
        e.structured_json, e.bucket, e.connector, e.event_name, e.agent_name,
        e.tool_name, e.session_id, e.run_id, e.request_id, e.trace_id,
        e.source, e.signal, e.binary_version
        """;

    private readonly string _connectionString;
    private readonly int _commonRowThreshold;
    private readonly int _runWalkBudget;
    private long _pageQueries;
    private long _countQueries;
    private long _spellingFallbacks;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="commonRowThreshold">
    /// Row count at which a connector counts as common; see
    /// <see cref="DefaultCommonRowThreshold"/>. Tests lower it to exercise both plans on a
    /// handful of rows.
    /// </param>
    /// <param name="runWalkBudget">
    /// How many rows a run filter may walk the retention index past before the run's own index is used instead; see
    /// <see cref="DefaultRunWalkBudget"/>. Tests lower it.
    /// </param>
    public AuditReader(string databasePath, int commonRowThreshold = DefaultCommonRowThreshold, int runWalkBudget = DefaultRunWalkBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(commonRowThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(runWalkBudget, 1);
        DatabasePath = databasePath;
        _commonRowThreshold = commonRowThreshold;
        _runWalkBudget = runWalkBudget;
        _connectionString = BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>
    /// How many times <see cref="QueryAsync"/> (and so <see cref="ListAsync"/>) has been called on this
    /// reader, counted when the call is made, whether or not it finished or was cancelled. A diagnostic
    /// seam: a panel that is meant to coalesce a burst of filter changes into one page query can be held
    /// to it, which latency alone never shows.
    /// </summary>
    public long PageQueryCount => Interlocked.Read(ref _pageQueries);

    /// <summary>The same count for <see cref="CountAsync"/> and <see cref="CountBySeverityAsync"/>.</summary>
    public long CountQueryCount => Interlocked.Read(ref _countQueries);

    /// <summary>
    /// How many times the index-only severity counts were thrown away because they did not add up to the window
    /// total (see <see cref="BuildSpellingCountSql"/>) and the exact <c>UPPER</c> statement ran instead. The answer is
    /// right either way, so a reconcile that fails quietly on every call would go unnoticed except as the old
    /// 600 MB refresh; the test suite holds this at zero for a database whose timestamps are all RFC3339 UTC.
    /// </summary>
    internal long SpellingCountFallbacks => Interlocked.Read(ref _spellingFallbacks);

    /// <summary>
    /// How a query expresses its time window, ordering and keyset cursor. Chosen per call by
    /// <see cref="DetectKeyModeAsync"/>.
    /// </summary>
    private enum KeyMode
    {
        /// <summary>
        /// The normal case: no row has a NULL <c>retention_timestamp_unix_nano</c>. Windows are
        /// plain range predicates on the raw column, ordering is
        /// <c>ORDER BY retention_timestamp_unix_nano, id</c> (the exact column order of the
        /// covering index, walked forwards or backwards), and the keyset cursor is
        /// <c>nanos &lt;= $c AND (nanos &lt; $c OR id &lt; $id)</c> — a sargable range plus a
        /// residual — so both the first page and every later page are an index seek plus a
        /// walk that stops after LIMIT rows (0.2 ms on the live database at any depth).
        /// </summary>
        Indexed,

        /// <summary>
        /// At least one row has a NULL column (the AFTER INSERT/UPDATE trigger produces NULL when
        /// the text matches none of its patterns; on the live database there are none). Windows
        /// become <c>(range OR (col IS NULL AND text-derived range))</c> — still sargable (the
        /// planner uses MULTI-INDEX OR, COUNT 24 h = 12 ms) — and paging falls back to the
        /// original <see cref="SortKey"/> ordering and keyset so every row, including the
        /// NULL-column ones, stays in one total order with no skipped or repeated rows. That path
        /// scans and sorts (0.8 s for a 24 h page on the live database): correctness over speed
        /// for a state that should not occur. Ordering on the raw column instead would sort NULLs
        /// last when descending / first when ascending and could not express "after this NULL
        /// row" in an index-friendly keyset predicate, so a page boundary landing on one would
        /// silently skip the rest. (A row whose text timestamp is <em>also</em> unparseable has a
        /// NULL sort key here and keeps the limitation the reader always had: it sorts as NULL
        /// and its cursor carries 0. The gateway always writes RFC3339, so this is theoretical.)
        /// </summary>
        NullSafe,
    }

    /// <summary>
    /// The read-only connection string used for every query: <c>Mode=ReadOnly</c> (never a
    /// write lock on the gateway's file) and an explicit <see cref="BusyTimeoutSeconds"/>.
    /// Also used by <see cref="DefenseClaw.Core.Inventory.InventoryReader"/> so both databases
    /// share one policy.
    /// </summary>
    public static string BuildReadOnlyConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

    /// <summary>
    /// Fetches one keyset page. Never uses OFFSET.
    /// <para>
    /// Order is <c>(retention_timestamp_unix_nano, id)</c> — descending unless
    /// <see cref="AuditQuery.Ascending"/>. <c>id</c> is the tiebreak, so rows sharing a
    /// timestamp still have one total order and a cursor never skips or repeats them.
    /// </para>
    /// </summary>
    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _pageQueries);
        return ReaderOffload.Run(() => QueryCoreAsync(query, cancellationToken), cancellationToken);
    }

    private async Task<AuditPage> QueryCoreAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = query.EffectiveLimit;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);
        var hints = await DetectHintsAsync(connection, query, AuditQueryShape.Page, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: BuildPageSql emits fixed text, const columns, ASC/DESC and $-placeholders; every value is a bound parameter
        command.CommandText = BuildPageSql(command, query, hints);

        var events = new List<AuditEvent>(limit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(Map(reader));
            }
        }

        // BuildPageSql fetched one extra row to answer HasMore without a second round trip.
        var hasMore = events.Count > limit;
        if (hasMore)
        {
            events.RemoveRange(limit, events.Count - limit);
        }

        var nextCursor = hasMore && events.Count > 0 ? events[^1].Cursor : (AuditCursor?)null;
        return new AuditPage(events, nextCursor, hasMore);
    }

    /// <summary>Convenience wrapper returning just the rows. (Off the caller's thread by way of <see cref="QueryAsync"/>.)</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken = default) =>
        (await QueryAsync(query, cancellationToken).ConfigureAwait(false)).Events;

    /// <summary>Single row by primary key.</summary>
    public Task<AuditEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => GetByIdCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<AuditEvent?> GetByIdCoreAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: the only holes are the const SelectColumns and SortKey; the id is the bound $id
        command.CommandText = $"SELECT {SelectColumns}, {SortKey} AS sort_nanos FROM audit_events e WHERE e.id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>
    /// How many ids one <see cref="GetByIdsAsync"/> statement carries; the rest go in further statements on the same
    /// connection. Far below SQLite's bound-parameter limit (32,766 since 3.32), so an older build is safe too.
    /// </summary>
    private const int IdBatchSize = 400;

    /// <summary>
    /// The rows with these ids (primary-key lookups, in any order; an id with no row is simply absent). What turns the alert
    /// queue's ids (<see cref="AlertQueueReader"/>, which reads none of the heavy columns) into rows a panel can show in full.
    /// Read-only, off the caller's thread, stoppable, like every other call here.
    /// </summary>
    public Task<IReadOnlyList<AuditEvent>> GetByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ReaderOffload.Run(() => GetByIdsCoreAsync(ids, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<AuditEvent>> GetByIdsCoreAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(static id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        var found = new List<AuditEvent>(wanted.Length);
        if (wanted.Length == 0 || !Exists)
        {
            return found;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        for (var offset = 0; offset < wanted.Length; offset += IdBatchSize)
        {
            var batch = new ArraySegment<string>(wanted, offset, Math.Min(IdBatchSize, wanted.Length - offset));

            await using var command = connection.CreateCommand();

            // Only the parameter names are concatenated; every id goes in as a bound value.
            var names = new string[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                names[i] = "$p" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(names[i], batch[i]);
            }

            command.CommandText =
                // nosemgrep: csharp-sqli -- allow-list: const columns and the $p0..$pN placeholders built just above from the loop index; the ids are bound parameters
                $"SELECT {SelectColumns}, {SortKey} AS sort_nanos FROM audit_events e WHERE e.id IN ({string.Join(',', names)})";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found.Add(Map(reader));
            }
        }

        return found;
    }

    /// <summary>
    /// Counts matching rows per severity — the dashboard tiles. Honours every filter on
    /// <paramref name="query"/> except paging.
    /// <para>
    /// A window-only query (no bucket, connector, text or upper bound — what the Overview asks for) is
    /// answered from <c>idx_audit_severity_timestamp</c> alone, one covering count per stored spelling; see
    /// <see cref="BuildSpellingCountSql"/>. Anything else, and any database where that answer does not
    /// reconcile with the window total, groups on <c>UPPER(severity)</c>: grouping on the raw column made
    /// SQLite pick <c>idx_audit_severity_timestamp</c> as a full-index scan (to avoid a sort) and ignore the
    /// window (1.7 s for 24 h), while the expression takes the retention-index range and a temp B-tree for
    /// the group (0.15–0.5 s for 24 h at ~47k rows — 0.7-0.9 s and 614 MB at today's ~145k, all of it
    /// fetching each row's severity). <see cref="AuditSeverityExtensions.Parse"/> upper-cases and trims before
    /// matching, so folding case in SQL cannot change the result — the loops still sum any groups that parse
    /// to the same level (<c>WARN</c>/<c>WARNING</c>, <c>INFO</c>/<c>NOTICE</c>, …).
    /// </para>
    /// </summary>
    public Task<IReadOnlyDictionary<AuditSeverity, int>> CountBySeverityAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _countQueries);
        return ReaderOffload.Run(() => CountBySeverityCoreAsync(query, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyDictionary<AuditSeverity, int>> CountBySeverityCoreAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);
        var hints = await DetectHintsAsync(connection, query, AuditQueryShape.CountBySeverity, cancellationToken).ConfigureAwait(false);

        var counts = new Dictionary<AuditSeverity, int>();
        if (UsesSpellingCounts(query, hints, AuditQueryShape.CountBySeverity)
            && await CountBySpellingAsync(connection, query, hints, cancellationToken).ConfigureAwait(false) is { } spellings)
        {
            // A spelling with no row in the window is no group, as it never was in the GROUP BY.
            foreach (var (spelling, count) in spellings.Where(s => s.Count > 0))
            {
                Add(counts, AuditSeverityExtensions.Parse(spelling), count);
            }

            return counts;
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: BuildSeverityCountSql builds from the same fixed fragments as BuildPageSql; every value is a bound parameter
        command.CommandText = BuildSeverityCountSql(command, query, hints);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Add(counts, AuditSeverityExtensions.Parse(reader.IsDBNull(0) ? null : reader.GetString(0)), reader.GetInt32(1));
        }

        return counts;

        static void Add(Dictionary<AuditSeverity, int> tiles, AuditSeverity severity, int count) =>
            tiles[severity] = tiles.TryGetValue(severity, out var existing) ? existing + count : count;
    }

    /// <summary>Total matching rows. Separate from paging so tiles stay accurate.</summary>
    public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _countQueries);
        return ReaderOffload.Run(() => CountCoreAsync(query, cancellationToken), cancellationToken);
    }

    private async Task<int> CountCoreAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);
        var hints = await DetectHintsAsync(connection, query, AuditQueryShape.Count, cancellationToken).ConfigureAwait(false);

        // "HIGH and above, last 24 hours" and nothing else: the same per-spelling covering counts as the tiles,
        // added up for the spellings that qualify. See UsesSpellingCounts for the shapes this covers.
        if (UsesSpellingCounts(query, hints, AuditQueryShape.Count)
            && query.MinimumSeverity is { } minimum
            && await CountBySpellingAsync(connection, query, hints, cancellationToken).ConfigureAwait(false) is { } spellings)
        {
            var qualifying = QualifyingSpellings(hints.PresentSeverities!, minimum).ToHashSet(StringComparer.Ordinal);
            return (int)spellings.Where(s => s.Spelling is not null && qualifying.Contains(s.Spelling)).Sum(s => (long)s.Count);
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: BuildCountSql builds from the same fixed fragments as BuildPageSql; every value is a bound parameter
        command.CommandText = BuildCountSql(command, query, hints);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs <see cref="BuildSpellingCountSql"/> and returns each stored spelling's count (NULL severity
    /// included, as a null spelling) — or <c>null</c> when they do not add up to the window total the same
    /// statement read, in which case the caller falls back to the exact <c>UPPER</c> shapes.
    /// </summary>
    private async Task<List<(string? Spelling, int Count)>?> CountBySpellingAsync(
        SqliteConnection connection,
        AuditQuery query,
        PlanHints hints,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildSpellingCountSql(command, query, hints);

        var counts = new List<(string? Spelling, int Count)>();
        long total = -1;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = reader.GetInt64(2);
            if (reader.GetInt64(0) == 1)
            {
                total = count;
            }
            else
            {
                counts.Add((reader.IsDBNull(1) ? null : reader.GetString(1), checked((int)count)));
            }
        }

        if (total >= 0 && counts.Sum(c => (long)c.Count) == total)
        {
            return counts;
        }

        _ = Interlocked.Increment(ref _spellingFallbacks);
        return null;
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines for the exact SQL the matching reader method
    /// would run for <paramref name="query"/> — same builder, same parameters, same key mode, and
    /// the same connector and bucket strategy (the cardinality probes run for real, so a connector or
    /// bucket with at least <see cref="DefaultCommonRowThreshold"/> rows explains as a retention-index
    /// walk where <see cref="WalkPays"/> and a rare one as an <c>idx_audit_connector</c> /
    /// <c>idx_audit_bucket_timestamp</c> seek).
    /// This exists so a regression test (and a curious operator) can assert that a window
    /// still uses <c>idx_retention_audit_events_timestamp</c> and that the newest-page query
    /// needs no temp B-tree for ORDER BY, instead of that only being visible as latency.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(
        AuditQuery query,
        AuditQueryShape shape,
        CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(query, shape, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(
        AuditQuery query,
        AuditQueryShape shape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);
        var hints = await DetectHintsAsync(connection, query, shape, cancellationToken).ConfigureAwait(false);

        // Mirrors QueryCoreAsync / CountCoreAsync / CountBySeverityCoreAsync: the statement a shape would run first.
        // (The exact UPPER fallback that follows a failed reconcile is the statement of a different data set, so it is
        // not described here.)
        await using var command = connection.CreateCommand();
        var sql = shape switch
        {
            AuditQueryShape.Page => BuildPageSql(command, query, hints),
            AuditQueryShape.Count when UsesSpellingCounts(query, hints, shape) => BuildSpellingCountSql(command, query, hints),
            AuditQueryShape.Count => BuildCountSql(command, query, hints),
            AuditQueryShape.CountBySeverity when UsesSpellingCounts(query, hints, shape) => BuildSpellingCountSql(command, query, hints),
            AuditQueryShape.CountBySeverity => BuildSeverityCountSql(command, query, hints),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown query shape."),
        };

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        // Columns: id, parent, notused, detail.
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    /// <summary>Distinct buckets, for the Audit panel's filter dropdown.</summary>
    public Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken cancellationToken = default) =>
        DistinctAsync("bucket", cancellationToken);

    /// <summary>Distinct connectors. NULL (platform-scoped rows) is omitted.</summary>
    public Task<IReadOnlyList<string>> ListConnectorsAsync(CancellationToken cancellationToken = default) =>
        DistinctAsync("connector", cancellationToken);

    /// <summary>Distinct actions.</summary>
    public Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken cancellationToken = default) =>
        DistinctAsync("action", cancellationToken);

    private Task<IReadOnlyList<string>> DistinctAsync(string column, CancellationToken cancellationToken) =>
        ReaderOffload.Run(() => DistinctCoreAsync(column, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> DistinctCoreAsync(string column, CancellationToken cancellationToken)
    {
        // Column names are compile-time constants from this class only — never user input.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        return (await LoadDistinctAsync(connection, column, skipEmpty: true, limit: null, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The distinct non-NULL values of <paramref name="column"/> in ascending (binary) order, by a
    /// <em>loose index scan</em>: each step is <c>MIN(column) WHERE column &gt; previous</c>, one index seek,
    /// so the cost is the number of distinct values (0.1-0.8 ms for the live table's 12 buckets, 1 connector
    /// and 31 actions) and not the number of rows. <c>SELECT DISTINCT column</c> walks the whole covering index
    /// instead — 763 + 263 + 398 ms and 55 MB for the three lists at 490 k rows, growing with the table, before
    /// the Audit panel showed a single row. Same rows as that statement: NULL is omitted, and
    /// <paramref name="skipEmpty"/> omits <c>''</c> too.
    /// <para>
    /// <paramref name="limit"/> stops the scan after that many values and returns <c>null</c> if the column has
    /// more, so a caller that needs the <em>complete</em> list can tell it does not have one.
    /// <paramref name="column"/> is a compile-time constant from this class, never user input.
    /// </para>
    /// </summary>
    private static async Task<List<string>?> LoadDistinctAsync(
        SqliteConnection connection,
        string column,
        bool skipEmpty,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: column is a literal at every call site (bucket, connector, action, severity), never input; only the const $limit text varies
        command.CommandText = BuildDistinctSql(column, skipEmpty, limited: limit is not null);

        if (limit is { } max)
        {
            // One past the limit, so "more than max" is distinguishable from "exactly max".
            command.Parameters.AddWithValue("$limit", max + 1);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return limit is { } cap && values.Count > cap ? null : values;
    }

    /// <summary>The loose-index-scan statement behind <see cref="LoadDistinctAsync"/> (internal so a test can EXPLAIN it).</summary>
    internal static string BuildDistinctSql(string column, bool skipEmpty, bool limited)
    {
        var first = skipEmpty
            ? $"SELECT MIN({column}) FROM audit_events WHERE {column} <> ''"
            : $"SELECT MIN({column}) FROM audit_events";

        return
            $"""
            WITH RECURSIVE t(v) AS (
                {first}
                UNION ALL
                SELECT (SELECT MIN({column}) FROM audit_events WHERE {column} > t.v) FROM t WHERE v IS NOT NULL{(limited ? " LIMIT $limit" : string.Empty)}
            )
            SELECT v FROM t WHERE v IS NOT NULL ORDER BY v
            """;
    }

    /// <summary>
    /// Decides between <see cref="KeyMode.Indexed"/> and <see cref="KeyMode.NullSafe"/> with one
    /// probe: does any row have a NULL <c>retention_timestamp_unix_nano</c>? NULLs are the first
    /// entries of the retention index, so <c>EXISTS</c> is a single index seek —
    /// <c>SEARCH … USING COVERING INDEX idx_retention_audit_events_timestamp
    /// (retention_timestamp_unix_nano=?)</c>, ~0.05 ms even on the 2.7 GB database — and it
    /// stops at the first hit. A row inserted between the probe and the query is invisible to
    /// the indexed query for an instant, which is the same window any paged read has.
    /// </summary>
    private static async Task<KeyMode> DetectKeyModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT EXISTS (SELECT 1 FROM audit_events WHERE retention_timestamp_unix_nano IS NULL)";

        var result = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var anyNull = result is not (null or DBNull) && Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
        return anyNull ? KeyMode.NullSafe : KeyMode.Indexed;
    }

    /// <summary>
    /// How one query will be written: the <see cref="KeyMode"/>, and whether its connector filter, its
    /// bucket filter and its minimum-severity filter (if any) select a <em>common</em> set of rows. A common
    /// set is written <c>+e.connector</c> / <c>+e.bucket</c> / <c>+e.severity</c> so the planner walks the
    /// retention index instead of seeking <c>idx_audit_connector</c> / <c>idx_audit_bucket_timestamp</c> /
    /// <c>idx_audit_severity_timestamp</c> — but only in the shapes where a walk pays; see
    /// <see cref="WalkPays"/>.
    /// <para>
    /// <paramref name="PresentSeverities"/> is every distinct non-NULL severity spelling stored in the table
    /// (null when the query does not need them, or when there are more than
    /// <see cref="MaxSeveritySpellings"/>, in which case severity is handled with <c>UPPER</c> as it used to
    /// be). It is what turns "at or above HIGH" into a list of raw column values.
    /// </para>
    /// </summary>
    private readonly record struct PlanHints(
        KeyMode Mode,
        bool CommonConnector,
        bool CommonBucket,
        IReadOnlyList<string>? PresentSeverities = null,
        bool CommonSeverity = false,
        bool CommonRun = false);

    private async Task<PlanHints> DetectHintsAsync(
        SqliteConnection connection,
        AuditQuery query,
        AuditQueryShape shape,
        CancellationToken cancellationToken)
    {
        var mode = await DetectKeyModeAsync(connection, cancellationToken).ConfigureAwait(false);
        var connectors = string.IsNullOrWhiteSpace(query.Connector) ? Array.Empty<string>() : new[] { query.Connector };
        var commonConnector = await IsCommonAsync(connection, "connector", connectors, cancellationToken).ConfigureAwait(false);
        var commonBucket = await IsCommonAsync(connection, "bucket", BucketsOf(query), cancellationToken).ConfigureAwait(false);

        // The distinct spellings are a loose index scan (sub-millisecond), read per query rather than cached so a
        // spelling the gateway starts writing tomorrow is never missing from today's predicate.
        IReadOnlyList<string>? present = null;
        var commonSeverity = false;
        var filtersSeverity = query.MinimumSeverity is { } minimum && minimum != AuditSeverity.Unknown;
        if (filtersSeverity || shape == AuditQueryShape.CountBySeverity)
        {
            present = await LoadDistinctAsync(connection, "severity", skipEmpty: false, MaxSeveritySpellings, cancellationToken).ConfigureAwait(false);
            if (present is not null && filtersSeverity)
            {
                var qualifying = QualifyingSpellings(present, query.MinimumSeverity!.Value);
                commonSeverity = await IsCommonAsync(connection, "severity", qualifying, cancellationToken).ConfigureAwait(false);
            }
        }

        var commonRun = !string.IsNullOrWhiteSpace(query.RunId)
            && await RunWalkPaysAsync(connection, query.RunId, query.EffectiveLimit, cancellationToken).ConfigureAwait(false);

        return new PlanHints(mode, commonConnector, commonBucket, present, commonSeverity, commonRun);
    }

    /// <summary>
    /// The stored spellings among <paramref name="present"/> whose upper-cased form is at or above
    /// <paramref name="minimum"/> — the rows <c>UPPER(severity) IN (…)</c> selected, as raw column values.
    /// </summary>
    private static List<string> QualifyingSpellings(IReadOnlyList<string> present, AuditSeverity minimum)
    {
        var allowed = new HashSet<string>(AuditSeverityExtensions.AtOrAbove(minimum), StringComparer.Ordinal);
        return present.Where(spelling => allowed.Contains(AsciiUpper(spelling))).ToList();
    }

    /// <summary>
    /// <c>UPPER()</c> as SQLite implements it without ICU: ASCII letters only, everything else untouched. Not
    /// <see cref="string.ToUpperInvariant"/>, which would also fold letters SQLite leaves alone and so select rows
    /// the SQL never did.
    /// </summary>
    private static string AsciiUpper(string value)
    {
        var needsFolding = false;
        foreach (var c in value)
        {
            if (c is >= 'a' and <= 'z')
            {
                needsFolding = true;
                break;
            }
        }

        return !needsFolding
            ? value
            : string.Create(value.Length, value, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var c = source[i];
                    span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
                }
            });
    }

    /// <summary>
    /// True when the rows <paramref name="column"/> (<c>connector</c> or <c>bucket</c>) selects
    /// for <paramref name="values"/> number at least the reader's common-row threshold
    /// (<see cref="DefaultCommonRowThreshold"/> unless the constructor lowered it). One bounded
    /// probe — the inner SELECT stops at the threshold, so its cost is capped no matter how big the
    /// table is, and it is answered from the covering single-column index without touching a data
    /// page (0.4 ms at 10,000 on the live database). False when there is nothing to filter on.
    /// <para>
    /// With <see cref="AuditQuery.IncludeNullConnector"/> the connector probe still counts only the
    /// named connector, not the platform (NULL) rows the predicate also keeps. Counting them made a
    /// rare connector look common on a database where 15% of rows are platform-scoped (44 k of
    /// 290 k), and the walk it then chose is 6× slower than the index for the no-window severity
    /// tiles of <c>codex + platform rows</c> (1.1 s against 0.18 s, measured live). The
    /// <c>OR … IS NULL</c> form for a rare connector costs what the NULL rows cost (≤ 0.5 s for any
    /// shape) and always did; only a common connector's index arm was the problem.
    /// </para>
    /// </summary>
    private async Task<bool> IsCommonAsync(
        SqliteConnection connection,
        string column,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return false;
        }

        // "column" is one of three compile-time constants from DetectHintsAsync — never user input.
        var names = new List<string>(values.Count);
        await using var probe = connection.CreateCommand();
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"$v{i.ToString(CultureInfo.InvariantCulture)}";
            names.Add(name);
            probe.Parameters.AddWithValue(name, values[i]);
        }

        probe.CommandText =
            // nosemgrep: csharp-sqli -- allow-list: column is one of three literals (connector, bucket, severity) from DetectHintsAsync; the values are bound $v0..$vN
            $"SELECT COUNT(*) FROM (SELECT 1 FROM audit_events WHERE {column} IN ({string.Join(", ", names)}) LIMIT $limit)";
        probe.Parameters.AddWithValue("$limit", _commonRowThreshold);

        var result = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not (null or DBNull) &&
            Convert.ToInt64(result, CultureInfo.InvariantCulture) >= _commonRowThreshold;
    }

    /// <summary>
    /// Whether a run filter is better written <c>+e.run_id</c> (walk the retention index, newest first) than as an index seek on
    /// <c>idx_audit_run_id</c> followed by a sort of the whole run. The run's entries in that index are ordered by rowid, which
    /// follows insertion and so time, so one backwards read of <paramref name="limit"/> + 1 entries finds the run's
    /// (limit + 1)-th newest row cheaply: a run with no more than a page of rows is false (the seek is a handful of rows), and
    /// otherwise the walk pays when that row is within <see cref="DefaultRunWalkBudget"/> rows of the table's newest
    /// (<c>MAX(rowid)</c>, one index probe). Never more than a few index probes, whatever the table or the run.
    /// </summary>
    private async Task<bool> RunWalkPaysAsync(SqliteConnection connection, string runId, int limit, CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT (SELECT MAX(rowid) FROM audit_events)
                 - (SELECT rowid FROM audit_events WHERE run_id = $run ORDER BY rowid DESC LIMIT 1 OFFSET $skip)
            """;
        probe.Parameters.AddWithValue("$run", runId);
        probe.Parameters.AddWithValue("$skip", limit);
        var result = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not (null or DBNull) && Convert.ToInt64(result, CultureInfo.InvariantCulture) <= _runWalkBudget;
    }

    /// <summary>The bucket names a query filters on: <see cref="AuditQuery.Bucket"/> plus <see cref="AuditQuery.Buckets"/>, blanks dropped.</summary>
    private static List<string> BucketsOf(AuditQuery query)
    {
        var buckets = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Bucket))
        {
            buckets.Add(query.Bucket);
        }

        if (query.Buckets is not null)
        {
            buckets.AddRange(query.Buckets.Where(b => !string.IsNullOrWhiteSpace(b)));
        }

        return buckets;
    }

    /// <summary>
    /// Whether writing a common connector/bucket/severity filter as a retention-index walk beats the index
    /// seek for this shape. A walk reads every row of the table — or, with a <c>From</c> bound, of the
    /// window — testing the filter as it goes, and stops early only where the answer is "the newest N"
    /// (<see cref="AuditQueryShape.Page"/>). A COUNT or GROUP BY with no lower bound must read every
    /// matching row on either path, and the index reads <em>only</em> those. Measured live per
    /// bucket (tool.activity, 43 k of 290 k rows): all-time severity tiles 0.29 s on the index against
    /// 1.37 s walking the table; all-time COUNT is answered from the covering index in 5 ms against
    /// 42 ms; but the newest page is 0.86 s on the index (it sorts every matching row) against 3 ms
    /// walking, and a 24 h COUNT/tiles about 0.5 s against 0.35 s.
    /// </summary>
    private static bool WalkPays(AuditQuery query, AuditQueryShape shape) =>
        shape == AuditQueryShape.Page || query.From is not null;

    /// <summary>
    /// One keyset page. Fetches <c>Limit + 1</c> rows so <c>HasMore</c> needs no second query.
    /// <para>
    /// Indexed mode: <c>ORDER BY e.retention_timestamp_unix_nano DESC, e.id DESC LIMIT n</c>
    /// matches the index's column order, so the plan is <c>SEARCH e USING INDEX
    /// idx_retention_audit_events_timestamp</c> (or <c>SCAN … USING INDEX</c> with no window),
    /// with no <c>USE TEMP B-TREE FOR ORDER BY</c>. NullSafe mode orders by the
    /// <c>sort_nanos</c> alias (the COALESCE) and does sort.
    /// </para>
    /// </summary>
    private static string BuildPageSql(SqliteCommand command, AuditQuery query, PlanHints hints)
    {
        var sql = new StringBuilder()
            .Append("SELECT ").Append(SelectColumns).Append(", ").Append(SortKey).AppendLine(" AS sort_nanos")
            .AppendLine("FROM audit_events e");

        AppendWhere(sql, command, query, hints, AuditQueryShape.Page);

        var direction = query.Ascending ? "ASC" : "DESC";
        var orderKey = hints.Mode == KeyMode.Indexed ? RetentionColumn : "sort_nanos";
        sql.Append("ORDER BY ").Append(orderKey).Append(' ').Append(direction)
           .Append(", e.id ").Append(direction).AppendLine()
           .AppendLine("LIMIT $limit");

        command.Parameters.AddWithValue("$limit", query.EffectiveLimit + 1);
        return sql.ToString();
    }

    private static string BuildCountSql(SqliteCommand command, AuditQuery query, PlanHints hints)
    {
        var sql = new StringBuilder().AppendLine("SELECT COUNT(*) FROM audit_events e");
        AppendWhere(sql, command, query, hints, AuditQueryShape.Count);
        return sql.ToString();
    }

    /// <summary>
    /// The exact tiles statement: <c>GROUP BY UPPER(severity)</c>. Correct for every filter and every database, and
    /// the price is a row fetch per row in the window (see the type documentation); the reader runs it only when
    /// <see cref="UsesSpellingCounts"/> says no, or the fast answer did not reconcile.
    /// </summary>
    private static string BuildSeverityCountSql(SqliteCommand command, AuditQuery query, PlanHints hints)
    {
        var sql = new StringBuilder().AppendLine("SELECT UPPER(e.severity), COUNT(*) FROM audit_events e");
        AppendWhere(sql, command, query, hints, AuditQueryShape.CountBySeverity);
        sql.AppendLine("GROUP BY UPPER(e.severity)");
        return sql.ToString();
    }

    /// <summary>
    /// Whether <paramref name="shape"/> for <paramref name="query"/> can be answered by
    /// <see cref="BuildSpellingCountSql"/>: the tiles with no minimum, or a count <em>with</em> a minimum severity,
    /// when the only other filter is a lower time bound — the columns the covering index does not hold (bucket,
    /// connector, action, details, …) would each need a row fetch per counted row, which is the cost being
    /// avoided — and the spellings are known.
    /// </summary>
    private static bool UsesSpellingCounts(AuditQuery query, PlanHints hints, AuditQueryShape shape)
    {
        if (hints.PresentSeverities is null
            || query.To is not null
            || !string.IsNullOrWhiteSpace(query.Connector)
            || !string.IsNullOrWhiteSpace(query.ActionContains)
            || !string.IsNullOrWhiteSpace(query.SearchText)
            || !string.IsNullOrWhiteSpace(query.RunId)
            || query.ActionAnyOf is { Count: > 0 }
            || BucketsOf(query).Count > 0)
        {
            return false;
        }

        var filtersSeverity = query.MinimumSeverity is { } minimum && minimum != AuditSeverity.Unknown;
        return shape switch
        {
            AuditQueryShape.CountBySeverity => !filtersSeverity,
            AuditQueryShape.Count => filtersSeverity,
            _ => false,
        };
    }

    /// <summary>
    /// One row per stored severity spelling (plus NULL) and a last row with the window total:
    /// <c>(kind, spelling, n)</c>, kind 0 for a spelling and 1 for the total.
    /// <para>
    /// Each spelling's count is <c>COUNT(*) WHERE severity = ? AND timestamp &gt;= ?</c>, which SQLite answers from
    /// <c>idx_audit_severity_timestamp</c> without reading a table row: <c>SEARCH … USING COVERING INDEX
    /// (severity=? AND timestamp&gt;?)</c>. The window, however, is defined on the retention nanos, and the index
    /// holds the <em>text</em> timestamp. The bound is therefore the text of the first whole second at or after
    /// the window start (<c>$textFrom1</c>): every row at or after it is in the window, and every row whose text
    /// is before the second the window starts in (<c>$textFrom0</c>) is out, whatever the fraction. Only the rows
    /// of that one second are in doubt, and a second of events is a handful of rows: they are counted exactly
    /// against the window predicate in a second, non-covering subquery. (Fractions are never compared as text —
    /// Go trims trailing zeros, so <c>…:23.1Z</c> sorts after <c>…:23.15Z</c> — only whole-second prefixes are.)
    /// </para>
    /// <para>
    /// The total is the ordinary window <c>COUNT(*)</c> on the retention index, in the same statement and so the
    /// same snapshot; <see cref="CountBySpellingAsync"/> rejects the answer if the spellings do not sum to it.
    /// That is the guard for what a text bound cannot see: a timestamp in a format that does not sort as
    /// <c>yyyy-MM-ddTHH:mm:ss…</c>, an offset instead of <c>Z</c>, a row with no retention key.
    /// </para>
    /// </summary>
    private static string BuildSpellingCountSql(SqliteCommand command, AuditQuery query, PlanHints hints)
    {
        var spellings = new List<string?>(hints.PresentSeverities!) { null };

        // The window predicate, once, so its parameter is bound once and the total and the boundary second cannot
        // disagree about it. Empty when there is no lower bound.
        var windowClauses = new List<string>();
        AppendWindow(windowClauses, command, query with { MinimumSeverity = null }, hints.Mode);
        var window = string.Join(" AND ", windowClauses);

        var boundarySecond = false;
        if (query.From is { } from)
        {
            var utc = from.UtcDateTime;
            var sinceSecond = utc.Ticks % TimeSpan.TicksPerSecond;
            boundarySecond = sinceSecond != 0;
            var firstWhole = boundarySecond ? utc.AddTicks(TimeSpan.TicksPerSecond - sinceSecond) : utc;
            command.Parameters.AddWithValue("$textFrom0", TextSecond(utc));
            command.Parameters.AddWithValue("$textFrom1", TextSecond(firstWhole));
        }

        var sql = new StringBuilder();
        for (var i = 0; i < spellings.Count; i++)
        {
            var name = $"$spelling{i.ToString(CultureInfo.InvariantCulture)}";
            var spelling = spellings[i];
            if (spelling is not null)
            {
                command.Parameters.AddWithValue(name, spelling);
            }

            var matches = spelling is null ? "severity IS NULL" : $"severity = {name}";
            _ = sql.Append("SELECT 0 AS kind, ").Append(spelling is null ? "NULL" : name).Append(" AS spelling, ");
            if (window.Length == 0)
            {
                _ = sql.Append($"(SELECT COUNT(*) FROM audit_events WHERE {matches})");
            }
            else
            {
                _ = sql.Append($"(SELECT COUNT(*) FROM audit_events WHERE {matches} AND timestamp >= $textFrom1)");
                if (boundarySecond)
                {
                    _ = sql.Append(" + (SELECT COUNT(*) FROM audit_events e")
                        .Append($" WHERE e.{matches} AND e.timestamp >= $textFrom0 AND e.timestamp < $textFrom1 AND {window})");
                }
            }

            _ = sql.AppendLine(" AS n").AppendLine("UNION ALL");
        }

        _ = sql.Append("SELECT 1, NULL, (SELECT COUNT(*) FROM audit_events e")
            .Append(window.Length == 0 ? string.Empty : " WHERE " + window)
            .AppendLine(")");
        return sql.ToString();

        static string TextSecond(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static void AppendWhere(StringBuilder sql, SqliteCommand command, AuditQuery query, PlanHints hints, AuditQueryShape shape)
    {
        var mode = hints.Mode;
        var includeCursor = shape == AuditQueryShape.Page;
        var walk = WalkPays(query, shape);
        var clauses = new List<string>();

        var buckets = BucketsOf(query);
        if (buckets.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < buckets.Count; i++)
            {
                var name = $"$bucket{i.ToString(CultureInfo.InvariantCulture)}";
                names.Add(name);
                command.Parameters.AddWithValue(name, buckets[i]);
            }

            // Same "+" trick as the connector below. Without it a common bucket seeks
            // idx_audit_bucket_timestamp (bucket=?) and then sorts every matching row for the ORDER BY:
            // 1.3 s (24 h) / 1.9 s (all time) for the newest telemetry.ingest page (156 k rows) and
            // 0.66 / 0.97 s for guardrail.evaluation (51 k); walking the retention index instead is 1-5 ms.
            var bucket = hints.CommonBucket && walk ? "+e.bucket" : "e.bucket";
            clauses.Add($"{bucket} IN ({string.Join(", ", names)})");
        }

        if (query.MinimumSeverity is { } minimum && minimum != AuditSeverity.Unknown)
        {
            if (hints.PresentSeverities is { } present)
            {
                // The raw column against the spellings that really are stored: the same rows as
                // UPPER(severity) IN (...) selected, but sargable. A common set is walked on the retention
                // index ("+"), a rare one sought on idx_audit_severity_timestamp — see PlanHints.
                var stored = QualifyingSpellings(present, minimum);
                if (stored.Count == 0)
                {
                    // Nothing stored is at or above the minimum.
                    clauses.Add("0");
                }
                else
                {
                    var names = new List<string>();
                    for (var i = 0; i < stored.Count; i++)
                    {
                        var name = $"$sev{i.ToString(CultureInfo.InvariantCulture)}";
                        names.Add(name);
                        command.Parameters.AddWithValue(name, stored[i]);
                    }

                    var severity = hints.CommonSeverity && walk ? "+e.severity" : "e.severity";
                    clauses.Add($"{severity} IN ({string.Join(", ", names)})");
                }
            }
            else
            {
                // More spellings than any real database has (see MaxSeveritySpellings): fold case per row.
                var allowed = AuditSeverityExtensions.AtOrAbove(minimum);
                var names = new List<string>();
                for (var i = 0; i < allowed.Count; i++)
                {
                    var name = $"$sev{i.ToString(CultureInfo.InvariantCulture)}";
                    names.Add(name);
                    command.Parameters.AddWithValue(name, allowed[i]);
                }

                clauses.Add($"UPPER(e.severity) IN ({string.Join(", ", names)})");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Connector))
        {
            command.Parameters.AddWithValue("$connector", query.Connector);

            // A leading "+" makes the term ineligible for an index without changing its value
            // (SQLite's documented way to steer the planner). A common connector is filtered
            // while walking the retention index; a rare one is sought on idx_audit_connector. Both
            // forms select exactly the same rows — see IsCommonAsync for why the choice is made per
            // query and WalkPays for the shapes where a walk is not worth it. The IS NULL arm stays
            // index-eligible either way; with the "+" on the other arm the OR can no longer become
            // a MULTI-INDEX OR, which is the point.
            var connector = hints.CommonConnector && walk ? "+e.connector" : "e.connector";
            clauses.Add(query.IncludeNullConnector
                ? $"({connector} = $connector OR e.connector IS NULL)"
                : $"{connector} = $connector");
        }

        if (!string.IsNullOrWhiteSpace(query.ActionContains))
        {
            command.Parameters.AddWithValue("$action", Like(query.ActionContains));
            clauses.Add("e.action LIKE $action ESCAPE '\\'");
        }

        if (!string.IsNullOrWhiteSpace(query.RunId))
        {
            command.Parameters.AddWithValue("$run", query.RunId);
            clauses.Add((hints.CommonRun && walk ? "+e.run_id" : "e.run_id") + " = $run");
        }

        if (query.ActionAnyOf is { Count: > 0 } anyOf)
        {
            // The Audit panel's presets: any of several substrings in the action or the details (the Mac's actionLike).
            var terms = new List<string>();
            for (var i = 0; i < anyOf.Count; i++)
            {
                var name = $"$any{i.ToString(CultureInfo.InvariantCulture)}";
                command.Parameters.AddWithValue(name, Like(anyOf[i]));
                terms.Add($"(e.action LIKE {name} ESCAPE '\\' OR e.details LIKE {name} ESCAPE '\\')");
            }

            clauses.Add("(" + string.Join(" OR ", terms) + ")");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            command.Parameters.AddWithValue("$search", Like(query.SearchText));
            clauses.Add("""
                (e.details LIKE $search ESCAPE '\'
                 OR e.event_name LIKE $search ESCAPE '\'
                 OR e.tool_name LIKE $search ESCAPE '\'
                 OR e.target LIKE $search ESCAPE '\')
                """);
        }

        AppendWindow(clauses, command, query, mode);

        if (includeCursor && query.After is { } cursor)
        {
            command.Parameters.AddWithValue("$cursorNanos", cursor.TimestampNanos);
            command.Parameters.AddWithValue("$cursorId", cursor.Id);
            var comparison = query.Ascending ? ">" : "<";

            // Indexed: the leading "nanos <= $c" (">=" ascending) is a plain range the planner
            // seeks to on the index; the parenthesised remainder is the tie-break, evaluated as
            // a residual on the few rows that share the cursor's timestamp. Written as a bare
            // "(nanos < $c OR (nanos = $c AND id < $id))" the OR invites MULTI-INDEX OR plus a
            // sort, which is the very thing this rewrite removes. NullSafe keeps the original
            // COALESCE form so a NULL-column row has a well-defined position.
            //
            // The id is compared as COALESCE(id, ''): "id TEXT PRIMARY KEY" admits NULL, and "NULL < 'x'" is not
            // true, so a null-id row tied on timestamp with the row a page ended on would be skipped. Empty is where
            // NULL sorts (below every string), and Map hands such a row exactly that id. It is only ever the residual
            // of the tie-break, never the range the planner seeks, so the plan is unchanged.
            clauses.Add(mode == KeyMode.Indexed
                ? $"({RetentionColumn} {comparison}= $cursorNanos AND ({RetentionColumn} {comparison} $cursorNanos OR {IdKey} {comparison} $cursorId))"
                : $"({SortKey} {comparison} $cursorNanos OR ({SortKey} = $cursorNanos AND {IdKey} {comparison} $cursorId))");
        }

        if (clauses.Count > 0)
        {
            sql.Append("WHERE ").AppendLine(string.Join("\n  AND ", clauses));
        }
    }

    /// <summary>
    /// The From (inclusive) / To (exclusive) window. Indexed mode: two plain bounds on the raw
    /// column, merged by the planner into one index range. NullSafe mode:
    /// <c>((col &gt;= $from AND col &lt; $to) OR (col IS NULL AND text-derived &gt;= $from AND
    /// text-derived &lt; $to))</c> — both bounds in one OR so each branch is a single index
    /// range (the second is the NULL run at the front of the index), rather than one OR per bound
    /// where the planner would range only one of them and filter the other.
    /// </summary>
    private static void AppendWindow(List<string> clauses, SqliteCommand command, AuditQuery query, KeyMode mode)
    {
        var bounds = new List<(string Operator, string Parameter)>();

        if (query.From is { } from)
        {
            command.Parameters.AddWithValue("$from", ToUnixNanos(from));
            bounds.Add((">=", "$from"));
        }

        if (query.To is { } to)
        {
            command.Parameters.AddWithValue("$to", ToUnixNanos(to));
            bounds.Add(("<", "$to"));
        }

        if (bounds.Count == 0)
        {
            return;
        }

        var indexedRange = string.Join(" AND ", bounds.Select(b => $"{RetentionColumn} {b.Operator} {b.Parameter}"));
        if (mode == KeyMode.Indexed)
        {
            clauses.Add(indexedRange);
            return;
        }

        var textRange = string.Join(" AND ", bounds.Select(b => $"{TextDerivedNanos} {b.Operator} {b.Parameter}"));
        clauses.Add($"(({indexedRange}) OR ({RetentionColumn} IS NULL AND {textRange}))");
    }

    /// <summary>Escapes LIKE wildcards so a literal % or _ in user input stays literal.</summary>
    private static string Like(string value) =>
        "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("%", "\\%", StringComparison.Ordinal)
                   .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    internal static long ToUnixNanos(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds() * 1_000_000L +
        (value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond) * 100L;

    internal static DateTimeOffset FromUnixNanos(long nanos) =>
        DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000L)
            .AddTicks(nanos % 1_000_000L / 100L);

    internal static AuditEvent Map(SqliteDataReader reader)
    {
        var rawTimestamp = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var nanos = reader.IsDBNull(20) ? 0L : reader.GetInt64(20);

        return new AuditEvent
        {
            // "id TEXT PRIMARY KEY" is not NOT NULL: SQLite lets a text primary key hold NULL, and one such row used to
            // make this method throw and the whole page with it. The row is still an audit event, so it is shown, with
            // an empty id. Empty is also exactly where NULL sorts (below every string), so the keyset cursor built from
            // it ("id < ''" descending, "id > ''" ascending) still lands on the right side of its ties.
            Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            RawTimestamp = rawTimestamp,
            Timestamp = ParseTimestamp(rawTimestamp, nanos),
            TimestampNanos = nanos,
            Action = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Target = Nullable(reader, 3),
            Actor = Nullable(reader, 4),
            Details = Nullable(reader, 5),
            Severity = Nullable(reader, 6),
            StructuredJsonRaw = Nullable(reader, 7),
            Bucket = Nullable(reader, 8),
            Connector = Nullable(reader, 9),
            EventName = Nullable(reader, 10),
            AgentName = Nullable(reader, 11),
            ToolName = Nullable(reader, 12),
            SessionId = Nullable(reader, 13),
            RunId = Nullable(reader, 14),
            RequestId = Nullable(reader, 15),
            TraceId = Nullable(reader, 16),
            Source = Nullable(reader, 17),
            Signal = Nullable(reader, 18),
            BinaryVersion = Nullable(reader, 19),
        };
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseTimestamp(string raw, long nanos)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return nanos > 0 ? FromUnixNanos(nanos) : DateTimeOffset.MinValue;
    }
}
