using DefenseClaw.Core.Correlation;

namespace DefenseClaw.Tests.Correlation;

/// <summary>The request side of the correlation routes: the gateway's own query rules, checked before anything is sent (CUST-355).</summary>
public sealed class CorrelationRoutesTests
{
    private const string Uuid = "01950000-0000-7000-8000-0000000000e1";

    [Fact]
    public void A_query_carries_exactly_one_anchor_the_limit_and_the_page_cursor_together()
    {
        var first = CorrelationRoutes.Query(CorrelationRoutes.Graph, CorrelationAnchor.ForAgent("agent-1"), 100);
        var next = CorrelationRoutes.Query(CorrelationRoutes.Graph, CorrelationAnchor.ForSession("s 1"), 25, "2026-10-01T10:00:07.123456789Z", Uuid);

        Assert.Equal("api/v1/correlation/graph?agent_id=agent-1&limit=100", first);
        Assert.Equal(
            "api/v1/correlation/graph?session_id=s%201&limit=25&after_time=2026-10-01T10%3A00%3A07.123456789Z&after_id=" + Uuid,
            next);
    }

    [Theory]
    [InlineData(CorrelationAnchorKind.Agent, "agent_id")]
    [InlineData(CorrelationAnchorKind.Session, "session_id")]
    [InlineData(CorrelationAnchorKind.Turn, "turn_id")]
    [InlineData(CorrelationAnchorKind.Lifecycle, "lifecycle_id")]
    [InlineData(CorrelationAnchorKind.Execution, "execution_id")]
    [InlineData(CorrelationAnchorKind.Record, "record_id")]
    [InlineData(CorrelationAnchorKind.ModelRequest, "model_request_id")]
    [InlineData(CorrelationAnchorKind.ModelResponse, "model_response_id")]
    [InlineData(CorrelationAnchorKind.ToolInvocation, "tool_invocation_id")]
    public void Each_anchor_kind_has_the_parameter_the_gateway_reads(CorrelationAnchorKind kind, string parameter)
    {
        var anchor = new CorrelationAnchor(kind, "x-1");

        Assert.Equal(parameter, anchor.ParameterName);
        Assert.Null(anchor.Problem());
        Assert.StartsWith("api/v1/correlation/conflicts?" + parameter + "=x-1&", CorrelationRoutes.Query(CorrelationRoutes.Conflicts, anchor), StringComparison.Ordinal);
    }

    [Fact]
    public void A_trace_anchor_may_carry_a_span_and_only_a_trace_may()
    {
        var trace = new string('a', 32);
        var span = new string('b', 16);

        Assert.Null(new CorrelationAnchor(CorrelationAnchorKind.Trace, trace, span).Problem());
        Assert.EndsWith("trace_id=" + trace + "&span_id=" + span + "&limit=100", CorrelationRoutes.Query(CorrelationRoutes.Timeline, new CorrelationAnchor(CorrelationAnchorKind.Trace, trace, span)), StringComparison.Ordinal);
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.Agent, "a", span).Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.Trace, trace, "short").Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.Trace, trace.ToUpperInvariant()).Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.Trace, "abc").Problem());
    }

    [Fact]
    public void Event_anchors_must_be_canonical_uuid_v7()
    {
        Assert.Null(new CorrelationAnchor(CorrelationAnchorKind.SemanticEvent, Uuid).Problem());
        Assert.Null(new CorrelationAnchor(CorrelationAnchorKind.LogicalEvent, Uuid).Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.SemanticEvent, "not-a-uuid").Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.LogicalEvent, Uuid.ToUpperInvariant()).Problem());
        Assert.NotNull(new CorrelationAnchor(CorrelationAnchorKind.SemanticEvent, Uuid.Replace("-7000-", "-4000-", StringComparison.Ordinal)).Problem());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("tab\tinside")]
    public void An_id_the_gateway_would_refuse_never_leaves_the_app(string value)
    {
        var anchor = CorrelationAnchor.ForAgent(value);

        Assert.NotNull(anchor.Problem());
        _ = Assert.Throws<ArgumentException>(() => CorrelationRoutes.Query(CorrelationRoutes.Graph, anchor));
    }

    [Fact]
    public void An_id_over_the_gateways_byte_limit_is_refused()
    {
        Assert.Null(CorrelationAnchor.ForAgent(new string('x', 512)).Problem());
        Assert.NotNull(CorrelationAnchor.ForAgent(new string('x', 513)).Problem());

        // The limit is in bytes: 300 two-byte characters are 600.
        Assert.NotNull(CorrelationAnchor.ForAgent(new string('é', 300)).Problem());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void A_limit_outside_one_to_five_hundred_is_refused(int limit)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CorrelationRoutes.Query(CorrelationRoutes.Graph, CorrelationAnchor.ForAgent("a"), limit));
    }

    [Fact]
    public void Half_a_cursor_is_refused_as_the_gateway_refuses_it()
    {
        _ = Assert.Throws<ArgumentException>(() => CorrelationRoutes.Query(CorrelationRoutes.Graph, CorrelationAnchor.ForAgent("a"), 10, "2026-10-01T10:00:07Z", null));
        _ = Assert.Throws<ArgumentException>(() => CorrelationRoutes.Query(CorrelationRoutes.Graph, CorrelationAnchor.ForAgent("a"), 10, null, Uuid));
    }

    [Fact]
    public void The_directory_path_clamps_its_page_and_escapes_the_cursor()
    {
        Assert.Equal("api/v1/agents/identities?limit=200", CorrelationRoutes.Directory(200, null));
        Assert.Equal("api/v1/agents/identities?limit=1000&cursor=200", CorrelationRoutes.Directory(5000, "200"));
        Assert.Equal("api/v1/agents/identities?limit=1", CorrelationRoutes.Directory(-3, string.Empty));
        Assert.Equal("api/v1/agents/identities?limit=5&cursor=a%26b", CorrelationRoutes.Directory(5, "a&b"));
    }
}
