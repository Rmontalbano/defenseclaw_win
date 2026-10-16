using System.Net;
using System.Text;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests.Correlation;

/// <summary>
/// The reads of the agent view over a fake HTTP gateway (CUST-355): the paths asked for, the bearer token and client header every request carries,
/// paging, the walk from a session to the agents around it, and what each failure becomes. Handlers are in memory; nothing opens a socket.
/// </summary>
public sealed class CorrelationServiceTests
{
    private static (CorrelationService Service, CorrelationFixtures.FakeGateway Handler) Build(TimeSpan? timeout = null)
    {
        var (client, handler) = CorrelationFixtures.Gateway(timeout);
        return (new CorrelationService((path, max, ct) => client.GetBoundedJsonAsync(path, max, true, ct)), handler);
    }

    private static string[] Paths(CorrelationFixtures.FakeGateway handler) => handler.Requests.Select(r => r.RequestUri!.PathAndQuery.TrimStart('/')).ToArray();

    [Fact]
    public async Task Every_request_is_a_get_with_the_bearer_token_and_the_client_header()
    {
        var (service, handler) = Build();

        _ = await service.ReadGraphAsync(CorrelationAnchor.ForAgent(CorrelationFixtures.RootAgent), 100, null, null, CancellationToken.None);
        _ = await service.ReadConflictsAsync(CorrelationAnchor.ForAgent(CorrelationFixtures.RootAgent), 100, CancellationToken.None);
        _ = await service.ReadDirectoryAsync(200, null, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("Bearer " + CorrelationFixtures.Token, r.Headers.Authorization?.ToString());
            Assert.Equal(GatewayClient.DefaultClientHeaderValue, Assert.Single(r.Headers.GetValues(GatewayClient.ClientHeaderName)));
        });
        Assert.Equal(
            [
                "api/v1/correlation/graph?agent_id=" + CorrelationFixtures.RootAgent + "&limit=100",
                "api/v1/correlation/conflicts?agent_id=" + CorrelationFixtures.RootAgent + "&limit=100",
                "api/v1/agents/identities?limit=200",
            ],
            Paths(handler));
    }

    [Fact]
    public async Task A_page_is_read_and_the_next_one_is_asked_for_with_the_cursor_the_first_returned()
    {
        var (service, handler) = Build();
        var anchor = CorrelationAnchor.ForAgent(CorrelationFixtures.RootAgent);

        var first = await service.ReadGraphAsync(anchor, 100, null, null, CancellationToken.None);
        Assert.True(first.IsOk);
        Assert.True(first.Value!.HasNext);

        var second = await service.ReadGraphAsync(anchor, 100, first.Value.NextAfterTimeText, first.Value.NextAfterId, CancellationToken.None);

        Assert.True(second.IsOk);
        Assert.False(second.Value!.HasNext);
        Assert.EndsWith(
            "&after_time=2026-10-01T10%3A00%3A07.000000000Z&after_id=01950000-0000-7000-8000-0000000000e8",
            Paths(handler)[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anchor_the_gateway_would_refuse_is_rejected_without_a_request()
    {
        var (service, handler) = Build();

        var read = await service.ReadGraphAsync(CorrelationAnchor.ForAgent(" padded "), 100, null, null, CancellationToken.None);

        Assert.Equal(CorrelationReadStatus.Rejected, read.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_walk_from_a_session_reads_its_agents_and_then_every_agent_around_them()
    {
        var (service, handler) = Build();
        var ledger = new AgentLedger();

        var outcome = await service.LoadAsync(ledger, CorrelationFixtures.Session, [], 100, CancellationToken.None);

        Assert.True(outcome.IsOk, outcome.Message);
        Assert.False(outcome.Capped);
        Assert.Empty(outcome.Notes);

        // The session, the root, then the root's four children and the child's own child (read once each).
        Assert.Equal(7, outcome.AgentsRead);
        var graph = Paths(handler).Where(p => p.StartsWith("api/v1/correlation/graph?", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, graph.Length);
        Assert.Contains(graph, p => p.Contains("session_id=" + CorrelationFixtures.Session, StringComparison.Ordinal));
        foreach (var agent in new[] { CorrelationFixtures.RootAgent, CorrelationFixtures.ChildAgent, CorrelationFixtures.InferredChild, CorrelationFixtures.ConflictedChild, CorrelationFixtures.UnknownChild, CorrelationFixtures.Grandchild })
        {
            Assert.Single(graph, p => p.Contains("agent_id=" + agent + "&", StringComparison.Ordinal));
        }

        var tree = AgentTree.Build(AgentTree.RootsOf(ledger.AgentIds, ledger.Relationships).Single(), ledger.Relationships);
        Assert.Equal(CorrelationFixtures.RootAgent, tree.Root.AgentId);
        Assert.Equal(6, tree.NodeCount);
        Assert.Equal(1, ledger.Timeline([CorrelationFixtures.ChildAgent]).Count(e => e.Kind == TimelineKind.Tool));
    }

    [Fact]
    public async Task Walking_from_an_agent_id_finds_its_parents_and_children_and_reads_each_once()
    {
        var (service, handler) = Build();
        var ledger = new AgentLedger();

        var outcome = await service.LoadAsync(ledger, null, [CorrelationFixtures.ChildAgent], 100, CancellationToken.None);

        Assert.True(outcome.IsOk, outcome.Message);

        // The child names its parent (the root) and its own child; the root names its other children.
        Assert.Equal(6, ledger.AgentCount);
        Assert.Equal(outcome.AgentsRead, Paths(handler).Count(p => p.StartsWith("api/v1/correlation/graph?", StringComparison.Ordinal)));
        Assert.Equal(Paths(handler).Distinct().Count(), Paths(handler).Length);
        Assert.Equal([CorrelationFixtures.RootAgent], AgentTree.RootsOf(ledger.AgentIds, ledger.Relationships));
    }

    [Fact]
    public async Task An_agent_already_in_the_ledger_is_not_read_again()
    {
        var (service, handler) = Build();
        var ledger = new AgentLedger();
        _ = await service.LoadAsync(ledger, null, [CorrelationFixtures.RootAgent], 100, CancellationToken.None);
        var before = handler.Requests.Count;

        _ = await service.LoadAsync(ledger, null, [CorrelationFixtures.RootAgent], 100, CancellationToken.None);

        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task One_descendant_that_cannot_be_read_is_a_note_and_the_rest_goes_on()
    {
        var (service, handler) = Build();
        handler.Override = r => r.RequestUri!.Query.Contains("agent_id=" + CorrelationFixtures.InferredChild, StringComparison.Ordinal)
            ? CorrelationFixtures.Json("{\"error\":\"correlation query failed\"}", HttpStatusCode.InternalServerError)
            : null;
        var ledger = new AgentLedger();

        var outcome = await service.LoadAsync(ledger, null, [CorrelationFixtures.RootAgent], 100, CancellationToken.None);

        Assert.True(outcome.IsOk);
        var note = Assert.Single(outcome.Notes);
        Assert.Contains("Could not read agent", note, StringComparison.Ordinal);
        Assert.True(ledger.Has(CorrelationFixtures.ChildAgent));
        Assert.True(ledger.Has(CorrelationFixtures.Grandchild));
        Assert.False(ledger.Has(CorrelationFixtures.InferredChild));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CorrelationReadStatus.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CorrelationReadStatus.Unsupported)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CorrelationReadStatus.NotReady)]
    [InlineData(HttpStatusCode.Unauthorized, CorrelationReadStatus.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway, CorrelationReadStatus.Failed)]
    public async Task A_failure_on_the_root_stops_the_walk_with_a_status_that_says_why(HttpStatusCode code, CorrelationReadStatus expected)
    {
        var (service, handler) = Build();
        handler.Override = _ => CorrelationFixtures.Json("{\"error\":\"nope\"}", code);

        var outcome = await service.LoadAsync(new AgentLedger(), CorrelationFixtures.Session, [], 100, CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
        Assert.False(outcome.IsOk);
        Assert.NotEmpty(outcome.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_capability_off_runtime_answers_404_to_every_route_and_nothing_is_invented()
    {
        var (service, handler) = Build();
        handler.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };

        var graph = await service.ReadGraphAsync(CorrelationAnchor.ForAgent("a"), 100, null, null, CancellationToken.None);
        var directory = await service.ReadDirectoryAsync(200, null, CancellationToken.None);
        var whole = await service.ReadDirectoryAsync(200, CancellationToken.None);

        Assert.Equal(CorrelationReadStatus.Unsupported, graph.Status);
        Assert.Equal(CorrelationReadStatus.Unsupported, directory.Status);
        Assert.Equal(CorrelationReadStatus.Unsupported, whole.Status);
        Assert.Null(graph.Value);
        Assert.False(graph.KeepsLastGood);
    }

    [Fact]
    public async Task An_answer_over_the_read_limit_is_not_read_and_the_message_is_not_a_half_page()
    {
        var (service, handler) = Build();
        handler.Override = _ =>
        {
            var padding = new string('x', CorrelationRoutes.MaxGraphBytes);
            return CorrelationFixtures.Json("{\"events\":[],\"pad\":\"" + padding + "\"}");
        };

        var read = await service.ReadGraphAsync(CorrelationAnchor.ForAgent("a"), 100, null, null, CancellationToken.None);

        Assert.Equal(CorrelationReadStatus.TooLarge, read.Status);
        Assert.Null(read.Value);
        Assert.True(read.KeepsLastGood);
    }

    [Fact]
    public async Task Garbage_from_a_listener_that_is_not_the_gateway_is_malformed_or_a_failure_not_a_page()
    {
        var (service, handler) = Build();
        handler.Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>hello</html>", Encoding.UTF8, "text/html") };

        var read = await service.ReadGraphAsync(CorrelationAnchor.ForAgent("a"), 100, null, null, CancellationToken.None);

        Assert.False(read.IsOk);
        Assert.NotEqual(CorrelationReadStatus.Ok, read.Status);
        Assert.Null(read.Value);
    }

    [Fact]
    public async Task The_token_is_in_no_message()
    {
        var (service, handler) = Build();
        handler.Override = _ => CorrelationFixtures.Json("{\"error\":\"bad Bearer " + CorrelationFixtures.Token + "\"}", HttpStatusCode.BadRequest);

        var read = await service.ReadGraphAsync(CorrelationAnchor.ForAgent("a"), 100, null, null, CancellationToken.None);

        Assert.Equal(CorrelationReadStatus.Rejected, read.Status);
        Assert.DoesNotContain(CorrelationFixtures.Token, read.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the directory

    [Fact]
    public async Task The_directory_follows_the_next_cursor_up_to_the_page_limit()
    {
        var (service, handler) = Build();
        var pages = 0;
        handler.Override = r =>
        {
            pages++;
            return CorrelationFixtures.Json("{\"enabled\":true,\"identities\":[{\"agent_id\":\"agt-" + pages + "\",\"connector\":\"codex\"}],\"total\":99,\"next_cursor\":\"" + pages + "\"}");
        };

        var read = await service.ReadDirectoryAsync(1, CancellationToken.None);

        Assert.True(read.IsOk);
        Assert.Equal(CorrelationService.MaxDirectoryPages, pages);
        Assert.Equal(CorrelationService.MaxDirectoryPages, read.Value!.Rows.Count);
        Assert.Equal(99, read.Value.Total);
        Assert.Equal(CorrelationService.MaxDirectoryPages.ToString(System.Globalization.CultureInfo.InvariantCulture), read.Value.NextCursor);
        Assert.EndsWith("&cursor=2", Paths(handler)[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_directory_page_that_fails_keeps_what_was_read_and_says_the_list_is_partial()
    {
        var (service, handler) = Build();
        handler.Override = r => r.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal)
            ? CorrelationFixtures.Json("{\"error\":\"directory unavailable\"}", HttpStatusCode.InternalServerError)
            : CorrelationFixtures.Json("{\"enabled\":true,\"identities\":[{\"agent_id\":\"agt-1\"}],\"total\":5,\"next_cursor\":\"1\"}");

        var read = await service.ReadDirectoryAsync(1, CancellationToken.None);

        Assert.True(read.IsOk);
        Assert.Single(read.Value!.Rows);
        Assert.StartsWith("The list is partial", read.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, read.Value.NextCursor);
    }

    [Fact]
    public async Task A_directory_the_gateway_does_not_keep_is_not_paged()
    {
        var (service, handler) = Build();
        handler.Override = _ => CorrelationFixtures.Json(CorrelationFixtures.Read("agents-identities.disabled.synthetic.json"));

        var read = await service.ReadDirectoryAsync(200, CancellationToken.None);

        Assert.True(read.IsOk);
        Assert.False(read.Value!.Enabled);
        Assert.Single(handler.Requests);
    }
}
