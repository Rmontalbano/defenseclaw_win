using System.Runtime.InteropServices;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-362: the app's own administrator gateway (elevated, started by the helper from Program Files) answered on the port while the app said
/// "something is answering ... not running from the install directory". Two things were wrong: the dashboard's detector judged the port's owner
/// with a verifier that knew nothing of the administrator copy, and an owner whose image path Windows would not give out was refused with words
/// that read like a stuck or rogue process. Here: a gateway with no readable path is still refused unless the administrator trust's records
/// name it, the verdict says which trusted gateway it is, and the detector words each case plainly. Nothing here reads a real process but this
/// test process itself.
/// </summary>
public class GatewayPeerUnreadableImageTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string CopyImage = @"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe";

    /// <summary>An administrator trust that answers as told and records what it was asked.</summary>
    private sealed class Trust : IAdminCopyTrust
    {
        public bool ImageTrusted { get; set; }

        public bool Started { get; set; }

        public string? Why { get; set; }

        public List<int> StartedAsked { get; } = new();

        public bool IsTrustedImage(string imagePath) => ImageTrusted;

        public bool IsStartedCopy(PortOwner owner)
        {
            StartedAsked.Add(owner.Pid);
            return Started;
        }

        public string? ExplainRefusal(PortOwner owner) => Why;
    }

    /// <summary>One that implements only the original member, as an older implementation would.</summary>
    private sealed class ImageOnlyTrust : IAdminCopyTrust
    {
        public bool IsTrustedImage(string imagePath) => true;
    }

    private sealed class FixedPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

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

    private static DefenseClawPaths Paths(string? data = null) =>
        new(
            dataDirectory: data ?? @"C:\fake\data",
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

    private static GatewayPeerVerifier Verifier(IAdminCopyTrust? trust, IPortOwnerInspector? inspector = null) =>
        new(Paths(), inspector ?? new FixedPortInspector(), adminCopy: trust);

    private static PortOwner Owner(string? image, string name = "defenseclaw-gateway", int pid = 11524) =>
        new(pid, name, "127.0.0.1", 18970, image);

    // ---- the verdict ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_gateway_whose_path_cannot_be_read_is_trusted_only_when_the_records_name_it(string? image)
    {
        var trust = new Trust { Started = true };

        var verdict = Verifier(trust).Judge(Owner(image));

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.AdministratorCopyByRecord), verdict);
        Assert.Equal(new[] { 11524 }, trust.StartedAsked);

        trust.Started = false;
        Assert.Equal(new PeerVerdict(PortOwnerTrust.Other, GatewayPeerRoute.None), Verifier(trust).Judge(Owner(image)));
    }

    [Fact]
    public void An_unreadable_path_with_no_administrator_trust_is_refused()
    {
        Assert.Equal(PortOwnerTrust.Other, Verifier(null).Classify(Owner(null)));
    }

    [Fact]
    public void An_older_trust_that_reads_no_records_refuses_an_unreadable_path_even_though_it_trusts_every_image()
    {
        var verifier = Verifier(new ImageOnlyTrust());

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(null)));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Owner(CopyImage)));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("defenseclaw")]
    [InlineData(null)]
    public void The_records_are_not_asked_about_a_process_with_another_name(string? name)
    {
        var trust = new Trust { Started = true };

        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(new PortOwner(11524, name, "127.0.0.1", 18970, null)));
        Assert.Empty(trust.StartedAsked);
    }

    [Fact]
    public void A_readable_path_is_never_overruled_by_the_records()
    {
        // The records say "this pid is ours", but Windows says it runs a file somewhere else: the kernel's answer stands.
        var trust = new Trust { Started = true, ImageTrusted = false };

        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(Owner(@"C:\Users\someone\Downloads\defenseclaw-gateway.exe")));
        Assert.Empty(trust.StartedAsked);
    }

    [Fact]
    public void The_route_says_which_trusted_gateway_it_is()
    {
        var trust = new Trust { ImageTrusted = true, Started = true };
        var verifier = Verifier(trust);

        Assert.Equal(GatewayPeerRoute.InstallDirectory, verifier.Judge(Owner(Bin + @"\defenseclaw-gateway.exe")).Route);
        Assert.Equal(GatewayPeerRoute.AdministratorCopy, verifier.Judge(Owner(CopyImage)).Route);
        Assert.Equal(GatewayPeerRoute.AdministratorCopyByRecord, verifier.Judge(Owner(null)).Route);
        Assert.Equal(new PeerVerdict(PortOwnerTrust.WslRelay, GatewayPeerRoute.None), verifier.Judge(Owner(null, name: "wslrelay")));
        Assert.Equal(new PeerVerdict(PortOwnerTrust.Unknown, GatewayPeerRoute.None), verifier.Judge(null));
    }

    [Fact]
    public void A_listener_matched_by_the_records_is_trusted_per_port_and_per_connection_alike()
    {
        var inspector = new FixedPortInspector { Owner = Owner(null) };
        var trust = new Trust { Started = true };
        var verifier = Verifier(trust, inspector);

        Assert.Equal(PortOwnerTrust.Gateway, verifier.ForPort(18970)());

        trust.Started = false;
        Assert.Equal(PortOwnerTrust.Other, verifier.ForPort(18970)());
    }

    // ---- the words --------------------------------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_path_is_described_as_unverifiable_not_as_a_gateway_from_elsewhere()
    {
        var text = Verifier(null).DescribeUntrusted(Owner(null));

        Assert.Contains("defenseclaw-gateway (pid 11524) could not be verified", text, StringComparison.Ordinal);
        Assert.Contains("Windows does not show this app which file it runs from", text, StringComparison.Ordinal);
        Assert.DoesNotContain("install directory", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_administrator_trusts_explanation_is_used_when_it_has_one()
    {
        var trust = new Trust { Why = "runs from another account's administrator copy, not this account's" };

        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs from another account's administrator copy, not this account's",
            Verifier(trust).DescribeUntrusted(Owner(@"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1-5-21-9-9-9-9\defenseclaw-gateway.exe")));

        // A process that is not the gateway at all keeps the plain sentence: there is nothing about the copy to explain.
        Assert.Contains("is not the DefenseClaw gateway", Verifier(trust).DescribeUntrusted(Owner(null, name: "node")), StringComparison.Ordinal);
    }

    // ---- the detector -----------------------------------------------------------------------------------------------

    private static async Task<InstallStatus> Detect(PortOwner owner, IAdminCopyTrust? trust, bool shareVerifier = true)
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var paths = Paths(temp.Path);
        var inspector = new FixedPortInspector { Owner = owner };
        var verifier = new GatewayPeerVerifier(paths, inspector, adminCopy: trust);
        var detector = shareVerifier
            ? new InstallStateDetector(paths, new HealthyGateway(), inspector, verifier)
            : new InstallStateDetector(paths, new HealthyGateway(), inspector);
        if (shareVerifier)
        {
            Assert.Same(verifier, detector.PeerVerifier);
        }

        return await detector.DetectAsync(18970);
    }

    [Fact]
    public async Task The_administrator_copy_is_named_as_the_gateway_this_app_started()
    {
        var status = await Detect(Owner(CopyImage), new Trust { ImageTrusted = true });

        Assert.Equal(PortOwnerTrust.Gateway, status.OwnerTrust);
        Assert.Equal(GatewayPeerRoute.AdministratorCopy, status.OwnerRoute);
        Assert.Equal("The administrator gateway started by this app is responding on 127.0.0.1:18970.", status.Detail);
    }

    [Fact]
    public async Task A_copy_matched_by_the_records_says_how_it_was_matched()
    {
        var status = await Detect(Owner(null), new Trust { Started = true });

        Assert.Equal(PortOwnerTrust.Gateway, status.OwnerTrust);
        Assert.Equal(GatewayPeerRoute.AdministratorCopyByRecord, status.OwnerRoute);
        Assert.StartsWith("The administrator gateway started by this app is responding on 127.0.0.1:18970. ", status.Detail, StringComparison.Ordinal);
        Assert.Contains("administrator helper's records", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unverified_process_is_still_called_one_and_sent_nothing()
    {
        var status = await Detect(Owner(null), new Trust { Started = false });

        Assert.Equal(PortOwnerTrust.Other, status.OwnerTrust);
        Assert.Equal(GatewayPeerRoute.None, status.OwnerRoute);
        Assert.StartsWith("Something is answering on 127.0.0.1:18970, but defenseclaw-gateway (pid 11524) could not be verified", status.Detail, StringComparison.Ordinal);
        Assert.Contains("sends it no credentials", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installed_gateway_keeps_its_words()
    {
        var status = await Detect(Owner(Bin + @"\defenseclaw-gateway.exe"), new Trust());

        Assert.Equal(GatewayPeerRoute.InstallDirectory, status.OwnerRoute);
        Assert.Equal("Gateway responding on 127.0.0.1:18970.", status.Detail);
    }

    [Fact]
    public async Task A_detector_given_no_verifier_trusts_only_the_installers_directories_as_before()
    {
        var status = await Detect(Owner(CopyImage), new Trust { ImageTrusted = true }, shareVerifier: false);

        Assert.Equal(PortOwnerTrust.Other, status.OwnerTrust);
    }

    // ---- the inspector's image-path seam ------------------------------------------------------------------------------

    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int RowSize = 24;

    private static uint OneListener(IntPtr table, ref int size, int family, int port, int pid)
    {
        if (family != AfInet)
        {
            return NoError;
        }

        if (table == IntPtr.Zero)
        {
            size = 4 + RowSize;
            return InsufficientBuffer;
        }

        Marshal.WriteInt32(table, 0, 1);
        Marshal.WriteInt32(table, 4, 2);
        Marshal.WriteInt32(table, 8, 0x0100007F);
        Marshal.WriteInt32(table, 12, ((port & 0xFF) << 8) | (port >> 8));
        Marshal.WriteInt32(table, 16, 0);
        Marshal.WriteInt32(table, 20, 0);
        Marshal.WriteInt32(table, 24, pid);
        return NoError;
    }

    [Fact]
    public void A_listener_whose_path_Windows_will_not_give_has_no_image_and_is_refused_without_records()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var asked = new List<int>();
        var inspector = new PortOwnerInspector(
            (IntPtr table, ref int size, int family) => OneListener(table, ref size, family, 4712, Environment.ProcessId),
            (IntPtr table, ref int size, int family) => NoError,
            pid =>
            {
                asked.Add(pid);
                return null; // Access denied, as for an elevated process that a security product filters.
            });

        var owner = inspector.FindListener(4712);
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.NotNull(owner);
        Assert.Null(owner!.ImagePath);
        Assert.Equal(self.ProcessName, owner.ProcessName);
        Assert.Equal(new[] { Environment.ProcessId }, asked);
        Assert.Equal(PortOwnerTrust.Other, Verifier(new ImageOnlyTrust()).Classify(owner));
    }

    [Fact]
    public void The_limited_query_reads_this_processs_own_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The one real call: this test process asking about itself, which any account may do.
        Assert.Equal(Environment.ProcessPath, PortOwnerInspector.ReadImagePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        Assert.Null(PortOwnerInspector.ReadImagePath(0));
        Assert.Null(PortOwnerInspector.ReadImagePath(-5));
    }
}
