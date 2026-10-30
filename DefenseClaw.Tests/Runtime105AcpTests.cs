using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>CUST-401: the two capabilities 1.0.5 adds for the ACP guard status and the linked Codex home, judged from the reported version.</summary>
public sealed class Runtime105AcpTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeCapabilities Probe(string version, string? rootHelp = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", rootHelp, rootHelp, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    public void Both_follow_the_reported_version(string version, bool expected)
    {
        var capabilities = Probe(version);

        Assert.Equal(expected, capabilities.Has(RuntimeCapability.AcpGuardHealth));
        Assert.Equal(expected, capabilities.Has(RuntimeCapability.CodexHomeLinkRefusal));
    }

    [Fact]
    public void A_runtime_that_has_not_answered_or_cannot_list_its_commands_has_neither()
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.AcpGuardHealth));
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.CodexHomeLinkRefusal));
        Assert.False(Probe("nightly").Has(RuntimeCapability.AcpGuardHealth));
        Assert.False(Probe("1.0.5", rootHelp: null).Has(RuntimeCapability.AcpGuardHealth));
    }

    [Fact]
    public void About_names_and_explains_both()
    {
        foreach (var capability in new[] { RuntimeCapability.AcpGuardHealth, RuntimeCapability.CodexHomeLinkRefusal })
        {
            Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(capability)));
            Assert.Contains("1.0.5", RuntimeCapabilityCatalog.Marker(capability), StringComparison.Ordinal);
            Assert.Contains(capability, RuntimeCapabilityCatalog.All);
        }
    }
}
