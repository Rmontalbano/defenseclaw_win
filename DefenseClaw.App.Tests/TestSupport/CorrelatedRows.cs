using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Synthetic audit rows that carry what the search tokens and the correlation ids read (CUST-261): an actor, a connector, a run, trace, request and
/// session id, and an <c>actions</c> row for the Current state lookup. Added to a database <see cref="AuditTestDatabase"/> or <see cref="AlertQueueDatabase"/>
/// made from the real DDL; those two shared fixtures are untouched. Every value is invented; one connection per call, not pooled.
/// </summary>
internal static class CorrelatedRows
{
    /// <summary>One audit row with every column the search and the correlation rows read.</summary>
    public static void Add(
        string path,
        string id,
        DateTimeOffset at,
        string action = "scan-finding",
        string severity = "HIGH",
        string? connector = "claudecode",
        string? bucket = "security.finding",
        string eventName = "finding.observed",
        string target = "",
        string actor = "audit_logger",
        string? details = null,
        string? runId = null,
        string? traceId = null,
        string? requestId = null,
        string? sessionId = null,
        string? structuredJson = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, connector, event_name, run_id, trace_id, request_id, session_id)
            VALUES ($id, $ts, $action, $target, $actor, $details, $severity, $structured, $bucket, $connector, $eventName, $run, $trace, $request, $session)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$structured", (object?)structuredJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$bucket", (object?)bucket ?? DBNull.Value);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventName", eventName);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$trace", (object?)traceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>A row of the <c>actions</c> table: what is being done to a skill, MCP server, plugin or tool (<paramref name="actionsJson"/> is its <c>actions_json</c>).</summary>
    public static void AddAction(string path, string targetType, string targetName, string actionsJson, string connector = "")
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions (id, target_type, target_name, source_path, actions_json, reason, updated_at, connector)
            VALUES ($id, $type, $name, NULL, $json, 'synthetic', '2026-10-01T12:00:00Z', $connector)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
        command.Parameters.AddWithValue("$type", targetType);
        command.Parameters.AddWithValue("$name", targetName);
        command.Parameters.AddWithValue("$json", actionsJson);
        command.Parameters.AddWithValue("$connector", connector);
        _ = command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
