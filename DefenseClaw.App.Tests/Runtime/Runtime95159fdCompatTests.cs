using System.Text.Json;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The app's CLI-output readers against the synthetic fixtures derived from DefenseClaw source commit 95159fd
/// (<c>Fixtures/runtime-95159fd</c>, shared with the Core suite; see <c>docs/RUNTIME-COMPAT-95159fd.md</c>). These are pure parsers and a
/// fake-CLI catalog: no test here starts a process or creates a window, so none needs the UI thread.
/// </summary>
public sealed class Runtime95159fdCompatTests
{
    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", relative.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- versions ----

    [Fact]
    public void Version_json_of_both_binaries_gives_their_version()
    {
        Assert.Equal("1.0.0", UpdateChecker.TryParseVersionJson(Read("cli/version-json.json")));
        Assert.Equal("1.0.0", UpdateChecker.TryParseVersionJson(Read("cli/gateway-version-json.json")));
    }

    // ---- status --json ----

    [Fact]
    public void Status_json_of_a_fresh_install_reads_the_deployment_facts_and_no_connectors()
    {
        var status = DefenseClawStatusReader.Parse(Read("cli/status-json.json"));

        Assert.Equal("windows", status.Environment);
        Assert.Equal(@"C:\Users\operator\.defenseclaw", status.DataDirectory);
        Assert.Equal(@"C:\Users\operator\.defenseclaw\config.yaml", status.ConfigPath);
        Assert.Equal("global user config", status.Scope);
        Assert.False(status.SandboxAvailable);
        Assert.False(status.SidecarRunning);
        Assert.Equal(0, status.BlockedSkills);
        Assert.Equal(0, status.TotalScans);
        Assert.Equal(0, status.ActiveAlerts);
        Assert.False(status.ApplicationProtectionEnabled);
        Assert.Empty(status.Connectors);
    }

    [Fact]
    public void Status_json_with_connectors_keeps_fail_mode_provenance_and_drift()
    {
        var status = DefenseClawStatusReader.Parse(Read("cli/status-json.connectors.json"));

        Assert.True(status.SidecarRunning);
        Assert.Equal(2, status.Connectors.Count);

        var claude = status.Connector("claudecode")!;
        Assert.Equal("Claude Code", claude.Friendly);
        Assert.Equal("observe", claude.Mode);
        Assert.True(claude.Enabled);
        Assert.Equal("open", claude.FailMode!.Effective);
        Assert.Equal("windows-sidecar", claude.FailMode.Provenance);
        Assert.False(claude.FailMode.HasDrift);

        var codex = status.Connector("codex")!;
        Assert.Equal("closed", codex.FailMode!.Effective);
        Assert.Equal("open", codex.FailMode.Configured);
        Assert.False(codex.FailMode.Current);
        Assert.True(codex.FailMode.HasDrift);
        Assert.Single(codex.FailMode.Drift);
    }

    // ---- doctor ----

    [Fact]
    public void The_doctor_cache_of_the_newer_cli_gives_the_counts_the_failures_and_the_capture_time()
    {
        var snapshot = DoctorCacheReader.Parse(Read("cli/doctor-cache.json"));

        Assert.Equal(16, snapshot.Passed);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal(1, snapshot.Warned);
        Assert.Equal(20, snapshot.Skipped);
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 0, 0, TimeSpan.Zero), snapshot.CapturedAt);
        Assert.Equal(39, snapshot.Checks.Count);

        var problems = snapshot.Problems();
        Assert.Equal(new[] { "Component compatibility: gateway", "Sidecar API", "Gateway token env" }, problems.Select(p => p.Label));
        Assert.Equal(new[] { "fail", "fail", "warn" }, problems.Select(p => p.Status));
    }

    [Fact]
    public void A_doctor_cache_written_by_cli_json_output_without_captured_at_reads_as_stale()
    {
        var snapshot = DoctorCacheReader.Parse(Read("cli/doctor-json.json"));

        Assert.Equal(2, snapshot.Failed);
        Assert.Null(snapshot.CapturedAt);
        Assert.True(snapshot.IsStale(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_cached_sidecar_failure_is_stale_when_the_newer_gateways_health_says_api_is_running()
    {
        var snapshot = DoctorCacheReader.Parse(Read("cli/doctor-cache.json"));
        var sidecar = snapshot.Problems().Single(p => p.Label == "Sidecar API");
        var health = JsonSerializer.Deserialize<GatewayHealth>(Read("rest/health.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(DoctorReconciliation.LiveHealthContradicts(sidecar, health));
        Assert.False(DoctorReconciliation.LiveHealthContradicts(sidecar, null));
        Assert.Empty(DoctorReconciliation.MissingRequiredCredentials(snapshot));
    }

    // ---- guardrail status (text; the newer CLI adds a Block/alert column and a block layout for narrow terminals) ----

    [Fact]
    public void Guardrail_status_with_no_connector_is_a_disabled_guardrail_and_no_roster()
    {
        var status = GuardrailStatusParser.Parse(Read("cli/guardrail-status.txt"));

        Assert.False(status.Enabled);
        Assert.False(status.HasRoster);
    }

    [Fact]
    public void The_table_with_the_block_alert_column_still_gives_each_connector_by_its_headers()
    {
        // Laid out the way _render_connector_table of the newer CLI lays it out (columns as wide as their widest cell, 2-space gaps).
        const string table =
            "  * enabled:    yes\n" +
            "      Connector    Key         State    Mode     Fail    Rule pack  Block/alert        HILT  Scan        Judge\n" +
            "      -----------  ----------  -------  -------  ------  ---------  -----------------  ----  ----------  -----\n" +
            "      Claude Code  claudecode  enabled  observe  closed  default    CRITICAL/MEDIUM+   off   regex_only  off  \n" +
            "      Codex        codex       enabled  action   open    strict     MEDIUM+/LOW+       on    regex_only  on   \n" +
            "  * port:       4000\n";

        var status = GuardrailStatusParser.Parse(table);

        Assert.True(status.Enabled);
        Assert.Equal(new[] { "claudecode", "codex" }, status.Connectors.Select(c => c.Key));
        Assert.Equal("Claude Code", status.Connectors[0].Name);
        Assert.Equal("closed", status.Connectors[0].Fail);
        Assert.Equal("action", status.Connectors[1].Mode);
        Assert.Equal("MEDIUM+/LOW+", status.Connectors[1].Column("Block/alert"));
        Assert.Equal("4000", status.Port);
    }

    [Fact]
    public void The_block_layout_a_narrow_terminal_gets_gives_the_same_roster()
    {
        // _render_connector_blocks of the newer CLI: "- label" then 10-space-indented "name:" lines.
        const string blocks =
            "  * enabled:    yes\n" +
            "      - Claude Code\n" +
            "          key:         claudecode\n" +
            "          state:       enabled\n" +
            "          mode:        observe\n" +
            "          fail:        closed\n" +
            "          rule-pack:   default\n" +
            "          block/alert: CRITICAL/MEDIUM+\n" +
            "          hilt:        off\n" +
            "          scan:        regex_only\n" +
            "          judge:       off\n" +
            "      - Codex\n" +
            "          key:         codex\n" +
            "          state:       disabled\n" +
            "          mode:        action\n" +
            "          fail:        open\n" +
            "  ! runtime fail-mode drift: settings.json says open, the gateway says closed\n" +
            "  * port:       4000\n";

        var status = GuardrailStatusParser.Parse(blocks);

        Assert.True(status.Enabled);
        Assert.Equal(new[] { "claudecode", "codex" }, status.Connectors.Select(c => c.Key));
        Assert.Equal("Claude Code", status.Connectors[0].Name);
        Assert.Equal("enabled", status.Connectors[0].State);
        Assert.Equal("closed", status.Connectors[0].Fail);
        Assert.Equal("off", status.Connectors[0].Column("HILT"));
        Assert.Equal("disabled", status.Connectors[1].State);
        Assert.Single(status.Warnings);
        Assert.Equal("4000", status.Port);
    }

    // ---- AI discovery ----

    [Fact]
    public void Agent_discovery_status_of_the_newer_cli_reads_as_a_reachable_service_that_is_off()
    {
        var live = AiDiscoveryPanelViewModel.ParseLiveStatus(Read("cli/agent-discovery-status.json"), out var problem);

        Assert.Null(problem);
        Assert.NotNull(live);
        Assert.True(live!.Reachable);
        Assert.False(live.Enabled);
        Assert.False(live.Drift);
        Assert.Equal("disabled", live.Result);
        Assert.Null(live.ScannedAt);
    }

    [Fact]
    public void The_runtime_route_of_the_newer_gateway_answers_the_off_state_the_panel_already_knows()
    {
        StaThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var vm = new AiDiscoveryPanelViewModel(services);
            using var document = JsonDocument.Parse(Read("rest/ai-usage-runtime.json"));

            vm.ApplyRuntime(DefenseClaw.Core.Gateway.GatewayResult<JsonDocument>.Ok(document, 200));

            Assert.Equal(("Off", "Neutral", "Runtime coverage is turned off"), (vm.RuntimeBadgeText, vm.RuntimeBadgeKey, vm.RuntimeTitle));
            Assert.Empty(vm.RuntimePlanes);
            Assert.Empty(vm.RuntimeFindings);
        });
    }

    // ---- the Setup tile grid ----

    private static readonly IReadOnlyDictionary<string, string> HelpFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [string.Empty] = "help/setup.txt",
        ["claude-code"] = "help/setup-claude-code.txt",
        ["cursor"] = "help/setup-cursor.txt",
        ["codex"] = "help/setup-codex.txt",
        ["guardrail"] = "help/setup-guardrail.txt",
        ["observability"] = "help/setup-observability.txt",
        ["redaction"] = "help/setup-redaction.txt",
        ["acp"] = "help/setup-acp.txt",
        ["gateway"] = "help/setup-gateway.txt",
        ["trusted-paths"] = "help/setup-trusted-paths.txt",
        ["rotate-token"] = "help/setup-rotate-token.txt",
        ["routing"] = "help/setup-routing.txt",
        ["local-observability"] = "help/setup-local-observability.txt",
    };

    private static WizardCatalog CatalogOver(string topFile)
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        Task<HelpProbeResult> Help(string executable, IReadOnlyList<string> path, CancellationToken token)
        {
            var key = string.Join(' ', path);
            var file = key.Length == 0 ? topFile : HelpFiles.GetValueOrDefault(key);
            return Task.FromResult(file is null
                ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.")
                : new HelpProbeResult(Read(file), null));
        }

        return new WizardCatalog(new SetupHelpProbe(paths, diskCache: null, Help));
    }

    [Fact]
    public async Task The_roster_of_the_newer_setup_help_becomes_one_card_per_command()
    {
        var catalog = CatalogOver("help/setup.txt");

        var cards = await catalog.LoadAsync();

        Assert.Empty(catalog.LoadError ?? string.Empty);
        Assert.Equal(35, cards.Count);
        foreach (var target in new[] { "acp", "claude-code", "codex", "cursor", "guardrail", "observability", "redaction", "routing", "rotate-token", "trusted-paths", "local-observability", "notifications-set" })
        {
            Assert.NotNull(catalog.Find(target));
        }
    }

    [Fact]
    public async Task The_windows_rendering_marks_the_connectors_the_platform_cannot_run_as_unsupported()
    {
        var catalog = CatalogOver("help/setup-windows.txt");

        _ = await catalog.LoadAsync();

        Assert.Equal(PlatformStatus.Unsupported, catalog.Find("openclaw")!.PlatformStatus);
        Assert.Equal(PlatformStatus.Unsupported, catalog.Find("openhands")!.PlatformStatus);
        Assert.Equal(PlatformStatus.Unsupported, catalog.Find("zeptoclaw")!.PlatformStatus);
        Assert.NotEqual(PlatformStatus.Unsupported, catalog.Find("claude-code")!.PlatformStatus);
    }

    [Fact]
    public async Task A_connector_page_with_four_spellings_for_yes_and_lower_case_choices_parses_into_fields()
    {
        var catalog = CatalogOver("help/setup.txt");
        _ = await catalog.LoadAsync();

        var cursor = await catalog.EnsureDetailAsync("cursor");

        Assert.True(cursor.IsDetailLoaded, cursor.DetailError);
        var fields = cursor.AllFields.ToList();
        Assert.Contains(fields, f => f.Flag == "--mode");
        Assert.Contains(fields, f => f.Flag == "--fail-mode");
        Assert.Contains(fields, f => f.Flag == "--hilt-min-severity");
        Assert.Contains(fields, f => f.Flag == "--workspace");

        var parsed = SetupHelpParser.Parse(Read("help/setup-cursor.txt"));
        var yes = parsed.Options.Single(o => o.Names.Contains("--non-interactive", StringComparer.Ordinal));
        Assert.Contains("-y", yes.Names);
        Assert.Contains("--accept-defaults", yes.Names);
        Assert.False(yes.TakesValue);
        Assert.Equal(new[] { "high", "medium", "low", "critical" }, parsed.Option("--hilt-min-severity")!.Choices);
        Assert.Contains("--workspace-dir", parsed.Option("--workspace")!.Names);
    }

    [Theory]
    [InlineData("help/setup-claude-code.txt")]
    [InlineData("help/setup-codex.txt")]
    [InlineData("help/setup-guardrail.txt")]
    [InlineData("help/setup-gateway.txt")]
    [InlineData("help/setup-routing.txt")]
    [InlineData("help/setup-trusted-paths.txt")]
    [InlineData("help/setup-local-observability.txt")]
    public void Every_captured_page_parses_to_a_usage_line_and_a_description(string file)
    {
        var parsed = SetupHelpParser.Parse(Read(file));

        Assert.StartsWith("Usage: defenseclaw setup", parsed.Usage, StringComparison.Ordinal);
        Assert.NotEmpty(parsed.Summary);
    }

    [Theory]
    [InlineData("help/setup-observability.txt")]
    [InlineData("help/setup-redaction.txt")]
    [InlineData("help/setup-rotate-token.txt")]
    [InlineData("help/setup-acp.txt")]
    public void Groups_and_pages_with_subcommands_list_them(string file)
    {
        var parsed = SetupHelpParser.Parse(Read(file));

        Assert.StartsWith("Usage: defenseclaw setup", parsed.Usage, StringComparison.Ordinal);
        Assert.NotEmpty(parsed.Summary);
    }
}
