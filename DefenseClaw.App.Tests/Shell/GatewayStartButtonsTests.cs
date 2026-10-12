using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The "Start gateway" button on the dashboard's degraded banner, the Overview's attention row and the tray flyout: when each is drawn and pressable,
/// that all three (and nothing else) go through <see cref="GatewayLifecycle"/>, that the argv is exactly <c>defenseclaw-gateway start</c>, and that
/// nothing runs until the review is confirmed. The runner, the review and the poll are fakes; no process starts.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GatewayStartButtonsTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false);
    private readonly List<string[]> _runs = new();
    private readonly List<string> _events = new();
    private int _refreshes;

    /// <summary>What the next review answers.</summary>
    private bool _confirm = true;

    /// <summary>The gateway state the lifecycle's gate judges (the monitor's current snapshot, standing in).</summary>
    private GatewaySnapshot _current = Snapshot(AppGatewayState.GatewayStopped);

    public GatewayStartButtonsTests()
    {
        _scene.Services.GatewayLifecycle = new GatewayLifecycle(
            () => _current,
            _scene.Services.Installation,
            argv =>
            {
                _events.Add("run");
                _runs.Add(argv.ToArray());
                var invocation = InvocationFactory.CreateFor(GatewayControl.Executable, argv.ToArray());
                InvocationFactory.Finish(invocation, 0);
                return Task.FromResult(invocation);
            },
            () =>
            {
                _refreshes++;
                return Task.CompletedTask;
            },
            () => { })
        {
            Confirmer = (_, _) =>
            {
                _events.Add("review");
                return _confirm;
            },
        };
    }

    public void Dispose() => _scene.Dispose();

    private AppServices Services => _scene.Services;

    private static GatewaySnapshot Snapshot(AppGatewayState state, bool wslFlag = false, InstallState? install = InstallState.GatewayStopped) => new()
    {
        State = state,
        Install = install,
        Detail = "synthetic",
        WslGatewayDetected = wslFlag,
        ApiPort = 18970,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static Task Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        return ((IAsyncRelayCommand)command).ExecuteAsync(null);
    }

    // ------------------------------------------------------------------ the shared offer

    public static IEnumerable<object[]> States() => new[]
    {
        // state, wsl flag, install, visible, enabled
        new object[] { AppGatewayState.Running, false, InstallState.Running, false, false },
        new object[] { AppGatewayState.GatewayStopped, false, InstallState.GatewayStopped, true, true },
        new object[] { AppGatewayState.Degraded, false, InstallState.Running, true, true },
        new object[] { AppGatewayState.Unknown, false, null!, true, false },
        new object[] { AppGatewayState.WslGatewayDetected, true, InstallState.Running, false, false },
        new object[] { AppGatewayState.GatewayStopped, true, InstallState.GatewayStopped, false, false },
        new object[] { AppGatewayState.NotInstalled, false, InstallState.NotInstalled, false, false },
        new object[] { AppGatewayState.NotInitialized, false, InstallState.InstalledNotInitialized, false, false },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void The_offer_is_drawn_only_while_the_gateway_is_down_and_never_for_wsl_or_a_missing_install(
        AppGatewayState state, bool wslFlag, InstallState? install, bool visible, bool enabled)
    {
        var offer = GatewayLifecycle.StartOffer(Snapshot(state, wslFlag, install), Services.Installation);

        Assert.Equal(visible, offer.Visible);
        Assert.Equal(enabled, offer.Enabled);
    }

    [Fact]
    public void A_stopped_gateway_offers_a_start_that_says_it_asks_first_and_unknown_says_it_is_still_checking()
    {
        var stopped = GatewayLifecycle.StartOffer(Snapshot(AppGatewayState.GatewayStopped), Services.Installation);
        var unknown = GatewayLifecycle.StartOffer(Snapshot(AppGatewayState.Unknown, install: null), Services.Installation);

        Assert.EndsWith("Asks for confirmation first.", stopped.Tip, StringComparison.Ordinal);
        Assert.Contains("Still checking", unknown.Tip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("managed layout")]
    [InlineData("invalid selection")]
    public void A_read_only_installation_shows_the_button_off_with_its_sentence_before_any_other_reason(string which)
    {
        var context = which == "managed layout" ? TestInstallations.Managed() : TestInstallations.Invalid();
        Services.Installation.Replace(context);

        var offer = GatewayLifecycle.StartOffer(Snapshot(AppGatewayState.GatewayStopped), Services.Installation);

        Assert.True(offer.Visible);
        Assert.False(offer.Enabled);
        Assert.Equal(context.BlockedReason, offer.Tip);
    }

    // ------------------------------------------------------------------ the three surfaces

    [Fact]
    public async Task The_banner_button_reviews_then_runs_exactly_defenseclaw_gateway_start_then_polls()
    {
        var vm = UiThread.Run(() => new MainWindowViewModel(Services));
        UiThread.Run(() => vm.Apply(_current));

        Assert.True(vm.ShowDegradedBanner);
        Assert.True(vm.ShowStartGateway);
        Assert.True(UiThread.Run(() => vm.StartGatewayCommand.CanExecute(null)));
        Assert.Empty(_runs);

        await UiThread.Run(() => Execute(vm.StartGatewayCommand));

        AssertRanOnceAfterReview();
        UiThread.Run(vm.Dispose);
    }

    [Fact]
    public async Task The_attention_row_button_reviews_then_runs_exactly_defenseclaw_gateway_start_then_polls()
    {
        var vm = UiThread.Run(() => new OverviewPanelViewModel(Services));
        UiThread.Run(() => vm.Apply(_current));

        var row = Assert.Single(vm.Attention, r => r.OffersStart);
        Assert.Equal("The gateway is not answering", row.Title);
        Assert.Equal("defenseclaw-gateway start", row.Command);
        Assert.True(UiThread.Run(() => vm.StartGatewayFromRowCommand.CanExecute(null)));
        Assert.Empty(_runs);

        await UiThread.Run(() => Execute(vm.StartGatewayFromRowCommand));

        AssertRanOnceAfterReview();
    }

    [Fact]
    public async Task The_flyout_button_closes_the_flyout_then_reviews_then_runs_exactly_defenseclaw_gateway_start()
    {
        var vm = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));
        var closed = 0;
        UiThread.Run(() =>
        {
            vm.CloseRequested += (_, _) =>
            {
                closed++;
                _events.Add("close");
            };
            vm.Apply(_current);
        });

        Assert.True(vm.ShowStartGateway);
        Assert.True(UiThread.Run(() => vm.StartGatewayCommand.CanExecute(null)));

        await UiThread.Run(() => Execute(vm.StartGatewayCommand));

        Assert.Equal(1, closed);
        Assert.Equal(new[] { "close", "review", "run" }, _events);
        AssertRanOnceAfterReview();
        UiThread.Run(vm.Dispose);
    }

    private void AssertRanOnceAfterReview()
    {
        Assert.Equal(new[] { "start" }, Assert.Single(_runs));
        Assert.Equal("defenseclaw-gateway", GatewayControl.Executable);
        Assert.Equal("defenseclaw-gateway start", GatewayControl.CommandText(GatewayAction.Start));
        Assert.True(_events.IndexOf("review") < _events.IndexOf("run"), "the review comes before the run");
        Assert.Equal(1, _refreshes);
    }

    [Fact]
    public async Task Declining_the_review_runs_nothing_on_any_surface()
    {
        _confirm = false;
        var banner = UiThread.Run(() => new MainWindowViewModel(Services));
        var overview = UiThread.Run(() => new OverviewPanelViewModel(Services));
        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));
        UiThread.Run(() =>
        {
            banner.Apply(_current);
            overview.Apply(_current);
            flyout.Apply(_current);
        });

        await UiThread.Run(() => Execute(banner.StartGatewayCommand));
        await UiThread.Run(() => Execute(overview.StartGatewayFromRowCommand));
        await UiThread.Run(() => Execute(flyout.StartGatewayCommand));

        Assert.Empty(_runs);
        Assert.Equal(0, _refreshes);
        Assert.Equal(3, _events.Count(e => e == "review"));
        UiThread.Run(banner.Dispose);
        UiThread.Run(flyout.Dispose);
    }

    [Fact]
    public async Task A_read_only_installation_turns_all_three_buttons_off_with_the_same_reason_and_nothing_runs()
    {
        var context = TestInstallations.Managed();
        Services.Installation.Replace(context);
        var banner = UiThread.Run(() => new MainWindowViewModel(Services));
        var overview = UiThread.Run(() => new OverviewPanelViewModel(Services));
        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));
        UiThread.Run(() =>
        {
            banner.Apply(_current);
            overview.Apply(_current);
            flyout.Apply(_current);
        });

        Assert.Equal(context.BlockedReason, banner.StartGatewayTip);
        Assert.Equal(context.BlockedReason, overview.StartGatewayTip);
        Assert.Equal(context.BlockedReason, flyout.StartGatewayTip);
        Assert.False(UiThread.Run(() => banner.StartGatewayCommand.CanExecute(null)));
        Assert.False(UiThread.Run(() => overview.StartGatewayFromRowCommand.CanExecute(null)));
        Assert.False(UiThread.Run(() => flyout.StartGatewayCommand.CanExecute(null)));

        // Even a direct request is refused at the gate, before any review.
        await Services.GatewayLifecycle.RunAsync(GatewayAction.Start);

        Assert.Empty(_runs);
        Assert.DoesNotContain("review", _events);
        UiThread.Run(banner.Dispose);
        UiThread.Run(flyout.Dispose);
    }

    [Theory]
    [InlineData(AppGatewayState.Running)]
    [InlineData(AppGatewayState.WslGatewayDetected)]
    public void A_running_or_wsl_gateway_draws_no_button_on_any_surface(AppGatewayState state)
    {
        var snapshot = Snapshot(state, wslFlag: state == AppGatewayState.WslGatewayDetected, install: InstallState.Running);
        var banner = UiThread.Run(() => new MainWindowViewModel(Services));
        var overview = UiThread.Run(() => new OverviewPanelViewModel(Services));
        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));

        UiThread.Run(() =>
        {
            banner.Apply(snapshot);
            overview.Apply(snapshot);
            flyout.Apply(snapshot);
        });

        Assert.False(banner.ShowStartGateway);
        Assert.False(flyout.ShowStartGateway);
        Assert.DoesNotContain(overview.Attention, r => r.OffersStart);
        UiThread.Run(banner.Dispose);
        UiThread.Run(flyout.Dispose);
    }

    [Fact]
    public void A_degraded_gateway_gets_the_banner_and_flyout_button_but_not_the_attention_row_button()
    {
        var snapshot = Snapshot(AppGatewayState.Degraded, install: InstallState.Running);
        var banner = UiThread.Run(() => new MainWindowViewModel(Services));
        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));

        UiThread.Run(() =>
        {
            banner.Apply(snapshot);
            flyout.Apply(snapshot);
        });

        Assert.True(banner.ShowStartGateway);
        Assert.True(flyout.ShowStartGateway);
        UiThread.Run(banner.Dispose);
        UiThread.Run(flyout.Dispose);
    }

    [Fact]
    public async Task A_second_request_while_a_start_is_running_is_ignored()
    {
        var gate = new TaskCompletionSource();
        var lifecycle = new GatewayLifecycle(
            () => _current,
            Services.Installation,
            async argv =>
            {
                _runs.Add(argv.ToArray());
                await gate.Task;
                var invocation = InvocationFactory.CreateFor(GatewayControl.Executable, argv.ToArray());
                InvocationFactory.Finish(invocation, 0);
                return invocation;
            },
            () => Task.CompletedTask,
            () => { })
        {
            Confirmer = (_, _) => true,
        };

        var first = lifecycle.RunAsync(GatewayAction.Start);
        await lifecycle.RunAsync(GatewayAction.Start);
        Assert.True(lifecycle.IsRunning);

        gate.SetResult();
        await first;

        Assert.Single(_runs);
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public async Task A_failed_start_is_reported_as_a_warning_notice_and_still_polls()
    {
        var notices = new List<GatewayLifecycleNotice>();
        var lifecycle = new GatewayLifecycle(
            () => _current,
            Services.Installation,
            argv =>
            {
                var invocation = InvocationFactory.CreateFor(GatewayControl.Executable, argv.ToArray());
                InvocationFactory.Finish(invocation, 1);
                return Task.FromResult(invocation);
            },
            () =>
            {
                _refreshes++;
                return Task.CompletedTask;
            },
            () => { })
        {
            Confirmer = (_, _) => true,
        };
        lifecycle.Notice += (_, notice) => notices.Add(notice);

        await lifecycle.RunAsync(GatewayAction.Start);

        var notice = Assert.Single(notices);
        Assert.True(notice.Level == ToastLevel.Warning, notice.Message);
        Assert.Contains("Exit code 1", notice.Message, StringComparison.Ordinal);
        Assert.Equal(1, _refreshes);
    }

    [Fact]
    public void The_review_shows_the_exact_command_with_the_start_label()
    {
        var review = UiThread.Run(() => GatewayActionDialog.ReviewFor(GatewayAction.Start));

        Assert.Equal("Start gateway", review.ConfirmLabel);
        Assert.Equal(new[] { "start" }, review.Steps.Single().Argv);
    }
}
