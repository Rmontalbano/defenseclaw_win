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

        public Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

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
