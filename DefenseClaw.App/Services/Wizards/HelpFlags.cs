namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Questions about a parsed <c>--help</c> screen that the setup flows ask before they put a flag on a command line: a flow built on the 0.8.10
/// surface checks that the installed CLI still lists each flag it would send (a curated layout drops what the CLI no longer has; a form that
/// builds its own command does the same, here).
/// </summary>
internal static class HelpFlags
{
    /// <summary>
    /// True when the screen lists <paramref name="flag"/> as an option - under any of its spellings, and as the negative half of a pair
    /// (<c>--restart / --no-restart</c> lists both, though the parser keeps only <c>--restart</c> among the names).
    /// </summary>
    public static bool Lists(this ParsedHelp help, string flag)
    {
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(flag);

        return help.Options.Any(o =>
            o.Names.Contains(flag, StringComparer.Ordinal) || string.Equals(o.NegativeFlag, flag, StringComparison.Ordinal));
    }

    /// <summary>The values of the first positional argument when the usage line gives a choice list (<c>{a|b|c}</c>); empty otherwise.</summary>
    public static IReadOnlyList<string> FirstChoices(this ParsedHelp help)
    {
        ArgumentNullException.ThrowIfNull(help);

        return help.Positionals.FirstOrDefault()?.Choices ?? Array.Empty<string>();
    }
}
