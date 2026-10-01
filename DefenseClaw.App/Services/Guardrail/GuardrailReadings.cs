using System.Text.RegularExpressions;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Services.Guardrail;

/// <summary>One line of a "per connector:" list: <c>- Claude Code (claudecode): value</c>.</summary>
/// <param name="Name">The display label ("Claude Code"), or the key when the CLI printed only that.</param>
/// <param name="Key">The config key <c>--connector</c> takes ("claudecode").</param>
/// <param name="Value">Everything after the colon, verbatim.</param>
public sealed record GuardrailPerConnectorLine(string Name, string Key, string Value);

/// <summary>What <c>guardrail hilt</c> (no arguments: read-only) printed.</summary>
/// <param name="Enabled">The global <c>guardrail.hilt.enabled</c>; null when the text could not be read.</param>
/// <param name="MinSeverity">The global minimum severity, upper-case ("HIGH"); empty when unread.</param>
public sealed record GuardrailHiltReading(
    bool? Enabled,
    string MinSeverity,
    IReadOnlyList<GuardrailConnectorHilt> Connectors,
    string Raw)
{
    public bool IsRead => Enabled is not null;
}

/// <summary>One connector's effective HILT policy.</summary>
public sealed record GuardrailConnectorHilt(string Name, string Key, bool? Enabled, string MinSeverity);

/// <summary>What <c>guardrail block-message</c> (no arguments: read-only) printed.</summary>
/// <param name="IsRead">The "guardrail.block_message:" line was found.</param>
/// <param name="Message">The global custom message; empty when the built-in default is in force.</param>
public sealed record GuardrailBlockMessageReading(
    bool IsRead,
    string Message,
    IReadOnlyList<GuardrailConnectorMessage> Connectors,
    string Raw)
{
    public bool IsDefault => Message.Length == 0;
}

/// <summary>One connector's effective block message; <see cref="Message"/> is empty for the built-in default.</summary>
public sealed record GuardrailConnectorMessage(string Name, string Key, string Message);

/// <summary>What <c>guardrail judge list</c> printed.</summary>
/// <param name="JudgeEnabled">The global <c>guardrail.judge.enabled</c>; null when unread.</param>
/// <param name="GateAll">The hook gate holds "all" (every hook connector).</param>
/// <param name="Gate">The connectors named in the gate; empty with <see cref="GateAll"/> false means the hook lane is off.</param>
/// <param name="HookTimeout">"5s (gateway default)" or "8s"; empty when unread.</param>
public sealed record GuardrailJudgeReading(
    bool? JudgeEnabled,
    bool GateAll,
    IReadOnlyList<string> Gate,
    string HookTimeout,
    IReadOnlyList<GuardrailConnectorJudge> Connectors,
    string Raw)
{
    public bool IsRead => JudgeEnabled is not null;

    /// <summary>True when <paramref name="key"/> is opted in to the hook-lane judge (by name or by "all").</summary>
    public bool IsGated(string key) => GateAll || Gate.Any(g => string.Equals(g, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One connector's effective judge state, e.g. "judged (hook lane)" with the CLI's trailing note.</summary>
public sealed record GuardrailConnectorJudge(string Key, string State, string Note);

/// <summary>
/// Readers for the text the read-only forms of the guardrail verbs print (<c>guardrail hilt</c>,
/// <c>guardrail block-message</c>, <c>guardrail judge list</c>; none has a <c>--json</c>). Shapes are taken from
/// <c>defenseclaw/commands/cmd_guardrail.py</c> and <c>cmd_judge.py</c>. Parsing never throws and never guesses: a reading that
/// cannot be made says so (<c>IsRead</c> false) and the caller shows <c>Raw</c>.
/// </summary>
public static partial class GuardrailReadings
{
    [GeneratedRegex(@"^\s*-\s+(?<name>.+?)\s+\((?<key>[A-Za-z0-9_.-]+)\):\s?(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PerConnectorPattern();

    [GeneratedRegex(@"^\s*-\s+(?<key>[A-Za-z0-9_.-]+):\s?(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyOnlyPattern();

    [GeneratedRegex(@"guardrail\.hilt\.enabled:\s*(?<v>true|false)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiltEnabledPattern();

    [GeneratedRegex(@"guardrail\.hilt\.min_severity:\s*(?<v>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiltMinPattern();

    [GeneratedRegex(@"enabled=(?<e>true|false)\s+min_severity=(?<m>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiltConnectorPattern();

    [GeneratedRegex(@"^\s*guardrail\.block_message:\s?(?<v>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockMessagePattern();

    [GeneratedRegex(@"guardrail\.judge\.enabled:\s*(?<v>true|false)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JudgeEnabledPattern();

    [GeneratedRegex(@"guardrail\.judge\.hook_connectors:\s*(?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex JudgeGatePattern();

    [GeneratedRegex(@"guardrail\.judge\.hook_timeout:\s*(?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex JudgeTimeoutPattern();

    [GeneratedRegex(@"'(?<n>[^']*)'|""(?<n>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();

    /// <summary>The CLI separates a state from its note with a dash (an em dash in the source); any non-word symbol between spaces counts.</summary>
    [GeneratedRegex(@"\s[^\w\s()+:.,]\s", RegexOptions.CultureInvariant)]
    private static partial Regex NoteSeparatorPattern();

    private static string[] Lines(string text) => CliText.NormalizeLineEndings(text).Split('\n');

    /// <summary>The "- Name (key): value" lines after a "per connector:" heading.</summary>
    private static List<GuardrailPerConnectorLine> PerConnector(string[] lines)
    {
        var result = new List<GuardrailPerConnectorLine>();
        var index = Array.FindIndex(lines, l => l.Trim().StartsWith("per connector:", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return result;
        }

        for (var i = index + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                break;
            }

            if (PerConnectorPattern().Match(line) is { Success: true } m)
            {
                result.Add(new GuardrailPerConnectorLine(m.Groups["name"].Value, m.Groups["key"].Value, m.Groups["value"].Value.TrimEnd()));
            }
            else if (KeyOnlyPattern().Match(line) is { Success: true } k)
            {
                result.Add(new GuardrailPerConnectorLine(k.Groups["key"].Value, k.Groups["key"].Value, k.Groups["value"].Value.TrimEnd()));
            }
        }

        return result;
    }

    public static GuardrailHiltReading ParseHilt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Lines(text);
        bool? enabled = null;
        var min = string.Empty;
        foreach (var line in lines)
        {
            if (enabled is null && HiltEnabledPattern().Match(line) is { Success: true } e)
            {
                enabled = string.Equals(e.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (min.Length == 0 && HiltMinPattern().Match(line) is { Success: true } m)
            {
                min = m.Groups["v"].Value.ToUpperInvariant();
            }
        }

        var connectors = new List<GuardrailConnectorHilt>();
        foreach (var entry in PerConnector(lines))
        {
            if (HiltConnectorPattern().Match(entry.Value) is { Success: true } m)
            {
                connectors.Add(new GuardrailConnectorHilt(
                    entry.Name,
                    entry.Key,
                    string.Equals(m.Groups["e"].Value, "true", StringComparison.OrdinalIgnoreCase),
                    m.Groups["m"].Value.ToUpperInvariant()));
            }
        }

        return new GuardrailHiltReading(enabled, min, connectors, text.Trim());
    }

    /// <summary>The marker the CLI prints instead of a message when the built-in text is in force.</summary>
    public const string BuiltInDefaultMarker = "(built-in default)";

    public static GuardrailBlockMessageReading ParseBlockMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Lines(text);
        var start = Array.FindIndex(lines, l => BlockMessagePattern().IsMatch(l));
        if (start < 0)
        {
            return new GuardrailBlockMessageReading(false, string.Empty, Array.Empty<GuardrailConnectorMessage>(), text.Trim());
        }

        // The message is one echoed string: a multi-line one (set in config.yaml by hand) runs on until the blank line.
        var first = BlockMessagePattern().Match(lines[start]).Groups["v"].Value;
        var message = new List<string> { first };
        for (var i = start + 1; i < lines.Length && lines[i].Trim().Length > 0 &&
                                !lines[i].Trim().StartsWith("per connector:", StringComparison.OrdinalIgnoreCase); i++)
        {
            message.Add(lines[i]);
        }

        var global = string.Join('\n', message).Trim();
        var connectors = PerConnector(lines)
            .Select(e => new GuardrailConnectorMessage(e.Name, e.Key, e.Value == BuiltInDefaultMarker ? string.Empty : e.Value))
            .ToList();

        return new GuardrailBlockMessageReading(true, global == BuiltInDefaultMarker ? string.Empty : global, connectors, text.Trim());
    }

    public static GuardrailJudgeReading ParseJudge(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Lines(text);
        bool? enabled = null;
        var gateAll = false;
        var gate = new List<string>();
        var timeout = string.Empty;
        var connectors = new List<GuardrailConnectorJudge>();
        var inEffective = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (enabled is null && JudgeEnabledPattern().Match(line) is { Success: true } e)
            {
                enabled = string.Equals(e.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (JudgeGatePattern().Match(line) is { Success: true } g)
            {
                var label = g.Groups["v"].Value.Trim();
                if (label.StartsWith("all", StringComparison.OrdinalIgnoreCase))
                {
                    gateAll = true;
                }
                else
                {
                    gate.AddRange(QuotedPattern().Matches(label).Select(q => q.Groups["n"].Value.Trim()).Where(n => n.Length > 0));
                    gateAll = gate.Contains("*");
                    gate.Remove("*");
                }
            }
            else if (JudgeTimeoutPattern().Match(line) is { Success: true } t)
            {
                timeout = t.Groups["v"].Value.Trim();
            }
            else if (trimmed.StartsWith("effective state per connector", StringComparison.OrdinalIgnoreCase))
            {
                inEffective = true;
            }
            else if (inEffective && KeyOnlyPattern().Match(line) is { Success: true } k)
            {
                var value = k.Groups["value"].Value.TrimEnd();
                var separator = NoteSeparatorPattern().Match(value);
                var state = separator.Success ? value[..separator.Index] : value;
                var note = separator.Success ? value[(separator.Index + separator.Length)..] : string.Empty;
                connectors.Add(new GuardrailConnectorJudge(k.Groups["key"].Value, state.Trim(), note.Trim()));
            }
        }

        return new GuardrailJudgeReading(enabled, gateAll, gate, timeout, connectors, text.Trim());
    }
}
