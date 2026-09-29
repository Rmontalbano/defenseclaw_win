using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the review page says about the command that is about to run: how much it changes, and whether it
/// bounces the gateway. Both come from one place so the wizard, the hub's guardrail controls and any
/// later surface tell the same story.
/// </summary>
public static class WizardReview
{
    /// <summary>The sentence the brief requires on every command that restarts the live gateway.</summary>
    public const string RestartSentence = "This restarts the DefenseClaw gateway.";

    /// <summary>
    /// Path words that make a <c>setup</c> command a read or a probe rather than a configuration write, so
    /// it does not restart anything (<c>setup observability list</c>, <c>setup … test</c>).
    /// </summary>
    private static readonly HashSet<string> NonRestartingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "show", "status", "test", "url", "logs", "env",
    };

    private static readonly HashSet<string> NonRestartingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--no-restart", "--dry-run", "--show", "--help",
    };

    /// <summary>"Read-only", "Changes state" or "Destructive" — the word on the tier badge.</summary>
    public static string TierText(CommandTier tier) => tier switch
    {
        CommandTier.ReadOnly => "Read-only",
        CommandTier.Destructive => "Destructive",
        _ => "Changes state",
    };

    /// <summary>The tone key the badge maps to: Ok for a read, Warn for a change, Bad for a destructive one.</summary>
    public static string TierKey(CommandTier tier) => tier switch
    {
        CommandTier.ReadOnly => "Ok",
        CommandTier.Destructive => "Bad",
        _ => "Warn",
    };

    /// <summary>
    /// True when running <paramref name="argv"/> restarts the live gateway. Every <c>setup</c> verb that
    /// writes config.yaml does, unless it was told not to (<c>--no-restart</c>) or is only a preview or a
    /// read. When in doubt this says yes: an unneeded warning costs a sentence, a missing one costs a
    /// dropped hook connection.
    /// </summary>
    public static bool RestartsGateway(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0 || !string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (argv.Any(NonRestartingFlags.Contains))
        {
            return false;
        }

        var path = argv.TakeWhile(a => !a.StartsWith('-')).Take(CommandTiers.MaxPathTokens + 1);
        if (path.Any(NonRestartingVerbs.Contains))
        {
            return false;
        }

        return CommandTiers.Classify(argv) != CommandTier.ReadOnly;
    }

    /// <summary>The full restart paragraph for a wizard whose definition may offer a restart toggle.</summary>
    public static string RestartWarning(WizardDefinition definition, WizardValues values, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(argv);

        if (!RestartsGateway(argv))
        {
            return string.Empty;
        }

        var offersToggle = definition.VisibleFields(values).Any(f => f.Kind == WizardFieldKind.Toggle && f.NegativeFlag == "--no-restart");
        return RestartSentence +
               " Agents that use DefenseClaw hooks may lose the gateway for a few seconds while it comes back." +
               (offersToggle ? " Choose \"Disable (--no-restart)\" for Restart on the last page to skip the restart." : string.Empty);
    }
}
