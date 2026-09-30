using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The kept-fresh unacknowledged-findings counts: refreshed on the monitor's alert tick and on demand, announcing real changes only,
/// and idle while nobody listens. Runs the real reader over a synthetic database built from the real DDL; the monitor is a fake that
/// counts its subscribers.
/// </summary>
public sealed class AlertCountsServiceTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-1);

    private readonly TempDirectory _temp = new();
    private readonly FakeSnapshotSource _source = new();
    private readonly AlertQueueDatabase _database;
    private readonly AlertQueueReader _reader;
    private readonly List<AlertCountsService> _services = new();

    public AlertCountsServiceTests()
    {
        _database = new AlertQueueDatabase(_temp.File("audit.db"));
        _reader = new AlertQueueReader(_database.Path);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private AlertCountsService Service(AlertQueueReader? reader = null, Action<Action>? post = null)
    {
        var service = new AlertCountsService(reader ?? _reader, _source, post: post ?? (action => action()));
        _services.Add(service);
        return service;
    }

    private void Finding(string id, int minutes, string severity, string? connector = "claudecode") =>
        _database.AddFinding(id, Base.AddMinutes(minutes), severity, connector);

    /// <summary>Collects <c>Changed</c> and lets a test wait for the next one.</summary>
    private sealed class Recorder
    {
        private readonly SemaphoreSlim _signal = new(0);
        private readonly List<AlertCountsChangedEventArgs> _events = new();

        public IReadOnlyList<AlertCountsChangedEventArgs> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void OnChanged(object? sender, AlertCountsChangedEventArgs e)
        {
            lock (_events)
            {
                _events.Add(e);
            }

            _ = _signal.Release();
        }

        public async Task<AlertCountsChangedEventArgs> NextAsync()
        {
            Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(30)), "Changed was not raised.");
            lock (_events)
            {
                return _events[^1];
            }
        }

        /// <summary>True if no event arrives within <paramref name="window"/>.</summary>
        public async Task<bool> QuietForAsync(TimeSpan window) => !await _signal.WaitAsync(window);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ idle

    [Fact]
    public async Task With_nothing_subscribed_it_does_no_work_at_all()
    {
        var service = Service();

        // The monitor ticks, the clock runs: nothing is attached, nothing is read.
        _source.Tick();
        await Task.Delay(150);
        _source.Tick();

        Assert.Equal(0, _reader.ReadCount);
        Assert.Equal(0, _source.CadenceSubscribers);
        Assert.False(service.IsRunning);
        Assert.False(service.HasData);
        Assert.Same(AlertCounts.Empty, service.Current);
        Assert.Null(service.Unavailable);
    }

    // ------------------------------------------------------------------ subscribing

    [Fact]
    public async Task The_first_subscriber_starts_it_with_one_read_and_gets_the_counts()
    {
        Finding("a", 1, "CRITICAL");
        Finding("b", 2, "HIGH");
        Finding("c", 3, "LOW");
        var service = Service();
        var recorder = new Recorder();

        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();

        Assert.Equal(3, first.Counts.Total);
        Assert.Equal(new SeverityTally(1, 1, 0, 1), first.Counts.Tally);
        Assert.Same(first.Counts, service.Current);
        Assert.True(service.HasData);
        Assert.True(service.IsRunning);
        Assert.Equal(1, _source.CadenceSubscribers);
        Assert.Equal(1, _reader.ReadCount);
        Assert.Null(first.Unavailable);
    }

    [Fact]
    public async Task An_empty_queue_is_still_news_the_first_time_because_zero_is_not_the_same_as_not_read_yet()
    {
        var service = Service();
        var recorder = new Recorder();

        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();

        Assert.Equal(0, first.Counts.Total);
        Assert.True(service.HasData);
        Assert.Single(recorder.Events);
    }

    [Fact]
    public async Task A_second_subscriber_joins_without_a_second_start()
    {
        Finding("a", 1, "HIGH");
        var service = Service();
        var one = new Recorder();
        var two = new Recorder();

        service.Changed += one.OnChanged;
        _ = await one.NextAsync();
        service.Changed += two.OnChanged;

        Assert.Equal(1, _source.CadenceSubscribers);
        Assert.Equal(1, _reader.ReadCount);

        // Both hear the next change.
        Finding("b", 2, "HIGH");
        _source.Tick();
        Assert.Equal(2, (await one.NextAsync()).Counts.Total);
        Assert.Equal(2, (await two.NextAsync()).Counts.Total);
    }

    // ------------------------------------------------------------------ the cadence

    [Fact]
    public async Task Each_alert_tick_reads_again_but_only_a_real_change_is_raised()
    {
        Finding("a", 1, "HIGH");
        var service = Service();
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        _ = await recorder.NextAsync();

        // Nothing changed in the database: the tick reads, and says nothing.
        _source.Tick();
        await WaitUntilAsync(() => _reader.ReadCount == 2, "the second read");
        Assert.True(await recorder.QuietForAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Single(recorder.Events);

        // A finding arrives: the next tick raises it, with the old counts beside the new.
        Finding("b", 2, "CRITICAL");
        _source.Tick();
        var changed = await recorder.NextAsync();

        Assert.Equal(2, changed.Counts.Total);
        Assert.Equal(1, changed.Counts.Critical);
        Assert.Equal(1, changed.Previous.Total);
        Assert.Equal(2, recorder.Events.Count);
    }

    [Fact]
    public async Task A_window_at_its_cap_still_announces_a_displaced_newest_row()
    {
        // Two findings overflow a window of one; a newer one displaces it and leaves every tally exactly where it was.
        using var small = new TempDirectory();
        var database = new AlertQueueDatabase(small.File("audit.db"));
        database.AddFinding("old", Base.AddMinutes(1), "HIGH");
        database.AddFinding("mid", Base.AddMinutes(2), "HIGH");
        var reader = new AlertQueueReader(database.Path, windowLimit: 1);
        var service = Service(reader);
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();
        Assert.True(first.Counts.HasMore);
        Assert.Equal("mid", first.Counts.Newest[0].Id);

        database.AddFinding("new", Base.AddMinutes(3), "HIGH");
        _source.Tick();
        var second = await recorder.NextAsync();

        Assert.Equal(first.Counts.Tally, second.Counts.Tally);
        Assert.Equal("new", second.Counts.Newest[0].Id);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task The_last_subscriber_leaving_detaches_from_the_monitor_and_stops_reading()
    {
        Finding("a", 1, "HIGH");
        var service = Service();
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        _ = await recorder.NextAsync();

        service.Changed -= recorder.OnChanged;

        Assert.Equal(0, _source.CadenceSubscribers);
        Assert.False(service.IsRunning);
        var reads = _reader.ReadCount;

        _source.Tick();
        _source.Tick();
        await Task.Delay(150);

        Assert.Equal(reads, _reader.ReadCount);
        Assert.Equal(1, service.Current.Total);   // the last counts stay readable
    }

    [Fact]
    public async Task Subscribing_again_picks_up_what_happened_while_nobody_was_listening()
    {
        Finding("a", 1, "HIGH");
        var service = Service();
        var first = new Recorder();
        service.Changed += first.OnChanged;
        _ = await first.NextAsync();
        service.Changed -= first.OnChanged;

        Finding("b", 2, "HIGH");
        var second = new Recorder();
        service.Changed += second.OnChanged;

        Assert.Equal(2, (await second.NextAsync()).Counts.Total);
        Assert.Equal(1, _source.CadenceSubscribers);
    }

    [Fact]
    public async Task One_of_two_subscribers_leaving_does_not_stop_it()
    {
        var service = Service();
        var one = new Recorder();
        var two = new Recorder();
        service.Changed += one.OnChanged;
        service.Changed += two.OnChanged;
        _ = await one.NextAsync();

        service.Changed -= one.OnChanged;

        Assert.True(service.IsRunning);
        Assert.Equal(1, _source.CadenceSubscribers);
    }

    // ------------------------------------------------------------------ on demand (after an acknowledge)

    [Fact]
    public async Task Refresh_after_an_acknowledge_drops_the_count_at_once_without_waiting_for_the_tick()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "HIGH");
        var service = Service();
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        Assert.Equal(2, (await recorder.NextAsync()).Counts.Total);

        _database.Acknowledge("b");
        await service.RefreshAsync();

        var after = await recorder.NextAsync();
        Assert.Equal(1, after.Counts.Total);
        Assert.Equal(1, service.Current.Total);
    }

    [Fact]
    public async Task Refresh_works_with_no_subscribers_updates_Current_and_does_not_start_the_service()
    {
        Finding("a", 1, "CRITICAL");
        var service = Service();

        await service.RefreshAsync();

        Assert.Equal(1, service.Current.Critical);
        Assert.True(service.HasData);
        Assert.False(service.IsRunning);
        Assert.Equal(0, _source.CadenceSubscribers);
        Assert.Equal(1, _reader.ReadCount);
    }

    [Fact]
    public async Task Refreshes_requested_together_share_reads_and_every_caller_sees_a_read_that_started_after_its_call()
    {
        var service = Service();
        await service.RefreshAsync();
        var before = _reader.ReadCount;

        Finding("x", 1, "HIGH");
        var callers = Enumerable.Range(0, 12).Select(_ => Task.Run(() => service.RefreshAsync())).ToArray();
        await Task.WhenAll(callers);

        // Twelve calls, far fewer reads: and all of them finished with the new finding in view.
        Assert.InRange(_reader.ReadCount - before, 1, 5);
        Assert.Equal(1, service.Current.Total);
    }

    [Fact]
    public async Task A_refresh_the_caller_cancelled_throws_but_a_failed_read_does_not()
    {
        var service = Service();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cts.Token));
    }

    // ------------------------------------------------------------------ failure

    [Fact]
    public async Task A_database_that_cannot_be_read_keeps_the_last_good_counts_says_why_once_and_recovers()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "CRITICAL");
        var service = Service();
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        _ = await recorder.NextAsync();
        Assert.Equal(2, service.Current.Total);

        // Somebody replaces the database with junk (or it is mid-write): the read fails.
        SqliteConnection.ClearAllPools();
        var good = File.ReadAllBytes(_database.Path);
        File.WriteAllBytes(_database.Path, Enumerable.Repeat((byte)0xFF, 8192).ToArray());

        await service.RefreshAsync();
        var failed = await recorder.NextAsync();

        Assert.NotNull(failed.Unavailable);
        Assert.Contains("could not be read", failed.Unavailable, StringComparison.Ordinal);
        Assert.Equal(2, failed.Counts.Total);       // not "all clear"
        Assert.Equal(2, service.Current.Total);
        Assert.Equal(failed.Unavailable, service.Unavailable);
        Assert.True(service.HasData);

        // The same failure again is not news.
        await service.RefreshAsync();
        Assert.True(await recorder.QuietForAsync(TimeSpan.FromMilliseconds(300)));

        // The database comes back: the counts are current again, and that is news.
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(_database.Path, good);
        await service.RefreshAsync();
        var recovered = await recorder.NextAsync();

        Assert.Null(recovered.Unavailable);
        Assert.Null(service.Unavailable);
        Assert.Equal(2, recovered.Counts.Total);
    }

    [Fact]
    public async Task A_database_that_is_not_there_yet_is_zero_alerts_not_a_failure()
    {
        var reader = new AlertQueueReader(_temp.File("not-created-yet.db"));
        var service = Service(reader);
        var recorder = new Recorder();

        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();

        Assert.Equal(0, first.Counts.Total);
        Assert.Null(first.Unavailable);
        Assert.True(service.HasData);
    }

    // ------------------------------------------------------------------ old databases: the gateway's list

    private static GatewayAlert Alert(string id, string severity, string action = "scan-finding", int minutes = 0) =>
        JsonSerializer.Deserialize<GatewayAlert>(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["timestamp"] = Base.AddMinutes(minutes),
            ["action"] = action,
            ["target"] = "t",
            ["severity"] = severity,
        }))!;

    [Fact]
    public async Task A_pre_v8_database_falls_back_to_the_list_the_monitor_already_fetched()
    {
        using var legacyDir = new TempDirectory();
        var legacy = AlertQueueDatabase.Legacy(legacyDir.File("audit.db"));
        _source.Current = GatewaySnapshot.Initial with
        {
            AlertsUnavailable = null,
            RecentAlerts = new[]
            {
                Alert("g1", "CRITICAL", minutes: 3),
                Alert("g2", "HIGH", minutes: 2),
                Alert("g3", "INFO", minutes: 1),
                Alert("g4", "MEDIUM", action: "dismiss-alert", minutes: 0),
            },
        };
        var service = Service(new AlertQueueReader(legacy.Path));
        var recorder = new Recorder();

        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();

        Assert.Equal(AlertCountsSource.Gateway, first.Counts.Source);
        Assert.Equal(new SeverityTally(1, 1, 0, 0), first.Counts.Tally);
        Assert.Equal(new[] { "g1", "g2" }, first.Counts.Newest.Select(i => i.Id));
        Assert.False(first.Counts.HasMore);
        Assert.Null(first.Unavailable);
    }

    [Fact]
    public async Task A_pre_v8_database_with_the_gateway_not_answering_is_empty_and_says_why()
    {
        using var legacyDir = new TempDirectory();
        var legacy = AlertQueueDatabase.Legacy(legacyDir.File("audit.db"));
        _source.Current = GatewaySnapshot.Initial with { AlertsUnavailable = "The gateway is not answering; alerts are unavailable." };
        var service = Service(new AlertQueueReader(legacy.Path));
        var recorder = new Recorder();

        service.Changed += recorder.OnChanged;
        var first = await recorder.NextAsync();

        Assert.Equal(0, first.Counts.Total);
        Assert.Equal("The gateway is not answering; alerts are unavailable.", first.Unavailable);
    }

    [Fact]
    public async Task A_pre_v8_fallback_follows_the_gateway_list_on_the_next_tick_with_no_extra_request()
    {
        using var legacyDir = new TempDirectory();
        var legacy = AlertQueueDatabase.Legacy(legacyDir.File("audit.db"));
        _source.Current = GatewaySnapshot.Initial with { AlertsUnavailable = null, RecentAlerts = new[] { Alert("g1", "HIGH") } };
        var service = Service(new AlertQueueReader(legacy.Path));
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        _ = await recorder.NextAsync();

        _source.Current = _source.Current with { RecentAlerts = new[] { Alert("g2", "CRITICAL", minutes: 1), Alert("g1", "HIGH") } };
        _source.Tick();
        var next = await recorder.NextAsync();

        Assert.Equal(2, next.Counts.Total);
        Assert.Equal(1, next.Counts.Critical);
    }

    // ------------------------------------------------------------------ events: threads, subscribers, teardown

    [Fact]
    public async Task Changed_goes_through_the_marshal_the_service_was_given_and_a_subscriber_that_left_in_the_meantime_is_not_called()
    {
        Finding("a", 1, "HIGH");
        var queued = new List<Action>();
        var service = Service(post: action =>
        {
            lock (queued)
            {
                queued.Add(action);
            }
        });
        var stays = new Recorder();
        var leaves = new Recorder();

        service.Changed += stays.OnChanged;
        service.Changed += leaves.OnChanged;
        await WaitUntilAsync(() => { lock (queued) { return queued.Count == 1; } }, "the raise to be queued");

        // Nothing has been raised: it waits for the "UI thread".
        Assert.Empty(stays.Events);

        service.Changed -= leaves.OnChanged;
        Action[] pending;
        lock (queued)
        {
            pending = queued.ToArray();
        }

        foreach (var action in pending)
        {
            action();
        }

        Assert.Single(stays.Events);
        Assert.Empty(leaves.Events);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_silence_the_others()
    {
        Finding("a", 1, "HIGH");
        var service = Service();
        var other = new Recorder();
        service.Changed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        service.Changed += other.OnChanged;

        _ = await other.NextAsync();

        Assert.Equal(1, service.Current.Total);
    }

    [Fact]
    public async Task Dispose_lets_go_of_the_monitor_and_later_subscribers_are_ignored()
    {
        var service = Service();
        var recorder = new Recorder();
        service.Changed += recorder.OnChanged;
        _ = await recorder.NextAsync();

        service.Dispose();
        service.Dispose();

        Assert.Equal(0, _source.CadenceSubscribers);
        Assert.False(service.IsRunning);

        var late = new Recorder();
        service.Changed += late.OnChanged;
        Assert.Equal(0, _source.CadenceSubscribers);
        Assert.Equal(1, _reader.ReadCount);
    }

    [Fact]
    public void Its_collaborators_are_required()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new AlertCountsService(null!, _source));
        _ = Assert.Throws<ArgumentNullException>(() => new AlertCountsService(_reader, null!));
    }
}
