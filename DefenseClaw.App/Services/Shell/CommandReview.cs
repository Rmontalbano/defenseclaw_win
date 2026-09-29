using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>Where a review dialog is in its life: waiting for a decision, running, or showing what happened.</summary>
public enum CommandReviewPhase
{
    /// <summary>Waiting for the operator: Cancel and the confirm button are live.</summary>
    Review,

    /// <summary>The commands are running; nothing can be cancelled from here.</summary>
    Running,

    /// <summary>Done: only Close is offered.</summary>
    Finished,
}

/// <summary>A consequence worth stating next to the command, drawn as a warning bar (<c>Title</c> is its heading).</summary>
public sealed record CommandReviewWarning(string Title, string Message)
{
    /// <summary>The bar every command that restarts the live gateway carries.</summary>
    public static CommandReviewWarning GatewayRestart(string? message = null) =>
        new("Gateway restart", message ?? CommandReview.RestartNotice);
}

/// <summary>
/// One command in a <see cref="CommandReview"/>: the exact argv, the tier it resolves to, and (for a
/// review that runs several in order) the status of its run. Everything but the status is fixed when it is built.
/// </summary>
public sealed partial class CommandReviewStep : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    /// <summary>Tone key of the status badge (Ok / Warn / Bad / Neutral).</summary>
    [ObservableProperty]
    private string _statusKey = "Neutral";

    /// <param name="argv">The arguments as handed to the CLI, without the executable.</param>
    /// <param name="purpose">One sentence on what this step does; shown when the review has several steps.</param>
    /// <param name="floor">The lowest tier this step may show: <see cref="CommandTiers"/> can only raise it.</param>
    /// <param name="executable">What <paramref name="argv"/> is handed to, for display.</param>
    /// <param name="number">The step's position, 1-based; 0 when it stands alone.</param>
    public CommandReviewStep(
        IReadOnlyList<string> argv,
        string purpose = "",
        CommandTier floor = CommandTier.ReadOnly,
        string executable = CommandReview.DefaultExecutable,
        int number = 0)
    {
        ArgumentNullException.ThrowIfNull(argv);
        Argv = argv.ToArray();
        Executable = executable;
        Purpose = purpose;
        Number = number;
        Tier = CommandReview.ResolveTier(Argv, floor);
        CommandText = CommandReview.CommandLine(executable, Argv);
    }

    public IReadOnlyList<string> Argv { get; }

    public string Executable { get; }

    public string Purpose { get; }

    public int Number { get; }

    public CommandTier Tier { get; }

    /// <summary>The exact command as it will run, e.g. <c>defenseclaw skill block -- pdf-tools</c>.</summary>
    public string CommandText { get; }

    public string Heading => Number > 0 ? $"Step {Number}" : string.Empty;

    /// <summary>The name a screen reader gives the box holding the argv.</summary>
    public string AutomationName => Number > 0 ? $"Command for step {Number}" : "Command that will run";

    public string TierLabel => CommandReview.LabelFor(Tier);

    public string TierKey => CommandReview.ToneFor(Tier);

    public bool HasStatus => StatusText.Length > 0;

    public void SetStatus(string text, string key)
    {
        StatusText = text;
        StatusKey = key;
    }

    public override string ToString() =>
        $"{(Number > 0 ? $"Step {Number}: " : string.Empty)}{CommandText}. {Purpose} {StatusText}".TrimEnd() + ".";
}

/// <summary>
/// Everything a confirmation surface says about a command before it runs — the one model behind the
/// shared <c>CommandReviewControl</c>, so the Govern, Discover, gateway, wizard and guardrail reviews
/// cannot drift apart.
/// <para>
/// <b>Tier.</b> Each step's tier is the stricter of what <see cref="CommandTiers.Classify"/> says about
/// its argv and a floor the caller supplies (<c>StateChanging</c> for a surface that exists only to
/// review a change, <c>Destructive</c> for a verb the classifier cannot know is dangerous). It only ever
/// goes up from there, so a target or a flag value that spells a read-only verb can never make a
/// mutation look harmless. The review's tier is its strictest step.
/// </para>
/// </summary>
public sealed record CommandReview
{
    /// <summary>The executable a review shows in front of the argv unless it says otherwise.</summary>
    public const string DefaultExecutable = "defenseclaw";

    /// <summary>The sentence every command that restarts the live gateway must carry.</summary>
    public const string RestartSentence = "This restarts the DefenseClaw gateway.";

    /// <summary>The restart paragraph shown in the "Gateway restart" bar.</summary>
    public const string RestartNotice =
        RestartSentence + " Agents that use DefenseClaw hooks may lose the gateway for a few seconds while it comes back.";

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

    private readonly string? _confirmLabel;

    /// <summary>What the dialog asks, as a question or a title: "Remove skill “x”?".</summary>
    public required string Title { get; init; }

    /// <summary>The commands in run order; a review has at least one.</summary>
    public required IReadOnlyList<CommandReviewStep> Steps { get; init; }

    /// <summary>One or two sentences on what running it does to the operator's install; empty hides the line.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Consequences to read before deciding, each drawn as a warning bar.</summary>
    public IReadOnlyList<CommandReviewWarning> Warnings { get; init; } = Array.Empty<CommandReviewWarning>();

    /// <summary>True when running it bounces the live gateway; adds a badge next to the tier. Pair it with <see cref="CommandReviewWarning.GatewayRestart"/>.</summary>
    public bool RestartsGateway { get; init; }

    /// <summary>The confirm button's text. Defaults to "Run command" / "Run destructive command".</summary>
    public string ConfirmLabel
    {
        get => _confirmLabel is { Length: > 0 } label ? label : DefaultConfirmLabel(Tier);
        init => _confirmLabel = value;
    }

    public string CancelLabel { get; init; } = "Cancel";

    /// <summary>The strictest tier of any step.</summary>
    public CommandTier Tier => Steps.Count == 0 ? CommandTier.StateChanging : Steps.Max(s => s.Tier);

    public bool IsDestructive => Tier == CommandTier.Destructive;

    public bool HasMultipleSteps => Steps.Count > 1;

    /// <summary>"Read-only", "Changes state" or "Destructive" — the word on the tier badge.</summary>
    public string TierLabel => LabelFor(Tier);

    /// <summary>Neutral / Warn / Bad — the tone key of the tier badge.</summary>
    public string TierKey => ToneFor(Tier);

    /// <summary>Every command, one per line: what the Copy button puts on the clipboard.</summary>
    public string CommandText => string.Join(Environment.NewLine, Steps.Select(s => s.CommandText));

    /// <summary>The name a screen reader gives the dialog.</summary>
    public string AutomationName => "Review command: " + Title;

    /// <summary>What a screen reader says about the tier and the way out.</summary>
    public string AutomationHelp =>
        $"{TierLabel} command. Nothing runs until you confirm. Escape cancels.";

    /// <summary>A review of one command; the common case.</summary>
    /// <param name="title">The question the dialog asks.</param>
    /// <param name="argv">The arguments as handed to the CLI, without the executable.</param>
    /// <param name="floor">The lowest tier to show; <see cref="CommandTiers"/> can only raise it.</param>
    /// <param name="executable">What <paramref name="argv"/> is handed to, for display.</param>
    public static CommandReview ForCommand(
        string title,
        IReadOnlyList<string> argv,
        CommandTier floor = CommandTier.ReadOnly,
        string executable = DefaultExecutable) =>
        new()
        {
            Title = title,
            Steps = new[] { new CommandReviewStep(argv, floor: floor, executable: executable) },
        };

    /// <summary>The label a confirm button gets when the surface does not name the action.</summary>
    public static string DefaultConfirmLabel(CommandTier tier) =>
        tier == CommandTier.Destructive ? "Run destructive command" : "Run command";

    /// <summary>"Read-only", "Changes state" or "Destructive".</summary>
    public static string LabelFor(CommandTier tier) => tier switch
    {
        CommandTier.ReadOnly => "Read-only",
        CommandTier.Destructive => "Destructive",
        _ => "Changes state",
    };

    /// <summary>
    /// The tone key the badge maps to (see the TONES index in <c>Themes\DefenseClaw.xaml</c>): neutral for a
    /// read, amber for a change, red for a destructive one. Colour is never the only cue — the badge says the word.
    /// </summary>
    public static string ToneFor(CommandTier tier) => tier switch
    {
        CommandTier.ReadOnly => "Neutral",
        CommandTier.Destructive => "Bad",
        _ => "Warn",
    };

    /// <summary>
    /// The stricter of the classifier's verdict on <paramref name="argv"/> and <paramref name="floor"/>.
    /// Only what comes before a <c>--</c> is classified: everything after it is a positional target (a
    /// skill or tool name from outside), so a target spelled <c>--help</c> cannot read as a preview flag.
    /// </summary>
    public static CommandTier ResolveTier(IReadOnlyList<string> argv, CommandTier floor = CommandTier.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var terminator = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                terminator = i;
                break;
            }
        }

        return Stricter(CommandTiers.Classify(terminator < 0 ? argv : argv.Take(terminator).ToArray()), floor);
    }

    /// <summary>The stricter of two tiers: Destructive beats StateChanging beats ReadOnly.</summary>
    public static CommandTier Stricter(CommandTier a, CommandTier b) => a > b ? a : b;

    /// <summary>The command as an operator reads (and copies) it: the executable, then each argument, quoted for display.</summary>
    public static string CommandLine(string executable, IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var args = string.Join(' ', argv.Select(Quote));
        return args.Length == 0 ? executable : executable + " " + args;
    }

    /// <summary>Display quoting only (the runner passes an argument list, no shell is involved): empty or spaced values get quotes.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace)
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;
    }

    /// <summary>
    /// True when running <paramref name="argv"/> restarts the live gateway. Every <c>setup</c> verb that
    /// writes config.yaml does, unless it was told not to (<c>--no-restart</c>) or is only a preview or a
    /// read. When in doubt this says yes: an unneeded warning costs a sentence, a missing one costs a
    /// dropped hook connection.
    /// </summary>
    public static bool RestartsGatewayFor(IReadOnlyList<string> argv)
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
}
