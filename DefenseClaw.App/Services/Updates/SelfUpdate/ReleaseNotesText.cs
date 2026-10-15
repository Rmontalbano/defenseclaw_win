using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>
/// Release notes as plain text for a label: the GitHub markdown is read, never rendered, so a release body cannot put an image, a link target
/// or markup in front of the operator. Headings lose their hashes, bullets become dots, links keep their text, code fences and HTML tags go.
/// </summary>
internal static partial class ReleaseNotesText
{
    /// <summary>The most characters kept; a longer body is cut with a pointer to the release page.</summary>
    public const int MaxChars = 4000;

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>\r\n]{0,200}>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(\*\*|__|~~|`)", RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "This release has no notes.";
        }

        var builder = new StringBuilder();
        var inFence = false;
        var blank = 0;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                line = Image().Replace(line, string.Empty);
                line = Link().Replace(line, "$1");
                line = Tag().Replace(line, string.Empty);
                line = Heading().Replace(line, string.Empty);
                line = Bullet().Replace(line, "$1• ");
                line = Emphasis().Replace(line, string.Empty);
            }

            if (line.Trim().Length == 0)
            {
                if (++blank > 1 || builder.Length == 0)
                {
                    continue;
                }
            }
            else
            {
                blank = 0;
            }

            _ = builder.Append(line).Append('\n');
            if (builder.Length > MaxChars)
            {
                break;
            }
        }

        var text = builder.ToString().Trim();
        return text.Length > MaxChars ? text[..MaxChars].TrimEnd() + "\n… (the release page has the rest)" : text.Length == 0 ? "This release has no notes." : text;
    }

    /// <summary>The first line with words in it, for the banner's second line.</summary>
    public static string FirstLine(string plain)
    {
        foreach (var line in plain.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length > 140 ? trimmed[..140] + "…" : trimmed;
            }
        }

        return string.Empty;
    }
}
