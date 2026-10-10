using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Who may be sent the bearer token. The name of a process is anyone's to pick, so the executable it runs
/// must also sit where the installer put the gateway.
/// </summary>
public class GatewayPeerVerifierTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string GatewayImage = Bin + @"\defenseclaw-gateway.exe";

    private sealed class FixedPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    private static DefenseClawPaths Paths(IEnumerable<string>? searchPath = null, Func<string, bool>? fileExists = null) =>
        new(
            dataDirectory: @"C:\fake\data",
            binDirectory: Bin,
            searchPath: searchPath ?? Array.Empty<string>(),
            fileExists: fileExists ?? (_ => false));

    private static GatewayPeerVerifier Verifier(DefenseClawPaths? paths = null, FixedPortInspector? inspector = null) =>
        new(paths ?? Paths(), inspector ?? new FixedPortInspector());

    private static PortOwner Owner(string? name, string? image, int pid = 4242) =>
        new(pid, name, "127.0.0.1", 18970, image);

    [Fact]
    public void The_gateway_from_the_install_directory_is_trusted()
    {
        Assert.Equal(PortOwnerTrust.Gateway, Verifier().Classify(Owner("defenseclaw-gateway", GatewayImage)));
    }

    [Fact]
    public void Name_image_and_directory_are_compared_case_insensitively_and_normalised()
    {
        var verifier = Verifier();

        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Owner("DefenseClaw-Gateway", @"c:\FAKE\install\BIN\DefenseClaw-Gateway.EXE")));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Owner("defenseclaw-gateway", @"C:\fake\install\.\bin\..\bin\defenseclaw-gateway.exe")));
    }

    [Fact]
    public void A_gateway_beside_the_one_the_path_resolves_to_is_not_trusted()
    {
        // CUST-247: trust follows the installer's directory, not an environment variable.
        var onPath = @"D:\tools\defenseclaw-gateway.exe";
        var verifier = Verifier(Paths(searchPath: new[] { @"D:\tools" }, fileExists: p => p == onPath));

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner("defenseclaw-gateway", onPath)));
    }

    [Theory]
    [InlineData("defenseclaw-gateway", @"C:\Users\someone\Downloads\defenseclaw-gateway.exe")]
    [InlineData("defenseclaw-gateway", @"C:\fake\install\bin\sub\defenseclaw-gateway.exe")]
    [InlineData("defenseclaw-gateway", @"C:\fake\install\defenseclaw-gateway.exe")]
    [InlineData("defenseclaw-gateway", null)]
    [InlineData("defenseclaw-gateway", "")]
    [InlineData("defenseclaw-gateway", @"C:\fake\install\bin\evil.exe")]
    [InlineData("node", GatewayImage)]
    [InlineData("SomeRandomServer", @"C:\tools\server.exe")]
    [InlineData(null, GatewayImage)]
    public void Anything_that_is_not_the_installed_gateway_is_not_trusted(string? name, string? image)
    {
        Assert.Equal(PortOwnerTrust.Other, Verifier().Classify(Owner(name, image)));
    }

    [Fact]
    public void A_wsl_relay_is_health_only_and_a_missing_owner_is_unknown()
    {
        var verifier = Verifier();

        Assert.Equal(PortOwnerTrust.WslRelay, verifier.Classify(Owner("wslrelay", @"C:\Windows\System32\wslrelay.exe")));
        Assert.Equal(PortOwnerTrust.Unknown, verifier.Classify(null));
        Assert.False(verifier.IsTrustedOwner(Owner("wslrelay", GatewayImage)));
        Assert.False(verifier.IsTrustedOwner(null));
    }

    [Fact]
    public void ForPort_asks_the_inspector_afresh_every_time()
    {
        var inspector = new FixedPortInspector { Owner = Owner("defenseclaw-gateway", GatewayImage) };
        var check = Verifier(inspector: inspector).ForPort(18970);

        Assert.Equal(PortOwnerTrust.Gateway, check());

        inspector.Owner = Owner("node", @"C:\node\node.exe");
        Assert.Equal(PortOwnerTrust.Other, check());

        inspector.Owner = null;
        Assert.Equal(PortOwnerTrust.Unknown, check());
    }

    [Fact]
    public void Descriptions_name_the_process_and_say_why()
    {
        var verifier = Verifier();

        Assert.Contains("wslrelay", verifier.DescribeUntrusted(Owner("wslrelay", null)), StringComparison.Ordinal);
        Assert.Contains("WSL relay", verifier.DescribeUntrusted(Owner("wslrelay", null)), StringComparison.Ordinal);
        Assert.Contains("install directory", verifier.DescribeUntrusted(Owner("defenseclaw-gateway", @"C:\elsewhere\defenseclaw-gateway.exe")), StringComparison.Ordinal);
        Assert.Contains("not the DefenseClaw gateway", verifier.DescribeUntrusted(Owner("node", null)), StringComparison.Ordinal);
        Assert.Contains("could not be identified", verifier.DescribeUntrusted(null), StringComparison.Ordinal);
    }

    // ---- the detector reports the same classification ----------------------------------------

    private sealed class HealthyGateway : IGatewayClient
    {
        public Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GatewayResult<GatewayHealth>.Ok(new GatewayHealth()));

        public Task<GatewayResult<GatewayStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<GatewayAlert>>> GetAlertsAsync(int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<SkillEntry>>> GetSkillsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<McpEntry>>> GetMcpsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<ToolCatalogEntry>>> GetToolsCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceBlockedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceAllowedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("defenseclaw-gateway", GatewayImage, PortOwnerTrust.Gateway)]
    [InlineData("SomeRandomServer", @"C:\tools\server.exe", PortOwnerTrust.Other)]
    [InlineData("wslrelay", null, PortOwnerTrust.WslRelay)]
    public async Task The_detector_reports_the_owner_trust_and_says_so_for_a_stranger(string name, string? image, PortOwnerTrust expected)
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        var detector = new InstallStateDetector(paths, new HealthyGateway(), new FixedPortInspector { Owner = Owner(name, image) });

        var status = await detector.DetectAsync(18970);

        Assert.Equal(expected, status.OwnerTrust);
        if (expected == PortOwnerTrust.Other)
        {
            Assert.Contains("SomeRandomServer", status.Detail, StringComparison.Ordinal);
            Assert.Contains("no credentials", status.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task With_no_owner_found_the_trust_is_unknown_and_the_detail_stays_quiet()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        var detector = new InstallStateDetector(paths, new HealthyGateway(), new FixedPortInspector());

        var status = await detector.DetectAsync(18970);

        Assert.Equal(PortOwnerTrust.Unknown, status.OwnerTrust);
        Assert.Equal(InstallState.Running, status.State);
        Assert.DoesNotContain("credentials", status.Detail, StringComparison.Ordinal);
    }
}
