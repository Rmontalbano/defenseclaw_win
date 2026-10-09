using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// Stale-data gating (CUST-312) on the Govern panels (Skills, MCPs, Plugins, Tools) and on Registries: a list read before <c>config.yaml</c> or
/// <c>.env</c> changed keeps its rows, turns every change off with the reason, and gets its say back from a fresh complete read; a change started
/// after the edit is refused when it is requested and again when it is confirmed, and the refusal is in Activity. The CLI is a script handed to
/// the view-model (<see cref="GovernPanelViewModelBase.RunCli"/>): no process starts, and every name and path is synthetic.
/// </summary>
public sealed class ConfigStalePanelTests : IDisposable
{
    private const string ToolRules = """[{"connector": "claudecode", "tools": [{"name": "delete_file", "connector": "claudecode", "scope": "connector", "status": "block"}]}]""";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly ConfigEdits _files;

    public ConfigStalePanelTests()
    {
        _services = TestServices.Create(_temp);
        _files = new ConfigEdits(_services);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ a scripted CLI

    private sealed class Cli
    {
        public List<string> Calls { get; } = new();

        public string ListJson { get; set; } = "[]";

        public int Lists => Calls.Count(c => c.EndsWith(" list --json", StringComparison.Ordinal));

        public IEnumerable<string> Mutations => Calls.Where(c => !c.EndsWith(" list --json", StringComparison.Ordinal) && !c.Contains(" info ", StringComparison.Ordinal));

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            Calls.Add(string.Join(' ', argv));
            return Result(0, argv, argv.Count >= 2 && argv[1] == "list" ? ListJson : "{}");
        }
    }

    private static CliInvocation Result(int exit, IReadOnlyList<string> argv, string stdout)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, stdout);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private (GovernPanelViewModelBase Vm, Cli Cli) Panel(string name)
    {
        var cli = new Cli();
        GovernPanelViewModelBase vm;
        switch (name)
        {
            case "skill":
                vm = new SkillsPanelViewModel(_services);
                cli.ListJson = """[{"name": "pdf-tools"}, {"name": "csv-tools"}]""";
                break;
            case "mcp":
                vm = new McpsPanelViewModel(_services);
                cli.ListJson = """[{"name": "docs", "connector": "claudecode"}, {"name": "wiki", "connector": "claudecode"}]""";
                break;
            case "plugin":
                vm = new PluginsPanelViewModel(_services);
                cli.ListJson = """[{"id": "p1", "connector": "claudecode"}, {"id": "p2", "connector": "claudecode"}]""";
                break;
            case "tool":
                vm = new ToolsPanelViewModel(_services);
                cli.ListJson = ToolRules;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }

        vm.RunCli = (argv, _) => Task.FromResult(cli.Handle(argv));
        if (vm is SkillsPanelViewModel or McpsPanelViewModel)
        {
            vm.ScannerFinder = _ => Task.FromResult<string?>(@"C:\scanners\scanner.exe");
        }

        return (vm, cli);
    }

    private static void Act(GovernPanelViewModelBase vm, GovernRow row, GovernVerbs verb) => ((IGovernRowHost)vm).OnRowAction(row, verb);

    // ------------------------------------------------------------------ Govern: a reload makes the list stale

    [Theory]
    [InlineData("skill")]
    [InlineData("mcp")]
    [InlineData("plugin")]
    [InlineData("tool")]
    public async Task After_a_complete_read_a_config_reload_keeps_the_rows_turns_changes_off_with_the_reason_and_a_refresh_restores_them(string panel)
    {
        var (vm, cli) = Panel(panel);
        await vm.InitializeAsync();
        vm.SetActive(true);
        try
        {
            var keys = vm.Rows.Select(r => r.Key).ToArray();
            var row = vm.Rows.First();
            var told = new List<string?>();
            row.PropertyChanged += (_, e) => told.Add(e.PropertyName);
            Assert.NotEmpty(keys);
            Assert.True(vm.IsDataTrusted);
            Assert.True(vm.CanChange);
            Assert.True(row.ChangesEnabled);
            Assert.Equal(1, cli.Lists);

            _files.EditConfig();
            _files.Reload();

            Assert.False(vm.IsDataTrusted);
            Assert.True(vm.Trust.IsStale);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DataUntrustedReason);
            Assert.False(vm.CanChange);
            Assert.False(vm.CanScanAll);

            // The rows are still there, and still information; what they cannot do any more is authorize a change.
            Assert.Equal(GovernState.Loaded, vm.State);
            Assert.Equal(keys, vm.Rows.Select(r => r.Key).ToArray());
            Assert.False(row.ChangesEnabled);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), row.ChangesBlockedReason);
            Assert.Contains(nameof(GovernRow.ChangesEnabled), told);
            Assert.Equal(1, cli.Lists); // hearing about it reads nothing: Refresh does

            // A fresh complete read gives it back.
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.True(vm.IsDataTrusted);
            Assert.False(vm.Trust.IsStale);
            Assert.Null(vm.DataUntrustedReason);
            Assert.True(vm.CanChange);
            Assert.True(vm.Rows.First().ChangesEnabled);
            Assert.Equal(2, cli.Lists);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_change_to_env_alone_does_the_same()
    {
        var (vm, _) = Panel("skill");
        await vm.InitializeAsync();
        vm.SetActive(true);
        try
        {
            _files.EditEnv();
            _files.Reload();

            Assert.False(vm.IsDataTrusted);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DataUntrustedReason);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Theory]
    [InlineData("config.yaml")]
    [InlineData(".env")]
    public async Task An_edit_to_either_file_reaches_an_active_panel_through_the_existing_watcher_with_no_reload_asked_for(string file)
    {
        // The watcher (a FileSystemWatcher with a poll backstop, ConfigChangeToken) watches both files and AppServices raises ConfigReloaded for either,
        // so a panel needs no watcher, timer or poll of its own: this is the real one, over the scratch directory.
        var (vm, _) = Panel("skill");
        await vm.InitializeAsync();
        vm.SetActive(true);
        try
        {
            Assert.True(vm.IsDataTrusted);

            if (file == "config.yaml")
            {
                _files.EditConfig();
            }
            else
            {
                _files.EditEnv();
            }

            UiThreadless.WaitFor(() => !vm.IsDataTrusted, timeoutMs: 60_000);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DataUntrustedReason);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_reload_that_changes_nothing_the_read_saw_changes_nothing()
    {
        var (vm, _) = Panel("skill");
        await vm.InitializeAsync();
        vm.SetActive(true);
        try
        {
            _files.Reload(); // an explicit reload with the files as they were read

            Assert.True(vm.IsDataTrusted);
            Assert.False(vm.Trust.IsStale);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_panel_that_re_reads_after_its_own_change_is_not_made_stale_by_the_watcher_speaking_late()
    {
        var (vm, cli) = Panel("mcp");
        await vm.InitializeAsync();
        vm.SetActive(true);
        try
        {
            _files.EditConfig();                           // a command this panel ran rewrote config.yaml ...
            await vm.RefreshCommand.ExecuteAsync(null);    // ... and the panel re-read straight after it ...
            Assert.True(vm.IsDataTrusted);
            Assert.Equal(2, cli.Lists);

            _files.Reload();                               // ... then the watcher's notification for that same edit arrives.

            Assert.True(vm.IsDataTrusted);
            Assert.False(vm.Trust.IsStale);
            Assert.Equal(2, cli.Lists);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_panel_that_was_away_finds_a_change_made_meanwhile_when_it_comes_back_and_reads_again()
    {
        var (vm, cli) = Panel("mcp");
        await vm.InitializeAsync();
        vm.SetActive(true);
        vm.SetActive(false);                  // the operator went to another panel
        var baseline = ConfigEdits.Subscribers(_services);

        _files.EditConfig();
        _files.Reload();                      // nobody is listening
        Assert.True(vm.IsDataTrusted);
        Assert.Equal(1, cli.Lists);

        vm.SetActive(true);                   // and came back
        try
        {
            // The one catch-up read of the visit: the list is known to be out of date, so it is read, like one that has gone old.
            UiThreadless.WaitFor(() => cli.Lists == 2 && !vm.IsBusy);
            Assert.True(vm.IsDataTrusted);
            Assert.False(vm.Trust.IsStale);
            Assert.Equal(baseline + 1, ConfigEdits.Subscribers(_services));
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_panel_listens_to_config_reloads_only_while_it_is_on_screen()
    {
        var (vm, _) = Panel("tool");
        await vm.InitializeAsync();
        var baseline = ConfigEdits.Subscribers(_services);

        vm.SetActive(true);
        Assert.Equal(baseline + 1, ConfigEdits.Subscribers(_services));

        vm.SetActive(false);
        Assert.Equal(baseline, ConfigEdits.Subscribers(_services));
    }

    // ------------------------------------------------------------------ Govern: refused at execution time

    [Fact]
    public async Task A_change_requested_after_an_edit_nobody_has_heard_about_is_refused_when_it_is_requested()
    {
        var (vm, cli) = Panel("mcp");
        await vm.InitializeAsync();
        var row = vm.Rows.First();
        Assert.True(vm.IsDataTrusted);

        _files.EditConfig();                       // no reload, no event: the panel is not even on screen
        Assert.True(vm.IsDataTrusted);             // nothing has told it

        Act(vm, row, GovernVerbs.Block);

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ResultMessage);
        Assert.False(vm.IsDataTrusted);
        Assert.False(row.ChangesEnabled);
        Assert.Empty(cli.Mutations);
        Assert.Empty(_services.Cli.Activity); // a click that opened no review had no command to record
    }

    [Fact]
    public async Task A_review_confirmed_after_an_edit_does_not_run_and_the_refusal_is_in_activity()
    {
        var (vm, cli) = Panel("mcp");
        await vm.InitializeAsync();
        Act(vm, vm.Rows.First(), GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);

        _files.EditConfig();                       // the operator reads the review; meanwhile config.yaml changes
        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ResultMessage);
        Assert.Empty(cli.Mutations);

        var entry = Assert.Single(_services.Cli.Activity);
        Assert.Equal(new[] { "mcp", "block", "--connector", "claudecode", "--", "docs" }, entry.Argv);
        Assert.False(entry.IsRunning);
        Assert.Null(entry.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
        Assert.Contains(CatalogTrust.ConfigChangedReason(), entry.FailureReason, StringComparison.Ordinal);

        // Refresh, and the same change goes through.
        await vm.RefreshCommand.ExecuteAsync(null);
        Act(vm, vm.Rows.First(), GovernVerbs.Block);
        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.Equal("mcp block --connector claudecode -- docs", Assert.Single(cli.Mutations));
        Assert.Equal("Done", vm.ResultTitle);
    }

    [Fact]
    public async Task A_refused_change_that_carries_a_secret_is_still_refused_and_is_not_written_down()
    {
        var (vm, cli) = Panel("skill");
        await vm.InitializeAsync();
        vm.Reason = "leaky-value-12345678";
        _services.Cli.RegisterSecret(new DefenseClaw.Core.Config.SecretValue("leaky-value-12345678"));
        Act(vm, vm.Rows.First(), GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);

        _files.EditConfig();
        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Empty(cli.Mutations);
        Assert.Empty(_services.Cli.Activity);
    }

    // ------------------------------------------------------------------ Registries

    private async Task<RegistriesPanelViewModel> RegistriesAsync()
    {
        var vm = new RegistriesPanelViewModel(_services);
        await vm.InitializeAsync(); // no CLI on the isolated path: the read fails, as in CatalogSafetyTests ...

        // ... so a successful read is stood in for, with a source on screen.
        var source = new RegistrySourceRow
        {
            Id = "corp-skills",
            Kind = "http_yaml",
            Content = "both",
            Enabled = true,
            EntriesSummary = "5 (1 clean)",
            LastSync = "2026-09-20T15:04:05+00:00",
            LastStatus = "ok",
            Location = "https://registry.example.test/defenseclaw-registry.yaml",
            Fields = new[] { new RegistryFieldRow("id", "corp-skills") },
        };
        vm.CliErrorMessage = null;
        vm.Trust.BeginRead();
        vm.Trust.MarkComplete();
        vm.NotifyTrust();
        vm.Sources.Add(source);
        vm.HasSources = true;
        vm.SelectedSource = source;
        return vm;
    }

    [Fact]
    public async Task Registries_after_a_config_reload_keep_their_rows_and_turn_every_change_off_with_the_reason()
    {
        var vm = await RegistriesAsync();
        vm.SetActive(true);
        try
        {
            Assert.True(vm.IsDataTrusted);
            Assert.True(vm.CanSyncAll);
            Assert.True(vm.CanChangeSelectedSource);
            var source = vm.SelectedSource;

            _files.EditEnv();
            _files.Reload();

            Assert.False(vm.IsDataTrusted);
            Assert.True(vm.Trust.IsStale);
            Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DataUntrustedReason);
            Assert.False(vm.CanSyncAll);
            Assert.False(vm.CanChangeSelectedSource);
            Assert.Single(vm.Sources);
            Assert.Same(source, vm.SelectedSource);
            Assert.True(vm.HasSources);

            // A complete read, stood in for, gives it back.
            vm.Trust.BeginRead();
            vm.Trust.MarkComplete();
            vm.NotifyTrust();
            Assert.True(vm.IsDataTrusted);
            Assert.True(vm.CanSyncAll);
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task Registries_refuse_a_command_requested_after_an_edit_the_watcher_has_not_reported()
    {
        var vm = await RegistriesAsync();
        _files.EditConfig();
        Assert.True(vm.IsDataTrusted);

        vm.SyncAllCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasActionMessage);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ActionMessage);
        Assert.False(vm.IsDataTrusted);
    }

    [Fact]
    public async Task Registries_do_not_run_a_review_confirmed_after_an_edit_and_the_refusal_is_in_activity()
    {
        var vm = await RegistriesAsync();
        var ran = new List<string>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "{}"));
        };
        vm.SyncAllCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);

        _files.EditConfig();
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.True(vm.Review.IsFinished);
        Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
        Assert.Contains(CatalogTrust.ConfigChangedReason(), vm.Review.ResultText, StringComparison.Ordinal);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.ActionMessage); // still there when the dialog is closed
        Assert.False(vm.IsDataTrusted);

        var entry = Assert.Single(_services.Cli.Activity);
        Assert.Equal(new[] { "registry", "sync", "--all", "--json" }, entry.Argv);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registries_listen_to_config_reloads_only_while_on_screen()
    {
        var vm = new RegistriesPanelViewModel(_services);
        await vm.InitializeAsync(); // the first read is over, so coming on screen starts none
        var baseline = ConfigEdits.Subscribers(_services);

        vm.SetActive(true);
        Assert.Equal(baseline + 1, ConfigEdits.Subscribers(_services));

        vm.SetActive(false);
        Assert.Equal(baseline, ConfigEdits.Subscribers(_services));
    }
}
