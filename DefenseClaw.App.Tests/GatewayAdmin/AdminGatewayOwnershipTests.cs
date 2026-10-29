using System.Diagnostics;
using System.Text;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.GatewayAdmin.Contract;
using DefenseClaw.GatewayAdmin.Tests;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// CUST-362. The app's own administrator gateway (started elevated by the helper from
/// <c>%ProgramFiles%\DefenseClaw Companion\AdminGateway\&lt;SID&gt;</c>) answered on the port, and the app called it a process "not running from
/// the install directory". The verified-owner check now accepts it by its image path - read without a handle when the elevated process cannot
/// be opened - in this app's Windows session, and refuses the token to everything else. The helper's <c>last-result.json</c> and the gateway's
/// <c>gateway.pid</c> never stand in for the path, however good or stale they are. Every disk is <see cref="FakeFs"/>, every process a
/// <see cref="PortOwner"/> value: nothing starts, elevates or reads Program Files or the profile.
/// </summary>
public sealed class AdminGatewayOwnershipTests
{
    private const int GatewayPid = 11524;
    private const int AppSession = 1;
    private const int OtherSession = 2;
    private const string OtherSid = "S-1-5-21-9999999999-8888888888-7777777777-1002";

    private static readonly DateTime Finished = new(2026, 10, 21, 9, 0, 30, DateTimeKind.Utc);

    /// <summary>A machine where the helper copied the gateway and its last run was a successful start that left a matching gateway.pid.</summary>
    private static AdminRig Started()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        rig.Fs.AddFile(
            rig.Layout.ResultPath,
            Encoding.UTF8.GetBytes(new AdminRunResult(1, AdminVerbs.Name(AdminVerb.Start), AdminExit.Ok, Finished.ToString("O"), new[] { "defenseclaw-gateway start exited with code 0." }).ToJson()));
        WritePid(rig, rig.Layout.GatewayCopyPath).LastWrite = Finished.AddSeconds(-12);
        return rig;
    }

    private static FakeFs.Item WritePid(AdminRig rig, string executable, string? owner = null, List<AclEntry>? acl = null)
    {
        var json = "{\"pid\":" + GatewayPid + ",\"executable\":" + System.Text.Json.JsonSerializer.Serialize(executable) +
                   ",\"data_dir\":" + System.Text.Json.JsonSerializer.Serialize(rig.Layout.DataDirectory) + ",\"start_time\":1761037200}";
        return rig.Fs.AddFile(Path.Combine(rig.Layout.DataDirectory, "gateway.pid"), Encoding.UTF8.GetBytes(json), owner ?? AdminRig.UserSid, acl ?? AdminRig.UserAcl());
    }

    /// <summary>The gateway as the inspector reports it: its name, its pid from the TCP table, a path (or none), a session, and how the path was read.</summary>
    private static PortOwner Listener(string? image, int? session = AppSession, bool withoutHandle = true, string name = "defenseclaw-gateway") =>
        new(GatewayPid, name, "127.0.0.1", 18970, image, session, withoutHandle);

    private static GatewayPeerVerifier Verifier(AdminRig rig) =>
        new(
            new DefenseClawPaths(dataDirectory: @"C:\TestUsers\operator\.defenseclaw", binDirectory: @"C:\TestApp\no-bin", searchPath: Array.Empty<string>(), fileExists: _ => false),
            new FixedPortInspector(),
            adminCopy: new AdminCopyTrust(rig.LayoutSource, rig.Fs),
            currentSessionId: AppSession);

    private sealed class FixedPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    // ---- accepted: by its file, in this session ---------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_administrator_gateway_is_recognised_by_its_file_however_the_path_was_read(bool withoutHandle)
    {
        var rig = Started();

        Assert.Equal(
            new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.AdministratorCopy),
            Verifier(rig).Judge(Listener(rig.Layout.GatewayCopyPath, withoutHandle: withoutHandle)));
    }

    // ---- refused: the records are never the basis ---------------------------------------------------------------------

    [Fact]
    public void A_gateway_with_no_path_is_refused_even_when_the_helpers_records_name_its_pid()
    {
        var rig = Started();

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(null)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(null, withoutHandle: false)));
        Assert.StartsWith(
            "defenseclaw-gateway (pid 11524) could not be verified: Windows does not show this app which file it runs from",
            Verifier(rig).DescribeUntrusted(Listener(null)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_records_and_a_reused_pid_in_another_session_are_refused()
    {
        // The copy died without a helper run; "start ok" and gateway.pid = 11524 stay behind. Another account loops until it gets pid 11524 and
        // runs the very file the copy is (Users may run it), or any file at all, on the port.
        var rig = Started();
        rig.Fs.Items[Path.Combine(rig.Layout.DataDirectory, "gateway.pid")].LastWrite = Finished.AddDays(-30);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(rig.Layout.GatewayCopyPath, OtherSession)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(rig.Layout.GatewayCopyPath, OtherSession, withoutHandle: false)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(rig.Layout.GatewayCopyPath, session: null)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(@"C:\Users\Public\defenseclaw-gateway.exe", OtherSession)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(null, OtherSession)));
        Assert.Contains("not this app's session 1, so it is not the gateway the helper started", Verifier(rig).DescribeUntrusted(Listener(rig.Layout.GatewayCopyPath, OtherSession, withoutHandle: false)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_pid_with_a_foreign_owner_or_loose_access_list_gives_no_trust_either()
    {
        var foreign = Started();
        WritePid(foreign, foreign.Layout.GatewayCopyPath, owner: OtherSid);
        Assert.Equal(PortOwnerTrust.Other, Verifier(foreign).Classify(Listener(null)));

        var loose = Started();
        WritePid(loose, loose.Layout.GatewayCopyPath, acl: new List<AclEntry> { new(AdminAcl.Everyone, true, 0x1F01FF) });
        Assert.Equal(PortOwnerTrust.Other, Verifier(loose).Classify(Listener(null)));
    }

    // ---- refused: the path, in this session -------------------------------------------------------------------------------

    [Fact]
    public void Another_accounts_copy_is_refused_and_called_that()
    {
        var rig = Started();
        var otherCopy = Path.Combine(rig.Layout.AdminGatewayRoot, OtherSid, AdminLayout.GatewayFileName);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(otherCopy)));
        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs from another account's administrator copy, not this account's, so it is not sent this account's token",
            Verifier(rig).DescribeUntrusted(Listener(otherCopy)));
    }

    [Fact]
    public void A_path_in_the_admin_folder_that_is_not_this_accounts_copy_is_refused()
    {
        var rig = Started();
        var loose = Path.Combine(rig.Layout.AdminGatewayRoot, AdminLayout.GatewayFileName);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(loose)));
        Assert.Contains("not from this account's administrator copy", Verifier(rig).DescribeUntrusted(Listener(loose)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\elsewhere\defenseclaw-gateway.exe")]
    [InlineData(@"C:\TestUsers\tester\Downloads\defenseclaw-gateway.exe")]
    public void A_path_outside_the_admin_folder_keeps_the_install_directory_sentence(string image)
    {
        var rig = Started();

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(image)));
        Assert.Contains("is not running from the DefenseClaw install directory", Verifier(rig).DescribeUntrusted(Listener(image)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_whose_hash_no_longer_matches_its_record_is_refused()
    {
        var rig = Started();
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Data = AdminRig.Gateway.Reverse().ToArray();
        rig.Fs.Items[rig.Layout.GatewayCopyPath].LastWrite = Finished.AddMinutes(5);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(rig.Layout.GatewayCopyPath)));
        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs from this account's administrator copy, but that copy did not pass its checks: it is not the file the helper recorded",
            Verifier(rig).DescribeUntrusted(Listener(rig.Layout.GatewayCopyPath)));
    }

    [Fact]
    public void A_copy_without_its_record_in_a_loosened_folder_or_without_a_layout_is_refused()
    {
        var noRecord = Started();
        noRecord.Fs.Items.Remove(noRecord.Layout.CopyRecordPath);
        Assert.Equal(PortOwnerTrust.Other, Verifier(noRecord).Classify(Listener(noRecord.Layout.GatewayCopyPath)));

        var loose = Started();
        loose.Fs.Items[loose.Layout.UserDirectory].Acl.Add(new AclEntry(AdminAcl.AuthenticatedUsers, true, 0x2));
        Assert.Equal(PortOwnerTrust.Other, Verifier(loose).Classify(Listener(loose.Layout.GatewayCopyPath)));
        Assert.Contains("did not pass its checks", Verifier(loose).DescribeUntrusted(Listener(loose.Layout.GatewayCopyPath)), StringComparison.Ordinal);

        var noLayout = Started();
        noLayout.LayoutSource.Layout = null;
        Assert.Equal(PortOwnerTrust.Other, Verifier(noLayout).Classify(Listener(noLayout.Layout.GatewayCopyPath)));
    }

    [Fact]
    public void A_process_with_another_name_is_refused_even_on_the_copys_path()
    {
        var rig = Started();

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Listener(rig.Layout.GatewayCopyPath, name: "node")));
    }

    // ---- the composition and the monitor ----------------------------------------------------------------------------------

    private const string Bin = @"C:\fake\install\bin";

    private static int OwnSession()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    private static AppServices Compose(TempDirectory temp, AdminRig rig, int port)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {port}\n");
        return AppServices.CreateIsolated(
            new DefenseClawPaths(
                dataDirectory: temp.Path,
                binDirectory: Bin,
                searchPath: Array.Empty<string>(),
                fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p)),
            claudeSettingsPath: temp.File("claude-settings.json"),
            adminLayout: rig.LayoutSource,
            adminFileSystem: rig.Fs,
            readerTimeouts: TestServices.ReaderTimeouts);
    }

    [Fact]
    public void The_dashboards_detector_judges_with_the_verifier_the_token_goes_through()
    {
        using var temp = new TempDirectory();
        var rig = Started();
        using var services = Compose(temp, rig, 18970);

        Assert.Same(services.PeerVerifier, services.InstallDetector.PeerVerifier);
        Assert.Equal(PortOwnerTrust.Gateway, services.PeerVerifier.Classify(Listener(rig.Layout.GatewayCopyPath, OwnSession())));
        Assert.Equal(PortOwnerTrust.Other, services.PeerVerifier.Classify(Listener(null, OwnSession())));
        Assert.Equal(PortOwnerTrust.Other, services.PeerVerifier.Classify(Listener(rig.Layout.GatewayCopyPath, OwnSession() + 1)));
    }

    private static async Task<(GatewaySnapshot Snapshot, GatewayMonitor Monitor, RawHttpListener Listener)> Poll(TempDirectory temp, AdminRig rig, Func<int, PortOwner> owner, List<IDisposable> keep)
    {
        var listener = new RawHttpListener(request =>
            request.Path == "/health"
                ? RawHttpListener.Response(200, "{\"provenance\":{\"binary_version\":\"1.0.4\"}}")
                : RawHttpListener.Response(401, "{\"error\":\"unauthorized\"}"));
        keep.Add(listener);
        var services = Compose(temp, rig, listener.Port);
        keep.Add(services);
        var inspector = new FixedPortInspector { Owner = owner(listener.Port) };
        var detector = new InstallStateDetector(services.Paths, services.Gateway, inspector, services.PeerVerifier);
        var monitor = new GatewayMonitor(services, new ManualClock(), detector);
        keep.Add(monitor);
        return (await monitor.RefreshAsync(), monitor, listener);
    }

    [Fact]
    public async Task The_monitor_shows_the_elevated_administrator_gateway_as_running_and_polls_it()
    {
        using var temp = new TempDirectory();
        var keep = new List<IDisposable>();
        try
        {
            var rig = Started();
            var (snapshot, monitor, _) = await Poll(temp, rig, port => Listener(rig.Layout.GatewayCopyPath, OwnSession()) with { Port = port }, keep);

            Assert.Equal(AppGatewayState.Running, snapshot.State);
            Assert.False(snapshot.PeerUnverified);
            Assert.Equal("The administrator gateway started by this app is responding on 127.0.0.1:" + snapshot.ApiPort + ".", snapshot.Detail);
            Assert.Equal(1, monitor.AlertsFetchCount);
        }
        finally
        {
            keep.Reverse();
            keep.ForEach(static d => d.Dispose());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_monitor_calls_a_pathless_or_foreign_session_gateway_unverified_and_sends_it_nothing(bool foreignSession)
    {
        using var temp = new TempDirectory();
        var keep = new List<IDisposable>();
        try
        {
            var rig = Started();
            var (snapshot, monitor, listener) = await Poll(
                temp,
                rig,
                port => (foreignSession ? Listener(rig.Layout.GatewayCopyPath, OwnSession() + 1) : Listener(null, OwnSession())) with { Port = port },
                keep);

            Assert.Equal(AppGatewayState.Degraded, snapshot.State);
            Assert.True(snapshot.PeerUnverified);
            Assert.Contains(foreignSession ? $"not this app's session {OwnSession()}" : "could not be verified", snapshot.Detail, StringComparison.Ordinal);
            Assert.Contains("sends it no credentials", snapshot.AlertsUnavailable, StringComparison.Ordinal);
            Assert.Equal(0, monitor.AlertsFetchCount);
            Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
        }
        finally
        {
            keep.Reverse();
            keep.ForEach(static d => d.Dispose());
        }
    }
}
