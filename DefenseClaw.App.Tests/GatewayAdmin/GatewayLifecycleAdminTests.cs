using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// The shared <see cref="GatewayLifecycle"/> with administrator mode: the review of what runs elevated comes before any prompt, nothing is asked
/// unless it is confirmed, stop tries the ordinary graceful way first, the refusal cases say why, and with the mode off nothing changes. The
/// runner, the reviews, the poll and the elevator are fakes; no process starts and no prompt appears.
/// </summary>
public sealed class GatewayLifecycleAdminTests
{
    private sealed class Harness
    {
        public AdminRig Rig { get; }
        public GatewayLifecycle Lifecycle { get; }
        public List<string[]> OrdinaryRuns { get; } = new();
        public List<CommandReview> Reviews { get; } = new();
        public List<string> OrdinaryReviews { get; } = new();
        public List<GatewayLifecycleNotice> Notices { get; } = new();
        public int Refreshes { get; private set; }
        public bool Confirm { get; set; } = true;
        public bool Stopped { get; set; }
        public bool GatewayRunning { get; set; }
        public Func<bool> ConfirmOrdinary { get; set; } = () => true;
        public Action? AfterOrdinaryRun { get; set; }
        public int UserStopped { get; private set; }

        public Harness(AdminRig rig, InstallationGuard? installation = null)
        {
            Rig = rig;
            var guard = installation ?? new InstallationGuard(TestInstallations.UserDefault());
            Lifecycle = new GatewayLifecycle(
                () => Snapshot(),
                guard,
                argv =>
                {
                    Rig.Events.Add("ordinary:" + string.Join(' ', argv));
                    OrdinaryRuns.Add(argv.ToArray());
                    var invocation = InvocationFactory.CreateFor(GatewayControl.Executable, argv.ToArray());
                    InvocationFactory.Finish(invocation, 0);
                    AfterOrdinaryRun?.Invoke();
                    return Task.FromResult(invocation);
                },
                () =>
                {
                    Refreshes++;
                    Rig.Events.Add("poll");
                    return Task.CompletedTask;
                },
                () => UserStopped++,
                rig.Service)
            {
                Confirmer = (_, action) =>
                {
                    OrdinaryReviews.Add(GatewayControl.CommandText(action));
                    return ConfirmOrdinary();
                },
                ReviewConfirmer = (_, review) =>
                {
                    Rig.Events.Add("review");
                    Reviews.Add(review);
                    return Confirm;
                },
                Delay = _ => Task.CompletedTask,
                StopSettleAttempts = 2,
            };
            Lifecycle.Notice += (_, notice) => Notices.Add(notice);
        }

        private GatewaySnapshot Snapshot() => new()
        {
            State = GatewayRunning ? AppGatewayState.Running : AppGatewayState.GatewayStopped,
            Install = GatewayRunning ? InstallState.Running : InstallState.GatewayStopped,
        };
    }

    private static async Task<Harness> Start(AdminRig rig)
    {
        _ = await rig.Service.RefreshAsync();
        return new Harness(rig);
    }

    // ------------------------------------------------------------------ mode off: nothing changes

    [Fact]
    public async Task With_the_mode_off_a_start_is_the_ordinary_reviewed_run_and_the_administrator_feature_is_never_consulted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = false };
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Equal(new[] { "defenseclaw-gateway start" }, h.OrdinaryReviews);
        Assert.Equal(new[] { new[] { "start" } }, h.OrdinaryRuns);
        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
        Assert.Equal(new[] { "Start gateway" }, h.Notices.Select(n => n.Title).ToArray());
    }

    [Fact]
    public async Task On_a_runtime_without_the_feature_the_mode_is_ignored_even_if_the_setting_is_on()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true, Supported = false };
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Equal(new[] { new[] { "start" } }, h.OrdinaryRuns);
        Assert.Empty(rig.Elevator.Requests);
    }

    [Fact]
    public async Task With_the_mode_off_an_ownership_problem_refuses_the_ordinary_start_with_the_exit_77_sentence()
    {
        var rig = new AdminRig { ModeOn = false };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Empty(h.OrdinaryRuns);
        Assert.Empty(h.OrdinaryReviews);
        var notice = Assert.Single(h.Notices);
        Assert.Contains("owned by Administrators", notice.Message, StringComparison.Ordinal);
        Assert.Contains("gateway.pid", notice.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ mode on: review before elevate

    [Fact]
    public async Task A_first_start_reviews_everything_then_asks_in_order_and_never_runs_the_ordinary_start()
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Elevator.OnRun = request =>
        {
            // What the helper's first run leaves behind: itself and the copy in Program Files. The second step checks for them before it asks.
            if (request.Verb == AdminVerb.InstallCopy)
            {
                rig.InstallHelper();
                rig.InstallCopy(AdminRig.Gateway);
            }

            rig.LeaveResult(request.Verb, 0, "ok");
        };
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Equal(new[] { "review", "manifest", "elevate:install-copy", "elevate:start" }, rig.Events.Where(e => e != "poll").ToArray());
        Assert.Empty(h.OrdinaryRuns);
        Assert.Empty(h.OrdinaryReviews);
        var review = Assert.Single(h.Reviews);
        Assert.Equal(2, review.Steps.Count);
        Assert.Equal(new[] { AdminVerb.InstallCopy, AdminVerb.Start }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
        Assert.True(h.Refreshes >= 1);
        Assert.Equal("Start Gateway as Administrator", h.Notices[^1].Title);
        Assert.Equal(ToastLevel.Info, h.Notices[^1].Level);
    }

    [Fact]
    public async Task Declining_the_review_asks_windows_for_nothing_and_writes_nothing()
    {
        var rig = new AdminRig { ModeOn = true };
        var h = await Start(rig);
        h.Confirm = false;

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Single(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
        Assert.Empty(h.OrdinaryRuns);
        Assert.Empty(h.Notices);
    }

    [Fact]
    public async Task A_refused_plan_shows_its_reason_and_neither_reviews_nor_elevates()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = AdminRig.Gateway.Reverse().ToArray();
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        var notice = Assert.Single(h.Notices);
        Assert.Contains("Choose Restart Gateway as Administrator", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_cancelled_at_the_prompt_ends_the_run_and_the_next_step_is_not_asked()
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Elevator.Behaviour = _ => ElevationOutcome.Cancelled;
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Equal(new[] { AdminVerb.InstallCopy }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
        var notice = Assert.Single(h.Notices);
        Assert.Equal(ToastLevel.Info, notice.Level);
        Assert.Contains("Cancelled at the Windows prompt", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_helper_that_refuses_is_a_warning_and_ends_the_run()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevator.OnRun = request => rig.LeaveResult(request.Verb, 77, "Refused: a gateway is already running.");
        rig.Elevator.Behaviour = _ => ElevationOutcome.Completed(AdminExit.Refused);
        var h = await Start(rig);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        var notice = Assert.Single(h.Notices);
        Assert.Equal(ToastLevel.Warning, notice.Level);
        Assert.Contains("already running", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_in_administrator_mode_is_one_reviewed_step_and_the_ordinary_restart_never_runs()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var h = await Start(rig);
        h.GatewayRunning = true;

        await h.Lifecycle.RunAsync(GatewayAction.Restart);

        Assert.Equal(new[] { AdminVerb.Restart }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
        Assert.Empty(h.OrdinaryRuns);
        Assert.Equal("Restart gateway as administrator?", Assert.Single(h.Reviews).Title);
    }

    [Fact]
    public async Task A_read_only_installation_blocks_it_before_the_review_with_the_installations_sentence()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var guard = new InstallationGuard(TestInstallations.Managed());
        _ = await rig.Service.RefreshAsync();
        var h = new Harness(rig, guard);

        await h.Lifecycle.RunAsync(GatewayAction.Start);

        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Equal(TestInstallations.Managed().BlockedReason, Assert.Single(h.Notices).Message);
    }

    [Fact]
    public async Task The_review_itself_carries_the_installations_reason_when_a_plan_is_built_on_a_read_only_installation()
    {
        // The standalone actions (Prepare, Repair) are not gated by GatewayControl.Availability; the review's own guard is what stops them.
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        _ = await rig.Service.RefreshAsync();
        var h = new Harness(rig, new InstallationGuard(TestInstallations.Managed()));

        await h.Lifecycle.RepairOwnershipAsync();

        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Equal(TestInstallations.Managed().BlockedReason, Assert.Single(h.Notices).Message);
    }

    // ------------------------------------------------------------------ stop

    [Fact]
    public async Task Stop_tries_the_ordinary_graceful_way_first_and_does_not_elevate_when_it_worked()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var h = await Start(rig);
        h.GatewayRunning = true;
        h.AfterOrdinaryRun = () => h.GatewayRunning = false;

        await h.Lifecycle.RunAsync(GatewayAction.Stop);

        Assert.Equal(new[] { "defenseclaw-gateway stop" }, h.OrdinaryReviews);
        Assert.Equal(new[] { new[] { "stop" } }, h.OrdinaryRuns);
        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Equal(1, h.UserStopped);
    }

    [Fact]
    public async Task Stop_that_leaves_the_gateway_running_offers_a_stop_as_administrator_with_its_own_review()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var h = await Start(rig);
        h.GatewayRunning = true;
        rig.Elevator.OnRun = request => rig.LeaveResult(request.Verb, 0, "stopped");

        await h.Lifecycle.RunAsync(GatewayAction.Stop);

        Assert.Equal(new[] { new[] { "stop" } }, h.OrdinaryRuns);
        var review = Assert.Single(h.Reviews);
        Assert.Equal("Stop gateway as administrator?", review.Title);
        Assert.Equal(new[] { AdminVerb.Stop }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
        Assert.True(rig.Events.IndexOf("ordinary:stop") < rig.Events.IndexOf("review"));
        Assert.True(rig.Events.IndexOf("review") < rig.Events.IndexOf("elevate:stop"));
        Assert.Contains(h.Notices, n => n.Message.Contains("Asking to stop it as administrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Declining_the_ordinary_stop_does_nothing_at_all()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var h = await Start(rig);
        h.GatewayRunning = true;
        h.ConfirmOrdinary = () => false;

        await h.Lifecycle.RunAsync(GatewayAction.Stop);

        Assert.Empty(h.OrdinaryRuns);
        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Equal(0, h.UserStopped);
    }

    [Fact]
    public async Task Declining_the_administrator_stop_leaves_the_gateway_running_and_asks_windows_for_nothing()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var h = await Start(rig);
        h.GatewayRunning = true;
        h.Confirm = false;

        await h.Lifecycle.RunAsync(GatewayAction.Stop);

        Assert.Single(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
    }

    // ------------------------------------------------------------------ prepare and repair

    [Fact]
    public async Task Prepare_reviews_then_runs_the_install_step_and_starts_nothing()
    {
        var rig = new AdminRig { ModeOn = true };
        var h = await Start(rig);

        await h.Lifecycle.PrepareAdministratorCopyAsync();

        Assert.Equal("Prepare the administrator copy?", Assert.Single(h.Reviews).Title);
        Assert.Equal(new[] { AdminVerb.InstallCopy }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
        Assert.Empty(h.OrdinaryRuns);
    }

    [Fact]
    public async Task Repair_ownership_reviews_then_runs_only_the_repair_verb()
    {
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        var h = await Start(rig);

        await h.Lifecycle.RepairOwnershipAsync();

        Assert.Equal("Repair ownership?", Assert.Single(h.Reviews).Title);
        Assert.Equal(new[] { AdminVerb.RepairOwnership }, rig.Elevator.Requests.Select(r => r.Verb).ToArray());
    }

    [Fact]
    public async Task Repair_with_nothing_to_repair_does_not_even_review()
    {
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };
        var h = await Start(rig);

        await h.Lifecycle.RepairOwnershipAsync();

        Assert.Empty(h.Reviews);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Contains("Nothing to repair", Assert.Single(h.Notices).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lifecycle_without_the_feature_says_so_for_prepare_and_repair()
    {
        var lifecycle = new GatewayLifecycle(
            () => new GatewaySnapshot(),
            new InstallationGuard(TestInstallations.UserDefault()),
            _ => throw new InvalidOperationException("must not run"),
            () => Task.CompletedTask,
            () => { });
        var notices = new List<GatewayLifecycleNotice>();
        lifecycle.Notice += (_, n) => notices.Add(n);

        await lifecycle.PrepareAdministratorCopyAsync();
        await lifecycle.RepairOwnershipAsync();

        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Contains("not available", n.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void The_buttons_are_named_as_the_mac_names_them()
    {
        Assert.Equal("Start Gateway as Administrator", GatewayControl.AdministratorTitle(GatewayAction.Start));
        Assert.Equal("Restart Gateway as Administrator", GatewayControl.AdministratorTitle(GatewayAction.Restart));
        Assert.Equal("Stop Gateway as Administrator", GatewayControl.AdministratorTitle(GatewayAction.Stop));
    }
}
