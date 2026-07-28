using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Typed view over <c>~/.defenseclaw/config.yaml</c>. Unknown properties are ignored by
/// the deserializer; unknown top-level sections survive verbatim on
/// <see cref="ConfigDocument.UnknownSections"/> so nothing is lost on round-trip.
/// </summary>
public sealed class DefenseClawConfig
{
    /// <summary>Top-level keys this model understands. Everything else is passthrough.</summary>
    public static readonly IReadOnlySet<string> KnownSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cisco_ai_defense",
        "claw",
        "guardrail",
        "llm",
        "config_version",
        "observability",
        "gateway",
        "ai_discovery",
    };

    [YamlMember(Alias = "config_version")]
    public int ConfigVersion { get; set; }

    [YamlMember(Alias = "claw")]
    public ClawSection Claw { get; set; } = new();

    [YamlMember(Alias = "gateway")]
    public GatewaySection Gateway { get; set; } = new();

    [YamlMember(Alias = "guardrail")]
    public GuardrailSection Guardrail { get; set; } = new();

    [YamlMember(Alias = "cisco_ai_defense")]
    public CiscoAiDefenseSection CiscoAiDefense { get; set; } = new();

    [YamlMember(Alias = "llm")]
    public LlmSection Llm { get; set; } = new();

    [YamlMember(Alias = "ai_discovery")]
    public AiDiscoverySection AiDiscovery { get; set; } = new();
}

public sealed class ClawSection
{
    /// <summary>Active connector, e.g. <c>claudecode</c>.</summary>
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }
}

public sealed class GatewaySection
{
    /// <summary>Default REST port for the local sidecar API.</summary>
    public const int DefaultApiPort = 18970;

    /// <summary>Environment variable consulted first by the token ladder.</summary>
    public const string DefaultTokenEnv = "DEFENSECLAW_GATEWAY_TOKEN";

    private int? _apiPort;
    private string? _tokenEnv;

    /// <summary>REST API port. Falls back to <see cref="DefaultApiPort"/> when unset or invalid.</summary>
    [YamlMember(Alias = "api_port")]
    public int ApiPort
    {
        get => _apiPort is > 0 and <= 65535 ? _apiPort.Value : DefaultApiPort;
        set => _apiPort = value;
    }

    /// <summary>True when config.yaml actually carried an api_port.</summary>
    [YamlIgnore]
    public bool HasExplicitApiPort => _apiPort is > 0 and <= 65535;

    /// <summary>Name of the env var holding the bearer token. Never the token itself.</summary>
    [YamlMember(Alias = "token_env")]
    public string TokenEnv
    {
        get => string.IsNullOrWhiteSpace(_tokenEnv) ? DefaultTokenEnv : _tokenEnv;
        set => _tokenEnv = value;
    }

    /// <summary>
    /// Literal token, the last rung of the ladder. Present in some installs; kept as a
    /// bare string here because YamlDotNet must materialize it, and immediately wrapped
    /// in a <see cref="SecretValue"/> by <see cref="TokenResolver"/>.
    /// </summary>
    [YamlMember(Alias = "token")]
    public string? Token { get; set; }

    /// <summary>OpenClaw fleet uplink host (not the local REST API).</summary>
    [YamlMember(Alias = "host")]
    public string? Host { get; set; }

    /// <summary>OpenClaw fleet uplink port (not the local REST API).</summary>
    [YamlMember(Alias = "port")]
    public int? Port { get; set; }
}

public sealed class GuardrailSection
{
    [YamlMember(Alias = "connector")]
    public string? Connector { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    [YamlMember(Alias = "scanner_mode")]
    public string? ScannerMode { get; set; }

    [YamlMember(Alias = "detection_strategy_completion")]
    public string? DetectionStrategyCompletion { get; set; }

    [YamlMember(Alias = "connectors")]
    public Dictionary<string, GuardrailConnectorSettings> Connectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GuardrailConnectorSettings
{
    /// <summary><c>observe</c> or <c>enforce</c>.</summary>
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }

    /// <summary><c>open</c> or <c>closed</c>. Pairing observe + closed is the classic bad default.</summary>
    [YamlMember(Alias = "hook_fail_mode")]
    public string? HookFailMode { get; set; }

    [YamlMember(Alias = "block_message")]
    public string? BlockMessage { get; set; }

    [YamlMember(Alias = "rule_pack_dir")]
    public string? RulePackDir { get; set; }

    /// <summary>True when observe mode is paired with a fail-closed hook.</summary>
    [YamlIgnore]
    public bool HasFailModeMismatch =>
        string.Equals(Mode, "observe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(HookFailMode, "closed", StringComparison.OrdinalIgnoreCase);
}

public sealed class CiscoAiDefenseSection
{
    [YamlMember(Alias = "api_key_env")]
    public string? ApiKeyEnv { get; set; }
}

public sealed class LlmSection
{
    [YamlMember(Alias = "api_key_env")]
    public string? ApiKeyEnv { get; set; }
}

public sealed class AiDiscoverySection
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    /// <summary><c>basic</c> or <c>enhanced</c>.</summary>
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }

    [YamlMember(Alias = "scan_interval_min")]
    public int ScanIntervalMin { get; set; }

    [YamlMember(Alias = "process_interval_s")]
    public int ProcessIntervalS { get; set; }

    [YamlMember(Alias = "scan_roots")]
    public List<string> ScanRoots { get; set; } = new();

    [YamlMember(Alias = "signature_packs")]
    public List<string> SignaturePacks { get; set; } = new();

    [YamlMember(Alias = "allow_workspace_signatures")]
    public bool AllowWorkspaceSignatures { get; set; }

    [YamlMember(Alias = "disabled_signature_ids")]
    public List<string> DisabledSignatureIds { get; set; } = new();

    [YamlMember(Alias = "include_shell_history")]
    public bool IncludeShellHistory { get; set; }

    [YamlMember(Alias = "include_package_manifests")]
    public bool IncludePackageManifests { get; set; }

    [YamlMember(Alias = "include_env_var_names")]
    public bool IncludeEnvVarNames { get; set; }

    [YamlMember(Alias = "include_network_domains")]
    public bool IncludeNetworkDomains { get; set; }

    [YamlMember(Alias = "max_files_per_scan")]
    public int MaxFilesPerScan { get; set; }

    [YamlMember(Alias = "max_file_bytes")]
    public long MaxFileBytes { get; set; }

    [YamlMember(Alias = "store_raw_local_paths")]
    public bool StoreRawLocalPaths { get; set; }

    [YamlMember(Alias = "confidence_policy_path")]
    public string? ConfidencePolicyPath { get; set; }

    [YamlMember(Alias = "require_trusted_binary_paths")]
    public bool RequireTrustedBinaryPaths { get; set; }

    [YamlMember(Alias = "trusted_binary_prefixes")]
    public List<string> TrustedBinaryPrefixes { get; set; } = new();
}
