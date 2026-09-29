using DefenseClaw.Core.Config;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// One test per rung of the ladder the Python CLI implements:
/// process env → ~/.defenseclaw/.env → literal gateway.token in config.yaml.
/// </summary>
public class TokenResolverTests
{
    private const string VariableName = "DEFENSECLAW_GATEWAY_TOKEN";

    private static DefenseClawConfig ConfigWith(string? literalToken = null, string? tokenEnv = null)
    {
        var config = new DefenseClawConfig();
        if (tokenEnv is not null)
        {
            config.Gateway.TokenEnv = tokenEnv;
        }

        config.Gateway.Token = literalToken;
        return config;
    }

    private static TokenResolver Resolver(
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, string>? dotEnv = null) =>
        new(
            () => dotEnv ?? new Dictionary<string, string>(),
            new DictionaryEnvironmentReader(environment ?? new Dictionary<string, string>()));

    [Fact]
    public void Rung1_process_environment_wins()
    {
        var resolver = Resolver(
            environment: new Dictionary<string, string> { [VariableName] = "from-env" },
            dotEnv: new Dictionary<string, string> { [VariableName] = "from-dotenv" });

        var resolution = resolver.Resolve(ConfigWith(literalToken: "from-config"));

        Assert.True(resolution.Found);
        Assert.Equal(TokenSource.Environment, resolution.Source);
        Assert.Equal("from-env", resolution.Token!.Reveal());
    }

    [Fact]
    public void Rung2_dot_env_file_used_when_environment_is_unset()
    {
        var resolver = Resolver(
            dotEnv: new Dictionary<string, string> { [VariableName] = "from-dotenv" });

        var resolution = resolver.Resolve(ConfigWith(literalToken: "from-config"));

        Assert.Equal(TokenSource.DotEnvFile, resolution.Source);
        Assert.Equal("from-dotenv", resolution.Token!.Reveal());
    }

    [Fact]
    public void Rung3_config_literal_is_the_last_resort()
    {
        var resolution = Resolver().Resolve(ConfigWith(literalToken: "from-config"));

        Assert.Equal(TokenSource.ConfigLiteral, resolution.Source);
        Assert.Equal("from-config", resolution.Token!.Reveal());
    }

    [Fact]
    public void No_token_anywhere_resolves_to_None()
    {
        var resolution = Resolver().Resolve(ConfigWith());

        Assert.False(resolution.Found);
        Assert.Equal(TokenSource.None, resolution.Source);
        Assert.Null(resolution.Token);
        Assert.Equal(VariableName, resolution.VariableName);
    }

    [Fact]
    public void Blank_rung_is_skipped_rather_than_accepted()
    {
        var resolver = Resolver(
            environment: new Dictionary<string, string> { [VariableName] = "   " },
            dotEnv: new Dictionary<string, string> { [VariableName] = "from-dotenv" });

        var resolution = resolver.Resolve(ConfigWith());

        Assert.Equal(TokenSource.DotEnvFile, resolution.Source);
    }

    [Fact]
    public void Custom_token_env_name_is_honoured()
    {
        var resolver = Resolver(
            environment: new Dictionary<string, string> { ["MY_CUSTOM_TOKEN"] = "custom" });

        var resolution = resolver.Resolve(ConfigWith(tokenEnv: "MY_CUSTOM_TOKEN"));

        Assert.Equal(TokenSource.Environment, resolution.Source);
        Assert.Equal("MY_CUSTOM_TOKEN", resolution.VariableName);
        Assert.Equal("custom", resolution.Token!.Reveal());
    }

    [Fact]
    public void Default_token_env_is_the_documented_variable()
    {
        Assert.Equal(VariableName, new DefenseClawConfig().Gateway.TokenEnv);
    }

    [Fact]
    public void Resolution_ToString_redacts_the_token()
    {
        var resolver = Resolver(environment: new Dictionary<string, string> { [VariableName] = "super-secret-value" });

        var text = resolver.Resolve(ConfigWith()).ToString();

        Assert.DoesNotContain("super-secret-value", text, StringComparison.Ordinal);
        Assert.Contains(VariableName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_a_real_dot_env_file_from_disk()
    {
        using var temp = new TempDirectory();
        temp.Write(".env", $"""
            # comment line
            UNRELATED=value

            export {VariableName}="quoted-token"
            """);

        var resolver = new TokenResolver(
            temp.File(".env"),
            new DictionaryEnvironmentReader(new Dictionary<string, string>()));

        var resolution = resolver.Resolve(ConfigWith());

        Assert.Equal(TokenSource.DotEnvFile, resolution.Source);
        Assert.Equal("quoted-token", resolution.Token!.Reveal());
    }

    [Fact]
    public void Missing_dot_env_file_is_not_an_error()
    {
        using var temp = new TempDirectory();

        var resolver = new TokenResolver(
            temp.File(".env"),
            new DictionaryEnvironmentReader(new Dictionary<string, string>()));

        Assert.False(resolver.Resolve(ConfigWith()).Found);
    }

    [Theory]
    [InlineData("KEY=value", "KEY", "value")]
    [InlineData("KEY='single'", "KEY", "single")]
    [InlineData("KEY=\"double\"", "KEY", "double")]
    [InlineData("export KEY=exported", "KEY", "exported")]
    [InlineData("  KEY = spaced  ", "KEY", "spaced")]
    [InlineData("KEY=has=equals", "KEY", "has=equals")]
    public void DotEnv_parses_the_shapes_the_cli_writes(string line, string key, string expected)
    {
        var parsed = DotEnvFile.Parse(line);

        Assert.Equal(expected, parsed[key]);
    }

    [Theory]
    [InlineData("# comment")]
    [InlineData("")]
    [InlineData("no-equals-sign")]
    [InlineData("=novalue")]
    [InlineData("export")]
    [InlineData("export FOO")]
    public void DotEnv_ignores_junk_lines(string line)
    {
        Assert.Empty(DotEnvFile.Parse(line));
    }

    // ----------------------------------------------------------------------------------
    // Inline comments and `export<TAB>`. python-dotenv and Go's godotenv both end an unquoted
    // value at a `#` that follows whitespace. This reader used to keep the comment in the
    // value, so `TOKEN=abc   # rotated` became the bearer token "abc   # rotated" and every
    // authenticated call answered 401 from a file that looked fine to its owner.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("TOKEN=abc   # rotated", "abc")]
    [InlineData("TOKEN=abc # rotated", "abc")]
    [InlineData("TOKEN=abc\t# rotated", "abc")]
    [InlineData("TOKEN=abc # one # two", "abc")]
    [InlineData("TOKEN=abc   # rotated\r", "abc")]
    [InlineData("TOKEN=   # only a comment", "")]
    public void DotEnv_strips_an_inline_comment_from_an_unquoted_value(string line, string expected)
    {
        Assert.Equal(expected, DotEnvFile.Parse(line)["TOKEN"]);
    }

    [Theory]
    [InlineData("TOKEN=abc#not-a-comment", "abc#not-a-comment")]
    [InlineData("TOKEN=#starts-with-hash", "#starts-with-hash")]
    [InlineData("TOKEN=\"abc # kept\"", "abc # kept")]
    [InlineData("TOKEN='abc # kept'", "abc # kept")]
    [InlineData("TOKEN=\"abc # kept\"   # trailing", "abc # kept")]
    [InlineData("TOKEN='abc # kept'   # trailing", "abc # kept")]
    [InlineData("TOKEN=\"abc\"   # rotated", "abc")]
    [InlineData("TOKEN=\"a\\\"b # c\"", "a\\\"b # c")]
    [InlineData("TOKEN=\"\"", "")]
    public void DotEnv_keeps_a_hash_that_is_part_of_the_value(string line, string expected)
    {
        // A '#' is only a comment after whitespace; inside quotes it is never one.
        Assert.Equal(expected, DotEnvFile.Parse(line)["TOKEN"]);
    }

    [Theory]
    [InlineData("export\tKEY=tabbed", "KEY", "tabbed")]
    [InlineData("export \t  KEY=mixed", "KEY", "mixed")]
    [InlineData("export   KEY=spaces", "KEY", "spaces")]
    [InlineData("export=1", "export", "1")]
    [InlineData("exports=1", "exports", "1")]
    [InlineData("exportKEY=1", "exportKEY", "1")]
    [InlineData("export = 1", "export", "1")]
    public void DotEnv_export_prefix_takes_any_whitespace_and_only_when_it_is_a_prefix(string line, string key, string expected)
    {
        Assert.Equal(expected, DotEnvFile.Parse(line)[key]);
    }

    [Fact]
    public void A_commented_dot_env_token_is_the_token_not_the_comment()
    {
        var resolver = new TokenResolver(
            () => DotEnvFile.Parse($"{VariableName}=abc123XYZ   # rotated 2026-09-01\n"),
            new DictionaryEnvironmentReader(new Dictionary<string, string>()));

        var resolution = resolver.Resolve(ConfigWith());

        Assert.Equal(TokenSource.DotEnvFile, resolution.Source);
        Assert.Equal("abc123XYZ", resolution.Token!.Reveal());
    }
}
