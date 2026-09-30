using DefenseClaw.Core.Security;

namespace DefenseClaw.App.Services;

/// <summary>
/// The warning a confirmation shows when a value the operator typed into a form is about to become part of a
/// command line and looks like a secret (<see cref="SecretHeuristics"/>).
/// <para>
/// This app never puts a secret it <i>knows</i> on argv — <c>CliRunner</c> refuses one — but a value typed into
/// the MCP, plugin or wizard forms is not known to it. Once it is on the command line it is visible to every
/// process on the machine, shown in the Activity list, written to the exported log and echoed by Copy command.
/// The warning says so, and says what to do instead where the installed CLI has another route: the callers pass
/// that advice, because it is a fact about one CLI verb and only they know which verb it is.
/// </para>
/// It is built on the existing <see cref="CommandReviewWarning"/> bar rather than a new control, so it appears
/// in every review surface that draws warnings.
/// </summary>
internal static class SecretFieldWarnings
{
    /// <summary>The bar's heading.</summary>
    public const string Title = "A secret would be on the command line";

    /// <summary>
    /// A warning for one <paramref name="value"/>, or null when it does not look like a secret. <paramref name="field"/>
    /// names where it came from ("The URL"); <paramref name="advice"/> is what to do instead.
    /// </summary>
    public static CommandReviewWarning? For(string field, string? value, string advice)
    {
        var kind = SecretHeuristics.Explain(value);
        return kind is null ? null : Build(field, plural: false, kind, advice);
    }

    /// <summary>
    /// One warning for a field made of several <paramref name="parts"/> (the MCP form's arguments, one per line;
    /// its environment entries): the parts are checked one at a time and also joined, because a secret can span
    /// two of them (<c>--api-key</c> on one line and its value on the next). Null when nothing looks secret.
    /// </summary>
    public static CommandReviewWarning? ForParts(string singular, string plural, IReadOnlyList<string> parts, string advice)
    {
        var kinds = new List<string>();
        var flagged = 0;
        foreach (var part in parts)
        {
            if (SecretHeuristics.Explain(part) is { } kind)
            {
                flagged++;
                if (!kinds.Contains(kind, StringComparer.Ordinal))
                {
                    kinds.Add(kind);
                }
            }
        }

        if (flagged == 0 && parts.Count > 1 && SecretHeuristics.Explain(string.Join(' ', parts)) is { } joined)
        {
            kinds.Add(joined);
            flagged = 1;
        }

        return flagged == 0
            ? null
            : Build(flagged == 1 ? singular : plural, plural: flagged != 1, string.Join(", ", kinds), advice);
    }

    /// <summary>
    /// One warning per non-null value in <paramref name="warnings"/>, appended to whatever
    /// <paramref name="review"/> already warns about (a restart, an argument that is expanded) — never replacing it.
    /// </summary>
    public static CommandReview AppendTo(CommandReview review, IEnumerable<CommandReviewWarning?> warnings)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(warnings);

        var added = warnings.OfType<CommandReviewWarning>().ToArray();
        return added.Length == 0
            ? review
            : review with { Warnings = review.Warnings.Concat(added).ToArray() };
    }

    /// <summary>
    /// The warnings for a wizard's command: every value in <paramref name="argv"/> that looks like a secret, named
    /// by the option it follows (<c>--url</c>). Never echoes a value. At most one warning, however many values.
    /// </summary>
    public static CommandReviewWarning? ForArgv(IReadOnlyList<string> argv, string advice)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var labels = new List<string>();
        var kinds = new List<string>();
        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token.StartsWith("--", StringComparison.Ordinal) || SecretHeuristics.Explain(token) is not { } kind)
            {
                continue;
            }

            var label = i > 0 && argv[i - 1].StartsWith("--", StringComparison.Ordinal)
                ? argv[i - 1]
                : $"argument {i + 1}";
            if (!labels.Contains(label, StringComparer.Ordinal))
            {
                labels.Add(label);
            }

            if (!kinds.Contains(kind, StringComparer.Ordinal))
            {
                kinds.Add(kind);
            }
        }

        if (labels.Count == 0)
        {
            return null;
        }

        var field = labels.Count == 1 ? $"The value of {labels[0]}" : $"The values of {string.Join(", ", labels)}";
        return Build(field, plural: labels.Count != 1, string.Join(", ", kinds), advice);
    }

    private static CommandReviewWarning Build(string field, bool plural, string kind, string advice) =>
        new(Title,
            $"{field} {(plural ? "look like they carry" : "looks like it carries")} a secret ({kind}). It will be visible on " +
            $"the command line to every process on this machine, shown in Activity and written to the exported log. {advice}");
}
