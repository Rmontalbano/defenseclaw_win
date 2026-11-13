using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>
/// CUST-452 (G9I-01): on an installed DefenseClaw 1.0.9 or later the stuck-pending-write row offers "Finish the interrupted change"
/// (<c>defenseclaw-gateway connector recover-config --data-dir ...</c>) through the shared review and no longer offers the move-aside; on 1.0.8 and
/// older the row, its buttons and its words are today's. Synthetic data throughout.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConnectorRecoveryOverviewTests : IDisposable
{
    private const string StuckId = "068ff1aa22bb33cc44dd55ee";
    private const string Config = "gateway:\n  api_port: 39871\nguardrail:\n  connector: codex\n  enabled: true\n  connectors:\n    codex:\n      mode: enforce\n      hook_fail_mode: open\n";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 22, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private string CodexConfig => Path.Combine(_temp.Path, ".codex", "config.toml");

    private static CasPendingWrite Pending() =>
        new(StuckId, @"C:\dc", new[] { @"C:\dc\.defenseclaw-cas-v2-" + StuckId + ".intent" }, new[] { @"C:\Users\tester\.codex\.tmp-cas-v2-stage-1" }, @"C:\Users\tester\.codex\config.toml", "staging+prepared", Now.AddMinutes(-12));

    private static GatewaySnapshot Snapshot(AppGatewayState state) => new()
    {
        State = state,
        Install = state == AppGatewayState.Running ? InstallState.Running : InstallState.GatewayStopped,
        Detail = "ok",
        Durability = new DurabilityFacts(null, new[] { Pending() }, Array.Empty<ConnectorFailMode>(), null),
        PolledAt = Now,
    };

    // ------------------------------------------------------------------ the pure rows

    [Fact]
    public void Before_1_0_9_the_row_and_its_button_are_exactly_today_s()
    {
        var row = Assert.Single(DurabilityAttention.Rows(Snapshot(AppGatewayState.GatewayStopped), _ => null, Now));

        Assert.Contains("DefenseClaw has no command to recover from this. Clearing it moves the pending files to a quarantine folder (nothing is deleted; you can put them back) while the gateway is stopped.", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.ClearStuckWrite, action.Id);
        Assert.Equal("Clear the stuck pending write", action.Label);
        Assert.Equal(row.Detail, Assert.Single(DurabilityAttention.Rows(Snapshot(AppGatewayState.GatewayStopped), _ => null, Now, recoverConfig: false)).Detail);
    }

    [Fact]
    public void On_1_0_9_the_row_offers_finish_the_interrupted_change_and_not_the_move_aside()
    {
        var row = Assert.Single(DurabilityAttention.Rows(Snapshot(AppGatewayState.GatewayStopped), _ => null, Now, recoverConfig: true));

        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.FinishInterruptedChange, action.Id);
        Assert.Equal("Finish the interrupted change", action.Label);
        Assert.True(action.Enabled);
        Assert.DoesNotContain("DefenseClaw has no command", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear", row.Detail, StringComparison.Ordinal);
        Assert.Contains("connector recover-config", row.Detail, StringComparison.Ordinal);
        Assert.Contains("unrecognized inode", row.Detail, StringComparison.Ordinal);
        Assert.Contains("the upgrade stays refused until that config can be finished", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_only_installation_switches_the_new_button_off_with_its_sentence()
    {
        var row = Assert.Single(DurabilityAttention.Rows(Snapshot(AppGatewayState.GatewayStopped), _ => "read-only", Now, recoverConfig: true));

        var action = Assert.Single(row.Actions.Items);
        Assert.False(action.Enabled);
        Assert.Equal("read-only", action.Tip);
    }

    [Fact]
    public void The_moved_aside_row_does_not_recommend_the_move_or_say_no_command_exists()
    {
        var facts = new DurabilityFacts(null, Array.Empty<CasPendingWrite>(), Array.Empty<ConnectorFailMode>(), new CasQuarantineInfo(@"C:\app\cas-quarantine\20261112T214000Z", Now.AddMinutes(-20), 3));
        var snapshot = Snapshot(AppGatewayState.GatewayStopped) with { Durability = facts };

        foreach (var recover in new[] { false, true })
        {
            var row = Assert.Single(DurabilityAttention.Rows(snapshot, _ => null, Now, recover));
            Assert.Equal(new[] { AttentionActionIds.PutBack, AttentionActionIds.ReselectCodex }, row.Actions.Items.Select(a => a.Id));
            Assert.DoesNotContain("no command", row.Detail, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ the panel and the review

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
    }

    private async Task<OverviewPanelViewModel> PanelAsync(string version, AppGatewayState state)
    {
        SeedStuckWrite();
        _services = TestServices.Create(_temp, Config, FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        var vm = new OverviewPanelViewModel(_services);
        var facts = DurabilityReader.Read(_services.Paths.DataDirectory, _services.Paths.HooksDirectory, _services.CasQuarantineRoot, DateTimeOffset.UtcNow);
        var snapshot = new GatewaySnapshot
        {
            State = state,
            Install = state == AppGatewayState.Running ? InstallState.Running : InstallState.GatewayStopped,
            Detail = "ok",
            CliPath = @"C:\Tools\defenseclaw.exe",
            ApiPort = 39871,
            ActiveConnectors = new[] { "codex" },
            Durability = facts,
            PolledAt = DateTimeOffset.UtcNow,
        };
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_services.Monitor, new object[] { snapshot });
        vm.Apply(snapshot);
        return vm;
    }

    [Theory]
    [InlineData(AppGatewayState.GatewayStopped)]
    [InlineData(AppGatewayState.Running)]
    public async Task On_1_0_9_the_button_reviews_the_gateway_command_with_the_data_dir_and_runs_it_only_after_confirming(AppGatewayState state)
    {
        var vm = await PanelAsync("1.0.9", state);
        var row = Assert.Single(vm.Attention, r => r.Title == "A write to Codex's config.toml is stuck");
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.FinishInterruptedChange, action.Id);
        Assert.True(action.Enabled);   // unlike the move-aside, a running gateway does not switch it off

        var ran = new List<(string Executable, string[] Argv)>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add((executable, argv.ToArray()));
            var done = InvocationFactory.CreateFor(executable, argv.ToArray());
            InvocationFactory.Finish(done, 0);
            return Task.FromResult(done);
        };

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.FinishInterruptedChange);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Finish the interrupted change?", review.Title);
        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw-gateway", step.Executable);
        Assert.Equal(new[] { "connector", "recover-config", "--data-dir", _services!.Paths.DataDirectory }, step.Argv);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.False(review.RestartsGateway);
        foreach (var phrase in new[]
        {
            "with the same recovery DefenseClaw runs before any later change to that file",
            "A change it cannot prove is kept and named",
            "a file you saved is never replaced",
            "finished N interrupted agent config change(s)",
            "finished 0 means nothing was finished; if the Overview still shows a stuck write, check that the folder it prints is your data folder.",
            "the recovery files (the .defenseclaw-cas-v2-* receipts in the data folder) are kept",
            "the upgrade stays refused until that config can be finished",
            "names each config it could not finish",
            "The installer refuses to change version while these recovery files remain and prints this command for you to run; it does not run it.",
        })
        {
            Assert.Contains(phrase, review.Summary, StringComparison.Ordinal);
        }

        // Only what the source shows: nothing about the gateway running or stopping, nothing about closing the agent, and no all-clear for a 0.
        Assert.DoesNotContain("keep running", review.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("before it stops the gateway", review.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing was left", review.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quit", review.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("close the agent", review.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ran);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var run = Assert.Single(ran);
        Assert.Equal("defenseclaw-gateway", run.Executable);
        Assert.Equal(step.Argv, run.Argv);
    }

    [Fact]
    public async Task On_1_0_8_the_panel_still_offers_only_the_move_aside_and_a_running_gateway_switches_it_off()
    {
        var vm = await PanelAsync("1.0.8", AppGatewayState.Running);

        var row = Assert.Single(vm.Attention, r => r.Title == "A write to Codex's config.toml is stuck");
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.ClearStuckWrite, action.Id);
        Assert.Equal("Clear the stuck pending write", action.Label);
        Assert.False(action.Enabled);
        Assert.Equal("Stop the gateway first: it may be writing these files right now.", action.Tip);
        Assert.Contains("DefenseClaw has no command to recover from this", row.Detail, StringComparison.Ordinal);
    }
}
