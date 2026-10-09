using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-263: the labelled fields of a canonical event as the 0.8.10 TUI's Logs detail lists them (<c>gateway_log_views.detail_pairs</c> over the row
/// <c>_v8_gateway_log_row</c> builds). Two halves: the list itself, rows under the TUI's labels in the TUI's order, built from a hand-made
/// <see cref="EventDetail"/>; and the reader, which fills it from the payload's keys and the row's columns. A database built from the real DDL with synthetic
/// rows only.
/// </summary>
public sealed class EventDetailTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private EventStreamReader Reader() => new(_database.Path);

    private void Add(
        string id,
        int minute,
        string bucket,
        string eventName,
        string severity = "INFO",
        string rowAction = "act",
        string? details = null,
        string? payload = null,
        string? projected = null,
        string? runId = null,
        string? traceId = null,
        string? requestId = null,
        string? sessionId = null,
        string source = "sidecar")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json,
                                      projected_record_json, run_id, trace_id, request_id, session_id)
            VALUES ($id, $timestamp, $action, '', 'gateway', $details, $severity, $bucket, $eventName, 'claudecode', $source, 'logs', $payload,
                    $projected, $runId, $traceId, $requestId, $sessionId)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$action", rowAction);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$eventName", eventName);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        command.Parameters.AddWithValue("$projected", (object?)projected ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$traceId", (object?)traceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private async Task<StreamEvent> OnlyRow(EventStreamKind kind = EventStreamKind.Events) =>
        Assert.Single((await Reader().ReadAsync(kind)).Rows);

    private static string[] Labels(IEnumerable<DetailPair> rows) => rows.Select(r => r.Label).ToArray();

    // ---- The list: the TUI's labels, in its order ----

    private static EventDetail Everything() => new()
    {
        Stage = "completed",
        Direction = "chat",
        Model = "gpt-4o-mini",
        Provider = "sidecar",
        RequestId = "req-1",
        RunId = "run-1",
        TraceId = "0af7651916cd43dd8448eb211c80319c",
        SpanId = "b7ad6b7169203331",
        SessionId = "sess-1",
        Categories = new[] { "JUDGE-INJ-INSTRUCT", "JUDGE-PII-USER" },
        LatencyMs = 42,
        JudgeKind = "injection",
        Reason = "matched a rule",
        JudgeSeverity = "HIGH",
        JudgeInputBytes = 512,
        JudgeParseError = "unexpected end of JSON input",
        Findings = new[]
        {
            new JudgeFinding("Instruction Manipulation", "HIGH", "JUDGE-INJ-INSTRUCT", "judge", 0.9),
            new JudgeFinding("PII", "MEDIUM", string.Empty, string.Empty, 0),
        },
    };

    [Fact]
    public void A_detail_with_everything_lists_every_label_of_the_TUIs_detail_in_its_order()
    {
        // detail_pairs: Stage, Direction, Model; Provider, Request ID, Run ID, Trace ID, Span ID, Session ID; Categories; Latency (ms); Judge kind; Reason; Judge severity;
        // Judge input bytes; Judge parse error; Finding 1, 2, ... (the first four rows - Timestamp, Event type, Severity, Action - belong to the event itself).
        var rows = Everything().Rows("MEDIUM");

        Assert.Equal(
            new[]
            {
                "Stage", "Direction", "Model", "Provider", "Request ID", "Run ID", "Trace ID", "Span ID", "Session ID", "Categories", "Latency (ms)", "Judge kind",
                "Reason", "Judge severity", "Judge input bytes", "Judge parse error", "Finding 1", "Finding 2",
            },
            Labels(rows));

        var values = rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("completed", values["Stage"]);
        Assert.Equal("gpt-4o-mini", values["Model"]);
        Assert.Equal("JUDGE-INJ-INSTRUCT, JUDGE-PII-USER", values["Categories"]);
        Assert.Equal("42", values["Latency (ms)"]);
        Assert.Equal("HIGH", values["Judge severity"]);
        Assert.Equal("512", values["Judge input bytes"]);
    }

    [Fact]
    public void A_row_with_nothing_to_say_has_no_rows()
    {
        Assert.Empty(EventDetail.Empty.Rows("INFO"));
        Assert.Empty(new EventDetail { LatencyMs = 0, JudgeInputBytes = 0, Categories = Array.Empty<string>() }.Rows(null));
    }

    [Theory]
    [InlineData("HIGH", "HIGH", false)]
    [InlineData("high", "HIGH", false)]
    [InlineData("MEDIUM", "HIGH", true)]
    [InlineData("", "HIGH", false)]
    [InlineData("HIGH", "", true)]
    public void The_judge_severity_is_a_row_only_when_it_is_not_the_rows_own(string judge, string row, bool shown)
    {
        var labels = Labels(new EventDetail { JudgeSeverity = judge }.Rows(row));

        Assert.Equal(shown, labels.Contains("Judge severity"));
    }

    [Theory]
    [InlineData("Instruction Manipulation", "HIGH", "JUDGE-INJ-INSTRUCT", "judge", 0.9, "category=Instruction Manipulation severity=HIGH rule=JUDGE-INJ-INSTRUCT source=judge conf=0.90")]
    [InlineData("PII", "MEDIUM", "", "", 0, "category=PII severity=MEDIUM")]
    [InlineData("PII", "MEDIUM", "JUDGE-PII-SSN", "", 0, "category=PII severity=MEDIUM rule=JUDGE-PII-SSN")]
    [InlineData("PII", "MEDIUM", "", "regex", 0, "category=PII severity=MEDIUM source=regex")]
    [InlineData("PII", "MEDIUM", "", "", 0.5, "category=PII severity=MEDIUM conf=0.50")]
    [InlineData("PII", "MEDIUM", "", "", 0.123456, "category=PII severity=MEDIUM conf=0.12")]
    [InlineData("", "", "", "", 0, "category= severity=")]
    public void A_finding_reads_the_way_the_TUI_writes_it(string category, string severity, string rule, string source, double confidence, string expected) =>
        Assert.Equal(expected, new JudgeFinding(category, severity, rule, source, confidence).Describe());

    [Fact]
    public void The_findings_are_numbered_from_one_after_the_judges_other_rows()
    {
        var detail = new EventDetail
        {
            JudgeKind = "pii",
            Findings = new[] { new JudgeFinding("a", "LOW", "", "", 0), new JudgeFinding("b", "LOW", "", "", 0), new JudgeFinding("c", "LOW", "", "", 0) },
        };

        Assert.Equal(new[] { "Judge kind", "Finding 1", "Finding 2", "Finding 3" }, Labels(detail.Rows("LOW")));
    }

    // ---- The reader: the payload's keys and the row's columns ----

    [Fact]
    public async Task A_judge_event_is_read_into_every_field_of_the_TUIs_detail()
    {
        Add(
            "judge",
            1,
            "guardrail.evaluation",
            "guardrail.judge.completed",
            "MEDIUM",
            payload: """
                {"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt-4o-mini","gen_ai.response.model":"gpt-4o-mini-2026",
                 "defenseclaw.guardrail.rule_ids":["JUDGE-INJ-INSTRUCT","JUDGE-PII-USER"],"defenseclaw.guardrail.latency_ms":42,
                 "defenseclaw.judge.kind":"injection","defenseclaw.judge.action":"block","defenseclaw.judge.input_bytes":512,
                 "defenseclaw.judge.parse_error":"unexpected end of JSON input","defenseclaw.judge.severity":"HIGH",
                 "defenseclaw.guardrail.reason":"prompt injection suspected",
                 "defenseclaw.judge.findings":[
                   {"category":"Instruction Manipulation","severity":"HIGH","rule":"JUDGE-INJ-INSTRUCT","source":"judge","confidence":0.9},
                   {"category":"PII","severity":"MEDIUM"}]}
                """,
            projected: """{"outcome":"blocked","correlation":{"trace_id":"0af7651916cd43dd8448eb211c80319c","span_id":"b7ad6b7169203331"}}""",
            runId: "run-1",
            traceId: "0af7651916cd43dd8448eb211c80319c",
            requestId: "req-1",
            sessionId: "sess-1");

        var row = await OnlyRow();

        Assert.Equal("judge", row.EventType);
        var detail = row.Detail;
        Assert.Equal("completed", detail.Stage);
        Assert.Equal("chat", detail.Direction);
        Assert.Equal("gpt-4o-mini", detail.Model);
        Assert.Equal("sidecar", detail.Provider);
        Assert.Equal("req-1", detail.RequestId);
        Assert.Equal("run-1", detail.RunId);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", detail.TraceId);
        Assert.Equal("b7ad6b7169203331", detail.SpanId);
        Assert.Equal("sess-1", detail.SessionId);
        Assert.Equal(new[] { "JUDGE-INJ-INSTRUCT", "JUDGE-PII-USER" }, detail.Categories);
        Assert.Equal(42, detail.LatencyMs);
        Assert.Equal("injection", detail.JudgeKind);
        Assert.Equal("prompt injection suspected", detail.Reason);
        Assert.Equal("HIGH", detail.JudgeSeverity);
        Assert.Equal(512, detail.JudgeInputBytes);
        Assert.Equal("unexpected end of JSON input", detail.JudgeParseError);
        Assert.Equal(2, detail.Findings.Count);
        Assert.Equal(new JudgeFinding("Instruction Manipulation", "HIGH", "JUDGE-INJ-INSTRUCT", "judge", 0.9), detail.Findings[0]);
        Assert.Equal(new JudgeFinding("PII", "MEDIUM", string.Empty, string.Empty, 0), detail.Findings[1]);

        Assert.Equal(
            new[]
            {
                "Stage", "Direction", "Model", "Provider", "Request ID", "Run ID", "Trace ID", "Span ID", "Session ID", "Categories", "Latency (ms)", "Judge kind",
                "Reason", "Judge severity", "Judge input bytes", "Judge parse error", "Finding 1", "Finding 2",
            },
            Labels(detail.Rows(row.SeverityText)));
    }

    [Theory]
    [InlineData("""{"gen_ai.operation.name":"chat","defenseclaw.hook.event":"preToolUse"}""", "chat")]
    [InlineData("""{"defenseclaw.hook.event":"preToolUse"}""", "preToolUse")]
    [InlineData("""{"gen_ai.operation.name":"","defenseclaw.hook.event":"preToolUse"}""", "preToolUse")]
    [InlineData("""{"other":"x"}""", "")]
    public async Task The_direction_is_the_operation_else_the_hook_event(string payload, string expected)
    {
        Add("row", 1, "guardrail.evaluation", "guardrail.evaluated", payload: payload);

        Assert.Equal(expected, (await OnlyRow()).Detail.Direction);
    }

    [Theory]
    [InlineData("""{"gen_ai.request.model":"a","gen_ai.response.model":"b"}""", "a")]
    [InlineData("""{"gen_ai.response.model":"b"}""", "b")]
    public async Task The_model_is_the_requested_one_else_the_one_that_answered(string payload, string expected)
    {
        Add("row", 1, "guardrail.evaluation", "guardrail.evaluated", payload: payload);

        Assert.Equal(expected, (await OnlyRow()).Detail.Model);
    }

    [Theory]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":42}""", 42)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":42.9}""", 42)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":"17"}""", 17)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":0,"defenseclaw.judge.latency_ms":12}""", 12)]
    [InlineData("""{"defenseclaw.judge.latency_ms":12}""", 12)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":"fast"}""", 0)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":99999999999999}""", 0)]
    [InlineData("""{"defenseclaw.guardrail.latency_ms":null}""", 0)]
    public async Task The_latency_is_the_guardrails_else_the_judges_and_a_value_that_is_not_a_number_is_none(string payload, int expected)
    {
        Add("row", 1, "guardrail.evaluation", "guardrail.evaluated", payload: payload);

        Assert.Equal(expected, (await OnlyRow()).Detail.LatencyMs);
    }

    [Fact]
    public async Task The_stage_is_the_last_part_of_the_event_name()
    {
        Add("dotted", 1, "guardrail.evaluation", "guardrail.judge.completed");
        Add("bare", 2, "guardrail.evaluation", "panic");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.Equal("completed", rows["dotted"].Detail.Stage);
        Assert.Equal("panic", rows["bare"].Detail.Stage);
    }

    [Fact]
    public async Task The_ids_are_the_rows_own_columns_and_a_row_without_them_has_none()
    {
        Add("with", 1, "guardrail.evaluation", "guardrail.evaluated", runId: "run-1", traceId: "trace-1", requestId: "req-1", sessionId: "sess-1");
        Add("without", 2, "guardrail.evaluation", "guardrail.evaluated");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.Equal(("run-1", "trace-1", "req-1", "sess-1"), (rows["with"].Detail.RunId, rows["with"].Detail.TraceId, rows["with"].Detail.RequestId, rows["with"].Detail.SessionId));
        Assert.Equal(string.Empty, rows["without"].Detail.RunId);
        Assert.Equal(string.Empty, rows["without"].Detail.TraceId);
        Assert.Equal(string.Empty, rows["without"].Detail.RequestId);
        Assert.Equal(string.Empty, rows["without"].Detail.SessionId);
        Assert.Equal(new[] { "Stage", "Provider" }, Labels(rows["without"].Detail.Rows(rows["without"].SeverityText)));
    }

    [Theory]
    [InlineData("""{"correlation":{"span_id":"b7ad6b7169203331"}}""", "b7ad6b7169203331")]
    [InlineData("""{"outcome":"allowed","correlation":{"trace_id":"t","span_id":"abc"},"body":{}}""", "abc")]
    [InlineData("""{"correlation":{"trace_id":"t"}}""", "")]
    [InlineData("""{"correlation":"nope"}""", "")]
    [InlineData("""{"correlation":{"span_id":null}}""", "")]
    [InlineData("not json at all", "")]
    [InlineData("""{"correlation":{"span_id":"cut""", "")]
    public async Task The_span_id_is_the_one_the_stored_record_names_and_a_record_that_does_not_is_no_failure(string projected, string expected)
    {
        Add("row", 1, "guardrail.evaluation", "guardrail.evaluated", projected: projected);

        Assert.Equal(expected, (await OnlyRow()).Detail.SpanId);
    }

    [Fact]
    public async Task A_record_over_the_limit_is_not_parsed_and_its_row_has_no_span()
    {
        var big = """{"correlation":{"span_id":"b7ad6b7169203331"},"pad":""" + "\"" + new string('a', EventStreamReader.PayloadByteLimit) + "\"}";
        Add("big", 1, "guardrail.evaluation", "guardrail.evaluated", projected: big);
        Add("small", 2, "guardrail.evaluation", "guardrail.evaluated", projected: """{"correlation":{"span_id":"1111111111111111"}}""");

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.Equal(string.Empty, rows["big"].Detail.SpanId);
        Assert.Equal("1111111111111111", rows["small"].Detail.SpanId);
    }

    [Fact]
    public async Task The_span_of_a_row_already_read_is_kept_and_only_the_rows_that_arrived_are_looked_up()
    {
        Add("first", 1, "guardrail.evaluation", "guardrail.evaluated", projected: """{"correlation":{"span_id":"aaaaaaaaaaaaaaaa"}}""");
        var reader = Reader();
        var before = (await reader.ReadAsync(EventStreamKind.Events)).Rows;
        Assert.Equal(1, reader.RowsDecoded);

        Add("second", 2, "guardrail.evaluation", "guardrail.evaluated", projected: """{"correlation":{"span_id":"bbbbbbbbbbbbbbbb"}}""");
        var after = (await reader.ReadAsync(EventStreamKind.Events)).Rows.ToDictionary(r => r.Id);

        Assert.Equal(2, reader.RowsDecoded);
        Assert.Same(before[0], after["first"]);
        Assert.Equal("aaaaaaaaaaaaaaaa", after["first"].Detail.SpanId);
        Assert.Equal("bbbbbbbbbbbbbbbb", after["second"].Detail.SpanId);
    }

    [Fact]
    public async Task More_rows_than_one_lookup_binds_each_get_their_span()
    {
        const int Count = 850;
        using (var connection = _database.OpenWritable())
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, event_name, connector, source, signal, projected_record_json)
                VALUES ($id, $timestamp, 'act', '', 'gateway', 'INFO', 'guardrail.evaluation', 'guardrail.evaluated', 'claudecode', 'sidecar', 'logs', $projected)
                """;
            var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var timestamp = command.Parameters.Add("$timestamp", Microsoft.Data.Sqlite.SqliteType.Text);
            var projected = command.Parameters.Add("$projected", Microsoft.Data.Sqlite.SqliteType.Text);
            for (var i = 0; i < Count; i++)
            {
                id.Value = "row-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                timestamp.Value = TestAuditDatabase.FormatTimestamp(Base.AddSeconds(i));
                projected.Value = "{\"correlation\":{\"span_id\":\"span-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "\"}}";
                _ = command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var rows = (await Reader().ReadAsync(EventStreamKind.Events)).Rows;

        Assert.Equal(Count, rows.Count);
        Assert.All(rows, r => Assert.Equal("span-" + r.Id["row-".Length..], r.Detail.SpanId));
    }

    // ---- Bounds and masking ----

    [Fact]
    public async Task A_payload_over_the_limit_leaves_its_fields_empty_and_the_row_says_why_while_the_columns_still_count()
    {
        var big = "{\"gen_ai.request.model\":\"gpt\",\"pad\":\"" + new string('a', EventStreamReader.PayloadByteLimit) + "\"}";
        Add("big", 1, "guardrail.evaluation", "guardrail.evaluated", payload: big, runId: "run-9", details: "kept");

        var row = await OnlyRow();

        Assert.True(row.PayloadOmitted);
        var oversized = Assert.Single(row.Oversized);
        Assert.Equal("payload_json", oversized.Column);
        Assert.Equal(string.Empty, row.Detail.Model);
        Assert.Equal(string.Empty, row.Detail.Direction);
        Assert.Equal("run-9", row.Detail.RunId);
        Assert.Equal("kept", row.Detail.Reason);
    }

    [Fact]
    public async Task Credentials_are_masked_in_every_field_the_detail_shows()
    {
        Add(
            "secret",
            1,
            "guardrail.evaluation",
            "guardrail.judge.completed",
            payload: """
                {"gen_ai.request.model":"model token=LEAK-MODEL-1","defenseclaw.guardrail.rule_ids":["rule password=LEAK-RULE-2"],
                 "defenseclaw.judge.kind":"kind api_key=LEAK-KIND-3","defenseclaw.judge.parse_error":"bad Authorization: Bearer LEAK-PARSE-4",
                 "defenseclaw.guardrail.reason":"reason secret=LEAK-REASON-5",
                 "defenseclaw.judge.findings":[{"category":"cat token=LEAK-CAT-6","severity":"HIGH","rule":"rule secret=LEAK-FRULE-7","source":"src password=LEAK-SRC-8"}]}
                """,
            runId: "run token=LEAK-RUN-9",
            requestId: "req secret=LEAK-REQ-10",
            source: "src password=LEAK-PROVIDER-11");

        var rows = (await OnlyRow()).Detail.Rows("INFO");

        // Every row the detail has, each one a field that carried a credential on the way in.
        Assert.Equal(
            new[] { "Stage", "Model", "Provider", "Request ID", "Run ID", "Categories", "Judge kind", "Reason", "Judge parse error", "Finding 1" },
            Labels(rows));
        Assert.All(rows.Where(r => r.Label != "Stage"), row =>
        {
            Assert.DoesNotContain("LEAK-", row.Value, StringComparison.Ordinal);
            Assert.Contains("[redacted]", row.Value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_long_list_in_a_payload_is_cut_to_the_limit()
    {
        var rules = string.Join(',', Enumerable.Range(0, 100).Select(i => $"\"rule-{i}\""));
        var findings = string.Join(',', Enumerable.Range(0, 100).Select(i => $"{{\"category\":\"c{i}\",\"severity\":\"LOW\"}}"));
        Add("long", 1, "guardrail.evaluation", "guardrail.judge.completed", payload: $"{{\"defenseclaw.guardrail.rule_ids\":[{rules}],\"defenseclaw.judge.findings\":[{findings}]}}");

        var detail = (await OnlyRow()).Detail;

        Assert.Equal(EventDetail.ListLimit, detail.Categories.Count);
        Assert.Equal(EventDetail.ListLimit, detail.Findings.Count);
    }

    [Fact]
    public async Task Findings_that_are_not_objects_and_values_of_the_wrong_kind_are_skipped_not_fatal()
    {
        Add(
            "odd",
            1,
            "guardrail.evaluation",
            "guardrail.judge.completed",
            payload: """{"defenseclaw.judge.findings":["text",7,{"category":"ok","severity":"LOW","confidence":"high"},{"category":"also ok","confidence":0.5}],"defenseclaw.guardrail.rule_ids":"not a list"}""");

        var detail = (await OnlyRow()).Detail;

        Assert.Equal(new[] { "category=ok severity=LOW", "category=also ok severity= conf=0.50" }, detail.Findings.Select(f => f.Describe()));
        Assert.Empty(detail.Categories);
    }

    // ---- Hook calls and errors ----

    [Fact]
    public async Task A_connector_hook_call_gets_the_decision_laid_out_row_by_row()
    {
        Add(
            "hook",
            1,
            "guardrail.evaluation",
            "guardrail.evaluated",
            rowAction: "connector-hook",
            details: "connector=claudecode action=block raw_action=block severity=HIGH mode=action would_block=true elapsed=41ms raw_payload=<redacted len=8 sha=84ed0c96>");

        var hook = (await OnlyRow()).Detail.Hook;

        Assert.Equal(
            new[] { "Connector", "Decision", "Decision (raw)", "Severity (decision)", "Enforcement mode", "Would block", "Elapsed", "Raw payload" },
            hook.Select(r => r.Label).ToArray());
        Assert.Equal("yes", hook.Single(r => r.Label == "Would block").Value);
        Assert.Equal("claudecode", hook.Single(r => r.Label == "Connector").Value);
    }

    [Fact]
    public async Task A_passing_observed_hook_call_leaves_out_the_two_rows_that_are_noise_on_every_one()
    {
        Add(
            "hook",
            1,
            "guardrail.evaluation",
            "guardrail.evaluated",
            rowAction: "connector-hook",
            details: "connector=codex action=allow severity=NONE mode=observe would_block=false elapsed=3ms");

        var hook = (await OnlyRow()).Detail.Hook;

        Assert.DoesNotContain(hook, r => r.Label is "Severity (decision)" or "Would block");
        Assert.Equal(new[] { "Connector", "Decision", "Enforcement mode", "Elapsed" }, hook.Select(r => r.Label).ToArray());
    }

    [Fact]
    public async Task A_row_that_is_not_a_hook_call_has_no_hook_rows_whatever_its_details_say()
    {
        Add("plain", 1, "guardrail.evaluation", "guardrail.evaluated", details: "connector=claudecode action=allow");

        Assert.Empty((await OnlyRow()).Detail.Hook);
    }

    [Theory]
    [InlineData("""{"defenseclaw.error.code":"sink_unreachable","defenseclaw.telemetry.rejection_reason":"other"}""", "sink_unreachable")]
    [InlineData("""{"defenseclaw.telemetry.rejection_reason":"over_quota"}""", "over_quota")]
    [InlineData("""{"x":1}""", "")]
    public async Task An_errors_code_is_the_error_code_else_the_rejection_reason(string payload, string expected)
    {
        Add("error", 1, "platform.health", "sink.failed", "HIGH", payload: payload);

        Assert.Equal(expected, (await OnlyRow()).Detail.ErrorCode);
    }

    // ---- Both streams ----

    [Fact]
    public async Task The_verdicts_stream_projects_the_same_fields_as_events()
    {
        Add("judge", 1, "guardrail.evaluation", "guardrail.judge.completed", payload: """{"defenseclaw.judge.kind":"pii"}""", runId: "run-1", projected: """{"correlation":{"span_id":"cccccccccccccccc"}}""");

        var verdict = (await OnlyRow(EventStreamKind.Verdicts)).Detail;
        var events = (await Reader().ReadAsync(EventStreamKind.Events)).Rows.Single().Detail;

        Assert.Equal(events.Rows("INFO"), verdict.Rows("INFO"));
        Assert.Equal("pii", verdict.JudgeKind);
        Assert.Equal("run-1", verdict.RunId);
        Assert.Equal("cccccccccccccccc", verdict.SpanId);
    }
}
