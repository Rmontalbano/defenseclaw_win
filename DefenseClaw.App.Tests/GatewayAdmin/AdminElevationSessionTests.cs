using DefenseClaw.App.Services;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// CUST-392, finding 4. The elevation label (the Runtime monitoring card's "the gateway runs as administrator", the status headline) used to call
/// any process running this account's administrator copy the helper's elevated gateway. Users may run that file, so the label now applies the
/// peer check's session rule too: another session's run of the copy is not the administrator gateway this app started, and nothing is claimed
/// about its elevation. This changes words only; the token decision is the verifier's.
/// </summary>
public sealed class AdminElevationSessionTests
{
    private const int OtherSession = AdminRig.AppSession + 4;

    private static GatewaySnapshot Running(string image, int? session) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        PortOwner = new PortOwner(4242, "defenseclaw-gateway", "127.0.0.1", 18970, image, session),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Another_sessions_run_of_the_copy_is_not_called_the_administrator_gateway(bool? token)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = token;
        rig.Snapshot = Running(rig.Layout.GatewayCopyPath, OtherSession);
        IGatewayElevationStatus elevation = rig.Service;

        var status = await rig.Service.RefreshAsync();

        Assert.Null(status.GatewayRunningElevated);
        Assert.Equal(GatewayElevation.Unknown, elevation.Current);
        Assert.DoesNotContain("running with administrator rights", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_of_the_copy_whose_session_is_unknown_is_not_called_it_either()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = true;
        rig.Snapshot = Running(rig.Layout.GatewayCopyPath, session: null);
        IGatewayElevationStatus elevation = rig.Service;

        var status = await rig.Service.RefreshAsync();

        Assert.Null(status.GatewayRunningElevated);
        Assert.NotEqual(GatewayElevation.Elevated, elevation.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task This_sessions_run_of_the_copy_is_still_the_administrator_gateway(bool? token)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = token;
        rig.Snapshot = Running(rig.Layout.GatewayCopyPath, AdminRig.AppSession);
        IGatewayElevationStatus elevation = rig.Service;

        var status = await rig.Service.RefreshAsync();

        Assert.True(status.GatewayRunningElevated);
        Assert.Equal(GatewayElevation.Elevated, elevation.Current);
        Assert.Equal("On. The gateway is running with administrator rights.", status.Headline);
    }

    [Fact]
    public async Task A_gateway_that_is_not_the_copy_keeps_its_tokens_answer_in_any_session()
    {
        // A gateway started as a task from the installer's folder: its token is read as before, and it is never the administrator copy.
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = false;
        rig.Snapshot = Running(@"C:\TestUsers\tester\.local\bin\defenseclaw-gateway.exe", OtherSession);
        IGatewayElevationStatus elevation = rig.Service;

        var status = await rig.Service.RefreshAsync();

        Assert.False(status.GatewayRunningElevated);
        Assert.Equal(GatewayElevation.NotElevated, elevation.Current);
    }
}
