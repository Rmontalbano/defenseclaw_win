using System.Globalization;

namespace DefenseClaw.Core.Audit;

/// <summary>One parsed <c>key=value</c> of an event's details: a readable label and a display value.</summary>
public sealed record DetailPair(string Label, string Value);

/// <summary>
/// Parses the gateway's human-readable <c>key=value</c> detail text (<c>action=block scanner=x token=&lt;redacted len=29
/// sha=c5482d9d&gt;</c>) without mistaking metadata inside a <c>&lt;redacted ...&gt;</c> placeholder for event fields. A port of
/// the Mac app's <c>StructuredDetailParser</c> (v1.1.25); pure, no I/O, never throws.
/// </summary>
public static class StructuredDetailParser
{
    private static readonly HashSet<string> SafeMetadataKeys = new(StringComparer.Ordinal)
    {
        "action", "connector", "decision", "evaluation_id", "finding_count",
        "findings", "max_severity", "rule_ids", "scan_id", "scanner",
    };

    /// <summary>
    /// Parses a contiguous structured record from the start of <paramref name="details"/>. When the text starts with prose or
    /// a redaction placeholder the result is empty and the caller keeps the raw text; parsing stops at the first thing
    /// that is not a pair.
    /// </summary>
    public static IReadOnlyList<DetailPair> Pairs(string? details)
    {
        var text = details ?? string.Empty;
        var index = SkipWhitespace(text, 0);
        var result = new List<DetailPair>();

        while (index < text.Length)
        {
            if (!TryParsePair(text, index, out var key, out var value, out var next))
            {
                break;
            }

            result.Add(new DetailPair(Label(key), DisplayValue(value)));
            index = SkipWhitespace(text, next);
        }

        return result;
    }

    /// <summary>
    /// Extracts only the useful top-level metadata (<c>action</c>, <c>scanner</c>, <c>rule_ids</c>, ...) from an otherwise raw
    /// record. Angle-bracket placeholders are skipped as a unit, so their <c>len</c> and <c>sha</c> attributes can never
    /// become rows. Each key appears once (the first occurrence).
    /// </summary>
    public static IReadOnlyList<DetailPair> SafeMetadataPairs(string? details)
    {
        var text = details ?? string.Empty;
        var index = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DetailPair>();

        while (index < text.Length)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length)
            {
                break;
            }

            if (text[index] == '<')
            {
                index = SkipAngleGroup(text, index);
                continue;
            }

            if (TryParsePair(text, index, out var key, out var value, out var next))
            {
                var normalized = key.ToLowerInvariant();
                if (SafeMetadataKeys.Contains(normalized) && seen.Add(normalized))
                {
                    result.Add(new DetailPair(Label(normalized), DisplayValue(value)));
                }

                index = next;
            }
            else
            {
                index = SkipToken(text, index);
            }
        }

        return result;
    }

    /// <summary>"rule_ids" becomes "Rule IDs", "max_severity" becomes "Max Severity".</summary>
    public static string Label(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (key.ToLowerInvariant())
        {
            case "evaluation_id": return "Evaluation ID";
            case "rule_ids": return "Rule IDs";
            case "scan_id": return "Scan ID";
        }

        var words = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLowerInvariant()));
    }

    private static bool TryParsePair(string text, int start, out string key, out string value, out int next)
    {
        key = value = string.Empty;
        next = start;
        var index = start;
        while (index < text.Length && text[index] != '=' && !char.IsWhiteSpace(text[index]))
        {
            if (text[index] is '<' or '>')
            {
                return false;
            }

            index++;
        }

        if (index == start || index >= text.Length || text[index] != '=')
        {
            return false;
        }

        key = text[start..index];
        index++;
        if (index >= text.Length)
        {
            return false;
        }

        var valueStart = index;
        if (text[index] is '"' or '\'' or '`')
        {
            var quote = text[index];
            index++;
            var chars = new System.Text.StringBuilder();
            var escaped = false;
            while (index < text.Length)
            {
                var c = text[index++];
                if (escaped)
                {
                    chars.Append(c);
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    value = chars.ToString();
                    next = index;
                    return true;
                }
                else
                {
                    chars.Append(c);
                }
            }

            return false;
        }

        if (text[index] == '<')
        {
            var after = SkipAngleGroup(text, index);
            if (after <= index || text[after - 1] != '>')
            {
                return false;
            }

            value = text[index..after];
            next = after;
            return true;
        }

        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index <= valueStart)
        {
            return false;
        }

        value = text[valueStart..index];
        next = index;
        return true;
    }

    /// <summary><c>&lt;redacted len=29 sha=c5482d9d&gt;</c> reads "redacted · 29 bytes · sha:c5482d9d"; any other value is unchanged.</summary>
    private static string DisplayValue(string value)
    {
        if (!value.StartsWith("<redacted", StringComparison.Ordinal) || !value.EndsWith('>'))
        {
            return value;
        }

        var body = value[1..^1]["redacted".Length..];
        string? length = null;
        string? digest = null;
        foreach (var pair in AttributePairs(body))
        {
            if (length is null && pair.Key.Equals("len", StringComparison.OrdinalIgnoreCase))
            {
                length = pair.Value;
            }
            else if (digest is null && pair.Key.Equals("sha", StringComparison.OrdinalIgnoreCase))
            {
                digest = pair.Value;
            }
        }

        var parts = new List<string> { "redacted" };
        if (length is not null)
        {
            parts.Add($"{length} bytes");
        }

        if (digest is not null)
        {
            parts.Add($"sha:{digest}");
        }

        return string.Join(" · ", parts);
    }

    private static List<KeyValuePair<string, string>> AttributePairs(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        var index = SkipWhitespace(text, 0);
        while (index < text.Length && TryParsePair(text, index, out var key, out var value, out var next))
        {
            result.Add(new KeyValuePair<string, string>(key, value));
            index = SkipWhitespace(text, next);
        }

        return result;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipToken(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipAngleGroup(string text, int start)
    {
        var index = start;
        var depth = 0;
        while (index < text.Length)
        {
            if (text[index] == '<')
            {
                depth++;
            }

            if (text[index] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
    }
}
