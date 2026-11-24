using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The Logs panel's Events view over one synthetic row of each 1.0.10 guardrail kind (a shadow measurement of each lane, a scan timeout, a canceled inspection, the
/// cascade's degraded row, a hook refusal on the <c>.dropped</c> tail) and the three rule ids whose wording changed. The labels need no runtime gate (a runtime that
/// never writes the rows never shows them); the rule wording does, and a 1.0.9 runtime's rows read exactly as before.
/// </summary>
public sealed class GuardrailRowLogsTests : IDisposable
{
    private const string LimitHook =
        "connector=codex result=ok action=alert raw_action=alert severity=CRITICAL mode=action would_block=false elapsed_ms=40 details_json=\"{}\"";

    private const string DriftHook =
        "connector=codex result=ok action=allow raw_action=allow severity=MEDIUM mode=observe would_block=false elapsed_ms=40 details_json=\"{}\"";

    private const string Dropped =
        "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: \"gateway unreachable\"; see logs/hook-failures.dropped";

    private const string OldLimit = "Shell command too long or too complex for the policy check";
    private const string OldOperand = "More than 1024 operands, so later words were checked only for protected files and links";

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
        var path = _temp.File("guardrail-rows.db");
        AuditTestDatabase.Create(path, rows: 0);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();

        void Add(string id, int minute, string action, string severity, string bucket, string eventName, string target, string details, string? payload = null, string actor = "gateway")
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                VALUES ($id, $ts, $action, $target, $actor, $details, $severity, $bucket, $event, 'codex', 'sidecar', 'logs', $payload)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ts", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$target", target);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$severity", severity);
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$event", eventName);
            command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
            _ = command.ExecuteNonQuery();
        }

        const string inspection = "legacy.audit.guardrail.inspection";
        Add("shadow-effects", 1, "guardrail-inspection", "INFO", "guardrail.evaluation", inspection, "codex",
            "lane=effects-shadow mode=shadow tier=t2 would=block sets=net program=curl kind=exfil rules=R1 reason=policy cost_us=120 effects=3");
        Add("shadow-secret", 2, "guardrail-inspection", "INFO", "guardrail.evaluation", inspection, "secret-flow:shadow",
            "rule=SF-1 mode=shadow kind=token program=curl host=example.test sets=S1 source_digest=abc123 would=block connector=codex");
        Add("timeout-scan", 3, "inspect-tool-timeout", "MEDIUM", "guardrail.evaluation", "legacy.audit.inspect.tool.timeout", "shell",
            "verdict=none reason=scan_timeout elapsed_ms=2001 budget_ms=2000");
        Add("timeout-canceled", 4, "inspect-response-timeout", "MEDIUM", "guardrail.evaluation", "legacy.audit.inspect.response.timeout", "post-response",
            "verdict=none reason=caller_canceled elapsed_ms=40 budget_ms=2000 request_id=req-9");
        Add("cascade", 5, "guardrail-degraded", "HIGH", "platform.health", "subsystem.degraded", string.Empty,
            "guardrail degraded: guardrail.cascade is invalid: unknown layer; the guardrail decides with the built-in order", actor: "guardrail");
        Add("dropped", 6, "guardrail-block", "MEDIUM", "guardrail.evaluation", "legacy.audit.guardrail.block", "codex:PreToolUse", Dropped);
        Add("limit", 7, "connector-hook", "CRITICAL", "guardrail.evaluation", "legacy.audit.connector.hook", "PreToolUse", LimitHook,
            "{\"defenseclaw.guardrail.rule_ids\":[\"ACTION-COMPOUND-LIMIT\",\"CMD-X\"]}");
        Add("operand", 8, "scan-finding", "CRITICAL", "security.finding", "finding.observed", "x", "scanner=hook-rules rule_id=ACTION-OPERAND-BOUND category=");
        Add("drift", 9, "connector-hook", "HIGH", "guardrail.evaluation", "legacy.audit.connector.hook", "PreToolUse", DriftHook,
            "{\"defenseclaw.guardrail.rule_ids\":[\"ALIGNMENT-DRIFT\"]}");
        return path;
    }

    private async Task<LogsPanelViewModel> PanelAsync(string version, bool actionableOnly)
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(Database()) };
        panel.SetActive(true);
        panel.ActiveSource = "Events";
        await panel.LoadStructuredAsync();
        panel.ActionableOnly = actionableOnly;
        return panel;
    }

    private static LogEntry Row(LogsPanelViewModel panel, string id) => panel.DisplayedLines.Single(l => l.Fields.Any(f => f.Name == "ID" && f.Value == id));

    private static string? Field(LogEntry entry, string name) => entry.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    private static string[] Ids(LogsPanelViewModel panel) =>
        panel.DisplayedLines.Select(l => l.Fields.First(f => f.Name == "ID").Value).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("1.0.6")]
    [InlineData("1.0.8")]
    [InlineData("1.0.9")]
    [InlineData("1.0.10")]
    public async Task The_new_row_kinds_are_labelled_on_every_runtime(string version)
    {
        var panel = await PanelAsync(version, actionableOnly: false);

        Assert.Equal(
            "What the effects lane would have done (would=block). The call was not changed.",
            Field(Row(panel, "shadow-effects"), "Shadow measurement"));
        Assert.Equal(
            "What secret flow tracking would have done (would=block). The call was not changed.",
            Field(Row(panel, "shadow-secret"), "Shadow measurement"));
        Assert.Equal("Inspection timed out, call decided without it", Field(Row(panel, "timeout-scan"), "Inspection timeout"));
        Assert.Equal(
            "Inspection canceled by the caller, call decided without it",
            Field(Row(panel, "timeout-canceled"), "Inspection timeout"));

        var cascade = Row(panel, "cascade");
        Assert.Equal("Guardrail cascade not applied as written", Field(cascade, "Guardrail cascade"));
        Assert.Contains("guardrail.cascade is invalid: unknown layer", Field(cascade, "Runtime says"), StringComparison.Ordinal);

        // The other rows carry none of them.
        Assert.Null(Field(Row(panel, "limit"), "Shadow measurement"));
        Assert.Null(Field(Row(panel, "dropped"), "Inspection timeout"));
        Assert.Null(Field(Row(panel, "dropped"), "Guardrail cascade"));
    }

    [Fact]
    public async Task With_Actionable_only_on_the_shadow_rows_and_a_canceled_inspection_are_hidden_and_a_scan_timeout_is_shown()
    {
        var panel = await PanelAsync("1.0.10", actionableOnly: true);

        Assert.DoesNotContain("shadow-effects", Ids(panel));
        Assert.DoesNotContain("shadow-secret", Ids(panel));
        Assert.DoesNotContain("timeout-canceled", Ids(panel));
        Assert.Contains("timeout-scan", Ids(panel));
        Assert.Contains("cascade", Ids(panel));
        Assert.Contains("dropped", Ids(panel));

        panel.ActionableOnly = false;
        Assert.Contains("shadow-effects", Ids(panel));
        Assert.Contains("shadow-secret", Ids(panel));
        Assert.Contains("timeout-canceled", Ids(panel));
    }

    [Fact]
    public async Task A_dropped_tail_refusal_gets_its_own_explanation_on_a_runtime_that_labels_refusals()
    {
        var panel = await PanelAsync("1.0.9", actionableOnly: false);

        var row = Row(panel, "dropped");
        Assert.Equal("Hook (gateway unreachable)", Field(row, "Decided by"));
        Assert.EndsWith(
            " The usual record could not be written, so the call is logged in logs\\hook-failures.dropped.",
            Field(row, "Explanation"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_1_0_10_runtime_words_the_over_limit_rule_for_every_surface_and_labels_the_alignment_check()
    {
        var panel = await PanelAsync("1.0.10", actionableOnly: false);

        var limit = Row(panel, "limit");
        Assert.Equal(
            "Input too large or complex for the policy check, judged at the unknown tier",
            Field(limit, "Rule meaning"));
        Assert.Equal("It ran and was recorded as an alert. The strict rule pack blocks it.", Field(limit, "Call outcome"));

        var drift = Row(panel, "drift");
        Assert.Equal("Alignment check: this tool call does not serve the user's task", Field(drift, "Rule meaning"));
        Assert.Equal(
            "Measured only. The call was not changed.",
            Field(drift, "Call outcome"));

        Assert.Equal(OldOperand, Field(Row(panel, "operand"), "Rule meaning"));
    }

    [Fact]
    public async Task A_1_0_9_runtime_reads_these_three_rule_ids_exactly_as_before()
    {
        var panel = await PanelAsync("1.0.9", actionableOnly: false);

        var limit = Row(panel, "limit");
        Assert.Equal(OldLimit, Field(limit, "Rule meaning"));
        Assert.Equal("The call ran and was recorded as an alert. The strict rule pack blocks it.", Field(limit, "Call outcome"));
        Assert.Equal("ACTION-COMPOUND-LIMIT, CMD-X", Field(limit, "Categories"));

        Assert.Equal(OldOperand, Field(Row(panel, "operand"), "Rule meaning"));

        var drift = Row(panel, "drift");
        Assert.Null(Field(drift, "Rule meaning"));
        Assert.Null(Field(drift, "Call outcome"));
        Assert.Equal("ALIGNMENT-DRIFT", Field(drift, "Categories"));
    }
}
