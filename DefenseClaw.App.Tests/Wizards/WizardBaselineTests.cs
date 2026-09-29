using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// S3: a wizard opened on a connector must start from what config.yaml says and send only what the operator
/// changes — never a reset. These run the real parse, curate and baseline steps on a Click-shaped help screen.
/// </summary>
public class WizardBaselineTests
{
    private const string ObserveConfig = """
        guardrail:
          connector: claudecode
          connectors:
            claudecode:
              mode: observe
              hook_fail_mode: closed
              block_message: ''
              rule_pack_dir: ''
        """;

    private const string ActionConfig = """
        guardrail:
          connector: claudecode
          connectors:
            claudecode:
              mode: action
              hook_fail_mode: closed
              block_message: ''
              rule_pack_dir: ''
        """;

    private static WizardDefinition Applied(string configYaml, bool readable = true) =>
        WizardBaseline.Apply(WizardSamples.ClaudeCode(), WizardSamples.Config(configYaml), readable);

    private static IReadOnlyList<string> Argv(WizardDefinition definition, WizardValues values) => definition.BuildArgv(values);

    // ------------------------------------------------------------------ the pipeline the tests stand on

    [Fact]
    public void The_claude_code_help_becomes_a_curated_hook_connector_wizard_without_the_docker_flag()
    {
        var definition = WizardSamples.ClaudeCode();

        Assert.True(definition.IsCurated);
        Assert.Equal(PlatformStatus.Certified, definition.PlatformStatus);
        Assert.Equal(new[] { "enforcement", "rules", "apply" }, definition.Steps.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--with-local-stack");
        Assert.Contains(definition.AllFields, f => f.Flag == "--mode");
        Assert.Contains(definition.AllFields, f => f.Flag == "--yes");
    }

    // ------------------------------------------------------------------ no change, no change sent

    [Fact]
    public void A_connector_in_observe_mode_with_nothing_touched_sends_only_the_non_interactive_flag()
    {
        var definition = Applied(ObserveConfig);

        var argv = Argv(definition, WizardSamples.StartingValues(definition));

        Assert.Equal(new[] { "setup", "claude-code", "--yes" }, argv);
    }

    [Fact]
    public void A_connector_in_action_mode_keeps_mode_action_because_omitting_it_would_reset_it_to_observe()
    {
        var definition = Applied(ActionConfig);

        var argv = Argv(definition, WizardSamples.StartingValues(definition));

        Assert.Equal(new[] { "setup", "claude-code", "--mode", "action", "--yes" }, argv);
    }

    [Fact]
    public void The_mode_field_starts_at_the_stored_mode_while_its_baseline_stays_what_omission_means()
    {
        var mode = WizardSamples.Find(Applied(ActionConfig), "--mode");

        Assert.Equal("action", mode.DefaultValue);
        Assert.Equal("observe", mode.BaselineValue);
        Assert.Contains("guardrail.connectors.claudecode", mode.BaselineSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Fields_taken_from_the_configuration_are_baselines_so_they_are_not_resent()
    {
        var failMode = WizardSamples.Find(Applied(ObserveConfig), "--fail-mode");

        Assert.Equal("closed", failMode.DefaultValue);
        Assert.Equal("closed", failMode.BaselineValue);
    }

    [Fact]
    public void An_enforce_mode_in_the_config_is_treated_as_action()
    {
        var definition = Applied(ObserveConfig.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal));

        var argv = Argv(definition, WizardSamples.StartingValues(definition));

        Assert.Equal(new[] { "setup", "claude-code", "--mode", "action", "--yes" }, argv);
    }

    [Fact]
    public void Only_the_fields_the_operator_changes_reach_argv()
    {
        var definition = Applied(ObserveConfig);
        var values = WizardSamples.StartingValues(definition);
        values["fail-mode"] = "open";
        values["restart"] = ToggleValues.Off;

        var argv = Argv(definition, values);

        Assert.Equal(new[] { "setup", "claude-code", "--fail-mode", "open", "--no-restart", "--yes" }, argv);
    }

    [Fact]
    public void Changing_a_field_and_changing_it_back_sends_nothing_for_it()
    {
        var definition = Applied(ObserveConfig);
        var values = WizardSamples.StartingValues(definition);
        values["fail-mode"] = "open";
        values["fail-mode"] = "closed";

        Assert.Equal(new[] { "setup", "claude-code", "--yes" }, Argv(definition, values));
    }

    [Fact]
    public void Moving_an_action_connector_back_to_observe_omits_mode_because_that_is_the_cli_default()
    {
        var definition = Applied(ActionConfig);
        var values = WizardSamples.StartingValues(definition);
        values["mode"] = "observe";

        Assert.Equal(new[] { "setup", "claude-code", "--yes" }, Argv(definition, values));
    }

    [Fact]
    public void Clearing_a_stored_block_message_is_sent_as_an_empty_value_because_the_cli_documents_pass_empty_to_clear()
    {
        var config = ObserveConfig.Replace("block_message: ''", "block_message: 'Blocked by policy'", StringComparison.Ordinal);
        var definition = Applied(config);
        var values = WizardSamples.StartingValues(definition);
        Assert.Equal("Blocked by policy", values["block-message"]);

        Assert.Equal(new[] { "setup", "claude-code", "--yes" }, Argv(definition, values));

        values["block-message"] = string.Empty;
        Assert.Equal(new[] { "setup", "claude-code", "--block-message", string.Empty, "--yes" }, Argv(definition, values));

        values["block-message"] = "New text";
        Assert.Equal(new[] { "setup", "claude-code", "--block-message", "New text", "--yes" }, Argv(definition, values));
    }

    [Fact]
    public void Hilt_settings_come_from_the_nested_hilt_block()
    {
        var config = ObserveConfig + "\n" + "      hilt:\n        enabled: true\n        min_severity: HIGH\n";
        var definition = Applied(config);
        var values = WizardSamples.StartingValues(definition);

        Assert.Equal(ToggleValues.On, values["human-approval"]);
        Assert.Equal("HIGH", values["hilt-min-severity"]);
        Assert.Equal(new[] { "setup", "claude-code", "--yes" }, Argv(definition, values));

        values["human-approval"] = ToggleValues.Off;
        Assert.Equal(new[] { "setup", "claude-code", "--no-human-approval", "--yes" }, Argv(definition, values));
    }

    [Fact]
    public void A_stored_value_the_control_cannot_show_is_left_alone()
    {
        var config = ObserveConfig.Replace("hook_fail_mode: closed", "hook_fail_mode: sideways", StringComparison.Ordinal) + "\n" +
                     "      hilt:\n        enabled: maybe\n        min_severity: EXTREME\n";
        var definition = Applied(config);

        Assert.Equal(string.Empty, WizardSamples.Find(definition, "--fail-mode").DefaultValue);
        Assert.Equal(string.Empty, WizardSamples.Find(definition, "--human-approval").DefaultValue);
        Assert.Equal(string.Empty, WizardSamples.Find(definition, "--hilt-min-severity").DefaultValue);
    }

    // ------------------------------------------------------------------ naming, absence, unreadable config

    [Theory]
    [InlineData("claudecode")]
    [InlineData("ClaudeCode")]
    [InlineData("claude_code")]
    [InlineData("claude-code")]
    public void The_cli_target_and_the_config_key_are_one_connector_however_the_key_is_spelled(string key)
    {
        var definition = Applied(ActionConfig.Replace("claudecode:", key + ":", StringComparison.Ordinal));

        Assert.Equal("action", WizardSamples.StartingValues(definition)["mode"]);
    }

    [Fact]
    public void A_connector_that_is_not_in_the_config_starts_every_field_at_the_cli_default_and_says_so()
    {
        var definition = Applied("guardrail:\n  connector: codex\n  connectors:\n    codex:\n      mode: action\n");

        Assert.Contains("not in config.yaml yet", definition.BaselineNote, StringComparison.Ordinal);
        Assert.Equal(string.Empty, definition.BaselineWarning);
        Assert.Equal("observe", WizardSamples.StartingValues(definition)["mode"]);
    }

    [Fact]
    public void An_unreadable_config_is_a_warning_on_every_page_and_the_definition_is_otherwise_untouched()
    {
        var original = WizardSamples.ClaudeCode();

        var definition = WizardBaseline.Apply(original, WizardSamples.Config(string.Empty), configReadable: false);

        Assert.Contains("could not be read", definition.BaselineWarning, StringComparison.Ordinal);
        Assert.Contains("downgrade", definition.BaselineWarning, StringComparison.Ordinal);
        Assert.Equal(string.Empty, definition.BaselineNote);
        Assert.Equal(original.Steps.Count, definition.Steps.Count);
        Assert.Equal("observe", WizardSamples.StartingValues(definition)["mode"]);
    }

    [Fact]
    public void The_baseline_note_says_where_the_answers_came_from()
    {
        var definition = Applied(ObserveConfig);

        Assert.Contains("guardrail.connectors.claudecode", definition.BaselineNote, StringComparison.Ordinal);
        Assert.Contains("Only fields you change are sent", definition.BaselineNote, StringComparison.Ordinal);
        Assert.Equal(string.Empty, definition.BaselineWarning);
    }

    [Fact]
    public void A_definition_with_no_pages_is_returned_as_it_is()
    {
        var empty = new WizardDefinition { Target = "claude-code", Title = "Claude Code", Group = WizardGroups.Connectors };

        Assert.Same(empty, WizardBaseline.Apply(empty, WizardSamples.Config(ObserveConfig), true));
    }

    // ------------------------------------------------------------------ non-connector targets

    [Fact]
    public void The_llm_wizard_starts_from_the_stored_llm_block()
    {
        var definition = WizardBaseline.Apply(
            WizardSamples.Llm(),
            WizardSamples.Config("llm:\n  provider: openai\n  model: gpt-4o\n  api_key_env: OPENAI_API_KEY\n  timeout: 30\n"),
            configReadable: true);
        var values = WizardSamples.StartingValues(definition);

        Assert.Equal("openai", values["provider"]);
        Assert.Equal("gpt-4o", values["model"]);
        Assert.Equal("OPENAI_API_KEY", values["api-key-env"]);
        Assert.Equal("30", values["timeout"]);

        // Untouched: nothing but the non-interactive switch is sent.
        Assert.Equal(new[] { "setup", "llm", "--non-interactive" }, definition.BuildArgv(values));

        values["model"] = "gpt-4.1";
        Assert.Equal(new[] { "setup", "llm", "--model", "gpt-4.1", "--non-interactive" }, definition.BuildArgv(values));
    }

    [Fact]
    public void A_numeric_flag_ignores_a_stored_value_that_is_not_a_number()
    {
        var definition = WizardBaseline.Apply(
            WizardSamples.Llm(),
            WizardSamples.Config("llm:\n  provider: openai\n  timeout: soon\n"),
            configReadable: true);

        Assert.Equal(string.Empty, WizardSamples.StartingValues(definition)["timeout"]);
    }

    [Fact]
    public void The_llm_wizard_is_left_alone_when_the_config_cannot_be_read()
    {
        var original = WizardSamples.Llm();

        Assert.Same(original, WizardBaseline.Apply(original, WizardSamples.Config("llm:\n  provider: openai\n"), configReadable: false));
    }

    [Fact]
    public void A_target_with_no_baseline_rules_is_returned_unchanged()
    {
        var other = new WizardDefinition
        {
            Target = "skill-scanner",
            Title = "Skill scanner",
            Group = WizardGroups.Scanners,
            Steps = new[]
            {
                new WizardStep
                {
                    Id = "p",
                    Title = "P",
                    Fields = new[] { new WizardField { Id = "policy", Label = "Policy", Kind = WizardFieldKind.Text, Flag = "--policy" } },
                },
            },
        };

        Assert.Same(other, WizardBaseline.Apply(other, WizardSamples.Config(ObserveConfig), true));
    }
}
