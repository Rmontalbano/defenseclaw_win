using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Security;

/// <summary>
/// Recognises text that is, or carries, a secret: the check behind "this value is about to go on a command
/// line" warnings and behind the choice of which <c>.env</c> values the CLI runner must refuse on argv.
/// <para>
/// A command line is visible to every process on the machine, is recorded in the Activity list and the
/// exported log, and is echoed by <c>Copy command</c>. The runner can only refuse or mask a secret it
/// <i>knows</i> (<c>CliRunner.RegisterSecret</c>); a value an operator types into a form is not known to it.
/// This class is the app's guess at what such a value is, made from its shape alone. It is deliberately
/// asymmetric with the runner's guard: a wrong guess here is a paragraph of warning text, while a wrong guess
/// there refuses a command or garbles its output, so <see cref="IsRegistrableEntry"/> is stricter than
/// <see cref="Explain"/>.
/// </para>
/// <para>
/// What counts, in the order it is checked:
/// <list type="bullet">
/// <item><description>a private-key block (<c>-----BEGIN … PRIVATE KEY-----</c>);</description></item>
/// <item><description>a chat webhook URL whose path is the credential (Slack, Discord, Teams, Webex, Zapier);</description></item>
/// <item><description>a token with a published prefix: <c>Bearer …</c>, <c>sk-…</c>, <c>ghp_/gho_/ghu_/ghs_/ghr_</c>,
/// <c>github_pat_</c>, <c>xox[abprse]-</c>, <c>AKIA…/ASIA…</c>, a JWT, a Google API key and a few more;</description></item>
/// <item><description><c>user:password@</c> inside a URL;</description></item>
/// <item><description><c>NAME=value</c> (or <c>NAME: value</c>, <c>--name value</c>, <c>?name=value</c>) where the
/// <i>name</i> says secret — TOKEN, SECRET, PASSWORD, API_KEY, AUTH, CREDENTIAL … — and the value is not a
/// reference (<c>$VAR</c>, <c>%VAR%</c>, <c>op://…</c>), a placeholder (<c>xxxx</c>, <c>&lt;your key&gt;</c>), a
/// plain word (<c>true</c>, <c>oauth</c>) or a file location.</description></item>
/// </list>
/// A name that ends in <c>_ENV</c>, <c>_FILE</c>, <c>_PATH</c>, <c>_URL</c>, <c>_ID</c>, <c>_MODE</c> … points at a secret
/// or describes one and is not itself one, so <c>API_KEY_ENV=MY_KEY</c> and <c>TOKEN_FILE=C:\t.txt</c> are clean.
/// </para>
/// <para>Nothing here returns, logs or throws a value: <see cref="Explain"/> names the kind of secret and, for the
/// name rule, the variable name the operator typed — never the value.</para>
/// </summary>
public static partial class SecretHeuristics
{
    /// <summary>The shortest value the name rule treats as a secret: below this it is a flag, a mode or a count.</summary>
    public const int MinimumNamedValueLength = 6;

    /// <summary>
    /// The shortest <c>.env</c> value <see cref="IsRegistrableEntry"/> accepts. The runner scrubs a registered
    /// secret from output wherever it occurs, with no length floor of its own, so a short value would garble
    /// every line that happens to contain it.
    /// </summary>
    public const int MinimumRegistrableLength = 8;

    /// <summary>Text longer than this is checked only up to here: a form field is never this long, a pasted file might be.</summary>
    private const int MaximumScannedLength = 64 * 1024;

    /// <summary>Whole words in a name that mark it as a secret wherever they sit (<c>SPLUNK_HEC_TOKEN</c>, <c>clientSecret</c>).</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "PWD", "CREDENTIAL", "CREDENTIALS", "CREDS",
        "APIKEY", "ACCESSKEY", "SECRETKEY", "PRIVATEKEY", "BEARER", "AUTHORIZATION",
    };

    /// <summary>
    /// Words that mark a name as a secret only as its last word: <c>API_KEY</c> and <c>DB_PASS</c> are, while
    /// <c>KEY_FILE</c>, <c>PASS_THROUGH</c> and <c>AUTH_MODE</c> are not.
    /// </summary>
    private static readonly HashSet<string> SecretFinalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "KEY", "PASS", "AUTH",
    };

    /// <summary>
    /// A last word that turns a secret-sounding name into a description of one — where it lives, what it is
    /// called, how long it lasts — rather than the secret itself.
    /// </summary>
    private static readonly HashSet<string> NonSecretFinalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENV", "FILE", "PATH", "DIR", "DIRECTORY", "FOLDER", "NAME", "URL", "URI", "HOST", "PORT", "ID", "IDS",
        "MODE", "TYPE", "FORMAT", "VERSION", "PREFIX", "SUFFIX", "LENGTH", "LEN", "SIZE", "TTL", "EXPIRY",
        "COUNT", "LIMIT", "ENABLED", "DISABLED", "COMMAND", "CMD", "HELPER", "PROVIDER", "PROFILE", "SCOPE",
        "ALGORITHM", "ALG", "METHOD", "HEADER",
    };

    /// <summary>Whole values that are words, not secrets, even under a secret-sounding name.</summary>
    private static readonly HashSet<string> PlainWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "none", "null", "nil", "undefined", "default", "basic", "bearer",
        "oauth", "oauth2", "required", "optional", "enabled", "disabled", "redacted", "placeholder", "changeme",
    };

    /// <summary>Prefixes of a value that <i>refers to</i> a secret instead of being one.</summary>
    private static readonly string[] ReferencePrefixes =
    {
        "$", "%", "{{", "op://", "env:", "vault:", "keyring:", "secretref:", "secret://", "ssm:", "kv:",
    };

    /// <summary>A chat webhook: the URL <i>is</i> the credential, so anyone who sees it can post as the operator.</summary>
    private static readonly (Regex Pattern, string Description)[] WebhookPatterns =
    {
        (SlackWebhookPattern(), "a Slack webhook URL"),
        (DiscordWebhookPattern(), "a Discord webhook URL"),
        (TeamsWebhookPattern(), "a Microsoft Teams webhook URL"),
        (WebexWebhookPattern(), "a Webex incoming webhook URL"),
        (ZapierWebhookPattern(), "a Zapier webhook URL"),
    };

    /// <summary>Tokens with a published prefix, each with what to call it.</summary>
    private static readonly (Regex Pattern, string Description)[] TokenPatterns =
    {
        (OpenAiStylePattern(), "an API key (sk-…)"),
        (GitHubTokenPattern(), "a GitHub token"),
        (GitHubFineGrainedPattern(), "a GitHub token"),
        (SlackTokenPattern(), "a Slack token"),
        (AwsAccessKeyPattern(), "an AWS access key ID"),
        (GoogleApiKeyPattern(), "a Google API key"),
        (JwtPattern(), "a JSON Web Token"),
        (GitLabTokenPattern(), "a GitLab token"),
        (NpmTokenPattern(), "an npm token"),
        (HuggingFacePattern(), "a Hugging Face token"),
        (StripeKeyPattern(), "a Stripe secret key"),
    };

    /// <summary>True when <paramref name="text"/> is, or carries, something that looks like a secret. See <see cref="Explain"/>.</summary>
    public static bool LooksSecret(string? text) => Explain(text) is not null;

    /// <summary>
    /// What <paramref name="text"/> looks like, as a noun phrase a sentence can use ("a Slack webhook URL",
    /// "a value for “API_KEY”"), or <c>null</c> when it looks like nothing sensitive. The first rule that
    /// matches decides; the value itself is never part of the answer.
    /// </summary>
    public static string? Explain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > MaximumScannedLength)
        {
            text = text[..MaximumScannedLength];
        }

        if (PrivateKeyPattern().IsMatch(text))
        {
            return "a private key";
        }

        foreach (var (pattern, description) in WebhookPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return description;
            }
        }

        if (BearerTokenIn(text))
        {
            return "a bearer token";
        }

        foreach (var (pattern, description) in TokenPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return description;
            }
        }

        if (UrlUserInfoPattern().IsMatch(text))
        {
            return "a password inside a URL (user:password@)";
        }

        foreach (Match match in NamedValuePattern().Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (IsSecretName(name) && IsSecretLookingValue(match.Groups["value"].Value))
            {
                return $"a value for “{Truncate(name)}”";
            }
        }

        foreach (Match match in SpacedFlagPattern().Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (IsSecretName(name) && IsSecretLookingValue(match.Groups["value"].Value))
            {
                return $"a value for “--{Truncate(name)}”";
            }
        }

        return null;
    }

    /// <summary>
    /// True when a variable, key or option called <paramref name="name"/> would hold a secret: its words include
    /// TOKEN, SECRET, PASSWORD … or end in KEY, PASS or AUTH — and it does not end in a word such as ENV, FILE,
    /// PATH or URL that makes it a pointer to, or a description of, a secret. Case, separators (<c>_ - .</c>) and
    /// camelCase are all read as word breaks.
    /// </summary>
    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var words = WordPattern().Matches(name).Select(m => m.Value).ToArray();
        if (words.Length == 0)
        {
            return false;
        }

        if (NonSecretFinalWords.Contains(words[^1]))
        {
            return false;
        }

        return words.Any(SecretWords.Contains) || SecretFinalWords.Contains(words[^1]);
    }

    /// <summary>
    /// True when a <c>.env</c> entry (or the variable a config <c>*_env</c> key names) is a secret worth
    /// registering with the CLI runner so it is refused on argv and scrubbed from output: a value of at least
    /// <see cref="MinimumRegistrableLength"/> characters that is not a reference, placeholder, plain word or file
    /// location, and that either sits under a secret name or has a secret's shape.
    /// </summary>
    public static bool IsRegistrableEntry(string? name, string? value)
    {
        if (!IsRegistrableValue(value))
        {
            return false;
        }

        return IsSecretName(name) || Explain(value) is not null;
    }

    /// <summary>
    /// The value half of <see cref="IsRegistrableEntry"/>, for a variable that is known to hold a secret because a
    /// config <c>*_env</c> key names it: long enough, and not a reference, placeholder, plain word or location.
    /// </summary>
    public static bool IsRegistrableValue(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length >= MinimumRegistrableLength && !IsNotASecret(trimmed);
    }

    /// <summary>The value half of the name rule: not empty or short, and not a reference, placeholder, word or location.</summary>
    private static bool IsSecretLookingValue(string? rawValue)
    {
        var value = Unquote(rawValue);
        return value.Length >= MinimumNamedValueLength && !IsNotASecret(value);
    }

    /// <summary>References, placeholders, plain words and file locations: what a secret-named setting holds when it holds no secret.</summary>
    private static bool IsNotASecret(string value)
    {
        if (PlainWords.Contains(value))
        {
            return true;
        }

        foreach (var prefix in ReferencePrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // <your-key>, [REDACTED], ***, xxxx, ....
        if ((value.StartsWith('<') && value.EndsWith('>')) ||
            value.Contains("REDACTED", StringComparison.OrdinalIgnoreCase) ||
            PlaceholderPattern().IsMatch(value))
        {
            return true;
        }

        return LooksLikeLocation(value);
    }

    /// <summary>A drive path, UNC path, relative or home path, or file: URL — where a secret is kept, not the secret.</summary>
    private static bool LooksLikeLocation(string value) =>
        value.StartsWith("./", StringComparison.Ordinal) ||
        value.StartsWith(".\\", StringComparison.Ordinal) ||
        value.StartsWith("../", StringComparison.Ordinal) ||
        value.StartsWith("..\\", StringComparison.Ordinal) ||
        value.StartsWith("~/", StringComparison.Ordinal) ||
        value.StartsWith("~\\", StringComparison.Ordinal) ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        DriveLocationPattern().IsMatch(value);

    private static string Unquote(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length >= 2 &&
               ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private static string Truncate(string name) => name.Length <= 40 ? name : name[..40] + "…";

    /// <summary>
    /// <c>Bearer &lt;token&gt;</c>: prose such as "Bearer authentication" is not a token, so the word after it must
    /// hold a digit or be long.
    /// </summary>
    private static bool BearerTokenIn(string text)
    {
        foreach (Match match in BearerPattern().Matches(text))
        {
            var token = match.Groups["token"].Value;
            if (token.Length >= 20 || token.Any(char.IsAsciiDigit))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"https?://hooks\.slack(?:-gov)?\.com/(?:services|workflows|triggers)/[A-Za-z0-9/_-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SlackWebhookPattern();

    [GeneratedRegex(@"https?://(?:[a-z]+\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d+/[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiscordWebhookPattern();

    [GeneratedRegex(@"https?://(?:[a-z0-9-]+\.webhook\.office\.com/webhook\w*|outlook\.office(?:365)?\.com/webhook\w*)/[A-Za-z0-9@/_.-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TeamsWebhookPattern();

    [GeneratedRegex(@"https?://webexapis\.com/v1/webhooks/incoming/[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WebexWebhookPattern();

    [GeneratedRegex(@"https?://hooks\.zapier\.com/hooks/catch/[A-Za-z0-9/_-]{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ZapierWebhookPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])bearer\s+(?<token>[A-Za-z0-9._~+/=-]{8,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    // sk-<20+ unbroken characters> (the classic OpenAI shape), or sk-<vendor>-<anything> for the newer keys
    // (sk-proj-…, sk-ant-api03-…, sk-svcacct-…, sk-or-v1-…), with a digit somewhere after the prefix. A slug such as
    // "sk-plugin-with-a-long-name" has no 20-character unbroken run and no vendor word, and a real key is never all letters.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])sk-(?=[A-Za-z0-9_-]*[0-9])(?:[A-Za-z0-9_]{20,}|(?:proj|ant|svcacct|admin|or)-[A-Za-z0-9_-]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStylePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])github_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubFineGrainedPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:xox[abprse]-[A-Za-z0-9-]{10,}|xapp-\d-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex SlackTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{35}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKeyPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])glpat-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitLabTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])npm_[A-Za-z0-9]{36}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NpmTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])hf_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex HuggingFacePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[sr]k_live_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex StripeKeyPattern();

    // scheme://user:password@host — a userinfo with a colon and a non-empty password before the first slash.
    // ssh://git@host:22/x has a user and no password, and http://host:8080/p@q has a slash first: neither matches.
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();

    // name=value or name: value, anywhere in the text (a KEY=VAL line, ?token=abc&x=1, --api-key=abc, "password": "abc").
    // The value sits in a lookahead so the next match may start inside it: a URL's own query string is found
    // even though the "https:" in front of it looked like a name.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z][A-Za-z0-9_.-]{0,63})[""']?\s*[=:]\s*(?=(?<value>""[^""]*""|'[^']*'|[^\s&#;,""'<>]+))", RegexOptions.CultureInvariant)]
    private static partial Regex NamedValuePattern();

    // --name value: only the option's own words decide (see IsSecretName), so --auth-mode oauth and --token-file x stay clean.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])--?(?<name>[A-Za-z][A-Za-z0-9_-]*)[ \t]+(?<value>[^\s""'-][^\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedFlagPattern();

    // The words of a name: API_KEY -> API, KEY; apiKey -> api, Key; APIKey -> API, Key; client-secret -> client, secret.
    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^(?:[*•xX#._-]{3,}|\.\.\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLocationPattern();
}
