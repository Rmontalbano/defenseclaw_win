using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// The synthetic fixture set derived from the DefenseClaw source commit 95159fd (<c>Fixtures/runtime-95159fd</c>): shapes taken from a
/// fresh install of that source, every machine, user, id, digest and timestamp replaced. See <c>docs/RUNTIME-COMPAT-95159fd.md</c>.
/// </summary>
public static class RuntimeFixtures
{
    public const string Directory = "runtime-95159fd";

    /// <summary>The DDL of that commit's <c>audit.db</c> after its 53 migrations, as its own <c>.schema</c> printed it.</summary>
    public const string AuditSchema = Directory + "/audit/audit-schema.sql";

    public const string JudgeBodiesSchema = Directory + "/audit/judge-bodies-schema.sql";

    /// <summary>Migrations the schema fixture stands for.</summary>
    public const int MigrationCount = 53;

    public static string Read(string relative) => FixtureFiles.ReadText(Directory + "/" + relative);

    /// <summary>A throwaway <c>audit.db</c> with that commit's schema and its <c>schema_version</c> rows 1..53.</summary>
    public static TestAuditDatabase CreateAuditDatabase()
    {
        var database = new TestAuditDatabase(AuditSchema);
        using var connection = database.OpenWritable();
        using var transaction = connection.BeginTransaction();
        for (var version = 1; version <= MigrationCount; version++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES ($v, '2030-01-15 09:04:50.000000000 +0000 UTC')";
            _ = command.Parameters.AddWithValue("$v", version);
            _ = command.ExecuteNonQuery();
        }

        transaction.Commit();
        return database;
    }

    /// <summary>
    /// Inserts one <c>audit_events</c> row the way that commit's v8 event-history writer does (<c>internal/audit/event_history_v8.go</c>):
    /// RFC 3339 with nine fractional digits, <c>source</c> and <c>signal = 'logs'</c> filled, <c>schema_version</c> 7, a bucket-catalog version.
    /// </summary>
    public static void InsertV8Event(
        TestAuditDatabase database,
        string id,
        string timestamp,
        string action,
        string bucket,
        string eventName,
        string severity = "INFO",
        string? connector = null,
        string? details = null,
        string? structuredJson = null,
        string? payloadJson = null,
        string actor = "audit_logger",
        string target = "",
        long? enforced = null)
    {
        using var connection = database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events
                (id, timestamp, action, target, actor, details, structured_json, severity, run_id, connector, enforced,
                 bucket, event_name, source, signal, bucket_catalog_version, payload_json, record_schema_version,
                 schema_version, binary_version, mandatory, sidecar_instance_id)
            VALUES
                ($id, $ts, $action, $target, $actor, $details, $structured, $severity, '00000000-0000-4000-8000-000000000001', $connector, $enforced,
                 $bucket, $event, 'sidecar', 'logs', 1, $payload, 1,
                 7, '1.0.0', 0, '00000000-0000-4000-8000-000000000002')
            """;
        _ = command.Parameters.AddWithValue("$id", id);
        _ = command.Parameters.AddWithValue("$ts", timestamp);
        _ = command.Parameters.AddWithValue("$action", action);
        _ = command.Parameters.AddWithValue("$target", target);
        _ = command.Parameters.AddWithValue("$actor", actor);
        _ = command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$structured", (object?)structuredJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$severity", severity);
        _ = command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$enforced", (object?)enforced ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$bucket", bucket);
        _ = command.Parameters.AddWithValue("$event", eventName);
        _ = command.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>A scalar query against <paramref name="database"/>, for assertions about its shape.</summary>
    public static T Scalar<T>(TestAuditDatabase database, string sql)
    {
        using var connection = database.OpenWritable();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
