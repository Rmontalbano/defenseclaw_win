using DefenseClaw.Core.Logs;

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
