using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>Why <see cref="RelatedEvents"/> are related to the event they were asked about.</summary>
public enum RelatedBasis
{
    /// <summary>The event has neither a run id nor a target: nothing to correlate on.</summary>
    None,

    /// <summary>Other events of the same run (<c>idx_audit_run_id</c>).</summary>
    Run,

    /// <summary>Other events about the same target, within <see cref="AuditCorrelationReader.TargetWindow"/> of the event (the target has no index).</summary>
    Target,
}

/// <summary>Events related to one event, newest first, and what they are related by.</summary>
public sealed record RelatedEvents(IReadOnlyList<AuditEvent> Events, RelatedBasis Basis);

/// <summary>One finding read out of a <c>scan_results.raw_json</c> document.</summary>
public sealed record RunFinding(
    string ScanId,
    string Scanner,
    string Target,
    string Title,
    string Severity,
    string Description,
    string Location,
    DateTimeOffset Timestamp);

/// <summary>
/// Read-only correlation queries for the Audit inspector: the events and the scan findings that share a run with the
/// selected event (the Mac's <c>relatedEvents</c> / <c>scanFindings</c>). Every statement is a bounded index lookup on its own
/// connection (<c>Mode=ReadOnly</c>), off the caller's thread, and a cancelled token interrupts the running statement.
/// <para>
/// <b>Plans</b> (live 6.7 GB audit.db): a run's events are read backwards from <c>idx_audit_run_id</c> in rowid order (no sort,
/// so the size of the run does not matter; ordered by the retention timestamp instead, a big run that ended weeks ago took 60 s)
/// and its scan results from <c>idx_scan_run_id</c>. A target has no index at all, so a target correlation is bounded to
/// <see cref="TargetWindow"/> either side of the event, a range seek on the retention index (0.6 s worst case for a target that
/// matches nothing, against 38 s unbounded).
/// </para>
/// </summary>
public sealed class AuditCorrelationReader
{
    /// <summary>Events listed under "Related events".</summary>
    public const int RelatedLimit = 8;

    /// <summary>Findings listed under "Findings in this run".</summary>
    public const int FindingsLimit = 10;

    /// <summary>How far either side of an event a target is searched for (the target column has no index).</summary>
    public static readonly TimeSpan TargetWindow = TimeSpan.FromHours(1);

    private readonly string _connectionString;

    public AuditCorrelationReader(string databasePath, bool immutable = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        IsImmutable = immutable;
        _connectionString = immutable
            ? AuditReader.BuildImmutableConnectionString(databasePath)
            : AuditReader.BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>True over an archived copy; see <see cref="AuditReader.BuildImmutableConnectionString"/>.</summary>
    public bool IsImmutable { get; }

    /// <summary>
    /// Up to <see cref="RelatedLimit"/> other events related to the one given: those of its run when it has one,
    /// otherwise those about its target within <see cref="TargetWindow"/>. The event itself is never listed.
    /// </summary>
    /// <param name="id">The event's id.</param>
    /// <param name="runId">Its run id, or null.</param>
    /// <param name="target">Its target, or null.</param>
    /// <param name="timestampNanos">Its <see cref="AuditEvent.TimestampNanos"/>: the centre of the target window.</param>
    public Task<RelatedEvents> RelatedAsync(string id, string? runId, string? target, long timestampNanos, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => RelatedCoreAsync(id, runId, target, timestampNanos, cancellationToken), cancellationToken);

    /// <summary>The same for an event in hand.</summary>
    public Task<RelatedEvents> RelatedAsync(AuditEvent source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RelatedAsync(source.Id, source.RunId, source.Target, source.TimestampNanos, cancellationToken);
    }

    /// <summary>Up to <see cref="FindingsLimit"/> findings of the scans recorded under <paramref name="runId"/>, newest scan first.</summary>
    public Task<IReadOnlyList<RunFinding>> FindingsInRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        return ReaderOffload.Run(() => FindingsCoreAsync(runId, cancellationToken), cancellationToken);
    }

    /// <summary>The query plan of the related-events statement for <paramref name="basis"/>, for the tests and the live timing notes.</summary>
    public Task<IReadOnlyList<string>> ExplainAsync(RelatedBasis basis, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(basis, "EXPLAIN QUERY PLAN ", cancellationToken), cancellationToken);

    /// <summary>The plan of the scan-findings statement.</summary>
    public Task<IReadOnlyList<string>> ExplainFindingsAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainFindingsCoreAsync(cancellationToken), cancellationToken);

    private async Task<RelatedEvents> RelatedCoreAsync(string id, string? runId, string? target, long timestampNanos, CancellationToken cancellationToken)
    {
        var hasRun = !string.IsNullOrWhiteSpace(runId);
        var hasTarget = !string.IsNullOrWhiteSpace(target);
        if (!hasRun && !hasTarget)
        {
            return new RelatedEvents(Array.Empty<AuditEvent>(), RelatedBasis.None);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", RelatedLimit);

        RelatedBasis basis;
        if (hasRun)
        {
            basis = RelatedBasis.Run;
            command.Parameters.AddWithValue("$run", runId);
            command.CommandText = RunSql;
        }
        else
        {
            basis = RelatedBasis.Target;
            var centre = timestampNanos;
            var half = (long)TargetWindow.TotalMilliseconds * 1_000_000L;
            command.Parameters.AddWithValue("$target", target);
            command.Parameters.AddWithValue("$from", centre - half);
            command.Parameters.AddWithValue("$to", centre + half);
            command.CommandText = TargetSql;
        }

        var events = new List<AuditEvent>(RelatedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(AuditReader.Map(reader));
        }

        if (basis == RelatedBasis.Run)
        {
            events = events.OrderByDescending(e => e.TimestampNanos).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();
        }

        return new RelatedEvents(events, basis);
    }

    private static string Select =>
        "SELECT " + AuditReader.SelectColumns + ", " + AuditReader.RetentionColumn + " AS sort_nanos FROM audit_events e ";

    private static string Order => " ORDER BY " + AuditReader.RetentionColumn + " DESC, e.id DESC LIMIT $limit";

    /// <summary>
    /// A run's entries in <c>idx_audit_run_id</c> are ordered by rowid (insertion order, which follows time), so reading the index
    /// backwards gives the newest eight with no sort whatever the run's size; they are put in timestamp order afterwards.
    /// </summary>
    private static string RunSql =>
        "SELECT " + AuditReader.SelectColumns + ", " + AuditReader.RetentionColumn + " AS sort_nanos FROM audit_events e " +
        "WHERE e.run_id = $run AND COALESCE(e.id, '') <> $id ORDER BY e.rowid DESC LIMIT $limit";

    private static string TargetSql =>
        Select + "WHERE e.target = $target AND COALESCE(e.id, '') <> $id AND " +
        AuditReader.RetentionColumn + " >= $from AND " + AuditReader.RetentionColumn + " < $to" + Order;

    private const string FindingsSql = """
        SELECT id, scanner, target, timestamp, raw_json
        FROM scan_results
        WHERE run_id = $run AND raw_json IS NOT NULL AND raw_json != ''
        ORDER BY timestamp DESC
        LIMIT $limit
        """;

    private async Task<IReadOnlyList<RunFinding>> FindingsCoreAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: FindingsSql is a const string; $run and $limit are bound
        command.CommandText = FindingsSql;
        command.Parameters.AddWithValue("$run", runId);
        // A scan may hold many findings and one finding is all that is needed from a scan, so ten scans bound the work.
        command.Parameters.AddWithValue("$limit", FindingsLimit);

        var findings = new List<RunFinding>(FindingsLimit);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (findings.Count < FindingsLimit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var scanId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var scanner = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var target = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var raw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var json = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                var stamp = DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

                foreach (var finding in ParseFindings(scanId, scanner, target, stamp, json))
                {
                    findings.Add(finding);
                    if (findings.Count >= FindingsLimit)
                    {
                        break;
                    }
                }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // No scan_results table (a database older than the scanner): nothing to list.
            return Array.Empty<RunFinding>();
        }

        return findings;
    }

    /// <summary>The <c>findings</c> array of one scan's raw JSON; empty for anything that is not that shape.</summary>
    internal static IEnumerable<RunFinding> ParseFindings(string scanId, string scanner, string target, DateTimeOffset timestamp, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("findings", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                yield return new RunFinding(
                    scanId,
                    Text(item, "scanner") is { Length: > 0 } own ? own : scanner,
                    target,
                    Text(item, "title") is { Length: > 0 } title ? title : $"Finding {index.ToString(CultureInfo.InvariantCulture)}",
                    Text(item, "severity"),
                    Text(item, "description"),
                    Text(item, "location"),
                    timestamp);
            }
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(RelatedBasis basis, string prefix, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: prefix is the literal "EXPLAIN QUERY PLAN " (its one caller) and the switch yields RunSql or TargetSql, both concatenations of consts; every value is bound
        command.CommandText = prefix + basis switch
        {
            RelatedBasis.Run => RunSql,
            RelatedBasis.Target => TargetSql,
            _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Nothing to explain."),
        };
        command.Parameters.AddWithValue("$id", "x");
        command.Parameters.AddWithValue("$limit", RelatedLimit);
        command.Parameters.AddWithValue("$run", "x");
        command.Parameters.AddWithValue("$target", "x");
        command.Parameters.AddWithValue("$from", 0L);
        command.Parameters.AddWithValue("$to", long.MaxValue);
        return await PlanAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ExplainFindingsCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- constant: the EXPLAIN prefix and the const FindingsSql; $run and $limit are bound
        command.CommandText = "EXPLAIN QUERY PLAN " + FindingsSql;
        command.Parameters.AddWithValue("$run", "x");
        command.Parameters.AddWithValue("$limit", FindingsLimit);
        return await PlanAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> PlanAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }
}
