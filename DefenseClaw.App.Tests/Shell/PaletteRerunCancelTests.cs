using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette's "Re-run last command" and "Cancel running command" (CUST-264; the TUI's <c>!</c> and Ctrl+C, over the Activity list). The rows say
/// which command they would act on, or why they cannot; Re-run opens the shared review and runs only what is confirmed; Cancel stops the newest run
/// that can be stopped. The review, the toast and the runner behind Re-run are test seams, so nothing here starts a DefenseClaw command; the one
/// real child is a <c>ping</c> under <c>cmd.exe</c>, ended by the test, as in <c>ActivityClearKeepsRunningTests</c>.
/// </summary>
public sealed class PaletteRerunCancelTests : IDisposable
{
    /// <summary>How long a wait for a child may take before it is called hung: generous, because it costs nothing when things are healthy.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("CI") is { Length: > 0 } ? 300 : 120);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly ShellActions _actions;
    private readonly PanelCatalog _catalog;
    private readonly List<string> _toasts = new();
    private readonly List<CommandReview> _reviews = new();
    private readonly List<(string Tool, string[] Argv)> _ran = new();
    private readonly List<CliInvocation> _activity = new();
    private bool _confirm = true;

    public PaletteRerunCancelTests()
    {
        _services = TestServices.Create(_temp);
        _catalog = new PanelCatalog(_services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        _actions = new ShellActions(_services, _catalog, tray, () => null)
        {
            Toast = (title, message) => _toasts.Add(title + ": " + message),
            Confirmer = review =>
            {
                _reviews.Add(review);
                return _confirm;
            },
        };
        _actions.Rerun.Runner = (tool, argv, _) =>
        {
            _ran.Add((tool, argv.ToArray()));
            var again = InvocationFactory.CreateFor(tool, argv.ToArray());
            InvocationFactory.Finish(again, 0);
            return Task.FromResult(again);
        };
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private IReadOnlyList<ShellCommand> Palette() => ShellCommandRegistry.Build(_catalog, _actions, _ => { }, () => { });

    private ShellCommand Row(string id) => Assert.Single(Palette(), c => c.Id == id);

    /// <summary>An entry the palette sees as the runner's ring does: newest first.</summary>
    private CliInvocation Entry(string executable, int? exit, DateTimeOffset startedAt, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv, startedAt);
        if (exit is { } code)
        {
            InvocationFactory.Finish(invocation, code, startedAt.AddSeconds(1));
        }

        _activity.Insert(0, invocation);
        _actions.ActivitySource = () => _activity.ToArray();
        return invocation;
    }

    // ------------------------------------------------------------------ the rows

    [Fact]
    public void Both_rows_are_in_the_palette_under_Monitor_with_no_chord_and_say_why_when_there_is_nothing_to_act_on()
    {
        var rerun = Row(ShellCommandRegistry.RerunLastId);
        var cancel = Row(ShellCommandRegistry.CancelRunningId);

        Assert.Equal("Re-run last command", rerun.Title);
        Assert.Equal("Cancel running command", cancel.Title);
        Assert.All(new[] { rerun, cancel }, r => Assert.Equal(ShellCommandRegistry.MonitorCategory, r.Category));
        Assert.All(new[] { rerun, cancel }, r => Assert.Null(r.Shortcut));
        Assert.Null(rerun.Cli);

        Assert.False(rerun.IsEnabled);
        Assert.Equal("There is no command in Activity to run again yet.", rerun.DisabledReason);
        Assert.False(cancel.IsEnabled);
        Assert.Equal("No command is running.", cancel.DisabledReason);

        // A disabled row cannot be chosen: nothing is reviewed and nothing runs.
        var palette = new CommandPaletteViewModel();
        palette.Load(Palette());
        Assert.False(palette.Choose(palette.Results.Single(r => r.Command.Id == rerun.Id)));
        Assert.Empty(_reviews);
    }

    [Fact]
    public void The_rows_are_found_by_what_an_operator_would_type()
    {
        var rows = Palette();

        Assert.Equal("Re-run last command", CommandPaletteViewModel.Rank(rows, "rerun")[0].Title);
        Assert.Equal("Re-run last command", CommandPaletteViewModel.Rank(rows, "re-run")[0].Title);
        Assert.Equal("Re-run last command", CommandPaletteViewModel.Rank(rows, "rlc")[0].Title);
        Assert.Equal("Cancel running command", CommandPaletteViewModel.Rank(rows, "cancel")[0].Title);
        Assert.Equal("Cancel running command", CommandPaletteViewModel.Rank(rows, "crc")[0].Title);
        Assert.Contains(CommandPaletteViewModel.Rank(rows, "stop running"), r => r.Id == ShellCommandRegistry.CancelRunningId);
    }

    [Fact]
    public void Each_row_has_its_own_icon()
    {
        var rerun = DcSections.OfCommand(ShellCommandRegistry.RerunLastId);
        var cancel = DcSections.OfCommand(ShellCommandRegistry.CancelRunningId);

        Assert.NotEqual(Wpf.Ui.Controls.SymbolRegular.Circle24, rerun.Icon);
        Assert.NotEqual(Wpf.Ui.Controls.SymbolRegular.Circle24, cancel.Icon);
        Assert.NotEqual(rerun.Icon, cancel.Icon);
    }

    // ------------------------------------------------------------------ Re-run last command

    [Fact]
    public void The_row_names_the_newest_command_that_can_be_run_again()
    {
        var now = DateTimeOffset.UtcNow;
        _ = Entry("defenseclaw", 0, now.AddMinutes(-9), "skill", "list");
        _ = Entry("defenseclaw-gateway", 0, now.AddMinutes(-5), "restart");
        _ = Entry(@"C:\Tools\cosign.exe", 0, now.AddMinutes(-1), "version");

        var row = Row(ShellCommandRegistry.RerunLastId);

        Assert.True(row.IsEnabled);
        Assert.Null(row.DisabledReason);
        Assert.Contains("defenseclaw-gateway restart", row.Description, StringComparison.Ordinal);
        Assert.Contains("Nothing runs until you confirm", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_it_opens_the_review_of_that_command_and_runs_it_only_once_confirmed()
    {
        _ = Entry("defenseclaw", 1, DateTimeOffset.UtcNow.AddMinutes(-2), "skill", "block", "--", "pdf-tools");

        _confirm = false;
        await _actions.RerunLastAsync();

        var declined = Assert.Single(_reviews);
        Assert.Equal("defenseclaw skill block -- pdf-tools", declined.CommandText);
        Assert.Equal(CommandTier.StateChanging, declined.Tier);
        Assert.Empty(_ran);
        Assert.Empty(_toasts);

        _confirm = true;
        await _actions.RerunLastAsync();

        Assert.Equal(2, _reviews.Count);
        var run = Assert.Single(_ran);
        Assert.Equal("defenseclaw", run.Tool);
        Assert.Equal(new[] { "skill", "block", "--", "pdf-tools" }, run.Argv);
        Assert.Equal(
            "Re-run last command: Ran defenseclaw skill block -- pdf-tools again: finished (exit 0). The new entry is at the top of the Activity list.",
            Assert.Single(_toasts));
    }

    [Fact]
    public void The_row_s_Run_is_the_same_thing_and_a_destructive_command_is_reviewed_as_destructive()
    {
        _ = Entry("defenseclaw", 0, DateTimeOffset.UtcNow.AddMinutes(-2), "skill", "quarantine", "--", "pdf-tools");

        Row(ShellCommandRegistry.RerunLastId).Run();

        var review = Assert.Single(_reviews);
        Assert.True(review.IsDestructive);
        Assert.Equal("Run destructive command", review.ConfirmLabel);
        Assert.Equal("Destructive", review.TierLabel);
        Assert.Equal(new[] { "skill", "quarantine", "--", "pdf-tools" }, Assert.Single(_ran).Argv);
    }

    [Fact]
    public void A_command_still_running_is_not_run_again_over_an_older_one_and_the_row_says_to_wait()
    {
        var now = DateTimeOffset.UtcNow;
        _ = Entry("defenseclaw", 0, now.AddMinutes(-9), "skill", "list");
        _ = Entry("defenseclaw", null, now.AddSeconds(-5), "doctor");

        var row = Row(ShellCommandRegistry.RerunLastId);

        Assert.False(row.IsEnabled);
        Assert.Contains("Wait for this command to finish, or cancel it", row.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_with_nothing_to_run_again_says_so_instead_of_opening_a_review()
    {
        await _actions.RerunLastAsync();

        Assert.Equal("Re-run last command: There is no command in Activity to run again yet.", Assert.Single(_toasts));
        Assert.Empty(_reviews);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task An_entry_that_carried_a_secret_is_never_the_last_command()
    {
        var now = DateTimeOffset.UtcNow;
        _ = Entry("defenseclaw", 0, now.AddMinutes(-3), "doctor");
        var withSecret = Entry("defenseclaw", 0, now.AddMinutes(-1), "keys", "set", "OPENAI_API_KEY");
        InvocationFactory.UseStdinSecret(withSecret);

        await _actions.RerunLastAsync();

        Assert.Equal(new[] { "doctor" }, Assert.Single(_ran).Argv);
    }

    // ------------------------------------------------------------------ Cancel running command

    [Fact]
    public void The_row_names_the_newest_run_that_can_be_stopped()
    {
        var now = DateTimeOffset.UtcNow;
        _ = Entry("defenseclaw", null, now.AddMinutes(-4), "agent", "discovery", "scan");
        _ = Entry("defenseclaw", 0, now.AddMinutes(-3), "doctor");
        _ = Entry("defenseclaw", null, now.AddMinutes(-2), "setup", "guardrail");

        var row = Row(ShellCommandRegistry.CancelRunningId);

        Assert.True(row.IsEnabled);
        Assert.Contains("defenseclaw setup guardrail", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_and_a_run_already_being_cancelled_cannot_be_stopped_again_and_the_row_says_why()
    {
        var installer = Entry("defenseclaw", null, DateTimeOffset.UtcNow.AddMinutes(-4), "upgrade");
        InvocationFactory.SurviveShutdown(installer);

        var row = Row(ShellCommandRegistry.CancelRunningId);
        Assert.False(row.IsEnabled);
        Assert.Contains("upgrade installer cannot be cancelled from here", row.DisabledReason, StringComparison.Ordinal);

        var stopping = Entry("defenseclaw", null, DateTimeOffset.UtcNow.AddMinutes(-1), "doctor");
        InvocationFactory.RequestCancel(stopping);
        row = Row(ShellCommandRegistry.CancelRunningId);
        Assert.False(row.IsEnabled);
        Assert.Contains("Already cancelling", row.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_run_besides_the_installer_is_still_cancellable()
    {
        var now = DateTimeOffset.UtcNow;
        var installer = Entry("defenseclaw", null, now.AddMinutes(-1), "upgrade");
        InvocationFactory.SurviveShutdown(installer);
        _ = Entry("defenseclaw", null, now.AddMinutes(-4), "doctor");

        var row = Row(ShellCommandRegistry.CancelRunningId);

        Assert.True(row.IsEnabled);
        Assert.Contains("defenseclaw doctor", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_it_for_a_run_the_runner_does_not_know_says_what_the_runner_says()
    {
        _ = Entry("defenseclaw", null, DateTimeOffset.UtcNow.AddMinutes(-1), "doctor");

        Row(ShellCommandRegistry.CancelRunningId).Run();

        // A synthetic entry is not in the runner's in-flight set, so the runner refuses with a sentence; the palette shows it.
        Assert.Equal(
            "Cancel running command: This invocation is not running under this runner, so it cannot be cancelled from here.",
            Assert.Single(_toasts));
    }

    [Fact]
    public async Task Choosing_it_stops_the_newest_real_run_with_everything_it_started()
    {
        // The runner's own ring this time: a ping under cmd.exe, the one real child, killed by the cancel (and by the finally if it is not).
        _actions.ActivitySource = null;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        CliInvocation? running = null;
        _services.Cli.InvocationStarted += (_, i) => running = i;
        var run = _services.Cli.RunExecutableAsync(cmd, new[] { "/c", "ping -n 60 127.0.0.1 >nul" });
        try
        {
            SpinWait.SpinUntil(() => running is { IsRunning: true }, Ceiling);
            Assert.NotNull(running);

            var row = Row(ShellCommandRegistry.CancelRunningId);
            Assert.True(row.IsEnabled);
            Assert.Contains("cmd /c", row.Description, StringComparison.Ordinal);

            row.Run();

            Assert.StartsWith("Cancel running command: Cancelling cmd /c", Assert.Single(_toasts), StringComparison.Ordinal);
            Assert.True(running!.CancelRequested);
            var finished = await run.WaitAsync(Ceiling);
            Assert.StartsWith("cancelled", finished.FailureReason, StringComparison.Ordinal);
            Assert.Null(finished.ExitCode);

            // Nothing is left to cancel.
            Assert.False(Row(ShellCommandRegistry.CancelRunningId).IsEnabled);
        }
        finally
        {
            if (running is { IsRunning: true })
            {
                _ = _services.Cli.Cancel(running, out _);
            }

            _ = _services.Cli.Shutdown(TimeSpan.FromSeconds(10));
            _ = await run.WaitAsync(Ceiling);
        }
    }
}
