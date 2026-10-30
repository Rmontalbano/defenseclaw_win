using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// DefenseClaw 1.0.5 on the Overview (CUST-398), synthetic throughout: the removed connectors' notice (an Info row when the entry carries it), the
/// install lock 1.0.5 ignores (a reviewed doctor repair) and the one an interrupted install keeps (the installer), each against 1.0.4, the time of the
/// last failed event-history write, and the agent settings that turn every hook off. Renders the attention card when <c>DC_RENDER_DIR</c> is set.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class Runtime105Tests : IDisposable
{
    /// <summary>A pid no process has (Windows pids are multiples of four, and this is far above any real one).</summary>
    private const int GonePid = 2147483646;

    private const string Config =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n    claudecode:\n      mode: observe\n";

    private const string LastFailureAt = "2026-10-29T08:15:30.123456789Z";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private AppServices Services => _services!;

    private string ClaudeSettingsPath => _temp.File("claude-settings.json");

    private string CodexConfigPath => Path.Combine(_temp.Path, "codex-home", "config.toml");

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<OverviewPanelViewModel> PanelAsync(string? version, InstallationContext? installation = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Config, version is null ? null : Runtime(version), installation);
        if (version is not null)
        {
            _ = await _services.Runtime.RefreshAsync(force: true);
        }

        var vm = new OverviewPanelViewModel(_services);
        vm.Apply(Snapshot());
        return vm;
    }

    private static GatewaySnapshot Snapshot(GatewayHealth? health = null, params string[] active) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = active.Length == 0 ? new[] { "codex", "claudecode" } : active,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private string MakeLock(int? pid, string? record = null)
    {
        var path = Path.Combine(_temp.Path, InterruptedInstallLock.LockName);
        _ = Directory.CreateDirectory(path);
        if (pid is { } value)
        {
            File.WriteAllText(Path.Combine(path, "pid"), value.ToString(CultureInfo.InvariantCulture) + "\r\n");
        }

        if (record is not null)
        {
            File.WriteAllText(Path.Combine(_temp.Path, record), "{}");
        }

        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        return path;
    }

    private static string CleanupStatus(bool notice) =>
        "{\"environment\":\"dev\",\"removed_connector_cleanup\":[" +
        "{\"connector\":\"openhands\",\"label\":\"OpenHands\"," +
        (notice ? "\"notice\":\"OpenHands/OmniGent support was removed in 1.0.5; DefenseClaw no longer guards it.\"," : string.Empty) +
        "\"items\":[{\"path\":\"C:\\\\Users\\\\operator\\\\.openhands\\\\hooks.json\",\"what\":\"still names a hook script an older setup wrote\"," +
        "\"step\":\"remove the hook entry from this file, then restart OpenHands\"}]}]}";

    private static GatewayHealth HealthWithFailureTime(bool withTime)
    {
        var json = OverviewScene.HealthJson(eventHistoryFailure: "sqlite_write_failed");
        if (withTime)
        {
            json = json.Replace(
                "\"event_history_failure\":\"sqlite_write_failed\"",
                "\"event_history_failure\":\"sqlite_write_failed\",\"event_history_last_sqlite_class\":\"io\",\"event_history_last_failure_at\":\"" + LastFailureAt + "\"",
                StringComparison.Ordinal);
        }

        return JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    // ------------------------------------------------------------------ removed connectors: the notice

    [Fact]
    public void The_status_read_keeps_the_notice_when_the_runtime_sends_one_and_none_otherwise()
    {
        var with = Assert.Single(DefenseClawStatusReader.Parse(CleanupStatus(notice: true)).RemovedConnectorCleanup);
        Assert.Equal("OpenHands/OmniGent support was removed in 1.0.5; DefenseClaw no longer guards it.", with.Notice);

        var without = Assert.Single(DefenseClawStatusReader.Parse(CleanupStatus(notice: false)).RemovedConnectorCleanup);
        Assert.Null(without.Notice);

        var blank = DefenseClawStatusReader.Parse(CleanupStatus(notice: true).Replace("OpenHands/OmniGent support was removed in 1.0.5; DefenseClaw no longer guards it.", "  ", StringComparison.Ordinal));
        Assert.Null(Assert.Single(blank.RemovedConnectorCleanup).Notice);
    }

    [Fact]
    public async Task With_the_notice_the_leftovers_are_information_and_without_it_they_stay_the_1_0_4_warning()
    {
        var vm = await PanelAsync("1.0.5");

        vm.ApplyStatus(DefenseClawStatusReader.Parse(CleanupStatus(notice: true)));

        var info = Assert.Single(vm.Attention, r => r.Title.StartsWith("OpenHands:", StringComparison.Ordinal));
        Assert.Equal("Info", info.SeverityKey);
        Assert.Equal("OpenHands: 1 leftover from an older setup", info.Title);
        Assert.StartsWith("OpenHands/OmniGent support was removed in 1.0.5", info.Detail, StringComparison.Ordinal);
        Assert.Contains("none of this blocks your agents", info.Detail, StringComparison.Ordinal);
        Assert.Contains("remove the hook entry from this file", info.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("was removed;", StringComparison.Ordinal));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(CleanupStatus(notice: false)));

        var old = Assert.Single(vm.Attention, r => r.Title.StartsWith("OpenHands was removed", StringComparison.Ordinal));
        Assert.Equal("High", old.SeverityKey);
        Assert.Contains("removed in DefenseClaw 1.0.4", old.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the install lock: stale vs recovery, 1.0.4 vs 1.0.5

    [Fact]
    public async Task On_1_0_5_a_lock_whose_installer_is_gone_does_not_block_and_offers_the_reviewed_doctor_repair()
    {
        var vm = await PanelAsync("1.0.5");
        var path = MakeLock(GonePid);

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        Assert.True(vm.LastInstallLock!.IsStale);
        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.StaleInstallLockTitle);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("it does not block your agents", row.Detail, StringComparison.Ordinal);
        Assert.Contains(path, row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(OverviewPanelViewModel.ClearStaleInstallLockActionId, action.Id);
        Assert.True(action.Enabled);

        var ran = new List<string[]>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(argv.ToArray());
            var done = InvocationFactory.CreateFor(executable, argv.ToArray());
            InvocationFactory.Finish(done, 0);
            return Task.FromResult(done);
        };
        await vm.RunAttentionActionCommand.ExecuteAsync(action.Id);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "doctor", "--fix", "--yes", "--fix-id", "doctor.install.stale-lock.clear" }, step.Argv);
        Assert.Equal(CommandReview.DefaultExecutable, step.Executable);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.False(review.RestartsGateway);
        Assert.Empty(ran);
        vm.Review.DismissCommand.Execute(null);
    }

    [Fact]
    public async Task On_1_0_4_the_same_lock_keeps_the_blocking_row_and_the_installer()
    {
        var vm = await PanelAsync("1.0.4");
        _ = MakeLock(GonePid);

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
        Assert.Contains("agent hooks are blocking until it's cleared", row.Detail, StringComparison.Ordinal);
        Assert.Equal(OverviewPanelViewModel.OpenInstallerActionId, Assert.Single(row.Actions.Items).Id);
        Assert.DoesNotContain(vm.Attention, r => r.Title == OverviewPanelViewModel.StaleInstallLockTitle);
    }

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.0.4", false)]
    public async Task A_lock_an_interrupted_install_keeps_still_waits_for_the_installer(string version, bool is105)
    {
        var vm = await PanelAsync(version);
        _ = MakeLock(GonePid, record: ".lifecycle-txn.json");

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        Assert.True(vm.LastInstallLock!.IsRecovery);
        var row = Assert.Single(vm.Attention, r => r.Title == (is105 ? OverviewPanelViewModel.RecoveryInstallLockTitle : OverviewPanelViewModel.InterruptedInstallTitle));
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("Running the installer again", row.Detail, StringComparison.Ordinal);
        Assert.Equal(OverviewPanelViewModel.OpenInstallerActionId, Assert.Single(row.Actions.Items).Id);
        Assert.Equal(is105, row.Detail.Contains("follow the gateway-outage rule", StringComparison.Ordinal));
        Assert.Equal(is105, row.Detail.Contains(".lifecycle-txn.json", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Attention, r => r.Title == OverviewPanelViewModel.StaleInstallLockTitle);

        // Its button still opens the installer, and a stale lock's id does nothing for it.
        AppServices? opened = null;
        vm.OpenInstaller = s => opened = s;
        await vm.RunAttentionActionCommand.ExecuteAsync(OverviewPanelViewModel.ClearStaleInstallLockActionId);
        Assert.False(vm.Review.IsOpen);
        await vm.RunAttentionActionCommand.ExecuteAsync(OverviewPanelViewModel.OpenInstallerActionId);
        Assert.Same(_services, opened);
    }

    [Fact]
    public async Task On_1_0_5_a_lock_whose_pid_a_live_process_has_keeps_the_blocking_wording()
    {
        var vm = await PanelAsync("1.0.5");
        _ = MakeLock(Environment.ProcessId);

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
        Assert.Contains("agent hooks are blocking until it's cleared", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_managed_install_the_repair_button_is_off_with_the_installations_reason()
    {
        var vm = await PanelAsync("1.0.5", TestInstallations.Managed(_temp.Path));
        _ = MakeLock(GonePid);

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        var action = Assert.Single(Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.StaleInstallLockTitle).Actions.Items);
        Assert.False(action.Enabled);
        await vm.RunAttentionActionCommand.ExecuteAsync(action.Id);
        Assert.False(vm.Review.IsOpen);
    }

    // ------------------------------------------------------------------ the time of the last failed event-history write

    [Fact]
    public async Task The_event_history_failure_names_the_time_of_the_last_failed_write_when_the_gateway_sends_it()
    {
        var vm = await PanelAsync("1.0.5");
        var at = DateTimeOffset.Parse(LastFailureAt, CultureInfo.InvariantCulture);

        vm.Apply(Snapshot(HealthWithFailureTime(withTime: true)));

        var expected = "sqlite_write_failed · last failure " + OverviewPanelViewModel.FormatResultTime(at);
        Assert.Equal(expected, vm.EventHistoryFailure);
        var row = Assert.Single(vm.Attention, r => r.Title == "The gateway cannot keep its event history");
        Assert.Contains(expected, row.Detail, StringComparison.Ordinal);

        vm.Apply(Snapshot(HealthWithFailureTime(withTime: false)));
        Assert.Equal("sqlite_write_failed", vm.EventHistoryFailure);
    }

    [Theory]
    [InlineData("\"event_history_last_failure_at\":\"yesterday\"")]
    [InlineData("\"event_history_last_failure_at\":12345")]
    [InlineData("\"event_history_last_failure_at\":null")]
    public void A_failure_time_that_is_not_a_time_is_left_out(string member)
    {
        var json = OverviewScene.HealthJson(eventHistoryFailure: "sqlite_write_failed")
            .Replace("\"event_history_failure\":\"sqlite_write_failed\"", "\"event_history_failure\":\"sqlite_write_failed\"," + member, StringComparison.Ordinal);
        var health = JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Null(OverviewPanelViewModel.FindFailureTime(health));
        Assert.Equal("sqlite_write_failed", OverviewPanelViewModel.EventHistoryFailureText(health));
    }

    // ------------------------------------------------------------------ agent settings that turn hooks off

    private void WriteAgentSettings(string? claude, string? codex)
    {
        if (claude is not null)
        {
            File.WriteAllText(ClaudeSettingsPath, claude);
        }

        if (codex is not null)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(CodexConfigPath)!);
            File.WriteAllText(CodexConfigPath, codex);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.3")]
    [InlineData("1.0.5")]
    public async Task Both_agents_turning_their_hooks_off_are_rows_on_any_runtime(string? version)
    {
        var vm = await PanelAsync(version);
        WriteAgentSettings("{\r\n  \"disableAllHooks\": true\r\n}", "model = \"o3\"\r\n[features]\r\nhooks = false\r\n");

        await vm.RefreshAgentHookSwitchesAsync(CancellationToken.None);

        var claude = Assert.Single(vm.Attention, r => r.Title == "Claude Code: its settings turn every hook off");
        Assert.Equal("High", claude.SeverityKey);
        Assert.Contains(ClaudeSettingsPath, claude.Detail, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw is not guarding its tool calls", claude.Detail, StringComparison.Ordinal);
        Assert.False(claude.HasCommand);

        var codex = Assert.Single(vm.Attention, r => r.Title == "Codex: its config turns hooks off");
        Assert.Equal("High", codex.SeverityKey);
        Assert.Contains("[features] hooks = false", codex.Detail, StringComparison.Ordinal);
        Assert.Contains(CodexConfigPath, codex.Detail, StringComparison.Ordinal);
        Assert.Equal("codex features enable hooks", codex.Command);

        // Turned back on: the rows go with the next read.
        WriteAgentSettings("{\"disableAllHooks\": false}", "[features]\nhooks = true\n");
        await vm.RefreshAgentHookSwitchesAsync(CancellationToken.None);
        Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("turn", StringComparison.Ordinal) && r.Title.Contains("hook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_an_active_connector_is_read_and_shown()
    {
        var vm = await PanelAsync("1.0.5");
        WriteAgentSettings("{\"disableAllHooks\": true}", "[features]\ncodex_hooks = false\n");
        vm.Apply(Snapshot(null, "codex"));

        await vm.RefreshAgentHookSwitchesAsync(CancellationToken.None);

        Assert.Null(vm.LastHookSwitches.ClaudeSettingsPath);
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith("Claude Code: its settings", StringComparison.Ordinal));
        var codex = Assert.Single(vm.Attention, r => r.Title == "Codex: its config turns hooks off");
        Assert.Contains("[features] codex_hooks = false", codex.Detail, StringComparison.Ordinal);

        // Codex leaves the roster: its row goes at once, before any new read.
        vm.Apply(Snapshot(null, "hermes"));
        Assert.DoesNotContain(vm.Attention, r => r.Title == "Codex: its config turns hooks off");
    }

    [Theory]
    [InlineData("{\"disableAllHooks\": true", "[features]\nhooks = false\nnot toml at all\n")]
    [InlineData("{\"disableAllHooks\": \"true\"}", "[features\nhooks = false\n")]
    [InlineData("", "")]
    public async Task Malformed_or_ambiguous_agent_settings_raise_no_row(string claude, string codex)
    {
        var vm = await PanelAsync("1.0.5");
        WriteAgentSettings(claude, codex);

        await vm.RefreshAgentHookSwitchesAsync(CancellationToken.None);

        Assert.Equal(default, vm.LastHookSwitches);
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith("Claude Code: its settings", StringComparison.Ordinal) || r.Title.StartsWith("Codex: its config", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_managed_installation_is_not_asked()
    {
        var vm = await PanelAsync("1.0.5", TestInstallations.Managed(_temp.Path));
        WriteAgentSettings("{\"disableAllHooks\": true}", "[features]\nhooks = false\n");

        await vm.RefreshAgentHookSwitchesAsync(CancellationToken.None);

        Assert.Equal(default, vm.LastHookSwitches);
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith("Claude Code: its settings", StringComparison.Ordinal) || r.Title.StartsWith("Codex: its config", StringComparison.Ordinal));
    }

    [Fact]
    public void An_isolated_composition_never_reads_the_operators_codex_config()
    {
        _services = TestServices.Create(_temp, Config);

        Assert.Equal(CodexConfigPath, Services.CodexConfig.ConfigPath);
    }

    // ------------------------------------------------------------------ a picture

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { "980x692", "1468x875" }
        from mode in new[] { "Light", "Dark" }
        select new object[] { size, mode };

    /// <summary>The attention card on a 1.0.5 fixture with every new row: the notice, the stale lock and its button, both agents' hooks off, and the failure time.</summary>
    [Theory]
    [MemberData(nameof(Renders))]
    public async Task The_attention_card_draws_the_1_0_5_rows(string size, string mode)
    {
        _services = TestServices.Create(_temp, Config, Runtime("1.0.5"));
        _ = await _services.Runtime.RefreshAsync(force: true);
        _ = MakeLock(GonePid);
        WriteAgentSettings("{\"disableAllHooks\": true}", "[features]\nhooks = false\n");
        var snapshot = Snapshot(HealthWithFailureTime(withTime: true));
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Services.Monitor, new object[] { snapshot });
        var parts = size.Split('x');
        var (width, height) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var fixture = new DefenseClaw.App.Tests.Appearance.AppearanceFixture(
            new DefenseClaw.App.Services.Appearance.AppearanceSettings(
                DefenseClaw.App.Services.Appearance.AppearanceStyle.Default, Enum.Parse<DefenseClaw.App.Services.Appearance.AppearanceMode>(mode)));

        PanelShell? shell = null;
        OverviewPanel? view = null;
        try
        {
            var vm = UiThread.Run(() =>
            {
                shell = new PanelShell(Services, width, height);
                view = shell.Show<OverviewPanel>();
                var panel = (OverviewPanelViewModel)shell.ViewModel;
                panel.Apply(snapshot);
                panel.ApplyStatus(DefenseClawStatusReader.Parse(CleanupStatus(notice: true)));
                return panel;
            });
            await UiThread.Run(() => vm.RefreshInstallLockAsync(CancellationToken.None));
            await UiThread.Run(() => vm.RefreshAgentHookSwitchesAsync(CancellationToken.None));

            var buttons = UiThread.Run(() =>
            {
                if (!vm.ShowAllAttention)
                {
                    vm.ToggleAttentionCommand.Execute(null);
                }

                shell!.Host.Relayout();
                RenderTo.Png(shell.Host, $"cust398-attention-{size}-{mode.ToLowerInvariant()}");
                return VisualTree.Descendants<Wpf.Ui.Controls.Button>(view!)
                    .Where(b => b.IsVisible).Select(b => b.Content as string).Where(c => c is not null).ToArray();
            });

            Assert.Contains(vm.Attention, r => r.Title == OverviewPanelViewModel.StaleInstallLockTitle);
            Assert.Contains(vm.Attention, r => r.Title == "Claude Code: its settings turn every hook off");
            Assert.Contains(vm.Attention, r => r.Title == "Codex: its config turns hooks off");
            Assert.Contains(vm.Attention, r => r.Title == "OpenHands: 1 leftover from an older setup");
            Assert.Contains(vm.Attention, r => r.Title == "The gateway cannot keep its event history" && r.Detail.Contains("last failure", StringComparison.Ordinal));
            Assert.Contains("Remove the lock", buttons);
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
