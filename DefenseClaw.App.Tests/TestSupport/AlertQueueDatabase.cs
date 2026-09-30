using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A throwaway <c>audit.db</c> for the alert queue: the genuine DDL (<c>Fixtures/audit-schema.sql</c>, with the retention triggers, the
/// bucket indexes and the acknowledgement table), synthetic findings only. <see cref="AuditTestDatabase"/> builds bulk INFO rows
/// for the Audit panel; this one adds what the queue reads — findings at a chosen severity, acknowledgements, and a pre-v8 shape.
/// </summary>
internal sealed class AlertQueueDatabase
{
    public AlertQueueDatabase(string path)
    {
        Path = path;
        var schema = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-schema.sql"));
        Execute(schema);
    }

    public string Path { get; }

    /// <summary>A finding as the gateway writes it: bucket <c>security.finding</c>, event <c>finding.observed</c>.</summary>
    public void AddFinding(string id, DateTimeOffset at, string severity, string? connector = "claudecode", string action = "scan-finding")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, connector, event_name)
            VALUES ($id, $timestamp, $action, $target, 'audit_logger', $severity, 'security.finding', $connector, 'finding.observed')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", Format(at));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", "/synthetic/" + id);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Records an acknowledgement the way the gateway's projection does, which takes the alert out of the queue.</summary>
    public void Acknowledge(string alertId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_acknowledgement_projection
                (alert_id, disposition, actor, disposition_at, projection_version, source, source_event_id, updated_at)
            VALUES ($id, 'acknowledged', 'test', '2026-09-30T12:00:00Z', 1, 'modern', 'src', '2026-09-30T12:00:00Z')
            """;
        command.Parameters.AddWithValue("$id", alertId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>A database from before schema v8: no bucket, no event_name, no acknowledgement table.</summary>
    public static AlertQueueDatabase Legacy(string path)
    {
        var database = new AlertQueueDatabase(path, legacy: true);
        return database;
    }

    private AlertQueueDatabase(string path, bool legacy)
    {
        Path = path;
        if (legacy)
        {
            Execute("""
                CREATE TABLE audit_events (id TEXT PRIMARY KEY, timestamp TEXT, action TEXT, target TEXT, actor TEXT,
                    details TEXT, severity TEXT, run_id TEXT, structured_json TEXT, connector TEXT);
                INSERT INTO audit_events (id, timestamp, action, severity) VALUES ('legacy-1', '2026-07-22T12:00:00Z', 'scan-finding', 'HIGH');
                """);
        }
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>RFC3339 with 7 fractional digits, the shape Go writes.</summary>
    public static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";
}
