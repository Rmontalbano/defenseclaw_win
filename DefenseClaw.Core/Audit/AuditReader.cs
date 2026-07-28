using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// Read-only, parameterized access to <c>~/.defenseclaw/audit.db</c>.
/// <para>
/// The database is actively written by the gateway in WAL mode, so every connection is
/// opened with <c>Mode=ReadOnly</c>: we are a guest in someone else's file and must never
/// take a write lock. Verified against a live 0.8.7 install — a read-only connection
/// against a WAL database with a hot -wal/-shm pair opens fine.
/// </para>
/// </summary>
public sealed class AuditReader
{
    /// <summary>
    /// Sort/range key. <c>retention_timestamp_unix_nano</c> is maintained by the DB's own
    /// AFTER INSERT/UPDATE triggers and indexed by
    /// <c>idx_retention_audit_events_timestamp</c>. The COALESCE fallback derives nanos
    /// from the text for any row a trigger somehow missed.
    /// </summary>
    private const string SortKey = """
        COALESCE(
            e.retention_timestamp_unix_nano,
            CAST(strftime('%s', substr(e.timestamp, 1, 19) || 'Z') AS INTEGER) * 1000000000
        )
        """;

    private const string SelectColumns = """
        e.id, e.timestamp, e.action, e.target, e.actor, e.details, e.severity,
        e.structured_json, e.bucket, e.connector, e.event_name, e.agent_name,
        e.tool_name, e.session_id, e.run_id, e.request_id, e.trace_id,
        e.source, e.signal, e.binary_version
        """;

    private readonly string _connectionString;

    public AuditReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _connectionString = BuildReadOnlyConnectionString(databasePath);
    }

    public string DatabasePath { get; }

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>The read-only connection string used for every query.</summary>
    public static string BuildReadOnlyConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

    /// <summary>Fetches one keyset page. Never uses OFFSET.</summary>
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = query.EffectiveLimit;
        var sql = new StringBuilder()
            .Append("SELECT ").Append(SelectColumns).Append(", ").Append(SortKey).AppendLine(" AS sort_nanos")
            .AppendLine("FROM audit_events e");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        AppendWhere(sql, command, query, includeCursor: true);

        var direction = query.Ascending ? "ASC" : "DESC";
        sql.Append("ORDER BY sort_nanos ").Append(direction)
           .Append(", e.id ").Append(direction).AppendLine()
           .AppendLine("LIMIT $limit");

        // Fetch one extra row to answer HasMore without a second round trip.
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.CommandText = sql.ToString();

        var events = new List<AuditEvent>(limit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(Map(reader));
            }
        }

        var hasMore = events.Count > limit;
        if (hasMore)
        {
            events.RemoveRange(limit, events.Count - limit);
        }

        var nextCursor = hasMore && events.Count > 0 ? events[^1].Cursor : (AuditCursor?)null;
        return new AuditPage(events, nextCursor, hasMore);
    }

    /// <summary>Convenience wrapper returning just the rows.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken = default) =>
        (await QueryAsync(query, cancellationToken).ConfigureAwait(false)).Events;

    /// <summary>Single row by primary key.</summary>
    public async Task<AuditEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns}, {SortKey} AS sort_nanos FROM audit_events e WHERE e.id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>
    /// Counts matching rows per severity — the dashboard tiles. Honours every filter on
    /// <paramref name="query"/> except paging.
    /// </summary>
    public async Task<IReadOnlyDictionary<AuditSeverity, int>> CountBySeverityAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = new StringBuilder()
            .AppendLine("SELECT e.severity, COUNT(*) FROM audit_events e");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        AppendWhere(sql, command, query, includeCursor: false);
        sql.AppendLine("GROUP BY e.severity");
        command.CommandText = sql.ToString();

        var counts = new Dictionary<AuditSeverity, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var severity = AuditSeverityExtensions.Parse(reader.IsDBNull(0) ? null : reader.GetString(0));
            var count = reader.GetInt32(1);
            counts[severity] = counts.TryGetValue(severity, out var existing) ? existing + count : count;
        }

        return counts;
    }

    /// <summary>Total matching rows. Separate from paging so tiles stay accurate.</summary>
    public async Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = new StringBuilder().AppendLine("SELECT COUNT(*) FROM audit_events e");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        AppendWhere(sql, command, query, includeCursor: false);
        command.CommandText = sql.ToString();

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
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

    private async Task<IReadOnlyList<string>> DistinctAsync(string column, CancellationToken cancellationToken)
    {
        // Column names are compile-time constants from this class only — never user input.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT {column} FROM audit_events WHERE {column} IS NOT NULL AND {column} <> '' ORDER BY {column}";

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void AppendWhere(StringBuilder sql, SqliteCommand command, AuditQuery query, bool includeCursor)
    {
        var clauses = new List<string>();

        var buckets = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Bucket))
        {
            buckets.Add(query.Bucket);
        }

        if (query.Buckets is not null)
        {
            buckets.AddRange(query.Buckets.Where(b => !string.IsNullOrWhiteSpace(b)));
        }

        if (buckets.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < buckets.Count; i++)
            {
                var name = $"$bucket{i.ToString(CultureInfo.InvariantCulture)}";
                names.Add(name);
                command.Parameters.AddWithValue(name, buckets[i]);
            }

            clauses.Add($"e.bucket IN ({string.Join(", ", names)})");
        }

        if (query.MinimumSeverity is { } minimum && minimum != AuditSeverity.Unknown)
        {
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

        if (!string.IsNullOrWhiteSpace(query.Connector))
        {
            command.Parameters.AddWithValue("$connector", query.Connector);
            clauses.Add(query.IncludeNullConnector
                ? "(e.connector = $connector OR e.connector IS NULL)"
                : "e.connector = $connector");
        }

        if (!string.IsNullOrWhiteSpace(query.ActionContains))
        {
            command.Parameters.AddWithValue("$action", Like(query.ActionContains));
            clauses.Add("e.action LIKE $action ESCAPE '\\'");
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

        if (query.From is { } from)
        {
            command.Parameters.AddWithValue("$from", ToUnixNanos(from));
            clauses.Add($"{SortKey} >= $from");
        }

        if (query.To is { } to)
        {
            command.Parameters.AddWithValue("$to", ToUnixNanos(to));
            clauses.Add($"{SortKey} < $to");
        }

        if (includeCursor && query.After is { } cursor)
        {
            command.Parameters.AddWithValue("$cursorNanos", cursor.TimestampNanos);
            command.Parameters.AddWithValue("$cursorId", cursor.Id);
            var comparison = query.Ascending ? ">" : "<";
            clauses.Add(
                $"({SortKey} {comparison} $cursorNanos OR ({SortKey} = $cursorNanos AND e.id {comparison} $cursorId))");
        }

        if (clauses.Count > 0)
        {
            sql.Append("WHERE ").AppendLine(string.Join("\n  AND ", clauses));
        }
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

    private static AuditEvent Map(SqliteDataReader reader)
    {
        var rawTimestamp = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var nanos = reader.IsDBNull(20) ? 0L : reader.GetInt64(20);

        return new AuditEvent
        {
            Id = reader.GetString(0),
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
