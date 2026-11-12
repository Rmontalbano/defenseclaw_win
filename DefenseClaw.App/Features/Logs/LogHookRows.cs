using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What the Logs panel says about a hook-related canonical event (CUST-449): a hook's own refusal, the judge-unavailable finding, a judge fallback and a
/// Claude Cowork call. Every label is gated by <see cref="HookRowGate"/>, so a runtime before 1.0.7 shows its rows exactly as it did. The Logs streams read the
/// payload, not <c>structured_json</c>, so the helpers read the row's action and details text (<see cref="StreamEvent.RowAction"/>, <see cref="StreamEvent.RowDetails"/>)
/// and the payload's surface and rule ids.
/// </summary>
internal static partial class LogHookRows
{
    /// <summary>What the labels add to one row.</summary>
    /// <param name="Message">The row's message: the event's own, or the hook-failure headline, or the same with the Cowork marker in front.</param>
    /// <param name="IsCowork">The row is a Claude Cowork call.</param>
    /// <param name="Fields">Inspector rows to add after the hook's own.</param>
    /// <param name="SkipFallbackRow">The hook's own <c>judge_fallback</c> row is replaced by the labelled one.</param>
    internal sealed record Labels(string Message, bool IsCowork, IReadOnlyList<LogField> Fields, bool SkipFallbackRow)
    {
        /// <summary>Nothing added: the event as the runtime wrote it.</summary>
        public static Labels None(string message) => new(message, false, Array.Empty<LogField>(), false);
    }

    /// <summary>The labels of <paramref name="e"/> under <paramref name="gate"/>.</summary>
    public static Labels Read(StreamEvent e, HookRowGate gate)
    {
        if (!gate.Any)
        {
            return Labels.None(e.Message);
        }

        var message = e.Message;
        var fields = new List<LogField>();
        var isCowork = false;
        var skipFallback = false;

        if (gate.HookFailure && HookFailureRow.Parse(e.RowAction, e.RowDetails) is { } refusal)
        {
            // The headline in place of the bare action word; IsActionable is the reader's and is not touched.
            message = DisplayRedaction.Text(
                refusal.Reason.Length > 0
                    ? $"{e.EventName} {refusal.Headline} — {refusal.Reason}"
                    : $"{e.EventName} {refusal.Headline}");
            fields.Add(new LogField("Decided by", HookFailureRow.SourceLabel));
            if (refusal.Reason.Length > 0)
            {
                fields.Add(new LogField("Hook's reason", refusal.Reason));
            }

            fields.Add(new LogField("Explanation", refusal.Explanation));
        }

        if (gate.JudgeEscalation)
        {
            var hit = JudgeEscalationFinding.AnyIn(e.Detail.Categories);
            if (RuleIdOf(e.RowDetails) is { Length: > 0 } rule && JudgeEscalationFinding.Is(rule))
            {
                fields.Add(new LogField("Rule ID", JudgeEscalationFinding.WithLabel(rule)));
                hit = true;
            }

            hit |= StructuredDetailParser.IsHook(e.RowAction)
                && JudgeEscalationFinding.ReasonIn(e.RowDetails);
            if (hit)
            {
                fields.Add(new LogField("Rule note", JudgeEscalationFinding.Explanation));
            }
        }

        if (gate.JudgeFallback && StructuredDetailParser.IsHook(e.RowAction))
        {
            var reason = HookJudgeFallback.Read(null, e.RowDetails);
            if (reason.Length > 0)
            {
                fields.Add(new LogField(HookJudgeFallback.FieldName, HookJudgeFallback.Short(reason)));
                fields.Add(new LogField("Fallback note", HookJudgeFallback.Describe(reason)));
                skipFallback = true;
            }
        }

        if (gate.Cowork && CoworkRow.Read(e.Connector, null, e.RowDetails, e.PayloadSurface) is { } call)
        {
            isCowork = true;
            message = CoworkRow.Marker + ": " + message;
            foreach (var fact in CoworkRow.Facts(call))
            {
                fields.Add(new LogField(fact.Key, fact.Value));
            }
        }

        return new Labels(message, isCowork, fields, skipFallback);
    }

    /// <summary>The Categories row's value: the rule ids, each with its label where it has one (the judge-unavailable id), when the runtime has earned it.</summary>
    public static string Categories(StreamEvent e, HookRowGate gate, string unlabelled) =>
        gate.JudgeEscalation && JudgeEscalationFinding.AnyIn(e.Detail.Categories)
            ? string.Join(", ", e.Detail.Categories.Select(JudgeEscalationFinding.WithLabel))
            : unlabelled;

    /// <summary>The <c>rule_id=</c> value of a finding row's details; empty when it has none.</summary>
    private static string RuleIdOf(string details)
    {
        if (details.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var match = RulePair().Match(details);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"(?:^|\s)rule_id=(\S+)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex RulePair();
}
