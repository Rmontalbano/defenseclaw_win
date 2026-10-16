using System.Net;
using System.Net.Http;
using DefenseClaw.App.Services.Observability;

namespace DefenseClaw.App.Tests.Observability;

/// <summary>
/// Whether Agent360 may be offered (CUST-354): the answer follows one injectable look at Grafana, is reused while fresh, is looked at again when stale, and
/// closes with the sentence that points to the Local OTel wizard. The HTTP probe runs over a fake handler. No socket is opened, no process started.
/// </summary>
public sealed class Agent360AvailabilityTests
{
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * TimestampFrequency);
    }

    private sealed class ScriptedProbe : IGrafanaProbe
    {
        public bool Answer { get; set; }

        public bool Throws { get; set; }

        public int Calls { get; private set; }

        public List<string> Addresses { get; } = new();

        public Task<bool> ProbeAsync(string baseUrl, CancellationToken cancellationToken)
        {
            Calls++;
            Addresses.Add(baseUrl);
            return Throws ? throw new InvalidOperationException("boom") : Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task Before_the_first_look_the_feature_is_closed_as_checking()
    {
        using var availability = new Agent360Availability(null, new ScriptedProbe());

        Assert.Null(availability.Reachable);
        Assert.False(availability.Decision.IsAvailable);
        Assert.Equal(Agent360Availability.CheckingReason, availability.Decision.Reason);
        Assert.Null(availability.UrlFor("claudecode", null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_grafana_that_answers_opens_the_feature_and_builds_links()
    {
        var probe = new ScriptedProbe { Answer = true };
        using var availability = new Agent360Availability(null, probe);

        await availability.RefreshAsync();

        Assert.True(availability.Reachable);
        Assert.True(availability.Decision.IsAvailable);
        Assert.Equal(
            "http://localhost:3000/d/defenseclaw-agent-360?var-connector=codex&from=now-6h&to=now",
            availability.UrlFor("codex", null));
    }

    [Fact]
    public async Task A_grafana_that_does_not_answer_closes_it_with_a_pointer_to_the_local_otel_wizard()
    {
        using var availability = new Agent360Availability(null, new ScriptedProbe { Answer = false });

        await availability.RefreshAsync();

        Assert.False(availability.Reachable);
        Assert.False(availability.Decision.IsAvailable);
        Assert.Contains("Local OTel wizard", availability.Decision.Reason, StringComparison.Ordinal);
        Assert.Contains("http://localhost:3000", availability.Decision.Reason, StringComparison.Ordinal);
        Assert.Null(availability.UrlFor("codex", null));
    }

    [Fact]
    public async Task A_probe_that_throws_is_not_reachable_and_does_not_fault_the_caller()
    {
        using var availability = new Agent360Availability(null, new ScriptedProbe { Throws = true });

        await availability.RefreshAsync();

        Assert.False(availability.Reachable);
    }

    [Fact]
    public async Task The_address_comes_from_the_resolver_once_and_falls_back_to_the_default()
    {
        var resolved = 0;
        var probe = new ScriptedProbe { Answer = true };
        using var availability = new Agent360Availability(
            _ =>
            {
                resolved++;
                return Task.FromResult<string?>("{\"grafana_url\":\"http://localhost:3300\"}");
            },
            probe);

        await availability.RefreshAsync();
        await availability.RefreshAsync();

        Assert.Equal(1, resolved);
        Assert.Equal("http://localhost:3300", availability.BaseUrl);
        Assert.All(probe.Addresses, address => Assert.Equal("http://localhost:3300", address));

        using var failing = new Agent360Availability(_ => throw new InvalidOperationException("no cli"), new ScriptedProbe { Answer = true });
        await failing.RefreshAsync();
        Assert.Equal(Agent360Link.DefaultBaseUrl, failing.BaseUrl);
        Assert.True(failing.Reachable);

        using var foreign = new Agent360Availability(_ => Task.FromResult<string?>("{\"grafana_url\":\"https://grafana.example.test\"}"), new ScriptedProbe { Answer = true });
        await foreign.RefreshAsync();
        Assert.Equal(Agent360Link.DefaultBaseUrl, foreign.BaseUrl);
    }

    [Fact]
    public async Task A_fresh_answer_is_reused_and_a_stale_one_is_looked_at_again()
    {
        var clock = new FakeClock();
        var probe = new ScriptedProbe { Answer = true };
        using var availability = new Agent360Availability(null, probe, clock);

        await availability.EnsureFreshAsync();
        await availability.EnsureFreshAsync();
        Assert.Equal(1, probe.Calls);

        clock.Advance(Agent360Availability.FreshWhenReachable + TimeSpan.FromSeconds(1));
        await availability.EnsureFreshAsync();
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task A_no_is_trusted_for_less_time_than_a_yes()
    {
        var clock = new FakeClock();
        var probe = new ScriptedProbe { Answer = false };
        using var availability = new Agent360Availability(null, probe, clock);

        await availability.EnsureFreshAsync();
        probe.Answer = true;
        clock.Advance(Agent360Availability.FreshWhenNot + TimeSpan.FromSeconds(1));
        await availability.EnsureFreshAsync();

        Assert.Equal(2, probe.Calls);
        Assert.True(availability.Reachable);
    }

    [Fact]
    public async Task Refresh_looks_again_whatever_the_age_and_changed_is_raised_only_for_a_different_answer()
    {
        var probe = new ScriptedProbe { Answer = true };
        using var availability = new Agent360Availability(null, probe);
        var raised = 0;
        availability.Changed += (_, _) => raised++;

        await availability.RefreshAsync();
        await availability.RefreshAsync();
        Assert.Equal(2, probe.Calls);
        Assert.Equal(1, raised);

        probe.Answer = false;
        await availability.RefreshAsync();
        Assert.Equal(2, raised);
    }

    // ---- the HTTP probe, over a fake handler ----

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_respond(request));
        }
    }

    [Fact]
    public async Task The_probe_gets_the_health_endpoint_and_a_2xx_is_a_yes()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new HttpGrafanaProbe(handler);

        Assert.True(await probe.ProbeAsync("http://localhost:3000", CancellationToken.None));
        Assert.Equal(new[] { new Uri("http://localhost:3000/api/health") }, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Any_other_status_is_a_no(HttpStatusCode status)
    {
        var probe = new HttpGrafanaProbe(new FakeHandler(_ => new HttpResponseMessage(status)));

        Assert.False(await probe.ProbeAsync("http://localhost:3000", CancellationToken.None));
    }

    [Fact]
    public async Task A_connection_failure_is_a_no()
    {
        var probe = new HttpGrafanaProbe(new FakeHandler(_ => throw new HttpRequestException("refused")));

        Assert.False(await probe.ProbeAsync("http://localhost:3000", CancellationToken.None));
    }

    [Fact]
    public async Task An_address_that_is_not_loopback_is_never_requested()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new HttpGrafanaProbe(handler);

        Assert.False(await probe.ProbeAsync("http://grafana.example.test:3000", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }
}
