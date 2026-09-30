using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The composition root hands the CLI runner every secret DefenseClaw is configured with — not just the gateway
/// token — at startup and again when config.yaml or .env changes (CUST-197 / D3-04), so a value that is pasted into a
/// form and happens to be one of them is refused on the command line and masked in output. Everything runs over a
/// scratch data directory with synthetic values: the real <c>~/.defenseclaw</c> is never read.
/// </summary>
public class AppServicesConfiguredSecretsTests
{
    private const string LlmKey = "synthetic-llm-key-197-aaaa";
    private const string HookValue = "synthetic-hook-value-197-bbbb";
    private const string LaterToken = "synthetic-later-token-197-cccc";
    private const string PlainSetting = "debug-verbose-setting";

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string Config(string webhookVariable) => LineEndings.Normalize($"""
        config_version: 8
        gateway:
          api_port: 18971
        webhooks:
          - name: chat
            secret_env: {webhookVariable}

        """);

    private static Task<CliInvocation> Echo(AppServices services, string value) =>
        services.Cli.RunExecutableAsync(CmdPath, new[] { "/c", "echo", value });

    [Fact]
    public async Task Secrets_in_dot_env_and_the_variables_config_names_are_refused_on_argv_from_startup()
    {
        using var temp = new TempDirectory();
        _ = temp.WriteFile(
            ".env",
            $"OPENAI_API_KEY={LlmKey}\nDC_TEST_197_HOOK_STUFF={HookValue}\nLOG_LEVEL={PlainSetting}\n");
        using var services = TestServices.Create(temp, Config("DC_TEST_197_HOOK_STUFF"));

        // A secret-named .env entry, and a plainly named one that a *_env key points at.
        await Assert.ThrowsAsync<SecretInArgumentException>(() => Echo(services, LlmKey));
        await Assert.ThrowsAsync<SecretInArgumentException>(() => Echo(services, $"--key={HookValue}"));

        // An ordinary setting in the same file is nobody's secret.
        var ordinary = await Echo(services, PlainSetting);
        Assert.Equal(0, ordinary.ExitCode);
    }

    [Fact]
    public async Task A_secret_that_appears_in_dot_env_after_startup_is_registered_when_the_config_reloads()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, Config("DC_TEST_197_UNSET_VARIABLE"));

        // Not yet known: a harmless echo of a value nothing has told the runner about.
        var before = await Echo(services, LaterToken);
        Assert.Equal(0, before.ExitCode);

        _ = temp.WriteFile(".env", $"SPLUNK_HEC_TOKEN={LaterToken}\n");
        services.ReloadConfig();

        await Assert.ThrowsAsync<SecretInArgumentException>(() => Echo(services, LaterToken));
    }

    [Fact]
    public async Task Reloading_again_with_the_same_secrets_changes_nothing()
    {
        using var temp = new TempDirectory();
        _ = temp.WriteFile(".env", $"OPENAI_API_KEY={LlmKey}\n");
        using var services = TestServices.Create(temp, Config("DC_TEST_197_UNSET_VARIABLE"));

        services.ReloadConfig();
        services.ReloadConfig();

        await Assert.ThrowsAsync<SecretInArgumentException>(() => Echo(services, LlmKey));
        var ordinary = await Echo(services, PlainSetting);
        Assert.Equal(0, ordinary.ExitCode);
    }

    [Fact]
    public async Task A_missing_dot_env_is_not_an_error()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, Config("DC_TEST_197_UNSET_VARIABLE"));

        services.ReloadConfig();

        Assert.Null(services.ConfigLoadError);
        var ordinary = await Echo(services, PlainSetting);
        Assert.Equal(0, ordinary.ExitCode);
    }

    [Fact]
    public async Task A_config_that_cannot_be_parsed_still_registers_dot_env_secrets()
    {
        using var temp = new TempDirectory();
        _ = temp.WriteFile(".env", $"OPENAI_API_KEY={LlmKey}\n");

        // Unbalanced flow mapping: the typed parse fails and the app comes up on defaults with a banner.
        using var services = TestServices.Create(temp, "gateway: {api_port: 18971\nllm: [\n");

        await Assert.ThrowsAsync<SecretInArgumentException>(() => Echo(services, LlmKey));
    }
}
