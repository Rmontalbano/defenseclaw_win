using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Typed view over <c>~/.defenseclaw/config.yaml</c>. Unknown properties are ignored by
/// the deserializer; unknown top-level sections survive verbatim on
/// <see cref="ConfigDocument.UnknownSections"/> so nothing is lost on round-trip.
/// <para>
/// <b>No section, list or dictionary on this model is ever null.</b> A <c>gateway:</c> key with
/// no children is valid YAML that means "null", and YamlDotNet honours that by calling the
/// property setter with <c>null</c> — after the <c>= new()</c> initializer, so the initializer
/// alone does not protect anything. The same goes for <c>connectors:</c>, <c>scan_roots:</c>
/// and a connector entry such as <c>claudecode:</c> with nothing under it. An operator who
/// comments out every child of a section gets exactly this file, and the first consumer to
/// write <c>config.Gateway.TokenEnv</c> used to throw on the watcher thread. Every such setter
/// therefore coalesces null back to a fresh default, and <see cref="Normalize"/> — which
/// <see cref="ConfigStore.Parse"/> calls — repairs the values the setters cannot see (a null
/// entry inside a collection).
/// </para>
/// </summary>
public sealed class DefenseClawConfig
{
    private ClawSection _claw = new();
    private GatewaySection _gateway = new();
    private GuardrailSection _guardrail = new();
    private CiscoAiDefenseSection _ciscoAiDefense = new();
    private LlmSection _llm = new();
    private AiDiscoverySection _aiDiscovery = new();

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
    public ClawSection Claw
    {
        get => _claw;
        set => _claw = value ?? new();
    }

    [YamlMember(Alias = "gateway")]
    public GatewaySection Gateway
    {
        get => _gateway;
        set => _gateway = value ?? new();
    }

    [YamlMember(Alias = "guardrail")]
    public GuardrailSection Guardrail
    {
        get => _guardrail;
        set => _guardrail = value ?? new();
    }

    [YamlMember(Alias = "cisco_ai_defense")]
    public CiscoAiDefenseSection CiscoAiDefense
    {
        get => _ciscoAiDefense;
        set => _ciscoAiDefense = value ?? new();
    }

    [YamlMember(Alias = "llm")]
    public LlmSection Llm
    {
        get => _llm;
        set => _llm = value ?? new();
    }

    [YamlMember(Alias = "ai_discovery")]
    public AiDiscoverySection AiDiscovery
    {
        get => _aiDiscovery;
        set => _aiDiscovery = value ?? new();
    }

    /// <summary>
    /// Repairs what the null-coalescing setters cannot: a <c>null</c> <i>inside</i> a collection
    /// (<c>connectors: { claudecode: }</c>, a bare <c>-</c> list item). Called by
    /// <see cref="ConfigStore.Parse"/> after deserializing; idempotent, and a no-op on a model
    /// built in code. Null entries are replaced by a default settings object in the dictionary
    /// (the key survives — "the operator named this connector" is information) and dropped from
    /// the string lists (an empty item carries none).
    /// </summary>
    internal void Normalize()
    {
        Guardrail.Normalize();
        AiDiscovery.Normalize();
    }
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
    private Dictionary<string, GuardrailConnectorSettings> _connectors = new(StringComparer.OrdinalIgnoreCase);

    [YamlMember(Alias = "connector")]
    public string? Connector { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    [YamlMember(Alias = "scanner_mode")]
    public string? ScannerMode { get; set; }

    [YamlMember(Alias = "detection_strategy_completion")]
    public string? DetectionStrategyCompletion { get; set; }

    /// <summary>
    /// Per-connector settings, keyed by connector name, matched case-insensitively. Never null,
    /// and no value in it is null — see <see cref="DefenseClawConfig"/> for why an empty
    /// <c>connectors:</c> or an empty <c>claudecode:</c> beneath it has to be handled.
    /// <para>
    /// The comparer needs the setter's help: YamlDotNet builds its own dictionary and assigns
    /// it, so the <c>OrdinalIgnoreCase</c> one created by the field initializer is discarded and
    /// a parsed config would otherwise answer <c>TryGetValue("ClaudeCode")</c> with nothing.
    /// If two keys differ only by case, the later one wins.
    /// </para>
    /// </summary>
    [YamlMember(Alias = "connectors")]
    public Dictionary<string, GuardrailConnectorSettings> Connectors
    {
        get => _connectors;
        set => _connectors = value is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : WithCaseInsensitiveKeys(value);
    }

    private static Dictionary<string, GuardrailConnectorSettings> WithCaseInsensitiveKeys(
        Dictionary<string, GuardrailConnectorSettings> source)
    {
        if (ReferenceEquals(source.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            return source;
        }

        var copy = new Dictionary<string, GuardrailConnectorSettings>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>Replaces null connector entries with default settings; see <see cref="DefenseClawConfig.Normalize"/>.</summary>
    internal void Normalize()
    {
        // Snapshot the keys: replacing a value while enumerating the dictionary itself throws.
        foreach (var key in _connectors.Keys.ToArray())
        {
            _connectors[key] ??= new GuardrailConnectorSettings();
        }
    }
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
    private List<string> _scanRoots = new();
    private List<string> _signaturePacks = new();
    private List<string> _disabledSignatureIds = new();
    private List<string> _trustedBinaryPrefixes = new();

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    /// <summary><c>basic</c> or <c>enhanced</c>.</summary>
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }

    [YamlMember(Alias = "scan_interval_min")]
    public int ScanIntervalMin { get; set; }

    [YamlMember(Alias = "process_interval_s")]
    public int ProcessIntervalS { get; set; }

    // The four list properties are never null: `scan_roots:` with no items is YAML null, and
    // YamlDotNet assigns it. See DefenseClawConfig for the full story.
    [YamlMember(Alias = "scan_roots")]
    public List<string> ScanRoots
    {
        get => _scanRoots;
        set => _scanRoots = value ?? new();
    }

    [YamlMember(Alias = "signature_packs")]
    public List<string> SignaturePacks
    {
        get => _signaturePacks;
        set => _signaturePacks = value ?? new();
    }

    [YamlMember(Alias = "allow_workspace_signatures")]
    public bool AllowWorkspaceSignatures { get; set; }

    [YamlMember(Alias = "disabled_signature_ids")]
    public List<string> DisabledSignatureIds
    {
        get => _disabledSignatureIds;
        set => _disabledSignatureIds = value ?? new();
    }

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
    public List<string> TrustedBinaryPrefixes
    {
        get => _trustedBinaryPrefixes;
        set => _trustedBinaryPrefixes = value ?? new();
    }

    /// <summary>
    /// Drops null items from the four string lists (a bare <c>-</c> in the YAML); see
    /// <see cref="DefenseClawConfig.Normalize"/>.
    /// </summary>
    internal void Normalize()
    {
        _ = _scanRoots.RemoveAll(static s => s is null);
        _ = _signaturePacks.RemoveAll(static s => s is null);
        _ = _disabledSignatureIds.RemoveAll(static s => s is null);
        _ = _trustedBinaryPrefixes.RemoveAll(static s => s is null);
    }
}
