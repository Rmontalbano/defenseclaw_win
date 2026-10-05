using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Alerts inspector's two lookups against a database built from the real DDL and synthetic rows only: the findings of a run's (or
/// target's) scans, worst first, and the history of a target from the newest rows. The plans are pinned as well as the rows, because
/// on the live 6.7 GB database an unbounded history lookup is a 65 s walk.
/// </summary>
public sealed class AlertDetailReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private AlertDetailReader Reader(int window = AlertDetailReader.HistoryWindow) => new(_database.Path, window);

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

    private void Scan(string id, string target, int minute, string? run)
    {
        Exec(
            "INSERT INTO scan_results (id, scanner, target, timestamp, run_id) VALUES ($id, 'skill-scanner', $target, $ts, $run)",
            ("$id", id), ("$target", target), ("$ts", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute))), ("$run", run));
    }

    private void FindingRow(string id, string scan, int minute, string severity, string title, string? remediation = "Remove it", string? location = "a.py", long? line = 7, string target = "t")
    {
        Exec(
            """
            INSERT INTO scan_findings (id, scan_id, scanner, target, rule_id, severity, title, description, location, line_number, remediation, timestamp)
            VALUES ($id, $scan, 'skill-scanner', $target, 'R-1', $severity, $title, 'why', $location, $line, $remediation, $ts)
            """,
            ("$id", id), ("$scan", scan), ("$target", target), ("$severity", severity), ("$title", title), ("$location", location),
            ("$line", line), ("$remediation", remediation), ("$ts", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute))));
    }

    private void Event(string id, int minute, string target, string action = "scan-finding", string severity = "HIGH")
    {
        Exec(
            "INSERT INTO audit_events (id, timestamp, action, target, actor, severity) VALUES ($id, $ts, $action, $target, 'audit_logger', $severity)",
            ("$id", id), ("$ts", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute))), ("$action", action), ("$target", target), ("$severity", severity));
    }

    // ---- findings ----

    [Fact]
    public async Task Findings_of_a_runs_scans_come_worst_first_with_remediation_and_location()
    {
        Scan("s1", "/skills/a", 1, "run-1");
        Scan("s2", "/skills/b", 2, "run-1");
        Scan("other", "/skills/a", 3, "run-2");
        FindingRow("f1", "s1", 1, "LOW", "Minor");
        FindingRow("f2", "s2", 2, "CRITICAL", "Exfiltration", remediation: "Delete the call", location: "x.py", line: 12);
        FindingRow("f3", "other", 3, "HIGH", "Not this run");

        var findings = await Reader().ReadFindingsAsync("run-1", "/skills/a");

        Assert.Equal(new[] { "Exfiltration", "Minor" }, findings.Select(f => f.Title).ToArray());
        Assert.Equal("CRITICAL", findings[0].Severity);
        Assert.Equal("Delete the call", findings[0].Remediation);
        Assert.Equal("x.py:12", findings[0].Location);
    }

    [Fact]
    public async Task A_run_with_no_scans_falls_back_to_the_target()
    {
        Scan("s1", "/skills/a", 1, null);
        FindingRow("f1", "s1", 1, "MEDIUM", "By target");

        var findings = await Reader().ReadFindingsAsync("run-unknown", "/skills/a");

        Assert.Equal("By target", Assert.Single(findings).Title);
    }

    [Fact]
    public async Task Findings_are_capped_and_a_blank_key_reads_nothing()
    {
        Scan("s1", "/skills/a", 1, "run-1");
        for (var i = 0; i < 30; i++)
        {
            FindingRow($"f{i:D2}", "s1", i, "HIGH", $"Finding {i}");
        }

        Assert.Equal(20, (await Reader().ReadFindingsAsync("run-1", null)).Count);
        Assert.Empty(await Reader().ReadFindingsAsync(null, "  "));
        Assert.Empty(await Reader().ReadFindingsAsync("nope", "nothing"));
    }

    [Fact]
    public async Task A_database_without_the_scan_tables_or_without_a_file_has_no_findings()
    {
        Exec("DROP TABLE scan_findings");

        Assert.Empty(await Reader().ReadFindingsAsync("run-1", "/x"));
        Assert.Empty(await new AlertDetailReader(Path.Combine(Path.GetTempPath(), "dcw-missing-" + Guid.NewGuid().ToString("n"), "audit.db")).ReadFindingsAsync("r", "t"));
    }

    // ---- history ----

    [Fact]
    public async Task History_is_the_targets_other_events_newest_first_without_the_alert_itself()
    {
        for (var i = 0; i < 8; i++)
        {
            Event($"e{i}", i, "/skills/a", severity: i % 2 == 0 ? "HIGH" : "LOW");
        }

        Event("elsewhere", 9, "/skills/b");

        var history = await Reader().ReadHistoryAsync(new[] { "/skills/a" }, excludeId: "e7", limit: 5);

        Assert.Equal(new[] { "e6", "e5", "e4", "e3", "e2" }, history.Select(h => h.Id).ToArray());
        Assert.Equal("HIGH", history[0].Severity);
        Assert.Equal("scan-finding", history[0].Action);
    }

    [Fact]
    public async Task History_looks_only_inside_the_newest_window()
    {
        Event("old", 1, "/skills/a");
        for (var i = 0; i < 10; i++)
        {
            Event($"n{i}", 10 + i, "/skills/zzz");
        }

        var history = await Reader(window: 10).ReadHistoryAsync(new[] { "/skills/a" }, null);

        Assert.Empty(history);
        Assert.Single(await Reader(window: 11).ReadHistoryAsync(new[] { "/skills/a" }, null));
    }

    [Fact]
    public async Task History_with_no_target_reads_nothing_and_takes_either_spelling_of_the_target()
    {
        Event("a", 1, "/p/x");
        Event("b", 2, "structured-target");

        Assert.Empty(await Reader().ReadHistoryAsync(new string?[] { null, " " }, null));
        Assert.Equal(2, (await Reader().ReadHistoryAsync(new string?[] { "/p/x", "structured-target", "/p/x" }, null)).Count);
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_lookup_and_a_missing_file_reads_nothing()
    {
        Event("a", 1, "/p/x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader().ReadHistoryAsync(new[] { "/p/x" }, null, cancellationToken: cts.Token));
        Assert.Empty(await new AlertDetailReader(Path.Combine(Path.GetTempPath(), "dcw-missing-" + Guid.NewGuid().ToString("n"), "audit.db")).ReadHistoryAsync(new[] { "x" }, null));
    }

    // ---- plans ----

    [Fact]
    public async Task The_plans_search_indexes_and_the_history_walks_the_timestamp_index_inside_a_bounded_window()
    {
        var plan = await Reader().ExplainAsync();

        Assert.Contains(plan, l => l.StartsWith("scans-by-run:", StringComparison.Ordinal) && l.Contains("idx_scan_run_id", StringComparison.Ordinal));
        Assert.Contains(plan, l => l.StartsWith("findings:", StringComparison.Ordinal) && l.Contains("idx_scan_findings_scan_id", StringComparison.Ordinal));
        Assert.Contains(plan, l => l.StartsWith("history:", StringComparison.Ordinal) && l.Contains("idx_audit_timestamp", StringComparison.Ordinal));

        // No lookup is allowed to scan a table by its own rows without an index to order them: the history's only scan is the
        // timestamp walk (which stops at the window), the findings' only search is by scan_id.
        Assert.DoesNotContain(plan, l => l.StartsWith("findings:", StringComparison.Ordinal) && l.Contains("SCAN scan_findings", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, l => l.StartsWith("history:", StringComparison.Ordinal) && l.Contains("USE TEMP B-TREE", StringComparison.Ordinal) && l.Contains("audit_events", StringComparison.Ordinal));
    }
}
