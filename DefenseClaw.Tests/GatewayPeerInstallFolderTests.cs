using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-392, finding 2. The installer's directories in this account's profile are this account's alone, so a gateway there keeps the old rule in
/// any session. One outside the profile (<c>&lt;DEFENSECLAW_HOME&gt;\.venv\Scripts</c> on another drive, where new folders give Authenticated
/// Users Modify) counts only for a process in this app's session, or when <see cref="IInstallFolderTrust"/> finds the file and its folders closed
/// to other accounts. The folder trust is a fake here; the app's own one is tested over an in-memory disk in the app suite.
/// </summary>
public class GatewayPeerInstallFolderTests
{
    private const string Profile = @"C:\Users\operator";
    private const string SetupBin = Profile + @"\AppData\Local\Programs\DefenseClaw\bin";
    private const string ScriptBin = Profile + @"\.local\bin";
    private const string ProfileVenv = Profile + @"\.defenseclaw\.venv\Scripts";
    private const string OutsideVenv = @"D:\dc\.venv\Scripts";
    private const string OutsideGateway = OutsideVenv + @"\defenseclaw-gateway.exe";
    private const int AppSession = 2;
    private const int OtherSession = 0;

    private sealed class Folders : IInstallFolderTrust
    {
        public Func<string, string?> Answer { get; set; } = _ => "D:\\dc lets S-1-5-11 write to it, change it or delete it";

        public List<string> Asked { get; } = new();

        public string? Problem(string imagePath)
        {
            Asked.Add(imagePath);
            return Answer(imagePath);
        }
    }

    private sealed class NoListener : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    private static DefenseClawPaths Paths() =>
        new(
            dataDirectory: @"D:\dc",
            binDirectory: SetupBin,
            searchPath: Array.Empty<string>(),
            fileExists: _ => false,
            fallbackBinDirectories: [ScriptBin, ProfileVenv, OutsideVenv, @"C:\Users\operator2\.local\bin"]);

    private static GatewayPeerVerifier Verifier(IInstallFolderTrust? folders) =>
        new(Paths(), new NoListener(), currentSessionId: AppSession, installFolders: folders, userProfile: Profile);

    private static PortOwner Gateway(string image, int? session, bool withoutHandle = false) =>
        new(4242, GatewayPeerVerifier.GatewayProcessName, "127.0.0.1", 18970, image, session, withoutHandle,
            SocketCreatedUtc: PeerFixtures.Bound, ProcessCreatedUtc: PeerFixtures.Started);

    [Theory]
    [InlineData(SetupBin)]
    [InlineData(ScriptBin)]
    [InlineData(ProfileVenv)]
    public void The_profiles_install_folders_keep_the_old_rule_in_any_session_without_a_folder_check(string bin)
    {
        // The common case: ~\.local\bin, %LOCALAPPDATA%\Programs\DefenseClaw\bin and <profile>\.defenseclaw\.venv. A gateway started as a task
        // (another session, opened with a handle) is still this account's.
        var folders = new Folders();
        var verifier = Verifier(folders);
        var image = bin + @"\defenseclaw-gateway.exe";

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.InstallDirectory), verifier.Judge(Gateway(image, OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Gateway(image, session: null)));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Gateway(image, AppSession)));
        Assert.Empty(folders.Asked);
    }

    [Fact]
    public void An_install_folder_outside_the_profile_counts_in_this_apps_session_without_a_folder_check()
    {
        var folders = new Folders();

        Assert.Equal(PortOwnerTrust.Gateway, Verifier(folders).Classify(Gateway(OutsideGateway, AppSession)));
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(folders).Classify(Gateway(OutsideGateway, AppSession, withoutHandle: true)));
        Assert.Empty(folders.Asked);
    }

    [Fact]
    public void Outside_the_profile_and_this_session_a_folder_other_accounts_may_change_is_refused_and_says_so()
    {
        var folders = new Folders();
        var verifier = Verifier(folders);
        var owner = Gateway(OutsideGateway, OtherSession);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(owner));
        Assert.Equal(new[] { OutsideGateway }, folders.Asked);
        Assert.Equal(
            @"defenseclaw-gateway (pid 4242) runs from D:\dc\.venv\Scripts, outside this account's profile, in Windows session 0, not this app's session 2, " +
            @"and D:\dc lets S-1-5-11 write to it, change it or delete it",
            verifier.DescribeUntrusted(owner));
    }

    [Fact]
    public void Outside_the_profile_and_this_session_a_folder_closed_to_other_accounts_is_trusted()
    {
        var folders = new Folders { Answer = _ => null };

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.InstallDirectory), Verifier(folders).Judge(Gateway(OutsideGateway, OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(folders).Classify(Gateway(OutsideGateway, session: null)));
    }

    [Fact]
    public void Without_a_folder_check_an_install_folder_outside_the_profile_needs_this_session()
    {
        var verifier = Verifier(null);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, OtherSession)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, session: null)));
        Assert.EndsWith("and this app does not check who may change that folder", verifier.DescribeUntrusted(Gateway(OutsideGateway, OtherSession)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_check_that_throws_refuses()
    {
        var folders = new Folders { Answer = _ => throw new UnauthorizedAccessException("denied") };

        Assert.Equal(PortOwnerTrust.Other, Verifier(folders).Classify(Gateway(OutsideGateway, OtherSession)));
        Assert.EndsWith("and who may change that folder could not be read", Verifier(folders).DescribeUntrusted(Gateway(OutsideGateway, OtherSession)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_read_without_a_handle_in_another_session_stays_refused_however_closed_its_folder_is()
    {
        var folders = new Folders { Answer = _ => null };

        Assert.Equal(PortOwnerTrust.Other, Verifier(folders).Classify(Gateway(OutsideGateway, OtherSession, withoutHandle: true)));
    }

    [Fact]
    public void A_folder_beside_the_profile_that_shares_its_name_prefix_is_outside_it()
    {
        var image = @"C:\Users\operator2\.local\bin\defenseclaw-gateway.exe";

        Assert.Equal(PortOwnerTrust.Other, Verifier(null).Classify(Gateway(image, OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(new Folders { Answer = _ => null }).Classify(Gateway(image, OtherSession)));
    }

    [Fact]
    public void A_profile_that_cannot_be_read_is_no_profile()
    {
        var verifier = new GatewayPeerVerifier(Paths(), new NoListener(), currentSessionId: AppSession, userProfile: string.Empty);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(ScriptBin + @"\defenseclaw-gateway.exe", OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Gateway(ScriptBin + @"\defenseclaw-gateway.exe", AppSession)));
    }
}
