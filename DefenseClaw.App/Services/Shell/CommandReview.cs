using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Text;

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
    /// <summary>The heading of <see cref="ArgumentExpansion"/>.</summary>
    public const string ArgumentExpansionTitle = "Arguments change on Windows";

    /// <summary>The heading of <see cref="SecretOutput"/>.</summary>
    public const string SecretOutputTitle = "Prints secret values";

    /// <summary>The heading of <see cref="UnusualCharacters"/>.</summary>
    public const string UnusualCharactersTitle = "Unusual characters in a name";

    /// <summary>The bar every command that restarts the live gateway carries.</summary>
    public static CommandReviewWarning GatewayRestart(string? message = null) =>
        new("Gateway restart", message ?? CommandReview.RestartNotice);

    /// <summary>
    /// The bar a command carries when the DefenseClaw CLI would rewrite one of its arguments before acting on it
    /// (see <see cref="ArgvHazards"/>): the command shown is then not the command that runs, so the bar says what
    /// each argument becomes. Not built for an argument that expands to itself.
    /// </summary>
    /// <param name="hazards">The arguments that change, paired with the step they belong to (0 when the review has one step).</param>
    public static CommandReviewWarning ArgumentExpansion(IReadOnlyList<(int Step, ArgvHazard Hazard)> hazards)
    {
        ArgumentNullException.ThrowIfNull(hazards);

        var lines = hazards
            .Select(h => (h.Step > 0 ? $"Step {h.Step}: " : string.Empty) + h.Hazard.Describe() + ".")
            .ToArray();
        return new CommandReviewWarning(
            ArgumentExpansionTitle,
            "The DefenseClaw CLI expands %VARIABLES%, $VARIABLES, ~ and wildcards in every argument on Windows - even after --. " +
            "It will not run exactly what is shown above: " + string.Join(' ', lines));
    }

    /// <summary>The bar a command carries when it asks the CLI to print values it normally masks.</summary>
    public static CommandReviewWarning SecretOutput() =>
        new(
            SecretOutputTitle,
            "This command prints secret values the CLI normally masks. The output lands in the Activity panel, in any log you " +
            "export and on the clipboard if you copy it.");

    /// <summary>
    /// The bar a review carries when a name it acts on - a skill, plugin, MCP server, registry entry or tool, which came from outside - has
    /// something in it other than printable ASCII: a right-to-left override, a zero-width or control character, a newline, or any
    /// non-ASCII letter (a homoglyph looks the same as the letter it imitates). The command above shows such a name with its control and
    /// format characters spelled out (<c>‮</c>); this says what is in it. Null when no name has anything unusual.
    /// </summary>
    /// <param name="names">The names the review acts on (what the command line carries after <c>--</c>, and any the surface lists itself).</param>
    public static CommandReviewWarning? UnusualCharacters(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var described = names
            .Distinct(StringComparer.Ordinal)
            .Select(DisplayNames.DescribeUnusual)
            .Where(d => d is not null)
            .ToArray();
        if (described.Length == 0)
        {
            return null;
        }

        var lead = described.Length == 1
            ? $"The name contains unusual characters ({described[0]})."
            : $"{described.Length} names contain unusual characters ({string.Join("; ", described)}).";
        return new CommandReviewWarning(
            UnusualCharactersTitle,
            lead + " It can look like a different name, so compare it with the command above, where such characters are spelled out. " +
            "The command that runs is exactly the one shown.");
    }
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

        // A sentence that names an item ("Approve <entry> from <source>.") is shown on one line with its control and format characters
        // spelled out, like the command below it. The argv above is what runs, and is not touched.
        Purpose = DisplayNames.Visible(purpose);
        Number = number;
        Tier = CommandReview.ResolveTier(Argv, floor);
        CommandText = CommandReview.CommandLine(executable, Argv);
        ClipboardText = CommandReview.ClipboardLine(executable, Argv);

        // Only the Python CLI re-expands its argv; the Go gateway does not.
        Hazards = ArgvHazards.AppliesTo(executable)
            ? ArgvHazards.FindChanges(Argv, CliWorkingDirectory.DefaultPath)
            : Array.Empty<ArgvHazard>();
        PrintsSecrets = CommandTiers.PrintsSecrets(Argv);
    }

    public IReadOnlyList<string> Argv { get; }

    /// <summary>
    /// The arguments the CLI would rewrite before running (<c>config.y?ml</c> becoming <c>config.yaml</c>), with what
    /// they become; empty for nearly every command. The review carries a warning for a step that has any.
    /// </summary>
    public IReadOnlyList<ArgvHazard> Hazards { get; }

    /// <summary>True when the command asks for secret values to be printed (<c>--reveal</c>, <c>--show-values</c>, <c>--show-credentials</c>).</summary>
    public bool PrintsSecrets { get; }

    public string Executable { get; }

    public string Purpose { get; }

    public int Number { get; }

    public CommandTier Tier { get; }

    /// <summary>The exact command as it will run, e.g. <c>defenseclaw skill block -- pdf-tools</c>. For reading; see <see cref="ClipboardText"/> for pasting.</summary>
    public string CommandText { get; }

    /// <summary>
    /// The same command as text that is safe to paste into PowerShell - each argument one literal string - which is
    /// what the Copy button puts on the clipboard. <see cref="CommandText"/> quotes only whitespace, so a name such as
    /// <c>x&amp;calc</c> would paste as two commands.
    /// </summary>
    public string ClipboardText { get; }

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
    private readonly string _title = string.Empty;
    private readonly IReadOnlyList<CommandReviewWarning> _warnings = Array.Empty<CommandReviewWarning>();

    /// <summary>
    /// What the dialog asks, as a question or a title: "Remove skill “x”?". One line, whatever it is given: the name of an item inside it came from
    /// outside, so its control and format characters are spelled out (<see cref="DisplayNames.Visible"/>) - a right-to-left override or a newline in
    /// a skill's name cannot turn the question into another one.
    /// </summary>
    public required string Title
    {
        get => _title;
        init => _title = DisplayNames.Visible(value);
    }

    /// <summary>
    /// Names this review acts on that came from outside and that the command line does not carry after a <c>--</c> (a registry entry the CLI takes
    /// before its options): they are checked for unusual characters like the targets after <c>--</c> are, and the review says so
    /// (<see cref="CommandReviewWarning.UnusualCharacters"/>). Empty for nearly every review, which acts on at most the targets.
    /// </summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>The commands in run order; a review has at least one.</summary>
    public required IReadOnlyList<CommandReviewStep> Steps { get; init; }

    /// <summary>One or two sentences on what running it does to the operator's install; empty hides the line.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Consequences to read before deciding, each drawn as a warning bar: the ones the caller supplied, then the
    /// ones the commands themselves call for - an argument the CLI would rewrite
    /// (<see cref="CommandReviewWarning.ArgumentExpansion"/>) and a request for secret values
    /// (<see cref="CommandReviewWarning.SecretOutput"/>). Derived here, so every surface that builds a review gets them
    /// without asking, and a caller cannot forget one.
    /// </summary>
    public IReadOnlyList<CommandReviewWarning> Warnings
    {
        get => WithDerivedWarnings(_warnings);
        init => _warnings = value ?? Array.Empty<CommandReviewWarning>();
    }

    private IReadOnlyList<CommandReviewWarning> WithDerivedWarnings(IReadOnlyList<CommandReviewWarning> supplied)
    {
        if (Steps is null)
        {
            return supplied;
        }

        List<CommandReviewWarning>? derived = null;

        var changes = Steps
            .SelectMany(s => s.Hazards.Select(h => (Step: Steps.Count > 1 ? s.Number : 0, Hazard: h)))
            .ToArray();
        if (changes.Length > 0 && !supplied.Any(w => w.Title == CommandReviewWarning.ArgumentExpansionTitle))
        {
            (derived ??= new()).Add(CommandReviewWarning.ArgumentExpansion(changes));
        }

        if (Steps.Any(s => s.PrintsSecrets) && !supplied.Any(w => w.Title == CommandReviewWarning.SecretOutputTitle))
        {
            (derived ??= new()).Add(CommandReviewWarning.SecretOutput());
        }

        if (!supplied.Any(w => w.Title == CommandReviewWarning.UnusualCharactersTitle) &&
            CommandReviewWarning.UnusualCharacters(TargetsAndNames()) is { } unusual)
        {
            (derived ??= new()).Add(unusual);
        }

        return derived is null ? supplied : supplied.Concat(derived).ToArray();
    }

    /// <summary>
    /// The names a review is about: every argument after a <c>--</c> in any step (the CLI is told that what follows is a target, an item named by
    /// something outside this app) and <see cref="Names"/>.
    /// </summary>
    private IEnumerable<string> TargetsAndNames()
    {
        foreach (var step in Steps)
        {
            var terminator = -1;
            for (var i = 0; i < step.Argv.Count; i++)
            {
                if (string.Equals(step.Argv[i], "--", StringComparison.Ordinal))
                {
                    terminator = i;
                    break;
                }
            }

            if (terminator >= 0)
            {
                for (var i = terminator + 1; i < step.Argv.Count; i++)
                {
                    yield return step.Argv[i];
                }
            }
        }

        foreach (var name in Names)
        {
            yield return name;
        }
    }

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

    /// <summary>Every command as it reads, one per line: what the dialog shows.</summary>
    public string CommandText => string.Join(Environment.NewLine, Steps.Select(s => s.CommandText));

    /// <summary>
    /// Every command as text that is safe to paste into PowerShell, one per line: what the Copy button puts on
    /// the clipboard (see <see cref="CommandReviewStep.ClipboardText"/>).
    /// </summary>
    public string ClipboardText => string.Join(Environment.NewLine, Steps.Select(s => s.ClipboardText));

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

    /// <summary>
    /// True when the app may run <paramref name="argv"/> on <paramref name="executable"/> with <b>no review step</b>: the argv is on the
    /// explicit allow-list of known reads (<see cref="CommandTiers.UnreviewedReadPaths"/>, or the gateway's
    /// <see cref="CommandTiers.UnreviewedGatewayReadPaths"/>) <i>and</i> <see cref="CommandTiers"/> still calls it read-only. The
    /// classifier alone is never enough: it reads the first verb of the path, so a command it calls read-only (<c>plan apply</c>,
    /// <c>validate fix</c>, any verb a future CLI adds under a read-only noun) may change state. Used wherever the app picks a command
    /// to run for the operator without asking: the palette, Background diagnose, the Overview's Diagnostics and doctor.
    /// </summary>
    /// <param name="executable">What <paramref name="argv"/> is handed to: <c>defenseclaw</c> or <c>defenseclaw-gateway</c> (anything else is never unreviewed).</param>
    public static bool MayRunUnreviewed(string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(argv);

        var name = Path.GetFileNameWithoutExtension(executable);
        var listed = string.Equals(name, DefaultExecutable, StringComparison.OrdinalIgnoreCase)
            ? CommandTiers.IsUnreviewedRead(argv)
            : string.Equals(name, GatewayControl.Executable, StringComparison.OrdinalIgnoreCase) && CommandTiers.IsUnreviewedGatewayRead(argv);

        return listed && ResolveTier(argv) == CommandTier.ReadOnly;
    }

    /// <summary><see cref="MayRunUnreviewed(string, IReadOnlyList{string})"/> for the <c>defenseclaw</c> CLI.</summary>
    public static bool MayRunUnreviewed(IReadOnlyList<string> argv) => MayRunUnreviewed(DefaultExecutable, argv);

    /// <summary>The command as an operator reads (and copies) it: the executable, then each argument, quoted for display.</summary>
    public static string CommandLine(string executable, IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var args = string.Join(' ', argv.Select(Quote));
        return args.Length == 0 ? executable : executable + " " + args;
    }

    /// <summary>
    /// The command as text to paste into PowerShell: <see cref="PowerShellQuoting.CommandLine"/>, so every argument
    /// arrives as one literal string whatever it contains. <see cref="CommandLine"/> is for reading and is not safe to paste.
    /// </summary>
    public static string ClipboardLine(string executable, IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return string.IsNullOrEmpty(executable) ? string.Join(' ', argv.Select(PowerShellQuoting.Argument)) : PowerShellQuoting.CommandLine(executable, argv);
    }

    /// <summary>
    /// Display quoting only (the runner passes an argument list, no shell is involved): empty or spaced values get quotes. An argument is
    /// shown with its control and format characters spelled out (<c>‮</c>, <c>\n</c>; <see cref="DisplayNames.Visible"/>) and so on one
    /// line: a name from outside cannot make the command box read as another command. The argv that runs, and the text the Copy button puts on
    /// the clipboard (<see cref="ClipboardLine"/>), are not changed.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var shown = DisplayNames.Visible(argument);
        if (shown.Length == 0)
        {
            return "\"\"";
        }

        return shown.Any(char.IsWhiteSpace)
            ? "\"" + shown.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : shown;
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

        if (argv.Count > 0 && string.Equals(argv[0], "guardrail", StringComparison.OrdinalIgnoreCase))
        {
            return GuardrailRestartsGateway(argv);
        }

        if (argv.Count == 0 || !string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Only a flag counts: one that is the value of another option (a webhook named "--show") or the option's
        // own argument does not turn the restart off, and nothing after a "--" is a flag at all.
        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < options.Length; i++)
        {
            if (NonRestartingFlags.Contains(options[i]) && CommandTiers.IsStandaloneFlag(options, i))
            {
                return false;
            }
        }

        var path = argv.TakeWhile(a => !a.StartsWith('-')).Take(CommandTiers.MaxPathTokens);
        if (path.Any(NonRestartingVerbs.Contains))
        {
            return false;
        }

        return CommandTiers.Classify(argv) != CommandTier.ReadOnly;
    }

    /// <summary>Options of the guardrail verbs that take a value, so the value is not mistaken for a positional.</summary>
    private static readonly HashSet<string> GuardrailValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--connector", "--min-severity", "--timeout",
    };

    /// <summary>
    /// <c>guardrail</c> verbs restart the gateway by default (<c>--restart</c> is on) and say so in their own --help. The reads do
    /// not: <c>status</c>, <c>list-packs</c>, <c>judge list</c>, and <c>fail-mode</c> / <c>hilt</c> / <c>block-message</c> with
    /// nothing to set. <c>enable</c>, <c>disable</c> and <c>judge add|remove</c> always write; the other three write when given a
    /// value (a positional, <c>--min-severity</c>, <c>--clear</c>) or <c>--yes</c>, which only a write has. Unknown verbs say yes.
    /// </summary>
    private static bool GuardrailRestartsGateway(IReadOnlyList<string> argv)
    {
        var terminator = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                terminator = i;
                break;
            }
        }

        var options = terminator < 0 ? argv : argv.Take(terminator).ToArray();
        var afterTerminator = terminator >= 0 && terminator < argv.Count - 1;

        // Only a standalone flag counts, as for setup: "--connector --no-restart" names a connector.
        for (var i = 1; i < options.Count; i++)
        {
            if (string.Equals(options[i], "--no-restart", StringComparison.OrdinalIgnoreCase) && CommandTiers.IsStandaloneFlag(options, i))
            {
                return false;
            }

            if (NonRestartingFlags.Contains(options[i]) && CommandTiers.IsStandaloneFlag(options, i))
            {
                return false;
            }
        }

        // Positionals: what is left once the options and their values are taken out.
        var positionals = new List<string>();
        var hasWriteOption = false;
        for (var i = 1; i < options.Count; i++)
        {
            var token = options[i];
            if (token.StartsWith('-'))
            {
                if (string.Equals(token, "--clear", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "--yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "--min-severity", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "--enable", StringComparison.OrdinalIgnoreCase))
                {
                    hasWriteOption = true;
                }

                if (GuardrailValueOptions.Contains(token))
                {
                    i++;
                }

                continue;
            }

            positionals.Add(token);
        }

        if (positionals.Count == 0)
        {
            return false;
        }

        switch (positionals[0].ToLowerInvariant())
        {
            case "enable":
            case "disable":
                return true;
            case "status":
            case "list-packs":
                return false;
            case "judge":
                // judge list reads; add / remove write.
                return positionals.Count > 1 && positionals[1].ToLowerInvariant() is "add" or "remove";
            case "fail-mode":
            case "hilt":
            case "block-message":
                return positionals.Count > 1 || hasWriteOption || afterTerminator;
            default:
                return true;
        }
    }
}
