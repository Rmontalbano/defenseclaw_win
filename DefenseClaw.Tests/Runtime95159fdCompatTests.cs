using System.Net;
using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Core readers and clients against the synthetic fixture set derived from DefenseClaw source commit 95159fd
/// (<c>Fixtures/runtime-95159fd</c>; see <c>docs/RUNTIME-COMPAT-95159fd.md</c>). The 0.8.10 fixtures and suites are untouched and stay green;
/// this class only adds the newer shapes.
/// </summary>
public sealed class Runtime95159fdCompatTests
{
    private static readonly string[] Forbidden =
    [
        Environment.UserName, "/home/", "dc-next", "win-build", "spike-otlp", ".defenseclaw-next", "localhost:18971", "127.0.0.1:18972", "127.0.0.1:18971",
    ];

    // ---- Gateway REST ----

    private const string Token = "fixture-bearer-token-0123456789";

    private static (GatewayClient Client, FakeHttpMessageHandler Handler) Gateway(Action<FakeHttpMessageHandler> map)
    {
        var handler = new FakeHttpMessageHandler();
        map(handler);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        return (new GatewayClient(http, () => new SecretValue(Token), ownsHttpClient: true), handler);
    }

    [Fact]
    public async Task Health_of_the_newer_gateway_parses_and_keeps_the_blocks_this_app_does_not_model()
    {
        var (client, _) = Gateway(h => h.Map("/health", RuntimeFixtures.Read("rest/health.json")));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var health = result.Value!;
        Assert.Equal("1.0.0", health.Provenance!.BinaryVersion);
        Assert.Equal(7, health.Provenance.SchemaVersion);
        Assert.True(health.Api!.IsRunning);
        Assert.True(health.Guardrail!.IsDisabled);
        Assert.True(health.FleetUplink!.IsDisabled);
        Assert.True(health.Telemetry!.IsRunning);
        Assert.Equal(103880, health.UptimeMs);

        // A fresh install with no connector carries neither "connector" nor "connectors"; both read as empty, never null.
        Assert.Null(health.Connector);
        Assert.Empty(health.Connectors);

        // Subsystems that appeared after 0.8.10 land in the extension bag instead of being dropped or throwing.
        Assert.NotNull(health.AdditionalData);
        Assert.Contains("routing", health.AdditionalData!.Keys);
        Assert.Contains("ai_runtime", health.AdditionalData.Keys);
        Assert.Contains("acp", health.AdditionalData.Keys);
    }

    [Fact]
    public async Task Status_alerts_and_the_list_routes_of_the_newer_gateway_parse()
    {
        var (client, _) = Gateway(h => h
            .Map("/status", RuntimeFixtures.Read("rest/status.json"))
            .Map("/alerts", RuntimeFixtures.Read("rest/alerts.json"))
            .Map("/mcps", RuntimeFixtures.Read("rest/mcps.json"))
            .Map("/enforce/blocked", RuntimeFixtures.Read("rest/enforce-blocked.json"))
            .Map("/enforce/allowed", RuntimeFixtures.Read("rest/enforce-allowed.json")));

        var status = await client.GetStatusAsync();
        Assert.Equal(GatewayStatus.Ok, status.Status);
        Assert.Equal("1.0.0", status.Value!.Provenance!.BinaryVersion);
        Assert.Empty(status.Value.ConnectorModes);
        Assert.True(status.Value.Health!.Api!.IsRunning);

        var alerts = await client.GetAlertsAsync();
        Assert.Equal(GatewayStatus.Ok, alerts.Status);
        Assert.Equal(2, alerts.Value!.Count);
        Assert.Equal("HIGH", alerts.Value[0].Severity);
        Assert.Equal("subsystem.degraded", alerts.Value[0].Action);
        Assert.Equal("otel-provider", alerts.Value[0].StructuredString("defenseclaw.health.subsystem"));
        Assert.NotEqual(default, alerts.Value[0].Timestamp);

        Assert.Empty((await client.GetMcpsAsync()).Value!);
        Assert.Empty((await client.GetEnforceBlockedAsync()).Value!);
        Assert.Empty((await client.GetEnforceAllowedAsync()).Value!);
    }

    [Fact]
    public async Task Routes_that_need_an_openclaw_upstream_answer_not_connected_and_are_read_as_such()
    {
        var (client, _) = Gateway(h => h
            .Map("/tools/catalog", RuntimeFixtures.Read("rest/tools-catalog-not-connected.json"), HttpStatusCode.BadGateway)
            .Map("/skills", RuntimeFixtures.Read("rest/skills-not-connected.json"), HttpStatusCode.BadGateway));

        Assert.Equal(GatewayStatus.NotConnected, (await client.GetSkillsAsync()).Status);
        Assert.Equal(GatewayStatus.NotConnected, (await client.GetToolsCatalogAsync()).Status);
    }

    [Fact]
    public async Task A_request_without_the_token_is_a_401_the_client_reads_as_unauthorized()
    {
        var handler = new FakeHttpMessageHandler().Map("/status", RuntimeFixtures.Read("rest/unauthorized.json"), HttpStatusCode.Unauthorized);
        using var client = new GatewayClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") });

        Assert.Equal(GatewayStatus.Unauthorized, (await client.GetStatusAsync()).Status);
    }

    [Fact]
    public async Task Every_request_names_the_client_the_way_the_newer_gateways_csrf_gate_asks_for()
    {
        var (client, handler) = Gateway(h => h
            .Map("/health", RuntimeFixtures.Read("rest/health.json"))
            .Map("/status", RuntimeFixtures.Read("rest/status.json"))
            .Map("/alerts", "[]"));

        _ = await client.GetHealthAsync();
        _ = await client.GetStatusAsync();
        _ = await client.GetAlertsAsync(25);

        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal(new[] { GatewayClient.DefaultClientHeaderValue }, request.Headers.GetValues(GatewayClient.ClientHeaderName));
        }

        // The header names the app, not the operator: no token, user or machine name rides in it.
        Assert.DoesNotContain(Token, GatewayClient.DefaultClientHeaderValue, StringComparison.Ordinal);
    }

    [Fact]
    public void The_error_body_of_a_missing_client_header_is_an_error_not_a_parse_failure()
    {
        // internal/gateway/api.go apiCSRFProtect: 403 {"error":"missing X-DefenseClaw-Client header"}
        var result = GatewayClient.Interpret<IReadOnlyList<GatewayAlert>>(HttpStatusCode.Forbidden, """{"error":"missing X-DefenseClaw-Client header"}""");

        Assert.False(result.IsOk);
        Assert.NotEqual(GatewayStatus.Ok, result.Status);
    }

    // ---- CLI JSON read by Core ----

    [Fact]
    public void Keys_list_of_the_newer_cli_parses_with_its_upper_case_requirement()
    {
        Assert.True(CredentialListParser.TryParse(RuntimeFixtures.Read("cli/keys-list.json"), out var rows, out var error), error);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsSet));
        Assert.All(rows, r => Assert.Equal("not_used", r.Requirement));
        Assert.All(rows, r => Assert.False(r.IsMissingRequired));
        Assert.Equal("DEFENSECLAW_LLM_KEY", rows[0].EnvName);
        Assert.Equal("unset", rows[0].Source);
    }

    // ---- config.yaml (schema 8, the same number as 0.8.10) ----

    [Fact]
    public void A_fresh_config_reads_with_the_defaults_the_app_relies_on()
    {
        var document = ConfigStore.Parse(RuntimeFixtures.Read("config/config.fresh.yaml"));

        Assert.Equal(8, document.Config.ConfigVersion);
        Assert.Equal(18970, document.Config.Gateway.ApiPort);
        Assert.Equal("DEFENSECLAW_GATEWAY_TOKEN", document.Config.Gateway.TokenEnv);
        Assert.Equal("local", document.Config.Guardrail.ScannerMode);
        Assert.Equal("DEFENSECLAW_LLM_KEY", document.Config.Llm.ApiKeyEnv);
        Assert.Equal("CISCO_AI_DEFENSE_API_KEY", document.Config.CiscoAiDefense.ApiKeyEnv);
        Assert.Empty(document.Config.Guardrail.Connectors);
        Assert.Contains("observability", document.Sections.Keys);
    }

    [Fact]
    public void A_configured_install_reads_connectors_and_keeps_every_key_the_app_does_not_model_when_a_section_is_replaced()
    {
        var yaml = RuntimeFixtures.Read("config/config.connectors.yaml");
        var document = ConfigStore.Parse(yaml);
        var config = document.Config;

        Assert.Equal("claudecode", config.Claw.Mode);
        Assert.True(config.Guardrail.Enabled);
        Assert.Equal("claudecode", config.Guardrail.Connector);
        Assert.Equal("observe", config.Guardrail.Connectors["claudecode"].Mode);
        Assert.Equal("open", config.Guardrail.Connectors["ClaudeCode"].HookFailMode);
        Assert.Equal("closed", config.Guardrail.Connectors["codex"].HookFailMode);
        Assert.Equal("Blocked by policy.", config.Guardrail.Connectors["codex"].BlockMessage);
        Assert.True(config.AiDiscovery.Enabled);
        Assert.Equal("enhanced", config.AiDiscovery.Mode);
        Assert.Equal(new[] { "~" }, config.AiDiscovery.ScanRoots);

        // The editor replaces whole top-level blocks; the other blocks (api_bind, hilt, block_at, observability.local ...) come back byte for byte.
        var edited = document.WithSectionReplaced("llm", "llm:\n  api_key_env: OTHER_KEY\n");
        Assert.Contains("api_bind: 127.0.0.1", edited, StringComparison.Ordinal);
        Assert.Contains("block_at: HIGH", edited, StringComparison.Ordinal);
        Assert.Contains("judge_bodies_path:", edited, StringComparison.Ordinal);
        Assert.Contains("OTHER_KEY", edited, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judge_history_reader_follows_observability_local_paths_of_the_newer_config()
    {
        using var directory = new TempDirectory();
        var paths = new DefenseClawPaths(directory.Path, directory.Path, [], _ => false);
        var configured = ConfigStore.Parse(RuntimeFixtures.Read("config/config.connectors.yaml"));

        var reader = JudgeHistoryReader.ForConfig(paths, configured.RawText);

        // The fixture names C:\Users\operator\.defenseclaw\...: those exact files, not the default beside the data directory.
        Assert.Equal(@"C:\Users\operator\.defenseclaw\judge_bodies.db", reader.JudgeBodiesPath);
        Assert.Equal(@"C:\Users\operator\.defenseclaw\audit.db", reader.LegacyAuditPath);
        Assert.Equal(JudgeHistoryStatus.Missing, (await reader.ReadAsync()).Status);
    }

    // ---- audit.db at schema 8 / 53 migrations ----

    [Fact]
    public void The_schema_fixture_is_the_53_migration_database_with_the_columns_the_readers_probe()
    {
        using var database = RuntimeFixtures.CreateAuditDatabase();

        Assert.Equal(38, RuntimeFixtures.Scalar<int>(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
        Assert.Equal(53, RuntimeFixtures.Scalar<int>(database, "SELECT MAX(version) FROM schema_version"));
        foreach (var column in new[] { "bucket", "event_name", "signal", "source", "payload_json", "connector", "enforced", "structured_json", "retention_timestamp_unix_nano" })
        {
            Assert.Equal(1, RuntimeFixtures.Scalar<int>(database, $"SELECT COUNT(*) FROM pragma_table_info('audit_events') WHERE name = '{column}'"));
        }

        // activity_events survives only as history from before 1.0 (nothing writes it any more); the reader must not need it.
        Assert.Equal(1, RuntimeFixtures.Scalar<int>(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'activity_events'"));
        Assert.Equal(1, RuntimeFixtures.Scalar<int>(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'alert_acknowledgement_projection'"));
    }

    private static string At(int minute, int nanos = 123456700) =>
        $"2030-01-15T10:{minute:D2}:00.{nanos:D9}Z";

    private static TestAuditDatabase SeededDatabase()
    {
        var database = RuntimeFixtures.CreateAuditDatabase();
        RuntimeFixtures.InsertV8Event(database, "finding-1", At(1), "scan-finding", "security.finding", "finding.observed", "HIGH", "claudecode",
            details: "finding.observed", structuredJson: """{"defenseclaw.finding.rule_id":"CMD-ENV-DUMP"}""");
        RuntimeFixtures.InsertV8Event(database, "finding-2", At(2), "scan-finding", "security.finding", "finding.observed", "MEDIUM", "codex");
        RuntimeFixtures.InsertV8Event(database, "hook-allow", At(3), "connector-hook", "guardrail.evaluation", "guardrail.evaluated", "INFO", "claudecode",
            details: "connector=claudecode result=ok action=allow raw_action=allow severity=NONE mode=action", enforced: 0);
        RuntimeFixtures.InsertV8Event(database, "hook-block", At(4), "connector-hook", "guardrail.evaluation", "guardrail.evaluated", "HIGH", "claudecode",
            details: "connector=claudecode result=ok action=block raw_action=block severity=HIGH mode=action", enforced: 1,
            payloadJson: """{"defenseclaw.guardrail.decision":"block"}""");
        RuntimeFixtures.InsertV8Event(database, "change", At(5), "config.change.applied", "compliance.activity", "config.change.applied", "INFO", null,
            actor: "cli", target: "config:config.yaml", details: """{"reason":"edit","before":{},"after":{}}""");
        RuntimeFixtures.InsertV8Event(database, "telemetry", At(6), "telemetry-destination", "telemetry.ingest", "span.received", "HIGH", null, actor: "defenseclaw");
        RuntimeFixtures.InsertV8Event(database, "metric-row", At(7), "scan", "guardrail.evaluation", "guardrail.evaluated", "INFO", "claudecode");
        using (var connection = database.OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit_events SET signal = 'metrics' WHERE id = 'metric-row'";
            _ = command.ExecuteNonQuery();
        }

        return database;
    }

    [Fact]
    public async Task Audit_reader_pages_rows_written_with_nanosecond_timestamps()
    {
        using var database = SeededDatabase();
        var reader = new AuditReader(database.Path);

        var page = await reader.QueryAsync(new AuditQuery { Limit = 3 });

        Assert.Equal(new[] { "metric-row", "telemetry", "change" }, page.Events.Select(e => e.Id));
        Assert.True(page.HasMore);
        var finding = await reader.GetByIdAsync("finding-1");
        Assert.NotNull(finding);
        Assert.Equal("security.finding", finding!.Bucket);
        Assert.Equal("finding.observed", finding.EventName);
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 1, 0, TimeSpan.Zero).AddTicks(1234567), finding.Timestamp);
        Assert.Contains("claudecode", await reader.ListConnectorsAsync());
        Assert.Contains("security.finding", await reader.ListBucketsAsync());
    }

    [Fact]
    public async Task Alert_queue_counts_the_findings_of_the_newer_database()
    {
        using var database = SeededDatabase();

        var result = await new AlertQueueReader(database.Path).ReadAsync();

        Assert.Equal(AlertQueueStatus.Ok, result.Status);
        Assert.Equal(2, result.Counts.Total);
        Assert.Equal(new[] { "finding-2", "finding-1" }, result.Counts.Newest.Select(i => i.Id));
        Assert.Contains(await new AlertQueueReader(database.Path).ExplainAsync(), line => line.Contains("idx_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mutations_come_from_the_audit_rows_because_activity_events_is_no_longer_written()
    {
        using var database = SeededDatabase();

        var result = await new MutationReader(database.Path).ReadAsync();

        Assert.Equal(MutationStatus.Ok, result.Status);
        Assert.True(result.HasActivityTable);
        var item = Assert.Single(result.Items);
        Assert.Equal(MutationSource.Audit, item.Source);
        Assert.Equal("config.change.applied", item.Action);
        Assert.Equal("cli", item.Actor);
        Assert.Equal("compliance.activity", item.Bucket);
    }

    [Fact]
    public async Task Hook_totals_activity_and_recent_metrics_count_connector_hook_rows()
    {
        using var database = SeededDatabase();

        var totals = await new ConnectorHookTotalsReader(database.Path).ReadAsync();
        Assert.Equal(ConnectorHookTotalsStatus.Ok, totals.Status);
        Assert.Equal(2, totals.Fleet.Calls);
        Assert.Equal(1, totals.Fleet.Blocks);
        Assert.Equal(2, totals.For("claudecode").Calls);

        var recent = await new RecentAuditMetricsReader(database.Path).ReadAsync();
        Assert.Equal(RecentAuditMetricsStatus.Ok, recent.Status);
        Assert.Equal(2, recent.HookCalls);

        var hourly = await new HourlyActivityReader(database.Path, 24, new FixedTime(new DateTimeOffset(2030, 1, 15, 10, 30, 0, TimeSpan.Zero))).ReadAsync();
        Assert.Equal(HourlyActivityStatus.Ok, hourly.Status);
        Assert.Equal(1, hourly.Allowed);
        Assert.Equal(1, hourly.Blocked);
    }

    [Fact]
    public async Task Event_stream_keeps_only_log_signal_rows_and_hides_telemetry_by_default()
    {
        using var database = SeededDatabase();
        var reader = new EventStreamReader(database.Path, 50);

        var events = await reader.ReadAsync(EventStreamKind.Events);
        Assert.Equal(EventStreamStatus.Ok, events.Status);
        Assert.DoesNotContain(events.Rows, r => r.Id == "metric-row");
        Assert.DoesNotContain(events.Rows, r => r.Bucket == "telemetry.ingest");
        Assert.Contains(events.Rows, r => r.Id == "hook-block");

        var verdicts = await reader.ReadAsync(EventStreamKind.Verdicts);
        Assert.Contains(verdicts.Rows, r => r.Id == "hook-block");
    }

    [Fact]
    public async Task Detail_correlation_and_egress_readers_run_against_the_new_tables_and_indexes()
    {
        using var database = SeededDatabase();

        Assert.Empty(await new AlertDetailReader(database.Path).ReadFindingsAsync("00000000-0000-4000-8000-000000000001", "target"));
        Assert.Empty(await new NetworkEgressReader(database.Path).ReadRecentAsync());
        Assert.Equal(0, await new NetworkEgressReader(database.Path).CountSilentBypassAsync(new DateTimeOffset(2030, 1, 15, 11, 0, 0, TimeSpan.Zero)));
        var source = await new AuditReader(database.Path).GetByIdAsync("finding-1");
        var related = await new AuditCorrelationReader(database.Path).RelatedAsync(source!);
        Assert.NotNull(related);
    }

    [Fact]
    public async Task Judge_history_reads_the_newer_judge_bodies_store()
    {
        using var directory = new TempDirectory();
        var path = directory.File("judge_bodies.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var ddl = connection.CreateCommand();
            ddl.CommandText = RuntimeFixtures.Read("audit/judge-bodies-schema.sql");
            _ = await ddl.ExecuteNonQueryAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO judge_responses (id, timestamp, kind, direction, model, action, severity, latency_ms, raw_response, request_id, timestamp_unix_nano)
                VALUES ('judge-1', '2030-01-15T10:01:00.123456789Z', 'injection', 'prompt', 'synthetic-model', 'allow', 'INFO', 12, '{"verdict":"safe"}', 'req-1', 1894788060123456789)
                """;
            _ = await insert.ExecuteNonQueryAsync();
        }

        var result = await new JudgeHistoryReader(path, null).ReadAsync();

        Assert.Equal(JudgeHistoryStatus.Ok, result.Status);
        var row = Assert.Single(result.Rows);
        Assert.Equal("judge-1", row.Id);
        Assert.Equal("injection", row.Kind);
        Assert.Contains("safe", row.Raw, StringComparison.Ordinal);
    }

    // ---- install layouts ----

    [Fact]
    public void The_setup_layout_wins_and_the_installer_script_layout_is_found_when_it_is_all_there_is()
    {
        var setupBin = @"C:\Users\operator\AppData\Local\Programs\DefenseClaw\bin";
        var scriptBin = @"C:\Users\operator\.local\bin";
        var venv = @"C:\Users\operator\.defenseclaw\.venv\Scripts";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DefenseClawPaths Build() => new(
            @"C:\Users\operator\.defenseclaw", setupBin, [], present.Contains, fallbackBinDirectories: [scriptBin, venv]);

        present.Add(Path.Combine(venv, "defenseclaw.exe"));
        Assert.Equal(Path.Combine(venv, "defenseclaw.exe"), Build().FindExecutable("defenseclaw"));

        present.Add(Path.Combine(scriptBin, "defenseclaw.exe"));
        Assert.Equal(Path.Combine(scriptBin, "defenseclaw.exe"), Build().FindExecutable("defenseclaw"));

        present.Add(Path.Combine(setupBin, "defenseclaw.exe"));
        Assert.Equal(Path.Combine(setupBin, "defenseclaw.exe"), Build().FindExecutable("defenseclaw"));
    }

    [Fact]
    public void The_default_fallbacks_are_the_installer_scripts_bin_directory_and_the_venv_under_the_data_directory()
    {
        var fallbacks = DefenseClawPaths.DefaultFallbackBinDirectories(@"C:\Users\operator\.defenseclaw");

        Assert.EndsWith(Path.Combine(".local", "bin"), fallbacks[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(@"C:\Users\operator\.defenseclaw", ".venv", "Scripts"), fallbacks[1]);
    }

    [Fact]
    public void A_caller_that_names_its_own_bin_directory_gets_no_surprise_fallbacks()
    {
        var paths = new DefenseClawPaths(@"C:\x\data", @"C:\x\bin", [], _ => false);

        Assert.Empty(paths.FallbackBinDirectories);
        Assert.Null(paths.FindExecutable("defenseclaw"));
    }

    [Fact]
    public void A_gateway_the_installer_script_started_from_the_dot_local_bin_directory_is_a_trusted_peer()
    {
        var paths = new DefenseClawPaths(
            @"C:\Users\operator\.defenseclaw",
            @"C:\Users\operator\AppData\Local\Programs\DefenseClaw\bin",
            [],
            _ => false,
            fallbackBinDirectories: [@"C:\Users\operator\.local\bin"]);
        var verifier = new GatewayPeerVerifier(paths, new NoListener());

        PortOwner Owner(string image) => new(1, "defenseclaw-gateway", "127.0.0.1", 18970, image);

        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Owner(@"C:\Users\operator\.local\bin\defenseclaw-gateway.exe")));
        Assert.Equal(PortOwnerTrust.Gateway, verifier.Classify(Owner(@"C:\Users\operator\AppData\Local\Programs\DefenseClaw\bin\defenseclaw-gateway.exe")));
        Assert.Equal(PortOwnerTrust.Other, verifier.Classify(Owner(@"C:\Users\operator\Downloads\defenseclaw-gateway.exe")));
    }

    private sealed class NoListener : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    // ---- the fixtures themselves ----

    [Fact]
    public void No_fixture_carries_a_machine_user_or_token()
    {
        var root = Path.Combine(FixtureFiles.Directory, RuntimeFixtures.Directory);
        var files = System.IO.Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.True(files.Length > 30);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var needle in Forbidden)
            {
                Assert.False(text.Contains(needle, StringComparison.OrdinalIgnoreCase), $"{Path.GetRelativePath(root, file)} contains '{needle}'");
            }

            Assert.DoesNotContain("Bearer ", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"sk-[A-Za-z0-9_-]{16,}", text);
        }
    }

    [Fact]
    public void Every_json_fixture_is_valid_json()
    {
        var root = Path.Combine(FixtureFiles.Directory, RuntimeFixtures.Directory);
        foreach (var file in System.IO.Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
