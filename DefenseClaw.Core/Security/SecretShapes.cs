namespace DefenseClaw.Core.Security;

/// <summary>
/// The shapes of the secrets the app recognises by what they look like, written once as regular-expression sources.
/// <see cref="SecretHeuristics"/> asks "is this, or does it carry, a secret?" of what an operator types, and
/// <see cref="Logs.DisplayRedaction"/> masks the same shapes in what the app displays: each builds its own
/// <c>[GeneratedRegex]</c> from these constants (a different timeout, a different job), so the two cannot drift apart on what a GitHub
/// token or a Slack webhook looks like.
/// </summary>
internal static class SecretShapes
{
    /// <summary>The opening line of a PEM private key (<c>-----BEGIN RSA PRIVATE KEY-----</c>, OPENSSH, PGP …).</summary>
    public const string PrivateKeyHeader = @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----";

    /// <summary>A whole PEM private key: the header, the body, and the closing line when there is one (a truncated log line has none).</summary>
    public const string PrivateKeyBlock = PrivateKeyHeader + @"[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----|\z)";

    // Chat webhooks: the URL is the credential, so anyone who sees it can post as the operator.
    public const string SlackWebhook = @"https?://hooks\.slack(?:-gov)?\.com/(?:services|workflows|triggers)/[A-Za-z0-9/_-]{8,}";

    public const string DiscordWebhook = @"https?://(?:[a-z]+\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d+/[A-Za-z0-9_-]{16,}";

    public const string TeamsWebhook = @"https?://(?:[a-z0-9-]+\.webhook\.office\.com/webhook\w*|outlook\.office(?:365)?\.com/webhook\w*)/[A-Za-z0-9@/_.-]{8,}";

    public const string WebexWebhook = @"https?://webexapis\.com/v1/webhooks/incoming/[A-Za-z0-9_-]{8,}";

    public const string ZapierWebhook = @"https?://hooks\.zapier\.com/hooks/catch/[A-Za-z0-9/_-]{6,}";

    // sk-<20+ unbroken characters> (the classic OpenAI shape), or sk-<vendor>-<anything> for the newer keys
    // (sk-proj-…, sk-ant-api03-…, sk-svcacct-…, sk-or-v1-…), with a digit somewhere after the prefix. A slug such as
    // "sk-plugin-with-a-long-name" has no 20-character unbroken run and no vendor word, and a real key is never all letters.
    public const string OpenAiStyleKey = @"(?<![A-Za-z0-9_-])sk-(?=[A-Za-z0-9_-]*[0-9])(?:[A-Za-z0-9_]{20,}|(?:proj|ant|svcacct|admin|or)-[A-Za-z0-9_-]{16,})";

    public const string GitHubToken = @"(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9]{30,}";

    public const string GitHubFineGrainedToken = @"(?<![A-Za-z0-9_])github_pat_[A-Za-z0-9_]{20,}";

    public const string SlackToken = @"(?<![A-Za-z0-9])(?:xox[abprse]-[A-Za-z0-9-]{10,}|xapp-\d-[A-Za-z0-9-]{10,})";

    public const string AwsAccessKeyId = @"(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])";

    public const string GoogleApiKey = @"(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{35}(?![A-Za-z0-9_-])";

    public const string JsonWebToken = @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}";

    public const string GitLabToken = @"(?<![A-Za-z0-9_-])glpat-[A-Za-z0-9_-]{20,}";

    public const string NpmToken = @"(?<![A-Za-z0-9_])npm_[A-Za-z0-9]{36}(?![A-Za-z0-9])";

    public const string HuggingFaceToken = @"(?<![A-Za-z0-9_])hf_[A-Za-z0-9]{30,}";

    public const string StripeKey = @"(?<![A-Za-z0-9_])[sr]k_live_[A-Za-z0-9]{16,}";

    // scheme://user:password@host - a userinfo with a colon and a non-empty password before the first slash.
    // ssh://git@host:22/x has a user and no password, and http://host:8080/p@q has a slash first: neither matches.
    // The groups let a masker keep the scheme and user and drop only the password; a plain IsMatch ignores them.
    // The scheme starts where a run of scheme characters starts (the lookbehind): without it every letter of a long
    // "sk-sk-sk-..." run would begin its own scan to the end of the run, which is quadratic on a megabyte of text.
    public const string UrlUserInfo = @"(?<![A-Za-z0-9+.-])(?<prefix>[A-Za-z][A-Za-z0-9+.-]*://[^/\s:@]+:)(?<password>[^/\s@]+)(?<at>@)";
}
