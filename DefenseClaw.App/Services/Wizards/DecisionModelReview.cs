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

    /// <summary>True for a <c>setup decision-model</c> command.</summary>
    public static bool IsDecisionModelCommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return argv.Count >= 2 &&
               string.Equals(argv[0], "setup", StringComparison.Ordinal) &&
               string.Equals(argv[1], DecisionModelCard.Target, StringComparison.Ordinal);
    }

    /// <summary>The bars for <paramref name="argv"/>, in the order the review shows them; empty when it is not a decision-model command or has nothing to say.</summary>
    public static IReadOnlyList<CommandReviewWarning> Warnings(IReadOnlyList<string> argv)
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
                "This writes decision_model.hook_connectors. Releases before 1.0.7 refuse this key (config.yaml no longer loads), so clear it before you go back: " +
                "defenseclaw setup decision-model --connectors \"\" --non-interactive."));
        }

        if (argv.Contains("--ping", StringComparer.Ordinal))
        {
            warnings.Add(new CommandReviewWarning(
                PingTitle,
                "After saving, the CLI sends one benign request to the decision model endpoint. The settings are saved whether or not it answers."));
        }

        return warnings;
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
