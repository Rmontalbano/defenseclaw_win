using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// What the Overview says about the gateway's health signals new in runtime 1.0.9, synthetic and verified from source only: the watcher's "admission scanning is
/// off" report (sidecar.go publishWatcherHealth), the repair beside a skipped connector (connectors_not_started_repairs), the hook launcher and hook guard
/// rows of doctor (hook_integrity.py), the judge max_parallel hint for a local judge, and the host sensor's dropped-events reason. Each against a 1.0.9 and a
/// 1.0.8 runtime; the owner-machine shape at the end draws nothing new.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RuntimeHealthSignalsTests : IDisposable
{
    private const string Config = "config_version: 9\nguardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n" +
                                  "ai_discovery:\n  runtime:\n    enabled: true\n    planes: [a, b]\n";

    private const string OwnSockets =
        "shadow egress partially covered: egress attribution is limited to this process's own sockets; run the gateway elevated for machine-wide coverage";

    private const string DropReason =
        "agent actions partially covered: 40 events were dropped from the sensor buffer under back-pressure (file events first; process events displaced the oldest)";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        string.Join(' ', args) switch
        {
            "--version-json" => RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}"),
            "agent discovery --help" => RuntimeProbeOutput.Ok("Usage: defenseclaw agent discovery [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  runtime  Runtime monitoring.\n"),
            "agent discovery runtime --help" => RuntimeProbeOutput.Ok(
                "Usage: defenseclaw agent discovery runtime [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  status  Status.\n  scan  Scan.\n  findings  Findings.\n  permissions  Permissions.\n"),
            _ => RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"),
        });

    private async Task<OverviewPanelViewModel> PanelAsync(string version, string config = Config, string? doctorCache = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        if (doctorCache is not null)
        {
            _ = _temp.WriteFile("doctor_cache.json", doctorCache);
        }

        var vm = new OverviewPanelViewModel(_services);
        if (doctorCache is not null)
        {
            await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        }

        return vm;
    }

    private static GatewaySnapshot Snapshot(object? watcher = null, object? guardrailDetails = null, object? aiRuntime = null)
    {
        var block = new Dictionary<string, object?>
        {
            ["uptime_ms"] = 600000,
            ["api"] = new { state = "running" },
            ["watcher"] = watcher ?? new { state = "running", details = new { skill_dirs = 2, plugin_dirs = 1, skill_take_action = false, plugin_take_action = false, mcp_take_action = false } },
            ["guardrail"] = new { state = "running", details = guardrailDetails ?? new { policy_mode = "observe" } },
        };
        if (aiRuntime is not null)
        {
            block["ai_runtime"] = aiRuntime;
        }

        return new GatewaySnapshot
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            Detail = "ok",
            Health = JsonSerializer.Deserialize<GatewayHealth>(JsonSerializer.Serialize(block), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ActiveConnectors = ["codex"],
            PolledAt = DateTimeOffset.UtcNow,
        };
    }

    // ------------------------------------------------------------------ watcher admission

    private static object DegradedWatcher(object details) => new
    {
        state = "degraded",
        last_error = "admission scanning is off for one or more connector roots; the gateway log names each root",
        details,
    };

    private static bool IsAdmissionRow(AttentionRow row) => row.Title.StartsWith(OverviewPanelViewModel.WatcherAdmissionTitlePrefix, StringComparison.Ordinal);

    [Fact]
    public async Task A_watcher_with_roots_unavailable_is_a_medium_row_with_the_windows_cause_and_a_tile_reason()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(DegradedWatcher(new
        {
            skill_dirs = 2,
            plugin_dirs = 1,
            watch_roots_unavailable = 2,
            admission_scanning_off = new[] { "claudecode", "codex" },
            admission_root_writable_folders = new[] { @"C:\Users\operator\shared" },
        })));

        var row = Assert.Single(vm.Attention, IsAdmissionRow);
        Assert.Equal("Admission scanning is off for claudecode and codex", row.Title);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("2 skill or plugin folders", row.Detail, StringComparison.Ordinal);
        Assert.Contains("junction or a symbolic link", row.Detail, StringComparison.Ordinal);
        Assert.Contains("The gateway log names each folder.", row.Detail, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\operator\shared", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasActions);
        Assert.False(row.HasCommand);

        var tile = Assert.Single(vm.ServiceRows, static r => r.Name == "Watchdog");
        Assert.Contains("2 skill dirs, 1 plugin dirs", tile.Detail, StringComparison.Ordinal);
        Assert.Contains("admission scanning is off for one or more connector roots", tile.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_connector_names_reads_configured_connectors_and_the_lists_are_bounded()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(DegradedWatcher(new { watch_roots_unavailable = 1 })));
        Assert.Equal("Admission scanning is off for configured connectors", Assert.Single(vm.Attention, IsAdmissionRow).Title);

        var folders = Enumerable.Range(0, 40).Select(static i => @"C:\Users\operator\f" + i.ToString(CultureInfo.InvariantCulture) + new string('x', 400)).ToArray();
        vm.Apply(Snapshot(DegradedWatcher(new { watch_roots_unavailable = 40, admission_root_writable_folders = folders })));
        var detail = Assert.Single(vm.Attention, IsAdmissionRow).Detail;
        Assert.Contains(@"C:\Users\operator\f5", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\operator\f6", detail, StringComparison.Ordinal);
        Assert.Contains("and 34 more", detail, StringComparison.Ordinal);
        Assert.True(detail.Length < 3000);
    }

    [Fact]
    public async Task A_degraded_watcher_without_the_key_or_a_running_one_has_no_row_and_the_tile_is_as_it_was()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(new { state = "degraded", last_error = "something else", details = new { skill_dirs = 2, plugin_dirs = 1 } }));

        Assert.DoesNotContain(vm.Attention, IsAdmissionRow);
        Assert.Equal("2 skill dirs, 1 plugin dirs", Assert.Single(vm.ServiceRows, static r => r.Name == "Watchdog").Detail);

        vm.Apply(Snapshot(new { state = "running", details = new { watch_roots_unavailable = 0, skill_dirs = 2 } }));
        Assert.DoesNotContain(vm.Attention, IsAdmissionRow);
    }

    // ------------------------------------------------------------------ skipped connector repair

    private static object Skipped(object? repair, string reason = "the plugin folder C:\\Users\\operator\\.claude\\plugins can be changed by other accounts") => new
    {
        policy_mode = "observe",
        connectors_not_started = new[] { "claudecode" },
        connectors_not_started_reasons = new Dictionary<string, string> { ["claudecode"] = reason },
        connectors_not_started_repairs = repair is null ? null : new Dictionary<string, object> { ["claudecode"] = repair },
    };

    private static bool IsSkipRow(AttentionRow row) => row.Title.StartsWith("Claude Code is not enforced", StringComparison.Ordinal);

    [Fact]
    public async Task The_repair_is_shown_as_a_copyable_fix_and_a_by_hand_step()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(guardrailDetails: Skipped(new
        {
            fix = @"icacls C:\Users\operator\.claude\plugins /inheritance:r",
            by_hand = "Remove write access for other accounts from the plugin folder",
        })));

        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Equal(@"icacls C:\Users\operator\.claude\plugins /inheritance:r", row.Command);
        Assert.Contains("The command below is the gateway's fix.", row.Detail, StringComparison.Ordinal);
        Assert.Contains("By hand: Remove write access for other accounts from the plugin folder.", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Then run: defenseclaw setup claudecode.", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasActions);
    }

    [Fact]
    public async Task Omitted_bytes_say_the_repair_is_too_long_and_a_field_over_the_cap_is_never_cut()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(guardrailDetails: Skipped(new { by_hand = "Fix the folder", omitted_bytes = 9000 })));
        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Contains(OverviewPanelViewModel.RepairTooLongText, row.Detail, StringComparison.Ordinal);
        Assert.Contains("defenseclaw setup claudecode", row.Command, StringComparison.Ordinal);

        vm.Apply(Snapshot(guardrailDetails: Skipped(new { fix = new string('a', ConnectorStartHealth.MaxRepairLength + 1) })));
        Assert.Equal("defenseclaw setup claudecode", Assert.Single(vm.Attention, IsSkipRow).Command);
    }

    [Fact]
    public async Task Without_the_repairs_key_the_row_is_what_it_was()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(guardrailDetails: Skipped(null)));

        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Equal("defenseclaw setup claudecode", row.Command);
        Assert.DoesNotContain("Fix:", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("By hand", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Read gateway.log for the cause, then run the command below. The app never runs it.", row.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ hook launcher doctor rows

    private static string Cache(DateTimeOffset capturedAt, string label, string status, string detail) =>
        JsonSerializer.Serialize(new
        {
            passed = 1,
            failed = status == "fail" ? 1 : 0,
            warned = status == "warn" ? 1 : 0,
            skipped = 0,
            captured_at = capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            checks = new[] { new { status, label, detail }, new { status = "pass", label = "Config file", detail = "ok" } },
        });

    private const string MismatchDetail =
        @"the DefenseClaw hook launcher C:\Users\operator\.defenseclaw\bin\defenseclaw-hook.exe does not match the copy the installer recorded, so every hook call fails";

    private const string UnverifiedDetail =
        @"reachable at C:\Users\operator\.codex\hooks.json; the DefenseClaw hook launcher C:\Users\operator\.defenseclaw\bin\defenseclaw-hook.exe is not verified: the installer recorded no copy of it to compare with";

    private static bool IsLauncherRow(AttentionRow row) => row.Title.Contains("DefenseClaw hook ", StringComparison.Ordinal);

    [Fact]
    public async Task A_launcher_that_is_not_the_recorded_copy_is_a_high_row_with_the_installer_step_and_no_button()
    {
        var vm = await PanelAsync("1.0.9", doctorCache: Cache(DateTimeOffset.UtcNow.AddMinutes(-4), "Codex hooks", "fail", MismatchDetail));

        vm.Apply(Snapshot());

        var row = Assert.Single(vm.Attention, IsLauncherRow);
        Assert.Equal("Codex: the DefenseClaw hook launcher is not the installed copy", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("Run the DefenseClaw installer again the way you installed it; setup cannot recreate the launcher.", row.Detail, StringComparison.Ordinal);
        Assert.Contains("(doctor ran 4m ago)", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasActions);
        Assert.False(row.HasCommand);
    }

    [Fact]
    public async Task An_unverified_launcher_is_a_medium_row()
    {
        var vm = await PanelAsync("1.0.9", doctorCache: Cache(DateTimeOffset.UtcNow.AddMinutes(-1), "Codex hooks [codex]", "warn", UnverifiedDetail));

        vm.Apply(Snapshot());

        var row = Assert.Single(vm.Attention, IsLauncherRow);
        Assert.Equal("Codex: the DefenseClaw hook launcher is not verified", row.Title);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("recorded no copy", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hook_guard_that_differs_from_the_record_is_a_high_row()
    {
        var detail = @"the DefenseClaw hook guard C:\Users\operator\.defenseclaw\bin\defenseclaw-hook-guard.exe does not match the copy the installer recorded, so Codex prompts and tool calls may run without DefenseClaw";
        var vm = await PanelAsync("1.0.9", doctorCache: Cache(DateTimeOffset.UtcNow.AddMinutes(-1), "Codex hooks", "fail", detail));

        vm.Apply(Snapshot());

        var row = Assert.Single(vm.Attention, IsLauncherRow);
        Assert.Equal("Codex: the DefenseClaw hook guard is not the installed copy", row.Title);
        Assert.Equal("High", row.SeverityKey);
    }

    [Fact]
    public async Task A_doctor_result_past_the_stale_window_makes_no_launcher_row()
    {
        var old = DateTimeOffset.UtcNow - OverviewPanelViewModel.DoctorStaleAfter - TimeSpan.FromMinutes(5);
        var vm = await PanelAsync("1.0.9", doctorCache: Cache(old, "Codex hooks", "fail", MismatchDetail));

        vm.Apply(Snapshot());

        Assert.DoesNotContain(vm.Attention, IsLauncherRow);
    }

    [Theory]
    [InlineData("fail", "the DefenseClaw hook launcher C:\\x\\defenseclaw-hook.exe is missing, so every hook call fails")]
    [InlineData("pass", "reachable at C:\\Users\\operator\\.codex\\hooks.json")]
    [InlineData("warn", "reachable at C:\\Users\\operator\\.codex\\hooks.json; running hosts are unverified")]
    public async Task A_1_0_8_doctor_cache_makes_no_new_launcher_row(string status, string detail)
    {
        var vm = await PanelAsync("1.0.8", doctorCache: Cache(DateTimeOffset.UtcNow.AddMinutes(-1), "Codex hooks", status, detail));

        vm.Apply(Snapshot());

        Assert.DoesNotContain(vm.Attention, IsLauncherRow);
    }

    // ------------------------------------------------------------------ judge max_parallel hint

    private static object Fallbacks() => new { policy_mode = "observe", judge_fallbacks = 12, judge_fallbacks_by_connector = new { codex = 12 }, judge_last_fallback = "timeout (codex)" };

    private static bool IsFallbackRow(AttentionRow row) => row.Title.StartsWith(OverviewPanelViewModel.JudgeFallbacksTitlePrefix, StringComparison.Ordinal);

    private static string JudgeConfig(string llm, string judge = "") =>
        "config_version: 9\nguardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n" +
        (judge.Length > 0 ? "  judge:\n" + judge : string.Empty) + llm;

    [Theory]
    [InlineData("llm:\n  provider: ollama\n  model: ollama/llama3\n", "", false)]
    [InlineData("llm:\n  model: ollama/llama3\n", "", false)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n  base_url: http://127.0.0.1:11434/v1\n", "", false)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n  base_url: http://192.168.1.20:8000/v1\n", "", false)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n", "    llm:\n      base_url: http://localhost:8000\n", false)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n", "    api_base: http://localhost:8000\n", false)]
    [InlineData("llm:\n  provider: ollama\n  model: ollama/llama3\n", "    llm:\n      provider: openai\n      model: openai/gpt-4o\n      base_url: https://api.openai.com/v1\n", true)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n", "", true)]
    [InlineData("llm:\n  provider: openai\n  model: openai/gpt-4o\n  base_url: https://example.com/v1\n", "", true)]
    [InlineData("llm:\n  provider: custom\n  model: custom/m\n  instance_name: team\n", "", true)]
    [InlineData("llm:\n  provider: ollama\n  model: ollama/llama3\n", "    max_parallel: 0\n", true)]
    public async Task On_1_0_9_a_local_judge_with_the_key_left_out_gets_no_hint(string llm, string judge, bool hint)
    {
        var vm = await PanelAsync("1.0.9", JudgeConfig(llm, judge));

        vm.Apply(Snapshot(guardrailDetails: Fallbacks()));

        var row = Assert.Single(vm.Attention, IsFallbackRow);
        Assert.Equal(hint, row.Detail.Contains("guardrail.judge.max_parallel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_1_0_8_a_local_judge_still_gets_the_hint()
    {
        var vm = await PanelAsync("1.0.8", JudgeConfig("llm:\n  provider: ollama\n  model: ollama/llama3\n"));

        vm.Apply(Snapshot(guardrailDetails: Fallbacks()));

        Assert.Contains("guardrail.judge.max_parallel", Assert.Single(vm.Attention, IsFallbackRow).Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ host sensor dropped events

    private static object AiRuntimeBlock(params string[] reasons) => new { state = "running", details = new { degraded = true, degraded_reasons = reasons } };

    [Fact]
    public async Task On_1_0_9_a_drop_reason_says_the_count_stays_until_the_gateway_restarts()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(aiRuntime: AiRuntimeBlock(DropReason)));

        var text = vm.RuntimeMonitoringDegradedText;
        Assert.Contains("40 events were dropped", text, StringComparison.Ordinal);
        Assert.EndsWith(" The dropped-events count stays until the gateway restarts.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing needs to change", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_reasons_get_the_restart_sentence_but_never_the_calm_one()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(aiRuntime: AiRuntimeBlock(OwnSockets, DropReason)));

        Assert.Contains("stays until the gateway restarts", vm.RuntimeMonitoringDegradedText, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing needs to change", vm.RuntimeMonitoringDegradedText, StringComparison.Ordinal);

        vm.Apply(Snapshot(aiRuntime: AiRuntimeBlock(OwnSockets, OwnSockets)));
        Assert.Contains("nothing needs to change", vm.RuntimeMonitoringDegradedText, StringComparison.Ordinal);
        Assert.DoesNotContain("stays until", vm.RuntimeMonitoringDegradedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_reasons_on_1_0_9_and_a_drop_reason_on_1_0_8_get_no_restart_sentence()
    {
        var vm = await PanelAsync("1.0.9");
        vm.Apply(Snapshot(aiRuntime: AiRuntimeBlock("process table unreadable: access denied")));
        Assert.Equal("The gateway reports runtime monitoring degraded: process table unreadable: access denied.", vm.RuntimeMonitoringDegradedText);

        var older = await PanelAsync("1.0.8");
        older.Apply(Snapshot(aiRuntime: AiRuntimeBlock(DropReason)));
        Assert.Equal("The gateway reports runtime monitoring degraded: " + DropReason + ".", older.RuntimeMonitoringDegradedText);
    }

    // ------------------------------------------------------------------ the owner's machine

    [Fact]
    public async Task The_owner_machine_shape_draws_none_of_the_new_rows_or_texts()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(aiRuntime: AiRuntimeBlock(OwnSockets)));

        Assert.DoesNotContain(vm.Attention, IsAdmissionRow);
        Assert.DoesNotContain(vm.Attention, IsLauncherRow);
        Assert.DoesNotContain(vm.Attention, IsSkipRow);
        Assert.DoesNotContain(vm.Attention, IsFallbackRow);
        Assert.Equal("2 skill dirs, 1 plugin dirs", Assert.Single(vm.ServiceRows, static r => r.Name == "Watchdog").Detail);
        Assert.DoesNotContain("stays until", vm.RuntimeMonitoringDegradedText, StringComparison.Ordinal);
    }
}
