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

    /// <summary>
    /// True for <c>setup [group options] openclaw [options] --replace</c>. The target is the first token after <c>setup</c> that is not one of the group's
    /// options (cmd_setup.py@1.0.10:547-600: <c>--connector/-c</c> and <c>--mode</c> take a value, the rest are flags; the only alias in the group is
    /// <c>claudecode</c>, and <c>openclaw</c> has none). <c>--replace</c> is a plain flag (<c>is_flag</c>), which click gives no <c>--replace=value</c> or
    /// abbreviated spelling. A <c>--</c> that comes before the target is click's own end of options (the target is the next token); a <c>--</c> after
    /// the target ends the command's options, so <c>setup openclaw -- --replace</c> is a positional and does not apply.
    /// </summary>
    public static bool Applies(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count < 3 || !string.Equals(argv[0], "setup", StringComparison.Ordinal))
        {
            return false;
        }

        var target = -1;
        for (var i = 1; i < argv.Count; i++)
        {
            var token = argv[i];
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                if (i + 1 < argv.Count)
                {
                    target = i + 1;
                }

                break;
            }

            if (!token.StartsWith('-') || token.Length == 1)
            {
                target = i;
                break;
            }

            if (TakesNextToken(token))
            {
                i++;
            }
        }

        if (target < 0 || !string.Equals(argv[target], "openclaw", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = target + 1; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(argv[i], "--replace", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the group option <paramref name="token"/> is spelled so that its value is the next token (<c>--mode observe</c>, <c>-c codex</c>, <c>-yc codex</c>).</summary>
    private static bool TakesNextToken(string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            return string.Equals(token, "--connector", StringComparison.Ordinal) || string.Equals(token, "--mode", StringComparison.Ordinal);
        }

        // A short bundle: flags run on until the first value option; `c` takes the rest of the token, or the next token when it ends the bundle.
        return token.IndexOf('c', 1) == token.Length - 1;
    }

    /// <summary>The bar, or none.</summary>
    public static IEnumerable<CommandReviewWarning> Warnings(IReadOnlyList<string> argv)
    {
        if (Applies(argv))
        {
            yield return new CommandReviewWarning(
                Title,
                "OpenClaw runs alone in this release. --replace removes every configured hook connector first, so DefenseClaw stops checking those agents. " +
                "Without --replace, setup refuses and changes nothing.");
        }
    }
}
