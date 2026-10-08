using System.Globalization;

namespace DefenseClaw.Core.Policy;

/// <summary>One thing that differs between two policies, in a sentence, and whether it makes the install less protected.</summary>
/// <param name="Area">The section it belongs to: "Severity actions", "Guardrail", "Firewall" ...</param>
/// <param name="Text">What changes, with both values.</param>
/// <param name="Weakens">True when the new value protects less than the old one.</param>
public sealed record PolicyChangeLine(string Area, string Text, bool Weakens);

/// <summary>
/// What putting one policy in place of another changes. <see cref="Weakens"/> is what decides whether the review asks for an
/// acknowledgement: it is true when any line weakens protection, and also when the comparison could not be made
/// (<see cref="IsComplete"/> false), because "we could not tell" must not read as "nothing changes".
/// </summary>
public sealed record PolicySwitchSummary(IReadOnlyList<PolicyChangeLine> Lines, bool IsComplete)
{
    /// <summary>The lines that reduce protection.</summary>
    public IReadOnlyList<PolicyChangeLine> WeakeningLines => Lines.Where(l => l.Weakens).ToArray();

    /// <summary>True when the switch reduces protection, or when that could not be ruled out.</summary>
    public bool Weakens => !IsComplete || Lines.Any(l => l.Weakens);

    /// <summary>True when the two policies read the same in every section this app can see.</summary>
    public bool IsEmpty => IsComplete && Lines.Count == 0;
}

/// <summary>
/// Compares two policies the way the review needs: which differences protect less. Everything it knows comes from what
/// <c>policy show</c> prints (see <see cref="PolicyTextParser"/>), so it follows what <c>_sync_opa_data</c> in 0.8.10 does with a
/// policy: the severity actions and scanner overrides are replaced as a whole on activation, while the guardrail, firewall,
/// enforcement and audit sections only overwrite the keys the policy file carries. <c>show</c> prints a default for a key the file
/// leaves out, so a value that merely reads as a default can be reported as a change that activation would not make - the
/// error is always towards asking, never towards staying quiet.
/// </summary>
public static class PolicyDiff
{
    private static readonly string[] Scanners = { "skill", "mcp", "plugin" };

    /// <summary>The severity names a guardrail threshold counts in (rank 1 to 4).</summary>
    private static readonly string[] RankNames = { "INFO", "LOW", "MEDIUM", "HIGH", "CRITICAL" };

    /// <summary>Compares <paramref name="before"/> (the policy in force) with <paramref name="after"/> (the one that would be).</summary>
    /// <param name="before">The active policy; null when it could not be read, which makes the summary incomplete.</param>
    /// <param name="after">The policy that would replace it, or the same policy as it would read after an edit.</param>
    public static PolicySwitchSummary Compare(PolicyDetail? before, PolicyDetail after)
    {
        ArgumentNullException.ThrowIfNull(after);

        if (before is null)
        {
            return Incomplete("The policy in force could not be read, so what this changes cannot be compared. Assume it can reduce protection.");
        }

        var lines = new List<PolicyChangeLine>();
        Admission(before, after, lines);
        Actions(before, after, lines);
        Overrides(before, after, lines);
        Guardrail(before.Guardrail, after.Guardrail, lines);
        Firewall(before.Firewall, after.Firewall, lines);

        if (before.EnforcementDelaySeconds is { } oldDelay && after.EnforcementDelaySeconds is { } newDelay && oldDelay != newDelay)
        {
            lines.Add(new PolicyChangeLine("Enforcement", $"Enforcement may lag by up to {Number(oldDelay)} s -> {Number(newDelay)} s", newDelay > oldDelay));
        }

        if (before.AuditRetentionDays is { } oldDays && after.AuditRetentionDays is { } newDays && oldDays != newDays)
        {
            lines.Add(new PolicyChangeLine("Audit", $"Audit retention {Number(oldDays)} days -> {Number(newDays)} days", newDays < oldDays));
        }

        return new PolicySwitchSummary(lines, IsComplete: true);
    }

    /// <summary>A summary for a comparison that could not be made: one line saying why, and <see cref="PolicySwitchSummary.Weakens"/> true.</summary>
    public static PolicySwitchSummary Incomplete(string reason) =>
        new(new[] { new PolicyChangeLine("Comparison", reason, Weakens: true) }, IsComplete: false);

    private static void Admission(PolicyDetail before, PolicyDetail after, List<PolicyChangeLine> lines)
    {
        if (before.ScanOnInstall is { } oldScan && after.ScanOnInstall is { } newScan && oldScan != newScan)
        {
            lines.Add(new PolicyChangeLine("Admission", $"Scan on install {Word(oldScan)} -> {Word(newScan)}", Weakens: oldScan && !newScan));
        }

        if (before.AllowListBypassScan is { } oldBypass && after.AllowListBypassScan is { } newBypass && oldBypass != newBypass)
        {
            lines.Add(new PolicyChangeLine("Admission", $"Allow-listed items skip the scan {Word(oldBypass)} -> {Word(newBypass)}", Weakens: !oldBypass && newBypass));
        }
    }

    private static void Actions(PolicyDetail before, PolicyDetail after, List<PolicyChangeLine> lines)
    {
        var severities = PolicyDetail.Severities
            .Concat(before.SeverityActions.Keys)
            .Concat(after.SeverityActions.Keys)
            .Distinct(StringComparer.Ordinal);
        foreach (var severity in severities)
        {
            var line = ActionChange("Severity actions", severity, before.ActionFor(severity), after.ActionFor(severity));
            if (line is not null)
            {
                lines.Add(line);
            }
        }
    }

    private static void Overrides(PolicyDetail before, PolicyDetail after, List<PolicyChangeLine> lines)
    {
        var scanners = Scanners.Concat(before.ScannerOverrides.Keys).Concat(after.ScannerOverrides.Keys).Distinct(StringComparer.Ordinal);
        foreach (var scanner in scanners)
        {
            var severities = new HashSet<string>(StringComparer.Ordinal);
            if (before.ScannerOverrides.TryGetValue(scanner, out var oldBySeverity))
            {
                severities.UnionWith(oldBySeverity.Keys);
            }

            if (after.ScannerOverrides.TryGetValue(scanner, out var newBySeverity))
            {
                severities.UnionWith(newBySeverity.Keys);
            }

            foreach (var severity in PolicyDetail.Severities.Concat(severities).Distinct(StringComparer.Ordinal).Where(severities.Contains))
            {
                var line = ActionChange("Scanner overrides", $"{scanner} {severity}", before.EffectiveActionFor(scanner, severity), after.EffectiveActionFor(scanner, severity));
                if (line is not null)
                {
                    lines.Add(line);
                }
            }
        }
    }

    private static PolicyChangeLine? ActionChange(string area, string label, PolicySeverityAction oldAction, PolicySeverityAction newAction)
    {
        var parts = new List<string>();
        var weaker = false;

        if (!string.Equals(oldAction.Install, newAction.Install, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"install {oldAction.Install} -> {newAction.Install}");
            weaker |= InstallRank(newAction.Install) < InstallRank(oldAction.Install);
        }

        if (!string.Equals(oldAction.File, newAction.File, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"file {oldAction.File} -> {newAction.File}");
            weaker |= FileRank(newAction.File) < FileRank(oldAction.File);
        }

        if (oldAction.RuntimeEffect != newAction.RuntimeEffect)
        {
            parts.Add($"runtime {oldAction.Runtime} -> {newAction.Runtime}");
            weaker |= newAction.RuntimeEffect == "allow";
        }

        return parts.Count == 0 ? null : new PolicyChangeLine(area, $"{label}: {string.Join(", ", parts)}", weaker);
    }

    private static void Guardrail(PolicyGuardrail? before, PolicyGuardrail? after, List<PolicyChangeLine> lines)
    {
        if (after is null)
        {
            return;
        }

        if (before is null)
        {
            // Nothing to compare with: the section is new to the comparison, not known to be weaker.
            lines.Add(new PolicyChangeLine("Guardrail", "The policy sets guardrail thresholds the active policy does not show", Weakens: false));
            return;
        }

        Threshold("block threshold", before.BlockThreshold, after.BlockThreshold, lines);
        Threshold("alert threshold", before.AlertThreshold, after.AlertThreshold, lines);

        if (before.HiltEnabled is { } oldHilt && after.HiltEnabled is { } newHilt && oldHilt != newHilt)
        {
            lines.Add(new PolicyChangeLine("Guardrail", $"Human approval (HILT) {Word(oldHilt)} -> {Word(newHilt)}", Weakens: oldHilt && !newHilt));
        }

        if (before.HiltEnabled != false && after.HiltEnabled != false
            && before.HiltMinSeverity is { } oldMin && after.HiltMinSeverity is { } newMin
            && !string.Equals(oldMin, newMin, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(new PolicyChangeLine("Guardrail", $"Human approval from severity {oldMin} -> {newMin}", Weakens: SeverityRank(newMin) > SeverityRank(oldMin)));
        }

        if (before.CiscoTrustLevel is { } oldTrust && after.CiscoTrustLevel is { } newTrust && !string.Equals(oldTrust, newTrust, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(new PolicyChangeLine("Guardrail", $"Cisco trust level {oldTrust} -> {newTrust}", Weakens: TrustRank(newTrust) < TrustRank(oldTrust)));
        }

        foreach (var category in before.PatternCounts.Keys.Concat(after.PatternCounts.Keys).Distinct(StringComparer.Ordinal))
        {
            var oldCount = before.PatternCounts.TryGetValue(category, out var o) ? o : 0;
            var newCount = after.PatternCounts.TryGetValue(category, out var n) ? n : 0;
            if (oldCount != newCount)
            {
                lines.Add(new PolicyChangeLine("Guardrail", $"{Title(category)} patterns {Number(oldCount)} -> {Number(newCount)}", Weakens: newCount < oldCount));
            }
        }

        foreach (var category in before.SeverityMappings.Keys.Concat(after.SeverityMappings.Keys).Distinct(StringComparer.Ordinal))
        {
            var hadOld = before.SeverityMappings.TryGetValue(category, out var oldSeverity);
            var hasNew = after.SeverityMappings.TryGetValue(category, out var newSeverity);
            if (hadOld && hasNew && !string.Equals(oldSeverity, newSeverity, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(new PolicyChangeLine("Guardrail", $"{Title(category)} findings count as {oldSeverity} -> {newSeverity}", Weakens: SeverityRank(newSeverity!) < SeverityRank(oldSeverity!)));
            }
            else if (hadOld && !hasNew)
            {
                lines.Add(new PolicyChangeLine("Guardrail", $"{Title(category)} findings no longer map to {oldSeverity}", Weakens: true));
            }
            else if (!hadOld && hasNew)
            {
                lines.Add(new PolicyChangeLine("Guardrail", $"{Title(category)} findings now count as {newSeverity}", Weakens: false));
            }
        }
    }

    private static void Threshold(string label, int? oldRank, int? newRank, List<PolicyChangeLine> lines)
    {
        if (oldRank is { } oldValue && newRank is { } newValue && oldValue != newValue)
        {
            lines.Add(new PolicyChangeLine("Guardrail", $"Guardrail {label} {RankText(oldValue)} -> {RankText(newValue)}", Weakens: newValue > oldValue));
        }
    }

    private static void Firewall(PolicyFirewall? before, PolicyFirewall? after, List<PolicyChangeLine> lines)
    {
        if (after is null)
        {
            return;
        }

        if (before is null)
        {
            lines.Add(new PolicyChangeLine("Firewall", "The policy sets firewall rules the active policy does not show", Weakens: false));
            return;
        }

        if (before.DefaultAction is { } oldAction && after.DefaultAction is { } newAction && !string.Equals(oldAction, newAction, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(new PolicyChangeLine("Firewall", $"Default egress action {oldAction} -> {newAction}", Weakens: string.Equals(newAction, "allow", StringComparison.OrdinalIgnoreCase)));
        }

        if (before.BlockedDestinationCount is { } oldBlocked && after.BlockedDestinationCount is { } newBlocked && oldBlocked != newBlocked)
        {
            lines.Add(new PolicyChangeLine("Firewall", $"Blocked destinations {Number(oldBlocked)} -> {Number(newBlocked)}", Weakens: newBlocked < oldBlocked));
        }

        if (before.AllowedDomainCount is { } oldDomains && after.AllowedDomainCount is { } newDomains && oldDomains != newDomains)
        {
            lines.Add(new PolicyChangeLine("Firewall", $"Allowed domains {Number(oldDomains)} -> {Number(newDomains)}", Weakens: newDomains > oldDomains));
        }

        var added = after.AllowedPorts.Except(before.AllowedPorts).OrderBy(p => p).ToArray();
        var removed = before.AllowedPorts.Except(after.AllowedPorts).OrderBy(p => p).ToArray();
        if (added.Length > 0)
        {
            lines.Add(new PolicyChangeLine("Firewall", $"Allows egress on port {string.Join(", ", added.Select(Number))}", Weakens: true));
        }

        if (removed.Length > 0)
        {
            lines.Add(new PolicyChangeLine("Firewall", $"No longer allows egress on port {string.Join(", ", removed.Select(Number))}", Weakens: false));
        }
    }

    private static int InstallRank(string install) => install.Trim().ToLowerInvariant() == "block" ? 2 : 1;

    private static int FileRank(string file) => file.Trim().ToLowerInvariant() == "quarantine" ? 1 : 0;

    private static int TrustRank(string level) => level.Trim().ToLowerInvariant() switch
    {
        "full" => 2,
        "advisory" => 1,
        _ => 0,
    };

    private static int SeverityRank(string severity) => Array.IndexOf(RankNames, severity.Trim().ToUpperInvariant());

    private static string RankText(int rank) =>
        rank is >= 1 and <= 4 ? $"{RankNames[rank]} ({Number(rank)})" : Number(rank);

    private static string Word(bool on) => on ? "on" : "off";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Title(string category) =>
        category.Length == 0 ? category : char.ToUpperInvariant(category[0]) + category[1..];
}
