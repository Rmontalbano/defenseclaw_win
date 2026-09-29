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
/// <b>What else is refused</b> (each one is a way a line-oriented rewrite silently
/// corrupts YAML, so the editor declines instead of guessing):
/// </para>
/// <list type="bullet">
/// <item>a scalar whose value is a block scalar (<c>|</c>, <c>&gt;</c>), a tag (<c>!</c>), an
/// anchor (<c>&amp;</c>) or an alias (<c>*</c>) — replacing it would drop or break the
/// construct;</item>
/// <item>a scalar (or list) whose next non-blank, non-comment line is indented deeper than
/// the key — that is a multi-line scalar or nested content, not the one-line value this
/// editor assumes;</item>
/// <item>a list whose block holds a comment line or a trailing <c>#</c> comment on an item —
/// rebuilding the block would drop the comment, and a comment line followed by another item
/// would end the block early and leave the old items behind as duplicates;</item>
/// <item>list items that are themselves mappings, nested sequences, flow collections, block
/// scalars, aliases or empty;</item>
/// <item>any replacement value containing a line break (a single-quoted scalar would fold it
/// into a space).</item>
/// </list>
/// <para>
/// Path segments are the full dotted path from the document root, including the
/// section's own top-level key — e.g. <c>["guardrail", "connectors", "claudecode",
/// "mode"]</c> for <c>guardrail.connectors.claudecode.mode"</c> — because
/// <paramref name="sectionText"/> always begins with that top-level key's own line.
/// </para>
/// <para>
/// <b>Line endings.</b> Every line keeps its own terminator. New list-item lines take the
/// key line's terminator, or — when the key is the last line of the section and has none
/// (a file that ends without a newline) — the section's detected line ending, so items are
/// never glued onto one line. The last rebuilt line inherits the terminator of the last
/// line it replaced, which preserves "no newline at end of file".
/// </para>
/// </summary>
public static partial class YamlSectionEditor
{
    /// <summary>One physical line, split so it can be put back together byte-for-byte except where changed.</summary>
    private readonly record struct RawLine(string Content, string Terminator)
    {
        public string Full => Content + Terminator;
    }

    /// <summary>
    /// Result of trying to find a scalar leaf at an exact path. <see cref="Unsupported"/> is
    /// set when the key exists exactly once but its line cannot be rewritten safely (trailing
    /// comment, block scalar/anchor/alias/tag, multi-line value) — the form shows the field
    /// read-only with a reason instead of offering an edit that will be refused.
    /// </summary>
    public readonly record struct ScalarLookup(bool Found, bool Ambiguous, string? RawValue, bool Unsupported = false)
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
        var matches = FindKeyLines(lines, path, scalarOnly: true);

        return matches.Count switch
        {
            0 => ScalarLookup.NotFound,
            1 => new ScalarLookup(
                true,
                false,
                ExtractValueText(lines[matches[0]].Content),
                Unsupported: !CanRewriteScalarLine(lines, matches[0])),
            _ => new ScalarLookup(true, true, null),
        };
    }

    /// <summary>
    /// Replaces the value of the scalar at <paramref name="path"/> with
    /// <paramref name="newRawValue"/> (already YAML-formatted — see
    /// <see cref="FormatScalar"/>). Returns the patched section text, or
    /// <see langword="null"/> if the path was not found, was ambiguous, or the line could
    /// not be safely rewritten (e.g. it carries a trailing comment, is a block scalar or
    /// alias, or continues on the next line).
    /// </summary>
    public static string? TrySetScalar(string sectionText, IReadOnlyList<string> path, string newRawValue)
    {
        ArgumentNullException.ThrowIfNull(newRawValue);

        // A raw value with a line break cannot be a one-line scalar (single quotes would fold
        // it into a space), and an empty one would silently turn the key into null.
        if (newRawValue.Length == 0 || ContainsLineBreak(newRawValue))
        {
            return null;
        }

        var lines = SplitLines(sectionText);
        var matches = FindKeyLines(lines, path, scalarOnly: true);

        if (matches.Count != 1)
        {
            return null;
        }

        var lineIndex = matches[0];
        if (!CanRewriteScalarLine(lines, lineIndex))
        {
            // A trailing comment, block scalar, alias, multi-line value — refuse rather
            // than risk eating or corrupting it.
            return null;
        }

        var match = KeyLine().Match(lines[lineIndex].Content);
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
    /// list at all, or when the block contains comments, nested content or anything but
    /// plain/quoted scalar items (see the class remarks).
    /// </summary>
    public static string? TrySetList(string sectionText, IReadOnlyList<string> path, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Any(ContainsLineBreak))
        {
            return null;
        }

        var lines = SplitLines(sectionText);
        var matches = FindKeyLines(lines, path, scalarOnly: false);

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
        var keyIndentLength = indent.Length;

        var scanned = ScanListBlock(lines, keyLineIndex, keyIndentLength, inlineValue);
        if (scanned is not { } block)
        {
            return null;
        }

        var keyTerminator = lines[keyLineIndex].Terminator;

        // The key line may have no terminator (last line of a file without a trailing
        // newline). Items that follow it still need line breaks, so borrow the section's.
        var lineEnding = keyTerminator.Length > 0 ? keyTerminator : DetectLineEnding(lines);

        // What ended the old block ends the new one: keeps "no newline at end of file" intact.
        var lastOldTerminator = block.End > keyLineIndex + 1 ? lines[block.End - 1].Terminator : keyTerminator;

        var result = new List<RawLine>(lines.Count + items.Count);
        result.AddRange(lines.Take(keyLineIndex));

        if (items.Count == 0)
        {
            result.Add(new RawLine($"{indent}{key}: []", lastOldTerminator));
        }
        else
        {
            result.Add(new RawLine($"{indent}{key}:", lineEnding));
            for (var i = 0; i < items.Count; i++)
            {
                var terminator = i == items.Count - 1 ? lastOldTerminator : lineEnding;
                result.Add(new RawLine($"{block.ItemIndent}- {FormatScalar(FormFieldKind.String, items[i])}", terminator));
            }
        }

        result.AddRange(lines.Skip(block.End));
        return Join(result);
    }

    /// <summary>
    /// Result of trying to find a list key at an exact path, without changing anything.
    /// <see cref="Unsupported"/> is set when the key exists exactly once but its block cannot
    /// be rewritten safely (comments inside the block, nested content, non-scalar items).
    /// </summary>
    public readonly record struct ListLookup(bool Found, bool Ambiguous, IReadOnlyList<string>? Items, bool Unsupported = false)
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
        var matches = FindKeyLines(lines, path, scalarOnly: false);

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
        if (inline.Length > 0 && inline != "[]")
        {
            return ListLookup.NotFound;
        }

        var block = ScanListBlock(lines, matches[0], indentLen, inline);
        return block is { } scanned
            ? new ListLookup(true, false, scanned.Items)
            : new ListLookup(true, false, null, Unsupported: true);
    }

    /// <summary>
    /// True when <paramref name="raw"/>, written as a plain (unquoted) scalar, would be read
    /// back as something other than a string — a number, a bool/null word, a timestamp.
    /// The form builder uses this to keep such fields read-only: <see cref="FormatScalar"/>
    /// quotes strings that look like these, so editing <c>threshold: 0.8</c> as a "string"
    /// field would silently turn the float into the string <c>'0.85'</c>.
    /// </summary>
    public static bool LooksLikeNonStringPlainScalar(string raw) =>
        raw.Length > 0 && (ReservedWord().IsMatch(raw) || LooksNumeric().IsMatch(raw) || LooksLikeTimestamp().IsMatch(raw));

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

    /// <summary>
    /// Whether a string must be single-quoted to survive as a string: leading/trailing
    /// whitespace, <c>": "</c>, <c>" #"</c>, a trailing colon (<c>D:</c>, <c>Blocked by policy:</c> — an
    /// unquoted trailing colon reads as a mapping key), YAML indicator characters up front,
    /// and anything a YAML parser would resolve to a non-string (bool/null words, numbers in
    /// any base, <c>.inf</c>/<c>.nan</c>, sexagesimal, timestamps). Over-quoting is always safe; under-quoting is not.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        if (value.Contains(": ", StringComparison.Ordinal) ||
            value.Contains(" #", StringComparison.Ordinal) ||
            value.Contains('\n') ||
            value.Contains('\r') ||
            value.Contains('\t'))
        {
            return true;
        }

        if (value[^1] == ':')
        {
            return true;
        }

        var first = value[0];
        if ("#&*!|>%@`\"'-?:,[]{}".IndexOf(first) >= 0)
        {
            return true;
        }

        return ReservedWord().IsMatch(value) || LooksNumeric().IsMatch(value) || LooksLikeTimestamp().IsMatch(value);
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

    /// <summary>Indexes of every key line whose full path equals <paramref name="path"/> (only those with an inline value when <paramref name="scalarOnly"/>).</summary>
    private static List<int> FindKeyLines(IReadOnlyList<RawLine> lines, IReadOnlyList<string> path, bool scalarOnly)
    {
        var matches = new List<int>();
        WalkKeyPaths(lines, (index, currentPath, valueText) =>
        {
            if ((!scalarOnly || valueText is not null) && PathsEqual(currentPath, path))
            {
                matches.Add(index);
            }
        });

        return matches;
    }

    /// <summary>
    /// True when the scalar on <paramref name="lineIndex"/> can be replaced by rewriting
    /// that one line: no <c>#</c> anywhere in the value (a trailing comment would be eaten),
    /// not a block scalar / tag / anchor / alias, and not continued on the following lines.
    /// </summary>
    private static bool CanRewriteScalarLine(IReadOnlyList<RawLine> lines, int lineIndex)
    {
        var match = KeyLine().Match(lines[lineIndex].Content);
        if (!match.Success)
        {
            return false;
        }

        var value = match.Groups["value"].Value;
        if (value.Contains('#'))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || "|>&*!".IndexOf(trimmed[0]) >= 0)
        {
            return false;
        }

        return !HasDeeperContinuation(lines, lineIndex, match.Groups["indent"].Value.Length);
    }

    /// <summary>
    /// True when the first non-blank, non-comment line after <paramref name="lineIndex"/>
    /// is indented deeper than <paramref name="keyIndent"/>. After a key with an inline
    /// scalar that can only be a multi-line (folded plain/quoted) scalar's continuation —
    /// which a one-line rewrite would leave behind as garbage.
    /// </summary>
    private static bool HasDeeperContinuation(IReadOnlyList<RawLine> lines, int lineIndex, int keyIndent)
    {
        for (var j = lineIndex + 1; j < lines.Count; j++)
        {
            var content = lines[j].Content;
            var trimmed = content.TrimStart(' ', '\t');
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            return content.Length - trimmed.Length > keyIndent;
        }

        return false;
    }

    /// <summary>The recognized extent of a list value: lines <c>[Start, End)</c> are the items to be replaced.</summary>
    private readonly record struct ListBlock(int Start, int End, string ItemIndent, IReadOnlyList<string> Items);

    /// <summary>
    /// Finds the block of <c>- item</c> lines that belongs to the list key on
    /// <paramref name="keyLineIndex"/>, or returns <see langword="null"/> when the value is
    /// anything this editor cannot rewrite without risk (see the class remarks).
    /// <para>
    /// <c>End</c> is one past the last item line, so blank lines and comments that trail the
    /// list — typically the separator before the next key — are left where they are. Blank
    /// lines <i>between</i> items are tolerated (the rebuilt block simply drops them), but a
    /// comment line before or between items is refused: a scan that stopped at it would leave
    /// the remaining items behind and the rewrite would duplicate them.
    /// </para>
    /// </summary>
    private static ListBlock? ScanListBlock(IReadOnlyList<RawLine> lines, int keyLineIndex, int keyIndent, string inlineValue)
    {
        var start = keyLineIndex + 1;

        if (inlineValue.Length > 0)
        {
            // Only an inline empty list ("key: []") is a list we recognize; anything
            // else inline (a scalar, a flow list, a comment) is not one this can touch.
            if (inlineValue != "[]" || HasDeeperContinuation(lines, keyLineIndex, keyIndent))
            {
                return null;
            }

            return new ListBlock(start, start, new string(' ', keyIndent), Array.Empty<string>());
        }

        var items = new List<string>();
        var itemIndentLength = -1;
        var itemIndent = string.Empty;
        var end = start;
        var sawComment = false;

        for (var i = start; i < lines.Count; i++)
        {
            var content = lines[i].Content;
            var trimmed = content.TrimStart(' ', '\t');
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed[0] == '#')
            {
                sawComment = true;
                continue;
            }

            var indent = content.Length - trimmed.Length;
            if (!IsSequenceEntry(trimmed) || indent < keyIndent)
            {
                if (indent > keyIndent)
                {
                    // Deeper, non-item content under the key: a nested mapping, or the
                    // continuation of a multi-line item. Not a plain list of scalars.
                    return null;
                }

                break;
            }

            if (sawComment)
            {
                // A comment sits before/between items: rebuilding would drop it, and an
                // early stop would duplicate the items after it.
                return null;
            }

            if (itemIndentLength < 0)
            {
                itemIndentLength = indent;
                itemIndent = content[..indent];
            }
            else if (indent != itemIndentLength)
            {
                return null; // nested sequence or ragged indentation
            }

            var afterDash = trimmed.Length == 1 ? string.Empty : trimmed[1..].Trim();
            if (!TryReadListItem(afterDash, out var item))
            {
                return null;
            }

            items.Add(item);
            end = i + 1;
        }

        return new ListBlock(start, end, itemIndentLength < 0 ? new string(' ', keyIndent) : itemIndent, items);
    }

    /// <summary>A block-sequence entry: a dash that is alone or followed by whitespace (so <c>-1</c> and <c>--flag</c> are not entries, <c>---</c> is not).</summary>
    private static bool IsSequenceEntry(string trimmed) =>
        trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' ' || trimmed[1] == '\t');

    /// <summary>
    /// Reads one list item's text (everything after the dash) as a scalar this editor may
    /// rewrite. Refuses empty items, block scalars, aliases/anchors/tags, flow collections,
    /// nested sequences and mapping entries, and any unquoted item with a trailing comment.
    /// </summary>
    private static bool TryReadListItem(string afterDash, out string item)
    {
        item = string.Empty;
        if (afterDash.Length == 0)
        {
            return false; // "-" alone: a null item or a nested block
        }

        var first = afterDash[0];
        if (first is '\'' or '"')
        {
            // A quoted item must be closed on this line with nothing after the closing
            // quote (anything after it is a comment or a multi-line scalar).
            if (afterDash.Length < 2 || afterDash[^1] != first)
            {
                return false;
            }

            item = UnquoteScalar(afterDash);
            return true;
        }

        if ("|>&*![{#".IndexOf(first) >= 0)
        {
            return false;
        }

        if (first == '-' && afterDash.Length > 1 && (afterDash[1] == ' ' || afterDash[1] == '\t'))
        {
            return false; // "- - x": a nested sequence
        }

        if (afterDash.Contains(" #", StringComparison.Ordinal) ||
            afterDash.Contains("\t#", StringComparison.Ordinal) ||
            afterDash.Contains(": ", StringComparison.Ordinal) ||
            afterDash.EndsWith(':'))
        {
            return false; // trailing comment, or a mapping entry rather than a scalar
        }

        item = afterDash;
        return true;
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

    private static bool ContainsLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

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

    /// <summary>The first real line terminator in the text (so CRLF files stay CRLF), or <c>"\n"</c> when there is none to copy.</summary>
    private static string DetectLineEnding(IReadOnlyList<RawLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Terminator.Length > 0)
            {
                return line.Terminator;
            }
        }

        return "\n";
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

    /// <summary>YAML 1.1 + 1.2 bool/null words (and the <c>y</c>/<c>n</c> shorthands some 1.1 loaders accept).</summary>
    [GeneratedRegex(@"^(?i:true|false|null|yes|no|on|off|y|n|~)$")]
    private static partial Regex ReservedWord();

    /// <summary>
    /// Anything a YAML parser may resolve to a number: decimals, floats, exponents, digit
    /// separators, hex/octal/binary, <c>.inf</c>/<c>.nan</c>, and 1.1 sexagesimal (<c>1:30</c>).
    /// </summary>
    [GeneratedRegex(
        @"^[-+]?(?:0x[0-9a-f_]+|0o[0-7_]+|0b[01_]+|\.inf|\.nan|(?:\.[0-9]+|[0-9][0-9_]*(?:\.[0-9_]*)?)(?:e[-+]?[0-9]+)?|[0-9][0-9_]*(?::[0-5]?[0-9])+(?:\.[0-9_]*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooksNumeric();

    /// <summary>A leading <c>YYYY-M-D</c>, which YAML 1.1 loaders resolve to a date/timestamp.</summary>
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}(?:$|[Tt ].*)")]
    private static partial Regex LooksLikeTimestamp();
}
