using DefenseClaw.App.Services;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Shell;

public class GatewayControlTests
{
    private static GatewaySnapshot Snapshot(InstallState? install, AppGatewayState state) => new()
    {
        Install = install,
        State = state,
    };

    // ------------------------------------------------------------------ what each action is

    [Theory]
    [InlineData(GatewayAction.Start, "start")]
    [InlineData(GatewayAction.Stop, "stop")]
    [InlineData(GatewayAction.Restart, "restart")]
    public void Each_action_is_the_gateway_verb_with_no_other_arguments(GatewayAction action, string verb)
    {
        Assert.Equal(verb, GatewayControl.Verb(action));
        Assert.Equal(new[] { verb }, GatewayControl.Argv(action));
        Assert.Equal("defenseclaw-gateway " + verb, GatewayControl.CommandText(action));
        Assert.Equal("defenseclaw-gateway " + verb + " succeeded.", GatewayControl.SucceededText(action));
    }

    [Fact]
    public void The_gateway_executable_is_the_sidecar_binary_never_the_cli()
    {
        Assert.Equal("defenseclaw-gateway", GatewayControl.Executable);
    }

    [Theory]
    [InlineData(GatewayAction.Start, "Start gateway")]
    [InlineData(GatewayAction.Stop, "Stop gateway")]
    [InlineData(GatewayAction.Restart, "Restart gateway")]
    public void Titles_read_as_menu_items(GatewayAction action, string title)
    {
        Assert.Equal(title, GatewayControl.Title(action));
        Assert.False(string.IsNullOrWhiteSpace(GatewayControl.Summary(action)));
    }

    [Fact]
    public void Stop_and_restart_say_what_the_hooks_do_meanwhile_and_start_does_not_need_to()
    {
        Assert.Contains("configured fail mode", GatewayControl.ReviewNote(GatewayAction.Stop), StringComparison.Ordinal);
        Assert.Contains("configured fail mode", GatewayControl.ReviewNote(GatewayAction.Restart), StringComparison.Ordinal);
        Assert.Contains("restarts the DefenseClaw gateway", GatewayControl.ReviewNote(GatewayAction.Restart), StringComparison.Ordinal);
        Assert.DoesNotContain("fail mode", GatewayControl.ReviewNote(GatewayAction.Start), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ availability

    [Theory]
    [InlineData(GatewayAction.Start)]
    [InlineData(GatewayAction.Stop)]
    [InlineData(GatewayAction.Restart)]
    public void Before_the_first_poll_nothing_is_available(GatewayAction action)
    {
        var (allowed, reason) = GatewayControl.Availability(action, Snapshot(null, AppGatewayState.Unknown));

        Assert.False(allowed);
        Assert.Contains("Still checking", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GatewayAction.Start)]
    [InlineData(GatewayAction.Stop)]
    [InlineData(GatewayAction.Restart)]
    public void Without_an_install_nothing_is_available(GatewayAction action)
    {
        var (allowed, reason) = GatewayControl.Availability(action, Snapshot(InstallState.NotInstalled, AppGatewayState.NotInstalled));

        Assert.False(allowed);
        Assert.Contains("not installed", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GatewayAction.Start)]
    [InlineData(GatewayAction.Stop)]
    [InlineData(GatewayAction.Restart)]
    public void An_uninitialized_install_points_at_init_first(GatewayAction action)
    {
        var (allowed, reason) = GatewayControl.Availability(
            action, Snapshot(InstallState.InstalledNotInitialized, AppGatewayState.NotInitialized));

        Assert.False(allowed);
        Assert.Contains("defenseclaw init", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_gateway_can_be_stopped_or_restarted_but_not_started()
    {
        var running = Snapshot(InstallState.Running, AppGatewayState.Running);

        Assert.Equal((false, "The gateway is already running."), GatewayControl.Availability(GatewayAction.Start, running));
        Assert.Equal((true, (string?)null), GatewayControl.Availability(GatewayAction.Stop, running));
        Assert.Equal((true, (string?)null), GatewayControl.Availability(GatewayAction.Restart, running));
    }

    [Fact]
    public void A_stopped_gateway_can_be_started_or_restarted_but_not_stopped()
    {
        var stopped = Snapshot(InstallState.GatewayStopped, AppGatewayState.GatewayStopped);

        Assert.Equal((true, (string?)null), GatewayControl.Availability(GatewayAction.Start, stopped));
        Assert.Equal((false, "The gateway is not running."), GatewayControl.Availability(GatewayAction.Stop, stopped));
        Assert.Equal((true, (string?)null), GatewayControl.Availability(GatewayAction.Restart, stopped));
    }

    [Fact]
    public void A_gateway_answering_badly_or_owned_by_wsl_is_not_the_native_one_running()
    {
        foreach (var state in new[] { AppGatewayState.Degraded, AppGatewayState.WslGatewayDetected })
        {
            var snapshot = Snapshot(InstallState.Running, state);

            Assert.True(GatewayControl.Availability(GatewayAction.Start, snapshot).Allowed);
            Assert.False(GatewayControl.Availability(GatewayAction.Stop, snapshot).Allowed);
            Assert.True(GatewayControl.Availability(GatewayAction.Restart, snapshot).Allowed);
        }
    }

    [Fact]
    public void The_install_state_is_checked_before_the_running_state()
    {
        // A snapshot can still say Running for a moment after the install vanished; the install answer wins.
        var (allowed, reason) = GatewayControl.Availability(
            GatewayAction.Stop, Snapshot(InstallState.NotInstalled, AppGatewayState.Running));

        Assert.False(allowed);
        Assert.Contains("not installed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Availability_requires_a_snapshot()
    {
        Assert.Throws<ArgumentNullException>(() => GatewayControl.Availability(GatewayAction.Start, null!));
    }

    [Fact]
    public void The_disabled_reason_is_what_the_palette_and_the_tray_show_in_place_of_the_description()
    {
        // One source of truth: the palette row for an unavailable action carries exactly this text.
        var (allowed, reason) = GatewayControl.Availability(
            GatewayAction.Start, Snapshot(InstallState.Running, AppGatewayState.Running));

        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
