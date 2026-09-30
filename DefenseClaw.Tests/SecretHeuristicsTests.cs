using DefenseClaw.Core.Security;

namespace DefenseClaw.Tests;

/// <summary>
/// What counts as a secret in a form field, and — as important — what does not. Every "clean" row is something an
/// operator legitimately types into an MCP, plugin or wizard field; a false positive there is a warning nobody
/// believes any more. Every token-shaped literal below is assembled from two halves so that no source scanner
/// mistakes this file for a leak; none of them is a real credential.
/// </summary>
public class SecretHeuristicsTests
{
    private const string GitHubToken = "ghp" + "_0123456789abcdefghijklmnopqrstuvwxyz";
    private const string GitHubFineGrained = "github" + "_pat_11ABCDEFG0abcdefghij_abcdefghijklmnop";
    private const string SlackToken = "xox" + "b-123456789012-abcdefghijklmnop";
    private const string AwsKey = "AKIA" + "ABCDEFGHIJKLMNOP";
    private const string AwsSessionKey = "ASIA" + "ABCDEFGHIJKLMNOP";
    private const string ClassicSk = "sk" + "-abcdefghij1234567890abcdefgh";
    private const string ProjectSk = "sk" + "-proj-abcdEFGH1234abcdEFGH1234abcd";
    private const string AnthropicSk = "sk" + "-ant-api03-Abcdefghijklmnopqrstuv1234";
    private const string GoogleKey = "AIza" + "SyA1234567890abcdefghijklmnopqrstuv";
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0" + ".abcdefghijklmnop";
    private const string SlackWebhook = "https://hooks.slack" + ".com/services/T00000000/B00000000/abcdefghijklmnopqrstuvwx";

    // ------------------------------------------------------------------ secrets, and what they are called

    [Theory]
    [InlineData("API_KEY=abcd1234", "API_KEY")]
    [InlineData("GITHUB_TOKEN=not-a-known-shape-but-named", "GITHUB_TOKEN")]
    [InlineData("db_pass=hunter22", "db_pass")]
    [InlineData("PASSWORD=hunter2!", "PASSWORD")]
    [InlineData("clientSecret: abc123456", "clientSecret")]
    [InlineData("AWS_SECRET_ACCESS_KEY=abcdefgh1234", "AWS_SECRET_ACCESS_KEY")]
    [InlineData("--api-key=abcdef12", "api-key")]
    [InlineData("--password hunter22", "--password")]
    [InlineData("--token abcdef123456", "--token")]
    [InlineData("{\"password\": \"hunter22\"}", "password")]
    [InlineData("https://example.com/mcp?token=abcdef123456", "token")]
    [InlineData("https://example.com/x?a=1&api_key=abcd1234", "api_key")]
    [InlineData("https://example.com/x?key=abcdef123456", "key")]
    public void A_secret_named_setting_with_a_real_looking_value_is_a_secret(string text, string nameFragment)
    {
        var explanation = SecretHeuristics.Explain(text);

        Assert.NotNull(explanation);
        Assert.Contains(nameFragment, explanation, StringComparison.Ordinal);
        Assert.True(SecretHeuristics.LooksSecret(text));
    }

    [Theory]
    [InlineData(GitHubToken, "GitHub token")]
    [InlineData(GitHubFineGrained, "GitHub token")]
    [InlineData(SlackToken, "Slack token")]
    [InlineData(AwsKey, "AWS access key")]
    [InlineData(AwsSessionKey, "AWS access key")]
    [InlineData(ClassicSk, "API key")]
    [InlineData(ProjectSk, "API key")]
    [InlineData(AnthropicSk, "API key")]
    [InlineData(GoogleKey, "Google API key")]
    [InlineData(Jwt, "JSON Web Token")]
    [InlineData("glpat" + "-abcdefghij0123456789", "GitLab token")]
    [InlineData("hf" + "_abcdefghijklmnopqrstuvwxyz0123456789", "Hugging Face token")]
    [InlineData("Bearer abcdef123456", "bearer token")]
    [InlineData("Authorization: Bearer abcdef123456", "bearer token")]
    [InlineData("bearer " + Jwt, "bearer token")]
    [InlineData("-----BEGIN PRIVATE KEY-----", "private key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private key")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----", "private key")]
    [InlineData("https://user:hunter2@example.com/mcp", "password inside a URL")]
    [InlineData("git+https://ci:s3cr3tvalue@git.example.com/org/repo.git", "password inside a URL")]
    [InlineData(SlackWebhook, "Slack webhook")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789", "Discord webhook")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc-def@ghi/IncomingWebhook/abc/def", "Teams webhook")]
    [InlineData("https://webexapis.com/v1/webhooks/incoming/Y2lzY29zcGFyazovL3VzL1dFQkhPT0svYWJj", "Webex incoming webhook")]
    public void A_token_with_a_published_shape_is_recognised_wherever_it_sits(string secret, string kind)
    {
        foreach (var text in new[] { secret, $"--flag {secret}", $"prefix {secret} suffix", $"NOTE={secret}" })
        {
            var explanation = SecretHeuristics.Explain(text);

            Assert.NotNull(explanation);
            Assert.Contains(kind, explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_explanation_never_contains_the_value()
    {
        foreach (var text in new[] { GitHubToken, SlackWebhook, "API_KEY=abcd1234", "https://user:hunter2@example.com/", Jwt })
        {
            var explanation = SecretHeuristics.Explain(text);

            Assert.NotNull(explanation);
            foreach (var value in new[] { GitHubToken, "abcd1234", "hunter2", "abcdefghijklmnopqrstuvwx", "abcdefghijklmnop" })
            {
                Assert.DoesNotContain(value, explanation, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------------ not secrets

    [Theory]
    // settings that are not secret-named
    [InlineData("LOG_LEVEL=debug")]
    [InlineData("NODE_ENV=production")]
    [InlineData("PATH=C:\\Windows\\System32")]
    [InlineData("PORT=8080")]
    [InlineData("--port 8080")]
    [InlineData("--transport stdio")]
    [InlineData("KEYBOARD_LAYOUT=us-international")]
    [InlineData("MONKEY=banana-split")]
    [InlineData("AUTHOR=jane.smith@example.com")]
    [InlineData("PASSPORT=us-12345678")]
    [InlineData("TOKENIZER=sentencepiece-model")]
    [InlineData("PASS_THROUGH=allowlist-config")]
    [InlineData("MAX_TOKENS=100000")]
    [InlineData("AUTH_MODE=oauth-device")]
    [InlineData("--auth-mode oauth-device")]
    // secret-named, but the name points at a secret rather than being one
    [InlineData("API_KEY_ENV=MY_API_KEY")]
    [InlineData("--api-key-env MY_API_KEY")]
    [InlineData("--secret-env DEFENSECLAW_HOOK_SECRET")]
    [InlineData("TOKEN_FILE=C:\\secrets\\token.txt")]
    [InlineData("--token-file C:\\secrets\\token.txt")]
    [InlineData("SECRET_NAME=prod-signing-secret")]
    // secret-named, but the value is not a secret
    [InlineData("API_KEY=")]
    [InlineData("API_KEY=abc")]
    [InlineData("PASSWORD=true")]
    [InlineData("TOKEN=none")]
    [InlineData("API_KEY=$MY_KEY")]
    [InlineData("API_KEY=${MY_KEY}")]
    [InlineData("API_KEY=%MY_KEY%")]
    [InlineData("API_KEY=<your-key>")]
    [InlineData("API_KEY=xxxxxxxx")]
    [InlineData("API_KEY=********")]
    [InlineData("API_KEY=[REDACTED]")]
    [InlineData("API_KEY=op://vault/item/field")]
    [InlineData("API_KEY=env:MY_KEY")]
    [InlineData("SSH_KEY=C:\\Users\\me\\.ssh\\id_ed25519")]
    [InlineData("SSH_KEY=~/.ssh/id_ed25519")]
    // addresses, packages and prose
    [InlineData("https://mcp.deepwiki.com/mcp")]
    [InlineData("http://localhost:3000/mcp")]
    [InlineData("https://example.com/api?page=2&sort=name")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("ssh://git@host:22/repo.git")]
    [InlineData("http://host:8080/path@here")]
    [InlineData("C:\\work\\project")]
    [InlineData("@modelcontextprotocol/server-filesystem")]
    [InlineData("npx -y some-package@1.2.3")]
    [InlineData("-y")]
    [InlineData("key: value")]
    [InlineData("Bearer authentication")]
    [InlineData("the password field is required")]
    [InlineData("task-runner-abcdefghijklmnop123")]
    [InlineData("sk-plugin-with-a-long-name-v2")]
    [InlineData("sk-internationalization")]
    [InlineData("ghp_short")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ordinary_values_are_not_secrets(string text)
    {
        Assert.Null(SecretHeuristics.Explain(text));
        Assert.False(SecretHeuristics.LooksSecret(text));
    }

    [Fact]
    public void Null_is_not_a_secret()
    {
        Assert.Null(SecretHeuristics.Explain(null));
        Assert.False(SecretHeuristics.LooksSecret(null));
    }

    [Fact]
    public void A_pasted_file_is_scanned_only_up_to_a_cap_and_does_not_hang()
    {
        // A megabyte of near-misses: the scan must stay fast and must not throw.
        var text = string.Concat(Enumerable.Repeat("name-with-dashes:value ", 60_000));

        Assert.Null(SecretHeuristics.Explain(text));
    }

    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData("API_KEY")]
    [InlineData("apiKey")]
    [InlineData("APIKEY")]
    [InlineData("KEY")]
    [InlineData("key")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("accessToken")]
    [InlineData("SPLUNK_HEC_TOKEN")]
    [InlineData("db-password")]
    [InlineData("DB_PASS")]
    [InlineData("client_secret")]
    [InlineData("AUTH")]
    [InlineData("Authorization")]
    [InlineData("x-api-key")]
    [InlineData("private_key")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    public void Names_that_say_secret(string name) => Assert.True(SecretHeuristics.IsSecretName(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("LOG_LEVEL")]
    [InlineData("TOKEN_FILE")]
    [InlineData("API_KEY_ENV")]
    [InlineData("AUTH_MODE")]
    [InlineData("KEYBOARD")]
    [InlineData("PASS_THROUGH")]
    [InlineData("AUTHOR")]
    [InlineData("MAX_TOKENS")]
    [InlineData("SECRET_NAME")]
    [InlineData("TOKEN_URL")]
    [InlineData("ACCESS_KEY_ID")]
    [InlineData("")]
    [InlineData("  ")]
    public void Names_that_do_not(string name) => Assert.False(SecretHeuristics.IsSecretName(name));

    // ------------------------------------------------------------------ what the runner should be told about

    [Theory]
    [InlineData("DEFENSECLAW_GATEWAY_TOKEN", "abcdef0123456789")]
    [InlineData("SPLUNK_HEC_TOKEN", "a1b2c3d4-e5f6-7890")]
    [InlineData("OPENAI_API_KEY", "not-shaped-but-named-key")]
    [InlineData("SOMETHING_ELSE", ClassicSk)]
    [InlineData("HTTPS_PROXY", "http://user:pw123@proxy.example:8080")]
    public void A_secret_entry_is_worth_registering(string name, string value) =>
        Assert.True(SecretHeuristics.IsRegistrableEntry(name, value));

    [Theory]
    [InlineData("API_KEY", "short")]
    [InlineData("API_KEY", "true")]
    [InlineData("API_KEY", "$SOME_OTHER_VARIABLE")]
    [InlineData("API_KEY", "op://vault/item/field")]
    [InlineData("API_KEY", "********************")]
    [InlineData("API_KEY", "")]
    [InlineData("DEFENSECLAW_HOME", "C:\\Users\\me\\.defenseclaw")]
    [InlineData("LOG_LEVEL", "debug-verbose")]
    [InlineData("EDITOR", "notepad++")]
    [InlineData("SPLUNK_HEC_URL", "https://splunk.example:8088")]
    [InlineData("TOKEN_FILE", "C:\\secrets\\token.txt")]
    public void A_setting_that_is_not_a_secret_is_left_alone(string name, string value) =>
        Assert.False(SecretHeuristics.IsRegistrableEntry(name, value));

    [Fact]
    public void A_value_named_by_a_config_env_key_needs_only_to_be_long_and_not_a_placeholder()
    {
        Assert.True(SecretHeuristics.IsRegistrableValue("plain-value-123"));
        Assert.True(SecretHeuristics.IsRegistrableValue("  padded-value-123  "));
        Assert.False(SecretHeuristics.IsRegistrableValue("short"));
        Assert.False(SecretHeuristics.IsRegistrableValue("false"));
        Assert.False(SecretHeuristics.IsRegistrableValue("$NAMED_VARIABLE"));
        Assert.False(SecretHeuristics.IsRegistrableValue("C:\\somewhere\\else"));
        Assert.False(SecretHeuristics.IsRegistrableValue(null));
    }
}
