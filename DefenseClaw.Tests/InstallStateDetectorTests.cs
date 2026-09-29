using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

public class InstallStateDetectorTests
{
    private const string FakeBin = @"C:\fake\bin";

    private sealed class StubGatewayClient : IGatewayClient
    {
        public GatewayResult<GatewayHealth> Health { get; set; } =
            GatewayResult<GatewayHealth>.Unreachable();

        /// <summary>How many times <c>/health</c> was requested through this client.</summary>
        public int HealthCalls { get; private set; }

        public Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            return Task.FromResult(Health);
        }

        public Task<GatewayResult<GatewayStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<GatewayStatusResponse>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<GatewayAlert>>> GetAlertsAsync(int? limit = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<GatewayAlert>>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<SkillEntry>>> GetSkillsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<SkillEntry>>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<McpEntry>>> GetMcpsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<McpEntry>>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<ToolCatalogEntry>>> GetToolsCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<ToolCatalogEntry>>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceBlockedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<EnforcementEntry>>.Unreachable());

        public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceAllowedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<IReadOnlyList<EnforcementEntry>>.Unreachable());
    }

    private sealed class StubPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    private static DefenseClawPaths PathsFor(string dataDirectory, bool installed) =>
        new(
            dataDirectory: dataDirectory,
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: p => installed
                ? p.StartsWith(FakeBin, StringComparison.OrdinalIgnoreCase) || File.Exists(p)
                : File.Exists(p));

    [Fact]
    public async Task NotInstalled_when_no_binaries_are_found()
    {
        using var temp = new TempDirectory();
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: false), new StubGatewayClient(), new StubPortInspector());

        var status = await detector.DetectAsync();

        Assert.Equal(InstallState.NotInstalled, status.State);
        Assert.Contains("not found", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstalledNotInitialized_when_config_yaml_is_missing()
    {
        using var temp = new TempDirectory();
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true), new StubGatewayClient(), new StubPortInspector());

        var status = await detector.DetectAsync();

        Assert.Equal(InstallState.InstalledNotInitialized, status.State);
        Assert.Contains("defenseclaw init", status.Detail, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(FakeBin, "defenseclaw.exe"), status.CliPath);
    }

    [Fact]
    public async Task GatewayStopped_when_health_is_unreachable()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true),
            new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Unreachable() },
            new StubPortInspector());

        var status = await detector.DetectAsync(18970);

        Assert.Equal(InstallState.GatewayStopped, status.State);
        Assert.Equal(GatewayStatus.Unreachable, status.HealthProbe);
        Assert.Contains("18970", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_when_health_answers()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var health = new GatewayHealth { Provenance = new Provenance { BinaryVersion = "0.8.7" } };
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true),
            new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Ok(health) },
            new StubPortInspector());

        var status = await detector.DetectAsync(18970);

        Assert.Equal(InstallState.Running, status.State);
        Assert.True(status.IsRunning);
        Assert.Equal("0.8.7", status.BinaryVersion);
        Assert.False(status.WslGatewayDetected);
    }

    [Fact]
    public async Task WslGatewayDetected_when_the_port_belongs_to_wslrelay()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var health = new GatewayHealth();
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true),
            new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Ok(health) },
            new StubPortInspector { Owner = new PortOwner(4242, "wslrelay", "127.0.0.1") });

        var status = await detector.DetectAsync(18970);

        Assert.True(status.WslGatewayDetected);
        Assert.Equal(4242, status.PortOwner!.Pid);
        Assert.Contains("WSL", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_gateway_owner_does_not_trip_the_wsl_flag()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true),
            new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Ok(new GatewayHealth()) },
            new StubPortInspector { Owner = new PortOwner(64740, "defenseclaw-gateway", "127.0.0.1") });

        var status = await detector.DetectAsync(18970);

        Assert.False(status.WslGatewayDetected);
        Assert.Equal(InstallState.Running, status.State);
    }

    [Fact]
    public async Task Detection_hands_back_the_health_result_so_a_poller_needs_only_one_probe()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var result = GatewayResult<GatewayHealth>.Ok(new GatewayHealth());
        var gateway = new StubGatewayClient { Health = result };
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true), gateway, new StubPortInspector());

        var status = await detector.DetectAsync(18970);

        Assert.Same(result, status.Health);
        Assert.Equal(GatewayStatus.Ok, status.HealthProbe);
        Assert.Equal(1, gateway.HealthCalls);
    }

    [Fact]
    public async Task Health_is_absent_when_detection_stops_before_probing()
    {
        using var notInstalledDir = new TempDirectory();
        var missing = new StubGatewayClient();
        var notInstalled = await new InstallStateDetector(
                PathsFor(notInstalledDir.Path, installed: false), missing, new StubPortInspector())
            .DetectAsync(18970);

        using var uninitializedDir = new TempDirectory();
        var uninitialized = new StubGatewayClient();
        var notInitialized = await new InstallStateDetector(
                PathsFor(uninitializedDir.Path, installed: true), uninitialized, new StubPortInspector())
            .DetectAsync(18970);

        Assert.Equal(InstallState.NotInstalled, notInstalled.State);
        Assert.Null(notInstalled.Health);
        Assert.Null(notInstalled.HealthProbe);
        Assert.Equal(0, missing.HealthCalls);

        Assert.Equal(InstallState.InstalledNotInitialized, notInitialized.State);
        Assert.Null(notInitialized.Health);
        Assert.Equal(0, uninitialized.HealthCalls);
    }

    [Fact]
    public async Task An_explicit_client_is_probed_instead_of_the_constructors()
    {
        // The app rebuilds its gateway client when gateway.api_port changes; the detector must
        // probe the client bound to the port it was asked about, not the one it was built with.
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        var original = new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Unreachable() };
        var retargeted = new StubGatewayClient { Health = GatewayResult<GatewayHealth>.Ok(new GatewayHealth()) };
        var detector = new InstallStateDetector(
            PathsFor(temp.Path, installed: true), original, new StubPortInspector());

        var status = await detector.DetectAsync(19999, retargeted);

        Assert.Equal(InstallState.Running, status.State);
        Assert.Equal(19999, status.Port);
        Assert.Equal(0, original.HealthCalls);
        Assert.Equal(1, retargeted.HealthCalls);
    }

    [Theory]
    [InlineData("wslrelay", true)]
    [InlineData("wslrelay.exe", true)]
    [InlineData("WSLRelay", true)]
    [InlineData("defenseclaw-gateway", false)]
    [InlineData(null, false)]
    public void PortOwner_recognises_the_wsl_relay(string? processName, bool expected)
    {
        Assert.Equal(expected, new PortOwner(1, processName, "127.0.0.1").IsWslRelay);
    }
}
