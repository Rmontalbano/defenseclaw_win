using System.Globalization;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Policy;

/// <summary>
/// Reads the text reports of the 0.8.10 <c>defenseclaw policy list</c> and <c>policy show</c>. Neither has a <c>--json</c> flag (the
/// source, <c>commands/cmd_policy.py</c>, prints with <c>click.echo</c>), so this reads what the commands print, defensively:
/// colour codes are cut, CRLF and LF read alike, a line it does not recognise is skipped rather than failing the page, and a
/// report with nothing recognisable in it is an error with a sentence, not an empty policy.
/// <para>
/// The shapes (checked against the installed 0.8.10 CLI): <c>list</c> prints "Available policies:", then per policy
/// <c>"  * name [built-in] [active]"</c> (the <c>*</c> only on the active one, four spaces otherwise) and the description on the
/// next line indented six; <c>show</c> prints "Policy: NAME", the description, then the sections Admission, Severity Actions,
/// Scanner Overrides, Guardrail, Firewall, Enforcement and Audit as <c>key: value</c> lines.
/// </para>
/// </summary>
public static partial class PolicyTextParser
{
    [GeneratedRegex("\u001b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiCodes();

    [GeneratedRegex(@"^\s+([A-Za-z]+)\s+install=(\S+)\s+file=(\S+)\s+runtime=(\S+)\s*$")]
    private static partial Regex ActionLine();

    [GeneratedRegex(@"^\s+([A-Za-z_]+):\s*(.*)$")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex Integer();

    [GeneratedRegex(@"enabled=(\w+)\s+min=(\S+)")]
    private static partial Regex Hilt();

    /// <summary>Parses the stdout of <c>policy list</c>.</summary>
    /// <param name="text">What the command printed.</param>
    /// <param name="listing">The policies; empty when the output carried none.</param>
    /// <param name="error">Why the output could not be read; empty on success.</param>
    public static bool TryParseList(string? text, out PolicyListing listing, out string error)
    {
        listing = new PolicyListing(Array.Empty<PolicySummary>());
        error = string.Empty;

        var lines = Lines(text);
        if (lines.All(l => l.Trim().Length == 0))
        {
            // The command always prints something; nothing at all is a read that did not happen.
            error = "policy list printed nothing.";
            return false;
        }

        if (lines.Any(l => l.Contains("no policies found", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var policies = new List<PolicySummary>();
        var sawHeading = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Available policies", StringComparison.OrdinalIgnoreCase))
            {
                sawHeading = true;
                continue;
            }

            // The footer: "  Activate a policy: ..." / "  Show details: ..." (two spaces; entries have four or "  * ").
            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal) && !line.StartsWith("  * ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("      ", StringComparison.Ordinal))
            {
                if (policies.Count > 0 && policies[^1].Description.Length == 0)
                {
                    policies[^1] = policies[^1] with { Description = trimmed };
                }

                continue;
            }

            var active = line.StartsWith("  * ", StringComparison.Ordinal);
            if (!active && !(line.StartsWith("    ", StringComparison.Ordinal) && line.Length > 4 && line[4] != ' '))
            {
                continue;
            }

            var name = line[4..].TrimEnd();
            var builtIn = false;
            while (true)
            {
                if (name.EndsWith(" [active]", StringComparison.Ordinal))
                {
                    active = true;
                    name = name[..^" [active]".Length].TrimEnd();
                }
                else if (name.EndsWith(" [built-in]", StringComparison.Ordinal))
                {
                    builtIn = true;
                    name = name[..^" [built-in]".Length].TrimEnd();
                }
                else
                {
                    break;
                }
            }

            if (name.Length > 0)
            {
                policies.Add(new PolicySummary(name, string.Empty, builtIn, active));
            }
        }

        if (policies.Count == 0)
        {
            error = sawHeading
                ? "policy list printed a heading but no policies."
                : "policy list printed something this app does not recognise.";
            return false;
        }

        listing = new PolicyListing(policies);
        return true;
    }

    /// <summary>Parses the stdout of <c>policy show NAME</c>.</summary>
    /// <param name="text">What the command printed.</param>
    /// <param name="detail">The policy; null when the output could not be read.</param>
    /// <param name="error">Why; empty on success.</param>
    public static bool TryParseDetail(string? text, out PolicyDetail? detail, out string error)
    {
        detail = null;
        error = string.Empty;

        var name = string.Empty;
        var description = string.Empty;
        bool? scanOnInstall = null;
        bool? allowListBypass = null;
        var actions = new Dictionary<string, PolicySeverityAction>(StringComparer.Ordinal);
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, PolicySeverityAction>>(StringComparer.Ordinal);
        int? blockThreshold = null, alertThreshold = null;
        bool? hiltEnabled = null;
        string? hiltMin = null, trust = null;
        var patterns = new Dictionary<string, int>(StringComparer.Ordinal);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var sawGuardrail = false;
        string? firewallDefault = null;
        int? blockedCount = null, domainCount = null;
        var ports = new List<int>();
        var sawFirewall = false;
        int? delay = null, retention = null;
        var recognised = false;

        var section = string.Empty;
        string? scannerType = null;
        string? subsection = null;

        foreach (var line in Lines(text))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();

            if (indent == 0)
            {
                subsection = null;
                scannerType = null;
                if (trimmed.StartsWith("Policy:", StringComparison.Ordinal))
                {
                    name = trimmed["Policy:".Length..].Trim();
                    section = "header";
                    recognised = true;
                }
                else if (trimmed.EndsWith(':'))
                {
                    section = trimmed[..^1].Trim().ToLowerInvariant();
                    sawGuardrail |= section == "guardrail";
                    sawFirewall |= section == "firewall";
                    recognised |= section is "admission" or "severity actions" or "scanner overrides" or "guardrail" or "firewall" or "enforcement" or "audit";
                }

                continue;
            }

            switch (section)
            {
                case "header":
                    if (description.Length == 0)
                    {
                        description = trimmed;
                    }

                    break;

                case "admission":
                    if (TryKeyValue(line, out var key, out var value))
                    {
                        if (key == "scan_on_install")
                        {
                            scanOnInstall = ParseBool(value);
                        }
                        else if (key == "allow_list_bypass_scan")
                        {
                            allowListBypass = ParseBool(value);
                        }
                    }

                    break;

                case "severity actions":
                    if (TryAction(line, out var severity, out var action))
                    {
                        actions[severity] = action;
                    }

                    break;

                case "scanner overrides":
                    if (TryAction(line, out var overrideSeverity, out var overrideAction))
                    {
                        if (scannerType is not null)
                        {
                            var map = (Dictionary<string, PolicySeverityAction>)overrides[scannerType];
                            map[overrideSeverity] = overrideAction;
                        }
                    }
                    else if (trimmed.EndsWith(':'))
                    {
                        scannerType = trimmed[..^1].Trim();
                        if (!overrides.ContainsKey(scannerType))
                        {
                            overrides[scannerType] = new Dictionary<string, PolicySeverityAction>(StringComparer.Ordinal);
                        }
                    }

                    break;

                case "guardrail":
                    if (indent >= 4 && subsection is not null && TryKeyValue(line, out var cat, out var catValue))
                    {
                        if (subsection == "patterns")
                        {
                            patterns[cat] = FirstInt(catValue) ?? 0;
                        }
                        else
                        {
                            mappings[cat] = catValue.Trim();
                        }
                    }
                    else if (TryKeyValue(line, out var gKey, out var gValue))
                    {
                        subsection = null;
                        switch (gKey)
                        {
                            case "block_threshold":
                                blockThreshold = FirstInt(gValue);
                                break;
                            case "alert_threshold":
                                alertThreshold = FirstInt(gValue);
                                break;
                            case "cisco_trust_level":
                                trust = gValue.Trim();
                                break;
                            case "hilt":
                                var hilt = Hilt().Match(gValue);
                                if (hilt.Success)
                                {
                                    hiltEnabled = ParseBool(hilt.Groups[1].Value);
                                    hiltMin = hilt.Groups[2].Value;
                                }

                                break;
                            case "patterns":
                            case "severity_mappings":
                                subsection = gKey;
                                break;
                        }
                    }

                    break;

                case "firewall":
                    if (TryKeyValue(line, out var fKey, out var fValue))
                    {
                        switch (fKey)
                        {
                            case "default_action":
                                firewallDefault = fValue.Trim();
                                break;
                            case "blocked_destinations":
                                blockedCount = FirstInt(fValue);
                                break;
                            case "allowed_domains":
                                domainCount = FirstInt(fValue);
                                break;
                            case "allowed_ports":
                                ports.AddRange(Integer().Matches(fValue).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)));
                                break;
                        }
                    }

                    break;

                case "enforcement":
                    if (TryKeyValue(line, out var eKey, out var eValue) && eKey == "max_enforcement_delay_seconds")
                    {
                        delay = FirstInt(eValue);
                    }

                    break;

                case "audit":
                    if (TryKeyValue(line, out var aKey, out var aValue) && aKey == "retention_days")
                    {
                        retention = FirstInt(aValue);
                    }

                    break;
            }
        }

        if (!recognised || name.Length == 0)
        {
            error = "policy show printed something this app does not recognise.";
            return false;
        }

        detail = new PolicyDetail(
            name,
            description,
            scanOnInstall,
            allowListBypass,
            actions,
            overrides,
            sawGuardrail ? new PolicyGuardrail(blockThreshold, alertThreshold, hiltEnabled, hiltMin, trust, patterns, mappings) : null,
            sawFirewall ? new PolicyFirewall(firewallDefault, blockedCount, domainCount, ports) : null,
            delay,
            retention);
        return true;
    }

    private static string[] Lines(string? text) =>
        AnsiCodes().Replace(text ?? string.Empty, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool TryKeyValue(string line, out string key, out string value)
    {
        var match = KeyValue().Match(line);
        key = match.Success ? match.Groups[1].Value : string.Empty;
        value = match.Success ? match.Groups[2].Value : string.Empty;
        return match.Success;
    }

    private static bool TryAction(string line, out string severity, out PolicySeverityAction action)
    {
        var match = ActionLine().Match(line);
        severity = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        action = match.Success
            ? new PolicySeverityAction(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value)
            : PolicySeverityAction.Default;
        return match.Success;
    }

    private static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static int? FirstInt(string value)
    {
        var match = Integer().Match(value);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
