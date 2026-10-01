using DefenseClaw.Core.Setup;

namespace DefenseClaw.Tests;

/// <summary>
/// The fixtures are synthetic, built from the shape the CLI source defines (never captured from a run: init is not run to see its
/// output): defenseclaw/commands/cmd_init.py <c>_run_first_run_cmd</c> (the <c>json.dumps(report.to_dict(), indent=2)</c> print, the extra
/// <c>connectors</c> key) and defenseclaw/bootstrap.py <c>FirstRunReport.to_dict</c> / <c>StepResult.to_dict</c> / <c>_rollup_status</c>.
/// </summary>
public sealed class InitReportValidatorTests
{
    private static string Report(
        string status = "ready",
        string setup = """{ "name": "Config", "status": "pass", "detail": "created", "next_command": "" }""",
        string readiness = """{ "name": "Gateway", "status": "pass", "detail": "ok", "next_command": "" }""",
        string extra = "") =>
        "{\n" +
        $"  \"status\": \"{status}\",\n" +
        "  \"config_file\": \"C:\\\\synthetic\\\\config.yaml\",\n" +
        "  \"data_dir\": \"C:\\\\synthetic\",\n" +
        "  \"connector\": \"codex\",\n" +
        "  \"profile\": \"observe\",\n" +
        $"  \"setup\": [{setup}],\n" +
        $"  \"readiness\": [{readiness}],\n" +
        "  \"next_commands\": [\"defenseclaw-gateway start\"]" + extra + "\n" +
        "}\n";

    [Fact]
    public void A_ready_report_is_accepted()
    {
        var result = InitReportValidator.Validate(Report());

        Assert.Equal(InitReportVerdict.Accepted, result.Verdict);
        Assert.True(result.IsAccepted);
        Assert.Equal("ready", result.Status);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(new[] { "defenseclaw-gateway start" }, result.NextCommands);
    }

    [Fact]
    public void A_partial_report_with_warn_and_skip_steps_is_accepted_and_lists_the_warning()
    {
        var result = InitReportValidator.Validate(Report(
            status: "partial",
            setup: """{ "name": "Sidecar", "status": "skip", "detail": "not started", "next_command": "defenseclaw-gateway start" }""",
            readiness: """{ "name": "Judge", "status": "warn", "detail": "no key", "next_command": "" }"""));

        Assert.Equal(InitReportVerdict.Accepted, result.Verdict);
        var warning = Assert.Single(result.Attention);
        Assert.Equal("Judge", warning.Name);
    }

    [Fact]
    public void Exit_code_zero_is_not_enough_a_needs_attention_report_is_refused()
    {
        var result = InitReportValidator.Validate(Report(
            status: "needs_attention",
            setup: """{ "name": "Config", "status": "fail", "detail": "schema", "next_command": "defenseclaw upgrade" }"""));

        Assert.Equal(InitReportVerdict.NeedsAttention, result.Verdict);
        Assert.False(result.IsAccepted);
        Assert.Equal("Config", Assert.Single(result.Attention).Name);
    }

    [Fact]
    public void A_failed_step_inside_a_ready_status_is_still_refused()
    {
        var result = InitReportValidator.Validate(Report(
            readiness: """{ "name": "Gateway", "status": "fail", "detail": "down", "next_command": "" }"""));

        Assert.Equal(InitReportVerdict.NeedsAttention, result.Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Setup complete.")]
    [InlineData("{ not json }")]
    [InlineData("[]")]
    [InlineData("{ \"status\": \"ready\" }")]
    [InlineData("{ \"status\": \"ready\", \"setup\": [] }")]
    [InlineData("{ \"setup\": [], \"readiness\": [] }")]
    [InlineData("{ \"status\": \"ready\", \"setup\": {}, \"readiness\": [] }")]
    public void No_report_or_an_incomplete_one_is_unreadable(string output)
    {
        Assert.Equal(InitReportVerdict.Unreadable, InitReportValidator.Validate(output).Verdict);
    }

    [Fact]
    public void Null_output_is_unreadable()
    {
        Assert.Equal(InitReportVerdict.Unreadable, InitReportValidator.Validate(null).Verdict);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("ok")]
    [InlineData("PASSED")]
    public void A_step_status_the_cli_does_not_define_makes_the_report_unreadable(string stepStatus)
    {
        var result = InitReportValidator.Validate(Report(
            setup: $$"""{ "name": "Config", "status": "{{stepStatus}}", "detail": "", "next_command": "" }"""));

        Assert.Equal(InitReportVerdict.Unreadable, result.Verdict);
    }

    [Fact]
    public void A_step_with_no_status_makes_the_report_unreadable()
    {
        var result = InitReportValidator.Validate(Report(setup: """{ "name": "Config" }"""));

        Assert.Equal(InitReportVerdict.Unreadable, result.Verdict);
    }

    [Fact]
    public void An_unknown_overall_status_is_unreadable_even_when_every_step_passes()
    {
        Assert.Equal(InitReportVerdict.Unreadable, InitReportValidator.Validate(Report(status: "finished")).Verdict);
    }

    [Fact]
    public void Banner_text_before_and_after_the_json_is_tolerated()
    {
        var output = "Detecting agents...\nnote: using {defaults}\n" + Report() + "done\n";

        Assert.Equal(InitReportVerdict.Accepted, InitReportValidator.Validate(output).Verdict);
    }

    [Fact]
    public void A_multi_connector_run_names_the_connectors()
    {
        var result = InitReportValidator.Validate(Report(extra: ",\n  \"connectors\": [\"codex\", \"claudecode\"]"));

        Assert.Equal(InitReportVerdict.Accepted, result.Verdict);
        Assert.Equal(new[] { "codex", "claudecode" }, result.Connectors);
    }

    [Fact]
    public void Empty_step_lists_are_a_complete_report()
    {
        var result = InitReportValidator.Validate(Report(setup: string.Empty, readiness: string.Empty));

        Assert.Equal(InitReportVerdict.Accepted, result.Verdict);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void Crlf_output_is_read_like_lf()
    {
        var output = Report().Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(InitReportVerdict.Accepted, InitReportValidator.Validate(output).Verdict);
    }
}
