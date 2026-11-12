using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// The finding a hook call gets when the LLM judge gave no verdict on a call it had to decide (DefenseClaw 1.0.7 and later; <c>escalatedJudgeFailClosedVerdict</c>,
/// internal/gateway/inspect.go). Under <c>regex_judge</c>, in action mode, a CRITICAL pattern finding the rules cannot enforce on their own waits for the judge;
/// when the judge gives none and the connector's <c>hook_fail_mode</c> is closed, the call is refused with this finding (severity CRITICAL, lane
/// <c>llm-judge</c>). It reaches the database as an ordinary finding row (action <c>scan-finding</c>, bucket <c>security.finding</c>, event
/// <c>finding.observed</c>, <c>defenseclaw.finding.rule_id</c> this id), so the alert queue already lists it; this type only names it.
/// </summary>
public static partial class JudgeEscalationFinding
{
    /// <summary>The rule id (<c>escalatedJudgeUnavailableFinding</c>).</summary>
    public const string RuleId = "JUDGE-UNAVAILABLE-ESCALATED";

    /// <summary>The start of the reason the gateway gives it.</summary>
    public const string ReasonPrefix = "judge-unavailable:";

    /// <summary>The human label shown next to the id.</summary>
    public const string Label = "Judge gave no verdict; hook_fail_mode is closed";

    /// <summary>What happened and what to look at, for the detail pane.</summary>
    public const string Explanation =
        "Under the regex_judge strategy a CRITICAL pattern finding the rules cannot enforce on their own waits for the LLM judge. The judge gave no verdict " +
        "(an error, a timeout, a late answer, no free judge slot or no hook budget left), and this connector's hook_fail_mode is closed, so the call was " +
        "refused. Where hook_fail_mode is open the rule verdict stands and the call runs with a judge-fallback warning. If this repeats, check the judge's " +
        "provider and its timeout; for a slow judge model, set guardrail.judge.max_parallel.";

    /// <summary>
    /// True when a hook row's details text carries the gateway's reason as its own <c>reason=judge-unavailable: ...</c> pair (renderHookAuditLegacyDetails,
    /// internal/gateway/hook_audit_envelope.go@1.0.8:289-300): the pair at the start or after a space, in the part before the quoted <c>raw_payload=</c> / the
    /// <c>details_json=</c> copy. Free text that merely mentions <c>judge-unavailable:</c> (a tool argument, a message) is not the marker.
    /// </summary>
    public static bool ReasonIn(string? details)
    {
        if (string.IsNullOrEmpty(details))
        {
            return false;
        }

        var end = details.Length;
        foreach (var marker in new[] { " raw_payload=", " details_json=" })
        {
            var at = details.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && at < end)
            {
                end = at;
            }
        }

        try
        {
            return ReasonPair().IsMatch(details.AsSpan(0, end));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?:^|\s)reason=""?" + ReasonPrefix, RegexOptions.CultureInvariant, 250)]
    private static partial Regex ReasonPair();

    /// <summary>True for this id, in any case, with or without the <c>llm-judge:</c> lane prefix the hook response gives findings.</summary>
    public static bool Is(string? ruleId)
    {
        var id = ruleId?.Trim() ?? string.Empty;
        if (id.StartsWith("llm-judge:", StringComparison.OrdinalIgnoreCase))
        {
            id = id["llm-judge:".Length..];
        }

        return string.Equals(id, RuleId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when any of <paramref name="ruleIds"/> is this id.</summary>
    public static bool AnyIn(IEnumerable<string>? ruleIds) => ruleIds?.Any(Is) == true;

    /// <summary>The id with its label, for a place that shows a rule id (<c>JUDGE-UNAVAILABLE-ESCALATED (Judge gave no verdict ...)</c>); any other id as it is.</summary>
    public static string WithLabel(string ruleId) => Is(ruleId) ? $"{ruleId} ({Label})" : ruleId;
}
