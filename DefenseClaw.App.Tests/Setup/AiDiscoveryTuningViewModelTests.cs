using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The AI Discovery tuning dialog (CUST-271) over a scratch composition and a script for the CLI: the form starts from config.yaml, only what
/// changed is sent, the command is visible before it runs, flags the installed CLI does not list are never sent, and the guards come in
/// the order of <see cref="SetupDialogViewModel"/>. No process starts; the help screens are the installed 0.8.10 CLI's own captures.
/// </summary>
public sealed class AiDiscoveryTuningViewModelTests : IDisposable
{
    private const string On = "ai_discovery:\n  enabled: true\n";

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

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", name));

    private static Task<HelpProbeResult> Screens(IReadOnlyList<string> path, CancellationToken _) =>
        Task.FromResult(new HelpProbeResult(Fixture(path[^1] == "disable" ? "agent-discovery-disable.txt" : "agent-discovery-enable.txt"), null));

    private sealed class Script
    {
        public List<string> Ran { get; } = new();

        public int Exit { get; set; }

        public Action? Effect { get; set; }

        public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
        {
            Ran.Add(executable + " " + string.Join(' ', argv));
            if (Exit == 0)
            {
                Effect?.Invoke();
            }

            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            InvocationFactory.Finish(invocation, Exit);
            return Task.FromResult(invocation);
        }
    }

    private (AiDiscoveryTuningViewModel Vm, AppServices Services, Script Cli) Dialog(
        string yaml = On,
        InstallationContext? installation = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>>? help = null,
        List<string>? applied = null)
    {
        var services = TestServices.Create(_temp, configYaml: yaml, installation: installation);
        _services.Add(services);
        var cli = new Script();
        var vm = new AiDiscoveryTuningViewModel(
            services,
            help ?? Screens,
            applied is null ? null : (result, argv) =>
            {
                applied.Add(string.Join(' ', argv));
                return Task.CompletedTask;
            });
        vm.Review.RunStep = cli.Step;
        return (vm, services, cli);
    }

    private static async Task<AiDiscoveryTuningViewModel> Opened(AiDiscoveryTuningViewModel vm)
    {
        vm.Open();
        await WaitAsync(() => vm.IsHelpReady);
        return vm;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // ---- seeded from config ----

    [Fact]
    public async Task Opening_starts_the_form_from_config_and_nothing_has_changed()
    {
        var (vm, _, cli) = Dialog("ai_discovery:\n  enabled: true\n  mode: passive\n  scan_interval_min: 7\n  include_shell_history: false\n");

        await Opened(vm);

        Assert.True(vm.IsOpen);
        Assert.True(vm.Enabled);
        Assert.Equal("passive", vm.Mode);
        Assert.Equal("7", vm.ScanInterval.Text);
        Assert.Equal("60", vm.ProcessInterval.Text);
        Assert.Equal("~", vm.ScanRoots.Text);
        Assert.False(vm.SourceSwitches.Single(s => s.Key == "include_shell_history").IsOn);
        Assert.True(vm.SourceSwitches.Single(s => s.Key == "include_env_var_names").IsOn);
        Assert.Equal("AI discovery: on  ·  Mode: passive", vm.StateLine);
        Assert.False(vm.HasChanges);
        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.Equal("Nothing to apply: nothing differs from config.yaml.", vm.ReviewBlockedReason);
        Assert.Equal(string.Empty, vm.CommandPreview);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_config_that_says_nothing_seeds_the_runtimes_defaults_with_discovery_off()
    {
        var (vm, _, _) = Dialog(string.Empty);

        await Opened(vm);

        Assert.False(vm.Enabled);
        Assert.Equal("enhanced", vm.Mode);
        Assert.Equal("5", vm.ScanInterval.Text);
        Assert.Equal("1000", vm.MaxFiles.Text);
        Assert.Equal("524288", vm.MaxBytes.Text);
        Assert.False(vm.HasChanges);
        Assert.False(vm.TuningActive);
        Assert.True(vm.ShowTuningOffNote);
    }

    // ---- the acceptance: a cadence change, reviewed and run, argv visible first ----

    [Fact]
    public async Task A_cadence_change_shows_its_command_in_the_form_and_in_the_review_and_runs_that_command()
    {
        var applied = new List<string>();
        var (vm, _, cli) = Dialog(applied: applied);
        await Opened(vm);

        vm.ScanInterval.Text = "10";

        Assert.True(vm.HasChanges);
        Assert.Equal("1 change to apply", vm.Summary);
        Assert.Equal("defenseclaw agent discovery enable --yes --scan-interval-min 10", vm.CommandPreview);
        Assert.Equal("was 5", vm.ScanInterval.ChangeText);
        Assert.Null(vm.ReviewBlockedReason);
        Assert.Empty(cli.Ran); // nothing runs from a field

        vm.ReviewChangesCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal("defenseclaw agent discovery enable --yes --scan-interval-min 10", step.CommandText);
        Assert.Empty(cli.Ran); // visible before it runs

        cli.Effect = () => File.WriteAllText(_temp.File("config.yaml"), "ai_discovery:\n  enabled: true\n  scan_interval_min: 10\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw agent discovery enable --yes --scan-interval-min 10" }, cli.Ran);
        Assert.Equal("Ok", vm.ResultKey);
        Assert.Contains("Scan interval (minutes)", vm.ResultText, StringComparison.Ordinal);
        Assert.StartsWith("Applied:", vm.ResultText, StringComparison.Ordinal);
        Assert.Equal("10", vm.ScanInterval.Text); // read back from the file
        Assert.False(vm.HasChanges);
        Assert.Equal(new[] { "agent discovery enable --yes --scan-interval-min 10" }, applied);
    }

    [Fact]
    public async Task Unticking_restart_and_scan_adds_the_flags_and_the_review_warns()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "15";

        vm.RestartAfter = false;

        Assert.Equal("defenseclaw agent discovery enable --yes --scan-interval-min 15 --no-restart --no-scan", vm.CommandPreview);
        vm.ReviewChangesCommand.Execute(null);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Gateway not restarted");
    }

    [Fact]
    public async Task Several_changes_are_one_command_with_only_the_changed_flags_and_scan_roots_join_with_commas()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.Mode = "passive";
        vm.ScanRoots.Text = "~/src\r\nD:\\work";
        vm.SourceSwitches.Single(s => s.Key == "include_shell_history").IsOn = false;

        Assert.Equal(3, vm.ChangeLines.Count);
        Assert.Equal("3 changes to apply", vm.Summary);
        Assert.Equal(
            "defenseclaw agent discovery enable --yes --mode passive --scan-roots ~/src,D:\\work --no-include-shell-history",
            vm.CommandPreview);
    }

    // ---- on and off ----

    [Fact]
    public async Task Turning_discovery_on_is_enable_with_whatever_else_changed()
    {
        var (vm, _, _) = Dialog(string.Empty);
        await Opened(vm);

        vm.Enabled = true;
        Assert.Equal("Turn on AI Discovery", vm.Summary);
        Assert.Equal("defenseclaw agent discovery enable --yes", vm.CommandPreview);

        vm.ScanInterval.Text = "20";
        Assert.StartsWith("Turn on AI Discovery with 1 change", vm.Summary, StringComparison.Ordinal);
        Assert.Equal("defenseclaw agent discovery enable --yes --scan-interval-min 20", vm.CommandPreview);
    }

    [Fact]
    public async Task Turning_discovery_off_is_disable_and_does_not_send_the_settings()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.ScanInterval.Text = "30";
        vm.Enabled = false;

        Assert.Equal("Turn off AI Discovery", vm.Summary);
        Assert.Equal("defenseclaw agent discovery disable --yes", vm.CommandPreview);
        vm.ReviewChangesCommand.Execute(null);
        Assert.Equal("Turn off AI Discovery?", vm.Review.CommandReview!.Title);
    }

    [Fact]
    public async Task A_discovery_that_stays_off_has_nothing_to_apply_even_if_settings_were_typed()
    {
        var (vm, _, _) = Dialog(string.Empty);
        await Opened(vm);

        vm.ScanInterval.Text = "30";

        Assert.False(vm.HasChanges);
        Assert.Equal(string.Empty, vm.CommandPreview);
    }

    // ---- values the CLI would refuse ----

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-3")]
    public async Task A_value_the_cli_would_refuse_is_marked_and_turns_review_off(string text)
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);

        vm.ScanInterval.Text = text;

        Assert.True(vm.ScanInterval.HasProblem);
        Assert.True(vm.HasProblems);
        Assert.StartsWith("Fix the values marked above first: ", vm.ReviewBlockedReason, StringComparison.Ordinal);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        vm.ReviewChangesCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_folder_with_a_comma_is_refused_because_the_cli_would_split_it()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.ScanRoots.Text = "~/a,b";

        Assert.True(vm.ScanRoots.HasProblem);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
    }

    // ---- flags are checked, never assumed ----

    [Fact]
    public async Task A_flag_the_installed_cli_does_not_list_is_unavailable_and_never_sent()
    {
        var older = Fixture("agent-discovery-enable.txt")
            .Replace("--scan-interval-min", "--scan-every", StringComparison.Ordinal);
        var (vm, _, _) = Dialog(help: (path, _) => Task.FromResult(new HelpProbeResult(
            path[^1] == "disable" ? Fixture("agent-discovery-disable.txt") : older, null)));
        await Opened(vm);

        Assert.False(vm.ScanInterval.IsAvailable);
        Assert.True(vm.ScanInterval.IsUnavailable);
        Assert.True(vm.ProcessInterval.IsAvailable);

        vm.ScanInterval.Text = "10";
        Assert.False(vm.HasChanges);
        Assert.Equal(string.Empty, vm.CommandPreview);

        vm.ProcessInterval.Text = "30";
        Assert.Equal("defenseclaw agent discovery enable --yes --process-interval-s 30", vm.CommandPreview);
    }

    [Fact]
    public async Task A_help_that_cannot_be_read_leaves_review_off_with_the_reason()
    {
        var (vm, _, cli) = Dialog(help: (_, _) => Task.FromResult(new HelpProbeResult(string.Empty, "timed out")));
        vm.Open();
        await WaitAsync(() => vm.HasHelpProblem);
        vm.ScanInterval.Text = "10";

        Assert.Contains("timed out", vm.HelpProblem, StringComparison.Ordinal);
        Assert.Equal(vm.HelpProblem, vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_cli_whose_enable_has_no_yes_is_refused_because_it_would_stop_at_a_question()
    {
        var (vm, _, _) = Dialog(help: (_, _) => Task.FromResult(new HelpProbeResult(
            "Usage: defenseclaw agent discovery enable [OPTIONS]\n\nOptions:\n  --mode [passive|enhanced]  Mode.\n", null)));
        vm.Open();
        await WaitAsync(() => vm.HasHelpProblem);

        Assert.Contains("--yes", vm.HelpProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cli_without_no_restart_cannot_skip_the_restart()
    {
        var older = Fixture("agent-discovery-enable.txt")
            .Replace("--no-restart", "--no-bounce", StringComparison.Ordinal)
            .Replace("--restart", "--bounce", StringComparison.Ordinal);
        var (vm, _, _) = Dialog(help: (path, _) => Task.FromResult(new HelpProbeResult(
            path[^1] == "disable" ? Fixture("agent-discovery-disable.txt") : older, null)));
        await Opened(vm);

        Assert.False(vm.CanSkipRestart);
    }

    // ---- guards ----

    [Fact]
    public async Task A_managed_installation_gives_its_reason_first()
    {
        var (vm, _, cli) = Dialog(installation: TestInstallations.Managed(_temp.Path));
        await Opened(vm);
        vm.ScanInterval.Text = "0";

        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason); // before the bad value and before nothing-to-apply
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanToggleEnabled);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task The_installation_becoming_managed_while_the_review_is_open_refuses_the_run()
    {
        var (vm, services, cli) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "10";
        vm.ReviewChangesCommand.Execute(null);

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_config_that_changed_after_it_was_read_makes_the_settings_stale_and_refuses_the_run()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "10";
        vm.ReviewChangesCommand.Execute(null);

        File.WriteAllText(_temp.File("config.yaml"), "ai_discovery:\n  enabled: true\n  scan_interval_min: 99\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Ran);
        Assert.NotNull(vm.StaleReason);
    }

    [Fact]
    public async Task A_failed_run_says_not_applied_and_keeps_the_typed_values_out_of_the_applied_text()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "10";
        vm.ReviewChangesCommand.Execute(null);

        cli.Exit = 1;
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", vm.ResultKey);
        Assert.StartsWith("Not applied.", vm.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Applied:", vm.ResultText, StringComparison.Ordinal);
    }

    // ---- warnings ----

    [Fact]
    public async Task Storing_raw_paths_and_workspace_signatures_each_get_a_warning_in_the_review()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.PrivacySwitches.Single(s => s.Key == "store_raw_local_paths").IsOn = true;
        vm.PrivacySwitches.Single(s => s.Key == "allow_workspace_signatures").IsOn = true;
        vm.ReviewChangesCommand.Execute(null);

        var titles = vm.Review.CommandReview!.Warnings.Select(w => w.Title).ToArray();
        Assert.Contains("Stores raw local paths", titles);
        Assert.Contains("Workspace signatures", titles);
    }

    [Fact]
    public async Task Refresh_forgets_typed_values_and_reads_the_file_again()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "10";

        File.WriteAllText(_temp.File("config.yaml"), "ai_discovery:\n  enabled: true\n  scan_interval_min: 3\n");
        vm.RefreshCommand.Execute(null);

        Assert.Equal("3", vm.ScanInterval.Text);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task Esc_closes_the_review_first_and_then_the_dialog()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);
        vm.ScanInterval.Text = "10";
        vm.ReviewChangesCommand.Execute(null);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.IsOpen);
        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsOpen);
        Assert.False(vm.HandleEscape());
    }
}
