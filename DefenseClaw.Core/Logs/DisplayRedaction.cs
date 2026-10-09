using System.Text.RegularExpressions;
using DefenseClaw.Core.Security;

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
/// What is masked, in the order the passes run:
/// </para>
/// <list type="bullet">
/// <item><description>a PEM private key, header to closing line (or to the end of the text when it is cut short);</description></item>
/// <item><description>an <c>authorization:</c> / <c>authorization=</c> header and a <c>Cookie:</c> / <c>Set-Cookie:</c> header lose everything to the
/// end of their line;</description></item>
/// <item><description>a JSON member whose <em>name</em> ends in a credential word (<c>"api_key": "…"</c>, <c>"x.token": …</c>,
/// <c>"private_key"</c>, <c>"secretKey"</c>, <c>"client_secret_value"</c>, <c>"cookie"</c>) loses its value, which the assignment pass cannot see
/// because a quote sits between the name and the colon;</description></item>
/// <item><description>an assignment of <c>api_key</c>, <c>access_token</c>, <c>refresh_token</c>, <c>token</c>, <c>password</c> or <c>secret</c>
/// (with <c>=</c>, <c>:</c> or just a space) and a <c>bearer</c> credential lose their value (a quoted one to its closing
/// quote, otherwise up to whitespace, a comma, a semicolon or a quote); with <c>=</c> or <c>:</c> only, so does
/// <c>private_key</c>, <c>secret_key</c>, <c>AWS_SECRET_ACCESS_KEY</c>, <c>client_secret…</c>, <c>passwd</c>, <c>pwd</c> and <c>passphrase</c>, and a name
/// that runs into <c>secret</c>, <c>token</c>, <c>password</c>, <c>passwd</c>, <c>passphrase</c> or <c>api_key</c> without a separator
/// (<c>mysecret=…</c>, <c>csrftoken=…</c>);</description></item>
/// <item><description>the shapes <see cref="SecretHeuristics"/> knows by their look alone, wherever they stand in the text: a chat webhook URL (Slack, Discord,
/// Teams, Webex, Zapier), and a token with a published prefix (<c>ghp_…</c>, <c>github_pat_…</c>, <c>sk-…</c>, <c>xox…</c>, <c>AKIA…</c>, a JWT and the like);</description></item>
/// <item><description>the password of a <c>scheme://user:password@host</c> URL.</description></item>
/// </list>
/// <para>
/// A name that only contains a credential word (<c>max_tokens</c>, <c>tokenizer</c>, <c>passwordless</c>) is left alone.
/// Where the Mac matches a key at a word boundary, this also matches one after an underscore (<c>id_token=…</c>,
/// <c>client_secret=…</c>): the boundary there is "not a letter or digit". Every pass has a timeout; input that is slow enough to hit it is
/// masked whole rather than shown.
/// </para>
/// </summary>
public static partial class DisplayRedaction
{
    /// <summary>What replaces a masked value.</summary>
    public const string Mask = "[redacted]";

    /// <summary>The default limit on a displayed message, in characters.</summary>
    public const int DefaultLimit = 4096;

    private const int TimeoutMilliseconds = 2000;

    /// <summary>Credential names that may be followed by <c>=</c>, <c>:</c> or just whitespace (<c>apikey sk-… done</c>).</summary>
    private const string KeyWords = "api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|secret";

    /// <summary>
    /// Credential names that count only with an explicit <c>=</c> or <c>:</c>: a bare word after them is more often prose (<c>pwd /tmp</c>) than a
    /// value. <c>client_secret</c> takes any short continuation (<c>client_secret_value</c>, <c>clientSecretBasic</c>), bounded so a long run of name
    /// characters cannot make the match quadratic.
    /// </summary>
    private const string ExplicitWords =
        "secret[_-]?access[_-]?key|secret[_-]?key|private[_-]?key|client[_-]?secret[A-Za-z0-9_.-]{0,40}|passwd|pwd|passphrase";

    /// <summary>A masked value: a quoted one to its closing quote, otherwise up to whitespace, a comma, a semicolon or a quote.</summary>
    private const string AssignedValue = @"(""[^""]*(?:""|$)|'[^']*(?:'|$)|[^\s,;""]+)";

    [GeneratedRegex(SecretShapes.PrivateKeyBlock, RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(authorization\s*[=:]\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex Authorization();

    [GeneratedRegex(@"(?<![A-Za-z0-9])((?:set-)?cookie\s*:\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex CookieHeader();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])((?:" + KeyWords + @")(?:\s*[=:]\s*|\s+)|(?:" + ExplicitWords + @")\s*[=:]\s*|bearer\s+)" + AssignedValue,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex Assignment();

    // The same pass without its whitespace form, for sentences (see Prose): "an elevated token to read the log" is prose, not "token = to".
    [GeneratedRegex(
        "(?<![A-Za-z0-9])((?:" + KeyWords + @")\s*[=:]\s*|(?:" + ExplicitWords + @")\s*[=:]\s*|bearer\s+)" + AssignedValue,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex ProseAssignment();

    // A name that runs straight into the word - mysecret=, csrftoken=, authToken:, dbpassword= - which the pass above does not see
    // because a letter or digit stands in front of it. Only "=" or ":", and only when something precedes the word, so the two passes
    // never cover the same text.
    [GeneratedRegex(
        @"(?<=[A-Za-z0-9])((?:secret|token|password|passwd|passphrase|api[_-]?key)\s*[=:]\s*)" + AssignedValue,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex RunOnAssignment();

    [GeneratedRegex(
        @"(""[A-Za-z0-9_.\-]*?(?:" + KeyWords + "|" + ExplicitWords + @"|authorization|cookie)""\s*:\s*)(""(?:[^""\\]|\\.)*""|[^\s,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex JsonMember();

    [GeneratedRegex(
        "(?:" + SecretShapes.SlackWebhook + ")|(?:" + SecretShapes.DiscordWebhook + ")|(?:" + SecretShapes.TeamsWebhook + ")|(?:" +
        SecretShapes.WebexWebhook + ")|(?:" + SecretShapes.ZapierWebhook + ")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex WebhookUrl();

    [GeneratedRegex(
        "(?:" + SecretShapes.OpenAiStyleKey + ")|(?:" + SecretShapes.GitHubToken + ")|(?:" + SecretShapes.GitHubFineGrainedToken + ")|(?:" +
        SecretShapes.SlackToken + ")|(?:" + SecretShapes.AwsAccessKeyId + ")|(?:" + SecretShapes.GoogleApiKey + ")|(?:" +
        SecretShapes.JsonWebToken + ")|(?:" + SecretShapes.GitLabToken + ")|(?:" + SecretShapes.NpmToken + ")|(?:" +
        SecretShapes.HuggingFaceToken + ")|(?:" + SecretShapes.StripeKey + ")",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex PrefixedToken();

    [GeneratedRegex(SecretShapes.UrlUserInfo, RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex UrlUserInfo();

    /// <summary>
    /// <paramref name="value"/> with credentials masked, then cut to at most <paramref name="limit"/> characters.
    /// A <see langword="null"/> or empty value is an empty string.
    /// </summary>
    /// <param name="value">The text to display.</param>
    /// <param name="limit">The most characters to keep, after masking; <see cref="int.MaxValue"/> for no limit.</param>
    public static string Text(string? value, int limit = DefaultLimit) => Redact(value, limit, bareWordAssignments: true);

    /// <summary>
    /// <see cref="Text"/> for a sentence a program wrote for a person (a plane's reason, a degraded line, guidance), as opposed to a
    /// command line or a log record. Every pass runs except one: a credential word followed by a space and a word is <i>not</i> taken as an
    /// assignment. Without that, "the gateway needs an elevated token to read the Security channel" comes out as "an elevated token
    /// [redacted] read the Security channel" - the reason is the whole point of showing it, and "token" is an ordinary word in prose. What
    /// still goes: <c>name=value</c> and <c>name: value</c> for the credential names, JSON members, <c>Authorization</c> and cookie headers, a
    /// bearer credential, PEM blocks, webhook URLs, the token shapes with a published prefix, and the password of a URL.
    /// </summary>
    public static string Prose(string? value, int limit = DefaultLimit) => Redact(value, limit, bareWordAssignments: false);

    private static string Redact(string? value, int limit, bool bareWordAssignments)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string masked;
        try
        {
            // A key block first: it spans lines, and the passes below would mask only its first word.
            masked = PrivateKeyBlock().Replace(value, Mask);
            masked = Authorization().Replace(masked, "$1" + Mask);
            masked = CookieHeader().Replace(masked, "$1" + Mask);

            // JSON members before assignments: the assignment pass would otherwise take the closing quote and brace of a value with it.
            masked = JsonMember().Replace(masked, "$1\"" + Mask + "\"");
            masked = (bareWordAssignments ? Assignment() : ProseAssignment()).Replace(masked, "$1" + Mask);
            masked = RunOnAssignment().Replace(masked, "$1" + Mask);

            // What a secret looks like wherever it stands, with no name in front of it to give it away.
            masked = WebhookUrl().Replace(masked, Mask);
            masked = PrefixedToken().Replace(masked, Mask);
            masked = UrlUserInfo().Replace(masked, "${prefix}" + Mask + "${at}");
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
