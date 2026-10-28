namespace DefenseClaw.Core.Runtime;

/// <summary>
/// A feature the app shows only when the connected DefenseClaw runtime has it. Every one is something the installed 0.8.10
/// runtime does not have and the verified newer runtime (source commit <see cref="RuntimeCapabilityCatalog.VerifiedCommit"/>) does;
/// a feature 0.8.10 already has is never gated, so nothing here can hide what works today.
/// </summary>
public enum RuntimeCapability
{
    /// <summary>The seven-view policy model: opt-in protection packs, scope postures, rule families and sandbox packs.</summary>
    PolicyModel,

    /// <summary>The Agent Client Protocol guard: <c>acp setup</c>, <c>acp adopt</c> and friends.</summary>
    AcpGuard,

    /// <summary>The advanced redaction editor: <c>setup redaction</c> with its bucket, defaults, destination, profile and route operations.</summary>
    RedactionAdvanced,

    /// <summary>The OpenShell sandbox CLI and the gateway's <c>/api/v1/sandbox/*</c> routes (the runtime says it targets Linux and macOS).</summary>
    Sandbox,

    /// <summary>
    /// The canonical observability configuration (introduced with config schema 8): the app can read and edit it. It says nothing about the
    /// version of the config file on disk (that is <c>config_version</c> in config.yaml, 9 on a 1.0.x machine).
    /// </summary>
    CanonicalSchema8,

    /// <summary>
    /// The larger TUI command registry: the connectors and runtime planes added since 0.8.10 (253 entries at the pin, 232 of them run on
    /// Windows, against 0.8.10's 231 and 210). It is what chooses the command palette's catalogue (<see cref="Cli.TuiRegistryCatalogues.For"/>).
    /// </summary>
    TuiRegistry,

    /// <summary>
    /// The AI Discovery runtime planes: <c>agent discovery runtime</c> (status, scan, findings, permissions, enable, disable) and the
    /// gateway's <c>GET /api/v1/ai-usage/runtime</c>. 0.8.10 has neither the command group nor the route.
    /// </summary>
    AiRuntime,

    /// <summary>
    /// The gateway's read-only correlation ledger (<c>GET /api/v1/correlation/{graph,explain,timeline,conflicts}</c>) and agent directory
    /// (<c>GET /api/v1/agents/identities</c>), which the Agents panel reads. Judged from the runtime's version (1.0.0 or later, the source the panel's
    /// shapes were written from); the panel itself still treats a 404 or 405 from the gateway as "not supported", so a version label alone never
    /// makes it look populated.
    /// </summary>
    CorrelationApi,

    /// <summary>
    /// The gateway's read-only IDE plugin inventory (<c>GET /api/v1/ai-usage/ide-plugins</c>), which the AI BOM's IDE Plugins tab reads, next to the agent
    /// directory (<c>GET /api/v1/agents/identities</c>) its Agent identities tab reads. Both routes arrived in 1.0.0 and the gateway does not register
    /// either on a managed Secure Client host, so the version only decides whether to ask; a 404 or 405 is still "not available on this host".
    /// </summary>
    IdeInventory,
    /// The gateway's administrator view of the policy it applies: <c>GET /api/v1/guardrail/profiles/resolve</c> (the Overview's Effective policy card,
    /// CUST-375). Judged from the runtime's version like <see cref="CorrelationApi"/>; the card itself still treats a 404 or 405 as "not supported".
    /// </summary>
    EffectivePolicy,
}

/// <summary>Names, the verified commit and the one sentence a hidden feature carries.</summary>
public static class RuntimeCapabilityCatalog
{
    /// <summary>The source commit the markers were derived from and checked against.</summary>
    public const string VerifiedCommit = "95159fd";

    /// <summary>
    /// What an unavailable feature says, wherever it says anything: a disabled palette row, a panel's empty state, a Setup tile.
    /// </summary>
    public const string UnsupportedMessage =
        "Requires a compatible DefenseClaw runtime (verified against source commit " + VerifiedCommit + ")";

    /// <summary>Every capability, in the order About lists them.</summary>
    public static IReadOnlyList<RuntimeCapability> All { get; } = Enum.GetValues<RuntimeCapability>();

    /// <summary>The label About shows.</summary>
    public static string DisplayName(RuntimeCapability capability) => capability switch
    {
        RuntimeCapability.PolicyModel => "Policy model (seven views)",
        RuntimeCapability.AcpGuard => "ACP guard",
        RuntimeCapability.RedactionAdvanced => "Advanced redaction",
        RuntimeCapability.Sandbox => "Sandboxes (Linux/macOS only)",
        RuntimeCapability.CanonicalSchema8 => "Canonical observability config editing",
        RuntimeCapability.TuiRegistry => "Extended command registry",
        RuntimeCapability.AiRuntime => "AI Discovery runtime planes",
        RuntimeCapability.CorrelationApi => "Agent view (correlation ledger)",
        RuntimeCapability.IdeInventory => "AI BOM IDE plugins and agent identities",
        RuntimeCapability.EffectivePolicy => "Effective policy (gateway profile resolution)",
        _ => capability.ToString(),
    };

    /// <summary>
    /// Whether the user can use the feature on this machine. The runtime's sandbox commands exist on Windows but the runtime itself says it
    /// targets Linux and macOS, so a summary of "what you have here" leaves them out.
    /// </summary>
    public static bool IsUsableHere(RuntimeCapability capability) =>
        !(capability == RuntimeCapability.Sandbox && OperatingSystem.IsWindows());

    /// <summary>The marker the probe looks for, in a sentence (About's tooltip and the docs).</summary>
    public static string Marker(RuntimeCapability capability) => capability switch
    {
        RuntimeCapability.PolicyModel => "'guardrail --help' lists 'protection'",
        RuntimeCapability.AcpGuard => "'acp --help' lists 'setup' and 'adopt'",
        RuntimeCapability.RedactionAdvanced => "'setup --help' lists 'redaction' and 'setup redaction --help' lists its bucket, defaults, destination, profile and route groups",
        RuntimeCapability.Sandbox => "'sandbox --help' lists 'pack', 'approvals' and 'doctor'",
        RuntimeCapability.CanonicalSchema8 => "'config --help' lists 'get' and the runtime reports version 1.0.0 or later",
        RuntimeCapability.TuiRegistry => "'setup --help' lists 'amp', 'devin' and 'kiro'",
        RuntimeCapability.AiRuntime => "'agent discovery --help' lists 'runtime' and 'agent discovery runtime --help' lists 'status', 'scan', 'findings' and 'permissions'",
        RuntimeCapability.CorrelationApi => "the runtime reports version 1.0.0 or later and lists its commands (the gateway then serves /api/v1/correlation/* and /api/v1/agents/identities)",
        RuntimeCapability.IdeInventory => "the runtime reports version 1.0.0 or later and lists its commands (the gateway then serves /api/v1/ai-usage/ide-plugins and /api/v1/agents/identities, except on a managed host)",
        RuntimeCapability.EffectivePolicy => "the runtime reports version 1.0.0 or later and lists its commands (the gateway then serves /api/v1/guardrail/profiles/resolve)",
        _ => string.Empty,
    };
}
