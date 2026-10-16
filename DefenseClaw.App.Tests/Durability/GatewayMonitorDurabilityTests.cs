using System.Net;
using System.Net.Sockets;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>
/// The monitor carries why a stopped gateway stopped (<see cref="GatewaySnapshot.LastFailure"/>) and what the disk says about its next start
/// (<see cref="GatewaySnapshot.Durability"/>, CUST-352). Every poll is over a scratch data directory with a synthetic gateway.log, hookcfg and
/// receipts; the port is one nothing listens on, or one a stub answers on.
/// </summary>
public sealed class GatewayMonitorDurabilityTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static AppServices Create(TempDirectory temp, int port)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 9\ngateway:\n  api_port: {port}\n");
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        return AppServices.CreateIsolated(paths, temp.File("claude-settings.json"));
    }

    private static string StaleLog() =>
        "2026-10-15T21:18:40Z [sidecar-api] listening on 127.0.0.1:18970\n" +
        "2026-10-15T21:18:41Z [guardrail] connector codex setup failed: connector codex setup failed: inspect effective Codex managed requirements: inspect selected Codex executable: " +
        $"GetFileAttributesEx {OldExe}: The system cannot find the path specified.\n" +
        "2026-10-15T21:18:41Z [sidecar] guardrail exited with error: connector codex setup failed\n";

    [Fact]
    public async Task A_stopped_gateway_carries_the_reason_its_log_gives()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        _ = temp.WriteFile("gateway.log", StaleLog());
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.GatewayStopped, snapshot.State);
        var stale = Assert.IsType<GatewayExitReason.StaleAgentExecutable>(snapshot.LastFailure);
        Assert.Equal(OldExe, stale.Path);
    }

    [Fact]
    public async Task A_stopped_gateway_whose_last_start_reached_listening_has_no_failure_and_one_with_no_log_has_none()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        using var monitor = new GatewayMonitor(services, new ManualClock());

        Assert.Null((await monitor.RefreshAsync()).LastFailure);

        _ = temp.WriteFile("gateway.log", StaleLog() + "2026-10-15T21:19:40Z [sidecar-api] listening on 127.0.0.1:18970\n");
        Assert.Null((await monitor.RefreshAsync()).LastFailure);
    }

    [Fact]
    public async Task The_reason_follows_the_log_when_it_changes_and_a_running_gateway_has_none()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        using var monitor = new GatewayMonitor(services, new ManualClock());

        _ = temp.WriteFile("gateway.log", StaleLog());
        Assert.IsType<GatewayExitReason.StaleAgentExecutable>((await monitor.RefreshAsync()).LastFailure);

        _ = temp.WriteFile(
            "gateway.log",
            StaleLog() + "2026-10-15T21:20:00Z [sidecar-api] listening on 127.0.0.1:18970\n" +
            "2026-10-15T21:20:01Z [guardrail] connector codex setup failed: connector codex setup failed: codex config.toml patch: write codex config: " +
            @"requested recovery leaf is occupied by an unrecognized inode: C:\Users\tester\.codex\config.toml" + "\n");
        Assert.IsType<GatewayExitReason.StuckConfigWrite>((await monitor.RefreshAsync()).LastFailure);

        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, "{\"provenance\":{\"binary_version\":\"1.0.1\"}}"));
        _ = temp.WriteFile("config.yaml", $"config_version: 9\ngateway:\n  api_port: {listener.Port}\n");
        services.ReloadConfig();
        var running = await monitor.RefreshAsync();
        Assert.NotEqual(AppGatewayState.GatewayStopped, running.State);
        Assert.Null(running.LastFailure);
    }

    [Fact]
    public async Task The_snapshot_reads_the_hook_fail_modes_and_a_closed_codex_with_the_gateway_down_is_blocked()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        Directory.CreateDirectory(Path.Combine(temp.Path, "hooks"));
        _ = temp.WriteFile(Path.Combine("hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal("closed", snapshot.Durability!.FailModeOf("codex"));
        Assert.True(snapshot.IsAgentBlocked);

        _ = temp.WriteFile(Path.Combine("hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"open\"}}");
        Assert.False((await monitor.RefreshAsync()).IsAgentBlocked);
    }

    [Fact]
    public async Task The_snapshot_sees_a_moved_pinned_executable_and_a_stuck_pending_write()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        _ = temp.WriteFile("hook_contract_lock.json", $"{{\"version\":2,\"connectors\":{{\"codex\":{{\"connector\":\"codex\",\"agent_executable\":{System.Text.Json.JsonSerializer.Serialize(OldExe)}}}}}}}");
        var codex = Path.Combine(temp.Path, ".codex");
        Directory.CreateDirectory(codex);
        var receipt = temp.WriteFile(".defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent", "{\"version\":2,\"phase\":\"staging\",\"target_path\":" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(codex, "config.toml")) + "}");
        var clock = new ManualClock();
        File.SetLastWriteTimeUtc(receipt, clock.GetUtcNow().UtcDateTime.AddMinutes(-30));
        using var monitor = new GatewayMonitor(services, clock);

        var snapshot = await monitor.RefreshAsync();

        Assert.True(snapshot.Durability!.CodexExecutableMissing);
        Assert.Equal(OldExe, snapshot.Durability.CodexPin!.Executable);
        var pending = Assert.Single(snapshot.Durability.PendingWrites);
        Assert.Equal("068ff1aa22bb33cc44dd55ee", pending.Id);
        Assert.True(pending.IsCodexConfig);
    }

    [Fact]
    public async Task The_durability_files_are_read_again_after_the_interval_or_on_a_manual_refresh_not_on_every_poll()
    {
        using var temp = new TempDirectory();
        using var services = Create(temp, ClosedPort());
        var clock = new ManualClock();
        using var monitor = new GatewayMonitor(services, clock);
        Directory.CreateDirectory(Path.Combine(temp.Path, "hooks"));

        var first = await monitor.PollOnceAsync();
        Assert.Empty(first.Durability!.FailModes);

        _ = temp.WriteFile(Path.Combine("hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        var second = await monitor.PollOnceAsync();
        Assert.Empty(second.Durability!.FailModes);

        clock.Advance(GatewayMonitor.DurabilityInterval + TimeSpan.FromSeconds(1));
        var third = await monitor.PollOnceAsync();
        Assert.Single(third.Durability!.FailModes);

        _ = temp.WriteFile(Path.Combine("hooks", ".hookcfg"), "{\"fail_modes\":{}}");
        var manual = await monitor.RefreshAsync();
        Assert.Empty(manual.Durability!.FailModes);
    }
}
