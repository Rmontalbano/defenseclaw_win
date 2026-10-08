namespace DefenseClaw.Core.Policy;

/// <summary>One line of <c>defenseclaw policy list</c> (0.8.10 <c>cmd_policy.list_policies</c>).</summary>
/// <param name="Name">The policy's name as the list prints it (the YAML's <c>name</c>, else the file stem).</param>
/// <param name="Description">The indented line under the name; empty when the policy has none.</param>
/// <param name="IsBuiltIn">True when the policy file is one the install bundles (the CLI's <c>[built-in]</c> tag).</param>
/// <param name="IsActive">True for the policy the live OPA <c>data.json</c> names (the CLI's <c>[active]</c> tag and <c>*</c> marker).</param>
public sealed record PolicySummary(string Name, string Description, bool IsBuiltIn, bool IsActive)
{
    /// <summary>
    /// True when <see cref="Name"/> is safe to hand to the CLI as a positional argument: letters, digits, <c>.</c> <c>_</c> <c>-</c>, starting
    /// with a letter or digit. A name from outside that is anything else (a path separator, a leading <c>-</c> that would read as an option,
    /// whitespace, a control character) is shown but never put on a command line.
    /// </summary>
    public bool HasSafeName => PolicyNames.IsSafe(Name);
}

/// <summary>What <c>policy list</c> printed.</summary>
public sealed record PolicyListing(IReadOnlyList<PolicySummary> Policies)
{
    /// <summary>The name of the policy marked active; null when none is (a fresh install, or <c>data.json</c> names a policy that is not listed).</summary>
    public string? ActiveName => Policies.FirstOrDefault(p => p.IsActive)?.Name;
}

/// <summary>The three actions a severity can carry: what happens at install, to the files, and at runtime (<c>policy show</c> prints them as <c>install= file= runtime=</c>).</summary>
/// <param name="Install"><c>block</c>, <c>allow</c> or <c>none</c>.</param>
/// <param name="File"><c>quarantine</c> or <c>none</c>.</param>
/// <param name="Runtime"><c>disable</c> / <c>block</c> (do not run) or <c>enable</c> / <c>allow</c> (run), as the policy file spells it.</param>
public sealed record PolicySeverityAction(string Install, string File, string Runtime)
{
    /// <summary>What a severity does when the policy says nothing about it (<c>cmd_policy.show</c>'s defaults).</summary>
    public static PolicySeverityAction Default { get; } = new("none", "none", "enable");

    /// <summary>The runtime action in the OPA vocabulary: <c>disable</c> and <c>block</c> are both "do not run" (<c>_opa_runtime_action</c>).</summary>
    public string RuntimeEffect => Runtime.Trim().ToLowerInvariant() is "disable" or "block" ? "block" : "allow";

    public override string ToString() => $"install={Install}, file={File}, runtime={Runtime}";
}

/// <summary>The guardrail section of a policy as <c>policy show</c> prints it. Null members are lines the output did not carry.</summary>
public sealed record PolicyGuardrail(
    int? BlockThreshold,
    int? AlertThreshold,
    bool? HiltEnabled,
    string? HiltMinSeverity,
    string? CiscoTrustLevel,
    IReadOnlyDictionary<string, int> PatternCounts,
    IReadOnlyDictionary<string, string> SeverityMappings);

/// <summary>The firewall section. <c>show</c> prints counts, not the lists, for domains and blocked destinations.</summary>
public sealed record PolicyFirewall(
    string? DefaultAction,
    int? BlockedDestinationCount,
    int? AllowedDomainCount,
    IReadOnlyList<int> AllowedPorts);

/// <summary>
/// One policy as <c>defenseclaw policy show NAME</c> prints it (a text report: 0.8.10 has no <c>--json</c> for it). Every section is
/// optional because the command prints the sections a policy has, and <see cref="PolicyTextParser"/> reads what it recognises.
/// </summary>
public sealed record PolicyDetail(
    string Name,
    string Description,
    bool? ScanOnInstall,
    bool? AllowListBypassScan,
    IReadOnlyDictionary<string, PolicySeverityAction> SeverityActions,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PolicySeverityAction>> ScannerOverrides,
    PolicyGuardrail? Guardrail,
    PolicyFirewall? Firewall,
    int? EnforcementDelaySeconds,
    int? AuditRetentionDays)
{
    /// <summary>The severities in the order the CLI prints them.</summary>
    public static readonly IReadOnlyList<string> Severities = new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO" };

    /// <summary>The action a severity has globally; the default when the policy does not list it.</summary>
    public PolicySeverityAction ActionFor(string severity) =>
        SeverityActions.TryGetValue(severity.ToUpperInvariant(), out var action) ? action : PolicySeverityAction.Default;

    /// <summary>The action a scanner type gets for a severity: its override if the policy has one, else the global action.</summary>
    public PolicySeverityAction EffectiveActionFor(string scannerType, string severity)
    {
        if (ScannerOverrides.TryGetValue(scannerType, out var bySeverity) && bySeverity.TryGetValue(severity.ToUpperInvariant(), out var overridden))
        {
            return overridden;
        }

        return ActionFor(severity);
    }
}

/// <summary>Policy names as the CLI accepts them, and as this app will put them on a command line.</summary>
public static class PolicyNames
{
    /// <summary>The longest name the app will put on a command line.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The policies the CLI refuses to create over or delete (<c>BUILTIN_POLICIES</c> in 0.8.10 <c>cmd_policy.py</c>). The bundled folder
    /// can hold more (the list shows <c>firewall-deny-default</c> as built-in), but these three are the ones the commands protect.
    /// </summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.Ordinal) { "default", "strict", "permissive" };

    /// <summary>True for a name made of ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c> that starts with a letter or digit.</summary>
    public static bool IsSafe(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        // ".." can never be part of a file stem the CLI would accept (_sanitize_policy_name).
        return !name.Contains("..", StringComparison.Ordinal)
               && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }
}
