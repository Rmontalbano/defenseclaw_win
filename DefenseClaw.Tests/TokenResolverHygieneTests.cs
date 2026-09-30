using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// A token pasted into an environment variable or a config line carries stray whitespace, and now and
/// then worse. None of it may reach an HTTP header untouched: a space or newline in a header value is a
/// <see cref="FormatException"/> (which used to escape the client and freeze the monitor) or a 401.
/// </summary>
public class TokenResolverHygieneTests
{
    private const string VariableName = "DEFENSECLAW_GATEWAY_TOKEN";

    private static DefenseClawConfig ConfigWith(string? literalToken = null)
    {
        var config = new DefenseClawConfig();
        config.Gateway.Token = literalToken;
        return config;
    }

    private static TokenResolver Resolver(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, string>? dotEnv = null) =>
        new(
            () => dotEnv ?? new Dictionary<string, string>(),
            new DictionaryEnvironmentReader(environment ?? new Dictionary<string, string>()));

    [Theory]
    [InlineData("  padded-token  ")]
    [InlineData("token-with-newline\r\n")]
    [InlineData("\ttabbed-token\t")]
    public void Every_rung_is_trimmed(string raw)
    {
        var expected = raw.Trim();

        var fromEnvironment = Resolver(environment: new Dictionary<string, string> { [VariableName] = raw })
            .Resolve(ConfigWith());
        var fromDotEnv = Resolver(dotEnv: new Dictionary<string, string> { [VariableName] = raw })
            .Resolve(ConfigWith());
        var fromConfig = Resolver().Resolve(ConfigWith(literalToken: raw));

        Assert.Equal(expected, fromEnvironment.Token!.Reveal());
        Assert.Equal(expected, fromDotEnv.Token!.Reveal());
        Assert.Equal(expected, fromConfig.Token!.Reveal());
        Assert.Null(fromEnvironment.Note);
    }

    [Theory]
    [InlineData("tok\0en")]
    [InlineData("first\nsecond")]
    [InlineData("esc\u001bape")]
    public void A_control_character_inside_the_value_makes_the_rung_absent_and_is_noted(string raw)
    {
        var resolver = Resolver(
            environment: new Dictionary<string, string> { [VariableName] = raw },
            dotEnv: new Dictionary<string, string> { [VariableName] = "from-dotenv" });

        var resolution = resolver.Resolve(ConfigWith());

        // The walk continues past the bad rung, exactly as it does for a blank one.
        Assert.Equal(TokenSource.DotEnvFile, resolution.Source);
        Assert.Equal("from-dotenv", resolution.Token!.Reveal());
        Assert.Contains("process environment", resolution.Note, StringComparison.Ordinal);
        Assert.Contains("control character", resolution.Note, StringComparison.Ordinal);
        Assert.Contains(VariableName, resolution.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_good_rung_left_the_note_says_why_nothing_was_found()
    {
        var resolver = Resolver(
            environment: new Dictionary<string, string> { [VariableName] = "bad\0value" },
            dotEnv: new Dictionary<string, string> { [VariableName] = "also\nbad" });

        var resolution = resolver.Resolve(ConfigWith(literalToken: "third\u0007bad"));

        Assert.False(resolution.Found);
        Assert.Equal(TokenSource.None, resolution.Source);
        Assert.Contains("process environment", resolution.Note, StringComparison.Ordinal);
        Assert.Contains(".env", resolution.Note, StringComparison.Ordinal);
        Assert.Contains("gateway.token", resolution.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_and_ToString_never_carry_the_rejected_value()
    {
        var resolver = Resolver(environment: new Dictionary<string, string> { [VariableName] = "leaky-secret\0tail" });

        var resolution = resolver.Resolve(ConfigWith());

        Assert.DoesNotContain("leaky-secret", resolution.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("leaky-secret", resolution.ToString(), StringComparison.Ordinal);
        Assert.Contains("control character", resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_resolution_has_no_note()
    {
        var resolution = Resolver(environment: new Dictionary<string, string> { [VariableName] = "clean-token" })
            .Resolve(ConfigWith());

        Assert.Null(resolution.Note);
    }
}
