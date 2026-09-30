using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Security;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Which values from <c>.env</c> and from the variables a config <c>*_env</c> key names are handed to the CLI runner
/// as secrets, and what the runner then does with them. Every value here is synthetic; the real
/// <c>~/.defenseclaw/.env</c> is never read.
/// </summary>
public class ConfiguredSecretsTests
{
    private const string GatewayToken = "synthetic-gateway-token-0001";
    private const string LlmKey = "synthetic-llm-key-0002";
    private const string HookSecret = "synthetic-hook-secret-0003";

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static IReadOnlyDictionary<string, string> Env(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    private static IEnvironmentReader ProcessEnv(params (string Name, string Value)[] entries) =>
        new DictionaryEnvironmentReader(Env(entries));

    private static string[] Reveal(IEnumerable<SecretValue> secrets) => secrets.Select(s => s.Reveal()).ToArray();

    // ------------------------------------------------------------------ *_env keys in config text

    private const string ConfigLf =
        "config_version: 8\n" +
        "gateway:\n" +
        "  token_env: MY_GATEWAY_TOKEN\n" +
        "llm:\n" +
        "  api_key_env: \"MY_LLM_KEY\"   # quoted, with a comment\n" +
        "webhooks:\n" +
        "  - name: chat\n" +
        "    secret_env: 'HOOK_SECRET'\n" +
        "  - name: quiet\n" +
        "    secret_env:\n" +
        "cisco_ai_defense:\n" +
        "  api_key_env: NOT A NAME\n" +
        "  # commented_env: NOT_THIS_EITHER\n" +
        "  token: literal-value-not-a-name\n" +
        "  duplicate_env: MY_LLM_KEY\n";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_names_come_from_every_env_key_in_any_section_whatever_the_line_ending(string newline)
    {
        var config = ConfigLf.Replace("\n", newline, StringComparison.Ordinal);

        var names = ConfiguredSecrets.EnvironmentVariableNamesIn(config);

        Assert.Equal(new[] { "MY_GATEWAY_TOKEN", "MY_LLM_KEY", "HOOK_SECRET" }, names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gateway:\n  api_port: 18970\n")]
    public void Config_text_with_no_env_keys_names_nothing(string? config) =>
        Assert.Empty(ConfiguredSecrets.EnvironmentVariableNamesIn(config));

    // ------------------------------------------------------------------ what is collected

    [Fact]
    public void Secret_entries_in_dot_env_are_collected_and_the_rest_are_not()
    {
        var dotEnv = Env(
            ("DEFENSECLAW_GATEWAY_TOKEN", GatewayToken),
            ("OPENAI_API_KEY", LlmKey),
            ("LOG_LEVEL", "debug-verbose"),
            ("SPLUNK_HEC_URL", "https://splunk.example:8088"),
            ("EMPTY_SECRET", string.Empty),
            ("SHORT_TOKEN", "abc"),
            ("REFERENCE_KEY", "$SOMETHING_ELSE_ENTIRELY"));

        var collected = Reveal(ConfiguredSecrets.Collect(null, dotEnv, ProcessEnv()));

        Assert.Equal(new[] { GatewayToken, LlmKey }, collected.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void A_variable_a_config_env_key_names_is_a_secret_whatever_its_value_looks_like()
    {
        // "plain-value-123" is under no secret-sounding name and has no secret shape: only the *_env key marks it.
        var dotEnv = Env(("MY_LLM_STUFF", "plain-value-123"), ("UNRELATED", "plain-value-456"));

        var collected = Reveal(ConfiguredSecrets.Collect("llm:\n  api_key_env: MY_LLM_STUFF\n", dotEnv, ProcessEnv()));

        Assert.Equal(new[] { "plain-value-123" }, collected);
    }

    [Fact]
    public void The_process_environment_wins_over_dot_env_for_a_named_variable_as_it_does_for_the_cli()
    {
        var dotEnv = Env(("MY_LLM_STUFF", "value-from-the-file-1"));
        var environment = ProcessEnv(("MY_LLM_STUFF", "value-from-the-process-2"));

        var collected = Reveal(ConfiguredSecrets.Collect("llm:\n  api_key_env: MY_LLM_STUFF\n", dotEnv, environment));

        Assert.Equal(new[] { "value-from-the-process-2" }, collected);
    }

    [Fact]
    public void A_named_variable_that_is_unset_short_or_a_placeholder_is_not_collected()
    {
        var config = "a_env: UNSET_ONE\nb_env: SHORT_ONE\nc_env: FLAG_ONE\nd_env: REFERENCE_ONE\n";
        var environment = ProcessEnv(("SHORT_ONE", "abc"), ("FLAG_ONE", "true"), ("REFERENCE_ONE", "%ELSEWHERE_ENTIRELY%"));

        Assert.Empty(ConfiguredSecrets.Collect(config, Env(), environment));
    }

    [Fact]
    public void The_same_value_from_two_sources_is_collected_once()
    {
        var dotEnv = Env(("DEFENSECLAW_GATEWAY_TOKEN", GatewayToken), ("OTHER_TOKEN", GatewayToken));
        var collected = Reveal(ConfiguredSecrets.Collect("gateway:\n  token_env: DEFENSECLAW_GATEWAY_TOKEN\n", dotEnv, ProcessEnv()));

        Assert.Equal(new[] { GatewayToken }, collected);
    }

    [Fact]
    public void Nothing_configured_collects_nothing()
    {
        Assert.Empty(ConfiguredSecrets.Collect(null, Env(), ProcessEnv()));
        Assert.Empty(ConfiguredSecrets.Collect(string.Empty, Env(), ProcessEnv()));
    }

    [Fact]
    public void A_collected_value_prints_as_redacted()
    {
        var secret = Assert.Single(ConfiguredSecrets.Collect(null, Env(("SPLUNK_HEC_TOKEN", GatewayToken)), ProcessEnv()));

        Assert.DoesNotContain(GatewayToken, secret.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what the runner does with them

    private static CliRunner Runner(TempDirectory temp) =>
        new(new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Path.Combine(temp.Path, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    [Fact]
    public async Task The_runner_refuses_a_collected_secret_on_argv_and_masks_it_in_output()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var dotEnv = Env(("OPENAI_API_KEY", LlmKey), ("LOG_LEVEL", "debug-verbose"));
        foreach (var secret in ConfiguredSecrets.Collect(null, dotEnv, ProcessEnv()))
        {
            runner.RegisterSecret(secret);
        }

        // On the command line, whole or as part of an argument: refused before anything starts.
        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", LlmKey }));
        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", $"--api-key={LlmKey}" }));

        // A value that is not a secret is not refused.
        var ok = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "debug-verbose" });
        Assert.Equal(0, ok.ExitCode);

        // In output, masked wherever it appears.
        var file = temp.Write("echo.txt", $"the key is {LlmKey}, again {LlmKey}");
        var typed = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        var text = string.Join('\n', typed.OutputLines.Select(l => l.Text));
        Assert.DoesNotContain(LlmKey, text, StringComparison.Ordinal);
        Assert.Contains(SecretValue.Redacted, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variable_a_config_env_key_names_is_refused_too()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var dotEnv = Env(("HOOK_SECRET", HookSecret));
        foreach (var secret in ConfiguredSecrets.Collect("webhooks:\n  - secret_env: HOOK_SECRET\n", dotEnv, ProcessEnv()))
        {
            runner.RegisterSecret(secret);
        }

        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", HookSecret }));
    }
}
