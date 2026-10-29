using System.Text;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.AiRuntime;
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
/// the install directory". The verified-owner check now accepts it - by its image path when Windows gives one, and otherwise by the helper's
/// admin-only records plus the gateway's <c>gateway.pid</c> naming the very pid on the port - and still refuses the token to everything it cannot
/// tie to that copy. Every disk is <see cref="FakeFs"/>, every process a <see cref="PortOwner"/> value: nothing starts, elevates or reads
/// Program Files or the profile.
/// </summary>
public sealed class AdminGatewayOwnershipTests
{
    private const int GatewayPid = 11524;
    private const string OtherSid = "S-1-5-21-9999999999-8888888888-7777777777-1002";

    /// <summary>When the helper says its last start finished.</summary>
    private static readonly DateTime Finished = new(2026, 10, 21, 9, 0, 30, DateTimeKind.Utc);

    private static string PidPath(AdminRig rig) => Path.Combine(rig.Layout.DataDirectory, GatewayPidRecord.FileName);

    /// <summary>A machine where the helper copied the gateway, started it, and the gateway wrote its pid record during that start.</summary>
    private static AdminRig Started(AdminVerb verb = AdminVerb.Start)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        LeaveResult(rig, verb, AdminExit.Ok);
        WritePid(rig, GatewayPid, rig.Layout.GatewayCopyPath, rig.Layout.DataDirectory, Finished.AddSeconds(-12));
        return rig;
    }

    private static void LeaveResult(AdminRig rig, AdminVerb verb, int exitCode, DateTime? finished = null) =>
        rig.Fs.AddFile(
            rig.Layout.ResultPath,
            Encoding.UTF8.GetBytes(new AdminRunResult(1, AdminVerbs.Name(verb), exitCode, (finished ?? Finished).ToString("O"), new[] { "defenseclaw-gateway start exited with code 0." }).ToJson()));

    private static FakeFs.Item WritePid(AdminRig rig, int pid, string executable, string? dataDirectory, DateTime written)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = pid,
            ["executable"] = executable,
            ["data_dir"] = dataDirectory,
            ["start_time"] = 1761037200,
            ["start_identity"] = "133423344180000000",
        });
        return WritePidText(rig, json, written);
    }

    private static FakeFs.Item WritePidText(AdminRig rig, string text, DateTime written)
    {
        var item = rig.Fs.AddFile(PidPath(rig), Encoding.UTF8.GetBytes(text), AdminRig.UserSid, AdminRig.UserAcl());
        item.LastWrite = written;
        return item;
    }

    /// <summary>The elevated gateway as an unelevated app sees it: its name, its pid from the TCP table, and no image path.</summary>
    private static PortOwner Hidden(int pid = GatewayPid) => new(pid, "defenseclaw-gateway", "127.0.0.1", 18970, null);

    private static PortOwner Visible(string image) => new(GatewayPid, "defenseclaw-gateway", "127.0.0.1", 18970, image);

    private static AdminCopyTrust Trust(AdminRig rig) => new(rig.LayoutSource, rig.Fs);

    private static GatewayPeerVerifier Verifier(AdminRig rig) =>
        new(
            new DefenseClawPaths(dataDirectory: @"C:\TestUsers\operator\.defenseclaw", binDirectory: @"C:\TestApp\no-bin", searchPath: Array.Empty<string>(), fileExists: _ => false),
            new FixedPortInspector(),
            adminCopy: Trust(rig));

    private sealed class FixedPortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    // ---- accepted ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AdminVerb.Start)]
    [InlineData(AdminVerb.Restart)]
    public void The_administrator_gateway_whose_path_is_hidden_is_matched_by_the_helpers_records(AdminVerb verb)
    {
        var rig = Started(verb);

        Assert.True(Trust(rig).IsStartedCopy(Hidden()));
        Assert.Null(Trust(rig).ExplainRefusal(Hidden()));
        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.AdministratorCopyByRecord), Verifier(rig).Judge(Hidden()));
    }

    [Fact]
    public void The_administrator_gateway_whose_path_is_readable_is_matched_by_its_file()
    {
        var rig = Started();

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.AdministratorCopy), Verifier(rig).Judge(Visible(rig.Layout.GatewayCopyPath)));
    }

    [Fact]
    public void A_record_without_a_data_folder_is_still_matched_by_the_rest()
    {
        var rig = Started();
        WritePid(rig, GatewayPid, rig.Layout.GatewayCopyPath, null, Finished.AddSeconds(-3));

        Assert.True(Trust(rig).IsStartedCopy(Hidden()));
    }

    // ---- refused: the helper's record -------------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_path_with_no_helper_record_is_refused()
    {
        var rig = Started();
        rig.Fs.Items.Remove(rig.Layout.ResultPath);

        AssertRefused(rig, Hidden(), "no record of starting a gateway");
    }

    [Theory]
    [InlineData(AdminVerb.Stop, AdminExit.Ok, "'stop' with exit code 0")]
    [InlineData(AdminVerb.InstallCopy, AdminExit.Ok, "'install-copy' with exit code 0")]
    [InlineData(AdminVerb.RepairOwnership, AdminExit.Ok, "'repair-ownership'")]
    [InlineData(AdminVerb.Start, AdminExit.Failed, "'start' with exit code 1")]
    [InlineData(AdminVerb.Start, AdminExit.Refused, "exit code 77")]
    public void A_helper_whose_last_run_was_not_a_successful_start_names_nothing(AdminVerb verb, int exitCode, string expected)
    {
        var rig = Started();
        LeaveResult(rig, verb, exitCode);

        AssertRefused(rig, Hidden(), expected);
    }

    [Fact]
    public void A_helper_record_that_users_can_write_is_refused()
    {
        var rig = Started();
        rig.Fs.Items[rig.Layout.ResultPath].Acl.Add(new AclEntry(AdminAcl.Users, true, 0x2));

        AssertRefused(rig, Hidden(), "not admin-only");
    }

    [Fact]
    public void A_helper_record_owned_by_the_user_is_refused()
    {
        var rig = Started();
        rig.Fs.Items[rig.Layout.ResultPath].Owner = AdminRig.UserSid;

        AssertRefused(rig, Hidden(), "not admin-only");
    }

    [Fact]
    public void A_helper_record_that_is_a_link_or_unreadable_is_refused()
    {
        var link = Started();
        link.Fs.Items[link.Layout.ResultPath].IsReparse = true;
        AssertRefused(link, Hidden(), "is a link");

        var garbled = Started();
        garbled.Fs.Items[garbled.Layout.ResultPath].Data = Encoding.UTF8.GetBytes("{ not json");
        AssertRefused(garbled, Hidden(), "cannot be read");
    }

    // ---- refused: gateway.pid ---------------------------------------------------------------------------------------

    [Fact]
    public void A_pid_file_naming_a_different_process_is_refused()
    {
        var rig = Started();

        AssertRefused(rig, Hidden(pid: 4242), "gateway.pid names pid 11524, a different process");
    }

    [Fact]
    public void A_pid_file_naming_another_accounts_copy_is_refused()
    {
        var rig = Started();
        var otherCopy = Path.Combine(rig.Layout.AdminGatewayRoot, OtherSid, AdminLayout.GatewayFileName);
        WritePid(rig, GatewayPid, otherCopy, rig.Layout.DataDirectory, Finished.AddSeconds(-12));

        AssertRefused(rig, Hidden(), "not this account's administrator copy");
    }

    [Theory]
    [InlineData(@"C:\TestUsers\tester\.local\bin\defenseclaw-gateway.exe")]
    [InlineData(@"C:\TestPF\DefenseClaw Companion\AdminGateway\defenseclaw-gateway.exe")]
    [InlineData(@"C:\Users\someone\Downloads\defenseclaw-gateway.exe")]
    public void A_pid_file_naming_a_path_outside_this_accounts_copy_is_refused(string executable)
    {
        var rig = Started();
        WritePid(rig, GatewayPid, executable, rig.Layout.DataDirectory, Finished.AddSeconds(-12));

        AssertRefused(rig, Hidden(), "not this account's administrator copy");
    }

    [Fact]
    public void A_pid_file_for_another_data_folder_is_refused()
    {
        var rig = Started();
        WritePid(rig, GatewayPid, rig.Layout.GatewayCopyPath, @"D:\elsewhere\.defenseclaw", Finished.AddSeconds(-12));

        AssertRefused(rig, Hidden(), "data folder");
    }

    [Theory]
    [InlineData("11524")]
    [InlineData("{\"pid\":11524}")]
    [InlineData("garbage")]
    public void A_pid_file_that_does_not_name_the_file_it_runs_is_refused(string text)
    {
        var rig = Started();
        WritePidText(rig, text, Finished.AddSeconds(-12));

        AssertRefused(rig, Hidden(), "does not name a process");
    }

    [Fact]
    public void A_missing_or_linked_pid_file_is_refused()
    {
        var missing = Started();
        missing.Fs.Items.Remove(PidPath(missing));
        AssertRefused(missing, Hidden(), "no gateway.pid");

        var link = Started();
        link.Fs.Items[PidPath(link)].IsReparse = true;
        AssertRefused(link, Hidden(), "is a link");
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(-241)]
    [InlineData(11)]
    [InlineData(86400)]
    public void A_pid_file_not_written_during_the_helpers_last_start_is_refused(int secondsFromFinish)
    {
        // An old gateway.pid whose pid Windows has since handed to another process, or one written long after: either way not that start's.
        var rig = Started();
        rig.Fs.Items[PidPath(rig)].LastWrite = Finished.AddSeconds(secondsFromFinish);

        AssertRefused(rig, Hidden(), "not written during the administrator helper's last start");
    }

    [Theory]
    [InlineData(-239)]
    [InlineData(0)]
    [InlineData(9)]
    public void A_pid_file_written_inside_the_start_window_is_accepted(int secondsFromFinish)
    {
        var rig = Started();
        rig.Fs.Items[PidPath(rig)].LastWrite = Finished.AddSeconds(secondsFromFinish);

        Assert.True(Trust(rig).IsStartedCopy(Hidden()));
    }

    // ---- refused: the copy itself -----------------------------------------------------------------------------------

    [Fact]
    public void A_copy_whose_hash_no_longer_matches_its_record_is_refused_whether_or_not_its_path_is_readable()
    {
        var rig = Started();
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Data = AdminRig.Gateway.Reverse().ToArray();
        rig.Fs.Items[rig.Layout.GatewayCopyPath].LastWrite = Finished.AddMinutes(5);

        AssertRefused(rig, Hidden(), "did not pass its checks");
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Visible(rig.Layout.GatewayCopyPath)));
        Assert.Contains("runs from this account's administrator copy, but that copy did not pass its checks", Verifier(rig).DescribeUntrusted(Visible(rig.Layout.GatewayCopyPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_without_its_record_or_in_a_loosened_folder_is_refused()
    {
        var noRecord = Started();
        noRecord.Fs.Items.Remove(noRecord.Layout.CopyRecordPath);
        AssertRefused(noRecord, Hidden(), "did not pass its checks");

        var loose = Started();
        loose.Fs.Items[loose.Layout.UserDirectory].Acl.Add(new AclEntry(AdminAcl.AuthenticatedUsers, true, 0x2));
        AssertRefused(loose, Hidden(), "did not pass its checks");
    }

    [Fact]
    public void Without_a_copy_or_a_layout_nothing_hidden_is_trusted()
    {
        var noCopy = new AdminRig(helperInstalled: true, copyInstalled: false);
        AssertRefused(noCopy, Hidden(), "there is no administrator copy");

        var noLayout = Started();
        noLayout.LayoutSource.Layout = null;
        Assert.False(Trust(noLayout).IsStartedCopy(Hidden()));
        Assert.Equal(PortOwnerTrust.Other, Verifier(noLayout).Classify(Hidden()));
    }

    // ---- refused: readable paths, and other processes -----------------------------------------------------------------

    [Fact]
    public void A_readable_path_is_never_matched_by_the_records()
    {
        var rig = Started();

        Assert.False(Trust(rig).IsStartedCopy(Visible(@"C:\Users\someone\Downloads\defenseclaw-gateway.exe")));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Visible(@"C:\Users\someone\Downloads\defenseclaw-gateway.exe")));
    }

    [Fact]
    public void Another_accounts_copy_is_refused_and_called_that()
    {
        var rig = Started();
        var otherCopy = Path.Combine(rig.Layout.AdminGatewayRoot, OtherSid, AdminLayout.GatewayFileName);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Visible(otherCopy)));
        Assert.Equal(
            "defenseclaw-gateway (pid 11524) runs from another account's administrator copy, not this account's, so it is not sent this account's token",
            Verifier(rig).DescribeUntrusted(Visible(otherCopy)));
    }

    [Fact]
    public void A_path_in_the_admin_folder_that_is_not_this_accounts_copy_is_refused()
    {
        var rig = Started();
        var loose = Path.Combine(rig.Layout.AdminGatewayRoot, AdminLayout.GatewayFileName);

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Visible(loose)));
        Assert.Contains("not from this account's administrator copy", Verifier(rig).DescribeUntrusted(Visible(loose)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_outside_the_admin_folder_keeps_the_install_directory_sentence()
    {
        var rig = Started();

        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(Visible(@"C:\elsewhere\defenseclaw-gateway.exe")));
        Assert.Contains("is not running from the DefenseClaw install directory", Verifier(rig).DescribeUntrusted(Visible(@"C:\elsewhere\defenseclaw-gateway.exe")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hidden_process_with_another_name_is_not_matched_even_when_the_records_name_its_pid()
    {
        var rig = Started();

        Assert.False(Trust(rig).IsStartedCopy(new PortOwner(GatewayPid, "node", "127.0.0.1", 18970, null)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(new PortOwner(GatewayPid, "node", "127.0.0.1", 18970, null)));
    }

    private static void AssertRefused(AdminRig rig, PortOwner owner, string because)
    {
        Assert.False(Trust(rig).IsStartedCopy(owner));
        Assert.Equal(PortOwnerTrust.Other, Verifier(rig).Classify(owner));

        var words = Verifier(rig).DescribeUntrusted(owner);
        Assert.StartsWith($"defenseclaw-gateway (pid {owner.Pid}) could not be verified: Windows does not show this app which file it runs from", words, StringComparison.Ordinal);
        Assert.Contains("it is not the administrator gateway this app started: ", words, StringComparison.Ordinal);
        Assert.Contains(because, words, StringComparison.Ordinal);
    }

    // ---- the composition, the monitor and the elevation status ----------------------------------------------------------

    private const string Bin = @"C:\fake\install\bin";

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
        Assert.Equal(PortOwnerTrust.Gateway, services.PeerVerifier.Classify(Visible(rig.Layout.GatewayCopyPath)));
        Assert.Equal(PortOwnerTrust.Gateway, services.PeerVerifier.Classify(Hidden()));
    }

    [Fact]
    public async Task The_monitor_shows_the_hidden_administrator_gateway_as_running_and_polls_it()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(request =>
            request.Path == "/health"
                ? RawHttpListener.Response(200, "{\"provenance\":{\"binary_version\":\"1.0.4\"}}")
                : RawHttpListener.Response(401, "{\"error\":\"unauthorized\"}"));
        var rig = Started();
        using var services = Compose(temp, rig, listener.Port);
        var inspector = new FixedPortInspector { Owner = Hidden() with { Port = listener.Port } };
        var detector = new InstallStateDetector(services.Paths, services.Gateway, inspector, services.PeerVerifier);
        using var monitor = new GatewayMonitor(services, new ManualClock(), detector);

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.Running, snapshot.State);
        Assert.False(snapshot.PeerUnverified);
        Assert.Equal(GatewayPeerRoute.AdministratorCopyByRecord, snapshot.PeerRoute);
        Assert.StartsWith("The administrator gateway started by this app is responding", snapshot.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("install directory", snapshot.Detail, StringComparison.Ordinal);
        Assert.Equal(1, monitor.AlertsFetchCount);
    }

    [Fact]
    public async Task The_monitor_still_calls_an_unmatched_hidden_gateway_unverified_and_sends_it_nothing()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(200, "{\"provenance\":{\"binary_version\":\"1.0.4\"}}"));
        var rig = Started();
        rig.Fs.Items.Remove(rig.Layout.ResultPath);
        using var services = Compose(temp, rig, listener.Port);
        var inspector = new FixedPortInspector { Owner = Hidden() with { Port = listener.Port } };
        var detector = new InstallStateDetector(services.Paths, services.Gateway, inspector, services.PeerVerifier);
        using var monitor = new GatewayMonitor(services, new ManualClock(), detector);

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.Degraded, snapshot.State);
        Assert.True(snapshot.PeerUnverified);
        Assert.Equal(GatewayPeerRoute.None, snapshot.PeerRoute);
        Assert.Contains("could not be verified", snapshot.Detail, StringComparison.Ordinal);
        Assert.Contains("no record of starting a gateway", snapshot.AlertsUnavailable, StringComparison.Ordinal);
        Assert.Equal(0, monitor.AlertsFetchCount);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task The_runtime_cards_elevation_follows_a_record_match()
    {
        var rig = Started();
        rig.ModeOn = true;
        IGatewayElevationStatus status = rig.Service;
        rig.Elevation.Answer = null; // Windows would not give the token either.

        rig.Snapshot = new GatewaySnapshot
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            PortOwner = Hidden(),
            PeerRoute = GatewayPeerRoute.AdministratorCopyByRecord,
        };
        _ = await rig.Service.RefreshAsync();
        Assert.True(rig.Service.Status.GatewayRunningElevated);
        Assert.Equal(GatewayElevation.Elevated, status.Current);

        // The same hidden process, not matched: nothing is claimed about it.
        rig.Snapshot = rig.Snapshot with { PeerRoute = GatewayPeerRoute.None };
        _ = await rig.Service.RefreshAsync();
        Assert.Null(rig.Service.Status.GatewayRunningElevated);
        Assert.Equal(GatewayElevation.Unknown, status.Current);
    }
}
