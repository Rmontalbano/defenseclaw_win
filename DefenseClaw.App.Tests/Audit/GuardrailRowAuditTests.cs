using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit inspector over one synthetic row of each 1.0.10 guardrail kind: a shadow measurement, an inspection timeout and the cascade's degraded row get a labelled
/// field on every runtime (no gate: a runtime that never writes them never shows them); the over-limit wording and the alignment check read as 1.0.10 only with the
/// cascade gate, and a 1.0.9 runtime's rows read exactly as before.
/// </summary>
public sealed class GuardrailRowAuditTests
{
    private static readonly HookRowGate Gate109 = new(true, true, true, true, true);
    private static readonly HookRowGate Gate108 = new(true, true, true, true);

    private const string Effects = "lane=effects-shadow mode=shadow tier=t2 would=block cost_us=120 effects=3";
    private const string OldLimit = "A shell command too long or too complex for the policy check.";
    private const string OldOperand = "The command had more than 1024 operands: the first 1024, and every later word that may name a protected file or is a link, were checked; the other names were not.";

    private static AuditEvent Row(string action, string target, string details, string severity, string? structured = null, string connector = "codex") => new()
    {
        Id = "a-1",
        Timestamp = DateTimeOffset.UtcNow,
        RawTimestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = action,
        Target = target,
        Actor = "gateway",
        Details = details,
        Severity = severity,
        Connector = connector,
        Bucket = "guardrail.evaluation",
        StructuredJsonRaw = structured,
    };

    private static string? Value(IReadOnlyList<DetailPair> pairs, string label) => pairs.FirstOrDefault(p => p.Label == label)?.Value;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_shadow_row_is_titled_and_labelled_on_every_gate(int gateIndex)
    {
        var gate = GateOf(gateIndex);
        var row = new AuditRow(Row("guardrail-inspection", "codex", Effects, "INFO"), gate);

        Assert.Equal("Shadow measurement (no action taken)", row.Title);
        Assert.Equal(
            "What the effects lane would have done (would=block); the call was not changed.",
            Value(row.DetailPairs, "Shadow measurement"));
        Assert.True(row.HasDetailPairs);
    }

    [Fact]
    public void A_secret_flow_row_names_secret_flow_tracking()
    {
        var row = new AuditRow(
            Row("guardrail-inspection", "secret-flow:shadow", "rule=SF-1 mode=shadow kind=token program=curl host=example.test sets=S1 source_digest=abc would=block connector=codex", "INFO"));

        Assert.Equal("What secret flow tracking would have done (would=block); the call was not changed.", Value(row.DetailPairs, "Shadow measurement"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_inspection_timeout_and_the_cascade_row_are_labelled_on_every_gate(int gateIndex)
    {
        var gate = GateOf(gateIndex);
        var scan = new AuditRow(Row("inspect-tool-timeout", "shell", "verdict=none reason=scan_timeout elapsed_ms=2001 budget_ms=2000", "MEDIUM"), gate);
        Assert.Equal("Inspection timed out: the call was decided without this check", scan.Title);
        Assert.Equal("Inspection timed out: the call was decided without this check", Value(scan.DetailPairs, "Inspection timeout"));

        var canceled = new AuditRow(Row("inspect-request-timeout", "pre-request", "verdict=none reason=caller_canceled elapsed_ms=40 budget_ms=2000", "MEDIUM"), gate);
        Assert.EndsWith("(the caller canceled it)", Value(canceled.DetailPairs, "Inspection timeout"), StringComparison.Ordinal);

        const string text = "guardrail degraded: guardrail.cascade is invalid: unknown layer; the guardrail decides with the built-in order";
        var cascade = new AuditRow(Row("guardrail-degraded", string.Empty, text, "HIGH"), gate);
        Assert.Equal("Guardrail cascade not applied as written", cascade.Title);
        Assert.Equal("Guardrail cascade not applied as written", Value(cascade.DetailPairs, "Guardrail cascade"));
        Assert.Equal(text, Value(cascade.DetailPairs, "Runtime says"));
    }

    [Fact]
    public void A_degraded_row_for_another_subsystem_and_an_ordinary_row_keep_their_title_and_pairs()
    {
        var other = new AuditRow(Row("guardrail-degraded", string.Empty, "guardrail degraded: judge is down; the rules decide", "HIGH"));
        Assert.Equal("guardrail-degraded", other.Title);
        Assert.Null(Value(other.DetailPairs, "Guardrail cascade"));
        Assert.Null(Value(other.DetailPairs, "Runtime says"));

        var hook = new AuditRow(Row("guardrail-block", "x", "would=block", "HIGH"));
        Assert.Equal("guardrail-block", hook.Title);
        Assert.Null(Value(hook.DetailPairs, "Shadow measurement"));
    }

    [Fact]
    public void A_1_0_10_runtime_words_the_over_limit_rule_for_every_surface_and_labels_the_alignment_check()
    {
        var limit = Row(
            "connector-hook", "PreToolUse", "connector=codex result=ok action=alert raw_action=alert mode=action would_block=false", "CRITICAL",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\"]}");
        var pairs = AuditHookRows.Pairs(Array.Empty<DetailPair>(), limit, HookRowGate.All, null, null);
        Assert.Equal(
            "An input too large or too complex for the policy check to read in full (a command, a tool's output or a model reply); DefenseClaw judged it at the rule pack's unknown tier.",
            Value(pairs, "Rule meaning"));
        Assert.Equal("It went through; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", Value(pairs, "Call outcome"));

        var drift = Row(
            "connector-hook", "PreToolUse", "connector=codex result=ok action=allow raw_action=allow mode=observe would_block=false", "MEDIUM",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ALIGNMENT-DRIFT\"]}");
        var driftPairs = AuditHookRows.Pairs(
            new[] { new DetailPair("Rule Id", "ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)") },
            drift,
            HookRowGate.All,
            null,
            null);
        Assert.Equal("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)", driftPairs[0].Value);
        Assert.Equal(
            "The alignment check (a question to the LLM judge) found that this tool call does not serve the task the user gave.",
            Value(driftPairs, "Rule meaning"));
        Assert.StartsWith("Measured only: the call was not changed", Value(driftPairs, "Call outcome"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_1_0_9_runtime_reads_the_limit_the_operand_bound_and_the_alignment_id_exactly_as_before()
    {
        var limit = Row(
            "connector-hook", "PreToolUse", "connector=codex result=ok action=alert raw_action=alert mode=action would_block=false", "CRITICAL",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ACTION-COMPOUND-LIMIT\",\"ACTION-OPERAND-BOUND\",\"ALIGNMENT-DRIFT\"]}");
        var pairs = AuditHookRows.Pairs(Array.Empty<DetailPair>(), limit, Gate109, null, null);

        Assert.Equal(new[] { OldLimit, OldOperand }, pairs.Where(p => p.Label == "Rule meaning").Select(p => p.Value));
        Assert.Equal("The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", Value(pairs, "Call outcome"));
        Assert.Single(pairs, p => p.Label == "Call outcome");

        var drift = Row(
            "connector-hook", "PreToolUse", "connector=codex result=ok action=allow raw_action=allow mode=observe", "MEDIUM",
            "{\"connector\":\"codex\",\"rule_ids\":[\"ALIGNMENT-DRIFT\"]}");
        var parsed = new[] { new DetailPair("Rule Id", "ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)") };
        Assert.Equal(parsed, AuditHookRows.Pairs(parsed, drift, Gate109, null, null));
        Assert.Equal(parsed, AuditHookRows.Pairs(parsed, drift, Gate108, null, null));
    }

    /// <summary>None, a 1.0.8 gate, a 1.0.9 gate and every label (a 1.0.10 gate).</summary>
    private static HookRowGate GateOf(int index) => index switch
    {
        0 => HookRowGate.None,
        1 => Gate108,
        2 => Gate109,
        _ => HookRowGate.All,
    };
}
