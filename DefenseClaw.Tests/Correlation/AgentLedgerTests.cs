using System.Globalization;
using System.Text;
using DefenseClaw.Core.Correlation;

namespace DefenseClaw.Tests.Correlation;

/// <summary>
/// The view-model side of the agent view that lives in Core (CUST-355): the ledger that merges pages, the timeline's order and grouping, the counts,
/// and the tree with the method and status of every edge. Pure: no gateway, no clock.
/// </summary>
public sealed class AgentLedgerTests
{
    private const string Root = CorrelationFixtures.RootAgent;

    private static CorrelationGraphPage Page(string fixture) => CorrelationReader.ParseGraphText(CorrelationFixtures.Read(fixture)).Value!;

    private static AgentLedger Loaded(bool secondPage = false)
    {
        var ledger = new AgentLedger();
        Assert.True(ledger.AddPage(Root, Page("correlation-graph.root.page1.synthetic.json")));
        if (secondPage)
        {
            Assert.True(ledger.AddPage(Root, Page("correlation-graph.root.page2.synthetic.json")));
        }

        return ledger;
    }

    private static CorrelationRelationshipRow Edge(string parent, string child, string method = "reported", string status = "active", string type = "parent_of", int created = 0) =>
        new(
            $"{parent}>{child}:{method}:{status}", "agent", type == "parent_of" ? parent : child, "agent", type == "parent_of" ? child : parent, type,
            CorrelationReader.MethodOf(method), method, 100, "rule", 1, CorrelationReader.StatusOf(status), status,
            new DateTimeOffset(2026, 10, 1, 10, 0, created, TimeSpan.Zero), null);

    // ------------------------------------------------------------------ order and grouping

    [Fact]
    public void The_timeline_is_in_time_order_one_entry_per_record()
    {
        var timeline = Loaded().Timeline([Root]);

        Assert.Equal(9, timeline.Count);
        Assert.Equal(timeline.OrderBy(e => e.Time).Select(e => e.RecordId), timeline.Select(e => e.RecordId));
        Assert.Equal("rec-e1", timeline[0].RecordId);
        Assert.Equal(
            ["Session started", "Prompt submitted", "Prompt submitted", "Model request", "Tool call", "Hook decision", "Tool call blocked", "Approval requested", "Approval resolved"],
            timeline.Select(e => e.Title));
        Assert.Equal(
            [TimelineKind.Lifecycle, TimelineKind.Prompt, TimelineKind.Prompt, TimelineKind.Model, TimelineKind.Tool, TimelineKind.Guardrail, TimelineKind.Block, TimelineKind.Approval, TimelineKind.Approval],
            timeline.Select(e => e.Kind));
    }

    [Fact]
    public void Entries_with_the_same_time_order_by_occurrence_then_record_and_an_entry_with_no_time_is_last()
    {
        var ledger = new AgentLedger();
        var page = CorrelationReader.ParseGraphText("""
            {
              "events": [
                {"semantic_event_id": "b", "event_name": "tool_start", "received_time": "2026-10-01T10:00:00Z"},
                {"semantic_event_id": "a", "event_name": "tool_start", "received_time": "2026-10-01T10:00:00Z"},
                {"semantic_event_id": "z", "event_name": "tool_end"}
              ],
              "observations": [
                {"record_id": "r2", "semantic_event_id": "a", "observed_at": "2026-10-01T10:00:00Z", "bucket": "tool.activity", "event_name": "tool.invocation.started"},
                {"record_id": "r1", "semantic_event_id": "a", "observed_at": "2026-10-01T10:00:00Z", "bucket": "tool.activity", "event_name": "tool.invocation.started"}
              ]
            }
            """).Value!;
        _ = ledger.AddPage("agent-x", page);

        var order = ledger.Timeline(["agent-x"]).Select(e => $"{e.SemanticEventId}/{e.RecordId}").ToArray();

        // a has two records at the same instant (r1 before r2); b has no record, so its time is when it was received; z has no time at all.
        Assert.Equal(["a/r1", "a/r2", "b/", "z/"], order);
    }

    [Fact]
    public void An_occurrence_with_no_record_is_still_a_row()
    {
        var ledger = new AgentLedger();
        _ = ledger.AddPage("agent-x", CorrelationReader.ParseGraphText("""{"events":[{"semantic_event_id":"e","event_name":"turn_end","received_time":"2026-10-01T10:00:00Z"}]}""").Value!);

        var entry = Assert.Single(ledger.Timeline(["agent-x"]));

        Assert.Equal(string.Empty, entry.RecordId);
        Assert.Equal(TimelineKind.Outcome, entry.Kind);
        Assert.Equal("Turn finished", entry.Title);
    }

    [Fact]
    public void Entries_group_by_turn_in_the_order_the_turns_appear_and_an_entry_with_no_turn_is_not_given_one()
    {
        var groups = TimelineGrouping.ByTurn(Loaded(secondPage: true).Timeline([Root]));

        Assert.Equal(["", "turn-0001", "turn-0002"], groups.Select(g => g.TurnId));
        Assert.Equal("No turn reported", groups[0].Label);
        Assert.Equal("Turn turn-0001", groups[1].Label);
        Assert.Equal(["Session started"], groups[0].Entries.Select(e => e.Title));
        Assert.Equal(11, groups[1].Entries.Count);
        Assert.Equal(["Block applied"], groups[2].Entries.Select(e => e.Title));
    }

    [Fact]
    public void A_scope_shows_only_the_occurrences_its_agents_pages_returned()
    {
        var ledger = Loaded();
        _ = ledger.AddPage(CorrelationFixtures.ChildAgent, Page("correlation-graph.child.synthetic.json"));

        Assert.Equal(9, ledger.Timeline([Root]).Count);
        Assert.Equal(2, ledger.Timeline([CorrelationFixtures.ChildAgent]).Count);
        Assert.Equal(11, ledger.Timeline([Root, CorrelationFixtures.ChildAgent]).Count);
        Assert.Empty(ledger.Timeline(["agent-nobody"]));
    }

    // ------------------------------------------------------------------ classification

    [Theory]
    [InlineData("tool.activity", "tool.invocation.blocked", "", TimelineKind.Block)]
    [InlineData("enforcement.action", "enforcement.block.applied", "", TimelineKind.Block)]
    [InlineData("enforcement.action", "enforcement.block.requested", "", TimelineKind.Enforcement)]
    [InlineData("enforcement.action", "span.approval.resolve", "", TimelineKind.Approval)]
    [InlineData("compliance.activity", "approval.requested", "", TimelineKind.Approval)]
    [InlineData("guardrail.evaluation", "guardrail.evaluation.completed", "", TimelineKind.Guardrail)]
    [InlineData("guardrail.evaluation", "hook_decision", "", TimelineKind.Guardrail)]
    [InlineData("model.io", "model.response", "", TimelineKind.Model)]
    [InlineData("model.io", "gen_ai.client.token.usage", "", TimelineKind.Model)]
    [InlineData("agent_lifecycle", "tool.started", "tool_start", TimelineKind.Tool)]
    [InlineData("agent.lifecycle", "turn_start", "", TimelineKind.Prompt)]
    [InlineData("agent.lifecycle", "span.agent.invoke", "turn_start", TimelineKind.Prompt)]
    [InlineData("agent.lifecycle", "subagent_stop", "", TimelineKind.Outcome)]
    [InlineData("agent.lifecycle", "session_start", "", TimelineKind.Lifecycle)]
    [InlineData("", "", "turn_end", TimelineKind.Outcome)]
    [InlineData("diagnostic", "something.new", "", TimelineKind.Other)]
    public void A_record_is_classified_from_its_bucket_and_name_then_from_its_occurrences(string bucket, string record, string occurrence, TimelineKind expected)
    {
        Assert.Equal(expected, TimelineClassifier.Classify(bucket, record, occurrence));
    }

    [Fact]
    public void A_name_this_app_has_no_words_for_is_shown_as_the_gateway_wrote_it()
    {
        Assert.Equal("something.new", TimelineClassifier.Title("something.new", "other"));
        Assert.Equal("Turn finished", TimelineClassifier.Title("span.x", "turn_end"));
        Assert.Equal("Event", TimelineClassifier.Title(string.Empty, string.Empty));
    }

    // ------------------------------------------------------------------ the counts

    [Fact]
    public void The_counts_come_from_distinct_occurrences_and_metric_points_are_not_counted_or_shown()
    {
        var all = Loaded(secondPage: true).Timeline([Root]);

        var kpis = AgentKpis.From(all);

        Assert.Equal(2, kpis.EnforcedBlocks);
        Assert.Equal(1, kpis.GuardrailEvaluations);
        Assert.Equal(1, kpis.ApprovalsRequested);
        Assert.Equal(1, kpis.ApprovalsResolved);
        Assert.Equal(2, kpis.ModelCalls);
        Assert.Equal(1, kpis.ToolCalls);
        Assert.Equal(1, kpis.MetricPointsHidden);
    }

    [Fact]
    public void Only_the_events_loaded_so_far_are_counted_and_the_ledger_knows_there_are_more()
    {
        var ledger = Loaded();

        Assert.Equal(1, AgentKpis.From(ledger.Timeline([Root])).EnforcedBlocks);
        Assert.True(ledger.HasMore(Root));

        _ = ledger.AddPage(Root, Page("correlation-graph.root.page2.synthetic.json"));

        Assert.Equal(2, AgentKpis.From(ledger.Timeline([Root])).EnforcedBlocks);
        Assert.False(ledger.HasMore(Root));
    }

    [Fact]
    public void What_the_ledger_does_not_hold_is_worded_not_reported_and_never_a_number()
    {
        Assert.Equal("Not reported", AgentKpis.NotReported);
        Assert.Contains("Nothing is estimated", AgentKpis.UsageNote, StringComparison.Ordinal);
        Assert.Contains("observe-mode would-block", AgentKpis.WouldBlockNote, StringComparison.Ordinal);
        Assert.Contains("verdict", AgentKpis.WouldBlockNote, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the ledger

    [Fact]
    public void Adding_the_same_page_twice_changes_nothing()
    {
        var ledger = Loaded();
        var before = (ledger.EventCount, ledger.Relationships.Count, ledger.Evidence.Count, ledger.Timeline([Root]).Count);

        _ = ledger.AddPage(Root, Page("correlation-graph.root.page1.synthetic.json"));

        Assert.Equal(before, (ledger.EventCount, ledger.Relationships.Count, ledger.Evidence.Count, ledger.Timeline([Root]).Count));
    }

    [Fact]
    public void The_cursor_of_the_next_page_is_the_pages_own_until_the_last_page()
    {
        var ledger = Loaded();

        var cursor = ledger.NextCursor(Root);
        Assert.Equal("2026-10-01T10:00:07.000000000Z", cursor!.Value.Time);
        Assert.Equal("01950000-0000-7000-8000-0000000000e8", cursor.Value.Id);
        Assert.Null(ledger.NextCursor("agent-unread"));

        _ = ledger.AddPage(Root, Page("correlation-graph.root.page2.synthetic.json"));
        Assert.Null(ledger.NextCursor(Root));
    }

    [Fact]
    public void A_session_slice_names_its_agents_and_is_not_counted_among_them()
    {
        var ledger = new AgentLedger();
        _ = ledger.AddPage(AgentLedger.SessionKey("session-0001"), Page("correlation-graph.root.page1.synthetic.json"));

        Assert.Equal([Root], ledger.AgentsOf(AgentLedger.SessionKey("session-0001")));
        Assert.Empty(ledger.AgentIds);
        Assert.True(ledger.Has("session:session-0001"));
        Assert.Empty(ledger.AgentsOf("session:none"));
    }

    [Fact]
    public void The_ledger_stops_taking_pages_at_its_event_limit_and_says_so()
    {
        var ledger = new AgentLedger();
        var accepted = 0;
        for (var page = 0; page < (AgentLedger.MaxEvents / 500) + 1; page++)
        {
            var json = new StringBuilder("{\"events\":[");
            for (var i = 0; i < 500; i++)
            {
                _ = json.Append(i > 0 ? "," : string.Empty).Append(CultureInfo.InvariantCulture, $"{{\"semantic_event_id\":\"p{page}-{i}\",\"event_name\":\"tool_start\"}}");
            }

            _ = json.Append("]}");
            if (ledger.AddPage("agent-x", CorrelationReader.ParseGraphText(json.ToString()).Value!))
            {
                accepted++;
            }
        }

        Assert.Equal(AgentLedger.MaxEvents / 500, accepted);
        Assert.True(ledger.Capped);
        Assert.Equal(AgentLedger.MaxEvents, ledger.EventCount);
        Assert.False(ledger.HasMore("agent-x"));
    }

    [Fact]
    public void The_ledger_takes_at_most_the_agent_limit()
    {
        var ledger = new AgentLedger();
        var empty = Page("correlation-graph.empty.synthetic.json");
        for (var i = 0; i < AgentLedger.MaxAgents; i++)
        {
            Assert.True(ledger.AddPage("agent-" + i.ToString(CultureInfo.InvariantCulture), empty));
        }

        Assert.False(ledger.AddPage("agent-one-too-many", empty));
        Assert.True(ledger.Capped);
        Assert.True(ledger.AddPage("agent-0", empty));
        Assert.Equal(AgentLedger.MaxAgents, ledger.AgentCount);
    }

    // ------------------------------------------------------------------ the tree

    [Fact]
    public void The_tree_has_every_child_in_creation_order_with_its_edge_whatever_the_edge_says()
    {
        var ledger = Loaded();
        _ = ledger.AddPage(CorrelationFixtures.ChildAgent, Page("correlation-graph.child.synthetic.json"));

        var tree = AgentTree.Build(Root, ledger.Relationships);

        Assert.False(tree.Truncated);
        Assert.Equal(6, tree.NodeCount);
        Assert.Equal(
            [CorrelationFixtures.ChildAgent, CorrelationFixtures.InferredChild, CorrelationFixtures.ConflictedChild, CorrelationFixtures.UnknownChild],
            tree.Root.Children.Select(c => c.AgentId));
        var c1 = tree.Root.Children[0];
        Assert.Equal(CorrelationFixtures.Grandchild, Assert.Single(c1.Children).AgentId);
        Assert.Equal(["reported / active", "inferred / unresolved", "derived / conflicted", "telepathy / newstatus"], tree.Root.Children.Select(c => $"{RelationshipDisplay.MethodLabel(c.Edge!)} / {RelationshipDisplay.StatusLabel(c.Edge!)}"));
        Assert.Equal(["trace exact / active"], c1.Children.Select(c => $"{RelationshipDisplay.MethodLabel(c.Edge!)} / {RelationshipDisplay.StatusLabel(c.Edge!)}"));
        Assert.Null(tree.Root.Edge);
    }

    [Fact]
    public void Only_a_reported_or_trace_exact_active_edge_is_settled_and_the_rest_keep_their_tones()
    {
        var tree = AgentTree.Build(Root, Loaded().Relationships);

        Assert.Equal([true, false, false, false], tree.Root.Children.Select(c => RelationshipDisplay.IsSettled(c.Edge!)));
        Assert.Equal(["Ok", "Medium", "Low", "Neutral"], tree.Root.Children.Select(c => RelationshipDisplay.MethodTone(c.Edge!)));
        Assert.Equal(["Ok", "Warn", "High", "Neutral"], tree.Root.Children.Select(c => RelationshipDisplay.StatusTone(c.Edge!)));
        Assert.Contains("inferred, unresolved, confidence 40, 1 piece of evidence", RelationshipDisplay.Sentence(tree.Root.Children[1].Edge!), StringComparison.Ordinal);
    }

    [Fact]
    public void Either_direction_of_the_lineage_edge_puts_the_child_under_its_parent()
    {
        var tree = AgentTree.Build("p", [Edge("p", "viaParentOf"), Edge("p", "viaDelegatedBy", type: "delegated_by", created: 1)]);

        Assert.Equal(["viaParentOf", "viaDelegatedBy"], tree.Root.Children.Select(c => c.AgentId));
    }

    [Fact]
    public void Where_edges_disagree_the_conflicted_one_is_shown_and_a_superseded_or_rejected_one_is_still_shown_when_alone()
    {
        var tree = AgentTree.Build(
            "p",
            [
                Edge("p", "a", "reported", "active", created: 1), Edge("p", "a", "derived", "conflicted", created: 2),
                Edge("p", "b", "reported", "superseded", created: 3),
                Edge("p", "c", "inferred", "rejected", created: 4),
                Edge("p", "d", "inferred", "candidate", created: 5), Edge("p", "d", "reported", "superseded", created: 6),
            ]);

        Assert.Equal(["a", "b", "c", "d"], tree.Root.Children.Select(c => c.AgentId));
        Assert.Equal(["conflicted", "superseded", "rejected", "unresolved"], tree.Root.Children.Select(c => RelationshipDisplay.StatusLabel(c.Edge!)));
    }

    [Fact]
    public void An_edge_between_things_that_are_not_both_agents_or_to_itself_is_not_a_child()
    {
        var notAgent = Edge("p", "q") with { ToKind = "session" };
        var self = Edge("p", "p");
        var other = Edge("p", "q") with { Type = "invokes" };

        Assert.Empty(AgentTree.Build("p", [notAgent, self, other]).Root.Children);
    }

    [Fact]
    public void A_loop_in_the_ledger_is_drawn_once_and_a_deep_chain_is_cut_and_says_so()
    {
        var loop = AgentTree.Build("a", [Edge("a", "b"), Edge("b", "a", created: 1)]);
        Assert.Equal(2, loop.NodeCount);
        Assert.Empty(loop.Root.Children[0].Children);

        var chain = Enumerable.Range(0, 12).Select(i => Edge("n" + i.ToString(CultureInfo.InvariantCulture), "n" + (i + 1).ToString(CultureInfo.InvariantCulture), created: i)).ToArray();
        var deep = AgentTree.Build("n0", chain);
        Assert.True(deep.Truncated);
        Assert.Equal(AgentTree.MaxDepth + 1, deep.NodeCount);
    }

    [Fact]
    public void A_very_wide_tree_is_cut_at_the_node_limit()
    {
        var wide = Enumerable.Range(0, AgentTree.MaxNodes + 50).Select(i => Edge("root", "c" + i.ToString(CultureInfo.InvariantCulture), created: i % 59)).ToArray();

        var tree = AgentTree.Build("root", wide);

        Assert.True(tree.Truncated);
        Assert.Equal(AgentTree.MaxNodes, tree.NodeCount);
    }

    [Fact]
    public void The_tops_of_a_set_of_agents_are_the_ones_with_no_parent_among_them()
    {
        var edges = new[] { Edge("r", "c1"), Edge("c1", "g1", created: 1), Edge("outside", "orphan", created: 2) };

        Assert.Equal(["r", "orphan", "lone"], AgentTree.RootsOf(["r", "c1", "g1", "orphan", "lone"], edges));
        Assert.Equal(["c1"], AgentTree.RootsOf(["c1", "g1"], edges));
        Assert.Equal(["a"], AgentTree.RootsOf(["a", "b"], [Edge("a", "b"), Edge("b", "a", created: 1)]));
        Assert.Empty(AgentTree.RootsOf([], edges));
    }

    [Fact]
    public void Parents_and_children_of_an_agent_come_from_both_edge_types()
    {
        var edges = new[] { Edge("p", "c"), Edge("q", "c", type: "delegated_by", created: 1) };

        Assert.Equal(["c"], AgentTree.ChildrenOf("p", edges));
        Assert.Equal(["p", "q"], AgentTree.ParentsOf("c", edges).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(AgentTree.ChildrenOf("c", edges));
    }

    [Fact]
    public void A_long_id_is_shortened_around_its_ends_and_a_short_one_is_whole()
    {
        Assert.Equal("agent-root-0…aaaaaa", AgentTree.Short(Root));
        Assert.Equal("agt-0000000000000001", AgentTree.Short("agt-0000000000000001"));
    }
}
