using System.Globalization;
using System.Text;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Turns a parsed CLI option into a wizard field: picks the control, the default answer, and
/// — the part that matters — decides whether a value is allowed anywhere near argv.
/// <para>
/// <b>The secret rule.</b> DefenseClaw's own convention is that config.yaml stores the NAME
/// of an environment variable (<c>*_api_key_env</c>), never the secret. Any flag ending in
/// <c>-env</c> is therefore an ordinary text field: its value is a variable name and is safe
/// on the command line. Flags that take the secret itself (<c>--token</c>,
/// <c>--access-token</c>, <c>--hec-token</c>) become <see cref="WizardFieldKind.Secret"/>,
/// which never reaches argv — <see cref="Core.Cli.CliRunner"/> throws if it ever did — and is
/// piped to the child's stdin instead.
/// </para>
/// </summary>
public static class WizardFieldBuilder
{
    /// <summary>
    /// Flags that mean "do not prompt me". Every wizard turns these on by default: the app
    /// pipes no keystrokes, so a setup command left interactive would block on a prompt the
    /// operator cannot see, and the wizard would hang instead of finishing.
    /// </summary>
    private static readonly IReadOnlySet<string> NonInteractiveFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--yes",
        "--non-interactive",
        "--accept-defaults",
    };

    /// <summary>Substrings that mark a flag as carrying the secret itself rather than its variable name.</summary>
    private static readonly IReadOnlyList<string> SecretMarkers = new[]
    {
        "token",
        "api-key",
        "access-key",
        "secret",
        "password",
        "passphrase",
    };

    private static readonly IReadOnlyDictionary<string, string> Acronyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["llm"] = "LLM",
        ["api"] = "API",
        ["mcp"] = "MCP",
        ["otlp"] = "OTLP",
        ["tls"] = "TLS",
        ["ca"] = "CA",
        ["url"] = "URL",
        ["hec"] = "HEC",
        ["s3"] = "S3",
        ["aws"] = "AWS",
        ["gcp"] = "GCP",
        ["ssm"] = "SSM",
        ["hilt"] = "HILT",
        ["o11y"] = "O11y",
        ["id"] = "ID",
        ["ms"] = "ms",
        ["json"] = "JSON",
        ["adc"] = "ADC",
        ["iam"] = "IAM",
    };

    /// <summary>Builds a field for <paramref name="option"/>, or null if it should not be offered.</summary>
    public static WizardField? From(
        ParsedOption option,
        string? visibleWhenFieldId = null,
        IReadOnlyList<string>? visibleWhenValues = null,
        string? idPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(option);

        var kind = KindFor(option);
        var id = (idPrefix ?? string.Empty) + Identifier(option.Flag);

        return new WizardField
        {
            Id = id,
            Label = Humanize(option.Flag),
            Kind = kind,
            Flag = option.Flag,
            NegativeFlag = option.NegativeFlag,
            Help = option.Description,
            Choices = ChoicesFor(option, kind),
            DefaultValue = DefaultFor(option, kind),
            Placeholder = PlaceholderFor(option, kind),
            VisibleWhenFieldId = visibleWhenFieldId,
            VisibleWhenValues = visibleWhenValues ?? Array.Empty<string>(),
        };
    }

    /// <summary>Field for a positional argument lifted out of the usage line.</summary>
    public static WizardField FromPositional(
        ParsedPositional positional,
        int order,
        string? idPrefix = null,
        string? visibleWhenFieldId = null,
        IReadOnlyList<string>? visibleWhenValues = null)
    {
        ArgumentNullException.ThrowIfNull(positional);

        var choices = positional.Choices.Count > 0
            ? new[] { WizardChoice.Unchanged }.Concat(positional.Choices.Select(WizardChoice.Of)).ToArray()
            : Array.Empty<WizardChoice>();

        return new WizardField
        {
            // Two positionals can share a name (notifications-set takes {slot} {on|off}, both
            // metavar-less), so the ordinal is part of the id or they would overwrite each other.
            Id = string.Create(
                CultureInfo.InvariantCulture,
                $"{idPrefix ?? string.Empty}arg{order}-{Identifier(positional.Name)}"),
            // "value" is what a metavar-less {a|b} group parses to; two of them in a row read
            // as one field repeated unless the ordinal makes it into the label.
            Label = string.Equals(positional.Name, "value", StringComparison.Ordinal)
                ? string.Create(CultureInfo.CurrentCulture, $"Argument {order}")
                : Humanize(positional.Name),
            Kind = positional.Choices.Count > 0 ? WizardFieldKind.Choice : WizardFieldKind.Text,
            Help = positional.IsRequired
                ? "Required positional argument."
                : "Optional positional argument; leave blank to use the command's own default.",
            Choices = choices,
            IsPositional = true,
            PositionalOrder = order,
            IsRequired = positional.IsRequired,
            VisibleWhenFieldId = visibleWhenFieldId,
            VisibleWhenValues = visibleWhenValues ?? Array.Empty<string>(),
        };
    }

    public static WizardFieldKind KindFor(ParsedOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        if (option.NegativeFlag is { Length: > 0 })
        {
            return WizardFieldKind.Toggle;
        }

        if (!option.TakesValue)
        {
            return WizardFieldKind.Switch;
        }

        if (option.Choices.Count > 0)
        {
            return WizardFieldKind.Choice;
        }

        var metavar = option.Metavar.ToUpperInvariant();
        if (metavar == "INTEGER")
        {
            return WizardFieldKind.Integer;
        }

        var flag = option.Flag;

        // "--judge-api-key-env" holds a variable NAME: safe in argv, and it is what the CLI persists.
        if (flag.EndsWith("-env", StringComparison.Ordinal) || flag.Contains("-env-", StringComparison.Ordinal))
        {
            return WizardFieldKind.EnvVarName;
        }

        if (SecretMarkers.Any(marker => flag.Contains(marker, StringComparison.Ordinal)))
        {
            return WizardFieldKind.Secret;
        }

        if (metavar is "FILE" or "DIRECTORY" or "PATH" ||
            flag.EndsWith("-dir", StringComparison.Ordinal) ||
            flag.EndsWith("-file", StringComparison.Ordinal) ||
            flag.EndsWith("-path", StringComparison.Ordinal))
        {
            return WizardFieldKind.Path;
        }

        return WizardFieldKind.Text;
    }

    private static IReadOnlyList<WizardChoice> ChoicesFor(ParsedOption option, WizardFieldKind kind) => kind switch
    {
        WizardFieldKind.Choice => new[] { WizardChoice.Unchanged }
            .Concat(option.Choices.Select(WizardChoice.Of))
            .ToArray(),
        WizardFieldKind.Toggle => new[]
        {
            WizardChoice.Unchanged,
            new WizardChoice(ToggleValues.On, $"Enable  ({option.Flag})"),
            new WizardChoice(ToggleValues.Off, $"Disable ({option.NegativeFlag})"),
        },
        _ => Array.Empty<WizardChoice>(),
    };

    /// <summary>
    /// Pre-fills the CLI's own documented default so the review screen shows the whole truth:
    /// a toggle left at its default still prints its flag rather than relying on an implicit
    /// value the operator cannot see.
    /// </summary>
    private static string DefaultFor(ParsedOption option, WizardFieldKind kind)
    {
        switch (kind)
        {
            case WizardFieldKind.Switch:
                return option.Names.Any(NonInteractiveFlags.Contains) ? ToggleValues.On : ToggleValues.Off;

            case WizardFieldKind.Toggle:
                return ToggleDefault(option);

            case WizardFieldKind.Secret:
                return string.Empty;

            default:
                return option.Default is { Length: > 0 } value && !value.Contains(' ', StringComparison.Ordinal)
                    ? value
                    : string.Empty;
        }
    }

    /// <summary>
    /// Click prints a paired flag's default as the bare name that wins:
    /// <c>[default: restart]</c> for <c>--restart / --no-restart</c>, <c>[default: no-local-stack]</c>
    /// for the inverse. Matching against both spellings is what tells the two apart.
    /// </summary>
    private static string ToggleDefault(ParsedOption option)
    {
        if (option.Default is not { Length: > 0 } raw)
        {
            return ToggleValues.Unset;
        }

        var value = raw.Trim().TrimStart('-');
        var positive = option.Flag.TrimStart('-');
        var negative = (option.NegativeFlag ?? string.Empty).TrimStart('-');

        if (string.Equals(value, negative, StringComparison.OrdinalIgnoreCase) ||
            value is "false" or "off" or "no")
        {
            return ToggleValues.Off;
        }

        if (string.Equals(value, positive, StringComparison.OrdinalIgnoreCase) ||
            value is "true" or "on" or "yes")
        {
            return ToggleValues.On;
        }

        return ToggleValues.Unset;
    }

    private static string PlaceholderFor(ParsedOption option, WizardFieldKind kind) => kind switch
    {
        WizardFieldKind.EnvVarName => "ENV_VAR_NAME",
        WizardFieldKind.Secret => "piped to stdin, never to argv",
        WizardFieldKind.Path => "path",
        WizardFieldKind.Integer => "number",
        _ => option.Default is { Length: > 0 } value ? value : string.Empty,
    };

    /// <summary>
    /// True for the flags that suppress the CLI's prompts. Used both to default them on and
    /// to warn when one is on at the same time as a secret that only an interactive prompt
    /// would read.
    /// </summary>
    public static bool IsNonInteractiveFlag(string? flag) =>
        flag is { Length: > 0 } && NonInteractiveFlags.Contains(flag);

    /// <summary>Stable field id: the long flag with its dashes stripped.</summary>
    public static string Identifier(string flag) => flag.TrimStart('-');

    /// <summary><c>--hilt-min-severity</c> → <c>HILT min severity</c>.</summary>
    public static string Humanize(string flag)
    {
        var words = flag.TrimStart('-').Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return flag;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            if (Acronyms.TryGetValue(words[i], out var acronym))
            {
                builder.Append(acronym);
                continue;
            }

            var word = words[i];
            builder.Append(i == 0 ? char.ToUpper(word[0], CultureInfo.CurrentCulture) : word[0]);
            builder.Append(word.AsSpan(1));
        }

        return builder.ToString();
    }
}
