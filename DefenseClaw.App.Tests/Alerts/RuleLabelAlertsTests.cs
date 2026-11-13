using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The rule-id sentences, the shell-command limit outcome, the 1.0.9 "alert" decision and the Cowork mark of a runtime-only row in the Alerts panel, at the row level
/// (synthetic <c>alerts --json</c> rows). A runtime without these ids or keys gets rows exactly as before.
/// </summary>
public sealed class RuleLabelAlertsTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private static readonly HookRowGate Gate109 = HookRowGate.All;

    private static readonly HookRowGate Gate108 = new(true, true, true, true);

    private static RuntimeAlertRow Runtime(string extra, string id = "r-1", string action = "connector-hook", string details = "connector=codex action=alert raw_action=alert mode=action would_block=false")
    {
        var json = "[{\"id\":\"" + id + "\",\"timestamp\":\"" + Base.ToString("O") + "\",\"severity\":\"CRITICAL\",\"action\":\"" + action + "\",\"target\":\"claudecode:PreToolUse\"," +
            "\"actor\":\"gateway\",\"connector\":\"claudecode\",\"details\":\"" + details + "\"" + extra + "}]";
        return RuntimeAlertSnapshotParser.Parse(json).Rows.Single();
    }

    private static AlertItem Audited(string id, string structured, string details = "connector=claudecode result=ok action=allow severity=LOW mode=action would_block=false") =>
        AlertItem.FromAudit(new AuditEvent
        {
            Id = id,
            Timestamp = Base,
            RawTimestamp = AlertQueueDatabase.Format(Base),
            Action = "connector-hook",
            Target = "PreToolUse",
            Actor = "gateway",
            Details = details,
            Severity = "HIGH",
            Connector = "claudecode",
            StructuredJsonRaw = structured,
        });

    // ------------------------------------------------------------------ rule ids

    [Fact]
    public void A_runtime_only_row_headlines_the_sentence_of_a_known_rule_and_keeps_the_raw_fact()
    {
        var row = Runtime(",\"rule\":\"ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit\",\"decision\":\"alert\"");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.Equal("ACTION-COMPOUND-LIMIT", item.RuleId);
        Assert.Equal(RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"), item.Headline);
        Assert.Contains(item.Fields, f => f.Name == "runtime.rule" && f.Value.StartsWith("ACTION-COMPOUND-LIMIT: Shell command exceeds", StringComparison.Ordinal));
        Assert.Contains("The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", item.HookRowNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_limit_row_says_it_was_blocked()
    {
        var row = Runtime(",\"rule\":\"ACTION-COMPOUND-LIMIT: x\",\"decision\":\"blocked\"", details: "connector=codex action=block raw_action=block mode=action would_block=true");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.Contains("The call was blocked.", item.HookRowNote, StringComparison.Ordinal);
        Assert.DoesNotContain("The call ran", item.HookRowNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_row_without_decision_or_action_fields_gets_only_the_label()
    {
        var row = Runtime(",\"rule\":\"ACTION-COMPOUND-LIMIT: x\"", details: "something else");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.Equal(RuleIdLabels.Sentence("ACTION-COMPOUND-LIMIT"), item.Headline);
        Assert.Equal(string.Empty, item.HookRowNote);
    }

    [Fact]
    public void Another_known_rule_gets_its_sentence_and_no_outcome_note()
    {
        var row = Runtime(",\"rule\":\"tamper.agent_hook_config_disable: Turn off the agent's DefenseClaw hooks\",\"decision\":\"blocked\"", details: "connector=claudecode action=block");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.Equal("An edit that turns off the agent's DefenseClaw hooks in its settings.", item.Headline);
        Assert.Equal(string.Empty, item.HookRowNote);
    }

    [Fact]
    public void An_unmapped_rule_keeps_the_runtimes_own_headline()
    {
        var row = Runtime(",\"rule\":\"CMD-X: Something the CLI titled\",\"decision\":\"alert\"");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.Equal("CMD-X: Something the CLI titled", item.Headline);
        Assert.Equal(string.Empty, item.HookRowNote);
    }

    // ------------------------------------------------------------------ decision alert

    [Fact]
    public void An_alert_decision_on_a_hook_row_reads_as_ran_and_recorded_only_with_the_1_0_9_gate()
    {
        var row = Runtime(",\"rule\":\"CMD-X: t\",\"decision\":\"alert\"");

        var newer = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);
        Assert.Contains(newer.HookRowFacts, f => f.Name == "Call outcome" && f.Value == "Ran; recorded as an alert");

        var older = AlertItem.FromRuntime(row).WithHookRows(Gate108, row);
        Assert.DoesNotContain(older.HookRowFacts, f => f.Name == "Call outcome");

        var allowRow = Runtime(",\"decision\":\"allow\"");
        Assert.DoesNotContain(AlertItem.FromRuntime(allowRow).WithHookRows(Gate109, allowRow).HookRowFacts, f => f.Name == "Call outcome");
    }

    // ------------------------------------------------------------------ cowork by surface

    [Fact]
    public void A_runtime_only_row_with_the_cowork_surface_is_marked_and_shown_as_cowork_with_its_stored_target_kept()
    {
        var row = Runtime(",\"surface\":\"cowork\"");
        var item = AlertItem.FromRuntime(row).WithHookRows(Gate109, row);

        Assert.True(item.IsCowork);
        Assert.Equal("cowork:PreToolUse", item.TargetRef);
        Assert.Equal("claudecode:PreToolUse", item.RawTarget);
        Assert.Equal("claudecode", item.Connector);
        Assert.Single(item.HookRowFacts, f => f.Name == "Surface");
    }

    [Fact]
    public void A_row_without_surface_is_not_marked_on_any_gate()
    {
        var row = Runtime(string.Empty);
        foreach (var gate in new[] { Gate108, Gate109 })
        {
            var item = AlertItem.FromRuntime(row).WithHookRows(gate, row);
            Assert.False(item.IsCowork);
            Assert.Equal("claudecode:PreToolUse", item.TargetRef);
        }

        var empty = Runtime(",\"surface\":\"\"");
        Assert.False(AlertItem.FromRuntime(empty).WithHookRows(Gate109, empty).IsCowork);
    }

    [Fact]
    public void The_cowork_mark_needs_the_runtime_to_know_cowork()
    {
        var row = Runtime(",\"surface\":\"cowork\"");
        Assert.False(AlertItem.FromRuntime(row).WithHookRows(HookRowGate.None, row).IsCowork);
    }

    [Fact]
    public void A_row_both_sources_return_is_marked_once()
    {
        const string envelope = "{\"connector\":\"claudecode\",\"event\":\"PreToolUse\",\"extra\":{\"surface\":\"cowork\",\"cowork_session_id\":\"s-1\"}}";
        var row = Runtime(",\"surface\":\"cowork\"", id: "h-1");
        var audited = Audited("h-1", envelope);

        var merged = audited.WithRuntime(row).WithHookRows(Gate109, row);
        Assert.True(merged.IsCowork);
        Assert.Single(merged.HookRowFacts, f => f.Name == "Surface");
        Assert.Single(merged.HookRowFacts, f => f.Name == "Cowork session");
        Assert.Equal("cowork:PreToolUse", merged.TargetRef);

        // Labelled again (a rebuild) it is still one mark, one target.
        var again = merged.WithHookRows(Gate109, row);
        Assert.Single(again.HookRowFacts, f => f.Name == "Surface");
        Assert.Equal("cowork:PreToolUse", again.TargetRef);

        // An audit row with no envelope falls to the runtime's surface, once.
        var plain = Audited("h-2", "{}", details: "connector=claudecode result=ok action=allow");
        var plainRow = Runtime(",\"surface\":\"cowork\"", id: "h-2");
        var viaSurface = plain.WithRuntime(plainRow).WithHookRows(Gate109, plainRow);
        Assert.True(viaSurface.IsCowork);
        Assert.Single(viaSurface.HookRowFacts, f => f.Name == "Surface");
    }
}
