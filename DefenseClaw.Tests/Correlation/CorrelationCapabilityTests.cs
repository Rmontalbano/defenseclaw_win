using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests.Correlation;

/// <summary>The capability behind the Agents panel (CUST-355): judged from the runtime's version, closed until the runtime has shown it, and cheap (no process of its own).</summary>
public sealed class CorrelationCapabilityTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeSnapshot Probe(string versionJson, string? rootHelp = Help, string? setupHelp = Help) =>
        RuntimeProbe.Evaluate(new RuntimeProbeScreens(versionJson, rootHelp, setupHelp, null, null, null, null, null), "test", "fp", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.1", true)]
    [InlineData("1.0.2", true)]
    [InlineData("1.4.0", true)]
    [InlineData("0.8.10", false)]
    [InlineData("0.9.9", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected)
    {
        var snapshot = Probe("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}");

        Assert.Equal(expected, snapshot.Capabilities.Has(RuntimeCapability.CorrelationApi));
        Assert.Equal(expected, RuntimeGate.Check(snapshot.Capabilities, RuntimeCapability.CorrelationApi).IsAvailable);
    }

    [Fact]
    public void A_runtime_that_cannot_say_what_it_is_has_the_capability_closed_with_the_standard_sentence()
    {
        var unknown = Probe("garbage");
        var gate = RuntimeGate.Check(unknown.Capabilities, RuntimeCapability.CorrelationApi);

        Assert.False(unknown.IsKnown);
        Assert.False(gate.IsAvailable);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, gate.Reason);
        Assert.False(RuntimeGate.Check(null, RuntimeCapability.CorrelationApi).IsAvailable);
        Assert.False(RuntimeGate.Check(RuntimeCapabilities.Unknown, RuntimeCapability.CorrelationApi).IsAvailable);
    }

    [Fact]
    public void A_1_0_runtime_whose_help_is_not_a_command_list_has_not_shown_it_has_it()
    {
        var snapshot = Probe("{\"version\":\"1.0.1\"}", rootHelp: "Error: something went wrong\n", setupHelp: "Commands:\nnot indented\n");

        Assert.True(snapshot.IsKnown);
        Assert.False(snapshot.Capabilities.Has(RuntimeCapability.CorrelationApi));
    }

    [Fact]
    public void A_version_without_a_number_does_not_open_it()
    {
        Assert.False(Probe("{\"version\":\"nightly\"}").Capabilities.Has(RuntimeCapability.CorrelationApi));
    }

    [Fact]
    public void About_has_a_name_and_a_marker_for_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.CorrelationApi)));
        Assert.Contains("1.0.0", RuntimeCapabilityCatalog.Marker(RuntimeCapability.CorrelationApi), StringComparison.Ordinal);
        Assert.Contains(RuntimeCapability.CorrelationApi, RuntimeCapabilityCatalog.All);
    }
}
