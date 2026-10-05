using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>How an <see cref="AlertQueueReader.ReadAsync"/> ended, short of throwing.</summary>
public enum AlertQueueStatus
{
    /// <summary>The queue was read; <see cref="AlertQueueResult.Counts"/> is its window (empty when nothing is waiting).</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no <c>audit_events</c> table: nothing has been recorded, so nothing is waiting.</summary>
    NoDatabase,

    /// <summary>
    /// The database predates the v8 schema (no <c>bucket</c> / <c>event_name</c> column), where "a finding" cannot be told
    /// from telemetry. <see cref="AlertQueueResult.Counts"/> is empty; the caller falls back to the gateway's <c>/alerts</c> list
    /// (<see cref="AlertCounts.FromGateway"/>).
    /// </summary>
    LegacySchema,
}

/// <summary>What <see cref="AlertQueueReader.ReadAsync"/> found.</summary>
/// <param name="Status">Whether <paramref name="Counts"/> is the queue, or why it is not.</param>
/// <param name="Counts">The window's counts and its newest rows; <see cref="AlertCounts.Empty"/> unless <paramref name="Status"/> is <see cref="AlertQueueStatus.Ok"/>.</param>
/// <param name="Elapsed">How long the read took, open to last row; what a slow database is diagnosed from.</param>
public sealed record AlertQueueResult(AlertQueueStatus Status, AlertCounts Counts, TimeSpan Elapsed);

/// <summary>
/// The alert queue, read from <c>audit.db</c>: the Mac app's single definition of "unacknowledged findings"
/// (<c>AuditStore.alertQueueEvents</c>, the TUI's <c>list_alert_summaries(500)</c>), which every badge, chip, tray count
/// and the Alerts panel must agree on.
/// <para>
/// <b>The definition.</b> The newest <see cref="DefaultWindowLimit"/> <c>audit_events</c> rows such that
/// (1) the severity is above INFO — CRITICAL, HIGH, MEDIUM or LOW, compared without case (WARN and ERROR are not findings);
/// (2) the row is a finding, not telemetry: <c>bucket IS NULL</c> (a row from before buckets existed) or
/// <c>bucket = 'security.finding'</c> with <c>event_name = 'finding.observed'</c>;
/// (3) <c>action NOT LIKE 'dismiss%'</c> (a dismissal record is not itself an alert);
/// (4) the id is not in <c>alert_acknowledgement_projection</c> — the gateway's record of what was acknowledged or
/// dismissed. That table is missing on a database that never had one, which reads as "nothing acknowledged".
/// Ordered by <c>timestamp</c> then <c>rowid</c>, newest first. One difference from the Mac: the severity test is part of the
/// query, so the window holds 500 <em>alerts</em>; the Mac takes the newest 500 rows including INFO and ERROR and drops those
/// afterwards, which can leave it fewer.
/// </para>
/// <para>
/// <b>Query shape (measured on the live 6.7 GB / 577 k-row database, where 34.5 k rows are above INFO and 8.2 k are
/// findings).</b> The Mac's single statement has an <c>OR</c> between its two bucket conditions, so SQLite answers it with a
/// multi-index OR: every finding row's rowid is gathered and fetched, then sorted — 55 ms warm. Written as two arms instead,
/// one per condition, each with its own <c>ORDER BY timestamp DESC, rowid DESC LIMIT n</c>, and merged
/// (<c>UNION ALL</c>, then the same order and limit), each arm is a search on an index whose order is the sort order
/// (<c>idx_audit_event_name_timestamp</c> / <c>idx_audit_bucket_timestamp</c>: equality on the first column, then
/// <c>timestamp</c>, then the implicit rowid), so it walks newest-first and stops after <c>n</c> matches with no temp B-tree:
/// 3 ms warm, the same 500 rows in the same order (see the plan in <see cref="ExplainAsync"/>, which the Core suite pins).
/// The severity test is <c>UPPER(severity) IN (…)</c> — a residual filter here, never the driving index, so the case-folding
/// that keeps <see cref="AuditReader"/> away from <c>UPPER</c> costs nothing. The database has no <c>sqlite_stat1</c>, so the
/// planner's choice rests on its defaults; the arms are simple enough that it does not vary, and the tests assert it.
/// </para>
/// <para>
/// <b>Read-only, off the caller's thread, stoppable.</b> The connection is <c>Mode=ReadOnly</c> (the file is the gateway's);
/// the query runs through <see cref="ReaderOffload"/> so the caller gets its thread back at once; <c>ReadAsync</c> takes a
/// token and a timeout, both of which end the running statement with <c>sqlite3_interrupt</c>. A schema probe
/// (<c>pragma_table_info</c>, well under a millisecond) precedes every read, so a database upgraded under a running app is
/// read with the right shape the next time.
/// </para>
/// </summary>
public sealed class AlertQueueReader
{
    /// <summary>The Mac's window: how many alerts count, and the most the badge can show.</summary>
    public const int DefaultWindowLimit = 500;

    /// <summary>The longest <see cref="AlertQueueItem.Target"/> kept. Targets are paths and URLs; the list needs the start of one.</summary>
    public const int TargetLimit = 512;

    /// <summary>How long <see cref="ReadAsync"/> lets the statement run unless told otherwise; the read itself takes milliseconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const string FindingArm = "e.bucket = 'security.finding' AND e.event_name = 'finding.observed'";
    private const string LegacyArm = "e.bucket IS NULL";

    private readonly string _connectionString;
    private readonly int _windowLimit;
    private long _reads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="windowLimit">How many alerts make up the window; <see cref="DefaultWindowLimit"/> (the Mac's) in the app, smaller in a test.</param>
    public AlertQueueReader(string databasePath, int windowLimit = DefaultWindowLimit)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLimit, 1);
        DatabasePath = databasePath;
        _windowLimit = windowLimit;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called, counted when the call is made; the idle-cost tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>
    /// Reads the queue: one statement, one snapshot. Never blocks the caller's thread.
    /// </summary>
    /// <param name="newestLimit">How many rows <see cref="AlertCounts.Newest"/> keeps (the tallies always cover the whole window).</param>
    /// <param name="timeout">How long the statement may run; <see cref="DefaultTimeout"/> when null, <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">Stops a running statement, not only the wait for it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statement did not finish in <paramref name="timeout"/> and was interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<AlertQueueResult> ReadAsync(
        int newestLimit = AlertCounts.DefaultNewestLimit,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newestLimit);
        _ = Interlocked.Increment(ref _reads);

        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "The timeout must be positive.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limit != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            return await ReaderOffload.Run(() => ReadCoreAsync(newestLimit, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The alert queue query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    private async Task<AlertQueueResult> ReadCoreAsync(int newestLimit, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        if (!File.Exists(DatabasePath))
        {
            return new AlertQueueResult(AlertQueueStatus.NoDatabase, AlertCounts.Empty, clock.Elapsed);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var schema = await ProbeSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!schema.HasAuditTable)
        {
            return new AlertQueueResult(AlertQueueStatus.NoDatabase, AlertCounts.Empty, clock.Elapsed);
        }

        if (!schema.SupportsQueue)
        {
            return new AlertQueueResult(AlertQueueStatus.LegacySchema, AlertCounts.Empty, clock.Elapsed);
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: BuildSql picks fixed fragments by whether the database has a column or table (a boolean, never the name itself); $limit is bound
        command.CommandText = BuildSql(schema);

        // One past the window, so "there are more" needs no second query.
        command.Parameters.AddWithValue("$limit", _windowLimit + 1);

        var window = new List<AlertQueueItem>(Math.Min(_windowLimit + 1, 512));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                window.Add(Map(reader));
            }
        }

        var hasMore = window.Count > _windowLimit;
        if (hasMore)
        {
            window.RemoveRange(_windowLimit, window.Count - _windowLimit);
        }

        return new AlertQueueResult(AlertQueueStatus.Ok, new AlertCounts(window, hasMore, newestLimit), clock.Elapsed);
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines for the exact statement <see cref="ReadAsync"/> runs on this database.
    /// Empty when there is no database or its schema cannot serve the queue. A regression test holds the plan to two index
    /// searches with no temp B-tree inside an arm: on the live database the alternative is 55 ms instead of 3, and on
    /// ten synthetic rows it is invisible.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return Array.Empty<string>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var schema = await ProbeSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!schema.HasAuditTable || !schema.SupportsQueue)
        {
            return Array.Empty<string>();
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: the EXPLAIN prefix and BuildSql, as annotated above; $limit is bound
        command.CommandText = "EXPLAIN QUERY PLAN " + BuildSql(schema);
        command.Parameters.AddWithValue("$limit", _windowLimit + 1);

        // Columns: id, parent, notused, detail.
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    /// <summary>What the database has, as far as the queue cares.</summary>
    private sealed record Schema(HashSet<string> AuditColumns, bool HasProjection)
    {
        public bool HasAuditTable => AuditColumns.Count > 0;

        public bool Has(string column) => AuditColumns.Contains(column);

        /// <summary>
        /// The queue needs to tell a finding from telemetry (<c>bucket</c> + <c>event_name</c>, schema v8) and the columns it lists.
        /// Anything less is the old world, where the gateway's own list is the only finding-aware source.
        /// </summary>
        public bool SupportsQueue =>
            Has("bucket") && Has("event_name") && Has("id") && Has("timestamp") && Has("action") && Has("severity");
    }

    private static async Task<Schema> ProbeSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var audit = await ColumnsOfAsync(connection, "audit_events", cancellationToken).ConfigureAwait(false);
        var projection = await ColumnsOfAsync(connection, "alert_acknowledgement_projection", cancellationToken).ConfigureAwait(false);
        return new Schema(audit, projection.Contains("alert_id"));
    }

    /// <summary>The table's column names, without case; empty when the table does not exist (<c>pragma_table_info</c> of a missing table is no rows).</summary>
    private static async Task<HashSet<string>> ColumnsOfAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// The statement: two newest-first arms merged (see the type documentation). Optional parts follow the database: no
    /// acknowledgement table, no acknowledgement condition; no <c>connector</c> or <c>target</c> column, a NULL in their place.
    /// </summary>
    private static string BuildSql(Schema schema)
    {
        var connector = schema.Has("connector") ? "e.connector" : "NULL";
        var target = schema.Has("target") ? $"substr(e.target, 1, {TargetLimit.ToString(CultureInfo.InvariantCulture)})" : "NULL";
        var acknowledged = schema.HasProjection
            ? " AND NOT EXISTS (SELECT 1 FROM alert_acknowledgement_projection p WHERE p.alert_id = e.id)"
            : string.Empty;

        var findings = Arm(FindingArm);
        var legacy = Arm(LegacyArm);

        return
            $"""
            SELECT rid, id, ts, action, target, severity, connector FROM (
                SELECT * FROM ({findings})
                UNION ALL
                SELECT * FROM ({legacy})
            ) ORDER BY ts DESC, rid DESC LIMIT $limit
            """;

        string Arm(string bucketCondition) =>
            $"""
            SELECT e.rowid AS rid, e.id AS id, e.timestamp AS ts, e.action AS action, {target} AS target, e.severity AS severity, {connector} AS connector
                FROM audit_events e
                WHERE {bucketCondition}
                  AND UPPER(e.severity) IN ('CRITICAL', 'HIGH', 'MEDIUM', 'LOW')
                  AND e.action NOT LIKE 'dismiss%'{acknowledged}
                ORDER BY e.timestamp DESC, e.rowid DESC LIMIT $limit
            """;
    }

    private static AlertQueueItem Map(SqliteDataReader reader)
    {
        var raw = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var connector = reader.IsDBNull(6) ? null : reader.GetString(6).Trim();
        var target = reader.IsDBNull(4) ? null : reader.GetString(4);

        return new AlertQueueItem(
            Id: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Severity: AuditSeverityExtensions.Parse(reader.IsDBNull(5) ? null : reader.GetString(5)),
            Action: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Target: string.IsNullOrEmpty(target) ? null : target,
            Connector: string.IsNullOrEmpty(connector) ? null : connector,
            Timestamp: DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue);
    }
}
