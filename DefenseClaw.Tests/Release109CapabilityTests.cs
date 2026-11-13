using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The version-judged capabilities of DefenseClaw 1.0.9 (CUST-451), synthetic throughout: each is present from 1.0.9 on, absent before it, absent on a
/// runtime whose commands were never read, and named with its release in About. Every earlier capability stays present on 1.0.9.
/// </summary>
public sealed class Release109CapabilityTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static readonly RuntimeCapability[] Added =
    [
        RuntimeCapability.ConnectorRecoverConfig,
        RuntimeCapability.CoworkPluginStale,
        RuntimeCapability.CoworkPluginRunsAfterRemoval,
        RuntimeCapability.ShellProtectedFileFloor,
        RuntimeCapability.JudgeLocalParallelDefault,
        RuntimeCapability.HostSensorDropLatch,
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
    public void A_capability_is_present_from_1_0_9_on_and_absent_before_it(RuntimeCapability capability)
    {
        foreach (var version in new[] { "0.8.10", "1.0.0", "1.0.6", "1.0.7", "1.0.8", "nightly" })
        {
            Assert.False(Probe(version).Capabilities.Has(capability));
        }

        foreach (var version in new[] { "1.0.9", "1.0.10", "1.1.0" })
        {
            Assert.True(Probe(version).Capabilities.Has(capability));
        }
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_capability_is_absent_without_evidence_and_names_its_release(RuntimeCapability capability)
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(capability));
        Assert.False(Probe("1.0.9", help: null).Capabilities.Has(capability));
        Assert.Contains("1.0.9", RuntimeCapabilityCatalog.DisplayName(capability), StringComparison.Ordinal);
        Assert.Contains("1.0.9", RuntimeCapabilityCatalog.Marker(capability), StringComparison.Ordinal);
        Assert.Contains(capability, RuntimeCapabilityCatalog.All);
    }

    [Fact]
    public void Everything_a_1_0_8_runtime_has_a_1_0_9_runtime_has_too_and_what_1_0_9_adds_is_this_list()
    {
        var at108 = RuntimeCapabilityCatalog.All.Where(c => Probe("1.0.8").Capabilities.Has(c)).ToHashSet();
        var at109 = RuntimeCapabilityCatalog.All.Where(c => Probe("1.0.9").Capabilities.Has(c)).ToHashSet();

        Assert.Subset(at109, at108);
        Assert.Subset(Added.ToHashSet(), at109.Except(at108).ToHashSet());
    }
}
