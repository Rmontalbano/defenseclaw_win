using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// One synthetic install for the Overview's tests: a scratch data directory holding a config.yaml (two connectors, the gateway's API on a port
/// nothing listens on, so no test can reach a real gateway), a stale doctor cache, an <c>ai_discovery_state.json</c>, and an <c>audit.db</c>
/// built from the genuine DDL with a handful of rows whose counts the tests assert: hook calls and blocks in known hours, and two findings.
/// Every name is made up. Nothing is read from, or written to, the real <c>~\.defenseclaw</c>.
/// <para>
/// What the rows are (the tests' arithmetic): the current hour has 4 <c>claudecode</c> and 2 <c>hermes</c> allowed hook calls; three hours ago 3
/// allowed and 1 blocked hook call, all <c>claudecode</c>; five hours ago one <c>guardrail-block</c> event from <c>hermes</c>. So the newest-500
/// window holds 10 hook calls (8 and 2) and 2 blocks (1 and 1), the day holds 9 allowed and 2 blocked decisions, and the alert queue holds one
/// HIGH finding of <c>claudecode</c> and one MEDIUM of <c>hermes</c>.
/// </para>
/// </summary>
internal sealed class OverviewScene : IDisposable
{
    public const int Port = 39871;

    private OverviewScene(TempDirectory temp, AppServices services)
    {
        Temp = temp;
        Services = services;
    }

    public TempDirectory Temp { get; }

    public AppServices Services { get; }

    public static OverviewScene Create(bool twoConnectors = true, bool seedAudit = true, bool seedAgents = true)
    {
        var temp = new TempDirectory();

        _ = temp.WriteFile("config.yaml", ConfigYaml(twoConnectors));
        _ = temp.WriteFile("doctor_cache.json", DoctorCache());
        if (seedAgents)
        {
            _ = temp.WriteFile("ai_discovery_state.json", AgentsJson());
        }

        if (seedAudit)
        {
            SeedAudit(temp.File("audit.db"));
        }

        var services = AppServices.CreateIsolated(TestServices.IsolatedPaths(temp.Path), claudeSettingsPath: temp.File("claude-settings.json"), readerTimeouts: TestServices.ReaderTimeouts);
        return new OverviewScene(temp, services);
    }

    public static string ConfigYaml(bool twoConnectors) =>
        $"gateway:\n  api_port: {Port}\nguardrail:\n  connector: claudecode\n  enabled: true\n  scanner_mode: local\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n" +
        (twoConnectors ? "    hermes:\n      mode: observe\n      hook_fail_mode: open\n      rule_pack_dir: C:\\packs\\strict\n" : string.Empty) +
        "ai_discovery:\n  enabled: true\n  mode: enhanced\n  scan_interval_min: 5\n";

    public static string DoctorCache()
    {
        var captured = DateTimeOffset.UtcNow.AddDays(-17).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return "{\"passed\":42,\"failed\":0,\"warned\":1,\"skipped\":11,\"captured_at\":\"" + captured + "\",\"checks\":[" +
               "{\"status\":\"warn\",\"label\":\"Connector registration\",\"detail\":\"Detected but not configured.\"}]}";
    }

    /// <summary>Ten agent signals (three for one connector, to be deduped), one of them new, one changed, one gone, and twenty local models the card must ignore.</summary>
    public static string AgentsJson()
    {
        var now = DateTimeOffset.UtcNow;
        var agents = new (string Name, string Vendor, string Connector, double Confidence, string State, int SeenMinutesAgo)[]
        {
            ("Claude Code", "Anthropic", "claudecode", 0.98, "seen", 3),
            ("Claude Code", "Anthropic", "claudecode", 0.98, "seen", 4),
            ("Claude Code", "Anthropic", "claudecode", 0.97, "seen", 5),
            ("Codex", "OpenAI", "codex", 0.98, "new", 3),
            ("Cursor", "Anysphere", "cursor", 0.95, "changed", 3),
            ("Antigravity", "Google", "antigravity", 0.95, "seen", 3),
            ("Gemini CLI", "Google", "geminicli", 0.94, "seen", 3),
            ("GitHub Copilot", "GitHub", "copilot", 0.93, "seen", 3),
            ("Hermes Agent", "Nous Research", "hermes", 0.92, "seen", 3),
            ("OpenClaw", "DefenseClaw", "openclaw", 0.91, "seen", 3),
            ("Windsurf", "Codeium", "windsurf", 0.70, "seen", 9),
            ("Qodo", "Qodo", "qodo", 0.76, "gone", 40),
        };

        var signals = string.Join(
            ",",
            agents.Select((a, i) => string.Create(
                CultureInfo.InvariantCulture,
                $"\"sha256:{i}\":{{\"name\":\"{a.Name}\",\"vendor\":\"{a.Vendor}\",\"product\":\"{a.Name}\",\"category\":\"active_process\",\"confidence\":{a.Confidence},\"state\":\"{a.State}\",\"supported_connector\":\"{a.Connector}\",\"last_seen\":\"{now.AddMinutes(-a.SeenMinutesAgo):O}\"}}")));
        var models = string.Join(
            ",",
            Enumerable.Range(0, 20).Select(i => string.Create(
                CultureInfo.InvariantCulture,
                $"\"sha256:m{i}\":{{\"name\":\"model {i}\",\"vendor\":\"Local\",\"category\":\"local_model\",\"confidence\":0.9,\"state\":\"seen\"}}")));
        return $"{{\"version\":2,\"updated_at\":\"{now.AddSeconds(-50):O}\",\"signals\":{{{signals},{models}}}}}";
    }

    /// <summary>A /health body the way 0.8.10 shapes it, with two destinations, and with or without the event-history failure.</summary>
    public static string HealthJson(bool twoConnectors = true, string? eventHistoryFailure = null, bool failureInDetails = true)
    {
        var now = DateTimeOffset.UtcNow;
        string T(double hours) => now.AddHours(hours).ToString("O", CultureInfo.InvariantCulture);

        var claude = "{\"name\":\"claudecode\",\"state\":\"running\",\"source\":\"manual\",\"since\":\"" + T(-5) + "\",\"last_activity_at\":\"" + now.AddMinutes(-3).ToString("O", CultureInfo.InvariantCulture) +
                     "\",\"requests\":3707,\"errors\":0,\"tool_inspections\":1322,\"tool_blocks\":0,\"subprocess_blocks\":0,\"tool_inspection_mode\":\"both\",\"subprocess_policy\":\"shims\"}";
        var hermes = "{\"name\":\"hermes\",\"state\":\"running\",\"source\":\"auto\",\"since\":\"" + T(-5) + "\",\"last_activity_at\":\"" + T(-4) +
                     "\",\"requests\":52,\"errors\":1,\"tool_inspections\":12,\"tool_blocks\":2,\"subprocess_blocks\":1,\"tool_inspection_mode\":\"both\",\"subprocess_policy\":\"shims\"}";
        var connectors = twoConnectors ? "[" + claude + "," + hermes + "]" : "[" + claude + "]";

        var failure = eventHistoryFailure is null ? string.Empty : ",\"event_history_failure\":\"" + eventHistoryFailure + "\"";
        var inDetails = failureInDetails ? failure : string.Empty;
        var besideDetails = failureInDetails ? string.Empty : failure;

        return "{\"started_at\":\"" + T(-4.5) + "\",\"uptime_ms\":16134031," +
               "\"api\":{\"state\":\"running\",\"since\":\"" + T(-4.5) + "\",\"details\":{\"addr\":\"127.0.0.1:18970\"}}," +
               "\"watcher\":{\"state\":\"running\",\"since\":\"" + T(-4.5) + "\",\"details\":{\"skill_dirs\":2,\"plugin_dirs\":2,\"skill_take_action\":true,\"mcp_take_action\":true,\"plugin_take_action\":true}}," +
               "\"telemetry\":{\"state\":\"running\",\"since\":\"" + T(-4.5) + "\"" + besideDetails + ",\"details\":{\"destination_count\":2,\"retention_days\":90," +
               "\"destinations\":[{\"name\":\"local-sqlite\",\"kind\":\"sqlite\",\"enabled\":true,\"state\":\"healthy\",\"signals\":[\"logs\"],\"reason\":\"activated\",\"counters\":{\"accepted\":0,\"delivered\":0}}," +
               "{\"name\":\"splunk-hec\",\"preset\":\"splunk_hec\",\"kind\":\"otlp\",\"enabled\":true,\"state\":\"degraded\",\"signals\":[\"logs\",\"traces\"],\"reason\":\"retrying\",\"counters\":{\"accepted\":120,\"dropped\":2}}]" + inDetails + "}}," +
               "\"sinks\":{\"state\":\"running\",\"details\":{\"sinks\":[{\"name\":\"audit-file\",\"kind\":\"jsonl\",\"enabled\":true}]}}," +
               "\"guardrail\":{\"state\":\"running\",\"since\":\"" + T(-4.5) + "\",\"details\":{\"policy_mode\":\"observe\",\"enforcement_enabled\":false,\"summary\":\"observability-only\"}}," +
               "\"ai_discovery\":{\"state\":\"running\",\"since\":\"" + T(-4.5) + "\",\"details\":{\"mode\":\"enhanced\",\"active_signals\":78,\"last_scan\":\"" + now.AddSeconds(-50).ToString("O", CultureInfo.InvariantCulture) + "\"}}," +
               "\"connector\":" + claude + ",\"connectors\":" + connectors + "," +
               "\"provenance\":{\"schema_version\":7,\"content_hash\":\"x\",\"generation\":0,\"binary_version\":\"0.8.10\"}}";
    }

    public static GatewayHealth Health(bool twoConnectors = true, string? eventHistoryFailure = null, bool failureInDetails = true) =>
        JsonSerializer.Deserialize<GatewayHealth>(HealthJson(twoConnectors, eventHistoryFailure, failureInDetails), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>A running, initialized gateway that reports two connectors (or one).</summary>
    public static GatewaySnapshot Snapshot(bool twoConnectors = true, GatewayHealth? health = null, bool running = true) => new()
    {
        State = running ? AppGatewayState.Running : AppGatewayState.GatewayStopped,
        Install = running ? DefenseClaw.Core.Install.InstallState.Running : DefenseClaw.Core.Install.InstallState.GatewayStopped,
        Detail = "Gateway answering on 127.0.0.1:18970",
        Health = running ? health ?? Health(twoConnectors) : null,
        BinaryVersion = "0.8.10",
        ApiPort = Port,
        ActiveConnectors = twoConnectors ? new[] { "claudecode", "hermes" } : new[] { "claudecode" },
        PolledAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Makes <paramref name="snapshot"/> the monitor's current one and raises its events, as a poll does (the monitor's publish step is private
    /// and is reached here by reflection: no poll loop runs in a test, and the scope and the alert counts follow the monitor).
    /// </summary>
    public void Publish(GatewaySnapshot snapshot) =>
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Services.Monitor, new object[] { snapshot });

    /// <summary>The status --json the Overview reads, for two connectors: one whose hook fail mode has drifted and one that has not.</summary>
    public const string StatusJson = """
        {"environment":"windows","deployment_mode":"","data_dir":"C:\\Users\\example\\.defenseclaw","config":"C:\\Users\\example\\.defenseclaw\\config.yaml","scope":"global user config",
         "sandbox":{"available":false},"scanners":{"skill-scanner":"installed"},
         "enforcement":{"blocked_skills":0,"allowed_skills":3,"blocked_mcps":1,"allowed_mcps":1},
         "activity":{"total_scans":5032,"active_alerts":8392},"sidecar":{"running":true},
         "connectors":[
           {"name":"claudecode","friendly":"Claude Code","mode":"observe","fail_mode":{"effective":"closed","provenance":"claude-env","configured":"open","desired":"open","runtime":"closed","current":false,
             "drift":["registration-stale","windows-sidecar-closed"]},"enabled":true,"source":"manual"},
           {"name":"hermes","friendly":"Hermes","mode":"observe","fail_mode":{"effective":"open","provenance":"config","configured":"open","desired":"open","runtime":"open","current":true,"drift":[]},"enabled":true,"source":"auto"}],
         "application_protection":{"enabled":false,"health_state":"disabled"}}
        """;

    private static void SeedAudit(string path)
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-schema.sql"));
        var hourStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, DateTimeOffset.UtcNow.Hour, 0, 0, TimeSpan.Zero);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();

        // The schema and the rows in one transaction: run statement by statement the ~200 DDL statements are a commit each (a journal file
        // created, synced and deleted), ~2 s a database on a quiet machine against 30 ms this way, with the same sqlite_master.
        using var transaction = connection.BeginTransaction();
        using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = transaction;
            ddl.CommandText = schema;
            _ = ddl.ExecuteNonQuery();
        }

        var n = 0;
        void Add(DateTimeOffset at, string action, string connector, string? details, string severity = "INFO", string? bucket = null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, connector, bucket, event_name)
                VALUES ($id, $ts, $action, '', 'audit_logger', $details, $severity, $connector, $bucket, $event)
                """;
            insert.Parameters.AddWithValue("$id", "row-" + (n++).ToString("D4", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$ts", at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            insert.Parameters.AddWithValue("$action", action);
            insert.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            insert.Parameters.AddWithValue("$severity", severity);
            insert.Parameters.AddWithValue("$connector", connector);
            insert.Parameters.AddWithValue("$bucket", (object?)bucket ?? DBNull.Value);
            insert.Parameters.AddWithValue("$event", bucket == "security.finding" ? "finding.observed" : "x");
            _ = insert.ExecuteNonQuery();
        }

        static string Hook(string decision, string mode = "observe") => $"connector=claudecode result=ok action={decision} raw_action={decision} severity=NONE mode={mode}";

        for (var i = 0; i < 4; i++)
        {
            Add(hourStart.AddMinutes(1 + i), "connector-hook", "claudecode", Hook("allow"));
        }

        for (var i = 0; i < 2; i++)
        {
            Add(hourStart.AddMinutes(10 + i), "connector-hook", "hermes", Hook("allow"));
        }

        for (var i = 0; i < 3; i++)
        {
            Add(hourStart.AddHours(-3).AddMinutes(1 + i), "connector-hook", "claudecode", Hook("allow"));
        }

        Add(hourStart.AddHours(-3).AddMinutes(10), "connector-hook", "claudecode", Hook("block", "action"));
        Add(hourStart.AddHours(-5).AddMinutes(1), "guardrail-block", "hermes", null);

        Add(hourStart.AddHours(-2).AddMinutes(1), "scan-finding", "claudecode", null, severity: "HIGH", bucket: "security.finding");
        Add(hourStart.AddHours(-2).AddMinutes(2), "scan-finding", "hermes", null, severity: "MEDIUM", bucket: "security.finding");
        transaction.Commit();
        SqlitePools.Release(path);
    }

    public void Dispose()
    {
        Services.Dispose();
        SqlitePools.Release(Temp.Path);
        Temp.Dispose();
    }
}
