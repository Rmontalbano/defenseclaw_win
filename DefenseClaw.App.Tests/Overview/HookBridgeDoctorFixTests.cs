using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The doctor row for an agent hook that is still the older PowerShell EncodedCommand bridge (CUST-448, runtime 1.0.8; doctor_hooks.py): the pure parser
/// and the Overview row with its reviewed "Re-run setup". Synthetic fixtures.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class HookBridgeDoctorFixTests : IDisposable
{
    private const string Phrase = "stale: PowerShell EncodedCommand hook exits 0 in Constrained Language mode when the launcher cannot start; ";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static DoctorCheckRow Check(string label, string command, string status = "fail") =>
        new() { Label = label, Status = status, Detail = Phrase + "run `" + command + "` to repair the native registration" };

    // ------------------------------------------------------------------ the parser

    [Theory]
    [InlineData("Codex hooks", "codex", "defenseclaw setup codex --yes --restart")]
    [InlineData("Claude Code hooks", "claude-code", "defenseclaw setup claude-code --yes --restart")]
    [InlineData("Copilot hooks", "copilot", "defenseclaw setup copilot --yes --restart")]
    [InlineData("Antigravity hooks", "antigravity", "defenseclaw setup antigravity --yes --restart")]
    [InlineData("Devin hooks", "devin", "defenseclaw setup devin --yes --restart")]
    [InlineData("Hermes hooks", "hermes", "defenseclaw setup hermes --yes --restart")]
    [InlineData("Codex hooks [codex]", "codex", "defenseclaw setup codex --yes --restart")]
    [InlineData("Copilot hooks [copilot]", "copilot", "defenseclaw setup copilot --mode action --yes --restart")]
    [InlineData("Copilot hooks", "copilot", "defenseclaw setup copilot --mode observe --yes --restart")]
    public void Each_agents_failing_hooks_row_gives_its_exact_setup_command(string label, string connector, string command)
    {
        var repair = HookBridgeDoctorFix.From(Check(label, command));

        Assert.NotNull(repair);
        Assert.Equal(connector, repair.Connector);
        Assert.Equal(command, repair.Command);
        Assert.Equal(command.Split(' ').Skip(1), repair.Argv);
        Assert.Equal(label.Split(' ')[0] == "Claude" ? "Claude Code" : label.Split(' ')[0], repair.Agent);
    }

    [Fact]
    public void Single_quotes_stand_for_backticks()
    {
        var row = new DoctorCheckRow
        {
            Label = "Codex hooks",
            Status = "fail",
            Detail = Phrase + "run 'defenseclaw setup codex --yes --restart' to repair the native registration",
        };

        Assert.Equal("defenseclaw setup codex --yes --restart", HookBridgeDoctorFix.From(row)?.Command);
    }

    [Fact]
    public void The_action_id_tells_two_copilot_modes_apart()
    {
        var plain = HookBridgeDoctorFix.From(Check("Copilot hooks", "defenseclaw setup copilot --yes --restart"))!;
        var action = HookBridgeDoctorFix.From(Check("Copilot hooks", "defenseclaw setup copilot --mode action --yes --restart"))!;

        Assert.NotEqual(plain.ActionId, action.ActionId);
    }

    [Theory]
    [InlineData("Codex hooks", "defenseclaw setup claude-code --yes --restart")]
    [InlineData("Claude Code hooks", "defenseclaw setup codex --yes --restart")]
    [InlineData("Hermes hooks", "defenseclaw setup devin --yes --restart")]
    public void A_command_for_another_agent_than_the_label_is_nothing(string label, string command) =>
        Assert.Null(HookBridgeDoctorFix.From(Check(label, command)));

    [Theory]
    [InlineData("pass")]
    [InlineData("warn")]
    [InlineData("skip")]
    [InlineData("FAIL")]
    public void Only_a_failing_row_counts(string status) =>
        Assert.Null(HookBridgeDoctorFix.From(Check("Codex hooks", "defenseclaw setup codex --yes --restart", status)));

    [Theory]
    [InlineData("Kiro hooks")]
    [InlineData("Codex config")]
    [InlineData("Codex hooks [Bad Name]")]
    [InlineData("Codex hooks extra")]
    [InlineData("codex hooks")]
    [InlineData("")]
    public void A_label_that_is_not_one_of_the_agents_hooks_rows_is_nothing(string label) =>
        Assert.Null(HookBridgeDoctorFix.From(Check(label, "defenseclaw setup codex --yes --restart")));

    [Theory]
    [InlineData("hook is missing")]
    [InlineData("PowerShell EncodedCommand hook exits 0 in Constrained Language mode: run `defenseclaw setup codex` to repair the native registration")]
    [InlineData("PowerShell EncodedCommand hook exits 0 in Constrained Language mode: run `defenseclaw setup codex --yes --restart && calc` to repair the native registration")]
    [InlineData("PowerShell EncodedCommand hook exits 0 in Constrained Language mode: run `defenseclaw setup codex --mode other --yes --restart` to repair the native registration")]
    [InlineData("PowerShell EncodedCommand hook exits 0 in Constrained Language mode: run `defenseclaw setup kiro --yes --restart` to repair the native registration")]
    [InlineData("")]
    public void Any_other_detail_is_nothing(string detail) =>
        Assert.Null(HookBridgeDoctorFix.From(new DoctorCheckRow { Label = "Codex hooks", Status = "fail", Detail = detail }));

    // ------------------------------------------------------------------ the row

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private const string Config = "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n";

    private static string CacheWithBridge(DateTimeOffset capturedAt, string extraDetail = "") =>
        "{\"passed\":1,\"failed\":1,\"warned\":0,\"skipped\":0,\"captured_at\":\"" + capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\",\"checks\":[" +
        "{\"status\":\"fail\",\"label\":\"Codex hooks\",\"detail\":\"stale: PowerShell EncodedCommand hook exits 0 in Constrained Language mode when the launcher cannot start; run `defenseclaw setup codex --yes --restart` to repair the native registration" + extraDetail + "\"}," +
        "{\"status\":\"pass\",\"label\":\"Config file\",\"detail\":\"ok\"}]}";

    private static GatewaySnapshot Snapshot() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = null,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private async Task<OverviewPanelViewModel> PanelAsync(string version, DateTimeOffset? capturedAt = null, string extraDetail = "")
    {
        _services = TestServices.Create(_temp, Config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        _ = _temp.WriteFile("doctor_cache.json", CacheWithBridge(capturedAt ?? DateTimeOffset.UtcNow.AddMinutes(-2), extraDetail));
        var vm = new OverviewPanelViewModel(_services);
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        vm.Apply(Snapshot());
        return vm;
    }

    private static bool IsBridgeRow(AttentionRow row) => row.Title.EndsWith("hooks need to be set up again", StringComparison.Ordinal);

    [Fact]
    public async Task On_1_0_8_the_failing_row_is_a_high_attention_row_with_a_reviewed_re_run_setup()
    {
        var vm = await PanelAsync("1.0.8");

        var row = Assert.Single(vm.Attention, IsBridgeRow);
        Assert.Equal("Codex hooks need to be set up again", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("EncodedCommand", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Constrained Language mode", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Doctor: Codex hooks: stale:", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal("Re-run setup", action.Label);

        // The generic doctor row stays.
        Assert.Contains(vm.Attention, static r => r.Title.StartsWith("Doctor found 1 failure", StringComparison.Ordinal));

        // Pressing the button opens a review of the exact command; nothing runs.
        await vm.RunAttentionActionCommand.ExecuteAsync(action.Id);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(new[] { "setup", "codex", "--yes", "--restart" }, step.Argv);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public async Task On_1_0_7_there_is_no_such_row_and_the_generic_one_is_all_there_is()
    {
        var vm = await PanelAsync("1.0.7");

        Assert.DoesNotContain(vm.Attention, IsBridgeRow);
        Assert.Contains(vm.Attention, static r => r.Title.StartsWith("Doctor found 1 failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_row_says_when_doctor_last_ran()
    {
        var vm = await PanelAsync("1.0.8", DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Contains("(doctor ran 5m ago)", Assert.Single(vm.Attention, IsBridgeRow).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_doctor_result_older_than_the_stale_window_makes_no_row_and_no_review()
    {
        var vm = await PanelAsync("1.0.8", DateTimeOffset.UtcNow - OverviewPanelViewModel.DoctorStaleAfter - TimeSpan.FromMinutes(5));

        Assert.DoesNotContain(vm.Attention, IsBridgeRow);

        await vm.RunAttentionActionCommand.ExecuteAsync("hook-bridge-setup:codex");
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task After_the_repair_succeeded_the_row_is_gone_until_a_newer_doctor_result_fails_again()
    {
        var captured = DateTimeOffset.UtcNow.AddMinutes(-3);
        var vm = await PanelAsync("1.0.8", captured);
        Assert.Single(vm.Attention, IsBridgeRow);

        await vm.OnHookBridgeRepairFinishedAsync("codex", new DiscoverReviewResult(true, []), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.DoesNotContain(vm.Attention, IsBridgeRow);

        // doctor ran again after the repair and still fails: the row is back.
        _ = _temp.WriteFile("doctor_cache.json", CacheWithBridge(DateTimeOffset.UtcNow));
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        vm.Apply(Snapshot());
        Assert.Single(vm.Attention, IsBridgeRow);
    }

    [Fact]
    public async Task A_repair_that_failed_leaves_the_row()
    {
        var vm = await PanelAsync("1.0.8");

        await vm.OnHookBridgeRepairFinishedAsync("codex", new DiscoverReviewResult(false, []), DateTimeOffset.UtcNow);

        Assert.Single(vm.Attention, IsBridgeRow);
    }

    [Fact]
    public async Task The_doctor_text_in_the_row_is_cut_at_400_characters()
    {
        var vm = await PanelAsync("1.0.8", extraDetail: " " + new string('x', 2000));

        var detail = Assert.Single(vm.Attention, IsBridgeRow).Detail;
        var at = detail.IndexOf("Doctor: Codex hooks:", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var doctorText = detail[at..detail.IndexOf(" (doctor ran", StringComparison.Ordinal)];
        Assert.Equal(OverviewPanelViewModel.MaxHookBridgeDetail, doctorText.Length);
        Assert.EndsWith("…", doctorText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ order and navigation buttons

    [Fact]
    public async Task A_high_row_is_listed_before_a_medium_one_built_earlier_and_equal_severities_keep_their_order()
    {
        var vm = await PanelAsync("1.0.8");

        // Built in this order: "gateway answered, not cleanly" (Medium) comes before the bridge row (High) and the generic doctor row.
        vm.Apply(Snapshot() with { State = AppGatewayState.Degraded, Detail = "answered 500" });

        var severities = vm.Attention.Select(static r => r.SeverityKey).ToList();
        Assert.Contains("Medium", severities);
        Assert.Contains("High", severities);
        Assert.True(severities.IndexOf("High") < severities.IndexOf("Medium"), string.Join(",", severities));
        Assert.Equal(severities.OrderBy(static s => s switch { "Critical" => 0, "High" => 1, "Medium" => 2, _ => 3 }).ToList(), severities);
    }
}
