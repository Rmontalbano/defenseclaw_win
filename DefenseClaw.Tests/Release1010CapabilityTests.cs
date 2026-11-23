using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The version-judged capabilities of DefenseClaw 1.0.10 (CUST-455), synthetic throughout: each is present from 1.0.10 on, absent before it (1.0.9 and
/// 1.0.1 included: the release number is compared as a version, not as text), absent on a runtime whose commands were never read, and named with its
/// release in About. Every earlier capability stays present on 1.0.10.
/// </summary>
public sealed class Release1010CapabilityTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static readonly RuntimeCapability[] Added =
    [
        RuntimeCapability.SetupKeepsRoster,
        RuntimeCapability.OpenClawNativeWindows,
        RuntimeCapability.OpenClawTokenSeparate,
        RuntimeCapability.AgentSelfUpdateKeepsGateway,
        RuntimeCapability.RoutingCli,
        RuntimeCapability.RoutingHotReload,
        RuntimeCapability.Tokenomics,
        RuntimeCapability.DecisionModelProtocol,
        RuntimeCapability.GuardrailCascade,
    ];

    public static TheoryData<RuntimeCapability> Capabilities()
    {
        var data = new TheoryData<RuntimeCapability>();
        foreach (var capability in Added)
        {
            data.Add(capability);
        }

        return data;
    }

    private static RuntimeSnapshot Probe(string version, string? help = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", help, help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_capability_is_present_from_1_0_10_on_and_absent_before_it(RuntimeCapability capability)
    {
        foreach (var version in new[] { "0.8.10", "1.0.0", "1.0.1", "1.0.2", "1.0.8", "1.0.9", "nightly" })
        {
            Assert.False(Probe(version).Capabilities.Has(capability));
        }

        foreach (var version in new[] { "1.0.10", "1.0.11", "1.1.0", "2.0.0" })
        {
            Assert.True(Probe(version).Capabilities.Has(capability));
        }
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_capability_is_absent_without_evidence_and_names_its_release(RuntimeCapability capability)
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(capability));
        Assert.False(Probe("1.0.10", help: null).Capabilities.Has(capability));
        Assert.Contains("1.0.10", RuntimeCapabilityCatalog.DisplayName(capability), StringComparison.Ordinal);
        Assert.Contains("1.0.10", RuntimeCapabilityCatalog.Marker(capability), StringComparison.Ordinal);
        Assert.Contains(capability, RuntimeCapabilityCatalog.All);
    }

    [Fact]
    public void Everything_a_1_0_9_runtime_has_a_1_0_10_runtime_has_too_and_what_1_0_10_adds_is_this_list()
    {
        var at109 = RuntimeCapabilityCatalog.All.Where(c => Probe("1.0.9").Capabilities.Has(c)).ToHashSet();
        var at1010 = RuntimeCapabilityCatalog.All.Where(c => Probe("1.0.10").Capabilities.Has(c)).ToHashSet();

        Assert.Subset(at1010, at109);
        Assert.Equal(Added.ToHashSet(), at1010.Except(at109).ToHashSet());
    }
}
