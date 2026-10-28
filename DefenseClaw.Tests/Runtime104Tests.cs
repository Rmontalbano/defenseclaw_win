using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>DefenseClaw 1.0.4 behaviour (CUST-373): the capabilities, the removed connectors, the per-user fail-mode rule, the install lock and the gateway token's name.</summary>
public sealed class Runtime104Tests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string RegistrySetup = "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  amp    Amp.\n  devin  Devin.\n  kiro   Kiro.\n";

    private static RuntimeSnapshot Probe(string version, string? setupHelp = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, setupHelp, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------------ capabilities

    [Theory]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.5", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.3", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    public void Both_1_0_4_capabilities_follow_the_reported_version(string version, bool expected)
    {
        var capabilities = Probe(version).Capabilities;

        Assert.Equal(expected, capabilities.Has(RuntimeCapability.ConnectorsRetired));
        Assert.Equal(expected, capabilities.Has(RuntimeCapability.PerUserHookRules));
        Assert.Equal(expected, RemovedConnectors.AreRetired(capabilities));
    }

    [Fact]
    public void A_runtime_that_has_not_answered_keeps_the_connectors_and_the_old_fail_mode_reading()
    {
        Assert.False(RemovedConnectors.AreRetired(RuntimeCapabilities.Unknown));
        Assert.False(RemovedConnectors.AreRetired(null));
        Assert.False(HookFailModeRule.SavedValueIgnored(RuntimeCapabilities.Unknown, managed: false));
        Assert.False(Probe("nightly").Capabilities.Has(RuntimeCapability.ConnectorsRetired));
        Assert.False(Probe("1.0.4", rootHelpless: true).Capabilities.Has(RuntimeCapability.PerUserHookRules));
    }

    private static RuntimeSnapshot Probe(string version, bool rootHelpless) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"version\":\"" + version + "\"}", null, null, null, null, null, null, null), "test", "fp", DateTimeOffset.UnixEpoch);

    [Fact]
    public void About_names_and_explains_both()
    {
        foreach (var capability in new[] { RuntimeCapability.ConnectorsRetired, RuntimeCapability.PerUserHookRules, RuntimeCapability.Config104Keys })
        {
            Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(capability)));
            Assert.Contains("1.0.4", RuntimeCapabilityCatalog.Marker(capability), StringComparison.Ordinal);
            Assert.Contains(capability, RuntimeCapabilityCatalog.All);
        }
    }

    // ------------------------------------------------------------------ the removed connectors

    [Theory]
    [InlineData("openhands", true)]
    [InlineData("OmniGent", true)]
    [InlineData(" open-hands ", true)]
    [InlineData("OPEN_HANDS", true)]
    [InlineData("codex", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_removed_names_are_those_the_runtime_recognises(string? name, bool expected) =>
        Assert.Equal(expected, RemovedConnectors.IsRemoved(name));

    [Fact]
    public void Without_drops_them_only_when_the_runtime_removed_them()
    {
        var names = new[] { "codex", "openhands", "omnigent", "cursor" };

        Assert.Equal(new[] { "codex", "cursor" }, RemovedConnectors.Without(names, retired: true));
        Assert.Equal(names, RemovedConnectors.Without(names, retired: false));
    }

    [Fact]
    public void The_command_catalogue_of_a_1_0_4_runtime_lacks_the_two_setup_entries_and_nothing_else()
    {
        var older = Probe("1.0.3", RegistrySetup).Capabilities;
        var current = Probe("1.0.4", RegistrySetup).Capabilities;

        Assert.True(older.Has(RuntimeCapability.TuiRegistry));
        Assert.Same(TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.For(older));

        var derived = TuiRegistryCatalogues.For(current);
        Assert.Same(TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors, derived);
        Assert.Equal(TuiRegistryCatalogues.Extended.Entries.Count - 2, derived.Entries.Count);
        Assert.Equal(derived.Entries.Count, derived.SourceEntries);
        Assert.Null(derived.Find("setup openhands"));
        Assert.Null(derived.Find("setup omnigent"));
        Assert.NotNull(TuiRegistryCatalogues.Extended.Find("setup openhands"));
        Assert.Equal(
            TuiRegistryCatalogues.Extended.Entries.Where(e => e.Name is not ("setup openhands" or "setup omnigent")).Select(e => e.Name),
            derived.Entries.Select(e => e.Name));

        // 0.8.10's catalogue is untouched by the capability.
        Assert.Same(TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.For(Probe("0.8.10").Capabilities));
    }

    // ------------------------------------------------------------------ the fail-mode rule

    [Fact]
    public void A_saved_value_is_ignored_only_on_a_per_user_1_0_4_host()
    {
        var current = Probe("1.0.4").Capabilities;
        var older = Probe("1.0.3").Capabilities;

        Assert.True(HookFailModeRule.SavedValueIgnored(current, managed: false));
        Assert.False(HookFailModeRule.SavedValueIgnored(current, managed: true));
        Assert.False(HookFailModeRule.SavedValueIgnored(older, managed: false));
    }

    [Fact]
    public void Observe_with_a_saved_closed_is_a_mismatch_only_where_the_saved_value_counts()
    {
        var settings = new GuardrailConnectorSettings { Mode = "observe", HookFailMode = "closed" };

        Assert.True(settings.HasFailModeMismatch);
        Assert.True(settings.HasFailModeMismatchOn(savedValueIgnored: false));
        Assert.False(settings.HasFailModeMismatchOn(savedValueIgnored: true));
        Assert.True(settings.SavesFailClosed);
        Assert.False(new GuardrailConnectorSettings { Mode = "observe", HookFailMode = "open" }.SavesFailClosed);
    }

    [Fact]
    public void The_rule_text_is_the_runtimes_own_and_names_the_strict_pack_command()
    {
        Assert.Equal("defenseclaw guardrail use-pack strict", HookFailModeRule.StrictPackCommand);
        Assert.Contains("follows the policy tier", HookFailModeRule.PerUserOutageRule, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the install lock

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static string MakeLock(TempDirectory data, string? pid, TimeSpan age)
    {
        var path = data.File(InterruptedInstallLock.LockName);
        Directory.CreateDirectory(path);
        if (pid is not null)
        {
            File.WriteAllText(Path.Combine(path, "pid"), pid + "\r\n");
        }

        Directory.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    [Fact]
    public void No_lock_is_no_finding()
    {
        using var data = new TempDirectory();

        Assert.Null(InterruptedInstallLock.Read(data.Path, Now, _ => null));
    }

    [Fact]
    public void A_lock_held_by_a_running_powershell_is_an_install_in_progress()
    {
        using var data = new TempDirectory();
        MakeLock(data, "4321", TimeSpan.FromMinutes(30));

        Assert.Null(InterruptedInstallLock.Read(data.Path, Now, pid => pid == 4321 ? "powershell" : null));
        Assert.Null(InterruptedInstallLock.Read(data.Path, Now, pid => pid == 4321 ? "pwsh" : null));
    }

    [Theory]
    [InlineData("4321", null)]
    [InlineData("4321", "notepad")]
    [InlineData("not a number", "powershell")]
    [InlineData(null, "powershell")]
    public void A_lock_nobody_installs_under_is_a_finding(string? pid, string? runningAs)
    {
        using var data = new TempDirectory();
        var path = MakeLock(data, pid, TimeSpan.FromHours(3));

        var finding = InterruptedInstallLock.Read(data.Path, Now, p => runningAs);

        Assert.NotNull(finding);
        Assert.Equal(path, finding.Path);
        Assert.Equal(pid == "4321" ? 4321 : null, finding.HolderProcessId);
    }

    [Fact]
    public void A_lock_that_has_just_been_taken_is_given_a_moment_for_its_pid_file()
    {
        using var data = new TempDirectory();
        MakeLock(data, pid: null, age: TimeSpan.FromSeconds(10));

        Assert.Null(InterruptedInstallLock.Read(data.Path, Now, _ => null));
    }

    [Fact]
    public void A_lock_that_is_a_plain_file_blocks_the_hooks_too()
    {
        using var data = new TempDirectory();
        var path = data.Write(InterruptedInstallLock.LockName, "x");
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromHours(1)).UtcDateTime);

        Assert.NotNull(InterruptedInstallLock.Read(data.Path, Now, _ => null));
    }

    // ------------------------------------------------------------------ the gateway token's name

    [Theory]
    [InlineData("DEFENSECLAW_GATEWAY_TOKEN", true)]
    [InlineData("defenseclaw_gateway_token", true)]
    [InlineData("OpenClaw_Gateway_Token", true)]
    [InlineData("OPENAI_API_KEY", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_builtin_gateway_token_names_match_without_regard_to_case(string? name, bool expected) =>
        Assert.Equal(expected, GatewayTokenNames.IsGatewayToken(name));

    [Fact]
    public void The_configured_token_env_matches_without_regard_to_case_too()
    {
        Assert.True(GatewayTokenNames.IsGatewayToken("my_gw_token", "MY_GW_TOKEN"));
        Assert.False(GatewayTokenNames.IsGatewayToken("my_gw_token", null));
    }

    [Fact]
    public void A_listed_credential_knows_it_is_the_gateway_token_in_any_case()
    {
        const string json = "[{\"env_name\":\"defenseclaw_gateway_token\",\"canonical_env_name\":\"DEFENSECLAW_GATEWAY_TOKEN\",\"feature\":\"Gateway\",\"requirement\":\"REQUIRED\",\"source\":\"dotenv\",\"set\":true,\"description\":\"d\"},"
                            + "{\"env_name\":\"OPENAI_API_KEY\",\"feature\":\"LLM\",\"requirement\":\"optional\",\"source\":\"unset\",\"set\":false,\"description\":\"d\"}]";

        Assert.True(CredentialListParser.TryParse(json, out var rows, out _));
        Assert.True(rows[0].IsGatewayToken);
        Assert.False(rows[1].IsGatewayToken);
    }
}
