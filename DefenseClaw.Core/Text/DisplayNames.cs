using System.Globalization;
using System.Text;

namespace DefenseClaw.Core.Text;

/// <summary>
/// How a name that came from outside (a skill, plugin, MCP server, registry entry or tool - a directory name, a manifest field, a
/// registry index) is shown where an operator decides something from it: a review heading, the command box of a review, an Activity
/// row. A name is attacker-chosen text, so on its own it can make a confirmation read as a different one: a right-to-left override
/// (<c>U+202E</c>) reverses what follows it, zero-width characters make two names look alike, a newline pushes the rest of the line out of
/// sight, and a homoglyph (<c>pdf-tools</c> with a Cyrillic letter) looks the same and is not.
/// <para>
/// <see cref="Visible"/> spells every control and format character out (<c>‮</c>, <c>\n</c>) and so keeps a name on one line;
/// <see cref="DescribeUnusual"/> says what else is in it, for a warning beside the command. The text handed to the CLI is not touched:
/// this is for display only.
/// </para>
/// </summary>
public static class DisplayNames
{
    /// <summary>How many distinct characters <see cref="DescribeUnusual"/> lists before it says "…".</summary>
    public const int MaxDescribed = 6;

    /// <summary>
    /// <paramref name="text"/> with every control and format character written out: <c>\n</c>, <c>\r</c> and <c>\t</c> for those three,
    /// <c>\uXXXX</c> (<c>\UXXXXXXXX</c> beyond the BMP) for the rest - bidirectional overrides, zero-width characters, the other control
    /// characters, line and paragraph separators, spaces that are not U+0020, private-use and unassigned characters, and an unpaired
    /// surrogate. Everything else, including ordinary non-ASCII letters, is kept as it is. The result is always one line. A backslash is
    /// not doubled, so a Windows path stays readable; a name that literally contains <c>‮</c> as text is therefore shown the same
    /// as one that contains the character, which is why <see cref="DescribeUnusual"/> is the thing that tells them apart.
    /// </summary>
    public static string Visible(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var first = FirstToEscape(text);
        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        builder.Append(text, 0, first);

        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(c, text[i + 1]);
                if (NeedsEscape(codePoint))
                {
                    builder.Append(CultureInfo.InvariantCulture, $"\\U{codePoint:X8}");
                }
                else
                {
                    builder.Append(c).Append(text[i + 1]);
                }

                i++;
                continue;
            }

            switch (c)
            {
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (NeedsEscape(c))
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when <paramref name="text"/> has anything but printable ASCII in it: a character <see cref="Visible"/> would spell out, or any
    /// other non-ASCII character (a homoglyph looks the same as the letter it imitates, so it can only be flagged, not escaped).
    /// </summary>
    public static bool IsUnusual(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < ' ' or > '~')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What is unusual about <paramref name="text"/>, as the inside of a sentence's brackets: each distinct character as its code point and a
    /// short word for what kind it is (<c>U+202E bidirectional control</c>, <c>U+200B zero-width or invisible</c>, <c>U+0430 non-ASCII
    /// character</c>), in the order they first appear, at most <see cref="MaxDescribed"/> of them, then "…". Null when there is nothing unusual.
    /// </summary>
    public static string? DescribeUnusual(string? text)
    {
        if (!IsUnusual(text))
        {
            return null;
        }

        var seen = new HashSet<int>();
        var parts = new List<string>();
        var more = false;

        for (var i = 0; i < text!.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                codePoint = text[i];
            }

            if (codePoint is >= ' ' and <= '~' || !seen.Add(codePoint))
            {
                continue;
            }

            if (parts.Count == MaxDescribed)
            {
                more = true;
                break;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"U+{codePoint:X4} {KindOf(codePoint)}"));
        }

        return string.Join(", ", parts) + (more ? ", …" : string.Empty);
    }

    private static int FirstToEscape(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= ' ' and <= '~')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (NeedsEscape(char.ConvertToUtf32(c, text[i + 1])))
                {
                    return i;
                }

                i++;
                continue;
            }

            if (NeedsEscape(c))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A character that cannot be told from nothing, from another character or from a line break by looking at it: a control or format
    /// character, a separator other than U+0020, a private-use or unassigned code point, an unpaired surrogate.
    /// </summary>
    private static bool NeedsEscape(int codePoint)
    {
        if (codePoint is >= ' ' and <= '~')
        {
            return false;
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.SpaceSeparator or
            UnicodeCategory.PrivateUse or
            UnicodeCategory.OtherNotAssigned or
            UnicodeCategory.Surrogate;
    }

    private static string KindOf(int codePoint)
    {
        if (codePoint is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x200E or 0x200F or 0x061C)
        {
            return "bidirectional control";
        }

        if (codePoint is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF or 0x00AD or 0x180E or 0x034F)
        {
            return "zero-width or invisible";
        }

        if (codePoint is '\n' or '\r' or 0x0085 or 0x2028 or 0x2029)
        {
            return "line break";
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
        {
            UnicodeCategory.Control => "control character",
            UnicodeCategory.Format => "invisible format character",
            UnicodeCategory.SpaceSeparator => "space that is not U+0020",
            UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => "private-use or unassigned character",
            _ => "non-ASCII character",
        };
    }
}
