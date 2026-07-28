using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// One subsystem block from <c>/health</c>: <c>state</c>, <c>since</c> and a free-form
/// <c>details</c> object whose shape differs per subsystem, so it stays a
/// <see cref="JsonElement"/> with typed helpers on top.
/// </summary>
public sealed class ServiceState
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsDisabled => string.Equals(State, "disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a string out of <c>details</c>; null when absent or not a string.</summary>
    public string? DetailString(string key) =>
        Details is { ValueKind: JsonValueKind.Object } details &&
        details.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads a bool out of <c>details</c>; null when absent or not a bool.</summary>
    public bool? DetailBool(string key) =>
        Details is { ValueKind: JsonValueKind.Object } details &&
        details.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>Per-connector counters from <c>/health</c>.</summary>
public sealed class ConnectorStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary><c>manual</c>, <c>auto</c>, …</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }

    [JsonPropertyName("last_activity_at")]
    public DateTimeOffset? LastActivityAt { get; init; }

    [JsonPropertyName("tool_inspection_mode")]
    public string? ToolInspectionMode { get; init; }

    [JsonPropertyName("subprocess_policy")]
    public string? SubprocessPolicy { get; init; }

    [JsonPropertyName("requests")]
    public long Requests { get; init; }

    [JsonPropertyName("errors")]
    public long Errors { get; init; }

    [JsonPropertyName("tool_inspections")]
    public long ToolInspections { get; init; }

    [JsonPropertyName("tool_blocks")]
    public long ToolBlocks { get; init; }

    [JsonPropertyName("subprocess_blocks")]
    public long SubprocessBlocks { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Binary/config provenance stamped into every gateway response.</summary>
public sealed class Provenance
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    /// <summary>The running DefenseClaw version, e.g. <c>0.8.7</c>. Used for API-drift gating.</summary>
    [JsonPropertyName("binary_version")]
    public string? BinaryVersion { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Payload of <c>GET /health</c> — the only unauthenticated endpoint on 0.8.7.</summary>
public sealed class GatewayHealth
{
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("uptime_ms")]
    public long UptimeMs { get; init; }

    [JsonPropertyName("api")]
    public ServiceState? Api { get; init; }

    [JsonPropertyName("config")]
    public ServiceState? Config { get; init; }

    [JsonPropertyName("watcher")]
    public ServiceState? Watcher { get; init; }

    [JsonPropertyName("telemetry")]
    public ServiceState? Telemetry { get; init; }

    /// <summary>OpenClaw fleet uplink — <c>disabled</c> in standalone installs.</summary>
    [JsonPropertyName("gateway")]
    public ServiceState? FleetUplink { get; init; }

    [JsonPropertyName("guardrail")]
    public ServiceState? Guardrail { get; init; }

    [JsonPropertyName("ai_discovery")]
    public ServiceState? AiDiscovery { get; init; }

    [JsonPropertyName("application_protection")]
    public ServiceState? ApplicationProtection { get; init; }

    [JsonPropertyName("connector")]
    public ConnectorStatus? Connector { get; init; }

    [JsonPropertyName("connectors")]
    public IReadOnlyList<ConnectorStatus> Connectors { get; init; } = Array.Empty<ConnectorStatus>();

    [JsonPropertyName("provenance")]
    public Provenance? Provenance { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    public TimeSpan Uptime => TimeSpan.FromMilliseconds(UptimeMs);

    /// <summary>Every named subsystem, for the Overview panel's Services box.</summary>
    public IEnumerable<KeyValuePair<string, ServiceState>> Services()
    {
        if (Api is not null) yield return new("api", Api);
        if (Config is not null) yield return new("config", Config);
        if (Watcher is not null) yield return new("watcher", Watcher);
        if (Telemetry is not null) yield return new("telemetry", Telemetry);
        if (Guardrail is not null) yield return new("guardrail", Guardrail);
        if (AiDiscovery is not null) yield return new("ai_discovery", AiDiscovery);
        if (ApplicationProtection is not null) yield return new("application_protection", ApplicationProtection);
        if (FleetUplink is not null) yield return new("gateway", FleetUplink);
    }
}
