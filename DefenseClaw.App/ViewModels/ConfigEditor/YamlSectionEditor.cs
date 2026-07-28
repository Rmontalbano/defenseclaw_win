using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// A conservative, line-oriented "YAML-aware setter" that patches one scalar or one
/// simple string list inside a single top-level section's raw text — the text handed
/// out by <see cref="DefenseClaw.Core.Config.ConfigDocument.SectionText"/>.
/// <para>
/// This is deliberately not a general YAML editor. It never inserts a key that is not
/// already present, never touches indentation it did not compute itself, and refuses
/// (returns <see langword="null"/>) the moment a path cannot be located unambiguously —
/// e.g. it is nested inside a list of mappings, the line carries a trailing <c>#</c>
/// comment, or the same path matches more than once. Every refusal means "edit this in
/// the RAW tab instead," never a best-effort guess.
/// </para>
/// <para>
/// Path segments are the full dotted path from the document root, including the
/// section's own top-level key — e.g. <c>["guardrail", "connectors", "claudecode",
/// "mode"]</c> for <c>guardrail.connectors.claudecode.mode"</c> — because
/// <paramref name="sectionText"/> always begins with that top-level key's own line.
/// </para>
/// </summary>
public static partial class YamlSectionEditor
{
    /// <summary>One physical line, split so it can be put back together byte-for-byte except where changed.</summary>
    private readonly record struct RawLine(string Content, string Terminator)
    {
        public string Full => Content + Terminator;
    }

    /// <summary>Result of trying to find a scalar leaf at an exact path.</summary>
    public readonly record struct ScalarLookup(bool Found, bool Ambiguous, string? RawValue)
    {
        public static readonly ScalarLookup NotFound = new(false, false, null);
    }

    /// <summary>
    /// Looks for a scalar assignment at <paramref name="path"/> without changing anything.
    /// Used by the form builder to decide, up front, whether a field is editable.
    /// </summary>
    public static ScalarLookup FindScalar(string sectionText, IReadOnlyList<string> path)
    {
        var lines = SplitLines(sectionText);
        var matches = new List<int>();
        WalkKeyPaths(lines, (index, currentPath, valueText) =>
        {
            if (valueText is not null && PathsEqual(currentPath, path))
            {
                matches.Add(index);
            }
        });

        return matches.Count switch
        {
            0 => ScalarLookup.NotFound,
            1 => new ScalarLookup(true, false, ExtractValueText(lines[matches[0]].Content)),
            _ => new ScalarLookup(true, true, null),
        };
    }

    /// <summary>
    /// Replaces the value of the scalar at <paramref name="path"/> with
    /// <paramref name="newRawValue"/> (already YAML-formatted — see
    /// <see cref="FormatScalar"/>). Returns the patched section text, or
    /// <see langword="null"/> if the path was not found, was ambiguous, or the line could
    /// not be safely rewritten (e.g. it carries a trailing comment).
    /// </summary>
    public static string? TrySetScalar(string sectionText, IReadOnlyList<string> path, string newRawValue)
    {
        var lines = SplitLines(sectionText);
        var matches = new List<int>();
        WalkKeyPaths(lines, (index, currentPath, valueText) =>
        {
            if (valueText is not null && PathsEqual(currentPath, path))
            {
                matches.Add(index);
            }
        });

        if (matches.Count != 1)
        {
            return null;
        }

        var lineIndex = matches[0];
        var match = KeyLine().Match(lines[lineIndex].Content);
        if (!match.Success || match.Groups["value"].Value.Contains('#'))
        {
            // A trailing comment (or anything else the regex didn't expect) — refuse
            // rather than risk eating it.
            return null;
        }

        var indent = match.Groups["indent"].Value;
        var key = match.Groups["key"].Value;
        var newContent = $"{indent}{key}: {newRawValue}";
        lines[lineIndex] = lines[lineIndex] with { Content = newContent };

        return Join(lines);
    }

    /// <summary>
    /// Replaces the block-sequence value of the list key at <paramref name="path"/> with
    /// <paramref name="items"/>. Handles both an inline empty list (<c>key: []</c>) and an
    /// existing block of <c>- item</c> lines. Returns <see langword="null"/> under the same
    /// conditions as <see cref="TrySetScalar"/>, plus when the key's current value is not a
    /// list at all.
    /// </summary>
    public static string? TrySetList(string sectionText, IReadOnlyList<string> path, IReadOnlyList<string> items)
    {
        var lines = SplitLines(sectionText);
        var matches = new List<int>();
        WalkKeyPaths(lines, (index, currentPath, _) =>
        {
            if (PathsEqual(currentPath, path))
            {
                matches.Add(index);
            }
        });

        if (matches.Count != 1)
        {
            return null;
        }

        var keyLineIndex = matches[0];
        var match = KeyLine().Match(lines[keyLineIndex].Content);
        if (!match.Success)
        {
            return null;
        }

        var indent = match.Groups["indent"].Value;
        var key = match.Groups["key"].Value;
        var inlineValue = match.Groups["value"].Value.Trim();
        var keyIndentLength = match.Groups["indent"].Value.Length;

        int blockStart;
        int blockEnd;

        if (inlineValue.Length > 0)
        {
            // Only an inline empty list ("key: []") is a list we recognize; anything
            // else inline (a scalar, a comment) is not a list this method can touch.
            if (inlineValue != "[]")
            {
                return null;
            }

            blockStart = keyLineIndex + 1;
            blockEnd = blockStart;
        }
        else
        {
            blockStart = keyLineIndex + 1;
            blockEnd = blockStart;
            while (blockEnd < lines.Count && IsListItemAtOrBelow(lines[blockEnd].Content, keyIndentLength))
            {
                blockEnd++;
            }
        }

        var itemIndent = new string(' ', keyIndentLength);
        var terminator = lines[keyLineIndex].Terminator;

        var replacement = new List<RawLine>();
        if (items.Count == 0)
        {
            lines[keyLineIndex] = lines[keyLineIndex] with { Content = $"{indent}{key}: []" };
        }
        else
        {
            lines[keyLineIndex] = lines[keyLineIndex] with { Content = $"{indent}{key}:" };
            foreach (var item in items)
            {
                replacement.Add(new RawLine($"{itemIndent}- {FormatScalar(FormFieldKind.String, item)}", terminator));
            }
        }

        var result = new List<RawLine>(lines.Count + replacement.Count);
        result.AddRange(lines.Take(blockStart));
        result.AddRange(replacement);
        result.AddRange(lines.Skip(blockEnd));

        return Join(result);
    }

    /// <summary>Result of trying to find a list key at an exact path, without changing anything.</summary>
    public readonly record struct ListLookup(bool Found, bool Ambiguous, IReadOnlyList<string>? Items)
    {
        public static readonly ListLookup NotFound = new(false, false, null);
    }

    /// <summary>
    /// Looks for a block- or inline-empty sequence at <paramref name="path"/>. Used by the
    /// form builder to decide whether a list field is editable, and as a fallback source
    /// for its current items.
    /// </summary>
    public static ListLookup FindList(string sectionText, IReadOnlyList<string> path)
    {
        var lines = SplitLines(sectionText);
        var matches = new List<int>();
        WalkKeyPaths(lines, (index, currentPath, _) =>
        {
            if (PathsEqual(currentPath, path))
            {
                matches.Add(index);
            }
        });

        if (matches.Count == 0)
        {
            return ListLookup.NotFound;
        }

        if (matches.Count > 1)
        {
            return new ListLookup(true, true, null);
        }

        var match = KeyLine().Match(lines[matches[0]].Content);
        if (!match.Success)
        {
            return ListLookup.NotFound;
        }

        var indentLen = match.Groups["indent"].Value.Length;
        var inline = match.Groups["value"].Value.Trim();
        if (inline.Length > 0)
        {
            return inline == "[]" ? new ListLookup(true, false, Array.Empty<string>()) : ListLookup.NotFound;
        }

        var items = new List<string>();
        var j = matches[0] + 1;
        while (j < lines.Count && IsListItemAtOrBelow(lines[j].Content, indentLen))
        {
            var trimmed = lines[j].Content.TrimStart(' ', '\t');
            if (trimmed.Length > 0)
            {
                items.Add(UnquoteScalar(trimmed.TrimStart('-').TrimStart(' ', '\t')));
            }

            j++;
        }

        return new ListLookup(true, false, items);
    }

    /// <summary>Strips a single layer of YAML quoting, undoing doubled single quotes — the inverse of <see cref="FormatScalar"/>.</summary>
    public static string UnquoteScalar(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return raw;
    }

    /// <summary>Renders a value the way this editor's own patches use — the counterpart to the parser's classification.</summary>
    public static string FormatScalar(FormFieldKind kind, object? value)
    {
        switch (kind)
        {
            case FormFieldKind.Bool:
                return value is true ? "true" : "false";
            case FormFieldKind.Int:
                return value switch
                {
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    _ => "0",
                };
            default:
                var text = value?.ToString() ?? string.Empty;
                return FormatStringScalar(text);
        }
    }

    private static string FormatStringScalar(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (NeedsQuoting(value))
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        return value;
    }

    private static bool NeedsQuoting(string value)
    {
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        if (value.Contains(": ", StringComparison.Ordinal) ||
            value.Contains(" #", StringComparison.Ordinal) ||
            value.Contains('\n') ||
            value.Contains('\t'))
        {
            return true;
        }

        var first = value[0];
        if ("#&*!|>%@`\"'-?:,[]{}".IndexOf(first) >= 0)
        {
            return true;
        }

        return ReservedWord().IsMatch(value) || LooksNumeric().IsMatch(value);
    }

    /// <summary>
    /// Walks every mapping-key line in order, tracking a stack of (indent, key) so each
    /// key line can be reported with its full path from the section root. Sequence-item
    /// lines and comments do not affect the stack — paths through a list of mappings are
    /// out of scope for this editor by design.
    /// </summary>
    private static void WalkKeyPaths(IReadOnlyList<RawLine> lines, Action<int, IReadOnlyList<string>, string?> onKeyLine)
    {
        var stack = new List<(int Indent, string Key)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var content = lines[i].Content;
            var trimmed = content.TrimStart(' ', '\t');
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var match = KeyLine().Match(content);
            if (!match.Success)
            {
                continue;
            }

            var indent = match.Groups["indent"].Value.Length;
            var key = match.Groups["key"].Value;

            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var path = new List<string>(stack.Count + 1);
            path.AddRange(stack.Select(s => s.Key));
            path.Add(key);

            var valueGroup = match.Groups["value"];
            var hasValue = valueGroup.Success && valueGroup.Value.Trim().Length > 0;
            onKeyLine(i, path, hasValue ? valueGroup.Value.Trim() : null);

            stack.Add((indent, key));
        }
    }

    private static bool IsListItemAtOrBelow(string content, int minIndent)
    {
        var trimmed = content.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
        {
            return true; // blank line inside a block sequence — tolerate it, don't split the block.
        }

        var indent = content.Length - trimmed.Length;
        return indent >= minIndent && trimmed.StartsWith('-');
    }

    private static string? ExtractValueText(string content)
    {
        var match = KeyLine().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static bool PathsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<RawLine> SplitLines(string text)
    {
        var lines = new List<RawLine>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && text[i] != '\n')
            {
                i++;
            }

            var contentEnd = i;
            string terminator;
            if (i < text.Length)
            {
                if (contentEnd > start && text[contentEnd - 1] == '\r')
                {
                    contentEnd--;
                    terminator = "\r\n";
                }
                else
                {
                    terminator = "\n";
                }

                i++;
            }
            else
            {
                terminator = string.Empty;
            }

            lines.Add(new RawLine(text[start..contentEnd], terminator));
        }

        return lines;
    }

    private static string Join(IReadOnlyList<RawLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.Full);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]*):(?:[ \t](?<value>.*))?$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^(?i:true|false|null|yes|no|on|off|~)$")]
    private static partial Regex ReservedWord();

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?$")]
    private static partial Regex LooksNumeric();
}
