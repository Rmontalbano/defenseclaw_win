using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>
/// The Overview's durability rows and their buttons (CUST-352) on a synthetic install: a data directory with a gateway.log, a hook_contract_lock.json,
/// <c>.hookcfg</c> and CAS receipts, a Codex folder beside it, and a config.yaml. Each button opens the shared review with its exact argv, runs nothing
/// until it is confirmed, and is off - with the installation's sentence first - on a read-only installation. Nothing starts a process; the file
/// operations run for real, but only inside the scratch directory. A PNG of the rows is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewDurabilityTests : IDisposable
{
    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";
    private const string StuckId = "068ff1aa22bb33cc44dd55ee";
    private const string Config = "gateway:\n  api_port: 39871\nguardrail:\n  connector: codex\n  enabled: true\n  connectors:\n    codex:\n      mode: enforce\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private AppServices Services => _services!;

    private string CodexConfig => Path.Combine(_temp.Path, ".codex", "config.toml");

    private void SeedStuckWrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CodexConfig)!);
        File.WriteAllText(CodexConfig, "model = \"x\"\n");
        var staging = ".tmp-cas-v2-stage-" + new string('7', 32);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(CodexConfig)!, staging), "new body");
        var body = JsonSerializer.Serialize(new { version = 2, phase = "staging", target_path = CodexConfig, stage_final_name = staging });
        foreach (var suffix in new[] { string.Empty, ".allocation", ".prepared" })
        {
            var path = Path.Combine(_temp.Path, $".defenseclaw-cas-v2-{StuckId}.intent{suffix}");
            File.WriteAllText(path, body);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
        }

        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(CodexConfig)!, staging), DateTime.UtcNow.AddMinutes(-30));
    }

    private DurabilityFacts ReadFacts() =>
        DurabilityReader.Read(Services.Paths.DataDirectory, Services.Paths.HooksDirectory, Services.CasQuarantineRoot, DateTimeOffset.UtcNow);

    private static GatewaySnapshot Snapshot(AppGatewayState state, DurabilityFacts? facts, GatewayExitReason? failure = null) => new()
    {
        State = state,
        Install = state == AppGatewayState.Running ? InstallState.Running : InstallState.GatewayStopped,
        Detail = state == AppGatewayState.Running ? "ok" : "Nothing is listening on port 39871.",
        CliPath = @"C:\Tools\defenseclaw.exe",
        ApiPort = 39871,
        ActiveConnectors = new[] { "codex" },
        Durability = facts,
        LastFailure = failure,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private OverviewPanelViewModel Panel(Func<DurabilityFacts, GatewaySnapshot>? build = null, string config = Config, InstallationContext? installation = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, installation: installation);
        var vm = new OverviewPanelViewModel(_services);
        var snapshot = (build ?? (facts => Snapshot(AppGatewayState.GatewayStopped, facts)))(ReadFacts());
        Publish(snapshot);
        vm.Apply(snapshot);
        return vm;
    }

    private void Publish(GatewaySnapshot snapshot) =>
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Services.Monitor, new object[] { snapshot });

    private static List<(string Executable, string[] Argv)> Capture(OverviewPanelViewModel vm)
    {
        var ran = new List<(string, string[])>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add((executable, argv.ToArray()));
            var done = InvocationFactory.CreateFor(executable, argv.ToArray());
            InvocationFactory.Finish(done, 0);
            return Task.FromResult(done);
        };
        return ran;
    }

    private static AttentionRow Row(OverviewPanelViewModel vm, string titleStart) =>
        Assert.Single(vm.Attention, r => r.Title.StartsWith(titleStart, StringComparison.Ordinal));

    // ---- the stopped row says why ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_stopped_row_says_why_the_last_start_ended_and_the_flyout_says_the_same()
    {
        var stale = new GatewayExitReason.StaleAgentExecutable("codex", OldExe);
        var vm = Panel(facts => Snapshot(AppGatewayState.GatewayStopped, facts, stale));

        var row = Row(vm, "The gateway is not answering");
        Assert.Contains("The gateway stops at start: Codex's executable moved after a Codex update.", row.Detail, StringComparison.Ordinal);
        Assert.True(row.OffersStart);

        var snapshot = Snapshot(AppGatewayState.GatewayStopped, ReadFacts(), stale);
        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));
        UiThread.Run(() => flyout.Apply(snapshot));
        Assert.Contains("The gateway stops at start: Codex's executable moved after a Codex update.", flyout.StateDetail, StringComparison.Ordinal);
        UiThread.Run(flyout.Dispose);
    }

    [Fact]
    public void A_blocked_agent_gets_the_critical_row_first_with_the_one_start_button_and_the_flyout_warns()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "hooks"));
        File.WriteAllText(Path.Combine(_temp.Path, "hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        var vm = Panel();

        Assert.Equal("Codex is blocked: fail mode is closed and the gateway is down", vm.Attention[0].Title);
        Assert.Equal("Critical", vm.Attention[0].SeverityKey);
        Assert.True(vm.Attention[0].OffersStart);
        Assert.False(Row(vm, "The gateway is not answering").OffersStart);
        Assert.Equal(AttentionActionIds.SwitchToFailOpen, Assert.Single(vm.Attention[0].Actions.Items).Id);

        var flyout = UiThread.Run(() => new TrayFlyoutViewModel(Services, () => { }, () => { }));
        UiThread.Run(() => flyout.Apply(Snapshot(AppGatewayState.GatewayStopped, ReadFacts())));
        Assert.True(flyout.HasFailModeNote);
        Assert.Contains("Codex is blocked: fail mode is closed and the gateway is down", flyout.FailModeNote, StringComparison.Ordinal);
        UiThread.Run(flyout.Dispose);
    }

    [Fact]
    public void The_connector_row_shows_the_fail_mode_the_hooks_obey_and_says_when_config_yaml_disagrees()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "hooks"));
        File.WriteAllText(Path.Combine(_temp.Path, "hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        var vm = Panel();

        var codex = Assert.Single(vm.ConnectorRows, r => r.Name == "codex");
        Assert.Equal("closed", codex.FailMode);
        Assert.Contains("the hooks (.hookcfg) fail closed; config.yaml says open", codex.Drift, StringComparison.Ordinal);
    }

    // ---- Re-select Codex ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-15T20:00:00Z [guardrail] direct-upstream mode: codex policy_mode=observe enforcement=false", "observe")]
    [InlineData("2026-10-15T20:00:00Z [guardrail] direct-upstream mode: codex policy_mode=action enforcement=true", "action")]
    [InlineData("", "action")]
    public async Task Re_select_runs_setup_codex_with_the_current_mode_always_on_the_command_line(string logLine, string expectedMode)
    {
        if (logLine.Length > 0)
        {
            File.WriteAllText(Path.Combine(_temp.Path, "gateway.log"), logLine + "\n");
        }

        var vm = Panel(facts => Snapshot(AppGatewayState.GatewayStopped, facts, new GatewayExitReason.StaleAgentExecutable("codex", OldExe)));
        var ran = Capture(vm);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ReselectCodex);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Re-select Codex?", review.Title);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", expectedMode }, step.Argv);
        Assert.Equal(CommandReview.DefaultExecutable, step.Executable);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Contains("--mode", step.CommandText, StringComparison.Ordinal);
        Assert.True(review.RestartsGateway);
        Assert.Empty(ran);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var run = Assert.Single(ran);
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", expectedMode }, run.Argv);
    }

    [Fact]
    public async Task While_codex_runs_the_re_select_review_and_the_fail_open_review_carry_the_warning_and_the_file_move_does_not()
    {
        SeedStuckWrite();
        var vm = Panel();
        Services.AgentProcesses = new AgentProcessProbe(() => new[] { "Codex" });

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ReselectCodex);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Codex is running" && w.Message.EndsWith("Quit Codex first.", StringComparison.Ordinal));
        Assert.True(vm.Review.ConfirmCommand.CanExecute(null));
        vm.Review.DismissCommand.Execute(null);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.SwitchToFailOpen);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Codex is running");
        vm.Review.DismissCommand.Execute(null);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ClearStuckWrite);
        Assert.DoesNotContain(vm.Review.CommandReview!.Warnings, w => w.Title == "Codex is running");
    }

    [Fact]
    public async Task With_only_the_codex_desktop_apps_sandbox_service_running_no_review_carries_the_warning()
    {
        SeedStuckWrite();
        var vm = Panel();
        Services.AgentProcesses = AgentProcessProbe.FromProcesses(() => new[]
        {
            new RunningProcess("codex-windows-sandbox-service"),
            new RunningProcess("codex-windows-sandbox-setup"),
        });

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ReselectCodex);
        Assert.DoesNotContain(vm.Review.CommandReview!.Warnings, w => w.Title == "Codex is running");
        vm.Review.DismissCommand.Execute(null);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.SwitchToFailOpen);
        Assert.DoesNotContain(vm.Review.CommandReview!.Warnings, w => w.Title == "Codex is running");
    }

    // ---- Switch to fail open ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fail_open_reviews_guardrail_fail_mode_open_and_says_config_set_does_not_take_effect()
    {
        var vm = Panel();
        var ran = Capture(vm);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.SwitchToFailOpen);

        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "guardrail", "fail-mode", "open", "--yes" }, step.Argv);
        Assert.Equal("defenseclaw guardrail fail-mode open --yes", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Contains(review.Warnings, w => w.Message.Contains("config set guardrail.hook_fail_mode does not take effect", StringComparison.Ordinal));
        Assert.False(review.RestartsGateway);
        Assert.Contains("does not start it", review.Summary, StringComparison.Ordinal);
        Assert.Empty(ran);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "guardrail", "fail-mode", "open", "--yes" }, Assert.Single(ran).Argv);
    }

    [Fact]
    public async Task Fail_open_on_a_running_gateway_carries_the_restart_bar()
    {
        var vm = Panel(facts => Snapshot(AppGatewayState.Running, facts));

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.SwitchToFailOpen);

        Assert.True(vm.Review.CommandReview!.RestartsGateway);
    }

    // ---- Clear the stuck pending write, and Put back -------------------------------------------------------------------------------

    [Fact]
    public async Task Clearing_the_stuck_write_reviews_a_file_operation_lists_every_path_and_moves_only_after_confirming()
    {
        SeedStuckWrite();
        var vm = Panel();
        var pending = Assert.Single(vm.Attention, r => r.Title == "A write to Codex's config.toml is stuck");
        Assert.Equal(AttentionActionIds.ClearStuckWrite, Assert.Single(pending.Actions.Items).Id);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ClearStuckWrite);

        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal("DefenseClaw for Windows", step.Executable);
        Assert.Equal("move-aside", step.Argv[0]);
        foreach (var file in ReadFacts().PendingWrites.Single().AllFiles)
        {
            Assert.Contains(file, step.Argv);
            Assert.True(File.Exists(file));
        }

        Assert.Contains(review.Warnings, w => w.Title == "A file operation, not a DefenseClaw command");
        Assert.Contains("never deleted", review.Summary, StringComparison.Ordinal);
        Assert.Empty(Services.Cli.Activity);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsFinished, vm.Review.ResultText);
        Assert.Empty(ReadFacts().PendingWrites);
        Assert.True(File.Exists(CodexConfig));
        Assert.Equal("model = \"x\"\n", File.ReadAllText(CodexConfig));
        var quarantine = ReadFacts().Quarantine;
        Assert.NotNull(quarantine);
        Assert.StartsWith(Services.CasQuarantineRoot, quarantine!.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(quarantine.Directory, "manifest.json")));
        var entry = Assert.Single(Services.Cli.Activity);
        Assert.Contains("DefenseClaw for Windows", entry.Executable, StringComparison.Ordinal);
        Assert.Equal(0, entry.ExitCode);
    }

    [Fact]
    public async Task Putting_back_restores_the_files_and_the_stuck_write_is_pending_again()
    {
        SeedStuckWrite();
        var vm = Panel();
        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ClearStuckWrite);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        vm.Review.DismissCommand.Execute(null);

        var after = Panel();
        var info = Row(after, "Files moved aside");
        Assert.Equal(new[] { AttentionActionIds.PutBack, AttentionActionIds.ReselectCodex }, info.Actions.Items.Select(a => a.Id).ToArray());

        await after.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.PutBack);
        var step = Assert.Single(after.Review.CommandReview!.Steps);
        Assert.Equal("put-back", step.Argv[0]);
        Assert.Equal("DefenseClaw for Windows", step.Executable);
        await after.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(ReadFacts().PendingWrites);
        Assert.Null(ReadFacts().Quarantine);
    }

    [Fact]
    public async Task The_file_operations_are_off_while_the_gateway_runs_and_do_nothing_when_pressed()
    {
        SeedStuckWrite();
        var vm = Panel(facts => Snapshot(AppGatewayState.Running, facts));

        var action = Assert.Single(Row(vm, "A write to Codex's config.toml").Actions.Items);
        Assert.False(action.Enabled);
        Assert.Equal("Stop the gateway first: it may be writing these files right now.", action.Tip);

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ClearStuckWrite);

        Assert.False(vm.Review.IsOpen);
        Assert.Single(ReadFacts().PendingWrites);
    }

    // ---- a read-only installation --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_read_only_installation_every_button_is_off_with_its_sentence_first_and_pressing_opens_nothing()
    {
        SeedStuckWrite();
        Directory.CreateDirectory(Path.Combine(_temp.Path, "hooks"));
        File.WriteAllText(Path.Combine(_temp.Path, "hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        var context = TestInstallations.Managed();
        var vm = Panel(
            facts => Snapshot(AppGatewayState.GatewayStopped, facts, new GatewayExitReason.StaleAgentExecutable("codex", OldExe)),
            installation: context);

        var actions = vm.Attention.SelectMany(r => r.Actions.Items).ToArray();
        Assert.True(actions.Length >= 3);
        Assert.All(actions, a =>
        {
            Assert.False(a.Enabled);
            Assert.Equal(context.BlockedReason, a.Tip);
        });

        foreach (var id in new[] { AttentionActionIds.ReselectCodex, AttentionActionIds.ClearStuckWrite, AttentionActionIds.SwitchToFailOpen, AttentionActionIds.PutBack })
        {
            await vm.RunAttentionActionCommand.ExecuteAsync(id);
            Assert.False(vm.Review.IsOpen, id);
        }

        Assert.Empty(Services.Cli.Activity);
        Assert.Single(ReadFacts().PendingWrites);
    }

    [Fact]
    public async Task An_unknown_or_empty_action_id_does_nothing()
    {
        var vm = Panel();

        await vm.RunAttentionActionCommand.ExecuteAsync("format-the-disk");
        await vm.RunAttentionActionCommand.ExecuteAsync(null);
        await vm.RunAttentionActionCommand.ExecuteAsync(string.Empty);

        Assert.False(vm.Review.IsOpen);
    }

    // ---- a picture -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_rows_are_drawn_with_their_buttons_and_a_PNG_is_written_when_asked()
    {
        SeedStuckWrite();
        Directory.CreateDirectory(Path.Combine(_temp.Path, "hooks"));
        File.WriteAllText(Path.Combine(_temp.Path, "hooks", ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        _services = TestServices.Create(_temp, Config);
        var facts = new DurabilityFacts(
            new AgentExecutablePin("codex", OldExe, "hook_contract_lock.json", Exists: false),
            ReadFacts().PendingWrites,
            ReadFacts().FailModes,
            new CasQuarantineInfo(Path.Combine(Services.CasQuarantineRoot, "20261015T214000Z"), DateTimeOffset.UtcNow.AddMinutes(-20), 4));
        var snapshot = Snapshot(AppGatewayState.GatewayStopped, facts, new GatewayExitReason.StaleAgentExecutable("codex", OldExe));
        Publish(snapshot);

        PanelShell? shell = null;
        try
        {
            var buttons = UiThread.Run(() =>
            {
                shell = new PanelShell(Services, 1100, 1500);
                var view = shell.Show<OverviewPanel>();
                var vm = (OverviewPanelViewModel)shell.ViewModel;
                vm.Apply(snapshot);
                vm.ToggleAttentionCommand.Execute(null);
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "durability-attention-rows");
                return VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Where(b => b.IsVisible).Select(b => b.Content as string).Where(c => c is not null).ToArray();
            });

            Assert.Contains("Switch to fail open", buttons);
            Assert.Contains("Re-select Codex", buttons);
            Assert.Contains("Clear the stuck pending write", buttons);
            Assert.Contains("Put back", buttons);
            Assert.Contains("Re-run Codex setup", buttons);
            Assert.Single(buttons, "Start gateway");
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
