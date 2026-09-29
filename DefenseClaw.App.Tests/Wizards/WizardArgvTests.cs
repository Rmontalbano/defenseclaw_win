using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>How answers become argv: baselines, repeatable flags, gates, positionals and secrets.</summary>
public class WizardArgvTests
{
    private static WizardField Field(
        string id,
        WizardFieldKind kind,
        string? flag = null,
        string? negative = null,
        string baseline = "",
        bool allowEmpty = false,
        bool positional = false,
        int order = 0,
        string? gate = null,
        string[]? gateValues = null) => new()
    {
        Id = id,
        Label = id,
        Kind = kind,
        Flag = flag,
        NegativeFlag = negative,
        BaselineValue = baseline,
        AllowEmptyWhenChanged = allowEmpty,
        IsPositional = positional,
        PositionalOrder = order,
        VisibleWhenFieldId = gate,
        VisibleWhenValues = gateValues ?? Array.Empty<string>(),
    };

    private static WizardDefinition Definition(params WizardField[] fields) => new()
    {
        Target = "sample",
        Title = "Sample",
        Group = WizardGroups.Other,
        Steps = new[] { new WizardStep { Id = "one", Title = "One", Fields = fields } },
    };

    private static WizardValues Values(params (string Id, string Value)[] answers)
    {
        var values = new WizardValues();
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    // ------------------------------------------------------------------ repeatable flags

    [Fact]
    public void A_repeatable_flag_emits_one_flag_value_pair_per_line()
    {
        var definition = Definition(Field("judge-hook-connectors", WizardFieldKind.Lines, "--judge-hook-connectors"));

        var argv = definition.BuildArgv(Values(("judge-hook-connectors", "claudecode\r\ncodex\n  cursor  \n\n")));

        Assert.Equal(
            new[]
            {
                "setup", "sample",
                "--judge-hook-connectors", "claudecode",
                "--judge-hook-connectors", "codex",
                "--judge-hook-connectors", "cursor",
            },
            argv);
    }

    [Fact]
    public void A_repeatable_flag_left_at_its_baseline_is_omitted_however_the_lines_are_spaced()
    {
        var definition = Definition(Field("roots", WizardFieldKind.Lines, "--root", baseline: "a\nb"));

        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("roots", "a\nb"))));
        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("roots", " a \r\n\r\n b "))));
        Assert.Equal(
            new[] { "setup", "sample", "--root", "a", "--root", "b", "--root", "c" },
            definition.BuildArgv(Values(("roots", "a\nb\nc"))));
    }

    [Fact]
    public void A_repeatable_flag_is_read_off_the_help_and_emits_per_line()
    {
        var definition = WizardSamples.ClaudeCode();
        var field = WizardSamples.Find(definition, "--judge-hook-connectors");
        Assert.Equal(WizardFieldKind.Lines, field.Kind);

        var values = WizardSamples.StartingValues(definition);
        values[field.Id] = "claudecode\ncodex";

        Assert.Equal(
            new[]
            {
                "setup", "claude-code",
                "--judge-hook-connectors", "claudecode",
                "--judge-hook-connectors", "codex",
                "--yes",
            },
            definition.BuildArgv(values));
    }

    // ------------------------------------------------------------------ toggles, switches, text

    [Fact]
    public void A_toggle_left_unchanged_or_at_its_baseline_emits_nothing_and_a_change_emits_the_matching_flag()
    {
        var definition = Definition(
            Field("restart", WizardFieldKind.Toggle, "--restart", "--no-restart", baseline: ToggleValues.On));

        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("restart", ToggleValues.On))));
        Assert.Equal(new[] { "setup", "sample", "--no-restart" }, definition.BuildArgv(Values(("restart", ToggleValues.Off))));

        // "Leave unchanged" (empty) differs from a baseline of On but names neither flag, so nothing is written.
        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("restart", ToggleValues.Unset))));
    }

    [Fact]
    public void A_toggle_with_no_baseline_emits_the_flag_for_the_side_chosen()
    {
        var definition = Definition(Field("judge", WizardFieldKind.Toggle, "--enable-judge", "--no-enable-judge"));

        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("judge", ToggleValues.Unset))));
        Assert.Equal(new[] { "setup", "sample", "--enable-judge" }, definition.BuildArgv(Values(("judge", ToggleValues.On))));
        Assert.Equal(new[] { "setup", "sample", "--no-enable-judge" }, definition.BuildArgv(Values(("judge", ToggleValues.Off))));
    }

    [Fact]
    public void A_bare_switch_is_written_only_when_it_is_on_and_was_not_already_on()
    {
        var offByDefault = Definition(Field("verify", WizardFieldKind.Switch, "--verify"));
        Assert.Equal(new[] { "setup", "sample", "--verify" }, offByDefault.BuildArgv(Values(("verify", ToggleValues.On))));
        Assert.Equal(new[] { "setup", "sample" }, offByDefault.BuildArgv(Values(("verify", ToggleValues.Off))));

        var onByDefault = Definition(Field("verify", WizardFieldKind.Switch, "--verify", baseline: ToggleValues.On));
        Assert.Equal(new[] { "setup", "sample" }, onByDefault.BuildArgv(Values(("verify", ToggleValues.On))));
    }

    [Fact]
    public void Text_is_trimmed_and_written_only_when_it_differs_from_its_baseline()
    {
        var definition = Definition(Field("model", WizardFieldKind.Text, "--model", baseline: "gpt-4o"));

        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("model", "  gpt-4o  "))));
        Assert.Equal(new[] { "setup", "sample", "--model", "gpt-4.1" }, definition.BuildArgv(Values(("model", "  gpt-4.1 "))));
    }

    [Fact]
    public void Clearing_a_field_is_only_sent_where_the_cli_treats_empty_as_a_value()
    {
        var clearable = Definition(Field("msg", WizardFieldKind.Text, "--block-message", baseline: "old", allowEmpty: true));
        var plain = Definition(Field("msg", WizardFieldKind.Text, "--block-message", baseline: "old"));

        Assert.Equal(new[] { "setup", "sample", "--block-message", string.Empty }, clearable.BuildArgv(Values(("msg", ""))));
        Assert.Equal(new[] { "setup", "sample" }, plain.BuildArgv(Values(("msg", ""))));
    }

    [Fact]
    public void A_secret_field_never_reaches_argv_whatever_it_holds()
    {
        var definition = Definition(Field("api-key", WizardFieldKind.Secret, "--api-key"));

        Assert.Equal(new[] { "setup", "sample" }, definition.BuildArgv(Values(("api-key", "sk-should-never-be-here"))));
    }

    [Fact]
    public void An_env_var_name_field_is_an_ordinary_flag_value()
    {
        var definition = Definition(Field("api-key-env", WizardFieldKind.EnvVarName, "--api-key-env"));

        Assert.Equal(
            new[] { "setup", "sample", "--api-key-env", "OPENAI_API_KEY" },
            definition.BuildArgv(Values(("api-key-env", "OPENAI_API_KEY"))));
    }

    // ------------------------------------------------------------------ positionals and gates

    [Fact]
    public void Positionals_come_first_in_their_own_order_and_flags_follow_in_definition_order()
    {
        var definition = Definition(
            Field("model", WizardFieldKind.Text, "--model"),
            Field("second", WizardFieldKind.Text, positional: true, order: 2),
            Field("first", WizardFieldKind.Text, positional: true, order: 1),
            Field("name", WizardFieldKind.Text, "--name"));

        var argv = definition.BuildArgv(Values(("model", "m"), ("second", "b"), ("first", "a"), ("name", "n")));

        Assert.Equal(new[] { "setup", "sample", "a", "b", "--model", "m", "--name", "n" }, argv);
    }

    [Fact]
    public void A_field_whose_gate_is_not_satisfied_is_not_emitted()
    {
        var definition = Definition(
            Field("subcommand", WizardFieldKind.Choice, positional: true),
            Field("add:token-name", WizardFieldKind.Text, "--name", gate: "subcommand", gateValues: new[] { "add" }),
            Field("list:limit", WizardFieldKind.Text, "--limit", gate: "subcommand", gateValues: new[] { "list" }));

        var argv = definition.BuildArgv(Values(("subcommand", "add"), ("add:token-name", "x"), ("list:limit", "5")));

        Assert.Equal(new[] { "setup", "sample", "add", "--name", "x" }, argv);
    }

    [Fact]
    public void A_gated_page_hides_its_fields_when_the_gate_is_closed()
    {
        var definition = new WizardDefinition
        {
            Target = "sample",
            Title = "Sample",
            Group = WizardGroups.Other,
            Steps = new[]
            {
                new WizardStep
                {
                    Id = "picker",
                    Title = "Picker",
                    Fields = new[] { Field("subcommand", WizardFieldKind.Choice, positional: true) },
                },
                new WizardStep
                {
                    Id = "add-page",
                    Title = "Add",
                    VisibleWhenFieldId = "subcommand",
                    VisibleWhenValues = new[] { "add" },
                    Fields = new[] { Field("name", WizardFieldKind.Text, "--name") },
                },
            },
        };

        Assert.Equal(new[] { "setup", "sample", "add", "--name", "n" }, definition.BuildArgv(Values(("subcommand", "add"), ("name", "n"))));
        Assert.Equal(new[] { "setup", "sample", "list" }, definition.BuildArgv(Values(("subcommand", "list"), ("name", "n"))));
    }

    // ------------------------------------------------------------------ what the review page says

    [Fact]
    public void Described_changes_compare_against_what_the_configuration_held_when_the_wizard_opened()
    {
        var definition = WizardBaseline.Apply(
            WizardSamples.ClaudeCode(),
            WizardSamples.Config("guardrail:\n  connectors:\n    claudecode:\n      mode: action\n      hook_fail_mode: closed\n"),
            configReadable: true);
        var values = WizardSamples.StartingValues(definition);
        Assert.Empty(definition.DescribeChanges(values));

        values["mode"] = "observe";
        values["restart"] = ToggleValues.Off;

        var lines = definition.DescribeChanges(values);
        Assert.Contains("Mode: action → observe", lines);
        Assert.Contains(lines, l => l.StartsWith("Restart:", StringComparison.Ordinal) && l.EndsWith("→ off", StringComparison.Ordinal));
    }

    [Fact]
    public void Visible_credentials_are_the_secret_fields_the_run_depends_on()
    {
        var definition = WizardSamples.Llm();

        var credentials = definition.VisibleCredentials(WizardSamples.StartingValues(definition)).ToList();

        Assert.Equal("--api-key", Assert.Single(credentials).Flag);
    }
}
