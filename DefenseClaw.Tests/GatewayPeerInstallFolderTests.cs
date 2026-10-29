using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-392, finding 2. The installer's directories in this account's profile are this account's alone, so a gateway there keeps the old rule in
/// any session. One outside the profile (<c>&lt;DEFENSECLAW_HOME&gt;\.venv\Scripts</c> on another drive, where new folders give Authenticated
/// Users Modify) needs both halves: the process in this app's session, and <see cref="IInstallFolderTrust"/> finding the file and its folders
/// closed to other accounts. Either alone is too weak: a closed folder still lets another account run the genuine file in its own session (the
/// administrator copy's case), and this session alone does not say who put the file in a loose folder. The folder trust is a fake here; the
/// app's own one is tested over an in-memory disk in the app suite.
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

    private static Folders Closed() => new() { Answer = _ => null };

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outside_the_profile_this_session_and_a_closed_folder_together_are_trusted(bool withoutHandle)
    {
        var folders = Closed();

        Assert.Equal(
            new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.InstallDirectory),
            Verifier(folders).Judge(Gateway(OutsideGateway, AppSession, withoutHandle)));
        Assert.Equal(new[] { OutsideGateway }, folders.Asked);
    }

    [Fact]
    public void Outside_the_profile_a_closed_folder_in_another_session_is_refused_and_says_so()
    {
        // Other accounts may read and run the genuine file from a folder they cannot write: their run of it, in their session, is not this
        // account's gateway, so the folder is not even asked about.
        var folders = Closed();
        var verifier = Verifier(folders);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, OtherSession)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, session: null)));
        Assert.Empty(folders.Asked);
        Assert.Equal(
            @"defenseclaw-gateway (pid 4242) runs from D:\dc\.venv\Scripts, outside this account's profile, in Windows session 0, not this app's session 2: " +
            "a gateway there counts only in this app's session",
            verifier.DescribeUntrusted(Gateway(OutsideGateway, OtherSession)));
    }

    [Fact]
    public void Outside_the_profile_a_folder_other_accounts_may_change_is_refused_in_this_session_too_and_says_so()
    {
        var folders = new Folders();
        var verifier = Verifier(folders);
        var owner = Gateway(OutsideGateway, AppSession);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(owner));
        Assert.Equal(
            @"defenseclaw-gateway (pid 4242) runs from D:\dc\.venv\Scripts, outside this account's profile, and D:\dc lets S-1-5-11 write to it, change it or delete it",
            verifier.DescribeUntrusted(owner));
    }

    [Fact]
    public void Without_a_folder_check_no_install_folder_outside_the_profile_counts()
    {
        var verifier = Verifier(null);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, AppSession)));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Gateway(OutsideGateway, OtherSession)));
        Assert.EndsWith("and this app does not check who may change that folder", verifier.DescribeUntrusted(Gateway(OutsideGateway, AppSession)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_check_that_throws_refuses()
    {
        var folders = new Folders { Answer = _ => throw new UnauthorizedAccessException("denied") };

        Assert.Equal(PortOwnerTrust.Other, Verifier(folders).Classify(Gateway(OutsideGateway, AppSession)));
        Assert.EndsWith("and who may change that folder could not be read", Verifier(folders).DescribeUntrusted(Gateway(OutsideGateway, AppSession)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_beside_the_profile_that_shares_its_name_prefix_is_outside_it()
    {
        var image = @"C:\Users\operator2\.local\bin\defenseclaw-gateway.exe";

        Assert.Equal(PortOwnerTrust.Other, Verifier(Closed()).Classify(Gateway(image, OtherSession)));
        Assert.Equal(PortOwnerTrust.Other, Verifier(null).Classify(Gateway(image, AppSession)));
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(Closed()).Classify(Gateway(image, AppSession)));
    }

    [Fact]
    public void A_profile_that_cannot_be_read_is_no_profile()
    {
        var bare = new GatewayPeerVerifier(Paths(), new NoListener(), currentSessionId: AppSession, userProfile: string.Empty);
        var checkedFolders = new GatewayPeerVerifier(Paths(), new NoListener(), currentSessionId: AppSession, installFolders: Closed(), userProfile: string.Empty);
        var image = ScriptBin + @"\defenseclaw-gateway.exe";

        Assert.Equal(PortOwnerTrust.Other, bare.Classify(Gateway(image, AppSession)));
        Assert.Equal(PortOwnerTrust.Other, checkedFolders.Classify(Gateway(image, OtherSession)));
        Assert.Equal(PortOwnerTrust.Gateway, checkedFolders.Classify(Gateway(image, AppSession)));
    }
}
