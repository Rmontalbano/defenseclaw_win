using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the review page of the LLM and guardrail wizards says that the command line does not: three things the commands do beyond writing
/// config.yaml. Each is judged on the argv, which is what runs, so it cannot drift from the command the operator is about to confirm.
/// <list type="bullet">
///   <item><b>Ping.</b> <c>setup llm --ping</c> sends one request to the provider after saving (<c>defenseclaw.llm.ping</c>, 0.8.10: the message
///     "ping", one token, five seconds, no retries, through LiteLLM with the stored key). It is the one thing a setup command does that leaves
///     the machine, so it is a reviewed step with its own bar rather than a quiet switch.</item>
///   <item><b>TLS verification off.</b> <c>--insecure-skip-verify</c> (and the judge's, and <c>provider add</c>'s) stops the CLI checking the
///     endpoint's certificate; its own help says "lab use only".</item>
///   <item><b>Guardrail off.</b> <c>guardrail disable</c> tears the connector hooks down; the Setup hub reviews it the same way and floors it at Destructive.</item>
/// </list>
/// </summary>
public static class WizardCautions
{
    /// <summary>The heading of the ping bar.</summary>
    public const string PingTitle = "Contacts the provider";

    /// <summary>The heading of the TLS bar.</summary>
    public const string InsecureTlsTitle = "TLS verification is off";

    /// <summary>The heading of the guardrail-off bar.</summary>
    public const string GuardrailOffTitle = "Turns the guardrail off";

    private static readonly string[] InsecureFlags = { "--insecure-skip-verify", "--judge-insecure-skip-verify" };

    /// <summary>The bars to show beside the review of <paramref name="argv"/>; empty for almost every command.</summary>
    public static IReadOnlyList<CommandReviewWarning> For(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var warnings = new List<CommandReviewWarning>();
        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();

        if (IsSetupLlm(options) && HasFlag(options, "--ping"))
        {
            warnings.Add(new CommandReviewWarning(
                PingTitle,
                "After saving, the CLI sends one request - the message \"ping\", asking for one token - to the provider's endpoint, with the key that " +
                "the environment variable you named holds. It is a real call: the provider can bill, rate-limit or log it. The settings are saved " +
                "whether or not the provider answers, and the result is printed in the output."));
        }

        if (InsecureFlags.Any(flag => HasFlag(options, flag)))
        {
            warnings.Add(new CommandReviewWarning(
                InsecureTlsTitle,
                "The CLI will not check this endpoint's certificate, so anyone on the network path can read what is sent to it, the API key " +
                "included. The CLI describes this option as for lab use only; give it the endpoint's CA bundle instead if you can."));
        }

        if (GuardrailScope.IsDisableCommand(argv))
        {
            warnings.Add(new CommandReviewWarning(GuardrailOffTitle, DisableSentence(options)));
        }

        return warnings;
    }

    /// <summary>
    /// The lowest tier the review may show: turning the guardrail off tears the connectors' hooks down, so it is Destructive - the tier the Setup
    /// hub gives the same command. Null for everything else (the wizard's own floor applies).
    /// </summary>
    public static CommandTier? Floor(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return GuardrailScope.IsDisableCommand(argv) ? CommandTier.Destructive : null;
    }

    private static string DisableSentence(IReadOnlyList<string> options)
    {
        var index = options.ToList().FindIndex(a => string.Equals(a, "--connector", StringComparison.Ordinal));
        var scope = index >= 0 && index + 1 < options.Count
            ? $"Only {options[index + 1]} is affected: its peers keep running, and its policy is kept for a later enable."
            : "No connector is named, so this is the global switch: the guardrail goes off for every active connector.";

        return "Disabling tears down the connector hooks (for example the live Claude Code hooks), so agents on this machine run without " +
               "DefenseClaw protection until the guardrail is enabled again. " + scope;
    }

    private static bool IsSetupLlm(IReadOnlyList<string> options) =>
        options.Count >= 2 &&
        string.Equals(options[0], "setup", StringComparison.Ordinal) &&
        string.Equals(options[1], "llm", StringComparison.Ordinal);

    /// <summary>
    /// Bare switches of the commands these wizards build, so that the token after one of them is a flag and not its value. The classifier's own
    /// list (<see cref="CommandTiers.IsSwitch"/>) is the read-only and confirm switches; these are the ones a setup wizard adds, and it is a
    /// <c>--ping</c> right after an <c>--insecure-skip-verify</c> that must still be seen as one.
    /// </summary>
    private static readonly HashSet<string> WizardSwitches = new(StringComparer.Ordinal)
    {
        "--ping", "--no-ping", "--insecure-skip-verify", "--judge-insecure-skip-verify", "--non-interactive", "--accept-defaults", "--yes",
        "--human-approval", "--no-human-approval", "--restart", "--no-restart", "--verify", "--no-verify", "--show", "--disable",
        "--inherit-llm", "--no-inherit-llm", "--inherit", "--no-inherit", "--replace", "--no-replace", "--enable-judge", "--no-enable-judge",
        "--no-reload",
    };

    /// <summary>
    /// True when <paramref name="flag"/> is on the command as a flag - not the value of the option before it. The option before it takes a value
    /// unless it is a known bare switch, so <c>--model --ping</c> is a model named like a flag, as the CLI reads it.
    /// </summary>
    private static bool HasFlag(IReadOnlyList<string> options, string flag)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (!string.Equals(options[i], flag, StringComparison.Ordinal))
            {
                continue;
            }

            var previous = i == 0 ? null : options[i - 1];
            if (previous is null ||
                !previous.StartsWith('-') ||
                previous.Contains('=', StringComparison.Ordinal) ||
                WizardSwitches.Contains(previous) ||
                CommandTiers.IsSwitch(previous))
            {
                return true;
            }
        }

        return false;
    }
}
