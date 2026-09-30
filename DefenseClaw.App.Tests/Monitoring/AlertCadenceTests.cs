using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The monitor's alert tick (<see cref="GatewayMonitor.AlertCadenceElapsed"/>): the 30 s beat the alert counts refresh on. It is
/// scheduled only for a subscriber, raised after the poll is published, independent of the gateway being healthy, and immune to
/// a wall-clock step, like the <c>/alerts</c> fetch it rides beside. Polls a scratch directory and a listener of its own.
/// </summary>
public sealed class AlertCadenceTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string HealthBody = "{\"provenance\":{\"binary_version\":\"9.9.9\"}}";

    private static RawHttpListener Quiet() => new(_ => RawHttpListener.Response(200, HealthBody));

    private static AppServices Create(TempDirectory temp, int port)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {port}\n");
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        return AppServices.CreateIsolated(paths, temp.File("claude-settings.json"));
    }

    [Fact]
    public async Task With_no_subscriber_nothing_is_scheduled_however_often_it_polls()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            _ = await monitor.PollOnceAsync();
        }

        Assert.Equal(0, monitor.AlertCadenceSubscriberCount);

        // A subscriber that arrives late is ticked on the very next poll: nothing was "used up" while nobody listened.
        var ticks = 0;
        monitor.AlertCadenceElapsed += (_, _) => ticks++;
        _ = await monitor.PollOnceAsync();

        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task It_ticks_once_per_thirty_seconds_after_the_poll_is_published()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);

        var seen = new List<GatewaySnapshot>();
        var currentAtTick = new List<GatewaySnapshot>();
        monitor.AlertCadenceElapsed += (_, e) =>
        {
            seen.Add(e.Snapshot);
            currentAtTick.Add(monitor.Current);
        };

        _ = await monitor.PollOnceAsync();
        Assert.Single(seen);

        // The snapshot a subscriber is handed is the one the poll published, already readable as Current.
        Assert.Same(monitor.Current, seen[0]);
        Assert.Same(seen[0], currentAtTick[0]);

        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await monitor.PollOnceAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        _ = await monitor.PollOnceAsync();
        Assert.Single(seen);

        clock.Advance(TimeSpan.FromSeconds(6));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task It_does_not_wait_for_a_healthy_gateway_because_the_audit_database_is_readable_without_one()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        using var monitor = new GatewayMonitor(services, new ManualClock());
        var ticks = 0;
        monitor.AlertCadenceElapsed += (_, _) => ticks++;

        // No trusted gateway owns the port here, so /alerts is never fetched - and the tick still comes.
        _ = await monitor.PollOnceAsync();

        Assert.Equal(0, monitor.AlertsFetchCount);
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task A_forced_alert_refresh_ticks_at_once_and_restarts_the_cadence()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);
        var ticks = 0;
        monitor.AlertCadenceElapsed += (_, _) => ticks++;

        _ = await monitor.PollOnceAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        _ = await monitor.RefreshAlertsNowAsync();
        Assert.Equal(2, ticks);

        // The forced tick restarted the 30 s: the next loop poll, 10 s later, is not due.
        clock.Advance(TimeSpan.FromSeconds(10));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, ticks);
    }

    [Fact]
    public async Task A_wall_clock_step_neither_stops_nor_hastens_the_tick()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);
        var ticks = 0;
        monitor.AlertCadenceElapsed += (_, _) => ticks++;

        _ = await monitor.PollOnceAsync();
        clock.StepWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(26));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(1, ticks);

        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, ticks);

        clock.StepWallClock(TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, ticks);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_silence_the_others_or_the_poll()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        using var monitor = new GatewayMonitor(services, new ManualClock());
        var second = 0;
        monitor.AlertCadenceElapsed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        monitor.AlertCadenceElapsed += (_, _) => second++;

        var snapshot = await monitor.PollOnceAsync();

        Assert.Equal(1, second);
        Assert.Same(snapshot, monitor.Current);
    }

    [Fact]
    public async Task The_counts_service_on_the_real_composition_attaches_only_while_subscribed()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);

        Assert.Equal(0, services.Monitor.AlertCadenceSubscriberCount);

        EventHandler<AlertCountsChangedEventArgs> handler = (_, _) => { };
        services.AlertCounts.Changed += handler;
        Assert.Equal(1, services.Monitor.AlertCadenceSubscriberCount);

        services.AlertCounts.Changed -= handler;
        Assert.Equal(0, services.Monitor.AlertCadenceSubscriberCount);

        // The first subscription read the (scratch, empty) audit directory once: no database there means zero alerts.
        await Task.Delay(50);
        Assert.Equal(0, services.AlertCounts.Current.Total);
    }
}
