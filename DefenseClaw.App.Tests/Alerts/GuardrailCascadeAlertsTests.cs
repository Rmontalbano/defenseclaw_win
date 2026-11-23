using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The Alerts panel's wording of the over-limit rule and the alignment check on a guardrail-cascade runtime (1.0.10), and that a 1.0.9 runtime (the same gate with the
/// cascade off) reads these rows exactly as it did. The runtime's own rule title is the headline in both.
/// </summary>
public sealed class GuardrailCascadeAlertsTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private static readonly HookRowGate Gate109 = new(true, true, true, true, true);
    private static readonly HookRowGate Gate1010 = new(true, true, true, true, true, true);

    private const string OldLimit = "A shell command too long or too complex for the policy check.";
    private const string OldOperand = "The command had more than 1024 operands: the first 1024, and every later word that may name a protected file or is a link, were checked; the other names were not.";
    private const string NewLimit =
        "An input too large or too complex for the policy check to read in full (a command, a tool's output or a model reply); DefenseClaw judged it at the rule pack's unknown tier.";
    private const string Drift = "The alignment check (a question to the LLM judge) found that this tool call does not serve the task the user gave.";

    private static RuntimeAlertRow Runtime(string rule, string decision, string details)
    {
        var json = "[{\"id\":\"r-1\",\"timestamp\":\"" + Base.ToString("O") + "\",\"severity\":\"CRITICAL\",\"action\":\"connector-hook\",\"target\":\"codex:PreToolUse\"," +
            "\"actor\":\"gateway\",\"connector\":\"codex\",\"details\":\"" + details + "\",\"rule\":\"" + rule + "\",\"decision\":\"" + decision + "\"}]";
        return RuntimeAlertSnapshotParser.Parse(json).Rows.Single();
    }

    private static AlertItem Item(HookRowGate gate, RuntimeAlertRow row) => AlertItem.FromRuntime(row).WithHookRows(gate, row);

    private static string[] Paragraphs(AlertItem item) => item.HookRowNote.Split(Environment.NewLine + Environment.NewLine);

    [Fact]
    public void A_cascade_runtime_words_the_limit_for_every_surface_and_keeps_the_runtimes_title()
    {
        var row = Runtime("ACTION-COMPOUND-LIMIT: Input exceeds policy evaluation limit, judged as unknown (bounded_truncated cap=items)", "alert", "connector=codex action=alert raw_action=alert mode=action would_block=false");
        var item = Item(Gate1010, row);

        Assert.Equal("ACTION-COMPOUND-LIMIT: Input exceeds policy evaluation limit, judged as unknown (bounded_truncated cap=items)", item.Headline);
        var paragraphs = Paragraphs(item);
        Assert.Equal(NewLimit, paragraphs[0]);
        Assert.Equal("It went through; DefenseClaw recorded it as an alert (the strict rule pack blocks it).", paragraphs[1]);
    }

    [Theory]
    [InlineData("blocked", "connector=codex action=block raw_action=block mode=action would_block=true", "It was blocked.")]
    [InlineData("alert", "connector=codex action=alert raw_action=alert mode=observe", "It went through; DefenseClaw recorded it as an alert (observe mode).")]
    public void The_cascade_outcome_is_worded_without_the_word_command(string decision, string details, string expected)
    {
        var item = Item(Gate1010, Runtime("ACTION-COMPOUND-LIMIT: x", decision, details));

        Assert.Equal(expected, Paragraphs(item)[1]);
    }

    [Theory]
    [InlineData("alert", "connector=codex action=alert raw_action=alert mode=action would_block=false", "The call ran; DefenseClaw recorded it as an alert (the strict rule pack blocks it).")]
    [InlineData("blocked", "connector=codex action=block raw_action=block mode=action would_block=true", "The call was blocked.")]
    public void A_1_0_9_runtime_reads_the_limit_exactly_as_before(string decision, string details, string outcome)
    {
        var item = Item(Gate109, Runtime("ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit", decision, details));

        Assert.Equal("ACTION-COMPOUND-LIMIT: Shell command exceeds policy evaluation limit", item.Headline);
        var paragraphs = Paragraphs(item);
        Assert.Equal(OldLimit, paragraphs[0]);
        Assert.Equal(outcome, paragraphs[1]);
    }

    [Fact]
    public void The_operand_bound_sentence_is_the_same_in_both_eras_and_the_alignment_id_reads_only_on_a_cascade_runtime()
    {
        var operand = Runtime("ACTION-OPERAND-BOUND: Command has too many operands", "alert", "connector=codex action=alert raw_action=alert mode=action");
        Assert.Equal(OldOperand, Paragraphs(Item(Gate1010, operand))[0]);
        Assert.Equal(OldOperand, Paragraphs(Item(Gate109, operand))[0]);

        var drift = Runtime("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)", "allow", "connector=codex action=allow raw_action=allow mode=observe would_block=false");
        var item = Item(Gate1010, drift);
        Assert.Equal("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)", item.Headline);
        var paragraphs = Paragraphs(item);
        Assert.Equal(Drift, paragraphs[0]);
        Assert.StartsWith("Measured only: the call was not changed", paragraphs[1], StringComparison.Ordinal);

        var before = Item(Gate109, drift);
        Assert.Equal("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)", before.Headline);
        Assert.Equal(string.Empty, before.HookRowNote);
        Assert.Empty(before.HookRowFacts);
        Assert.False(before.HasHookRowNotes);
    }

    [Theory]
    [InlineData("alert", "connector=codex action=alert raw_action=alert mode=action", "The call ran; DefenseClaw recorded an alert.")]
    [InlineData("blocked", "connector=codex action=block raw_action=block mode=action would_block=true", "The call was blocked.")]
    public void The_alignment_outcome_follows_the_rows_own_decision(string decision, string details, string expected)
    {
        var item = Item(Gate1010, Runtime("ALIGNMENT-DRIFT: Tool call does not serve the user task (alignment check)", decision, details));

        Assert.Equal(Drift, Paragraphs(item)[0]);
        Assert.Equal(expected, Paragraphs(item)[1]);
    }

    [Fact]
    public void A_row_from_an_older_gate_with_the_cascade_off_and_older_still_is_unchanged()
    {
        foreach (var gate in new[] { new HookRowGate(true, true, true, true), HookRowGate.None })
        {
            foreach (var rule in new[] { "ACTION-COMPOUND-LIMIT: x", "ALIGNMENT-DRIFT: x" })
            {
                var row = Runtime(rule, "alert", "connector=codex action=alert raw_action=alert mode=action");
                var item = Item(gate, row);
                Assert.Equal(rule, item.Headline);
                Assert.Equal(string.Empty, item.HookRowNote);
                Assert.Empty(item.HookRowFacts);
            }
        }
    }

    [Fact]
    public void The_gate_turns_the_cascade_on_from_the_capability_and_counts_it_as_a_label()
    {
        Assert.True(HookRowGate.All.Cascade);
        Assert.True(HookRowGate.All.Any);
        Assert.False(HookRowGate.None.Cascade);
        Assert.False(Gate109.Cascade);
        Assert.True(new HookRowGate(false, false, false, false, false, true).Any);
    }
}
