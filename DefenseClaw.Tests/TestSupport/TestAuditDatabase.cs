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

    /// <summary>
    /// When set to a fixture path under <c>Fixtures</c> (for example <c>runtime-95159fd/audit/audit-schema.sql</c>), every
    /// database this class builds uses that DDL instead of the 0.8.10 one, so the whole Core suite can be run once against another
    /// runtime's schema (<c>DEFENSECLAW_TEST_AUDIT_SCHEMA=... dotnet test</c>). Unset in CI: the default is the 0.8.10 schema.
    /// </summary>
    public const string SchemaOverrideVariable = "DEFENSECLAW_TEST_AUDIT_SCHEMA";

    public TestAuditDatabase()
        : this(Environment.GetEnvironmentVariable(SchemaOverrideVariable) is { Length: > 0 } over ? over : FixtureFiles.AuditSchema)
    {
    }

    /// <param name="schemaFixture">The DDL to build the database from, relative to the <c>Fixtures</c> directory.</param>
    public TestAuditDatabase(string schemaFixture)
    {
        _directory = new TempDirectory("dcw-audit");
        Path = _directory.File("audit.db");

        using var connection = OpenWritable();
        using var command = connection.CreateCommand();

        // One transaction for the whole schema. It is ~200 statements, and run one by one each is a commit of its own - a journal file
        // created, synced and deleted - which took ~2 s a database on a quiet machine (30 ms in one transaction, the same sqlite_master
        // either way) and many times that on a loaded or slow one: most of what every test using this class spent, and a long stretch of
        // file-system work for a scanner or a busy disk to get in the way of.
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;

        // SQLite's own tokenizer splits the statements, so the multi-statement trigger
        // bodies in the DDL survive intact.
        command.CommandText = FixtureFiles.ReadText(schemaFixture);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public string Path { get; }

    public SqliteConnection OpenWritable()
    {
        var connection = new SqliteConnection(SqlitePools.WritableConnectionString(Path));

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
        string actor = "audit_logger",
        long? enforced = null,
        string target = "")
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events
                (id, timestamp, action, target, actor, details, severity,
                 structured_json, bucket, connector, event_name, tool_name, session_id, enforced)
            VALUES
                ($id, $timestamp, $action, $target, $actor, $details, $severity,
                 $structured, $bucket, $connector, $eventName, $toolName, $sessionId, $enforced)
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
        command.Parameters.AddWithValue("$enforced", (object?)enforced ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", target);
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
        // Microsoft.Data.Sqlite pools connections; without this the file stays locked and the temp directory cannot be removed. Only this
        // database's pools are cleared: ClearAllPools() would also dispose a connection another test's reader is taking from its pool.
        SqlitePools.Release(_directory.Path);
        _directory.Dispose();
    }
}
