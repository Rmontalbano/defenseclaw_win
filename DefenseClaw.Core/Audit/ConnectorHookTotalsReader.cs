using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>How a <see cref="ConnectorHookTotalsReader.ReadAsync"/> ended, short of throwing.</summary>
public enum ConnectorHookTotalsStatus
{
    /// <summary>The totals were read; they may legitimately be zero.</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no readable <c>audit_events</c> table: nothing has been recorded.</summary>
    NoDatabase,
}

/// <summary>All-time connector-hook rows and the enforced blocks among them, for one connector or the fleet.</summary>
public readonly record struct HookCounts(long Calls, long Blocks);

/// <summary>
/// The newest hook rows (<see cref="ConnectorHookTotalsReader.RecentWindow"/>) of one connector or the fleet, as the TUI's tile captions
/// read them: the decision split, the busiest target, and the most blocked target.
/// </summary>
/// <param name="Allow">Recent hook rows decided allow.</param>
/// <param name="Alert">Recent hook rows decided alert (a warn, or a block that observe mode did not enforce).</param>
/// <param name="Block">Recent hook rows decided block.</param>
/// <param name="TopHook">The target most hook rows hit (the TUI's "top"); empty when no row names one.</param>
/// <param name="TopBlockedTarget">The target most recent blocks hit; empty when there is none.</param>
/// <param name="TopBlockedCount">How many recent blocks that target took.</param>
public sealed record RecentHookSplit(int Allow, int Alert, int Block, string TopHook, string TopBlockedTarget, int TopBlockedCount)
{
    public static RecentHookSplit Empty { get; } = new(0, 0, 0, string.Empty, string.Empty, 0);

    public int Total => Allow + Alert + Block;
}

/// <summary>What <see cref="ConnectorHookTotalsReader.ReadAsync"/> found.</summary>
public sealed record ConnectorHookTotals(ConnectorHookTotalsStatus Status, TimeSpan Elapsed)
{
    private static readonly IReadOnlyDictionary<string, HookCounts> NoCounts = new Dictionary<string, HookCounts>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, RecentHookSplit> NoRecent = new Dictionary<string, RecentHookSplit>(StringComparer.OrdinalIgnoreCase);

    /// <summary>All-time counts per connector (lower-cased and trimmed; rows with no connector are under the empty string).</summary>
    public IReadOnlyDictionary<string, HookCounts> ByConnector { get; init; } = NoCounts;

    /// <summary>
    /// False while the block count is still catching up on a database it has not scanned before: <see cref="HookCounts.Blocks"/> is then a
    /// lower bound. Hook calls are exact either way.
    /// </summary>
    public bool BlocksComplete { get; init; } = true;

    /// <summary>How many hook rows the block scan has covered, against <see cref="HookRows"/>; the progress of a catch-up.</summary>
    public long BlocksScanned { get; init; }

    /// <summary>Every hook row in the database.</summary>
    public long HookRows { get; init; }

    /// <summary>The newest hook rows, whole fleet.</summary>
    public RecentHookSplit Recent { get; init; } = RecentHookSplit.Empty;

    /// <summary>The newest hook rows per connector (the newest <see cref="ConnectorHookTotalsReader.RecentWindow"/> across the fleet, split by connector).</summary>
    public IReadOnlyDictionary<string, RecentHookSplit> RecentByConnector { get; init; } = NoRecent;

    /// <summary>The fleet's all-time counts.</summary>
    public HookCounts Fleet
    {
        get
        {
            long calls = 0;
            long blocks = 0;
            foreach (var counts in ByConnector.Values)
            {
                calls += counts.Calls;
                blocks += counts.Blocks;
            }

            return new HookCounts(calls, blocks);
        }
    }

    /// <summary>The all-time counts of <paramref name="connector"/> (without case); zero for one with no hook row.</summary>
    public HookCounts For(string connector) =>
        ByConnector.TryGetValue(connector?.Trim() ?? string.Empty, out var counts) ? counts : default;

    /// <summary>The recent split of <paramref name="connector"/>; empty for one the window has no row for.</summary>
    public RecentHookSplit RecentFor(string connector) =>
        RecentByConnector.TryGetValue(connector?.Trim() ?? string.Empty, out var split) ? split : RecentHookSplit.Empty;

    public static ConnectorHookTotals None(ConnectorHookTotalsStatus status, TimeSpan elapsed) => new(status, elapsed);
}

/// <summary>
/// The Overview's Hook Calls and Blocks tiles, counted the way the 0.8.10 TUI counts them
/// (<c>services/overview_state.py</c> over <c>Store.connector_hook_event_stats</c>): <b>all time</b>, grouped by connector, not the newest 500 rows.
/// <para>
/// <b>The definitions.</b> A <i>hook call</i> is a row whose <c>action</c> is <c>connector-hook</c>. A <i>block</i> is a hook row the gateway
/// <i>enforced</i> (<c>hook_metrics.connector_hook_decision</c>): <c>enforced</c> set, or <c>action=block|deny</c> in its details unless
/// <c>mode=observe</c> or <c>enforced=0</c> (that is an alert: a would-block). Tokens are exact, so <c>raw_action=block</c> is not <c>action=</c>.
/// </para>
/// <para>
/// <b>Cost on a 10 GB database.</b> Hook calls are <c>COUNT(*) … GROUP BY connector</c> over <c>idx_audit_action_connector_timestamp</c>, a covering
/// index: no table row is touched (0.25 s on 73 000 hook rows of a 9.8 GB file, warm). Whether a row is a block needs its <c>details</c>, which
/// is a table read per row (1-9 s for the same 73 000 rows, cold), and that is too much to repeat on a timer. So the scan is <b>incremental
/// and resumable</b>: it walks <c>(timestamp, rowid)</c> upward in chunks along <c>idx_audit_action_timestamp</c> (no sort), keeps a
/// per-connector tally and a watermark, and each later read covers only the rows after the watermark. A first read on a large database
/// stops after <see cref="ScanBudget"/> and reports <see cref="ConnectorHookTotals.BlocksComplete"/> false; the next read carries on. If the
/// cheap total ever disagrees with the rows scanned (retention pruned old rows, or a row landed behind the watermark), the tally is dropped and
/// rebuilt, at most once per <see cref="RebuildCooldown"/>.
/// </para>
/// <para>
/// The newest <see cref="RecentWindow"/> hook rows (an index walk, no sort) feed the captions. Read-only, off the caller's thread and stoppable,
/// exactly as <see cref="RecentAuditMetricsReader"/>. Not covered: rows whose connector or decision only exists in <c>structured_json</c> (legacy
/// rows; the TUI resolves those through SQLite callbacks), which count as hook calls under the connector column's value (empty when null).
/// </para>
/// </summary>
public sealed class ConnectorHookTotalsReader
{
    /// <summary>The TUI's window for "recent": the newest 500 hook rows.</summary>
    public const int RecentWindow = 500;

    /// <summary>How much of <c>details</c> is read per row.</summary>
    public const int DetailsLimit = 1024;

    /// <summary>How long <see cref="ReadAsync"/> lets the cheap statements run unless told otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one read spends on the block scan before it returns what it has and lets the next read carry on.</summary>
    public static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);

    /// <summary>The soonest a tally that disagrees with the database is thrown away again.</summary>
    public static readonly TimeSpan RebuildCooldown = TimeSpan.FromMinutes(10);

    private readonly string _connectionString;
    private readonly int _chunk;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private string _markTimestamp = string.Empty;
    private long _markRowId = -1;
    private long _scanned;
    private DateTime _lastRebuild = DateTime.MinValue;
    private long _reads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="chunk">How many hook rows one scan statement covers; smaller in a test so a catch-up spans reads.</param>
    public ConnectorHookTotalsReader(string databasePath, int chunk = 2_000)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunk, 1);
        DatabasePath = databasePath;
        _chunk = chunk;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called, counted when the call is made; the idle-cost tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>
    /// The most full chunks one read scans before it returns what it has, on top of <see cref="ScanBudget"/>. Unbounded by default, so in
    /// production the time budget alone decides. A test sets 1 to make a catch-up span several reads by <i>counting</i> chunks instead of
    /// timing them (a zero time budget gives one chunk per read too, but only because the clock has moved by the time the check runs).
    /// </summary>
    internal int MaxChunksPerRead { get; set; } = int.MaxValue;

    /// <summary>
    /// True when a hook row with these <paramref name="details"/> and <paramref name="enforced"/> flag is an enforced block: the TUI's
    /// <c>aggregate_connector_hook_decision</c> == "block".
    /// </summary>
    public static bool IsEnforcedBlock(string? details, long? enforced)
    {
        var explicitEnforced = enforced is { } flag ? flag != 0 : (bool?)null;
        if (explicitEnforced == true)
        {
            return true;
        }

        string action = string.Empty;
        string mode = string.Empty;
        foreach (var (key, value) in Tokens(details))
        {
            if (key == "action")
            {
                action = value.Trim().ToLowerInvariant();
            }
            else if (key == "mode")
            {
                mode = value.Trim().ToLowerInvariant();
            }
        }

        return action is "block" or "deny" && explicitEnforced != false && mode != "observe";
    }

    /// <summary>The TUI's three-way decision for a recent hook row: <c>allow</c>, <c>alert</c> or <c>block</c>.</summary>
    internal static string Decision(string? details, long? enforced)
    {
        if (IsEnforcedBlock(details, enforced))
        {
            return "block";
        }

        string action = string.Empty;
        string rawAction = string.Empty;
        bool? wouldBlock = null;
        foreach (var (key, value) in Tokens(details))
        {
            var normalized = value.Trim().ToLowerInvariant();
            switch (key)
            {
                case "action":
                    action = normalized;
                    break;
                case "raw_action":
                    rawAction = normalized;
                    break;
                case "would_block":
                    wouldBlock = normalized switch { "true" or "1" or "yes" => true, "false" or "0" or "no" or "" => false, _ => null };
                    break;
            }
        }

        if (action is "alert" or "warn" or "block" or "deny")
        {
            return "alert";   // a block that was not enforced (observe mode) is a would-block
        }

        return rawAction is "alert" or "warn" or "block" or "deny" || wouldBlock == true ? "alert" : "allow";
    }

    /// <summary>Exact whitespace-delimited <c>key=value</c> tokens (<c>hook_metrics._iter_detail_tokens</c>): quoted values may hold spaces; a token with no <c>=</c> is skipped.</summary>
    private static IEnumerable<(string Key, string Value)> Tokens(string? details)
    {
        var text = (details ?? string.Empty).Trim();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var keyStart = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] != '=')
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                continue;
            }

            var key = text[keyStart..i];
            i++;
            if (key.Length == 0)
            {
                continue;
            }

            if (i < text.Length && text[i] == '"')
            {
                i++;
                var value = new System.Text.StringBuilder();
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        i++;
                        break;
                    }

                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    _ = value.Append(text[i]);
                    i++;
                }

                yield return (key, value.ToString());
                continue;
            }

            var valueStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            yield return (key, text[valueStart..i]);
        }
    }

    /// <summary>
    /// Reads the totals: the exact hook-call counts, the block tally brought up to date (as far as <see cref="ScanBudget"/> allows), and the
    /// recent split. One at a time; never blocks the caller's thread.
    /// </summary>
    /// <param name="timeout">How long the cheap statements may run; <see cref="DefaultTimeout"/> when null. The block scan is bounded by <see cref="ScanBudget"/> instead and returns partial totals rather than failing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">A statement did not finish in <paramref name="timeout"/> and was interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<ConnectorHookTotals> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
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
            return await ReaderOffload.Run(() => ReadCoreAsync(limit, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The hook totals query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    /// <summary>The <c>EXPLAIN QUERY PLAN</c> detail lines of the three statements, in the order <c>totals</c>, <c>scan</c>, <c>recent</c>; empty without a database.</summary>
    public Task<IReadOnlyList<string>> ExplainAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(cancellationToken), cancellationToken);

    private async Task<ConnectorHookTotals> ReadCoreAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (!File.Exists(DatabasePath))
        {
            return ConnectorHookTotals.None(ConnectorHookTotalsStatus.NoDatabase, clock.Elapsed);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

            var columns = await ColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!columns.Contains("action") || !columns.Contains("timestamp"))
            {
                return ConnectorHookTotals.None(ConnectorHookTotalsStatus.NoDatabase, clock.Elapsed);
            }

            // One read snapshot for the three questions, so the cheap total and the scan agree about which rows exist.
            await using var snapshot = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var calls = await CallsAsync(connection, snapshot, columns, cancellationToken).ConfigureAwait(false);
            long total = 0;
            foreach (var count in calls.Values)
            {
                total += count;
            }

            await ScanAsync(connection, snapshot, columns, total, clock, limit, cancellationToken).ConfigureAwait(false);
            var recent = await RecentAsync(connection, snapshot, columns, cancellationToken).ConfigureAwait(false);

            var byConnector = new Dictionary<string, HookCounts>(StringComparer.OrdinalIgnoreCase);
            foreach (var (connector, count) in calls)
            {
                byConnector[connector] = new HookCounts(count, _blocks.TryGetValue(connector, out var blocks) ? Math.Min(blocks, count) : 0);
            }

            var complete = !_scanIncomplete;
            return new ConnectorHookTotals(ConnectorHookTotalsStatus.Ok, clock.Elapsed)
            {
                ByConnector = byConnector,
                BlocksComplete = complete,
                BlocksScanned = Math.Min(_scanned, total),
                HookRows = total,
                Recent = recent.Fleet,
                RecentByConnector = recent.ByConnector,
            };
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private bool _scanIncomplete;

    /// <summary>The all-time hook-call counts per connector: a covering-index group count.</summary>
    private static async Task<Dictionary<string, long>> CallsAsync(SqliteConnection connection, SqliteTransaction transaction, HashSet<string> columns, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // nosemgrep: csharp-sqli -- allow-list: CallsSql emits fixed text with the literal column connector (or NULL when the table lacks one)
        command.CommandText = CallsSql(columns);

        var calls = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var connector = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            calls[connector] = calls.TryGetValue(connector, out var existing) ? existing + reader.GetInt64(1) : reader.GetInt64(1);
        }

        return calls;
    }

    /// <summary>Brings the block tally up to the newest hook row, or as far as the budget goes; rebuilds it when it disagrees with the cheap total.</summary>
    private async Task ScanAsync(SqliteConnection connection, SqliteTransaction transaction, HashSet<string> columns, long total, Stopwatch clock, TimeSpan limit, CancellationToken cancellationToken)
    {
        var scanStart = clock.Elapsed;
        var resetIfStale = true;
        var fullChunks = 0;
        _scanIncomplete = false;

        while (true)
        {
            var chunkStart = clock.Elapsed;
            long rows = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // nosemgrep: csharp-sqli -- allow-list: ScanSql emits fixed text with the const DetailsLimit and literal columns; marks and limit are bound
                command.CommandText = ScanSql(columns);
                _ = command.Parameters.AddWithValue("$ts", _markTimestamp);
                _ = command.Parameters.AddWithValue("$rid", _markRowId);
                _ = command.Parameters.AddWithValue("$limit", _chunk);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows++;
                    _scanned++;   // per row: a statement stopped by the timeout leaves the tally and the watermark consistent
                    _markRowId = reader.GetInt64(0);
                    _markTimestamp = reader.GetString(1);
                    if (reader.IsDBNull(3))
                    {
                        continue;   // not a block candidate: the statement left details out
                    }

                    var connector = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim().ToLowerInvariant();
                    long? enforced = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                    if (IsEnforcedBlock(reader.GetString(3), enforced))
                    {
                        _blocks[connector] = _blocks.TryGetValue(connector, out var existing) ? existing + 1 : 1;
                    }
                }
            }

            if (rows < _chunk)
            {
                // Caught up. The tally must cover exactly the rows the cheap count sees; if it does not, start over once.
                if (_scanned != total && resetIfStale && DateTime.UtcNow - _lastRebuild > RebuildCooldown)
                {
                    _lastRebuild = DateTime.UtcNow;
                    resetIfStale = false;
                    _blocks.Clear();
                    _markTimestamp = string.Empty;
                    _markRowId = -1;
                    _scanned = 0;
                    continue;
                }

                return;
            }

            // Another chunk only if the budget has room and a chunk as slow as the last one still ends inside the caller's timeout.
            var chunkTook = clock.Elapsed - chunkStart;
            fullChunks++;
            if (fullChunks >= MaxChunksPerRead || clock.Elapsed - scanStart > ScanBudget || (limit != Timeout.InfiniteTimeSpan && clock.Elapsed + (chunkTook * 2) > limit))
            {
                _scanIncomplete = true;
                return;
            }
        }
    }

    private async Task<(RecentHookSplit Fleet, IReadOnlyDictionary<string, RecentHookSplit> ByConnector)> RecentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> columns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // nosemgrep: csharp-sqli -- allow-list: RecentSql emits fixed text with the const DetailsLimit and literal columns; $limit is bound
        command.CommandText = RecentSql(columns);
        _ = command.Parameters.AddWithValue("$limit", RecentWindow);

        var fleet = new Tally();
        var perConnector = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var connector = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim().ToLowerInvariant();
                var target = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                var details = reader.IsDBNull(2) ? null : reader.GetString(2);
                long? enforced = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                var decision = Decision(details, enforced);

                fleet.Add(decision, target);
                if (!perConnector.TryGetValue(connector, out var tally))
                {
                    perConnector[connector] = tally = new Tally();
                }

                tally.Add(decision, target);
            }
        }

        var split = new Dictionary<string, RecentHookSplit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (connector, tally) in perConnector)
        {
            split[connector] = tally.ToSplit();
        }

        return (fleet.ToSplit(), split);
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

        var lines = new List<string>();
        foreach (var sql in new[] { CallsSql(columns), ScanSql(columns), RecentSql(columns) })
        {
            await using var command = connection.CreateCommand();
            // nosemgrep: csharp-sqli -- allow-list: the EXPLAIN prefix and the Sql builders, as annotated above
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var name in new[] { "$ts", "$rid", "$limit" })
            {
                if (sql.Contains(name, StringComparison.Ordinal))
                {
                    _ = command.Parameters.AddWithValue(name, name == "$ts" ? string.Empty : name == "$rid" ? -1L : _chunk);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(reader.GetString(3));
            }
        }

        return lines;
    }

    private static string ConnectorColumn(HashSet<string> columns) => columns.Contains("connector") ? "connector" : "NULL";

    private static string DetailsExpression(HashSet<string> columns) =>
        columns.Contains("details") ? $"substr(details, 1, {DetailsLimit.ToString(CultureInfo.InvariantCulture)})" : "NULL";

    private static string EnforcedColumn(HashSet<string> columns) => columns.Contains("enforced") ? "enforced" : "NULL";

    private static string TargetColumn(HashSet<string> columns) => columns.Contains("target") ? "target" : "NULL";

    private static string CallsSql(HashSet<string> columns) =>
        $"SELECT lower(trim(COALESCE({ConnectorColumn(columns)}, ''))), COUNT(*) FROM audit_events WHERE action = 'connector-hook' GROUP BY 1";

    /// <summary>
    /// The next chunk of hook rows after the watermark, oldest first. Details is returned only for a row that could be a block (enforced, or
    /// the words block / deny somewhere in it), so the long tail of allow rows costs no marshalling.
    /// </summary>
    private static string ScanSql(HashSet<string> columns)
    {
        var details = DetailsExpression(columns);
        var enforced = EnforcedColumn(columns);
        var candidate = columns.Contains("details")
            ? $"({enforced} IS NOT NULL AND {enforced} <> 0) OR {details} LIKE '%block%' OR {details} LIKE '%deny%'"
            : $"({enforced} IS NOT NULL AND {enforced} <> 0)";

        return $"SELECT rowid, timestamp, {ConnectorColumn(columns)}, CASE WHEN {candidate} THEN {details} END, {enforced} " +
               "FROM audit_events WHERE action = 'connector-hook' AND timestamp >= $ts AND (timestamp > $ts OR rowid > $rid) " +
               "ORDER BY timestamp, rowid LIMIT $limit";
    }

    private static string RecentSql(HashSet<string> columns) =>
        $"SELECT {ConnectorColumn(columns)}, {TargetColumn(columns)}, {DetailsExpression(columns)}, {EnforcedColumn(columns)} " +
        "FROM audit_events WHERE action = 'connector-hook' ORDER BY timestamp DESC, rowid DESC LIMIT $limit";

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

    /// <summary>Counts for one recent scope; insertion-ordered so a tie in the busiest target goes to the first seen, as the TUI's <c>max</c> does.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _blocked = new(StringComparer.Ordinal);
        private int _allow;
        private int _alert;
        private int _block;

        public void Add(string decision, string target)
        {
            switch (decision)
            {
                case "block":
                    _block++;
                    var key = target.Length == 0 ? "(unknown)" : target;
                    _blocked[key] = _blocked.TryGetValue(key, out var seen) ? seen + 1 : 1;
                    break;
                case "alert":
                    _alert++;
                    break;
                default:
                    _allow++;
                    break;
            }

            if (target.Length > 0)
            {
                _hits[target] = _hits.TryGetValue(target, out var hit) ? hit + 1 : 1;
            }
        }

        public RecentHookSplit ToSplit()
        {
            var (top, _) = Max(_hits);
            var (blockedTarget, blockedCount) = Max(_blocked);
            return new RecentHookSplit(_allow, _alert, _block, top, blockedTarget, blockedCount);
        }

        private static (string Key, int Count) Max(Dictionary<string, int> counts)
        {
            var best = (Key: string.Empty, Count: 0);
            foreach (var (key, count) in counts)
            {
                if (count > best.Count)
                {
                    best = (key, count);
                }
            }

            return best;
        }
    }
}
