using System.Globalization;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The scan, finding, correlation and egress reads break timestamp ties by <c>rowid DESC</c>, as <see cref="AuditReader"/> does (CUST-378) and the
/// Mac's later build does for every ordered read (CUST-389): with a <c>LIMIT</c> in the statement, which of a run of tied rows survives the cut must
/// not depend on the plan. Every fixture row below carries the same timestamp, so the rowid is the only thing that orders them. Synthetic rows only.
/// </summary>
public sealed class AuditReadTieOrderTests : IDisposable
{
    private static readonly DateTimeOffset Shared = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database; values go in as parameters
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        _ = command.ExecuteNonQuery();
    }

    private static string Pad(int value, int width = 2) => value.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Stamp => TestAuditDatabase.FormatTimestamp(Shared);

    private void ScanResult(string id, string run, string? rawJson = null, string target = "skill-x") =>
        Exec(
            "INSERT INTO scan_results (id, scanner, target, timestamp, raw_json, run_id) VALUES ($id, 'skill-scanner', $target, $ts, $raw, $run)",
            ("$id", id), ("$target", target), ("$ts", Stamp), ("$raw", rawJson), ("$run", run));

    private void ScanFinding(string id, string scan, string title) =>
        Exec(
            """
            INSERT INTO scan_findings (id, scan_id, scanner, target, rule_id, severity, title, description, timestamp)
            VALUES ($id, $scan, 'skill-scanner', 'skill-x', 'R-1', 'HIGH', $title, 'why', $ts)
            """,
            ("$id", id), ("$scan", scan), ("$title", title), ("$ts", Stamp));

    private static long Nanos => (Shared - DateTimeOffset.UnixEpoch).Ticks * 100L;

    /// <summary>
    /// An audit event with no id (the schema allows it), so nothing but the rowid separates it from its neighbours on one timestamp. The retention
    /// instant is written here: the schema's insert trigger keys on the id, so it never fills it in for a NULL one.
    /// </summary>
    private void EventWithoutId(string details, string target) =>
        Exec(
            "INSERT INTO audit_events (id, timestamp, retention_timestamp_unix_nano, action, target, actor, details, severity) VALUES (NULL, $ts, $nanos, 'scan', $target, 'audit_logger', $details, 'INFO')",
            ("$ts", Stamp), ("$nanos", Nanos), ("$target", target), ("$details", details));

    // ---- AlertDetailReader ----

    [Fact]
    public async Task The_scans_kept_for_an_alerts_findings_are_the_latest_written_of_a_tie()
    {
        // Fourteen scans of one run, all at one instant; only the newest ScanLimit (10) are read. One finding each, same severity, same time.
        for (var i = 1; i <= 14; i++)
        {
            ScanResult("s" + Pad(i), "run-1");
            ScanFinding("f" + Pad(i), "s" + Pad(i), "finding-" + Pad(i));
        }

        var findings = await new AlertDetailReader(_database.Path, readTimeout: TestTimeouts.Ceiling).ReadFindingsAsync("run-1", null, limit: 20);

        // Scans 05..14 (the ten written last) and, within the tie, the later finding first.
        Assert.Equal(
            Enumerable.Range(5, 10).Reverse().Select(i => "finding-" + Pad(i)),
            findings.Select(f => f.Title));
    }

    [Fact]
    public async Task The_findings_kept_under_the_cap_are_the_latest_written_of_a_tie()
    {
        // 120 findings of one scan at one instant: the statement's cap is max(limit * 5, 100) = 100, and the 20 dropped must be the oldest written.
        ScanResult("only", "run-1");
        for (var i = 1; i <= 120; i++)
        {
            ScanFinding("f" + Pad(i, 3), "only", "finding-" + Pad(i, 3));
        }

        var findings = await new AlertDetailReader(_database.Path, readTimeout: TestTimeouts.Ceiling).ReadFindingsAsync("run-1", null, limit: 3);

        Assert.Equal(new[] { "finding-120", "finding-119", "finding-118" }, findings.Select(f => f.Title));
    }

    // ---- AuditCorrelationReader ----

    [Fact]
    public async Task The_findings_of_a_run_come_from_the_latest_written_scans_of_a_tie()
    {
        for (var i = 1; i <= 14; i++)
        {
            ScanResult(
                "s" + Pad(i),
                "run-1",
                "{\"findings\":[{\"title\":\"scan-" + Pad(i) + "\",\"severity\":\"HIGH\"}]}");
        }

        var findings = await new AuditCorrelationReader(_database.Path).FindingsInRunAsync("run-1");

        Assert.Equal(AuditCorrelationReader.FindingsLimit, findings.Count);
        Assert.Equal(
            Enumerable.Range(5, 10).Reverse().Select(i => "scan-" + Pad(i)),
            findings.Select(f => f.Title));
    }

    [Fact]
    public async Task The_events_related_by_target_keep_the_latest_written_of_a_tie_even_with_no_ids()
    {
        // Twelve id-less events of one target at one instant; the reader lists eight.
        for (var i = 1; i <= 12; i++)
        {
            EventWithoutId("e" + Pad(i), "skill-x");
        }

        var related = await new AuditCorrelationReader(_database.Path).RelatedAsync("anchor", null, "skill-x", Nanos);

        Assert.Equal(RelatedBasis.Target, related.Basis);
        Assert.Equal(
            Enumerable.Range(5, 8).Reverse().Select(i => "e" + Pad(i)),
            related.Events.Select(e => e.Details));
    }

    [Fact]
    public async Task The_events_related_by_run_list_a_tie_of_id_less_events_latest_written_first()
    {
        for (var i = 1; i <= 5; i++)
        {
            Exec(
                "INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, run_id) VALUES (NULL, $ts, 'scan', '', 'audit_logger', $details, 'INFO', 'run-1')",
                ("$ts", Stamp), ("$details", "e" + Pad(i, 1)));
        }

        var related = await new AuditCorrelationReader(_database.Path).RelatedAsync("anchor", "run-1", null, 0);

        Assert.Equal(new[] { "e5", "e4", "e3", "e2", "e1" }, related.Events.Select(e => e.Details));
    }

    // ---- NetworkEgressReader ----

    [Fact]
    public async Task The_older_egress_table_keeps_the_latest_written_of_a_tie_under_the_limit()
    {
        for (var i = 1; i <= 6; i++)
        {
            Exec(
                "INSERT INTO network_egress_events (id, timestamp, hostname, policy_outcome, blocked, severity) VALUES ($id, $ts, $host, 'allow', 0, 'INFO')",
                ("$id", "n" + Pad(i, 1)), ("$ts", Stamp), ("$host", "host-" + Pad(i, 1) + ".example.test"));
        }

        var rows = await new NetworkEgressReader(_database.Path).ReadRecentAsync(limit: 3);

        Assert.Equal(new[] { "egress:n6", "egress:n5", "egress:n4" }, rows.Select(r => r.Id));
    }
}
