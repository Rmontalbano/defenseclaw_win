using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Logs;

/// <summary>
/// Display redaction for log text: the Mac's <c>DisplayRedaction.text</c>, defence in depth for anything shown from the
/// runtime's own output, not a substitute for the producer redacting it. There is deliberately no switch to show the raw
/// text.
/// <para>
/// <b>Redact first, then truncate.</b> A credential that straddles the display limit would otherwise be cut mid-value and
/// leave its first characters readable, so <see cref="Text"/> masks the whole string and only then applies the limit.
/// </para>
/// <para>
/// Three passes: an <c>authorization:</c> / <c>authorization=</c> header loses everything to the end of its line; an
/// assignment of <c>api_key</c>, <c>access_token</c>, <c>refresh_token</c>, <c>token</c>, <c>password</c> or <c>secret</c>
/// (with <c>=</c>, <c>:</c> or just a space) and a <c>bearer</c> credential lose their value (a quoted one to its closing
/// quote, otherwise up to whitespace, a comma, a semicolon or a quote); and a JSON member whose <em>name</em> ends in one of those words
/// (<c>"api_key": "…"</c>, <c>"x.token": …</c>) loses its value, which the assignment pass cannot see because a quote sits
/// between the name and the colon. A name that only contains the word (<c>max_tokens</c>, <c>tokenizer</c>) is left alone.
/// Where the Mac matches a key at a word boundary, this also matches one after an underscore (<c>id_token=…</c>,
/// <c>client_secret=…</c>): the boundary there is "not a letter or digit".
/// </para>
/// </summary>
public static partial class DisplayRedaction
{
    /// <summary>What replaces a masked value.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The default limit on a displayed message, in characters.</summary>
    public const int DefaultLimit = 4096;

    private const int TimeoutMilliseconds = 2000;

    private const string KeyWords = "api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|secret";

    [GeneratedRegex(@"(?<![A-Za-z0-9])(authorization\s*[=:]\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex Authorization();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])((?:" + KeyWords + @")(?:\s*[=:]\s*|\s+)|bearer\s+)(""[^""]*(?:""|$)|'[^']*(?:'|$)|[^\s,;""]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex Assignment();

    [GeneratedRegex(
        @"(""[A-Za-z0-9_.\-]*?(?:" + KeyWords + @"|passwd|authorization)""\s*:\s*)(""(?:[^""\\]|\\.)*""|[^\s,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex JsonMember();

    /// <summary>
    /// <paramref name="value"/> with credentials masked, then cut to at most <paramref name="limit"/> characters.
    /// A <see langword="null"/> or empty value is an empty string.
    /// </summary>
    /// <param name="value">The text to display.</param>
    /// <param name="limit">The most characters to keep, after masking; <see cref="int.MaxValue"/> for no limit.</param>
    public static string Text(string? value, int limit = DefaultLimit)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string masked;
        try
        {
            masked = Authorization().Replace(value, "$1" + Mask);

            // JSON members first: the assignment pass would otherwise take the closing quote and brace of a value with it.
            masked = JsonMember().Replace(masked, "$1\"" + Mask + "\"");
            masked = Assignment().Replace(masked, "$1" + Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: showing none of it is the safe answer to "could not tell what was a credential".
            return Mask;
        }

        return Truncate(masked, limit);
    }

    private static string Truncate(string value, int limit)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= limit)
        {
            return value;
        }

        // Do not end on half a surrogate pair.
        var end = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return value[..end];
    }
}
