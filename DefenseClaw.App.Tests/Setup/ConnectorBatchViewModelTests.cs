using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The connector batch dialog (CUST-271): the form starts from the roster in config.yaml (so doing nothing drops nothing), the command carries only
/// flags the installed CLI lists, the roster-replacing consequence is a warning before it runs, and the guards come in the shared order. The help
/// screen is the 0.8.10 CLI's own <c>setup --help</c> (<c>Fixtures/runtime-0.8.10/setup.txt</c>); no process starts.
/// </summary>
public sealed class ConnectorBatchViewModelTests : IDisposable
{
    private const string Roster = "guardrail:\n  connectors:\n    codex:\n      mode: observe\n";

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

    private static string SetupHelp() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt"));

    private static readonly IReadOnlyList<OfferedConnector> Connectors = new[]
    {
        new OfferedConnector("codex", "Codex", "codex", PlatformStatus.Certified),
        new OfferedConnector("claudecode", "Claude Code", "claude-code", PlatformStatus.Certified),
        new OfferedConnector("cursor", "Cursor", "cursor", PlatformStatus.NotCertified),
    };

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

    private (ConnectorBatchViewModel Vm, AppServices Services, Script Cli) Dialog(
        string yaml = Roster,
        InstallationContext? installation = null,
        string? help = null,
        string? helpError = null)
    {
        var services = TestServices.Create(_temp, configYaml: yaml, installation: installation);
        _services.Add(services);
        var cli = new Script();
        var vm = new ConnectorBatchViewModel(
            services,
            _ => Task.FromResult(new HelpProbeResult(help ?? SetupHelp(), helpError)),
            () => Connectors);
        vm.Review.RunStep = cli.Step;
        return (vm, services, cli);
    }

    private static async Task<ConnectorBatchViewModel> Opened(ConnectorBatchViewModel vm)
    {
        vm.Open();
        for (var i = 0; i < 100 && vm.IsChecking; i++)
        {
            await Task.Delay(10);
        }

        return vm;
    }

    private static BatchRow Row(ConnectorBatchViewModel vm, string id) => vm.Rows.Single(r => r.Id == id);

    [Fact]
    public async Task The_form_starts_with_the_configured_connectors_ticked_and_nothing_to_apply()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);

        Assert.Equal(new[] { "codex", "claudecode", "cursor" }, vm.Rows.Select(r => r.Id));
        Assert.Equal(new[] { true, false, false }, vm.Rows.Select(r => r.IsOn));
        Assert.Equal("Configured now (observe)", Row(vm, "codex").StateText);
        Assert.Equal("Configured now: Codex.", vm.RosterLine);
        Assert.False(vm.HasChanges);
        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.StartsWith("Nothing to apply:", vm.ReviewBlockedReason, StringComparison.Ordinal);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task Ticking_a_second_connector_is_one_batch_command_with_the_roster_kept()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);

        Row(vm, "claudecode").IsOn = true;

        Assert.Equal("defenseclaw setup --yes --connector codex --connector claudecode --mode observe", vm.CommandPreview);
        Assert.Equal("1 change to apply", vm.Summary);
        Assert.Equal(new[] { "Add Claude Code in observe mode" }, vm.ChangeLines);

        vm.ReviewChangesCommand.Execute(null);
        Assert.Equal("defenseclaw setup --yes --connector codex --connector claudecode --mode observe", Assert.Single(vm.Review.CommandReview!.Steps).CommandText);
        Assert.Empty(cli.Ran);

        cli.Effect = () => File.WriteAllText(_temp.File("config.yaml"), Roster + "    claudecode:\n      mode: observe\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(cli.Ran);
        Assert.Equal("Ok", vm.ResultKey);
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.IsOn));
        Assert.Contains("Claude Code", vm.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unticking_a_configured_connector_says_it_will_be_dropped_in_the_form_and_in_the_review()
    {
        var (vm, _, _) = Dialog(Roster + "    claudecode:\n      mode: observe\n");
        await Opened(vm);

        Row(vm, "claudecode").IsOn = false;

        Assert.Equal("will be dropped", Row(vm, "claudecode").ChangeText);
        Assert.Contains("Drop Claude Code", vm.ChangeLines);
        vm.ReviewChangesCommand.Execute(null);
        var warning = Assert.Single(vm.Review.CommandReview!.Warnings, w => w.Title == "Drops configured connectors");
        Assert.Contains("Claude Code", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_only_the_mode_of_a_configured_connector_is_a_change()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.Mode = "action";

        Assert.True(vm.HasChanges);
        Assert.Equal("defenseclaw setup --yes --connector codex --mode action", vm.CommandPreview);
        Assert.Contains("Codex: observe to action", vm.ChangeLines);
        vm.ReviewChangesCommand.Execute(null);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Action mode can block");
    }

    [Theory]
    [InlineData(1, "--detected")]
    [InlineData(2, "--all")]
    public async Task Detected_and_all_are_the_clis_own_flags(int index, string flag)
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);

        vm.ExtraIndex = index;

        Assert.Equal($"defenseclaw setup --yes --connector codex {flag} --mode observe", vm.CommandPreview);
        vm.ReviewChangesCommand.Execute(null);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Sets the roster");
    }

    [Fact]
    public async Task Nothing_ticked_and_nothing_added_leaves_review_off_because_the_cli_would_open_a_picker()
    {
        var (vm, _, cli) = Dialog(string.Empty);
        await Opened(vm);

        Assert.StartsWith("Tick at least one connector", vm.ReviewBlockedReason, StringComparison.Ordinal);
        vm.ReviewChangesCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task Unticking_restart_adds_no_restart_and_a_warning()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;

        vm.RestartAfter = false;

        Assert.EndsWith("--mode observe --no-restart", vm.CommandPreview, StringComparison.Ordinal);
        vm.ReviewChangesCommand.Execute(null);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Gateway not restarted");
    }

    [Fact]
    public async Task A_connector_that_is_not_certified_on_windows_is_warned_about()
    {
        var (vm, _, _) = Dialog();
        await Opened(vm);
        Row(vm, "cursor").IsOn = true;

        vm.ReviewChangesCommand.Execute(null);

        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Not certified on Windows" && w.Message.Contains("Cursor", StringComparison.Ordinal));
    }

    // ---- flags are checked ----

    [Fact]
    public async Task A_cli_whose_setup_has_no_batch_flags_leaves_review_off_with_the_reason()
    {
        var (vm, _, cli) = Dialog(help: "Usage: defenseclaw setup [OPTIONS] [COMMAND]\n\nOptions:\n  --help  Show this message and exit.\n");
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;

        Assert.True(vm.HasHelpProblem);
        Assert.Contains("--connector", vm.HelpProblem, StringComparison.Ordinal);
        Assert.Equal(vm.HelpProblem, vm.ReviewBlockedReason);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_cli_without_detected_blocks_that_choice_only()
    {
        var older = SetupHelp().Replace("--detected", "--found", StringComparison.Ordinal);
        var (vm, _, _) = Dialog(help: older);
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;

        Assert.False(vm.CanChooseDetected);
        Assert.True(vm.CanChooseAll);
        vm.ExtraIndex = 1;
        Assert.Equal("The installed DefenseClaw's setup does not list --detected.", vm.ReviewBlockedReason);
    }

    [Fact]
    public async Task A_help_that_cannot_be_read_leaves_review_off()
    {
        var (vm, _, _) = Dialog(help: string.Empty, helpError: "timed out");
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;

        Assert.Contains("timed out", vm.ReviewBlockedReason, StringComparison.Ordinal);
    }

    // ---- guards ----

    [Fact]
    public async Task A_managed_installation_gives_its_reason_first()
    {
        var (vm, _, cli) = Dialog(installation: TestInstallations.Managed(_temp.Path));
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;

        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task The_installation_becoming_managed_while_the_review_is_open_refuses_the_run()
    {
        var (vm, services, cli) = Dialog();
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;
        vm.ReviewChangesCommand.Execute(null);

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_config_that_changed_after_the_list_was_read_refuses_the_run()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;
        vm.ReviewChangesCommand.Execute(null);

        File.WriteAllText(_temp.File("config.yaml"), string.Empty);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Ran);
        Assert.NotNull(vm.StaleReason);
    }

    [Fact]
    public async Task A_failed_run_says_not_applied()
    {
        var (vm, _, cli) = Dialog();
        await Opened(vm);
        Row(vm, "claudecode").IsOn = true;
        vm.ReviewChangesCommand.Execute(null);

        cli.Exit = 1;
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", vm.ResultKey);
        Assert.StartsWith("Not applied.", vm.ResultText, StringComparison.Ordinal);
    }
}
