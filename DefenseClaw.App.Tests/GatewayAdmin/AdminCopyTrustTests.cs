using DefenseClaw.App.Services;
using DefenseClaw.GatewayAdmin.Contract;
using DefenseClaw.GatewayAdmin.Tests;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// The app's answer to "may the gateway token go to the process whose image is this path": only for the administrator copy's exact path, only
/// while it is in an admin-only chain with no link, the file the helper recorded, and unchanged since; anything else, or anything unreadable, is no.
/// </summary>
public sealed class AdminCopyTrustTests
{
    private static AdminCopyTrust Trust(AdminRig rig) => new(rig.LayoutSource, rig.Fs);

    [Fact]
    public void The_installed_recorded_admin_only_copy_is_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);

        Assert.True(Trust(rig).IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Theory]
    [InlineData(@"C:\TestUsers\tester\.local\bin\defenseclaw-gateway.exe")]
    [InlineData(@"C:\TestPF\DefenseClaw Companion\AdminGateway\defenseclaw-gateway.exe")]
    [InlineData(@"C:\TestPF\DefenseClaw Companion\AdminGateway\S-1-5-21-9-9-9-9\defenseclaw-gateway.exe")]
    [InlineData("")]
    public void Any_other_path_is_not_the_copy(string image)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);

        Assert.False(Trust(rig).IsTrustedImage(image));
    }

    [Fact]
    public void The_case_and_dots_of_the_path_do_not_matter()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);

        Assert.True(Trust(rig).IsTrustedImage(rig.Layout.UserDirectory.ToUpperInvariant() + @"\.\DEFENSECLAW-GATEWAY.EXE"));
    }

    [Fact]
    public void A_copy_whose_bytes_changed_is_no_longer_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        var trust = Trust(rig);
        Assert.True(trust.IsTrustedImage(rig.Layout.GatewayCopyPath));

        rig.Fs.Items[rig.Layout.GatewayCopyPath].Data = AdminRig.Gateway.Reverse().ToArray();
        rig.Fs.Items[rig.Layout.GatewayCopyPath].LastWrite = DateTime.UtcNow;

        Assert.False(trust.IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Fact]
    public void A_folder_that_stopped_being_admin_only_is_noticed_on_the_next_request()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        var trust = Trust(rig);
        Assert.True(trust.IsTrustedImage(rig.Layout.GatewayCopyPath));

        rig.Fs.Items[rig.Layout.AdminGatewayRoot].Acl.Add(new AclEntry(AdminAcl.Users, true, 0x2));

        Assert.False(trust.IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Fact]
    public void A_link_in_the_chain_is_not_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        rig.Fs.Items[rig.Layout.UserDirectory].IsReparse = true;

        Assert.False(Trust(rig).IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Fact]
    public void A_copy_without_the_helpers_record_is_not_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        rig.Fs.Items.Remove(rig.Layout.CopyRecordPath);

        Assert.False(Trust(rig).IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Fact]
    public void Without_a_layout_nothing_is_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        rig.LayoutSource.Layout = null;

        Assert.False(Trust(rig).IsTrustedImage(rig.Layout.GatewayCopyPath));
    }

    [Fact]
    public void An_unreadable_access_list_is_not_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].SecurityUnreadable = true;

        Assert.False(Trust(rig).IsTrustedImage(rig.Layout.GatewayCopyPath));
    }
}
