using System.Text.Json;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The Runtime monitoring card's pure model (CUST-348): <c>ai_discovery.runtime</c> read from a config schema 9 file, the gateway's <c>ai_runtime</c> health block,
/// the plane-by-plane state they make together, and the exact argv each choice becomes. Fixtures are synthetic, written from the 1.0.0 source
/// (<c>Fixtures/runtime-1.0.0</c>); nothing here starts a process.
/// </summary>
public sealed class RuntimeMonitoringTests
{
    private static string Fixture(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", "runtime-1.0.0", .. path]));

    private static GatewayHealth Health(string name) =>
        JsonSerializer.Deserialize<GatewayHealth>(Fixture("rest", name), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static RuntimeMonitoringConfig Config(string yaml) => RuntimeMonitoringConfig.From(ConfigYamlReader.Parse(yaml));

    private static readonly RuntimeMonitoringConfig V9 = Config(Fixture("config", "config.v9.runtime.synthetic.yaml"));

    private static RuntimeMonitoringState Build(RuntimeMonitoringConfig config, string? health, GatewayElevation elevation = GatewayElevation.Unknown, ProbeReading<int?>? cmdline = null) =>
        RuntimeMonitoringState.Build(
            config,
            health is null ? RuntimeHealthView.NotReported : RuntimeHealthView.From(Health(health)),
            gatewayAnswered: health is not null,
            elevation,
            cmdline);

    // ---- config schema 9 ----

    [Fact]
    public void A_schema_9_file_with_the_runtime_block_reads_through_the_typed_model_and_the_text_reader()
    {
        var yaml = Fixture("config", "config.v9.runtime.synthetic.yaml");

        var typed = ConfigStore.Parse(yaml).Config;
        Assert.Equal(9, typed.ConfigVersion);
        Assert.True(typed.AiDiscovery.Enabled);

        Assert.Equal(9, V9.ConfigVersion);
        Assert.True(V9.Enabled);
        Assert.Equal(new[] { "a", "b", "c" }, V9.Planes);
        Assert.True(V9.EnableHostPlane);
        Assert.True(V9.DnsCapture);
        Assert.Equal("auto", V9.Acquisition);
        Assert.Equal(new[] { "a", "b", "c" }, V9.EffectivePlanes);
    }

    [Fact]
    public void A_schema_8_file_without_the_block_reads_as_off_and_selects_a_and_b_if_it_were_turned_on()
    {
        var config = Config(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "config", "config.fresh.yaml")));

        Assert.False(config.Enabled);
        Assert.Equal(8, config.ConfigVersion);
        Assert.Equal(new[] { "a", "b" }, config.EffectivePlanes);
        Assert.False(config.PlaneSelected("c"));
    }

    [Theory]
    [InlineData("", false, "a,b")]
    [InlineData("", true, "a,b,c")]
    [InlineData("[a, b]", true, "a,b")]
    [InlineData("[a, b, c]", false, "a,b")]
    [InlineData("[B]", false, "b")]
    [InlineData("[a, b, c]", true, "a,b,c")]
    public void The_effective_planes_follow_the_gateways_rule_for_planes_and_the_host_opt_in(string planes, bool hostPlane, string expected)
    {
        var yaml = "ai_discovery:\n  runtime:\n    enabled: true\n" + (planes.Length > 0 ? $"    planes: {planes}\n" : string.Empty) +
            $"    enable_host_plane: {(hostPlane ? "true" : "false")}\n";

        Assert.Equal(expected.Split(','), Config(yaml).EffectivePlanes);
    }

    [Fact]
    public void Plane_c_listed_without_the_opt_in_is_named_as_asked_in_two_places_and_granted_in_neither()
    {
        var config = Config("ai_discovery:\n  runtime:\n    enabled: true\n    planes: [a, b, c]\n");

        Assert.True(config.HostPlaneListedWithoutOptIn);
        var c = Build(config, null).Planes.Single(p => p.Id == "c");
        Assert.False(c.SelectedInConfig);
        Assert.Contains("enable_host_plane is false", c.ConfigText, StringComparison.Ordinal);
    }

    // ---- /health ----

    [Fact]
    public void The_health_block_is_read_per_plane()
    {
        var view = RuntimeHealthView.From(Health("health.ai-runtime.ab-running.synthetic.json"));

        Assert.True(view.Reported);
        Assert.Equal("running", view.State);
        Assert.False(view.Degraded);
        Assert.True(view.Planes["a"].Running);
        Assert.Contains("Toolhelp32", view.Planes["a"].Mechanism, StringComparison.Ordinal);
        Assert.True(view.Planes["b"].Running);
        Assert.False(view.Planes["c"].Running);
        Assert.Contains("enable_host_plane", view.Planes["c"].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_without_the_block_is_not_reported_not_disabled()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>("{\"api\":{\"state\":\"running\"}}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.False(RuntimeHealthView.From(health).Reported);
        Assert.False(RuntimeHealthView.From(null).Reported);
    }

    // ---- the state matrix ----

    [Fact]
    public void Config_on_with_a_and_b_running_and_c_not_selected_reads_a_and_b_up_and_c_off()
    {
        var config = Config("config_version: 9\nai_discovery:\n  runtime:\n    enabled: true\n");

        var state = Build(config, "health.ai-runtime.ab-running.synthetic.json");

        Assert.Equal(new[] { RuntimePlaneRunState.Running, RuntimePlaneRunState.Running, RuntimePlaneRunState.NotRunning }, state.Planes.Select(p => p.Run));
        Assert.Equal(new[] { true, true, false }, state.Planes.Select(p => p.SelectedInConfig));
        Assert.Equal("on · A running · B running · C off", state.Summary);
        Assert.Equal(string.Empty, state.ApplyNote);
        Assert.Contains("Plane C is an explicit opt-in", state.Planes[2].ConfigText, StringComparison.Ordinal);
        Assert.Contains("Not running: ai_discovery.runtime.enable_host_plane is false", state.Planes[2].RunText, StringComparison.Ordinal);
        Assert.Equal("Running via Toolhelp32 snapshot, GetProcessTimes, GetProcessMemoryInfo.", state.Planes[0].RunText);
    }

    [Fact]
    public void Config_on_with_plane_c_blind_shows_c_selected_and_not_running_with_the_gateways_reason()
    {
        var state = Build(V9, "health.ai-runtime.c-blind.synthetic.json");

        var c = state.Planes[2];
        Assert.True(c.SelectedInConfig);
        Assert.Equal(RuntimePlaneRunState.NotRunning, c.Run);
        Assert.Contains("an ETW session needs an elevated token", c.RunText, StringComparison.Ordinal);
        Assert.Equal("on · A running · B running · C not running", state.Summary);
    }

    [Fact]
    public void The_gateway_not_answering_leaves_every_plane_unknown_and_never_running()
    {
        var state = Build(V9, health: null);

        Assert.All(state.Planes, p => Assert.Equal(RuntimePlaneRunState.Unknown, p.Run));
        Assert.All(state.Planes, p => Assert.StartsWith("Not known", p.RunText, StringComparison.Ordinal));
        Assert.Contains("did not answer", state.ApplyNote, StringComparison.Ordinal);
        Assert.Equal("on · A selected · B selected · C selected", state.Summary);
    }

    [Fact]
    public void An_old_gateway_without_the_block_says_it_does_not_report_monitoring()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>("{\"api\":{\"state\":\"running\"}}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var state = RuntimeMonitoringState.Build(RuntimeMonitoringConfig.Off, RuntimeHealthView.From(health), true, GatewayElevation.Unknown, null);

        Assert.All(state.Planes, p => Assert.Equal("Not reported by this gateway.", p.RunText));
        Assert.Equal("This gateway does not report runtime monitoring.", state.ApplyNote);
        Assert.Contains("off in config.yaml", state.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_gateway_block_with_config_on_asks_for_a_restart()
    {
        var disabled = JsonSerializer.Deserialize<GatewayHealth>("{\"ai_runtime\":{\"state\":\"disabled\"}}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var state = RuntimeMonitoringState.Build(V9, RuntimeHealthView.From(disabled), true, GatewayElevation.Unknown, null);

        Assert.Equal("config.yaml turns monitoring on but the gateway reports it disabled: restart the gateway to apply it.", state.ApplyNote);
        Assert.All(state.Planes, p => Assert.Equal(RuntimePlaneRunState.NotRunning, p.Run));
        Assert.Equal("Not running: the gateway reports runtime monitoring disabled.", state.Planes[0].RunText);
    }

    [Fact]
    public void Config_off_with_the_gateway_still_running_asks_for_a_restart_to_stop_it()
    {
        var state = Build(RuntimeMonitoringConfig.Off, "health.ai-runtime.ab-running.synthetic.json");

        Assert.Equal("config.yaml turns monitoring off but the gateway still reports it running: restart the gateway to stop it.", state.ApplyNote);
        Assert.All(state.Planes, p => Assert.Contains("Off: runtime monitoring is off.", p.ConfigText, StringComparison.Ordinal));
    }

    // ---- requirements ----

    private static RuntimeRequirement Req(RuntimeMonitoringState state, string plane, string startsWith) =>
        state.Planes.Single(p => p.Id == plane).Requirements.Single(r => r.Text.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void Planes_a_and_b_work_without_elevation_and_the_gaps_are_spelled_out()
    {
        var state = Build(V9, "health.ai-runtime.c-blind.synthetic.json");

        Assert.Equal(RuntimeRequirementState.Met, Req(state, "a", "Works without administrator rights").State);
        Assert.Contains("other users' processes and elevated processes", Req(state, "a", "Gap without an elevated gateway").Text, StringComparison.Ordinal);
        Assert.Equal(RuntimeRequirementState.Note, Req(state, "a", "Gap without an elevated gateway").State);

        Assert.Equal(RuntimeRequirementState.Met, Req(state, "b", "Works without administrator rights").State);
        Assert.Contains("image path and owner", Req(state, "b", "Gap without an elevated gateway").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plane_c_needs_an_elevated_gateway_which_is_unavailable_until_the_administrator_mode_exists()
    {
        var unknown = Build(V9, "health.ai-runtime.c-blind.synthetic.json", GatewayElevation.Unknown);
        var notElevated = Build(V9, "health.ai-runtime.c-blind.synthetic.json", GatewayElevation.NotElevated);
        var elevated = Build(V9, "health.ai-runtime.c-blind.synthetic.json", GatewayElevation.Elevated);

        var pending = Req(unknown, "c", "Needs Run gateway as administrator");
        Assert.Equal(RuntimeRequirementState.Unavailable, pending.State);
        Assert.Equal(RuntimeRequirementState.NotMet, Req(notElevated, "c", "Needs Run gateway as administrator").State);
        Assert.Equal(RuntimeRequirementState.Met, Req(elevated, "c", "The gateway runs as administrator").State);
    }

    [Fact]
    public void The_audit_policy_is_never_guessed_it_cannot_be_checked_without_admin()
    {
        foreach (var elevation in Enum.GetValues<GatewayElevation>())
        {
            var state = Build(V9, "health.ai-runtime.c-blind.synthetic.json", elevation);
            var audit = Req(state, "c", "Advanced Audit Policy");

            Assert.Equal(RuntimeRequirementState.CannotCheck, audit.State);
            Assert.Contains("can't check without admin", audit.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_command_line_setting_is_read_without_elevation_and_a_failed_read_is_not_a_fault()
    {
        const string Start = "Command lines in process creation events";

        Assert.Equal(RuntimeRequirementState.Met, Req(Build(V9, null, cmdline: ProbeReading<int?>.Ok(1)), "c", Start).State);
        Assert.Equal(RuntimeRequirementState.NotMet, Req(Build(V9, null, cmdline: ProbeReading<int?>.Ok(0)), "c", Start).State);
        Assert.Equal(RuntimeRequirementState.NotMet, Req(Build(V9, null, cmdline: ProbeReading<int?>.Ok(null)), "c", Start).State);
        Assert.Equal(RuntimeRequirementState.CannotCheck, Req(Build(V9, null, cmdline: ProbeReading<int?>.Failed("access denied")), "c", Start).State);
        Assert.Equal(RuntimeRequirementState.CannotCheck, Req(Build(V9, null), "c", Start).State);
    }

    [Fact]
    public void Plane_c_opt_in_is_met_only_when_both_gates_are_open()
    {
        var closed = Build(Config("ai_discovery:\n  runtime:\n    enabled: true\n"), null);
        var open = Build(V9, null);

        Assert.Equal(RuntimeRequirementState.NotMet, Req(closed, "c", "Not opted in").State);
        Assert.Equal(RuntimeRequirementState.Met, Req(open, "c", "Opted in").State);
    }

    [Fact]
    public void Dns_capture_requirement_follows_config_and_the_gateways_mechanism_text()
    {
        const string Start = "DNS capture is";

        Assert.Equal(RuntimeRequirementState.Note, Req(Build(Config("ai_discovery:\n  runtime:\n    enabled: true\n"), "health.ai-runtime.ab-running.synthetic.json"), "b", Start).State);
        Assert.Equal(RuntimeRequirementState.NotMet, Req(Build(V9, "health.ai-runtime.c-blind.synthetic.json"), "b", Start).State);
        Assert.Equal(RuntimeRequirementState.CannotCheck, Req(Build(V9, null), "b", Start).State);

        var capturing = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"ai_runtime\":{\"state\":\"running\",\"details\":{\"planes\":{\"b\":{\"available\":true,\"running\":true,\"mechanism\":\"GetExtendedTcpTable and DNS capture (Microsoft-Windows-DNS-Client/Operational (event 3008))\"}}}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var met = RuntimeMonitoringState.Build(V9, RuntimeHealthView.From(capturing), true, GatewayElevation.Unknown, null);
        Assert.Equal(RuntimeRequirementState.Met, Req(met, "b", Start).State);
    }

    [Fact]
    public void Every_plane_says_what_it_records_and_its_privacy_note()
    {
        var state = Build(RuntimeMonitoringConfig.Off, null);

        Assert.Contains("command line", state.Planes[0].Records, StringComparison.Ordinal);
        Assert.Contains("Environment variable values are never read", state.Planes[0].Privacy, StringComparison.Ordinal);
        Assert.Contains("DNS Client operational log", state.Planes[1].Records, StringComparison.Ordinal);
        Assert.Contains("domains", state.Planes[1].Privacy, StringComparison.Ordinal);
        Assert.Contains("Security event log", state.Planes[2].Records, StringComparison.Ordinal);
        Assert.Contains("AI agent being in the process lineage", state.Planes[2].Records, StringComparison.Ordinal);
    }

    // ---- the changes and their argv ----

    private static string[] Argv(RuntimeMonitoringConfig config, RuntimeMonitoringPlan.Wanted wanted, bool restart = true) =>
        RuntimeMonitoringPlan.Argv(config, wanted, restart)?.ToArray() ?? [];

    private static readonly RuntimeMonitoringConfig Off = RuntimeMonitoringConfig.Off;

    private static readonly RuntimeMonitoringConfig OnAb = Config("ai_discovery:\n  runtime:\n    enabled: true\n    planes: [a, b]\n");

    [Fact]
    public void Turning_monitoring_on_enables_a_and_b_and_spells_out_that_c_stays_off()
    {
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--no-enable-host-plane"],
            Argv(Off, new(Enabled: true, HostPlane: false, DnsCapture: false)));
    }

    [Fact]
    public void Turning_monitoring_on_with_plane_c_and_dns_adds_both_flags()
    {
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--enable-host-plane", "--dns-capture"],
            Argv(Off, new(true, true, true)));
    }

    [Fact]
    public void Turning_plane_c_on_and_off_while_monitoring_is_on_sends_only_the_host_plane_flag()
    {
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--enable-host-plane"],
            Argv(OnAb, new(true, true, false)));
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--no-enable-host-plane"],
            Argv(V9, new(true, false, true)));
    }

    [Fact]
    public void Dns_capture_alone_sends_only_the_dns_flag_beside_the_unchanged_host_plane_choice()
    {
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--no-enable-host-plane", "--dns-capture"],
            Argv(OnAb, new(true, false, true)));
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--enable-host-plane", "--no-dns-capture"],
            Argv(V9, new(true, true, false)));
    }

    [Fact]
    public void Turning_monitoring_off_is_disable_whatever_the_other_switches_say()
    {
        Assert.Equal(["agent", "discovery", "runtime", "disable", "--yes"], Argv(V9, new(false, true, true)));
        Assert.Equal(["agent", "discovery", "runtime", "disable", "--yes"], Argv(V9, new(false, false, false)));
    }

    [Fact]
    public void Unticking_the_restart_adds_no_restart_to_both_verbs()
    {
        Assert.Equal(
            ["agent", "discovery", "runtime", "disable", "--yes", "--no-restart"],
            Argv(V9, new(false, true, true), restart: false));
        Assert.Equal(
            ["agent", "discovery", "runtime", "enable", "--yes", "--no-enable-host-plane", "--no-restart"],
            Argv(Off, new(true, false, false), restart: false));
    }

    [Fact]
    public void No_change_makes_no_command()
    {
        Assert.Null(RuntimeMonitoringPlan.Argv(V9, RuntimeMonitoringPlan.Current(V9), true));
        Assert.Null(RuntimeMonitoringPlan.Argv(Off, RuntimeMonitoringPlan.Current(Off), true));
        Assert.Empty(RuntimeMonitoringPlan.Diff(V9, RuntimeMonitoringPlan.Current(V9)));

        // Plane C and DNS capture mean nothing while monitoring stays off: they are not changes, and they never turn it on.
        Assert.Null(RuntimeMonitoringPlan.Argv(Off, new(false, true, true), true));
    }

    [Fact]
    public void The_diff_names_each_change_in_words()
    {
        var lines = RuntimeMonitoringPlan.Diff(Off, new(true, true, true)).Select(c => c.Description).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Runtime monitoring: off → on", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Plane C (agent actions): off → on", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("DNS capture: off → on", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_card_builds_can_be_a_permissions_grant_or_revert()
    {
        foreach (var wanted in new RuntimeMonitoringPlan.Wanted[] { new(true, true, true), new(true, false, false), new(false, false, false) })
        {
            var argv = Argv(OnAb, wanted).Concat(Argv(Off, wanted)).Concat(Argv(V9, wanted));
            Assert.DoesNotContain(argv, a => a is "permissions" or "--grant" or "--revert");
        }
    }
}
