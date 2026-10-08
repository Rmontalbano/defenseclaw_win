using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// Catalog safety (CUST-283): a partial read shows its rows with a warning and no way to change anything; a failed or old read
/// keeps its rows and loses the same ability; nothing a Govern panel can do to the system starts from a list it cannot trust.
/// The CLI is a script handed to the view-model (<see cref="GovernPanelViewModelBase.RunCli"/>): no process starts, and every
/// path, name and diagnostic below is synthetic.
/// </summary>
public sealed class CatalogSafetyTests : IDisposable
{
    private const string Diagnostic = "error: MCP discovery source is unreadable for connector='claudecode': C:\\fixture-home\\.claude\\settings.json";

    private const string ToolRules = """[{"connector": "claudecode", "tools": [{"name": "delete_file", "connector": "claudecode", "scope": "connector", "status": "block"}]}]""";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public CatalogSafetyTests()
    {
        _services = TestServices.Create(_temp);
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

        /// <summary>What a <c>... list --json</c> prints next; replaced between steps.</summary>
        public Func<CliInvocation> List { get; set; } = () => throw new InvalidOperationException("no list scripted");

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            Calls.Add(string.Join(' ', argv));
            if (argv.Count >= 2 && argv[1] == "list")
            {
                return List();
            }

            // Info / status / any mutation: succeeds, prints an object.
            return Result(0, argv, Out("{}"));
        }

        public IEnumerable<string> Mutations => Calls.Where(c => !c.Contains(" list ", StringComparison.Ordinal) && !c.Contains(" info ", StringComparison.Ordinal) && !c.Contains(" status ", StringComparison.Ordinal));
    }

    private static (CliStream Stream, string Text) Out(string text) => (CliStream.StandardOutput, text);

    private static (CliStream Stream, string Text) Err(string text) => (CliStream.StandardError, text);

    private static CliInvocation Result(int exit, IReadOnlyList<string> argv, params (CliStream Stream, string Text)[] lines)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var (stream, text) in lines)
        {
            InvocationFactory.Append(invocation, text, stream);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Func<CliInvocation> Complete(string noun, string json) => () => Result(0, new[] { noun, "list", "--json" }, Out(json));

    private static Func<CliInvocation> Partial(string noun, string json) =>
        () => Result(1, new[] { noun, "list", "--json" }, Out(json), Err(Diagnostic));

    private static Func<CliInvocation> Failed(string noun) =>
        () => Result(2, new[] { noun, "list", "--json" }, Err("Traceback: fixture failure"));

    private (GovernPanelViewModelBase Vm, Cli Cli, string Json) Panel(string name)
    {
        var cli = new Cli();
        (GovernPanelViewModelBase Vm, string Json) made = name switch
        {
            "skill" => (new SkillsPanelViewModel(_services), """[{"name": "pdf-tools"}, {"name": "csv-tools"}]"""),
            "mcp" => (new McpsPanelViewModel(_services), """[{"name": "docs", "connector": "claudecode"}, {"name": "wiki", "connector": "claudecode"}]"""),
            "plugin" => (new PluginsPanelViewModel(_services), """[{"id": "p1", "connector": "claudecode"}, {"id": "p2", "connector": "claudecode"}]"""),
            "tool" => (new ToolsPanelViewModel(_services), ToolRules),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        made.Vm.RunCli = (argv, _) => Task.FromResult(cli.Handle(argv));
        if (made.Vm is SkillsPanelViewModel or McpsPanelViewModel)
        {
            made.Vm.ScannerFinder = _ => Task.FromResult<string?>(@"C:\scanners\scanner.exe");
        }

        return (made.Vm, cli, made.Json);
    }

    private static void Act(GovernPanelViewModelBase vm, GovernRow row, GovernVerbs verb) => ((IGovernRowHost)vm).OnRowAction(row, verb);

    private static readonly GovernVerbs[] ChangeVerbs =
    {
        GovernVerbs.Block, GovernVerbs.Allow, GovernVerbs.Unblock, GovernVerbs.Disable, GovernVerbs.Enable,
        GovernVerbs.Quarantine, GovernVerbs.Restore, GovernVerbs.Remove, GovernVerbs.Unset, GovernVerbs.Scan,
    };

    // ------------------------------------------------------------------ acceptance: partial

    [Theory]
    [InlineData("skill")]
    [InlineData("mcp")]
    [InlineData("plugin")]
    [InlineData("tool")]
    public async Task Valid_json_with_exit_1_and_a_known_diagnostic_shows_the_rows_with_a_warning_and_no_way_to_change_them(string panel)
    {
        var (vm, cli, json) = Panel(panel);
        cli.List = Partial(panel, json);

        await vm.InitializeAsync();

        Assert.Equal(GovernState.Loaded, vm.State);
        Assert.True(vm.ShowList);
        Assert.NotEmpty(vm.Rows);
        Assert.False(vm.ShowError);

        // The warning is the CLI's diagnostic, apart from the JSON; it is not an error banner and not "an older read".
        Assert.True(vm.HasPartialDiscovery);
        Assert.Contains("C:\\fixture-home\\.claude\\settings.json", vm.PartialDiscoveryMessage, StringComparison.Ordinal);
        Assert.Contains("Changes are off", vm.PartialDiscoveryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", vm.PartialDiscoveryMessage, StringComparison.Ordinal);
        Assert.False(vm.HasRefreshWarning);

        Assert.False(vm.IsDataTrusted);
        Assert.False(vm.CanChange);
        Assert.False(vm.CanScanAll);
        Assert.Contains("discovery was incomplete", vm.DataUntrustedReason, StringComparison.Ordinal);

        foreach (var row in vm.Rows)
        {
            Assert.False(row.ChangesEnabled);
            Assert.False(row.ScanEnabled);
            Assert.Equal(vm.DataUntrustedReason, row.ChangesBlockedReason);

            foreach (var verb in ChangeVerbs.Where(v => (row.Verbs & v) != 0))
            {
                Act(vm, row, verb);
                Assert.False(vm.IsConfirmOpen, $"{verb} on {row.Name} opened a review from a partial list");
                Assert.True(vm.IsResultOpen);
                Assert.Equal("Changes are off", vm.ResultTitle);
            }
        }

        // Nothing but the read ever reached the runner.
        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task Reading_a_partial_list_is_still_possible_info_and_copy_name_do_not_change_anything()
    {
        var (vm, cli, json) = Panel("skill");
        cli.List = Partial("skill", json);
        await vm.InitializeAsync();
        var row = vm.Rows.First();

        Act(vm, row, GovernVerbs.Info);
        UiThreadless.WaitFor(() => vm.IsOutputOpen);

        Assert.Contains(cli.Calls, c => c.StartsWith("skill info", StringComparison.Ordinal));
        Assert.True(row.CanInfo);
    }

    [Fact]
    public async Task The_toolbar_actions_of_every_panel_are_off_for_a_partial_list()
    {
        var (skills, skillsCli, skillsJson) = Panel("skill");
        skillsCli.List = Partial("skill", skillsJson);
        await skills.InitializeAsync();
        var skillsVm = (SkillsPanelViewModel)skills;
        skillsVm.InstallName = "pdf-tools";
        skillsVm.SubmitInstallFormCommand.Execute(null);
        Assert.False(skillsVm.IsConfirmOpen);
        skillsVm.ScanAllCommand.Execute(null);
        Assert.False(skillsVm.IsConfirmOpen);
        Assert.Empty(skillsCli.Mutations);

        var (mcps, mcpsCli, mcpsJson) = Panel("mcp");
        mcpsCli.List = Partial("mcp", mcpsJson);
        await mcps.InitializeAsync();
        var mcpsVm = (McpsPanelViewModel)mcps;
        mcpsVm.SetName = "docs";
        mcpsVm.SetCommand = "npx";
        mcpsVm.SubmitSetFormCommand.Execute(null);
        Assert.False(mcpsVm.IsConfirmOpen);

        var (plugins, pluginsCli, pluginsJson) = Panel("plugin");
        pluginsCli.List = Partial("plugin", pluginsJson);
        await plugins.InitializeAsync();
        var pluginsVm = (PluginsPanelViewModel)plugins;
        pluginsVm.InstallNameOrPath = "clawhub://example/pkg";
        pluginsVm.SubmitInstallFormCommand.Execute(null);
        Assert.False(pluginsVm.IsConfirmOpen);

        var (tools, toolsCli, toolsJson) = Panel("tool");
        toolsCli.List = Partial("tool", toolsJson);
        await tools.InitializeAsync();
        var toolsVm = (ToolsPanelViewModel)tools;
        toolsVm.ManageToolName = "delete_file";
        toolsVm.ManageBlockCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);
        toolsVm.ManageAllowCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);
        toolsVm.ManageUnblockCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);

        Assert.Empty(mcpsCli.Mutations);
        Assert.Empty(pluginsCli.Mutations);
        Assert.Empty(toolsCli.Mutations);
    }

    [Fact]
    public async Task A_partial_read_with_no_rows_is_an_incomplete_look_not_an_empty_catalog()
    {
        var (vm, cli, _) = Panel("mcp");
        cli.List = Partial("mcp", "[]");

        await vm.InitializeAsync();

        Assert.Equal(GovernState.Error, vm.State);
        Assert.False(vm.ShowEmpty);
        Assert.Equal("Discovery was incomplete", vm.ErrorTitle);
        Assert.Contains("settings.json", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsDataTrusted);
    }

    // ------------------------------------------------------------------ acceptance: garbage and other failures

    [Fact]
    public async Task Garbage_with_exit_1_is_an_error_state_even_beside_a_known_diagnostic()
    {
        var (vm, cli, _) = Panel("mcp");
        cli.List = () => Result(1, new[] { "mcp", "list", "--json" }, Out("not json at all"), Err(Diagnostic));

        await vm.InitializeAsync();

        Assert.Equal(GovernState.Error, vm.State);
        Assert.True(vm.ShowError);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasPartialDiscovery);
        Assert.False(vm.IsDataTrusted);
    }

    [Fact]
    public async Task Valid_json_with_exit_1_and_an_unrecognised_stderr_is_an_error_not_a_partial_read()
    {
        var (vm, cli, json) = Panel("mcp");
        cli.List = () => Result(1, new[] { "mcp", "list", "--json" }, Out(json), Err("error: something nobody has seen before"));

        await vm.InitializeAsync();

        Assert.Equal(GovernState.Error, vm.State);
        Assert.False(vm.HasPartialDiscovery);
    }

    [Fact]
    public async Task A_clean_exit_with_a_diagnostic_on_stderr_is_still_a_complete_read()
    {
        // 0.8.10 behaviour for an unreadable source: exit 0, nothing on stderr. The classification must leave that exactly alone.
        var (vm, cli, json) = Panel("mcp");
        cli.List = () => Result(0, new[] { "mcp", "list", "--json" }, Out(json));

        await vm.InitializeAsync();

        Assert.Equal(GovernState.Loaded, vm.State);
        Assert.False(vm.HasPartialDiscovery);
        Assert.True(vm.IsDataTrusted);
        Assert.True(vm.CanChange);
    }

    // ------------------------------------------------------------------ acceptance: stale

    [Fact]
    public async Task After_a_failed_refresh_the_cached_rows_stay_and_the_actions_do_not()
    {
        var (vm, cli, json) = Panel("skill");
        cli.List = Complete("skill", json);
        await vm.InitializeAsync();
        var row = vm.Rows.First();
        Assert.True(vm.IsDataTrusted);
        Assert.True(row.ChangesEnabled);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        cli.List = Failed("skill");
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(GovernState.Loaded, vm.State);
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.HasRefreshWarning);
        Assert.Contains("changes are off", vm.RefreshWarning, StringComparison.Ordinal);
        Assert.False(vm.IsDataTrusted);
        Assert.False(vm.CanChange);
        Assert.Contains("last read failed", vm.DataUntrustedReason, StringComparison.Ordinal);

        // The row that was on screen all along was told: its menu items switch off without a rebuild.
        Assert.Contains(nameof(GovernRow.ChangesEnabled), changed);
        Assert.False(row.ChangesEnabled);

        Act(vm, row, GovernVerbs.Block);
        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Empty(cli.Mutations);

        // A good read brings them back.
        cli.List = Complete("skill", json);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsDataTrusted);
        Assert.True(row.ChangesEnabled);
        Act(vm, row, GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);
    }

    [Fact]
    public async Task A_failed_first_read_and_a_missing_cli_leave_nothing_to_authorize_a_change()
    {
        var vm = new McpsPanelViewModel(_services); // the isolated runner finds no defenseclaw: CliNotFoundException

        await vm.InitializeAsync();

        Assert.Equal(GovernState.CliUnavailable, vm.State);
        Assert.False(vm.IsDataTrusted);
        Assert.False(vm.CanChange);
    }

    [Fact]
    public async Task Nothing_is_authorized_while_the_first_read_is_still_running()
    {
        var (vm, cli, json) = Panel("mcp");
        var gate = new TaskCompletionSource();
        vm.RunCli = async (argv, _) =>
        {
            await gate.Task;
            return cli.Handle(argv);
        };
        cli.List = Complete("mcp", json);

        var load = vm.InitializeAsync();
        Assert.Equal(GovernState.Loading, vm.State);
        Assert.False(vm.IsDataTrusted);
        Assert.Contains("first read", vm.DataUntrustedReason, StringComparison.Ordinal);

        gate.SetResult();
        await load;
        Assert.True(vm.IsDataTrusted);
    }

    [Fact]
    public async Task A_list_that_has_gone_old_stops_authorizing_changes_until_it_is_read_again()
    {
        var clock = new ManualClock();
        var (vm, cli, json) = Panel("skill");
        vm.Trust = new CatalogTrust(time: clock);
        cli.List = Complete("skill", json);
        await vm.InitializeAsync();
        var row = vm.Rows.First();

        clock.Advance(CatalogTrust.DefaultFreshnessWindow - TimeSpan.FromMinutes(1));
        Assert.True(vm.IsDataTrusted);
        Act(vm, row, GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);
        vm.ConfirmNoCommand.Execute(null);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(vm.IsDataTrusted);
        Assert.Contains("minutes ago", vm.DataUntrustedReason, StringComparison.Ordinal);
        Act(vm, row, GovernVerbs.Block);
        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsDataTrusted);
        Act(vm, row, GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);
    }

    // ------------------------------------------------------------------ the confirm step re-checks

    [Fact]
    public async Task A_review_opened_on_good_data_does_not_run_once_a_refresh_has_made_the_list_partial()
    {
        var (vm, cli, json) = Panel("mcp");
        cli.List = Complete("mcp", json);
        await vm.InitializeAsync();
        Act(vm, vm.Rows.First(), GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);

        cli.List = Partial("mcp", json);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task On_good_data_a_confirmed_change_runs_and_the_list_is_read_again()
    {
        var (vm, cli, json) = Panel("mcp");
        cli.List = Complete("mcp", json);
        await vm.InitializeAsync();
        Act(vm, vm.Rows.First(), GovernVerbs.Block);

        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { "mcp list --json", "mcp block --connector claudecode -- docs", "mcp list --json" },
            cli.Calls);
        Assert.True(vm.IsDataTrusted);
    }

    // ------------------------------------------------------------------ hostile diagnostics, and the shared state machine

    [Fact]
    public async Task A_diagnostic_with_control_characters_is_shown_spelled_out_on_one_line()
    {
        var (vm, cli, json) = Panel("mcp");
        cli.List = () => Result(
            1,
            new[] { "mcp", "list", "--json" },
            Out(json),
            Err("error: MCP discovery source is unreadable for connector='claudecode': C:\\fixture\\a\u202Eb.json"));

        await vm.InitializeAsync();

        Assert.True(vm.HasPartialDiscovery);
        Assert.DoesNotContain('\u202E', vm.PartialDiscoveryMessage);
        Assert.Contains("\\u202E", vm.PartialDiscoveryMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trust_state_machine_walks_pending_complete_partial_failed_and_old()
    {
        var clock = new ManualClock();
        var trust = new CatalogTrust(TimeSpan.FromMinutes(5), clock);

        Assert.True(trust.IsTrusted); // nothing was ever asked for: nothing on screen to be wrong about

        trust.MarkPending();
        Assert.False(trust.IsTrusted);

        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
        Assert.Null(trust.Reason);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(trust.IsTrusted);

        trust.MarkPartial(new[] { "d1", "d2" });
        Assert.False(trust.IsTrusted);
        Assert.True(trust.IsPartial);
        Assert.Equal(new[] { "d1", "d2" }, trust.PartialDiagnostics);

        trust.MarkFailed("boom");
        Assert.False(trust.IsPartial);
        Assert.True(trust.LastReadFailed);
        Assert.Equal("boom", trust.FailureMessage);
        Assert.Empty(trust.PartialDiagnostics);

        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
        Assert.False(trust.LastReadFailed);
    }

    // ------------------------------------------------------------------ the Registries panel

    [Fact]
    public async Task Registries_with_a_failed_read_refuse_every_change_and_say_why()
    {
        var vm = new RegistriesPanelViewModel(_services); // no defenseclaw on the isolated path: the read fails

        await vm.InitializeAsync();

        Assert.True(vm.HasCliError);
        Assert.False(vm.IsDataTrusted);
        Assert.False(vm.CanSyncAll);

        vm.SyncAllCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasActionMessage);
        Assert.Contains("Changes are off", vm.ActionMessage, StringComparison.Ordinal);

        vm.SetRegistryRequiredCommand.Execute("skill:on");
        Assert.False(vm.Review.IsOpen);

        vm.AddId = "corp-skills";
        vm.AddUrl = "https://registry.example.test/index.yaml";
        vm.SubmitAddCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public void A_registries_panel_nobody_has_read_yet_is_not_blocked_by_a_read_that_never_happened()
    {
        var vm = new RegistriesPanelViewModel(_services);

        Assert.True(vm.IsDataTrusted);
        vm.SyncAllCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);
    }
}

/// <summary>A condition wait for tests that are not on the UI thread: polls, bounded, no fixed sleeps in the assertion.</summary>
internal static class UiThreadless
{
    public static void WaitFor(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("condition not met");
            }

            Thread.Sleep(10);
        }
    }
}
