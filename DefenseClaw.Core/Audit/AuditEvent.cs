using System.Text.Json;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// Keyset pagination cursor. Ordering uses <c>retention_timestamp_unix_nano</c> — the
/// column the DB's own retention triggers maintain, and which the
/// <c>idx_retention_audit_events_timestamp(retention_timestamp_unix_nano, id)</c> index
/// covers. Ordering on the raw <c>timestamp</c> text is not safe: Go writes RFC3339Nano
/// with trailing zeros trimmed, so <c>…:23.1Z</c> sorts after <c>…:23.15Z</c>.
/// </summary>
public readonly record struct AuditCursor(long TimestampNanos, string Id)
{
    /// <summary>Round-trippable token for stashing in view state.</summary>
    public string ToToken() => $"{TimestampNanos}:{Id}";

    public static bool TryParse(string? token, out AuditCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf(':');
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(token.AsSpan(0, separator), out var nanos))
        {
            return false;
        }

        cursor = new AuditCursor(nanos, token[(separator + 1)..]);
        return true;
    }
}

/// <summary>
/// One row of <c>audit_events</c>. <see cref="StructuredJson"/> is parsed on first
/// access — the column is often several kilobytes and most rows are never expanded in
/// the UI.
/// </summary>
public sealed class AuditEvent
{
    private readonly object _parseGate = new();
    private IReadOnlyDictionary<string, JsonElement>? _structured;
    private bool _parsed;

    public required string Id { get; init; }

    /// <summary>Parsed timestamp. Falls back to <see cref="TimestampNanos"/> when the text is odd.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The column verbatim, e.g. <c>2026-07-28T19:41:04.536967Z</c>.</summary>
    public required string RawTimestamp { get; init; }

    /// <summary>Sort key; see <see cref="AuditCursor"/>.</summary>
    public long TimestampNanos { get; init; }

    /// <summary>e.g. <c>scan-finding</c>, <c>hook_decision</c>, <c>tool_invocation</c>.</summary>
    public required string Action { get; init; }

    public string? Target { get; init; }

    public string? Actor { get; init; }

    public string? Details { get; init; }

    /// <summary>INFO / LOW / MEDIUM / HIGH / CRITICAL as stored.</summary>
    public string? Severity { get; init; }

    /// <summary>e.g. <c>platform.health</c>, <c>asset.scan</c>, <c>guardrail.evaluation</c>.</summary>
    public string? Bucket { get; init; }

    /// <summary>Often NULL — plenty of platform events are not connector-scoped.</summary>
    public string? Connector { get; init; }

    public string? EventName { get; init; }

    public string? AgentName { get; init; }

    public string? ToolName { get; init; }

    public string? SessionId { get; init; }

    public string? RunId { get; init; }

    public string? RequestId { get; init; }

    public string? TraceId { get; init; }

    public string? Source { get; init; }

    public string? Signal { get; init; }

    public string? BinaryVersion { get; init; }

    /// <summary>Raw <c>structured_json</c> column; null when the row carries none, and when it was too large to load (see <see cref="Oversized"/>).</summary>
    public string? StructuredJsonRaw { get; init; }

    /// <summary>
    /// The values of this row that were bigger than the reader's payload limit (<see cref="AuditReader.DefaultPayloadLimitBytes"/>) and so were
    /// left in the database: their columns (<see cref="Details"/>, <see cref="StructuredJsonRaw"/>) are null, and each entry says which column,
    /// how big and the limit. Empty for a complete row. A row with entries is not an empty row and not a failed read: it is a row whose
    /// payload is unavailable, for that reason.
    /// </summary>
    public IReadOnlyList<OversizedValue> Oversized { get; init; } = Array.Empty<OversizedValue>();

    /// <summary>True when part of the row was too large to load (see <see cref="Oversized"/>).</summary>
    public bool IsOversized => Oversized.Count > 0;

    public AuditSeverity SeverityLevel => AuditSeverityExtensions.Parse(Severity);

    public AuditCursor Cursor => new(TimestampNanos, Id);

    /// <summary>
    /// Lazily parsed <c>structured_json</c>. Malformed JSON yields an empty map rather
    /// than throwing — one bad row must not break a whole page of results.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> StructuredJson
    {
        get
        {
            if (_parsed)
            {
                return _structured!;
            }

            lock (_parseGate)
            {
                if (!_parsed)
                {
                    _structured = ParseStructured(StructuredJsonRaw);
                    _parsed = true;
                }
            }

            return _structured!;
        }
    }

    public string? StructuredString(string key) =>
        StructuredJson.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyDictionary<string, JsonElement> ParseStructured(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["value"] = document.RootElement.Clone(),
                };
            }

            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Clone: the JsonDocument is disposed as we leave this scope.
                result[property.Name] = property.Value.Clone();
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }
}
