using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The reader exists because the Setup installer re-plants
/// <c>DEFENSECLAW_FAIL_MODE: closed</c> into Claude Code's settings.json behind the
/// operator's back (observed on the 0.8.10 upgrade). These tests use synthetic files in a
/// temp directory — nothing here ever reads the real <c>~/.claude/settings.json</c>.
/// </summary>
public class ClaudeSettingsReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new("dcw-claude-settings");

    [Fact]
    public void Missing_file_is_not_an_error()
    {
        var snapshot = ReaderFor("settings.json").Read();

        Assert.False(snapshot.Exists);
        Assert.Null(snapshot.ReadError);
        Assert.Null(snapshot.FailModeOverride);
    }

    [Fact]
    public void Reads_the_planted_fail_mode_override()
    {
        var path = _directory.Write("settings.json", """
            {
              "env": {
                "DEFENSECLAW_FAIL_MODE": "closed",
                "SOMETHING_ELSE": "1"
              }
            }
            """);

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.True(snapshot.Exists);
        Assert.Null(snapshot.ReadError);
        Assert.Equal("closed", snapshot.FailModeOverride);
        Assert.Equal(path, snapshot.SettingsPath);
    }

    [Fact]
    public void Missing_env_block_is_not_an_error()
    {
        var path = _directory.Write("no-env.json", """{ "permissions": { "allow": [] } }""");

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.True(snapshot.Exists);
        Assert.Null(snapshot.ReadError);
        Assert.Null(snapshot.FailModeOverride);
    }

    [Fact]
    public void Missing_variable_is_not_an_error()
    {
        var path = _directory.Write("other-vars.json", """{ "env": { "SOMETHING_ELSE": "1" } }""");

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.True(snapshot.Exists);
        Assert.Null(snapshot.ReadError);
        Assert.Null(snapshot.FailModeOverride);
    }

    [Fact]
    public void Malformed_json_reports_a_read_error()
    {
        var path = _directory.Write("broken.json", """{ "env": { "DEFENSECLAW_FAIL_MODE": """);

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.True(snapshot.Exists);
        Assert.NotNull(snapshot.ReadError);
        Assert.Null(snapshot.FailModeOverride);
    }

    [Fact]
    public void Non_string_value_is_ignored()
    {
        // Claude Code passes the env block to the child process verbatim, so a boolean
        // here is a typo the hook never acts on — not an override.
        var path = _directory.Write("bool.json", """{ "env": { "DEFENSECLAW_FAIL_MODE": true } }""");

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.True(snapshot.Exists);
        Assert.Null(snapshot.ReadError);
        Assert.Null(snapshot.FailModeOverride);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        var path = _directory.Write("relaxed.json", """
            {
              // planted by the installer on upgrade
              "env": {
                "DEFENSECLAW_FAIL_MODE": "closed",
              },
            }
            """);

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.Null(snapshot.ReadError);
        Assert.Equal("closed", snapshot.FailModeOverride);
    }

    [Fact]
    public void No_override_means_no_drift()
    {
        var drift = FailModeDrift.Evaluate(
            SnapshotWith(null),
            new ConnectorMode { Connector = "claudecode", HookFailMode = "open" },
            configured: null);

        Assert.Null(drift);
    }

    [Fact]
    public void Agreement_is_case_insensitive()
    {
        var drift = FailModeDrift.Evaluate(
            SnapshotWith("Closed"),
            new ConnectorMode { Connector = "claudecode", HookFailMode = "closed" },
            configured: null);

        Assert.Null(drift);
    }

    [Fact]
    public void Env_closed_against_runtime_open_is_a_dangerous_pairing()
    {
        var drift = FailModeDrift.Evaluate(
            SnapshotWith("closed"),
            new ConnectorMode { Connector = "claudecode", GuardrailMode = "observe", HookFailMode = "open" },
            configured: null);

        Assert.NotNull(drift);
        Assert.Equal("closed", drift!.EnvFailMode);
        Assert.Equal("open", drift.GatewayFailMode);
        Assert.Equal("/status", drift.GatewaySource);
        Assert.True(drift.IsDangerousPairing);
        Assert.Contains("'open'", drift.RemediationCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_yaml_is_the_fallback_when_status_is_silent()
    {
        // /status is unavailable exactly when the gateway is down, which is when a
        // fail-closed hook actually bites.
        var drift = FailModeDrift.Evaluate(
            SnapshotWith("closed"),
            runtime: null,
            new GuardrailConnectorSettings { Mode = "observe", HookFailMode = "open" });

        Assert.NotNull(drift);
        Assert.Equal("config.yaml", drift!.GatewaySource);
        Assert.Equal("observe", drift.GuardrailMode);
        Assert.True(drift.IsDangerousPairing);
    }

    [Fact]
    public void Nothing_to_compare_against_means_no_drift()
    {
        var drift = FailModeDrift.Evaluate(SnapshotWith("closed"), runtime: null, configured: null);

        Assert.Null(drift);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    private ClaudeSettingsReader ReaderFor(string fileName) => new(_directory.File(fileName));

    private ClaudeSettingsSnapshot SnapshotWith(string? failModeOverride) => new()
    {
        Exists = true,
        FailModeOverride = failModeOverride,
        SettingsPath = _directory.File("settings.json"),
    };
}
