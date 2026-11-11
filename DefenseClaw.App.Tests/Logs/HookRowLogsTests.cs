using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The Logs panel's hook-row labels (CUST-449): a hook's own refusal, the judge-unavailable id, a judge fallback and a Claude Cowork call, from synthetic rows in
/// the shapes the 1.0.7 and 1.0.8 gateways write. A 1.0.8 runtime shows the labels, the marker and the <c>surface:cowork</c> filter; a 1.0.6 runtime shows the
/// same rows exactly as before.
/// </summary>
public sealed class HookRowLogsTests : IDisposable
{
    private const string Tail = "; see logs/hook-failures.jsonl";
    private const string Refusal = "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: \"gateway unreachable\"" + Tail;
    private const string Session = "00000000-0000-4000-8000-000000000001";

    private const string CoworkHook =
        "connector=claudecode result=ok action=allow severity=LOW mode=action reason=x would_block=false elapsed_ms=812 judge_fallback=timeout " +
        "cowork_session_id=" + Session + " cowork_tool_class=shell prompt_id=prompt-1 surface=cowork details_json=\"{}\"";

    private const string PlainHook =
        "connector=claudecode result=ok action=allow severity=LOW mode=action reason=x would_block=false elapsed_ms=40 judge_fallback=error details_json=\"{}\"";

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
        var path = _temp.File("hook-rows.db");
        if (File.Exists(path))
        {
            // A second panel in one test reads the same rows.
            return path;
        }

        AuditTestDatabase.Create(path, rows: 0);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();

        void Add(string id, int minute, string action, string severity, string bucket, string eventName, string connector, string? details, string? payload = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                VALUES ($id, $ts, $action, '', 'gateway', $details, $severity, $bucket, $event, $connector, 'sidecar', 'logs', $payload)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ts", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            command.Parameters.AddWithValue("$severity", severity);
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$event", eventName);
            command.Parameters.AddWithValue("$connector", connector);
            command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
            _ = command.ExecuteNonQuery();
        }

        Add("refused", 1, "guardrail-block", "MEDIUM", "guardrail.evaluation", "legacy.audit.guardrail.block", "claudecode", Refusal);
        Add("policy", 2, "guardrail-block", "HIGH", "guardrail.evaluation", "legacy.audit.guardrail.block", "claudecode", "rule=CMD-X action=block");
        Add("cowork", 3, "connector-hook", "INFO", "guardrail.evaluation", "legacy.audit.connector.hook", "claudecode", CoworkHook);
        Add("plain", 4, "connector-hook", "INFO", "guardrail.evaluation", "legacy.audit.connector.hook", "claudecode", PlainHook);
        Add("finding", 5, "scan-finding", "CRITICAL", "security.finding", "finding.observed", "claudecode", "scanner=hook-rules rule_id=JUDGE-UNAVAILABLE-ESCALATED category=");
        Add(
            "ids", 6, "act", "HIGH", "guardrail.evaluation", "guardrail.evaluated", "claudecode", "decided",
            "{\"defenseclaw.guardrail.rule_ids\":[\"JUDGE-UNAVAILABLE-ESCALATED\",\"CMD-X\"]}");
        Add("cowork-decision", 7, "act", "HIGH", "guardrail.evaluation", "guardrail.evaluated", "claudecode", "decided on a Cowork call", "{\"defenseclaw.agent.surface\":\"cowork\"}");
        Add("codex", 8, "connector-hook", "INFO", "guardrail.evaluation", "legacy.audit.connector.hook", "codex", CoworkHook.Replace("claudecode", "codex", StringComparison.Ordinal));
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

    // ------------------------------------------------------------------ a 1.0.8 runtime

    [Fact]
    public async Task A_hook_refusal_is_titled_for_what_the_hook_did_and_says_who_decided()
    {
        var panel = await PanelAsync("1.0.8");
        var row = Row(panel, "refused");

        Assert.StartsWith("legacy.audit.guardrail.block Hook blocked the call itself", row.Message, StringComparison.Ordinal);
        Assert.Contains("gateway unreachable", row.Message, StringComparison.Ordinal);
        Assert.Equal(HookFailureRow.SourceLabel, Field(row, "Decided by"));
        Assert.Equal("gateway unreachable", Field(row, "Hook's reason"));
        Assert.Contains("hook_fail_mode is closed", Field(row, "Explanation"), StringComparison.Ordinal);

        // A policy's own block is still what it was.
        var policy = Row(panel, "policy");
        Assert.DoesNotContain("Hook blocked", policy.Message, StringComparison.Ordinal);
        Assert.Null(Field(policy, "Decided by"));
    }

    [Fact]
    public async Task The_labels_do_not_change_whether_a_row_is_actionable()
    {
        var on = await PanelAsync("1.0.8");
        var labelled = on.DisplayedLines.ToDictionary(l => Field(l, "ID")!, l => l.IsActionable);
        _services!.Dispose();
        _services = null;

        var off = await PanelAsync("1.0.6");

        foreach (var line in off.DisplayedLines)
        {
            Assert.Equal(line.IsActionable, labelled[Field(line, "ID")!]);
        }
    }

    [Fact]
    public async Task The_judge_unavailable_id_carries_its_label_and_a_note_wherever_the_inspector_shows_a_rule_id()
    {
        var panel = await PanelAsync("1.0.8");

        var finding = Row(panel, "finding");
        Assert.Contains("JUDGE-UNAVAILABLE-ESCALATED (", Field(finding, "Rule ID"), StringComparison.Ordinal);
        Assert.Contains("regex_judge", Field(finding, "Rule note"), StringComparison.Ordinal);

        var ids = Row(panel, "ids");
        var categories = Field(ids, "Categories")!;
        Assert.Contains("JUDGE-UNAVAILABLE-ESCALATED (", categories, StringComparison.Ordinal);
        Assert.EndsWith("CMD-X", categories, StringComparison.Ordinal);
        Assert.NotNull(Field(ids, "Rule note"));
    }

    [Fact]
    public async Task A_judge_fallback_on_a_hook_row_is_a_labelled_field_and_a_note()
    {
        var panel = await PanelAsync("1.0.8");

        var plain = Row(panel, "plain");
        Assert.Equal("judge call failed", Field(plain, "Judge fallback"));
        Assert.Contains("rules decided", Field(plain, "Fallback note"), StringComparison.Ordinal);

        // The hook's own "Judge Fallback: error" row is not shown a second time.
        Assert.DoesNotContain(plain.Fields, f => f.Name == "Judge Fallback");
    }

    [Fact]
    public async Task A_cowork_call_is_marked_and_its_facts_listed_and_the_connector_stays_claudecode()
    {
        var panel = await PanelAsync("1.0.8");

        var cowork = Row(panel, "cowork");
        Assert.True(cowork.IsCowork);
        Assert.StartsWith("Cowork: ", cowork.Message, StringComparison.Ordinal);
        Assert.Equal(Session, Field(cowork, "Cowork session"));
        Assert.Equal("shell (Cowork VM) (shell)", Field(cowork, "Cowork tool"));
        Assert.Equal("prompt-1", Field(cowork, "Cowork prompt"));
        Assert.Equal("claudecode", cowork.Connector);

        Assert.True(Row(panel, "cowork-decision").IsCowork);
        Assert.False(Row(panel, "plain").IsCowork);
        Assert.False(Row(panel, "codex").IsCowork);
    }

    [Fact]
    public async Task The_surface_cowork_token_keeps_only_cowork_rows_and_combines_with_a_connector()
    {
        var panel = await PanelAsync("1.0.8");

        panel.FilterText = "surface:cowork";
        Assert.Equal(["cowork", "cowork-decision"], panel.DisplayedLines.Select(l => Field(l, "ID")!).Order(StringComparer.Ordinal));

        panel.FilterText = "connector:claudecode surface:cowork";
        Assert.Equal(2, panel.DisplayedLines.Count);

        panel.FilterText = "connector:codex surface:cowork";
        Assert.Empty(panel.DisplayedLines);

        panel.FilterText = string.Empty;
        Assert.Equal(8, panel.DisplayedLines.Count);
    }

    // ------------------------------------------------------------------ a 1.0.6 runtime

    [Fact]
    public async Task A_1_0_6_runtime_shows_the_same_rows_exactly_as_before()
    {
        var panel = await PanelAsync("1.0.6");

        var refused = Row(panel, "refused");
        Assert.DoesNotContain("Hook blocked", refused.Message, StringComparison.Ordinal);
        Assert.Null(Field(refused, "Decided by"));
        Assert.Null(Field(refused, "Explanation"));

        var cowork = Row(panel, "cowork");
        Assert.False(cowork.IsCowork);
        Assert.DoesNotContain("Cowork", cowork.Message, StringComparison.Ordinal);
        Assert.Null(Field(cowork, "Cowork session"));
        Assert.Null(Field(cowork, "Judge fallback"));
        Assert.Null(Field(cowork, "Fallback note"));

        var finding = Row(panel, "finding");
        Assert.Null(Field(finding, "Rule ID"));
        Assert.Null(Field(finding, "Rule note"));
        Assert.Equal("JUDGE-UNAVAILABLE-ESCALATED, CMD-X", Field(Row(panel, "ids"), "Categories"));
    }

    [Fact]
    public async Task A_1_0_6_runtime_does_not_honour_the_surface_token()
    {
        var panel = await PanelAsync("1.0.6");

        // On such a runtime it is free text, as any unknown token is: no row says it, so none is listed.
        panel.FilterText = "surface:cowork";
        Assert.Empty(panel.DisplayedLines);
    }
}
