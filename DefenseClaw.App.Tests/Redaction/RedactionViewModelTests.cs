using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Redaction;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Redaction;

/// <summary>
/// The redaction window's view-model (CUST-295) against a fake <c>defenseclaw</c> that answers from the synthetic fixtures made from the
/// Docker capture of source commit 95159fd. No process starts: a read or a preview goes through <see cref="RedactionViewModel.RunCli"/>, a
/// confirmed apply through the review's <c>RunStep</c> seam, and the tests say which of the two a command arrived by.
/// </summary>
public sealed class RedactionViewModelTests : IDisposable
{
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

    private sealed class Harness
    {
        public required RedactionViewModel Vm { get; init; }

        public required FakeRedactionCli Cli { get; init; }
    }

    /// <summary>A window over a fake CLI. Not yet read: call <see cref="RedactionViewModel.RefreshAsync"/> (as the window does when it opens).</summary>
    private Harness Create(Action<FakeRedactionCli>? script = null, bool supported = true)
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);

        var cli = new FakeRedactionCli();
        script?.Invoke(cli);
        var vm = new RedactionViewModel(services)
        {
            RunCli = cli.Run,
            Gate = () => supported ? GateDecision.Open : GateDecision.Closed,
        };
        vm.Review.RunStep = cli.Step;
        return new Harness { Vm = vm, Cli = cli };
    }

    /// <summary>The mixed-profile status with ordered routes on a configured destination: what the route operations need.</summary>
    private static void WithRoutes(FakeRedactionCli cli)
    {
        cli.Status = FakeRedactionCli.Fixture("status-mixed.json");
        cli.ProfileList = FakeRedactionCli.Fixture("profile-list-custom.json");
        cli.Routes["example-otlp"] = FakeRedactionCli.Fixture("route-list-two.json");
    }

    private async Task<Harness> OpenAsync(Action<FakeRedactionCli>? script = null)
    {
        var harness = Create(script);
        await harness.Vm.RefreshAsync();
        return harness;
    }

    private static string Line(string[] argv) => string.Join(' ', argv);

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task Opening_reads_the_status_the_profiles_and_the_routes_of_the_destinations_that_have_them()
    {
        var h = await OpenAsync(WithRoutes);
        var vm = h.Vm;

        Assert.Equal(
            ["setup redaction status --json", "setup redaction profile list --json", "setup redaction route list example-otlp --json"],
            h.Cli.Ran.Select(Line).ToArray());
        Assert.All(h.Cli.Ran, argv => Assert.True(RedactionArgv.IsRead(argv)));

        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsCurrent);
        Assert.False(vm.HasStatusError);
        Assert.StartsWith("as of ", vm.AsOf, StringComparison.Ordinal);
        Assert.Equal(14, vm.BucketRows.Count);
        Assert.Equal(["local-sqlite", "example-otlp", "managed-enterprise-ai-defense"], vm.DestinationRows.Select(r => r.Name).ToArray());

        var routed = vm.DestinationRows.Single(r => r.Name == "example-otlp");
        Assert.Equal(["example-route", "example-strict-logs"], routed.Routes.Select(r => r.Name).ToArray());
        Assert.False(vm.DestinationRows.Single(r => r.Name == "local-sqlite").HasRoutes);
        Assert.True(vm.DestinationRows.Single(r => r.Name == "managed-enterprise-ai-defense").HasLock);

        Assert.Equal("Mixed profiles: none on 1, sensitive on 12, content on 1 of 14 buckets.", vm.Summary);
        Assert.Equal("Warn", vm.SummaryTone);
        Assert.Equal("Redaction: mixed", vm.StatusChipText);
        Assert.StartsWith("Plan ", vm.PlanText, StringComparison.Ordinal);
        Assert.Contains("judge", vm.JudgeNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, vm.WarningRows.Count);
    }

    [Fact]
    public async Task A_fresh_install_reads_with_one_command_and_starts_the_quick_sheet_on_the_profile_in_force()
    {
        var h = await OpenAsync();

        // one destination, no routes to list: status and profile list only
        Assert.Equal(["setup redaction status --json", "setup redaction profile list --json"], h.Cli.Ran.Select(Line).ToArray());
        Assert.Equal("none", h.Vm.QuickProfile); // the Mac's rule: the default in force when it is one of the four
        Assert.True(h.Vm.IsQuickNone);
        Assert.Equal("Redaction is off: every bucket is sent as recorded.", h.Vm.Summary);
    }

    [Fact]
    public async Task A_mixed_policy_starts_the_quick_sheet_on_sensitive_and_a_later_read_leaves_the_choice_alone()
    {
        var h = await OpenAsync(WithRoutes);
        Assert.Equal("sensitive", h.Vm.QuickProfile);

        h.Vm.QuickProfile = "strict";
        await h.Vm.RefreshAsync();
        Assert.Equal("strict", h.Vm.QuickProfile);
    }

    [Fact]
    public async Task A_runtime_without_the_editor_runs_nothing_and_says_why()
    {
        var h = Create(supported: false);
        var vm = h.Vm;

        await vm.RefreshAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PreviewQuickCommand.ExecuteAsync(null);
        await vm.ApplyQuickCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(h.Cli.Ran);
        Assert.Empty(h.Cli.Applied);
        Assert.False(vm.IsSupported);
        Assert.True(vm.IsUnsupported);
        Assert.False(vm.CanAct);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, vm.UnsupportedMessage);
        Assert.False(vm.HasStatus);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task A_status_that_cannot_be_read_is_an_error_and_turns_changes_off_but_leaves_previews_on()
    {
        var h = Create(cli => cli.ExitCode = 1);
        var vm = h.Vm;

        await vm.RefreshAsync();

        Assert.False(vm.HasStatus);
        Assert.False(vm.StatusIsCurrent);
        Assert.True(vm.HasStatusError);
        Assert.Contains("could not be read", vm.StatusError, StringComparison.Ordinal);
        Assert.Contains("exited 1", vm.StatusError, StringComparison.Ordinal);
        Assert.Equal(["setup redaction status --json"], h.Cli.Ran.Select(Line).ToArray()); // the rest is not attempted without a status

        Assert.False(vm.CanQuickApply);
        Assert.True(vm.CanQuickPreview);
        Assert.Contains("Changes are off", vm.ChangesBlockedReason, StringComparison.Ordinal);

        // a read that works again brings the changes back
        h.Cli.ExitCode = 0;
        await vm.RefreshAsync();
        Assert.True(vm.StatusIsCurrent);
        Assert.True(vm.CanQuickApply);
        Assert.Equal(string.Empty, vm.ChangesBlockedReason);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_rows_it_had_and_turns_changes_off()
    {
        var h = await OpenAsync();
        Assert.True(h.Vm.CanQuickApply);

        h.Cli.ExitCode = 1;
        await h.Vm.RefreshAsync();

        Assert.True(h.Vm.HasStatus); // the older read stays on screen
        Assert.Equal(14, h.Vm.BucketRows.Count);
        Assert.True(h.Vm.HasStatusError);
        Assert.False(h.Vm.StatusIsCurrent);
        Assert.False(h.Vm.CanQuickApply);
    }

    [Fact]
    public async Task A_status_that_is_not_json_is_an_error_with_a_reason()
    {
        var h = Create(cli => cli.Status = "this is not json");

        await h.Vm.RefreshAsync();

        Assert.True(h.Vm.HasStatusError);
        Assert.False(h.Vm.StatusIsCurrent);
        Assert.Contains("could not be read:", h.Vm.StatusError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ a preview can neither write nor restart

    private static readonly Dictionary<RedactionOperation, Action<RedactionFormViewModel>> Recipes = new()
    {
        [RedactionOperation.RemoveAll] = static _ => { },
        [RedactionOperation.ApplyEverywhere] = static f => f.Profile = "sensitive",
        [RedactionOperation.ApplyDefaults] = static f => f.Profile = "strict",
        [RedactionOperation.DefaultsSet] = static f => { f.Profile = "sensitive"; f.CollectLogs = "on"; },
        [RedactionOperation.DefaultsReset] = static _ => { },
        [RedactionOperation.BucketSet] = static f => { f.Bucket = "model.io"; f.Profile = "content"; f.CollectMetrics = "off"; },
        [RedactionOperation.BucketReset] = static f => f.Bucket = "model.io",
        [RedactionOperation.ProfileSet] = static f => { f.ProfileName = "example-profile"; f.Extends = "sensitive"; f.DetectorChecks[0].IsChecked = true; f.FieldRows.Single(r => r.Class == "path").Mode = "hash"; },
        [RedactionOperation.ProfileRemove] = static f => f.ProfileName = "example-profile",
        [RedactionOperation.DestinationSend] = static f => { f.Destination = "example-otlp"; f.SignalChecks[0].IsChecked = true; f.BucketChecks[1].IsChecked = true; f.Profile = "sensitive"; },
        [RedactionOperation.DestinationInherit] = static f => f.Destination = "example-otlp",
        [RedactionOperation.RouteAdd] = static f => { f.Destination = "example-otlp"; f.RouteName = "quiet-tools"; f.SignalChecks[0].IsChecked = true; f.BucketChecks[5].IsChecked = true; f.RouteAction = "drop"; f.MinSeverity = "HIGH"; },
        [RedactionOperation.RouteSet] = static f => { f.Destination = "example-otlp"; f.RouteName = "example-route"; },
        [RedactionOperation.RouteMove] = static f => { f.Destination = "example-otlp"; f.RouteName = "example-route"; f.PositionText = "2"; },
        [RedactionOperation.RouteRemove] = static f => { f.Destination = "example-otlp"; f.RouteName = "example-route"; },
    };

    public static TheoryData<string> Changes()
    {
        var data = new TheoryData<string>();
        foreach (var info in RedactionOperations.Mutations)
        {
            data.Add(info.Operation.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task The_primary_button_previews_by_default_and_a_preview_never_carries_a_restart_flag(string operation)
    {
        var op = Enum.Parse<RedactionOperation>(operation);
        var h = await OpenAsync(WithRoutes);
        var vm = h.Vm;
        var readsBefore = h.Cli.Ran.Count;

        vm.SelectedOperation = RedactionOperations.Info(op);
        Recipes[op](vm.Form);
        Assert.True(vm.DryRun, "dry run is the default");
        Assert.False(vm.HasProblem, vm.Problem);
        Assert.Equal("Preview", vm.PrimaryLabel);
        Assert.True(vm.CanRun);

        await vm.RunCommand.ExecuteAsync(null);

        var ran = Assert.Single(h.Cli.Ran.Skip(readsBefore));
        Assert.True(RedactionArgv.IsPreview(ran), Line(ran));
        Assert.Equal(op, RedactionArgv.Identify(ran));
        Assert.Equal("--dry-run", ran[^1]);
        Assert.DoesNotContain("--yes", ran);
        Assert.All(ran, t => Assert.False(RedactionArgv.IsRestartFlag(t), t));
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(ran));
        Assert.False(CommandReview.RestartsGatewayFor(ran));

        Assert.Empty(h.Cli.Applied);
        Assert.False(vm.Review.IsOpen);
        var outcome = vm.Outcome!;
        Assert.Equal(RedactionOutcomeKind.Preview, outcome.Kind);
        Assert.Equal("Dry run: nothing written", outcome.Badge);
        Assert.Contains("--dry-run", outcome.Command, StringComparison.Ordinal);
        Assert.Contains("Nothing was written and the gateway was not restarted.", outcome.Notes);
    }

    [Fact]
    public async Task Even_with_restart_ticked_a_preview_does_not_carry_it()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyEverywhere);
        vm.Form.Profile = "strict";

        // the box is not live while previewing, and turning dry run on clears it
        Assert.False(vm.RestartEnabled);
        vm.DryRun = false;
        Assert.True(vm.RestartEnabled);
        vm.Restart = true;
        vm.DryRun = true;
        Assert.False(vm.Restart);
        Assert.False(vm.RestartEnabled);

        // the quick sheet's own box does not reach a preview either
        vm.QuickRestart = true;
        vm.QuickProfile = "strict";
        await vm.PreviewQuickCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        var previews = h.Cli.Ran.Where(RedactionArgv.IsPreview).ToArray();
        Assert.Equal(2, previews.Length);
        Assert.All(previews, p => Assert.DoesNotContain(p, RedactionArgv.IsRestartFlag));
        Assert.Empty(h.Cli.Applied);
    }

    [Fact]
    public async Task The_door_for_previews_and_the_door_for_reads_refuse_everything_else()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        var sent = h.Cli.Ran.Count;

        // Both doors refuse before anything is started, so the refusal is synchronous.
        static void Refused(Func<Task<CliInvocation>> door) =>
            Assert.IsType<InvalidOperationException>(Record.Exception(() => { _ = door(); }));

        var apply = RedactionArgv.Apply(RedactionOperation.RemoveAll, new(), restart: true);
        Refused(() => vm.RunPreviewAsync(apply));
        Refused(() => vm.RunReadAsync(apply));
        Refused(() => vm.RunReadAsync(RedactionArgv.Preview(RedactionOperation.RemoveAll, new())));
        Refused(() => vm.RunPreviewAsync(RedactionArgv.Read(RedactionOperation.Status, new())));
        Refused(() => vm.RunPreviewAsync(["setup", "redaction", "remove-all", "--yes", "--json", "--dry-run"]));
        Refused(() => vm.RunPreviewAsync(["setup", "redaction", "remove-all", "--json", "--dry-run", "--restart"]));
        Refused(() => vm.RunReadAsync(["gateway", "restart"]));
        Assert.Equal(sent, h.Cli.Ran.Count); // none of them reached the runner

        // and the doors do open for the commands they are for
        _ = await vm.RunPreviewAsync(RedactionArgv.Preview(RedactionOperation.RemoveAll, new()));
        _ = await vm.RunReadAsync(RedactionArgv.Read(RedactionOperation.Status, new()));
        Assert.Equal(sent + 2, h.Cli.Ran.Count);
    }

    [Fact]
    public async Task The_command_under_the_form_is_the_one_the_button_runs()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketSet);

        Assert.True(vm.HasProblem);
        Assert.Equal("Choose a bucket.", vm.Problem);
        Assert.Equal(string.Empty, vm.CommandText);
        Assert.False(vm.CanRun);

        vm.Form.Bucket = "model.io";
        vm.Form.Profile = "content";
        vm.Form.CollectMetrics = "off";
        Assert.Equal("defenseclaw setup redaction bucket set model.io --profile content --no-metrics --json --dry-run", vm.CommandText);

        vm.DryRun = false;
        vm.Restart = true;
        Assert.Equal("Review and apply…", vm.PrimaryLabel);
        Assert.Equal("defenseclaw setup redaction bucket set model.io --profile content --no-metrics --yes --json --restart", vm.CommandText);

        vm.Restart = false;
        Assert.EndsWith("--yes --json --no-restart", vm.CommandText, StringComparison.Ordinal);

        // another operation starts from a clean form, dry run on again
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketReset);
        Assert.True(vm.DryRun);
        Assert.False(vm.Restart);
        Assert.Equal("Choose a bucket.", vm.Problem);
    }

    // ------------------------------------------------------------------ the readable diff

    [Fact]
    public async Task A_preview_is_shown_as_rows_with_the_difference_in_words()
    {
        var h = await OpenAsync();
        await h.Vm.PreviewQuickCommand.ExecuteAsync(null); // "none" is the profile in force: the fake still answers the capture's dry run

        var outcome = h.Vm.Outcome!;
        Assert.Equal("Preview: Apply profile everywhere", outcome.Heading);
        Assert.Equal("14 delivery legs would change.", outcome.Headline);
        Assert.Equal("14 start redacting", outcome.Breakdown);
        var row = Assert.Single(outcome.Diff);
        Assert.Equal("local-sqlite / all-collected-logs-and-mandatory-floor", row.Target);
        Assert.Equal(("none", "sensitive", "all 14 buckets", "starts redacting", "Ok"), (row.Before, row.After, row.BucketsText, row.KindText, row.Tone));
        Assert.Contains("model.io", row.BucketsDetail, StringComparison.Ordinal);
        Assert.Contains("local-sqlite", row.AutomationName, StringComparison.Ordinal);
        Assert.True(outcome.CanApply);
        Assert.Equal("Neutral", outcome.Tone);
    }

    [Fact]
    public async Task A_preview_that_would_start_raw_delivery_is_red_and_says_so_in_the_headline()
    {
        var h = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-route-remove.json"));
        await h.Vm.PreviewQuickCommand.ExecuteAsync(null);

        var outcome = h.Vm.Outcome!;
        Assert.Equal("Bad", outcome.Tone);
        Assert.Contains("28 delivery legs would carry raw, unredacted content.", outcome.Headline, StringComparison.Ordinal);
        Assert.All(outcome.Diff, r => Assert.Equal("Bad", r.Tone));
        Assert.Equal(["logs", "traces"], outcome.Diff.Select(r => r.Signal).ToArray());
    }

    [Fact]
    public async Task A_preview_of_a_change_that_moves_no_leg_says_so_and_a_no_op_is_not_offered_for_apply()
    {
        var h = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-defaults-reset.json"));
        await h.Vm.PreviewQuickCommand.ExecuteAsync(null);

        Assert.Equal("Nothing to change: the configuration already says this.", h.Vm.Outcome!.Headline);
        Assert.False(h.Vm.Outcome.CanApply);
        Assert.False(h.Vm.CanApplyPreview);
        Assert.False(h.Vm.Outcome.HasDiff);

        var rewrite = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-remove-all.json"));
        await rewrite.Vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.Contains("The configuration file would change, but no delivery leg would be redacted differently.", rewrite.Vm.Outcome!.Headline, StringComparison.Ordinal);
        Assert.Contains(rewrite.Vm.Outcome.Notes, n => n.Contains("only the file would be rewritten", StringComparison.Ordinal));
        Assert.True(rewrite.Vm.Outcome.CanApply);
    }

    [Fact]
    public async Task A_preview_that_fails_or_cannot_be_read_is_a_failure_and_offers_nothing()
    {
        var failing = await OpenAsync();
        failing.Cli.ExitCode = 1;
        await failing.Vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.Equal(RedactionOutcomeKind.Failed, failing.Vm.Outcome!.Kind);
        Assert.Equal("Bad", failing.Vm.Outcome.Tone);
        Assert.Contains("exited 1", failing.Vm.Outcome.Headline, StringComparison.Ordinal);
        Assert.False(failing.Vm.Outcome.CanApply);

        var garbled = await OpenAsync(cli => cli.PreviewAnswer = _ => "Redaction policy preview\n  Effective legs changed: 3");
        await garbled.Vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.Equal(RedactionOutcomeKind.Failed, garbled.Vm.Outcome!.Kind);
        Assert.Contains("could not be read", garbled.Vm.Outcome.Headline, StringComparison.Ordinal);
        Assert.Contains("Effective legs changed", garbled.Vm.Outcome.Raw, StringComparison.Ordinal);
        Assert.True(garbled.Vm.ShowOutcomeRaw); // a failure shows what the CLI printed without being asked
    }

    // ------------------------------------------------------------------ applying

    [Fact]
    public async Task Applying_previews_afresh_then_opens_the_review_and_runs_nothing_until_it_is_confirmed()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.QuickProfile = "sensitive";

        await vm.ApplyQuickCommand.ExecuteAsync(null);

        // a preview ran, and only a preview
        Assert.Contains(h.Cli.Ran, RedactionArgv.IsPreview);
        Assert.Empty(h.Cli.Applied);

        // the review carries the exact apply command, with the restart spelled out as not happening
        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal(
            ["setup", "redaction", "apply", "--scope", "all-configurable", "--profile", "sensitive", "--yes", "--json", "--no-restart"],
            step.Argv.ToArray());
        Assert.Equal("Apply the sensitive profile everywhere?", review.Title);
        Assert.False(review.RestartsGateway);
        Assert.False(CommandReview.RestartsGatewayFor(step.Argv));
        Assert.Contains(review.Warnings, w => w.Title == "Gateway not restarted" && w.Message.Contains("--no-restart", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway restart");
        Assert.Contains("14 delivery legs would change", review.Summary, StringComparison.Ordinal);
        Assert.Contains("A copy of config.yaml is saved", review.Summary, StringComparison.Ordinal);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.False(vm.Review.RequiresAcknowledgement); // nothing starts unredacted
        Assert.False(vm.CanAct); // a review is waiting: nothing else starts

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var applied = Assert.Single(h.Cli.Applied);
        Assert.Equal(step.Argv.ToArray(), applied);
        Assert.Equal(RedactionOutcomeKind.Applied, vm.Outcome!.Kind);
        Assert.Equal("Applied and verified", vm.Outcome.Badge);
        Assert.Equal("Ok", vm.Outcome.Tone);
        Assert.Contains(vm.Outcome.Notes, n => n.StartsWith("A copy of config.yaml was saved first: ", StringComparison.Ordinal));
        Assert.Contains(vm.Outcome.Notes, n => n.Contains("matches", StringComparison.Ordinal));
        Assert.Contains(vm.Outcome.Notes, n => n.Contains("was not restarted", StringComparison.Ordinal));

        // the policy is read again, and the form is back to a preview
        Assert.Equal(2, h.Cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status));
        Assert.True(vm.DryRun);
        Assert.True(vm.CanAct || vm.Review.IsOpen); // the finished review is still showing its result until it is closed
    }

    [Fact]
    public async Task A_restart_is_named_in_the_command_in_the_review_and_in_a_warning()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.QuickProfile = "strict";
        vm.QuickRestart = true;

        await vm.ApplyQuickCommand.ExecuteAsync(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("--restart", review.Steps[0].Argv[^1]);
        Assert.True(review.RestartsGateway);
        Assert.True(CommandReview.RestartsGatewayFor(review.Steps[0].Argv));
        Assert.Contains(review.Warnings, w => w.Title == "Gateway restart" && w.Message.Contains(CommandReview.RestartSentence, StringComparison.Ordinal));
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway not restarted");

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.EndsWith("--restart", Line(Assert.Single(h.Cli.Applied)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_quick_sheet_previews_before_it_offers_anything_to_apply()
    {
        // The Mac's simple sheet applies at once; here the same press runs a preview first and stops at the review.
        var h = await OpenAsync();
        h.Vm.QuickProfile = "content";
        var readsBefore = h.Cli.Ran.Count;

        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);

        var after = h.Cli.Ran.Skip(readsBefore).ToArray();
        var preview = Assert.Single(after);
        Assert.True(RedactionArgv.IsPreview(preview));
        Assert.Equal("setup redaction apply --scope all-configurable --profile content --json --dry-run", Line(preview));
        Assert.True(h.Vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
        Assert.Equal(RedactionOutcomeKind.Preview, h.Vm.Outcome!.Kind); // what the review is about is on screen behind it
    }

    [Fact]
    public async Task Every_time_the_window_opens_or_resets_the_editor_is_back_to_a_preview()
    {
        var h = await OpenAsync();
        var vm = h.Vm;

        // a new window starts in the safe state
        Assert.True(vm.DryRun);
        Assert.False(vm.Restart);
        Assert.False(vm.QuickRestart);
        Assert.False(vm.QuickArmed);

        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyEverywhere);
        vm.Form.Profile = "strict";
        vm.DryRun = false;
        vm.Restart = true;
        vm.QuickRestart = true;
        vm.QuickProfile = "none";
        await vm.ApplyQuickCommand.ExecuteAsync(null);
        Assert.True(vm.QuickArmed);
        var statusReads = h.Cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status);

        // opened again: nothing decided earlier carries over, and the policy is read afresh
        await vm.ReopenAsync();

        Assert.True(vm.DryRun);
        Assert.False(vm.Restart);
        Assert.False(vm.RestartEnabled);
        Assert.False(vm.QuickRestart);
        Assert.False(vm.QuickArmed);
        Assert.Equal("Preview", vm.PrimaryLabel);
        Assert.Equal(statusReads + 1, h.Cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status));

        // choosing another operation resets the editor too, and leaves the quick sheet alone
        vm.QuickRestart = true;
        vm.DryRun = false;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketReset);
        Assert.True(vm.DryRun);
        Assert.True(vm.QuickRestart);
    }

    [Fact]
    public async Task Cancelling_the_review_runs_nothing_and_puts_dry_run_back_on()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyDefaults);
        vm.Form.Profile = "strict";
        vm.DryRun = false;

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.Review.IsOpen);
        Assert.Equal("Make strict the default profile?", vm.Review.CommandReview!.Title);

        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
        Assert.True(vm.DryRun);
        Assert.False(vm.HandleEscape()); // nothing left to close
    }

    [Fact]
    public async Task Escape_closes_the_open_review_first()
    {
        var h = await OpenAsync();
        h.Vm.QuickProfile = "strict";
        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);
        Assert.True(h.Vm.Review.IsOpen);

        Assert.True(h.Vm.HandleEscape());
        Assert.False(h.Vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
    }

    [Fact]
    public async Task Raw_content_that_would_start_to_flow_needs_a_tick_and_a_destructive_review()
    {
        var h = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-route-remove.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyEverywhere);
        vm.Form.Profile = "strict";
        vm.DryRun = false;

        await vm.RunCommand.ExecuteAsync(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal(CommandTier.Destructive, review.Tier);
        Assert.True(review.IsDestructive);
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Equal(RedactionViewModel.RawAcknowledgement, vm.Review.AcknowledgementText);
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
        var warning = Assert.Single(review.Warnings, w => w.Title == "Raw content will flow");
        Assert.Contains("28 delivery legs will carry raw, unredacted content", warning.Message, StringComparison.Ordinal);
        Assert.Contains("example-otlp / capability-default", warning.Message, StringComparison.Ordinal);

        await vm.Review.ConfirmCommand.ExecuteAsync(null); // no tick: refused
        Assert.Empty(h.Cli.Applied);

        vm.Review.IsAcknowledged = true;
        Assert.True(vm.Review.ConfirmCommand.CanExecute(null));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(h.Cli.Applied);
    }

    [Fact]
    public async Task The_profile_none_needs_two_presses_before_it_gets_as_far_as_the_review()
    {
        var h = await OpenAsync(cli =>
        {
            cli.Status = FakeRedactionCli.Fixture("status-mixed.json"); // not uniform: the sheet starts on sensitive
            cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-route-remove.json");
        });
        var vm = h.Vm;
        var ranBefore = h.Cli.Ran.Count;
        vm.QuickProfile = "none";
        Assert.True(vm.IsQuickNone);
        Assert.Contains("stops redacting", vm.QuickNoneWarning, StringComparison.Ordinal);
        Assert.Equal("Apply…", vm.QuickApplyLabel);

        await vm.ApplyQuickCommand.ExecuteAsync(null);

        Assert.True(vm.QuickArmed);
        Assert.Equal("Apply: press again", vm.QuickApplyLabel);
        Assert.Equal(ranBefore, h.Cli.Ran.Count); // nothing ran, not even a preview
        Assert.False(vm.Review.IsOpen);

        // choosing another profile disarms it
        vm.QuickProfile = "strict";
        Assert.False(vm.QuickArmed);
        vm.QuickProfile = "none";
        await vm.ApplyQuickCommand.ExecuteAsync(null);
        Assert.True(vm.QuickArmed);

        await vm.ApplyQuickCommand.ExecuteAsync(null);

        Assert.False(vm.QuickArmed);
        Assert.True(vm.Review.IsOpen);
        Assert.Equal("Apply the none profile everywhere?", vm.Review.CommandReview!.Title);
        Assert.Equal(CommandTier.Destructive, vm.Review.CommandReview.Tier);
        Assert.Empty(h.Cli.Applied);
    }

    [Fact]
    public async Task Choosing_none_needs_a_tick_even_when_no_leg_would_change_today()
    {
        // dry-remove-all: the file would change and no leg would move, so nothing "starts to flow" yet; the profile is still no redaction
        var h = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-remove-all.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyDefaults);
        vm.Form.Profile = "none";
        vm.DryRun = false;

        await vm.RunCommand.ExecuteAsync(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal(CommandTier.Destructive, review.Tier);
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Equal(RedactionViewModel.NoneAcknowledgement, vm.Review.AcknowledgementText);
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
        Assert.Contains(review.Warnings, w => w.Title == "Profile none: no redaction");
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Raw content will flow");

        vm.Review.IsAcknowledged = true;
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["setup", "redaction", "apply", "--scope", "defaults", "--profile", "none", "--yes", "--json", "--no-restart"], Assert.Single(h.Cli.Applied));
    }

    [Fact]
    public async Task A_destination_named_with_a_control_character_is_shown_spelled_out_and_never_put_on_a_command_line()
    {
        // a right-to-left override in a destination's name: the config is the operator's, but a name is still just text
        var hostile = "ex‮ample";
        var h = await OpenAsync(cli =>
        {
            cli.Status = FakeRedactionCli.Fixture("status-mixed.json").Replace("example-otlp", "ex\\u202eample", StringComparison.Ordinal);
            cli.Routes[hostile] = FakeRedactionCli.Fixture("route-list-two.json");
        });
        var vm = h.Vm;

        // the policy still reads: the card says its routes were not, and nothing with that name was handed to the CLI
        Assert.True(vm.StatusIsCurrent);
        var row = vm.DestinationRows.Single(r => r.Name.StartsWith("ex", StringComparison.Ordinal) && !r.Name.StartsWith("example-", StringComparison.Ordinal));
        Assert.Equal("ex\\u202Eample", row.Name);
        Assert.DoesNotContain('‮', row.AutomationName);
        Assert.False(row.HasRoutes);
        Assert.True(row.HasRoutesNote);
        Assert.Contains("cannot be passed to the CLI safely", row.RoutesNote, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Cli.Ran, argv => argv.Any(token => token.Contains('‮', StringComparison.Ordinal)));

        // the box shows the escaped name; the value is the real one, and the form refuses to build a command from it
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationInherit);
        var choice = Assert.Single(vm.Form.DestinationChoices);
        Assert.Equal(hostile, choice.Value);
        Assert.Equal("ex\\u202Eample", choice.Label);
        Assert.Equal(hostile, vm.Form.Destination); // the only one on offer is chosen for you
        Assert.Equal("That destination's name cannot be passed to the CLI safely.", vm.Problem);
        Assert.False(vm.CanRun);
        Assert.Equal(string.Empty, vm.CommandText);
    }

    [Fact]
    public async Task A_route_named_with_a_control_character_is_shown_spelled_out_and_cannot_be_chosen_for_a_command()
    {
        var hostile = "r‮oute";
        var h = await OpenAsync(cli =>
        {
            WithRoutes(cli);
            cli.Routes["example-otlp"] = FakeRedactionCli.Fixture("route-list-two.json").Replace("example-route", "r\\u202eoute", StringComparison.Ordinal);
        });
        var vm = h.Vm;

        var routes = vm.DestinationRows.Single(r => r.Name == "example-otlp").Routes;
        Assert.Contains(routes, route => route.Name == "r\\u202Eoute");
        Assert.All(routes, route => Assert.DoesNotContain('‮', route.Name + route.Summary + route.AutomationName));

        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteRemove);
        Assert.Contains(vm.Form.RouteChoices, c => c.Value == hostile && c.Label.StartsWith("1. r\\u202Eoute", StringComparison.Ordinal));
        vm.Form.RouteName = hostile;
        Assert.Equal("That route's name cannot be passed to the CLI safely.", vm.Problem);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task The_optional_profile_box_says_what_leaving_it_out_means()
    {
        var h = await OpenAsync(WithRoutes);
        var form = h.Vm.Form;

        foreach (var (op, label) in new[]
                 {
                     (RedactionOperation.DefaultsSet, "Leave as is"),
                     (RedactionOperation.BucketSet, "Leave as is"),
                     (RedactionOperation.DestinationSend, "Use the default profile"),
                     (RedactionOperation.RouteAdd, "Use the default profile"),
                     (RedactionOperation.DefaultsSet, "Leave as is"),
                 })
        {
            h.Vm.SelectedOperation = RedactionOperations.Info(op);
            Assert.Equal(new RedactionChoice(string.Empty, label), form.OptionalProfileChoices[0]);
            Assert.Equal(["none", "sensitive", "content", "strict", "example-profile"], form.OptionalProfileChoices.Skip(1).Select(c => c.Value).ToArray());
        }

        Assert.Equal("example-profile (custom)", form.OptionalProfileChoices[^1].Label);
    }

    [Fact]
    public async Task A_window_that_was_closed_does_nothing_more()
    {
        var h = await OpenAsync();
        var ran = h.Cli.Ran.Count;

        h.Vm.Dispose();
        h.Vm.Dispose(); // closing twice is harmless

        Assert.False(h.Vm.CanAct);
        await h.Vm.RefreshAsync();
        await h.Vm.PreviewQuickCommand.ExecuteAsync(null);
        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);
        await h.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(ran, h.Cli.Ran.Count);
        Assert.False(h.Vm.Review.IsOpen);
    }

    [Fact]
    public async Task A_change_that_comes_to_nothing_is_not_offered_for_review()
    {
        var h = await OpenAsync(cli => cli.PreviewAnswer = _ => FakeRedactionCli.Fixture("dry-bucket-reset.json"));
        var vm = h.Vm;
        vm.QuickProfile = "strict";

        await vm.ApplyQuickCommand.ExecuteAsync(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
        Assert.True(vm.HasNotice);
        Assert.Equal("Nothing to apply", vm.NoticeTitle);
        Assert.Equal("Nothing to change: the configuration already says this.", vm.Outcome!.Headline);
    }

    [Fact]
    public async Task A_preview_that_fails_stops_the_apply_before_any_review()
    {
        var h = await OpenAsync();
        h.Cli.ExitCode = 1;
        h.Vm.QuickProfile = "strict";

        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);

        Assert.False(h.Vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
        Assert.Equal(RedactionOutcomeKind.Failed, h.Vm.Outcome!.Kind);
    }

    [Fact]
    public async Task No_change_can_be_applied_while_the_policy_is_one_that_was_not_read()
    {
        var h = await OpenAsync();
        h.Cli.ExitCode = 1;
        await h.Vm.RefreshAsync();
        h.Cli.ExitCode = 0;
        var ran = h.Cli.Ran.Count;

        h.Vm.QuickProfile = "strict";
        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);
        h.Vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyEverywhere);
        h.Vm.Form.Profile = "strict";
        h.Vm.DryRun = false;
        await h.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(ran, h.Cli.Ran.Count); // not even the preview
        Assert.False(h.Vm.Review.IsOpen);
        Assert.False(h.Vm.CanRun);
        Assert.Equal("Changes are off", h.Vm.NoticeTitle);
    }

    [Fact]
    public async Task An_apply_that_fails_is_shown_with_the_output_and_the_policy_is_read_again()
    {
        var h = await OpenAsync(cli => cli.ApplyExitCode = 1);
        h.Vm.QuickProfile = "strict";
        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);

        await h.Vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(RedactionOutcomeKind.Failed, h.Vm.Outcome!.Kind);
        Assert.Contains("did not finish", h.Vm.Outcome.Headline, StringComparison.Ordinal);
        Assert.Contains("restore the backup", h.Vm.Outcome.Raw, StringComparison.Ordinal);
        Assert.Equal(2, h.Cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status)); // it may have written the file before it failed
        Assert.True(h.Vm.DryRun);
    }

    [Fact]
    public async Task An_apply_that_finds_nothing_left_to_do_says_so_instead_of_claiming_a_change()
    {
        // the configuration was changed to the same thing between the preview and the apply
        var h = await OpenAsync(cli => cli.ApplyAnswer = _ => FakeRedactionCli.NothingToApplyText());
        h.Vm.QuickProfile = "strict";
        await h.Vm.ApplyQuickCommand.ExecuteAsync(null);

        await h.Vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(h.Cli.Applied);
        var outcome = h.Vm.Outcome!;
        Assert.Equal(RedactionOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Nothing written", outcome.Badge);
        Assert.Equal("Neutral", outcome.Tone);
        Assert.Equal("Nothing to change: the configuration already says this.", outcome.Headline);
        Assert.DoesNotContain(outcome.Notes, n => n.Contains("A copy of config.yaml", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Notes, n => n.Contains("checked against the preview", StringComparison.Ordinal));
    }

    [Fact]
    public void An_exit_0_that_says_it_did_not_apply_or_could_not_check_itself_is_a_failure_of_the_step()
    {
        static CliInvocation Step(string json) => FakeRedactionCli.Result(["setup", "redaction", "remove-all", "--yes", "--json", "--no-restart"], 0, stdout: json);

        Assert.Null(RedactionViewModel.VerifyApplied(Step(new FakeRedactionCli().AppliedText(RedactionOperation.ApplyEverywhere))));
        Assert.Null(RedactionViewModel.VerifyApplied(Step(FakeRedactionCli.Fixture("dry-defaults-reset.json")))); // nothing to apply is not a failure

        Assert.Contains("not a redaction result", RedactionViewModel.VerifyApplied(Step("Configuration updated and verified."))!, StringComparison.Ordinal);
        Assert.Contains("nothing was applied", RedactionViewModel.VerifyApplied(Step(FakeRedactionCli.Fixture("dry-apply-everywhere-sensitive.json")))!, StringComparison.Ordinal);

        var unverified = new FakeRedactionCli().AppliedText(RedactionOperation.ApplyEverywhere).Replace("\"verified_plan_digest\"", "\"verified_plan_digest_x\"", StringComparison.Ordinal);
        var message = RedactionViewModel.VerifyApplied(Step(unverified));
        Assert.Contains("was not checked against the preview", message, StringComparison.Ordinal);
        Assert.Contains("backup is", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the result card goes on to an apply

    [Fact]
    public async Task A_preview_can_be_applied_from_the_result_card_while_the_form_still_builds_that_command()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketSet);
        vm.Form.Bucket = "model.io";
        vm.Form.Profile = "content";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.CanApplyPreview);

        // editing the form makes the preview about something else
        vm.Form.Profile = "strict";
        Assert.False(vm.CanApplyPreview);
        vm.Form.Profile = "content";
        Assert.True(vm.CanApplyPreview);

        await vm.ApplyPreviewedCommand.ExecuteAsync(null);

        Assert.False(vm.DryRun); // the form shows what is happening
        Assert.True(vm.Review.IsOpen);
        Assert.Equal(
            "setup redaction bucket set model.io --profile content --yes --json --no-restart",
            Line(vm.Review.CommandReview!.Steps[0].Argv.ToArray()));
        Assert.Empty(h.Cli.Applied);
    }

    [Fact]
    public async Task The_quick_sheet_s_preview_is_applied_with_the_quick_sheet_s_restart_choice()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.QuickProfile = "content";
        await vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.True(vm.CanApplyPreview);

        vm.QuickRestart = true;
        await vm.ApplyPreviewedCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsOpen);
        Assert.Equal("--restart", vm.Review.CommandReview!.Steps[0].Argv[^1]);
        Assert.True(vm.DryRun); // the advanced form was not involved
    }

    // ------------------------------------------------------------------ every operation reviews its own command

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Every_change_is_applied_through_a_review_of_its_own_command(string operation)
    {
        var op = Enum.Parse<RedactionOperation>(operation);
        var h = await OpenAsync(WithRoutes);
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(op);
        Recipes[op](vm.Form);
        vm.DryRun = false;
        vm.Restart = true;

        await vm.RunCommand.ExecuteAsync(null);

        var info = RedactionOperations.Info(op);
        var changesSomething = op is not (RedactionOperation.DefaultsReset or RedactionOperation.BucketReset or RedactionOperation.DestinationInherit or RedactionOperation.RouteMove);
        Assert.Equal(changesSomething, vm.Review.IsOpen);
        Assert.Empty(h.Cli.Applied);
        if (!changesSomething)
        {
            Assert.Equal("Nothing to apply", vm.NoticeTitle); // the capture's dry run of these found nothing to change
            return;
        }

        var review = vm.Review.CommandReview!;
        var argv = review.Steps[0].Argv.ToArray();
        Assert.Equal(op, RedactionArgv.Identify(argv));
        Assert.Equal("--restart", argv[^1]);
        Assert.Contains("--yes", argv);
        Assert.DoesNotContain("--dry-run", argv);
        Assert.True(review.RestartsGateway);
        Assert.True(CommandReview.RestartsGatewayFor(argv));
        Assert.False(string.IsNullOrWhiteSpace(review.Title));
        Assert.EndsWith("?", review.Title, StringComparison.Ordinal);
        Assert.StartsWith(info.Summary, review.Summary, StringComparison.Ordinal);

        // a remove or a reset is drawn as destructive, and so is any change that starts raw delivery
        if (info.IsDestructive)
        {
            Assert.Equal(CommandTier.Destructive, review.Tier);
        }

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        if (vm.Review.RequiresAcknowledgement)
        {
            Assert.Empty(h.Cli.Applied);
            vm.Review.IsAcknowledged = true;
            await vm.Review.ConfirmCommand.ExecuteAsync(null);
        }

        Assert.Equal(argv, Assert.Single(h.Cli.Applied));
        Assert.Equal(RedactionOutcomeKind.Applied, vm.Outcome!.Kind);
    }

    // ------------------------------------------------------------------ destinations and routes

    [Fact]
    public async Task The_built_in_destination_is_not_offered_for_send_inherit_or_routes_and_the_form_says_so()
    {
        var h = await OpenAsync(); // local-sqlite only
        var vm = h.Vm;

        foreach (var op in new[] { RedactionOperation.DestinationSend, RedactionOperation.DestinationInherit, RedactionOperation.RouteList, RedactionOperation.RouteAdd, RedactionOperation.RouteSet, RedactionOperation.RouteMove, RedactionOperation.RouteRemove })
        {
            vm.SelectedOperation = RedactionOperations.Info(op);
            Assert.Empty(vm.Form.DestinationChoices);
            Assert.False(vm.Form.HasDestinationChoices);
            Assert.Contains("read-only", vm.Form.DestinationHint, StringComparison.Ordinal);
            Assert.Contains("local-sqlite", vm.Form.DestinationHint, StringComparison.Ordinal);
            Assert.Equal("Choose a destination.", vm.Problem);
            Assert.False(vm.CanRun);
        }

        // showing a destination is a read, and the built-in one can be shown
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationShow);
        Assert.Equal(["local-sqlite"], vm.Form.DestinationChoices.Select(c => c.Value).ToArray());
        Assert.Equal(string.Empty, vm.Form.DestinationHint);
        vm.Form.Destination = "local-sqlite";
        Assert.True(vm.CanRun);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("setup redaction destination show local-sqlite", Line(h.Cli.Ran[^1]));
        Assert.Contains("Destination: local-sqlite", vm.Outcome!.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_you_configured_is_offered_and_chosen_for_you_when_it_is_the_only_one()
    {
        var h = await OpenAsync(cli => cli.Status = FakeRedactionCli.Fixture("status-with-destination.json"));
        var vm = h.Vm;

        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationSend);

        Assert.Equal(["example-otlp"], vm.Form.DestinationChoices.Select(c => c.Value).ToArray());
        Assert.Equal("example-otlp", vm.Form.Destination);
        Assert.Contains("generated and read-only", vm.Form.DestinationHint, StringComparison.Ordinal);
        Assert.Equal("Select at least one signal.", vm.Problem);
    }

    [Fact]
    public async Task The_built_in_destination_is_refused_even_if_something_puts_it_in_the_form()
    {
        var h = await OpenAsync(cli => cli.Status = FakeRedactionCli.Fixture("status-with-destination.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationInherit);

        vm.Form.Destination = "local-sqlite"; // the box cannot offer it, but a value set from outside is still held to the rule

        Assert.Contains("generated and read-only", vm.Problem, StringComparison.Ordinal);
        Assert.False(vm.CanRun);
        Assert.Equal(string.Empty, vm.CommandText);
        Assert.Throws<ArgumentException>(() => RedactionArgv.Preview(RedactionOperation.DestinationInherit, vm.Form.ToInputs()));
    }

    [Fact]
    public async Task Replacing_a_route_starts_from_the_route_it_replaces()
    {
        var h = await OpenAsync(WithRoutes);
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteSet);
        Assert.Equal("example-otlp", vm.Form.Destination); // the one destination you configured
        Assert.Equal(["example-route", "example-strict-logs"], vm.Form.RouteChoices.Select(c => c.Value).ToArray());
        Assert.StartsWith("2. example-strict-logs", vm.Form.RouteChoices[1].Label, StringComparison.Ordinal);

        vm.Form.RouteName = "example-strict-logs";

        Assert.Equal(["logs", "traces"], vm.Form.SignalChecks.Where(c => c.IsChecked).Select(c => c.Value).ToArray());
        Assert.Equal(["model.io", "tool.activity"], vm.Form.BucketChecks.Where(c => c.IsChecked).Select(c => c.Value).ToArray());
        Assert.Equal("claudecode", vm.Form.ConnectorsText);
        Assert.Equal("MEDIUM", vm.Form.MinSeverity);
        Assert.Equal("send", vm.Form.RouteAction);
        Assert.Equal("strict", vm.Form.Profile);
        Assert.Equal(
            "defenseclaw setup redaction route set example-otlp example-strict-logs --signal logs --signal traces --bucket model.io --bucket tool.activity --connector claudecode --min-severity MEDIUM --route-action send --profile strict --json --dry-run",
            vm.CommandText);
    }

    [Fact]
    public async Task A_route_profile_is_asked_for_only_on_a_route_that_sends_logs_or_traces()
    {
        var h = await OpenAsync(WithRoutes);
        var form = h.Vm.Form;
        h.Vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteAdd);

        Assert.False(form.ShowOptionalProfile); // no signal yet
        Assert.True(form.ShowRouteProfileNote);

        form.SignalChecks.Single(c => c.Value == "metrics").IsChecked = true;
        Assert.False(form.ShowOptionalProfile); // metrics carry no content
        form.SignalChecks.Single(c => c.Value == "logs").IsChecked = true;
        Assert.True(form.ShowOptionalProfile);
        Assert.False(form.ShowRouteProfileNote);

        form.RouteAction = "drop";
        Assert.False(form.ShowOptionalProfile);
        form.RouteAction = "send";
        Assert.True(form.ShowOptionalProfile);
    }

    [Fact]
    public async Task All_buckets_stands_for_the_named_ones_in_a_send_policy()
    {
        var h = await OpenAsync(cli => cli.Status = FakeRedactionCli.Fixture("status-with-destination.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationSend);
        vm.Form.SignalChecks[0].IsChecked = true;
        vm.Form.BucketChecks[0].IsChecked = true;

        vm.Form.AllBuckets = true;

        Assert.All(vm.Form.BucketChecks, c => Assert.False(c.IsChecked));
        Assert.All(vm.Form.BucketChecks, c => Assert.False(c.IsEnabled));
        Assert.Equal("defenseclaw setup redaction destination send example-otlp --signal logs --bucket * --json --dry-run", vm.CommandText);

        vm.Form.AllBuckets = false;
        Assert.All(vm.Form.BucketChecks, c => Assert.True(c.IsEnabled));
        Assert.Equal("Select at least one bucket or *.", vm.Problem);
    }

    [Fact]
    public async Task A_custom_profile_is_new_until_the_runtime_lists_it()
    {
        var h = await OpenAsync(cli => cli.ProfileList = FakeRedactionCli.Fixture("profile-list-custom.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ProfileSet);
        Assert.Equal(["example-profile"], vm.Form.ProfileNames.ToArray()); // the custom ones only

        vm.Form.ProfileName = "mine";
        vm.Form.DetectorChecks[0].IsChecked = true;
        Assert.Equal("Choose the built-in profile the new profile starts from.", vm.Problem);
        vm.Form.Extends = "strict";
        Assert.Equal(string.Empty, vm.Problem);

        vm.Form.ProfileName = "example-profile";
        vm.Form.Extends = string.Empty;
        Assert.Equal(string.Empty, vm.Problem); // an existing profile keeps its base

        vm.Form.ProfileName = "strict";
        Assert.Equal("A built-in profile cannot be edited: give the custom profile a name of its own.", vm.Problem);

        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ProfileShow);
        Assert.Equal(["none", "sensitive", "content", "strict", "example-profile"], vm.Form.ProfileNames.ToArray());
        vm.Form.ProfileName = "nope";
        Assert.Equal("There is no profile named 'nope'.", vm.Problem);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task A_profile_read_shows_what_the_profile_does_to_each_class_of_field()
    {
        var h = await OpenAsync(cli => cli.ProfileList = FakeRedactionCli.Fixture("profile-list-custom.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ProfileShow);
        vm.Form.ProfileName = "strict";
        Assert.Equal("Run", vm.PrimaryLabel);
        Assert.False(vm.RestartEnabled);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("setup redaction profile show strict --json", Line(h.Cli.Ran[^1]));
        var outcome = vm.Outcome!;
        Assert.Equal(RedactionOutcomeKind.Read, outcome.Kind);
        Assert.Equal("The strict profile.", outcome.Headline);
        Assert.Contains(outcome.Facts, f => f is { Label: "Kind", Value: "built-in" });
        Assert.Contains(outcome.Facts, f => f is { Label: "content", Value: "remove" });
        Assert.Contains(outcome.Facts, f => f is { Label: "Detectors", Value: "pii, credentials, secrets" });
    }

    [Fact]
    public async Task A_route_read_lists_the_routes_in_order_and_feeds_the_route_box()
    {
        var h = await OpenAsync(cli => cli.Status = FakeRedactionCli.Fixture("status-with-destination.json"));
        var vm = h.Vm;
        h.Cli.Routes["example-otlp"] = FakeRedactionCli.Fixture("route-list-two.json");
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteList);
        Assert.Equal("example-otlp", vm.Form.Destination);

        await vm.RunCommand.ExecuteAsync(null);

        var outcome = vm.Outcome!;
        Assert.Equal("2 routes, tried in this order.", outcome.Headline);
        Assert.Equal(["1. example-route", "2. example-strict-logs"], outcome.Facts.Select(f => f.Label).ToArray());
        Assert.Equal(["example-route", "example-strict-logs"], vm.Routes["example-otlp"].Select(r => r.Name).ToArray());

        // and a destination with none says so
        h.Cli.Routes["example-otlp"] = FakeRedactionCli.Fixture("route-list-empty.json");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("has no ordered routes", vm.Outcome!.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_text_only_reads_show_the_text_as_it_is()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketList);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("setup redaction bucket list", Line(h.Cli.Ran[^1]));
        Assert.Contains("compliance.activity", vm.Outcome!.Raw, StringComparison.Ordinal);
        Assert.True(vm.ShowOutcomeRaw);
        Assert.False(vm.ShowRaw); // shown without asking: it is the answer
    }

    [Fact]
    public async Task Running_the_status_operation_reads_the_policy_again()
    {
        var h = await OpenAsync();
        var vm = h.Vm;
        h.Cli.Status = FakeRedactionCli.Fixture("status-mixed.json");
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.Status);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Redaction: mixed", vm.StatusChipText);
        Assert.Equal(RedactionOutcomeKind.Read, vm.Outcome!.Kind);
        Assert.Equal(vm.Summary, vm.Outcome.Headline);
    }

    [Fact]
    public async Task A_read_that_fails_is_a_failure_with_the_cli_s_words()
    {
        var h = await OpenAsync(cli => cli.Status = FakeRedactionCli.Fixture("status-with-destination.json"));
        var vm = h.Vm;
        vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationShow);
        vm.Form.Destination = "example-otlp";
        h.Cli.ExitCode = 1;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(RedactionOutcomeKind.Failed, vm.Outcome!.Kind);
        Assert.Contains("no effective destination", vm.Outcome.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_result_can_be_dismissed()
    {
        var h = await OpenAsync();
        await h.Vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.True(h.Vm.HasOutcome);

        h.Vm.DismissOutcomeCommand.Execute(null);

        Assert.False(h.Vm.HasOutcome);
        Assert.False(h.Vm.CanApplyPreview);
    }
}
