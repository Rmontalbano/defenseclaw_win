using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// Pause monitoring (CUST-204): an app-local switch, persisted in <c>monitoring.paused</c>, that stops the background poll and everything that rides
/// it, says so on every surface, and starts polling again the moment it is lifted. Each test polls a scratch data directory and a listener of its
/// own; none of it can reach the real install.
/// </summary>
public class GatewayMonitorPauseTests
{
    private static RawHttpListener Quiet() => new(_ => RawHttpListener.Response(200, "{}"));

    private static int HealthRequests(RawHttpListener listener) => listener.Requests.Count(r => r.Path == "/health");

    private static AppServices Create(TempDirectory temp, RawHttpListener listener)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {listener.Port}\n");
        return AppServices.CreateIsolated(TestServices.IsolatedPaths(temp.Path), claudeSettingsPath: temp.File("claude-settings.json"));
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMilliseconds = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(20);
        }
    }

    // ---- The flag ----

    [Fact]
    public void Pausing_is_kept_in_the_monitoring_settings_and_survives_a_restart()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);

        Assert.False(services.Monitor.IsPaused);

        Assert.True(services.Monitor.SetPaused(true));
        Assert.True(services.Monitor.IsPaused);
        Assert.True(services.Settings.Current.Monitoring.Paused);

        // "The app was restarted": a new reader of the file the old one wrote.
        Assert.True(AppSettingsStore.OpenFresh(services.Settings.FilePath).Current.Monitoring.Paused);

        Assert.True(services.Monitor.SetPaused(false));
        Assert.False(services.Monitor.IsPaused);
        Assert.False(AppSettingsStore.OpenFresh(services.Settings.FilePath).Current.Monitoring.Paused);
    }

    [Fact]
    public void Pausing_twice_is_the_same_as_pausing_once()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);

        var changes = new List<AppSettingsChangedEventArgs>();
        services.Settings.Changed += (_, e) => changes.Add(e);

        Assert.True(services.Monitor.SetPaused(true));
        Assert.True(services.Monitor.SetPaused(true));

        Assert.Single(changes);
    }

    // ---- No poll while paused ----

    [Fact]
    public async Task A_paused_monitor_makes_no_poll_and_a_resumed_one_does()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;

        _ = monitor.SetPaused(true);
        var returned = await monitor.PollOnceAsync();

        // Nothing was asked of the gateway, and what comes back is what the monitor already held.
        Assert.Equal(0, HealthRequests(listener));
        Assert.Same(monitor.Current, returned);

        _ = monitor.SetPaused(false);
        _ = await monitor.PollOnceAsync();

        Assert.Equal(1, HealthRequests(listener));
    }

    [Fact]
    public async Task The_alert_cadence_tick_and_so_the_audit_reads_behind_it_stop_while_paused()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;
        var ticks = 0;
        monitor.AlertCadenceElapsed += (_, _) => ticks++;

        _ = monitor.SetPaused(true);
        _ = await monitor.PollOnceAsync();
        _ = await monitor.PollOnceAsync();
        Assert.Equal(0, ticks);

        _ = monitor.SetPaused(false);
        _ = await monitor.PollOnceAsync();
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task An_explicit_refresh_still_polls_while_paused_and_the_snapshot_stays_paused()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;

        _ = monitor.SetPaused(true);
        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(1, HealthRequests(listener));
        Assert.True(snapshot.IsPaused);
        Assert.Equal(GatewaySnapshot.PausedLabel, snapshot.StateLabel);
    }

    // ---- What the surfaces are told ----

    [Fact]
    public async Task Pausing_publishes_one_snapshot_that_says_so_and_keeps_the_last_known_state()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;

        var before = await monitor.RefreshAsync();
        var published = new List<GatewaySnapshot>();
        monitor.StateChanged += (_, e) => published.Add(e.Snapshot);

        _ = monitor.SetPaused(true);
        _ = monitor.SetPaused(true);
        await WaitUntil(() => published.Count >= 1, "the paused snapshot");

        var paused = published[0];
        Assert.True(paused.IsPaused);
        Assert.Equal("Monitoring paused", paused.StateLabel);
        Assert.Equal(before.State, paused.State);
        Assert.Equal(before.Detail, paused.Detail);
        Assert.Equal(before.PolledAt, paused.PolledAt);
        Assert.Equal("Neutral", GatewayPresentation.StateTone(paused));
        Assert.Same(paused, monitor.Current);

        _ = monitor.SetPaused(false);
        await WaitUntil(() => published.Count >= 2, "the resumed snapshot");

        var resumed = published[1];
        Assert.False(resumed.IsPaused);
        Assert.Equal(before.StateLabel, resumed.StateLabel);
        Assert.Equal(GatewayPresentation.StateTone(before), GatewayPresentation.StateTone(resumed));

        // One snapshot per switch, however many times the switch was thrown.
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task A_pause_changed_behind_the_monitors_back_is_announced_the_same_way()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;
        monitor.Start();
        await WaitUntil(() => HealthRequests(listener) >= 1, "the first poll");
        var published = new List<GatewaySnapshot>();
        monitor.StateChanged += (_, e) => published.Add(e.Snapshot);

        // What a Settings page would do: change the setting, not the monitor.
        _ = services.Settings.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });

        await WaitUntil(() => published.Any(s => s.IsPaused), "the paused snapshot");
        Assert.True(monitor.IsPaused);
        Assert.True(monitor.Current.IsPaused);
    }

    [Fact]
    public void A_snapshot_that_differs_only_by_the_pause_does_not_render_the_same()
    {
        var running = new GatewaySnapshot { State = AppGatewayState.Running };
        var paused = running with { IsPaused = true };

        Assert.False(running.RendersSameAs(paused));
        Assert.True(paused.RendersSameAs(paused with { PolledAt = DateTimeOffset.UtcNow }));
        Assert.Equal("Running", running.StateLabel);
        Assert.Equal("Monitoring paused", paused.StateLabel);
        Assert.Equal("Ok", GatewayPresentation.StateTone(running));
        Assert.Equal("Neutral", GatewayPresentation.StateTone(paused));

        // The last known state is still there to come back to, and still classifies as it did.
        Assert.True(paused.IsRunning);
    }

    // ---- The loop ----

    [Fact]
    public async Task Started_paused_the_loop_makes_no_poll_and_resuming_polls_at_once()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;
        _ = services.Settings.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });

        monitor.Start();

        // An unpaused loop polls the instant it starts.
        await Task.Delay(1_200);
        Assert.Equal(0, HealthRequests(listener));
        await WaitUntil(() => monitor.Current.IsPaused, "the paused snapshot");

        // Resumed: the next poll is now, not after the five-second interval (which is what a loop that only looked at the flag each round would take).
        var resumedAt = Environment.TickCount64;
        _ = monitor.SetPaused(false);
        await WaitUntil(() => HealthRequests(listener) >= 1, "a poll after the resume", timeoutMilliseconds: 3_000);

        Assert.True(Environment.TickCount64 - resumedAt < GatewayMonitor.FastInterval.TotalMilliseconds);
        await WaitUntil(() => !monitor.Current.IsPaused, "the live snapshot");
    }

    [Fact]
    public async Task Resuming_cuts_short_the_wait_between_two_polls()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;

        monitor.Start();
        await WaitUntil(() => HealthRequests(listener) >= 1, "the first poll");

        // Inside the five-second wait after that poll: pause, then resume. The resume is the wake-up.
        _ = monitor.SetPaused(true);
        await Task.Delay(300);
        Assert.Equal(1, HealthRequests(listener));

        _ = monitor.SetPaused(false);
        await WaitUntil(() => HealthRequests(listener) >= 2, "the poll the resume asked for", timeoutMilliseconds: 3_000);
    }
}
