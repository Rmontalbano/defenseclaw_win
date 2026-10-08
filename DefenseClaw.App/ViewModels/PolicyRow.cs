using DefenseClaw.Core.Policy;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One row of the Policies table: a policy as <c>policy list</c> printed it, with every cell precomputed (the view binds, it does
/// not decide). Immutable; the panel rebuilds the rows on every read.
/// </summary>
public sealed class PolicyRow
{
    public PolicyRow(PolicySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
    }

    public PolicySummary Summary { get; }

    public string Name => Summary.Name;

    public string Description => Summary.Description;

    public bool IsBuiltIn => Summary.IsBuiltIn;

    public bool IsActive => Summary.IsActive;

    /// <summary>False for a name the app will not put on a command line (see <see cref="PolicySummary.HasSafeName"/>).</summary>
    public bool HasSafeName => Summary.HasSafeName;

    /// <summary>The kind column: "Built-in" (a file the install bundles) or "Custom" (one in the user's policy folder).</summary>
    public string Kind => IsBuiltIn ? "Built-in" : "Custom";

    public string KindTone => IsBuiltIn ? "Neutral" : "Medium";

    /// <summary>The active marker's word; empty on every other row, so the column reads as a mark, not a flag column.</summary>
    public string ActiveText => IsActive ? "Active" : string.Empty;

    public string ActiveTone => IsActive ? "Ok" : "Neutral";

    /// <summary>What a screen reader says for the row.</summary>
    public string AutomationText =>
        $"{Name}, {Kind.ToLowerInvariant()} policy{(IsActive ? ", active" : string.Empty)}{(Description.Length > 0 ? ". " + Description : string.Empty)}";

    public override string ToString() => Name;
}

/// <summary>One label / value line of a policy's detail.</summary>
public sealed record PolicyDetailLine(string Label, string Value);

/// <summary>One titled group of <see cref="PolicyDetailLine"/>s in the inspector ("Admission", "Severity actions", "Guardrail" ...).</summary>
public sealed record PolicyDetailSection(string Title, IReadOnlyList<PolicyDetailLine> Lines);

/// <summary>Turns a parsed <see cref="PolicyDetail"/> into the sections the inspector shows.</summary>
internal static class PolicyDetailSections
{
    public static IReadOnlyList<PolicyDetailSection> Build(PolicyDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var sections = new List<PolicyDetailSection>();

        var admission = new List<PolicyDetailLine>();
        if (detail.ScanOnInstall is { } scan)
        {
            admission.Add(new PolicyDetailLine("Scan on install", OnOff(scan)));
        }

        if (detail.AllowListBypassScan is { } bypass)
        {
            admission.Add(new PolicyDetailLine("Allow-listed items skip the scan", OnOff(bypass)));
        }

        Add(sections, "Admission", admission);

        Add(
            sections,
            "Severity actions",
            PolicyDetail.Severities.Concat(detail.SeverityActions.Keys).Distinct(StringComparer.Ordinal)
                .Where(detail.SeverityActions.ContainsKey)
                .Select(s => new PolicyDetailLine(s, detail.SeverityActions[s].ToString()))
                .ToList());

        foreach (var scanner in detail.ScannerOverrides.Where(o => o.Value.Count > 0))
        {
            Add(
                sections,
                $"Scanner override: {scanner.Key}",
                scanner.Value.Select(o => new PolicyDetailLine(o.Key, o.Value.ToString())).ToList());
        }

        if (detail.Guardrail is { } g)
        {
            var lines = new List<PolicyDetailLine>();
            if (g.BlockThreshold is { } block)
            {
                lines.Add(new PolicyDetailLine("Block threshold", RankText(block)));
            }

            if (g.AlertThreshold is { } alert)
            {
                lines.Add(new PolicyDetailLine("Alert threshold", RankText(alert)));
            }

            if (g.HiltEnabled is { } hilt)
            {
                lines.Add(new PolicyDetailLine("Human approval (HILT)", hilt ? $"on, from {g.HiltMinSeverity ?? "HIGH"}" : "off"));
            }

            if (g.CiscoTrustLevel is { Length: > 0 } trust)
            {
                lines.Add(new PolicyDetailLine("Cisco trust level", trust));
            }

            lines.AddRange(g.PatternCounts.Select(p => new PolicyDetailLine($"Patterns: {p.Key}", p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            lines.AddRange(g.SeverityMappings.Select(m => new PolicyDetailLine($"{m.Key} counts as", m.Value)));
            Add(sections, "Guardrail", lines);
        }

        if (detail.Firewall is { } f)
        {
            var lines = new List<PolicyDetailLine>();
            if (f.DefaultAction is { Length: > 0 } action)
            {
                lines.Add(new PolicyDetailLine("Default action", action));
            }

            if (f.BlockedDestinationCount is { } blocked)
            {
                lines.Add(new PolicyDetailLine("Blocked destinations", $"{blocked} entries"));
            }

            if (f.AllowedDomainCount is { } domains)
            {
                lines.Add(new PolicyDetailLine("Allowed domains", $"{domains} entries"));
            }

            lines.Add(new PolicyDetailLine("Allowed ports", f.AllowedPorts.Count == 0 ? "none" : string.Join(", ", f.AllowedPorts)));
            Add(sections, "Firewall", lines);
        }

        var other = new List<PolicyDetailLine>();
        if (detail.EnforcementDelaySeconds is { } delay)
        {
            other.Add(new PolicyDetailLine("Max enforcement delay", $"{delay} s"));
        }

        if (detail.AuditRetentionDays is { } days)
        {
            other.Add(new PolicyDetailLine("Audit retention", $"{days} days"));
        }

        Add(sections, "Enforcement and audit", other);
        return sections;
    }

    private static void Add(List<PolicyDetailSection> sections, string title, IReadOnlyList<PolicyDetailLine> lines)
    {
        if (lines.Count > 0)
        {
            sections.Add(new PolicyDetailSection(title, lines));
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string RankText(int rank) => rank switch
    {
        1 => "LOW and above (1)",
        2 => "MEDIUM and above (2)",
        3 => "HIGH and above (3)",
        4 => "CRITICAL only (4)",
        _ => rank.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
