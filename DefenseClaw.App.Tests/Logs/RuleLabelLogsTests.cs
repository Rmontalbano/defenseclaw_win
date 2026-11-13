using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The Logs panel's rule-id lines from synthetic rows. A 1.0.9 runtime gets a "Rule meaning" line and, for the shell-command limit, a "Call outcome" line; a 1.0.8
/// runtime shows these rows exactly as the 1.0.8 build did (no new field, the categories as the runtime wrote them).
/// </summary>
public sealed class RuleLabelLogsTests : IDisposable
{
    private const string LimitHook =
        "connector=codex result=ok action=alert raw_action=alert severity=CRITICAL mode=action would_block=false elapsed_ms=40 details_json=\"{}\"";

    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string Database()
    {
        var path = _temp.File("rule-labels.db");
        AuditTestDatabase.Create(path, rows: 0);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();

        void Add(string id, int minute, string action, string eventName, string details, string? payload)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                VALUES ($id, $ts, $action, '', 'gateway', $details, 'CRITICAL', 'guardrail.evaluation', $event, 'codex', 'sidecar', 'logs', $payload)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ts", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$event", eventName);
            command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
            _ = command.ExecuteNonQuery();
        }

        Add("limit", 1, "connector-hook", "legacy.audit.connector.hook", LimitHook, "{\"defenseclaw.guardrail.rule_ids\":[\"ACTION-COMPOUND-LIMIT\",\"CMD-X\"]}");
        Add("tamper", 2, "connector-hook", "legacy.audit.connector.hook", "connector=codex result=ok action=block raw_action=block severity=HIGH mode=action would_block=true details_json=\"{}\"", "{\"defenseclaw.guardrail.rule_ids\":[\"tamper.agent_hook_config_write\"]}");
        Add("finding", 3, "scan-finding", "finding.observed", "scanner=hook-rules rule_id=ACTION-OPERAND-BOUND category=", null);
        return path;
    }

    private async Task<LogsPanelViewModel> PanelAsync(string version)
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(Database()) };
        panel.SetActive(true);
        panel.ActiveSource = "Events";
        await panel.LoadStructuredAsync();
        panel.ActionableOnly = false;
        return panel;
    }

    private static LogEntry Row(LogsPanelViewModel panel, string id) => panel.DisplayedLines.Single(l => l.Fields.Any(f => f.Name == "ID" && f.Value == id));

    private static string? Field(LogEntry entry, string name) => entry.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    [Fact]
    public async Task A_1_0_9_runtime_adds_a_meaning_line_and_the_call_outcome_and_keeps_the_categories_as_written()
    {
        var panel = await PanelAsync("1.0.9");

        var limit = Row(panel, "limit");
        Assert.Equal("ACTION-COMPOUND-LIMIT, CMD-X", Field(limit, "Categories"));
        Assert.Equal(RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"), Field(limit, "Rule meaning"));
        Assert.StartsWith("The call ran; DefenseClaw recorded it as an alert", Field(limit, "Call outcome"), StringComparison.Ordinal);

        var tamper = Row(panel, "tamper");
        Assert.Equal(RuleIdLabels.Sentence("tamper.agent_hook_config_write"), Field(tamper, "Rule meaning"));
        Assert.Null(Field(tamper, "Call outcome"));

        Assert.Equal(RuleIdLabels.Sentence("ACTION-OPERAND-BOUND"), Field(Row(panel, "finding"), "Rule meaning"));
    }

    [Fact]
    public async Task A_1_0_8_runtime_shows_these_rows_without_any_new_field()
    {
        var panel = await PanelAsync("1.0.8");

        foreach (var id in new[] { "limit", "tamper", "finding" })
        {
            var row = Row(panel, id);
            Assert.Null(Field(row, "Rule meaning"));
            Assert.Null(Field(row, "Call outcome"));
            Assert.Null(Field(row, "Rule ID"));
            Assert.Null(Field(row, "Rule note"));
        }

        Assert.Equal("ACTION-COMPOUND-LIMIT, CMD-X", Field(Row(panel, "limit"), "Categories"));
        Assert.Equal("tamper.agent_hook_config_write", Field(Row(panel, "tamper"), "Categories"));
    }
}
