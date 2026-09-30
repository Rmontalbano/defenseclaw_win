using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// How the Audit panel starts loads. A filter change starts a load and nothing awaits it, so every burst of changes -
/// a search box typed into, the seven properties <c>Reset filters</c> sets - used to be that many full queries and counts
/// on a database of several gigabytes, each one running to the end even though only the last one was ever shown. The
/// reader's call counters (<see cref="DefenseClaw.Core.Audit.AuditReader.PageQueryCount"/>) are what make "how many
/// queries did that cost" assertable rather than a matter of latency.
/// </summary>
public sealed class AuditPanelLoadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private AuditPanelViewModel PanelOver(int rows)
    {
        AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), rows);
        _services = TestServices.Create(_temp);
        return new AuditPanelViewModel(_services);
    }

    private long PageQueries => _services!.Audit.PageQueryCount;

    private long CountQueries => _services!.Audit.CountQueryCount;

    // ------------------------------------------------------------------ one load for one burst

    [Fact]
    public async Task Resetting_the_filters_from_a_fully_filtered_state_issues_one_page_query_and_one_count()
    {
        var panel = PanelOver(300);
        await panel.InitializeAsync();
        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);

        // Every one of the seven filters away from its default.
        panel.SelectedBucket = "guardrail.evaluation";
        panel.SelectedSeverity = SeverityOption.All[^1];
        panel.SelectedConnector = panel.Connectors[1];
        panel.SelectedRange = TimeRangeOption.All[2];
        panel.SelectedActionOption = "hook_decision";
        panel.SearchText = "synthetic";
        await panel.LastLoad;
        Assert.Equal("hook_decision", panel.ActionFilter);

        var pagesBefore = PageQueries;
        var countsBefore = CountQueries;

        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;

        Assert.Equal(1, PageQueries - pagesBefore);
        Assert.Equal(1, CountQueries - countsBefore);

        // ... and it is the reset state that was loaded.
        Assert.Equal("All buckets", panel.SelectedBucket);
        Assert.Same(SeverityOption.Any, panel.SelectedSeverity);
        Assert.Same(ConnectorOption.All, panel.SelectedConnector);
        Assert.Same(TimeRangeOption.Day, panel.SelectedRange);
        Assert.Equal(string.Empty, panel.ActionFilter);
        Assert.Equal(string.Empty, panel.SearchText);
        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.Contains("of 300 matching events", panel.ResultSummary, StringComparison.Ordinal);
        Assert.False(panel.IsLoading);
    }

    [Fact]
    public async Task Resetting_filters_that_are_already_at_their_defaults_loads_nothing()
    {
        var panel = PanelOver(50);
        await panel.InitializeAsync();
        var pagesBefore = PageQueries;

        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;

        Assert.Equal(0, PageQueries - pagesBefore);
    }

    [Fact]
    public async Task A_burst_of_search_changes_queued_behind_a_running_load_runs_only_the_last_value()
    {
        var panel = PanelOver(300);
        await panel.InitializeAsync();
        var pagesBefore = PageQueries;
        var countsBefore = CountQueries;

        // A load is "running": nothing can query until it lets go, which is how a slow search looks to the next keystroke.
        await panel.LoadGate.WaitAsync();
        foreach (var typed in new[] { "s", "sy", "syn", "synt", "synth", "synthetic event 7" })
        {
            panel.SearchText = typed;
        }

        // Five of the six are already cancelled while they wait; none has issued a query.
        Assert.Equal(0, PageQueries - pagesBefore);

        _ = panel.LoadGate.Release();
        await panel.LastLoad;

        Assert.Equal(1, PageQueries - pagesBefore);
        Assert.Equal(1, CountQueries - countsBefore);

        // The rows are the last value's: "synthetic event 7", 70-79, 700..., within 300 rows: 7, 70-79, 170-179, 270-279.
        Assert.NotEmpty(panel.Rows);
        Assert.All(panel.Rows, row => Assert.Contains("synthetic event 7", row.Details, StringComparison.Ordinal));
        Assert.False(panel.IsLoading);
    }

    [Fact]
    public async Task A_load_that_was_superseded_before_it_started_never_shows_its_rows()
    {
        var panel = PanelOver(300);
        await panel.InitializeAsync();

        await panel.LoadGate.WaitAsync();
        panel.SearchText = "synthetic event 1";
        var superseded = panel.LastLoad;
        panel.SearchText = "synthetic event 2";
        var current = panel.LastLoad;

        // The older one ends as soon as it is replaced, without waiting for the gate.
        await superseded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(panel.Rows.Count == AuditPanelViewModel.PageSize, "the old rows stay until the newest load replaces them");

        _ = panel.LoadGate.Release();
        await current;

        Assert.All(panel.Rows, row => Assert.Contains("synthetic event 2", row.Details, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the first open

    [Fact]
    public async Task The_first_open_fills_the_filter_lists_and_the_first_page_together()
    {
        var panel = PanelOver(120);

        await panel.InitializeAsync();

        Assert.Contains("guardrail.evaluation", panel.Buckets);
        Assert.Contains(panel.Connectors, c => c.Connector == "claudecode" && !c.IncludeNull);
        Assert.Contains(panel.Connectors, c => c.PlatformOnly);
        Assert.Contains("hook_decision", panel.Actions);
        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);

        // A second load does not add them again.
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.Single(panel.Buckets, b => b == "guardrail.evaluation");
    }

    // ------------------------------------------------------------------ a NULL id (D3-15)

    [Fact]
    public async Task A_row_with_a_null_id_no_longer_takes_the_whole_page_down()
    {
        var panel = PanelOver(20);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_temp.Path, "audit.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, actor, severity, details)
                VALUES (NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 minute'), 'orphan', 'audit_logger', 'HIGH', 'a row whose id is NULL')
                """;
            _ = command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        await panel.InitializeAsync();

        Assert.Equal(string.Empty, panel.StatusNote);
        Assert.Equal(21, panel.Rows.Count);
        var orphan = Assert.Single(panel.Rows, r => r.Action == "orphan");
        Assert.Equal(string.Empty, orphan.Id);
        Assert.Contains(orphan.Fields, f => f.Name == "id" && f.Value.Contains("NULL", StringComparison.Ordinal));
    }
}
