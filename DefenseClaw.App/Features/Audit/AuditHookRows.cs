using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>The Audit panel's side of the hook-row labels (CUST-449): which labels the runtime has earned.</summary>
public sealed partial class AuditPanelViewModel
{
    private HookRowGate HookGate => HookRowGate.From(Services.Runtime.Capabilities);
}

/// <summary>
/// What the Audit panel says about a hook-related row (CUST-449): a hook's own refusal, the judge-unavailable finding, a judge fallback and a Claude Cowork
/// call, read from the row's action, details and <c>structured_json</c> envelope. Every label is gated by <see cref="HookRowGate"/>: with the gate off nothing
/// is read and the row is exactly what an older runtime wrote.
/// </summary>
internal static partial class AuditHookRows
{
    /// <summary>The hook refusal the row is (null when it is not one, or the runtime does not label them).</summary>
    public static HookFailureRefusal? Refusal(AuditEvent source, HookRowGate gate) =>
        gate.HookFailure ? HookFailureRow.Parse(source.Action, source.Details) : null;

    /// <summary>The Cowork call the row is (null when it is not one, or the runtime does not know Cowork). A cheap text check first, so a page of rows parses no payload that cannot say it.</summary>
    public static CoworkCall? Cowork(AuditEvent source, HookRowGate gate)
    {
        if (!gate.Cowork || !string.Equals(source.Connector?.Trim(), CoworkRow.Connector, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mentioned = source.StructuredJsonRaw?.Contains(CoworkRow.Surface, StringComparison.OrdinalIgnoreCase) == true
            || source.Details?.Contains(CoworkRow.Surface, StringComparison.OrdinalIgnoreCase) == true;
        return mentioned ? CoworkRow.Read(source.Connector, source.StructuredJson, source.Details) : null;
    }

    /// <summary>
    /// The inspector's "Parsed details" rows for the row: <paramref name="parsed"/> (the details as the TUI lays them out) with the rule ids labelled, and the
    /// facts and notes the gate allows after them. <paramref name="parsed"/> itself when there is nothing to add.
    /// </summary>
    public static IReadOnlyList<DetailPair> Pairs(
        IReadOnlyList<DetailPair> parsed,
        AuditEvent source,
        HookRowGate gate,
        HookFailureRefusal? refusal,
        CoworkCall? cowork)
    {
        if (!gate.Any)
        {
            return parsed;
        }

        var isHook = StructuredDetailParser.IsHook(source.Action);
        var fallback = gate.JudgeFallback && isHook ? HookJudgeFallback.Read(source.StructuredJson, source.Details) : string.Empty;
        var structuredIds = gate.JudgeEscalation || gate.AlertRan ? RuleIds(source) : Array.Empty<string>();
        var escalated = gate.JudgeEscalation
            && (JudgeEscalationFinding.AnyIn(structuredIds)
                || (isHook && JudgeEscalationFinding.ReasonIn(source.Details)));

        var rows = new List<DetailPair>(parsed.Count + 6);
        var labelled = false;
        var meanings = new List<string>();
        var limited = false;
        foreach (var pair in parsed)
        {
            if (fallback.Length > 0 && string.Equals(pair.Label, HookJudgeFallback.FieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (cowork is not null && string.Equals(pair.Label, "Surface", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (gate.AlertRan && IsRuleLabel(pair.Label))
            {
                // The 1.0.9 rule labels: the row keeps the runtime's own value and the sentence goes on a line of its own below.
                meanings.AddRange(RuleIdLabels.NewSentencesIn(pair.Value));
                limited |= RuleIdLabels.MentionsCompoundLimit(pair.Value);
            }

            if (gate.JudgeEscalation && IsRuleLabel(pair.Label) && MentionsEscalation(pair.Value))
            {
                rows.Add(new DetailPair(pair.Label, LabelEscalation(pair.Value)));
                labelled = true;
                escalated = true;
                continue;
            }

            rows.Add(pair);
        }

        if (refusal is not null)
        {
            rows.Add(new DetailPair("Decided by", HookFailureRow.SourceLabel));
            if (refusal.Reason.Length > 0)
            {
                rows.Add(new DetailPair("Hook's reason", refusal.Reason));
            }

            rows.Add(new DetailPair("Explanation", refusal.Explanation));
        }

        if (gate.JudgeEscalation)
        {
            if (!labelled && JudgeEscalationFinding.AnyIn(structuredIds))
            {
                rows.Add(new DetailPair("Rule IDs", string.Join(", ", structuredIds.Select(JudgeEscalationFinding.WithLabel))));
            }

            if (escalated)
            {
                rows.Add(new DetailPair("Rule note", JudgeEscalationFinding.Explanation));
            }
        }

        if (gate.AlertRan)
        {
            // ShellProtectedFileFloor is the 1.0.9 release marker here: the rule labels and the alert decision shipped in the same release and no separate
            // capability exists. A 1.0.7 or 1.0.8 runtime's rows are left as they always were.
            meanings.AddRange(RuleIdLabels.NewSentencesOf(structuredIds));
            limited |= structuredIds.Any(RuleIdLabels.IsCompoundLimit);
            foreach (var meaning in meanings.Distinct(StringComparer.Ordinal))
            {
                rows.Add(new DetailPair("Rule meaning", meaning));
            }

            if (limited && RuleIdLabels.CompoundLimitOutcome(null, source.Details) is { } outcome)
            {
                rows.Add(new DetailPair("Call outcome", outcome));
            }
        }

        if (fallback.Length > 0)
        {
            rows.Add(new DetailPair(HookJudgeFallback.FieldName, HookJudgeFallback.Short(fallback)));
            rows.Add(new DetailPair("Fallback note", HookJudgeFallback.Describe(fallback)));
        }

        if (cowork is not null)
        {
            foreach (var fact in CoworkRow.Facts(cowork))
            {
                rows.Add(new DetailPair(fact.Key, fact.Value));
            }
        }

        return rows;
    }

    /// <summary>The <c>rule_ids</c> array of the row's envelope (a hook decision), empty when it has none.</summary>
    private static IReadOnlyList<string> RuleIds(AuditEvent source)
    {
        if (!source.StructuredJson.TryGetValue("rule_ids", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (ids.Count == EventDetail.ListLimit)
            {
                break;
            }

            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool IsRuleLabel(string label) =>
        label.Equals("Rule Id", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Rule IDs", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Rule ID", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsEscalation(string value) => EscalationId().IsMatch(value);

    private static string LabelEscalation(string value) =>
        EscalationId().Replace(value, static match => JudgeEscalationFinding.WithLabel(match.Value));

    [GeneratedRegex(@"(?:llm-judge:)?" + JudgeEscalationFinding.RuleId, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex EscalationId();
}
