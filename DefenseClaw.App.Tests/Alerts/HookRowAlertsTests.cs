using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The hook-row labels of DefenseClaw 1.0.7 and 1.0.8 in the Alerts panel (CUST-449), from synthetic rows in the shape the gateway writes: a hook's own refusal
/// is no policy block, the judge-unavailable finding reads as what it is, <c>judge_fallback</c> and a Cowork call are named, and <c>surface:cowork</c> narrows the
/// list. A 1.0.6 runtime gets none of it. The rows reach the list through the real queue and audit reads; the refusal row, which the queue never holds (it is
/// in Logs and Audit), is tested at the row level.
/// </summary>
public sealed class HookRowAlertsTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private const string Session = "00000000-0000-4000-8000-000000000001";

    private const string Refusal =
        "hook blocked the call itself (fail-closed, PreToolUse): the agent got this block, not a gateway decision recorded for the call: " +
        "\"gateway unreachable: dial tcp 127.0.0.1:18970: connect refused\"; see logs/hook-failures.jsonl";

    private const string CoworkEnvelope =
        "{\"connector\":\"claudecode\",\"event\":\"PreToolUse\",\"result\":\"ok\",\"action\":\"allow\",\"extra\":" +
        "{\"surface\":\"cowork\",\"cowork_session_id\":\"" + Session + "\",\"prompt_id\":\"prompt-1\",\"cowork_tool_class\":\"shell\"}}";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private async Task<AppServices> CreateAsync(string version)
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        _ = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
        return _services;
    }

    private void Seed()
    {
        var db = _services!.Paths.AuditDatabasePath;
        CorrelatedRows.Add(
            db, "j-1", Base.AddMinutes(1), "scan-finding", "CRITICAL", "claudecode", target: "/synthetic/j-1",
            details: "scanner=hook-rules rule_id=JUDGE-UNAVAILABLE-ESCALATED category=",
            structuredJson: "{\"defenseclaw.finding.rule_id\":\"JUDGE-UNAVAILABLE-ESCALATED\"}");
        CorrelatedRows.Add(
            db, "h-fb", Base.AddMinutes(2), "connector-hook", "HIGH", "codex", bucket: null, target: "PreToolUse",
            details: "connector=codex result=ok action=allow severity=LOW mode=action would_block=false elapsed_ms=812 judge_fallback=timeout details_json=\"{}\"",
            structuredJson: "{\"connector\":\"codex\",\"event\":\"PreToolUse\",\"extra\":{\"judge_fallback\":\"timeout\"}}");
        CorrelatedRows.Add(
            db, "h-cw", Base.AddMinutes(3), "connector-hook", "HIGH", "claudecode", bucket: null, target: "PreToolUse",
            details: "connector=claudecode result=ok action=allow severity=LOW mode=action would_block=false",
            structuredJson: CoworkEnvelope);
    }

    private static AlertItem Item(AlertsPanelViewModel vm, string id) => vm.Alerts.Single(a => a.Key == id);

    // ------------------------------------------------------------------ a 1.0.8 runtime

    [Fact]
    public void On_a_108_runtime_the_rows_in_the_list_carry_their_labels()
    {
        StaThread.Run(async () =>
        {
            var services = await CreateAsync("1.0.8");
            Seed();
            var vm = new AlertsPanelViewModel(services);
            await vm.InitializeAsync();
            Assert.Equal(3, vm.Alerts.Count);

            var escalation = Item(vm, "j-1");
            Assert.Equal(JudgeEscalationFinding.RuleId, escalation.RuleId);
            Assert.Equal(JudgeEscalationFinding.Label, escalation.Headline);
            Assert.True(escalation.HasHookRowNotes);
            Assert.Contains(JudgeEscalationFinding.Explanation, escalation.HookRowNote, StringComparison.Ordinal);

            var fallback = Item(vm, "h-fb");
            var field = Assert.Single(fallback.HookRowFacts, f => f.Name == "Judge fallback");
            Assert.Equal(HookJudgeFallback.Short("timeout"), field.Value);
            Assert.Contains(HookJudgeFallback.Describe("timeout"), fallback.HookRowNote, StringComparison.Ordinal);
            Assert.False(fallback.IsCowork);

            var cowork = Item(vm, "h-cw");
            Assert.True(cowork.IsCowork);
            Assert.Equal("Cowork", cowork.CoworkMarker);
            Assert.Equal("claudecode", cowork.Connector);
            Assert.Equal("cowork:PreToolUse", cowork.TargetRef);
            Assert.Contains(cowork.HookRowFacts, f => f.Name == "Cowork session" && f.Value == Session);
            Assert.Contains(cowork.HookRowFacts, f => f.Name == "Cowork tool");
        });
    }

    [Fact]
    public void The_surface_cowork_token_keeps_only_the_cowork_rows_and_never_adds_a_connector()
    {
        StaThread.Run(async () =>
        {
            var services = await CreateAsync("1.0.8");
            Seed();
            var vm = new AlertsPanelViewModel(services);
            await vm.InitializeAsync();

            vm.FilterText = "surface:cowork";
            Assert.Equal(["h-cw"], vm.Alerts.Select(a => a.Key).ToArray());

            // Free text finds the marker too, and the token ANDs with another.
            vm.FilterText = "cowork";
            Assert.Contains(vm.Alerts, a => a.Key == "h-cw");
            vm.FilterText = "surface:cowork connector:codex";
            Assert.Empty(vm.Alerts);

            await services.AlertCounts.RefreshAsync();
            var byConnector = services.AlertCounts.Current.ByConnector;
            Assert.DoesNotContain(byConnector.Keys, k => string.Equals(k, "cowork", StringComparison.OrdinalIgnoreCase));
            Assert.True(byConnector.ContainsKey("claudecode"));
        });
    }

    [Fact]
    public void A_hooks_own_refusal_is_labelled_and_is_not_a_policy_block()
    {
        var refused = Audited("r-1", "guardrail-block", Refusal, "MEDIUM").WithHookRows(HookRowGate.All);

        Assert.Equal(HookFailureRow.Parse("guardrail-block", Refusal)!.Headline, refused.Headline);
        Assert.False(refused.IsBlock);
        Assert.True(refused.IsHookRefusal);
        Assert.Contains(refused.HookRowFacts, f => f.Name == "Decided by" && f.Value == HookFailureRow.SourceLabel);
        Assert.Contains(refused.HookRowFacts, f => f.Name == "Hook's reason" && f.Value.Contains("gateway unreachable", StringComparison.Ordinal));
        Assert.Contains(HookFailureRow.Parse("guardrail-block", Refusal)!.Explanation, refused.HookRowNote, StringComparison.Ordinal);

        var ordinary = Audited("r-2", "guardrail-block", "rule=CMD-X", "HIGH").WithHookRows(HookRowGate.All);
        Assert.True(ordinary.IsBlock);
        Assert.False(ordinary.HasHookRowNotes);
    }

    [Fact]
    public void A_labelled_row_stays_labelled_in_a_group_copy_and_when_labelled_again()
    {
        var cowork = Audited("c-1", "connector-hook", "connector=claudecode result=ok", "HIGH", "claudecode", CoworkEnvelope).WithHookRows(HookRowGate.All);
        var copy = cowork.CloneForGroup().WithHookRows(HookRowGate.All);

        Assert.True(copy.IsCowork);
        Assert.Equal("cowork:PreToolUse", copy.TargetRef);
        Assert.Equal(cowork.HookRowFacts.Count, copy.HookRowFacts.Count);
    }

    // ------------------------------------------------------------------ a 1.0.6 runtime

    [Fact]
    public void On_a_106_runtime_the_same_rows_look_as_they_always_did()
    {
        StaThread.Run(async () =>
        {
            var services = await CreateAsync("1.0.6");
            Seed();
            var vm = new AlertsPanelViewModel(services);
            await vm.InitializeAsync();
            Assert.Equal(3, vm.Alerts.Count);

            Assert.All(vm.Alerts, a =>
            {
                Assert.False(a.HasHookRowNotes);
                Assert.False(a.IsCowork);
                Assert.Empty(a.HookRowFacts);
            });
            Assert.NotEqual(JudgeEscalationFinding.Label, Item(vm, "j-1").Headline);
            Assert.Equal("PreToolUse", Item(vm, "h-cw").TargetRef);

            // The token is no Cowork token there: it is free text, as any unknown one is.
            vm.FilterText = "surface:cowork";
            Assert.Empty(vm.Alerts);
        });
    }

    [Fact]
    public void Without_the_capabilities_a_refusal_row_keeps_its_old_reading()
    {
        var refused = Audited("r-1", "guardrail-block", Refusal, "MEDIUM");
        var headline = refused.Headline;
        _ = refused.WithHookRows(HookRowGate.None);

        Assert.Equal(headline, refused.Headline);
        Assert.True(refused.IsBlock);
        Assert.False(refused.IsHookRefusal);
        Assert.False(refused.HasHookRowNotes);
    }

    private static AlertItem Audited(string id, string action, string details, string severity, string? connector = "claudecode", string? structured = null) =>
        AlertItem.FromAudit(new AuditEvent
        {
            Id = id,
            Timestamp = Base,
            RawTimestamp = AlertQueueDatabase.Format(Base),
            Action = action,
            Target = "PreToolUse",
            Actor = "gateway",
            Details = details,
            Severity = severity,
            Connector = connector,
            StructuredJsonRaw = structured,
        });
}
