using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// One finding of an LLM judge's verdict, as the 0.8.10 TUI's Logs detail lists it (<c>JudgeFinding</c> in <c>tui/services/gateway_log_views.py</c>): the compact five
/// fields, not the gateway's whole finding.
/// </summary>
/// <param name="Category">What the finding is about (<c>Instruction Manipulation</c>).</param>
/// <param name="Severity">Its severity as the judge wrote it.</param>
/// <param name="Rule">The rule it stands for (<c>JUDGE-INJ-INSTRUCT</c>); empty when there is none.</param>
/// <param name="Source">Who found it (<c>judge</c>, <c>regex</c>, ...); empty when it does not say.</param>
/// <param name="Confidence">0 to 1; 0 when the judge gave none, and then it is not shown.</param>
public sealed record JudgeFinding(string Category, string Severity, string Rule, string Source, double Confidence)
{
    /// <summary>
    /// The TUI's value for a "Finding N" row: <c>category=... severity=... rule=... source=... conf=0.90</c> - the rule and the source only when there are
    /// some, the confidence only above zero, to two places.
    /// </summary>
    public string Describe()
    {
        var value = "category=" + Category + " severity=" + Severity;
        if (Rule.Length > 0)
        {
            value += " rule=" + Rule;
        }

        if (Source.Length > 0)
        {
            value += " source=" + Source;
        }

        if (Confidence > 0)
        {
            value += " conf=" + Confidence.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return value;
    }
}

/// <summary>
/// The labelled fields of one canonical event, read from its payload and its correlation columns the way the 0.8.10 TUI reads them
/// (<c>_v8_gateway_log_row</c>) and listed the way its Logs detail does (<c>detail_pairs</c>): the Logs inspector's Verdicts and Events rows (CUST-263).
/// <para>
/// <b>Where each field comes from</b> (the TUI's keys, in the TUI's order of preference). Stage: the last part of the event name
/// (<c>guardrail.judge.completed</c> is <c>completed</c>). Direction: <c>gen_ai.operation.name</c>, else <c>defenseclaw.hook.event</c>. Model:
/// <c>gen_ai.request.model</c>, else <c>gen_ai.response.model</c>. Provider: who recorded the row. The run, trace, request and session ids are the row's own
/// columns; the span id is the one the stored record names (<c>projected_record_json</c> <c>correlation.span_id</c>), which the reader fetches for the rows it has
/// not seen before. Categories: <c>defenseclaw.guardrail.rule_ids</c>. Latency: <c>defenseclaw.guardrail.latency_ms</c> - and, where that is absent, the judge's
/// own <c>defenseclaw.judge.latency_ms</c>, which the TUI does not read and the judge log is the only one to carry. The judge's kind, input size and parse error:
/// <c>defenseclaw.judge.kind</c>, <c>.input_bytes</c>, <c>.parse_error</c>. Reason: the guardrail's, the evidence's, the finding's, the judge's or the error's
/// summary, else the row's details.
/// </para>
/// <para>
/// <b>Judge findings and a judge's severity.</b> The TUI's detail has a "Finding N" row per <see cref="JudgeFinding"/> and a "Judge severity" row when it
/// differs from the row's, but its canonical projection never fills the list and sets the severity to the row's - and no registered attribute carries either (the
/// pinned catalogue's judge log has the kind, action, latency, input size, parse error and error summary, and a count of findings). So they are read from
/// <c>defenseclaw.judge.findings</c> (an array of <c>{category, severity, rule, source, confidence}</c>, the shape of the gateway's own <c>Finding</c>) and
/// <c>defenseclaw.judge.severity</c> when a payload has them: a database written today shows neither, and one that carries them shows them as the TUI's detail would.
/// </para>
/// <para>
/// <b>Masked and bounded.</b> Every string is masked by <see cref="DisplayRedaction"/> as it is read, and a payload over
/// <see cref="EventStreamReader.PayloadByteLimit"/> is not read at all: the fields that come from it are then empty and the row says why
/// (<see cref="StreamEvent.Oversized"/>), which is not the same thing as a row that has none.
/// </para>
/// </summary>
public sealed record EventDetail
{
    /// <summary>The most categories and findings read from one payload; a 64 KiB payload could otherwise hold thousands.</summary>
    public const int ListLimit = 32;

    private static readonly string[] DirectionKeys = { "gen_ai.operation.name", "defenseclaw.hook.event" };
    private static readonly string[] ModelKeys = { "gen_ai.request.model", "gen_ai.response.model" };
    private static readonly string[] ErrorCodeKeys = { "defenseclaw.error.code", "defenseclaw.telemetry.rejection_reason" };

    /// <summary>An event with nothing to say beyond the row itself.</summary>
    public static EventDetail Empty { get; } = new();

    public string Stage { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>Who recorded the row (its <c>source</c> column).</summary>
    public string Provider { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string RunId { get; init; } = string.Empty;

    public string TraceId { get; init; } = string.Empty;

    public string SpanId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    /// <summary>The rule ids behind the verdict (<c>defenseclaw.guardrail.rule_ids</c>).</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public int LatencyMs { get; init; }

    public string JudgeKind { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// The judge's own severity, from <c>defenseclaw.judge.severity</c> when a payload names one; empty otherwise. (The TUI's is the row's own severity, so its
    /// "Judge severity" row never shows; here it shows when the judge's differs from the row's.)
    /// </summary>
    public string JudgeSeverity { get; init; } = string.Empty;

    public int JudgeInputBytes { get; init; }

    public string JudgeParseError { get; init; } = string.Empty;

    public IReadOnlyList<JudgeFinding> Findings { get; init; } = Array.Empty<JudgeFinding>();

    /// <summary>The stable code of an error (<c>defenseclaw.error.code</c>, else the telemetry rejection reason); empty when there is none.</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>
    /// The labelled rows of a connector's hook call, from its key=value details (<see cref="StructuredDetailParser.InspectorRows"/>: Connector, Tool,
    /// Decision, Enforcement mode, ...); empty for every other row.
    /// </summary>
    public IReadOnlyList<DetailPair> Hook { get; init; } = Array.Empty<DetailPair>();

    /// <summary>
    /// The TUI's rows after its first four (Timestamp, Event type, Severity, Action), in its order and under its labels (<c>detail_pairs</c>), each only when
    /// it has a value: Stage, Direction, Model, Provider, Request ID, Run ID, Trace ID, Span ID, Session ID, Categories, Latency (ms), Judge kind, Reason, Judge
    /// severity (only when it is not the row's), Judge input bytes, Judge parse error, and Finding 1, Finding 2, ....
    /// </summary>
    /// <param name="severity">The row's severity, which a judge severity must differ from to be a row of its own.</param>
    public IReadOnlyList<DetailPair> Rows(string? severity)
    {
        var rows = new List<DetailPair>(16 + Findings.Count);

        void Add(string label, string value)
        {
            if (value.Length > 0)
            {
                rows.Add(new DetailPair(label, value));
            }
        }

        Add("Stage", Stage);
        Add("Direction", Direction);
        Add("Model", Model);
        Add("Provider", Provider);
        Add("Request ID", RequestId);
        Add("Run ID", RunId);
        Add("Trace ID", TraceId);
        Add("Span ID", SpanId);
        Add("Session ID", SessionId);
        Add("Categories", string.Join(", ", Categories));
        Add("Latency (ms)", LatencyMs > 0 ? LatencyMs.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Add("Judge kind", JudgeKind);
        Add("Reason", Reason);
        Add("Judge severity", JudgeSeverity.Length > 0 && !string.Equals(JudgeSeverity, severity ?? string.Empty, StringComparison.OrdinalIgnoreCase) ? JudgeSeverity : string.Empty);
        Add("Judge input bytes", JudgeInputBytes > 0 ? JudgeInputBytes.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Add("Judge parse error", JudgeParseError);

        for (var i = 0; i < Findings.Count; i++)
        {
            Add("Finding " + (i + 1).ToString(CultureInfo.InvariantCulture), Findings[i].Describe());
        }

        return rows;
    }

    /// <summary>
    /// Reads the fields from one event's payload (<c>null</c> when it has none, was not an object or was too large) and its columns. <paramref name="reason"/> is
    /// the reason the reader settled on (the payload's, else the row's details); everything is masked here.
    /// </summary>
    internal static EventDetail Read(
        JsonElement? payload,
        string eventName,
        string provider,
        string runId,
        string traceId,
        string requestId,
        string sessionId,
        string reason,
        string? hookDetails)
    {
        var stageAt = eventName.LastIndexOf('.');
        var latency = Int(payload, "defenseclaw.guardrail.latency_ms");

        return new EventDetail
        {
            Stage = Mask(stageAt >= 0 ? eventName[(stageAt + 1)..] : eventName),
            Direction = Mask(Text(payload, DirectionKeys)),
            Model = Mask(Text(payload, ModelKeys)),
            Provider = Mask(provider),
            RequestId = Mask(requestId),
            RunId = Mask(runId),
            TraceId = Mask(traceId),
            SessionId = Mask(sessionId),
            Categories = ReadCategories(payload, "defenseclaw.guardrail.rule_ids"),
            LatencyMs = latency > 0 ? latency : Int(payload, "defenseclaw.judge.latency_ms"),
            JudgeKind = Mask(Text(payload, "defenseclaw.judge.kind")),
            Reason = Mask(reason),
            JudgeSeverity = Mask(Text(payload, "defenseclaw.judge.severity")),
            JudgeInputBytes = Int(payload, "defenseclaw.judge.input_bytes"),
            JudgeParseError = Mask(Text(payload, "defenseclaw.judge.parse_error")),
            Findings = ReadFindings(payload, "defenseclaw.judge.findings"),
            ErrorCode = Mask(Text(payload, ErrorCodeKeys)),
            Hook = hookDetails is { Length: > 0 } ? StructuredDetailParser.InspectorRows(hookDetails) : Array.Empty<DetailPair>(),
        };
    }

    private static string Mask(string? value) => DisplayRedaction.Text(value);

    /// <summary>The TUI's <c>payload_text</c>: the first of <paramref name="keys"/> whose value is a string, a number or a boolean, trimmed, that is not empty.</summary>
    private static string Text(JsonElement? payload, params string[] keys)
    {
        if (payload is not { } element)
        {
            return string.Empty;
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };

            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    /// <summary>The TUI's <c>_int</c>: a number (a fraction cut off) or a numeric string; anything else is 0, and so is a value that does not fit.</summary>
    private static int Int(JsonElement? payload, string key)
    {
        if (payload is not { } element || !element.TryGetProperty(key, out var value))
        {
            return 0;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var whole))
                {
                    return whole;
                }

                return value.TryGetDouble(out var real) && Math.Abs(real) < int.MaxValue ? (int)real : 0;
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            case JsonValueKind.True:
                return 1;
            default:
                return 0;
        }
    }

    private static IReadOnlyList<string> ReadCategories(JsonElement? payload, string key)
    {
        if (payload is not { } element || !element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var categories = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (categories.Count == ListLimit)
            {
                break;
            }

            var text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
            categories.Add(Mask(text));
        }

        return categories;
    }

    private static IReadOnlyList<JudgeFinding> ReadFindings(JsonElement? payload, string key)
    {
        if (payload is not { } element || !element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JudgeFinding>();
        }

        var findings = new List<JudgeFinding>();
        foreach (var item in value.EnumerateArray())
        {
            if (findings.Count == ListLimit)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            findings.Add(new JudgeFinding(
                Mask(Text(item, "category")),
                Mask(Text(item, "severity")),
                Mask(Text(item, "rule")),
                Mask(Text(item, "source")),
                Confidence(item)));
        }

        return findings;
    }

    private static double Confidence(JsonElement finding)
    {
        if (!finding.TryGetProperty("confidence", out var value))
        {
            return 0;
        }

        var confidence = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var real) ? real : 0,
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0,
        };

        return double.IsFinite(confidence) && confidence > 0 ? confidence : 0;
    }
}
