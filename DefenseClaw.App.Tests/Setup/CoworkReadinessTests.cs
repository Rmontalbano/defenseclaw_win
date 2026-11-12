using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Setup readiness "Cowork" row (runtime 1.0.8, CUST-445): shown only when the CLI's <c>status --json</c> sent a <c>cowork</c> object, green only when it
/// says protected, and its fix opens the wizard that fits the reason. Pure inputs, nothing runs.
/// </summary>
public sealed class CoworkReadinessTests
{
    private static ReadinessInputs Healthy() => new()
    {
        ActiveConnectors = new[] { "claudecode" },
        Gateway = AppGatewayStateKind.Reachable,
        GatewaySubsystemState = "running",
        ApiState = "running",
        GuardrailEnabled = true,
        GuardrailMode = "observe",
        Config = new ReadinessConfig("anthropic", "example-model", string.Empty, string.Empty, string.Empty, true, false, false),
    };

    private static CoworkProtection State(bool isProtected, string code, string detail, string step = "") =>
        new(isProtected, detail, step, code, null, null, null);

    private static ReadinessCheck? CoworkRow(ReadinessInputs inputs) =>
        SetupReadiness.Build(inputs).SingleOrDefault(static c => c.Title == "Cowork");

    [Fact]
    public void No_cowork_object_means_no_row_and_the_other_rows_are_unchanged()
    {
        var without = SetupReadiness.Build(Healthy());

        Assert.DoesNotContain(without, static c => c.Title == "Cowork");
        Assert.Equal(9, without.Count);
    }

    [Fact]
    public void A_protected_state_is_a_passing_row_with_the_clis_sentence_and_no_fix()
    {
        var row = CoworkRow(Healthy() with { Cowork = State(true, string.Empty, "protected, action mode: policy hits are blocked") });

        Assert.NotNull(row);
        Assert.Equal(ReadinessStatus.Pass, row.Status);
        Assert.Equal("protected, action mode: policy hits are blocked", row.Detail);
        Assert.Null(row.Fix);
    }

    [Fact]
    public void The_row_sits_right_after_the_guardrail_row()
    {
        var titles = SetupReadiness.Build(Healthy() with { Cowork = State(true, string.Empty, "protected") }).Select(static c => c.Title).ToList();

        Assert.Equal(titles.IndexOf("Guardrail") + 1, titles.IndexOf("Cowork"));
    }

    [Fact]
    public void A_cowork_that_was_never_set_up_is_optional_not_counted_and_still_opens_the_cowork_wizard()
    {
        var inputs = Healthy() with { Cowork = State(false, CoworkReasonCodes.PluginNotBuilt, "not protected: example", "defenseclaw setup cowork") };
        var row = CoworkRow(inputs);

        Assert.NotNull(row);
        Assert.Equal(ReadinessStatus.Optional, row.Status);
        Assert.False(row.NeedsAttention);
        Assert.Equal("Not set up · not protected: example Next: defenseclaw setup cowork", row.Detail);
        Assert.Equal(0, SetupReadiness.Build(inputs).Count(static c => c.NeedsAttention));
        var fix = Assert.IsType<ReadinessFix>(row.Fix);
        Assert.Equal("cowork", fix.WizardTarget);
        Assert.Equal(CoworkProtectionFacts.SetupArgv, Assert.Single(fix.Steps).Argv);
    }

    [Fact]
    public void A_plugin_that_is_not_uploaded_warns_and_opens_the_cowork_wizard()
    {
        var row = CoworkRow(Healthy() with { Cowork = State(false, CoworkReasonCodes.NoEvents, "not protected: example", "defenseclaw setup cowork") });

        Assert.NotNull(row);
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.True(row.NeedsAttention);
        Assert.Equal("not protected: example Next: defenseclaw setup cowork", row.Detail);
        var fix = Assert.IsType<ReadinessFix>(row.Fix);
        Assert.Equal(ReadinessFixKind.Wizard, fix.Kind);
        Assert.Equal("setup cowork", fix.Label);
        Assert.Equal(CoworkWizard.Target, fix.WizardTarget);
        Assert.Equal("cowork", fix.WizardTarget);
        var step = Assert.Single(fix.Steps);
        Assert.Equal("defenseclaw", step.Executable);
        Assert.Equal(CoworkProtectionFacts.SetupArgv, step.Argv);
        Assert.Equal("Build the Cowork plugin, then upload it in Claude Desktop", step.Purpose);
        Assert.False(fix.RestartsGateway);
    }

    [Fact]
    public void Claude_code_being_off_warns_and_opens_the_claude_code_wizard()
    {
        var row = CoworkRow(Healthy() with { Cowork = State(false, CoworkReasonCodes.ClaudeCodeOff, "not protected: Claude Code is off", "defenseclaw setup claude-code") });

        Assert.NotNull(row);
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        var fix = Assert.IsType<ReadinessFix>(row.Fix);
        Assert.Equal(ReadinessFixKind.Wizard, fix.Kind);
        Assert.Equal("claude-code", fix.WizardTarget);
    }

    [Theory]
    [InlineData(CoworkReasonCodes.GatewayDown)]
    [InlineData(CoworkReasonCodes.ClaudeCodeNotStarted)]
    [InlineData(CoworkReasonCodes.HealthUnavailable)]
    public void A_state_that_depends_on_the_gateway_warns_with_no_fix_of_its_own(string code)
    {
        var row = CoworkRow(Healthy() with { Cowork = State(false, code, "not protected: gateway", "defenseclaw-gateway start") });

        Assert.NotNull(row);
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Null(row.Fix);
        Assert.Contains("Next: defenseclaw-gateway start", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_with_no_step_says_just_the_detail()
    {
        var row = CoworkRow(Healthy() with { Cowork = State(false, CoworkReasonCodes.GatewayDown, "not protected: gateway") });

        Assert.NotNull(row);
        Assert.Equal("not protected: gateway", row.Detail);
    }

    [Fact]
    public void A_cowork_row_that_warns_counts_as_one_item_needing_attention_in_the_checklist()
    {
        var checks = SetupReadiness.Build(Healthy() with { Cowork = State(false, CoworkReasonCodes.NoEvents, "not protected: example") });

        Assert.Equal(1, checks.Count(static c => c.NeedsAttention));
    }
}
