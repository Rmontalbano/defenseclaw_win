using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.GatewayAdmin.Contract;
using DefenseClaw.GatewayAdmin.Tests;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// CUST-392, finding 2, the app's half: <see cref="InstallFolderTrust"/> reads the owner and access list of a gateway installed outside the profile
/// and of every folder above it through <see cref="IAdminFileSystem"/>, and judges them with <see cref="AdminAcl"/> with this account trusted
/// beside the administrative principals. A <c>DEFENSECLAW_HOME</c> made on a drive's root inherits Authenticated Users Modify and is refused for
/// a gateway outside this app's session; the same folder locked to this account is trusted. Every disk is a <see cref="FakeFs"/>: nothing reads
/// a real folder.
/// </summary>
public sealed class InstallFolderTrustTests
{
    private const string Account = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherAccount = "S-1-5-21-9999999999-8888888888-7777777777-1002";
    private const string Root = @"D:\";
    private const string Home = @"D:\dc";
    private const string Venv = Home + @"\.venv";
    private const string Scripts = Venv + @"\Scripts";
    private const string Gateway = Scripts + @"\defenseclaw-gateway.exe";
    private const int AppSession = 1;
    private const int OtherSession = 0;

    /// <summary>Modify: read, write, append, attributes, execute and delete.</summary>
    private const uint Modify = 0x1301BF;

    private const uint FullControl = 0x1F01FF;

    /// <summary>A volume root as Windows lays it down: administrators own it, Users read, Authenticated Users may add folders and pass Modify down to them.</summary>
    private static List<AclEntry> RootAcl() => new()
    {
        new AclEntry(AdminAcl.System, true, FullControl),
        new AclEntry(AdminAcl.Administrators, true, FullControl),
        new AclEntry(AdminAcl.Users, true, AdminAcl.ReadExecuteMask),
        new AclEntry(AdminAcl.AuthenticatedUsers, true, 0x4),
        new AclEntry(AdminAcl.AuthenticatedUsers, true, Modify, InheritOnly: true),
    };

    /// <summary>What a folder made under that root inherits: Authenticated Users may change it.</summary>
    private static List<AclEntry> InheritedAcl() => new()
    {
        new AclEntry(AdminAcl.System, true, FullControl),
        new AclEntry(AdminAcl.Administrators, true, FullControl),
        new AclEntry(AdminAcl.Users, true, AdminAcl.ReadExecuteMask),
        new AclEntry(AdminAcl.AuthenticatedUsers, true, Modify),
    };

    /// <summary>The same folder locked to this account: it, SYSTEM and Administrators, and Users may read and run.</summary>
    private static List<AclEntry> AccountAcl() => new()
    {
        new AclEntry(Account, true, FullControl),
        new AclEntry(AdminAcl.System, true, FullControl),
        new AclEntry(AdminAcl.Administrators, true, FullControl),
        new AclEntry(AdminAcl.Users, true, AdminAcl.ReadExecuteMask),
    };

    private static FakeFs Disk(Func<List<AclEntry>> acl)
    {
        var fs = new FakeFs();
        fs.AddDir(Root, AdminAcl.TrustedInstaller, RootAcl());
        fs.AddDir(Home, Account, acl());
        fs.AddDir(Venv, Account, acl());
        fs.AddDir(Scripts, Account, acl());
        fs.AddFile(Gateway, new byte[] { 0x4D, 0x5A }, Account, acl());
        return fs;
    }

    private static InstallFolderTrust Trust(FakeFs fs, string? account = Account) => new(fs, () => account);

    private static GatewayPeerVerifier Verifier(FakeFs fs) =>
        new(
            new DefenseClawPaths(
                dataDirectory: Home,
                binDirectory: @"C:\Users\operator\AppData\Local\Programs\DefenseClaw\bin",
                searchPath: Array.Empty<string>(),
                fileExists: _ => false,
                fallbackBinDirectories: [Scripts]),
            new NoListener(),
            currentSessionId: AppSession,
            installFolders: Trust(fs),
            userProfile: @"C:\Users\operator");

    private sealed class NoListener : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    private static readonly DateTime Started = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private static PortOwner Owner(int session) =>
        new(4242, GatewayPeerVerifier.GatewayProcessName, "127.0.0.1", 18970, Gateway, session, ImageWithoutHandle: false,
            SocketCreatedUtc: Started.AddSeconds(1), ProcessCreatedUtc: Started);

    // ---- the brief's two cases, through the verifier ----------------------------------------------------------------------

    [Fact]
    public void A_home_outside_the_profile_with_the_inherited_access_list_is_refused_to_another_session()
    {
        var fs = Disk(InheritedAcl);
        var verifier = Verifier(fs);

        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(OtherSession)));
        Assert.Equal(
            @"defenseclaw-gateway (pid 4242) runs from D:\dc\.venv\Scripts, outside this account's profile, in Windows session 0, not this app's session 1, " +
            @"and D:\dc\.venv\Scripts\defenseclaw-gateway.exe lets S-1-5-11 write to it, change it or delete it",
            verifier.DescribeUntrusted(Owner(OtherSession)));
    }

    [Fact]
    public void The_same_home_locked_to_this_account_is_trusted_in_another_session()
    {
        var verifier = Verifier(Disk(AccountAcl));

        Assert.Equal(new PeerVerdict(PortOwnerTrust.Gateway, GatewayPeerRoute.InstallDirectory), verifier.Judge(Owner(OtherSession)));
    }

    [Fact]
    public void In_this_apps_session_the_inherited_access_list_does_not_matter()
    {
        Assert.Equal(PortOwnerTrust.Gateway, Verifier(Disk(InheritedAcl)).Classify(Owner(AppSession)));
    }

    // ---- the policy, node by node -----------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_chain_under_an_ordinary_root_has_no_problem()
    {
        Assert.Null(Trust(Disk(AccountAcl)).Problem(Gateway));
    }

    [Fact]
    public void Administrators_may_own_and_write_every_node()
    {
        var fs = Disk(FakeFs.AdminOnly);
        foreach (var item in fs.Items.Values.Where(static i => i.Owner == Account))
        {
            item.Owner = AdminAcl.Administrators;
        }

        Assert.Null(Trust(fs).Problem(Gateway));
    }

    [Fact]
    public void A_loose_folder_above_a_locked_file_is_found()
    {
        var fs = Disk(AccountAcl);
        fs.Items[Home].Acl = InheritedAcl();

        Assert.Equal(@"D:\dc lets S-1-5-11 write to it, change it or delete it", Trust(fs).Problem(Gateway));
    }

    [Fact]
    public void A_root_that_lets_others_delete_what_it_holds_is_found()
    {
        var fs = Disk(AccountAcl);
        fs.Items[Root].Acl.Add(new AclEntry(AdminAcl.AuthenticatedUsers, true, 0x40));

        Assert.Equal(@"D:\ lets S-1-5-11 write to it, change it or delete it", Trust(fs).Problem(Gateway));
    }

    [Fact]
    public void Another_account_as_owner_or_writer_is_found()
    {
        var owned = Disk(AccountAcl);
        owned.Items[Gateway].Owner = OtherAccount;
        Assert.Equal($@"{Gateway} is owned by {OtherAccount}, not this account or an administrator", Trust(owned).Problem(Gateway));

        var written = Disk(AccountAcl);
        written.Items[Scripts].Acl.Add(new AclEntry(OtherAccount, true, 0x2));
        Assert.Equal($@"{Scripts} lets {OtherAccount} write to it, change it or delete it", Trust(written).Problem(Gateway));
    }

    [Fact]
    public void A_deny_entry_or_an_inherit_only_entry_is_not_a_write_right()
    {
        var fs = Disk(AccountAcl);
        fs.Items[Scripts].Acl.Add(new AclEntry(AdminAcl.AuthenticatedUsers, false, Modify));
        fs.Items[Scripts].Acl.Add(new AclEntry(AdminAcl.AuthenticatedUsers, true, Modify, InheritOnly: true));

        Assert.Null(Trust(fs).Problem(Gateway));
    }

    [Fact]
    public void A_link_a_missing_node_an_unreadable_list_or_no_list_is_a_problem()
    {
        var link = Disk(AccountAcl);
        link.Items[Venv].IsReparse = true;
        Assert.Equal(@"D:\dc\.venv is a link or junction, which is never followed", Trust(link).Problem(Gateway));

        var missing = Disk(AccountAcl);
        missing.Items.Remove(Gateway);
        Assert.Equal($"{Gateway} is not there", Trust(missing).Problem(Gateway));

        var unreadable = Disk(AccountAcl);
        unreadable.Items[Home].SecurityUnreadable = true;
        Assert.Equal(@"the access list of D:\dc could not be read", Trust(unreadable).Problem(Gateway));

        var noDacl = Disk(AccountAcl);
        noDacl.Items[Gateway].HasDacl = false;
        Assert.StartsWith($"{Gateway} has no access list", Trust(noDacl).Problem(Gateway), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_where_the_file_should_be_is_a_problem()
    {
        var fs = Disk(AccountAcl);
        fs.Items.Remove(Gateway);
        fs.AddDir(Gateway, Account, AccountAcl());

        Assert.Equal($"{Gateway} is a folder, not a file", Trust(fs).Problem(Gateway));
    }

    [Fact]
    public void An_unknown_account_or_a_network_path_is_a_problem()
    {
        Assert.StartsWith("this account could not be identified", Trust(Disk(AccountAcl), account: null).Problem(Gateway), StringComparison.Ordinal);
        Assert.Contains("network or device path", Trust(Disk(AccountAcl)).Problem(@"\\server\share\dc\.venv\Scripts\defenseclaw-gateway.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_disk_that_throws_is_a_problem()
    {
        Assert.Equal("who may change that folder could not be read", new InstallFolderTrust(new ThrowingFs(), () => Account).Problem(Gateway));
    }

    private sealed class ThrowingFs : IAdminFileSystem
    {
        public FsNode Stat(string path) => throw new IOException("the disk went away");

        public Stream OpenReadExclusive(string path) => throw new NotSupportedException();

        public void EnsureAdminFolder(string path) => throw new NotSupportedException();

        public Stream CreateTemp(string folder, out string tempPath) => throw new NotSupportedException();

        public void Replace(string tempPath, string finalPath) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void WriteText(string path, string text) => throw new NotSupportedException();

        public string? ReadText(string path, int maxBytes) => null;
    }
}
