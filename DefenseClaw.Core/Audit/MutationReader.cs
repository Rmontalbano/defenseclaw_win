using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>Where a <see cref="MutationItem"/> was recorded.</summary>
public enum MutationSource
{
    /// <summary>The <c>activity_events</c> table: the gateway's change journal, with before / after / diff and version fields.</summary>
    ActivityEvent,

    /// <summary>A canonical <c>compliance.activity</c> or <c>enforcement.action</c> row of <c>audit_events</c>: who and what, rarely a diff.</summary>
    Audit,
}

/// <summary>How a <see cref="MutationReader.ReadAsync"/> ended, short of throwing.</summary>
public enum MutationStatus
{
    /// <summary>The database was read (the list may still be empty).</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it has neither <c>activity_events</c> nor <c>audit_events</c>: nothing has been recorded.</summary>
    NoDatabase,
}

/// <summary>One configuration or policy change.</summary>
/// <param name="Id">The row's id (<c>activity_events.id</c> or <c>audit_events.id</c>).</param>
/// <param name="Source">Which table it came from.</param>
/// <param name="Timestamp">When it happened; <see cref="DateTimeOffset.MinValue"/> when the stored text could not be read.</param>
/// <param name="RawTimestamp">The stored text, for a tooltip.</param>
/// <param name="Actor">Who made the change (a user, <c>gateway_api</c>, <c>defenseclaw</c>); empty when unknown.</param>
/// <param name="Action">What was done (<c>config.change.applied</c>, <c>quarantine</c>).</param>
/// <param name="TargetType">The kind of thing changed (<c>policy</c>, <c>config</c>); empty for an audit row.</param>
/// <param name="Target">The thing changed (id, path); empty when none.</param>
/// <param name="Reason">Why, in the row's own words; for an audit row its <c>details</c>.</param>
/// <param name="BeforeJson">The value before the change, as stored; empty when not recorded.</param>
/// <param name="AfterJson">The value after the change, as stored; empty when not recorded.</param>
/// <param name="DiffJson">The recorded diff, as stored; empty when not recorded.</param>
/// <param name="VersionFrom">The version before; empty when not recorded.</param>
/// <param name="VersionTo">The version after; empty when not recorded.</param>
/// <param name="Connector">The connector the change belongs to; null when the row names none (an explicit connector filter hides it, like every other Mac screen).</param>
/// <param name="Bucket"><c>compliance.activity</c> / <c>enforcement.action</c> for an audit row, empty for an activity event.</param>
/// <param name="StructuredJson">An audit row's <c>structured_json</c> (capped); empty for an activity event.</param>
public sealed record MutationItem(
    string Id,
    MutationSource Source,
    DateTimeOffset Timestamp,
    string RawTimestamp,
    string Actor,
    string Action,
    string TargetType,
    string Target,
    string Reason,
    string BeforeJson,
    string AfterJson,
    string DiffJson,
    string VersionFrom,
    string VersionTo,
    string? Connector,
    string Bucket,
    string StructuredJson)
{
    /// <summary>True when the row recorded a before and / or an after value.</summary>
    public bool HasBeforeAfter => BeforeJson.Length > 0 || AfterJson.Length > 0;

    /// <summary>True when the row recorded a diff.</summary>
    public bool HasDiff => DiffJson.Length > 0;
}

/// <summary>What <see cref="MutationReader.ReadAsync"/> found.</summary>
/// <param name="Status">Whether <paramref name="Items"/> is a read of the database, or why not.</param>
/// <param name="Items">Newest first, both sources merged; empty when nothing is recorded.</param>
/// <param name="HasActivityTable">Whether <c>activity_events</c> exists (it is created by the gateway and may be empty or, on an old database, absent).</param>
/// <param name="HasMore">Whether either source had more rows than the window.</param>
/// <param name="Elapsed">How long the read took, open to last row.</param>
public sealed record MutationResult(MutationStatus Status, IReadOnlyList<MutationItem> Items, bool HasActivityTable, bool HasMore, TimeSpan Elapsed)
{
    public static MutationResult Empty(MutationStatus status, TimeSpan elapsed) => new(status, Array.Empty<MutationItem>(), false, false, elapsed);
}

/// <summary>
/// The configuration and policy change history, read from <c>audit.db</c>: the Mac's Activity ▸ Mutations list
/// (<c>AuditStore.activityEvents</c>: <c>SELECT * FROM activity_events ORDER BY timestamp DESC LIMIT ?</c>) plus the canonical
/// <c>compliance.activity</c> and <c>enforcement.action</c> rows of <c>audit_events</c>, which is where a change made from the
/// CLI or TUI shows up on a database whose <c>activity_events</c> is empty.
/// <para>
/// <b>Three statements, one snapshot.</b> Each is a search on an index whose order is the sort order, so each walks newest-first
/// and stops after <c>limit</c> rows with no temp B-tree: <c>activity_events</c> by <c>idx_activity_timestamp</c>, and each audit
/// bucket by <c>idx_audit_bucket_timestamp</c> (equality on <c>bucket</c>, then <c>timestamp</c>, then the implicit rowid). They are
/// merged in memory and cut to <c>limit</c>. A single <c>bucket IN (…)</c> statement would read both buckets and sort them
/// (<c>USE TEMP B-TREE FOR ORDER BY</c>); <see cref="ExplainAsync"/> reports the plan and the Core suite pins it. The audit arms
/// leave out <c>api-auth-failure</c> rows: they are in the <c>compliance.activity</c> bucket (469 of its 494 rows on the live database)
/// but record a rejected request, not a change, and would bury the changes.
/// </para>
/// <para>
/// <b>Tolerant.</b> A missing <c>activity_events</c> reads as none; a missing optional column reads as NULL; a database that predates
/// <c>bucket</c> contributes no audit rows. The long text columns are capped (<see cref="PayloadLimit"/>) in the statement, so a
/// huge before / after image never crosses into memory whole. Read-only (<c>Mode=ReadOnly</c>), off the caller's thread
/// (<see cref="ReaderOffload"/>), with a timeout and a token that both end a running statement (<c>sqlite3_interrupt</c>).
/// </para>
/// </summary>
public sealed class MutationReader
{
    /// <summary>How many rows <see cref="ReadAsync"/> returns unless told otherwise (the Mac's <c>activityEvents(limit: 500)</c>).</summary>
    public const int DefaultLimit = 500;

    /// <summary>The longest before / after / diff / structured text kept per row, in characters.</summary>
    public const int PayloadLimit = 262_144;

    /// <summary>The <c>compliance.activity</c> action that records a rejected request, not a change.</summary>
    public const string AuthFailureAction = "api-auth-failure";

    /// <summary>How long <see cref="ReadAsync"/> lets its statements run unless told otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The <c>audit_events.bucket</c> values that are changes.</summary>
    public static readonly IReadOnlyList<string> AuditBuckets = new[] { "compliance.activity", "enforcement.action" };

    private readonly string _connectionString;
    private long _reads;

    public MutationReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>Reads the newest <paramref name="limit"/> changes. Never blocks the caller's thread.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statements did not finish in <paramref name="timeout"/> and were interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<MutationResult> ReadAsync(int limit = DefaultLimit, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _ = Interlocked.Increment(ref _reads);

        var allowed = timeout ?? DefaultTimeout;
        if (allowed <= TimeSpan.Zero && allowed != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), allowed, "The timeout must be positive.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (allowed != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(allowed);
        }

        try
        {
            return await ReaderOffload.Run(() => ReadCoreAsync(limit, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The mutation history query did not finish within {allowed.TotalSeconds:0.##} s and was stopped."));
        }
    }

    private async Task<MutationResult> ReadCoreAsync(int limit, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (!File.Exists(DatabasePath))
        {
            return MutationResult.Empty(MutationStatus.NoDatabase, clock.Elapsed);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var schema = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!schema.HasActivity && !schema.CanReadAudit)
        {
            return MutationResult.Empty(MutationStatus.NoDatabase, clock.Elapsed);
        }

        var items = new List<MutationItem>();
        var hasMore = false;

        if (schema.HasActivity)
        {
            hasMore |= await ReadArmAsync(connection, ActivitySql(schema), null, limit, items, MapActivity, cancellationToken).ConfigureAwait(false);
        }

        if (schema.CanReadAudit)
        {
            foreach (var bucket in AuditBuckets)
            {
                hasMore |= await ReadArmAsync(connection, AuditSql(schema), bucket, limit, items, MapAudit, cancellationToken).ConfigureAwait(false);
            }
        }

        // Newest first; equal instants keep their statement order (activity events, then audit rows).
        var merged = items
            .Select((item, index) => (item, index))
            .OrderByDescending(pair => pair.item.Timestamp)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToList();
        if (merged.Count > limit)
        {
            merged.RemoveRange(limit, merged.Count - limit);
            hasMore = true;
        }

        return new MutationResult(MutationStatus.Ok, merged, schema.HasActivity, hasMore, clock.Elapsed);
    }

    /// <summary>Runs one arm for <c>limit + 1</c> rows into <paramref name="into"/>; true when it had more than <c>limit</c>.</summary>
    private static async Task<bool> ReadArmAsync(
        SqliteConnection connection,
        string sql,
        string? bucket,
        int limit,
        List<MutationItem> into,
        Func<SqliteDataReader, MutationItem> map,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.Parameters.AddWithValue("$limit", limit + 1);
        if (bucket is not null)
        {
            _ = command.Parameters.AddWithValue("$bucket", bucket);
            _ = command.Parameters.AddWithValue("$authFailure", AuthFailureAction);
        }

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > limit)
            {
                return true;
            }

            into.Add(map(reader));
        }

        return false;
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines of every statement <see cref="ReadAsync"/> runs on this database, in order
    /// (activity events, then each audit bucket). Empty when there is nothing to read. A regression test holds each to one index
    /// search with no temp B-tree.
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

        var schema = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();
        if (schema.HasActivity)
        {
            await ExplainOneAsync(connection, ActivitySql(schema), null, lines, cancellationToken).ConfigureAwait(false);
        }

        if (schema.CanReadAudit)
        {
            foreach (var bucket in AuditBuckets)
            {
                await ExplainOneAsync(connection, AuditSql(schema), bucket, lines, cancellationToken).ConfigureAwait(false);
            }
        }

        return lines;
    }

    private static async Task ExplainOneAsync(SqliteConnection connection, string sql, string? bucket, List<string> lines, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        _ = command.Parameters.AddWithValue("$limit", DefaultLimit + 1);
        if (bucket is not null)
        {
            _ = command.Parameters.AddWithValue("$bucket", bucket);
            _ = command.Parameters.AddWithValue("$authFailure", AuthFailureAction);
        }

        // Columns: id, parent, notused, detail.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }
    }

    /// <summary>What the database has, as far as the history cares.</summary>
    private sealed record Schema(HashSet<string> Activity, HashSet<string> Audit)
    {
        public bool HasActivity => Activity.Contains("id") && Activity.Contains("timestamp") && Activity.Contains("action");

        public bool CanReadAudit => Audit.Contains("id") && Audit.Contains("timestamp") && Audit.Contains("action") && Audit.Contains("bucket");
    }

    private static async Task<Schema> ProbeAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        new(
            await ColumnsOfAsync(connection, "activity_events", cancellationToken).ConfigureAwait(false),
            await ColumnsOfAsync(connection, "audit_events", cancellationToken).ConfigureAwait(false));

    /// <summary>The table's column names, without case; empty when the table does not exist.</summary>
    private static async Task<HashSet<string>> ColumnsOfAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        _ = command.Parameters.AddWithValue("$table", table);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static string Capped(HashSet<string> columns, string column) =>
        columns.Contains(column) ? $"substr({column}, 1, {PayloadLimit.ToString(CultureInfo.InvariantCulture)}) AS {column}" : $"NULL AS {column}";

    private static string Plain(HashSet<string> columns, string column) =>
        columns.Contains(column) ? column : $"NULL AS {column}";

    // Columns: 0 id, 1 timestamp, 2 actor, 3 action, 4 target_type, 5 target_id, 6 reason, 7 before, 8 after, 9 diff, 10 from, 11 to.
    private static string ActivitySql(Schema schema) =>
        $"""
        SELECT id, timestamp, {Plain(schema.Activity, "actor")}, action, {Plain(schema.Activity, "target_type")},
               {Plain(schema.Activity, "target_id")}, {Capped(schema.Activity, "reason")},
               {Capped(schema.Activity, "before_json")}, {Capped(schema.Activity, "after_json")},
               {Capped(schema.Activity, "diff_json")}, {Plain(schema.Activity, "version_from")},
               {Plain(schema.Activity, "version_to")}
          FROM activity_events
         ORDER BY timestamp DESC, rowid DESC LIMIT $limit
        """;

    // Columns: 0 id, 1 timestamp, 2 actor, 3 action, 4 target, 5 details, 6 structured_json, 7 connector, 8 bucket.
    private static string AuditSql(Schema schema) =>
        $"""
        SELECT id, timestamp, {Plain(schema.Audit, "actor")}, action, {Plain(schema.Audit, "target")},
               {Capped(schema.Audit, "details")}, {Capped(schema.Audit, "structured_json")},
               {Plain(schema.Audit, "connector")}, bucket
          FROM audit_events
         WHERE bucket = $bucket AND action <> $authFailure
         ORDER BY timestamp DESC, rowid DESC LIMIT $limit
        """;

    private static string Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateTimeOffset ParseTime(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static MutationItem MapActivity(SqliteDataReader reader)
    {
        var raw = Text(reader, 1);
        return new MutationItem(
            Id: Text(reader, 0),
            Source: MutationSource.ActivityEvent,
            Timestamp: ParseTime(raw),
            RawTimestamp: raw,
            Actor: Text(reader, 2),
            Action: Text(reader, 3),
            TargetType: Text(reader, 4),
            Target: Text(reader, 5),
            Reason: Text(reader, 6),
            BeforeJson: Text(reader, 7),
            AfterJson: Text(reader, 8),
            DiffJson: Text(reader, 9),
            VersionFrom: Text(reader, 10),
            VersionTo: Text(reader, 11),
            Connector: null,
            Bucket: string.Empty,
            StructuredJson: string.Empty);
    }

    private static MutationItem MapAudit(SqliteDataReader reader)
    {
        var raw = Text(reader, 1);
        var connector = Text(reader, 7).Trim();
        return new MutationItem(
            Id: Text(reader, 0),
            Source: MutationSource.Audit,
            Timestamp: ParseTime(raw),
            RawTimestamp: raw,
            Actor: Text(reader, 2),
            Action: Text(reader, 3),
            TargetType: string.Empty,
            Target: Text(reader, 4),
            Reason: Text(reader, 5),
            BeforeJson: string.Empty,
            AfterJson: string.Empty,
            DiffJson: string.Empty,
            VersionFrom: string.Empty,
            VersionTo: string.Empty,
            Connector: connector.Length == 0 ? null : connector,
            Bucket: Text(reader, 8),
            StructuredJson: Text(reader, 6));
    }
}
