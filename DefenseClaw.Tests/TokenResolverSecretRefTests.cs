using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// An OpenClaw secret reference saved as the gateway token: the 1.0.5/1.0.6 gateway uses that text as its bearer, 1.0.7+ skips it.
/// The resolver follows the runtime it is told about and defaults to the old behaviour.
/// </summary>
public class TokenResolverSecretRefTests
{
    private const string Reference = "{\"source\": \"env\", \"id\": \"SOME_VAR\"}";

    private static TokenResolver NewResolver() =>
        new(
            () => new Dictionary<string, string>(),
            new DictionaryEnvironmentReader(new Dictionary<string, string>()));

    private static TokenResolution Resolve(bool? skip)
    {
        var config = new DefenseClawConfig();
        config.Gateway.Token = Reference;
        var resolver = NewResolver();
        if (skip is { } value)
        {
            resolver.SkipSecretReferences = () => value;
        }

        return resolver.Resolve(config);
    }

    [Fact]
    public void No_information_uses_the_literal_text_and_adds_no_note()
    {
        var result = Resolve(skip: null);

        Assert.True(result.Found);
        Assert.Equal(TokenSource.ConfigLiteral, result.Source);
        Assert.Null(result.Note);
    }

    [Fact]
    public void A_runtime_before_1_0_7_uses_the_literal_text_and_adds_no_note()
    {
        var result = Resolve(skip: false);

        Assert.True(result.Found);
        Assert.Null(result.Note);
    }

    [Fact]
    public void A_runtime_with_the_skip_ignores_it_and_says_so_without_echoing_it()
    {
        var result = Resolve(skip: true);

        Assert.False(result.Found);
        Assert.Contains("defenseclaw doctor --fix", result.Note);
        Assert.DoesNotContain("SOME_VAR", result.Note);
    }

    [Fact]
    public void A_value_that_times_the_pattern_out_is_not_a_reference_and_does_not_throw()
    {
        var hostile = "{" + string.Concat(Enumerable.Repeat("\"source\":", 20000));

        Assert.False(TokenResolver.IsSecretRefText(hostile));

        var config = new DefenseClawConfig();
        config.Gateway.Token = hostile;
        var resolver = NewResolver();
        resolver.SkipSecretReferences = () => true;

        Assert.True(resolver.Resolve(config).Found);
    }
}
