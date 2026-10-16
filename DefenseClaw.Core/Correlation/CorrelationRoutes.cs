using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Correlation;

/// <summary>What a correlation request is anchored on. The gateway takes exactly one per request.</summary>
public enum CorrelationAnchorKind
{
    Agent,
    Session,
    Turn,
    Lifecycle,
    Execution,
    Trace,
    Record,
    SemanticEvent,
    LogicalEvent,
    ModelRequest,
    ModelResponse,
    ToolInvocation,
}

/// <summary>One anchor: the kind, the id, and (for a trace) an optional span.</summary>
public sealed record CorrelationAnchor(CorrelationAnchorKind Kind, string Value, string? SpanId = null)
{
    public static CorrelationAnchor ForAgent(string agentId) => new(CorrelationAnchorKind.Agent, agentId);

    public static CorrelationAnchor ForSession(string sessionId) => new(CorrelationAnchorKind.Session, sessionId);

    /// <summary>The query parameter the gateway reads this anchor from.</summary>
    public string ParameterName => Kind switch
    {
        CorrelationAnchorKind.Agent => "agent_id",
        CorrelationAnchorKind.Session => "session_id",
        CorrelationAnchorKind.Turn => "turn_id",
        CorrelationAnchorKind.Lifecycle => "lifecycle_id",
        CorrelationAnchorKind.Execution => "execution_id",
        CorrelationAnchorKind.Trace => "trace_id",
        CorrelationAnchorKind.Record => "record_id",
        CorrelationAnchorKind.SemanticEvent => "semantic_event_id",
        CorrelationAnchorKind.LogicalEvent => "logical_event_id",
        CorrelationAnchorKind.ModelRequest => "model_request_id",
        CorrelationAnchorKind.ModelResponse => "model_response_id",
        CorrelationAnchorKind.ToolInvocation => "tool_invocation_id",
        _ => "agent_id",
    };

    /// <summary>The short word the lookup box offers (<c>Agent</c>, <c>Session</c>).</summary>
    public string Label => Kind switch
    {
        CorrelationAnchorKind.Agent => "agent",
        CorrelationAnchorKind.Session => "session",
        CorrelationAnchorKind.Turn => "turn",
        CorrelationAnchorKind.Lifecycle => "lifecycle",
        CorrelationAnchorKind.Execution => "execution",
        CorrelationAnchorKind.Trace => "trace",
        CorrelationAnchorKind.Record => "record",
        CorrelationAnchorKind.SemanticEvent => "semantic event",
        CorrelationAnchorKind.LogicalEvent => "logical event",
        CorrelationAnchorKind.ModelRequest => "model request",
        CorrelationAnchorKind.ModelResponse => "model response",
        CorrelationAnchorKind.ToolInvocation => "tool invocation",
        _ => "agent",
    };

    /// <summary>
    /// Why the gateway would refuse this anchor, or null when it would take it. The same checks the gateway runs (<c>validateCorrelationQueryShape</c>),
    /// done here so an id typed with a space in it costs a sentence, not a round trip that answers 400.
    /// </summary>
    public string? Problem()
    {
        if (string.IsNullOrEmpty(Value))
        {
            return "Enter an id.";
        }

        if (Value.Trim() != Value)
        {
            return "The id has spaces at its start or end.";
        }

        if (Encoding.UTF8.GetByteCount(Value) > CorrelationRoutes.MaxIdentifierBytes)
        {
            return "The id is longer than " + CorrelationRoutes.MaxIdentifierBytes.ToString(CultureInfo.InvariantCulture) + " bytes.";
        }

        if (Value.Any(char.IsControl))
        {
            return "The id has a control character in it.";
        }

        if (Kind is CorrelationAnchorKind.SemanticEvent or CorrelationAnchorKind.LogicalEvent && !CorrelationRoutes.UuidV7.IsMatch(Value))
        {
            return "A " + Label + " id is a canonical UUIDv7.";
        }

        if (Kind == CorrelationAnchorKind.Trace && !CorrelationRoutes.LowerHex(Value, 32))
        {
            return "A trace id is 32 lowercase hexadecimal characters.";
        }

        if (SpanId is { Length: > 0 })
        {
            if (Kind != CorrelationAnchorKind.Trace)
            {
                return "A span id needs a trace id.";
            }

            if (!CorrelationRoutes.LowerHex(SpanId, 16))
            {
                return "A span id is 16 lowercase hexadecimal characters.";
            }
        }

        return null;
    }
}

/// <summary>
/// The four read-only routes of the gateway's correlation ledger (<c>/api/v1/correlation/{graph,explain,timeline,conflicts}</c>) and the limits
/// every read of them works to. The query rules are the gateway's own (<c>parseCorrelationQuery</c> in <c>internal/gateway/api_correlation_v8.go</c>
/// of 1.0.x): exactly one anchor, each parameter once, <c>limit</c> 1 to 500, <c>after_time</c> and <c>after_id</c> together.
/// </summary>
public static class CorrelationRoutes
{
    public const string Graph = "api/v1/correlation/graph";

    public const string Explain = "api/v1/correlation/explain";

    public const string Timeline = "api/v1/correlation/timeline";

    public const string Conflicts = "api/v1/correlation/conflicts";

    /// <summary>The agent directory: one identity per harness install, newest first (an authenticated read; 1.0.x).</summary>
    public const string AgentIdentities = "api/v1/agents/identities";

    /// <summary>The gateway's largest <c>limit</c>.</summary>
    public const int MaxLimit = 500;

    /// <summary>The page size this app asks for: a screenful of events per request.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The gateway's longest identifier.</summary>
    public const int MaxIdentifierBytes = 512;

    /// <summary>The most bytes of one graph or conflicts answer read (a page of 500 events with their records is well under 2 MiB).</summary>
    public const int MaxGraphBytes = 4 * 1024 * 1024;

    /// <summary>The most bytes of one directory page read (1000 identities are about 400 KiB).</summary>
    public const int MaxDirectoryBytes = 2 * 1024 * 1024;

    internal static readonly Regex UuidV7 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal static bool LowerHex(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The relative request path for <paramref name="route"/>. Throws <see cref="ArgumentException"/> for an anchor the gateway would refuse or
    /// a cursor that is only half there: callers check <see cref="CorrelationAnchor.Problem"/> first and show the sentence.
    /// </summary>
    public static string Query(string route, CorrelationAnchor anchor, int limit = DefaultLimit, string? afterTime = null, string? afterId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(route);
        ArgumentNullException.ThrowIfNull(anchor);

        if (anchor.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(anchor));
        }

        if (limit < 1 || limit > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and " + MaxLimit.ToString(CultureInfo.InvariantCulture));
        }

        if (string.IsNullOrEmpty(afterTime) != string.IsNullOrEmpty(afterId))
        {
            throw new ArgumentException("after_time and after_id must be provided together.", nameof(afterId));
        }

        var text = new StringBuilder(route)
            .Append('?').Append(anchor.ParameterName).Append('=').Append(Uri.EscapeDataString(anchor.Value));
        if (anchor.SpanId is { Length: > 0 })
        {
            _ = text.Append("&span_id=").Append(Uri.EscapeDataString(anchor.SpanId));
        }

        _ = text.Append("&limit=").Append(limit.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(afterTime))
        {
            _ = text.Append("&after_time=").Append(Uri.EscapeDataString(afterTime)).Append("&after_id=").Append(Uri.EscapeDataString(afterId!));
        }

        return text.ToString();
    }

    /// <summary>The request path of one page of the directory.</summary>
    public static string Directory(int limit, string? cursor)
    {
        var text = new StringBuilder(AgentIdentities).Append("?limit=").Append(Math.Clamp(limit, 1, 1000).ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(cursor))
        {
            _ = text.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        return text.ToString();
    }
}
