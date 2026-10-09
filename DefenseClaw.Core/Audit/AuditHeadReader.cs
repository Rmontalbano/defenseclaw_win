using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>How an <see cref="AuditHeadReader.ReadAsync"/> ended, short of throwing.</summary>
public enum AuditHeadStatus
{
    /// <summary>The table was read; <see cref="AuditHead.HeadRowId"/> is its newest row (0 when it is empty).</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no <c>audit_events</c> table: nothing has been recorded.</summary>
    NoDatabase,
}

/// <summary>What <see cref="AuditHeadReader.ReadAsync"/> found.</summary>
/// <param name="Status">Whether the rest is the table's, or why there is none.</param>
/// <param name="HeadRowId">The newest <c>rowid</c> in <c>audit_events</c>; 0 for an empty table and for no database.</param>
/// <param name="Newer">How many rows have a <c>rowid</c> above the marker that was asked about, counted only up to the limit that was asked for.</param>
public sealed record AuditHead(AuditHeadStatus Status, long HeadRowId, int Newer)
{
    /// <summary>No database, no rows.</summary>
    public static AuditHead None { get; } = new(AuditHeadStatus.NoDatabase, 0, 0);
}

/// <summary>
/// "How many audit rows are newer than the last one I looked at?" - the number behind the Audit sidebar badge (CUST-265) - answered with
/// one statement of two index probes, and with no statement at all when the shared <see cref="AuditChangeProbe"/> says the file has not
/// moved since the last answer.
/// <para>
/// <b>The marker is a <c>rowid</c>, not a time.</b> A row carries the instant the gateway made it, and the gateway can commit it later: on a
/// real database 5% of the rows are committed behind a newer one, up to two minutes behind, and most of them are scan findings (see the
/// Audit live refresh). A "newer than the last timestamp I saw" marker would never count those, however long they waited. The rowid is the
/// order of insertion: <c>audit_events</c> is a rowid table (its primary key is the text <c>id</c>), so a row committed after the marker
/// was taken has a larger rowid whatever time it carries, and the count is exactly "rows added since". Retention deletes the oldest rows,
/// which are all below the marker and change nothing above it. A rowid that goes <em>down</em> (the table was emptied, or <c>VACUUM</c>
/// renumbered it) shows as a head below the marker, and the caller starts again from the head instead of counting a renumbered table as new.
/// </para>
/// <para>
/// <b>Cost.</b> <c>MAX(rowid)</c> is the right-most leaf of the table's B-tree; <c>COUNT(*)</c> over <c>rowid &gt; $marker LIMIT $limit</c> is
/// one seek and at most <c>$limit</c> cells - so the count is <em>bounded</em>, and a badge that stops at 99 asks for 100. Neither reads
/// a row's text (<c>details</c>, <c>structured_json</c>, <c>payload_json</c> can be megabytes): the plan is pinned by a test
/// (<see cref="ExplainAsync"/>) because a scan here would be a read of the whole 6.7 GB table.
/// </para>
/// <para>
/// <b>An unchanged database is not read again.</b> The answer is remembered under the probe's stamp (taken <em>before</em> the read, see
/// <see cref="SnapshotMemo{TKey, TValue}"/>) per (marker, limit): a call that finds the same stamp returns it with no connection and no
/// statement (<see cref="UnchangedReads"/> counts those, <see cref="StatementCount"/> the statements that did run). Without a probe nothing
/// is remembered. Read-only (<c>Mode=ReadOnly</c>), off the caller's thread, and stoppable by a token or the timeout, like every reader of
/// the file. No timer, no thread, no watcher: it does something only when asked.
/// </para>
/// </summary>
public sealed class AuditHeadReader
{
    /// <summary>How long a read may run when its caller names no limit; the app passes <see cref="ReaderTimeouts"/>' badge-class limit.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // Two scalar sub-selects. The first is SQLite's MIN/MAX shortcut on the rowid (a descent to the last leaf); the second stops
    // after $limit rows, so its cost does not depend on how far behind the marker is. $limit 0 makes the count free (a head-only read).
    private const string Sql =
        """
        SELECT (SELECT MAX(rowid) FROM audit_events),
               (SELECT COUNT(*) FROM (SELECT 1 FROM audit_events WHERE rowid > $marker LIMIT $limit))
        """;

    private readonly string _connectionString;
    private readonly AuditChangeProbe? _probe;
    private readonly TimeSpan _readTimeout;
    private readonly SnapshotMemo<(long Marker, int Limit), AuditHead> _results;

    private long _reads;
    private long _statements;
    private long _unchangedReads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="probe">The change probe of this database, shared with the other readers of it; null reads every time.</param>
    /// <param name="readTimeout">How long a read may run when its caller names no limit; <see cref="DefaultTimeout"/> when null. <see cref="Timeout.InfiniteTimeSpan"/> is no limit.</param>
    /// <param name="timeProvider">The clock the remembered answers' age is measured on; the system's when null.</param>
    public AuditHeadReader(string databasePath, AuditChangeProbe? probe = null, TimeSpan? readTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _readTimeout = readTimeout ?? DefaultTimeout;
        if (_readTimeout <= TimeSpan.Zero && _readTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout), _readTimeout, "The timeout must be positive.");
        }

        DatabasePath = databasePath;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
        _probe = probe;
        _results = new SnapshotMemo<(long Marker, int Limit), AuditHead>(capacity: 4, time: timeProvider);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called; the idle-cost tests hold it against the number of ticks.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>How many statements this reader has run over its life. A read of an unchanged database must not add to it.</summary>
    public long StatementCount => Interlocked.Read(ref _statements);

    /// <summary>How many reads were answered without running the statement because the probe said the database had not changed.</summary>
    public long UnchangedReads => Interlocked.Read(ref _unchangedReads);

    /// <summary>
    /// Reads the head and counts the rows above <paramref name="marker"/>, up to <paramref name="limit"/>. A head-only read passes
    /// <c>long.MaxValue</c> and a limit of 0 (nothing is above that, and nothing is counted).
    /// </summary>
    /// <param name="marker">The rowid last looked at; rows with a larger one are "newer".</param>
    /// <param name="limit">The most rows to count, 0 or more. A badge that shows "99+" asks for 100 and learns only that there are at least that many.</param>
    /// <param name="timeout">How long the statement may run; the reader's own limit when null.</param>
    /// <param name="cancellationToken">Stops a running statement, not only the wait for it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statement did not finish in time and was interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<AuditHead> ReadAsync(long marker, int limit, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _ = Interlocked.Increment(ref _reads);

        var allowed = timeout ?? _readTimeout;
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
            return await ReaderOffload.Run(() => ReadCoreAsync(marker, limit, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The audit head query did not finish within {allowed.TotalSeconds:0.##} s and was stopped."));
        }
    }

    private async Task<AuditHead> ReadCoreAsync(long marker, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return AuditHead.None;
        }

        // The stamp before anything is read (see SnapshotMemo): a commit that lands after it makes the next sample differ, so the worst
        // case is one read too many. An unknown stamp is never stored and never matches.
        var stamp = _probe?.Sample() ?? AuditStamp.Unknown;
        var key = (marker, limit);
        if (_results.TryGet(key, stamp, out var remembered))
        {
            _ = Interlocked.Increment(ref _unchangedReads);
            return remembered;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.AddWithValue("$marker", marker);
        command.Parameters.AddWithValue("$limit", limit);

        _ = Interlocked.Increment(ref _statements);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var head = 0L;
            var newer = 0L;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                head = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                newer = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }

            return Remember(key, stamp, new AuditHead(AuditHeadStatus.Ok, head, (int)Math.Min(newer, int.MaxValue)));
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            // A file the gateway has created but not yet given its schema: nothing recorded. The stamp moves when the table appears.
            return Remember(key, stamp, AuditHead.None);
        }
    }

    private AuditHead Remember((long Marker, int Limit) key, AuditStamp stamp, AuditHead result)
    {
        _results.Store(key, stamp, result);
        return result;
    }

    /// <summary>SQLITE_ERROR with SQLite's own "no such table" text (it is not translated).</summary>
    private static bool IsMissingTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines of the exact statement <see cref="ReadAsync"/> runs on this database. Empty when there is no
    /// database or no table. A test holds the plan to searches on the rowid with no scan: the alternative reads the whole table.
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

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + Sql;
        command.Parameters.AddWithValue("$marker", 0L);
        command.Parameters.AddWithValue("$limit", 100);

        // Columns: id, parent, notused, detail.
        var lines = new List<string>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(reader.GetString(3));
            }
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            return Array.Empty<string>();
        }

        return lines;
    }
}
