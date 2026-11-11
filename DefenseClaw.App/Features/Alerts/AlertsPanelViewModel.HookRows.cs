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
internal readonly record struct HookRowGate(bool HookFailure, bool JudgeEscalation, bool JudgeFallback, bool Cowork)
{
    /// <summary>No label: what runtimes before 1.0.7 get.</summary>
    public static HookRowGate None => default;

    /// <summary>Every label (a test's shorthand for a 1.0.8 runtime).</summary>
    public static HookRowGate All => new(true, true, true, true);

    /// <summary>True when any label is on.</summary>
    public bool Any => HookFailure || JudgeEscalation || JudgeFallback || Cowork;

    /// <summary>The gate for what the runtime can do.</summary>
    public static HookRowGate From(RuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new(
            capabilities.Has(RuntimeCapability.HookFailureBlockRows),
            capabilities.Has(RuntimeCapability.JudgeEscalation),
            capabilities.Has(RuntimeCapability.JudgeFallbackOnHookRows),
            capabilities.Has(RuntimeCapability.CoworkProtection));
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

        if (gate.JudgeFallback && StructuredDetailParser.IsHook(Action) && HookJudgeFallback.Read(HookStructured, details) is { Length: > 0 } reason)
        {
            facts.Add(new AlertField(HookJudgeFallback.FieldName, HookJudgeFallback.Short(reason)));
            notes.Add(HookJudgeFallback.Describe(reason));
        }

        if (gate.Cowork && CoworkRow.Read(row?.Connector ?? Connector, HookStructured, details) is { } call)
        {
            cowork = true;
            facts.AddRange(CoworkRow.Facts(call).Select(p => new AlertField(p.Key, p.Value)));
            if (StructuredDetailParser.IsHook(Action) && HookEvent(details) is { Length: > 0 } hookEvent && !TargetRef.StartsWith(CoworkRow.Surface + ":", StringComparison.Ordinal))
            {
                TargetRef = CoworkRow.Target(hookEvent);
            }
        }

        HookRows = facts.Count == 0 && notes.Count == 0 && !refusalRow && !cowork ? HookRowState.Empty : new HookRowState(refusalRow, cowork, facts, notes);
        return this;
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
