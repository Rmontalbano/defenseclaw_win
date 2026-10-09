using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Builds a synthetic <c>audit.db</c> from the genuine DDL (<c>Fixtures/audit-schema.sql</c>, linked from
/// the Core suite) so the retention trigger and indexes the reader paginates on behave as in production.
/// Every row is invented; nothing is copied from a real database.
/// </summary>
internal static class AuditTestDatabase
{
    /// <summary>
    /// Creates <paramref name="path"/> and fills it with <paramref name="rows"/> events, newest first by
    /// index: row 0 is one second old, row 1 two seconds, and so on, so they all sit inside the panel's
    /// default 24-hour window. <paramref name="connectorFor"/> maps a row index to its connector (null =
    /// a platform row).
    /// </summary>
    public static void Create(string path, int rows, Func<int, string?>? connectorFor = null, string? structuredJson = null, DateTimeOffset? newest = null, string? idPrefix = null)
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-schema.sql"));

        // "newest" dates the first row (one second before it) instead of now: an archive's history is old.
        var now = newest ?? DateTimeOffset.UtcNow;

        using (var connection = Open(path))
        {
            // The schema and the rows in one transaction. The schema is ~200 statements, and run one by one each is a commit of its own - a
            // journal file created, synced and deleted - which took ~2 s a database on a quiet machine (30 ms in a transaction, the same
            // sqlite_master either way) and was nearly all of what a test over this database spent, on a machine where every file operation
            // can be slow.
            using var transaction = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = transaction;
                ddl.CommandText = schema;
                _ = ddl.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, connector, event_name)
                VALUES ($id, $timestamp, 'hook_decision', '', 'audit_logger', $details, 'INFO', $structured, 'guardrail.evaluation', $connector, 'evt')
                """;

            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var timestamp = insert.Parameters.Add("$timestamp", SqliteType.Text);
            var details = insert.Parameters.Add("$details", SqliteType.Text);
            var structured = insert.Parameters.Add("$structured", SqliteType.Text);
            var connector = insert.Parameters.Add("$connector", SqliteType.Text);

            for (var i = 0; i < rows; i++)
            {
                id.Value = (idPrefix ?? "evt-") + i.ToString("D6", CultureInfo.InvariantCulture);
                timestamp.Value = (now - TimeSpan.FromSeconds(i + 1)).UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";
                details.Value = "synthetic event " + i.ToString(CultureInfo.InvariantCulture);
                structured.Value = (object?)structuredJson ?? DBNull.Value;
                connector.Value = (object?)(connectorFor is null ? "claudecode" : connectorFor(i)) ?? DBNull.Value;
                _ = insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Pooled connections would keep the file locked and stop the scratch directory being removed.
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }
}
