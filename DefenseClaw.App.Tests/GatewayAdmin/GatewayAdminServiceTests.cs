using DefenseClaw.Core.AiRuntime;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Install;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>
/// The state matrix of "Run gateway as administrator" on the app side: what the status says for each kind of machine, what is refused before
/// anything is asked, which steps a plan has, and that the only call that could elevate is made by a confirmed step and by nothing else. Every
/// effect (the disk, the account, the release, the prompt) is a fake; no test touches Program Files or raises a UAC prompt.
/// </summary>
public sealed class GatewayAdminServiceTests
{
    // ------------------------------------------------------------------ the status

    [Fact]
    public async Task On_a_runtime_without_runtime_monitoring_the_mode_is_unsupported_even_when_the_setting_is_on()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { Supported = false, ModeOn = true };

        var status = await rig.Service.RefreshAsync();

        Assert.False(status.Supported);
        Assert.Equal("Needs DefenseClaw 1.0.", status.UnsupportedReason);
        Assert.False(status.IsActive);
        Assert.False(status.CanToggle);
        Assert.Equal("Needs DefenseClaw 1.0.", status.Headline);
        // Nothing of Program Files was read for a runtime that cannot use it.
        Assert.Equal(0, rig.Fs.Opens);
    }

    [Theory]
    [InlineData(AdminAccountKind.StandardUser)]
    [InlineData(AdminAccountKind.Unknown)]
    public async Task A_standard_user_cannot_turn_it_on_and_is_told_why(AdminAccountKind kind)
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Account.Kind = kind;

        var status = await rig.Service.RefreshAsync();

        Assert.Equal(AdminEligibility.NeedsLocalAdministrator, status.Eligibility);
        Assert.False(status.CanToggle);
        Assert.False(status.IsActive);
        Assert.Contains("local administrator", status.Headline, StringComparison.Ordinal);
        Assert.False(rig.Service.TrySetMode(true));
        Assert.Empty(rig.Saved);
    }

    [Fact]
    public async Task A_missing_profile_or_program_files_means_not_eligible()
    {
        var rig = new AdminRig();
        rig.LayoutSource.Layout = null;

        var status = await rig.Service.RefreshAsync();

        Assert.Equal(AdminEligibility.LayoutUnavailable, status.Eligibility);
        Assert.False(status.CanToggle);
    }

    [Theory]
    [InlineData(AdminAccountKind.SplitTokenAdministrator)]
    [InlineData(AdminAccountKind.AlreadyElevated)]
    public async Task A_local_administrator_is_eligible(AdminAccountKind kind)
    {
        var rig = new AdminRig();
        rig.Account.Kind = kind;

        var status = await rig.Service.RefreshAsync();

        Assert.Equal(AdminEligibility.Eligible, status.Eligibility);
        Assert.True(status.CanToggle);
    }

    [Fact]
    public async Task Nothing_installed_yet_says_the_first_start_installs_the_helper()
    {
        var rig = new AdminRig { ModeOn = true };

        var status = await rig.Service.RefreshAsync();

        Assert.True(status.IsActive);
        Assert.False(status.HelperInstalled);
        Assert.True(status.HelperStaged);
        Assert.False(status.CopyPresent);
        Assert.Contains("installs the helper", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installed_helper_and_a_matching_trusted_copy_is_the_ready_state()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };

        var status = await rig.Service.RefreshAsync();

        Assert.True(status.HelperTrusted);
        Assert.True(status.CopyTrusted);
        Assert.True(status.CopyMatchesInstalled);
        Assert.Equal("1.0.0", status.CopyVersion);
        Assert.False(status.OwnershipProblem);
        Assert.Equal("On. The administrator copy matches the installed gateway.", status.Headline);
    }

    [Fact]
    public async Task A_copy_that_differs_from_the_installed_gateway_says_to_restart_as_administrator()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = AdminRig.Gateway.Reverse().ToArray();

        var status = await rig.Service.RefreshAsync();

        Assert.False(status.CopyMatchesInstalled);
        Assert.Contains("Restart Gateway as Administrator", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_or_helper_that_is_not_the_installed_file_is_reported_and_not_trusted()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Acl.Add(new AclEntry(AdminRig.UserSid, true, 0x2));

        var status = await rig.Service.RefreshAsync();

        Assert.True(status.CopyPresent);
        Assert.False(status.CopyTrusted);
        Assert.Contains(status.Problems, p => p.Contains(AdminRig.UserSid, StringComparison.Ordinal));
        Assert.Contains("not the file the helper installed", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_owned_by_administrators_are_the_windows_exit_77_and_offer_the_repair()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "hook_contract_lock.json"), new byte[] { 1 }, AdminAcl.Administrators);

        var status = await rig.Service.RefreshAsync();

        Assert.True(status.OwnershipProblem);
        Assert.Equal(new[] { "hook_contract_lock.json" }, status.OwnershipFiles);
        Assert.True(status.CanRepairOwnership);
        Assert.Equal(GatewayAdminStatus.OwnershipSentence, status.Headline);
        Assert.Contains("Run Repair ownership", GatewayAdminStatus.OwnershipSentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ownership_problem_is_shown_with_the_mode_off_too_because_that_is_when_it_bites()
    {
        var rig = new AdminRig { ModeOn = false };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);

        var status = await rig.Service.RefreshAsync();

        Assert.False(status.IsActive);
        Assert.True(status.CanRepairOwnership);
        Assert.Equal(GatewayAdminStatus.OwnershipSentence, status.Headline);
        Assert.NotNull(rig.Service.NormalPathBlockedReason(GatewayAction.Start));
        Assert.NotNull(rig.Service.NormalPathBlockedReason(GatewayAction.Restart));
        Assert.Null(rig.Service.NormalPathBlockedReason(GatewayAction.Stop));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(null, @"C:\TestPF\DefenseClaw Companion\AdminGateway\S-1-5-21-1111111111-2222222222-3333333333-1001\defenseclaw-gateway.exe", true)]
    [InlineData(null, @"C:\Users\x\.local\bin\defenseclaw-gateway.exe", null)]
    public async Task A_running_gateways_elevation_comes_from_its_token_or_failing_that_its_image(bool? token, string? image, bool? expected)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = token;
        rig.Snapshot = new GatewaySnapshot
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            PortOwner = new DefenseClaw.Core.Net.PortOwner(4242, "defenseclaw-gateway", "127.0.0.1", 18970, image, AdminRig.AppSession),
        };

        var status = await rig.Service.RefreshAsync();

        Assert.Equal(expected, status.GatewayRunningElevated);
    }

    [Fact]
    public async Task Elevated_means_our_trusted_copy_is_the_running_gateway_with_an_elevated_token()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        IGatewayElevationStatus status = rig.Service;
        Assert.Equal(GatewayElevation.Unknown, status.Current);

        GatewaySnapshot Running(string image) => new()
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            PortOwner = new DefenseClaw.Core.Net.PortOwner(1, "defenseclaw-gateway", "127.0.0.1", 18970, image, AdminRig.AppSession),
        };

        rig.Elevation.Answer = true;
        rig.Snapshot = Running(rig.Layout.GatewayCopyPath);
        _ = await rig.Service.RefreshAsync();
        Assert.Equal(GatewayElevation.Elevated, status.Current);

        // An elevated process that is not our copy is not ours.
        rig.Snapshot = Running(@"C:\other\defenseclaw-gateway.exe");
        _ = await rig.Service.RefreshAsync();
        Assert.Equal(GatewayElevation.NotElevated, status.Current);

        rig.Elevation.Answer = false;
        rig.Snapshot = Running(rig.Layout.GatewayCopyPath);
        _ = await rig.Service.RefreshAsync();
        Assert.Equal(GatewayElevation.NotElevated, status.Current);
    }

    [Fact]
    public async Task A_gateway_that_is_not_running_has_no_elevation_to_report()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Elevation.Answer = true;

        var status = await rig.Service.RefreshAsync();

        Assert.Null(status.GatewayRunningElevated);
    }

    // ------------------------------------------------------------------ the switch

    [Fact]
    public async Task The_switch_saves_the_setting_and_starts_and_elevates_nothing()
    {
        var rig = new AdminRig();
        _ = await rig.Service.RefreshAsync();

        Assert.True(rig.Service.TrySetMode(true));
        Assert.True(rig.Service.TrySetMode(false));

        Assert.Equal(new[] { true, false }, rig.Saved);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
        Assert.Empty(rig.Fs.Changes);
    }

    [Fact]
    public async Task Turning_it_off_is_always_allowed_even_where_turning_it_on_is_not()
    {
        var rig = new AdminRig { ModeOn = true, Supported = false };
        _ = await rig.Service.RefreshAsync();

        Assert.False(rig.Service.TrySetMode(true));
        Assert.True(rig.Service.TrySetMode(false));
        Assert.Equal(new[] { false }, rig.Saved);
    }

    // ------------------------------------------------------------------ the plans

    [Fact]
    public async Task A_first_start_installs_the_helper_and_the_copy_then_starts_in_that_order_with_the_gateway_checked_against_its_release()
    {
        var rig = new AdminRig { ModeOn = true };

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Null(plan.Refusal);
        Assert.Equal(new[] { AdminVerb.InstallCopy, AdminVerb.Start }, plan.Steps.Select(s => s.Verb).ToArray());
        Assert.True(plan.Steps[0].BootstrapsHelper);
        Assert.True(plan.Steps[0].WritesManifest);
        Assert.False(plan.Steps[1].WritesManifest);
        Assert.Equal(new[] { "1.0.0" }, rig.Release.Asked);
        Assert.Equal(AdminRig.Sha(AdminRig.Helper), plan.StagedHelperSha256);

        // The review shows exactly what runs elevated: both steps, the helper by path, the copy and its hashes. Nothing has run.
        var review = plan.Review!;
        Assert.Equal("Start gateway as administrator?", review.Title);
        Assert.Equal(2, review.Steps.Count);
        Assert.Equal(new[] { "install-copy" }, review.Steps[0].Argv);
        Assert.Equal(new[] { "start" }, review.Steps[1].Argv);
        Assert.Equal(@"C:\TestApp\DefenseClaw.GatewayAdmin.exe", review.Steps[0].Executable);
        Assert.Equal(rig.Layout.HelperExe, review.Steps[1].Executable);
        Assert.Contains(rig.Layout.GatewayCopyPath, review.Summary, StringComparison.Ordinal);
        Assert.Contains(Hex.Short(AdminRig.Sha(AdminRig.Gateway)), review.Summary, StringComparison.Ordinal);
        Assert.Contains("2 Windows prompts", review.Summary, StringComparison.Ordinal);
        Assert.Contains(review.Warnings, w => w.Title == "Runs with administrator rights");
        Assert.All(review.Steps, s => Assert.Equal(DefenseClaw.Core.Cli.CommandTier.StateChanging, s.Tier));
        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
    }

    [Fact]
    public async Task A_start_with_a_trusted_matching_copy_is_one_step_and_needs_no_network()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Equal(new[] { AdminVerb.Start }, plan.Steps.Select(s => s.Verb).ToArray());
        Assert.Empty(rig.Release.Asked);
        Assert.Contains("one Windows prompt", plan.Review!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_when_the_installed_gateway_differs_is_refused_with_the_macs_sentence_and_only_restart_refreshes()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = AdminRig.Gateway.Reverse().ToArray();

        var start = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        Assert.Equal(
            "The installed gateway differs from the administrator copy. Choose Restart Gateway as Administrator to use the installed version. No gateway action was performed.",
            start.Refusal);

        // The release the installed gateway would be checked against is that of the new bytes; here it is not the same as the installed file.
        var restart = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);
        Assert.Contains("is not the defenseclaw-gateway.exe in the 1.0.0 release", restart.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_with_a_newer_installed_gateway_has_one_step_that_writes_the_manifest_after_the_release_check()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var newer = AdminRig.Gateway.Reverse().ToArray();
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = newer;
        rig.Release.Release = rig.Release.Release! with { GatewaySha256 = AdminRig.Sha(newer) };

        var plan = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);

        Assert.Null(plan.Refusal);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(AdminVerb.Restart, step.Verb);
        Assert.True(step.WritesManifest);
        Assert.False(step.BootstrapsHelper);
        Assert.Equal("Restart gateway as administrator?", plan.Review!.Title);
        Assert.Contains("1.0.0", plan.Review.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_of_a_matching_trusted_copy_needs_no_manifest_and_no_network()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };

        var plan = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);

        var step = Assert.Single(plan.Steps);
        Assert.False(step.WritesManifest);
        Assert.Empty(rig.Release.Asked);
    }

    [Fact]
    public async Task A_restart_before_anything_is_installed_installs_then_restarts()
    {
        var rig = new AdminRig { ModeOn = true };

        var plan = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);

        Assert.Equal(new[] { AdminVerb.InstallCopy, AdminVerb.Restart }, plan.Steps.Select(s => s.Verb).ToArray());
        Assert.True(plan.Steps[0].BootstrapsHelper);
        Assert.False(plan.Steps[1].WritesManifest);
    }

    [Fact]
    public async Task Stop_is_one_step_with_a_trusted_copy_and_a_refusal_without_one()
    {
        var installed = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var stop = await installed.Service.PlanAsync(GatewayAction.Stop, CancellationToken.None);
        Assert.Equal(new[] { AdminVerb.Stop }, stop.Steps.Select(s => s.Verb).ToArray());
        Assert.Empty(installed.Release.Asked);

        var none = new AdminRig { ModeOn = true };
        var refused = await none.Service.PlanAsync(GatewayAction.Stop, CancellationToken.None);
        Assert.Contains("nothing to stop as administrator", refused.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installed_gateway_that_is_not_the_release_is_never_copied_to_where_it_runs_with_administrator_rights()
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Release.Release = rig.Release.Release! with { GatewaySha256 = new string('c', 64) };

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Contains("a build from source or a modified file", plan.Refusal, StringComparison.Ordinal);
        Assert.Empty(rig.Elevator.Requests);
    }

    [Fact]
    public async Task A_release_that_cannot_be_checked_stops_the_plan_with_its_reason()
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Release.Release = null;
        rig.Release.Problem = "The signature on checksums.txt did not verify: nope";

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Equal("The signature on checksums.txt did not verify: nope", plan.Refusal);
    }

    [Fact]
    public async Task An_unknown_version_cannot_be_checked_against_a_release()
    {
        var rig = new AdminRig { ModeOn = true, InstalledVersion = null };

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Contains("version is not known", plan.Refusal, StringComparison.Ordinal);
        Assert.Empty(rig.Release.Asked);
    }

    [Fact]
    public async Task The_release_is_fetched_once_per_version_for_the_session()
    {
        var rig = new AdminRig { ModeOn = true };

        _ = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        _ = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);

        Assert.Single(rig.Release.Asked);
    }

    [Fact]
    public async Task A_helper_that_is_not_next_to_the_app_stops_the_first_run()
    {
        var rig = new AdminRig { ModeOn = true };
        rig.Stage.Helper = null;
        rig.Stage.Problem = "DefenseClaw.GatewayAdmin.exe is not next to the app.";

        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        Assert.Equal("DefenseClaw.GatewayAdmin.exe is not next to the app.", plan.Refusal);
    }

    [Theory]
    [InlineData(GatewayAction.Start)]
    [InlineData(GatewayAction.Restart)]
    public async Task Files_owned_by_administrators_refuse_a_start_or_restart_but_not_a_stop(GatewayAction action)
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "audit.db"), new byte[] { 1 }, AdminAcl.Administrators);

        var plan = await rig.Service.PlanAsync(action, CancellationToken.None);
        var stop = await rig.Service.PlanAsync(GatewayAction.Stop, CancellationToken.None);

        Assert.Contains("Run Repair ownership", plan.Refusal, StringComparison.Ordinal);
        Assert.Contains("audit.db", plan.Refusal, StringComparison.Ordinal);
        Assert.Null(stop.Refusal);
    }

    [Fact]
    public async Task A_loader_switch_in_the_apps_environment_refuses_every_plan_before_anything_is_asked()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        rig.EnvironmentNames = new[] { "PATH", "DOTNET_STARTUP_HOOKS" };

        foreach (var action in new[] { GatewayAction.Start, GatewayAction.Stop, GatewayAction.Restart })
        {
            var plan = await rig.Service.PlanAsync(action, CancellationToken.None);
            Assert.Contains("DOTNET_STARTUP_HOOKS", plan.Refusal, StringComparison.Ordinal);
        }

        Assert.Contains("DOTNET_STARTUP_HOOKS", (await rig.Service.PlanPrepareAsync(CancellationToken.None)).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plans_are_refused_on_an_unsupported_runtime_and_for_a_standard_user_with_the_same_words_as_the_status()
    {
        var old = new AdminRig { ModeOn = true, Supported = false };
        Assert.Equal("Needs DefenseClaw 1.0.", (await old.Service.PlanAsync(GatewayAction.Start, CancellationToken.None)).Refusal);

        var standard = new AdminRig { ModeOn = true };
        standard.Account.Kind = AdminAccountKind.StandardUser;
        var plan = await standard.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        Assert.Contains("local administrator", plan.Refusal, StringComparison.Ordinal);
        Assert.Empty(standard.Elevator.Requests);
    }

    [Fact]
    public async Task Prepare_installs_the_helper_and_the_copy_and_starts_nothing()
    {
        var rig = new AdminRig { ModeOn = true };

        var plan = await rig.Service.PlanPrepareAsync(CancellationToken.None);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(AdminVerb.InstallCopy, step.Verb);
        Assert.True(step.BootstrapsHelper);
        Assert.Equal("Prepare the administrator copy?", plan.Review!.Title);
        Assert.Contains("starts and stops nothing", plan.Review.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_is_refused_when_there_is_nothing_to_prepare()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };

        var plan = await rig.Service.PlanPrepareAsync(CancellationToken.None);

        Assert.Contains("Nothing to prepare", plan.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_ownership_names_the_folder_and_the_files_and_needs_the_helper_installed()
    {
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);

        var plan = await rig.Service.PlanRepairOwnershipAsync(CancellationToken.None);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(AdminVerb.RepairOwnership, step.Verb);
        Assert.False(step.BootstrapsHelper);
        Assert.Contains(rig.Layout.DataDirectory, plan.Review!.Summary, StringComparison.Ordinal);
        Assert.Contains("gateway.pid", plan.Review.Summary, StringComparison.Ordinal);
        Assert.Contains("Links are not followed", plan.Review.Summary, StringComparison.Ordinal);

        var noHelper = new AdminRig { ModeOn = true };
        noHelper.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        Assert.Contains("Prepare administrator copy first", (await noHelper.Service.PlanRepairOwnershipAsync(CancellationToken.None)).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_ownership_with_nothing_wrong_is_refused()
    {
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };

        var plan = await rig.Service.PlanRepairOwnershipAsync(CancellationToken.None);

        Assert.Contains("Nothing to repair", plan.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_never_elevates_writes_a_manifest_or_changes_a_file()
    {
        var rig = new AdminRig { ModeOn = true };

        _ = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        _ = await rig.Service.PlanAsync(GatewayAction.Restart, CancellationToken.None);
        _ = await rig.Service.PlanAsync(GatewayAction.Stop, CancellationToken.None);
        _ = await rig.Service.PlanPrepareAsync(CancellationToken.None);
        _ = await rig.Service.PlanRepairOwnershipAsync(CancellationToken.None);

        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
        Assert.Empty(rig.Fs.Changes);
    }

    // ------------------------------------------------------------------ running a step

    [Fact]
    public async Task The_first_step_writes_the_manifest_before_the_prompt_and_starts_the_staged_helper_held_to_the_reviewed_hash()
    {
        var rig = new AdminRig { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        rig.Elevator.OnRun = _ => rig.LeaveResult(AdminVerb.InstallCopy, 0, "Installed the helper.", "Copied the gateway.");

        var outcome = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(new[] { "manifest", "elevate:install-copy" }, rig.Events);
        var request = Assert.Single(rig.Elevator.Requests);
        Assert.Equal(@"C:\TestApp\DefenseClaw.GatewayAdmin.exe", request.Executable);
        Assert.Equal(AdminRig.Sha(AdminRig.Helper), request.HoldAndVerifySha256);
        var manifest = Assert.Single(rig.Manifest.Written);
        Assert.Equal(AdminRig.UserSid, manifest.UserSid);
        Assert.Equal(AdminRig.Sha(AdminRig.Gateway), manifest.GatewaySha256);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("Verified", manifest.SignatureState);
        Assert.Contains("Copied the gateway.", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_helper_that_changed_since_it_was_reviewed_is_not_started()
    {
        var rig = new AdminRig { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        rig.Stage.Helper = new StagedHelper(@"C:\TestApp\DefenseClaw.GatewayAdmin.exe", new string('9', 64));

        var outcome = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("not the file you reviewed", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Elevator.Requests);
        Assert.Empty(rig.Manifest.Written);
    }

    [Fact]
    public async Task A_later_step_runs_the_helper_in_program_files_and_only_after_checking_it_is_still_the_file_it_installed()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        var ok = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);
        Assert.True(ok.Succeeded);
        var request = Assert.Single(rig.Elevator.Requests);
        Assert.Equal(rig.Layout.HelperExe, request.Executable);
        Assert.Null(request.HoldAndVerifySha256);
        Assert.Equal(AdminVerb.Start, request.Verb);

        // Someone grants Users write access to the helper in Program Files: it is not started again.
        rig.Fs.Items[rig.Layout.HelperExe].Acl.Add(new AclEntry(AdminAcl.Users, true, 0x2));
        var refused = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Contains("not the admin-only file", refused.Message, StringComparison.Ordinal);
        Assert.Single(rig.Elevator.Requests);
    }

    [Fact]
    public async Task A_step_that_needs_a_manifest_but_has_no_checked_release_does_not_start()
    {
        var rig = new AdminRig(helperInstalled: true) { ModeOn = true };
        var plan = new AdminPlan(null, new[] { new AdminStep(AdminVerb.InstallCopy, false, true, "x") }, null);

        var outcome = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(rig.Elevator.Requests);
    }

    [Fact]
    public async Task The_outcomes_of_a_run_map_to_plain_messages()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        var step = plan.Steps[0];

        rig.Elevator.Behaviour = _ => ElevationOutcome.Cancelled;
        var cancelled = await rig.Service.ExecuteAsync(plan, step, CancellationToken.None);
        Assert.True(cancelled.Cancelled);
        Assert.Equal("Cancelled at the Windows prompt. Nothing was changed.", cancelled.Message);

        rig.Elevator.Behaviour = _ => ElevationOutcome.Failed("the helper did not finish in time");
        Assert.Contains("could not be started: the helper did not finish in time", (await rig.Service.ExecuteAsync(plan, step, CancellationToken.None)).Message, StringComparison.Ordinal);

        rig.Elevator.Behaviour = _ => ElevationOutcome.Completed(AdminExit.Declined);
        var declined = await rig.Service.ExecuteAsync(plan, step, CancellationToken.None);
        Assert.True(declined.Cancelled);
        Assert.Contains("Declined", declined.Message, StringComparison.Ordinal);

        rig.Elevator.OnRun = _ => rig.LeaveResult(AdminVerb.Start, 77, "Refused: a gateway is already running.");
        rig.Elevator.Behaviour = _ => ElevationOutcome.Completed(AdminExit.Refused);
        var refused = await rig.Service.ExecuteAsync(plan, step, CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Contains("The helper refused: Refused: a gateway is already running.", refused.Message, StringComparison.Ordinal);

        rig.Elevator.OnRun = null;
        rig.Elevator.Behaviour = _ => ElevationOutcome.Completed(AdminExit.Usage);
        Assert.Contains("did not accept the command", (await rig.Service.ExecuteAsync(plan, step, CancellationToken.None)).Message, StringComparison.Ordinal);

        rig.Elevator.Behaviour = _ => ElevationOutcome.Completed(1);
        Assert.Contains("exit code 1", (await rig.Service.ExecuteAsync(plan, step, CancellationToken.None)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_note_from_another_verb_or_from_before_the_run_is_not_shown()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);

        // Left by an earlier run of another verb.
        rig.LeaveResult(AdminVerb.Stop, 0, "Stopped something else.");
        var wrongVerb = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);
        Assert.DoesNotContain("Stopped something else.", wrongVerb.Message, StringComparison.Ordinal);

        // Same verb but dated before this run began.
        rig.Fs.AddFile(
            rig.Layout.ResultPath,
            System.Text.Encoding.UTF8.GetBytes(new AdminRunResult(1, "start", 0, rig.Now.UtcDateTime.AddHours(-3).ToString("O"), new[] { "Old news." }).ToJson()));
        var stale = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);
        Assert.DoesNotContain("Old news.", stale.Message, StringComparison.Ordinal);
        Assert.Equal("The gateway was started with administrator rights.", stale.Message);
    }

    [Fact]
    public async Task The_status_is_refreshed_after_a_step()
    {
        var rig = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        var plan = await rig.Service.PlanAsync(GatewayAction.Start, CancellationToken.None);
        var raised = 0;
        rig.Service.Changed += (_, _) => raised++;

        _ = await rig.Service.ExecuteAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.True(raised >= 1);
    }

    [Fact]
    public void The_app_has_exactly_one_place_that_can_raise_a_prompt()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DefenseClaw.Win.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "DefenseClaw.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\", StringComparison.Ordinal) || file.Contains(@"\bin\", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.Contains("\"runas\"", StringComparison.OrdinalIgnoreCase) && !file.EndsWith("GatewayAdminWindows.cs", StringComparison.Ordinal))
                {
                    offenders.Add(file);
                }
            }
        }

        Assert.Empty(offenders);
    }
}
