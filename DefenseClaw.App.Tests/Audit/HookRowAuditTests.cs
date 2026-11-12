using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel's hook-row labels (CUST-449): a hook's own refusal, the judge-unavailable id, a judge fallback and a Claude Cowork call (its marker on the
/// target and its filter), from synthetic rows in the shapes the 1.0.7 and 1.0.8 gateways write. A 1.0.8 runtime shows them; a 1.0.6 runtime shows the same rows
/// exactly as before and offers no Cowork choice.
/// </summary>
public sealed class HookRowAuditTests : IDisposable
{
    private const string Tail = "; see logs/hook-failures.jsonl";
    private const string Refusal = "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: \"gateway unreachable\"" + Tail;
    private const string Session = "00000000-0000-4000-8000-000000000001";

    private const string CoworkDetails =
        "connector=claudecode result=ok action=allow severity=LOW mode=action reason=x would_block=false elapsed_ms=812 judge_fallback=timeout " +
        "cowork_session_id=" + Session + " cowork_tool_class=shell prompt_id=prompt-1 surface=cowork details_json=\"{}\"";

    private const string CoworkEnvelope =
        "{\"schema\":\"s\",\"connector\":\"claudecode\",\"event\":\"PreToolUse\",\"result\":\"ok\",\"action\":\"allow\",\"would_block\":false," +
        "\"extra\":{\"surface\":\"cowork\",\"cowork_session_id\":\"" + Session + "\",\"prompt_id\":\"prompt-1\",\"cowork_tool_class\":\"shell\",\"judge_fallback\":\"timeout\"}}";

    private const string PlainDetails =
        "connector=claudecode result=ok action=allow severity=LOW mode=action reason=x would_block=false elapsed_ms=40 judge_fallback=error details_json=\"{}\"";

    private const string PlainEnvelope =
        "{\"schema\":\"s\",\"connector\":\"claudecode\",\"event\":\"PreToolUse\",\"result\":\"ok\",\"action\":\"allow\",\"extra\":{\"judge_fallback\":\"error\"}}";

    private const string EscalatedDetails =
        "connector=claudecode result=ok action=block severity=CRITICAL mode=action reason=\"judge-unavailable: a CRITICAL finding the rules cannot prove needs the LLM judge, " +
        "which gave no verdict; hook_fail_mode is closed\" would_block=true elapsed_ms=90";

    private const string EscalatedEnvelope =
        "{\"schema\":\"s\",\"connector\":\"claudecode\",\"event\":\"PreToolUse\",\"result\":\"ok\",\"action\":\"block\",\"rule_ids\":[\"llm-judge:JUDGE-UNAVAILABLE-ESCALATED\",\"CMD-X\"]}";

    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private async Task<AuditPanelViewModel> OpenAsync(string version, bool withOldCoworkRow = false)
    {
        AuditTestDatabase.Create(DbPath, 0, newest: _clock.GetUtcNow());
        void Add(string id, double secondsAgo, string action, string severity, string? connector, string eventName, string target, string? details, string? structured = null) =>
            CorrelatedRows.Add(
                DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, severity, connector, "guardrail.evaluation", eventName, target, "gateway", details, structuredJson: structured);

        Add("refused", 1, "guardrail-block", "MEDIUM", "claudecode", "legacy.audit.guardrail.block", "claudecode:PreToolUse", Refusal);
        Add("policy", 2, "guardrail-block", "HIGH", "claudecode", "legacy.audit.guardrail.block", "claudecode:PreToolUse", "rule=CMD-X action=block");
        Add("cowork", 3, "connector-hook", "INFO", "claudecode", "legacy.audit.connector.hook", "PreToolUse", CoworkDetails, CoworkEnvelope);
        Add("plain", 4, "connector-hook", "INFO", "claudecode", "legacy.audit.connector.hook", "PreToolUse", PlainDetails, PlainEnvelope);
        Add("escalated", 5, "connector-hook", "CRITICAL", "claudecode", "legacy.audit.connector.hook", "PreToolUse", EscalatedDetails, EscalatedEnvelope);
        Add("finding", 6, "scan-finding", "CRITICAL", "claudecode", "finding.observed", "claudecode", "scanner=hook-rules rule_id=JUDGE-UNAVAILABLE-ESCALATED category=");
        Add("codex", 7, "connector-hook", "INFO", "codex", "legacy.audit.connector.hook", "PreToolUse", CoworkDetails.Replace("claudecode", "codex", StringComparison.Ordinal), CoworkEnvelope.Replace("claudecode", "codex", StringComparison.Ordinal));

        if (withOldCoworkRow)
        {
            Add("cowork-old", 10 * 86400, "connector-hook", "INFO", "claudecode", "legacy.audit.connector.hook", "PreToolUse", CoworkDetails, CoworkEnvelope);
        }

        _services = TestServices.Create(_temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false };
        await panel.InitializeAsync();
        return panel;
    }

    private static AuditRow Row(AuditPanelViewModel panel, string id) => panel.Rows.Single(r => r.Id == id);

    private static string? Pair(AuditRow row, string label) => row.DetailPairs.FirstOrDefault(p => p.Label == label)?.Value;

    // ------------------------------------------------------------------ a 1.0.8 runtime

    [Fact]
    public async Task A_hook_refusal_is_titled_for_what_the_hook_did_and_says_who_decided()
    {
        var panel = await OpenAsync("1.0.8");
        var refused = Row(panel, "refused");

        Assert.StartsWith("Hook blocked the call itself", refused.Title, StringComparison.Ordinal);
        Assert.Equal(HookFailureRow.SourceLabel, Pair(refused, "Decided by"));
        Assert.Equal("gateway unreachable", Pair(refused, "Hook's reason"));
        Assert.Contains("hook_fail_mode is closed", Pair(refused, "Explanation"), StringComparison.Ordinal);

        // The action column is the row's own, and a policy's block is what it was.
        Assert.Equal("guardrail-block", refused.Action);
        var policy = Row(panel, "policy");
        Assert.Equal("guardrail-block", policy.Title);
        Assert.Null(Pair(policy, "Decided by"));
    }

    [Fact]
    public async Task The_judge_unavailable_id_carries_its_label_and_a_note_in_the_inspector()
    {
        var panel = await OpenAsync("1.0.8");

        var escalated = Row(panel, "escalated");
        Assert.Contains("JUDGE-UNAVAILABLE-ESCALATED (", Pair(escalated, "Rule IDs"), StringComparison.Ordinal);
        Assert.Contains("CMD-X", Pair(escalated, "Rule IDs"), StringComparison.Ordinal);
        Assert.Contains("regex_judge", Pair(escalated, "Rule note"), StringComparison.Ordinal);

        var finding = Row(panel, "finding");
        Assert.Contains("JUDGE-UNAVAILABLE-ESCALATED (", Pair(finding, "Rule Id"), StringComparison.Ordinal);
        Assert.NotNull(Pair(finding, "Rule note"));
    }

    [Fact]
    public async Task A_judge_fallback_is_a_labelled_field_and_a_note()
    {
        var panel = await OpenAsync("1.0.8");

        var plain = Row(panel, "plain");
        Assert.Equal("judge call failed", Pair(plain, "Judge fallback"));
        Assert.Contains("rules decided", Pair(plain, "Fallback note"), StringComparison.Ordinal);
        Assert.Null(Pair(plain, "Judge Fallback"));
    }

    [Fact]
    public async Task A_cowork_call_reads_cowork_in_its_target_and_lists_its_facts()
    {
        var panel = await OpenAsync("1.0.8");
        var cowork = Row(panel, "cowork");

        Assert.True(cowork.IsCowork);
        Assert.Equal("claudecode · cowork:PreToolUse", cowork.TargetText);
        Assert.Equal("claudecode cowork:PreToolUse", cowork.Title);
        Assert.Equal("PreToolUse", cowork.Target);
        Assert.Equal("claudecode", cowork.Connector);
        Assert.Equal(Session, Pair(cowork, "Cowork session"));
        Assert.Equal("shell (Cowork VM) (shell)", Pair(cowork, "Cowork tool"));
        Assert.Equal("prompt-1", Pair(cowork, "Cowork prompt"));
        Assert.Equal(1, cowork.DetailPairs.Count(p => p.Label == "Surface"));
        Assert.StartsWith("Cowork", Pair(cowork, "Surface"), StringComparison.Ordinal);

        var plain = Row(panel, "plain");
        Assert.False(plain.IsCowork);
        Assert.Equal("claudecode · PreToolUse", plain.TargetText);
    }

    [Fact]
    public async Task The_cowork_choice_is_one_more_option_beside_claudecode_and_narrows_to_its_cowork_rows()
    {
        var panel = await OpenAsync("1.0.8");

        var option = Assert.Single(panel.Connectors, c => c.CoworkOnly);
        Assert.Equal("claudecode: Cowork only (last 7 days at most)", option.Label);
        Assert.Equal("claudecode", option.Connector);

        // Cowork is no connector: the connector choices are the ones the database has, and nothing else is named cowork.
        Assert.DoesNotContain(panel.Connectors, c => !c.CoworkOnly && string.Equals(c.Connector, "cowork", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, panel.Connectors.Count(c => c is { CoworkOnly: false, IncludeNull: false, PlatformOnly: false } && c.Connector == "claudecode"));

        panel.SelectedConnector = option;
        await panel.LastLoad;

        Assert.Equal(["cowork"], panel.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task The_cowork_choice_never_runs_without_a_lower_bound_even_under_all_time()
    {
        var panel = await OpenAsync("1.0.8", withOldCoworkRow: true);
        panel.SelectedRange = TimeRangeOption.AllTime;
        await panel.LastLoad;

        // All time, plain claudecode: the ten-day-old Cowork row is listed.
        panel.SelectedConnector = panel.Connectors.First(c => c is { CoworkOnly: false, IncludeNull: false, PlatformOnly: false } && c.Connector == "claudecode");
        await panel.LastLoad;
        Assert.Contains(panel.Rows, r => r.Id == "cowork-old");

        // All time, Cowork only: it starts seven days back, so the old row is not read, and the option's label says so.
        panel.SelectedConnector = ConnectorOption.CoworkOnlyOption;
        await panel.LastLoad;
        Assert.Equal(["cowork"], panel.Rows.Select(r => r.Id));
        Assert.Contains("7 days", ConnectorOption.CoworkOnlyOption.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_plain_claudecode_again_lists_every_claudecode_row()
    {
        var panel = await OpenAsync("1.0.8");

        panel.SelectedConnector = ConnectorOption.CoworkOnlyOption;
        await panel.LastLoad;
        panel.SelectedConnector = panel.Connectors.First(c => c is { CoworkOnly: false, IncludeNull: false, PlatformOnly: false } && c.Connector == "claudecode");
        await panel.LastLoad;

        // Back on plain claudecode every claudecode row is listed again.
        Assert.Equal(6, panel.Rows.Count);
    }

    // ------------------------------------------------------------------ a 1.0.6 runtime

    [Fact]
    public async Task A_1_0_6_runtime_shows_the_same_rows_exactly_as_before_and_offers_no_cowork_choice()
    {
        var panel = await OpenAsync("1.0.6");

        var refused = Row(panel, "refused");
        Assert.Equal("guardrail-block", refused.Title);
        Assert.Null(Pair(refused, "Decided by"));
        Assert.Null(Pair(refused, "Explanation"));

        var cowork = Row(panel, "cowork");
        Assert.False(cowork.IsCowork);
        Assert.Equal("claudecode · PreToolUse", cowork.TargetText);
        Assert.Equal("claudecode PreToolUse", cowork.Title);
        Assert.Null(Pair(cowork, "Cowork session"));
        Assert.Null(Pair(cowork, "Judge fallback"));
        Assert.Equal("cowork", Pair(cowork, "Surface"));

        var escalated = Row(panel, "escalated");
        Assert.Null(Pair(escalated, "Rule IDs"));
        Assert.Null(Pair(escalated, "Rule note"));
        Assert.DoesNotContain("(", Pair(Row(panel, "finding"), "Rule Id"), StringComparison.Ordinal);

        Assert.DoesNotContain(panel.Connectors, c => c.CoworkOnly);
    }
}
