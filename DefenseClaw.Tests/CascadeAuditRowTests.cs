using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>The cascade's degraded audit row of DefenseClaw 1.0.10 (guardrail-degraded): which rows it is, and that a degraded row for any other subsystem is left as it is.</summary>
public sealed class CascadeAuditRowTests
{
    private const string Invalid = "guardrail degraded: guardrail.cascade is invalid: unknown layer \"x\"; the guardrail decides with the built-in order";
    private const string Narrower = "guardrail degraded: guardrail.cascade: the gateway does not apply 2 narrower-scope setting(s); the global values decide";

    [Theory]
    [InlineData("guardrail-degraded", "", Invalid)]
    [InlineData("guardrail-degraded", "", Narrower)]
    [InlineData("guardrail-degraded", "guardrail.cascade", "anything")]
    [InlineData("Guardrail-Degraded", null, "guardrail.cascade is invalid: x")]
    [InlineData("guardrail-degraded", null, "guardrail.cascade: the gateway does not apply 1 narrower-scope setting(s)")]
    public void The_cascade_rows_are_recognised(string action, string? target, string details) =>
        Assert.True(CascadeAuditRow.IsCascade(action, target, details));

    [Theory]
    [InlineData("guardrail-degraded", "", "guardrail degraded: judge is down; the rules decide")]
    [InlineData("guardrail-degraded", "judge", "guardrail degraded: guardrail.cascadeX is invalid: no")]
    [InlineData("guardrail-degraded", "", "guardrail degraded: see guardrail.cascade is invalid: later")]
    [InlineData("guardrail-degraded", "", null)]
    [InlineData("subsystem-degraded", "guardrail.cascade", Invalid)]
    [InlineData("guardrail-block", "", Invalid)]
    public void A_degraded_row_for_another_subsystem_is_not_one(string action, string? target, string? details) =>
        Assert.False(CascadeAuditRow.IsCascade(action, target, details));

    [Fact]
    public void The_runtimes_own_text_is_shown_on_one_line_and_bounded()
    {
        Assert.Equal(Invalid, CascadeAuditRow.Text(Invalid));
        Assert.Equal("a b c", CascadeAuditRow.Text(" a\nb\nc "));
        Assert.Equal(600, CascadeAuditRow.Text("guardrail degraded: guardrail.cascade is invalid: " + new string('e', 2000)).Length);
        Assert.Equal(string.Empty, CascadeAuditRow.Text(null));
    }

    [Fact]
    public void The_shared_labels_give_the_headline_and_the_runtimes_text()
    {
        Assert.Equal("Guardrail cascade not applied as written", GuardrailRowNotes.Headline("guardrail-degraded", "", Invalid));
        var notes = GuardrailRowNotes.For("guardrail-degraded", "", Invalid);
        Assert.Equal(2, notes.Count);
        Assert.Equal(new KeyValuePair<string, string>("Guardrail cascade", "Guardrail cascade not applied as written"), notes[0]);
        Assert.Equal(new KeyValuePair<string, string>("Runtime says", Invalid), notes[1]);

        Assert.Empty(GuardrailRowNotes.For("guardrail-degraded", "", "guardrail degraded: judge is down"));
        Assert.Null(GuardrailRowNotes.Headline("guardrail-degraded", "", "guardrail degraded: judge is down"));
    }
}
