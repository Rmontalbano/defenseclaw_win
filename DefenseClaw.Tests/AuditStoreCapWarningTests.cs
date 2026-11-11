using System.Text.Json;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.Tests;

/// <summary>
/// The audit store's size-cap warning of runtime 1.0.7 (<c>/health</c> <c>telemetry.details.retention_state</c> <c>warning</c> with <c>retention_cap_*</c>), synthetic
/// and checked against the CLI's sentence (v8_status.py). The reader is field-driven; the version gate is the Overview's.
/// </summary>
public sealed class AuditStoreCapWarningTests
{
    private const string Used = "70464307";
    private const string Cap = "67108864";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static RetentionHealth Read(string state, string used, string cap, string reason) =>
        DestinationHealthReader.ReadRetention(Json(
            "{\"retention_state\":\"" + state + "\",\"retention_days\":7" +
            (used.Length > 0 ? ",\"retention_cap_used_bytes\":" + used : string.Empty) +
            (cap.Length > 0 ? ",\"retention_cap_bytes\":" + cap : string.Empty) +
            (reason.Length > 0 ? ",\"retention_cap_reason\":\"" + reason + "\"" : string.Empty) + "}"));

    [Fact]
    public void An_active_correlation_warning_reads_in_the_clis_words()
    {
        var read = Read("warning", Used, Cap, "active_correlation_state");

        Assert.True(read.IsCapWarning);
        Assert.Equal(
            "audit store cap not reached: 67.2 MiB used, cap 64.0 MiB; active correlation state is retained; end unused sessions or raise observability.local.max_store_mb",
            read.CapWarningSentence);
        Assert.Equal("warning", read.ControllerLabel);
    }

    [Fact]
    public void A_retained_history_warning_reads_in_the_clis_words()
    {
        var read = Read("warning", Used, Cap, "retained_history_or_pending_work");

        Assert.Equal(
            "audit store cap not reached: 67.2 MiB used, cap 64.0 MiB; recent history or pending retention work remains; allow another pass or raise observability.local.max_store_mb",
            read.CapWarningSentence);
    }

    [Theory]
    [InlineData("warning", Used, Cap, "something_new")]
    [InlineData("warning", Used, Cap, "")]
    [InlineData("warning", "", Cap, "active_correlation_state")]
    [InlineData("warning", Used, "", "active_correlation_state")]
    [InlineData("warning", "1000", "2000", "active_correlation_state")]
    [InlineData("warning", "2000", "2000", "active_correlation_state")]
    [InlineData("warning", "2000", "0", "active_correlation_state")]
    [InlineData("warning", "9007199254740993", Cap, "active_correlation_state")]
    [InlineData("warning", "2199023255553", "1099511627777", "active_correlation_state")]
    public void Numbers_or_a_reason_that_cannot_be_trusted_give_the_plain_sentence(string state, string used, string cap, string reason) =>
        Assert.Equal("audit store cap not reached; inspect the gateway retention health", Read(state, used, cap, reason).CapWarningSentence);

    [Fact]
    public void The_cap_keys_are_ignored_unless_the_state_is_warning()
    {
        var read = Read("healthy", Used, Cap, "active_correlation_state");

        Assert.False(read.IsCapWarning);
        Assert.Null(read.CapUsedBytes);
        Assert.Null(read.CapBytes);
        Assert.Equal(string.Empty, read.CapReason);
        Assert.Equal(string.Empty, read.CapWarningSentence);
    }

    [Fact]
    public void A_warning_is_not_a_degraded_reaper()
    {
        Assert.False(Read("warning", Used, Cap, "active_correlation_state").NeedsAttention);
        Assert.True(Read("degraded", string.Empty, string.Empty, string.Empty).NeedsAttention);
        Assert.True(Read("stopped", string.Empty, string.Empty, string.Empty).NeedsAttention);
    }

    [Fact]
    public void A_record_made_the_old_way_still_works()
    {
        var old = new RetentionHealth("healthy", string.Empty, 7, 64);

        Assert.False(old.IsCapWarning);
        Assert.Equal(string.Empty, old.CapWarningSentence);
        Assert.Equal("healthy", old.ControllerLabel);
    }
}
