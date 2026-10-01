using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Guardrail;

/// <summary>
/// The guardrail controls drive the CLI only through the runner they are given, so every test here hands in a fake: no process
/// starts, and a mutating verb is recorded, never run.
/// </summary>
public class GuardrailControlsViewModelTests
{
    internal const string Status = """

          Guardrail status
          ----------------
          • enabled:    yes
              Connector    Key         State    Mode     Fail  Rule pack  HILT  Scan        Judge
              -----------  ----------  -------  -------  ----  ---------  ----  ----------  -----
              Claude Code  claudecode  enabled  observe  open  default    off   regex_only  off
              Codex        codex       enabled  action   open  strict     on    regex_only  off
          • port:       4000

        """;

    internal const string StatusOne = """

          Guardrail status
          ----------------
          • enabled:    yes
              Connector    Key         State    Mode     Fail  Rule pack  HILT  Scan        Judge
              -----------  ----------  -------  -------  ----  ---------  ----  ----------  -----
              Claude Code  claudecode  enabled  observe  open  default    off   regex_only  off
          • port:       4000

        """;

    internal const string Hilt = """

          guardrail.hilt.enabled: true
          guardrail.hilt.min_severity: MEDIUM

          per connector:
              - Claude Code (claudecode): enabled=true min_severity=MEDIUM
              - Codex (codex): enabled=false min_severity=CRITICAL

        """;

    internal const string HiltOneConnector = """

          guardrail.hilt.enabled: false
          guardrail.hilt.min_severity: HIGH

          per connector:
              - Claude Code (claudecode): enabled=false min_severity=HIGH

        """;

    internal const string Message = """

          guardrail.block_message: Blocked by Acme

          per connector:
              - Claude Code (claudecode): Blocked by Acme
              - Codex (codex): Codex policy

        """;

    internal const string MessageOneConnector = """

          guardrail.block_message: (built-in default)

          per connector:
              - Claude Code (claudecode): (built-in default)

        """;

    internal const string Judge = """

          guardrail.judge.enabled:         false
          guardrail.judge.hook_connectors: ['claudecode']
          guardrail.judge.hook_timeout:    5s (gateway default)

          effective state per connector:
              - claudecode: gated on, judge inactive - judge disabled
              - codex: regex + AID only - opt in: defenseclaw guardrail judge add codex
              - openclaw: judge off

        """;

    /// <summary>A runner that answers the four reads from fixed text and records everything else.</summary>
    internal sealed class FakeCli
    {
        public string Status { get; set; } = GuardrailControlsViewModelTests.Status;

        public string Hilt { get; set; } = GuardrailControlsViewModelTests.Hilt;

        public string Message { get; set; } = GuardrailControlsViewModelTests.Message;

        public string Judge { get; set; } = GuardrailControlsViewModelTests.Judge;

        public int WriteExitCode { get; set; }

        public List<string> Reads { get; } = new();

        public List<string> Writes { get; } = new();

        public Task<CliInvocation> Run(IReadOnlyList<string> argv, CancellationToken token)
        {
            var line = string.Join(' ', argv);
            var text = line switch
            {
                "guardrail status" => Status,
                "guardrail hilt" => Hilt,
                "guardrail block-message" => Message,
                "guardrail judge list" => Judge,
                _ => null,
            };

            var invocation = InvocationFactory.Create(argv: argv.ToArray());
            if (text is null)
            {
                Writes.Add(line);
                InvocationFactory.Append(invocation, "done");
                InvocationFactory.Finish(invocation, WriteExitCode);
            }
            else
            {
                Reads.Add(line);
                foreach (var l in text.Split('\n'))
                {
                    InvocationFactory.Append(invocation, l);
                }

                InvocationFactory.Finish(invocation, 0);
            }

            return Task.FromResult(invocation);
        }
    }

    private static async Task<(GuardrailControlsViewModel Vm, FakeCli Cli)> Loaded(Action<FakeCli>? setup = null)
    {
        var cli = new FakeCli();
        setup?.Invoke(cli);
        var vm = new GuardrailControlsViewModel(cli.Run);
        await vm.RefreshAsync();
        return (vm, cli);
    }

    [Fact]
    public async Task Refreshing_runs_only_the_four_read_verbs_and_shows_what_they_said()
    {
        var (vm, cli) = await Loaded();

        Assert.Equal(new[] { "guardrail status", "guardrail hilt", "guardrail block-message", "guardrail judge list" }, cli.Reads);
        Assert.Empty(cli.Writes);
        Assert.False(vm.HasError);
        Assert.Contains("HILT is on", vm.HiltSummary, StringComparison.Ordinal);
        Assert.Equal("MEDIUM", vm.SelectedMinSeverity);
        Assert.Equal("Blocked by Acme", vm.CurrentBlockMessage);
        Assert.Equal("Blocked by Acme", vm.MessageText);
        Assert.Contains("$ defenseclaw guardrail judge list", vm.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judge_list_offers_add_for_ungated_hook_connectors_remove_for_gated_ones_and_neither_for_proxy_connectors()
    {
        var (vm, _) = await Loaded();

        Assert.Collection(
            vm.JudgeRows,
            r => Assert.Equal(("claudecode", false, true), (r.Key, r.CanAdd, r.CanRemove)),
            r => Assert.Equal(("codex", true, false), (r.Key, r.CanAdd, r.CanRemove)),
            r => Assert.Equal(("openclaw", false, false), (r.Key, r.CanAdd, r.CanRemove)));
    }

    [Fact]
    public async Task Choosing_a_connector_shows_its_own_hilt_and_block_message()
    {
        var (vm, _) = await Loaded();
        Assert.True(vm.HasMultipleConnectors);

        vm.SelectedScope = vm.Scopes.Single(s => s.Key == "codex");

        Assert.Contains("HILT is off for Codex", vm.HiltSummary, StringComparison.Ordinal);
        Assert.Equal("CRITICAL", vm.SelectedMinSeverity);
        Assert.Equal("Codex policy", vm.CurrentBlockMessage);
        Assert.Equal("Codex policy", vm.MessageText);
    }

    [Fact]
    public async Task Turning_hilt_on_reviews_the_exact_argv_at_the_state_changing_tier_with_the_restart_and_the_multi_connector_warning()
    {
        var (vm, cli) = await Loaded();
        vm.SelectedMinSeverity = "HIGH";

        vm.TurnHiltOnCommand.Execute(null);

        Assert.True(vm.IsReviewOpen);
        var review = vm.Review!;
        Assert.Equal("defenseclaw guardrail hilt on --yes --min-severity HIGH", review.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == "Gateway restart");
        Assert.Contains(review.Warnings, w => w.Title == "Rewrites every connector's HILT");
        Assert.Empty(cli.Writes);
    }

    [Fact]
    public async Task A_scoped_hilt_change_carries_connector_and_drops_the_overwrite_warning()
    {
        var (vm, _) = await Loaded();
        vm.SelectedScope = vm.Scopes.Single(s => s.Key == "codex");

        vm.TurnHiltOffCommand.Execute(null);

        Assert.Equal("defenseclaw guardrail hilt off --yes --connector codex", vm.Review!.CommandText);
        Assert.DoesNotContain(vm.Review.Warnings, w => w.Title.StartsWith("Rewrites", StringComparison.Ordinal));
        Assert.Equal("Turn HILT off for Codex?", vm.Review.Title);
    }

    [Fact]
    public async Task Setting_a_block_message_on_a_multi_connector_install_warns_that_it_overwrites_every_connectors_override()
    {
        var (vm, _) = await Loaded();

        vm.MessageText = "Blocked by Acme Security";
        vm.SetBlockMessageCommand.Execute(null);

        var review = vm.Review!;
        Assert.Equal("defenseclaw guardrail block-message --yes -- \"Blocked by Acme Security\"", review.CommandText);
        var warning = Assert.Single(review.Warnings, w => w.Title == "Overwrites every connector's message");
        Assert.Contains("every active connector", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_connector_install_has_no_scope_picker_and_no_overwrite_warning()
    {
        var (vm, _) = await Loaded(c =>
        {
            c.Status = StatusOne;
            c.Hilt = HiltOneConnector;
            c.Message = MessageOneConnector;
        });

        Assert.False(vm.HasMultipleConnectors);
        Assert.Null(vm.ScopeKey);
        vm.MessageText = "Hello";
        vm.SetBlockMessageCommand.Execute(null);

        Assert.DoesNotContain(vm.Review!.Warnings, w => w.Title.Contains("every connector", StringComparison.Ordinal));
        Assert.Equal("Built-in default", vm.CurrentBlockMessage);
    }

    [Fact]
    public async Task Clearing_the_message_reviews_clear_and_an_empty_or_multiline_text_cannot_be_set()
    {
        var (vm, _) = await Loaded();

        vm.ClearBlockMessageCommand.Execute(null);
        Assert.Equal("defenseclaw guardrail block-message --clear --yes", vm.Review!.CommandText);
        vm.CancelReviewCommand.Execute(null);

        vm.MessageText = "   ";
        vm.SetBlockMessageCommand.Execute(null);
        Assert.False(vm.IsReviewOpen);

        vm.MessageText = "line one\nline two";
        Assert.True(vm.HasMessageProblem);
        vm.SetBlockMessageCommand.Execute(null);
        Assert.False(vm.IsReviewOpen);
    }

    [Fact]
    public async Task Unchecking_the_restart_box_rebuilds_the_review_with_no_restart_and_says_it_is_not_in_effect()
    {
        var (vm, _) = await Loaded();
        vm.TurnHiltOffCommand.Execute(null);
        Assert.True(vm.Review!.RestartsGateway);

        vm.RestartAfter = false;

        Assert.EndsWith("--no-restart", vm.Review!.CommandText, StringComparison.Ordinal);
        Assert.False(vm.Review.RestartsGateway);
        Assert.Contains(vm.Review.Warnings, w => w.Title == "Gateway not restarted");
    }

    [Fact]
    public async Task Judge_add_reviews_a_state_change_and_warns_when_the_judge_itself_is_off_unless_it_is_enabled_too()
    {
        var (vm, _) = await Loaded();

        vm.AddJudgeCommand.Execute("codex");
        Assert.Equal("defenseclaw guardrail judge add codex", vm.Review!.CommandText);
        Assert.Equal(CommandTier.StateChanging, vm.Review.Tier);
        Assert.Contains(vm.Review.Warnings, w => w.Title == "The judge is off");
        vm.CancelReviewCommand.Execute(null);

        vm.AlsoEnableJudge = true;
        vm.JudgeTimeoutText = "8";
        vm.AddJudgeCommand.Execute("codex");
        Assert.Equal("defenseclaw guardrail judge add codex --enable --timeout 8", vm.Review!.CommandText);
        Assert.DoesNotContain(vm.Review.Warnings, w => w.Title == "The judge is off");
    }

    [Fact]
    public async Task Judge_remove_is_reviewed_at_the_tier_the_classifier_gives_remove_and_a_bad_timeout_blocks_add()
    {
        var (vm, _) = await Loaded();

        vm.RemoveJudgeCommand.Execute("claudecode");
        Assert.Equal("defenseclaw guardrail judge remove claudecode", vm.Review!.CommandText);
        Assert.Equal(CommandTier.Destructive, vm.Review.Tier);
        vm.CancelReviewCommand.Execute(null);

        vm.JudgeTimeoutText = "soon";
        Assert.True(vm.HasTimeoutProblem);
        vm.AddJudgeCommand.Execute("codex");
        Assert.False(vm.IsReviewOpen);
    }

    [Fact]
    public async Task Nothing_runs_until_the_review_is_confirmed_and_a_confirmed_write_is_followed_by_a_re_read()
    {
        var (vm, cli) = await Loaded();
        vm.TurnHiltOffCommand.Execute(null);
        Assert.Empty(cli.Writes);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "guardrail hilt off --yes" }, cli.Writes);
        Assert.False(vm.IsReviewOpen);
        Assert.True(vm.HasResult);
        Assert.Equal("exit 0", vm.ResultBadge);
        Assert.Equal(8, cli.Reads.Count);
    }

    [Fact]
    public async Task A_failed_write_shows_the_exit_code_and_does_not_re_read()
    {
        var (vm, cli) = await Loaded(c => c.WriteExitCode = 1);
        vm.TurnHiltOffCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("exit 1", vm.ResultBadge);
        Assert.Equal("Bad", vm.ResultKey);
        Assert.Equal(4, cli.Reads.Count);
    }

    [Fact]
    public async Task Cancelling_the_review_runs_nothing()
    {
        var (vm, cli) = await Loaded();
        vm.TurnHiltOnCommand.Execute(null);

        vm.CancelReviewCommand.Execute(null);

        Assert.False(vm.IsReviewOpen);
        Assert.Empty(cli.Writes);
    }

    [Fact]
    public async Task A_cli_that_cannot_be_found_is_reported_not_thrown()
    {
        var vm = new GuardrailControlsViewModel((_, _) => throw new CliNotFoundException("defenseclaw", Array.Empty<string>()));

        await vm.RefreshAsync();

        Assert.True(vm.HasError);
        Assert.Contains("was not found", vm.Error, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_read_that_exits_non_zero_is_an_error_with_its_last_lines()
    {
        var vm = new GuardrailControlsViewModel((argv, _) =>
        {
            var invocation = InvocationFactory.Create(argv: argv.ToArray());
            InvocationFactory.Append(invocation, "Error: broken", CliStream.StandardError);
            InvocationFactory.Finish(invocation, 2);
            return Task.FromResult(invocation);
        });

        await vm.RefreshAsync();

        Assert.True(vm.HasError);
        Assert.Contains("exited 2", vm.Error, StringComparison.Ordinal);
        Assert.Contains("Error: broken", vm.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_the_parsers_cannot_read_is_said_so_instead_of_shown_as_a_setting()
    {
        var (vm, _) = await Loaded(c =>
        {
            c.Hilt = "something unexpected";
            c.Message = "something unexpected";
            c.Judge = "something unexpected";
        });

        Assert.Contains("could not be read", vm.HiltSummary, StringComparison.Ordinal);
        Assert.Equal("Warn", vm.HiltKey);
        Assert.Contains("could not be read", vm.CurrentBlockMessage, StringComparison.Ordinal);
        Assert.Contains("could not be read", vm.JudgeSummary, StringComparison.Ordinal);
    }
}
