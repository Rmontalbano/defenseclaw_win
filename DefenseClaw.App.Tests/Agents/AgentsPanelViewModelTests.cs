using System.Net;
using System.Windows.Data;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>
/// The Agents panel's view-model (CUST-355): the directory per connector, the tree with every edge's method and status, the timeline's order and
/// grouping, paging, the counts (and the figures that are "Not reported"), and every way the gateway can fail or not have the routes. The gateway is
/// an in-memory handler over synthetic fixtures behind the real client; no socket opens and nothing is changed anywhere.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AgentsPanelViewModelTests : IDisposable
{
    private const string CodexIdentity = "agt-0000000000000001";
    private const string RetiredIdentity = "agt-0000000000000003";

    private readonly AgentsScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private async Task<AgentsPanelViewModel> OpenWithRootAsync()
    {
        var vm = await _scene.OpenAsync();
        _scene.Select(CodexIdentity);
        return vm;
    }

    private string[] Headers() => _scene.Ui(() => _scene.Vm.Timeline.Where(r => r.IsHeader).Select(r => r.HeaderText).ToArray());

    private string[] Entries() => _scene.Ui(() => _scene.Vm.Timeline.Where(r => r.IsEntry).Select(r => r.Title).ToArray());

    private string Kpi(string label) => _scene.Ui(() => _scene.Vm.Kpis.Single(k => k.Label == label).Value);

    // ------------------------------------------------------------------ the directory

    [Fact]
    public async Task The_directory_lists_every_identity_grouped_by_connector_newest_first()
    {
        var vm = await _scene.OpenAsync();

        Assert.Equal(AgentsState.Ready, _scene.Ui(() => vm.State));
        Assert.Equal("3 agents, per connector", _scene.Ui(() => vm.CaptionText));
        var groups = _scene.Ui(() => vm.AgentsView.Groups.Cast<CollectionViewGroup>().Select(g => $"{g.Name}:{string.Join(',', g.Items.Cast<AgentDirectoryRowVm>().Select(r => r.AgentId))}").ToArray());
        Assert.Equal(
            ["claudecode:agt-0000000000000002", "codex:agt-0000000000000001,agt-0000000000000003"],
            groups);
        Assert.Contains("retired account", _scene.Ui(() => vm.Agents.Single(a => a.AgentId == RetiredIdentity).AutomationName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_filter_box_narrows_the_directory_by_connector_user_id_or_session()
    {
        var vm = await _scene.OpenAsync();

        int Shown(string text) => _scene.Ui(() =>
        {
            vm.FilterText = text;
            return vm.AgentsView.Cast<AgentDirectoryRowVm>().Count();
        });

        Assert.Equal(3, Shown(string.Empty));
        Assert.Equal(1, Shown("claude"));
        Assert.Equal(2, Shown("codex"));
        Assert.Equal(1, Shown("former-user"));
        Assert.Equal(1, Shown("session-0900"));
        Assert.Equal(0, Shown("nothing-matches-this"));
        Assert.True(_scene.Ui(() => vm.ShowNoMatch));
        Assert.True(_scene.Ui(vm.HandleEscape));
        Assert.Equal(string.Empty, _scene.Ui(() => vm.FilterText));
        Assert.False(_scene.Ui(vm.HandleEscape));
    }

    [Fact]
    public async Task A_gateway_that_keeps_no_directory_says_so_and_an_agent_id_can_still_be_opened()
    {
        _scene.Handler.Override = r => r.RequestUri!.AbsolutePath.EndsWith("identities", StringComparison.Ordinal)
            ? AgentsScene.Json(AgentsScene.Read("agents-identities.disabled.synthetic.json"))
            : null;

        var vm = await _scene.OpenAsync();

        Assert.Equal(AgentsState.Disabled, _scene.Ui(() => vm.State));
        Assert.Equal(AgentsPanelViewModel.DisabledTitle, _scene.Ui(() => vm.StateTitle));
        Assert.Empty(_scene.Ui(() => vm.Agents.ToArray()));

        _scene.Ui(() =>
        {
            vm.LookupText = AgentsScene.Root;
            vm.OpenLookupCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => !vm.IsOpening && vm.HasLedger && vm.HasConflicts, "the agent to open by id");
        Assert.Single(_scene.Ui(() => vm.TreeRoots.ToArray()));
        Assert.Equal("Agent agent-root-0…aaaaaa", _scene.Ui(() => vm.SelectedTitle));
    }

    [Fact]
    public async Task An_empty_directory_is_a_state_of_its_own_and_not_a_failure()
    {
        _scene.Handler.Override = r => r.RequestUri!.AbsolutePath.EndsWith("identities", StringComparison.Ordinal)
            ? AgentsScene.Json("{\"enabled\":true,\"persisted\":true,\"identities\":null,\"total\":0,\"next_cursor\":\"\"}")
            : null;

        var vm = await _scene.OpenAsync();

        Assert.Equal(AgentsState.Empty, _scene.Ui(() => vm.State));
        Assert.Equal(AgentsPanelViewModel.EmptyTitle, _scene.Ui(() => vm.StateTitle));
        Assert.False(_scene.Ui(() => vm.IsStale));
    }

    // ------------------------------------------------------------------ one agent

    [Fact]
    public async Task Opening_an_identity_walks_its_last_session_to_the_agents_around_it_and_builds_the_tree_with_every_edge_on_show()
    {
        var vm = await OpenWithRootAsync();

        var root = Assert.Single(_scene.Ui(() => vm.TreeRoots.ToArray()));
        Assert.Equal(AgentsScene.Root, root.AgentId);
        var children = _scene.Ui(() => root.Children.Select(c => $"{c.MethodLabel}|{c.StatusLabel}|{c.MethodTone}|{c.StatusTone}").ToArray());
        Assert.Equal(
            ["reported|active|Ok|Ok", "inferred|unresolved|Medium|Warn", "derived|conflicted|Low|High", "telepathy|newstatus|Neutral|Neutral"],
            children);
        Assert.Equal("trace exact", _scene.Ui(() => root.Children[0].Children.Single().MethodLabel));
        Assert.Contains("inferred, unresolved", _scene.Ui(() => root.Children[1].AutomationName), StringComparison.Ordinal);

        Assert.True(_scene.Ui(() => vm.ShowTree));
        Assert.Equal("Conflicts: 1 relationship is conflicted; 2 deliveries contradicted an earlier one.", _scene.Ui(() => vm.ConflictsText));
        Assert.Equal("codex agent agt-0000000000000001", _scene.Ui(() => vm.SelectedTitle));
    }

    [Fact]
    public async Task The_timeline_is_in_time_order_grouped_by_turn_with_a_header_per_group()
    {
        _ = await OpenWithRootAsync();

        Assert.Equal(
            ["No turn reported · 2 entries", "Turn turn-0001 · 8 entries", "Turn turn-c1 · 1 entry"],
            Headers());

        // Root and subagent together; inside each turn group the entries are in time order, and the subagent's own turn comes last.
        Assert.Equal(
            [
                "Session started", "Subagent started",
                "Prompt submitted", "Prompt submitted", "Model request", "Tool call", "Hook decision", "Tool call blocked", "Approval requested", "Approval resolved",
                "Tool call",
            ],
            Entries());
        Assert.StartsWith("11 entries in time order, grouped by turn", _scene.Ui(() => _scene.Vm.TimelineCaption), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_counts_are_the_enforced_blocks_and_what_the_ledger_holds_and_the_rest_say_not_reported()
    {
        _ = await OpenWithRootAsync();

        Assert.Equal("1", Kpi("Enforced blocks"));
        Assert.Equal("Not reported", Kpi("Observe-mode would-blocks"));
        Assert.Equal("Not reported", Kpi("Tokens"));
        Assert.Equal("Not reported", Kpi("Cost"));
        Assert.Equal("1", Kpi("Guardrail evaluations"));
        Assert.Equal("1 asked · 1 settled", Kpi("Approvals"));
        Assert.Equal("1", Kpi("Model calls"));
        Assert.Equal("2", Kpi("Tool calls"));

        var unreported = _scene.Ui(() => _scene.Vm.Kpis.Where(k => !k.IsReported).Select(k => k.Label).ToArray());
        Assert.Equal(["Observe-mode would-blocks", "Tokens", "Cost"], unreported);
        Assert.All(_scene.Ui(() => _scene.Vm.Kpis.Where(k => !k.IsReported).ToArray()), k => Assert.Contains("Not reported", k.Note, StringComparison.Ordinal));
        Assert.Contains("loaded so far", _scene.Ui(() => _scene.Vm.KpiCaption), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_more_asks_for_the_next_page_with_the_cursor_and_adds_to_the_timeline_and_the_counts()
    {
        var vm = await OpenWithRootAsync();
        Assert.True(_scene.Ui(() => vm.CanLoadMore));
        Assert.Contains("more events are available", _scene.Ui(() => vm.TimelineCaption), StringComparison.Ordinal);

        _scene.Ui(() =>
        {
            vm.LoadMoreCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => !vm.CanLoadMore && !vm.IsPaging, "the next page");

        Assert.Equal("2", Kpi("Enforced blocks"));
        Assert.Equal("2", Kpi("Model calls"));
        Assert.Contains("Turn turn-0002 · 1 entry", Headers());
        Assert.EndsWith(
            "&after_time=2026-10-01T10%3A00%3A07.000000000Z&after_id=01950000-0000-7000-8000-0000000000e8",
            _scene.Handler.Paths.Last(p => p.StartsWith("api/v1/correlation/graph?agent_id=" + AgentsScene.Root, StringComparison.Ordinal)),
            StringComparison.Ordinal);
        Assert.Contains("Counts cover every event", _scene.Ui(() => vm.KpiCaption), StringComparison.Ordinal);
        Assert.DoesNotContain("more events are available", _scene.Ui(() => vm.TimelineCaption), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metric_points_are_hidden_until_asked_for_and_the_caption_says_how_many()
    {
        var vm = await OpenWithRootAsync();
        _scene.Ui(() =>
        {
            vm.LoadMoreCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => !vm.CanLoadMore && !vm.IsPaging, "the next page");

        Assert.Contains("1 metric points hidden", _scene.Ui(() => vm.TimelineCaption), StringComparison.Ordinal);
        var without = Entries().Length;

        _scene.Ui(() =>
        {
            vm.ShowMetrics = true;
            return 0;
        });

        Assert.Equal(without + 1, Entries().Length);
        Assert.DoesNotContain("metric points hidden", _scene.Ui(() => vm.TimelineCaption), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picking_a_subagent_in_the_tree_narrows_the_timeline_and_the_counts_to_it()
    {
        var vm = await OpenWithRootAsync();

        _scene.Ui(() =>
        {
            vm.FocusAgent(AgentsScene.Child);
            return 0;
        });

        Assert.Equal(["Subagent started", "Tool call"], Entries());
        Assert.Equal("0", Kpi("Enforced blocks"));
        Assert.Equal("1", Kpi("Tool calls"));

        _scene.Ui(() =>
        {
            vm.FocusAgent(AgentsScene.Root);
            vm.IncludeSubagents = false;
            return 0;
        });

        Assert.Equal(9, Entries().Length);
        Assert.DoesNotContain("Subagent started", Entries());
        Assert.Equal("1", Kpi("Tool calls"));
    }

    [Fact]
    public async Task An_identity_that_has_not_reported_a_session_cannot_be_opened_and_says_why_without_asking_the_gateway()
    {
        var vm = await _scene.OpenAsync();
        var before = _scene.Handler.Requests.Count;

        _scene.Ui(() => vm.SelectedAgent = vm.Agents.Single(a => a.AgentId == RetiredIdentity));

        Assert.Contains("has not reported a session yet", _scene.Ui(() => vm.OpenMessage), StringComparison.Ordinal);
        Assert.False(_scene.Ui(() => vm.HasLedger));
        Assert.Equal(before, _scene.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_lookup_id_the_gateway_would_refuse_is_explained_and_never_sent()
    {
        var vm = await _scene.OpenAsync();
        var before = _scene.Handler.Requests.Count;

        foreach (var text in new[] { "   ", "tab\tinside", new string('x', 600) })
        {
            _scene.Ui(() =>
            {
                vm.LookupText = text;
                vm.OpenLookupCommand.Execute(null);
                return 0;
            });

            Assert.NotEqual(string.Empty, _scene.Ui(() => vm.LookupProblem));
        }

        Assert.Equal(before, _scene.Handler.Requests.Count);
    }

    [Fact]
    public async Task One_subagent_that_cannot_be_read_is_a_note_and_the_rest_of_the_tree_still_opens()
    {
        _scene.Handler.Override = r => r.RequestUri!.Query.Contains("agent_id=" + AgentsScene.InferredChild, StringComparison.Ordinal)
            ? AgentsScene.Json("{\"error\":\"correlation query failed\"}", HttpStatusCode.InternalServerError)
            : null;
        var vm = await _scene.OpenAsync();

        _scene.Select(CodexIdentity);

        Assert.Single(_scene.Ui(() => vm.TreeRoots.ToArray()));
        Assert.Contains(_scene.Ui(() => vm.Notes.ToArray()), n => n.StartsWith("Could not read agent", StringComparison.Ordinal));
        Assert.Equal(4, _scene.Ui(() => vm.TreeRoots[0].Children.Count));
    }

    [Fact]
    public async Task A_gateway_with_the_directory_but_not_the_ledger_explains_it_on_the_agent_and_keeps_the_directory()
    {
        _scene.Handler.Override = r => r.RequestUri!.AbsolutePath.Contains("correlation", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
            : null;
        var vm = await _scene.OpenAsync();

        _scene.Select(CodexIdentity);

        Assert.Equal(AgentsState.Ready, _scene.Ui(() => vm.State));
        Assert.Contains("does not serve the correlation ledger", _scene.Ui(() => vm.OpenMessage), StringComparison.Ordinal);
        Assert.False(_scene.Ui(() => vm.HasLedger));
        Assert.Empty(_scene.Ui(() => vm.Timeline.ToArray()));
    }

    // ------------------------------------------------------------------ capability off, failures, stale

    [Fact]
    public async Task A_gateway_without_the_routes_is_explained_and_nothing_is_listed()
    {
        _scene.Handler.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };

        var vm = await _scene.OpenAsync();

        Assert.Equal(AgentsState.Unsupported, _scene.Ui(() => vm.State));
        Assert.Equal(AgentsPanelViewModel.UnsupportedTitle, _scene.Ui(() => vm.StateTitle));
        Assert.Contains("DefenseClaw 1.0", _scene.Ui(() => vm.StateDetail), StringComparison.Ordinal);
        Assert.Empty(_scene.Ui(() => vm.Agents.ToArray()));
        Assert.False(_scene.Ui(() => vm.ShowDirectory));
        Assert.True(_scene.Ui(() => vm.ShowStateCard));
    }

    [Fact]
    public async Task A_first_read_that_fails_is_unavailable_and_never_an_empty_directory_and_a_retry_recovers()
    {
        _scene.Handler.Override = _ => throw new HttpRequestException("connection refused");

        var vm = await _scene.OpenAsync();

        Assert.Equal(AgentsState.Unavailable, _scene.Ui(() => vm.State));
        Assert.Contains("not an empty directory", _scene.Ui(() => vm.StateDetail), StringComparison.Ordinal);
        Assert.Contains("The gateway is not answering.", _scene.Ui(() => vm.StateDetail), StringComparison.Ordinal);

        _scene.Handler.Override = null;
        _scene.Ui(() =>
        {
            vm.RefreshCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => vm.State == AgentsState.Ready, "the directory after a retry");
        Assert.Equal(3, _scene.Ui(() => vm.Agents.Count));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_list_marked_stale_and_a_missing_route_clears_it()
    {
        var vm = await _scene.OpenAsync();

        _scene.Handler.Override = _ => throw new HttpRequestException("connection refused");
        _scene.Ui(() =>
        {
            vm.RefreshCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => vm.IsStale, "the stale marker");

        Assert.Equal(AgentsState.Ready, _scene.Ui(() => vm.State));
        Assert.Equal(3, _scene.Ui(() => vm.Agents.Count));
        Assert.StartsWith("The agents below are from the last successful read.", _scene.Ui(() => vm.StaleBanner), StringComparison.Ordinal);
        Assert.EndsWith("(stale)", _scene.Ui(() => vm.CaptionText), StringComparison.Ordinal);

        _scene.Handler.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        _scene.Ui(() =>
        {
            vm.RefreshCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => vm.State == AgentsState.Unsupported, "the version verdict");

        Assert.Empty(_scene.Ui(() => vm.Agents.ToArray()));
        Assert.False(_scene.Ui(() => vm.IsStale));
    }

    [Fact]
    public async Task Everything_the_panel_asks_the_gateway_is_a_get_with_the_token_and_nothing_else_is_sent()
    {
        var vm = await OpenWithRootAsync();
        _scene.Ui(() =>
        {
            vm.LoadMoreCommand.Execute(null);
            return 0;
        });
        UiThread.WaitFor(() => !vm.CanLoadMore && !vm.IsPaging, "the next page");

        Assert.NotEmpty(_scene.Handler.Requests);
        Assert.All(_scene.Handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.StartsWith("Bearer ", r.Headers.Authorization?.ToString(), StringComparison.Ordinal);
            Assert.Null(r.Content);
        });
        Assert.All(_scene.Handler.Paths, p => Assert.True(
            p.StartsWith("api/v1/correlation/", StringComparison.Ordinal) || p.StartsWith("api/v1/agents/identities", StringComparison.Ordinal),
            p));
    }
}
