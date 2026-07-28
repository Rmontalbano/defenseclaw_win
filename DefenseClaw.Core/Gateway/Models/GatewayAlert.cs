using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// One entry from <c>GET /alerts</c>. The interesting content lives in the flat
/// <c>structured</c> bag keyed by dotted OpenTelemetry-style attribute names, so the
/// well-known keys get typed accessors while the raw bag stays available.
/// </summary>
public sealed class GatewayAlert
{
    /// <summary>Well-known <c>structured</c> keys, so panels do not hardcode strings.</summary>
    public static class Keys
    {
        public const string RuleId = "defenseclaw.finding.rule_id";
        public const string Title = "defenseclaw.finding.title";
        public const string Confidence = "defenseclaw.finding.confidence";
        public const string TargetRef = "defenseclaw.finding.target_ref";
        public const string Tags = "defenseclaw.finding.tags";
        public const string DataAxes = "defenseclaw.finding.data_axes";
        public const string ContentFingerprint = "defenseclaw.finding.content_fingerprint";
        public const string FindingId = "defenseclaw.finding.id";
        public const string ToolCapabilityClass = "defenseclaw.finding.tool_capability_class";
        public const string EvidenceSummary = "defenseclaw.guardrail.evidence_summary";
        public const string Scanner = "defenseclaw.scan.scanner";
        public const string ScanId = "defenseclaw.scan.id";
        public const string EvaluationId = "defenseclaw.evaluation.id";
        public const string Severity = "defenseclaw.security.severity";
    }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }

    /// <summary>INFO / LOW / MEDIUM / WARN / HIGH / CRITICAL.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("structured")]
    public Dictionary<string, JsonElement>? Structured { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    /// <summary>e.g. <c>CMD-ENV-DUMP</c>.</summary>
    public string? RuleId => StructuredString(Keys.RuleId);

    /// <summary>e.g. <c>Environment variable dump</c>.</summary>
    public string? Title => StructuredString(Keys.Title);

    /// <summary>e.g. <c>claudecode:PreToolUse</c>.</summary>
    public string? TargetRef => StructuredString(Keys.TargetRef);

    /// <summary>The matched snippet the scanner keyed on — the "evidence" the UI must show.</summary>
    public string? EvidenceSummary => StructuredString(Keys.EvidenceSummary);

    public string? Scanner => StructuredString(Keys.Scanner);

    public string? FindingId => StructuredString(Keys.FindingId);

    public double? Confidence =>
        Structured is not null &&
        Structured.TryGetValue(Keys.Confidence, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public IReadOnlyList<string> Tags => StructuredStringArray(Keys.Tags);

    public IReadOnlyList<string> DataAxes => StructuredStringArray(Keys.DataAxes);

    /// <summary>Reads a string attribute out of the <c>structured</c> bag.</summary>
    public string? StructuredString(string key) =>
        Structured is not null &&
        Structured.TryGetValue(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private IReadOnlyList<string> StructuredStringArray(string key)
    {
        if (Structured is null ||
            !Structured.TryGetValue(key, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
            {
                items.Add(text);
            }
        }

        return items;
    }
}
