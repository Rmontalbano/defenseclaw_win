using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services;

/// <summary>
/// The one value the palette asks for when a registry entry needs an argument it can take safely: a name, URL or path, or one word from a
/// fixed set. It is read off the TUI's hint (<c>&lt;skill-name&gt;</c>, <c>&lt;observe|action&gt;</c>); a hint with more in it (a flag to
/// add, a list of options) is not a form and the entry is copied for the operator to finish instead.
/// </summary>
/// <param name="Label">What is asked for (<c>skill-name</c>); for a choice, "one of observe, action".</param>
/// <param name="Choices">The words allowed, or empty for free text.</param>
internal sealed partial record ArgumentForm(string Label, IReadOnlyList<string> Choices)
{
    /// <summary>The longest value taken. A name, URL or path is far shorter; anything longer is a mistake or a paste of something else.</summary>
    public const int MaxLength = 512;

    public bool IsChoice => Choices.Count > 0;

    /// <summary>The empty box's hint: the word's name, or "observe or action" / "HIGH, MEDIUM or LOW" for a choice.</summary>
    public string Placeholder => !IsChoice
        ? Label
        : Choices.Count == 1 ? Choices[0] : string.Join(", ", Choices.Take(Choices.Count - 1)) + " or " + Choices[^1];

    /// <summary>The form for a TUI hint, or null when the hint is anything but one <c>&lt;word&gt;</c> or one <c>&lt;a|b|c&gt;</c>.</summary>
    public static ArgumentForm? Parse(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint) || SingleWord().Match(hint.Trim()) is not { Success: true } match)
        {
            return null;
        }

        var words = match.Groups["words"].Value.Split('|');
        return words.Length == 1 ? new ArgumentForm(words[0], Array.Empty<string>()) : new ArgumentForm("one of " + string.Join(", ", words), words);
    }

    /// <summary>
    /// Null when <paramref name="text"/> is a value the form accepts, with <paramref name="value"/> set to what goes on the command line
    /// (trimmed; a choice in the spelling the command knows). Otherwise the sentence that says what is wrong.
    /// </summary>
    public string? Check(string? text, out string value)
    {
        value = string.Empty;

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return IsChoice ? $"Type {Placeholder}." : $"Type the {Label}.";
        }

        if (trimmed.Length > MaxLength)
        {
            return $"That is longer than {MaxLength} characters.";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "Line breaks and other control characters cannot go on a command line.";
        }

        if (IsChoice)
        {
            var match = Choices.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return $"Use {Placeholder}.";
            }

            value = match;
            return null;
        }

        value = trimmed;
        return null;
    }

    [GeneratedRegex(@"^<(?<words>[A-Za-z][A-Za-z0-9_.-]*(?:\|[A-Za-z][A-Za-z0-9_.-]*)*)>$")]
    private static partial Regex SingleWord();
}

/// <summary>
/// One CLI command the palette offers: an entry of the connected runtime's TUI command registry (the Mac's command sheet, "all 253
/// current TUI entries"), as the argv the TUI itself would run, a category and the TUI's own one-line description. The argv carries the
/// few fixed options the registry gives it (<c>--yes</c>, <c>--json</c> ...; <see cref="TuiRegistryCatalogues.ReviewedFlags"/>) and
/// nothing an operator typed, so there is nothing a secret could ride on; a value the operator types is added after a <c>--</c> and is
/// always reviewed.
/// </summary>
/// <param name="Argv">The arguments without the executable, e.g. <c>agent</c>, <c>discovery</c>, <c>scan</c>. Never a shell line.</param>
/// <param name="Category">One of <see cref="CuratedCommandCatalog.Categories"/>.</param>
/// <param name="Summary">The first help sentence, or the TUI's description.</param>
/// <param name="Usage">The usage line, kept to say what an unrunnable command still needs.</param>
/// <param name="RequiredArguments">What it cannot run without, in the words it is asked for; empty when it runs as it stands.</param>
/// <param name="Executable"><c>defenseclaw</c>, or <c>defenseclaw-gateway</c> for the gateway's own verbs.</param>
/// <param name="TuiName">The registry's name for it (<c>scan skill --all</c>); null for a command that did not come from a registry.</param>
/// <param name="ArgumentHint">The registry's hint for what it needs (<c>&lt;skill-name&gt;</c>); empty when nothing.</param>
internal sealed record CuratedCommand(
    IReadOnlyList<string> Argv,
    string Category,
    string Summary,
    string Usage,
    IReadOnlyList<string> RequiredArguments,
    string Executable = CommandReview.DefaultExecutable,
    string? TuiName = null,
    string ArgumentHint = "")
{
    /// <summary>
    /// Argv (the whole line, joined) that read a hidden prompt or a menu from the console, which a window without one cannot answer: the
    /// bare <c>setup</c> is the connector picker, <c>keys set</c> and <c>keys fill-missing</c> use <c>getpass</c>. A registry description that says
    /// "interactive" joins them (<see cref="NeedsTerminal"/>).
    /// </summary>
    private static readonly HashSet<string> NeedTheConsole = new(StringComparer.Ordinal) { "setup", "keys set", "keys fill-missing --yes" };

    /// <summary>
    /// Stable across runs and across catalogues - what a remembered "last command" can store: <c>cli.</c> and the registry's name with its
    /// spaces as dots (<c>cli.skill.list</c>). A command that came from no registry is named by its argv.
    /// </summary>
    public string Id => "cli." + (TuiName is { Length: > 0 } name ? name.Replace(' ', '.') : string.Join('.', Argv));

    /// <summary>What the palette row says: the registry's name for it, or the command line when it has none.</summary>
    public string Title => TuiName ?? CommandLineText;

    /// <summary>The command as it reads, e.g. <c>defenseclaw agent discovery scan</c>: the review's title, the detail pane's preview.</summary>
    public string CommandLineText => CommandReview.CommandLine(Executable, Argv);

    /// <summary>The same command as text to paste into PowerShell (every argument one literal string).</summary>
    public string ClipboardText => CommandReview.ClipboardLine(Executable, Argv);

    public bool NeedsArguments => RequiredArguments.Count > 0;

    /// <summary>
    /// True when it has to be run in a console of the operator's own: the registry calls it interactive, or it is one of the commands the
    /// CLI answers with a prompt. The app has no console to give it - stdin is closed, so the CLI would stop at its first question - so
    /// Run copies the command instead.
    /// </summary>
    public bool NeedsTerminal =>
        string.Equals(Executable, CommandReview.DefaultExecutable, StringComparison.Ordinal) &&
        (NeedTheConsole.Contains(string.Join(' ', Argv)) || Summary.Contains("interactive", StringComparison.OrdinalIgnoreCase));

    /// <summary>The small form that takes its argument, or null: it needs none, needs a console, or needs more than one plain value.</summary>
    public ArgumentForm? Form => NeedsArguments && !NeedsTerminal ? ArgumentForm.Parse(ArgumentHint) : null;

    /// <summary>
    /// The gateway verb this row stands for when it is one of the three lifecycle verbs. They run through the same path as the tray's and the
    /// palette's Gateway rows (availability, the review, "a stop is the operator's word" so nothing starts it again, the toast), not as a bare
    /// command.
    /// </summary>
    public GatewayAction? LifecycleAction =>
        string.Equals(Executable, GatewayControl.Executable, StringComparison.Ordinal) && Argv.Count == 1
            ? Argv[0] switch
            {
                "start" => GatewayAction.Start,
                "stop" => GatewayAction.Stop,
                "restart" => GatewayAction.Restart,
                _ => null,
            }
            : null;

    /// <summary>
    /// The entry of <see cref="GatewayVerbs"/> this row stands for - <c>watchdog start</c>, <c>connector teardown</c>, <c>policy reload</c> and the
    /// rest of the gateway's fixed verbs, the exact two-word argv on <c>defenseclaw-gateway</c> - or null. What the review says it does, and
    /// whether it needs the running sidecar, comes from there; its tier does not (that is <see cref="Tier"/>).
    /// </summary>
    public GatewayVerbs.Verb? GatewayVerb =>
        string.Equals(Executable, GatewayControl.Executable, StringComparison.Ordinal) ? GatewayVerbs.Find(Argv) : null;

    /// <summary>
    /// True when the palette may run it with no review: only a command with nothing to add, on the explicit allow-list of known reads
    /// (<see cref="CommandReview.MayRunUnreviewed(string, IReadOnlyList{string})"/>). A command <see cref="CommandTiers"/> calls read-only by
    /// its first verb but that is not on the list - <c>plan apply</c>, a verb a newer CLI added, <c>skill info</c> once a name is on it - is
    /// reviewed like any change.
    /// </summary>
    public bool RunsWithoutReview => !NeedsArguments && CommandReview.MayRunUnreviewed(Executable, Argv);

    /// <summary>What the review and the tier policy make of it: only an allow-listed read is read-only and runs as it is; everything else is at least a change, and is reviewed first.</summary>
    public CommandTier Tier => CommandReview.ResolveTier(Argv, RunsWithoutReview ? CommandTier.ReadOnly : CommandTier.StateChanging);

    /// <summary>The argv with the operator's value added as a target: after <c>--</c>, so nothing typed can be read as an option.</summary>
    public IReadOnlyList<string> ArgvWith(string argument) => Argv.Append("--").Append(argument).ToArray();

    /// <summary>The command as it reads with a value (or the form's placeholder, <c>&lt;skill-name&gt;</c>, when none is typed yet).</summary>
    public string CommandLineWith(string? argument) =>
        Form is null
            ? CommandLineText
            : CommandReview.CommandLine(Executable, Argv.Append("--").Append(string.IsNullOrWhiteSpace(argument) ? ArgumentHint : argument.Trim()));

    /// <summary>The same with a value, as PowerShell text.</summary>
    public string ClipboardTextWith(string argument) => CommandReview.ClipboardLine(Executable, ArgvWith(argument));

    /// <summary>The palette row for a TUI registry entry.</summary>
    public static CuratedCommand FromRegistry(TuiRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new CuratedCommand(
            entry.Argv,
            CuratedCommandCatalog.CategoryOf(entry.Category),
            entry.Description,
            string.Empty,
            entry.NeedsArgument ? new[] { entry.ArgumentHint } : Array.Empty<string>(),
            entry.Executable,
            entry.Name,
            entry.ArgumentHint);
    }
}

/// <summary>
/// The palette's CLI commands for one runtime: its TUI command registry (<see cref="TuiRegistryCatalogues"/>, generated by
/// <c>tools/gen-tui-registry.py</c>), minus what the runtime itself does not run on Windows. Which registry follows what the connected
/// runtime reported about itself (<see cref="TuiRegistryCatalogues.For"/>): the installed 0.8.10's 231 entries until a runtime shows it has the
/// larger one. Nothing is read from the CLI, so nothing is run to build it; <see cref="HiddenNote"/> says how many entries Windows does not offer.
/// </summary>
internal sealed class CuratedCommandCatalog
{
    private static readonly string[] CategoryOrder =
    {
        "Daemon", "Enforce", "Info", "Install", "Other", "Policy", "Sandbox", "Scan", "Setup",
    };

    /// <summary>The Mac sheet's categories, which are the registry's own (Sandbox is never populated here: the runtime does not run sandboxes on Windows).</summary>
    public static readonly IReadOnlyList<string> Categories = CategoryOrder;

    private static readonly CuratedCommandCatalog BaselineCommands = new(TuiRegistryCatalogues.Baseline);
    private static readonly CuratedCommandCatalog ExtendedCommands = new(TuiRegistryCatalogues.Extended);

    private readonly Dictionary<string, CuratedCommand> _byId;

    public CuratedCommandCatalog(TuiRegistryCatalogue source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));

        Commands = source.OnWindows
            .Select(CuratedCommand.FromRegistry)
            .OrderBy(c => Array.IndexOf(CategoryOrder, c.Category))
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _byId = Commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    /// <summary>The catalogue for a runtime with these capabilities (see <see cref="TuiRegistryCatalogues.For"/>).</summary>
    public static CuratedCommandCatalog For(RuntimeCapabilities? capabilities) =>
        ReferenceEquals(TuiRegistryCatalogues.For(capabilities), TuiRegistryCatalogues.Extended) ? ExtendedCommands : BaselineCommands;

    /// <summary>The registry these commands are read from.</summary>
    public TuiRegistryCatalogue Source { get; }

    /// <summary>What Windows runs: Mac-sheet category order, then by name.</summary>
    public IReadOnlyList<CuratedCommand> Commands { get; }

    /// <summary>How many entries the registry has that the runtime does not run on Windows.</summary>
    public int HiddenCount => Source.HiddenOnWindows.Count;

    /// <summary>"21 hidden on Windows", or empty when none is.</summary>
    public string HiddenNote => HiddenCount == 0 ? string.Empty : $"{HiddenCount} hidden on Windows";

    /// <summary>One line per reason: how many entries and the runtime's own words. For the note's tooltip.</summary>
    public string HiddenDetail => HiddenCount == 0
        ? string.Empty
        : "Not offered on Windows, because the runtime does not run them here:" + string.Concat(Source.HiddenReasons.Select(
            r => Environment.NewLine + $"{r.Count} {(r.Count == 1 ? "command" : "commands")}: {r.Reason}"));

    /// <summary>The command with this id (<see cref="CuratedCommand.Id"/>), or null: it is not in this catalogue, or Windows does not run it.</summary>
    public CuratedCommand? Find(string id) => id is not null && _byId.TryGetValue(id, out var command) ? command : null;

    /// <summary>The Mac sheet's category for a registry category ("scan" is "Scan"); anything the sheet has no heading for is "Other".</summary>
    internal static string CategoryOf(string registryCategory)
    {
        var heading = registryCategory.Length == 0 ? registryCategory : char.ToUpperInvariant(registryCategory[0]) + registryCategory[1..];
        return Categories.Contains(heading) ? heading : "Other";
    }

    /// <summary>
    /// True when <paramref name="argv"/> must not be offered or run: it is empty, any token has a shell metacharacter or a space, or any token is
    /// an option that is not one of <see cref="TuiRegistryCatalogues.ReviewedFlags"/> - which keeps out the flags that carry a credential
    /// (<c>--value</c>, <c>--token</c>, <c>--api-key</c> and the like) along with every other one nobody has read. A palette entry is a noun path with the
    /// few options the registry gives it, so none of these is expected - this is the line that makes sure it stays so.
    /// </summary>
    public static bool Refuses(IReadOnlyList<string> argv) => TuiRegistryCatalogues.ArgvProblem(argv) is not null;

    /// <summary>
    /// Splits a command line into an argv list with no shell: whitespace separates, nothing else is interpreted. Null when the
    /// text is empty, has a quote or shell metacharacter (it is not a plain noun path), or fails <see cref="Refuses"/>.
    /// </summary>
    public static IReadOnlyList<string>? Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && string.Equals(tokens[0], CommandReview.DefaultExecutable, StringComparison.OrdinalIgnoreCase))
        {
            tokens = tokens[1..];
        }

        return Refuses(tokens) ? null : tokens;
    }
}
