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
        " It only reads routing settings and the gateway's routing state; it changes nothing. It is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first.";

    /// <summary>The sentence for <c>routing test</c>, a dry run.</summary>
    public const string TestNote =
        " A dry run: the gateway decides where the prompt would go, and may send the prompt to the configured classifier to do so (that can be a hosted model). " +
        "The model the prompt would be routed to is not called, and nothing is saved. " +
        "It is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first.";

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
