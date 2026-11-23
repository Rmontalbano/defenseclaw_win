using System.Net;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The two reads behind the Overview's "Model routing" and "Tokenomics" rows (CUST-457), on synthetic bodies shaped from DefenseClaw 1.0.10's source:
/// <c>/health</c>'s <c>routing</c> block (<c>sidecar.go</c> effectiveRoutingHealthDetails, <c>routeonly_ready.go</c> routeOnlyReadiness) and the bounded
/// <c>GET /usage?by=model&amp;since=24h</c> (<c>api_usage.go</c> handleUsage, <c>modelusage/store.go</c> Row). Handlers are in memory.
/// </summary>
public class ModelUsageAndRoutingHealthTests
{
    private const string Token = "fixture-bearer-token-0123456789";

    private static GatewayHealth Health(string routing) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":1000,\"api\":{\"state\":\"running\"}" + (routing.Length > 0 ? ",\"routing\":" + routing : string.Empty) + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private const string Since = "\"since\":\"2026-11-20T10:00:00Z\"";

    // ------------------------------------------------------------------ routing block

    [Fact]
    public void A_default_install_reads_disabled_and_is_not_a_problem()
    {
        var health = Health("{\"state\":\"disabled\"," + Since + ",\"details\":{\"enabled\":false}}");

        var routing = RoutingHealth.From(health)!;

        Assert.True(routing.IsOff);
        Assert.False(routing.ReportsProblem);
        Assert.Equal("disabled", routing.Text);
        Assert.Empty(routing.RouteOnly);
    }

    [Fact]
    public void The_routing_key_stays_in_the_extension_bag_and_the_typed_view_reads_it()
    {
        var health = Health("{\"state\":\"disabled\",\"details\":{\"enabled\":false}}");

        Assert.Contains("routing", health.AdditionalData!.Keys);
        Assert.NotNull(health.ModelRoutingBlock);
    }

    [Fact]
    public void A_health_without_routing_has_no_block()
    {
        var health = Health(string.Empty);

        Assert.Null(health.ModelRoutingBlock);
        Assert.Null(RoutingHealth.From(health));
    }

    [Theory]
    [InlineData("\"disabled\"")]
    [InlineData("[1,2]")]
    [InlineData("12")]
    [InlineData("{\"state\":7,\"since\":\"not a date\"}")]
    public void A_routing_block_of_the_wrong_shape_never_fails_the_health_parse(string routing)
    {
        var health = Health(routing);

        Assert.Equal(1000, health.UptimeMs);
        Assert.Null(RoutingHealth.From(health)?.RouteOnly.FirstOrDefault());
    }

    [Fact]
    public void Rules_running_with_two_models_reads_state_classifier_and_count()
    {
        var health = Health("{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"mode\":\"managed\",\"version\":\"0.3.0\",\"model_count\":2,\"port\":8080}}");

        var routing = RoutingHealth.From(health)!;

        Assert.False(routing.IsOff);
        Assert.False(routing.ReportsProblem);
        Assert.Equal("running · rules · 2 models", routing.Text);
    }

    [Fact]
    public void A_1_0_9_shaped_block_without_classifier_or_route_only_gives_the_row_without_those_parts()
    {
        var health = Health("{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"mode\":\"managed\",\"version\":\"0.3.0\",\"model_count\":1,\"port\":8080}}");

        var routing = RoutingHealth.From(health)!;

        Assert.Equal("running · 1 model", routing.Text);
        Assert.Empty(routing.RouteOnly);
    }

    [Fact]
    public void A_vllm_sr_error_is_a_problem_and_its_last_error_is_one_bounded_line()
    {
        var longError = "router container exited\nwith code 3 " + new string('x', 500);
        var health = Health("{\"state\":\"error\",\"last_error\":" + JsonSerializer.Serialize(longError) + "," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"vllm_sr\",\"mode\":\"managed\",\"model_count\":0}}");

        var routing = RoutingHealth.From(health)!;

        Assert.True(routing.ReportsProblem);
        Assert.StartsWith("error · vllm_sr · 0 models · router container exited", routing.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', routing.Text);
        Assert.EndsWith("…", routing.Text, StringComparison.Ordinal);
        Assert.True(routing.LastError.Length <= RoutingHealth.MaxTextLength + 1);
    }

    [Fact]
    public void A_route_only_connector_that_is_not_ready_carries_the_cause_problem_and_fix_bounded()
    {
        var health = Health(
            "{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":1,\"route_only\":[" +
            "{\"connector\":\"codex\",\"custody\":false,\"credential\":\"agent\",\"ready\":false,\"cause\":\"upstream_not_set\"," +
            "\"problem\":\"routing.route_only.upstreams.codex is not set\",\"fix\":\"set routing.route_only.upstreams.codex (base_url and api) in config.yaml\"}," +
            "{\"connector\":\"claudecode\",\"api\":\"anthropic\",\"credential\":\"agent\",\"ready\":true}," +
            "{\"connector\":\"codex\",\"ready\":true}," +
            "\"not an object\"]}}");

        var routing = RoutingHealth.From(health)!;

        Assert.Equal(2, routing.RouteOnly.Count);
        var codex = routing.RouteOnly[0];
        Assert.Equal("codex", codex.Connector);
        Assert.False(codex.Ready);
        Assert.Equal("not ready (upstream_not_set). routing.route_only.upstreams.codex is not set. Fix: set routing.route_only.upstreams.codex (base_url and api) in config.yaml", codex.Text);
        Assert.Equal("ready", routing.RouteOnly[1].Text);
    }

    [Fact]
    public void A_ready_flag_that_is_missing_or_not_a_boolean_is_not_ready()
    {
        var health = Health("{\"state\":\"running\",\"details\":{\"enabled\":true,\"route_only\":[{\"connector\":\"codex\"},{\"connector\":\"claudecode\",\"ready\":\"yes\"}]}}");

        Assert.All(RoutingHealth.From(health)!.RouteOnly, static c => Assert.False(c.Ready));
    }

    [Fact]
    public void Route_only_rows_are_capped()
    {
        var items = string.Join(",", Enumerable.Range(0, 30).Select(static i => "{\"connector\":\"c" + i + "\",\"ready\":true}"));
        var health = Health("{\"state\":\"running\",\"details\":{\"enabled\":true,\"route_only\":[" + items + "]}}");

        Assert.Equal(RoutingHealth.MaxRouteOnly, RoutingHealth.From(health)!.RouteOnly.Count);
    }

    // ------------------------------------------------------------------ usage

    [Fact]
    public void The_store_off_body_is_off_not_an_error()
    {
        var read = ModelUsageReader.ParseText("{\"enabled\":false,\"hint\":\"set observability.tokenomics.enabled and observability.tokenomics.local_store.enabled to true in config.yaml\",\"rows\":[]}");

        Assert.Equal(ModelUsageStatus.StoreOff, read.Status);
    }

    [Fact]
    public void An_on_body_with_no_rows_says_no_usage()
    {
        var read = ModelUsageReader.ParseText("{\"enabled\":true,\"by\":\"model\",\"scope\":\"own\",\"from\":\"2026-11-19T10:00:00Z\",\"to\":\"2026-11-20T10:00:00Z\",\"rows\":[]}");

        Assert.Equal(ModelUsageStatus.Recorded, read.Status);
        Assert.Equal("no usage in the last 24 h", ModelUsageReader.Describe(read));
    }

    [Fact]
    public void Rows_without_cost_usd_have_no_price_and_the_cost_is_never_zero()
    {
        var read = ModelUsageReader.ParseText(
            "{\"enabled\":true,\"by\":\"model\",\"rows\":[" +
            "{\"key\":\"other\",\"calls\":5,\"usage_calls\":5,\"input_tokens\":100,\"output_tokens\":20,\"cache_read_tokens\":0,\"cache_creation_tokens\":0,\"reasoning_tokens\":0,\"cost_calls\":0,\"latency_ms_sum\":10}," +
            "{\"key\":\"other\",\"calls\":2,\"errors\":1,\"usage_calls\":1,\"input_tokens\":1,\"output_tokens\":1,\"cache_read_tokens\":0,\"cache_creation_tokens\":0,\"reasoning_tokens\":0,\"cost_calls\":0,\"latency_ms_sum\":1}]}");

        Assert.Equal(7, read.Calls);
        Assert.Null(read.CostUsd);
        Assert.Equal(0, read.PricedCalls);
        Assert.Equal("7 calls in the last 24 h, no price table for these models, so no cost", ModelUsageReader.Describe(read));
    }

    [Fact]
    public void Priced_and_unpriced_rows_sum_the_cost_and_say_how_many_calls_were_priced()
    {
        var read = ModelUsageReader.ParseText(
            "{\"enabled\":true,\"by\":\"model\",\"rows\":[" +
            "{\"key\":\"model-a\",\"calls\":9,\"usage_calls\":9,\"input_tokens\":1,\"output_tokens\":1,\"cost_usd\":0.0412,\"cost_calls\":8,\"latency_ms_sum\":1}," +
            "{\"key\":\"other\",\"calls\":3,\"usage_calls\":3,\"input_tokens\":1,\"output_tokens\":1,\"cost_calls\":0,\"latency_ms_sum\":1}]}");

        Assert.Equal(12, read.Calls);
        Assert.Equal(8, read.PricedCalls);
        Assert.Equal(0.0412, read.CostUsd!.Value, 6);
        Assert.Equal("12 calls in the last 24 h, $0.04 (8 of 12 calls priced)", ModelUsageReader.Describe(read));
    }

    [Fact]
    public void A_tiny_cost_is_not_shown_as_zero_and_one_call_is_singular()
    {
        var read = ModelUsageReader.ParseText("{\"enabled\":true,\"rows\":[{\"key\":\"model-a\",\"calls\":1,\"cost_usd\":0.0004,\"cost_calls\":1}]}");

        Assert.Equal("1 call in the last 24 h, under $0.01 (1 of 1 calls priced)", ModelUsageReader.Describe(read));
    }

    [Fact]
    public void A_full_page_of_rows_says_at_least()
    {
        var rows = string.Join(",", Enumerable.Range(0, ModelUsageReader.RowLimit).Select(static i => "{\"key\":\"m" + i + "\",\"calls\":1}"));
        var read = ModelUsageReader.ParseText("{\"enabled\":true,\"rows\":[" + rows + "]}");

        Assert.StartsWith("at least 100 calls", ModelUsageReader.Describe(read), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"rows\":[]}")]
    [InlineData("{\"enabled\":\"yes\",\"rows\":[]}")]
    public void A_body_that_is_not_a_usage_report_is_unreadable(string body)
    {
        Assert.Equal(ModelUsageStatus.Unreadable, ModelUsageReader.ParseText(body).Status);
    }

    [Fact]
    public void Hostile_numbers_are_ignored_not_trusted()
    {
        var read = ModelUsageReader.ParseText(
            "{\"enabled\":true,\"rows\":[{\"key\":\"a\",\"calls\":-5,\"cost_usd\":-1,\"cost_calls\":99},{\"key\":\"b\",\"calls\":9223372036854775807},{\"key\":\"c\",\"calls\":9223372036854775807}]}");

        Assert.Equal(long.MaxValue, read.Calls);
        Assert.Null(read.CostUsd);
    }

    [Fact]
    public void The_request_is_one_bounded_authenticated_get_of_the_last_24_hours_by_model()
    {
        Assert.Equal(ModelUsageReader.Route, "usage?by=model&since=24h&limit=" + ModelUsageReader.RowLimit);
        Assert.InRange(ModelUsageReader.RowLimit, 1, 1000); // the store caps a limit at 1000 and defaults to 200 (store.go Run)
        Assert.Equal(64 * 1024, ModelUsageReader.MaxBytes);
    }

    private static (GatewayClient Client, List<HttpRequestMessage> Requests) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new Scripted(request =>
        {
            requests.Add(request);
            return respond(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/"), Timeout = TimeSpan.FromSeconds(30) };
        return (new GatewayClient(http, () => new SecretValue(Token), ownsHttpClient: true, verifyPeer: null), requests);
    }

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_client_sends_the_bearer_token_and_reads_the_answer()
    {
        var (client, requests) = Build(_ => Json("{\"enabled\":true,\"rows\":[{\"key\":\"model-a\",\"calls\":4,\"cost_usd\":0.5,\"cost_calls\":4}]}"));

        var result = await client.GetBoundedJsonAsync(ModelUsageReader.Route, ModelUsageReader.MaxBytes, requiresAuth: true);
        var read = ModelUsageReader.FromGateway(result);
        result.Value?.Dispose();

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/usage", request.RequestUri!.AbsolutePath);
        Assert.Equal("by=model&since=24h&limit=100", request.RequestUri.Query.TrimStart('?'));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal(ModelUsageStatus.Recorded, read.Status);
        Assert.Equal(4, read.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_or_failure_is_unreadable_not_an_exception(HttpStatusCode status)
    {
        var (client, _) = Build(_ => Json("the usage store could not be read", status));

        var result = await client.GetBoundedJsonAsync(ModelUsageReader.Route, ModelUsageReader.MaxBytes, requiresAuth: true);

        Assert.Equal(ModelUsageStatus.Unreadable, ModelUsageReader.FromGateway(result).Status);
    }

    [Fact]
    public async Task An_answer_past_the_byte_cap_is_unreadable()
    {
        var body = "{\"enabled\":true,\"rows\":[" + string.Join(",", Enumerable.Range(0, 3000).Select(static i => "{\"key\":\"m" + i + "\",\"calls\":1}")) + "]}";
        Assert.True(body.Length > ModelUsageReader.MaxBytes);
        var (client, _) = Build(_ => Json(body));

        var result = await client.GetBoundedJsonAsync(ModelUsageReader.Route, ModelUsageReader.MaxBytes, requiresAuth: true);

        Assert.Equal(ModelUsageStatus.Unreadable, ModelUsageReader.FromGateway(result).Status);
    }
}
