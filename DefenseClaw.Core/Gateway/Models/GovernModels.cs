using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// Fields common to the Govern-group listings. On the 0.8.7 install these endpoints
/// answer <c>{"error":"gateway: not connected"}</c> until an upstream is configured, so
/// the concrete shape is unverified: every property is optional and everything else
/// lands in <see cref="AdditionalData"/>. Panels should prefer
/// <c>GatewayClient.GetRawJsonAsync</c> when they need a field not modelled here.
/// </summary>
public abstract class GovernEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("connector")]
    public string? Connector { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    /// <summary>Best-effort display label.</summary>
    public string DisplayName => Name ?? Id ?? Path ?? "(unnamed)";
}

/// <summary>Entry from <c>GET /skills</c>.</summary>
public sealed class SkillEntry : GovernEntry
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("blocked")]
    public bool? Blocked { get; init; }
}

/// <summary>Entry from <c>GET /mcps</c>.</summary>
public sealed class McpEntry : GovernEntry
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("transport")]
    public string? Transport { get; init; }
}

/// <summary>Entry from <c>GET /tools/catalog</c>.</summary>
public sealed class ToolCatalogEntry : GovernEntry
{
    [JsonPropertyName("server")]
    public string? Server { get; init; }

    [JsonPropertyName("capability_class")]
    public string? CapabilityClass { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Entry from <c>GET /enforce/blocked</c> and <c>GET /enforce/allowed</c>.</summary>
public sealed class EnforcementEntry : GovernEntry
{
    /// <summary>skill / mcp / plugin / tool.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}
