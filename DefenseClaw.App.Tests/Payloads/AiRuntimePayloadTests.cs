using System.Net;
using System.Text;
using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// The AI Discovery panel's runtime section reads <c>GET /api/v1/ai-usage/runtime</c>. Two facts about that route, checked
/// on 2026-09-29:
/// <list type="bullet">
/// <item>DefenseClaw 0.8.10 does not have it. The installed <c>defenseclaw-gateway.exe</c> (0.8.10, commit bf45995c7f)
/// has string entries for <c>/api/v1/ai-usage</c>, <c>…/components/</c>, <c>…/confidence/policy</c>, <c>…/discovery</c> and
/// <c>…/scan</c> and none for <c>…/runtime</c>, and the upstream repository (github.com/cisco-ai-defense/defenseclaw) first
/// registers it in <c>internal/gateway/api.go</c> with commit c97e652fc7 (2026-09-11, "absorb ShadowClaw as the AI Discovery
/// runtime planes"), which is 64 commits after the 0.8.10 tag; 0.8.10 is still the latest release. So on 0.8.10 the answer
/// is an HTTP 404. Captured live on 2026-09-29 with an authenticated GET (a read-only probe, token used in-process only):
/// /api/v1/ai-usage answered 200 and /api/v1/ai-usage/runtime answered 404 with the body <c>404 page not found</c> as
/// text/plain, byte for byte what an unknown route such as /api/v1/ai-usage/not-a-route answered.</item>
/// <item>Where it exists its reply is <c>aiRuntimeResponse</c>, <c>internal/gateway/ai_runtime_api.go:31-97</c> upstream:
/// enabled, scanned_at, findings[] (finding_id, pid, process, cmdline, user, agent_name, score, severity, signals[],
/// providers[{hostname, address, port, category, confidence, attribution_source}], correlation{verdict, reason, …},
/// first_seen, last_seen), planes[{plane, name, available, running, mechanism, reason}], the process and connection
/// counts, host_plane_observations, host_plane_gated, degraded and degraded_reasons[]. The service being off answers
/// <c>enabled: false</c> with empty arrays (lines 99-111), not an error. Severities are critical, high, medium, low, info
/// (internal/sensor/scoring/scoring.go:44-48); planes are a, b, c (internal/sensor/platform/plane.go:58-64).</item>
/// </list>
/// The tests below feed that 404 (and the other answers) through the real <see cref="GatewayClient"/> over a stub handler; they
/// never touch the live gateway or the API token. A populated reply could not be captured live, since 0.8.10 has no such
/// route, so the fixtures for it follow the Go struct alone.
/// </summary>
public sealed class AiRuntimePayloadTests : IDisposable
{
    private const string RuntimePath = "api/v1/ai-usage/runtime";

    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public AiRuntimePayloadTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private sealed class StubGateway : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubGateway(HttpStatusCode status, string body, string contentType = "application/json")
            : this(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) })
        {
        }

        public StubGateway(Func<HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_respond());
        }
    }

    /// <summary>The runtime section after the panel asked a gateway that answered <paramref name="handler"/>.</summary>
    private AiDiscoveryPanelViewModel Ask(StubGateway handler, Action<AiDiscoveryPanelViewModel>? then = null)
    {
        var vm = new AiDiscoveryPanelViewModel(_services);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        using var client = new GatewayClient(http, () => new SecretValue("test-only-token-0000"));

        var result = client.GetRawJsonAsync(RuntimePath, requiresAuth: true).GetAwaiter().GetResult();
        try
        {
            vm.ApplyRuntime(result);
        }
        finally
        {
            result.Value?.Dispose();
        }

        then?.Invoke(vm);
        return vm;
    }

    private AiDiscoveryPanelViewModel Answer(string fixture) => Ask(new StubGateway(HttpStatusCode.OK, PayloadFixtures.Read(fixture)));

    // ------------------------------------------------------------------ what 0.8.10 answers

    [Theory]
    [InlineData("404 page not found\n", "text/plain")]
    [InlineData("{\"error\":\"not found\"}", "application/json")]
    [InlineData("", "application/json")]
    public void A_404_is_the_not_supported_state_however_the_gateway_words_it(string body, string contentType)
    {
        StaThread.Run(() =>
        {
            var stub = new StubGateway(HttpStatusCode.NotFound, body, contentType);

            var vm = Ask(stub);

            Assert.Equal("Not supported", vm.RuntimeBadgeText);
            Assert.Equal("Neutral", vm.RuntimeBadgeKey);
            Assert.Equal("Runtime coverage is not supported by this DefenseClaw version", vm.RuntimeTitle);
            Assert.Contains("not a problem with this machine", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("absence of runtime findings says nothing about the host", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.False(vm.RuntimeHasSnapshot);
            Assert.Empty(vm.RuntimePlanes);
            Assert.Empty(vm.RuntimeFindings);

            // What was asked: a plain authenticated GET of the route.
            Assert.Equal(HttpMethod.Get, stub.Request!.Method);
            Assert.Equal("/api/v1/ai-usage/runtime", stub.Request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", stub.Request.Headers.Authorization!.Scheme);
        });
    }

    [Fact]
    public void A_405_is_the_same_state_as_a_404()
    {
        StaThread.Run(() =>
        {
            var vm = Ask(new StubGateway(HttpStatusCode.MethodNotAllowed, "method not allowed\n", "text/plain"));

            Assert.Equal("Not supported", vm.RuntimeBadgeText);
        });
    }

    [Fact]
    public void A_snapshot_from_before_is_cleared_when_the_gateway_later_answers_404()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.watching.json");
            Assert.NotEmpty(vm.RuntimeFindings);

            using var http = new HttpClient(new StubGateway(HttpStatusCode.NotFound, "404 page not found\n", "text/plain")) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
            using var client = new GatewayClient(http, () => new SecretValue("test-only-token-0000"));
            vm.ApplyRuntime(client.GetRawJsonAsync(RuntimePath).GetAwaiter().GetResult());

            Assert.Equal("Not supported", vm.RuntimeBadgeText);
            Assert.False(vm.RuntimeHasSnapshot);
            Assert.Empty(vm.RuntimeFindings);
            Assert.Empty(vm.RuntimePlanes);
        });
    }

    [Fact]
    public void Other_gateway_answers_are_their_own_states_and_never_read_as_a_clean_host()
    {
        StaThread.Run(() =>
        {
            var unauthorized = Ask(new StubGateway(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"));
            Assert.Equal(("Unknown", "Warn"), (unauthorized.RuntimeBadgeText, unauthorized.RuntimeBadgeKey));
            Assert.Contains("token not accepted", unauthorized.RuntimeTitle, StringComparison.Ordinal);
            Assert.Contains("not a clean-host result", unauthorized.RuntimeDetail, StringComparison.Ordinal);

            var notConnected = Ask(new StubGateway(HttpStatusCode.OK, "{\"error\":\"gateway: not connected\"}"));
            Assert.Equal("Not connected", notConnected.RuntimeBadgeText);

            var broken = Ask(new StubGateway(HttpStatusCode.InternalServerError, "boom", "text/plain"));
            Assert.Equal(("Error", "Bad"), (broken.RuntimeBadgeText, broken.RuntimeBadgeKey));
            Assert.Contains("HTTP 500", broken.RuntimeDetail, StringComparison.Ordinal);

            var down = Ask(new StubGateway(() => throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable)));
            Assert.Equal(("Unknown", "Warn"), (down.RuntimeBadgeText, down.RuntimeBadgeKey));
            Assert.Contains("gateway not reachable", down.RuntimeTitle, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------ what a gateway with the route answers

    [Fact]
    public void A_watching_snapshot_shows_the_planes_the_findings_worst_first_and_the_coverage_numbers()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.watching.json");

            Assert.Equal(("Watching", "Ok", "Runtime coverage"), (vm.RuntimeBadgeText, vm.RuntimeBadgeKey, vm.RuntimeTitle));
            Assert.True(vm.RuntimeHasSnapshot);
            Assert.Contains("3 findings;", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("312 processes observed (4 skipped)", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("88 connections (9 not attributed to a process)", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("polled", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("No findings is not the same as a clean host", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.DoesNotContain("Degraded:", vm.RuntimeDetail, StringComparison.Ordinal);

            // Planes: running is up, available but not running is idle, unavailable is blind (never quiet).
            Assert.Equal(
                new[] { ("inference heartbeat", "up", "Ok"), ("shadow egress", "up", "Ok"), ("agent actions", "blind", "Bad") },
                vm.RuntimePlanes.Select(p => (p.Name, p.StateText, p.StateKey)).ToArray());
            Assert.Equal("process table sampling", vm.RuntimePlanes[0].Mechanism);
            Assert.Null(vm.RuntimePlanes[0].Reason);
            Assert.Equal("no kernel event source on this platform", vm.RuntimePlanes[2].Reason);
            Assert.True(vm.RuntimePlanes[2].HasReason);

            // Findings: critical before high before medium.
            Assert.Equal(new[] { "python.exe", "helper.exe", "node.exe" }, vm.RuntimeFindings.Select(f => f.Process).ToArray());
        });
    }

    [Fact]
    public void A_finding_reads_its_process_score_agent_and_the_hostnames_of_its_providers()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.watching.json");

            var critical = vm.RuntimeFindings[0];
            Assert.Equal(("critical", "Critical", "82", "4321", "example-agent"), (critical.Severity, critical.SeverityKey, critical.ScoreDisplay, critical.PidDisplay, critical.Agent));

            // aiRuntimeProvider names its peer "hostname" (ai_runtime_api.go:60-67), and the address stands in when the
            // hostname is empty, which happens when a peer was attributed by address alone.
            Assert.Equal("api.example.test, 198.51.100.7", critical.Providers);

            var high = vm.RuntimeFindings[1];
            Assert.Equal(("High", "63"), (high.SeverityKey, high.ScoreDisplay));
            Assert.Null(high.Agent);
            Assert.Null(high.Providers);

            var medium = vm.RuntimeFindings[2];
            Assert.Equal("Medium", medium.SeverityKey);
            Assert.Equal("models.example.test", medium.Providers);
        });
    }

    [Fact]
    public void A_degraded_snapshot_says_which_plane_stopped_and_why()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.degraded.json");

            Assert.Equal(("Degraded", "Warn", "Runtime coverage is degraded"), (vm.RuntimeBadgeText, vm.RuntimeBadgeKey, vm.RuntimeTitle));
            Assert.True(vm.RuntimeHasSnapshot);
            Assert.Contains("0 findings;", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Contains("Degraded: shadow egress: DNS capture stopped: access denied.", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.Empty(vm.RuntimeFindings);

            var idle = vm.RuntimePlanes[1];
            Assert.Equal(("shadow egress", "idle", "Warn", "DNS capture stopped: access denied"), (idle.Name, idle.StateText, idle.StateKey, idle.Reason));
            Assert.Equal("blind", vm.RuntimePlanes[2].StateText);
        });
    }

    [Fact]
    public void A_gateway_with_the_runtime_service_off_answers_enabled_false_and_that_is_the_off_state()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.disabled.json");

            Assert.Equal(("Off", "Neutral", "Runtime coverage is turned off"), (vm.RuntimeBadgeText, vm.RuntimeBadgeKey, vm.RuntimeTitle));
            Assert.False(vm.RuntimeHasSnapshot);
            Assert.Empty(vm.RuntimePlanes);
            Assert.Empty(vm.RuntimeFindings);
        });
    }

    [Fact]
    public void A_reply_without_the_enabled_flag_is_incomplete_and_not_a_clean_host()
    {
        StaThread.Run(() =>
        {
            var vm = Answer("ai-runtime.incomplete.json");

            Assert.Equal(("Incomplete", "Warn"), (vm.RuntimeBadgeText, vm.RuntimeBadgeKey));
            Assert.Contains("Nothing here should be read as a clean host", vm.RuntimeDetail, StringComparison.Ordinal);
            Assert.False(vm.RuntimeHasSnapshot);
        });
    }

    [Fact]
    public void A_reply_that_is_not_an_object_is_incomplete_too()
    {
        StaThread.Run(() =>
        {
            var vm = Ask(new StubGateway(HttpStatusCode.OK, "[1, 2, 3]"));

            Assert.Equal("Incomplete", vm.RuntimeBadgeText);
        });
    }

    [Fact]
    public void A_reply_with_findings_and_planes_of_the_wrong_kind_is_read_as_far_as_it_goes()
    {
        StaThread.Run(() =>
        {
            const string odd = """
                {"enabled": true, "findings": {"not": "a list"}, "planes": "none", "degraded": "yes",
                 "future_member": {"a": [1, 2]}, "processes_observed": null}
                """;

            var vm = Ask(new StubGateway(HttpStatusCode.OK, odd));

            Assert.Equal("Watching", vm.RuntimeBadgeText);
            Assert.True(vm.RuntimeHasSnapshot);
            Assert.Empty(vm.RuntimeFindings);
            Assert.Empty(vm.RuntimePlanes);
            Assert.Contains("0 findings; ? processes observed", vm.RuntimeDetail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_fixtures_are_valid_json_shaped_like_the_upstream_reply()
    {
        // A guard for the fixtures themselves: every member the upstream struct always writes is present in a full reply.
        using var document = JsonDocument.Parse(PayloadFixtures.Read("ai-runtime.watching.json"));
        var root = document.RootElement;

        foreach (var member in new[]
                 {
                     "enabled", "findings", "planes", "processes_observed", "processes_skipped", "connections_observed",
                     "connections_unattributed", "host_plane_observations", "host_plane_gated", "degraded",
                 })
        {
            Assert.True(root.TryGetProperty(member, out _), member);
        }

        foreach (var finding in root.GetProperty("findings").EnumerateArray())
        {
            foreach (var member in new[] { "finding_id", "pid", "process", "score", "severity", "signals", "correlation" })
            {
                Assert.True(finding.TryGetProperty(member, out _), member);
            }
        }
    }
}
