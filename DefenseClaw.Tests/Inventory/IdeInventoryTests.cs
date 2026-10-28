using System.Net;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.Correlation;

namespace DefenseClaw.Tests.Inventory;

/// <summary>
/// The IDE plugin inventory reader and its gateway read (CUST-376): the shape of <c>GET /api/v1/ai-usage/ide-plugins</c> in 1.0.x
/// (<c>handleAIUsageIDEPlugins</c>, <c>inventory.IDEPlugin</c>), the CLI's own cell wording, paging, and what each failure becomes. The fixtures are
/// synthetic; the handlers are in memory and nothing opens a socket.
/// </summary>
public sealed class IdeInventoryTests
{
    private static IdePluginPage Page(string fixture)
    {
        var read = IdeInventoryReader.ParseText(CorrelationFixtures.Read(fixture));
        Assert.True(read.IsOk, read.Message);
        return read.Value!;
    }

    private static (IdeInventoryService Service, CorrelationFixtures.FakeGateway Handler) Build()
    {
        var (client, handler) = CorrelationFixtures.Gateway();
        return (new IdeInventoryService((path, max, ct) => client.GetBoundedJsonAsync(path, max, true, ct)), handler);
    }

    // ------------------------------------------------------------------ the reader

    [Fact]
    public void A_page_is_read_with_each_plugin_joined_to_its_installation()
    {
        var page = Page("ide-plugins.synthetic.json");

        Assert.True(page.Enabled);
        Assert.Equal("all", page.Scope);
        Assert.Equal(6, page.Rows.Count);
        Assert.Equal(3, page.Installations.Count);
        Assert.Equal(new IdeInventoryCounts(6, 2, 1, 1, 3), page.Counts);
        Assert.NotNull(page.ScannedAt);

        var assistant = page.Rows.Single(r => r.Fingerprint == "fp-1");
        Assert.Equal("1.99.0", assistant.IdeVersion);
        Assert.Equal("vscode 1.99.0", assistant.IdeLabel);
        Assert.Equal("example.assistant (Example Assistant)", assistant.PluginLabel);
        Assert.Equal("yes", assistant.EnabledLabel);
        Assert.True(assistant.IsAi);
        Assert.Equal("sig-example-assistant", assistant.AiSignatureId);
        Assert.NotNull(assistant.InstalledAt);
    }

    [Fact]
    public void The_cells_are_worded_as_the_cli_words_them()
    {
        var page = Page("ide-plugins.synthetic.json");

        // A display name equal to the id is not repeated; a missing one leaves the id alone.
        Assert.Equal("example.formatter", page.Rows.Single(r => r.Fingerprint == "fp-2").PluginLabel);
        Assert.Equal("example.linter", page.Rows.Single(r => r.Fingerprint == "fp-3").PluginLabel);

        Assert.Equal("no", page.Rows.Single(r => r.Fingerprint == "fp-3").EnabledLabel);
        var remote = page.Rows.Single(r => r.Fingerprint == "fp-4");
        Assert.Equal("client side", remote.EnabledLabel);
        Assert.True(remote.IsUnknown);
        Assert.Equal("pycharm 2025.2 (remote dev)", remote.IdeLabel);
        Assert.Equal("unknown", page.Rows.Single(r => r.Fingerprint == "fp-5").EnabledLabel);
        Assert.True(page.Installations.Single(i => i.InstallId == "inst-cursor-1").Partial);
    }

    [Theory]
    [InlineData("""{"plugins": []}""")]
    [InlineData("""{"plugins": null}""")]
    public void An_empty_inventory_is_ok_and_empty(string json)
    {
        var read = IdeInventoryReader.ParseText(json);

        Assert.True(read.IsOk);
        Assert.Empty(read.Value!.Rows);
    }

    [Theory]
    [InlineData("""{"installations": []}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    public void An_answer_without_plugins_is_malformed_and_never_an_empty_inventory(string json)
    {
        var read = IdeInventoryReader.ParseText(json);

        Assert.Equal(CorrelationReadStatus.Malformed, read.Status);
        Assert.Null(read.Value);
    }

    [Fact]
    public void A_row_without_an_id_or_a_name_is_skipped_and_counted()
    {
        var read = IdeInventoryReader.ParseText("""{"plugins": [{"version": "1"}, {"plugin_id": "example.ok"}, 7]}""");

        Assert.True(read.IsOk);
        Assert.Equal(["example.ok"], read.Value!.Rows.Select(r => r.PluginId).ToArray());
        Assert.Equal(2, read.Value.Skipped);
    }

    [Fact]
    public void A_gateway_with_discovery_off_and_one_with_the_inventory_off_say_so()
    {
        var disabled = Page("ide-plugins.disabled.synthetic.json");
        var off = Page("ide-plugins.off.synthetic.json");

        Assert.False(disabled.Enabled);
        Assert.Contains("disabled", disabled.Reason, StringComparison.Ordinal);
        Assert.True(off.Enabled);
        Assert.True(off.IsOff);
    }

    [Fact]
    public void The_path_asks_for_a_bounded_page_and_encodes_the_cursor()
    {
        Assert.Equal("api/v1/ai-usage/ide-plugins?limit=1000", IdeInventoryRoutes.Page(5000, null));
        Assert.Equal("api/v1/ai-usage/ide-plugins?limit=1", IdeInventoryRoutes.Page(0, string.Empty));
        Assert.Equal("api/v1/ai-usage/ide-plugins?limit=500&cursor=2.gen%26x", IdeInventoryRoutes.Page(500, "2.gen&x"));
    }

    // ------------------------------------------------------------------ the read

    [Fact]
    public async Task The_read_is_one_authenticated_get_of_the_documented_route()
    {
        var (service, handler) = Build();
        handler.Override = r => CorrelationFixtures.Json(CorrelationFixtures.Read("ide-plugins.synthetic.json"));

        var read = await service.ReadAsync(CancellationToken.None);

        Assert.True(read.IsOk, read.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api/v1/ai-usage/ide-plugins?limit=1000", request.RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal("Bearer " + CorrelationFixtures.Token, request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Pages_are_followed_by_cursor_and_installations_are_joined_across_them()
    {
        var (service, handler) = Build();
        handler.Override = r => CorrelationFixtures.Json(CorrelationFixtures.Read(
            r.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal) ? "ide-plugins.page2.synthetic.json" : "ide-plugins.page1.synthetic.json"));

        var read = await service.ReadAsync(CancellationToken.None);

        Assert.True(read.IsOk, read.Message);
        Assert.Equal(["example.assistant", "example.formatter"], read.Value!.Rows.Select(r => r.PluginId).Order().ToArray());
        Assert.All(read.Value.Rows, r => Assert.Equal("1.99.0", r.IdeVersion));
        Assert.Single(read.Value.Installations);
        Assert.Equal(string.Empty, read.Value.NextCursor);
        Assert.Equal(
            ["api/v1/ai-usage/ide-plugins?limit=1000", "api/v1/ai-usage/ide-plugins?limit=1000&cursor=1.gen-a"],
            handler.Requests.Select(r => r.RequestUri!.PathAndQuery.TrimStart('/')).ToArray());
    }

    [Fact]
    public async Task A_stale_cursor_on_a_later_page_keeps_what_was_read_and_says_it_is_partial()
    {
        var (service, handler) = Build();
        handler.Override = r => r.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal)
            ? CorrelationFixtures.Json("""{"error":"the IDE plugin inventory changed since the previous page"}""", HttpStatusCode.Conflict)
            : CorrelationFixtures.Json(CorrelationFixtures.Read("ide-plugins.page1.synthetic.json"));

        var read = await service.ReadAsync(CancellationToken.None);

        Assert.True(read.IsOk);
        Assert.Single(read.Value!.Rows);
        Assert.StartsWith("The list is partial", read.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CorrelationReadStatus.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CorrelationReadStatus.Unsupported)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CorrelationReadStatus.NotReady)]
    [InlineData(HttpStatusCode.InternalServerError, CorrelationReadStatus.Failed)]
    public async Task A_gateway_without_the_route_is_unsupported_and_other_failures_are_not_an_empty_list(HttpStatusCode code, CorrelationReadStatus expected)
    {
        var (service, handler) = Build();
        handler.Override = r => CorrelationFixtures.Json("""{"error":"x"}""", code);

        var read = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(expected, read.Status);
        Assert.Null(read.Value);
    }

    // ------------------------------------------------------------------ the capability

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.4", true)]
    [InlineData("0.8.10", false)]
    public void The_capability_follows_the_version_like_the_agent_view(string version, bool expected)
    {
        var help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
        var snapshot = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", help, help, null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, snapshot.Capabilities.Has(RuntimeCapability.IdeInventory));
        Assert.Equal(expected, RuntimeGate.Check(snapshot.Capabilities, RuntimeCapability.IdeInventory).IsAvailable);
        Assert.False(RuntimeGate.Check(RuntimeCapabilities.Unknown, RuntimeCapability.IdeInventory).IsAvailable);
        Assert.Contains("1.0.0", RuntimeCapabilityCatalog.Marker(RuntimeCapability.IdeInventory), StringComparison.Ordinal);
    }
}
