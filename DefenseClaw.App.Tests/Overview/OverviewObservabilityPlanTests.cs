using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Observability card with the compiled plan (CUST-272): destinations merged with <c>/health</c> by name, the states a plan destination takes when the
/// gateway is silent, the Local SQLite line, the redaction label, the one-line note when the plan cannot be read, and when the command is run. The plan is
/// the fixtures' (a fresh install's capture, and a synthetic one over invented destinations written from the 0.8.10 emitter), the gateway's answers are
/// built here, and the command is a fake: nothing starts a process. Every credential is a made-up value that starts with <c>synth</c>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewObservabilityPlanTests : IDisposable
{
    private const string PinnedPlan = "runtime-95159fd/cli/observability-plan.json";
    private const string DestinationsPlan = "runtime-0.8.10/cli/observability-plan.destinations.synthetic.json";

    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthtoken", "synthkey", "synthpath-secret", "synthfrag" };

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _created = new();
    private AppServices? _services;

    public void Dispose()
    {
        foreach (var services in _created)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    // ---- the install ----

    private string AuditPath => Path.Combine(_temp.Path, "evidence", "audit-custom.db").Replace('\\', '/');

    private string JudgePath => Path.Combine(_temp.Path, "evidence", "judge-custom.db").Replace('\\', '/');

    /// <summary>A config.yaml with the local store set, three destinations that carry a credential somewhere in their address, and a disabled one.</summary>
    private string Config(string retention = "30", string guardrail = "") =>
        $"""
        gateway:
          api_port: {OverviewScene.Port}
        guardrail:
          connector: claudecode
          enabled: true
        {guardrail}observability:
          local:
            path: {AuditPath}
            judge_bodies_path: {JudgePath}
        {(retention.Length == 0 ? string.Empty : "    retention_days: " + retention + "\n")}  destinations:
            - name: example-otlp
              kind: otlp
              endpoint: https://collector.example.test:4318/v1/logs
            - name: example-splunk
              kind: splunk_hec
              endpoint: https://synthuser:synthpass@splunk.example.test:8088/services/collector/event?token=synthtoken#synthfrag
            - name: example-archive
              kind: http_jsonl
              enabled: false
              endpoint: https://archive.example.test/synthpath-secret/hook
            - name: example-metrics
              kind: prometheus
              listen: 127.0.0.1:9464
        """;

    /// <summary>A panel over a fresh install whose gateway port is one nothing listens on (the scene's), so no test can reach a real gateway.</summary>
    private OverviewPanelViewModel Panel(string? config = null)
    {
        _services = TestServices.Create(
            _temp,
            config is null ? Config() : config.StartsWith("gateway:", StringComparison.Ordinal) ? config : $"gateway:\n  api_port: {OverviewScene.Port}\n" + config);
        _created.Add(_services);
        return new OverviewPanelViewModel(_services);
    }

    // ---- what the gateway says ----

    private const string LocalSqlite =
        "{\"name\":\"local-sqlite\",\"kind\":\"sqlite\",\"enabled\":true,\"state\":\"healthy\",\"reason\":\"activated\",\"signals\":[\"logs\"],\"counters\":{\"accepted\":0,\"delivered\":0,\"dropped\":0}}";

    private const string ExampleOtlp =
        "{\"name\":\"example-otlp\",\"kind\":\"otlp\",\"enabled\":true,\"state\":\"healthy\",\"reason\":\"activated\",\"signals\":[\"logs\",\"traces\",\"metrics\"]," +
        "\"queue\":{\"items\":3,\"bytes\":1024,\"max_items\":2048,\"max_bytes\":67108864,\"dropped\":0},\"last_success_at\":\"2030-01-15T09:17:58.497333253Z\",\"counters\":{\"accepted\":120,\"delivered\":117}}";

    private const string ExampleSplunk =
        "{\"name\":\"example-splunk\",\"kind\":\"splunk_hec\",\"enabled\":true,\"state\":\"degraded\",\"reason\":\"retrying\",\"signals\":[\"logs\"]," +
        "\"last_failure_class\":\"timeout\",\"last_failure_at\":\"2030-01-15T09:30:00Z\",\"last_success_at\":\"2030-01-15T09:00:00Z\"," +
        "\"last_error\":\"Post \\\"https://synthuser:synthpass@splunk.example.test:8088/services/collector/event?token=synthtoken\\\": context deadline exceeded\"}";

    private const string UnlistedExtra =
        "{\"name\":\"unlisted-extra\",\"kind\":\"otlp\",\"enabled\":true,\"state\":\"healthy\",\"signals\":[\"logs\"]}";

    private static string HealthJson(string destinations, string details = "", string beside = "") =>
        "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}," +
        "\"telemetry\":{\"state\":\"running\",\"details\":{\"destinations\":[" + destinations + "]" + (details.Length > 0 ? "," + details : string.Empty) + "}}" +
        (beside.Length > 0 ? "," + beside : string.Empty) + "}";

    private static GatewaySnapshot Running(string healthJson) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = JsonSerializer.Deserialize<GatewayHealth>(healthJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static readonly string Sinks =
        "\"sinks\":{\"state\":\"running\",\"details\":{\"sinks\":[{\"name\":\"audit-file\",\"kind\":\"jsonl\",\"enabled\":true,\"state\":\"healthy\"}]}}";

    private static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static ObservabilityPlanRead Ok(string fixture) =>
        new(ObservabilityPlanStatus.Ok, ObservabilityPlanParser.Parse(Fixture(fixture)).Plan, string.Empty, DateTimeOffset.UtcNow);

    private static ObservabilityPlanRead Failed(ObservabilityPlanStatus status, string message) => new(status, null, message, DateTimeOffset.UtcNow);

    /// <summary>The panel with the full set of destinations on both sides: the synthetic plan, the gateway reporting four of its five, and a sink.</summary>
    private OverviewPanelViewModel Merged()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(string.Join(",", LocalSqlite, ExampleOtlp, ExampleSplunk, UnlistedExtra), "\"retention_state\":\"healthy\",\"retention_days\":90", Sinks)));
        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));
        return vm;
    }

    private static ObservabilityRow Row(OverviewPanelViewModel vm, string name) => vm.ObservabilityRows.Single(r => r.Name == name);

    // ---- merge by name ----

    [Fact]
    public void The_plans_destinations_come_first_each_merged_with_the_gateways_entry_then_the_entries_the_plan_does_not_list_then_the_sinks()
    {
        var vm = Merged();

        Assert.Equal(
            new[] { "local-sqlite", "example-otlp", "example-splunk", "example-archive", "example-metrics", "unlisted-extra", "audit-file" },
            vm.ObservabilityRows.Select(r => r.Name).ToArray());
        Assert.True(vm.HasObservabilityPlan);
        Assert.True(vm.HasObservabilityRows);
    }

    [Fact]
    public void A_destination_the_gateway_reports_gets_its_state_queue_and_last_result_from_it_and_its_policy_buckets_redaction_and_limits_from_the_plan()
    {
        var vm = Merged();

        var otlp = Row(vm, "example-otlp");
        Assert.Equal("otel", otlp.Target);
        Assert.Equal("otlp", otlp.Kind);
        Assert.Equal("enabled", otlp.Policy);
        Assert.Equal("healthy", otlp.State);
        Assert.Equal("Ok", otlp.StateKey);
        Assert.Equal("logs, traces, metrics", otlp.Signals);
        Assert.Equal("13/14", otlp.Buckets);
        Assert.Equal("redacted: sensitive", otlp.Redaction);
        Assert.Equal("3/2048 items, 1.0 KiB/64.0 MiB, 0 dropped", otlp.Queue);
        Assert.Equal("queue=2048 items/64.0 MiB; batch=256 items/8.0 MiB; delay=1000ms", otlp.Limits);
        Assert.Equal("ok " + FormatTime("2030-01-15T09:17:58Z"), otlp.LastResult);
        Assert.Equal("Neutral", otlp.LastResultKey);
        Assert.Equal("collector.example.test:4318", otlp.Endpoint);
        Assert.Contains("activated", otlp.Detail, StringComparison.Ordinal);
        Assert.True(otlp.HasPlan);
    }

    [Fact]
    public void A_destination_whose_last_word_was_an_error_says_so_and_is_drawn_as_a_warning()
    {
        var vm = Merged();

        var splunk = Row(vm, "example-splunk");

        Assert.Equal("degraded", splunk.State);
        Assert.Equal("Warn", splunk.StateKey);
        Assert.Equal("Warn", splunk.LastResultKey);
        Assert.Equal("ok " + FormatTime("2030-01-15T09:00:00Z") + "; error " + FormatTime("2030-01-15T09:30:00Z") + " (timeout)", splunk.LastResult);
        Assert.Equal("5/14", splunk.Buckets);
        Assert.Equal("mixed: none, strict (+ conditional routes)", splunk.Redaction);
        Assert.Equal("splunk.example.test:8088", splunk.Endpoint);
        Assert.Equal("splunk_hec", splunk.Kind);
    }

    [Fact]
    public void The_local_store_reaches_every_bucket_and_has_no_address_and_no_limits()
    {
        var vm = Merged();

        var local = Row(vm, "local-sqlite");

        Assert.Equal("enabled", local.Policy);
        Assert.Equal("14/14", local.Buckets);
        Assert.Equal("unredacted (none)", local.Redaction);
        Assert.Equal("not-applicable", local.Limits);
        Assert.Equal("0 dropped", local.Queue);
        Assert.Equal("unavailable", local.LastResult);
        Assert.Equal(string.Empty, local.Endpoint);
        Assert.Equal("sqlite", local.Kind);
        Assert.Equal("logs", local.Signals);
        Assert.Equal(new[] { "Buckets", "Redaction", "Queue", "Limits", "Last result" }, local.Facts.Select(f => f.Label).ToArray());
    }

    [Fact]
    public void A_gateway_entry_the_plan_does_not_list_and_a_sink_are_the_rows_they_always_were()
    {
        var vm = Merged();

        var extra = Row(vm, "unlisted-extra");
        Assert.False(extra.HasPlan);
        Assert.Equal(string.Empty, extra.Policy);
        Assert.Equal(string.Empty, extra.Buckets);
        Assert.Empty(extra.Facts);
        Assert.Equal("healthy", extra.State);
        Assert.Equal("logs", extra.Signals);

        var sink = Row(vm, "audit-file");
        Assert.Equal("audit_sinks", sink.Target);
        Assert.Equal("jsonl", sink.Kind);
        Assert.Equal("audit-events", sink.Signals);
        Assert.False(sink.HasPlan);
    }

    [Fact]
    public void Names_are_matched_without_case_and_an_entry_claims_one_destination_only()
    {
        var vm = Panel();
        var twin = LocalSqlite.Replace("local-sqlite", "LOCAL-SQLITE", StringComparison.Ordinal);
        vm.Apply(Running(HealthJson(twin + "," + LocalSqlite)));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        // The first entry is the plan's local store; the second has no destination left to claim it and stays a row of its own.
        Assert.Equal(new[] { "LOCAL-SQLITE", "local-sqlite" }, vm.ObservabilityRows.Select(r => r.Name).ToArray());
        Assert.True(vm.ObservabilityRows[0].HasPlan);
        Assert.False(vm.ObservabilityRows[1].HasPlan);
    }

    [Fact]
    public void A_galileo_destination_keeps_its_proper_name_with_or_without_the_gateways_entry()
    {
        var plan = ObservabilityPlanParser.Parse(
            "{\"rows\":[{\"bucket\":\"a\",\"signal\":\"logs\",\"destination\":\"galileo\",\"decision\":\"send\",\"redaction_profile\":\"none\"}]}").Plan!;
        var withEntry = Panel();
        withEntry.Apply(Running(HealthJson("{\"name\":\"galileo\",\"preset\":\"galileo\",\"kind\":\"otlp\",\"enabled\":true,\"state\":\"healthy\",\"signals\":[\"logs\"]}")));
        var silent = new OverviewPanelViewModel(_services!);
        silent.Apply(Running(HealthJson(string.Empty)));

        withEntry.ApplyObservabilityPlan(new ObservabilityPlanRead(ObservabilityPlanStatus.Ok, plan, string.Empty, DateTimeOffset.UtcNow));
        silent.ApplyObservabilityPlan(new ObservabilityPlanRead(ObservabilityPlanStatus.Ok, plan, string.Empty, DateTimeOffset.UtcNow));

        Assert.Equal("Galileo", Assert.Single(withEntry.ObservabilityRows).Name);
        Assert.Equal("galileo", withEntry.ObservabilityRows[0].Kind);
        Assert.Equal("Galileo", Assert.Single(silent.ObservabilityRows).Name);
    }

    // ---- disabled and unavailable ----

    [Fact]
    public void A_destination_the_policy_turns_off_is_disabled_and_one_that_is_on_and_silent_is_unavailable()
    {
        var vm = Merged();

        var archive = Row(vm, "example-archive");
        Assert.Equal("disabled", archive.Policy);
        Assert.Equal("disabled", archive.State);
        Assert.Equal("Neutral", archive.StateKey);
        Assert.Equal("0/14", archive.Buckets);
        Assert.Equal("not-applicable", archive.Redaction);
        Assert.Equal("unavailable", archive.Queue);
        Assert.Equal("unavailable", archive.LastResult);
        Assert.Equal("—", archive.Signals);
        Assert.Equal("http_jsonl", archive.Kind);
        Assert.Equal("archive.example.test", archive.Endpoint);
        Assert.Contains("Off in the policy", archive.Detail, StringComparison.Ordinal);

        var metrics = Row(vm, "example-metrics");
        Assert.Equal("enabled", metrics.Policy);
        Assert.Equal("unavailable", metrics.State);
        Assert.Equal("Neutral", metrics.StateKey);
        Assert.Equal("metrics", metrics.Signals);
        Assert.Equal("13/14", metrics.Buckets);
        Assert.Equal("unavailable", metrics.Queue);
        Assert.Equal("unavailable", metrics.LastResult);
        Assert.Equal("127.0.0.1:9464", metrics.Endpoint);
        Assert.Contains("does not report this destination", metrics.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_gateway_down_the_configured_destinations_are_still_listed_each_disabled_or_unavailable()
    {
        var vm = Panel();
        vm.Apply(GatewaySnapshot.Initial);

        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));

        Assert.True(vm.HasObservabilityRows);
        Assert.Equal(
            new[] { "unavailable", "unavailable", "unavailable", "disabled", "unavailable" },
            vm.ObservabilityRows.Select(r => r.State).ToArray());
        Assert.Equal(
            new[] { "enabled", "enabled", "enabled", "disabled", "enabled" },
            vm.ObservabilityRows.Select(r => r.Policy).ToArray());
    }

    [Fact]
    public void An_entry_without_a_state_is_as_silent_as_no_entry()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson("{\"name\":\"local-sqlite\",\"kind\":\"sqlite\",\"enabled\":true,\"signals\":[\"logs\"]}")));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        var local = Assert.Single(vm.ObservabilityRows);
        Assert.Equal("unavailable", local.State);
        Assert.Equal("enabled", local.Policy);
    }

    [Fact]
    public void The_gateways_state_is_kept_even_where_it_disagrees_with_the_policy_a_restart_is_due()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite.Replace("local-sqlite", "example-archive", StringComparison.Ordinal))));

        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));

        var archive = Row(vm, "example-archive");
        Assert.Equal("disabled", archive.Policy);
        Assert.Equal("healthy", archive.State);
        Assert.Equal("Ok", archive.StateKey);
    }

    // ---- both runtimes, by presence ----

    [Theory]
    [InlineData("runtime-0.8.10/rest/health.sinks.synthetic.json", DestinationsPlan, 90)]
    [InlineData("runtime-95159fd/rest/health.json", PinnedPlan, 7)]
    public void Each_runtimes_own_health_and_plan_merge_through_the_same_code(string health, string plan, int days)
    {
        var vm = Panel(Config(retention: string.Empty));
        var snapshot = Running(Fixture(health));

        vm.Apply(snapshot);
        vm.ApplyObservabilityPlan(Ok(plan));

        var local = Row(vm, "local-sqlite");
        Assert.Equal("healthy", local.State);
        Assert.Equal("enabled", local.Policy);
        Assert.Equal("14/14", local.Buckets);
        Assert.Equal("unredacted (none)", local.Redaction);
        Assert.True(vm.HasLocalStorage);

        // config.yaml is silent about retention, so the window is the one the running gateway reports (the runtime's own default: 90 days, 7 days).
        Assert.Equal(days.ToString(CultureInfo.InvariantCulture) + " days", vm.LocalStorage!.Retention);
        Assert.Equal("healthy", vm.LocalStorage.Controller);
    }

    // ---- Local SQLite ----

    [Fact]
    public void The_local_sqlite_line_is_the_tuis_with_the_retention_config_yaml_names()
    {
        var vm = Merged();

        Assert.True(vm.HasLocalStorage);
        Assert.Equal("Local SQLite · retention=30 days · controller=healthy · judge capture=enabled", vm.LocalStorageSummary);
        Assert.Equal($"Event history: {Path.GetFullPath(AuditPath)} · Judge bodies: {Path.GetFullPath(JudgePath)}", vm.LocalStoragePaths);
        Assert.Equal("Neutral", vm.LocalStorageToneKey);
    }

    [Theory]
    [InlineData("0", "unbounded", "Warn")]
    [InlineData("1", "1 day", "Neutral")]
    [InlineData("30", "30 days", "Neutral")]
    [InlineData("365", "365 days", "Neutral")]
    public void Retention_is_what_config_yaml_says_even_where_the_running_gateway_still_has_another_window(string written, string shown, string tone)
    {
        var vm = Panel(Config(written));
        vm.Apply(Running(HealthJson(LocalSqlite, "\"retention_state\":\"healthy\",\"retention_days\":90")));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(shown, vm.LocalStorage!.Retention);
        Assert.Contains($"retention={shown} ", vm.LocalStorageSummary, StringComparison.Ordinal);
        Assert.Equal(tone, vm.LocalStorageToneKey);
        Assert.Equal(tone == "Warn", vm.LocalStorage.NeedsAttention);
    }

    [Fact]
    public void Where_config_yaml_is_silent_the_window_is_the_gateways_and_where_both_are_silent_it_is_the_runtimes_default()
    {
        var gateway = Panel(Config(retention: string.Empty));
        gateway.Apply(Running(HealthJson(LocalSqlite, "\"retention_days\":90")));
        gateway.ApplyObservabilityPlan(Ok(PinnedPlan));

        var neither = new OverviewPanelViewModel(_services!);
        neither.Apply(Running(HealthJson(LocalSqlite)));
        neither.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal("90 days", gateway.LocalStorage!.Retention);
        Assert.Equal("runtime default", neither.LocalStorage!.Retention);
        Assert.Equal("unavailable", neither.LocalStorage.Controller);
    }

    [Theory]
    [InlineData("\"retention_state\":\"healthy\"", "healthy", "Neutral")]
    [InlineData("\"retention_state\":\"waiting_for_readiness\"", "waiting_for_readiness", "Neutral")]
    [InlineData("\"retention_state\":\"degraded\",\"retention_failure\":\"sqlite_busy\"", "degraded (sqlite_busy)", "Warn")]
    [InlineData("\"retention_state\":\"stopped\"", "stopped", "Warn")]
    [InlineData("\"retention_days\":30", "unavailable", "Neutral")]
    public void The_controller_is_the_gateways_word_for_the_reaper_and_a_failing_one_is_a_warning(string details, string controller, string tone)
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite, details)));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(controller, vm.LocalStorage!.Controller);
        Assert.Equal(tone, vm.LocalStorageToneKey);
    }

    [Theory]
    [InlineData("", "enabled")]
    [InlineData("  retain_judge_bodies: true\n", "enabled")]
    [InlineData("  retain_judge_bodies: false\n", "disabled")]
    public void Judge_capture_is_on_unless_config_yaml_turns_it_off(string guardrail, string capture)
    {
        var vm = Panel(Config(guardrail: guardrail));
        vm.Apply(Running(HealthJson(LocalSqlite)));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(capture, vm.LocalStorage!.JudgeCapture);
        Assert.Contains($"judge capture={capture}", vm.LocalStorageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_config_yaml_names_no_files_they_are_the_data_directorys_and_a_relative_one_is_read_against_it()
    {
        var silent = Panel("observability:\n  destinations: []\n");
        silent.Apply(Running(HealthJson(LocalSqlite)));
        silent.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(_services!.Paths.AuditDatabasePath, silent.LocalStorage!.EventHistoryPath);
        Assert.Equal(_services.Paths.JudgeBodiesDatabasePath, silent.LocalStorage.JudgeBodiesPath);

        var relative = Panel("observability:\n  local:\n    path: history/audit.db\n    judge_bodies_path: history/judge.db\n");
        relative.Apply(Running(HealthJson(LocalSqlite)));
        relative.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(Path.GetFullPath(Path.Combine("history", "audit.db"), _services!.Paths.DataDirectory), relative.LocalStorage!.EventHistoryPath);
        Assert.Equal(Path.GetFullPath(Path.Combine("history", "judge.db"), _services.Paths.DataDirectory), relative.LocalStorage.JudgeBodiesPath);
    }

    [Fact]
    public void Without_a_plan_there_is_no_local_sqlite_line_and_the_card_is_what_the_gateway_reports()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(string.Join(",", LocalSqlite, ExampleOtlp))));

        Assert.False(vm.HasLocalStorage);
        Assert.False(vm.HasObservabilityPlan);
        Assert.Null(vm.LocalStorage);
        Assert.Equal(string.Empty, vm.LocalStorageSummary);
        Assert.Equal(string.Empty, vm.LocalStoragePaths);
        Assert.All(vm.ObservabilityRows, r => Assert.False(r.HasPlan));
    }

    // ---- the redaction label ----

    [Fact]
    public void The_redaction_label_is_loading_then_the_plans_aggregate_and_it_is_a_row_of_the_configuration_card()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite)));
        Assert.Equal("per-route (loading)", vm.RedactionSummary);
        Assert.Equal("per-route (loading)", ConfigurationRow(vm, "Redaction").Value);

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal("per-route · unredacted", vm.RedactionSummary);
        var row = ConfigurationRow(vm, "Redaction");
        Assert.Equal("per-route · unredacted", row.Value);
        Assert.Equal("Neutral", row.ToneKey);

        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));

        Assert.Equal("per-route · none,sensitive,strict", vm.RedactionSummary);
        Assert.Equal("per-route · none,sensitive,strict", ConfigurationRow(vm, "Redaction").Value);
    }

    [Fact]
    public void A_plan_that_could_not_be_read_says_the_label_is_unavailable_and_not_that_it_is_loading()
    {
        var vm = Panel();

        vm.ApplyObservabilityPlan(Failed(ObservabilityPlanStatus.Failed, "the command exited 1"));

        Assert.Equal("per-route (unavailable)", vm.RedactionSummary);
        Assert.Equal("per-route (unavailable)", ConfigurationRow(vm, "Redaction").Value);
    }

    [Fact]
    public void The_redaction_row_sits_after_the_four_the_card_opens_with_and_scoped_it_is_marked_global()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite)));
        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label.StartsWith("Redaction", StringComparison.Ordinal));
        vm.ToggleConfigurationCommand.Execute(null);
        var labels = vm.ConfigurationRows.Select(r => r.Label).ToList();
        Assert.True(labels.IndexOf("Redaction") >= OverviewPanelViewModel.ConfigurationRowsShown);
        Assert.Equal(labels.IndexOf("Guardrail") + 1, labels.IndexOf("Redaction"));
        Assert.Equal(labels.IndexOf("Redaction") + 1, labels.IndexOf("Deployment mode"));

    }

    [Fact]
    public void Scoped_to_a_connector_the_redaction_row_is_the_installs_and_says_so()
    {
        using var scene = OverviewScene.Create(seedAudit: false, seedAgents: false);
        var vm = new OverviewPanelViewModel(scene.Services);
        scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());
        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));

        Assert.True(scene.Services.ConnectorScope.Set("hermes"));
        vm.ApplyScope();

        vm.ToggleConfigurationCommand.Execute(null);
        var row = Assert.Single(vm.ConfigurationRows, r => r.Label == "Redaction (global)");
        Assert.Equal("per-route · none,sensitive,strict", row.Value);
        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label == "Redaction");
    }

    private static ConfigRow ConfigurationRow(OverviewPanelViewModel vm, string label)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(r => r.Label == label);
    }

    // ---- a plan that cannot be read ----

    [Theory]
    [InlineData(ObservabilityPlanStatus.NotInstalled, "'defenseclaw' was not found")]
    [InlineData(ObservabilityPlanStatus.TimedOut, "the command did not finish in 30 s")]
    [InlineData(ObservabilityPlanStatus.Failed, "the command exited 1")]
    [InlineData(ObservabilityPlanStatus.Malformed, "the command did not print a plan (The plan was not JSON)")]
    public void A_plan_that_could_not_be_read_leaves_exactly_the_gateways_card_with_a_one_line_note(ObservabilityPlanStatus status, string message)
    {
        var health = Running(HealthJson(string.Join(",", LocalSqlite, ExampleOtlp, ExampleSplunk), "\"retention_days\":90", Sinks));
        var before = Panel();
        before.Apply(health);
        var after = new OverviewPanelViewModel(_services!);
        after.Apply(health);

        after.ApplyObservabilityPlan(Failed(status, message));

        Assert.Equal(before.ObservabilityRows.ToArray(), after.ObservabilityRows.ToArray());
        Assert.False(after.HasObservabilityPlan);
        Assert.False(after.HasLocalStorage);
        Assert.True(after.HasObservabilityNote);
        Assert.Equal($"Observability plan unavailable: {message}. The card shows what the gateway reports.", after.ObservabilityNote);
        Assert.DoesNotContain('\n', after.ObservabilityNote);
        Assert.Equal("Warn", after.ObservabilityNoteToneKey);
        Assert.False(before.HasObservabilityNote);
    }

    [Fact]
    public void A_good_read_after_a_failed_one_brings_the_plan_back_and_replaces_the_warning_with_where_it_came_from()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite)));
        vm.ApplyObservabilityPlan(Failed(ObservabilityPlanStatus.TimedOut, "the command did not finish in 30 s"));
        Assert.Equal("Warn", vm.ObservabilityNoteToneKey);

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.True(vm.HasObservabilityPlan);
        Assert.True(vm.HasLocalStorage);
        Assert.Equal("Neutral", vm.ObservabilityNoteToneKey);
        Assert.StartsWith("Policy, buckets, redaction and limits are from defenseclaw observability plan, read ", vm.ObservabilityNote, StringComparison.Ordinal);
        Assert.EndsWith("state, queue and last result are from the gateway.", vm.ObservabilityNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_cli_the_real_reader_fails_into_the_same_note_and_nothing_is_started_or_recorded()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson(LocalSqlite)));
        var rowsBefore = vm.ObservabilityRows.ToArray();

        await vm.RefreshObservabilityPlanAsync(force: true, CancellationToken.None);

        Assert.Equal("Observability plan unavailable: 'defenseclaw' was not found. The card shows what the gateway reports.", vm.ObservabilityNote);
        Assert.Equal(rowsBefore, vm.ObservabilityRows.ToArray());
        Assert.False(vm.HasObservabilityPlan);
        Assert.Empty(_services!.Cli.Activity);
    }

    // ---- when the command runs ----

    /// <summary>A command that counts how often it is asked and answers with a plan.</summary>
    private sealed class Counter
    {
        private int _calls;
        private readonly string _stdout;

        public Counter(string stdout) => _stdout = stdout;

        public int Calls => Volatile.Read(ref _calls);

        public Task<PlanCommandOutput> Run(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken token)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(new PlanCommandOutput(0, null, _stdout));
        }
    }

    private static ObservabilityPlanReader ReaderOver(Counter counter, AppServices services) => new(counter.Run, () => services.Config);

    [Fact]
    public async Task A_poll_never_runs_the_command_and_the_data_refresh_reads_once_then_waits_for_the_plan_to_be_five_minutes_old()
    {
        var vm = Panel();
        var counter = new Counter(Fixture(DestinationsPlan));
        vm.PlanReader = ReaderOver(counter, _services!);
        var snapshot = Running(HealthJson(LocalSqlite));

        for (var poll = 0; poll < 12; poll++)
        {
            vm.Apply(snapshot);
        }

        Assert.Equal(0, counter.Calls);

        await vm.InitializeAsync();
        Assert.Equal(1, counter.Calls);
        Assert.True(vm.HasObservabilityPlan);

        await vm.InitializeAsync();
        await vm.RefreshObservabilityPlanAsync(force: false, CancellationToken.None);
        for (var poll = 0; poll < 12; poll++)
        {
            vm.Apply(snapshot);
        }

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Refresh_reads_the_plan_again_whatever_its_age()
    {
        var vm = Panel();
        var counter = new Counter(Fixture(DestinationsPlan));
        vm.PlanReader = ReaderOver(counter, _services!);
        await vm.InitializeAsync();
        Assert.Equal(1, counter.Calls);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task A_forced_read_runs_it_and_an_unforced_one_in_the_same_minute_does_not()
    {
        var vm = Panel();
        var counter = new Counter(Fixture(DestinationsPlan));
        vm.PlanReader = ReaderOver(counter, _services!);

        await vm.RefreshObservabilityPlanAsync(force: false, CancellationToken.None);
        await vm.RefreshObservabilityPlanAsync(force: false, CancellationToken.None);
        Assert.Equal(1, counter.Calls);

        await vm.RefreshObservabilityPlanAsync(force: true, CancellationToken.None);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public void A_config_change_while_the_panel_is_on_screen_reads_the_plan_again_and_the_local_sqlite_line_follows_the_new_retention()
    {
        var counter = new Counter(Fixture(PinnedPlan));
        OverviewPanelViewModel vm = null!;
        UiThread.Run(() =>
        {
            vm = Panel(Config("30"));
            vm.PlanReader = ReaderOver(counter, _services!);
            vm.Apply(Running(HealthJson(LocalSqlite)));
            vm.SetActive(true);
        });

        try
        {
            UiThread.WaitFor(() => counter.Calls >= 1 && vm.HasLocalStorage, "the plan was read when the panel came on screen");
            Assert.Equal(1, counter.Calls);
            Assert.Equal("30 days", UiThread.Run(() => vm.LocalStorage!.Retention));

            File.WriteAllText(_services!.Paths.ConfigFilePath, Config("45"));
            _services.ReloadConfig();

            UiThread.WaitFor(() => counter.Calls >= 2, "the plan was read again after config.yaml changed");
            UiThread.WaitFor(() => vm.LocalStorage?.Retention == "45 days", "the line shows the new retention");
            Assert.Equal(2, counter.Calls);
        }
        finally
        {
            UiThread.Run(() => vm.SetActive(false));
        }
    }

    [Fact]
    public async Task A_config_change_while_the_panel_was_away_is_picked_up_by_the_next_read_and_not_before()
    {
        var vm = Panel(Config("30"));
        var counter = new Counter(Fixture(PinnedPlan));
        vm.PlanReader = ReaderOver(counter, _services!);
        vm.Apply(Running(HealthJson(LocalSqlite)));
        await vm.RefreshObservabilityPlanAsync(force: false, CancellationToken.None);
        Assert.Equal("30 days", vm.LocalStorage!.Retention);

        File.WriteAllText(_services!.Paths.ConfigFilePath, Config("45"));
        _services.ReloadConfig();

        // Nothing has asked yet: what is on screen is what was read.
        Assert.Equal(1, counter.Calls);

        await vm.RefreshObservabilityPlanAsync(force: false, CancellationToken.None);

        Assert.Equal(2, counter.Calls);
        Assert.Equal("45 days", vm.LocalStorage!.Retention);
    }

    // ---- unchanged data keeps its rows ----

    [Fact]
    public void The_same_plan_and_the_same_answer_keep_the_rows_they_had_and_a_changed_one_replaces_only_its_row()
    {
        var vm = Merged();
        var before = vm.ObservabilityRows.ToArray();

        vm.Apply(Running(HealthJson(string.Join(",", LocalSqlite, ExampleOtlp, ExampleSplunk, UnlistedExtra), "\"retention_state\":\"healthy\",\"retention_days\":90", Sinks)));
        vm.ApplyObservabilityPlan(Ok(DestinationsPlan));

        Assert.All(before.Zip(vm.ObservabilityRows), pair => Assert.Same(pair.First, pair.Second));

        vm.Apply(Running(HealthJson(
            string.Join(",", LocalSqlite, ExampleOtlp.Replace("\"items\":3", "\"items\":9", StringComparison.Ordinal), ExampleSplunk, UnlistedExtra),
            "\"retention_state\":\"healthy\",\"retention_days\":90",
            Sinks)));

        var after = vm.ObservabilityRows.ToArray();
        Assert.NotSame(before[1], after[1]);
        Assert.Equal("9/2048 items, 1.0 KiB/64.0 MiB, 0 dropped", after[1].Queue);
        Assert.Same(before[0], after[0]);
        Assert.Same(before[2], after[2]);
    }

    [Fact]
    public void The_same_read_applied_twice_is_not_redrawn()
    {
        var vm = Merged();
        var read = Ok(PinnedPlan);
        vm.ApplyObservabilityPlan(read);
        var rows = vm.ObservabilityRows.ToArray();

        vm.ApplyObservabilityPlan(read);

        Assert.Equal(rows, vm.ObservabilityRows.ToArray());
        Assert.All(rows.Zip(vm.ObservabilityRows), pair => Assert.Same(pair.First, pair.Second));
    }

    // ---- no secret on any surface ----

    /// <summary>Everything the card puts in front of a person or a screen reader: every string a row exposes, the lines, the note, the configuration rows.</summary>
    private static string EverythingShown(OverviewPanelViewModel vm)
    {
        var text = new StringBuilder();
        foreach (var row in vm.ObservabilityRows)
        {
            text.AppendLine(row.ToString());
            foreach (var property in typeof(ObservabilityRow).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)))
            {
                text.AppendLine(property.GetValue(row) as string);
            }

            foreach (var fact in row.Facts)
            {
                text.AppendLine(fact.Label + " " + fact.Value + " " + fact.ToneKey);
            }
        }

        foreach (var property in typeof(OverviewPanelViewModel).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0))
        {
            try
            {
                text.AppendLine(property.GetValue(vm) as string);
            }
            catch (TargetInvocationException)
            {
                // A getter that needs something this test did not set up shows nothing.
            }
        }

        foreach (var row in vm.ConfigurationRows.Concat(ExpandedConfiguration(vm)))
        {
            text.AppendLine(row.ToString());
        }

        foreach (var row in vm.ServiceRows.Select(r => r.ToString()).Concat(vm.Attention.Select(a => a.ToString())))
        {
            text.AppendLine(row);
        }

        return text.ToString();
    }

    private static IEnumerable<ConfigRow> ExpandedConfiguration(OverviewPanelViewModel vm)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.ToArray();
    }

    [Fact]
    public void No_credential_in_an_address_or_in_what_the_gateway_says_about_a_failure_is_on_any_surface_of_the_card()
    {
        // example-splunk has a credential in its address in config.yaml and again in the gateway's last_error; example-archive has a token in its path.
        var vm = Merged();

        var shown = EverythingShown(vm);

        Assert.Contains("splunk.example.test:8088", shown, StringComparison.Ordinal);
        Assert.Contains("archive.example.test", shown, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("api_key", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_gateways_own_words_about_a_failure_are_cut_to_the_host_in_the_tooltip_with_or_without_a_plan()
    {
        var withoutPlan = Panel();
        withoutPlan.Apply(Running(HealthJson(ExampleSplunk)));

        var detail = Row(withoutPlan, "example-splunk").Detail;

        Assert.Contains("last error: Post \"https://splunk.example.test:8088\": context deadline exceeded", detail, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, detail, StringComparison.OrdinalIgnoreCase));

        var withPlan = Merged();
        Assert.Equal(detail, Row(withPlan, "example-splunk").Detail);
    }

    [Fact]
    public void A_credential_the_gateway_writes_into_a_reason_or_an_error_is_masked()
    {
        var vm = Panel();
        vm.Apply(Running(HealthJson("{\"name\":\"d\",\"kind\":\"otlp\",\"state\":\"degraded\",\"reason\":\"refused api_key=synthkey-synthkey\",\"last_error\":\"Authorization: Bearer synthtoken-synthtoken\"}")));

        var detail = Row(vm, "d").Detail;

        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, detail, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[redacted]", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_config_yaml_gives_that_cannot_be_read_is_never_shown_in_its_place()
    {
        var config = "observability:\n  destinations:\n    - name: local-sqlite\n      kind: otlp\n      endpoint: https://synthuser:synthpass@:99999/x?api_key=synthkey\n";
        var vm = Panel(config);
        vm.Apply(Running(HealthJson(LocalSqlite)));

        vm.ApplyObservabilityPlan(Ok(PinnedPlan));

        Assert.Equal(EndpointDisplay.Unreadable, Row(vm, "local-sqlite").Endpoint);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, EverythingShown(vm), StringComparison.OrdinalIgnoreCase));
    }

    // ---- the row ----

    [Fact]
    public void A_row_announces_what_the_plan_and_the_gateway_say_in_sentences_and_one_without_a_plan_announces_what_it_always_did()
    {
        var vm = Merged();

        var otlp = Row(vm, "example-otlp").ToString();
        Assert.StartsWith("example-otlp, otel destination. otlp. policy enabled. healthy. signals logs, traces, metrics. buckets 13/14.", otlp, StringComparison.Ordinal);
        Assert.Contains("redaction redacted: sensitive.", otlp, StringComparison.Ordinal);
        Assert.Contains("queue 3/2048 items, 1.0 KiB/64.0 MiB, 0 dropped.", otlp, StringComparison.Ordinal);
        Assert.Contains("endpoint collector.example.test:4318", otlp, StringComparison.Ordinal);

        Assert.Equal("unlisted-extra, otel destination. otlp. healthy. signals logs", Row(vm, "unlisted-extra").ToString());
    }

    [Fact]
    public void A_facts_line_leaves_out_what_has_nothing_to_say()
    {
        var row = new ObservabilityRow { Name = "d", Redaction = "unredacted (none)", Queue = "—", Limits = "", LastResult = "ok 14:03:07", LastResultKey = "Warn", Endpoint = "h:1", Policy = "enabled" };

        Assert.Equal(
            new[] { ("Redaction", "unredacted (none)", "Neutral"), ("Last result", "ok 14:03:07", "Warn"), ("Endpoint", "h:1", "Neutral") },
            row.Facts.Select(f => (f.Label, f.Value, f.ToneKey)).ToArray());
        Assert.True(row.HasFacts);
        Assert.False(new ObservabilityRow { Name = "d" }.HasFacts);
    }

    [Fact]
    public void A_time_is_the_clock_for_today_and_the_date_with_it_for_an_older_day()
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 14, 3, 7, now.Offset);
        var older = today.AddDays(-9);

        Assert.Equal(today.ToString("HH:mm:ss", CultureInfo.CurrentCulture), OverviewPanelViewModel.FormatResultTime(today));
        Assert.Equal(older.ToString("MMM d HH:mm", CultureInfo.CurrentCulture), OverviewPanelViewModel.FormatResultTime(older));
    }

    private static string FormatTime(string rfc3339) =>
        OverviewPanelViewModel.FormatResultTime(DateTimeOffset.Parse(rfc3339, CultureInfo.InvariantCulture));
}
