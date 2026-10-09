using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// CUST-264 on a read-only installation (CUST-308): Rerun of an Activity entry, the palette's "Re-run last command" and "Cancel running command",
/// the gateway's fixed verbs, and the recents, with the Splunk dashboards rows (CUST-317) among them. The rule these hold to is the installation's:
/// its sentence is the only reason a control gives and it comes before every other (running, a gateway that is not running, Terraform), a read still
/// works, and cancelling is not a change to the installation. Nothing starts a process: the review, the toast and the runner behind Rerun are test
/// seams, and the Terraform look is a fake.
/// </summary>
public sealed class InstallationRerunTests : IDisposable
{
    private const string Cli = "defenseclaw";
    private const string Gateway = "defenseclaw-gateway";

    private static readonly TerraformStatus TerraformReady =
        new(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe");

    private static readonly TerraformStatus NoTerraform =
        new(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform");

    private static readonly GatewaySnapshot GatewayRunning = new() { Install = InstallState.Running, State = AppGatewayState.Running };

    private static readonly GatewaySnapshot GatewayStopped = new() { Install = InstallState.GatewayStopped, State = AppGatewayState.GatewayStopped };

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private sealed class Answers
    {
        public bool Confirm { get; set; } = true;

        /// <summary>Runs when a review is shown, before the answer: what changes while the question is on screen.</summary>
        public Action<CommandReview>? WhileOpen { get; set; }
    }

    private sealed record Scene(
        AppServices Services,
        ShellActions Actions,
        PanelCatalog Catalog,
        Answers Answer,
        List<string> Toasts,
        List<CommandReview> Reviews,
        List<(string Tool, string[] Argv)> Ran,
        List<CliInvocation> Activity)
    {
        public CommandRerun Rerun => Actions.Rerun;
    }

    /// <summary>
    /// Actions over isolated services as the palette tests build them: the review is recorded and answered by <see cref="Answers"/>, the toast
    /// is a recorder, and what a confirmed Rerun reaches is a fake that records the tool and argv and finishes with exit 0.
    /// </summary>
    private Scene Create(InstallationContext? installation = null, ITerraformProbe? terraform = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            terraformProbe: terraform);
        _services.Add(services);

        var catalog = new PanelCatalog(services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var answer = new Answers();
        var toasts = new List<string>();
        var reviews = new List<CommandReview>();
        var ran = new List<(string Tool, string[] Argv)>();
        var actions = new ShellActions(services, catalog, tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = _ => { },
            Confirmer = review =>
            {
                reviews.Add(review);
                answer.WhileOpen?.Invoke(review);
                return answer.Confirm;
            },
        };
        actions.Rerun.Runner = (tool, argv, _) =>
        {
            ran.Add((tool, argv.ToArray()));
            var again = InvocationFactory.CreateFor(tool, argv.ToArray());
            InvocationFactory.Finish(again, 0);
            return Task.FromResult(again);
        };
        return new Scene(services, actions, catalog, answer, toasts, reviews, ran, new List<CliInvocation>());
    }

    private InstallationContext Managed() => TestInstallations.ManagedAt(_temp.Path);

    private InstallationGuard ManagedGuard() => new(Managed());

    private static InstallationGuard WritableGuard() => new(TestInstallations.UserDefault());

    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Task<string> Declined(CliInvocation invocation, CommandTier tier) => Task.FromResult(string.Empty);

    /// <summary>An entry the palette sees as the runner's ring does: newest first.</summary>
    private static CliInvocation Entry(Scene scene, string executable, int? exit, DateTimeOffset startedAt, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv, startedAt);
        if (exit is { } code)
        {
            InvocationFactory.Finish(invocation, code, startedAt.AddSeconds(1));
        }

        scene.Activity.Insert(0, invocation);
        scene.Actions.ActivitySource = () => scene.Activity.ToArray();
        return invocation;
    }

    private static IReadOnlyList<ShellCommand> Palette(Scene scene) =>
        ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { }, curated: CuratedCommandCatalog.For(null).Commands);

    private static ShellCommand Row(Scene scene, string id) => Assert.Single(Palette(scene), c => c.Id == id);

    /// <summary>Entries whose Rerun would change the installation.</summary>
    public static IEnumerable<object[]> Changes() => new[]
    {
        new object[] { Cli, new[] { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" } },
        new object[] { Cli, new[] { "skill", "quarantine", "--", "pdf-tools" } },
        new object[] { Cli, new[] { "setup", "guardrail" } },
        new object[] { Gateway, new[] { "restart" } },
        new object[] { Gateway, new[] { "stop" } },
        new object[] { Gateway, new[] { "watchdog", "stop" } },
        new object[] { Gateway, new[] { "policy", "reload" } },
        new object[] { Gateway, new[] { "connector", "teardown" } },
    };

    /// <summary>Entries that only read: the runner lets them through on a read-only installation, so their Rerun works.</summary>
    public static IEnumerable<object[]> Reads() => new[]
    {
        new object[] { Cli, new[] { "doctor" } },
        new object[] { Cli, new[] { "skill", "list" } },
        new object[] { Cli, new[] { "keys", "check" } },
        new object[] { Gateway, new[] { "status" } },
        new object[] { Gateway, new[] { "watchdog", "status" } },
        new object[] { Gateway, new[] { "connector", "verify" } },
        new object[] { Gateway, new[] { "connector", "list-backups" } },
        new object[] { Gateway, new[] { "policy", "domains" } },
    };

    // ------------------------------------------------------------------ which Rerun is off, and what it says

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_change_has_a_Rerun_that_is_drawn_but_off_and_the_installations_sentence_is_the_only_reason_it_gives(string tool, string[] argv)
    {
        var guard = ManagedGuard();

        // Finished or still running: the sentence is the installation's either way, ahead of "wait for this command to finish".
        foreach (var entry in new[] { Finished(tool, 0, argv), InvocationFactory.CreateFor(tool, argv) })
        {
            var block = CommandRerun.InstallationBlockOf(entry, guard);
            Assert.Equal(TestInstallations.ManagedReason, block);

            var state = CommandRerun.StateOf(entry, block);
            Assert.True(state.Offered, entry.CommandLine);
            Assert.False(state.Enabled, entry.CommandLine);
            Assert.Equal(TestInstallations.ManagedReason, state.Hint);
        }
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public void A_read_can_still_be_run_again_on_a_read_only_installation(string tool, string[] argv)
    {
        var entry = Finished(tool, 0, argv);

        var block = CommandRerun.InstallationBlockOf(entry, ManagedGuard());

        Assert.Null(block);
        var state = CommandRerun.StateOf(entry, block);
        Assert.True(state.Offered);
        Assert.True(state.Enabled);
        Assert.Contains("Nothing runs until you confirm", state.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_writable_installation_holds_nothing_back(string tool, string[] argv)
    {
        var entry = Finished(tool, 0, argv);

        Assert.Null(CommandRerun.InstallationBlockOf(entry, WritableGuard()));
        Assert.True(CommandRerun.StateOf(entry).Enabled);
    }

    [Fact]
    public void An_invalid_installation_is_read_only_too_and_gives_its_own_sentence()
    {
        var guard = new InstallationGuard(TestInstallations.Invalid());

        Assert.False(string.IsNullOrWhiteSpace(guard.BlockedReason));
        Assert.NotEqual(TestInstallations.ManagedReason, guard.BlockedReason);
        Assert.Equal(guard.BlockedReason, CommandRerun.InstallationBlockOf(Finished(Cli, 0, "skill", "block", "--", "pdf-tools"), guard));
        Assert.Null(CommandRerun.InstallationBlockOf(Finished(Cli, 0, "doctor"), guard));
    }

    [Fact]
    public void An_entry_with_no_Rerun_at_all_keeps_the_sentence_that_says_why_whatever_the_installation()
    {
        var guard = ManagedGuard();
        var onStdin = Finished(Cli, 0, "keys", "set", "OPENAI_API_KEY");
        InvocationFactory.UseStdinSecret(onStdin);
        var installer = Finished(Cli, 0, "upgrade");
        InvocationFactory.SurviveShutdown(installer);
        var signature = Finished(@"C:\Tools\cosign.exe", 0, "verify-blob", "--bundle", "x");
        var bare = Finished(Gateway, 0);

        foreach (var entry in new[] { onStdin, installer, signature, bare })
        {
            var state = CommandRerun.StateOf(entry, CommandRerun.InstallationBlockOf(entry, guard));

            Assert.False(state.Offered, entry.CommandLine);
            Assert.False(state.Enabled, entry.CommandLine);
            Assert.NotEqual(TestInstallations.ManagedReason, state.Hint);
        }
    }

    // ------------------------------------------------------------------ the review

    [Fact]
    public void The_review_of_a_change_carries_the_installations_reason_like_every_other_review_and_a_read_is_reviewed_as_ever()
    {
        var managed = Create(Managed());
        var rerun = new CommandRerun(managed.Services, owner: () => null);

        var change = rerun.ReviewFor(Finished(Cli, 0, "skill", "block", "--", "pdf-tools"), CommandTier.StateChanging);
        Assert.True(change.IsBlocked);
        Assert.Equal(TestInstallations.ManagedReason, change.BlockedReason);
        Assert.Single(change.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);
        Assert.Equal(new[] { "skill", "block", "--", "pdf-tools" }, Assert.Single(change.Steps).Argv);

        var destructive = rerun.ReviewFor(Finished(Cli, 0, "skill", "quarantine", "--", "pdf-tools"), CommandTier.Destructive);
        Assert.True(destructive.IsBlocked);
        Assert.True(destructive.IsDestructive);

        // The gateway's restart is also a warning of its own; the installation's bar is one more, not a replacement.
        var restart = rerun.ReviewFor(Finished(Gateway, 0, "restart"));
        Assert.True(restart.IsBlocked);
        Assert.Contains(restart.Warnings, w => w.Title == "Gateway restart");
        Assert.Single(restart.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);

        foreach (var read in new[] { Finished(Cli, 0, "doctor"), Finished(Gateway, 0, "connector", "verify"), Finished(Gateway, 0, "policy", "domains") })
        {
            var review = rerun.ReviewFor(read);
            Assert.False(review.IsBlocked, read.CommandLine);
            Assert.Null(review.BlockedReason);
            Assert.DoesNotContain(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        }
    }

    [Fact]
    public void On_a_writable_installation_no_review_is_blocked()
    {
        var writable = Create();
        var rerun = new CommandRerun(writable.Services, owner: () => null);

        foreach (var argv in new[] { new[] { "skill", "block", "--", "pdf-tools" }, new[] { "skill", "quarantine", "--", "pdf-tools" }, new[] { "doctor" } })
        {
            var review = rerun.ReviewFor(Finished(Cli, 0, argv));
            Assert.False(review.IsBlocked);
            Assert.DoesNotContain(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        }
    }

    // ------------------------------------------------------------------ the run

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Rerun_of_a_change_on_a_read_only_installation_says_the_sentence_and_shows_no_review_runs_nothing_and_records_nothing(string tool, string[] argv)
    {
        var scene = Create(Managed());

        var message = await scene.Rerun.RunAsync(Finished(tool, 0, argv));

        Assert.Equal(TestInstallations.ManagedReason, message);
        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.Ran);
        Assert.Empty(scene.Services.Cli.Activity);
        Assert.False(scene.Services.GatewayAutoStart.UserStopped);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Rerun_of_a_read_on_a_read_only_installation_is_reviewed_without_a_block_and_runs(string tool, string[] argv)
    {
        var scene = Create(Managed());

        var message = await scene.Rerun.RunAsync(Finished(tool, 0, argv));

        var review = Assert.Single(scene.Reviews);
        Assert.False(review.IsBlocked);
        Assert.DoesNotContain(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        var run = Assert.Single(scene.Ran);
        Assert.Equal(tool, run.Tool);
        Assert.Equal(argv, run.Argv);
        Assert.StartsWith("Ran " + tool + " ", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Cli, new[] { "skill", "block", "--", "pdf-tools" })]
    [InlineData(Gateway, new[] { "stop" })]
    public async Task A_review_confirmed_after_the_installation_turned_read_only_is_refused_recorded_and_runs_nothing(string tool, string[] argv)
    {
        var scene = Create();
        scene.Answer.WhileOpen = review =>
        {
            // Built while the installation was writable; config.yaml is edited to managed while the question is on screen.
            Assert.False(review.IsBlocked);
            scene.Services.Installation.Replace(Managed());
        };

        var message = await scene.Rerun.RunAsync(Finished(tool, 0, argv));

        Assert.Equal("Not run. " + TestInstallations.ManagedReason + " Nothing was changed; the refusal is in the Activity list.", message);
        Assert.Empty(scene.Ran);

        // A confirmed stop is taken for the operator's word that the gateway stays down; this one stopped nothing.
        Assert.False(scene.Services.GatewayAutoStart.UserStopped);

        // The refusal is the one Activity entry every refusal is: "refused", no exit code, the command that was not started.
        var recorded = Assert.Single(scene.Services.Cli.Activity);
        Assert.Equal(tool, recorded.Executable);
        Assert.Equal(argv, recorded.Argv);
        Assert.Null(recorded.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix + " — ", recorded.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, recorded.FailureReason, StringComparison.Ordinal);
        Assert.False(CommandRerun.StateOf(recorded).Offered);
    }

    [Fact]
    public async Task A_read_confirmed_after_the_installation_turned_read_only_still_runs()
    {
        var scene = Create();
        scene.Answer.WhileOpen = _ => scene.Services.Installation.Replace(Managed());

        var message = await scene.Rerun.RunAsync(Finished(Cli, 0, "doctor"));

        Assert.StartsWith("Ran defenseclaw doctor again", message, StringComparison.Ordinal);
        Assert.Equal(new[] { "doctor" }, Assert.Single(scene.Ran).Argv);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public void A_re_run_the_runner_itself_declined_is_reported_as_not_run_and_never_as_ran_again()
    {
        var scene = Create(Managed());
        var reason = InstallationGate.RefusalReason(scene.Services.Installation.Context);
        var refused = scene.Services.Cli.RecordRefusal(Cli, new[] { "skill", "block", "--", "pdf-tools" }, reason);

        var text = CommandRerun.Describe(Cli, refused.Argv, refused);

        Assert.StartsWith("Not run. DefenseClaw for Windows runs only read-only commands against this installation. " + TestInstallations.ManagedReason, text, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed; the refusal is in the Activity list.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ran ", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the Activity row

    [Fact]
    public void A_row_whose_entry_would_change_a_read_only_installation_draws_Rerun_off_with_the_installations_sentence()
    {
        var guard = ManagedGuard();
        string? Block(CliInvocation entry) => CommandRerun.InstallationBlockOf(entry, guard);

        var change = new ActivityRow(Finished(Cli, 1, "skill", "block", "--", "pdf-tools"), rerun: Declined, rerunBlock: Block);
        var read = new ActivityRow(Finished(Cli, 0, "doctor"), rerun: Declined, rerunBlock: Block);

        Assert.True(change.CanOfferRerun);
        Assert.False(change.CanRerun);
        Assert.False(change.RerunCommand.CanExecute(null));
        Assert.Equal(TestInstallations.ManagedReason, change.RerunHint);

        Assert.True(read.CanOfferRerun);
        Assert.True(read.CanRerun);
        Assert.True(read.RerunCommand.CanExecute(null));
        Assert.Contains("Nothing runs until you confirm", read.RerunHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_row_says_the_installations_sentence_not_to_wait_and_says_it_once_the_command_has_finished()
    {
        var guard = ManagedGuard();
        var run = InvocationFactory.CreateFor(Cli, new[] { "setup", "guardrail" });
        var row = new ActivityRow(run, rerun: Declined, rerunBlock: entry => CommandRerun.InstallationBlockOf(entry, guard));

        Assert.True(row.CanOfferRerun);
        Assert.False(row.CanRerun);
        Assert.Equal(TestInstallations.ManagedReason, row.RerunHint);

        InvocationFactory.Finish(run, 0);
        row.Tick();

        Assert.False(row.CanRerun);
        Assert.Equal(TestInstallations.ManagedReason, row.RerunHint);
    }

    [Fact]
    public void A_row_built_without_the_installation_is_never_held_back_by_it()
    {
        var row = new ActivityRow(Finished(Cli, 0, "skill", "block", "--", "pdf-tools"), rerun: Declined);

        Assert.True(row.CanRerun);
    }

    [Fact]
    public void The_row_follows_the_installation_when_it_is_asked_again_in_both_directions()
    {
        var scene = Create();
        var row = new ActivityRow(
            Finished(Gateway, 0, "restart"),
            rerun: Declined,
            rerunBlock: entry => CommandRerun.InstallationBlockOf(entry, scene.Services.Installation));
        var changes = 0;
        row.RerunCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.True(row.CanRerun);

        scene.Services.Installation.Replace(Managed());

        // A finished row is not ticked, so it keeps what it was drawn with until it is asked again.
        Assert.True(row.CanRerun);
        row.RefreshRerun();
        Assert.False(row.CanRerun);
        Assert.True(row.CanOfferRerun);
        Assert.Equal(TestInstallations.ManagedReason, row.RerunHint);
        Assert.False(row.RerunCommand.CanExecute(null));
        Assert.True(changes > 0, "the button was not told");

        scene.Services.Installation.Replace(TestInstallations.UserDefault());
        row.RefreshRerun();
        Assert.True(row.CanRerun);
        Assert.True(row.RerunCommand.CanExecute(null));
        Assert.Contains("Nothing runs until you confirm", row.RerunHint, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the palette: Re-run last command, Cancel running command

    [Fact]
    public async Task Re_run_last_follows_its_target_on_a_read_only_installation()
    {
        var scene = Create(Managed());
        var now = DateTimeOffset.UtcNow;
        _ = Entry(scene, Cli, 0, now.AddMinutes(-9), "skill", "list");
        _ = Entry(scene, Cli, 0, now.AddMinutes(-2), "skill", "block", "--", "pdf-tools");

        // The newest command that can be run again is a change: the row is off with the installation's sentence, and Run says it instead of reviewing.
        var row = Row(scene, ShellCommandRegistry.RerunLastId);
        Assert.False(row.IsEnabled);
        Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);

        await scene.Actions.RerunLastAsync();
        Assert.Equal("Re-run last command: " + TestInstallations.ManagedReason, Assert.Single(scene.Toasts));
        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.Ran);

        // A newer read is the target now, and it works.
        _ = Entry(scene, Cli, 0, now.AddMinutes(-1), "doctor");
        row = Row(scene, ShellCommandRegistry.RerunLastId);
        Assert.True(row.IsEnabled);
        Assert.Null(row.DisabledReason);
        Assert.Contains("defenseclaw doctor", row.Description, StringComparison.Ordinal);

        scene.Toasts.Clear();
        await scene.Actions.RerunLastAsync();
        var review = Assert.Single(scene.Reviews);
        Assert.False(review.IsBlocked);
        Assert.Equal(new[] { "doctor" }, Assert.Single(scene.Ran).Argv);
        Assert.StartsWith("Re-run last command: Ran defenseclaw doctor again", Assert.Single(scene.Toasts), StringComparison.Ordinal);
    }

    [Fact]
    public void The_installations_sentence_comes_before_the_one_for_a_target_that_is_still_running_and_before_nothing_to_run()
    {
        var now = DateTimeOffset.UtcNow;

        var writable = Create();
        _ = Entry(writable, Cli, null, now.AddSeconds(-5), "setup", "guardrail");
        Assert.Contains("Wait for this command to finish, or cancel it", Row(writable, ShellCommandRegistry.RerunLastId).DisabledReason, StringComparison.Ordinal);

        var managed = Create(Managed());
        _ = Entry(managed, Cli, null, now.AddSeconds(-5), "setup", "guardrail");
        Assert.Equal(TestInstallations.ManagedReason, Row(managed, ShellCommandRegistry.RerunLastId).DisabledReason);

        // With nothing to run again there is no target for the installation to hold back: the row says that, as it does anywhere.
        var empty = Create(Managed());
        Assert.Equal("There is no command in Activity to run again yet.", Row(empty, ShellCommandRegistry.RerunLastId).DisabledReason);
    }

    [Fact]
    public void Re_run_last_on_a_palette_opened_before_the_installation_turned_read_only_ends_in_the_sentence_not_in_a_review()
    {
        var scene = Create();
        _ = Entry(scene, Gateway, 0, DateTimeOffset.UtcNow.AddMinutes(-2), "restart");
        var row = Row(scene, ShellCommandRegistry.RerunLastId);
        Assert.True(row.IsEnabled);

        // The row was drawn while the installation was writable; Run asks again, and has nothing to await on the way to the toast.
        scene.Services.Installation.Replace(Managed());
        row.Run();

        Assert.Equal("Re-run last command: " + TestInstallations.ManagedReason, Assert.Single(scene.Toasts));
        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.Ran);
        Assert.False(scene.Services.GatewayAutoStart.UserStopped);
    }

    [Fact]
    public void Cancel_running_stays_on_for_a_change_on_a_read_only_installation_because_cancelling_changes_nothing_in_it()
    {
        var scene = Create(Managed());
        var running = Entry(scene, Cli, null, DateTimeOffset.UtcNow.AddMinutes(-1), "setup", "guardrail");

        var row = Row(scene, ShellCommandRegistry.CancelRunningId);

        Assert.True(row.IsEnabled);
        Assert.Null(row.DisabledReason);
        Assert.Contains("defenseclaw setup guardrail", row.Description, StringComparison.Ordinal);

        row.Run();

        // Not the installation's sentence: the runner's own, for an entry that is not one of its runs (this one is synthetic).
        var toast = Assert.Single(scene.Toasts);
        Assert.StartsWith("Cancel running command: ", toast, StringComparison.Ordinal);
        Assert.DoesNotContain(TestInstallations.ManagedReason, toast, StringComparison.Ordinal);
        Assert.False(running.CancelRequested);
    }

    // ------------------------------------------------------------------ the gateway's fixed verbs

    private static readonly string[] ChangingVerbs = { "watchdog start", "watchdog stop", "connector teardown", "policy reload" };

    private static readonly string[] ReadingVerbs = { "watchdog status", "connector verify", "connector list-backups", "policy domains" };

    private static IReadOnlyList<ShellCommand> VerbRows(Scene scene) =>
        ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, scene.Actions);

    public static IEnumerable<object[]> GatewayStates() => new[]
    {
        new object[] { "checking" },
        new object[] { "stopped" },
        new object[] { "running" },
    };

    private static GatewaySnapshot SnapshotFor(string state) => state switch
    {
        "stopped" => GatewayStopped,
        "running" => GatewayRunning,
        _ => new GatewaySnapshot(),
    };

    [Theory]
    [MemberData(nameof(GatewayStates))]
    public void On_a_read_only_installation_the_changing_verbs_are_off_with_the_installations_sentence_whatever_the_gateway_is_doing(string state)
    {
        var scene = Create(Managed());
        scene.Actions.SnapshotSource = () => SnapshotFor(state);
        var rows = VerbRows(scene);

        // policy reload would say "Still checking..." or "The gateway is not running..." on its own; the installation is said first, and alone.
        foreach (var name in ChangingVerbs)
        {
            var row = Assert.Single(rows, r => r.Title == name);
            Assert.False(row.IsEnabled, $"{name} while {state}");
            Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);
        }

        // The reads are not held back by the installation, and are as the gateway's state says: on, none of them needs the sidecar.
        foreach (var name in ReadingVerbs)
        {
            var row = Assert.Single(rows, r => r.Title == name);
            Assert.True(row.IsEnabled, $"{name} while {state}");
            Assert.Null(row.DisabledReason);
        }
    }

    [Theory]
    [InlineData("checking", "Still checking the gateway; try again in a moment.")]
    [InlineData("stopped", "The gateway is not running, so there is no sidecar to reload the policies in.")]
    [InlineData("running", null)]
    public void On_a_writable_installation_the_same_policy_reload_row_gives_the_gateways_own_reason(string state, string? reason)
    {
        var scene = Create();
        scene.Actions.SnapshotSource = () => SnapshotFor(state);
        var rows = VerbRows(scene);

        var reload = Assert.Single(rows, r => r.Title == "policy reload");
        Assert.Equal(reason is null, reload.IsEnabled);
        Assert.Equal(reason, reload.DisabledReason);
        Assert.DoesNotContain(TestInstallations.ManagedReason, reload.DisabledReason ?? string.Empty, StringComparison.Ordinal);

        // And nothing else of the list depends on the gateway's state.
        foreach (var name in ChangingVerbs.Where(n => n != "policy reload").Concat(ReadingVerbs))
        {
            Assert.True(Assert.Single(rows, r => r.Title == name).IsEnabled, $"{name} while {state}");
        }
    }

    [Fact]
    public async Task Running_a_changing_verb_on_a_read_only_installation_says_the_sentence_and_shows_no_review_and_starts_nothing()
    {
        var scene = Create(Managed());
        scene.Answer.Confirm = true;

        foreach (var name in ChangingVerbs)
        {
            scene.Toasts.Clear();
            var command = CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find(name)!);

            await scene.Actions.RunCuratedAsync(command);

            Assert.Equal(command.Title + ": " + TestInstallations.ManagedReason, Assert.Single(scene.Toasts));
        }

        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_reading_verb_on_a_read_only_installation_goes_to_the_runner_which_lets_it_through()
    {
        var scene = Create(Managed());

        foreach (var name in ReadingVerbs)
        {
            scene.Toasts.Clear();
            await scene.Actions.RunCuratedAsync(CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find(name)!));

            // The isolated runner has no CLI to find: that, and not a refusal, is what stops it.
            var toast = Assert.Single(scene.Toasts);
            Assert.Contains("Could not run it", toast, StringComparison.Ordinal);
            Assert.DoesNotContain(TestInstallations.ManagedReason, toast, StringComparison.Ordinal);
        }

        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ the recents

    private static readonly string[] Recents =
    {
        "cli.setup.splunk.dashboards.apply",
        "cli.doctor",
        "cli.setup.guardrail",
        "cli.policy.reload",
        "cli.watchdog.status",
    };

    private AppSettingsStore RecentsStore()
    {
        var store = AppSettingsStore.OpenFresh(_temp.File("palette-recents.json"));
        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = Recents } });
        return store;
    }

    private static CommandPaletteViewModel Open(Scene scene, AppSettingsStore store)
    {
        var palette = new CommandPaletteViewModel { RecentsStore = store };
        palette.Load(Palette(scene));
        return palette;
    }

    [Fact]
    public async Task A_recent_command_the_installation_now_blocks_stays_among_the_recents_and_says_why_beside_the_ones_that_still_work()
    {
        var scene = Create(Managed(), new FixedTerraformProbe(TerraformReady));
        scene.Actions.SnapshotSource = () => GatewayStopped;
        await scene.Services.Terraform.RefreshAsync();
        var store = RecentsStore();

        var palette = Open(scene, store);

        // The five lead, in the order they were run, each marked recent; the rest follow in the registry's order.
        Assert.Equal(Recents, palette.Results.Take(5).Select(r => r.Command.Id));
        Assert.All(palette.Results.Take(5), r => Assert.True(r.IsRecent, r.Command.Id));
        Assert.All(palette.Results.Skip(5), r => Assert.False(r.IsRecent, r.Command.Id));

        // The change among them, the Splunk dashboards' apply included, is off with the installation's sentence in place of its description - not
        // Terraform's, and not the gateway's - and says so to a screen reader. The reads are on.
        foreach (var id in new[] { "cli.setup.splunk.dashboards.apply", "cli.setup.guardrail", "cli.policy.reload" })
        {
            var item = Assert.Single(palette.Results, r => r.Command.Id == id);
            Assert.False(item.IsEnabled, id);
            Assert.Equal(TestInstallations.ManagedReason, item.Command.DisabledReason);
            Assert.Equal(TestInstallations.ManagedReason, item.DetailLine);
            Assert.EndsWith(", recent, unavailable. " + TestInstallations.ManagedReason, item.AutomationName, StringComparison.Ordinal);
        }

        foreach (var id in new[] { "cli.doctor", "cli.watchdog.status" })
        {
            var item = Assert.Single(palette.Results, r => r.Command.Id == id);
            Assert.True(item.IsEnabled, id);
            Assert.Equal(item.Command.Description, item.DetailLine);
            Assert.EndsWith(", recent", item.AutomationName, StringComparison.Ordinal);
        }

        // Enter and the arrow keys never land on a row that is off: the first recent that works is selected, and the next one is the one after it.
        Assert.Equal("cli.doctor", palette.Selected!.Command.Id);
        palette.MoveSelection(1);
        Assert.Equal("cli.watchdog.status", palette.Selected!.Command.Id);

        // A row that is off cannot be chosen, and choosing around it leaves the list as it was.
        Assert.False(palette.Choose(palette.Results[0]));
        Assert.Equal(Recents, store.Current.Palette.RecentCommandIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_recents_on_a_writable_installation_give_terraforms_and_the_gateways_reasons_never_the_installations(bool terraformThere)
    {
        var scene = Create(terraform: new FixedTerraformProbe(terraformThere ? TerraformReady : NoTerraform));
        scene.Actions.SnapshotSource = () => GatewayStopped;
        await scene.Services.Terraform.RefreshAsync();

        var palette = Open(scene, RecentsStore());

        Assert.Equal(Recents, palette.Results.Take(5).Select(r => r.Command.Id));
        var apply = palette.Results[0];
        Assert.Equal(terraformThere, apply.IsEnabled);
        if (!terraformThere)
        {
            Assert.StartsWith(NoTerraform.Summary, apply.DetailLine, StringComparison.Ordinal);
        }

        var reload = Assert.Single(palette.Results, r => r.Command.Id == "cli.policy.reload");
        Assert.False(reload.IsEnabled);
        Assert.Equal("The gateway is not running, so there is no sidecar to reload the policies in.", reload.DetailLine);

        Assert.All(palette.Results.Take(5), r => Assert.DoesNotContain(TestInstallations.ManagedReason, r.DetailLine, StringComparison.Ordinal));
        Assert.True(Assert.Single(palette.Results, r => r.Command.Id == "cli.setup.guardrail").IsEnabled);
    }

    [Fact]
    public async Task The_recents_are_only_ids_so_a_command_comes_back_on_when_the_installation_is_writable_again()
    {
        var scene = Create(Managed(), new FixedTerraformProbe(TerraformReady));
        scene.Actions.SnapshotSource = () => GatewayRunning;
        await scene.Services.Terraform.RefreshAsync();
        var store = RecentsStore();

        Assert.False(Open(scene, store).Results[0].IsEnabled);

        scene.Services.Installation.Replace(TestInstallations.UserDefault());
        var again = Open(scene, store);

        Assert.Equal(Recents, again.Results.Take(5).Select(r => r.Command.Id));
        Assert.All(again.Results.Take(5), r => Assert.True(r.IsEnabled, r.Command.Id));
        Assert.Equal("cli.setup.splunk.dashboards.apply", again.Selected!.Command.Id);
    }
}
