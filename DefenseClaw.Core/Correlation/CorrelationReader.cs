using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Core.Correlation;

/// <summary>How a read of a correlation or directory route ended.</summary>
public enum CorrelationReadStatus
{
    /// <summary>An answer arrived and has the shape this app reads.</summary>
    Ok,

    /// <summary>The gateway has no such route (HTTP 404 or 405): a runtime without the ledger. A limit of the version, not a fault.</summary>
    Unsupported,

    /// <summary>The gateway answered 503: the correlation ledger is not open (the audit store is not ready).</summary>
    NotReady,

    /// <summary>The gateway answered 400: it refused the query. The reason is its own sentence.</summary>
    Rejected,

    /// <summary>Nothing answered, or the listener is not the gateway.</summary>
    Unreachable,

    /// <summary>The gateway refused the app's token.</summary>
    Unauthorized,

    /// <summary>The answer is longer than the limit this app reads and was not read.</summary>
    TooLarge,

    /// <summary>It answered, and the answer is not what the route returns (not JSON, not an object).</summary>
    Malformed,

    /// <summary>Any other failure (a 5xx, an error envelope).</summary>
    Failed,
}

/// <summary>The outcome of one read: the value, or the sentence that says why there is none.</summary>
public sealed record CorrelationRead<T>(CorrelationReadStatus Status, T? Value, string Message)
    where T : class
{
    public bool IsOk => Status == CorrelationReadStatus.Ok && Value is not null;

    /// <summary>
    /// True when the failure says nothing about the version: what is on screen is kept and marked stale rather than replaced by an empty
    /// answer, because an empty timeline reads as an agent that did nothing when it is really a lost connection.
    /// </summary>
    public bool KeepsLastGood => Status is CorrelationReadStatus.Unreachable or CorrelationReadStatus.Failed or CorrelationReadStatus.NotReady or CorrelationReadStatus.TooLarge;
}

/// <summary>
/// Reads the gateway's correlation answers (<c>graph</c>, <c>conflicts</c>) and the agent directory into models, with a limit on everything.
/// <para>
/// <b>Shapes.</b> They are the ones <c>internal/audit/correlation_query_v8.go</c> and <c>internal/gateway/agent_identity_recorder.go</c> of 1.0.x
/// marshal, written from that source and not from a live answer. The reader is tolerant in both directions: a member it does not know is ignored
/// (a newer gateway adds fields), an optional one that is missing is its empty value, a member of the wrong kind is its empty value, and an entry
/// that is not an object is counted in <c>Skipped</c> and dropped. Go writes a nil slice as <c>null</c> and the zero time as
/// <c>0001-01-01T00:00:00Z</c>; the first is an empty list and the second is "no time". An answer that is not a JSON object is
/// <see cref="CorrelationReadStatus.Malformed"/>, never an empty page.
/// </para>
/// <para>
/// <b>Limits.</b> The body is bounded by the caller's read (<see cref="CorrelationRoutes.MaxGraphBytes"/>); on top of that a page keeps at most
/// <see cref="MaxEvents"/> events, <see cref="MaxObservations"/> records, <see cref="MaxRelationships"/> relationships and
/// <see cref="MaxEvidence"/> evidence rows, and every string is cut to a length. What is cut is counted, never silent.
/// </para>
/// </summary>
public static class CorrelationReader
{
    public const int MaxEvents = 500;
    public const int MaxObservations = 5000;
    public const int MaxRelationships = 2000;
    public const int MaxEvidence = 5000;
    public const int MaxReceipts = 500;
    public const int MaxIdentities = 1000;

    /// <summary>Turns the gateway client's answer into a read result. The document is not disposed here.</summary>
    public static CorrelationRead<T> FromGateway<T>(GatewayResult<JsonDocument> result, Func<JsonElement, CorrelationRead<T>> parse)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(parse);

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                return result.Value is { } document
                    ? parse(document.RootElement)
                    : Fail<T>(CorrelationReadStatus.Malformed, "The gateway's answer was empty.");

            case GatewayStatus.Unreachable:
                return Fail<T>(CorrelationReadStatus.Unreachable, "The gateway is not answering.");

            case GatewayStatus.Unauthorized:
                return Fail<T>(CorrelationReadStatus.Unauthorized, "The gateway refused this app's API token.");

            case GatewayStatus.NotConnected:
                return Fail<T>(CorrelationReadStatus.NotReady, "The gateway is running but the correlation ledger is not wired up.");

            default:
                return result.HttpStatusCode switch
                {
                    404 or 405 => Fail<T>(CorrelationReadStatus.Unsupported, "This gateway does not serve the correlation ledger (HTTP " + result.HttpStatusCode.Value.ToString(CultureInfo.InvariantCulture) + ")."),
                    503 => Fail<T>(CorrelationReadStatus.NotReady, "The gateway's correlation ledger is not available yet (its audit store is not ready)."),
                    400 => Fail<T>(CorrelationReadStatus.Rejected, "The gateway refused the query: " + AiRuntimeJson.Prose(result.ErrorMessage, 256)),
                    _ when result.ErrorMessage is { } text && text.Contains("larger than", StringComparison.Ordinal) =>
                        Fail<T>(CorrelationReadStatus.TooLarge, "The gateway's answer is larger than this app reads, so it was not read."),
                    >= 200 and <= 299 => Fail<T>(CorrelationReadStatus.Malformed, "The gateway's answer could not be read: " + AiRuntimeJson.Prose(result.ErrorMessage, 256)),
                    { } code => Fail<T>(CorrelationReadStatus.Failed, "The gateway returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + "."),
                    null => Fail<T>(CorrelationReadStatus.Failed, "The gateway returned an error: " + AiRuntimeJson.Prose(result.ErrorMessage, 256)),
                };
        }
    }

    /// <summary>Reads a graph page from JSON text (fixtures and tests); text that is not JSON is <see cref="CorrelationReadStatus.Malformed"/>.</summary>
    public static CorrelationRead<CorrelationGraphPage> ParseGraphText(string json) => ParseText(json, ParseGraph);

    /// <summary>Reads a conflicts page from JSON text.</summary>
    public static CorrelationRead<CorrelationConflictsPage> ParseConflictsText(string json) => ParseText(json, ParseConflicts);

    /// <summary>Reads a directory page from JSON text.</summary>
    public static CorrelationRead<AgentDirectoryPage> ParseDirectoryText(string json) => ParseText(json, ParseDirectory);

    private static CorrelationRead<T> ParseText<T>(string json, Func<JsonElement, CorrelationRead<T>> parse)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Fail<T>(CorrelationReadStatus.Malformed, "The gateway's answer is not valid JSON.");
        }
    }

    /// <summary>Reads the graph page in <paramref name="root"/>.</summary>
    public static CorrelationRead<CorrelationGraphPage> ParseGraph(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail<CorrelationGraphPage>(CorrelationReadStatus.Malformed, "The correlation answer is not a JSON object.");
        }

        var skipped = 0;
        var events = Objects(root, "events", MaxEvents, ref skipped, ReadEvent);
        var observations = Objects(root, "observations", MaxObservations, ref skipped, ReadObservation);
        var relationships = Objects(root, "relationships", MaxRelationships, ref skipped, ReadRelationship);
        var evidence = Objects(root, "evidence", MaxEvidence, ref skipped, ReadEvidence);

        var nextTime = Str(root, "next_after_time");
        var nextId = Name(root, "next_after_id");
        var next = nextTime.Length > 0 && ParseTime(nextTime) is not null && nextId.Length > 0;

        var page = new CorrelationGraphPage(
            events,
            observations,
            relationships,
            evidence,
            ParseTime(Str(root, "as_of")),
            ParseTime(Str(root, "ingest_watermark")),
            AiRuntimeJson.Flag(root, "truncated"),
            next ? nextTime : null,
            next ? nextId : null,
            skipped);
        return new CorrelationRead<CorrelationGraphPage>(CorrelationReadStatus.Ok, page, string.Empty);
    }

    /// <summary>Reads the conflicts page in <paramref name="root"/>.</summary>
    public static CorrelationRead<CorrelationConflictsPage> ParseConflicts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail<CorrelationConflictsPage>(CorrelationReadStatus.Malformed, "The correlation answer is not a JSON object.");
        }

        var skipped = 0;
        var page = new CorrelationConflictsPage(
            Objects(root, "relationships", MaxRelationships, ref skipped, ReadRelationship),
            Objects(root, "receipts", MaxReceipts, ref skipped, ReadReceipt),
            AiRuntimeJson.Flag(root, "truncated"));
        return new CorrelationRead<CorrelationConflictsPage>(CorrelationReadStatus.Ok, page, string.Empty);
    }

    /// <summary>Reads the directory page in <paramref name="root"/>.</summary>
    public static CorrelationRead<AgentDirectoryPage> ParseDirectory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("identities", out var list) || list.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            return Fail<AgentDirectoryPage>(CorrelationReadStatus.Malformed, "The agent directory answer is incomplete: it is missing identities. Nothing here is an empty directory.");
        }

        var skipped = 0;
        var rows = Objects(root, "identities", MaxIdentities, ref skipped, ReadIdentity);
        var enabled = !root.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
        var total = Math.Max(AiRuntimeJson.Long(root, "total"), rows.Count);
        var page = new AgentDirectoryPage(
            enabled,
            AiRuntimeJson.Flag(root, "persisted"),
            AiRuntimeJson.Prose(Str(root, "persist_error"), 256),
            rows,
            total,
            Name(root, "next_cursor"),
            skipped);
        return new CorrelationRead<AgentDirectoryPage>(CorrelationReadStatus.Ok, page, string.Empty);
    }

    private static IReadOnlyList<T> Objects<T>(JsonElement parent, string name, int max, ref int skipped, Func<JsonElement, T?> read)
        where T : class
    {
        var list = new List<T>();
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || list.Count >= max || read(element) is not { } item)
            {
                skipped++;
                continue;
            }

            list.Add(item);
        }

        return list;
    }

    private static CorrelationEventRow? ReadEvent(JsonElement e)
    {
        var id = Name(e, "semantic_event_id");
        return id.Length == 0
            ? null
            : new CorrelationEventRow(
                id,
                Name(e, "logical_event_id"),
                Name(e, "connector"),
                Name(e, "connector_instance_id"),
                Name(e, "source_rail"),
                Name(e, "event_name"),
                ParseTime(Str(e, "source_time")),
                ParseTime(Str(e, "received_time")),
                Name(e, "completeness"));
    }

    private static CorrelationObservationRow? ReadObservation(JsonElement e)
    {
        var record = Name(e, "record_id");
        return record.Length == 0
            ? null
            : new CorrelationObservationRow(
                record,
                Name(e, "semantic_event_id"),
                Name(e, "signal"),
                Name(e, "bucket"),
                Name(e, "event_name"),
                ParseTime(Str(e, "observed_at")),
                Name(e, "trace_id"),
                Name(e, "span_id"),
                Name(e, "session_id"),
                Name(e, "turn_id"),
                Name(e, "agent_id"),
                Name(e, "lifecycle_id"),
                Name(e, "execution_id"),
                Name(e, "model_request_id"),
                Name(e, "model_response_id"),
                Name(e, "tool_invocation_id"),
                Name(e, "status"));
    }

    private static CorrelationRelationshipRow? ReadRelationship(JsonElement e)
    {
        var id = Name(e, "relationship_id");
        if (id.Length == 0)
        {
            return null;
        }

        var method = Name(e, "method");
        var status = Name(e, "status");
        return new CorrelationRelationshipRow(
            id,
            Name(e, "from_kind"),
            Name(e, "from_id"),
            Name(e, "to_kind"),
            Name(e, "to_id"),
            Name(e, "type"),
            MethodOf(method),
            method,
            Math.Clamp(AiRuntimeJson.Int(e, "confidence"), 0, 100),
            Name(e, "rule_id"),
            Math.Max(AiRuntimeJson.Long(e, "evidence_count"), 0),
            StatusOf(status),
            status,
            ParseTime(Str(e, "created_at")),
            ParseTime(Str(e, "last_seen_at")));
    }

    private static CorrelationEvidenceRow? ReadEvidence(JsonElement e)
    {
        var id = Name(e, "evidence_id");
        return id.Length == 0
            ? null
            : new CorrelationEvidenceRow(
                id,
                Name(e, "relationship_id"),
                Name(e, "record_id"),
                Name(e, "semantic_event_id"),
                Name(e, "role"),
                Name(e, "integrity"),
                ParseTime(Str(e, "created_at")));
    }

    private static CorrelationReceiptConflict? ReadReceipt(JsonElement e)
    {
        var id = Name(e, "semantic_event_id");
        return id.Length == 0
            ? null
            : new CorrelationReceiptConflict(
                Name(e, "connector_instance_id"),
                id,
                Name(e, "conflicts_with_semantic_event_id"),
                Math.Max(AiRuntimeJson.Long(e, "delivery_count"), 0),
                ParseTime(Str(e, "first_received_at")),
                ParseTime(Str(e, "last_received_at")));
    }

    private static AgentIdentityRow? ReadIdentity(JsonElement e)
    {
        var id = Name(e, "agent_id");
        return id.Length == 0
            ? null
            : new AgentIdentityRow(
                id,
                Name(e, "user_name"),
                Name(e, "connector"),
                ParseTime(Str(e, "first_seen")),
                ParseTime(Str(e, "last_seen")),
                Name(e, "last_session_id"),
                Math.Max(AiRuntimeJson.Long(e, "sessions_seen"), 0),
                AiRuntimeJson.Flag(e, "retired"));
    }

    /// <summary>The method a ledger method word names; anything else is <see cref="RelationshipMethod.Unknown"/>.</summary>
    public static RelationshipMethod MethodOf(string text) => text switch
    {
        "reported" => RelationshipMethod.Reported,
        "trace_exact" => RelationshipMethod.TraceExact,
        "derived" => RelationshipMethod.Derived,
        "inferred" => RelationshipMethod.Inferred,
        _ => RelationshipMethod.Unknown,
    };

    /// <summary>The status a ledger status word names; anything else is <see cref="RelationshipStatus.Unknown"/>.</summary>
    public static RelationshipStatus StatusOf(string text) => text switch
    {
        "active" => RelationshipStatus.Active,
        "candidate" => RelationshipStatus.Candidate,
        "superseded" => RelationshipStatus.Superseded,
        "rejected" => RelationshipStatus.Rejected,
        "conflicted" => RelationshipStatus.Conflicted,
        _ => RelationshipStatus.Unknown,
    };

    /// <summary>An RFC 3339 time as UTC; null when it is empty, not a time, or Go's zero time (year 1, "never set").</summary>
    internal static DateTimeOffset? ParseTime(string text)
    {
        var parsed = AiRuntimeJson.ParseTime(text);
        return parsed is { Year: > 1 } ? parsed : null;
    }

    private static string Str(JsonElement element, string name) => AiRuntimeJson.Str(element, name);

    private static string Name(JsonElement element, string name) => AiRuntimeJson.Name(element, name);

    private static CorrelationRead<T> Fail<T>(CorrelationReadStatus status, string message)
        where T : class =>
        new(status, null, message);
}
