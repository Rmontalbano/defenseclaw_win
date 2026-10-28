using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The badge, tray and Overview counts with the runtime's own alert list folded in (CUST-380): one count for a row both sources hold, runtime-only
/// kinds counted, detection-only findings out, a failed or old list ignored, and a runtime without the capability unchanged. Synthetic database and a
/// scripted CLI answer in the shape upstream's <c>alerts --json</c> prints; no process is started.
/// </summary>
public sealed class AlertCountsRuntimeTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TempDirectory _temp = new();
    private readonly FakeSnapshotSource _source = new();
    private readonly ManualTime _time = new(Base);
    private readonly AlertQueueDatabase _database;
    private readonly List<AlertCountsService> _services = new();
    private RuntimeAlertFetch _answer = new(true, "[]", null);
    private bool _enabled = true;
    private int _runs;

    public AlertCountsRuntimeTests()
    {
        _database = new AlertQueueDatabase(_temp.File("audit.db"));
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private static string Printed(params (string Id, string Action, string Severity, string? Connector)[] rows) =>
        "[" + string.Join(",", rows.Select(r =>
            $"{{\"id\":\"{r.Id}\",\"timestamp\":\"{Base.AddMinutes(-5):o}\",\"severity\":\"{r.Severity}\",\"action\":\"{r.Action}\",\"target\":\"/synthetic/{r.Id}\",\"actor\":\"audit_logger\",\"connector\":{(r.Connector is null ? "null" : "\"" + r.Connector + "\"")},\"details\":\"x\",\"decision\":\"blocked\"}}")) + "]";

    private void Runtime(params (string Id, string Action, string Severity, string? Connector)[] rows) =>
        _answer = new RuntimeAlertFetch(true, Printed(rows), null);

    private (AlertCountsService Counts, RuntimeAlertsService Runtime) Build(bool excludeDetectionOnly = true)
    {
        var runtime = new RuntimeAlertsService(
            () => _enabled,
            _ =>
            {
                _runs++;
                return Task.FromResult(_answer);
            },
            post: action => action(),
            time: _time);
        var reader = new AlertQueueReader(_database.Path, excludeDetectionOnly: () => excludeDetectionOnly && _enabled);
        var counts = new AlertCountsService(reader, _source, post: action => action(), runtimeAlerts: runtime, time: _time);
        _services.Add(counts);
        return (counts, runtime);
    }

    private async Task<AlertCounts> CountAsync((AlertCountsService Counts, RuntimeAlertsService Runtime) built)
    {
        // The first read starts the shared run; once it has settled the next read sees its list.
        await built.Counts.RefreshAsync();
        await built.Runtime.Settled.WaitAsync(TestTimeouts.Ceiling);
        await built.Counts.RefreshAsync();
        return built.Counts.Current;
    }

    private void Finding(string id, int minutes, string severity = "HIGH", string? connector = "claudecode") =>
        _database.AddFinding(id, Base.AddMinutes(minutes), severity, connector);

    [Fact]
    public async Task An_alert_in_both_sources_counts_once()
    {
        Finding("f1", -10);
        Finding("f2", -9);
        Runtime(("f1", "scan-finding", "HIGH", "claudecode"), ("r1", "connector-hook", "HIGH", "codex"));

        var counts = await CountAsync(Build());

        Assert.Equal(3, counts.Total);
        Assert.Equal(3, counts.High);
        Assert.Equal(["f1", "f2", "r1"], counts.Newest.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Runtime_only_kinds_are_counted_by_severity_and_connector()
    {
        Finding("f1", -10, "MEDIUM");
        Runtime(
            ("hook", "connector-hook", "HIGH", "codex"),
            ("health", "health-failure", "ERROR", null),
            ("egress", "egress-block", "CRITICAL", "claudecode"));

        var counts = await CountAsync(Build());

        Assert.Equal(4, counts.Total);
        Assert.Equal(1, counts.Critical);
        Assert.Equal(2, counts.High); // ERROR is a High alert, as the Mac shows it
        Assert.Equal(1, counts.Medium);
        Assert.Equal(1, counts.TallyFor(c => c == "codex").Total);
        Assert.Equal(1, counts.TallyFor(c => c is null).Total);
    }

    [Fact]
    public async Task Detection_only_findings_stay_out_of_the_counts()
    {
        Finding("plain", -10);
        Finding("only", -9);
        using (var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit_events SET payload_json = '{\"defenseclaw.finding.tags\":[\"detection-only\"]}' WHERE id = 'only'";
            _ = command.ExecuteNonQuery();
        }

        Runtime(("r1", "connector-hook", "HIGH", "codex"));

        var counts = await CountAsync(Build());

        Assert.Equal(2, counts.Total);
        Assert.DoesNotContain(counts.Newest, i => i.Id == "only");
    }

    [Fact]
    public async Task A_failed_run_falls_back_to_the_database_silently()
    {
        Finding("f1", -10);
        _answer = new RuntimeAlertFetch(false, string.Empty, "the runtime's alert command failed: boom");

        var built = Build();
        var counts = await CountAsync(built);

        Assert.Equal(1, counts.Total);
        Assert.Null(built.Counts.Unavailable);
    }

    [Fact]
    public async Task A_list_that_went_stale_stops_counting_and_a_failed_refresh_keeps_it_stopped()
    {
        Finding("f1", -10);
        Runtime(("r1", "connector-hook", "HIGH", "codex"));
        var built = Build();
        Assert.Equal(2, (await CountAsync(built)).Total);

        // Four minutes on, the CLI no longer answers: the old list is too old to count.
        _time.Now = Base.AddMinutes(4);
        _answer = new RuntimeAlertFetch(false, string.Empty, "the runtime's alert command timed out");
        await built.Counts.RefreshAsync();
        await built.Runtime.Settled.WaitAsync(TestTimeouts.Ceiling);
        await built.Counts.RefreshAsync();

        Assert.Equal(1, built.Counts.Current.Total);
    }

    [Fact]
    public async Task Without_the_capability_the_counts_are_the_databases_and_the_cli_is_never_asked()
    {
        Finding("f1", -10);
        Finding("f2", -9, "LOW");
        Runtime(("r1", "connector-hook", "HIGH", "codex"));
        _enabled = false;

        var built = Build(excludeDetectionOnly: false);
        var counts = await CountAsync(built);

        var plain = await new AlertQueueReader(_database.Path).ReadAsync();
        Assert.True(plain.Counts.SameAs(counts));
        Assert.Equal(2, counts.Total);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task The_service_only_listens_to_the_shared_list_while_it_runs_and_a_new_list_reaches_the_subscribers()
    {
        Finding("f1", -10);
        var built = Build();
        var seen = new List<int>();
        EventHandler<AlertCountsChangedEventArgs> handler = (_, e) => seen.Add(e.Counts.Total);

        built.Counts.Changed += handler;
        await Task.Delay(50);
        await built.Runtime.Settled.WaitAsync(TestTimeouts.Ceiling);
        Runtime(("r1", "connector-hook", "HIGH", "codex"));
        built.Runtime.Request(force: true);
        await built.Runtime.Settled.WaitAsync(TestTimeouts.Ceiling);
        await WaitUntilAsync(() => built.Counts.Current.Total == 2);

        Assert.Contains(2, seen);
        built.Counts.Changed -= handler;
        Assert.False(built.Counts.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for the counts.");
            await Task.Delay(10);
        }
    }
}
