using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Codex;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// DefenseClaw 1.0.5 behaviour the app reads (CUST-398), synthetic throughout: the version-judged stale-lock capability, the install lock's stale and
/// recovery cases, Claude Code's <c>disableAllHooks</c>, and Codex's <c>[features] hooks = false</c> (including files that do not parse, in LF and CRLF).
/// </summary>
public sealed class Runtime105AttentionTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeSnapshot Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------------ capability

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_stale_lock_rule_follows_the_reported_version(string version, bool expected)
    {
        var capabilities = Probe(version).Capabilities;

        Assert.Equal(expected, capabilities.Has(RuntimeCapability.StaleInstallLockIgnored));
        Assert.Equal(version is "1.0.5" or "1.1.0" or "1.0.4", capabilities.Has(RuntimeCapability.PerUserHookRules));
    }

    [Fact]
    public void A_runtime_that_has_not_answered_or_cannot_list_its_commands_does_not_have_it()
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.StaleInstallLockIgnored));
        var helpless = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"version\":\"1.0.5\"}", null, null, null, null, null, null, null), "test", "fp", DateTimeOffset.UnixEpoch);
        Assert.False(helpless.Capabilities.Has(RuntimeCapability.StaleInstallLockIgnored));
    }

    [Fact]
    public void About_names_and_explains_it_and_it_is_the_last_capability()
    {
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.StaleInstallLockIgnored)));
        Assert.Contains("1.0.5", RuntimeCapabilityCatalog.Marker(RuntimeCapability.StaleInstallLockIgnored), StringComparison.Ordinal);
        Assert.Contains(RuntimeCapability.StaleInstallLockIgnored, RuntimeCapabilityCatalog.All);
    }

    // ------------------------------------------------------------------ the install lock

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static string MakeLock(TempDirectory data, string? pid)
    {
        var path = data.File(InterruptedInstallLock.LockName);
        Directory.CreateDirectory(path);
        if (pid is not null)
        {
            File.WriteAllText(Path.Combine(path, "pid"), pid + "\n");
        }

        Directory.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromHours(2)).UtcDateTime);
        return path;
    }

    [Fact]
    public void A_lock_whose_pid_names_no_process_is_stale()
    {
        using var data = new TempDirectory();
        MakeLock(data, "4321");

        var finding = InterruptedInstallLock.Read(data.Path, Now, _ => null)!;

        Assert.True(finding.HolderGone);
        Assert.Null(finding.RecoveryRecord);
        Assert.True(finding.IsStale);
        Assert.False(finding.IsRecovery);
    }

    [Theory]
    [InlineData("previous.new", false)]
    [InlineData(".repair", false)]
    [InlineData(".rollback-hold", true)]
    [InlineData(".lifecycle-txn.json", false)]
    public void A_recovery_record_beside_the_lock_makes_it_an_interrupted_install(string record, bool asFolder)
    {
        using var data = new TempDirectory();
        MakeLock(data, "4321");
        var path = data.File(record);
        if (asFolder)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, "{}");
        }

        var finding = InterruptedInstallLock.Read(data.Path, Now, _ => null)!;

        Assert.True(finding.IsRecovery);
        Assert.False(finding.IsStale);
        Assert.Equal(path, finding.RecoveryRecord);
    }

    [Theory]
    [InlineData("4321", "notepad")] // a live process has the pid: the runtime compares identities the app does not read
    [InlineData(null, null)]        // no pid yet
    [InlineData("0", null)]         // not a pid
    [InlineData("not a number", null)]
    public void A_lock_whose_installer_is_not_proven_gone_is_neither_stale_nor_recovery(string? pid, string? runningAs)
    {
        using var data = new TempDirectory();
        MakeLock(data, pid);
        File.WriteAllText(data.File(".repair"), "x");

        var finding = InterruptedInstallLock.Read(data.Path, Now, _ => runningAs)!;

        Assert.False(finding.HolderGone);
        Assert.False(finding.IsStale);
        Assert.False(finding.IsRecovery);
    }

    [Fact]
    public void A_lock_that_is_a_plain_file_is_never_stale()
    {
        using var data = new TempDirectory();
        var path = data.Write(InterruptedInstallLock.LockName, "x");
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromHours(1)).UtcDateTime);

        var finding = InterruptedInstallLock.Read(data.Path, Now, _ => null)!;

        Assert.False(finding.IsStale);
        Assert.False(finding.IsRecovery);
    }

    // ------------------------------------------------------------------ Claude Code disableAllHooks

    [Theory]
    [InlineData("{\"disableAllHooks\": true}", true)]
    [InlineData("{\n  // operator policy\n  \"disableAllHooks\": true,\n}", true)]
    [InlineData("{\r\n  \"env\": {\"DEFENSECLAW_FAIL_MODE\": \"open\"},\r\n  \"disableAllHooks\": true\r\n}", true)]
    [InlineData("{\"disableAllHooks\": false}", false)]
    [InlineData("{\"disableAllHooks\": \"true\"}", false)]
    [InlineData("{\"disableAllHooks\": 1}", false)]
    [InlineData("{\"hooks\": {}}", false)]
    [InlineData("[true]", false)]
    public void Claude_settings_that_turn_every_hook_off_are_read(string json, bool expected)
    {
        using var data = new TempDirectory();
        var path = data.Write("settings.json", json);

        var snapshot = new ClaudeSettingsReader(path).Read();

        Assert.Equal(expected, snapshot.DisableAllHooks);
        Assert.Null(snapshot.ReadError);
    }

    [Fact]
    public void Claude_settings_that_do_not_parse_or_are_missing_turn_nothing_off()
    {
        using var data = new TempDirectory();
        var broken = data.Write("settings.json", "{\"disableAllHooks\": true");

        var read = new ClaudeSettingsReader(broken).Read();
        Assert.False(read.DisableAllHooks);
        Assert.NotNull(read.ReadError);

        Assert.False(new ClaudeSettingsReader(data.File("absent.json")).Read().DisableAllHooks);
    }

    // ------------------------------------------------------------------ Codex [features] hooks = false

    [Theory]
    [InlineData("[features]\nhooks = false\n", "hooks")]
    [InlineData("model = \"o3\"\n\n[features]\ncodex_hooks = false # older name\n", "codex_hooks")]
    [InlineData("[ features ]\n\"hooks\" = false\n", "hooks")]
    [InlineData("[\"features\"]\nhooks=false\n", "hooks")]
    [InlineData("features.hooks = false\n[otel]\nexporter = \"none\"\n", "hooks")]
    [InlineData("features = { web_search = true, hooks = false }\n", "hooks")]
    [InlineData("\uFEFF[features]\nhooks = false", "hooks")]
    [InlineData("args = [\n  \"a\",\n  \"b\", # c\n]\n[features]\nhooks = false\n", "hooks")]
    public void Codex_config_that_turns_hooks_off_is_read(string toml, string expected)
    {
        Assert.Equal(expected, CodexConfigReader.HooksOffKey(toml));
        Assert.Equal(expected, CodexConfigReader.HooksOffKey(toml.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[features]\nhooks = true\n")]
    [InlineData("[features]\nhooks = \"false\"\n")]
    [InlineData("[features]\n# hooks = false\n")]
    [InlineData("[features.extra]\nhooks = false\n")]
    [InlineData("[profiles.work]\nhooks = false\n")]
    [InlineData("hooks = false\n")]
    [InlineData("[[hooks.PreToolUse]]\nhooks = false\n")]
    [InlineData("notes = \"\"\"\n[features]\nhooks = false\n\"\"\"\n")]
    [InlineData("notes = '[features] hooks = false'\n")]
    public void Codex_config_that_leaves_hooks_on_says_so(string toml)
    {
        Assert.Null(CodexConfigReader.HooksOffKey(toml));
        Assert.Null(CodexConfigReader.HooksOffKey(toml.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("[features\nhooks = false\n")]
    [InlineData("[features]\nhooks = false\nthis is not toml\n")]
    [InlineData("[features]\nhooks = false\nargs = [\n\"a\",\n")]
    [InlineData("[features]\nhooks = false\nnotes = '''\nunclosed\n")]
    [InlineData("[features]\nhooks =\n")]
    [InlineData("[features]\nbad key = false\nhooks = false\n")]
    public void Codex_config_that_does_not_parse_makes_no_claim(string toml)
    {
        Assert.Null(CodexConfigReader.HooksOffKey(toml));
        Assert.Null(CodexConfigReader.HooksOffKey(toml.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_codex_reader_reads_the_file_and_tolerates_a_missing_or_oversized_one()
    {
        using var data = new TempDirectory();
        var path = data.Write("config.toml", "[features]\nhooks = false\n");

        var read = new CodexConfigReader(path).Read();
        Assert.True(read.Exists);
        Assert.True(read.HooksOff);
        Assert.Equal(path, read.ConfigPath);

        var missing = new CodexConfigReader(data.File("absent.toml")).Read();
        Assert.False(missing.Exists);
        Assert.False(missing.HooksOff);
        Assert.Null(missing.ReadError);

        var huge = data.Write("huge.toml", "[features]\nhooks = false\n" + new string('#', (int)CodexConfigReader.MaxBytes));
        var oversized = new CodexConfigReader(huge).Read();
        Assert.False(oversized.HooksOff);
        Assert.NotNull(oversized.ReadError);
    }

    [Fact]
    public void Codex_home_moves_the_config()
    {
        Assert.Equal(Path.Combine(@"D:\codex-home", "config.toml"), CodexConfigReader.DefaultConfigPath(name => name == CodexConfigReader.HomeVariableName ? @" D:\codex-home " : null));
        Assert.EndsWith(Path.Combine(".codex", "config.toml"), CodexConfigReader.DefaultConfigPath(_ => "  "), StringComparison.OrdinalIgnoreCase);
    }
}
