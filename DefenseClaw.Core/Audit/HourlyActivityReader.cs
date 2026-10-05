using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>How a <see cref="HourlyActivityReader.ReadAsync"/> ended, short of throwing.</summary>
public enum HourlyActivityStatus
{
    /// <summary>The window was read; every hour is present, and may legitimately be zero.</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no readable <c>audit_events</c> table: nothing has been recorded.</summary>
    NoDatabase,

    /// <summary>
    /// The database has no <c>idx_audit_action_timestamp (action, timestamp)</c> index, so the hourly counts would be a scan of the
    /// whole table. Nothing is read: a chart that costs tens of seconds on a large database is worse than no chart.
    /// </summary>
    NoIndex,
}

/// <summary>One hour of the chart: decisions that let the agent through, and decisions that stopped it.</summary>
/// <param name="HourStart">The start of the hour, UTC (the stored timestamps are UTC, so an hour is one prefix of the text).</param>
/// <param name="Allowed">Hook decisions in the hour that were not blocks.</param>
/// <param name="Blocked">Block-class events in the hour.</param>
public readonly record struct HourlyBucket(DateTimeOffset HourStart, int Allowed, int Blocked)
{
    public int Total => Allowed + Blocked;
}

/// <summary>What <see cref="HourlyActivityReader.ReadAsync"/> counted.</summary>
/// <param name="Status">Whether the hours are a read of the window, or why there is nothing to show.</param>
/// <param name="Hours">Oldest first, exactly <see cref="HourlyActivityReader.Hours"/> of them, each present even when zero (a gap in a bar chart is an hour with no bar, not a missing hour).</param>
/// <param name="From">The start of the first hour.</param>
/// <param name="To">The end of the last hour (the start of the hour after it).</param>
/// <param name="Elapsed">How long the read took, open to last row; what a slow database is diagnosed from.</param>
public sealed record HourlyActivity(
    HourlyActivityStatus Status,
    IReadOnlyList<HourlyBucket> Hours,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Elapsed)
{
    public int Allowed => Hours.Sum(static h => h.Allowed);

    public int Blocked => Hours.Sum(static h => h.Blocked);

    public int Total => Allowed + Blocked;

    /// <summary>The tallest hour, for scaling a chart.</summary>
    public int Peak => Hours.Count == 0 ? 0 : Hours.Max(static h => h.Total);

    /// <summary>Nothing recorded: what a missing database, or one without the index, reads as.</summary>
    public static HourlyActivity None(HourlyActivityStatus status, DateTimeOffset from, DateTimeOffset to, int hours, TimeSpan elapsed) =>
        new(status, EmptyHours(from, hours), from, to, elapsed);

    internal static IReadOnlyList<HourlyBucket> EmptyHours(DateTimeOffset from, int hours)
    {
        var list = new HourlyBucket[hours];
        for (var i = 0; i < hours; i++)
        {
            list[i] = new HourlyBucket(from.AddHours(i), 0, 0);
        }

        return list;
    }
}

/// <summary>
/// The Overview's "Activity - last 24 h" chart: hook decisions per hour, allowed against blocked, read from <c>audit.db</c>.
/// <para>
/// <b>What is counted.</b> The Mac's chart counts every audit row and calls it "blocked" when the action contains <c>block</c> or
/// <c>reject</c>, which on this database means a chart of telemetry ingestion (on a live install 102 k of the 183 k rows of a day are
/// <c>correlation.relationship.changed</c>). This one counts the decisions the guardrail made: a row whose <c>action</c> is
/// <c>connector-hook</c> is one hook call, and it is <i>blocked</i> by the same rule as the tray's and the Overview's Blocks tile
/// (<see cref="RecentAuditMetricsReader.IsBlock"/>: a <c>details</c> decision of <c>block</c> or <c>deny</c>), while the block-class
/// events that are not hook calls (<see cref="BlockActions"/>) are blocked too. Anything else, in particular a hook call that raised
/// an alert without blocking, is allowed.
/// </para>
/// <para>
/// <b>Query shape (measured on the live 6.7 GB database, 183 k rows in the last 24 h).</b> The Mac's single
/// <c>GROUP BY substr(timestamp, 1, 13), CASE ... action LIKE ...</c> walks <c>idx_audit_timestamp</c> and fetches every row of the day
/// for its <c>action</c>: 13.7 s. Here each kind of row is one equality on <c>action</c>, which is the first column of
/// <c>idx_audit_action_timestamp (action, timestamp DESC)</c>, so each is a range seek that reads only its own rows: the hook rows
/// (16.6 k, with the start of <c>details</c> fetched to judge the decision, about 0.11 s) and the block-class events (a covering
/// index scan, about a millisecond). The index is forced (<c>INDEXED BY</c>) and its presence checked first, so a database without
/// it reports <see cref="HourlyActivityStatus.NoIndex"/> instead of quietly scanning. <see cref="ExplainAsync"/> returns the plans for a
/// test to pin.
/// </para>
/// <para>
/// <b>Read-only, off the caller's thread, stoppable,</b> exactly as <see cref="RecentAuditMetricsReader"/> and
/// <see cref="AlertQueueReader"/>: a <c>Mode=ReadOnly</c> connection, the query through <see cref="ReaderOffload"/>, a token and a timeout
/// that end a running statement with <c>sqlite3_interrupt</c>, and both statements inside one read transaction so the two halves see one
/// snapshot.
/// </para>
/// </summary>
public sealed class HourlyActivityReader
{
    /// <summary>The Mac's window: the last 24 hours.</summary>
    public const int DefaultHours = 24;

    /// <summary>The hook-call action; the rows the decisions are read from.</summary>
    public const string HookAction = "connector-hook";

    /// <summary>The index both statements are driven by.</summary>
    public const string IndexName = "idx_audit_action_timestamp";

    /// <summary>How much of <c>details</c> is read per hook row; the decision token is among its first few dozen characters.</summary>
    public const int DetailsLimit = 256;

    /// <summary>How long <see cref="ReadAsync"/> lets the statements run unless told otherwise; the read itself takes a fraction of a second.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The block-class events that are not hook calls, matched exactly (an exact match is what lets the index answer them). The first four
    /// are <see cref="RecentAuditMetricsReader.IsBlock"/>'s; <c>install-blocked</c> is the gateway's record of an install it refused.
    /// </summary>
    public static readonly IReadOnlyList<string> BlockActions = new[] { "block", "guardrail-block", "deny", "quarantine", "install-blocked" };

    private const string HourFormat = "yyyy-MM-dd'T'HH";
    private const string LegacyHourFormat = "yyyy-MM-dd HH";
    private const string TextBoundFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private long _reads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="hours">How many hourly buckets the window has, ending with the current hour; <see cref="DefaultHours"/> in the app.</param>
    /// <param name="timeProvider">The clock the window is anchored to; the system one when null.</param>
    public HourlyActivityReader(string databasePath, int hours = DefaultHours, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(hours, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hours, 24 * 14);
        DatabasePath = databasePath;
        Hours = hours;
        _time = timeProvider ?? TimeProvider.System;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>How many hourly buckets a read returns.</summary>
    public int Hours { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called, counted when the call is made; the idle-cost tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>
    /// Counts the window: two statements, one snapshot. Never blocks the caller's thread.
    /// </summary>
    /// <param name="timeout">How long the statements may run; <see cref="DefaultTimeout"/> when null, <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">Stops a running statement, not only the wait for it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statements did not finish in <paramref name="timeout"/> and were interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<HourlyActivity> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);

        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "The timeout must be positive.");
        }

        var to = CurrentHourEnd();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limit != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            return await ReaderOffload.Run(() => ReadCoreAsync(to, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The hourly activity query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines for the two statements <see cref="ReadAsync"/> runs, hook rows first. Empty when there is
    /// no database, no usable table or no index. A regression test holds both to a search on <see cref="IndexName"/> with no table scan.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(CancellationToken cancellationToken = default)
    {
        var to = CurrentHourEnd();
        return ReaderOffload.Run(() => ExplainCoreAsync(to, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The UTC hour of a stored timestamp text (its first thirteen characters, <c>2026-09-30T22</c>), or null when it does not read as
    /// one (a legacy spelling with a space is accepted). Public so the unit tests can hold the bucketing to the text the database holds.
    /// </summary>
    public static DateTimeOffset? ParseHour(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return null;
        }

        if (DateTime.TryParseExact(prefix, HourFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hour) ||
            DateTime.TryParseExact(prefix, LegacyHourFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(hour, DateTimeKind.Utc));
        }

        return null;
    }

    /// <summary>The end of the current UTC hour: the window's right edge.</summary>
    private DateTimeOffset CurrentHourEnd()
    {
        var now = _time.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
    }

    private async Task<HourlyActivity> ReadCoreAsync(DateTimeOffset to, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var from = to.AddHours(-Hours);

        if (!File.Exists(DatabasePath))
        {
            return HourlyActivity.None(HourlyActivityStatus.NoDatabase, from, to, Hours, clock.Elapsed);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var shape = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (shape == Shape.NoTable)
        {
            return HourlyActivity.None(HourlyActivityStatus.NoDatabase, from, to, Hours, clock.Elapsed);
        }

        if (shape == Shape.NoIndex)
        {
            return HourlyActivity.None(HourlyActivityStatus.NoIndex, from, to, Hours, clock.Elapsed);
        }

        var allowed = new int[Hours];
        var blocked = new int[Hours];

        // One read transaction, so the hook rows and the block events are of one moment.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var hooks = connection.CreateCommand())
        {
            hooks.Transaction = transaction;
            // nosemgrep: csharp-sqli -- constant: BuildHookSql interpolates only the const IndexName, HookAction and DetailsLimit; $from and $to are bound
            hooks.CommandText = BuildHookSql(shape == Shape.WithDetails);
            Bind(hooks, from, to);

            await using var reader = await hooks.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var slot = SlotOf(reader.IsDBNull(0) ? null : reader.GetString(0), from);
                if (slot < 0)
                {
                    continue;
                }

                var details = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (RecentAuditMetricsReader.IsBlock(HookAction, details))
                {
                    blocked[slot]++;
                }
                else
                {
                    allowed[slot]++;
                }
            }
        }

        await using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = BuildBlockSql();
            Bind(events, from, to);

            await using var reader = await events.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var slot = SlotOf(reader.IsDBNull(0) ? null : reader.GetString(0), from);
                if (slot >= 0)
                {
                    blocked[slot] += (int)Math.Min(int.MaxValue, reader.GetInt64(1));
                }
            }
        }

        var hours = new HourlyBucket[Hours];
        for (var i = 0; i < Hours; i++)
        {
            hours[i] = new HourlyBucket(from.AddHours(i), allowed[i], blocked[i]);
        }

        return new HourlyActivity(HourlyActivityStatus.Ok, hours, from, to, clock.Elapsed);
    }

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return Array.Empty<string>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var shape = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (shape is Shape.NoTable or Shape.NoIndex)
        {
            return Array.Empty<string>();
        }

        var from = to.AddHours(-Hours);
        var lines = new List<string>();
        foreach (var sql in new[] { BuildHookSql(shape == Shape.WithDetails), BuildBlockSql() })
        {
            await using var command = connection.CreateCommand();
            // nosemgrep: csharp-sqli -- constant: the EXPLAIN prefix and BuildHookSql / BuildBlockSql (const IndexName and HookAction, the fixed BlockActions list); $from and $to are bound
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            Bind(command, from, to);

            // Columns: id, parent, notused, detail.
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(reader.GetString(3));
            }
        }

        return lines;
    }

    /// <summary>Which hour of the window a stored hour prefix falls in, or -1 when it is outside it or unreadable.</summary>
    private int SlotOf(string? prefix, DateTimeOffset from)
    {
        if (ParseHour(prefix) is not { } hour)
        {
            return -1;
        }

        var slot = (int)Math.Round((hour - from).TotalHours);
        return slot >= 0 && slot < Hours ? slot : -1;
    }

    private static void Bind(SqliteCommand command, DateTimeOffset from, DateTimeOffset to)
    {
        // The stored text is RFC 3339 UTC ("2026-09-30T22:51:01.6158834Z"): a bound without a zone or fraction sorts just before every
        // timestamp of its own second, which is what a ">=" needs, and "<" the next hour's first second.
        command.Parameters.AddWithValue("$from", from.UtcDateTime.ToString(TextBoundFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.UtcDateTime.ToString(TextBoundFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>The hook rows: their hour, and the start of <c>details</c> when the database has one (NULL, so nothing blocks on the action alone, when it has not).</summary>
    private static string BuildHookSql(bool withDetails)
    {
        var details = withDetails
            ? string.Create(CultureInfo.InvariantCulture, $"substr(details, 1, {DetailsLimit})")
            : "NULL";

        return $"SELECT substr(timestamp, 1, 13), {details} FROM audit_events INDEXED BY {IndexName} " +
               $"WHERE action = '{HookAction}' AND timestamp >= $from AND timestamp < $to";
    }

    private static string BuildBlockSql()
    {
        var actions = string.Join(", ", BlockActions.Select(static a => "'" + a + "'"));
        return $"SELECT substr(timestamp, 1, 13), COUNT(*) FROM audit_events INDEXED BY {IndexName} " +
               $"WHERE action IN ({actions}) AND timestamp >= $from AND timestamp < $to GROUP BY 1";
    }

    private enum Shape
    {
        NoTable,
        NoIndex,
        WithDetails,
        WithoutDetails,
    }

    /// <summary>One small schema read: the columns the statements need, and whether the index that makes them cheap is there.</summary>
    private static async Task<Shape> ProbeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('audit_events')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = columns.Add(reader.GetString(0));
            }
        }

        if (!columns.Contains("action") || !columns.Contains("timestamp"))
        {
            return Shape.NoTable;
        }

        // The index must lead with (action, timestamp): a same-named index of another shape would make INDEXED BY a scan.
        var indexColumns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno";
            command.Parameters.AddWithValue("$index", IndexName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                indexColumns.Add(reader.GetString(0));
            }
        }

        if (indexColumns.Count < 2 ||
            !string.Equals(indexColumns[0], "action", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(indexColumns[1], "timestamp", StringComparison.OrdinalIgnoreCase))
        {
            return Shape.NoIndex;
        }

        return columns.Contains("details") ? Shape.WithDetails : Shape.WithoutDetails;
    }
}
