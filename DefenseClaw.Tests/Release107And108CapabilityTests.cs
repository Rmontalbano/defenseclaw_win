using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The version-judged capabilities of DefenseClaw 1.0.7 and 1.0.8 (CUST-444), synthetic throughout: each is present from its release on, absent before it,
/// absent on a runtime whose commands were never read, and named with its release in About.
/// </summary>
public sealed class Release107And108CapabilityTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    public static TheoryData<RuntimeCapability, string> Capabilities() => new()
    {
        { RuntimeCapability.DecisionModelCascade, "1.0.7" },
        { RuntimeCapability.AuditStoreCapWarning, "1.0.7" },
        { RuntimeCapability.AgentNotInstalledSkip, "1.0.7" },
        { RuntimeCapability.HookFailureBlockRows, "1.0.7" },
        { RuntimeCapability.JudgeEscalation, "1.0.7" },
        { RuntimeCapability.PerUserCliLauncher, "1.0.7" },
        { RuntimeCapability.CoworkProtection, "1.0.8" },
        { RuntimeCapability.ClaudeDesktopDiscovery, "1.0.8" },
        { RuntimeCapability.JudgeExtraBody, "1.0.8" },
        { RuntimeCapability.OpenShellMiddlewareRetired, "1.0.8" },
        { RuntimeCapability.PowerShellBridgeFailClosed, "1.0.8" },
        { RuntimeCapability.OtlpReplay, "1.0.8" },
        { RuntimeCapability.JudgeFallbackOnHookRows, "1.0.8" },
        { RuntimeCapability.OpenAiCompatibleProviders, "1.0.8" },
        { RuntimeCapability.SecretRefGatewayTokenSkipped, "1.0.7" },
        { RuntimeCapability.RuntimeMonitoringDegradedReasons, "1.0.7" },
    };

    private static RuntimeSnapshot Probe(string version, string? help = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", help, help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_capability_is_present_from_its_release_on_and_absent_before_it(RuntimeCapability capability, string release)
    {
        var floor = Version.Parse(release);
        foreach (var version in new[] { "1.0.0", "1.0.4", "1.0.5", "1.0.6", "1.0.7", "1.0.8", "1.0.9", "1.1.0" })
        {
            Assert.Equal(Version.Parse(version) >= floor, Probe(version).Capabilities.Has(capability));
        }

        Assert.False(Probe("0.8.10").Capabilities.Has(capability));
        Assert.False(Probe("nightly").Capabilities.Has(capability));
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_capability_is_absent_without_evidence_and_names_its_release(RuntimeCapability capability, string release)
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(capability));
        Assert.False(Probe("1.0.8", help: null).Capabilities.Has(capability));
        Assert.Contains(release, RuntimeCapabilityCatalog.DisplayName(capability), StringComparison.Ordinal);
        Assert.Contains(release, RuntimeCapabilityCatalog.Marker(capability), StringComparison.Ordinal);
        Assert.Contains(capability, RuntimeCapabilityCatalog.All);
    }

    [Fact]
    public void Every_capability_of_the_two_releases_is_in_this_list()
    {
        var listed = Capabilities().Select(row => (RuntimeCapability)row[0]).ToHashSet();
        var from107 = RuntimeCapabilityCatalog.All.Where(c => Probe("1.0.8").Capabilities.Has(c) && !Probe("1.0.6").Capabilities.Has(c)).ToHashSet();

        Assert.Superset(listed, from107);
    }
}
