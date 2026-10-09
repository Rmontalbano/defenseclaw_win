using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A synthetic <c>audit.db</c> (the genuine DDL, no rows) to put configuration and policy changes into: <c>activity_events</c> rows
/// and canonical <c>compliance.activity</c> / <c>enforcement.action</c> audit rows. Every value is invented.
/// </summary>
internal sealed class MutationDatabase
{
    /// <summary>The newest change is this old; the rest are a minute apart behind it.</summary>
    public static readonly DateTimeOffset Newest = DateTimeOffset.UtcNow.AddMinutes(-3);

    private int _count;

    public MutationDatabase(string path)
    {
        Path = path;
        AuditTestDatabase.Create(path, 0);
    }

    public string Path { get; }

    /// <summary>Adds an <c>activity_events</c> row; each call is a minute older than the one before.</summary>
    public MutationDatabase Activity(
        string id,
        string action = "policy.update",
        string actor = "admin",
        string targetType = "policy",
        string targetId = "default",
        string? reason = null,
        string? before = null,
        string? after = null,
        string? diff = null,
        string? from = null,
        string? to = null)
    {
        Run(
            """
            INSERT INTO activity_events (id, timestamp, actor, action, target_type, target_id, reason, before_json, after_json, diff_json, version_from, version_to)
            VALUES ($id, $ts, $actor, $action, $tt, $ti, $reason, $before, $after, $diff, $from, $to)
            """,
            ("$id", id), ("$ts", NextTimestamp()), ("$actor", actor), ("$action", action), ("$tt", targetType), ("$ti", targetId),
            ("$reason", reason), ("$before", before), ("$after", after), ("$diff", diff), ("$from", from), ("$to", to));
        return this;
    }

    /// <summary>Adds a canonical audit row in <paramref name="bucket"/>.</summary>
    public MutationDatabase Change(string id, string action = "config.change.applied", string bucket = "compliance.activity", string? connector = null, string? structuredJson = null)
    {
        Run(
            """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, connector, event_name)
            VALUES ($id, $ts, $action, '', 'gateway_api', $details, 'INFO', $structured, $bucket, $connector, $action)
            """,
            ("$id", id), ("$ts", NextTimestamp()), ("$action", action), ("$details", "synthetic " + action), ("$structured", structuredJson ?? "{\"operation\":\"" + action + "\"}"),
            ("$bucket", bucket), ("$connector", connector));
        return this;
    }

    private string NextTimestamp() =>
        Newest.AddMinutes(-_count++).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    private void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database; values go in as parameters
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            _ = command.ExecuteNonQuery();
        }

        SqlitePools.Release(Path);
    }
}
