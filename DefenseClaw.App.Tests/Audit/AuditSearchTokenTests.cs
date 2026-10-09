using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-261 on the Audit panel's view-model: the search box's <c>field:value</c> tokens become filters of the very query the list is read with, so the page,
/// "Load more", the live refresh and the export all honour them; a search of any kind pauses "Actionable only" (CUST-262); words that are not tokens are the
/// search they always were. A synthetic audit.db from the real DDL read by the real reader; the clock is fixed so a refresh never straddles the minute the
/// time window starts on.
/// </summary>
public sealed class AuditSearchTokenTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private AppServices Services => _services!;

    private async Task<AuditPanelViewModel> OpenAsync(Action seed, bool actionableOnly = true)
    {
        AuditTestDatabase.Create(DbPath, 0, newest: _clock.GetUtcNow());
        seed();
        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = actionableOnly };
        panel.SetActive(true);
        await panel.InitializeAsync();
        return panel;
    }

    private void Add(
        string id,
        double secondsAgo,
        string action = "hook_decision",
        string severity = "INFO",
        string? connector = "claudecode",
        string target = "",
        string actor = "audit_logger",
        string? details = "synthetic extra",
        string? runId = null,
        string? traceId = null,
        string? requestId = null,
        string? sessionId = null,
        string? bucket = "guardrail.evaluation") =>
        CorrelatedRows.Add(
            DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, severity, connector, bucket, "evt", target, actor, details, runId, traceId, requestId, sessionId);

    /// <summary>Six rows that differ in one way each; 1, 2, 3 ... seconds old (so newest first is the order they are numbered in).</summary>
    private void Seed()
    {
        Add("a-block", 1, "skill-block", "HIGH", "claudecode", "evil-skill", "cli", "install=block", "run-aaa", "trace-aaaa", "req-aaaa", "ses-aaaa", "enforcement.action");
        Add("b-hook", 2, "connector-hook", "INFO", "codex", "preToolUse", "gateway", "connector=codex action=allow would_block=false", "run-bbb", "trace-bbbb", "req-bbbb", "ses-bbbb");
        Add("c-scan", 3, "scan-finding", "MEDIUM", "claudecode", "skills/my skill", "scanner", "found foo:bar here", "run-ccc", bucket: "security.finding");
        Add("d-config", 4, "config.change.applied", "INFO", null, "config.yaml", "cli", "applied", bucket: "compliance.activity");
        Add("e-tool", 5, "tool_invocation", "LOW", "codex", "Bash", "agent", "ran a command", "run-aaa", bucket: "tool.activity");
        Add("f-quiet", 6);
    }

    private static string[] Ids(AuditPanelViewModel panel) => panel.Rows.Select(r => r.Id).ToArray();

    private async Task Search(AuditPanelViewModel panel, string typed)
    {
        panel.SearchText = typed;
        await panel.LastLoad;
    }

    // ------------------------------------------------------------------ the acceptance

    [Fact]
    public async Task A_connector_token_narrows_the_list_and_pauses_the_actionable_view()
    {
        var panel = await OpenAsync(Seed);
        Assert.True(panel.IsActionableApplied);

        await Search(panel, "connector:claudecode");

        Assert.Equal(new[] { "a-block", "c-scan", "f-quiet" }, Ids(panel));
        Assert.False(panel.IsActionableApplied);
        Assert.True(panel.ActionableOnly);
        Assert.Equal("a search is on", panel.ActionableSuspendedBy);
        Assert.Equal(0, panel.HiddenCount);

        // The name whole and without regard to case; another connector's rows, and the platform row, are not in it.
        await Search(panel, "CONNECTOR:Codex");
        Assert.Equal(new[] { "b-hook", "e-tool" }, Ids(panel));
        await Search(panel, "connector:claude");
        Assert.Empty(Ids(panel));

        // Clearing it brings the actionable view back.
        await Search(panel, string.Empty);
        Assert.True(panel.IsActionableApplied);
        Assert.Equal(new[] { "a-block", "b-hook" }, Ids(panel));
    }

    [Theory]
    [InlineData("severity:high", new[] { "a-block" })]
    [InlineData("run:run-aaa", new[] { "a-block", "e-tool" })]
    [InlineData("id:c-s", new[] { "c-scan" })]
    [InlineData("actor:cli", new[] { "a-block", "d-config" })]
    [InlineData("type:skill", new[] { "a-block" })]
    [InlineData("type:tool", new[] { "e-tool" })]
    [InlineData("type:security", new[] { "c-scan" })]
    [InlineData("target:\"my skill\"", new[] { "c-scan" })]
    [InlineData("action:connector", new[] { "b-hook" })]
    [InlineData("details:would_block", new[] { "b-hook" })]
    [InlineData("trace:bbbb", new[] { "b-hook" })]
    [InlineData("request:req-aaaa", new[] { "a-block" })]
    [InlineData("session:ses-bbbb", new[] { "b-hook" })]
    public async Task Each_token_filters_its_column(string typed, string[] expected)
    {
        var panel = await OpenAsync(Seed);

        await Search(panel, typed);

        Assert.Equal(expected, Ids(panel));
    }

    [Fact]
    public async Task Tokens_free_text_and_the_filter_bar_all_narrow_together()
    {
        var panel = await OpenAsync(Seed);

        await Search(panel, "connector:claudecode foo:bar");
        Assert.Equal(new[] { "c-scan" }, Ids(panel));

        await Search(panel, "connector:claudecode severity:high");
        Assert.Equal(new[] { "a-block" }, Ids(panel));

        // The filter bar is still there beside the box: a minimum severity and a typed connector both hold.
        panel.SelectedSeverity = SeverityOption.All.Single(o => o.Value == DefenseClaw.Core.Audit.AuditSeverity.High);
        await Search(panel, "connector:claudecode");
        Assert.Equal(new[] { "a-block" }, Ids(panel));
        await Search(panel, "connector:codex");
        Assert.Empty(Ids(panel));
    }

    [Fact]
    public async Task Words_that_are_not_tokens_are_the_search_they_always_were()
    {
        var panel = await OpenAsync(Seed);

        // foo: is no field, so foo:bar is the phrase to look for; the details of c-scan say it.
        await Search(panel, "foo:bar");
        Assert.Equal(new[] { "c-scan" }, Ids(panel));

        await Search(panel, "synthetic");
        Assert.Equal(new[] { "f-quiet" }, Ids(panel));

        // A token with no value yet (the name being typed) narrows nothing.
        await Search(panel, "connector:");
        Assert.Equal(6, panel.Rows.Count);
    }

    [Fact]
    public async Task Reset_filters_clears_the_tokens_with_the_rest()
    {
        var panel = await OpenAsync(Seed);
        await Search(panel, "connector:codex actor:gateway");
        Assert.Equal(new[] { "b-hook" }, Ids(panel));

        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;

        Assert.Equal(string.Empty, panel.SearchText);
        Assert.Equal(new[] { "a-block", "b-hook" }, Ids(panel));
    }

    [Fact]
    public async Task Show_same_target_searches_for_the_target_as_text_even_when_it_reads_like_a_token()
    {
        var panel = await OpenAsync(
            () =>
            {
                Add("tok-1", 1, target: "type:abc", details: "x");
                Add("tok-2", 2, target: "abc", details: "x");
            },
            actionableOnly: false);
        var row = panel.Rows.Single(r => r.Id == "tok-1");

        panel.ShowSameTarget(row);
        await panel.LastLoad;

        // Typed as text ("type:abc" in quotes), so it finds the one event about that target rather than every event of a type.
        Assert.Equal("\"type:abc\"", panel.SearchText);
        Assert.Equal(new[] { "tok-1" }, Ids(panel));

        panel.ShowSameTarget(panel.Rows.Single());
        await panel.LastLoad;
        Assert.Equal("\"type:abc\"", panel.SearchText);
    }

    // ------------------------------------------------------------------ everything that reads the list's query follows it

    [Fact]
    public async Task Load_more_continues_a_filtered_list_without_gaps_or_repeats()
    {
        var panel = await OpenAsync(
            () =>
            {
                for (var i = 0; i < 130; i++)
                {
                    Add($"codex-{i:D3}", 10 + i, connector: "codex", details: "x");
                }

                for (var i = 0; i < 40; i++)
                {
                    Add($"claude-{i:D3}", 10.5 + i * 3, connector: "claudecode", details: "x");
                }
            });

        await Search(panel, "connector:codex");

        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.True(panel.HasMore);

        await panel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(130, panel.Rows.Count);
        Assert.False(panel.HasMore);
        Assert.All(panel.Rows, r => Assert.StartsWith("codex-", r.Id, StringComparison.Ordinal));
        Assert.Equal(130, panel.Rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal("130 of 130 matching events · Last 24 hours", panel.ResultSummary);
    }

    [Fact]
    public async Task The_live_refresh_adds_only_the_new_rows_that_match_the_search()
    {
        var panel = await OpenAsync(Seed);
        await Search(panel, "connector:codex");
        Assert.Equal(new[] { "b-hook", "e-tool" }, Ids(panel));

        // A codex event and a claudecode one arrive: the search is on, so only the first is listed.
        Add("new-codex", 0.5, connector: "codex", details: "x");
        Add("new-claude", 0.6, connector: "claudecode", details: "x");
        await panel.PollLiveAsync();

        Assert.Equal(new[] { "new-codex", "b-hook", "e-tool" }, Ids(panel));

        // And one that matches a token but not the free text beside it stays out.
        await Search(panel, "connector:codex ran");
        Assert.Equal(new[] { "e-tool" }, Ids(panel));
        Add("new-codex-2", 0.4, connector: "codex", details: "x");
        await panel.PollLiveAsync();
        Assert.Equal(new[] { "e-tool" }, Ids(panel));
    }

    [Fact]
    public async Task The_export_writes_the_rows_the_search_selects()
    {
        var panel = await OpenAsync(Seed);
        var path = _temp.File("search-export.json");
        panel.ExportPathPicker = () => path;
        await Search(panel, "connector:codex");

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var ids = document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { "b-hook", "e-tool" }, ids.Order(StringComparer.Ordinal));
        Assert.Contains("2 matching events", panel.ExportNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_total_counts_the_filtered_rows_when_the_view_is_not_narrowed()
    {
        var panel = await OpenAsync(Seed, actionableOnly: false);

        await Search(panel, "actor:cli");

        Assert.Equal(new[] { "a-block", "d-config" }, Ids(panel));
        Assert.Equal("2 of 2 matching events · Last 24 hours", panel.ResultSummary);
    }

    // ------------------------------------------------------------------ the Connector column's rule

    [Fact]
    public async Task The_connector_column_shows_only_with_more_than_one_connector_active()
    {
        var panel = await OpenAsync(Seed);

        Assert.False(panel.ShowConnectorColumn);
        Services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
        Assert.False(panel.ShowConnectorColumn);
        Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
        Assert.True(panel.ShowConnectorColumn);
        Services.ConnectorScope.UpdateRoster(new[] { "codex", "Codex" });
        Assert.False(panel.ShowConnectorColumn);
    }

    [Fact]
    public async Task The_connector_cell_is_the_connector_or_a_dash()
    {
        var panel = await OpenAsync(Seed, actionableOnly: false);

        Assert.Equal("claudecode", panel.Rows.Single(r => r.Id == "a-block").ConnectorCell);
        Assert.Equal("codex", panel.Rows.Single(r => r.Id == "b-hook").ConnectorCell);
        Assert.Equal("—", panel.Rows.Single(r => r.Id == "d-config").ConnectorCell);
        Assert.Equal("platform", panel.Rows.Single(r => r.Id == "d-config").Connector);
    }
}
