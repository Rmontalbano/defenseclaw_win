using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>How a <see cref="RecentAuditMetricsReader.ReadAsync"/> ended, short of throwing.</summary>
public enum RecentAuditMetricsStatus
{
    /// <summary>The window was read; the counts may legitimately be zero.</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no readable <c>audit_events</c> table: nothing has been recorded.</summary>
    NoDatabase,
}

/// <summary>What <see cref="RecentAuditMetricsReader.ReadAsync"/> counted.</summary>
/// <param name="Status">Whether the counts are a read of the window, or why there is nothing to read.</param>
/// <param name="HookCalls">Connector-hook events in the window (<c>action = 'connector-hook'</c>): the Mac's "Hook Calls".</param>
/// <param name="Blocks">Block-class events in the window: the Mac's "Blocks".</param>
/// <param name="Window">How many rows the window actually held (at most the window size; fewer on a young database).</param>
/// <param name="Elapsed">How long the read took, open to last row; what a slow database is diagnosed from.</param>
public sealed record RecentAuditMetrics(RecentAuditMetricsStatus Status, int HookCalls, int Blocks, int Window, TimeSpan Elapsed)
{
    /// <summary>Nothing recorded: what a missing database reads as.</summary>
    public static RecentAuditMetrics None(RecentAuditMetricsStatus status, TimeSpan elapsed) => new(status, 0, 0, 0, elapsed);
}

/// <summary>
/// The two numbers behind the tray flyout's "Hook Calls" and "Blocks" rows, counted over the newest
/// <see cref="DefaultWindow"/> rows of <c>audit.db</c> (the Mac's <c>overviewHookCallCount</c> and <c>overviewBlockCount</c>, which are the TUI's
/// tiles: "latest 500 audit events", "latest 500 decisions").
/// <para>
/// <b>The definitions</b> (<c>AuditStore.swift</c>). The window is the newest 500 rows by <c>timestamp DESC, rowid DESC</c>, every row,
/// whatever its kind. A <i>hook call</i> is a row whose <c>action</c> is exactly <c>connector-hook</c>. A <i>block</i> is a row whose
/// <c>action</c> is <c>block</c>, <c>guardrail-block</c>, <c>deny</c> or <c>quarantine</c> (compared without case), or whose
/// <c>details</c> carry the token <c>action=block</c> / <c>action=deny</c> (the first <c>action=</c> token, split on white space, commas
/// and semicolons, quotes trimmed, compared without case; a hook decision is written that way). The two are counted over the same rows, so a
/// block rate is <c>blocks / hook calls</c> as the Mac prints it, not a true fraction.
/// </para>
/// <para>
/// <b>Query shape.</b> <c>SELECT action, substr(details, 1, n) … ORDER BY timestamp DESC, rowid DESC LIMIT 500</c> is a backward walk of
/// <c>idx_audit_timestamp (timestamp)</c> (the rowid is the index's implicit last column, so the order is the index's own): it stops after
/// 500 rows with no sort, however large the table is. The counting is done here rather than in SQL because the Mac's "first
/// <c>action=</c> token" rule is a tokenizer, not a <c>LIKE</c>. <c>details</c> is cut to <see cref="DetailsLimit"/> characters so a row with
/// a large payload costs nothing; the <c>action=</c> token sits in the first few dozen characters of a hook row. <see cref="ExplainAsync"/>
/// returns the plan for a test to pin.
/// </para>
/// <para>
/// <b>Read-only, off the caller's thread, stoppable</b>, exactly as <see cref="AlertQueueReader"/>: a <c>Mode=ReadOnly</c> connection,
/// the query through <see cref="ReaderOffload"/>, a token and a timeout that end a running statement with <c>sqlite3_interrupt</c>.
/// </para>
/// </summary>
public sealed class RecentAuditMetricsReader
{
    /// <summary>The Mac's window: the latest 500 audit events.</summary>
    public const int DefaultWindow = 500;

    /// <summary>How much of <c>details</c> is read per row; the decision token is at the start of a hook row's details.</summary>
    public const int DetailsLimit = 1024;

    /// <summary>How long <see cref="ReadAsync"/> lets the statement run unless told otherwise; the read itself takes milliseconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly char[] DetailSeparators = { ' ', '\t', '\r', '\n', ',', ';' };
    private static readonly char[] DetailQuotes = { '"', '\'', '`' };

    private readonly string _connectionString;
    private readonly int _window;
    private long _reads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="window">How many of the newest rows to count; <see cref="DefaultWindow"/> (the Mac's) in the app, smaller in a test.</param>
    public RecentAuditMetricsReader(string databasePath, int window = DefaultWindow)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        DatabasePath = databasePath;
        _window = window;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called, counted when the call is made; the idle-cost tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>
    /// Counts the window: one statement, one snapshot. Never blocks the caller's thread.
    /// </summary>
    /// <param name="timeout">How long the statement may run; <see cref="DefaultTimeout"/> when null, <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">Stops a running statement, not only the wait for it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statement did not finish in <paramref name="timeout"/> and was interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<RecentAuditMetrics> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
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
            return await ReaderOffload.Run(() => ReadCoreAsync(linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The audit metrics query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines for the exact statement <see cref="ReadAsync"/> runs on this database. Empty when there is
    /// no database or no usable table. A regression test holds the plan to one index scan with no temp B-tree: on the live 6.9 GB database the
    /// alternative is a sort of every row.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// True when a row with this <paramref name="action"/> and <paramref name="details"/> is a block: the Mac's <c>overviewBlockCount</c> test.
    /// </summary>
    public static bool IsBlock(string? action, string? details)
    {
        var kind = (action ?? string.Empty).ToLowerInvariant();
        if (kind is "block" or "guardrail-block" or "deny" or "quarantine")
        {
            return true;
        }

        var decision = DetailValue("action", details).ToLowerInvariant();
        return decision is "block" or "deny";
    }

    /// <summary>
    /// The value of the first <c>key=</c> token in <paramref name="details"/>, quotes trimmed; empty when there is none. Tokens are split on
    /// white space, commas and semicolons (the Mac's <c>detailValue</c>), so <c>raw_action=x</c> is not <c>action=</c>.
    /// </summary>
    public static string DetailValue(string key, string? details)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (string.IsNullOrEmpty(details))
        {
            return string.Empty;
        }

        var prefix = key + "=";
        foreach (var token in details.Split(DetailSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return token[prefix.Length..].Trim(DetailQuotes);
            }
        }

        return string.Empty;
    }

    private async Task<RecentAuditMetrics> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        if (!File.Exists(DatabasePath))
        {
            return RecentAuditMetrics.None(RecentAuditMetricsStatus.NoDatabase, clock.Elapsed);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var columns = await ColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("action") || !columns.Contains("timestamp"))
        {
            return RecentAuditMetrics.None(RecentAuditMetricsStatus.NoDatabase, clock.Elapsed);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSql(columns);
        command.Parameters.AddWithValue("$limit", _window);

        var rows = 0;
        var hooks = 0;
        var blocks = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows++;
                var action = reader.IsDBNull(0) ? null : reader.GetString(0);
                var details = reader.IsDBNull(1) ? null : reader.GetString(1);

                if (string.Equals(action, "connector-hook", StringComparison.Ordinal))
                {
                    hooks++;
                }

                if (IsBlock(action, details))
                {
                    blocks++;
                }
            }
        }

        return new RecentAuditMetrics(RecentAuditMetricsStatus.Ok, hooks, blocks, rows, clock.Elapsed);
    }

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return Array.Empty<string>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var columns = await ColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("action") || !columns.Contains("timestamp"))
        {
            return Array.Empty<string>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + BuildSql(columns);
        command.Parameters.AddWithValue("$limit", _window);

        // Columns: id, parent, notused, detail.
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    /// <summary>The statement. A database with no <c>details</c> column (a very old schema) reads NULL there: only the action can then count.</summary>
    private static string BuildSql(HashSet<string> columns)
    {
        var details = columns.Contains("details")
            ? $"substr(details, 1, {DetailsLimit.ToString(CultureInfo.InvariantCulture)})"
            : "NULL";

        return $"SELECT action, {details} FROM audit_events ORDER BY timestamp DESC, rowid DESC LIMIT $limit";
    }

    /// <summary>The table's column names, without case; empty when the table does not exist (<c>pragma_table_info</c> of a missing table is no rows).</summary>
    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('audit_events')";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
