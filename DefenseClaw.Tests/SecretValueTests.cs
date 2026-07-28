using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

public class SecretValueTests
{
    private const string Token = "s3cr3t-gateway-token-abcdef0123456789";

    [Fact]
    public void ToString_never_reveals_the_value()
    {
        var secret = new SecretValue(Token);

        Assert.Equal(SecretValue.Redacted, secret.ToString());
        Assert.DoesNotContain(Token, secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void String_interpolation_redacts()
    {
        var secret = new SecretValue(Token);

        var message = $"connecting with {secret}";

        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
        Assert.Contains(SecretValue.Redacted, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_returns_the_raw_value()
    {
        Assert.Equal(Token, new SecretValue(Token).Reveal());
    }

    [Fact]
    public void Matches_compares_by_value()
    {
        var secret = new SecretValue(Token);

        Assert.True(secret.Matches(Token));
        Assert.False(secret.Matches(Token + "x"));
        Assert.False(secret.Matches(null));
    }

    [Fact]
    public void Scrub_removes_every_occurrence()
    {
        var secret = new SecretValue(Token);

        var scrubbed = secret.Scrub($"Authorization: Bearer {Token} (retry with {Token})");

        Assert.DoesNotContain(Token, scrubbed, StringComparison.Ordinal);
        Assert.Equal(2, scrubbed.Split(SecretValue.Redacted).Length - 1);
    }

    [Fact]
    public void AppearsIn_detects_embedded_secrets()
    {
        var secret = new SecretValue(Token);

        Assert.True(secret.AppearsIn($"--token={Token}"));
        Assert.False(secret.AppearsIn("--token=something-else"));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new SecretValue(Token), new SecretValue(Token));
        Assert.NotEqual(new SecretValue(Token), new SecretValue("other"));
    }

    [Fact]
    public void GetHashCode_does_not_encode_the_value()
    {
        // Two different secrets of equal length must hash alike: the hash carries no
        // information beyond length, so a hash dump cannot leak the token.
        Assert.Equal(new SecretValue("aaaaaaaa").GetHashCode(), new SecretValue("bbbbbbbb").GetHashCode());
    }
}
