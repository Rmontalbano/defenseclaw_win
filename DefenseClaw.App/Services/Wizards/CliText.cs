using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Small helpers for text a CLI printed. <b>The CLI's stdout is CRLF on Windows</b> — Python's text
/// mode turns every <c>\n</c> into <c>\r\n</c> on a pipe — and the parsers that read it (Click's help
/// screens, <c>guardrail status</c>) were written against LF samples, so each of them starts by
/// normalising through here rather than trusting where a stray <c>\r</c> might land.
/// </summary>
internal static partial class CliText
{
    /// <summary>
    /// Every line break — <c>\r\n</c>, a lone <c>\r</c>, and the doubled <c>\r\r\n</c> a CRLF-writing child
    /// piped through a second translation produces — becomes one <c>\n</c>.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Contains('\r', StringComparison.Ordinal)
            ? LineBreak().Replace(text, "\n")
            : text;
    }

    [GeneratedRegex(@"\r*\n|\r", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();
}
