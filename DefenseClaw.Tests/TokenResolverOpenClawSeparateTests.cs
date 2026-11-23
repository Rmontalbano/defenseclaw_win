using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// 1.0.10 keeps the OpenClaw token (OPENCLAW_GATEWAY_TOKEN) apart from DefenseClaw's API token. The resolver follows the runtime it
/// is told about (OpenClawTokenSeparate) and defaults to the 1.0.9 behaviour. All values here are invented.
/// </summary>
public class TokenResolverOpenClawSeparateTests
{
    private const string Canonical = "DEFENSECLAW_GATEWAY_TOKEN";
    private const string OpenClaw = "OPENCLAW_GATEWAY_TOKEN";

    private static DefenseClawConfig ConfigWith(string? tokenEnv = null, string? literal = null)
    {
        var config = new DefenseClawConfig();
        if (tokenEnv is not null)
        {
            config.Gateway.TokenEnv = tokenEnv;
        }

        config.Gateway.Token = literal;
        return config;
    }

    private static TokenResolution Resolve(
        bool separate,
        DefenseClawConfig config,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? dotEnv = null)
    {
        var resolver = new TokenResolver(
            () => dotEnv ?? new Dictionary<string, string>(),
            new DictionaryEnvironmentReader(env ?? new Dictionary<string, string>()))
        {
            OpenClawTokenSeparate = () => separate,
        };
        return resolver.Resolve(config);
    }

    [Theory]
    [InlineData("OPENCLAW_GATEWAY_TOKEN")]
    [InlineData("openclaw_gateway_token")]
    [InlineData("OpenClaw_Gateway_Token")]
    public void A_token_env_naming_the_OpenClaw_variable_is_skipped_and_the_canonical_one_used(string tokenEnv)
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "oc-invented-1", [Canonical] = "dc-invented-1" };

        var result = Resolve(true, ConfigWith(tokenEnv), env);

        Assert.True(result.Found);
        Assert.Equal("dc-invented-1", result.Token!.Reveal());
        Assert.Equal(TokenSource.Environment, result.Source);
        Assert.Equal(Canonical, result.VariableName);
        Assert.Contains(OpenClaw, result.Note);
        Assert.Contains("defenseclaw doctor --fix", result.Note);
        Assert.DoesNotContain("oc-invented-1", result.Note);
        Assert.DoesNotContain("dc-invented-1", result.Note);
    }

    [Fact]
    public void Nothing_but_the_OpenClaw_token_means_no_token_and_the_note_still_names_only_the_variable()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "oc-invented-2" };

        var result = Resolve(true, ConfigWith(OpenClaw), env);

        Assert.False(result.Found);
        Assert.Equal(Canonical, result.VariableName);
        Assert.DoesNotContain("oc-invented-2", result.ToString());
    }

    [Fact]
    public void A_canonical_value_equal_to_the_OpenClaw_token_is_skipped_and_the_literal_used()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "shared-invented", [Canonical] = " shared-invented " };

        var result = Resolve(true, ConfigWith(literal: "literal-invented"), env);

        Assert.Equal(TokenSource.ConfigLiteral, result.Source);
        Assert.Equal("literal-invented", result.Token!.Reveal());
    }

    [Fact]
    public void The_OpenClaw_token_can_come_from_the_dot_env_file()
    {
        var dotEnv = new Dictionary<string, string> { [OpenClaw] = "shared-invented", [Canonical] = "shared-invented" };

        var result = Resolve(true, ConfigWith(literal: "literal-invented"), dotEnv: dotEnv);

        Assert.Equal(TokenSource.ConfigLiteral, result.Source);
    }

    [Fact]
    public void A_literal_equal_to_the_OpenClaw_token_resolves_to_nothing()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "shared-invented" };

        var result = Resolve(true, ConfigWith(literal: "shared-invented"), env);

        Assert.False(result.Found);
        Assert.Equal(TokenSource.None, result.Source);
    }

    [Fact]
    public void A_custom_token_env_with_no_value_falls_through_to_the_canonical_one()
    {
        var env = new Dictionary<string, string> { ["MY_GW_TOKEN"] = "  ", [Canonical] = "dc-invented-3" };

        var result = Resolve(true, ConfigWith("MY_GW_TOKEN"), env);

        Assert.Equal("dc-invented-3", result.Token!.Reveal());
        Assert.Equal(Canonical, result.VariableName);
        Assert.Null(result.Note);
    }

    [Fact]
    public void A_custom_token_env_with_a_value_wins_over_the_canonical_one()
    {
        var env = new Dictionary<string, string> { ["MY_GW_TOKEN"] = "mine-invented", [Canonical] = "dc-invented-4" };

        var result = Resolve(true, ConfigWith("MY_GW_TOKEN"), env);

        Assert.Equal("mine-invented", result.Token!.Reveal());
        Assert.Equal("MY_GW_TOKEN", result.VariableName);
    }

    [Fact]
    public void A_secret_reference_is_still_skipped()
    {
        var env = new Dictionary<string, string> { [Canonical] = "{\"source\": \"env\", \"id\": \"SOME_VAR\"}" };

        var result = Resolve(true, ConfigWith(), env);

        Assert.False(result.Found);
        Assert.Contains("defenseclaw doctor --fix", result.Note);
        Assert.DoesNotContain("SOME_VAR", result.Note);
    }

    [Fact]
    public void A_secret_reference_in_the_OpenClaw_variable_is_not_an_OpenClaw_token()
    {
        var env = new Dictionary<string, string>
        {
            [OpenClaw] = "{\"source\": \"env\", \"id\": \"SOME_VAR\"}",
            [Canonical] = "dc-invented-5",
        };

        var result = Resolve(true, ConfigWith(), env);

        Assert.Equal("dc-invented-5", result.Token!.Reveal());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_owner_shape_resolves_the_same_source_and_name_either_way(bool separate)
    {
        var dotEnv = new Dictionary<string, string> { [Canonical] = "dc-invented-6" };

        var result = Resolve(separate, ConfigWith(Canonical), dotEnv: dotEnv);

        Assert.Equal(TokenSource.DotEnvFile, result.Source);
        Assert.Equal(Canonical, result.VariableName);
        Assert.Equal("dc-invented-6", result.Token!.Reveal());
        Assert.Null(result.Note);
    }

    // ---- decision off: the 1.0.9 results, unchanged

    [Fact]
    public void Without_the_decision_a_token_env_naming_the_OpenClaw_variable_is_used_as_before()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "oc-invented-7", [Canonical] = "dc-invented-7" };

        var result = Resolve(false, ConfigWith(OpenClaw), env);

        Assert.Equal("oc-invented-7", result.Token!.Reveal());
        Assert.Equal(OpenClaw, result.VariableName);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Without_the_decision_a_value_equal_to_the_OpenClaw_token_is_still_used()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "shared-invented", [Canonical] = "shared-invented" };

        var result = Resolve(false, ConfigWith(literal: "literal-invented"), env);

        Assert.Equal(TokenSource.Environment, result.Source);
        Assert.Equal("shared-invented", result.Token!.Reveal());
    }

    [Fact]
    public void Without_the_decision_an_empty_custom_token_env_does_not_fall_through()
    {
        var env = new Dictionary<string, string> { [Canonical] = "dc-invented-8" };

        var result = Resolve(false, ConfigWith("MY_GW_TOKEN", "literal-invented"), env);

        Assert.Equal(TokenSource.ConfigLiteral, result.Source);
        Assert.Equal("MY_GW_TOKEN", result.VariableName);
    }

    [Fact]
    public void Without_the_decision_a_literal_equal_to_the_OpenClaw_token_is_used()
    {
        var env = new Dictionary<string, string> { [OpenClaw] = "shared-invented" };

        var result = Resolve(false, ConfigWith(literal: "shared-invented"), env);

        Assert.Equal(TokenSource.ConfigLiteral, result.Source);
    }

    [Fact]
    public void The_decision_defaults_to_off()
    {
        var resolver = new TokenResolver(() => new Dictionary<string, string>(), new DictionaryEnvironmentReader(new Dictionary<string, string>()));

        Assert.False(resolver.OpenClawTokenSeparate());
    }

    // ---- the credential names (G10I-04)

    [Fact]
    public void On_1_0_10_the_gateway_token_names_leave_out_the_OpenClaw_variable()
    {
        Assert.Equal(new[] { Canonical }, GatewayTokenNames.For(true));
        Assert.False(GatewayTokenNames.IsGatewayToken(OpenClaw, null, openClawTokenSeparate: true));
        Assert.False(GatewayTokenNames.IsGatewayToken("openclaw_gateway_token", OpenClaw, openClawTokenSeparate: true));
        Assert.True(GatewayTokenNames.IsGatewayToken("defenseclaw_gateway_token", null, openClawTokenSeparate: true));
        Assert.True(GatewayTokenNames.IsGatewayToken("my_gw_token", "MY_GW_TOKEN", openClawTokenSeparate: true));
    }

    [Fact]
    public void Before_1_0_10_the_gateway_token_names_are_unchanged()
    {
        Assert.Equal(new[] { Canonical, OpenClaw }, GatewayTokenNames.For(false));
        Assert.Same(GatewayTokenNames.Builtin, GatewayTokenNames.For(false));
        Assert.True(GatewayTokenNames.IsGatewayToken(OpenClaw));
        Assert.True(GatewayTokenNames.IsGatewayToken("openclaw_gateway_token", null, false));
    }
}
