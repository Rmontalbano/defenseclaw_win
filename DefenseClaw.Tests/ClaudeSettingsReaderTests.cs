using System.Diagnostics;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The reader exists because the Setup installer re-plants
/// <c>DEFENSECLAW_FAIL_MODE: closed</c> into Claude Code's settings.json behind the
/// operator's back (observed on the 0.8.10 upgrade). These tests use synthetic files in a
/// temp directory — nothing here ever reads or writes the real <c>~/.claude/settings.json</c>,
/// including the tests that execute the generated remediation command.
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

    // ----------------------------------------------------------------------------------
    // The copyable remediation command. The first version round-tripped the file through
    // ConvertFrom-Json / ConvertTo-Json / Set-Content: it cannot parse the comments and
    // trailing commas this reader tolerates, it reformatted and stripped comments when it
    // did work, and 5.1's Set-Content -Encoding utf8 added a BOM. These tests pin the
    // replacement, and two of them run it for real — against copies in a temp directory.
    // ----------------------------------------------------------------------------------

    private FailModeDrift DriftFor(string settingsPath, string gatewayFailMode = "open") => new()
    {
        EnvFailMode = "closed",
        GatewayFailMode = gatewayFailMode,
        GatewaySource = FailModeDrift.StatusSource,
        SettingsPath = settingsPath,
    };

    [Fact]
    public void Remediation_command_edits_the_text_instead_of_round_tripping_the_json()
    {
        var path = _directory.File("settings.json");

        var command = DriftFor(path).RemediationCommand;

        // Abort on the first failure rather than carrying on with a null or a half-read file.
        Assert.Contains("$ErrorActionPreference = 'Stop'", command, StringComparison.Ordinal);

        // A backup is taken, and the write is BOM-free UTF-8.
        Assert.Contains("Copy-Item", command, StringComparison.Ordinal);
        Assert.Contains(".bak-", command, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::WriteAllText", command, StringComparison.Ordinal);
        Assert.Contains("UTF8Encoding $false", command, StringComparison.Ordinal);

        // Targeted replacement, refusing unless the key occurs exactly once.
        Assert.Contains("[regex]::Replace", command, StringComparison.Ordinal);
        Assert.Contains("-ne 1", command, StringComparison.Ordinal);
        Assert.Contains("throw", command, StringComparison.Ordinal);

        // None of the pieces that made the old form destructive.
        Assert.DoesNotContain("ConvertFrom-Json", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertTo-Json", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-Encoding utf8", command, StringComparison.Ordinal);

        // It edits the file the drift was read from, and it is still one copyable line.
        Assert.Contains($"'{path}'", command, StringComparison.Ordinal);
        Assert.Contains("$v = 'open'", command, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('\r', command);
        Assert.DoesNotContain("@@", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Remediation_command_quotes_a_path_that_contains_an_apostrophe()
    {
        // An unescaped ' would end the PowerShell string early and run the rest of the path
        // as code. Typographic quotes are string delimiters to PowerShell as well.
        var command = DriftFor(@"C:\Users\O'Brien\.claude\settings.json").RemediationCommand;
        Assert.Contains(@"C:\Users\O''Brien\.claude\settings.json", command, StringComparison.Ordinal);

        var typographic = DriftFor("C:\\Users\\O\u2019Brien\\.claude\\settings.json").RemediationCommand;
        Assert.Contains("O\u2019\u2019Brien", typographic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("open\nRemove-Item C:\\ -Recurse")]
    [InlineData("open\n")]
    [InlineData("open\u2019; Remove-Item C:\\ -Recurse; \u2018")]
    [InlineData("$(Remove-Item C:\\ -Recurse)")]
    [InlineData("")]
    public void Remediation_command_refuses_a_fail_mode_that_is_not_a_plain_word(string hostile)
    {
        // The value comes from /status or config.yaml and lands in a script the operator
        // pastes into a shell: it is validated, never escaped. What comes back is a comment.
        var command = DriftFor(_directory.File("settings.json"), hostile).RemediationCommand;

        Assert.StartsWith("# ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", command, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item", command, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
    }

    private static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Runs the drift's remediation command exactly as an operator pasting it would, in a child
    /// Windows PowerShell 5.1. Refuses to run unless the file it targets is inside the temp
    /// directory this test created — the real ~/.claude/settings.json is never a target.
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunRemediationAsync(string settingsPath)
    {
        Assert.StartsWith(_directory.Path, settingsPath, StringComparison.OrdinalIgnoreCase);
        var command = DriftFor(settingsPath).RemediationCommand;
        Assert.Contains(settingsPath, command, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsPowerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private string[] BackupsOf(string settingsPath) =>
        Directory.GetFiles(Path.GetDirectoryName(settingsPath)!, Path.GetFileName(settingsPath) + ".bak-*");

    [Fact]
    public async Task Remediation_command_changes_only_the_value_in_a_hand_edited_file()
    {
        if (!File.Exists(WindowsPowerShell))
        {
            return; // Not a Windows box; the command targets Windows PowerShell.
        }

        // Comments, a trailing comma and a BOM: everything ConvertFrom-Json chokes on.
        var original = "\uFEFF" + """
            {
              // planted by the installer on upgrade
              "env": {
                "DEFENSECLAW_FAIL_MODE": "closed",
                "KEEP": "1",
              },
              /* operator note */
              "permissions": { "allow": ["Bash(ls)",], },
            }
            """;
        var path = _directory.File("edited.json");
        await File.WriteAllTextAsync(path, original, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (exitCode, output) = await RunRemediationAsync(path);

        Assert.True(exitCode == 0, output);

        // Byte-for-byte the original, except the one value. (The BOM was in the text, so it is
        // encoded as a BOM: the command must drop it.)
        var expected = original.TrimStart('\uFEFF').Replace("\"closed\"", "\"open\"", StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "a BOM was written");
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Contains("// planted by the installer", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // The pre-edit file was kept, untouched.
        var backup = Assert.Single(BackupsOf(path));
        Assert.Equal(
            original.TrimStart('\uFEFF'),
            (await File.ReadAllTextAsync(backup)).TrimStart('\uFEFF'));

        // And what it produced is what the reader — which shares Claude Code's tolerance — reads.
        Assert.Equal("open", new ClaudeSettingsReader(path).Read().FailModeOverride);
    }

    [Fact]
    public async Task Remediation_command_refuses_an_ambiguous_file_and_changes_nothing()
    {
        if (!File.Exists(WindowsPowerShell))
        {
            return;
        }

        // The key also appears in a comment: two matches, so which one is meant is a human's call.
        var original = """
            {
              // was: "DEFENSECLAW_FAIL_MODE": "open"
              "env": { "DEFENSECLAW_FAIL_MODE": "closed" }
            }
            """;
        var path = _directory.File("ambiguous.json");
        await File.WriteAllTextAsync(path, original);

        var (exitCode, output) = await RunRemediationAsync(path);

        Assert.NotEqual(0, exitCode);

        // PowerShell wraps a long error message at a fixed width, mid-word if need be, and the
        // message embeds the (long) temp path — so compare with whitespace removed. (When a child
        // PowerShell reports errors as CLIXML, line breaks arrive as _x000D__x000A_ instead.)
        var unwrapped = string
            .Concat(output.Where(c => !char.IsWhiteSpace(c)))
            .Replace("_x000D__x000A_", string.Empty, StringComparison.Ordinal);
        Assert.Contains("Nothingwaschanged", unwrapped, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(BackupsOf(path));
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
