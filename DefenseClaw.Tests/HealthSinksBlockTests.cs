using System.Text.Json;
using System.Text.Json.Nodes;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>/health</c> with a <c>sinks</c> block (CUST-313: the Overview's Sinks card). <c>runtime-0.8.10/rest/health.sinks.synthetic.json</c> is the live 0.8.10
/// capture (<c>health-0.8.10.json</c>) plus the one block it lacks, in the shape the 0.8.10 TUI's reader takes for every block
/// (<c>_subsystem_from_mapping</c>: state, since, last_error, details) and the Observability card already reads (<c>details.sinks[]</c>). Neither runtime emits
/// the block today, so this is what a gateway that did would send.
/// <para>
/// The health model deliberately has no typed <c>sinks</c>: a typed block of the wrong shape (a number for <c>state</c>) would fail the whole <c>/health</c>
/// parse, and whatever answers on the gateway port decides what that says. The block stays in the extension bag and the Overview parses it on its own,
/// where a block it cannot read is a card that says "not reported".
/// </para>
/// </summary>
public class HealthSinksBlockTests
{
    private const string Fixture = "runtime-0.8.10/rest/health.sinks.synthetic.json";

    private static GatewayClient ClientFor(string body)
    {
        var handler = new FakeHttpMessageHandler().Map("/health", body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        return new GatewayClient(http, () => new SecretValue("fixture-bearer-token-0123456789"), ownsHttpClient: true);
    }

    [Fact]
    public async Task The_0_8_10_payload_with_a_sinks_block_parses_and_keeps_the_block_untyped()
    {
        var result = await ClientFor(FixtureFiles.ReadText(Fixture)).GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var health = result.Value!;
        Assert.Equal("0.8.10", health.Provenance!.BinaryVersion);
        Assert.True(health.Api!.IsRunning);
        Assert.True(health.FleetUplink!.IsDisabled);
        Assert.Single(health.Connectors);

        // The extension bag holds it; the typed list of subsystems is the same eight blocks as before.
        Assert.NotNull(health.AdditionalData);
        var sinks = health.AdditionalData!["sinks"];
        Assert.Equal(JsonValueKind.Object, sinks.ValueKind);
        Assert.Equal("running", sinks.GetProperty("state").GetString());
        Assert.Equal("1 of 1 enabled", sinks.GetProperty("details").GetProperty("summary").GetString());
        Assert.Equal("audit-file", sinks.GetProperty("details").GetProperty("sinks")[0].GetProperty("name").GetString());
        Assert.Equal(8, health.Services().Count());
    }

    [Fact]
    public void The_fixture_is_the_capture_and_one_block_more()
    {
        var capture = JsonNode.Parse(FixtureFiles.ReadText("health-0.8.10.json"))!.AsObject();
        var synthetic = JsonNode.Parse(FixtureFiles.ReadText(Fixture))!.AsObject();

        // The capture's gateway.details.scope holds a mojibake em dash (UTF-8 read as cp1252); the fixture has the real one. Everything else is as captured.
        _ = capture["gateway"]!["details"]!.AsObject().Remove("scope");
        _ = synthetic["gateway"]!["details"]!.AsObject().Remove("scope");

        Assert.NotNull(synthetic["sinks"]);
        _ = synthetic.Remove("sinks");

        Assert.True(JsonNode.DeepEquals(capture, synthetic), "the fixture drifted from the capture it was made from");
    }

    [Theory]
    [InlineData("{\"uptime_ms\":1,\"sinks\":{\"state\":5}}")]
    [InlineData("{\"uptime_ms\":1,\"sinks\":{\"since\":\"not-a-date\"}}")]
    [InlineData("{\"uptime_ms\":1,\"sinks\":[1,2,3]}")]
    [InlineData("{\"uptime_ms\":1,\"sinks\":null}")]
    public async Task A_sinks_block_of_the_wrong_shape_does_not_fail_the_health_parse(string body)
    {
        var result = await ClientFor(body).GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        Assert.Equal(1, result.Value!.UptimeMs);
        Assert.Contains("sinks", result.Value.AdditionalData!.Keys);
    }
}
