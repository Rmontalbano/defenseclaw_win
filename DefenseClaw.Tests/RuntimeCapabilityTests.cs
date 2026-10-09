using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Runtime detection (CUST-291). Two sets of fixtures: <c>runtime-0.8.10</c>, the help screens of the installed 0.8.10 CLI, and
/// <c>runtime-95159fd</c>, those of the pinned newer source (copied from the Docker captures; the root screen is rebuilt from the
/// captured command tree because the capture run skipped it). The same markers must say "no" on the first and "yes" on the second.
/// </summary>
public class RuntimeCapabilityTests
{
    internal static string Fixture(string set, string file) => FixtureFiles.ReadText(Path.Combine("runtime-" + set, file));

    private static bool Has(string set, string file) => File.Exists(Path.Combine(FixtureFiles.Directory, "runtime-" + set, file));

    internal static RuntimeProbeScreens Screens(string set, string? version = null) => new(
        VersionJson: version ?? Fixture(set, "version.json"),
        RootHelp: Fixture(set, "root.txt"),
        SetupHelp: Fixture(set, "setup.txt"),
        GuardrailHelp: Fixture(set, "guardrail.txt"),
        ConfigHelp: Fixture(set, "config.txt"),
        SandboxHelp: Fixture(set, "sandbox.txt"),
        AcpHelp: Has(set, "acp.txt") ? Fixture(set, "acp.txt") : null,
        RedactionHelp: Has(set, "setup-redaction.txt") ? Fixture(set, "setup-redaction.txt") : null,
        DiscoveryHelp: Has(set, "agent-discovery.txt") ? Fixture(set, "agent-discovery.txt") : null,
        DiscoveryRuntimeHelp: Has(set, "agent-discovery-runtime.txt") ? Fixture(set, "agent-discovery-runtime.txt") : null);

    private static RuntimeSnapshot Evaluate(RuntimeProbeScreens screens) =>
        RuntimeProbe.Evaluate(screens, "defenseclaw.exe", "fp", DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_installed_0_8_10_runtime_is_known_and_has_none_of_the_newer_capabilities()
    {
        // 0.8.10 has no acp or redaction screens; its 'sandbox' group is the old Linux-only standalone mode.
        var snapshot = Evaluate(Screens("0.8.10") with { AcpHelp = null, RedactionHelp = null });

        Assert.True(snapshot.IsKnown);
        Assert.Equal("0.8.10", snapshot.Identity!.Version);
        Assert.Equal("defenseclaw-cli", snapshot.Identity.Name);
        Assert.Equal(1, snapshot.Identity.SchemaVersion);
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.False(snapshot.Capabilities.Has(capability), capability + " must be absent on 0.8.10");
        }

        Assert.Empty(snapshot.Capabilities.Present);
        Assert.False(snapshot.Capabilities.HasSetupCommand("amp"));
        Assert.True(snapshot.Capabilities.HasSetupCommand("claude-code"));
        Assert.Empty(snapshot.Capabilities.Notes);
    }

    [Fact]
    public void The_pinned_runtime_has_every_capability()
    {
        var snapshot = Evaluate(Screens("95159fd"));

        Assert.True(snapshot.IsKnown);
        Assert.Equal("1.0.0", snapshot.Identity!.Version);
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.True(snapshot.Capabilities.Has(capability), capability + " must be present at the pin");
        }

        Assert.True(snapshot.Capabilities.HasSetupCommand("redaction"));
        Assert.True(snapshot.Capabilities.HasSetupCommand("kiro"));
    }

    [Fact]
    public void The_pinned_sandbox_help_says_it_is_for_linux_and_macos_and_that_becomes_a_note_not_a_denial()
    {
        var snapshot = Evaluate(Screens("95159fd"));

        Assert.True(snapshot.Capabilities.Has(RuntimeCapability.Sandbox));
        Assert.Contains(snapshot.Capabilities.Notes, n => n.Contains("Linux and macOS only", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_capability_needs_its_own_marker_and_one_missing_screen_removes_only_that_capability()
    {
        var pin = Screens("95159fd");

        var noAcp = Evaluate(pin with { AcpHelp = null }).Capabilities;
        Assert.False(noAcp.Has(RuntimeCapability.AcpGuard));
        Assert.True(noAcp.Has(RuntimeCapability.Sandbox));
        Assert.True(noAcp.Has(RuntimeCapability.RedactionAdvanced));

        var noRedaction = Evaluate(pin with { RedactionHelp = null }).Capabilities;
        Assert.False(noRedaction.Has(RuntimeCapability.RedactionAdvanced));
        Assert.True(noRedaction.Has(RuntimeCapability.AcpGuard));

        var noGuardrail = Evaluate(pin with { GuardrailHelp = null }).Capabilities;
        Assert.False(noGuardrail.Has(RuntimeCapability.PolicyModel));
        Assert.Contains(noGuardrail.Notes, n => n.Contains("guardrail --help", StringComparison.Ordinal));

        var noRuntimePlanes = Evaluate(pin with { DiscoveryRuntimeHelp = null }).Capabilities;
        Assert.False(noRuntimePlanes.Has(RuntimeCapability.AiRuntime));
        Assert.True(noRuntimePlanes.Has(RuntimeCapability.AcpGuard));
        Assert.True(noRuntimePlanes.Has(RuntimeCapability.Sandbox));
    }

    // ------------------------------------------------------------------ the runtime planes (CUST-309)

    [Fact]
    public void The_runtime_planes_are_absent_on_0_8_10_whose_discovery_screen_lists_no_runtime_group()
    {
        var snapshot = Evaluate(Screens("0.8.10") with { AcpHelp = null, RedactionHelp = null });

        Assert.Contains("scan", RuntimeProbe.ParseCommands(Fixture("0.8.10", "agent-discovery.txt")));
        Assert.DoesNotContain("runtime", RuntimeProbe.ParseCommands(Fixture("0.8.10", "agent-discovery.txt")));
        Assert.False(snapshot.Capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.Empty(snapshot.Capabilities.AiRuntimeCommands);
        Assert.False(snapshot.Capabilities.HasAiRuntimeCommand("scan"));
        Assert.Empty(snapshot.Capabilities.Notes);
    }

    [Fact]
    public void The_pinned_runtime_has_the_runtime_planes_and_every_subcommand_of_them()
    {
        var capabilities = Evaluate(Screens("95159fd")).Capabilities;

        Assert.True(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.Equal(
            new[] { "disable", "enable", "findings", "permissions", "scan", "selftest", "status" },
            capabilities.AiRuntimeCommands.Order(StringComparer.Ordinal).ToArray());
        foreach (var command in new[] { "status", "scan", "findings", "permissions", "selftest", "enable", "disable" })
        {
            Assert.True(capabilities.HasAiRuntimeCommand(command), command);
        }

        Assert.False(capabilities.HasAiRuntimeCommand("grant"));
        Assert.False(capabilities.HasAiRuntimeCommand(string.Empty));
    }

    [Fact]
    public void Each_runtime_subcommand_is_judged_on_its_own_so_a_runtime_without_enable_still_offers_the_poll()
    {
        const string withoutEnable = """
            Usage: defenseclaw agent discovery runtime [OPTIONS] COMMAND [ARGS]...

            Commands:
              findings     List scored runtime findings.
              permissions  What each runtime plane needs.
              scan         Poll the runtime planes immediately.
              status       Show what the runtime planes can see.
            """;

        var capabilities = Evaluate(Screens("95159fd") with { DiscoveryRuntimeHelp = withoutEnable }).Capabilities;

        Assert.True(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.True(capabilities.HasAiRuntimeCommand("scan"));
        Assert.False(capabilities.HasAiRuntimeCommand("enable"));
        Assert.False(capabilities.HasAiRuntimeCommand("disable"));
    }

    [Theory]
    [InlineData("status", "scan")]
    [InlineData("status", "findings")]
    [InlineData("scan", "permissions")]
    public void A_runtime_screen_that_lacks_one_of_the_four_marker_commands_does_not_open_the_panel(string first, string second)
    {
        var help = $"Commands:\n  {first}  one\n  {second}  two\n";

        var capabilities = Evaluate(Screens("95159fd") with { DiscoveryRuntimeHelp = help }).Capabilities;

        Assert.False(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.True(capabilities.Has(RuntimeCapability.AcpGuard));
    }

    [Fact]
    public void A_runtime_screen_that_was_read_does_not_count_unless_the_discovery_screen_lists_the_group()
    {
        var capabilities = Evaluate(Screens("95159fd") with { DiscoveryHelp = Fixture("0.8.10", "agent-discovery.txt") }).Capabilities;

        Assert.False(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.Empty(capabilities.AiRuntimeCommands);
    }

    [Fact]
    public void The_group_is_listed_but_its_screen_could_not_be_read_so_the_planes_stay_hidden_with_a_note()
    {
        var capabilities = Evaluate(Screens("95159fd") with { DiscoveryRuntimeHelp = null }).Capabilities;

        Assert.False(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.Contains(capabilities.Notes, n => n.Contains("agent discovery runtime --help", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_discovery_screen_hides_the_planes_whatever_the_runtime_screen_says()
    {
        var capabilities = Evaluate(Screens("95159fd") with { DiscoveryHelp = null }).Capabilities;

        Assert.False(capabilities.Has(RuntimeCapability.AiRuntime));
        Assert.Empty(capabilities.AiRuntimeCommands);
        Assert.Contains(capabilities.Notes, n => n.Contains("agent discovery --help", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_runtime_has_no_runtime_subcommands()
    {
        Assert.False(RuntimeCapabilities.Unknown.HasAiRuntimeCommand("scan"));
        Assert.Empty(RuntimeCapabilities.Unknown.AiRuntimeCommands);
    }

    [Fact]
    public void Schema_8_needs_a_one_dot_oh_runtime_as_well_as_the_newer_config_commands()
    {
        var pin = Screens("95159fd");
        var older = Evaluate(pin with { VersionJson = "{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"0.9.3\"}" });

        Assert.True(older.IsKnown);
        Assert.False(older.Capabilities.Has(RuntimeCapability.CanonicalSchema8));
        Assert.True(older.Capabilities.Has(RuntimeCapability.AcpGuard));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Traceback (most recent call last):\n  File \"x\", line 1")]
    [InlineData("{\"name\":\"defenseclaw-cli\"}")]
    [InlineData("{\"version\":\"\"}")]
    [InlineData("{\"version\":7}")]
    [InlineData("[1,2,3]")]
    [InlineData("{not json")]
    public void A_version_document_that_is_missing_or_garbage_makes_everything_unknown(string? version)
    {
        var snapshot = Evaluate(Screens("95159fd") with { VersionJson = version });

        Assert.False(snapshot.IsKnown);
        Assert.Null(snapshot.Identity);
        Assert.False(snapshot.Capabilities.IsKnown);
        Assert.Empty(snapshot.Capabilities.Present);
        Assert.False(snapshot.Capabilities.HasSetupCommand("redaction"));
        Assert.NotNull(snapshot.UnknownReason);
    }

    [Fact]
    public void A_runtime_that_cannot_list_its_commands_is_unknown_rather_than_empty()
    {
        var snapshot = Evaluate(Screens("95159fd") with { RootHelp = null, SetupHelp = null });

        Assert.False(snapshot.IsKnown);
        Assert.Contains("1.0.0", snapshot.UnknownReason, StringComparison.Ordinal);
        Assert.Empty(snapshot.Capabilities.Present);
    }

    [Fact]
    public void Help_that_is_not_a_command_list_proves_nothing()
    {
        var garbage = new RuntimeProbeScreens(
            "{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"1.0.0\"}",
            "Error: something went wrong\n",
            "Commands:\nnot indented\n",
            "<html>",
            string.Empty,
            "Commands:\n  PACK   shouting\n",
            "Usage: x\n",
            "Commands:\n\t\n");
        var snapshot = Evaluate(garbage);

        Assert.True(snapshot.IsKnown);
        Assert.Empty(snapshot.Capabilities.Present);
    }

    [Fact]
    public void Only_the_column_zero_Commands_heading_counts_so_0_8_10s_sandbox_description_is_not_mistaken_for_it()
    {
        var commands = RuntimeProbe.ParseCommands(Fixture("0.8.10", "sandbox.txt"));

        Assert.Equal(new[] { "init", "setup" }, commands.Order().ToArray());
    }

    [Fact]
    public void Wrapped_description_lines_and_uppercase_words_are_not_command_names()
    {
        const string help = """
            Usage: x [OPTIONS] COMMAND

            Commands:
              alpha     Does a thing and then
                        keeps going on a second line named Beta.
              beta-two  Short.
              Gamma     Not lowercase.
              d1_x      Underscore and digit.

            Another heading:
              ignored   after the block
            """;

        Assert.Equal(new[] { "alpha", "beta-two", "d1_x" }, RuntimeProbe.ParseCommands(help).Order().ToArray());
    }

    [Theory]
    [InlineData("1.0.0", true, 1, 0, 0)]
    [InlineData("v0.8.10", true, 0, 8, 10)]
    [InlineData("0.8.10-rc1", true, 0, 8, 10)]
    [InlineData("1.0", true, 1, 0, 0)]
    [InlineData("1", false, 0, 0, 0)]
    [InlineData("dev", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    [InlineData("1.x.0", false, 0, 0, 0)]
    public void Versions_are_read_from_their_numeric_prefix(string text, bool ok, int major, int minor, int patch)
    {
        Assert.Equal(ok, RuntimeProbe.TryParseVersion(text, out var version));
        if (ok)
        {
            Assert.Equal(major, version.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(Math.Max(0, patch), Math.Max(0, version.Build));
        }
    }

    [Fact]
    public void A_version_line_among_other_output_is_found()
    {
        var identity = RuntimeProbe.ParseVersionJson("warning: something\n{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"1.0.0\"}\n", "src");

        Assert.NotNull(identity);
        Assert.Equal("1.0.0", identity.Version);
        Assert.Equal("src", identity.Source);
    }

    [Fact]
    public void The_gate_is_open_for_what_0_8_10_has_closed_for_what_it_lacks_and_closed_when_unknown()
    {
        var old = Evaluate(Screens("0.8.10") with { AcpHelp = null, RedactionHelp = null }).Capabilities;
        var pin = Evaluate(Screens("95159fd")).Capabilities;

        Assert.True(RuntimeGate.Check(old, null).IsAvailable);
        Assert.True(RuntimeGate.Check(RuntimeCapabilities.Unknown, null).IsAvailable);

        var denied = RuntimeGate.Check(old, RuntimeCapability.AcpGuard);
        Assert.False(denied.IsAvailable);
        Assert.True(denied.IsHidden);
        Assert.Equal(
            "Requires a compatible DefenseClaw runtime (verified against source commit 95159fd)",
            denied.Reason);

        Assert.True(RuntimeGate.Check(pin, RuntimeCapability.AcpGuard).IsAvailable);
        Assert.Null(RuntimeGate.Check(pin, RuntimeCapability.AcpGuard).Reason);
        Assert.False(RuntimeGate.Check(RuntimeCapabilities.Unknown, RuntimeCapability.Sandbox).IsAvailable);
        Assert.False(RuntimeGate.Check(null, RuntimeCapability.Sandbox).IsAvailable);

        Assert.False(RuntimeGate.CheckSetupCommand(old, "kiro").IsAvailable);
        Assert.True(RuntimeGate.CheckSetupCommand(pin, "kiro").IsAvailable);
        Assert.False(RuntimeGate.CheckSetupCommand(RuntimeCapabilities.Unknown, "kiro").IsAvailable);
    }

    [Fact]
    public void Every_capability_has_a_name_and_a_marker_for_About()
    {
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(capability)));
            Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.Marker(capability)));
        }
    }
}
