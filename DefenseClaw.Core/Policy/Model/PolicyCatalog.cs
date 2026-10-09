namespace DefenseClaw.Core.Policy.Model;

// What the Policies model is built from. These are the records of the newer runtime's own policy catalog (defenseclaw.policy_catalog at
// source commit 95159fd: PolicySummary, ConnectorPack, RulePack, ProtectionPack, ScopePosture, RuleFamily, ToolChain), with the same
// fields and the same meanings; see docs/RUNTIME-COMPAT-95159fd.md for where each one comes from on Windows. The runtime's seventh
// view, Sandbox packs, has no record here: it is not shown where sandboxes are unsupported, and they are unsupported on Windows.

/// <summary>One named policy, as <c>policy list --json</c> prints it (the runtime's <c>PolicySummary.to_json()</c>).</summary>
/// <param name="Name">The policy's name.</param>
/// <param name="Description">The one-line description; empty when the policy has none.</param>
/// <param name="IsBuiltIn">True for a policy the install bundles.</param>
/// <param name="IsActive">True for the policy the live OPA data names.</param>
/// <param name="SourcePath">The policy file the runtime read.</param>
/// <param name="BlockAt">The policy's guardrail block threshold for LLM traffic through the guardrail proxy, on the catalog scale (<c>CRITICAL</c>, <c>HIGH+</c> ...). Not the tool-call level.</param>
/// <param name="AlertAt">The alert threshold for LLM traffic, same scale.</param>
/// <param name="InstallBlockAt">The lowest severity at which an install is blocked (<c>HIGH+</c>), or <c>none</c>.</param>
/// <param name="FirewallDefault"><c>deny</c>, <c>allow</c> or empty when the policy does not say.</param>
/// <param name="Hilt">Whether the policy turns human approval on; null when it does not say.</param>
/// <param name="ScannerOverrides">How many per-scanner severity overrides the policy carries.</param>
/// <param name="AddsWebhooks">True when activating it adds the policy's webhooks to the operator's.</param>
/// <param name="SetsCisco">True when activating it changes the Cisco AI Defense settings.</param>
/// <param name="IsEdited">True for a built-in name served from the user's policy folder (an edit saved a copy that shadows the bundled file).</param>
public sealed record NamedPolicy(
    string Name,
    string Description,
    bool IsBuiltIn,
    bool IsActive,
    string SourcePath,
    string BlockAt,
    string AlertAt,
    string InstallBlockAt,
    string FirewallDefault,
    bool? Hilt,
    int ScannerOverrides,
    bool AddsWebhooks,
    bool SetsCisco,
    bool IsEdited);

/// <summary>The rule pack one scope enforces (<c>guardrail list-packs --json</c>: the <c>global</c> block and each <c>connectors</c> row).</summary>
/// <param name="Scope"><c>global</c> or a connector name.</param>
/// <param name="Pack">The pack's name (a preset, a folder name, or <c>protected-NAME</c> for a composed pack).</param>
/// <param name="Folder">The pack's folder.</param>
/// <param name="Source"><c>default</c> (the built-in default), <c>global</c> (the configured global pack) or <c>override</c> (the connector's own).</param>
public sealed record ScopePack(string Scope, string Pack, string Folder, string Source)
{
    /// <summary>True for the global row.</summary>
    public bool IsGlobal => string.Equals(Scope, PolicyScopes.Global, StringComparison.Ordinal);
}

/// <summary>One rule pack the runtime knows (<c>guardrail list-packs --json</c> <c>packs[]</c>).</summary>
/// <param name="Name">The pack's name.</param>
/// <param name="Folder">The pack's folder.</param>
/// <param name="Kind"><c>preset</c> for default / strict / permissive; <c>custom</c> otherwise.</param>
/// <param name="UsedBy">The scopes that enforce it.</param>
public sealed record RulePackEntry(string Name, string Folder, string Kind, IReadOnlyList<string> UsedBy)
{
    public bool IsPreset => string.Equals(Kind, "preset", StringComparison.Ordinal);
}

/// <summary>One rule of an opt-in protection pack, for its detail.</summary>
public sealed record ProtectionRule(string Id, string Severity, string Title);

/// <summary>One opt-in protection pack (<c>guardrail protection list --json</c> <c>packs[]</c>).</summary>
/// <param name="Name">The pack's name (<c>database-destruction-protection</c>).</param>
/// <param name="Title">The pack's title (<c>Database destruction protection</c>).</param>
/// <param name="Summary">What the pack's README says it is.</param>
/// <param name="Covers">A short phrase (at most 40 characters) for a table cell.</param>
/// <param name="RuleCount">How many rules the pack carries.</param>
/// <param name="RuleIds">The rules' ids.</param>
/// <param name="Status"><c>selectable</c>, or <c>staged</c> for a contract that cannot be turned on yet.</param>
/// <param name="Rules">Each rule's id, severity and title.</param>
public sealed record ProtectionPack(
    string Name,
    string Title,
    string Summary,
    string Covers,
    int RuleCount,
    IReadOnlyList<string> RuleIds,
    string Status,
    IReadOnlyList<ProtectionRule> Rules)
{
    /// <summary>True for a pack with no rules the runtime can enforce yet.</summary>
    public bool IsStaged => string.Equals(Status, "staged", StringComparison.Ordinal);
}

/// <summary>
/// One scope's posture: the global default, or one active connector. The tool-call levels are the ones the gateway resolves
/// (<c>policy_catalog.resolve_levels</c>); <see cref="OwnBlockAt"/> and <see cref="OwnAlertAt"/> are what the scope itself sets, so
/// a value that is not set here is shown as inherited.
/// </summary>
/// <param name="Scope"><c>global</c> or a connector name.</param>
/// <param name="Mode"><c>observe</c> or <c>action</c>.</param>
/// <param name="ModeSource"><c>global</c> or <c>override</c> (the connector has its own mode).</param>
/// <param name="Hilt"><c>off</c>, or the lowest severity that asks a person (<c>CRITICAL</c>, <c>HIGH+</c>, <c>MEDIUM+</c>, <c>LOW+</c>).</param>
/// <param name="Pack">The rule pack's name.</param>
/// <param name="PackFolder">The rule pack's folder.</param>
/// <param name="PackSource"><c>default</c>, <c>global</c> or <c>override</c>.</param>
/// <param name="Protection">The opt-in packs layered into the scope's rule pack.</param>
/// <param name="BlockAt">The tool-call block level on the catalog scale (<c>HIGH+</c>).</param>
/// <param name="AlertAt">The tool-call alert level, never above the block level.</param>
/// <param name="LevelsSource"><c>override</c>, <c>global</c> or <c>pack</c>: the more specific source of the two levels.</param>
/// <param name="OwnBlockAt">What the scope sets itself (<c>CRITICAL</c> ... <c>LOW</c>); empty when it inherits. On the global row, the global value.</param>
/// <param name="OwnAlertAt">The same for the alert level.</param>
public sealed record ScopePosture(
    string Scope,
    string Mode,
    string ModeSource,
    string Hilt,
    string Pack,
    string PackFolder,
    string PackSource,
    IReadOnlyList<string> Protection,
    string BlockAt,
    string AlertAt,
    string LevelsSource,
    string OwnBlockAt,
    string OwnAlertAt)
{
    public bool IsGlobal => string.Equals(Scope, PolicyScopes.Global, StringComparison.Ordinal);
}

/// <summary>One rule family of a scope's effective pack (<c>command</c>, <c>secret</c> ...).</summary>
/// <param name="Name">The family (the rule files' <c>category</c>).</param>
/// <param name="Rules">How many rules the family declares.</param>
/// <param name="Enabled">How many of them are enabled.</param>
/// <param name="Description">What the family catches.</param>
public sealed record RuleFamily(string Name, int Rules, int Enabled, string Description);

/// <summary>One bounded tool-call chain from the runtime's built-in catalog (read-only).</summary>
/// <param name="Id">The chain's id (<c>chain.secret_read_then_egress</c>).</param>
/// <param name="Title">What it detects.</param>
/// <param name="Severity">The severity of a match.</param>
/// <param name="Domain"><c>sql</c>, <c>kubernetes</c>, <c>cloud</c>, <c>host</c>, <c>credentials</c>, <c>data-egress</c>, <c>network</c> or <c>security-controls</c>.</param>
/// <param name="CanBlock">True when a match can block; false when it only alerts.</param>
/// <param name="EventWindow">How many of the latest tool calls it looks at.</param>
/// <param name="TimeWindowSeconds">How far back it looks, in seconds.</param>
/// <param name="Requires">What has to hold for the events to join up.</param>
/// <param name="Note">The catalog's note (<c>Alert-only</c>).</param>
public sealed record ToolChain(
    string Id,
    string Title,
    string Severity,
    string Domain,
    bool CanBlock,
    int EventWindow,
    int TimeWindowSeconds,
    IReadOnlyList<string> Requires,
    string Note);

/// <summary>The scope names the catalog uses.</summary>
public static class PolicyScopes
{
    /// <summary>The scope every connector without its own value follows.</summary>
    public const string Global = "global";
}

/// <summary>
/// Everything the seven views show, as one read left it. Each part carries its own error, as the runtime's catalog does: a part that
/// could not be read empties its own view and leaves the others. Immutable.
/// </summary>
public sealed record PolicyCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RuleFamily>> NoFamilies =
        new Dictionary<string, IReadOnlyList<RuleFamily>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> NoBases = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<NamedPolicy> Policies { get; init; } = Array.Empty<NamedPolicy>();

    /// <summary>Why <see cref="Policies"/> could not be read; empty when it was.</summary>
    public string PoliciesError { get; init; } = string.Empty;

    public ScopePack? GlobalPack { get; init; }

    /// <summary>The pack each active connector enforces, in the order the runtime lists them.</summary>
    public IReadOnlyList<ScopePack> ConnectorPacks { get; init; } = Array.Empty<ScopePack>();

    public IReadOnlyList<RulePackEntry> Packs { get; init; } = Array.Empty<RulePackEntry>();

    /// <summary>Why the rule packs could not be read; empty when they were.</summary>
    public string PackError { get; init; } = string.Empty;

    /// <summary>The global scope, then each active connector.</summary>
    public IReadOnlyList<ScopePosture> Postures { get; init; } = Array.Empty<ScopePosture>();

    /// <summary>The opt-in packs, selectable first and staged last.</summary>
    public IReadOnlyList<ProtectionPack> Protection { get; init; } = Array.Empty<ProtectionPack>();

    /// <summary>The rule families of each pack folder in use.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RuleFamily>> Families { get; init; } = NoFamilies;

    public IReadOnlyList<ToolChain> Chains { get; init; } = Array.Empty<ToolChain>();

    /// <summary>For a composed pack folder, the pack it was built on.</summary>
    public IReadOnlyDictionary<string, string> PackBases { get; init; } = NoBases;

    /// <summary>Why the protection settings (postures, opt-in packs) could not be read; empty when they were.</summary>
    public string PostureError { get; init; } = string.Empty;

    /// <summary>True when <c>guardrail.connectors</c> has entries: a change for one connector then names it (<c>--connector X</c>); on a single-connector install it changes the global value.</summary>
    public bool MultiConnector { get; init; }

    /// <summary>The policy folder (where composed packs go), or empty when it is not known.</summary>
    public string PolicyFolder { get; init; } = string.Empty;

    /// <summary>True when the read printed nothing of any part (a catalog that has not been read).</summary>
    public bool IsEmpty =>
        Policies.Count == 0 && GlobalPack is null && Postures.Count == 0 && Protection.Count == 0 && Chains.Count == 0 && Packs.Count == 0;
}
