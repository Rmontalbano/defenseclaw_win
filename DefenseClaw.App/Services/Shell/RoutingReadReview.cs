using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// What the palette's review says about the <c>routing</c> commands this app offers (runtime 1.0.10, <see cref="RoutingCliExclusions.OfferedRoutingCommands"/>).
/// The command tiers do not know them, so the review carries the "Changes state" badge; this sentence says what each really does, from
/// <c>cmd_routing.py</c> at 1.0.10, so the text does not leave the badge unexplained. The tier and the list of commands that run without review are unchanged.
/// </summary>
internal static class RoutingReadReview
{
    /// <summary>The sentence for the seven routing commands that only read.</summary>
    public const string ReadNote =
        " It only reads routing settings and routing state and changes nothing. It is not on the app's list of read-only commands, so it is reviewed first.";

    /// <summary>The sentence for <c>routing test</c>, a dry run.</summary>
    public const string TestNote =
        " A dry run: the gateway may send the prompt to the configured classifier, which can be a hosted model. " +
        "The routed model is not called and nothing is saved. It is not on the app's list of read-only commands, so it is reviewed first.";

    /// <summary>The sentence for <paramref name="argv"/> when it is one of the offered routing commands (options after the words do not matter), else empty.</summary>
    public static string Note(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        foreach (var command in RoutingCliExclusions.OfferedRoutingCommands)
        {
            var words = command.Split(' ');
            if (argv.Count >= words.Length && words.Select((w, i) => string.Equals(w, argv[i], StringComparison.Ordinal)).All(static same => same))
            {
                return command == "routing test" ? TestNote : ReadNote;
            }
        }

        return string.Empty;
    }
}
