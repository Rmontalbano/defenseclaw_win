using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>One finding of a scan, as the Alerts inspector's "Findings" cards show it (<c>scan_findings</c>).</summary>
public sealed record ScanFindingDetail(
    string Id,
    DateTimeOffset Timestamp,
    string Scanner,
    string Target,
    string RuleId,
    string Severity,
    string Title,
    string Description,
    string Location,
    string Remediation);

/// <summary>One earlier audit event on the same target, a row of "History for this target".</summary>
public sealed record TargetHistoryEvent(string Id, DateTimeOffset Timestamp, string Action, string Severity);

/// <summary>
/// The two read-only lookups behind the Alerts inspector (the Mac's <c>AuditStore.scanFindings</c> and <c>relatedEvents</c>), shaped
/// for a database that is gigabytes large. Both are cancellable (a token or a timeout ends the running statement) and never write.
/// <para>
/// <b>Findings.</b> The Mac reads <c>scan_results.raw_json</c> and parses it; the finding rows are also in <c>scan_findings</c> as
/// columns, so this reads them there: first the newest <see cref="ScanLimit"/> scans of the alert's <em>run</em> (<c>idx_scan_run_id</c>),
/// or - when the run has none, or the alert has no run - of its <em>target</em> (<c>scan_results</c> is a few thousand rows; the walk of
/// <c>idx_scan_timestamp</c> is milliseconds), then those scans' findings by <c>scan_id</c> (<c>idx_scan_findings_scan_id</c>), worst first.
/// Missing tables or columns read as "no findings"; a database without the optional columns still answers.
/// </para>
/// <para>
/// <b>History.</b> <c>audit_events.target</c> has no index, so the Mac's <c>WHERE target = ? ORDER BY timestamp DESC LIMIT n</c> is a
/// walk of <em>every</em> row when the target is rare: 65-70 s on the live 6.7 GB database. This reads from the newest
/// <see cref="HistoryWindow"/> rows only - a backward walk of <c>idx_audit_timestamp</c> that stops at the window - and filters inside it:
/// 0.1-0.3 s warm. The trade is stated to the operator ("newest 20,000 events searched"), not hidden.
/// </para>
/// </summary>
public sealed class AlertDetailReader
{
    /// <summary>How many scans of a run or target contribute findings.</summary>
    public const int ScanLimit = 10;

    /// <summary>How many findings the inspector lists (the Mac's 20).</summary>
    public const int DefaultFindingLimit = 20;

    /// <summary>How many newest <c>audit_events</c> rows the history lookup searches.</summary>
    public const int HistoryWindow = 20_000;

    /// <summary>The longest finding text kept; descriptions can be whole documents.</summary>
    public const int TextLimit = 2_000;

    private readonly string _databasePath;
    private readonly int _historyWindow;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="historyWindow">How many newest rows the history lookup searches; <see cref="HistoryWindow"/> in the app, small in a test.</param>
    public AlertDetailReader(string databasePath, int historyWindow = HistoryWindow)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyWindow, 1);
        _databasePath = databasePath;
        _historyWindow = historyWindow;
    }

    /// <summary>How many times <see cref="ReadFindingsAsync"/> / <see cref="ReadHistoryAsync"/> were called; the idle tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    private long _reads;

    /// <summary>The findings of the scans of <paramref name="runId"/> (else <paramref name="target"/>), worst first, at most <paramref name="limit"/>.</summary>
    public Task<IReadOnlyList<ScanFindingDetail>> ReadFindingsAsync(
        string? runId,
        string? target,
        int limit = DefaultFindingLimit,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var run = runId?.Trim() ?? string.Empty;
        var key = target?.Trim() ?? string.Empty;
        if (run.Length == 0 && key.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<ScanFindingDetail>>(Array.Empty<ScanFindingDetail>());
        }

        return ReadOnlyQuery.RunAsync<IReadOnlyList<ScanFindingDetail>>(
            _databasePath,
            timeout,
            cancellationToken,
            Array.Empty<ScanFindingDetail>(),
            (connection, token) => FindingsCoreAsync(connection, run, key, limit, token));
    }

    private static async Task<IReadOnlyList<ScanFindingDetail>> FindingsCoreAsync(
        SqliteConnection connection, string run, string target, int limit, CancellationToken cancellationToken)
    {
        var results = await ReadOnlyQuery.ColumnsOfAsync(connection, "scan_results", cancellationToken).ConfigureAwait(false);
        var findings = await ReadOnlyQuery.ColumnsOfAsync(connection, "scan_findings", cancellationToken).ConfigureAwait(false);
        if (!results.Contains("id") || !findings.Contains("scan_id") || !findings.Contains("severity"))
        {
            return Array.Empty<ScanFindingDetail>();
        }

        var scanIds = new List<string>();
        if (run.Length > 0 && results.Contains("run_id"))
        {
            scanIds = await ScanIdsAsync(connection, "run_id", run, cancellationToken).ConfigureAwait(false);
        }

        if (scanIds.Count == 0 && target.Length > 0 && results.Contains("target"))
        {
            scanIds = await ScanIdsAsync(connection, "target", target, cancellationToken).ConfigureAwait(false);
        }

        if (scanIds.Count == 0)
        {
            return Array.Empty<ScanFindingDetail>();
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: FindingsSql names only its own literal columns (f.<name>, or NULL when the table lacks it) and $s0..$sN placeholders; the scan ids are bound
        command.CommandText = FindingsSql(findings, scanIds.Count);
        for (var i = 0; i < scanIds.Count; i++)
        {
            command.Parameters.AddWithValue("$s" + i.ToString(CultureInfo.InvariantCulture), scanIds[i]);
        }

        command.Parameters.AddWithValue("$cap", Math.Max(limit * 5, 100));

        var rows = new List<ScanFindingDetail>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ScanFindingDetail(
                Id: ReadOnlyQuery.Text(reader, 0) ?? string.Empty,
                Timestamp: ReadOnlyQuery.Timestamp(ReadOnlyQuery.Text(reader, 1)),
                Scanner: ReadOnlyQuery.Text(reader, 2) ?? string.Empty,
                Target: ReadOnlyQuery.Text(reader, 3) ?? string.Empty,
                RuleId: ReadOnlyQuery.Text(reader, 4) ?? string.Empty,
                Severity: (ReadOnlyQuery.Text(reader, 5) ?? string.Empty).Trim().ToUpperInvariant(),
                Title: Clip(ReadOnlyQuery.Text(reader, 6)),
                Description: Clip(ReadOnlyQuery.Text(reader, 7)),
                Location: Clip(Where(ReadOnlyQuery.Text(reader, 8), reader.IsDBNull(9) ? null : reader.GetInt64(9))),
                Remediation: Clip(ReadOnlyQuery.Text(reader, 10))));
        }

        // Worst first, then newest: the query could not order by "severity" (text).
        return rows
            .OrderByDescending(f => (int)AuditSeverityExtensions.Parse(f.Severity))
            .ThenByDescending(f => f.Timestamp)
            .Take(limit)
            .ToList();
    }

    /// <summary>The statement <see cref="ReadFindingsAsync"/> runs for <paramref name="scanCount"/> scans (what the Core suite pins the plan of).</summary>
    private static string FindingsSql(HashSet<string> columns, int scanCount)
    {
        string Column(string name) => columns.Contains(name) ? "f." + name : "NULL";

        var inList = new StringBuilder();
        for (var i = 0; i < scanCount; i++)
        {
            _ = inList.Append(i == 0 ? "$s" : ", $s").Append(i.ToString(CultureInfo.InvariantCulture));
        }

        return
            $"""
            SELECT {Column("id")}, {Column("timestamp")}, {Column("scanner")}, {Column("target")},
                   {Column("rule_id")}, f.severity, {Column("title")}, {Column("description")},
                   {Column("location")}, {Column("line_number")}, {Column("remediation")}
            FROM scan_findings f
            WHERE f.scan_id IN ({inList})
            {(columns.Contains("timestamp") ? "ORDER BY f.timestamp DESC" : string.Empty)}
            LIMIT $cap
            """;
    }

    private static async Task<List<string>> ScanIdsAsync(SqliteConnection connection, string column, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: column is "run_id" or "target" at every call site, never input; the value searched for is the bound $key
        command.CommandText = ScanIdsSql(column);
        command.Parameters.AddWithValue("$key", value);
        command.Parameters.AddWithValue("$scans", ScanLimit);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ReadOnlyQuery.Text(reader, 0) is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string ScanIdsSql(string column) =>
        $"SELECT id FROM scan_results WHERE {column} = $key ORDER BY timestamp DESC LIMIT $scans";

    private static string? Where(string? location, long? line) =>
        string.IsNullOrEmpty(location) ? null : line is > 0 ? $"{location}:{line.Value.ToString(CultureInfo.InvariantCulture)}" : location;

    private static string Clip(string? text) =>
        text is null ? string.Empty : text.Length <= TextLimit ? text : text[..TextLimit] + "…";

    // ---------------------------------------------------------------------------------------- history

    /// <summary>
    /// Earlier audit events whose <c>target</c> is one of <paramref name="targets"/>, newest first, from the newest
    /// <c>historyWindow</c> rows of the table. <paramref name="excludeId"/> (the alert itself) is left out.
    /// </summary>
    public Task<IReadOnlyList<TargetHistoryEvent>> ReadHistoryAsync(
        IEnumerable<string?> targets,
        string? excludeId,
        int limit = 5,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var wanted = targets
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (wanted.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<TargetHistoryEvent>>(Array.Empty<TargetHistoryEvent>());
        }

        return ReadOnlyQuery.RunAsync<IReadOnlyList<TargetHistoryEvent>>(
            _databasePath,
            timeout,
            cancellationToken,
            Array.Empty<TargetHistoryEvent>(),
            async (connection, token) =>
            {
                var columns = await ReadOnlyQuery.ColumnsOfAsync(connection, "audit_events", token).ConfigureAwait(false);
                if (!HasHistoryColumns(columns))
                {
                    return Array.Empty<TargetHistoryEvent>();
                }

                await using var command = connection.CreateCommand();
                command.CommandText = HistorySql(wanted.Length);
                BindHistory(command, wanted, excludeId, limit);

                var rows = new List<TargetHistoryEvent>(limit);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    rows.Add(new TargetHistoryEvent(
                        Id: ReadOnlyQuery.Text(reader, 0) ?? string.Empty,
                        Timestamp: ReadOnlyQuery.Timestamp(ReadOnlyQuery.Text(reader, 1)),
                        Action: ReadOnlyQuery.Text(reader, 2) ?? string.Empty,
                        Severity: (ReadOnlyQuery.Text(reader, 3) ?? string.Empty).Trim().ToUpperInvariant()));
                }

                return rows;
            });
    }

    private static bool HasHistoryColumns(HashSet<string> columns) =>
        columns.Contains("id") && columns.Contains("timestamp") && columns.Contains("action") && columns.Contains("severity") && columns.Contains("target");

    private static string HistorySql(int targetCount)
    {
        var inList = new StringBuilder();
        for (var i = 0; i < targetCount; i++)
        {
            _ = inList.Append(i == 0 ? "$t" : ", $t").Append(i.ToString(CultureInfo.InvariantCulture));
        }

        return
            $"""
            SELECT id, timestamp, action, severity FROM (
                SELECT rowid AS rid, id, timestamp, action, severity, target
                FROM audit_events ORDER BY timestamp DESC, rowid DESC LIMIT $window
            ) WHERE target IN ({inList}) AND id <> $exclude
            ORDER BY timestamp DESC, rid DESC LIMIT $limit
            """;
    }

    private void BindHistory(SqliteCommand command, string[] targets, string? excludeId, int limit)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            command.Parameters.AddWithValue("$t" + i.ToString(CultureInfo.InvariantCulture), targets[i]);
        }

        command.Parameters.AddWithValue("$exclude", excludeId ?? string.Empty);
        command.Parameters.AddWithValue("$window", _historyWindow);
        command.Parameters.AddWithValue("$limit", limit);
    }

    // ---------------------------------------------------------------------------------------- plans

    /// <summary>
    /// <c>EXPLAIN QUERY PLAN</c> detail lines of the statements the two lookups run on this database (the scan-id lookup by run and by
    /// target, the findings lookup, the history lookup), each line prefixed with the statement's name. Empty when there is no database.
    /// A regression test holds them to index searches / the bounded walk; on the live database the history alternative is 65 s, not 0.2.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(CancellationToken cancellationToken = default) =>
        ReadOnlyQuery.RunAsync<IReadOnlyList<string>>(
            _databasePath,
            ReadOnlyQuery.DefaultTimeout,
            cancellationToken,
            Array.Empty<string>(),
            async (connection, token) =>
            {
                var lines = new List<string>();
                await Plan(connection, "scans-by-run", ScanIdsSql("run_id"), token, lines, ("$key", "x"), ("$scans", ScanLimit)).ConfigureAwait(false);
                await Plan(connection, "scans-by-target", ScanIdsSql("target"), token, lines, ("$key", "x"), ("$scans", ScanLimit)).ConfigureAwait(false);

                var findings = await ReadOnlyQuery.ColumnsOfAsync(connection, "scan_findings", token).ConfigureAwait(false);
                if (findings.Contains("scan_id"))
                {
                    await Plan(connection, "findings", FindingsSql(findings, 2), token, lines, ("$s0", "x"), ("$s1", "y"), ("$cap", 100)).ConfigureAwait(false);
                }

                var audit = await ReadOnlyQuery.ColumnsOfAsync(connection, "audit_events", token).ConfigureAwait(false);
                if (HasHistoryColumns(audit))
                {
                    await Plan(connection, "history", HistorySql(1), token, lines, ("$t0", "x"), ("$exclude", ""), ("$window", _historyWindow), ("$limit", 5)).ConfigureAwait(false);
                }

                return lines;
            });

    private static async Task Plan(
        SqliteConnection connection, string name, string sql, CancellationToken cancellationToken, List<string> lines, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: sql is only ever the output of ScanIdsSql, FindingsSql or HistorySql (see ExplainAsync), assembled as annotated above; parameters are bound
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (parameter, value) in parameters)
        {
            command.Parameters.AddWithValue(parameter, value);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add($"{name}: {reader.GetString(3)}");
            }
        }
        catch (SqliteException)
        {
            // A table this database does not have has no plan; the caller sees only the statements that exist.
        }
    }
}
