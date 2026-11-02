using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// What DefenseClaw 1.0.6 adds to <c>/health</c> and config.yaml that the app now reads (CUST-426), synthetic and verified from source only: the
/// version-judged <see cref="RuntimeCapability.OpenCodeV2Only"/> (opencode_v2.go), the judge-fallback details of <c>guardrail.details</c>
/// (judge_health.go <c>addFallbackDetailsLocked</c>; the CLI's words are cmd_status.py <c>judge_fallback_summary</c>), and the audit size cap
/// (<c>retention_max_store_mb</c>, health.go:1732; <c>observability.local.max_store_mb</c>).
/// </summary>
public sealed class JudgeFallbackAndSizeCapTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeSnapshot Probe(string version, string? help = Help) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", help, help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    private static ServiceState Block(string detailsJson) =>
        JsonSerializer.Deserialize<ServiceState>("{\"state\":\"running\",\"details\":" + detailsJson + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------ OpenCodeV2Only

    [Theory]
    [InlineData("1.0.6", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.5", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_opencode_capability_follows_the_reported_version(string version, bool expected) =>
        Assert.Equal(expected, Probe(version).Capabilities.Has(RuntimeCapability.OpenCodeV2Only));

    [Fact]
    public void A_runtime_that_has_not_answered_or_cannot_list_its_commands_does_not_have_it()
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.OpenCodeV2Only));
        Assert.False(Probe("1.0.6", help: null).Capabilities.Has(RuntimeCapability.OpenCodeV2Only));
    }

    [Fact]
    public void About_names_and_explains_the_opencode_capability()
    {
        Assert.Contains("1.0.6", RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.OpenCodeV2Only), StringComparison.Ordinal);
        Assert.Contains("1.0.6", RuntimeCapabilityCatalog.Marker(RuntimeCapability.OpenCodeV2Only), StringComparison.Ordinal);
        Assert.Contains(RuntimeCapability.OpenCodeV2Only, RuntimeCapabilityCatalog.All);
    }

    // ------------------------------------------------------------------ judge fallbacks

    [Fact]
    public void A_1_0_6_block_reads_the_total_the_connectors_in_order_and_the_last_one()
    {
        var read = JudgeFallbackHealth.From(Block(
            "{\"judge_state\":\"ok\",\"judge_fallbacks\":12,\"judge_fallbacks_by_connector\":{\"codex\":9,\"claudecode\":3}," +
            "\"judge_last_fallback\":\"timeout (codex)\",\"judge_last_fallback_at\":\"2030-01-15T09:17:58Z\"}"));

        Assert.NotNull(read);
        Assert.Equal(12, read.Total);
        Assert.Equal(["claudecode", "codex"], read.ByConnector.Select(static pair => pair.Key));
        Assert.Equal("timeout (codex)", read.Last);
        Assert.Equal(
            "the rules decided 12 hook check(s) because the judge gave no verdict in time (claudecode 3, codex 9); last: timeout (codex)",
            read.Summary);
    }

    [Fact]
    public void The_summary_leaves_out_what_the_gateway_did_not_send()
    {
        var read = JudgeFallbackHealth.From(Block("{\"judge_fallbacks\":1}"));

        Assert.Equal("the rules decided 1 hook check(s) because the judge gave no verdict in time", read!.Summary);
        Assert.Empty(read.ByConnector);
    }

    [Fact]
    public void A_whole_number_written_as_a_float_counts()
    {
        Assert.Equal(4, JudgeFallbackHealth.From(Block("{\"judge_fallbacks\":4.0}"))!.Total);
    }

    [Theory]
    [InlineData("{\"judge_state\":\"ok\",\"judge_recent_calls\":3}")]
    [InlineData("{\"judge_fallbacks\":0}")]
    [InlineData("{\"judge_fallbacks\":-2}")]
    [InlineData("{\"judge_fallbacks\":2.5}")]
    [InlineData("{\"judge_fallbacks\":\"12\"}")]
    [InlineData("{\"judge_fallbacks\":null}")]
    [InlineData("{\"judge_fallbacks\":true}")]
    [InlineData("{\"judge_fallbacks\":{\"codex\":9}}")]
    [InlineData("[]")]
    [InlineData("\"judge_fallbacks\"")]
    [InlineData("null")]
    public void Absent_zero_or_malformed_counts_are_no_fallbacks(string details) =>
        Assert.Null(JudgeFallbackHealth.From(Block(details)));

    [Fact]
    public void No_block_or_no_details_is_no_fallbacks()
    {
        Assert.Null(JudgeFallbackHealth.From(null));
        Assert.Null(JudgeFallbackHealth.From(JsonSerializer.Deserialize<ServiceState>("{\"state\":\"running\"}")));
    }

    [Fact]
    public void Malformed_companions_are_dropped_and_the_count_still_stands()
    {
        var read = JudgeFallbackHealth.From(Block(
            "{\"judge_fallbacks\":5,\"judge_fallbacks_by_connector\":{\"codex\":\"many\",\"claudecode\":2,\"neg\":-1,\"\":4}," +
            "\"judge_last_fallback\":{\"reason\":\"timeout\"}}"));

        Assert.Equal(5, read!.Total);
        Assert.Equal([new KeyValuePair<string, long>("claudecode", 2)], read.ByConnector);
        Assert.Equal(string.Empty, read.Last);

        var notAMap = JudgeFallbackHealth.From(Block("{\"judge_fallbacks\":5,\"judge_fallbacks_by_connector\":[1,2],\"judge_last_fallback\":7}"));
        Assert.Equal(5, notAMap!.Total);
        Assert.Empty(notAMap.ByConnector);
        Assert.Equal(string.Empty, notAMap.Last);
    }

    [Fact]
    public void Words_the_gateway_wrote_are_cut_and_made_fit_to_draw()
    {
        var long160 = new string('x', 400);
        var read = JudgeFallbackHealth.From(Block(
            "{\"judge_fallbacks\":2,\"judge_fallbacks_by_connector\":{\"" + new string('c', 100) + "\":1},\"judge_last_fallback\":\"" + long160 + "\\u202e\"}"));

        Assert.True(read!.ByConnector[0].Key.Length <= 40);
        Assert.True(read.Last.Length <= 160);
        Assert.DoesNotContain('\u202e', read.Summary);

        var bidi = JudgeFallbackHealth.From(Block("{\"judge_fallbacks\":2,\"judge_last_fallback\":\"timeout\\u202e (codex)\"}"));
        Assert.DoesNotContain('\u202e', bidi!.Last);
    }

    [Fact]
    public void A_huge_connector_map_is_bounded()
    {
        var entries = string.Join(",", Enumerable.Range(0, 200).Select(static i => $"\"c{i:D3}\":1"));
        var read = JudgeFallbackHealth.From(Block("{\"judge_fallbacks\":200,\"judge_fallbacks_by_connector\":{" + entries + "}}"));

        Assert.True(read!.ByConnector.Count <= 33);
    }

    // ------------------------------------------------------------------ audit size cap: the gateway

    [Fact]
    public void The_gateway_reports_the_cap_beside_the_window()
    {
        var read = DestinationHealthReader.ReadRetention(Json("{\"retention_state\":\"healthy\",\"retention_days\":7,\"retention_max_store_mb\":512}"));

        Assert.Equal(512, read.MaxStoreMb);
        Assert.Equal(7, read.Days);
        Assert.Equal("healthy", read.State);
        Assert.Equal("size cap 512 MiB", RetentionHealth.SizeCap(512));
    }

    [Theory]
    [InlineData("{\"retention_days\":7}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":0}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":-5}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":\"512\"}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":512.5}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":null}")]
    [InlineData("{\"retention_days\":7,\"retention_max_store_mb\":[512]}")]
    [InlineData("[]")]
    public void No_cap_or_a_malformed_one_is_no_cap(string details) =>
        Assert.Null(DestinationHealthReader.ReadRetention(Json(details)).MaxStoreMb);

    [Fact]
    public void A_cap_beside_an_unknown_reaper_state_is_still_read()
    {
        var read = DestinationHealthReader.ReadRetention(Json("{\"retention_state\":\"weird\",\"retention_days\":0,\"retention_max_store_mb\":64}"));

        Assert.Equal(string.Empty, read.State);
        Assert.Equal(64, read.MaxStoreMb);
    }

    // ------------------------------------------------------------------ audit size cap: config.yaml

    private static ObservabilityConfigFacts Facts(string cap) =>
        ObservabilityConfigFacts.FromYaml("observability:\n  local:\n    retention_days: 7\n" + (cap.Length == 0 ? string.Empty : "    max_store_mb: " + cap + "\n"));

    [Theory]
    [InlineData("64", 64)]
    [InlineData("512", 512)]
    [InlineData("1048576", 1048576)]
    [InlineData("'256'", 256)]
    public void A_cap_in_the_runtimes_range_is_read(string written, long expected) => Assert.Equal(expected, Facts(written).MaxStoreMb);

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("63")]
    [InlineData("1048577")]
    [InlineData("-64")]
    [InlineData("12.5")]
    [InlineData("lots")]
    [InlineData("[64]")]
    [InlineData("null")]
    public void No_cap_or_one_the_runtime_would_refuse_is_no_cap(string written) => Assert.Null(Facts(written).MaxStoreMb);

    [Fact]
    public void The_cap_does_not_disturb_the_other_facts_and_crlf_reads_the_same()
    {
        var yaml = "observability:\r\n  local:\r\n    retention_days: 30\r\n    max_store_mb: 128\r\n    path: audit.db\r\n";
        var facts = ObservabilityConfigFacts.FromYaml(yaml);

        Assert.Equal(128, facts.MaxStoreMb);
        Assert.Equal(30, facts.RetentionDays);
        Assert.Equal("audit.db", facts.LocalPath);
        Assert.Null(ObservabilityConfigFacts.Empty.MaxStoreMb);
        Assert.Null(ObservabilityConfigFacts.FromYaml("not: [closed").MaxStoreMb);
    }
}
