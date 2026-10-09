using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Adds synthetic events to a database <see cref="AuditTestDatabase"/> made, the way the gateway would: <c>retention_timestamp_unix_nano</c> is left
/// to the schema's trigger. Every value is invented. One connection per call, not pooled, so the file is free again when the call returns.
/// </summary>
internal static class AuditEventWriter
{
    /// <summary>One event: a quiet INFO hook decision unless told otherwise.</summary>
    public static void Add(
        string path,
        string id,
        DateTimeOffset at,
        string action = "hook_decision",
        string severity = "INFO",
        string? details = "synthetic extra",
        string? connector = "claudecode",
        string bucket = "guardrail.evaluation",
        string? runId = null,
        string target = "")
    {
        using var connection = Open(path);
        Insert(connection, null, id, at, action, severity, details, connector, bucket, runId, target);
    }

    /// <summary>Many events in one transaction: <paramref name="make"/> is called with 0 ... count - 1 and returns the event's values.</summary>
    public static void AddMany(string path, int count, Func<int, (string Id, DateTimeOffset At, string Action, string Severity, string? Details, string? Connector)> make)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var row = make(i);
            Insert(connection, transaction, row.Id, row.At, row.Action, row.Severity, row.Details, row.Connector, "guardrail.evaluation", null, string.Empty);
        }

        transaction.Commit();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        DateTimeOffset at,
        string action,
        string severity,
        string? details,
        string? connector,
        string bucket,
        string? runId,
        string target)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, connector, event_name, run_id)
            VALUES ($id, $ts, $action, $target, 'audit_logger', $details, $severity, $bucket, $connector, 'evt', $run)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }
}
