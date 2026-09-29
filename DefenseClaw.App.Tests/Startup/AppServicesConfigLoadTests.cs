using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// At startup the config read (config.yaml, its YAML parse, the token ladder) can run on a pool thread while
/// the UI thread themes the app, and the services join it. Whichever thread does it, the outcome must be the
/// same, and above all a config that cannot be used must still surface exactly as before: the app comes up on
/// defaults with an error the banner shows, never a dead process.
/// </summary>
public class AppServicesConfigLoadTests
{
    // A variable name nothing sets, so the token comes from the file and the result does not depend on the machine's environment.
    private const string TokenVariable = "DC_APP_TESTS_NO_SUCH_TOKEN_VARIABLE";

    // LF whatever the checkout: the tests below edit it with "\n" in their patterns.
    private static readonly string GoodConfig = LineEndings.Normalize($$"""
        config_version: 8
        gateway:
          host: 127.0.0.1
          api_port: 18971
          token_env: {{TokenVariable}}
          token: sample-not-a-real-token

        """);

    private static AppServices Create(TempDirectory temp, string? configYaml, bool onPoolThread)
    {
        if (configYaml is not null)
        {
            _ = temp.WriteFile("config.yaml", configYaml);
        }

        return AppServices.CreateIsolated(
            TestServices.IsolatedPaths(temp.Path),
            claudeSettingsPath: temp.File("claude-settings.json"),
            readConfigOnPoolThread: onPoolThread);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_good_config_is_loaded_and_its_token_resolved(bool onPoolThread)
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, GoodConfig, onPoolThread);

        Assert.Null(services.ConfigLoadError);
        Assert.Equal(18971, services.ApiPort);
        Assert.Equal(18971, services.Config.Config.Gateway.ApiPort);
        Assert.Equal(TokenSource.ConfigLiteral, services.Token.Source);
        Assert.Equal(TokenVariable, services.Token.VariableName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_config_comes_up_on_defaults_without_an_error(bool onPoolThread)
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, configYaml: null, onPoolThread);

        Assert.Null(services.ConfigLoadError);
        Assert.Equal(string.Empty, services.Config.RawText);
        Assert.Equal(TokenSource.None, services.Token.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_config_that_is_not_yaml_comes_up_on_defaults_and_says_why(bool onPoolThread)
    {
        using var temp = new TempDirectory();
        using var missing = Create(new TempDirectory(), configYaml: null, onPoolThread);
        using var services = Create(temp, "gateway: [unclosed\n  api_port: 18971\n", onPoolThread);

        Assert.NotNull(services.ConfigLoadError);
        Assert.Contains("not valid YAML", services.ConfigLoadError, StringComparison.Ordinal);

        // Defaults, not half of what the file said: the port is the missing-config one, not 18971.
        Assert.Equal(missing.ApiPort, services.ApiPort);
        Assert.NotEqual(18971, services.ApiPort);
        Assert.Equal(TokenSource.None, services.Token.Source);
    }

    [Fact]
    public void The_pool_thread_read_gives_the_same_result_as_the_inline_one()
    {
        using var inlineTemp = new TempDirectory();
        using var pooledTemp = new TempDirectory();
        _ = inlineTemp.WriteFile(".env", $"{TokenVariable}=from-dot-env\n");
        _ = pooledTemp.WriteFile(".env", $"{TokenVariable}=from-dot-env\n");

        // No token in the file, so the .env rung answers: both reads have to reach it.
        var noToken = GoodConfig.Replace("  token: sample-not-a-real-token\n", string.Empty, StringComparison.Ordinal);
        using var inlined = Create(inlineTemp, noToken, onPoolThread: false);
        using var pooled = Create(pooledTemp, noToken, onPoolThread: true);

        Assert.Equal(inlined.ConfigLoadError, pooled.ConfigLoadError);
        Assert.Equal(inlined.ApiPort, pooled.ApiPort);
        Assert.Equal(inlined.Config.RawText, pooled.Config.RawText);
        Assert.Equal(TokenSource.DotEnvFile, inlined.Token.Source);
        Assert.Equal(inlined.Token.Source, pooled.Token.Source);
        Assert.Equal(inlined.Token.VariableName, pooled.Token.VariableName);
    }

    [Fact]
    public void A_reload_after_the_pool_thread_read_still_works()
    {
        // The reload path shares the load code with startup; a config edited after launch must still take effect.
        using var temp = new TempDirectory();
        using var services = Create(temp, GoodConfig, onPoolThread: true);
        Assert.Equal(18971, services.ApiPort);

        _ = temp.WriteFile("config.yaml", GoodConfig.Replace("18971", "18972", StringComparison.Ordinal));
        services.ReloadConfig();

        Assert.Null(services.ConfigLoadError);
        Assert.Equal(18972, services.ApiPort);
    }
}
