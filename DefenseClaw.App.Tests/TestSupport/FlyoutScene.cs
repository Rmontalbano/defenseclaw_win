using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// What the tray flyout's tests and renders stand on: a scratch data directory with a config that names two connectors, a real-schema
/// <c>audit.db</c> (<see cref="AlertQueueDatabase"/>) holding synthetic hook calls, blocks and findings, an <see cref="AppServices"/> over it, and
/// a snapshot built by hand (the gateway state "Running" cannot be reached through a poll without a real gateway on the port).
/// </summary>
internal sealed class FlyoutScene : IDisposable
{
    /// <summary>The clock the flyout's tests run on (<see cref="ManualClock"/>'s starting wall time), so "3w ago" is exact.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly RawHttpListener _listener = new(_ => RawHttpListener.Response(200, "{}"));
    private int _sequence;

    public FlyoutScene()
    {
        _ = _temp.WriteFile(
            "config.yaml",
            $"config_version: 8\ngateway:\n  api_port: {_listener.Port}\nguardrail:\n  connectors:\n    claudecode:\n      mode: observe\n    copilot:\n      mode: enforce\n");

        Audit = new AlertQueueDatabase(_temp.File("audit.db"));
        Services = AppServices.CreateIsolated(TestServices.IsolatedPaths(_temp.Path), claudeSettingsPath: _temp.File("claude-settings.json"));
    }

    public AlertQueueDatabase Audit { get; }

    public AppServices Services { get; }

    public string AuditPath => Audit.Path;

    /// <summary>A gateway that is up: 4 days of uptime, two connectors with counters, the version the live install reports.</summary>
    public static GatewaySnapshot RunningSnapshot(long uptimeMs = 4L * 86_400_000) => new()
    {
        State = AppGatewayState.Running,
        Detail = "Gateway is running.",
        Health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":" + uptimeMs.ToString(CultureInfo.InvariantCulture) + "," +
            "\"connectors\":[" +
            "{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":1934,\"tool_blocks\":0,\"subprocess_blocks\":0}," +
            "{\"name\":\"copilot\",\"state\":\"running\",\"requests\":52,\"tool_blocks\":3,\"subprocess_blocks\":1}]}")!,
        HealthStatus = GatewayStatus.Ok,
        BinaryVersion = "0.8.10",
        ApiPort = 18970,
        ActiveConnectors = new[] { "claudecode", "copilot" },
        PolledAt = Now.AddSeconds(-2),
    };

    /// <summary>A hook call: the row the gateway writes for every connector hook, with its decision in <c>details</c>.</summary>
    public void AddHookCall(DateTimeOffset at, string decision = "allow")
    {
        Execute(
            "INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, connector) " +
            "VALUES ($id, $at, 'connector-hook', '', 'audit_logger', $details, 'INFO', 'claudecode')",
            ("$id", NextId("hook")),
            ("$at", AlertQueueDatabase.Format(at)),
            ("$details", $"connector=claudecode result=ok action={decision} raw_action={decision} severity=NONE mode=observe"));
    }

    /// <summary>A row of some other kind (telemetry, a scan): in the window, but neither a hook call nor a block.</summary>
    public void AddOther(DateTimeOffset at, string action = "tool_invocation", string? details = null)
    {
        Execute(
            "INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity) " +
            "VALUES ($id, $at, $action, '', 'audit_logger', $details, 'INFO')",
            ("$id", NextId("other")),
            ("$at", AlertQueueDatabase.Format(at)),
            ("$action", action),
            ("$details", details));
    }

    /// <summary>
    /// A busy window as the live database has it: hook calls (every 10th one blocked), tool invocations and telemetry, newest at
    /// <see cref="Now"/>, then <paramref name="findings"/> unacknowledged findings three weeks old.
    /// </summary>
    public void Populate(int hookCalls = 46, int blocks = 4, int other = 150, int findings = 12)
    {
        for (var i = 0; i < hookCalls; i++)
        {
            AddHookCall(Now.AddSeconds(-30 * i), i < blocks ? "block" : "allow");
        }

        for (var i = 0; i < other; i++)
        {
            AddOther(Now.AddSeconds(-7 * i - 3));
        }

        var severities = new[] { "HIGH", "MEDIUM", "LOW", "CRITICAL", "HIGH" };
        for (var i = 0; i < findings; i++)
        {
            Audit.AddFinding($"finding-{i:D3}", Now.AddDays(-21).AddMinutes(-i), severities[i % severities.Length]);
        }
    }

    /// <summary>
    /// <paramref name="count"/> unacknowledged HIGH findings in one statement (a loop of <see cref="AlertQueueDatabase.AddFinding"/> opens a
    /// connection per row), newest at <paramref name="newest"/>, a second apart.
    /// </summary>
    public void AddFindingsInBulk(int count, DateTimeOffset newest)
    {
        Execute(
            """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < $count - 1)
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, connector, event_name)
            SELECT 'bulk-finding-' || i,
                   strftime('%Y-%m-%dT%H:%M:%S', $newest, '-' || i || ' seconds') || '.0000000Z',
                   'scan-finding', '/synthetic/' || i, 'audit_logger', 'HIGH', 'security.finding', 'claudecode', 'finding.observed'
            FROM n
            """,
            ("$count", count),
            ("$newest", newest.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
    }

    public void Dispose()
    {
        Services.Dispose();
        _listener.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string NextId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _sequence):D5}";

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Audit.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database; values go in as parameters
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        _ = command.ExecuteNonQuery();
    }
}
