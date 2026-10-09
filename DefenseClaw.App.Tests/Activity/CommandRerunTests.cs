using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// Rerun (CUST-264): which Activity entries can be run again, the review that opens for one (the same argv, the entry's tier - a destructive
/// one with focus on Cancel), and what a confirmed review reaches. Nothing here starts a process: the runner and the review dialog are the
/// test seams of <see cref="CommandRerun"/>, and the runner of the isolated services has no CLI to find.
/// </summary>
public sealed class CommandRerunTests : IDisposable
{
    private const string Cli = "defenseclaw";
    private const string Gateway = "defenseclaw-gateway";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly CommandRerun _rerun;
    private readonly List<CommandReview> _reviews = new();
    private readonly List<(string Tool, string[] Argv, CliRunOptions? Options)> _ran = new();
    private readonly List<string> _refreshes = new();
    private bool _confirm = true;
    private int _exit;
    private Exception? _runnerFails;

    public CommandRerunTests()
    {
        _services = TestServices.Create(_temp);
        _rerun = new CommandRerun(_services, owner: () => null)
        {
            Confirmer = review =>
            {
                _reviews.Add(review);
                return _confirm;
            },
            Runner = (tool, argv, options) =>
            {
                _ran.Add((tool, argv.ToArray(), options));
                if (_runnerFails is { } failure)
                {
                    throw failure;
                }

                var again = InvocationFactory.CreateFor(tool, argv.ToArray());
                InvocationFactory.Finish(again, _exit);
                return Task.FromResult(again);
            },
            RefreshGateway = () =>
            {
                _refreshes.Add("refresh");
                return Task.CompletedTask;
            },
        };
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    // ------------------------------------------------------------------ which entries have a Rerun

    [Fact]
    public void A_finished_defenseclaw_or_gateway_command_can_be_run_again()
    {
        foreach (var entry in new[] { Finished(Cli, 0, "doctor"), Finished(Cli, 1, "skill", "list"), Finished(Gateway, 0, "restart"), Finished(@"C:\Programs\DefenseClaw\bin\defenseclaw.exe", 0, "status") })
        {
            var state = CommandRerun.StateOf(entry);
            Assert.True(state.Offered, entry.CommandLine);
            Assert.True(state.Enabled, entry.CommandLine);
            Assert.Contains("Nothing runs until you confirm", state.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_failed_a_timed_out_and_a_cancelled_run_can_all_be_run_again()
    {
        foreach (var reason in new[] { "timed out after 120 s — process tree killed", "cancelled — process tree killed", "The system cannot find the file specified." })
        {
            var entry = InvocationFactory.CreateFor(Cli, new[] { "doctor" });
            InvocationFactory.Fail(entry, reason);

            Assert.True(CommandRerun.StateOf(entry).Enabled, reason);
        }
    }

    [Fact]
    public void A_command_still_running_has_a_Rerun_that_is_off_and_says_why()
    {
        var running = InvocationFactory.CreateFor(Cli, new[] { "doctor" });

        var state = CommandRerun.StateOf(running);

        Assert.True(state.Offered);
        Assert.False(state.Enabled);
        Assert.Contains("Wait for this command to finish, or cancel it", state.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_carried_a_secret_has_no_Rerun_because_the_app_never_kept_the_value()
    {
        var onStdin = Finished(Cli, 0, "keys", "set", "OPENAI_API_KEY");
        InvocationFactory.UseStdinSecret(onStdin);
        var inEnvironment = Finished(Cli, 0, "setup", "splunk", "--accelerator");
        InvocationFactory.UseEnvironmentSecret(inEnvironment, "SPLUNK_ACCESS_TOKEN");

        // Typed at the command's hidden prompt in a pseudo-console (CUST-221): the same rule.
        var typedAtPrompt = Finished(Cli, 0, "keys", "set", "OPENAI_API_KEY");
        InvocationFactory.UsePromptSecret(typedAtPrompt);

        foreach (var entry in new[] { onStdin, inEnvironment, typedAtPrompt })
        {
            var state = CommandRerun.StateOf(entry);
            Assert.False(state.Offered);
            Assert.False(state.Enabled);
            Assert.Contains("secret", state.Hint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(@"C:\Tools\cosign.exe", "verify-blob", "--bundle", "x")]
    [InlineData(@"C:\Tools\cosign.exe", "version")]
    [InlineData(@"C:\Program Files\Docker\docker.exe", "exec", "dc", "defenseclaw", "doctor")]
    [InlineData(@"C:\Temp\DefenseClaw-Setup.exe", "/S")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c", "echo", "x")]
    public void An_entry_that_is_not_a_DefenseClaw_command_has_no_Rerun(string executable, params string[] argv)
    {
        var state = CommandRerun.StateOf(Finished(executable, 0, argv));

        Assert.False(state.Offered);
        Assert.Contains("Only DefenseClaw commands", state.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_that_outlives_the_app_has_no_Rerun_even_under_the_cli_s_name()
    {
        var installer = Finished(Cli, 0, "upgrade");
        InvocationFactory.SurviveShutdown(installer);

        Assert.False(CommandRerun.StateOf(installer).Offered);
    }

    [Fact]
    public void A_run_with_nothing_after_the_tool_is_never_offered_least_of_all_the_gateway_which_would_start_a_second_daemon()
    {
        var bareGateway = Finished(Gateway, 0);
        var bareCli = Finished(Cli, 0);

        var gateway = CommandRerun.StateOf(bareGateway);
        Assert.False(gateway.Offered);
        Assert.Contains("second gateway daemon", gateway.Hint, StringComparison.Ordinal);
        Assert.False(CommandRerun.StateOf(bareCli).Offered);
        Assert.Throws<InvalidOperationException>(() => _rerun.ReviewFor(bareGateway));
    }

    [Fact]
    public void A_refusal_and_a_command_handed_to_a_terminal_ran_nothing_so_there_is_nothing_to_run_again()
    {
        var refused = _services.Cli.RecordRefusal(Cli, new[] { "skill", "block", "--", "x" }, "The list is out of date.");
        var handedOff = _services.Cli.RecordHandOff(Cli, new[] { "keys", "set", "OPENAI_API_KEY" }, "Run this in a terminal.");

        Assert.False(CommandRerun.StateOf(refused).Offered);
        Assert.Contains("refused", CommandRerun.StateOf(refused).Hint, StringComparison.Ordinal);
        Assert.False(CommandRerun.StateOf(handedOff).Offered);
        Assert.Contains("terminal", CommandRerun.StateOf(handedOff).Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_command_is_the_newest_that_can_be_run_again_skipping_the_ones_that_cannot()
    {
        var older = InvocationFactory.CreateFor(Cli, new[] { "doctor" }, DateTimeOffset.UtcNow.AddMinutes(-9));
        InvocationFactory.Finish(older, 0);
        var newer = InvocationFactory.CreateFor(Gateway, new[] { "restart" }, DateTimeOffset.UtcNow.AddMinutes(-5));
        InvocationFactory.Finish(newer, 0);
        var signature = InvocationFactory.CreateFor(@"C:\Tools\cosign.exe", new[] { "version" }, DateTimeOffset.UtcNow.AddMinutes(-1));
        InvocationFactory.Finish(signature, 0);
        var secret = InvocationFactory.CreateFor(Cli, new[] { "keys", "set", "X" }, DateTimeOffset.UtcNow.AddSeconds(-30));
        InvocationFactory.Finish(secret, 0);
        InvocationFactory.UseStdinSecret(secret);

        Assert.Same(newer, CommandRerun.LastOffered(new[] { signature, secret, newer, older }));
        Assert.Same(newer, CommandRerun.LastOffered(new[] { older, newer, signature, secret }));
        Assert.Null(CommandRerun.LastOffered(new[] { signature, secret }));
        Assert.Null(CommandRerun.LastOffered(Array.Empty<CliInvocation>()));
    }

    // ------------------------------------------------------------------ the review

    [Fact]
    public void The_review_is_the_exact_argv_on_its_own_tool_at_the_tier_it_had()
    {
        var entry = Finished(Cli, 0, "skill", "block", "--connector", "claudecode", "--", "pdf-tools");

        var review = _rerun.ReviewFor(entry, CommandRerun.TierOf(entry));

        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" }, step.Argv);
        Assert.Equal("defenseclaw", step.Executable);
        Assert.Equal("defenseclaw skill block --connector claudecode -- pdf-tools", step.CommandText);
        Assert.Equal("Run defenseclaw skill block --connector claudecode -- pdf-tools again?", review.Title);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("Run command", review.ConfirmLabel);
        Assert.False(review.RestartsGateway);
    }

    [Fact]
    public void A_destructive_entry_is_reviewed_as_destructive_with_the_danger_confirm()
    {
        var entry = Finished(Cli, 0, "skill", "quarantine", "--", "pdf-tools");

        var review = _rerun.ReviewFor(entry, CommandRerun.TierOf(entry));

        Assert.True(review.IsDestructive);
        Assert.Equal(CommandTier.Destructive, review.Tier);
        Assert.Equal("Run destructive command", review.ConfirmLabel);
        Assert.Equal("Destructive", review.TierLabel);
    }

    [Fact]
    public void The_entry_s_own_tier_is_the_floor_so_a_review_is_never_milder_than_the_row_said()
    {
        var entry = Finished(Cli, 0, "doctor");

        Assert.Equal(CommandTier.ReadOnly, _rerun.ReviewFor(entry, CommandTier.ReadOnly).Tier);
        Assert.Equal(CommandTier.ReadOnly, _rerun.ReviewFor(entry).Tier);
        Assert.Equal(CommandTier.StateChanging, _rerun.ReviewFor(entry, CommandTier.StateChanging).Tier);
        Assert.Equal(CommandTier.Destructive, _rerun.ReviewFor(entry, CommandTier.Destructive).Tier);
    }

    [Theory]
    [InlineData("setup", "observability", "list", "--json")]
    [InlineData("setup", "webhook", "list", "--json")]
    [InlineData("setup", "trusted-paths", "list", "--json")]
    [InlineData("setup", "webhook", "show", "--json", "--", "example-slack")]
    public void A_rerun_of_a_setup_editor_read_is_reviewed_as_the_read_it_is(params string[] argv)
    {
        // CUST-326: the classifier used to call these a change, so the review said "changes state" and offered the restart notice.
        var entry = Finished(Cli, 0, argv);

        Assert.Equal(CommandTier.ReadOnly, CommandRerun.TierOf(entry));

        var review = _rerun.ReviewFor(entry);
        Assert.Equal(CommandTier.ReadOnly, review.Tier);
        Assert.False(review.RestartsGateway);
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway restart");
    }

    [Theory]
    [InlineData("setup", "guardrail")]
    [InlineData("guardrail", "enable")]
    public void A_command_that_restarts_the_gateway_says_so_as_every_other_review_does(params string[] argv)
    {
        var review = _rerun.ReviewFor(Finished(Cli, 0, argv));

        Assert.True(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == "Gateway restart");
    }

    [Fact]
    public void The_gateways_start_stop_and_restart_are_reviewed_in_the_words_of_the_tray()
    {
        var restart = _rerun.ReviewFor(Finished(Gateway, 0, "restart"));
        var stop = _rerun.ReviewFor(Finished(Gateway, 0, "stop"));
        var start = _rerun.ReviewFor(Finished(Gateway, 0, "start"));

        Assert.True(restart.RestartsGateway);
        Assert.Contains(GatewayControl.ReviewNote(GatewayAction.Restart), restart.Summary, StringComparison.Ordinal);
        Assert.Contains(GatewayControl.ReviewNote(GatewayAction.Stop), stop.Summary, StringComparison.Ordinal);
        Assert.Contains(GatewayControl.ReviewNote(GatewayAction.Start), start.Summary, StringComparison.Ordinal);
        Assert.All(new[] { restart, stop, start }, r => Assert.Equal("defenseclaw-gateway", r.Steps[0].Executable));
        Assert.All(new[] { restart, stop, start }, r => Assert.Equal(CommandTier.StateChanging, r.Tier));
        Assert.False(stop.RestartsGateway);
    }

    [Fact]
    public void A_fixed_gateway_verb_is_reviewed_in_the_words_of_the_gateways_own_help()
    {
        var teardown = _rerun.ReviewFor(Finished(Gateway, 0, "connector", "teardown"));
        var reload = _rerun.ReviewFor(Finished(Gateway, 0, "policy", "reload"));

        Assert.True(teardown.IsDestructive);
        Assert.Contains("pristine backup", teardown.Summary, StringComparison.Ordinal);
        Assert.Contains("audit database", teardown.Summary, StringComparison.Ordinal);
        Assert.Equal(CommandTier.StateChanging, reload.Tier);
        Assert.Contains("reload its OPA policies", reload.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_review_says_it_is_the_same_command_and_how_it_ended_the_last_time()
    {
        var ok = _rerun.ReviewFor(Finished(Cli, 0, "doctor"));
        var failed = _rerun.ReviewFor(Finished(Cli, 3, "doctor"));
        var stopped = InvocationFactory.CreateFor(Cli, new[] { "doctor" });
        InvocationFactory.Fail(stopped, "timed out after 120 s — process tree killed");
        var timedOut = _rerun.ReviewFor(stopped);

        Assert.Contains("This runs the same command again, exactly as it ran at", ok.Summary, StringComparison.Ordinal);
        Assert.Contains("(exit 0)", ok.Summary, StringComparison.Ordinal);
        Assert.Contains("(exit 3)", failed.Summary, StringComparison.Ordinal);
        Assert.Contains("(timed out after 120 s)", timedOut.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_an_invisible_character_in_it_carries_the_unusual_characters_bar_like_any_review()
    {
        var review = _rerun.ReviewFor(Finished(Cli, 0, "skill", "block", "--", "pdf\u202Etools"));

        Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.UnusualCharactersTitle);
        Assert.DoesNotContain('\u202E', review.Steps[0].CommandText);
    }

    [Fact]
    public void An_entry_that_cannot_be_run_again_has_no_review()
    {
        var onStdin = Finished(Cli, 0, "keys", "set", "X");
        InvocationFactory.UseStdinSecret(onStdin);

        Assert.Throws<InvalidOperationException>(() => _rerun.ReviewFor(onStdin));
        Assert.Throws<InvalidOperationException>(() => _rerun.ReviewFor(Finished(@"C:\Tools\cosign.exe", 0, "version")));
    }

    // ------------------------------------------------------------------ the run

    [Fact]
    public async Task Declining_the_review_runs_nothing_and_says_nothing()
    {
        _confirm = false;

        var message = await _rerun.RunAsync(Finished(Cli, 0, "skill", "block", "--", "pdf-tools"));

        Assert.Equal(string.Empty, message);
        _ = Assert.Single(_reviews);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Confirming_runs_the_exact_argv_on_the_same_tool_and_reports_how_it_went()
    {
        var message = await _rerun.RunAsync(Finished(Cli, 1, "skill", "block", "--connector", "claudecode", "--", "pdf-tools"));

        var run = Assert.Single(_ran);
        Assert.Equal("defenseclaw", run.Tool);
        Assert.Equal(new[] { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" }, run.Argv);
        Assert.Equal(
            "Ran defenseclaw skill block --connector claudecode -- pdf-tools again: finished (exit 0). The new entry is at the top of the Activity list.",
            message);

        _exit = 2;
        var failed = await _rerun.RunAsync(Finished(Cli, 0, "doctor"));
        Assert.Equal("Ran defenseclaw doctor again: exited with code 2. The new entry is at the top of the Activity list.", failed);
    }

    [Fact]
    public async Task The_runner_is_told_to_refuse_a_name_the_cli_would_rewrite_so_what_was_reviewed_is_what_runs()
    {
        _ = await _rerun.RunAsync(Finished(Cli, 0, "skill", "block", "--", "pdf-tools"));

        Assert.True(Assert.Single(_ran).Options!.RefuseExpandingTargets);
        Assert.False(_ran[0].Options!.RetainFullOutput);
    }

    [Fact]
    public async Task A_rerun_of_a_parsed_json_read_keeps_the_whole_output_as_the_first_run_did()
    {
        var entry = InvocationFactory.Create(true, "skill", "list", "--json");
        InvocationFactory.Finish(entry, 0);

        _ = await _rerun.RunAsync(entry);

        Assert.True(Assert.Single(_ran).Options!.RetainFullOutput);
    }

    [Fact]
    public async Task A_command_still_running_is_not_reviewed_or_run_and_the_reason_comes_back()
    {
        var message = await _rerun.RunAsync(InvocationFactory.CreateFor(Cli, new[] { "doctor" }));

        Assert.Contains("Wait for this command to finish", message, StringComparison.Ordinal);
        Assert.Empty(_reviews);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task An_entry_that_cannot_be_run_again_is_not_reviewed_or_run_either()
    {
        var onStdin = Finished(Cli, 0, "keys", "set", "X");
        InvocationFactory.UseStdinSecret(onStdin);

        var message = await _rerun.RunAsync(onStdin);

        Assert.Contains("secret", message, StringComparison.Ordinal);
        Assert.Empty(_reviews);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task A_confirmed_stop_is_the_operators_word_so_the_automatic_start_is_told_and_the_status_is_refreshed()
    {
        _confirm = false;
        _ = await _rerun.RunAsync(Finished(Gateway, 0, "stop"));
        Assert.False(_services.GatewayAutoStart.UserStopped);
        Assert.Empty(_refreshes);

        _confirm = true;
        _ = await _rerun.RunAsync(Finished(Gateway, 0, "restart"));
        Assert.False(_services.GatewayAutoStart.UserStopped);
        Assert.Single(_refreshes);

        _ = await _rerun.RunAsync(Finished(Gateway, 0, "stop"));
        Assert.True(_services.GatewayAutoStart.UserStopped);
        Assert.Equal(2, _refreshes.Count);
        Assert.Equal(new[] { "restart", "stop" }, _ran.Select(r => r.Argv[0]));
    }

    [Fact]
    public async Task Another_gateway_verb_is_an_ordinary_run_that_does_not_refresh_the_status_or_touch_the_automatic_start()
    {
        _ = await _rerun.RunAsync(Finished(Gateway, 0, "watchdog", "stop"));

        Assert.Equal(new[] { "watchdog", "stop" }, Assert.Single(_ran).Argv);
        Assert.False(_services.GatewayAutoStart.UserStopped);
        Assert.Empty(_refreshes);
    }

    [Fact]
    public async Task A_command_that_cannot_start_comes_back_as_a_sentence_not_an_exception()
    {
        _runnerFails = new CliNotFoundException("defenseclaw", new[] { @"C:\nowhere" });
        var notFound = await _rerun.RunAsync(Finished(Cli, 0, "doctor"));
        Assert.StartsWith("Could not find 'defenseclaw'", notFound, StringComparison.Ordinal);

        _runnerFails = new SecretInArgumentException(2);
        var secret = await _rerun.RunAsync(Finished(Cli, 0, "doctor"));
        Assert.Contains("contains a secret", secret, StringComparison.Ordinal);

        _runnerFails = new InvalidOperationException("boom");
        var other = await _rerun.RunAsync(Finished(Cli, 0, "doctor"));
        Assert.Equal("Could not run it: boom", other);
    }

    [Fact]
    public async Task A_second_rerun_while_one_waits_for_its_answer_is_told_to_wait()
    {
        string? inner = null;
        _rerun.Confirmer = review =>
        {
            _reviews.Add(review);
            inner ??= _rerun.RunAsync(Finished(Cli, 0, "status")).GetAwaiter().GetResult();
            return true;
        };

        var outer = await _rerun.RunAsync(Finished(Cli, 0, "doctor"));

        Assert.Contains("Another command is waiting for your answer", inner, StringComparison.Ordinal);
        Assert.StartsWith("Ran defenseclaw doctor again", outer, StringComparison.Ordinal);
        Assert.Equal(new[] { "doctor" }, _ran.Select(r => r.Argv[0]));

        // And it is free again afterwards.
        _ = await _rerun.RunAsync(Finished(Cli, 0, "status"));
        Assert.Equal(new[] { "doctor", "status" }, _ran.Select(r => r.Argv[0]));
    }
}

/// <summary>The Activity row's side of Rerun: when the button is drawn and pressable, and what pressing it hands to the panel's re-run.</summary>
public sealed class ActivityRowRerunTests
{
    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Task<string> Declined(CliInvocation invocation, CommandTier tier) => Task.FromResult(string.Empty);

    [Fact]
    public void A_row_built_without_a_way_to_run_it_has_no_Rerun()
    {
        var row = new ActivityRow(Finished("defenseclaw", 0, "doctor"));

        Assert.False(row.CanOfferRerun);
        Assert.False(row.CanRerun);
        Assert.False(row.RerunCommand.CanExecute(null));
    }

    [Fact]
    public void A_finished_command_has_a_Rerun_that_can_be_pressed()
    {
        var row = new ActivityRow(Finished("defenseclaw", 1, "skill", "list"), rerun: Declined);

        Assert.True(row.CanOfferRerun);
        Assert.True(row.CanRerun);
        Assert.True(row.RerunCommand.CanExecute(null));
        Assert.Contains("Nothing runs until you confirm", row.RerunHint, StringComparison.Ordinal);
    }

    [Fact]
    public void While_the_command_runs_Rerun_is_drawn_but_off_and_comes_on_when_it_finishes()
    {
        var run = InvocationFactory.CreateFor("defenseclaw", new[] { "doctor" });
        var row = new ActivityRow(run, rerun: Declined);

        Assert.True(row.CanOfferRerun);
        Assert.False(row.CanRerun);
        Assert.False(row.RerunCommand.CanExecute(null));
        Assert.Contains("Wait for this command to finish", row.RerunHint, StringComparison.Ordinal);

        InvocationFactory.Finish(run, 0);
        row.Tick();

        Assert.True(row.CanRerun);
        Assert.True(row.RerunCommand.CanExecute(null));
        Assert.Contains("Nothing runs until you confirm", row.RerunHint, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_carried_a_secret_or_is_a_signature_check_has_no_Rerun_at_all()
    {
        var onStdin = Finished("defenseclaw", 0, "keys", "set", "X");
        InvocationFactory.UseStdinSecret(onStdin);
        var inEnvironment = Finished("defenseclaw", 0, "setup", "splunk");
        InvocationFactory.UseEnvironmentSecret(inEnvironment, "SPLUNK_ACCESS_TOKEN");
        var cosign = Finished(@"C:\Tools\cosign.exe", 0, "verify-blob", "--bundle", "x");

        foreach (var entry in new[] { onStdin, inEnvironment, cosign })
        {
            var row = new ActivityRow(entry, rerun: Declined);
            Assert.False(row.CanOfferRerun, entry.CommandLine);
            Assert.False(row.CanRerun, entry.CommandLine);
            Assert.False(row.RerunCommand.CanExecute(null), entry.CommandLine);
        }
    }

    [Fact]
    public void The_row_knows_the_tier_of_its_command_for_the_review_to_open_at()
    {
        Assert.Equal(CommandTier.Destructive, new ActivityRow(Finished("defenseclaw", 0, "skill", "quarantine", "--", "x")).Tier);
        Assert.Equal(CommandTier.StateChanging, new ActivityRow(Finished("defenseclaw", 0, "skill", "block", "--", "x")).Tier);
        Assert.Equal(CommandTier.ReadOnly, new ActivityRow(Finished("defenseclaw", 0, "doctor")).Tier);
        Assert.Equal(CommandTier.ReadOnly, new ActivityRow(Finished(@"C:\Tools\cosign.exe", 0, "verify-blob", "--bundle", "x")).Tier);
    }

    [Fact]
    public async Task Pressing_Rerun_hands_the_entry_and_its_tier_to_the_panels_rerun_and_shows_what_it_says()
    {
        var notices = new List<string>();
        CliInvocation? askedEntry = null;
        CommandTier? askedTier = null;
        var entry = Finished("defenseclaw", 0, "skill", "quarantine", "--", "pdf-tools");
        var row = new ActivityRow(
            entry,
            notify: notices.Add,
            rerun: (invocation, tier) =>
            {
                askedEntry = invocation;
                askedTier = tier;
                return Task.FromResult("Ran defenseclaw skill quarantine -- pdf-tools again: finished (exit 0).");
            });

        await row.RerunCommand.ExecuteAsync(null);

        Assert.Same(entry, askedEntry);
        Assert.Equal(CommandTier.Destructive, askedTier);
        Assert.Equal("Ran defenseclaw skill quarantine -- pdf-tools again: finished (exit 0).", Assert.Single(notices));
    }

    [Fact]
    public async Task A_declined_review_says_nothing_and_the_button_is_off_while_the_review_and_run_are_going()
    {
        var notices = new List<string>();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = new ActivityRow(Finished("defenseclaw", 0, "doctor"), notify: notices.Add, rerun: (_, _) => gate.Task);

        var pending = row.RerunCommand.ExecuteAsync(null);
        Assert.False(row.RerunCommand.CanExecute(null));

        gate.SetResult(string.Empty);
        await pending;

        Assert.True(row.RerunCommand.CanExecute(null));
        Assert.Empty(notices);
    }
}
