using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The monitor is the single source of gateway state for the tray, the shell and the panels, so what it
/// does when things go wrong is what the operator sees: a subscriber that throws, a poll that throws, a
/// clock that steps, and a listener on the API port that is not the gateway.
/// <para>
/// Every test polls a scratch data directory and a listener of its own (or nothing); none of it can reach
/// the real install.
/// </para>
/// </summary>
public class GatewayMonitorTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string GatewayImage = Bin + @"\defenseclaw-gateway.exe";
    private const string TokenVariable = "DC_APP_TESTS_MONITOR_TOKEN";
    private const string SecretToken = "monitor-test-token-9f8e7d6c5b4a";
    private const string HealthBody = "{\"provenance\":{\"binary_version\":\"9.9.9\"}}";

    private sealed class FixedPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    /// <summary>
    /// A listener that answers <c>/health</c> and nothing else needs. Tests that do not care about the answer
    /// still point the config at one: Windows retries a connection to a closed loopback port for about two
    /// seconds before it gives up, per poll.
    /// </summary>
    private static RawHttpListener Quiet() => new(_ => RawHttpListener.Response(200, HealthBody));

    /// <summary>Paths that see the installer's binaries (a fake bin directory) but the scratch data directory.</summary>
    private static DefenseClawPaths InstalledPaths(TempDirectory temp) =>
        new(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

    private static AppServices Create(TempDirectory temp, int port, string? claudeSettingsPath = null, string? dotEnv = null)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {port}\n  token_env: {TokenVariable}\n");
        if (dotEnv is not null)
        {
            _ = temp.WriteFile(".env", dotEnv);
        }

        return AppServices.CreateIsolated(InstalledPaths(temp), claudeSettingsPath ?? temp.File("claude-settings.json"));
    }

    // ---- D1-02: one bad subscriber must not starve the rest --------------------------------------------

    [Fact]
    public async Task A_throwing_StateChanged_subscriber_does_not_skip_the_others_or_PollCompleted()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var secondCalls = 0;
        var pollCompletedCalls = 0;
        monitor.StateChanged += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        monitor.StateChanged += (_, _) => secondCalls++;
        monitor.PollCompleted += (_, _) => throw new InvalidOperationException("and so does this one");
        monitor.PollCompleted += (_, _) => pollCompletedCalls++;

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(1, secondCalls);
        Assert.Equal(1, pollCompletedCalls);
        Assert.Same(snapshot, monitor.Current);

        // The material change was announced once, to everybody; the next poll is not a change but is still a poll.
        _ = await monitor.RefreshAsync();
        Assert.Equal(1, secondCalls);
        Assert.Equal(2, pollCompletedCalls);
    }

    // ---- D1-03: a poll that throws is shown, not swallowed ---------------------------------------------

    /// <summary>A settings path with a NUL in it: <c>new FileInfo</c> throws <see cref="ArgumentException"/>, which nothing in the poll guards.</summary>
    private const string PoisonedSettingsPath = "C:\\bad\0settings.json";

    [Fact]
    public async Task A_poll_that_throws_is_surfaced_after_three_in_a_row()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port, claudeSettingsPath: PoisonedSettingsPath);
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var stateChanges = new List<GatewaySnapshot>();
        monitor.StateChanged += (_, e) => stateChanges.Add(e.Snapshot);

        for (var poll = 1; poll < GatewayMonitor.PollFaultsBeforeSurface; poll++)
        {
            _ = await monitor.PollOnceAsync();

            // Still "Checking…": one or two faults are a blip, nothing is published yet.
            Assert.Equal(AppGatewayState.Unknown, monitor.Current.State);
            Assert.Empty(stateChanges);
        }

        var surfaced = await monitor.PollOnceAsync();

        Assert.StartsWith("The gateway poll is failing: ", surfaced.Detail, StringComparison.Ordinal);
        Assert.Contains("Exception", surfaced.Detail, StringComparison.Ordinal);
        Assert.Equal(AppGatewayState.Degraded, surfaced.State);
        Assert.Same(surfaced, monitor.Current);
        Assert.Single(stateChanges);
    }

    [Fact]
    public async Task A_manual_refresh_reports_the_fault_at_once_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port, claudeSettingsPath: PoisonedSettingsPath);
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.StartsWith("The gateway poll is failing: ", snapshot.Detail, StringComparison.Ordinal);
        Assert.Same(snapshot, monitor.Current);
    }

    // ---- D1-04: the cadence gates follow the monotonic clock ------------------------------------------------

    [Fact]
    public async Task A_wall_clock_step_neither_stops_nor_hastens_the_alert_and_status_polls()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(request =>
            request.Path == "/health"
                ? RawHttpListener.Response(200, HealthBody)
                : RawHttpListener.Response(401, "{\"error\":\"unauthorized\"}"));
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();

        // The detector is told the gateway owns the port, so the monitor goes on to fetch alerts and status.
        var inspector = new FixedPortInspector
        {
            Owner = new PortOwner(4242, "defenseclaw-gateway", "127.0.0.1", listener.Port, GatewayImage),
        };
        var detector = new InstallStateDetector(services.Paths, services.Gateway, inspector);
        using var monitor = new GatewayMonitor(services, clock, detector);

        _ = await monitor.PollOnceAsync();
        Assert.Equal(1, monitor.AlertsFetchCount);
        Assert.Equal(1, monitor.StatusFetchCount);

        // Five real seconds later: inside the 30 s cadence, nothing is fetched.
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(1, monitor.AlertsFetchCount);

        // The wall clock is stepped back an hour, then the cadence really elapses. A gate written as
        // UtcNow - stamp would read minus 59 minutes here and stay shut for another hour.
        clock.StepWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(26));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, monitor.AlertsFetchCount);
        Assert.Equal(2, monitor.StatusFetchCount);

        // And a step forwards must not open it early.
        clock.StepWallClock(TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await monitor.PollOnceAsync();
        Assert.Equal(2, monitor.AlertsFetchCount);
        Assert.Equal(2, monitor.StatusFetchCount);
    }

    [Fact]
    public async Task The_snapshot_carries_the_injected_clocks_wall_time()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener.Port);
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(clock.GetUtcNow(), snapshot.PolledAt);
    }

    // ---- D3-02: the token goes to the gateway and nobody else -------------------------------------------------

    [Fact]
    public async Task A_stranger_answering_on_the_port_is_never_sent_the_token_and_is_not_called_running()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(404, "{\"error\":\"not found\"}"));
        using var services = Create(temp, listener.Port, dotEnv: $"{TokenVariable}={SecretToken}\n");
        using var monitor = new GatewayMonitor(services, new ManualClock());

        Assert.True(services.Token.Found, "the fixture token must resolve, or this test proves nothing");

        var snapshot = await monitor.RefreshAsync();

        // The listener is this test process: not defenseclaw-gateway, so no credentials, no /alerts, no /status.
        Assert.Equal(0, monitor.AlertsFetchCount);
        Assert.Equal(0, monitor.StatusFetchCount);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(listener.Requests, r => r.Path != "/health");
        Assert.NotEqual(AppGatewayState.Running, snapshot.State);
        Assert.Contains("not the DefenseClaw gateway", snapshot.AlertsUnavailable, StringComparison.Ordinal);
        Assert.Contains("no credentials", snapshot.AlertsUnavailable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_health_answer_from_a_stranger_is_degraded_not_running()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, HealthBody));
        using var services = Create(temp, listener.Port, dotEnv: $"{TokenVariable}={SecretToken}\n");
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.Degraded, snapshot.State);
        Assert.Equal(GatewayStatus.Ok, snapshot.HealthStatus);
        Assert.Equal(0, monitor.AlertsFetchCount);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task A_wsl_relay_is_read_through_health_only()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, HealthBody));
        using var services = Create(temp, listener.Port, dotEnv: $"{TokenVariable}={SecretToken}\n");
        var inspector = new FixedPortInspector
        {
            Owner = new PortOwner(31337, "wslrelay", "127.0.0.1", listener.Port, @"C:\Windows\System32\wslrelay.exe"),
        };
        var detector = new InstallStateDetector(services.Paths, services.Gateway, inspector);
        using var monitor = new GatewayMonitor(services, new ManualClock(), detector);

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.WslGatewayDetected, snapshot.State);
        Assert.Equal(0, monitor.AlertsFetchCount);
        Assert.Equal(0, monitor.StatusFetchCount);
        Assert.Contains("WSL relay", snapshot.AlertsUnavailable, StringComparison.Ordinal);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
    }
}
