namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The review bar for <c>setup openclaw --replace</c> (runtime 1.0.10). OpenClaw runs alone in this release, so setup refuses while hook connectors are
/// configured, and <c>--replace</c> is the switch that removes every one of them first (cmd_setup.py@1.0.10 <c>_refuse_proxy_next_to_hook_connectors</c>,
/// 15501-15524, and the option's help "Switch to OpenClaw: remove every configured hook connector (it cannot run next to them)"). The wizard's own field
/// help says so; this bar says it again beside the exact command, and the review is Destructive.
/// </summary>
internal static class OpenClawReplaceReview
{
    /// <summary>The heading of the bar.</summary>
    public const string Title = "Removes every other connector";

    /// <summary>True for <c>setup openclaw ... --replace</c> (before any <c>--</c>).</summary>
    public static bool Applies(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        return options.Length >= 2 &&
               string.Equals(options[0], "setup", StringComparison.Ordinal) &&
               string.Equals(options[1], "openclaw", StringComparison.Ordinal) &&
               options.Contains("--replace", StringComparer.Ordinal);
    }

    /// <summary>The bar, or none.</summary>
    public static IEnumerable<CommandReviewWarning> Warnings(IReadOnlyList<string> argv)
    {
        if (Applies(argv))
        {
            yield return new CommandReviewWarning(
                Title,
                "OpenClaw runs alone in this release. With --replace, setup removes every configured hook connector (Codex, Claude Code and the others) " +
                "before it sets up OpenClaw, so DefenseClaw stops checking those agents. To keep them, leave --replace off: setup then refuses and changes nothing.");
        }
    }
}
