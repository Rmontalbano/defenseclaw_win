using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Observability;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Observability;

/// <summary>
/// "Open Agent360" on the Overview, its connector rows and the command palette (CUST-354): offered only while the local Grafana answers (a fake probe),
/// otherwise a hint that points to the Local OTel wizard; the link carries the connector of the row or the scope; the coverage note follows the plan.
/// Nothing opens a browser (the URL seam is faked) or a socket.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class Agent360PanelTests : IDisposable
{
    private sealed class Probe : IGrafanaProbe
    {
        public bool Answer { get; set; }

        public Task<bool> ProbeAsync(string baseUrl, CancellationToken cancellationToken) => Task.FromResult(Answer);
    }

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly Probe _probe = new();

    public Agent360PanelTests()
    {
        _services = TestServices.Create(_temp);
        _services.Agent360.Dispose();
        _services.Agent360 = new Agent360Availability(null, _probe);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private static ConnectorRow Row(string name, bool unconfigured = false) =>
        new() { Name = name, StateText = "ok", IsUnconfigured = unconfigured };

    private static ObservabilityPlan NarrowedPlan() =>
        new(
            "canonical_go_compiled_routes",
            8,
            "digest",
            14,
            new[] { new PlanDestination("local-observability", "otlp", true, new[] { "logs" }, 14, new[] { "none" }, false, null) },
            0);

    // ---- the Overview's button ----

    [Fact]
    public void Before_a_look_nothing_is_offered_and_nothing_is_said()
    {
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services));

        Assert.False(vm.CanOpenAgent360);
        Assert.False(vm.HasAgent360Hint);
        Assert.False(vm.HasAgent360Note);
    }

    [Fact]
    public async Task A_grafana_that_does_not_answer_shows_a_hint_that_points_to_the_wizard_and_no_button()
    {
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services));
        _probe.Answer = false;

        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        Assert.False(vm.CanOpenAgent360);
        Assert.True(vm.HasAgent360Hint);
        Assert.Contains("Local OTel wizard", vm.Agent360Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grafana_that_answers_offers_the_button_and_clears_the_hint()
    {
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services));
        _probe.Answer = false;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));
        _probe.Answer = true;

        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        Assert.True(vm.CanOpenAgent360);
        Assert.False(vm.HasAgent360Hint);
    }

    [Fact]
    public async Task The_overview_button_opens_the_dashboard_for_all_connectors_when_not_scoped()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        UiThread.Run(() => vm.OpenAgent360Command.Execute(null));

        Assert.Equal(new[] { "http://localhost:3000/d/defenseclaw-agent-360?from=now-6h&to=now" }, opened);
    }

    [Fact]
    public async Task The_overview_button_follows_the_connector_scope()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
        Assert.True(_services.ConnectorScope.Set("codex"));
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        UiThread.Run(() => vm.OpenAgent360Command.Execute(null));

        Assert.Equal(new[] { "http://localhost:3000/d/defenseclaw-agent-360?var-connector=codex&from=now-6h&to=now" }, opened);
    }

    [Fact]
    public async Task A_grafana_that_went_away_since_the_button_was_drawn_opens_nothing_and_says_why()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));
        _probe.Answer = false;
        await _services.Agent360.RefreshAsync();

        UiThread.Run(() => vm.OpenAgent360Command.Execute(null));

        Assert.Empty(opened);
        Assert.False(vm.CanOpenAgent360);
        Assert.Contains("Local OTel wizard", vm.Agent360Hint, StringComparison.Ordinal);
    }

    // ---- a connector row ----

    [Fact]
    public async Task A_connector_rows_button_opens_the_dashboard_for_that_connector()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        UiThread.Run(() => vm.OpenConnectorAgent360Command.Execute(Row("claudecode")));

        Assert.Equal(new[] { "http://localhost:3000/d/defenseclaw-agent-360?var-connector=claudecode&from=now-6h&to=now" }, opened);
    }

    [Fact]
    public async Task A_connector_that_is_not_configured_has_no_dashboard_to_open()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        UiThread.Run(() => vm.OpenConnectorAgent360Command.Execute(Row("codex", unconfigured: true)));
        UiThread.Run(() => vm.OpenConnectorAgent360Command.Execute(null));

        Assert.Empty(opened);
    }

    [Fact]
    public async Task A_connector_name_with_query_characters_cannot_add_a_parameter()
    {
        var opened = new List<string>();
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services) { Agent360Opener = opened.Add });
        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        UiThread.Run(() => vm.OpenConnectorAgent360Command.Execute(Row("x&from=0")));

        var url = Assert.Single(opened);
        Assert.Contains("var-connector=x%26from%3D0&from=now-6h&to=now", url, StringComparison.Ordinal);
    }

    // ---- the plan ----

    [Fact]
    public async Task A_narrowed_plan_adds_the_partial_coverage_note_only_while_the_stack_answers()
    {
        var vm = UiThread.Run(() => new OverviewPanelViewModel(_services));
        _probe.Answer = false;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));
        UiThread.Run(() => vm.ApplyObservabilityPlan(new ObservabilityPlanRead(ObservabilityPlanStatus.Ok, NarrowedPlan(), string.Empty, DateTimeOffset.UtcNow)));
        Assert.False(vm.HasAgent360Note);

        _probe.Answer = true;
        await UiThread.Run(() => vm.RefreshAgent360Async(force: true, CancellationToken.None));

        Assert.True(vm.HasAgent360Note);
        Assert.StartsWith("Partial coverage", vm.Agent360Note, StringComparison.Ordinal);
    }

    // ---- the palette ----

    private (ShellActions Actions, IReadOnlyList<ShellCommand> Rows, List<string> Opened) Palette()
    {
        var opened = new List<string>();
        var catalog = new PanelCatalog(_services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var actions = new ShellActions(_services, catalog, tray, () => null) { UrlOpener = opened.Add };
        var rows = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, curated: Array.Empty<CuratedCommand>(), connectorScope: _services.ConnectorScope);
        return (actions, rows, opened);
    }

    [Fact]
    public void The_palette_row_is_greyed_out_with_the_wizard_pointer_until_grafana_answers()
    {
        StaThread.Run(() =>
        {
            var (_, rows, opened) = Palette();
            var row = Assert.Single(rows, r => r.Id == "app.open-agent360");

            Assert.Equal("Open Agent360", row.Title);
            Assert.False(row.IsEnabled);
            Assert.NotNull(row.DisabledReason);
            Assert.Empty(opened);
        });
    }

    [Fact]
    public async Task The_palette_row_opens_the_dashboard_once_grafana_answers()
    {
        _probe.Answer = true;
        await _services.Agent360.RefreshAsync();

        StaThread.Run(() =>
        {
            var (_, rows, opened) = Palette();
            var row = Assert.Single(rows, r => r.Id == "app.open-agent360");

            Assert.True(row.IsEnabled);
            row.Run();

            Assert.Equal(new[] { "http://localhost:3000/d/defenseclaw-agent-360?from=now-6h&to=now" }, opened);
        });
    }
}
