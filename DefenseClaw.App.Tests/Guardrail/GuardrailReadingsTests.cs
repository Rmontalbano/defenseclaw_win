using DefenseClaw.App.Services.Guardrail;

namespace DefenseClaw.App.Tests.Guardrail;

/// <summary>
/// The read-only guardrail verbs print text. These samples are laid out the way <c>cmd_guardrail.py</c> / <c>cmd_judge.py</c> print
/// them (the first of each is a capture of the real CLI's shape, with the account's values replaced by synthetic ones).
/// </summary>
public class GuardrailReadingsTests
{
    private const string HiltOff = """

          guardrail.hilt.enabled: false
          guardrail.hilt.min_severity: HIGH

          per connector:
              - Claude Code (claudecode): enabled=false min_severity=HIGH

          CRITICAL findings always block; HILT confirms risky confirmable actions at/above min_severity.

        """;

    private const string HiltMixed = """

          guardrail.hilt.enabled: true
          guardrail.hilt.min_severity: medium

          per connector:
              - Claude Code (claudecode): enabled=true min_severity=MEDIUM
              - Codex (codex): enabled=false min_severity=CRITICAL

          CRITICAL findings always block; HILT confirms risky confirmable actions at/above min_severity.

        """;

    [Fact]
    public void A_status_table_followed_directly_by_the_summary_bullets_does_not_read_a_bullet_as_a_connector()
    {
        // No drift warning between the table and the bullets: the shape the real CLI prints on a healthy install.
        var status = DefenseClaw.App.Services.Wizards.GuardrailStatusParser.Parse(GuardrailControlsViewModelTests.StatusOne);

        var connector = Assert.Single(status.Connectors);
        Assert.Equal("claudecode", connector.Key);
        Assert.Equal("4000", status.Port);
    }

    [Fact]
    public void Hilt_off_reads_as_off_with_the_default_severity_and_one_connector()
    {
        var hilt = GuardrailReadings.ParseHilt(HiltOff);

        Assert.True(hilt.IsRead);
        Assert.False(hilt.Enabled);
        Assert.Equal("HIGH", hilt.MinSeverity);
        var connector = Assert.Single(hilt.Connectors);
        Assert.Equal(("Claude Code", "claudecode", false, "HIGH"), (connector.Name, connector.Key, connector.Enabled, connector.MinSeverity));
    }

    [Fact]
    public void Hilt_on_for_some_connectors_keeps_each_connectors_own_policy_and_upper_cases_severities()
    {
        var hilt = GuardrailReadings.ParseHilt(HiltMixed);

        Assert.True(hilt.Enabled);
        Assert.Equal("MEDIUM", hilt.MinSeverity);
        Assert.Collection(
            hilt.Connectors,
            c => Assert.Equal(("claudecode", true, "MEDIUM"), (c.Key, c.Enabled, c.MinSeverity)),
            c => Assert.Equal(("codex", false, "CRITICAL"), (c.Key, c.Enabled, c.MinSeverity)));
    }

    [Fact]
    public void Hilt_text_with_crlf_line_ends_reads_the_same()
    {
        var hilt = GuardrailReadings.ParseHilt(HiltMixed.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.True(hilt.Enabled);
        Assert.Equal(2, hilt.Connectors.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: No such command 'hilt'.")]
    public void Hilt_text_that_is_not_a_reading_is_not_read_and_keeps_the_raw_text(string text)
    {
        var hilt = GuardrailReadings.ParseHilt(text);

        Assert.False(hilt.IsRead);
        Assert.Empty(hilt.Connectors);
        Assert.Equal(text.Trim(), hilt.Raw);
    }

    [Fact]
    public void The_built_in_block_message_is_no_message()
    {
        const string text = """

              guardrail.block_message: (built-in default)

              per connector:
                  - Claude Code (claudecode): (built-in default)

            """;

        var message = GuardrailReadings.ParseBlockMessage(text);

        Assert.True(message.IsRead);
        Assert.True(message.IsDefault);
        Assert.Equal(string.Empty, message.Message);
        var connector = Assert.Single(message.Connectors);
        Assert.Equal(("claudecode", string.Empty), (connector.Key, connector.Message));
    }

    [Fact]
    public void A_custom_message_is_read_whole_and_per_connector_overrides_are_kept_apart()
    {
        const string text = """

              guardrail.block_message: Blocked by Acme Security (see #sec-help): ask the on-call.

              per connector:
                  - Claude Code (claudecode): Blocked by Acme Security (see #sec-help): ask the on-call.
                  - Codex (codex): Codex policy (strict): no

            """;

        var message = GuardrailReadings.ParseBlockMessage(text);

        Assert.Equal("Blocked by Acme Security (see #sec-help): ask the on-call.", message.Message);
        Assert.False(message.IsDefault);
        Assert.Collection(
            message.Connectors,
            c => Assert.Equal(("claudecode", "Blocked by Acme Security (see #sec-help): ask the on-call."), (c.Key, c.Message)),
            c => Assert.Equal(("Codex", "codex", "Codex policy (strict): no"), (c.Name, c.Key, c.Message)));
    }

    [Fact]
    public void A_block_message_that_the_cli_did_not_print_is_not_read()
    {
        var message = GuardrailReadings.ParseBlockMessage("Error: something else.");

        Assert.False(message.IsRead);
        Assert.Equal("Error: something else.", message.Raw);
    }

    private const string JudgeOff = """

          guardrail.judge.enabled:         false
          guardrail.judge.hook_connectors: [] (hook lane off)
          guardrail.judge.hook_timeout:    5s (gateway default)

          effective state per connector:
              - claudecode: regex + AID only - opt in: defenseclaw guardrail judge add claudecode

        """;

    private const string JudgeOn = """

          guardrail.judge.enabled:         true
          guardrail.judge.hook_connectors: ['hermes', 'claudecode']
          guardrail.judge.hook_timeout:    8s

          effective state per connector:
              - claudecode: judged (hook lane: prompt) — completion: regex_only
              - hermes: judged (hook lane)
              - opencode: regex + AID only — opt in: defenseclaw guardrail judge add opencode
              - openclaw: judged (proxy lane)

        """;

    [Fact]
    public void A_closed_hook_lane_reads_as_an_empty_gate_with_the_default_timeout()
    {
        var judge = GuardrailReadings.ParseJudge(JudgeOff);

        Assert.True(judge.IsRead);
        Assert.False(judge.JudgeEnabled);
        Assert.False(judge.GateAll);
        Assert.Empty(judge.Gate);
        Assert.Equal("5s (gateway default)", judge.HookTimeout);
        var connector = Assert.Single(judge.Connectors);
        Assert.Equal(("claudecode", "regex + AID only"), (connector.Key, connector.State));
        Assert.Contains("opt in", connector.Note, StringComparison.Ordinal);
        Assert.False(judge.IsGated("claudecode"));
    }

    [Fact]
    public void A_gate_naming_connectors_reads_each_name_and_each_connectors_effective_state()
    {
        var judge = GuardrailReadings.ParseJudge(JudgeOn);

        Assert.True(judge.JudgeEnabled);
        Assert.Equal(new[] { "hermes", "claudecode" }, judge.Gate);
        Assert.Equal("8s", judge.HookTimeout);
        Assert.True(judge.IsGated("HERMES"));
        Assert.False(judge.IsGated("opencode"));
        Assert.Collection(
            judge.Connectors,
            c => Assert.Equal(("claudecode", "judged (hook lane: prompt)"), (c.Key, c.State)),
            c => Assert.Equal(("hermes", "judged (hook lane)", string.Empty), (c.Key, c.State, c.Note)),
            c => Assert.Equal(("opencode", "regex + AID only"), (c.Key, c.State)),
            c => Assert.Equal(("openclaw", "judged (proxy lane)"), (c.Key, c.State)));
    }

    [Fact]
    public void The_note_after_a_state_is_split_off_whatever_dash_the_runner_decoded()
    {
        // The CLI writes an em dash; a console code page may turn it into anything.
        foreach (var dash in new[] { "—", "-", "–", "�", "?" })
        {
            var judge = GuardrailReadings.ParseJudge(
                $"  guardrail.judge.enabled: true\n  guardrail.judge.hook_connectors: all\n\n  effective state per connector:\n      - codex: gated on, judge inactive {dash} judge disabled\n");

            var connector = Assert.Single(judge.Connectors);
            Assert.Equal("gated on, judge inactive", connector.State);
            Assert.Equal("judge disabled", connector.Note);
        }
    }

    [Fact]
    public void An_all_gate_covers_every_connector()
    {
        var judge = GuardrailReadings.ParseJudge("  guardrail.judge.enabled: true\n  guardrail.judge.hook_connectors: all\n");

        Assert.True(judge.GateAll);
        Assert.True(judge.IsGated("anything"));
        Assert.Empty(judge.Connectors);
    }

    [Fact]
    public void A_star_in_a_hand_edited_gate_list_means_all()
    {
        var judge = GuardrailReadings.ParseJudge("  guardrail.judge.enabled: true\n  guardrail.judge.hook_connectors: ['*']\n");

        Assert.True(judge.GateAll);
        Assert.Empty(judge.Gate);
    }

    [Fact]
    public void No_connectors_configured_leaves_the_list_empty_without_failing()
    {
        var judge = GuardrailReadings.ParseJudge(
            "  guardrail.judge.enabled:         false\n  guardrail.judge.hook_connectors: [] (hook lane off)\n  guardrail.judge.hook_timeout:    5s (gateway default)\n\n  no connectors configured.\n");

        Assert.True(judge.IsRead);
        Assert.Empty(judge.Connectors);
    }
}
