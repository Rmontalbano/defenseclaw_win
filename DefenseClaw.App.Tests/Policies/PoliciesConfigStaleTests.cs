using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Govern;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// Stale-data gating (CUST-312) on both surfaces of the Policies panel: the table of named policies on 0.8.10 and the policy model on a runtime
/// that has it. Data read before <c>config.yaml</c> or <c>.env</c> changed keeps its rows and turns every change off with the reason; a change
/// requested after the edit is refused, and so is a review that was open when it happened - from the click, and again when the operator
/// confirms. The CLI is a script and no process starts.
/// </summary>
public sealed class PoliciesConfigStaleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly ConfigEdits _files;

    public PoliciesConfigStaleTests()
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: ProbeRunner(Pinned));
        _files = new ConfigEdits(_services);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ the table of named policies (0.8.10)

    private static CliInvocation Result(int exit, IReadOnlyList<string> argv, string stdout)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, stdout);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private sealed class Table
    {
        public List<string> Calls { get; } = new();

        public int Lists => Calls.Count(c => c == "policy list");

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            var text = string.Join(' ', argv);
            Calls.Add(text);
            return text switch
            {
                "policy list" => Result(0, argv, PolicyFixtures.ListReport),
                "policy validate" => Result(0, argv, "  OK data.json: OK\n  OK All validations passed."),
                _ when argv.Count == 3 && argv[1] == "show" => Result(0, argv, PolicyFixtures.Show(argv[2])),
                _ => throw new InvalidOperationException("a read-only door ran: " + text),
            };
        }
    }

    private async Task<(PoliciesPanelViewModel Vm, Table Cli, List<string> Ran)> OpenTableAsync()
    {
        var cli = new Table();
        var ran = new List<string>();
        var vm = new PoliciesPanelViewModel(_services) { RunCli = (argv, _) => Task.FromResult(cli.Handle(argv)) };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "ok"));
        };
        await vm.InitializeAsync();
        Assert.False(vm.UsesModel); // nothing was probed: the 0.8.10 table
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "strict");
        for (var i = 0; i < 2000 && (vm.IsDetailLoading || vm.Detail is null); i++)
        {
            await Task.Delay(10);
        }

        return (vm, cli, ran);
    }

    [Fact]
    public async Task The_table_after_a_config_reload_keeps_its_rows_and_turns_every_change_off_with_the_reason()
    {
        var (vm, cli, _) = await OpenTableAsync();
        vm.SetActive(true);
        try
        {
            Assert.True(vm.CanChange);
            Assert.True(vm.CanActivate);
            var names = vm.Rows.Select(r => r.Name).ToArray();
            var lists = cli.Lists;

            _files.EditConfig();
            _files.Reload();

            Assert.False(vm.Trust.IsTrusted);
            Assert.True(vm.Trust.IsStale);
            Assert.False(vm.CanChange);
            Assert.False(vm.CanActivate);
            Assert.False(vm.CanEdit);
            Assert.False(vm.CanDelete);
            Assert.False(vm.CanCreate);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ChangesBlockedReason);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ActivateTip);
            Assert.Equal(PoliciesState.Loaded, vm.State);
            Assert.Equal(names, vm.Rows.Select(r => r.Name).ToArray());
            Assert.Equal(lists, cli.Lists); // hearing about it reads nothing

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.True(vm.Trust.IsTrusted);
            Assert.True(vm.CanChange);
            Assert.True(vm.CanActivate);
            Assert.Equal(lists + 1, cli.Lists);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task The_table_refuses_a_change_requested_after_an_edit_the_watcher_has_not_reported()
    {
        var (vm, cli, _) = await OpenTableAsync();
        _files.EditConfig();
        Assert.True(vm.CanActivate); // nothing has told the buttons
        cli.Calls.Clear();

        await vm.ActivateCommand.ExecuteAsync(null);
        vm.CreateCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.False(vm.Form.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.NoticeMessage);
        Assert.Empty(cli.Calls);
        Assert.False(vm.CanActivate);
    }

    [Fact]
    public async Task The_table_does_not_run_a_review_that_was_open_when_the_config_changed_and_the_refusal_is_in_activity()
    {
        var (vm, _, ran) = await OpenTableAsync();
        await vm.ActivateCommand.ExecuteAsync(null);
        Assert.True(vm.Review.IsOpen);

        _files.EditEnv();
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.True(vm.Review.IsFinished);
        Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.NoticeMessage);
        Assert.False(vm.CanChange);

        var entry = Assert.Single(_services.Cli.Activity);
        Assert.Equal(new[] { "policy", "activate", "strict" }, entry.Argv);
        Assert.Null(entry.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);

        // Close the dialog, refresh, and the same activation is offered again and goes through.
        vm.Review.DismissCommand.Execute(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ActivateCommand.ExecuteAsync(null);
        Assert.True(vm.Review.IsOpen);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw policy activate strict" }, ran);
    }

    [Fact]
    public async Task The_table_finds_a_change_made_while_it_was_away_and_reads_again_when_it_comes_back()
    {
        var (vm, cli, _) = await OpenTableAsync();
        vm.SetActive(true);
        vm.SetActive(false);
        var lists = cli.Lists;

        _files.EditConfig();
        _files.Reload(); // nobody is listening
        Assert.True(vm.Trust.IsTrusted);

        vm.SetActive(true);
        try
        {
            UiThreadless.WaitFor(() => cli.Lists == lists + 1 && !vm.IsBusy);
            Assert.True(vm.Trust.IsTrusted);
            Assert.False(vm.Trust.IsStale);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task The_table_listens_to_config_reloads_only_while_the_panel_is_on_screen()
    {
        var (vm, _, _) = await OpenTableAsync();
        var baseline = ConfigEdits.Subscribers(_services);

        vm.SetActive(true);
        Assert.True(ConfigEdits.Subscribers(_services) > baseline);

        vm.SetActive(false);
        Assert.Equal(baseline, ConfigEdits.Subscribers(_services));
    }

    // ------------------------------------------------------------------ the policy model (the pinned runtime)

    private async Task<(PolicyModelViewModel Vm, ScriptedCli Cli, List<string> Ran)> OpenModelAsync()
    {
        var cli = new ScriptedCli(Connectors);
        var ran = new List<string>();
        var vm = new PolicyModelViewModel(_services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = cli.Run };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "ok"));
        };
        await vm.InitializeAsync();
        return (vm, cli, ran);
    }

    private static PolicyActionViewModel Act(PolicyModelViewModel vm, string group, string title) =>
        vm.ActionGroups.Single(g => g.Name == group).Actions.Single(a => a.Title == title);

    private static void Pick(PolicyModelViewModel vm, string key) => vm.SelectedRow = vm.Rows.Single(r => r.Key == key);

    [Fact]
    public async Task The_model_after_a_config_reload_keeps_its_rows_and_says_the_config_moved_in_its_own_words()
    {
        var (vm, cli, _) = await OpenModelAsync();
        Pick(vm, "global");
        Assert.True(vm.CanChange);
        var rows = vm.Rows.Select(r => r.Key).ToArray();

        // A review is open, which holds the automatic read back until it is closed, so the state of the data is what shows.
        await vm.RunActionCommand.ExecuteAsync(Act(vm, "Mode", "Log only (observe)"));
        Assert.True(vm.Review.IsOpen);
        vm.SetActive(true);
        try
        {
            cli.Calls.Clear();

            _files.EditConfig();
            _files.Reload();

            Assert.True(vm.Trust.IsStale);
            Assert.False(vm.CanChange);
            Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), vm.ChangesBlockedReason);
            Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), vm.ActionsNote);
            Assert.Equal(rows, vm.Rows.Select(r => r.Key).ToArray());
            Assert.Empty(cli.Calls);
        }
        finally
        {
            vm.Review.HandleEscape(); // closing it makes the panel read again (see the next test); let that finish before the services go
            UiThreadless.WaitFor(() => !vm.IsBusy);
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task The_model_does_not_run_a_review_confirmed_after_the_config_changed_and_reads_again_when_it_is_closed()
    {
        var (vm, cli, ran) = await OpenModelAsync();
        vm.SetActive(true);
        try
        {
            Pick(vm, "claudecode");
            await vm.RunActionCommand.ExecuteAsync(Act(vm, "Tool-call alert level", "LOW+"));
            Assert.True(vm.Review.IsOpen);

            _files.EditConfig();                 // no reload yet: the files are what says so
            await vm.Review.ConfirmCommand.ExecuteAsync(null);

            Assert.Empty(ran);
            Assert.True(vm.Review.IsFinished);
            Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
            Assert.Equal("Changes are off", vm.NoticeTitle);
            Assert.False(vm.CanChange);
            var entry = Assert.Single(_services.Cli.Activity);
            Assert.Equal(new[] { "guardrail", "alert-at", "LOW", "--connector", "claudecode" }, entry.Argv);
            Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);

            // This panel reads again by itself when the dialog closes (a stale answer is not left on screen), and the data is good again.
            cli.Calls.Clear();
            vm.Review.DismissCommand.Execute(null);
            UiThreadless.WaitFor(() => cli.Calls.Count >= 4 && !vm.IsBusy);
            Assert.True(vm.CanChange);
            Assert.False(vm.Trust.IsStale);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task After_a_change_the_settings_are_not_to_be_acted_on_until_they_are_read_again_even_when_no_watched_file_moved()
    {
        // A change need not touch config.yaml or .env (a policy file, a pack folder), so the files cannot be what says the settings moved: the
        // panel marks them stale itself, and the fresh read that follows is what gives them back.
        var (vm, cli, ran) = await OpenModelAsync();
        Pick(vm, "claudecode");
        await vm.RunActionCommand.ExecuteAsync(Act(vm, "Tool-call alert level", "LOW+"));
        Assert.True(vm.Review.IsOpen);

        var gate = new TaskCompletionSource();
        vm.RunCli = async (argv, _) =>
        {
            await gate.Task;
            return cli.Handle(argv);
        };

        var confirmed = vm.Review.ConfirmCommand.ExecuteAsync(null); // runs the scripted change, then reads again - held at the gate
        UiThreadless.WaitFor(() => vm.Trust.IsStale);

        Assert.Single(ran);
        Assert.False(vm.CanChange);
        Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), vm.ChangesBlockedReason);
        Assert.False(confirmed.IsCompleted);

        gate.SetResult();
        await confirmed;

        Assert.False(vm.Trust.IsStale);
        Assert.True(vm.CanChange);
        Assert.Equal("Done", vm.NoticeTitle);
    }

    [Fact]
    public async Task The_model_refuses_a_change_requested_after_an_edit_the_watcher_has_not_reported()
    {
        var (vm, cli, _) = await OpenModelAsync();
        Pick(vm, "global");
        var observe = Act(vm, "Mode", "Log only (observe)");
        Assert.True(observe.IsEnabled);

        _files.EditEnv();
        cli.Calls.Clear();
        await vm.RunActionCommand.ExecuteAsync(observe);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), vm.NoticeMessage);
        Assert.Empty(cli.Calls);
        Assert.False(observe.IsEnabled);
    }
}
