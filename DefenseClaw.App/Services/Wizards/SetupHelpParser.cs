using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>One <c>Commands:</c> entry from a Click help screen.</summary>
/// <param name="Name">The subcommand noun.</param>
/// <param name="Summary">Its one-line description, re-joined across wrapped lines.</param>
public sealed record ParsedCommand(string Name, string Summary);

/// <summary>One entry from a Click <c>Options:</c> block, before it becomes a field.</summary>
public sealed record ParsedOption
{
    /// <summary>Every spelling, in declaration order, e.g. <c>-y</c>, <c>--yes</c>.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Preferred long flag: the first <c>--name</c>, else the first name.</summary>
    public required string Flag { get; init; }

    /// <summary>Set for paired flags: <c>--restart / --no-restart</c> → <c>--no-restart</c>.</summary>
    public string? NegativeFlag { get; init; }

    /// <summary>Raw metavar: <c>TEXT</c>, <c>INTEGER</c>, <c>FILE</c>, <c>[a|b]</c>, or empty.</summary>
    public string Metavar { get; init; } = string.Empty;

    /// <summary>Enumerated values when the metavar was a <c>[a|b|c]</c> list.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    public string Description { get; init; } = string.Empty;

    /// <summary>Contents of a <c>[default: …]</c> marker, if the help declared one.</summary>
    public string? Default { get; init; }

    public bool TakesValue => Metavar.Length > 0;
}

/// <summary>A positional argument lifted out of the <c>Usage:</c> line.</summary>
public sealed record ParsedPositional
{
    public required string Name { get; init; }

    public bool IsRequired { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
}

/// <summary>Everything one <c>defenseclaw setup &lt;target&gt; --help</c> screen yields.</summary>
public sealed record ParsedHelp
{
    public string Usage { get; init; } = string.Empty;

    /// <summary>The prose block between the usage line and <c>Options:</c>.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>First sentence of <see cref="Description"/>, for the hub card.</summary>
    public string Summary { get; init; } = string.Empty;

    public PlatformStatus PlatformStatus { get; init; } = PlatformStatus.Unknown;

    public string PlatformNote { get; init; } = string.Empty;

    public IReadOnlyList<ParsedOption> Options { get; init; } = Array.Empty<ParsedOption>();

    public IReadOnlyList<ParsedCommand> Commands { get; init; } = Array.Empty<ParsedCommand>();

    public IReadOnlyList<ParsedPositional> Positionals { get; init; } = Array.Empty<ParsedPositional>();

    /// <summary>True when the usage line showed a bracketed, therefore optional, <c>[COMMAND]</c>.</summary>
    public bool SubcommandOptional { get; init; }

    public bool HasSubcommands => Commands.Count > 0;

    public ParsedOption? Option(string flag) =>
        Options.FirstOrDefault(o => o.Names.Contains(flag, StringComparer.Ordinal));
}

/// <summary>
/// Parses Click's help output, which is the only machine-readable description of the setup
/// surface DefenseClaw 0.8.7 exposes.
/// <para>
/// <b>Why parse at all.</b> The wizard catalog is generated at runtime for the same reason
/// the config editor's section list is: a 0.8.x release lands weekly and adds setup targets.
/// A hard-coded catalog would be wrong within the month; a parsed one shows a new target the
/// day the CLI ships it.
/// </para>
/// <para>
/// <b>Layout assumptions</b>, all verified against 0.8.7 output: option entries start at
/// column 2 with a dash; their descriptions either follow on the same line after a run of
/// spaces or continue on lines indented past column 2; the usage line wraps by hard-breaking
/// tokens, so continuation lines are re-joined without a space while a bracket is open.
/// Anything this parser cannot make sense of degrades to a text field or to
/// <see cref="PlatformStatus.Unknown"/> — never to a guess.
/// </para>
/// </summary>
public static class SetupHelpParser
{
    private static readonly Regex PlatformStatusPattern = new(
        @"Platform status on \w+:\s*(?<status>[a-z_]+)\s*(?:[—–-]\s*(?<note>.*))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex DefaultPattern = new(
        @"\[default:\s*(?<value>[^\]]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>Section headings Click emits at column 0.</summary>
    private const string OptionsHeading = "Options:";
    private const string CommandsHeading = "Commands:";
    private const string UsageHeading = "Usage:";

    /// <param name="commandDepth">
    /// How many nouns follow <c>setup</c> in this screen's usage line: 1 for
    /// <c>setup guardrail</c>, 2 for <c>setup observability add</c>. Positional parsing needs
    /// it to know where the command name stops and the arguments start.
    /// </param>
    public static ParsedHelp Parse(string helpText, int commandDepth = 1)
    {
        ArgumentNullException.ThrowIfNull(helpText);

        var lines = helpText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var usage = ExtractUsage(lines);
        var description = ExtractDescription(lines);
        var (status, note) = ExtractPlatformStatus(description.Length > 0 ? description : helpText);

        return new ParsedHelp
        {
            Usage = usage,
            Description = description,
            Summary = FirstSentence(description),
            PlatformStatus = status,
            PlatformNote = note,
            Options = ParseOptions(lines),
            Commands = ParseCommands(lines),
            Positionals = ParsePositionals(usage, commandDepth),
            SubcommandOptional = usage.Contains("[COMMAND]", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Maps a status word onto <see cref="PlatformStatus"/>. Absence of the line means the
    /// integration is certified here — that is Click's convention, and it is why
    /// claude-code and codex carry no such line on 0.8.7.
    /// </summary>
    public static (PlatformStatus Status, string Note) ExtractPlatformStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var match = PlatformStatusPattern.Match(text);
        if (!match.Success)
        {
            return (PlatformStatus.Certified, string.Empty);
        }

        var word = match.Groups["status"].Value.Trim();
        var note = Collapse(match.Groups["note"].Value);

        var status = word.ToUpperInvariant() switch
        {
            "NOT_CERTIFIED" => PlatformStatus.NotCertified,
            "UNSUPPORTED" => PlatformStatus.Unsupported,
            "CERTIFIED" or "SUPPORTED" => PlatformStatus.Certified,
            _ => PlatformStatus.Unknown,
        };

        return (status, note);
    }

    /// <summary>
    /// Reads certification out of a one-line command summary such as
    /// <c>Cursor: not_certified on windows.</c> — the top-level <c>setup --help</c> screen
    /// carries these, so the hub can badge every card before the per-target probes land.
    /// </summary>
    public static PlatformStatus StatusFromSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return PlatformStatus.Unknown;
        }

        if (summary.Contains("not_certified", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformStatus.NotCertified;
        }

        return summary.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            ? PlatformStatus.Unsupported
            : PlatformStatus.Unknown;
    }

    /// <summary>
    /// Re-joins the usage block. Click hard-wraps inside <c>{…}</c> and <c>[…]</c> groups
    /// (<c>setup notifications-set</c> splits <c>block_enforced</c> across two lines), so
    /// continuation lines are concatenated without a space while a bracket is open and with
    /// one otherwise.
    /// </summary>
    private static string ExtractUsage(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        var depth = 0;
        var started = false;

        foreach (var line in lines)
        {
            if (!started)
            {
                if (!line.StartsWith(UsageHeading, StringComparison.Ordinal))
                {
                    continue;
                }

                started = true;
                builder.Append(line.Trim());
                depth += Depth(line);
                continue;
            }

            // The usage block ends at the first blank line or any line that starts at column 0.
            if (line.Trim().Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            builder.Append(depth > 0 ? string.Empty : " ").Append(line.Trim());
            depth += Depth(line);
        }

        return builder.ToString();

        static int Depth(string line)
        {
            var delta = 0;
            foreach (var c in line)
            {
                if (c is '{' or '[')
                {
                    delta++;
                }
                else if (c is '}' or ']')
                {
                    delta--;
                }
            }

            return delta;
        }
    }

    /// <summary>The indented prose between the usage block and the first section heading.</summary>
    private static string ExtractDescription(IReadOnlyList<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        var afterUsage = false;

        foreach (var line in lines)
        {
            if (!afterUsage)
            {
                if (line.StartsWith(UsageHeading, StringComparison.Ordinal))
                {
                    afterUsage = true;
                }

                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (IsHeading(line))
            {
                break;
            }

            // Still inside the wrapped usage block: those lines are indented far past the
            // description's two-space indent and were already consumed by ExtractUsage.
            if (paragraphs.Count == 0 && current.Length == 0 && line.Length - line.TrimStart().Length > 20)
            {
                continue;
            }

            AppendWrapped(current, trimmed);
        }

        Flush();
        return string.Join("\n\n", paragraphs);

        void Flush()
        {
            if (current.Length > 0)
            {
                paragraphs.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static bool IsHeading(string line) =>
        line.StartsWith(OptionsHeading, StringComparison.Ordinal) ||
        line.StartsWith(CommandsHeading, StringComparison.Ordinal);

    private static IReadOnlyList<ParsedOption> ParseOptions(IReadOnlyList<string> lines)
    {
        var options = new List<ParsedOption>();
        var index = IndexOf(lines, OptionsHeading);
        if (index < 0)
        {
            return options;
        }

        string? spec = null;
        var description = new StringBuilder();

        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];

            if (line.Trim().Length == 0)
            {
                continue;
            }

            // Column 0 means a new section (Commands:, or the trailing epilog paragraph).
            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.TrimStart();

            if (indent <= 3)
            {
                // Click prints an epilog paragraph at the same indent as the option specs.
                // Prose there is where the Options block ends — without this, a wrapped
                // epilog line that happens to start with a flag (claude-code's help wraps
                // "(`setup openclaw --help`)") would be parsed as another option.
                if (!trimmed.StartsWith('-'))
                {
                    break;
                }

                Commit();

                // Click separates the spec from its description with a run of spaces. An
                // option whose spec is too wide gets the whole line to itself instead.
                var split = SplitSpec(trimmed);
                spec = split.Spec;
                description.Append(split.Description);
                continue;
            }

            if (spec is null)
            {
                continue;
            }

            AppendWrapped(description, trimmed);
        }

        Commit();
        return options;

        void Commit()
        {
            if (spec is null)
            {
                return;
            }

            var option = BuildOption(spec, description.ToString());
            if (option is not null)
            {
                options.Add(option);
            }

            spec = null;
            description.Clear();
        }
    }

    /// <summary>
    /// Appends a wrapped continuation line. Click breaks long words at hyphens without
    /// repeating one, so <c>[default: no-local-</c> + <c>stack]</c> has to re-join with no
    /// space — otherwise the default reads "no-local- stack" and matches neither flag.
    /// </summary>
    private static void AppendWrapped(StringBuilder builder, string continuation)
    {
        if (builder.Length == 0)
        {
            builder.Append(continuation);
            return;
        }

        var endsInHyphen = builder[^1] == '-' && (builder.Length < 2 || builder[^2] != ' ');
        if (!endsInHyphen)
        {
            builder.Append(' ');
        }

        builder.Append(continuation);
    }

    /// <summary>Splits <c>--mode [observe|action]      Hook policy mode.</c> at the gap.</summary>
    private static (string Spec, string Description) SplitSpec(string trimmed)
    {
        var gap = trimmed.IndexOf("  ", StringComparison.Ordinal);
        return gap < 0
            ? (trimmed, string.Empty)
            : (trimmed[..gap].Trim(), trimmed[gap..].Trim());
    }

    private static ParsedOption? BuildOption(string spec, string rawDescription)
    {
        var description = Collapse(rawDescription);
        var defaultValue = ExtractDefault(description);

        string? negative = null;
        var positiveSpec = spec;

        // Paired flags: "--restart / --no-restart", "--enabled / --disabled".
        var slash = spec.IndexOf(" / ", StringComparison.Ordinal);
        if (slash >= 0)
        {
            positiveSpec = spec[..slash].Trim();
            negative = spec[(slash + 3)..].Trim().Split(' ', ',')[0];
        }

        var tokens = positiveSpec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = tokens.TakeWhile(t => t.StartsWith('-')).ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        if (names.Contains("--help", StringComparer.Ordinal))
        {
            return null;
        }

        var metavar = string.Join(' ', tokens.Skip(names.Length));
        var choices = ParseChoiceList(metavar);

        return new ParsedOption
        {
            Names = names,
            Flag = names.FirstOrDefault(n => n.StartsWith("--", StringComparison.Ordinal)) ?? names[0],
            NegativeFlag = negative,
            Metavar = metavar,
            Choices = choices,
            Description = description,
            Default = defaultValue,
        };
    }

    /// <summary>
    /// <c>[observe|action]</c> → two values. <c>[|guardrail|scanners.skill]</c> keeps its
    /// leading empty slot out of the list; the field adds its own "leave unchanged" entry.
    /// </summary>
    private static IReadOnlyList<string> ParseChoiceList(string metavar)
    {
        if (metavar.Length < 3 || metavar[0] != '[' || metavar[^1] != ']' || !metavar.Contains('|', StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        return metavar[1..^1]
            .Split('|', StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToArray();
    }

    private static string? ExtractDefault(string description)
    {
        var match = DefaultPattern.Match(description);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static IReadOnlyList<ParsedCommand> ParseCommands(IReadOnlyList<string> lines)
    {
        var commands = new List<ParsedCommand>();
        var index = IndexOf(lines, CommandsHeading);
        if (index < 0)
        {
            return commands;
        }

        string? name = null;
        var summary = new StringBuilder();

        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.TrimStart();

            if (indent <= 3)
            {
                Commit();
                var split = SplitSpec(trimmed);
                name = split.Spec;
                summary.Append(split.Description);
                continue;
            }

            if (name is null)
            {
                continue;
            }

            AppendWrapped(summary, trimmed);
        }

        Commit();
        return commands;

        void Commit()
        {
            if (name is { Length: > 0 })
            {
                commands.Add(new ParsedCommand(name, Collapse(summary.ToString())));
            }

            name = null;
            summary.Clear();
        }
    }

    /// <summary>
    /// Lifts positional arguments out of the (re-joined) usage line: bare <c>CONNECTOR</c>,
    /// angled <c>&lt;preset&gt;</c>, required <c>{a|b}</c> and optional <c>[a|b]</c> value
    /// sets. <c>[OPTIONS]</c>, <c>COMMAND</c> and <c>[ARGS]...</c> are structural and are
    /// handled by the catalog, not as fields.
    /// </summary>
    private static IReadOnlyList<ParsedPositional> ParsePositionals(string usage, int commandDepth)
    {
        var positionals = new List<ParsedPositional>();
        if (usage.Length == 0)
        {
            return positionals;
        }

        // Drop "Usage: defenseclaw setup" plus one token per command noun — otherwise a
        // subcommand's own name ("observability add") is read as its first argument.
        var tokens = Tokenize(usage);
        foreach (var token in tokens.Skip(3 + Math.Max(commandDepth, 0)))
        {
            if (token is "[OPTIONS]" or "[ARGS]..." or "COMMAND" or "[COMMAND]" or "[ARGS]" or "...")
            {
                continue;
            }

            if (token.StartsWith('{') && token.EndsWith('}'))
            {
                positionals.Add(new ParsedPositional
                {
                    Name = "value",
                    IsRequired = true,
                    Choices = token[1..^1].Split('|', StringSplitOptions.TrimEntries).Where(v => v.Length > 0).ToArray(),
                });
                continue;
            }

            if (token.StartsWith('[') && token.EndsWith(']') && token.Contains('|', StringComparison.Ordinal))
            {
                positionals.Add(new ParsedPositional
                {
                    Name = "value",
                    IsRequired = false,
                    Choices = token[1..^1].Split('|', StringSplitOptions.TrimEntries).Where(v => v.Length > 0).ToArray(),
                });
                continue;
            }

            var name = token.Trim('<', '>', '[', ']', '.');
            if (name.Length == 0 || name.Equals("ARGS", StringComparison.Ordinal))
            {
                continue;
            }

            positionals.Add(new ParsedPositional
            {
                Name = name.ToLowerInvariant(),
                IsRequired = !token.StartsWith('['),
            });
        }

        return positionals;
    }

    /// <summary>Splits on whitespace, honouring <c>{}</c>/<c>[]</c> groups that contain spaces.</summary>
    private static IReadOnlyList<string> Tokenize(string usage)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var c in usage)
        {
            if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
            }

            if (char.IsWhiteSpace(c) && depth <= 0)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static int IndexOf(IReadOnlyList<string> lines, string heading)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(heading, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>First sentence of the description, capped so a hub card stays one card tall.</summary>
    private static string FirstSentence(string description)
    {
        if (description.Length == 0)
        {
            return string.Empty;
        }

        var first = description.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[0];
        var stop = first.IndexOf(". ", StringComparison.Ordinal);
        var sentence = stop > 0 ? first[..(stop + 1)] : first;

        return sentence.Length <= 220 ? sentence : sentence[..217] + "…";
    }
}
