using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The Stale chip (CUST-273): on while the last good gateway poll is more than three polling intervals old. The acceptance is that a monitor whose
/// poll is blocked - which publishes nothing, so no event can announce it - turns the chip on within one interval of the line being crossed. The
/// real monitor is driven here on a clock the test moves (<see cref="TickingClock"/>: its timers fire from <c>Advance</c>), against a loopback
/// listener of its own; nothing waits for real time to pass. Synthetic data only.
/// </summary>
public sealed class StatusStripStaleTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string HealthBody = "{\"provenance\":{\"binary_version\":\"9.9.9\"}}";

    private static DefenseClawPaths InstalledPaths(TempDirectory temp) =>
        new(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

    private static AppServices Create(TempDirectory temp, int port, string? claudeSettingsPath = null)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {port}\nguardrail:\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n");
        return AppServices.CreateIsolated(InstalledPaths(temp), claudeSettingsPath ?? temp.File("claude-settings.json"));
    }

    // ---- The acceptance: blocking polls turns Stale on within one interval ------------------------------------------------------------------

    [Fact]
    public async Task A_blocked_poll_turns_Stale_on_within_one_interval_of_the_last_good_poll_being_three_intervals_old()
    {
        using var temp = new TempDirectory();
        var blockHealth = false;
        using var release = new ManualResetEventSlim(false);
        using var listener = new RawHttpListener(request =>
        {
            if (Volatile.Read(ref blockHealth) && request.Path == "/health")
            {
                _ = release.Wait(TimeSpan.FromSeconds(60));
            }

            return RawHttpListener.Response(200, HealthBody);
        });
        using var services = Create(temp, listener.Port);
        var clock = new TickingClock();
        using var monitor = new GatewayMonitor(services, clock);
        using var commands = new CommandActivity(services.Cli, action => action());
        using var strip = new StatusStripViewModel(services, monitor, clock, action => action(), commands);

        // One good poll, at t = 0; the strip is on screen and looks every polling interval (5 s).
        _ = await monitor.PollOnceAsync();
        Assert.Equal(TimeSpan.Zero, monitor.SinceLastGoodPoll);
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.Cadence);
        strip.SetActive(true);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, clock.ActivePeriods);
        Assert.False(strip.Chip(StripChipKey.Stale).IsShown);

        // The next poll blocks inside the gateway's answer: nothing is published, no event says so.
        var requestsBefore = listener.Requests.Count;
        Volatile.Write(ref blockHealth, true);
        var blocked = monitor.PollOnceAsync();
        await WaitUntilAsync(() => listener.Requests.Count > requestsBefore, "the blocked poll to reach the gateway");
        Assert.False(blocked.IsCompleted);

        // Up to and including three intervals: the data is not yet stale (the line is strictly more than 15 s).
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), monitor.SinceLastGoodPoll);
        Assert.False(strip.Chip(StripChipKey.Stale).IsShown);

        // The line is crossed just after 15 s; the strip looks again within one interval, and the chip is on.
        clock.Advance(TimeSpan.FromSeconds(5));
        var chip = strip.Chip(StripChipKey.Stale);
        Assert.True(chip.IsShown, "20 s after the last good poll the chip must be on: one interval after the 15 s line");
        Assert.Equal("Stale", chip.Text);
        Assert.Contains("20 s", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("5 s check interval", chip.ToolTip, StringComparison.Ordinal);
        Assert.False(blocked.IsCompleted, "still blocked: the chip came from the clock, not from a poll");

        // The poll comes back; the next look finds a fresh one and the chip goes.
        release.Set();
        _ = await blocked;
        Assert.Equal(TimeSpan.Zero, monitor.SinceLastGoodPoll);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(chip.IsShown);
    }

    [Fact]
    public async Task A_wall_clock_step_neither_makes_the_data_stale_nor_fresh()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, HealthBody));
        using var services = Create(temp, listener.Port);
        var clock = new TickingClock();
        using var monitor = new GatewayMonitor(services, clock);

        _ = await monitor.PollOnceAsync();
        clock.Advance(TimeSpan.FromSeconds(10));

        // The monotonic clock is what "how long ago" is measured on: an hour back or forward changes nothing.
        clock.StepWallClock(TimeSpan.FromHours(-1));
        Assert.Equal(TimeSpan.FromSeconds(10), monitor.SinceLastGoodPoll);
        Assert.False(PollFreshness.IsStale(monitor.SinceLastGoodPoll, monitor.Cadence));

        clock.StepWallClock(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromSeconds(10), monitor.SinceLastGoodPoll);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(PollFreshness.IsStale(monitor.SinceLastGoodPoll, monitor.Cadence));
    }

    [Fact]
    public async Task A_poll_that_throws_is_not_a_good_poll_even_when_its_failure_is_published()
    {
        // A settings path with a NUL in it makes every poll throw (the same trick the monitor's own fault tests use). After three in a row the monitor
        // publishes a snapshot saying so - stamped with its own time, but it brought nothing: there is still no good poll.
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, HealthBody));
        using var services = Create(temp, listener.Port, claudeSettingsPath: "C:\\bad\0settings.json");
        var clock = new TickingClock();
        using var monitor = new GatewayMonitor(services, clock);

        for (var poll = 0; poll < GatewayMonitor.PollFaultsBeforeSurface; poll++)
        {
            _ = await monitor.PollOnceAsync();
        }

        Assert.StartsWith("The gateway poll is failing: ", monitor.Current.Detail, StringComparison.Ordinal);
        Assert.NotEqual(DateTimeOffset.MinValue, monitor.Current.PolledAt);
        Assert.Null(monitor.SinceLastGoodPoll);
    }

    [Fact]
    public void The_cadence_follows_the_health_pulse_the_operator_set()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, HealthBody));
        using var services = Create(temp, listener.Port);
        using var monitor = new GatewayMonitor(services, new TickingClock());
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.Cadence);

        _ = services.Settings.Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = 2 } });
        Assert.Equal(TimeSpan.FromSeconds(2), monitor.Cadence);

        _ = services.Settings.Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = 60 } });
        Assert.Equal(TimeSpan.FromSeconds(60), monitor.Cadence);
        Assert.False(monitor.IsPaused);
    }

    // ---- The look, with the freshness in the test's hands -------------------------------------------------------------------------------------

    [Fact]
    public void The_strip_keeps_no_timer_while_it_is_off_screen_and_looks_at_once_when_it_comes_back()
    {
        using var scene = new StripScene();
        Assert.Equal(0, scene.Clock.ActiveTimers);

        // Stale while nobody looks: the chip is not maintained (no timer), and is right the moment the strip is on screen again.
        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(40);
        scene.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown);

        scene.Strip.SetActive(true);
        Assert.True(scene.Chip(StripChipKey.Stale).IsShown);
        Assert.Equal(1, scene.Clock.ActiveTimers);

        scene.Strip.SetActive(false);
        Assert.Equal(0, scene.Clock.ActiveTimers);
        Assert.False(scene.Strip.IsActive);
    }

    [Fact]
    public void While_on_screen_the_strip_looks_once_per_interval_and_follows_the_data_up_and_down()
    {
        using var scene = new StripScene();
        scene.Strip.SetActive(true);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, scene.Clock.ActivePeriods);

        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(16);
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown, "no tick yet");
        scene.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(scene.Chip(StripChipKey.Stale).IsShown);

        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(1);
        scene.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown);
    }

    [Fact]
    public void A_new_health_pulse_changes_what_three_intervals_is_and_how_often_the_strip_looks()
    {
        using var scene = new StripScene();
        scene.Strip.SetActive(true);
        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(8);
        scene.Strip.EvaluateFreshness();
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown);

        // The operator sets the pulse to 2 s: 8 s is now more than three intervals, and the strip looks every 2 s.
        scene.Freshness.Cadence = TimeSpan.FromSeconds(2);
        _ = scene.Services.Settings.Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = 2 } });

        Assert.True(scene.Chip(StripChipKey.Stale).IsShown);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, scene.Clock.ActivePeriods);
    }

    [Fact]
    public void Pausing_monitoring_takes_the_chip_away_and_resuming_brings_it_back_if_nothing_polled()
    {
        using var scene = new StripScene();
        scene.Strip.SetActive(true);
        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromMinutes(5);
        scene.Strip.EvaluateFreshness();
        Assert.True(scene.Chip(StripChipKey.Stale).IsShown);

        scene.Freshness.IsPaused = true;
        _ = scene.Services.Settings.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown);

        scene.Freshness.IsPaused = false;
        _ = scene.Services.Settings.Update(s => s with { Monitoring = s.Monitoring with { Paused = false } });
        Assert.True(scene.Chip(StripChipKey.Stale).IsShown);
    }

    [Fact]
    public void A_disposed_strip_keeps_no_timer_and_a_late_tick_does_nothing()
    {
        var scene = new StripScene();
        scene.Strip.SetActive(true);
        Assert.Equal(1, scene.Clock.ActiveTimers);

        scene.Strip.Dispose();
        Assert.Equal(0, scene.Clock.ActiveTimers);

        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromMinutes(5);
        scene.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(scene.Chip(StripChipKey.Stale).IsShown);
        scene.Dispose();
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(30, 10)]
    [InlineData(60, 10)]
    public void The_strip_looks_once_per_polling_interval_but_never_more_often_than_a_second_or_less_often_than_ten(double cadenceSeconds, double lookSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(lookSeconds), PollFreshness.LookEvery(TimeSpan.FromSeconds(cadenceSeconds)));

    [Fact]
    public void The_look_is_never_slower_than_the_interval_it_is_measured_in_unless_that_interval_is_long()
    {
        // "Within one interval" holds for every pulse the Settings page allows (2 to 60 s): the look is the interval, or 10 s, whichever is shorter.
        for (var seconds = 2; seconds <= 60; seconds++)
        {
            var cadence = TimeSpan.FromSeconds(seconds);
            Assert.True(PollFreshness.LookEvery(cadence) <= cadence);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 120_000; // a wait, not a bound: CI runners have run up to ~25x slower than a desktop
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }
}
