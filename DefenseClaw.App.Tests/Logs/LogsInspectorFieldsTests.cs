using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-263: the Logs inspector's labelled fields for an event of Verdicts or Events - the 0.8.10 TUI's detail (<c>detail_pairs</c>): Stage, Direction, Model,
/// Provider, the ids, Categories, Latency, the judge's kind, reason, severity, input size, parse error and one numbered Finding per finding, under its labels and in
/// its order. A synthetic judge event with findings goes through the real reader and the panel; no window.
/// </summary>
public sealed class LogsInspectorFieldsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The TUI's labels for a judge event with findings, in its order: the four every row has, then the rest, then what the app adds.</summary>
    internal static readonly string[] JudgeLabels =
    {
        "Timestamp", "Event type", "Severity", "Action",
        "Stage", "Direction", "Model", "Provider", "Request ID", "Run ID", "Trace ID", "Span ID", "Session ID", "Categories", "Latency (ms)",
        "Judge kind", "Reason", "Judge severity", "Judge input bytes", "Judge parse error", "Finding 1", "Finding 2",
        "Bucket", "Event name", "Connector", "Actor", "ID",
    };

    /// <summary>The synthetic judge payload: every key the TUI's detail reads, two findings, and a judge severity that differs from the row's.</summary>
    internal const string JudgePayload = """
        {"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt-4o-mini",
         "defenseclaw.guardrail.rule_ids":["JUDGE-INJ-INSTRUCT","JUDGE-PII-USER"],"defenseclaw.guardrail.latency_ms":42,
         "defenseclaw.judge.kind":"injection","defenseclaw.judge.action":"block","defenseclaw.judge.input_bytes":512,
         "defenseclaw.judge.parse_error":"unexpected end of JSON input","defenseclaw.judge.severity":"HIGH",
         "defenseclaw.guardrail.reason":"prompt injection suspected",
         "defenseclaw.judge.findings":[
           {"category":"Instruction Manipulation","severity":"HIGH","rule":"JUDGE-INJ-INSTRUCT","source":"judge","confidence":0.9},
           {"category":"PII","severity":"MEDIUM","rule":"JUDGE-PII-USER","source":"judge"}]}
        """;

    internal const string JudgeRecord = """{"outcome":"blocked","correlation":{"trace_id":"0af7651916cd43dd8448eb211c80319c","span_id":"b7ad6b7169203331"}}""";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsInspectorFieldsTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    internal sealed record Ids(string Run, string Trace, string Request, string Session);

    internal delegate void AddEvent(string id, int minute, string bucket, string eventName, string severity, string rowAction, string? details, string? payload, string? projected, Ids? ids);

    /// <summary>Makes an audit.db from the real DDL at <paramref name="path"/> and fills it with the events <paramref name="seed"/> adds.</summary>
    internal static void Fill(string path, Action<AddEvent> seed)
    {
        AuditTestDatabase.Create(path, rows: 0);
        Append(path, seed);
    }

    /// <summary>Adds the events <paramref name="seed"/> adds to the audit.db at <paramref name="path"/>, which has the real DDL already.</summary>
    internal static void Append(string path, Action<AddEvent> seed)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            seed((id, minute, bucket, eventName, severity, rowAction, details, payload, projected, ids) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json,
                                              projected_record_json, run_id, trace_id, request_id, session_id)
                    VALUES ($id, $timestamp, $action, '', 'gateway', $details, $severity, $bucket, $eventName, 'claudecode', 'sidecar', 'logs', $payload,
                            $projected, $runId, $traceId, $requestId, $sessionId)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$timestamp", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                command.Parameters.AddWithValue("$action", rowAction);
                command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
                command.Parameters.AddWithValue("$severity", severity);
                command.Parameters.AddWithValue("$bucket", bucket);
                command.Parameters.AddWithValue("$eventName", eventName);
                command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                command.Parameters.AddWithValue("$projected", (object?)projected ?? DBNull.Value);
                command.Parameters.AddWithValue("$runId", (object?)ids?.Run ?? DBNull.Value);
                command.Parameters.AddWithValue("$traceId", (object?)ids?.Trace ?? DBNull.Value);
                command.Parameters.AddWithValue("$requestId", (object?)ids?.Request ?? DBNull.Value);
                command.Parameters.AddWithValue("$sessionId", (object?)ids?.Session ?? DBNull.Value);
                _ = command.ExecuteNonQuery();
            });
        }

        SqlitePools.Release(path);
    }

    /// <summary>The judge event with findings, with all four ids and a stored record that names the span.</summary>
    internal static void AddJudge(AddEvent add) =>
        add("judge-1", 1, "guardrail.evaluation", "guardrail.judge.completed", "MEDIUM", "act", null, JudgePayload, JudgeRecord, new Ids("run-7", "0af7651916cd43dd8448eb211c80319c", "req-7", "sess-7"));

    private string Database(Action<AddEvent> seed)
    {
        var path = _temp.File("inspector-audit.db");
        Fill(path, seed);
        return path;
    }

    private async Task<LogsPanelViewModel> PanelAsync(string path, string source)
    {
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(path) };
        panel.SetActive(true);
        panel.ActiveSource = source;
        panel.SelectedPreset = "all";
        await panel.LoadStructuredAsync();
        return panel;
    }

    [Theory]
    [InlineData("Verdicts")]
    [InlineData("Events")]
    public async Task A_judge_event_with_findings_shows_every_label_of_the_TUIs_detail_in_its_order(string source)
    {
        var panel = await PanelAsync(Database(AddJudge), source);

        var entry = Assert.Single(panel.DisplayedLines);
        Assert.Equal(JudgeLabels, entry.Fields.Select(f => f.Name).ToArray());

        var values = entry.Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("judge", values["Event type"]);
        Assert.Equal("MEDIUM", values["Severity"]);
        Assert.Equal("block", values["Action"]);
        Assert.Equal("completed", values["Stage"]);
        Assert.Equal("chat", values["Direction"]);
        Assert.Equal("gpt-4o-mini", values["Model"]);
        Assert.Equal("sidecar", values["Provider"]);
        Assert.Equal("req-7", values["Request ID"]);
        Assert.Equal("run-7", values["Run ID"]);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", values["Trace ID"]);
        Assert.Equal("b7ad6b7169203331", values["Span ID"]);
        Assert.Equal("sess-7", values["Session ID"]);
        Assert.Equal("JUDGE-INJ-INSTRUCT, JUDGE-PII-USER", values["Categories"]);
        Assert.Equal("42", values["Latency (ms)"]);
        Assert.Equal("injection", values["Judge kind"]);
        Assert.Equal("prompt injection suspected", values["Reason"]);
        Assert.Equal("HIGH", values["Judge severity"]);
        Assert.Equal("512", values["Judge input bytes"]);
        Assert.Equal("unexpected end of JSON input", values["Judge parse error"]);
        Assert.Equal("category=Instruction Manipulation severity=HIGH rule=JUDGE-INJ-INSTRUCT source=judge conf=0.90", values["Finding 1"]);
        Assert.Equal("category=PII severity=MEDIUM rule=JUDGE-PII-USER source=judge", values["Finding 2"]);
        Assert.Equal("guardrail.evaluation", values["Bucket"]);
        Assert.Equal("guardrail.judge.completed", values["Event name"]);
        Assert.Equal("claudecode", values["Connector"]);
        Assert.Equal("judge-1", values["ID"]);
    }

    [Fact]
    public async Task The_inspector_of_an_event_is_headed_as_one_and_a_files_line_as_a_line()
    {
        var panel = await PanelAsync(Database(AddJudge), "Events");

        var entry = Assert.Single(panel.DisplayedLines);

        Assert.True(entry.IsStructured);
        Assert.Equal("Event details", entry.InspectorTitle);
        Assert.Equal("Line details", new LogEntry(LogLine.Parse("[api] hello", 1)).InspectorTitle);
    }

    [Fact]
    public async Task A_plain_verdict_shows_only_the_rows_it_has()
    {
        var panel = await PanelAsync(
            Database(add => add("v-1", 1, "enforcement.action", "enforcement.applied", "HIGH", "install-blocked", "type=skill", null, null, null)),
            "Verdicts");

        var entry = Assert.Single(panel.DisplayedLines);

        Assert.Equal(
            new[] { "Timestamp", "Event type", "Severity", "Action", "Stage", "Provider", "Reason", "Bucket", "Event name", "Connector", "Actor", "ID" },
            entry.Fields.Select(f => f.Name).ToArray());
        Assert.Equal("applied", entry.Fields.Single(f => f.Name == "Stage").Value);
        Assert.Equal("type=skill", entry.Fields.Single(f => f.Name == "Reason").Value);
        Assert.DoesNotContain(entry.Fields, f => f.Name is "Model" or "Direction" or "Categories" or "Latency (ms)" or "Span ID");
    }

    [Fact]
    public async Task The_severity_row_is_the_stored_word_even_where_it_is_not_on_the_ladder()
    {
        var panel = await PanelAsync(
            Database(add => add("e-1", 1, "platform.health", "sink.checked", "ERROR", "act", "sink unreachable", null, null, null)),
            "Events");

        var entry = Assert.Single(panel.DisplayedLines);

        Assert.Equal("ERROR", entry.Fields.Single(f => f.Name == "Severity").Value);
    }

    [Fact]
    public async Task A_payload_too_large_to_read_says_which_fields_are_missing_and_why_while_the_columns_stay()
    {
        var big = "{\"gen_ai.request.model\":\"gpt\",\"pad\":\"" + new string('a', EventStreamReader.PayloadByteLimit + 100) + "\"}";
        var panel = await PanelAsync(
            Database(add => add("big", 1, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "act", "kept", big, null, new Ids("run-9", "t", "r", "s"))),
            "Events");

        var entry = Assert.Single(panel.DisplayedLines);
        var fields = entry.Fields.ToDictionary(f => f.Name, f => f.Value);

        Assert.DoesNotContain("Model", fields.Keys);
        Assert.Equal("run-9", fields["Run ID"]);
        Assert.Equal("kept", fields["Reason"]);
        Assert.Contains("payload_json is", fields["Unavailable"], StringComparison.Ordinal);
        Assert.Contains("over the 64 KB limit", fields["Unavailable"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connector_hook_call_gets_its_decision_laid_out_and_no_second_connector_row()
    {
        var panel = await PanelAsync(
            Database(add => add(
                "hook-1",
                1,
                "guardrail.evaluation",
                "guardrail.evaluated",
                "INFO",
                "connector-hook",
                "connector=claudecode action=block severity=HIGH mode=action would_block=true elapsed=41ms",
                null,
                null,
                null)),
            "Events");

        var entry = Assert.Single(panel.DisplayedLines);
        var names = entry.Fields.Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "Connector", "Decision", "Severity (decision)", "Enforcement mode", "Would block", "Elapsed" }, names.SkipWhile(n => n != "Connector").Take(6).ToArray());
        Assert.Single(names, "Connector");
        Assert.Equal("yes", entry.Fields.Single(f => f.Name == "Would block").Value);
        Assert.Equal("41ms", entry.Fields.Single(f => f.Name == "Elapsed").Value);
    }

    [Fact]
    public async Task Credentials_never_reach_the_inspector_through_a_labelled_field()
    {
        var panel = await PanelAsync(
            Database(add => add(
                "secret",
                1,
                "guardrail.evaluation",
                "guardrail.judge.completed",
                "HIGH",
                "act",
                null,
                """{"gen_ai.request.model":"m token=LEAK-1","defenseclaw.judge.kind":"k password=LEAK-2","defenseclaw.judge.findings":[{"category":"c secret=LEAK-3","severity":"HIGH"}]}""",
                null,
                new Ids("run api_key=LEAK-4", "t", "req token=LEAK-5", "s"))),
            "Events");

        var entry = Assert.Single(panel.DisplayedLines);

        Assert.DoesNotContain(entry.Fields, f => f.Value.Contains("LEAK-", StringComparison.Ordinal));
        Assert.DoesNotContain("LEAK-", entry.Raw, StringComparison.Ordinal);
        Assert.Contains("[redacted]", entry.Fields.Single(f => f.Name == "Model").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_lines_fields_are_what_they_were()
    {
        var plain = new LogEntry(LogLine.Parse("[api] hello", 7));

        Assert.Equal(new[] { "component", "line" }, plain.Fields.Select(f => f.Name).ToArray());
    }
}
