using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What a review of <c>setup decision-model</c> says beyond its argv (CUST-446), drawn as warning bars on the wizard's last page. Nothing for any
/// other command. The facts are DefenseClaw 1.0.7 and 1.0.8's: <c>--connectors</c> writes <c>decision_model.hook_connectors</c>, which releases
/// before 1.0.7 refuse (the schema says so), and <c>--ping</c> sends one request after the settings are saved.
/// </summary>
public static class DecisionModelReview
{
    /// <summary>The heading of the bar about going back to a release before 1.0.7; the one the protected-file watch uses for the same kind of key.</summary>
    public const string RollbackTitle = "Rolling back to an older release";

    /// <summary>The heading of the bar that says <c>--ping</c> contacts the endpoint.</summary>
    public const string PingTitle = "Sends one request to the endpoint";

    /// <summary>The heading of the bar that says Gemini Decision 1 needs a URL and a model.</summary>
    public const string GeminiNeedsEndpointTitle = "Gemini Decision 1 needs a URL and a model";

    // The host infer_protocol (decision_model.py@1.0.10:107-114) turns into the gemini-decisions protocol.
    private const string GeminiHost = "generativelanguage.googleapis.com";

    /// <summary>True for a <c>setup decision-model</c> command.</summary>
    public static bool IsDecisionModelCommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return argv.Count >= 2 &&
               string.Equals(argv[0], "setup", StringComparison.Ordinal) &&
               string.Equals(argv[1], DecisionModelCard.Target, StringComparison.Ordinal);
    }

    /// <summary>The bars for <paramref name="argv"/>, in the order the review shows them; empty when it is not a decision-model command or has nothing to say.</summary>
    /// <param name="argv">The command.</param>
    /// <param name="config">
    /// config.yaml as it is now (read only), or null when the caller has none: the settings the command does not name are the ones already saved, and
    /// the CLI infers the protocol from the URL only when none is saved. Null judges the argv alone.
    /// </param>
    public static IReadOnlyList<CommandReviewWarning> Warnings(IReadOnlyList<string> argv, ConfigYamlReader? config = null)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsDecisionModelCommand(argv))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var warnings = new List<CommandReviewWarning>();

        // Only a non-empty list writes the key: an empty one clears it, which is what the bar tells the operator to do before going back.
        if (ValueOf(argv, "--connectors") is { Length: > 0 })
        {
            warnings.Add(new CommandReviewWarning(
                RollbackTitle,
                "Releases before 1.0.7 refuse decision_model.hook_connectors and will not load this config.yaml. Before going back, clear it with " +
                "defenseclaw setup decision-model --connectors \"\" --non-interactive."));
        }

        // The protocol key is written when --protocol names gemini-decisions, and also when no --protocol is given, a --base-url is, no protocol is saved yet and
        // the URL's host is Google's Decisions API (cli/defenseclaw/decision_model.py@1.0.10:99-102 apply_settings and infer_protocol: system-one is the default
        // and is never written). Only the 1.0.10 wizard can send --protocol: its help is the one that has it.
        var protocolGiven = ValueOf(argv, "--protocol");
        var inferred = protocolGiven is null && InfersGemini(ValueOf(argv, "--base-url"), config);
        if (string.Equals(protocolGiven?.Trim(), DecisionModelCard.GeminiProtocol, StringComparison.OrdinalIgnoreCase) || inferred)
        {
            warnings.Add(new CommandReviewWarning(
                RollbackTitle,
                "Releases before 1.0.10 refuse the new decision_model keys protocol, block_risk_score, escalate_rule_flagged, history_turns, " +
                "history_tool_results and flow_guard. Rolling back to 1.0.9 or earlier needs decision_model.protocol removed first."));

            // What the command leaves out is what config.yaml already holds (the CLI sets only the fields it is given).
            if (!HasSetting(argv, "--base-url", config?.Text("decision_model", "base_url")) || !HasSetting(argv, "--model", config?.Text("decision_model", "model")))
            {
                warnings.Add(new CommandReviewWarning(
                    GeminiNeedsEndpointTitle,
                    "Gemini Decision 1 needs --base-url https://generativelanguage.googleapis.com/v1beta/decisions and --model gemini-decision-1 when the decision " +
                    "model is on, and setup fills in nothing in non-interactive mode."));
            }
        }

        if (argv.Contains("--ping", StringComparer.Ordinal))
        {
            warnings.Add(new CommandReviewWarning(
                PingTitle,
                "After saving, the CLI sends one request to the decision model endpoint. Settings are saved whether or not it answers."));
        }

        return warnings;
    }

    // True when the endpoint the command saves has a value: the flag's own (an empty one clears it), else what config.yaml holds.
    private static bool HasSetting(IReadOnlyList<string> argv, string flag, string? saved) =>
        !string.IsNullOrWhiteSpace(ValueOf(argv, flag) ?? saved);

    // apply_settings: no --protocol, a --base-url, no protocol saved, and infer_protocol (urlsplit's hostname, lower-cased, is Google's Decisions API host).
    private static bool InfersGemini(string? baseUrl, ConfigYamlReader? config)
    {
        if (baseUrl is null || !string.IsNullOrWhiteSpace(config?.Text("decision_model", "protocol")))
        {
            return false;
        }

        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) &&
               !string.IsNullOrEmpty(uri.Host) &&
               string.Equals(uri.Host, GeminiHost, StringComparison.OrdinalIgnoreCase);
    }

    // The value after the flag, or null when the flag is not there or ends the command. Everything after "--" is not an option.
    private static string? ValueOf(IReadOnlyList<string> argv, string flag)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(argv[i], flag, StringComparison.Ordinal))
            {
                return argv[i + 1];
            }
        }

        return null;
    }
}
