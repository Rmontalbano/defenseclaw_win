using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Which of the hook-row labels the runtime has earned (CUST-449). An older runtime gets <see cref="None"/>: its rows look exactly as they did.
/// </summary>
/// <param name="HookFailure">1.0.7: a hook's own refusal row (<see cref="RuntimeCapability.HookFailureBlockRows"/>).</param>
/// <param name="JudgeEscalation">1.0.7: the judge-unavailable finding (<see cref="RuntimeCapability.JudgeEscalation"/>).</param>
/// <param name="JudgeFallback">1.0.8: <c>judge_fallback</c> on a hook row (<see cref="RuntimeCapability.JudgeFallbackOnHookRows"/>).</param>
/// <param name="Cowork">1.0.8: Claude Cowork rows (<see cref="RuntimeCapability.CoworkProtection"/>).</param>
/// <param name="AlertRan">1.0.9: an <c>allow</c> hook row in action mode reads <c>alert</c> in <c>alerts --json</c> (<see cref="RuntimeCapability.ShellProtectedFileFloor"/> marks a 1.0.9 runtime).</param>
internal readonly record struct HookRowGate(bool HookFailure, bool JudgeEscalation, bool JudgeFallback, bool Cowork, bool AlertRan = false)
{
    /// <summary>No label: what runtimes before 1.0.7 get.</summary>
    public static HookRowGate None => default;

    /// <summary>Every label (a test's shorthand for a 1.0.8 runtime).</summary>
    public static HookRowGate All => new(true, true, true, true, true);

    /// <summary>True when any label is on.</summary>
    public bool Any => HookFailure || JudgeEscalation || JudgeFallback || Cowork || AlertRan;

    /// <summary>The gate for what the runtime can do.</summary>
    public static HookRowGate From(RuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new(
            capabilities.Has(RuntimeCapability.HookFailureBlockRows),
            capabilities.Has(RuntimeCapability.JudgeEscalation),
            capabilities.Has(RuntimeCapability.JudgeFallbackOnHookRows),
            capabilities.Has(RuntimeCapability.CoworkProtection),
            capabilities.Has(RuntimeCapability.ShellProtectedFileFloor));
    }
}

public sealed partial class AlertsPanelViewModel
{
    private static readonly Regex SurfaceToken = new(@"(?<!\S)surface:cowork(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private HookRowGate HookGate => HookRowGate.From(Services.Runtime.Capabilities);

    /// <summary>The filter text without a <c>surface:cowork</c> word, which is honoured only on a runtime that knows Cowork; elsewhere it stays free text, as any unknown token does.</summary>
    private string WithoutSurfaceToken(string? text)
    {
        if (string.IsNullOrEmpty(text) || !HookGate.Cowork)
        {
            return text ?? string.Empty;
        }

        return SurfaceToken.Replace(text, string.Empty);
    }

    /// <summary>True while the search box holds <c>surface:cowork</c> on a runtime that knows Cowork: only Cowork rows are listed.</summary>
    private bool CoworkOnly => HookGate.Cowork && !string.IsNullOrEmpty(FilterText) && SurfaceToken.IsMatch(FilterText);
}

/// <summary>What the inspector says about a hook-related row (CUST-449): the facts, the notes, and whether it is a hook's own refusal or a Cowork call.</summary>
/// <param name="IsRefusal">The row is a hook's own refusal or unchecked-call note: no gateway decision, so not a policy block.</param>
/// <param name="IsCowork">The row is a Claude Cowork call.</param>
/// <param name="Facts">Labelled values for the inspector.</param>
/// <param name="Notes">Plain-words notes for the inspector.</param>
internal sealed record HookRowState(bool IsRefusal, bool IsCowork, IReadOnlyList<AlertField> Facts, IReadOnlyList<string> Notes)
{
    /// <summary>A row with nothing to say.</summary>
    public static HookRowState Empty { get; } = new(false, false, Array.Empty<AlertField>(), Array.Empty<string>());
}

public sealed partial class AlertItem
{
    /// <summary>The audit row's parsed <c>structured_json</c> (null when the row came from elsewhere): what the hook-row labels read.</summary>
    internal IReadOnlyDictionary<string, JsonElement>? HookStructured { get; private init; }

    /// <summary>Everything the hook-row labels derived; <see cref="HookRowState.Empty"/> until <see cref="WithHookRows"/> finds something.</summary>
    internal HookRowState HookRows { get; private set; } = HookRowState.Empty;

    /// <summary>True when the row is a hook's own refusal (it is not a policy block).</summary>
    public bool IsHookRefusal => HookRows.IsRefusal;

    /// <summary>True for a Claude Cowork call; the connector stays <c>claudecode</c>.</summary>
    public bool IsCowork => HookRows.IsCowork;

    /// <summary>The small marker shown on a Cowork row.</summary>
    public string CoworkMarker => CoworkRow.Marker;

    /// <summary>True when the inspector has a hook-row section to show.</summary>
    public bool HasHookRowNotes => HookRows.Facts.Count > 0 || HookRows.Notes.Count > 0;

    /// <summary>The section's labelled values (Decided by, Hook's reason, Judge fallback, the Cowork facts).</summary>
    public IReadOnlyList<AlertField> HookRowFacts => HookRows.Facts;

    /// <summary>The section's notes, one paragraph each.</summary>
    public string HookRowNote => string.Join(Environment.NewLine + Environment.NewLine, HookRows.Notes);

    /// <summary>
    /// Labels a hook-related row the runtime can explain: a hook's own refusal (1.0.7; such rows normally reach the Logs and Audit panels, not Alerts, so this
    /// labels one only when a fallback path brings it here), the judge-unavailable finding (1.0.7), <c>judge_fallback</c> (1.0.8) and a Cowork call (1.0.8). A row
    /// with nothing to label, or on a runtime that cannot, is returned as it is. Safe to call again on a row already labelled. Called on a row being built.
    /// </summary>
    /// <param name="gate">What the runtime has earned.</param>
    /// <param name="row">The runtime's own row for it, when there is one: its details and facts fill what the audit row did not give.</param>
    internal AlertItem WithHookRows(HookRowGate gate, RuntimeAlertRow? row = null)
    {
        if (!gate.Any)
        {
            return this;
        }

        var details = Details.Length > 0 ? Details : row?.Details;
        var facts = new List<AlertField>();
        var notes = new List<string>();
        var refusalRow = false;
        var cowork = false;

        if (gate.HookFailure && HookFailureRow.Parse(Action, details) is { } refusal)
        {
            refusalRow = true;
            Headline = refusal.Headline;
            facts.Add(new AlertField("Decided by", HookFailureRow.SourceLabel));
            if (refusal.Reason.Length > 0)
            {
                facts.Add(new AlertField("Hook's reason", refusal.Reason));
            }

            notes.Add(refusal.Explanation);
        }

        if (gate.JudgeEscalation && (JudgeEscalationFinding.Is(RuleId) || RuleIdsHold(row)))
        {
            if (JudgeEscalationFinding.Is(RuleId) && HeadlineIsRaw(details))
            {
                Headline = JudgeEscalationFinding.Label;
            }

            notes.Add(JudgeEscalationFinding.Explanation);
        }

        if (gate.AlertRan)
        {
            AddRuleIdLabels(row, details, notes);
        }

        if (gate.AlertRan && StructuredDetailParser.IsHook(Action) && string.Equals(RowFact(row, "decision"), "alert", StringComparison.OrdinalIgnoreCase))
        {
            // alerts --json prints decision "alert" for a call that ran and was recorded as an alert (cmd_alerts.py@1.0.9:296-297; 1.0.8 said "allow").
            // Checked for both modes: action mode labels an allowed finding "alert" (hook_metrics.py@1.0.9:121-130); observe mode never does (it prints
            // "allow" or "would block (observe mode)"), so there "alert" is only a hook row whose own action was alert, a call the hook let run. A block
            // returns "blocked" (or the post-tool label) before any of these (cmd_alerts.py@1.0.9:283-285).
            facts.Add(new AlertField("Call outcome", "Ran; recorded as an alert"));
        }

        if (gate.JudgeFallback && StructuredDetailParser.IsHook(Action) && HookJudgeFallback.Read(HookStructured, details) is { Length: > 0 } reason)
        {
            facts.Add(new AlertField(HookJudgeFallback.FieldName, HookJudgeFallback.Short(reason)));
            notes.Add(HookJudgeFallback.Describe(reason));
        }

        var connector = row?.Connector ?? Connector;
        var fromRuntimeSurface = false;
        var coworkCall = gate.Cowork ? CoworkRow.Read(connector, HookStructured, details) : null;
        if (gate.Cowork && coworkCall is null && CoworkRow.FromRuntimeSurface(connector, RowFact(row, "surface")) is { } bySurface)
        {
            // A row only the runtime listed has no envelope; 1.0.9 prints its surface (cmd_alerts.py@1.0.9:829-830). A row both sources return was read above, so it is marked once.
            coworkCall = bySurface;
            fromRuntimeSurface = true;
        }

        if (coworkCall is { } call)
        {
            cowork = true;
            facts.AddRange(CoworkRow.Facts(call).Select(p => new AlertField(p.Key, p.Value)));
            if (!TargetRef.StartsWith(CoworkRow.Surface + ":", StringComparison.Ordinal))
            {
                if (fromRuntimeSurface && CoworkRow.RetargetStored(TargetRef) is { Length: > 0 } stored)
                {
                    TargetRef = stored;
                }
                else if (StructuredDetailParser.IsHook(Action) && HookEvent(details) is { Length: > 0 } hookEvent)
                {
                    TargetRef = CoworkRow.Target(hookEvent);
                }
            }
        }

        HookRows = facts.Count == 0 && notes.Count == 0 && !refusalRow && !cowork ? HookRowState.Empty : new HookRowState(refusalRow, cowork, facts, notes);
        return this;
    }

    /// <summary>
    /// The sentence for a rule id of <see cref="RuleIdLabels"/> as a note of its own (the runtime's own rule title stays the headline), and for the shell-command
    /// limit rule what happened to the call, from the row's own decision and action fields. Called only when <see cref="HookRowGate.AlertRan"/> is on:
    /// <see cref="RuntimeCapability.ShellProtectedFileFloor"/> is used as the 1.0.9 release marker because the rule labels and the alert decision change shipped in the
    /// same release and no separate capability exists, so a 1.0.7 or 1.0.8 runtime's rows read as they always did. The judge-unavailable id is labelled above.
    /// </summary>
    private void AddRuleIdLabels(RuntimeAlertRow? row, string? details, List<string> notes)
    {
        var ids = RuleIdList(row).Prepend(RuleId).Where(id => id.Length > 0 && !JudgeEscalationFinding.Is(id)).ToList();
        foreach (var sentence in ids.Select(RuleIdLabels.Sentence).Where(x => x is { Length: > 0 }).Distinct(StringComparer.Ordinal))
        {
            notes.Add(sentence!);
        }

        if (ids.Any(RuleIdLabels.IsCompoundLimit) && RuleIdLabels.CompoundLimitOutcome(RowFact(row, "decision"), details) is { } outcome)
        {
            notes.Add(outcome);
        }
    }

    private static string RowFact(RuntimeAlertRow? row, string name) =>
        row?.Facts.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Value ?? string.Empty;

    /// <summary>The rule ids the row names: the structured <c>rule_ids</c> array, the runtime's <c>rule_ids</c> fact and the id of its <c>rule</c> fact.</summary>
    private IEnumerable<string> RuleIdList(RuntimeAlertRow? row)
    {
        var ids = new List<string>();
        if (HookStructured is { } structured && structured.TryGetValue("rule_ids", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            ids.AddRange(array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty));
        }

        if (RowFact(row, "rule_ids") is { Length: > 0 } fact)
        {
            ids.AddRange(fact.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        if (RuleIdLabels.IdOfRuleFact(RowFact(row, "rule")) is { Length: > 0 } fromRule)
        {
            ids.Add(fromRule);
        }

        return ids;
    }

    /// <summary>True when the headline is no better than the raw text of the finding: empty, the action, the rule id, <c>finding.observed</c> or the scanner's details line.</summary>
    private bool HeadlineIsRaw(string? details) =>
        string.IsNullOrWhiteSpace(Headline) ||
        string.Equals(Headline, Action, StringComparison.Ordinal) ||
        string.Equals(Headline, RuleId, StringComparison.Ordinal) ||
        string.Equals(Headline, "finding.observed", StringComparison.Ordinal) ||
        (details is { Length: > 0 } && string.Equals(Headline, details, StringComparison.Ordinal)) ||
        Headline.StartsWith("scanner=", StringComparison.Ordinal) ||
        Headline.Contains("rule_id=", StringComparison.Ordinal);

    /// <summary>True when the row's <c>rule_ids</c> (the structured array of a hook row, or the runtime's fact of that name) name the judge-unavailable finding.</summary>
    private bool RuleIdsHold(RuntimeAlertRow? row)
    {
        if (HookStructured is { } structured && structured.TryGetValue("rule_ids", out var ids) && ids.ValueKind == JsonValueKind.Array &&
            JudgeEscalationFinding.AnyIn(ids.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty)))
        {
            return true;
        }

        var fact = row?.Facts.FirstOrDefault(f => string.Equals(f.Name, "rule_ids", StringComparison.Ordinal))?.Value;
        return fact is { Length: > 0 } && JudgeEscalationFinding.AnyIn(fact.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The hook event (<c>PreToolUse</c>): the envelope's <c>event</c>, else the details' <c>event=</c> pair, else the row's target.</summary>
    private string HookEvent(string? details)
    {
        if (HookStructured is { } structured && structured.TryGetValue("event", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } fromJson)
        {
            return fromJson;
        }

        if (!string.IsNullOrEmpty(details) && StructuredDetailParser.Values(details).TryGetValue("event", out var fromDetails) && fromDetails.Length > 0)
        {
            return fromDetails;
        }

        return RawTarget.Length > 0 ? RawTarget : TargetRef;
    }
}
