using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// CUST-261 on the Alerts panel's view-model: the search box's <c>field:value</c> tokens through the shared parser, the free text that now also covers the run,
/// severity, source and correlation ids (a trace id pasted from another tool finds its alert), the correlation rows an alert carries, the TUI's multi-line
/// Copy details, and the rule behind the Connector column. Synthetic findings from the real DDL; the panel runs on a plain STA thread.
/// </summary>
public sealed class AlertsSearchTokenTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private const string TraceOne = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string TraceTwo = "00f067aa0ba902b700f067aa0ba902b7";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public AlertsSearchTokenTests()
    {
        _services = TestServices.Create(_temp);
        _ = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
        Seed();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string Db => _services.Paths.AuditDatabasePath;

    /// <summary>Five findings, oldest first: two connectors, one platform finding, one connector hook, and ids on most of them.</summary>
    private void Seed()
    {
        CorrelatedRows.Add(Db, "f-1", Base.AddMinutes(1), "scan-finding", "HIGH", "claudecode", target: "/skills/evil", actor: "scanner", details: "found a bad thing",
            runId: "run-aaa", traceId: TraceOne, requestId: "req-11111111", sessionId: "ses-aaaa");
        CorrelatedRows.Add(Db, "f-2", Base.AddMinutes(2), "skill-block", "CRITICAL", "codex", target: "evil-skill", actor: "cli", details: "blocked by policy",
            runId: "run-bbb", traceId: TraceTwo, requestId: "req-22222222", sessionId: "ses-bbbb");
        CorrelatedRows.Add(Db, "f-3", Base.AddMinutes(3), "guardrail-block", "MEDIUM", "claudecode", target: "skills/my skill", actor: "gateway", details: "saw foo:bar here");
        CorrelatedRows.Add(Db, "f-4", Base.AddMinutes(4), "config-drift", "LOW", null, target: "config.yaml");
        CorrelatedRows.Add(
            Db, "h-1", Base.AddMinutes(5), "connector-hook", "HIGH", "claudecode", bucket: null, target: "preToolUse",
            details: "connector=claudecode action=block severity=HIGH mode=enforce would_block=true elapsed=41ms",
            runId: "run-ccc", traceId: "abcdef0123456789abcdef0123456789", requestId: "req-33333333", sessionId: "ses-cccc");
    }

    private static string[] Ids(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Key).ToArray();

    private static AlertItem Item(AlertsPanelViewModel vm, string id) => vm.Alerts.Single(a => a.Key == id);

    /// <summary>A panel over the five findings, loaded (the queue read is bounded by the suite's ceiling: <c>TestServices.ReaderTimeouts</c>).</summary>
    private async Task<AlertsPanelViewModel> LoadedAsync()
    {
        var vm = new AlertsPanelViewModel(_services);
        await vm.InitializeAsync();
        Assert.Equal(5, vm.Alerts.Count);
        return vm;
    }

    // ------------------------------------------------------------------ tokens

    [Fact]
    public void A_connector_token_narrows_the_list_to_that_connector()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            vm.FilterText = "connector:claudecode";
            Assert.Equal(new[] { "h-1", "f-3", "f-1" }, Ids(vm));

            // The name is matched whole, whatever its case; another connector's alerts and the platform finding are not in it.
            vm.FilterText = "Connector:CODEX";
            Assert.Equal(new[] { "f-2" }, Ids(vm));
            vm.FilterText = "connector:claude";
            Assert.Empty(vm.Alerts);
            vm.FilterText = "connector:nobody";
            Assert.Empty(vm.Alerts);
            Assert.Equal("No alerts match the current filters", vm.EmptyTitle);

            vm.FilterText = string.Empty;
            Assert.Equal(5, vm.Alerts.Count);
        });
    }

    [Theory]
    [InlineData("severity:high", new[] { "h-1", "f-1" })]
    [InlineData("severity:CRIT", new[] { "f-2" })]
    [InlineData("severity:medium", new[] { "f-3" })]
    [InlineData("run:run-a", new[] { "f-1" })]
    [InlineData("run_id:BBB", new[] { "f-2" })]
    [InlineData("id:f-4", new[] { "f-4" })]
    [InlineData("actor:cli", new[] { "f-2" })]
    [InlineData("actor:SCAN", new[] { "f-1" })]
    [InlineData("type:skill", new[] { "f-2" })]
    [InlineData("type:scan", new[] { "f-1" })]
    [InlineData("target:evil", new[] { "f-2", "f-1" })]
    [InlineData("target:\"my skill\"", new[] { "f-3" })]
    [InlineData("action:block", new[] { "f-3", "f-2" })]
    [InlineData("details:found", new[] { "f-1" })]
    [InlineData("trace:4bf9", new[] { "f-1" })]
    [InlineData("request:req-2222", new[] { "f-2" })]
    [InlineData("session:ses-cccc", new[] { "h-1" })]
    public void Each_token_narrows_to_its_field(string typed, string[] expected)
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            vm.FilterText = typed;

            Assert.Equal(expected, Ids(vm));
        });
    }

    [Fact]
    public void Tokens_and_free_text_narrow_together_and_with_the_other_filters()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            vm.FilterText = "connector:claudecode severity:high found";
            Assert.Equal(new[] { "f-1" }, Ids(vm));

            vm.FilterText = "connector:claudecode severity:high missing";
            Assert.Empty(vm.Alerts);

            // The severity tiles still narrow beside the box.
            vm.FilterText = "connector:claudecode";
            vm.SelectSeverityCommand.Execute(vm.Tiles[1]);
            Assert.Equal(new[] { "h-1", "f-1" }, Ids(vm));
        });
    }

    [Fact]
    public void The_shared_connector_scope_and_a_typed_connector_must_both_hold()
    {
        StaThread.Run(async () =>
        {
            _services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.True(_services.ConnectorScope.Set("codex"));
            var vm = await LoadedUnderScopeAsync();

            // Scoped to codex: one alert; a typed claudecode is another connector, so nothing.
            Assert.Equal(new[] { "f-2" }, Ids(vm));
            vm.FilterText = "connector:claudecode";
            Assert.Empty(vm.Alerts);
            vm.FilterText = "connector:codex";
            Assert.Equal(new[] { "f-2" }, Ids(vm));
        });
    }

    /// <summary>Loaded under a scope: the list is the scope's rows (codex's one), not all five.</summary>
    private async Task<AlertsPanelViewModel> LoadedUnderScopeAsync()
    {
        var vm = new AlertsPanelViewModel(_services);
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public void Words_that_are_not_tokens_are_free_text_exactly_as_typed()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            // foo: is no field, so foo:bar is the phrase to look for - and f-3 says it.
            vm.FilterText = "foo:bar";
            Assert.Equal(new[] { "f-3" }, Ids(vm));

            // A token with no value yet (the name being typed) narrows nothing.
            vm.FilterText = "connector:";
            Assert.Equal(5, vm.Alerts.Count);

            // Quoted words are one phrase.
            vm.FilterText = "\"bad thing\"";
            Assert.Equal(new[] { "f-1" }, Ids(vm));
        });
    }

    // ------------------------------------------------------------------ free text covers more

    [Theory]
    [InlineData(TraceOne, new[] { "f-1" })]
    [InlineData("4bf92f35", new[] { "f-1" })]
    [InlineData(TraceTwo, new[] { "f-2" })]
    [InlineData("req-33333333", new[] { "h-1" })]
    [InlineData("ses-bbbb", new[] { "f-2" })]
    [InlineData("run-ccc", new[] { "h-1" })]
    public void A_pasted_id_finds_its_alert_with_no_token_at_all(string pasted, string[] expected)
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            vm.FilterText = pasted;

            Assert.Equal(expected, Ids(vm));
        });
    }

    [Theory]
    [InlineData("CRITICAL", new[] { "f-2" })]
    [InlineData("gateway", new[] { "f-3" })]
    [InlineData("codex", new[] { "f-2" })]
    [InlineData("run-aaa", new[] { "f-1" })]
    public void The_free_text_covers_the_run_the_severity_the_source_and_the_connector_too(string typed, string[] expected)
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            vm.FilterText = typed;

            Assert.Equal(expected, Ids(vm));
        });
    }

    [Fact]
    public void What_a_search_always_covered_it_still_covers()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            foreach (var typed in new[] { "scan-finding", "evil-skill", "/skills/evil", "found a bad", "config.yaml" })
            {
                vm.FilterText = typed;
                Assert.NotEmpty(vm.Alerts);
            }

            vm.FilterText = "no row says this";
            Assert.Empty(vm.Alerts);
        });
    }

    [Fact]
    public void The_alert_items_own_match_is_the_same_search()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();
            var item = Item(vm, "f-1");

            Assert.True(item.Matches(TraceOne));
            Assert.True(item.Matches("bad THING"));
            Assert.False(item.Matches("req-22222222"));
            Assert.True(item.Matches(DefenseClaw.Core.Audit.SearchQuery.Parse("connector:claudecode trace:4bf9 found")));
            Assert.False(item.Matches(DefenseClaw.Core.Audit.SearchQuery.Parse("connector:codex")));
            Assert.True(item.Matches(DefenseClaw.Core.Audit.SearchQuery.Empty));
        });
    }

    // ------------------------------------------------------------------ correlation ids

    [Fact]
    public void An_alert_lists_the_correlation_ids_it_has_in_the_TUIs_order()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            var with = Item(vm, "f-1");
            Assert.True(with.HasCorrelation);
            Assert.Equal(
                new[] { ("Run ID", "run-aaa"), ("Trace ID", TraceOne), ("Request ID", "req-11111111"), ("Session ID", "ses-aaaa") },
                with.Correlation.Select(f => (f.Name, f.Value)).ToArray());

            var without = Item(vm, "f-3");
            Assert.False(without.HasCorrelation);
            Assert.Empty(without.Correlation);
        });
    }

    [Fact]
    public void A_group_of_repeats_keeps_the_ids_of_the_alert_that_stands_for_it()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();
            var original = Item(vm, "f-1");

            var clone = original.CloneForGroup();
            Assert.Equal(TraceOne, clone.TraceId);
            Assert.Equal("req-11111111", clone.RequestId);
            Assert.Equal("ses-aaaa", clone.SessionId);
            Assert.Equal("run-aaa", clone.RunId);
            Assert.Equal("found a bad thing", clone.Details);
            Assert.Equal(original.Correlation.Select(f => f.ToString()), clone.Correlation.Select(f => f.ToString()));
            Assert.Equal(original.CopyDetailText, clone.CopyDetailText);

            // And the row the folded list shows is such a copy.
            vm.CollapseRepeats = true;
            Assert.Equal(TraceOne, Item(vm, "f-1").TraceId);
            Assert.True(Item(vm, "f-1").HasCorrelation);
        });
    }

    // ------------------------------------------------------------------ copy details

    [Fact]
    public void Copy_details_of_one_alert_is_the_TUIs_multi_line_form_with_the_ids()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();
            var item = Item(vm, "f-1");

            var text = LineEndings.Normalize(AlertsPanelViewModel.CopyText(new[] { item }));

            Assert.Equal(
                "Severity: HIGH\n" +
                "Action: scan-finding\n" +
                "Target: /skills/evil\n" +
                "Timestamp: " + item.Timestamp.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                "Details: found a bad thing\n" +
                "Run ID: run-aaa\n" +
                "Trace ID: " + TraceOne + "\n" +
                "Request ID: req-11111111\n" +
                "Session ID: ses-aaaa",
                text);
        });
    }

    [Fact]
    public void An_alert_with_no_ids_has_no_id_lines()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            var text = LineEndings.Normalize(AlertsPanelViewModel.CopyText(new[] { Item(vm, "f-3") }));

            Assert.StartsWith("Severity: MEDIUM\nAction: guardrail-block\nTarget: skills/my skill\nTimestamp: ", text, StringComparison.Ordinal);
            Assert.EndsWith("\nDetails: saw foo:bar here", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ID:", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_hook_call_copies_its_decision_mode_and_timing_as_labelled_lines_like_the_TUI()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            var text = LineEndings.Normalize(AlertsPanelViewModel.CopyText(new[] { Item(vm, "h-1") }));

            var lines = text.Split('\n');
            Assert.Equal(
                new[]
                {
                    "Severity: HIGH",
                    "Action: connector-hook",
                    "Target: preToolUse",
                },
                lines.Take(3));
            Assert.Equal(
                new[]
                {
                    "Connector: claudecode",
                    "Decision: block",
                    "Severity (decision): HIGH",
                    "Enforcement mode: enforce",
                    "Would block: yes",
                    "Elapsed: 41ms",
                    "Run ID: run-ccc",
                    "Trace ID: abcdef0123456789abcdef0123456789",
                    "Request ID: req-33333333",
                    "Session ID: ses-cccc",
                },
                lines.Skip(4));
        });
    }

    [Fact]
    public void Several_alerts_are_copied_one_line_each_as_before()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();
            var one = Item(vm, "f-1");
            var two = Item(vm, "f-2");

            var text = LineEndings.Normalize(AlertsPanelViewModel.CopyText(new[] { one, two }));

            Assert.Equal(one.CopyLine + "\n" + two.CopyLine, text);
            Assert.Equal(string.Empty, AlertsPanelViewModel.CopyText(Array.Empty<AlertItem>()));
        });
    }

    // ------------------------------------------------------------------ the Connector column's rule

    [Fact]
    public void The_connector_column_shows_only_with_more_than_one_connector_active()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);

            Assert.False(vm.ShowConnectorColumn);

            _services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            Assert.False(vm.ShowConnectorColumn);

            _services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.True(vm.ShowConnectorColumn);

            // The same connector spelled twice is one.
            _services.ConnectorScope.UpdateRoster(new[] { "codex", "Codex" });
            Assert.False(vm.ShowConnectorColumn);

            _services.ConnectorScope.UpdateRoster(Array.Empty<string>());
            Assert.False(vm.ShowConnectorColumn);
        });
    }

    [Fact]
    public void The_connector_cell_is_the_connector_or_a_dash()
    {
        StaThread.Run(async () =>
        {
            var vm = await LoadedAsync();

            Assert.Equal("claudecode", Item(vm, "f-1").ConnectorCell);
            Assert.Equal("codex", Item(vm, "f-2").ConnectorCell);
            Assert.Equal("—", Item(vm, "f-4").ConnectorCell);
        });
    }
}
