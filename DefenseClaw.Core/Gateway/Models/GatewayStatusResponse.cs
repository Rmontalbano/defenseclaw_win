using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// Enforcement posture for one connector. The Overview panel warns when
/// <see cref="HasFailModeMismatch"/> is true.
/// </summary>
public sealed class ConnectorMode
{
    private IReadOnlyList<string> _telemetry = Array.Empty<string>();

    [JsonPropertyName("connector")]
    public string? Connector { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>e.g. <c>agent_lifecycle_hooks</c>.</summary>
    [JsonPropertyName("enforcement_surface")]
    public string? EnforcementSurface { get; init; }

    /// <summary><c>observe</c> or <c>enforce</c>.</summary>
    [JsonPropertyName("guardrail_mode")]
    public string? GuardrailMode { get; init; }

    [JsonPropertyName("hook_enforcement")]
    public bool HookEnforcement { get; init; }

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    [JsonPropertyName("hook_fail_mode")]
    public string? HookFailMode { get; init; }

    /// <summary><c>observability</c> or <c>enforcement</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("policy_mode")]
    public string? PolicyMode { get; init; }

    [JsonPropertyName("proxy_intercept")]
    public bool ProxyIntercept { get; init; }

    /// <summary>
    /// Telemetry sinks in use, e.g. <c>hooks</c>, <c>otel</c>. Never null: an empty Go slice
    /// arrives as <c>"telemetry":null</c>, which the setter turns into an empty list — see
    /// <see cref="NullSafe"/>.
    /// </summary>
    [JsonPropertyName("telemetry")]
    public IReadOnlyList<string> Telemetry
    {
        get => _telemetry;
        init => _telemetry = NullSafe.ListOrEmpty(value);
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    /// <summary>Observe mode paired with a fail-closed hook — the recurring bad default.</summary>
    public bool HasFailModeMismatch =>
        string.Equals(GuardrailMode, "observe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(HookFailMode, "closed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Process-level facts about the running sidecar.</summary>
public sealed class RuntimeInfo
{
    [JsonPropertyName("data_dir")]
    public string? DataDir { get; init; }

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Payload of <c>GET /status</c>. Requires a bearer token on 0.8.7.</summary>
public sealed class GatewayStatusResponse
{
    private IReadOnlyList<ConnectorMode> _connectorModes = Array.Empty<ConnectorMode>();

    [JsonPropertyName("connector_mode")]
    public ConnectorMode? ConnectorMode { get; init; }

    /// <summary>
    /// Enforcement posture per connector. Never null and holds no null element: an empty Go
    /// slice arrives as <c>"connector_modes":null</c>, which the setter turns into an empty
    /// list — see <see cref="NullSafe"/>.
    /// </summary>
    [JsonPropertyName("connector_modes")]
    public IReadOnlyList<ConnectorMode> ConnectorModes
    {
        get => _connectorModes;
        init => _connectorModes = NullSafe.ListOrEmpty(value);
    }

    [JsonPropertyName("health")]
    public GatewayHealth? Health { get; init; }

    [JsonPropertyName("provenance")]
    public Provenance? Provenance { get; init; }

    [JsonPropertyName("runtime")]
    public RuntimeInfo? Runtime { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}
