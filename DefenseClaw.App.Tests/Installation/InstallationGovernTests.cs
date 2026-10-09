using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Govern;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The Govern panels (Skills, MCP servers, Plugins, Tools) and Registries on a read-only installation (CUST-308): the list still reads and shows,
/// and every control that changes something is off with the installation's own sentence - the same place the stale-list rules already switch
/// them off (<see cref="GovernPanelViewModelBase.IsDataTrusted"/>), so a change cannot start from either. The CLI is a script handed to the
/// view-model (<see cref="GovernPanelViewModelBase.RunCli"/>): no process starts, and every name below is synthetic.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationGovernTests : IDisposable
{
    private const string ToolRules = """[{"connector": "claudecode", "tools": [{"name": "delete_file", "connector": "claudecode", "scope": "connector", "status": "block"}]}]""";

    private static readonly GovernVerbs[] ChangeVerbs =
    {
        GovernVerbs.Block, GovernVerbs.Allow, GovernVerbs.Unblock, GovernVerbs.Disable, GovernVerbs.Enable,
        GovernVerbs.Quarantine, GovernVerbs.Restore, GovernVerbs.Remove, GovernVerbs.Unset, GovernVerbs.Scan,
    };

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public InstallationGovernTests() => _services = TestServices.Create(_temp);

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

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            Calls.Add(string.Join(' ', argv));
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            InvocationFactory.Append(invocation, argv.Count >= 2 && argv[1] == "list" ? ListJson : "{}");
            InvocationFactory.Finish(invocation, 0);
            return invocation;
        }

        public IEnumerable<string> Mutations => Calls.Where(c => !c.Contains(" list ", StringComparison.Ordinal) && !c.Contains(" info ", StringComparison.Ordinal) && !c.Contains(" status ", StringComparison.Ordinal));
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

    // ------------------------------------------------------------------ the usual installation

    [Theory]
    [InlineData("skill")]
    [InlineData("mcp")]
    [InlineData("plugin")]
    [InlineData("tool")]
    public async Task On_a_writable_installation_a_complete_read_offers_changes(string panel)
    {
        var (vm, _) = Panel(panel);

        await vm.InitializeAsync();

        Assert.True(vm.IsDataTrusted);
        Assert.True(vm.CanChange);
        Assert.Null(vm.DataUntrustedReason);
        Assert.All(vm.Rows, row => Assert.True(row.ChangesEnabled));
    }

    // ------------------------------------------------------------------ a read-only installation

    [Theory]
    [InlineData("skill")]
    [InlineData("mcp")]
    [InlineData("plugin")]
    [InlineData("tool")]
    public async Task A_read_only_installation_still_shows_the_list_and_turns_every_change_off_with_its_sentence(string panel)
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var (vm, cli) = Panel(panel);

        await vm.InitializeAsync();

        // The list is a read: it is shown, complete and current, and says nothing is wrong with it.
        Assert.Equal(GovernState.Loaded, vm.State);
        Assert.True(vm.ShowList);
        Assert.NotEmpty(vm.Rows);
        Assert.True(vm.Trust.IsTrusted);
        Assert.False(vm.HasPartialDiscovery);
        Assert.False(vm.HasRefreshWarning);

        // Nothing it offers to change is on, and every tooltip is the installation's sentence.
        Assert.False(vm.IsDataTrusted);
        Assert.False(vm.CanChange);
        Assert.False(vm.CanScanAll);
        Assert.Equal(TestInstallations.ManagedReason, vm.DataUntrustedReason);
        foreach (var row in vm.Rows)
        {
            Assert.False(row.ChangesEnabled);
            Assert.False(row.ScanEnabled);
            Assert.Equal(TestInstallations.ManagedReason, row.ChangesBlockedReason);

            foreach (var verb in ChangeVerbs.Where(v => (row.Verbs & v) != 0))
            {
                Act(vm, row, verb);
                Assert.False(vm.IsConfirmOpen, $"{verb} on {row.Name} opened a review on a read-only installation");
                Assert.True(vm.IsResultOpen);
                Assert.Equal("Changes are off", vm.ResultTitle);
                Assert.Equal(TestInstallations.ManagedReason, vm.ResultMessage);
            }
        }

        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task A_read_only_installation_still_reads_a_skills_details()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var (vm, cli) = Panel("skill");
        await vm.InitializeAsync();
        var row = vm.Rows.First();

        Act(vm, row, GovernVerbs.Info);
        UiThreadless.WaitFor(() => vm.IsOutputOpen);

        Assert.Contains(cli.Calls, c => c.StartsWith("skill info", StringComparison.Ordinal));
        Assert.True(row.CanInfo);
    }

    [Fact]
    public async Task The_toolbar_actions_of_every_panel_are_refused_on_a_read_only_installation()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));

        var (skills, skillsCli) = Panel("skill");
        await skills.InitializeAsync();
        var skillsVm = (SkillsPanelViewModel)skills;
        skillsVm.InstallName = "pdf-tools";
        skillsVm.SubmitInstallFormCommand.Execute(null);
        Assert.False(skillsVm.IsConfirmOpen);
        skillsVm.ScanAllCommand.Execute(null);
        Assert.False(skillsVm.IsConfirmOpen);

        var (mcps, mcpsCli) = Panel("mcp");
        await mcps.InitializeAsync();
        var mcpsVm = (McpsPanelViewModel)mcps;
        mcpsVm.SetName = "docs";
        mcpsVm.SetCommand = "npx";
        mcpsVm.SubmitSetFormCommand.Execute(null);
        Assert.False(mcpsVm.IsConfirmOpen);

        var (plugins, pluginsCli) = Panel("plugin");
        await plugins.InitializeAsync();
        var pluginsVm = (PluginsPanelViewModel)plugins;
        pluginsVm.InstallNameOrPath = "clawhub://example/pkg";
        pluginsVm.SubmitInstallFormCommand.Execute(null);
        Assert.False(pluginsVm.IsConfirmOpen);

        var (tools, toolsCli) = Panel("tool");
        await tools.InitializeAsync();
        var toolsVm = (ToolsPanelViewModel)tools;
        toolsVm.ManageToolName = "delete_file";
        toolsVm.ManageBlockCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);
        toolsVm.ManageAllowCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);
        toolsVm.ManageUnblockCommand.Execute(null);
        Assert.False(toolsVm.IsConfirmOpen);

        Assert.All(new[] { skills, mcps, plugins, tools }, panel => Assert.Equal("Changes are off", panel.ResultTitle));
        Assert.Empty(skillsCli.Mutations);
        Assert.Empty(mcpsCli.Mutations);
        Assert.Empty(pluginsCli.Mutations);
        Assert.Empty(toolsCli.Mutations);
    }

    [Fact]
    public async Task A_review_opened_while_the_installation_was_writable_does_not_run_once_it_is_read_only()
    {
        var (vm, cli) = Panel("mcp");
        await vm.InitializeAsync();
        Act(vm, vm.Rows.First(), GovernVerbs.Block);
        Assert.True(vm.IsConfirmOpen);

        // The config the review was opened on was edited to managed while the question was on screen.
        _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.ConfirmYesCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal("Changes are off", vm.ResultTitle);
        Assert.Equal(TestInstallations.ManagedReason, vm.ResultMessage);
        Assert.Empty(cli.Mutations);
    }

    // ------------------------------------------------------------------ a verdict that moves while the panel is on screen

    [Fact]
    public async Task A_panel_on_screen_redraws_every_row_when_the_installation_turns_read_only_and_back()
    {
        var (vm, _) = Panel("skill");
        await vm.InitializeAsync();
        var row = vm.Rows.First();
        Assert.True(row.ChangesEnabled);
        var changed = new List<string?>();
        var rowChanged = new List<string?>();

        UiThread.Run(() =>
        {
            vm.SetActive(true);
            try
            {
                ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => rowChanged.Add(e.PropertyName);

                _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

                Assert.Contains(nameof(GovernPanelViewModelBase.IsDataTrusted), changed);
                Assert.Contains(nameof(GovernPanelViewModelBase.DataUntrustedReason), changed);
                Assert.Contains(nameof(GovernPanelViewModelBase.CanChange), changed);
                Assert.Contains(nameof(PanelViewModelBase.InstallationBlockedReason), changed);
                Assert.Contains(nameof(GovernRow.ChangesEnabled), rowChanged);
                Assert.False(vm.CanChange);
                Assert.False(row.ChangesEnabled);
                Assert.Equal(TestInstallations.ManagedReason, row.ChangesBlockedReason);

                _services.Installation.Replace(TestInstallations.UserDefault());

                Assert.True(vm.CanChange);
                Assert.True(row.ChangesEnabled);
                Assert.Null(row.ChangesBlockedReason);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ Registries

    [Fact]
    public void Registries_on_a_read_only_installation_refuse_every_change_and_say_why()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var vm = new RegistriesPanelViewModel(_services);

        Assert.False(vm.IsDataTrusted);
        Assert.Equal(TestInstallations.ManagedReason, vm.DataUntrustedReason);
        Assert.False(vm.CanSyncAll);
        Assert.False(vm.CanChangeSelectedSource);

        vm.SyncAllCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasActionMessage);
        Assert.Equal(TestInstallations.ManagedReason, vm.ActionMessage);

        vm.SetRegistryRequiredCommand.Execute("skill:on");
        Assert.False(vm.Review.IsOpen);

        vm.AddId = "corp-skills";
        vm.AddUrl = "https://registry.example.test/index.yaml";
        vm.SubmitAddCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public void Registries_on_a_writable_installation_that_nobody_has_read_yet_are_not_blocked()
    {
        var vm = new RegistriesPanelViewModel(_services);

        Assert.True(vm.IsDataTrusted);
        Assert.Null(vm.DataUntrustedReason);
        vm.SyncAllCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);
    }
}
