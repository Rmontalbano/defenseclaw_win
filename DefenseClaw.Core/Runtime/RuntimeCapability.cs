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

    /// <summary>Configuration schema 8, the canonical observability configuration.</summary>
    CanonicalSchema8,

    /// <summary>The larger command registry: the connectors and runtime planes added since 0.8.10 (232 entries on Windows at the pin, against 231).</summary>
    TuiRegistry,
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
        RuntimeCapability.Sandbox => "Sandboxes",
        RuntimeCapability.CanonicalSchema8 => "Configuration schema 8",
        RuntimeCapability.TuiRegistry => "Extended command registry",
        _ => capability.ToString(),
    };

    /// <summary>The marker the probe looks for, in a sentence (About's tooltip and the docs).</summary>
    public static string Marker(RuntimeCapability capability) => capability switch
    {
        RuntimeCapability.PolicyModel => "'guardrail --help' lists 'protection'",
        RuntimeCapability.AcpGuard => "'acp --help' lists 'setup' and 'adopt'",
        RuntimeCapability.RedactionAdvanced => "'setup --help' lists 'redaction' and 'setup redaction --help' lists its bucket, defaults, destination, profile and route groups",
        RuntimeCapability.Sandbox => "'sandbox --help' lists 'pack', 'approvals' and 'doctor'",
        RuntimeCapability.CanonicalSchema8 => "'config --help' lists 'get' and the runtime reports version 1.0.0 or later",
        RuntimeCapability.TuiRegistry => "'setup --help' lists 'amp', 'devin' and 'kiro'",
        _ => string.Empty,
    };
}
