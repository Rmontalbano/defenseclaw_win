using System.Runtime.InteropServices;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-362. The app's own elevated administrator gateway answered on the port while the dashboard called it "not running from the install
/// directory". Its path is now read without a handle when it cannot be opened, and trusted by the usual rules, but a path read that way says
/// which file runs, not whose process it is: it counts only in this app's Windows session. The administrator copy, which every account may
/// run from Program Files, counts only in this app's session however its path was read. A gateway whose path Windows gives neither way is
/// refused, as before. Nothing here reads a real process but this test process itself.
/// </summary>
public class GatewayPeerSessionTests
{
    private const string Bin = @"C:\fake\install\bin";
    private const string Installed = Bin + @"\defenseclaw-gateway.exe";
    private const string CopyImage = @"C:\fake\pf\DefenseClaw Companion\AdminGateway\S-1-5-21-1-2-3-1001\defenseclaw-gateway.exe";
    private const int AppSession = 2;
    private const int OtherSession = 5;

    /// <summary>An administrator trust that trusts the copy path and records every question.</summary>
    private sealed class Trust : IAdminCopyTrust
    {
        public bool Trusted { get; set; } = true;

        public List<string> Asked { get; } = new();

        public bool IsTrustedImage(string imagePath)
        {
            Asked.Add(imagePath);
            return Trusted && string.Equals(imagePath, CopyImage, StringComparison.OrdinalIgnoreCase);
        }
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

    private static GatewayPeerVerifier Verifier(IAdminCopyTrust? trust = null, IPortOwnerInspector? inspector = null, DefenseClawPaths? paths = null) =>
        new(paths ?? Paths(), inspector ?? new FixedPortInspector(), adminCopy: trust, currentSessionId: AppSession);

    private static PortOwner Owner(string? image, int? session = AppSession, bool withoutHandle = false, string name = "defenseclaw-gateway") =>
        new(11524, name, "127.0.0.1", 18970, image, session, withoutHandle);

    // ---- the verdict ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_gateway_whose_path_Windows_gives_neither_way_is_refused(string? image)
    {
        var trust = new Trust();

        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(Owner(image)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(trust).Classify(Owner(image, withoutHandle: true)));
        Assert.Empty(trust.Asked);
    }

    [Fact]
    public void The_elevated_administrator_copy_read_without_a_handle_in_this_session_is_the_gateway()
    {
        var verdict = Verifier(new Trust()).Judge(Owner(CopyImage, withoutHandle: true));

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.AdministratorCopy), verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_administrator_copy_in_another_session_is_refused_however_its_path_was_read(bool withoutHandle)
    {
        // Every account may run the file in Program Files: another account running it (or a pid it reused) is not the helper's start.
        var verifier = Verifier(new Trust());

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(CopyImage, OtherSession, withoutHandle)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(CopyImage, session: null, withoutHandle)));
    }

    [Fact]
    public void The_installed_gateway_read_without_a_handle_counts_only_in_this_session()
    {
        var verifier = Verifier();

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.InstallDirectory), verifier.Judge(Owner(Installed, AppSession, withoutHandle: true)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(Installed, OtherSession, withoutHandle: true)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(Installed, session: null, withoutHandle: true)));
    }

    [Fact]
    public void The_installed_gateway_read_with_a_handle_keeps_its_old_rule_in_any_session()
    {
        // Unchanged from before CUST-362: a process this app could open, running the installer's file (a gateway started as a task, say).
        Assert.Equal(PortOwnerTrust.Gateway, Verifier().Classify(Owner(Installed, OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, Verifier().Classify(Owner(Installed, session: null)));
    }

    [Fact]
    public void A_stranger_read_without_a_handle_is_refused_in_this_session_too()
    {
        Assert.Equal(PortOwnerTrust.Other, Verifier(new Trust()).Classify(Owner(@"C:\Users\someone\Downloads\defenseclaw-gateway.exe", withoutHandle: true)));
    }

    [Fact]
    public void A_listener_whose_session_is_unknown_never_matches_this_apps()
    {
        // currentSessionId null reads this process's own session; a listener whose session could not be read never matches it.
        var verifier = new GatewayPeerVerifier(Paths(), new FixedPortInspector(), adminCopy: new Trust(), currentSessionId: null);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(Installed, session: null, withoutHandle: true)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(CopyImage, session: null)));
    }

    [Fact]
    public void The_route_names_the_trusted_gateway_and_nothing_else()
    {
        var verifier = Verifier(new Trust());

        Assert.Equal(GatewayPeerRoute.InstallDirectory, verifier.Judge(Owner(Installed)).Route);
        Assert.Equal(GatewayPeerRoute.AdministratorCopy, verifier.Judge(Owner(CopyImage)).Route);
        Assert.Equal(new PeerVerdict(PortOwnerTrust.WslRelay, GatewayPeerRoute.None), verifier.Judge(Owner(null, name: "wslrelay")));
        Assert.Equal(new PeerVerdict(PortOwnerTrust.Unknown, GatewayPeerRoute.None), verifier.Judge(null));
        Assert.Equal(new PeerVerdict(PortOwnerTrust.Other, GatewayPeerRoute.None), verifier.Judge(Owner(CopyImage, OtherSession)));
    }

    [Fact]
    public void Per_port_the_session_is_judged_on_every_call()
    {
        var inspector = new FixedPortInspector { Owner = Owner(CopyImage, withoutHandle: true) };
        var check = Verifier(new Trust(), inspector).ForPort(18970);

        Assert.Equal(PortOwnerTrust.Gateway, check());

        inspector.Owner = Owner(CopyImage, OtherSession, withoutHandle: true);
        Assert.Equal(PortOwnerTrust.Other, check());
    }

    // ---- the words ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_refusal_says_why_in_plain_words()
    {
        var verifier = Verifier(new Trust());

        Assert.Equal(
            "defenseclaw-gateway (pid 11524) could not be verified: Windows does not show this app which file it runs from",
            verifier.DescribeUntrusted(Owner(null)));
        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs in Windows session 5, not this app's session 2, and Windows would not let this app open it to see which account runs it",
            verifier.DescribeUntrusted(Owner(Installed, OtherSession, withoutHandle: true)));
        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs this account's administrator copy, but in Windows session 5, not this app's session 2, so it is not the gateway the helper started",
            verifier.DescribeUntrusted(Owner(CopyImage, OtherSession)));
        Assert.Contains("a Windows session this app could not read", verifier.DescribeUntrusted(Owner(CopyImage, session: null)), StringComparison.Ordinal);
        Assert.Contains("is not running from the DefenseClaw install directory", verifier.DescribeUntrusted(Owner(@"C:\elsewhere\defenseclaw-gateway.exe")), StringComparison.Ordinal);
        Assert.Contains("is not the DefenseClaw gateway", verifier.DescribeUntrusted(Owner(null, name: "node")), StringComparison.Ordinal);
    }

    // ---- the detector ---------------------------------------------------------------------------------------------------

    private static async Task<InstallStatus> Detect(PortOwner owner, IAdminCopyTrust? trust, bool shareVerifier = true)
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var paths = Paths(temp.Path);
        var inspector = new FixedPortInspector { Owner = owner };
        var verifier = Verifier(trust, inspector, paths);
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
        var status = await Detect(Owner(CopyImage, withoutHandle: true), new Trust());

        Assert.Equal(PortOwnerTrust.Gateway, status.OwnerTrust);
        Assert.Equal(GatewayPeerRoute.AdministratorCopy, status.OwnerRoute);
        Assert.Equal("The administrator gateway started by this app is responding on 127.0.0.1:18970.", status.Detail);
    }

    [Fact]
    public async Task An_unverified_process_is_still_called_one_and_sent_nothing()
    {
        var status = await Detect(Owner(null), new Trust());

        Assert.Equal(PortOwnerTrust.Other, status.OwnerTrust);
        Assert.Equal(GatewayPeerRoute.None, status.OwnerRoute);
        Assert.StartsWith("Something is answering on 127.0.0.1:18970, but defenseclaw-gateway (pid 11524) could not be verified", status.Detail, StringComparison.Ordinal);
        Assert.Contains("sends it no credentials", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installed_gateway_keeps_its_words()
    {
        var status = await Detect(Owner(Installed), new Trust());

        Assert.Equal(GatewayPeerRoute.InstallDirectory, status.OwnerRoute);
        Assert.Equal("Gateway responding on 127.0.0.1:18970.", status.Detail);
    }

    [Fact]
    public async Task A_detector_given_no_verifier_trusts_only_the_installers_directories_as_before()
    {
        var status = await Detect(Owner(CopyImage), new Trust(), shareVerifier: false);

        Assert.Equal(PortOwnerTrust.Other, status.OwnerTrust);
    }

    // ---- the inspector ----------------------------------------------------------------------------------------------------

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

    private static PortOwnerInspector Inspector(PortOwnerInspector.ImagePathReader image, List<int> asked) =>
        new(
            (IntPtr table, ref int size, int family) => OneListener(table, ref size, family, 4712, Environment.ProcessId),
            (IntPtr table, ref int size, int family) => NoError,
            pid =>
            {
                asked.Add(pid);
                return image(pid);
            });

    [Fact]
    public void A_listener_carries_its_session_and_how_its_path_was_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var asked = new List<int>();
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        var owner = Inspector(_ => new ProcessImage(CopyImage, WithoutHandle: true), asked).FindListener(4712);

        Assert.NotNull(owner);
        Assert.Equal(CopyImage, owner!.ImagePath);
        Assert.True(owner.ImageWithoutHandle);
        Assert.Equal(self.SessionId, owner.SessionId);
        Assert.Equal(self.ProcessName, owner.ProcessName);
        Assert.Equal(new[] { Environment.ProcessId }, asked);
    }

    [Fact]
    public void A_listener_whose_path_could_not_be_read_either_way_has_none_and_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var owner = Inspector(_ => null, new List<int>()).FindListener(4712);

        Assert.NotNull(owner);
        Assert.Null(owner!.ImagePath);
        Assert.False(owner.ImageWithoutHandle);
        Assert.Equal(PortOwnerTrust.Other, Verifier(new Trust()).Classify(owner with { ProcessName = GatewayPeerVerifier.GatewayProcessName, SessionId = AppSession }));
    }
}
