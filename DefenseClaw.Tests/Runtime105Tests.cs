using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>DefenseClaw 1.0.5 self-protection (CUST-400): which runtimes have it, and the one set of sentences that says what observe and fail-open do.</summary>
public sealed class Runtime105Tests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeSnapshot Probe(string version, string? rootHelp = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", rootHelp, rootHelp, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.0.6", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected)
    {
        var capabilities = Probe(version).Capabilities;

        Assert.Equal(expected, capabilities.Has(RuntimeCapability.ObserveBlocksSelfChange));
        Assert.Equal(expected, ObserveModeCopy.BlocksSelfChange(capabilities));
    }

    [Fact]
    public void A_runtime_that_has_not_answered_or_could_not_list_its_commands_keeps_the_old_text()
    {
        Assert.False(ObserveModeCopy.BlocksSelfChange(null));
        Assert.False(ObserveModeCopy.BlocksSelfChange(RuntimeCapabilities.Unknown));
        Assert.False(Probe("1.0.5", rootHelp: null).Capabilities.Has(RuntimeCapability.ObserveBlocksSelfChange));
    }

    [Fact]
    public void About_names_and_explains_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.ObserveBlocksSelfChange)));
        Assert.Contains("1.0.5", RuntimeCapabilityCatalog.Marker(RuntimeCapability.ObserveBlocksSelfChange), StringComparison.Ordinal);
        Assert.Contains(RuntimeCapability.ObserveBlocksSelfChange, RuntimeCapabilityCatalog.All);
    }

    // ------------------------------------------------------------------ the copy

    [Fact]
    public void An_earlier_runtime_keeps_exactly_todays_sentences()
    {
        Assert.Equal("Observe records what the agent does and never blocks it.", ObserveModeCopy.AddConnectorSentence(false));
        Assert.Equal("Observe records what an agent does and never blocks it.", ObserveModeCopy.BatchObserveNote(false));
        Assert.Equal("observe, which never blocks", ObserveModeCopy.ObserveFallbackClause(false));
        Assert.Equal("observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.", ObserveModeCopy.ObserveFailClosedNote(false));
        Assert.Equal(
            "Open allows the tool call or prompt and logs the failure, so a misbehaving gateway never blocks an agent. Recommended for almost all installs.",
            ObserveModeCopy.FailOpenNote(false));
    }

    [Fact]
    public void From_1_0_5_no_sentence_says_observe_never_blocks_without_the_exception()
    {
        var sentences = new[]
        {
            ObserveModeCopy.AddConnectorSentence(true),
            ObserveModeCopy.BatchObserveNote(true),
            ObserveModeCopy.ObserveFallbackClause(true),
            ObserveModeCopy.ObserveFailClosedNote(true),
        };

        Assert.All(sentences, s => Assert.Contains("never blocks on policy findings", s, StringComparison.Ordinal));
        Assert.DoesNotContain("never blocks it", string.Concat(sentences), StringComparison.Ordinal);
        Assert.Contains("still blocks changes to its own files", ObserveModeCopy.AddConnectorSentence(true), StringComparison.Ordinal);
        Assert.Contains("still blocks changes to its own files", ObserveModeCopy.BatchObserveNote(true), StringComparison.Ordinal);
        Assert.Contains("changes to DefenseClaw's own files", ObserveModeCopy.ObserveFailClosedNote(true), StringComparison.Ordinal);
    }

    [Fact]
    public void From_1_0_5_fail_open_no_longer_claims_a_gateway_can_never_block_an_agent()
    {
        var note = ObserveModeCopy.FailOpenNote(true);

        Assert.DoesNotContain("never blocks", note, StringComparison.Ordinal);
        Assert.Contains("did not answer in time is still blocked", note, StringComparison.Ordinal);
        Assert.Contains("Recommended for almost all installs.", note, StringComparison.Ordinal);
    }
}
