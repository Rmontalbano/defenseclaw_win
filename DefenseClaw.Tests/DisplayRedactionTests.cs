using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Security;

namespace DefenseClaw.Tests;

/// <summary>Display redaction: what is masked, what is left alone, and that masking happens before the length limit.</summary>
public sealed class DisplayRedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: [redacted]")]
    [InlineData("authorization=Basic dXNlcjpwYXNz trailing", "authorization=[redacted]")]
    [InlineData("calling x with api_key=sk-12345 now", "calling x with api_key=[redacted] now")]
    [InlineData("api-key: sk-12345,next", "api-key: [redacted],next")]
    [InlineData("apikey sk-12345 done", "apikey [redacted] done")]
    [InlineData("access_token=AAA refresh_token=BBB", "access_token=[redacted] refresh_token=[redacted]")]
    [InlineData("token=abc123;rest", "token=[redacted];rest")]
    [InlineData("password = 'hunter two' ok", "password = [redacted] ok")]
    [InlineData("password: \"a b c\" ok", "password: [redacted] ok")]
    [InlineData("secret=xyz", "secret=[redacted]")]
    [InlineData("sent Bearer eyJhbGciOi to host", "sent Bearer [redacted] to host")]
    [InlineData("id_token=XYZ client_secret=QQQ", "id_token=[redacted] client_secret=[redacted]")]
    [InlineData("PASSWORD=Hunter2", "PASSWORD=[redacted]")]
    public void Credential_assignments_are_masked(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Theory]
    [InlineData("{\"token\":\"abc\",\"n\":1}", "{\"token\":\"[redacted]\",\"n\":1}")]
    [InlineData("{\"api_key\": \"sk-1\\\"2\", \"ok\": true}", "{\"api_key\": \"[redacted]\", \"ok\": true}")]
    [InlineData("{\"x.password\":  12345}", "{\"x.password\":  \"[redacted]\"}")]
    [InlineData("{\"authorization\":\"Bearer abc\"}", "{\"authorization\":\"[redacted]\"}")]
    [InlineData("{\"client_secret\":null}", "{\"client_secret\":\"[redacted]\"}")]
    public void Json_members_named_for_a_credential_lose_their_value(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Theory]
    [InlineData("{\"max_tokens\":4096,\"input_tokens\":12}")]
    [InlineData("tokenizer=gpt and the tokens=3 were counted")]
    [InlineData("a plain line with nothing to hide")]
    [InlineData("passwordless login enabled")]
    public void Text_that_only_mentions_the_word_is_left_alone(string input) =>
        Assert.Equal(input, DisplayRedaction.Text(input));

    // ---- shapes the first version missed (F8). Every value is synthetic; a token with a published prefix is put together at run
    // time, so no scanner reads a credential-shaped literal in this file. ----

    [Theory]
    [InlineData("private_key=abc123", "private_key=[redacted]")]
    [InlineData("private-key: abc123", "private-key: [redacted]")]
    [InlineData("PRIVATE_KEY=abc123 done", "PRIVATE_KEY=[redacted] done")]
    [InlineData("secretKey=abc123", "secretKey=[redacted]")]
    [InlineData("secret_key: abc123", "secret_key: [redacted]")]
    [InlineData("secret-key=abc123", "secret-key=[redacted]")]
    [InlineData("client_secret_value=abc123", "client_secret_value=[redacted]")]
    [InlineData("clientSecretBasic: abc123", "clientSecretBasic: [redacted]")]
    [InlineData("AWS_SECRET_ACCESS_KEY=abc/def+ghi== next", "AWS_SECRET_ACCESS_KEY=[redacted] next")]
    [InlineData("passwd=abc123", "passwd=[redacted]")]
    [InlineData("pwd=abc123", "pwd=[redacted]")]
    [InlineData("Server=db;Uid=app;Pwd=abc123;Database=x", "Server=db;Uid=app;Pwd=[redacted];Database=x")]
    [InlineData("passphrase: 'two words'", "passphrase: [redacted]")]
    [InlineData("mysecret=abc123", "mysecret=[redacted]")]
    [InlineData("appSecret: abc123", "appSecret: [redacted]")]
    [InlineData("clientsecret=abc123", "clientsecret=[redacted]")]
    [InlineData("csrftoken=abc123; path=/", "csrftoken=[redacted]; path=/")]
    [InlineData("dbpassword=abc123", "dbpassword=[redacted]")]
    [InlineData("x-amz-security-token: abc123", "x-amz-security-token: [redacted]")]
    public void More_credential_names_lose_their_value(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Theory]
    [InlineData("{\"private_key\": \"abc\"}", "{\"private_key\": \"[redacted]\"}")]
    [InlineData("{\"private-key\":\"abc\"}", "{\"private-key\":\"[redacted]\"}")]
    [InlineData("{\"secretKey\":\"abc\",\"n\":1}", "{\"secretKey\":\"[redacted]\",\"n\":1}")]
    [InlineData("{\"secret_key\":\"abc\"}", "{\"secret_key\":\"[redacted]\"}")]
    [InlineData("{\"client_secret_value\": \"abc\"}", "{\"client_secret_value\": \"[redacted]\"}")]
    [InlineData("{\"AWS_SECRET_ACCESS_KEY\":\"abc\"}", "{\"AWS_SECRET_ACCESS_KEY\":\"[redacted]\"}")]
    [InlineData("{\"passwd\":\"abc\",\"pwd\":\"abc\"}", "{\"passwd\":\"[redacted]\",\"pwd\":\"[redacted]\"}")]
    [InlineData("{\"Cookie\": \"a=1; b=2\"}", "{\"Cookie\": \"[redacted]\"}")]
    [InlineData("{\"set-cookie\":\"sid=abc\"}", "{\"set-cookie\":\"[redacted]\"}")]
    public void More_json_members_named_for_a_credential_lose_their_value(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Fact]
    public void A_private_key_in_a_json_string_is_masked_whole()
    {
        var key = "-----BEGIN " + "PRIVATE KEY-----\\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\\n-----END " + "PRIVATE KEY-----\\n";

        var shown = DisplayRedaction.Text("{\"type\":\"service_account\",\"private_key\":\"" + key + "\",\"x\":1}");

        Assert.Equal("{\"type\":\"service_account\",\"private_key\":\"[redacted]\",\"x\":1}", shown);
    }

    [Theory]
    [InlineData("Cookie: a=1; b=2", "Cookie: [redacted]")]
    [InlineData("cookie:a=1", "cookie:[redacted]")]
    [InlineData("Set-Cookie: sid=abc123; Path=/; HttpOnly", "Set-Cookie: [redacted]")]
    [InlineData("GET /x\r\nCookie: a=1\r\nHost: h", "GET /x\r\nCookie: [redacted]\r\nHost: h")]
    [InlineData("x-cookie: abc", "x-cookie: [redacted]")]
    public void A_cookie_header_loses_everything_to_the_end_of_its_line(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Fact]
    public void A_pem_private_key_is_masked_from_its_header_to_its_closing_line()
    {
        var key = "-----BEGIN " + "RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0synthetic\nbodybodybody\n-----END " + "RSA PRIVATE KEY-----";

        Assert.Equal("loaded [redacted] ok", DisplayRedaction.Text("loaded " + key + " ok"));
        Assert.Equal("a\n[redacted]\nb", DisplayRedaction.Text("a\n" + key + "\nb"));
        Assert.Equal("[redacted]", DisplayRedaction.Text("-----BEGIN " + "OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END " + "OPENSSH PRIVATE KEY-----"));
    }

    [Fact]
    public void A_pem_private_key_cut_short_is_masked_to_the_end_of_the_text()
    {
        // A log line is cut at a limit: the header is there, the closing line is not.
        var shown = DisplayRedaction.Text("key: -----BEGIN " + "PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nmore body");

        Assert.Equal("key: [redacted]", shown);
    }

    private static readonly string GitHubClassic = "gh" + "p_" + new string('a', 36);
    private static readonly string GitHubFineGrained = "github" + "_pat_" + new string('b', 30);
    private static readonly string OpenAiKey = "sk" + "-" + new string('c', 40) + "9";
    private static readonly string OpenAiProjectKey = "sk" + "-proj-" + new string('d', 30) + "7";
    private static readonly string SlackBotToken = "xo" + "xb-" + "1234567890-abcdefghij";
    private static readonly string AwsKeyId = "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    private static readonly string Jwt = "ey" + "J" + "hbGciOiJIUzI1NiJ9" + "." + "ey" + "J" + "zdWIiOiJ4In0xxxx" + "." + "c2lnbmF0dXJlLXN5bnRoZXRpYw";
    private static readonly string GitLabPat = "gl" + "pat-" + new string('e', 24);
    private static readonly string NpmToken = "np" + "m_" + new string('f', 36);
    private static readonly string HuggingFace = "h" + "f_" + new string('g', 34);
    private static readonly string StripeLive = "sk" + "_live_" + new string('h', 20);
    private static readonly string GoogleKey = "AI" + "za" + new string('i', 35);

    public static TheoryData<string> BareTokens() => new()
    {
        GitHubClassic, GitHubFineGrained, OpenAiKey, OpenAiProjectKey, SlackBotToken, AwsKeyId, Jwt, GitLabPat, NpmToken, HuggingFace, StripeLive, GoogleKey,
    };

    [Theory]
    [MemberData(nameof(BareTokens))]
    public void A_token_with_a_published_prefix_is_masked_wherever_it_stands(string token)
    {
        // The same shape SecretHeuristics recognises on a command line: the two share their definitions (SecretShapes).
        Assert.NotNull(SecretHeuristics.Explain(token));
        Assert.Equal("clone failed for [redacted] (403)", DisplayRedaction.Text($"clone failed for {token} (403)"));
        Assert.Equal("[redacted]", DisplayRedaction.Text(token));
        Assert.Equal("x=[redacted],y", DisplayRedaction.Text($"x={token},y"));
        Assert.DoesNotContain(token, DisplayRedaction.Text($"{{\"detail\":\"bad credential {token}\"}}"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_that_crosses_the_limit_is_masked_before_the_cut()
    {
        var shown = DisplayRedaction.Text(new string('x', 90) + " " + GitHubClassic, limit: 105);

        Assert.DoesNotContain("aaaa", shown, StringComparison.Ordinal);
        Assert.Equal(new string('x', 90) + " [redacted]", shown);
    }

    [Theory]
    [InlineData("postgres://app:hunter2@db.internal:5432/orders", "postgres://app:[redacted]@db.internal:5432/orders")]
    [InlineData("https://user:pa55w0rd@example.com/path?x=1", "https://user:[redacted]@example.com/path?x=1")]
    [InlineData("connecting to amqp://guest:guest@mq:5672 now", "connecting to amqp://guest:[redacted]@mq:5672 now")]
    [InlineData("git+https://ci:tok3n@host.example/org/repo.git", "git+https://ci:[redacted]@host.example/org/repo.git")]
    public void The_password_in_a_url_is_masked_and_the_rest_of_the_url_kept(string input, string expected) =>
        Assert.Equal(expected, DisplayRedaction.Text(input));

    [Theory]
    [InlineData("ssh://git@host:22/x")]
    [InlineData("http://host:8080/p@q")]
    [InlineData("https://example.com/a:b@c")]
    [InlineData("see https://example.com:8443/docs")]
    public void A_url_with_no_password_in_it_is_left_alone(string input) =>
        Assert.Equal(input, DisplayRedaction.Text(input));

    public static TheoryData<string> Webhooks() => new()
    {
        "https://hooks." + "slack.com/services/T00000000/B00000000/" + new string('X', 24),
        "https://discord" + ".com/api/webhooks/123456789012345678/" + new string('y', 40),
        "https://contoso." + "webhook.office.com/webhookb2/11111111-2222-3333-4444-555555555555@66666666-7777-8888-9999-000000000000/IncomingWebhook/" + new string('z', 32) + "/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "https://webexapis" + ".com/v1/webhooks/incoming/" + new string('w', 40),
        "https://hooks." + "zapier.com/hooks/catch/123456/abcdef",
    };

    [Theory]
    [MemberData(nameof(Webhooks))]
    public void A_chat_webhook_url_is_masked_whole(string url)
    {
        Assert.NotNull(SecretHeuristics.Explain(url));
        Assert.Equal("posting to [redacted] failed", DisplayRedaction.Text($"posting to {url} failed"));
        Assert.Equal("webhook=[redacted]", DisplayRedaction.Text($"webhook={url}"));
    }

    [Fact]
    public void A_webhook_with_a_query_string_loses_both_the_path_and_the_token()
    {
        var url = "https://hooks." + "slack.com/services/T00000000/B00000000/" + new string('X', 24);

        Assert.Equal("[redacted]?token=[redacted]", DisplayRedaction.Text(url + "?token=abc"));
    }

    [Fact]
    public void Masking_is_idempotent_and_a_second_pass_changes_nothing()
    {
        var text = $"Authorization: Bearer abc; Cookie: a=1\n{GitHubClassic} postgres://app:hunter2@db/x mysecret=abc {{\"private_key\":\"abc\"}} pwd=abc " +
                   "https://hooks." + "slack.com/services/T00000000/B00000000/" + new string('X', 24);

        var once = DisplayRedaction.Text(text);

        Assert.Equal(once, DisplayRedaction.Text(once));
        Assert.DoesNotContain("hunter2", once, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubClassic, once, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sort_key=abc and key=def and primary_key=1")]
    [InlineData("ssh_key_file=C:\\Users\\a\\id and token_count=12 and secret_len=8")]
    [InlineData("the secrets directory and a tokenizer, passport=ABC123, cookies: 3 items, cookie jar")]
    [InlineData("pwdump is a tool; the cwd=C:\\x and OLDPWD=/home/a")]
    [InlineData("monkey=banana, whiskey=neat, keyboard=us, skyline-view, sk-learn, risk-based, task-runner-ab12")]
    [InlineData("see https://example.com/docs and ghp is short, gh_not_a_token, github_pattern=x")]
    public void Ordinary_text_that_only_sounds_like_a_secret_is_left_alone(string input) =>
        Assert.Equal(input, DisplayRedaction.Text(input));

    [Fact]
    public void A_megabyte_of_hostile_text_of_each_new_shape_is_masked_or_refused_in_bounded_time()
    {
        var inputs = new[]
        {
            "\"" + string.Concat(Enumerable.Repeat("client_secret", 80_000)),
            "-----BEGIN " + string.Concat(Enumerable.Repeat("-----BEGIN ", 90_000)),
            string.Concat(Enumerable.Repeat("sk-", 350_000)),
            "ghp_" + new string('a', 1_000_000),
            string.Concat(Enumerable.Repeat("a:b@", 250_000)),
            "scheme://" + string.Concat(Enumerable.Repeat("u:", 500_000)),
            string.Concat(Enumerable.Repeat("secret=\"", 130_000)),
            string.Concat(Enumerable.Repeat("cookie:", 150_000)),
            string.Concat(Enumerable.Repeat("https://hooks.", 70_000)),
        };

        foreach (var input in inputs)
        {
            var started = DateTime.UtcNow;
            _ = DisplayRedaction.Text(input);

            // Each pass has a two second timeout and a timeout masks the whole text, so nine passes and a generous margin for a slow machine.
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), $"{input.Length} characters starting {input[..Math.Min(20, input.Length)]}");
        }
    }

    [Fact]
    public void A_credential_that_crosses_the_limit_is_masked_before_the_cut()
    {
        // 90 characters of filler, then a secret that starts before the 100-character limit and ends after it.
        var input = new string('x', 90) + " password=SUPERSECRETVALUE";

        var shown = DisplayRedaction.Text(input, limit: 105);

        Assert.DoesNotContain("SUPER", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", shown, StringComparison.Ordinal);
        Assert.True(shown.Length <= 105);
        Assert.Contains("password=[re", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_never_ends_on_half_a_surrogate_pair()
    {
        var shown = DisplayRedaction.Text("ab😀cd", limit: 3);

        Assert.Equal("ab", shown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_is_nothing_out(string? input) => Assert.Equal(string.Empty, DisplayRedaction.Text(input));

    [Fact]
    public void A_limit_of_zero_shows_nothing_and_the_default_is_4096()
    {
        Assert.Equal(string.Empty, DisplayRedaction.Text("anything", limit: 0));
        Assert.Equal(DisplayRedaction.DefaultLimit, DisplayRedaction.Text(new string('a', 10_000)).Length);
    }

    [Fact]
    public void A_long_run_of_quote_like_characters_is_handled_in_bounded_time()
    {
        var hostile = "token=" + new string('"', 200_000) + new string('\\', 200_000);

        var started = DateTime.UtcNow;
        _ = DisplayRedaction.Text(hostile);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }
}
