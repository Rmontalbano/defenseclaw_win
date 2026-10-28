using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The runtime's alert list in the Alerts panel (CUST-374): when the service asks the CLI (capability, interval, force), what it keeps when a run
/// fails ("Alerts may be stale"), "No such command" as not supported, the merge with the database queue without duplicates, and the runtime facts on a row.
/// Synthetic lists in the shape upstream's <c>alerts --json</c> prints; no process is started.
/// </summary>
public sealed class RuntimeAlertsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Printed(params (string Id, string Rule)[] rows) =>
        "[\n" + string.Join(",\n", rows.Select(r =>
            $"  {{\n    \"action\": \"scan-finding\",\n    \"actor\": \"audit_logger\",\n    \"connector\": \"claudecode\",\n    \"details\": \"finding.observed\",\n    \"id\": \"{r.Id}\",\n    \"rule\": \"{r.Rule}\",\n    \"severity\": \"HIGH\",\n    \"target\": \"/synthetic/{r.Id}\",\n    \"timestamp\": \"{Now:o}\"\n  }}")) + "\n]\n";

    /// <summary>A service over a scripted CLI: each run takes the next answer; the count of runs and the time are the test's to read.</summary>
    private sealed class Harness
    {
        private readonly Queue<RuntimeAlertFetch> _answers = new();

        public Harness()
        {
            Service = new RuntimeAlertsService(
                () => Enabled,
                _ =>
                {
                    Runs++;
                    return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : new RuntimeAlertFetch(true, "[]", null));
                },
                (since, _) => Task.FromResult(DetectionOnly),
                action => action(),
                Time);
        }

        public RuntimeAlertsService Service { get; }

        public ManualTime Time { get; } = new(Now);

        public bool Enabled { get; set; } = true;

        public int DetectionOnly { get; set; }

        public int Runs { get; private set; }

        public void Answer(RuntimeAlertFetch fetch) => _answers.Enqueue(fetch);

        /// <summary>Requests a run and waits until none is going.</summary>
        public async Task RequestAsync(bool force)
        {
            Service.Request(force);
            await Service.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Without_the_capability_nothing_is_run_and_nothing_is_known()
    {
        var harness = new Harness { Enabled = false };

        await harness.RequestAsync(force: true);

        Assert.Equal(0, harness.Runs);
        Assert.Equal(RuntimeAlertState.Empty, harness.Service.Current);
        Assert.False(harness.Service.IsEnabled);
    }

    [Fact]
    public async Task A_run_keeps_the_list_and_the_detection_only_count()
    {
        var harness = new Harness { DetectionOnly = 3 };
        harness.Answer(new RuntimeAlertFetch(true, Printed(("a", "R-1: One"), ("b", "R-2: Two")), null));

        await harness.RequestAsync(force: false);

        var state = harness.Service.Current;
        Assert.Equal(1, harness.Runs);
        Assert.Equal(["a", "b"], state.Rows.Select(r => r.Id));
        Assert.Equal(Now, state.FetchedAt);
        Assert.Null(state.StaleReason);
        Assert.Equal(3, state.DetectionOnly24h);
    }

    [Fact]
    public async Task Requests_inside_the_interval_do_not_start_another_run_unless_forced()
    {
        var harness = new Harness();

        await harness.RequestAsync(force: false);
        harness.Time.Now = Now.AddSeconds(30);
        await harness.RequestAsync(force: false);
        Assert.Equal(1, harness.Runs);

        await harness.RequestAsync(force: true);
        Assert.Equal(2, harness.Runs);

        harness.Time.Now = Now.AddSeconds(30) + RuntimeAlertsService.MinInterval + TimeSpan.FromSeconds(1);
        await harness.RequestAsync(force: false);
        Assert.Equal(3, harness.Runs);
    }

    [Fact]
    public async Task A_failed_run_keeps_the_last_good_list_and_says_why_it_may_be_stale()
    {
        var harness = new Harness();
        harness.Answer(new RuntimeAlertFetch(true, Printed(("a", "R-1: One")), null));
        await harness.RequestAsync(force: false);

        harness.Answer(new RuntimeAlertFetch(false, string.Empty, "the runtime's alert command failed: audit store is locked"));
        await harness.RequestAsync(force: true);

        var state = harness.Service.Current;
        Assert.Equal(["a"], state.Rows.Select(r => r.Id));
        Assert.Equal("the runtime's alert command failed: audit store is locked", state.StaleReason);

        harness.Answer(new RuntimeAlertFetch(true, Printed(("a", "R-1: One"), ("c", "R-3: Three")), null));
        await harness.RequestAsync(force: true);

        Assert.Null(harness.Service.Current.StaleReason);
        Assert.Equal(["a", "c"], harness.Service.Current.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Output_that_is_not_the_list_is_stale_not_empty()
    {
        var harness = new Harness();
        harness.Answer(new RuntimeAlertFetch(true, Printed(("a", "R-1: One")), null));
        await harness.RequestAsync(force: false);

        harness.Answer(new RuntimeAlertFetch(true, "Traceback (most recent call last):", null));
        await harness.RequestAsync(force: true);

        Assert.Equal(["a"], harness.Service.Current.Rows.Select(r => r.Id));
        Assert.Contains("not in the expected form", harness.Service.Current.StaleReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_such_command_is_not_supported_and_neither_a_banner_nor_another_run()
    {
        var harness = new Harness();
        harness.Answer(new RuntimeAlertFetch(false, string.Empty, null, NotSupported: true));

        await harness.RequestAsync(force: false);
        harness.Time.Now = Now.AddMinutes(10);
        await harness.RequestAsync(force: false);

        Assert.Equal(1, harness.Runs);
        Assert.Null(harness.Service.Current.StaleReason);
        Assert.Empty(harness.Service.Current.Rows);
    }

    [Theory]
    [InlineData("Error: No such command 'alerts'.", true)]
    [InlineData("Error: No such option: --json", true)]
    [InlineData("Error: audit store is locked", false)]
    public void Click_s_usage_errors_are_not_supported(string line, bool expected) =>
        Assert.Equal(expected, RuntimeAlertsService.IsNotSupported(line));

    // ---- The merge ----

    private static RuntimeAlertRow Runtime(string id, int minute, string action = "connector-hook", string severity = "HIGH") =>
        RuntimeAlertSnapshotParser.Parse(
            $"[{{\"id\":\"{id}\",\"timestamp\":\"{Now.AddMinutes(minute):o}\",\"severity\":\"{severity}\",\"action\":\"{action}\",\"target\":\"/synthetic/{id}\",\"connector\":\"codex\",\"details\":\"blocked\",\"decision\":\"blocked\",\"rule\":\"R-9: Nine\"}}]").Rows.Single();

    private static AlertQueueItem Queued(string id, int minute) =>
        new(id, AuditSeverity.High, "scan-finding", "/synthetic/" + id, "claudecode", Now.AddMinutes(minute));

    [Fact]
    public void A_runtime_alert_the_queue_holds_is_listed_once_and_one_it_lacks_is_added_in_time_order()
    {
        var queue = new[] { Queued("q3", 3), Queued("q1", 1) };
        var runtime = new[] { Runtime("q1", 1), Runtime("r2", 2), Runtime("q3", 3) };

        var merged = AlertsPanelViewModel.MergeRuntime(queue, runtime);

        Assert.Equal(["q3", "r2", "q1"], merged.Select(i => i.Id));
        Assert.Equal("codex", merged.Single(i => i.Id == "r2").Connector);
        Assert.Equal("claudecode", merged.Single(i => i.Id == "q1").Connector);
    }

    [Fact]
    public void A_queue_row_the_runtime_s_list_lacks_stays_and_an_empty_list_changes_nothing()
    {
        var queue = new[] { Queued("q1", 1) };

        Assert.Same(queue, AlertsPanelViewModel.MergeRuntime(queue, Array.Empty<RuntimeAlertRow>()));
        Assert.Same(queue, AlertsPanelViewModel.MergeRuntime(queue, [Runtime("q1", 1)]));
        Assert.Equal(["q1", "r9"], AlertsPanelViewModel.MergeRuntime(queue, [Runtime("r9", 0)]).Select(i => i.Id));
    }

    [Fact]
    public void The_merged_window_is_capped_at_the_queue_s_window()
    {
        var queue = Enumerable.Range(0, AlertQueueReader.DefaultWindowLimit).Select(i => Queued("q" + i, 10 + i)).ToArray();

        var merged = AlertsPanelViewModel.MergeRuntime(queue, [Runtime("old", -5)]);

        Assert.Equal(AlertQueueReader.DefaultWindowLimit, merged.Count);
        Assert.DoesNotContain(merged, i => i.Id == "old");
    }

    // ---- The inspector ----

    [Fact]
    public void A_row_gains_the_runtime_facts_at_the_top_and_its_rule_as_the_headline()
    {
        var row = Runtime("q1", 1, "scan-finding");
        var audited = AlertItem.FromQueue(Queued("q1", 1));

        var withFacts = audited.WithRuntime(row);

        Assert.True(withFacts.HasRuntimeFacts);
        Assert.False(audited.HasRuntimeFacts);
        Assert.Equal("R-9: Nine", withFacts.Headline);
        Assert.Equal(["runtime.decision", "runtime.rule"], withFacts.Fields.Select(f => f.Name));
        Assert.Equal("blocked", withFacts.Fields[0].Value);
        Assert.Equal(audited.Key, withFacts.Key);
    }

    [Fact]
    public void An_alert_only_the_runtime_listed_is_a_row_of_what_it_says()
    {
        var item = AlertItem.FromRuntime(Runtime("r2", 2, "connector-hook"));

        Assert.True(item.HasAuditId);
        Assert.Equal("connector-hook", item.Action);
        Assert.Equal("HIGH", item.Severity);
        Assert.Equal("codex", item.Connector);
        Assert.Equal("/synthetic/r2", item.TargetRef);
        Assert.Equal("R-9: Nine", item.Headline);
        Assert.Equal("R-9", item.RuleId);
        Assert.Equal("blocked", item.Details);
    }

    [Fact]
    public void The_detection_only_line_names_how_many_and_where_to_read_them()
    {
        Assert.StartsWith("1 detection-only finding of the last 24 hours is not listed", AlertsPanelViewModel.DetectionOnlySentence(1), StringComparison.Ordinal);
        Assert.StartsWith("3 detection-only findings of the last 24 hours are not listed", AlertsPanelViewModel.DetectionOnlySentence(3), StringComparison.Ordinal);
        Assert.Contains("defenseclaw audit export --since 24h", AlertsPanelViewModel.DetectionOnlySentence(3), StringComparison.Ordinal);
    }
}
