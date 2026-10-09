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

    /// <summary>A scan of <paramref name="target"/> (for <paramref name="runId"/>) with one finding per <paramref name="findings"/> entry (severity, title).</summary>
    public void AddScan(string scanId, string target, string? runId, DateTimeOffset at, params (string Severity, string Title)[] findings)
    {
        using var connection = Open();
        using (var scan = connection.CreateCommand())
        {
            scan.CommandText = "INSERT INTO scan_results (id, scanner, target, timestamp, run_id) VALUES ($id, 'skill-scanner', $target, $ts, $run)";
            scan.Parameters.AddWithValue("$id", scanId);
            scan.Parameters.AddWithValue("$target", target);
            scan.Parameters.AddWithValue("$ts", Format(at));
            scan.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
            _ = scan.ExecuteNonQuery();
        }

        for (var i = 0; i < findings.Length; i++)
        {
            using var finding = connection.CreateCommand();
            finding.CommandText =
                """
                INSERT INTO scan_findings (id, scan_id, scanner, target, rule_id, severity, title, description, location, remediation, timestamp)
                VALUES ($id, $scan, 'skill-scanner', $target, 'R-1', $severity, $title, 'Calls out to an unknown host.', 'main.py:12', 'Remove the call.', $ts)
                """;
            finding.Parameters.AddWithValue("$id", scanId + "-f" + i);
            finding.Parameters.AddWithValue("$scan", scanId);
            finding.Parameters.AddWithValue("$target", target);
            finding.Parameters.AddWithValue("$severity", findings[i].Severity);
            finding.Parameters.AddWithValue("$title", findings[i].Title);
            finding.Parameters.AddWithValue("$ts", Format(at));
            _ = finding.ExecuteNonQuery();
        }
    }

    /// <summary>An audit row of any action on <paramref name="target"/> (not a finding: no bucket), for a target's history.</summary>
    public void AddEvent(string id, DateTimeOffset at, string target, string action, string severity = "INFO")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit_events (id, timestamp, action, target, actor, severity) VALUES ($id, $ts, $action, $target, 'audit_logger', $severity)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", Format(at));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$severity", severity);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>An egress decision as the gateway records it: bucket <c>network.egress</c>, the <c>defenseclaw.network.*</c> attributes in <c>structured_json</c>.</summary>
    public void AddEgress(string id, DateTimeOffset at, string decision, string branch, bool looksLikeLlm, string target = "api.example.test")
    {
        var attributes =
            "{\"defenseclaw.network.decision\":\"" + decision + "\",\"defenseclaw.network.branch\":\"" + branch +
            "\",\"defenseclaw.network.looks_like_llm\":" + (looksLikeLlm ? "true" : "false") +
            ",\"defenseclaw.network.target_ref\":\"" + target + "\",\"defenseclaw.network.reason\":\"policy says so\"}";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, bucket, connector, event_name)
            VALUES ($id, $ts, 'network-egress', '', 'gateway', 'INFO', $structured, 'network.egress', 'claudecode', 'network.egress')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", Format(at));
        command.Parameters.AddWithValue("$structured", attributes);
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

        // One transaction, whatever the script: the audit schema is ~200 statements, and run one by one each is a commit of its own - a
        // journal file created, synced and deleted - which took ~2 s a database on a quiet machine (30 ms in a transaction, the same
        // sqlite_master either way) and is where a test over this database spent nearly all of its time.
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
        transaction.Commit();
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
