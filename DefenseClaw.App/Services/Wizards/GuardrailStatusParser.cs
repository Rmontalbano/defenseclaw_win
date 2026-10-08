using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>One row of the <c>guardrail status</c> roster table, keyed by the table's own headers.</summary>
public sealed record GuardrailConnectorRow(IReadOnlyList<KeyValuePair<string, string>> Columns)
{
    /// <summary>The column whose header matches <paramref name="header"/> (ignoring case), or empty.</summary>
    public string Column(string header) => Columns
        .FirstOrDefault(c => string.Equals(c.Key, header, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

    /// <summary>Display name: "Claude Code".</summary>
    public string Name => Column("Connector");

    /// <summary>The config key, which is what <c>--connector</c> takes: "claudecode".</summary>
    public string Key => Column("Key");

    public string State => Column("State");

    public string Mode => Column("Mode");

    public string Fail => Column("Fail");

    public override string ToString() =>
        $"{(Name.Length > 0 ? Name : Key)}: {State}, mode {Mode}, fail-{Fail}";
}

/// <summary>What <c>guardrail status</c> printed, split into the parts the Setup hub shows.</summary>
public sealed record GuardrailStatus(
    bool? Enabled,
    IReadOnlyList<GuardrailConnectorRow> Connectors,
    IReadOnlyList<string> Warnings,
    string Port,
    string Raw)
{
    /// <summary>True when the roster table was found. When false the panel shows <see cref="Raw"/> instead.</summary>
    public bool HasRoster => Connectors.Count > 0;
}

/// <summary>
/// Reads the text <c>defenseclaw guardrail status</c> prints (it has no <c>--json</c>; catalog §4 item 8).
/// <para>
/// The output is a fixed-width table:
/// </para>
/// <code>
///   • enabled:    yes
///       Connector    Key         State    Mode     Fail    Rule pack  HILT  Scan        Judge
///       -----------  ----------  -------  -------  ------  ---------  ----  ----------  -----
///       Claude Code  claudecode  enabled  observe  closed  default    off   regex_only  off
///   ! runtime fail-mode drift: …
///   • port:       4000
/// </code>
/// <para>
/// The connector name can hold a space ("Claude Code"), so the row cannot be split on whitespace: the
/// column boundaries are read from the dashed rule under the header and each row is sliced at them.
/// Anything that does not fit is left to the caller's verbatim view — parsing here never throws and never
/// guesses. The bullet glyph is ignored on purpose (the runner may decode it as a different code page).
/// </para>
/// </summary>
public static partial class GuardrailStatusParser
{
    [GeneratedRegex(@"enabled:\s*(?<v>yes|no|true|false|on|off)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnabledPattern();

    [GeneratedRegex(@"port:\s*(?<v>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();

    /// <summary>A rule line of at least two dash runs separated by spaces, e.g. <c>-----  -----</c>.</summary>
    [GeneratedRegex(@"^\s+-{2,}(\s{1,4}-{2,})+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RulePattern();

    public static GuardrailStatus Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = CliText.NormalizeLineEndings(text).Split('\n');
        bool? enabled = null;
        var port = string.Empty;
        var warnings = new List<string>();
        var rows = new List<GuardrailConnectorRow>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (enabled is null && EnabledPattern().Match(line) is { Success: true } enabledMatch)
            {
                enabled = enabledMatch.Groups["v"].Value.ToLowerInvariant() is "yes" or "true" or "on";
                continue;
            }

            if (port.Length == 0 && PortPattern().Match(line) is { Success: true } portMatch)
            {
                port = portMatch.Groups["v"].Value;
                continue;
            }

            if (trimmed.StartsWith('!'))
            {
                warnings.Add(trimmed.TrimStart('!', ' '));
                continue;
            }

            if (rows.Count == 0 && i > 0 && RulePattern().IsMatch(line))
            {
                ReadTable(lines, i, rows);
            }
        }

        if (rows.Count == 0)
        {
            ReadBlocks(lines, rows);
        }

        return new GuardrailStatus(enabled, rows, warnings, port, text.Trim());
    }

    /// <summary>The block layout's field names, as the table headers spell them.</summary>
    private static readonly IReadOnlyDictionary<string, string> BlockHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["key"] = "Key",
        ["state"] = "State",
        ["mode"] = "Mode",
        ["fail"] = "Fail",
        ["rule-pack"] = "Rule pack",
        ["block/alert"] = "Block/alert",
        ["hilt"] = "HILT",
        ["scan"] = "Scan",
        ["judge"] = "Judge",
    };

    [GeneratedRegex(@"^\s+-\s+(?<label>\S.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockTitlePattern();

    [GeneratedRegex(@"^\s+(?<name>[a-z][a-z/\-]*):\s*(?<value>.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockFieldPattern();

    /// <summary>
    /// The layout newer CLIs fall back to when the roster table would be wider than the terminal: one block per connector,
    /// <c>- Claude Code</c> then indented <c>key:</c> / <c>state:</c> / <c>mode:</c> ... lines (DefenseClaw source commit 95159fd,
    /// <c>_render_connector_blocks</c>). Read into the same rows the table gives, under the table's header spellings, so a roster does not
    /// vanish because a terminal was narrow or a connector name long. A block without a <c>key:</c> line is not a connector and is skipped.
    /// </summary>
    private static void ReadBlocks(string[] lines, List<GuardrailConnectorRow> rows)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (BlockTitlePattern().Match(lines[i]) is not { Success: true } title)
            {
                continue;
            }

            var columns = new List<KeyValuePair<string, string>> { new("Connector", title.Groups["label"].Value) };
            var j = i + 1;
            for (; j < lines.Length && BlockFieldPattern().Match(lines[j]) is { Success: true } field; j++)
            {
                if (BlockHeaders.TryGetValue(field.Groups["name"].Value, out var header))
                {
                    columns.Add(new KeyValuePair<string, string>(header, field.Groups["value"].Value));
                }
            }

            if (columns.Any(c => c.Key == "Key"))
            {
                rows.Add(new GuardrailConnectorRow(columns));
            }

            i = Math.Max(i, j - 1);
        }
    }

    private static void ReadTable(string[] lines, int ruleIndex, List<GuardrailConnectorRow> rows)
    {
        var rule = lines[ruleIndex];
        var spans = ColumnSpans(rule);
        if (spans.Count < 3)
        {
            return;
        }

        var header = lines[ruleIndex - 1];
        var headers = spans.Select(s => Slice(header, s.Start, s.End).Trim()).ToArray();
        if (headers.All(h => h.Length == 0))
        {
            return;
        }

        for (var i = ruleIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];

            // The table ends at the first blank line or anything that is not an indented data row. The summary bullets that
            // follow a table with no warning ("• fail = ...", "• port: ...") are indented less than its rows are, whatever
            // glyph the runner decoded the bullet as.
            if (line.Trim().Length == 0 || !char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('!') ||
                line.Length - line.TrimStart().Length < rule.Length - rule.TrimStart().Length)
            {
                break;
            }

            var columns = new List<KeyValuePair<string, string>>(spans.Count);
            for (var c = 0; c < spans.Count; c++)
            {
                columns.Add(new KeyValuePair<string, string>(headers[c], Slice(line, spans[c].Start, spans[c].End).Trim()));
            }

            rows.Add(new GuardrailConnectorRow(columns));
        }
    }

    /// <summary>
    /// Start/end of each dash run in the rule line. The last column runs to the end of the line, because
    /// a value there can be wider than its header (the rule is only as wide as the header).
    /// </summary>
    private static List<(int Start, int End)> ColumnSpans(string rule)
    {
        var spans = new List<(int Start, int End)>();
        var i = 0;
        while (i < rule.Length)
        {
            if (rule[i] != '-')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < rule.Length && rule[i] == '-')
            {
                i++;
            }

            spans.Add((start, i));
        }

        if (spans.Count > 0)
        {
            spans[^1] = (spans[^1].Start, int.MaxValue);
        }

        return spans;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return string.Empty;
        }

        var stop = end == int.MaxValue ? line.Length : Math.Min(end, line.Length);
        return line[start..stop];
    }
}
