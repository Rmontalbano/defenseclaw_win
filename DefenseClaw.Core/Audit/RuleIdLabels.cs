using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// One plain sentence for each rule id DefenseClaw 1.0.9 added to hook rows that have no description of their own. Upstream gives each only a title and a
/// severity, so the sentence says what the rule saw, from the title: internal/gateway/operand_bound.go@1.0.9:34,:113-118 (ACTION-OPERAND-BOUND),
/// share_body_unread.go@1.0.9:77,:97-103 (ACTION-SHARE-BODY-UNREAD), windows_opened_path.go@1.0.9:456-457 (TOOL-PATH-UNVERIFIED),
/// policies/guardrail/{default,strict,permissive}/rules/cognitive.yaml@1.0.9:112-135 (tamper.agent_hook_config_disable / _write) and
/// trusted_action_dispatch.go@1.0.9:9580-9588 (ACTION-COMPOUND-LIMIT). An id with no sentence is shown as it is. The judge-unavailable id keeps its own
/// label (<see cref="JudgeEscalationFinding.Label"/>). The ids are the runtime's own spelling (upper case for the ACTION and TOOL rules, lower case for
/// the tamper rules); a lookup ignores case, the output keeps the id as the row wrote it.
/// </summary>
public static class RuleIdLabels
{
    /// <summary>The rule id of a shell command too long or too complex for the policy check.</summary>
    public const string CompoundLimit = "ACTION-COMPOUND-LIMIT";

    private static readonly IReadOnlyDictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ACTION-OPERAND-BOUND"] =
            "The command had more than 1024 operands: the first 1024, and every later word that may name a protected file or is a link, were checked; the other names were not.",
        ["ACTION-SHARE-BODY-UNREAD"] = "A script on a network share could not be read for the policy check.",
        ["TOOL-PATH-UNVERIFIED"] =
            "A Windows file path (a short name or a user-profile folder) the file system could not verify, so the file it opens could not be checked.",
        ["tamper.agent_hook_config_disable"] = "An edit that turns off the agent's DefenseClaw hooks in its settings.",
        ["tamper.agent_hook_config_write"] = "A shell command that writes to an agent's hook settings file.",
        [CompoundLimit] = "A shell command too long or too complex for the policy check.",
    };

    private static readonly Regex AnyId = new(
        @"(?<![\w.\-])(?:llm-judge:)?(?:" + string.Join("|", Sentences.Keys.Select(Regex.Escape)) + "|" + Regex.Escape(JudgeEscalationFinding.RuleId) + @")(?![\w.\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The sentence for <paramref name="ruleId"/> (the judge-unavailable id gives its label); null when the id has none.</summary>
    public static string? Sentence(string? ruleId)
    {
        var id = ruleId?.Trim() ?? string.Empty;
        if (JudgeEscalationFinding.Is(id))
        {
            return JudgeEscalationFinding.Label;
        }

        return Sentences.TryGetValue(id, out var sentence) ? sentence : null;
    }

    /// <summary>True when the id has a sentence (including the judge-unavailable id).</summary>
    public static bool Has(string? ruleId) => Sentence(ruleId) is not null;

    /// <summary>True when any of <paramref name="ruleIds"/> has a sentence.</summary>
    public static bool AnyIn(IEnumerable<string>? ruleIds) => ruleIds?.Any(Has) == true;

    /// <summary>True for the shell-command limit rule.</summary>
    public static bool IsCompoundLimit(string? ruleId) => string.Equals(ruleId?.Trim(), CompoundLimit, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id with its sentence, for a place that shows a rule id (<c>ACTION-COMPOUND-LIMIT (A shell command ...)</c>); any other id as it is.</summary>
    public static string WithLabel(string ruleId)
    {
        if (JudgeEscalationFinding.Is(ruleId))
        {
            return JudgeEscalationFinding.WithLabel(ruleId);
        }

        return Sentence(ruleId) is { } sentence ? $"{ruleId} ({sentence})" : ruleId;
    }

    /// <summary>The id part of the runtime's <c>rule</c> value (<c>ACTION-COMPOUND-LIMIT: Shell command exceeds ...</c>): the text before the first colon, trimmed.</summary>
    public static string IdOfRuleFact(string? ruleFact)
    {
        var text = ruleFact ?? string.Empty;
        var colon = text.IndexOf(':');
        return (colon < 0 ? text : text[..colon]).Trim();
    }

    /// <summary>
    /// The runtime's <c>rule</c> value (<c>ID: title</c>) with a sentence in place of the title when the id has one; the value as it is otherwise.
    /// The sentence is shown without the id, which the row's rule-id column and the raw runtime.rule field keep.
    /// </summary>
    public static string HeadlineOfRuleFact(string ruleFact) =>
        !JudgeEscalationFinding.Is(IdOfRuleFact(ruleFact)) && Sentence(IdOfRuleFact(ruleFact)) is { } sentence ? sentence : ruleFact;

    /// <summary>True when <paramref name="text"/> names a rule id that has a sentence.</summary>
    public static bool Mentions(string? text) => Match(text, static m => m.Success);

    /// <summary>True when <paramref name="text"/> names the shell-command limit rule.</summary>
    public static bool MentionsCompoundLimit(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(CompoundLimit, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="text"/> with each rule id that has a sentence followed by it; other text as it is.</summary>
    public static string Replace(string text)
    {
        try
        {
            return AnyId.Replace(text, static match => WithLabel(match.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static bool Match(string? text, Func<Match, bool> test)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return test(AnyId.Match(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// What happened to the call the shell-command limit rule fired on, said only from the row's own fields and never from the runtime version.
    /// DefenseClaw 1.0.9 outside the strict rule pack records the limit as an alert and lets the call run; the strict pack blocks it
    /// (internal/gateway/shell_protected_file_profile.go@1.0.9:134-154). <paramref name="decision"/> is the label <c>alerts --json</c> prints; the hook
    /// row's details text carries <c>action=</c> and <c>raw_action=</c> (renderHookAuditLegacyDetails, hook_audit_envelope.go@1.0.9:289-312). Null when
    /// the row says neither (no fields, or an observe-mode row that only would have blocked).
    /// </summary>
    public static string? CompoundLimitOutcome(string? decision, string? details)
    {
        var values = StructuredDetailParser.Values(details);
        var action = Word(values.GetValueOrDefault("action"));
        var raw = Word(values.GetValueOrDefault("raw_action"));
        var label = Word(decision);

        var verdict = label.Length > 0 ? label : action;
        if (verdict is "blocked" or "block" or "deny" or "denied")
        {
            return "The call was blocked.";
        }

        var ranAsAlert = verdict == "alert"
            || action == "alert"
            || (verdict is "allow" or "allowed" && raw == "alert");
        if (!ranAsAlert)
        {
            return null;
        }

        // In observe mode no pack blocks it (claudecode_hook_profile.go@1.0.9:124-139 answers allow), so the strict-pack clause is left out.
        return Word(values.GetValueOrDefault("mode")) == "observe"
            ? "The call ran; DefenseClaw recorded it as an alert (observe mode)."
            : "The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).";
    }

    private static string Word(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
