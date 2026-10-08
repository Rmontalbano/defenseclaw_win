using System.Globalization;

namespace DefenseClaw.Core.Policy;

/// <summary>The four <c>policy edit</c> subcommands of 0.8.10 (<c>edit actions|scanner|guardrail|firewall</c>).</summary>
public enum PolicyEditSection
{
    /// <summary><c>edit actions</c>: the global install / file / runtime action of one severity.</summary>
    Actions,

    /// <summary><c>edit scanner</c>: a per-scanner-type override of one severity (or its removal).</summary>
    Scanner,

    /// <summary><c>edit guardrail</c>: thresholds, trust level, one pattern added or removed, one severity mapping.</summary>
    Guardrail,

    /// <summary><c>edit firewall</c>: default action and one domain / blocked destination / port added or removed.</summary>
    Firewall,
}

/// <summary>
/// One <c>defenseclaw policy edit SECTION ...</c> command, as the form builds it. <c>edit</c> is not interactive in 0.8.10 (every
/// value is a flag, there is no prompt), so the app builds the argv itself and shows it for review; the edited policy is named with
/// <c>--policy-name</c> so the command never falls back to "the active policy" by accident. A member left null or empty is a flag not
/// passed. <see cref="Validate"/> says what is wrong with the request (nothing to change, a value the CLI would reject or misread);
/// <see cref="ToArgv"/> must only be called once it returns null.
/// </summary>
public sealed record PolicyEdit
{
    public static readonly IReadOnlyList<string> Severities = new[] { "critical", "high", "medium", "low", "info" };
    public static readonly IReadOnlyList<string> RuntimeChoices = new[] { "disable", "enable" };
    public static readonly IReadOnlyList<string> FileChoices = new[] { "quarantine", "none" };
    public static readonly IReadOnlyList<string> InstallChoices = new[] { "block", "allow", "none" };
    public static readonly IReadOnlyList<string> ScannerTypes = new[] { "skill", "mcp", "plugin" };
    public static readonly IReadOnlyList<string> TrustLevels = new[] { "full", "advisory", "none" };
    public static readonly IReadOnlyList<string> FirewallActions = new[] { "allow", "deny" };

    /// <summary>The longest free-text value (pattern, domain, destination) the app will put on a command line.</summary>
    public const int MaxValueLength = 200;

    public required PolicyEditSection Section { get; init; }

    // actions / scanner
    public string Severity { get; init; } = string.Empty;
    public string ScannerType { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public string Install { get; init; } = string.Empty;
    public bool RemoveOverride { get; init; }

    // guardrail
    public int? BlockThreshold { get; init; }
    public int? AlertThreshold { get; init; }
    public string CiscoTrustLevel { get; init; } = string.Empty;
    public string AddPatternCategory { get; init; } = string.Empty;
    public string AddPattern { get; init; } = string.Empty;
    public string RemovePatternCategory { get; init; } = string.Empty;
    public string RemovePattern { get; init; } = string.Empty;
    public string MappingCategory { get; init; } = string.Empty;
    public string MappingSeverity { get; init; } = string.Empty;

    // firewall
    public string DefaultAction { get; init; } = string.Empty;
    public string AddDomain { get; init; } = string.Empty;
    public string RemoveDomain { get; init; } = string.Empty;
    public string AddBlocked { get; init; } = string.Empty;
    public string RemoveBlocked { get; init; } = string.Empty;
    public int? AddPort { get; init; }
    public int? RemovePort { get; init; }

    /// <summary>What is wrong with this request, in a sentence; null when it is ready to review.</summary>
    public string? Validate()
    {
        switch (Section)
        {
            case PolicyEditSection.Actions:
                return Check(Severity, Severities, "severity", required: true)
                       ?? Check(Runtime, RuntimeChoices, "runtime")
                       ?? Check(File, FileChoices, "file action")
                       ?? Check(Install, InstallChoices, "install action")
                       ?? (Runtime.Length + File.Length + Install.Length == 0 ? "Pick at least one action to change." : null);

            case PolicyEditSection.Scanner:
                return Check(ScannerType, ScannerTypes, "scanner type", required: true)
                       ?? Check(Severity, Severities, "severity", required: true)
                       ?? Check(Runtime, RuntimeChoices, "runtime")
                       ?? Check(File, FileChoices, "file action")
                       ?? Check(Install, InstallChoices, "install action")
                       ?? (!RemoveOverride && Runtime.Length + File.Length + Install.Length == 0
                           ? "Pick at least one action to set, or choose to remove the override."
                           : null);

            case PolicyEditSection.Guardrail:
                return CheckRank(BlockThreshold, "block threshold")
                       ?? CheckRank(AlertThreshold, "alert threshold")
                       ?? Check(CiscoTrustLevel, TrustLevels, "Cisco trust level")
                       ?? CheckPair(AddPatternCategory, AddPattern, "pattern to add")
                       ?? CheckPair(RemovePatternCategory, RemovePattern, "pattern to remove")
                       ?? CheckMapping()
                       ?? (Nothing(BlockThreshold, AlertThreshold, CiscoTrustLevel, AddPattern, RemovePattern, MappingCategory)
                           ? "Set at least one guardrail value to change."
                           : null);

            case PolicyEditSection.Firewall:
                return Check(DefaultAction, FirewallActions, "default action")
                       ?? CheckValue(AddDomain, "domain to allow")
                       ?? CheckValue(RemoveDomain, "domain to remove")
                       ?? CheckValue(AddBlocked, "destination to block")
                       ?? CheckValue(RemoveBlocked, "destination to unblock")
                       ?? CheckPort(AddPort, "port to allow")
                       ?? CheckPort(RemovePort, "port to remove")
                       ?? (Nothing(DefaultAction, AddDomain, RemoveDomain, AddBlocked, RemoveBlocked, AddPort, RemovePort)
                           ? "Set at least one firewall value to change."
                           : null);

            default:
                return "Unknown section.";
        }
    }

    /// <summary>The arguments after <c>defenseclaw</c>. Only call when <see cref="Validate"/> returned null.</summary>
    public IReadOnlyList<string> ToArgv(string policyName)
    {
        if (!PolicyNames.IsSafe(policyName))
        {
            throw new ArgumentException("Not a policy name the CLI can be handed.", nameof(policyName));
        }

        var argv = new List<string> { "policy", "edit", Section.ToString().ToLowerInvariant() };
        switch (Section)
        {
            case PolicyEditSection.Actions:
                Add(argv, "--severity", Severity);
                Add(argv, "--runtime", Runtime);
                Add(argv, "--file", File);
                Add(argv, "--install", Install);
                break;

            case PolicyEditSection.Scanner:
                Add(argv, "--type", ScannerType);
                Add(argv, "--severity", Severity);
                if (RemoveOverride)
                {
                    argv.Add("--remove");
                }
                else
                {
                    Add(argv, "--runtime", Runtime);
                    Add(argv, "--file", File);
                    Add(argv, "--install", Install);
                }

                break;

            case PolicyEditSection.Guardrail:
                Add(argv, "--block-threshold", BlockThreshold?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(argv, "--alert-threshold", AlertThreshold?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(argv, "--cisco-trust-level", CiscoTrustLevel);
                if (AddPattern.Length > 0)
                {
                    argv.AddRange(new[] { "--add-pattern", AddPatternCategory, AddPattern });
                }

                if (RemovePattern.Length > 0)
                {
                    argv.AddRange(new[] { "--remove-pattern", RemovePatternCategory, RemovePattern });
                }

                if (MappingCategory.Length > 0)
                {
                    argv.AddRange(new[] { "--set-severity-mapping", MappingCategory, MappingSeverity.ToUpperInvariant() });
                }

                break;

            case PolicyEditSection.Firewall:
                Add(argv, "--default-action", DefaultAction);
                Add(argv, "--add-domain", AddDomain);
                Add(argv, "--remove-domain", RemoveDomain);
                Add(argv, "--add-blocked", AddBlocked);
                Add(argv, "--remove-blocked", RemoveBlocked);
                Add(argv, "--add-port", AddPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(argv, "--remove-port", RemovePort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }

        argv.AddRange(new[] { "--policy-name", policyName });
        return argv;
    }

    /// <summary>
    /// The policy as it would read after this edit, from what <c>policy show</c> printed before it. Counts stand in for the lists
    /// <c>show</c> does not print (a pattern added is one more in its category; a domain removed is one fewer), so the result is an
    /// estimate that is exact for what the review needs: which way each protection moves.
    /// </summary>
    public PolicyDetail ApplyTo(PolicyDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var actions = new Dictionary<string, PolicySeverityAction>(detail.SeverityActions, StringComparer.Ordinal);
        var overrides = detail.ScannerOverrides.ToDictionary(
            p => p.Key,
            p => (IReadOnlyDictionary<string, PolicySeverityAction>)new Dictionary<string, PolicySeverityAction>(p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var guardrail = detail.Guardrail;
        var firewall = detail.Firewall;

        switch (Section)
        {
            case PolicyEditSection.Actions:
                actions[Severity.ToUpperInvariant()] = Merge(detail.ActionFor(Severity), Install, File, Runtime);
                break;

            case PolicyEditSection.Scanner:
            {
                var key = Severity.ToUpperInvariant();
                if (RemoveOverride)
                {
                    if (overrides.TryGetValue(ScannerType, out var existing))
                    {
                        var copy = new Dictionary<string, PolicySeverityAction>(existing, StringComparer.Ordinal);
                        _ = copy.Remove(key);
                        if (copy.Count == 0)
                        {
                            _ = overrides.Remove(ScannerType);
                        }
                        else
                        {
                            overrides[ScannerType] = copy;
                        }
                    }
                }
                else
                {
                    var copy = overrides.TryGetValue(ScannerType, out var existing)
                        ? new Dictionary<string, PolicySeverityAction>(existing, StringComparer.Ordinal)
                        : new Dictionary<string, PolicySeverityAction>(StringComparer.Ordinal);

                    // A new override starts from the CLI's own seed: runtime allow, file none, install none.
                    var start = copy.TryGetValue(key, out var current) ? current : new PolicySeverityAction("none", "none", "allow");
                    copy[key] = Merge(start, Install, File, Runtime);
                    overrides[ScannerType] = copy;
                }

                break;
            }

            case PolicyEditSection.Guardrail:
            {
                var g = guardrail ?? new PolicyGuardrail(null, null, null, null, null, new Dictionary<string, int>(), new Dictionary<string, string>());
                var patterns = new Dictionary<string, int>(g.PatternCounts, StringComparer.Ordinal);
                var mappings = new Dictionary<string, string>(g.SeverityMappings, StringComparer.Ordinal);
                if (AddPattern.Length > 0)
                {
                    patterns[AddPatternCategory] = (patterns.TryGetValue(AddPatternCategory, out var n) ? n : 0) + 1;
                }

                if (RemovePattern.Length > 0 && patterns.TryGetValue(RemovePatternCategory, out var count))
                {
                    patterns[RemovePatternCategory] = Math.Max(0, count - 1);
                }

                if (MappingCategory.Length > 0)
                {
                    mappings[MappingCategory] = MappingSeverity.ToUpperInvariant();
                }

                guardrail = g with
                {
                    BlockThreshold = BlockThreshold ?? g.BlockThreshold,
                    AlertThreshold = AlertThreshold ?? g.AlertThreshold,
                    CiscoTrustLevel = CiscoTrustLevel.Length > 0 ? CiscoTrustLevel : g.CiscoTrustLevel,
                    PatternCounts = patterns,
                    SeverityMappings = mappings,
                };
                break;
            }

            case PolicyEditSection.Firewall:
            {
                var f = firewall ?? new PolicyFirewall(null, null, null, Array.Empty<int>());
                var ports = f.AllowedPorts.ToList();
                if (AddPort is { } add && !ports.Contains(add))
                {
                    ports.Add(add);
                }

                if (RemovePort is { } remove)
                {
                    _ = ports.Remove(remove);
                }

                firewall = f with
                {
                    DefaultAction = DefaultAction.Length > 0 ? DefaultAction : f.DefaultAction,
                    AllowedDomainCount = Shift(f.AllowedDomainCount, AddDomain.Length > 0, RemoveDomain.Length > 0),
                    BlockedDestinationCount = Shift(f.BlockedDestinationCount, AddBlocked.Length > 0, RemoveBlocked.Length > 0),
                    AllowedPorts = ports,
                };
                break;
            }
        }

        return detail with { SeverityActions = actions, ScannerOverrides = overrides, Guardrail = guardrail, Firewall = firewall };
    }

    private static int? Shift(int? count, bool added, bool removed)
    {
        if (count is not { } value)
        {
            return null;
        }

        return Math.Max(0, value + (added ? 1 : 0) - (removed ? 1 : 0));
    }

    private static PolicySeverityAction Merge(PolicySeverityAction start, string install, string file, string runtime) =>
        new(install.Length > 0 ? install : start.Install, file.Length > 0 ? file : start.File, runtime.Length > 0 ? runtime : start.Runtime);

    private static void Add(List<string> argv, string flag, string value)
    {
        if (value.Length > 0)
        {
            argv.Add(flag);
            argv.Add(value);
        }
    }

    private static bool Nothing(params object?[] values) =>
        values.All(v => v is null || (v is string s && s.Length == 0));

    private static string? Check(string value, IReadOnlyList<string> choices, string what, bool required = false)
    {
        if (value.Length == 0)
        {
            return required ? $"Pick a {what}." : null;
        }

        return choices.Contains(value, StringComparer.Ordinal) ? null : $"'{value}' is not a {what} the CLI accepts.";
    }

    private static string? CheckRank(int? value, string what) =>
        value is null or (>= 1 and <= 4) ? null : $"The {what} is a severity rank from 1 (LOW) to 4 (CRITICAL).";

    private static string? CheckPort(int? value, string what) =>
        value is null or (>= 1 and <= 65535) ? null : $"The {what} must be between 1 and 65535.";

    /// <summary>A free-text value: printable, no leading dash (it would read as an option), bounded.</summary>
    private static string? CheckValue(string value, string what)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length > MaxValueLength)
        {
            return $"The {what} is longer than {MaxValueLength.ToString(CultureInfo.InvariantCulture)} characters.";
        }

        if (value.StartsWith('-') || value != value.Trim() || value.Any(char.IsControl))
        {
            return $"The {what} cannot start with '-', have spaces at either end or contain control characters.";
        }

        return null;
    }

    private static string? CheckPair(string category, string value, string what)
    {
        if (value.Length == 0 && category.Length == 0)
        {
            return null;
        }

        if (!PolicyNames.IsSafe(category))
        {
            return $"Name the category of the {what} (letters, digits, '.', '_' or '-').";
        }

        return value.Length == 0 ? $"Enter the {what}." : CheckValue(value, what);
    }

    private string? CheckMapping()
    {
        if (MappingCategory.Length == 0 && MappingSeverity.Length == 0)
        {
            return null;
        }

        if (!PolicyNames.IsSafe(MappingCategory))
        {
            return "Name the category of the severity mapping (letters, digits, '.', '_' or '-').";
        }

        return Check(MappingSeverity.ToLowerInvariant(), Severities, "severity", required: true);
    }
}
