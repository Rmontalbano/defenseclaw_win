using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// A throwaway audit.db built from the real DDL in <c>Fixtures/audit-schema.sql</c> and
/// filled with synthetic rows only. Using the genuine schema means the retention
/// triggers and the <c>idx_retention_audit_events_timestamp</c> index behave exactly as
/// they do in production, which is what <see cref="DefenseClaw.Core.Audit.AuditReader"/>
/// paginates on — but no real audit data is ever copied into the repo or the test run.
/// </summary>
public sealed class TestAuditDatabase : IDisposable
{
    private readonly TempDirectory _directory;

    public TestAuditDatabase()
    {
        _directory = new TempDirectory("dcw-audit");
        Path = _directory.File("audit.db");

        using var connection = OpenWritable();
        using var command = connection.CreateCommand();

        // SQLite's own tokenizer splits the statements, so the multi-statement trigger
        // bodies in the DDL survive intact.
        command.CommandText = FixtureFiles.ReadText(FixtureFiles.AuditSchema);
        command.ExecuteNonQuery();
    }

    public string Path { get; }

    public SqliteConnection OpenWritable()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        return connection;
    }

    /// <summary>
    /// Inserts one synthetic audit_events row. <c>retention_timestamp_unix_nano</c> is left
    /// to the schema's AFTER INSERT trigger, exactly as the gateway leaves it.
    /// </summary>
    public void InsertEvent(
        string id,
        DateTimeOffset timestamp,
        string action,
        string? severity = null,
        string? bucket = null,
        string? connector = null,
        string? details = null,
        string? structuredJson = null,
        string? eventName = null,
        string? toolName = null,
        string? sessionId = null,
        string actor = "audit_logger")
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events
                (id, timestamp, action, target, actor, details, severity,
                 structured_json, bucket, connector, event_name, tool_name, session_id)
            VALUES
                ($id, $timestamp, $action, '', $actor, $details, $severity,
                 $structured, $bucket, $connector, $eventName, $toolName, $sessionId)
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", (object?)severity ?? DBNull.Value);
        command.Parameters.AddWithValue("$structured", (object?)structuredJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$bucket", (object?)bucket ?? DBNull.Value);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventName", (object?)eventName ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolName", (object?)toolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// RFC3339 with 7 fractional digits — the shape Go's time.RFC3339Nano writes into
    /// the real database.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections; without this the file stays locked and
        // the temp directory cannot be removed.
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}
