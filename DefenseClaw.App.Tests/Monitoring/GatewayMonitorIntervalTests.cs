using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The health interval (CUST-203, Settings → Monitoring → "Health pulse"): <c>monitoring.healthIntervalSeconds</c> is the wait between the
/// background loop's polls, read before every wait, so a change applies to the wait that follows it — live, not at the next start. Each test
/// polls a scratch data directory and a listener of its own; none of it can reach the real install.
/// </summary>
public class GatewayMonitorIntervalTests
{
    private static RawHttpListener Quiet() => new(_ => RawHttpListener.Response(200, "{}"));

    private static int HealthRequests(RawHttpListener listener) => listener.Requests.Count(r => r.Path == "/health");

    private static AppServices Create(TempDirectory temp, RawHttpListener listener)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {listener.Port}\n");
        return AppServices.CreateIsolated(TestServices.IsolatedPaths(temp.Path), claudeSettingsPath: temp.File("claude-settings.json"));
    }

    private static void SetInterval(AppServices services, int seconds) =>
        Assert.True(services.Settings.Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = seconds } }));

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMilliseconds)
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

    // ---- What the wait is ----

    [Fact]
    public void The_interval_is_the_setting_five_seconds_until_it_is_changed_and_is_read_every_time()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);

        Assert.Equal(TimeSpan.FromSeconds(5), services.Monitor.HealthInterval);
        Assert.Equal(GatewayMonitor.FastInterval, services.Monitor.HealthInterval);

        SetInterval(services, 12);
        Assert.Equal(TimeSpan.FromSeconds(12), services.Monitor.HealthInterval);
        Assert.Equal(TimeSpan.FromSeconds(12), services.Monitor.NextWait(0));

        SetInterval(services, 2);
        Assert.Equal(TimeSpan.FromSeconds(2), services.Monitor.NextWait(0));
    }

    [Fact]
    public void An_unreachable_gateway_still_backs_off_but_never_to_a_faster_pace_than_the_pulse()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var monitor = services.Monitor;

        // Below the threshold the pulse is the wait; at it, the slower of the pulse and the 30 s back-off.
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.NextWait(GatewayMonitor.FailuresBeforeBackoff - 1));
        Assert.Equal(GatewayMonitor.SlowInterval, monitor.NextWait(GatewayMonitor.FailuresBeforeBackoff));

        SetInterval(services, 60);
        Assert.Equal(TimeSpan.FromSeconds(60), monitor.NextWait(GatewayMonitor.FailuresBeforeBackoff));
        Assert.Equal(TimeSpan.FromSeconds(60), monitor.NextWait(0));

        SetInterval(services, 2);
        Assert.Equal(GatewayMonitor.SlowInterval, monitor.NextWait(GatewayMonitor.FailuresBeforeBackoff + 4));
    }

    // ---- The loop ----

    [Fact]
    public async Task A_short_interval_makes_the_loop_poll_faster_than_the_default_would()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        SetInterval(services, 2);

        services.Monitor.Start();
        await WaitUntil(() => HealthRequests(listener) >= 1, "the first poll", 3_000);
        var first = Environment.TickCount64;

        // The default wait is five seconds; a two-second pulse has polled again well inside that.
        await WaitUntil(() => HealthRequests(listener) >= 2, "a second poll at the two-second pulse", 4_000);

        Assert.True(Environment.TickCount64 - first < 4_500, "the second poll came after the default interval, not the one that was set");
    }

    [Fact]
    public async Task Changing_the_interval_while_the_loop_waits_applies_at_once_without_a_restart()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);

        // A minute between polls: after the first one the loop is parked in a long wait.
        SetInterval(services, 60);
        services.Monitor.Start();
        await WaitUntil(() => HealthRequests(listener) >= 1, "the first poll", 3_000);
        await Task.Delay(800);
        Assert.Equal(1, HealthRequests(listener));

        // Shortened: the wait that was sized for a minute is ended, the loop polls, and from then on it follows two seconds.
        SetInterval(services, 2);
        await WaitUntil(() => HealthRequests(listener) >= 2, "the poll the change asked for", 2_500);
        await WaitUntil(() => HealthRequests(listener) >= 3, "the next poll at the new pulse", 4_500);
    }
}
