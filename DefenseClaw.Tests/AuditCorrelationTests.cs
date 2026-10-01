using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Audit panel's Mac-parity extras in Core: the run / action-term filters of <see cref="AuditQuery"/>, the correlation reader,
/// the export serializer and the detail parser. Synthetic rows in a database built from the real DDL.
/// </summary>
public sealed class AuditCorrelationTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private void Insert(string id, DateTimeOffset at, string action, string? run = null, string? target = null, string? details = null, string severity = "INFO")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, run_id)
            VALUES ($id, $ts, $action, $target, 'audit_logger', $details, $severity, 'guardrail.evaluation', $run)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(at));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", (object?)target ?? string.Empty);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private void InsertScan(string id, DateTimeOffset at, string run, string? rawJson, string target = "skill-x")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_results (id, scanner, target, timestamp, raw_json, run_id)
            VALUES ($id, 'skill-scanner', $target, $ts, $raw, $run)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(at));
        command.Parameters.AddWithValue("$raw", (object?)rawJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", run);
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ AuditQuery: RunId and ActionAnyOf

    [Fact]
    public async Task A_run_filter_returns_only_that_runs_events_and_counts_them()
    {
        Insert("a1", Base.AddMinutes(1), "scan", run: "run-1");
        Insert("a2", Base.AddMinutes(2), "scan", run: "run-2");
        Insert("a3", Base.AddMinutes(3), "block", run: "run-1");
        Insert("a4", Base.AddMinutes(4), "block");
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryAsync(new AuditQuery { RunId = "run-1" });

        Assert.Equal(new[] { "a3", "a1" }, page.Events.Select(e => e.Id));
        Assert.Equal(2, await reader.CountAsync(new AuditQuery { RunId = "run-1" }));
        Assert.Equal(0, await reader.CountAsync(new AuditQuery { RunId = "no-such-run" }));
    }

    [Fact]
    public async Task A_run_with_more_than_a_page_near_the_newest_rows_is_walked_on_the_retention_index_and_others_use_the_run_index()
    {
        // "recent" holds the newest rows; "old" is a run of the same size with 20 newer rows after it; "small" fits one page.
        for (var i = 0; i < 4; i++)
        {
            Insert("old" + i, Base.AddMinutes(i), "scan", run: "old");
        }

        for (var i = 0; i < 20; i++)
        {
            Insert("fill" + i, Base.AddMinutes(10 + i), "scan");
        }

        for (var i = 0; i < 4; i++)
        {
            Insert("rec" + i, Base.AddMinutes(40 + i), "scan", run: "recent");
        }

        Insert("s0", Base.AddMinutes(50), "scan", run: "small");

        // A walk budget of 10 rows, and a page of 2.
        var reader = new AuditReader(_database.Path, runWalkBudget: 10);
        static AuditQuery Run(string run) => new() { RunId = run, Limit = 2 };

        var recent = await reader.ExplainAsync(Run("recent"), AuditQueryShape.Page);
        var old = await reader.ExplainAsync(Run("old"), AuditQueryShape.Page);
        var small = await reader.ExplainAsync(Run("small"), AuditQueryShape.Page);

        Assert.Contains(recent, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(recent, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.Contains(old, line => line.Contains("idx_audit_run_id", StringComparison.Ordinal));
        Assert.Contains(small, line => line.Contains("idx_audit_run_id", StringComparison.Ordinal));

        // Whichever plan, the same newest-first rows, and a keyset page after them.
        var first = await reader.QueryAsync(Run("old"));
        Assert.Equal(new[] { "old3", "old2" }, first.Events.Select(e => e.Id));
        Assert.True(first.HasMore);
        var second = await reader.QueryAsync(Run("old") with { After = first.NextCursor });
        Assert.Equal(new[] { "old1", "old0" }, second.Events.Select(e => e.Id));
        Assert.Equal(new[] { "rec3", "rec2" }, (await reader.QueryAsync(Run("recent"))).Events.Select(e => e.Id));
    }

    [Fact]
    public async Task Action_terms_match_the_action_or_the_details_case_insensitively_and_combine_by_and()
    {
        Insert("t1", Base.AddMinutes(1), "hook_decision", details: "decision=BLOCK reason=x", severity: "HIGH");
        Insert("t2", Base.AddMinutes(2), "quarantine-skill", severity: "INFO");
        Insert("t3", Base.AddMinutes(3), "scan", details: "all clear");
        Insert("t4", Base.AddMinutes(4), "key-rotation", severity: "HIGH");
        var reader = new AuditReader(_database.Path);

        var blocks = await reader.QueryAsync(new AuditQuery { ActionAnyOf = new[] { "block", "reject", "enforce", "quarantine" } });
        Assert.Equal(new[] { "t2", "t1" }, blocks.Events.Select(e => e.Id));

        var highBlocks = await reader.QueryAsync(new AuditQuery
        {
            ActionAnyOf = new[] { "block", "quarantine" },
            MinimumSeverity = AuditSeverity.High,
        });
        Assert.Equal(new[] { "t1" }, highBlocks.Events.Select(e => e.Id));
        Assert.Equal(1, await reader.CountAsync(new AuditQuery { ActionAnyOf = new[] { "block", "quarantine" }, MinimumSeverity = AuditSeverity.High }));

        // A % in a term is a literal, not a wildcard.
        Assert.Empty((await reader.QueryAsync(new AuditQuery { ActionAnyOf = new[] { "%" } })).Events);
    }

    // ------------------------------------------------------------------ the correlation reader

    [Fact]
    public async Task Related_events_of_a_run_are_the_other_events_of_that_run_newest_first_and_at_most_eight()
    {
        for (var i = 0; i < 12; i++)
        {
            Insert("e" + i.ToString("D2", CultureInfo.InvariantCulture), Base.AddMinutes(i), "tool_invocation", run: "run-a");
        }

        Insert("other", Base.AddMinutes(30), "tool_invocation", run: "run-b");
        var correlation = new AuditCorrelationReader(_database.Path);

        var related = await correlation.RelatedAsync("e05", "run-a", "ignored-when-there-is-a-run", Base.AddMinutes(5).Ticks);

        Assert.Equal(RelatedBasis.Run, related.Basis);
        Assert.Equal(AuditCorrelationReader.RelatedLimit, related.Events.Count);
        Assert.Equal("e11", related.Events[0].Id);
        Assert.DoesNotContain(related.Events, e => e.Id == "e05");
        Assert.All(related.Events, e => Assert.Equal("run-a", e.RunId));
    }

    [Fact]
    public async Task Related_events_of_a_target_are_bounded_to_an_hour_either_side()
    {
        Insert("t0", Base, "scan", target: "skill-x");
        Insert("t1", Base.AddMinutes(20), "block", target: "skill-x");
        Insert("t2", Base.AddMinutes(-30), "scan", target: "skill-x");
        Insert("far", Base.AddHours(5), "scan", target: "skill-x");
        Insert("else", Base.AddMinutes(5), "scan", target: "skill-y");
        var correlation = new AuditCorrelationReader(_database.Path);
        var centre = (Base - DateTimeOffset.UnixEpoch).Ticks * 100L;

        var related = await correlation.RelatedAsync("t0", null, "skill-x", centre);

        Assert.Equal(RelatedBasis.Target, related.Basis);
        Assert.Equal(new[] { "t1", "t2" }, related.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task An_event_with_neither_run_nor_target_has_nothing_to_relate_by()
    {
        var related = await new AuditCorrelationReader(_database.Path).RelatedAsync("x", null, "  ", 0);

        Assert.Equal(RelatedBasis.None, related.Basis);
        Assert.Empty(related.Events);
    }

    [Fact]
    public async Task Findings_in_a_run_come_from_the_scan_results_raw_json_newest_scan_first_and_at_most_ten()
    {
        InsertScan("s1", Base.AddMinutes(1), "run-a", """{"findings":[{"id":"f1","title":"Old finding","severity":"LOW"}]}""");
        InsertScan(
            "s2",
            Base.AddMinutes(2),
            "run-a",
            JsonSerializer.Serialize(new
            {
                findings = Enumerable.Range(1, 14).Select(i => new { title = "Finding " + i, severity = "HIGH", location = "SKILL.md:" + i, description = "d", scanner = "own-scanner" }),
            }));
        InsertScan("s3", Base.AddMinutes(3), "run-a", "{ not json");
        InsertScan("s4", Base.AddMinutes(4), "run-a", null);
        InsertScan("s5", Base.AddMinutes(5), "run-b", """{"findings":[{"title":"elsewhere"}]}""");
        var correlation = new AuditCorrelationReader(_database.Path);

        var findings = await correlation.FindingsInRunAsync("run-a");

        Assert.Equal(AuditCorrelationReader.FindingsLimit, findings.Count);
        Assert.Equal("Finding 1", findings[0].Title);
        Assert.Equal("own-scanner", findings[0].Scanner);
        Assert.Equal("HIGH", findings[0].Severity);
        Assert.Equal("SKILL.md:1", findings[0].Location);
        Assert.DoesNotContain(findings, f => f.Title == "elsewhere");
        Assert.Empty(await correlation.FindingsInRunAsync("no-such-run"));
    }

    [Fact]
    public void A_finding_without_a_title_is_numbered_and_one_without_its_own_scanner_takes_the_scans()
    {
        var found = AuditCorrelationReader.ParseFindings("s", "scan-scanner", "t", Base, """{"findings":[{"severity":"MEDIUM"},"junk",{"title":"Named"}]}""").ToList();

        Assert.Equal(new[] { "Finding 1", "Named" }, found.Select(f => f.Title));
        Assert.All(found, f => Assert.Equal("scan-scanner", f.Scanner));
    }

    [Fact]
    public async Task The_correlation_statements_are_index_lookups()
    {
        var correlation = new AuditCorrelationReader(_database.Path);

        var run = await correlation.ExplainAsync(RelatedBasis.Run);
        var target = await correlation.ExplainAsync(RelatedBasis.Target);
        var findings = await correlation.ExplainFindingsAsync();

        // The run is read backwards from its own index, already in rowid (time) order: no sort for any size of run.
        Assert.Contains(run, l => l.Contains("idx_audit_run_id", StringComparison.Ordinal));
        Assert.DoesNotContain(run, l => l.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.Contains(target, l => l.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        Assert.Contains(findings, l => l.Contains("idx_scan_run_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_token_stops_a_correlation_query()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var correlation = new AuditCorrelationReader(_database.Path);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => correlation.RelatedAsync("x", "run", null, 0, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => correlation.FindingsInRunAsync("run", cancelled.Token));
    }

    // ------------------------------------------------------------------ export

    private static AuditEvent Event(string id, string action, string? target = null, string? details = null) => new()
    {
        Id = id,
        Timestamp = Base,
        RawTimestamp = TestAuditDatabase.FormatTimestamp(Base),
        Action = action,
        Target = target,
        Details = details,
        Actor = "audit_logger",
        Severity = "HIGH",
        RunId = "run-1",
        Bucket = "guardrail.evaluation",
        EventName = "evt",
        Connector = "claudecode",
    };

    [Fact]
    public void Json_export_carries_the_macs_fields_plus_bucket_event_name_and_connector()
    {
        var json = AuditExport.ToJson(new[] { Event("a1", "block", "t", "d, \"q\"") });

        using var document = JsonDocument.Parse(json);
        var row = document.RootElement[0];
        Assert.Equal(
            new[] { "id", "timestamp", "action", "target", "actor", "details", "severity", "run_id", "bucket", "event_name", "connector" },
            row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-07-28T12:00:00.0000000Z", row.GetProperty("timestamp").GetString());
        Assert.Equal("d, \"q\"", row.GetProperty("details").GetString());
        Assert.Equal("run-1", row.GetProperty("run_id").GetString());
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("", "")]
    public void A_csv_cell_is_neutralised_against_formulas_and_quoted_when_it_must_be(string value, string expected) =>
        Assert.Equal(expected, AuditExport.CsvCell(value));

    [Fact]
    public void Csv_export_writes_a_header_and_one_line_per_event_with_formula_cells_defused()
    {
        var csv = AuditExport.ToCsv(new[] { Event("a1", "block", "=cmd|' /C calc'!A0", "line1\nline2") });

        var lines = csv.Split("\r\n");
        Assert.Equal("id,timestamp,action,target,actor,details,severity,run_id,bucket,event_name,connector", lines[0]);
        Assert.Contains("'=cmd|' /C calc'!A0", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(",=cmd", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"line1\nline2\"", csv, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the k=v detail parser

    [Fact]
    public void Details_parse_into_labelled_pairs_and_a_redaction_placeholder_reads_as_length_and_digest()
    {
        var pairs = StructuredDetailParser.Pairs("action=block rule_ids=CMD-ENV-DUMP evaluation_id=ev1 token=<redacted len=29 sha=c5482d9d> note=\"two words\"");

        Assert.Equal(
            new[]
            {
                ("Action", "block"),
                ("Rule IDs", "CMD-ENV-DUMP"),
                ("Evaluation ID", "ev1"),
                ("Token", "redacted · 29 bytes · sha:c5482d9d"),
                ("Note", "two words"),
            },
            pairs.Select(p => (p.Label, p.Value)).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain prose with no pairs")]
    [InlineData("<redacted len=3 sha=abc> action=block")]
    [InlineData("=novalue")]
    public void Prose_or_a_leading_placeholder_is_not_a_record(string details) =>
        Assert.Empty(StructuredDetailParser.Pairs(details));

    [Fact]
    public void Parsing_stops_at_the_first_thing_that_is_not_a_pair()
    {
        var pairs = StructuredDetailParser.Pairs("a=1 b=2 then some prose c=3");

        Assert.Equal(new[] { "A", "B" }, pairs.Select(p => p.Label));
    }

    [Fact]
    public void Safe_metadata_picks_known_keys_out_of_prose_and_never_reads_inside_a_placeholder()
    {
        var pairs = StructuredDetailParser.SafeMetadataPairs(
            "blocked the call: scanner=skill-scan secret=<redacted len=9 sha=ff scanner=evil> max_severity=HIGH scanner=second other=1");

        Assert.Equal(
            new[] { ("Scanner", "skill-scan"), ("Max Severity", "HIGH") },
            pairs.Select(p => (p.Label, p.Value)).ToArray());
    }

    [Theory]
    [InlineData("max_severity", "Max Severity")]
    [InlineData("scan_id", "Scan ID")]
    [InlineData("RULE_IDS", "Rule IDs")]
    [InlineData("finding_count", "Finding Count")]
    [InlineData("x", "X")]
    public void A_key_becomes_a_readable_label(string key, string label) => Assert.Equal(label, StructuredDetailParser.Label(key));

    [Fact]
    public void The_parser_never_throws_on_malformed_input()
    {
        foreach (var text in new[] { "a=\"unterminated", "a=<redacted", "a=<<>", "=", "a=", "<", ">", "a='x\\", "k=<redacted len=>"})
        {
            _ = StructuredDetailParser.Pairs(text);
            _ = StructuredDetailParser.SafeMetadataPairs(text);
        }
    }
}
