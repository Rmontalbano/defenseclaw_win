using System.Buffers;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Observability;

/// <summary>
/// What of a destination's address may be shown: the host, and the port when there is one. Nothing else leaves this class.
/// <para>
/// A destination URL is where credentials end up when an operator pastes one from a vendor's console: <c>user:password@host</c>, a token in the
/// query (<c>?api_key=…</c>), a token in the path (chat webhooks, some APM endpoints) or in the fragment. The DefenseClaw TUI strips userinfo, query
/// and fragment (<c>redact_endpoint_for_display</c>) and keeps the path; this keeps only <c>host[:port]</c>, so a token in the path is not shown
/// either. The scheme is dropped too: it is the host the operator needs to recognise a destination by.
/// </para>
/// <para>
/// <b>Fail closed.</b> The text is read by hand, not by <see cref="Uri"/> (which normalises a default port away and reads <c>//host</c> as a Windows
/// share), and the host has to look like a host - letters, digits, <c>-</c>, <c>.</c>, <c>_</c>, or a bracketed IPv6 literal - with a numeric port. Anything
/// else is <see cref="Unreadable"/>, never the raw text: a string this cannot read is a string whose parts are not known to be safe.
/// </para>
/// </summary>
public static partial class EndpointDisplay
{
    /// <summary>What stands in for an address that cannot be read as a host. Never carries any of the original text.</summary>
    public const string Unreadable = "<unreadable endpoint>";

    private const int MaxEndpointLength = 2048;
    private const int MaxHostLength = 253;
    private const int RegexTimeoutMilliseconds = 1000;

    /// <summary>Where an authority ends: the first of a slash, a question mark, a number sign or a backslash.</summary>
    private static readonly SearchValues<char> AuthorityEnd = SearchValues.Create("/?#\\");

    /// <summary>A URL inside a sentence: a scheme, <c>://</c>, then everything up to a space or a quote.</summary>
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s""'<>`\\]+", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex UrlInText();

    /// <summary>
    /// <c>host</c> or <c>host:port</c> of <paramref name="endpoint"/> (a URL, or a bare <c>host:port</c>), without userinfo, path, query or
    /// fragment. Empty for an empty value, <see cref="Unreadable"/> for one that has no host this can vouch for.
    /// </summary>
    public static string Host(string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length > MaxEndpointLength)
        {
            return Unreadable;
        }

        var rest = value;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            if (!IsScheme(value.AsSpan(0, schemeEnd)))
            {
                return Unreadable;
            }

            rest = value[(schemeEnd + 3)..];
        }
        else if (value.StartsWith("//", StringComparison.Ordinal))
        {
            rest = value[2..];
        }

        // The authority ends at the first / ? # or \ (the URL standard reads a backslash as a slash for the web schemes).
        var end = rest.AsSpan().IndexOfAny(AuthorityEnd);
        var authority = end < 0 ? rest : rest[..end];

        // An @ in the path means the address is not what it looks like: the usual cause is a password with a raw '/' in it
        // (user:pa/ss@host), which a parser reads as host "user" and which would show the start of the credential as a host. Not guessed at.
        if (end >= 0)
        {
            var tail = rest.AsSpan(end);
            var queryOrFragment = tail.IndexOfAny('?', '#');
            if ((queryOrFragment < 0 ? tail : tail[..queryOrFragment]).Contains('@'))
            {
                return Unreadable;
            }
        }

        // Userinfo is everything up to the last @: it may itself hold a ':' and an '@'.
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        return TryHostAndPort(authority, out var shown) ? shown : Unreadable;
    }

    /// <summary>
    /// <paramref name="text"/> made fit to show where it came from the runtime and may mention an address (a delivery error such as
    /// <c>Post "https://user:pass@collector:4318/v1/logs?token=…": context deadline exceeded</c>): every URL in it is cut to
    /// <c>scheme://host[:port]</c>, then <see cref="DisplayRedaction.Prose"/> masks what looks like a credential (<c>token=…</c>, a bearer value, a
    /// key with a known prefix), control and bidirectional characters are written out, and the result is cut to <paramref name="limit"/>.
    /// </summary>
    public static string ScrubText(string? text, int limit = 240)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!TryCutUrls(text, out var scrubbed))
        {
            return DisplayRedaction.Mask;
        }

        scrubbed = DisplayNames.Visible(DisplayRedaction.Prose(scrubbed, int.MaxValue)).Trim();
        return scrubbed.Length <= limit ? scrubbed : scrubbed[..Math.Max(0, limit - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// One line of a command's output made fit to store and show: every URL in it is cut to <c>scheme://host[:port]</c> (<see cref="Host"/>), then
    /// what looks like a credential is masked (<see cref="DisplayRedaction.Prose"/>: <c>token=...</c>, a bearer value, a key with a known prefix, an
    /// <c>Authorization</c> header). Unlike <see cref="ScrubText"/> the line is otherwise exactly as it was - no trimming, no cutting to a length, no
    /// control characters written out - because a runner's per-line filter must not change the stored lines of a JSON document or of an indented
    /// report. A line the pattern matcher gives up on (its time limit) is replaced by <see cref="DisplayRedaction.Mask"/>; null and empty are empty.
    /// </summary>
    public static string ScrubLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        return TryCutUrls(line, out var cut) ? DisplayRedaction.Prose(cut, int.MaxValue) : DisplayRedaction.Mask;
    }

    /// <summary>Every URL in <paramref name="text"/> cut to <c>scheme://host[:port]</c>, or <see cref="Unreadable"/> where it cannot be read; false when the matcher timed out.</summary>
    private static bool TryCutUrls(string text, out string cut)
    {
        try
        {
            cut = UrlInText().Replace(text, static match =>
            {
                var schemeEnd = match.Value.IndexOf("://", StringComparison.Ordinal);
                var host = Host(match.Value);
                return host == Unreadable ? Unreadable : match.Value[..schemeEnd] + "://" + host;
            });
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            cut = string.Empty;
            return false;
        }
    }

    private static bool IsScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.Length is 0 or > 16 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryHostAndPort(string authority, out string shown)
    {
        shown = string.Empty;
        if (authority.Length == 0)
        {
            return false;
        }

        string host;
        string port;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 2 || !IsIpv6Literal(authority.AsSpan(1, close - 1)))
            {
                return false;
            }

            host = authority[..(close + 1)];
            var after = authority[(close + 1)..];
            if (after.Length == 0)
            {
                port = string.Empty;
            }
            else if (after[0] == ':')
            {
                port = after[1..];
            }
            else
            {
                return false;
            }
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon >= 0 && authority.IndexOf(':', colon + 1) >= 0)
            {
                return false; // a bare IPv6 address, or something that is not host:port
            }

            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? string.Empty : authority[(colon + 1)..];
            if (!IsHostName(host))
            {
                return false;
            }
        }

        if (port.Length > 0 && (port.Length > 5 || !port.All(char.IsAsciiDigit) || int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) > 65535))
        {
            return false;
        }

        shown = port.Length == 0 ? host : host + ":" + port;
        return true;
    }

    private static bool IsHostName(string host)
    {
        if (host.Length is 0 or > MaxHostLength)
        {
            return false;
        }

        foreach (var c in host)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIpv6Literal(ReadOnlySpan<char> inside)
    {
        // Hex digits, colons and a dotted tail (::ffff:192.0.2.1); a zone id (%eth0) is allowed after them.
        var zone = inside.IndexOf('%');
        var address = zone < 0 ? inside : inside[..zone];
        if (address.Length == 0)
        {
            return false;
        }

        foreach (var c in address)
        {
            if (!char.IsAsciiHexDigit(c) && c is not (':' or '.'))
            {
                return false;
            }
        }

        if (zone >= 0)
        {
            var id = inside[(zone + 1)..];
            if (id.Length is 0 or > 32)
            {
                return false;
            }

            foreach (var c in id)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                {
                    return false;
                }
            }
        }

        return address.Contains(':');
    }
}
