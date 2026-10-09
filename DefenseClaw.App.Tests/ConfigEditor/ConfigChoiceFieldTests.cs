using System.ComponentModel;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-268 at the form builder and the field: which keys become a Choice (and with which list), what a value the list does not have does,
/// the hints, and what a field says about the value it holds. The view-model side - Review &amp; Save - is <c>ConfigEditorFieldValidationTests</c>.
/// </summary>
public class ConfigChoiceFieldTests
{
    private static readonly ConfigChoiceProfile Windows = new("windows", false);

    private const string Sample = """
        claw:
          mode: claudecode
          home_dir: ''
        gateway:
          host: 127.0.0.1
          api_port: 18970
          tls_skip_verify: false
          token_env: DEFENSECLAW_GATEWAY_TOKEN
        guardrail:
          mode: observe
          scanner_mode: local
          hook_fail_mode: open
          connector: claudecode
          connectors:
            claudecode:
              mode: observe
              hook_fail_mode: open
              block_message: ''
        claude_code:
          mode: ''
          fail_mode: ''
        llm:
          provider: openai
          model: gpt-4o
          base_url: http://localhost:11434
          api_key_env: OPENAI_API_KEY
        skill_actions:
          critical:
            file: quarantine
            runtime: disable
            install: block
        config_version: 8
        """;

    /// <summary>A form built from a masked source, with every commit its fields made.</summary>
    private sealed class Built(ConfigFormBuilder.BuildResult result, List<FormField> commits)
    {
        public ConfigFormBuilder.BuildResult Result { get; } = result;

        public List<FormField> Commits { get; } = commits;

        public FormField Field(string path) => All(Result.Sections).SelectMany(g => g.Fields).Single(f => f.Path == path);

        private static IEnumerable<FormGroup> All(IEnumerable<FormGroup> groups)
        {
            foreach (var group in groups)
            {
                yield return group;
                foreach (var nested in All(group.SubGroups))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <param name="yaml">The masked source the CLI would print (the file itself, when nothing is masked); defaults to <see cref="Sample"/>.</param>
    /// <param name="raw">The file on disk; defaults to <paramref name="yaml"/>.</param>
    private static Built Build(string? yaml = null, string? raw = null, ConfigChoiceProfile? profile = null)
    {
        var source = LineEndings.Normalize(yaml ?? Sample) + "\n";
        var file = LineEndings.Normalize(raw ?? yaml ?? Sample) + "\n";
        var commits = new List<FormField>();
        var result = ConfigFormBuilder.Build(source, ConfigStore.Parse(file), commits.Add, _ => { }, profile ?? Windows);
        return new Built(result, commits);
    }

    // ------------------------------------------------------------------ claw.mode: a combo without openclaw / zeptoclaw

    [Fact]
    public void Claw_mode_is_a_choice_whose_list_on_windows_has_neither_openclaw_nor_zeptoclaw()
    {
        var mode = Build().Field("claw.mode");

        Assert.Equal(FormFieldKind.Choice, mode.Kind);
        Assert.True(mode.IsEditable);
        Assert.Equal(new[] { "codex", "claudecode" }, mode.ChoiceValues);
        Assert.Equal(new[] { "codex", "claudecode" }, mode.Choices.Select(c => c.Value));
        Assert.DoesNotContain(mode.Choices, c => c.Value is "openclaw" or "zeptoclaw");
        Assert.Equal("claudecode", mode.TextValue);
        Assert.Equal("claudecode", mode.SelectedChoice);
        Assert.Equal("Active agent framework.", mode.Hint);
        Assert.Contains("choice setting", mode.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_key_on_a_mac_keeps_every_connector()
    {
        var mode = Build(profile: new ConfigChoiceProfile("darwin", false)).Field("claw.mode");

        Assert.Equal(13, mode.ChoiceValues.Count);
        Assert.Contains("openclaw", mode.ChoiceValues);
        Assert.Contains("zeptoclaw", mode.ChoiceValues);
    }

    [Fact]
    public void The_pinned_runtimes_connectors_make_a_value_it_has_a_listed_one_and_0810s_make_it_an_extra_item()
    {
        var raw = "claw:\n  mode: kiro\n";

        var pinned = Build(raw, profile: new ConfigChoiceProfile("windows", true)).Field("claw.mode");
        Assert.Contains("kiro", pinned.ChoiceValues);
        Assert.Equal(pinned.ChoiceValues.Count, pinned.Choices.Count);
        Assert.True(pinned.Validation.IsOk);

        var installed = Build(raw, profile: Windows).Field("claw.mode");
        Assert.DoesNotContain("kiro", installed.ChoiceValues);
        Assert.Equal("kiro", installed.Choices[^1].Value);
        Assert.True(installed.Validation.IsWarning);
    }

    // ------------------------------------------------------------------ a value the list does not have is shown and kept

    [Theory]
    [InlineData("openclaw")]
    [InlineData("zeptoclaw")]
    [InlineData("cursor")]
    [InlineData("something-new")]
    public void A_value_the_list_does_not_have_is_shown_selected_and_never_replaced(string value)
    {
        var built = Build($"claw:\n  mode: {value}\n");
        var mode = built.Field("claw.mode");

        Assert.Equal(FormFieldKind.Choice, mode.Kind);
        Assert.Equal(value, mode.TextValue);
        Assert.Equal(value, mode.SelectedChoice);
        Assert.Equal(new[] { "codex", "claudecode", value }, mode.Choices.Select(c => c.Value));
        Assert.Equal(value, mode.Choices[^1].Label);
        Assert.Equal(value, mode.CurrentRawValue);
        Assert.False(mode.IsChanged);
        Assert.False(mode.IsDirty);
        Assert.Empty(built.Commits);
    }

    [Fact]
    public void An_unknown_value_is_a_warning_that_says_it_is_kept_never_an_error()
    {
        var mode = Build("claw:\n  mode: openclaw\n").Field("claw.mode");

        Assert.True(mode.Validation.IsWarning);
        Assert.False(mode.Validation.IsError);
        Assert.Equal("already in config.yaml, kept as is: choose one of: codex, claudecode", mode.Validation.Message);
        Assert.StartsWith("Warning: already in config.yaml, kept as is", mode.ValidationText, StringComparison.Ordinal);
        Assert.Equal("Warn", mode.ValidationTone);
        Assert.True(mode.ShowValidation);
    }

    [Fact]
    public void A_combo_that_clears_its_selection_cannot_blank_the_value()
    {
        var built = Build("claw:\n  mode: openclaw\n");
        var mode = built.Field("claw.mode");

        mode.SelectedChoice = null;

        Assert.Equal("openclaw", mode.TextValue);
        Assert.Empty(built.Commits);
    }

    [Fact]
    public void Picking_a_listed_value_is_the_only_thing_that_changes_it()
    {
        var built = Build("claw:\n  mode: openclaw\n");
        var mode = built.Field("claw.mode");

        mode.SelectedChoice = "codex";

        Assert.Equal("codex", mode.TextValue);
        Assert.Equal("codex", mode.CurrentRawValue);
        Assert.True(mode.IsChanged);
        Assert.True(mode.IsDirty);
        Assert.True(mode.Validation.IsOk);
        Assert.Same(mode, Assert.Single(built.Commits));
    }

    [Fact]
    public void Putting_the_original_value_back_is_not_a_change_and_is_not_an_error()
    {
        var built = Build("claw:\n  mode: openclaw\n");
        var mode = built.Field("claw.mode");

        mode.SelectedChoice = "codex";
        mode.SelectedChoice = "openclaw";

        Assert.False(mode.IsChanged);
        Assert.False(mode.Validation.IsError);
        Assert.Equal(2, built.Commits.Count);
    }

    [Fact]
    public void A_value_typed_into_a_choice_that_is_not_in_the_list_is_an_error_to_fix()
    {
        var built = Build();
        var mode = built.Field("guardrail.mode");

        mode.TextValue = "enforce";

        Assert.True(mode.Validation.IsError);
        Assert.Equal("choose one of: observe, action", mode.Validation.Message);
        Assert.Equal("Critical", mode.ValidationTone);
    }

    [Fact]
    public void A_blank_the_list_offers_is_a_listed_item_named_blank_and_raises_nothing()
    {
        var failMode = Build().Field("claude_code.fail_mode");

        Assert.Equal(FormFieldKind.Choice, failMode.Kind);
        Assert.Equal(new[] { string.Empty, "open", "closed" }, failMode.Choices.Select(c => c.Value));
        Assert.Equal(new[] { "(blank)", "open", "closed" }, failMode.Choices.Select(c => c.Label));
        Assert.Equal(string.Empty, failMode.SelectedChoice);
        Assert.True(failMode.Validation.IsOk);
        Assert.Equal("Legacy policy-layer hint.", failMode.Hint);
    }

    [Fact]
    public void A_choice_formats_a_plain_word_plain_and_the_empty_string_quoted()
    {
        var failMode = Build().Field("claude_code.fail_mode");

        failMode.SelectedChoice = "closed";
        Assert.Equal("closed", failMode.CurrentRawValue);

        failMode.SelectedChoice = string.Empty;
        Assert.Equal("''", failMode.CurrentRawValue);
    }

    // ------------------------------------------------------------------ which keys become choices, and which do not

    [Fact]
    public void The_lists_the_issue_names_are_all_choices()
    {
        var built = Build();

        Assert.Equal(FormFieldKind.Choice, built.Field("llm.provider").Kind);
        Assert.Equal(ConfigFieldCatalog.LlmProviders, built.Field("llm.provider").ChoiceValues);
        Assert.Equal(FormFieldKind.Choice, built.Field("claude_code.fail_mode").Kind);
        Assert.Equal(FormFieldKind.Choice, built.Field("guardrail.mode").Kind);
        Assert.Equal(new[] { "observe", "action" }, built.Field("guardrail.mode").ChoiceValues);
        Assert.Equal(new[] { "observe", "action" }, built.Field("guardrail.connectors.claudecode.mode").ChoiceValues);
        Assert.Equal(new[] { string.Empty, "codex", "claudecode" }, built.Field("guardrail.connector").ChoiceValues);

        foreach (var (column, options) in new[] { ("file", new[] { "none", "quarantine" }), ("runtime", new[] { "enable", "disable" }), ("install", new[] { "none", "block", "allow" }) })
        {
            var cell = built.Field($"skill_actions.critical.{column}");
            Assert.Equal(FormFieldKind.Choice, cell.Kind);
            Assert.Equal(options, cell.ChoiceValues);
            Assert.StartsWith("On CRITICAL: ", cell.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Free_text_and_numbers_and_switches_stay_what_they_were()
    {
        var built = Build();

        Assert.Equal(FormFieldKind.String, built.Field("llm.model").Kind);
        Assert.Equal(FormFieldKind.String, built.Field("gateway.host").Kind);
        Assert.Equal(FormFieldKind.Int, built.Field("gateway.api_port").Kind);
        Assert.Equal(FormFieldKind.Bool, built.Field("gateway.tls_skip_verify").Kind);
        Assert.Equal(FormFieldKind.EnvName, built.Field("gateway.token_env").Kind);
        Assert.Empty(built.Field("llm.model").Choices);
    }

    [Fact]
    public void A_plain_true_or_integer_under_a_choice_key_keeps_its_own_control()
    {
        var built = Build("guardrail:\n  mode: true\nllm:\n  provider: 5\n");

        Assert.Equal(FormFieldKind.Bool, built.Field("guardrail.mode").Kind);
        Assert.Equal(FormFieldKind.Int, built.Field("llm.provider").Kind);
    }

    [Fact]
    public void A_plain_null_under_a_choice_key_is_read_only_so_its_type_is_kept()
    {
        var mode = Build("claw:\n  mode: null\n").Field("claw.mode");

        Assert.Equal(FormFieldKind.Choice, mode.Kind);
        Assert.False(mode.IsEditable);
        Assert.Contains("null/yes/no", mode.DisabledReason, StringComparison.Ordinal);
        Assert.True(mode.Validation.IsOk);
    }

    [Fact]
    public void A_masked_value_in_a_choice_key_is_read_only_and_never_checked_and_a_secret_stays_a_secret()
    {
        var built = Build(
            yaml: "llm:\n  provider: '[REDACTED]'\n  api_key: '[REDACTED]'\n",
            raw: "llm:\n  provider: openai\n  api_key: sample-key\n");

        var provider = built.Field("llm.provider");
        Assert.False(provider.IsEditable);
        Assert.True(provider.IsMasked);
        Assert.True(provider.Validation.IsOk);
        Assert.Equal(FormFieldKind.Secret, built.Field("llm.api_key").Kind);
    }

    [Fact]
    public void A_key_the_raw_file_does_not_hold_alone_is_still_read_only_with_the_reason()
    {
        var provider = Build(yaml: "llm:\n  provider: openai\n", raw: "llm:\n  provider: openai # trailing\n").Field("llm.provider");

        Assert.Equal(FormFieldKind.Choice, provider.Kind);
        Assert.False(provider.IsEditable);
        Assert.Contains("trailing comment", provider.DisabledReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the hook fail mode is read-only, as it is in the TUI

    [Theory]
    [InlineData("guardrail.hook_fail_mode")]
    [InlineData("guardrail.connectors.claudecode.hook_fail_mode")]
    public void The_guardrail_hook_fail_mode_shows_its_value_and_cannot_be_edited_here(string path)
    {
        var built = Build();
        var field = built.Field(path);

        Assert.Equal(FormFieldKind.String, field.Kind);
        Assert.Equal("open", field.TextValue);
        Assert.False(field.IsEditable);
        Assert.True(field.ShowReadOnlyNote);
        Assert.Contains("Setup panel", field.DisabledReason, StringComparison.Ordinal);

        field.TextValue = "closed";

        Assert.Empty(built.Commits);
        Assert.False(field.IsDirty);
        Assert.True(field.Validation.IsOk);
    }

    // ------------------------------------------------------------------ hints

    [Fact]
    public void A_field_carries_the_tuis_hint_and_shows_it_with_the_path_in_its_tooltip_and_help_text()
    {
        var port = Build().Field("gateway.api_port");

        Assert.Equal("REST sidecar port.", port.Hint);
        Assert.True(port.HasHint);
        Assert.Equal("REST sidecar port." + Environment.NewLine + "gateway.api_port", port.ToolTipText);
        Assert.Equal("REST sidecar port. gateway.api_port", port.HelpText);
    }

    [Fact]
    public void A_field_the_tui_does_not_list_has_no_hint_and_the_path_stands_alone()
    {
        var other = Build("somewhere:\n  else: 1\n").Field("somewhere.else");

        Assert.Null(other.Hint);
        Assert.False(other.HasHint);
        Assert.Equal("somewhere.else", other.ToolTipText);
        Assert.Equal("somewhere.else", other.HelpText);
    }

    [Fact]
    public void A_per_connector_hint_names_the_connector()
    {
        var mode = Build().Field("guardrail.connectors.claudecode.mode");

        Assert.Equal("Per-connector mode for claudecode (blank inherits the global mode).", mode.Hint);
    }

    // ------------------------------------------------------------------ what a field says about its value

    [Fact]
    public void Seventy_thousand_in_a_port_is_an_error_and_the_old_port_is_fine_again()
    {
        var built = Build();
        var port = built.Field("gateway.api_port");
        Assert.True(port.Validation.IsOk);

        port.NumberValue = 70000;

        Assert.True(port.Validation.IsError);
        Assert.Equal("port must be between 1 and 65535", port.Validation.Message);
        Assert.Equal("Error: port must be between 1 and 65535", port.ValidationText);
        Assert.Equal("Critical", port.ValidationTone);
        Assert.True(port.ShowValidation);
        Assert.True(port.IsChanged);
        Assert.Same(port, Assert.Single(built.Commits));

        port.NumberValue = 18970;

        Assert.True(port.Validation.IsOk);
        Assert.False(port.ShowValidation);
        Assert.False(port.IsChanged);
    }

    [Fact]
    public void A_value_that_is_already_wrong_in_the_file_is_a_warning_until_the_operator_changes_it()
    {
        var port = Build("gateway:\n  api_port: 70000\n").Field("gateway.api_port");

        Assert.True(port.Validation.IsWarning);
        Assert.False(port.Validation.IsError);
        Assert.Equal("already in config.yaml, kept as is: port must be between 1 and 65535", port.Validation.Message);

        port.NumberValue = 70001;
        Assert.True(port.Validation.IsError);

        port.NumberValue = 70000;
        Assert.True(port.Validation.IsWarning);
    }

    [Fact]
    public void Certificate_verification_off_is_a_warning_when_loaded_and_when_switched_on()
    {
        var built = Build();
        var skip = built.Field("gateway.tls_skip_verify");
        Assert.True(skip.Validation.IsOk);

        skip.BoolValue = true;
        Assert.True(skip.Validation.IsWarning);
        Assert.Equal("TLS verification is disabled; dev-only", skip.Validation.Message);

        skip.BoolValue = false;
        Assert.True(skip.Validation.IsOk);

        var loadedOn = Build("gateway:\n  tls_skip_verify: true\n").Field("gateway.tls_skip_verify");
        Assert.True(loadedOn.Validation.IsWarning);
        Assert.Equal("TLS verification is disabled; dev-only", loadedOn.Validation.Message);
    }

    [Fact]
    public void An_env_var_name_is_checked_and_a_secret_typed_there_is_a_warning_that_never_repeats_it()
    {
        var env = Build().Field("gateway.token_env");
        var secret = "s" + "k-" + new string('q', 40);

        env.TextValue = "my_token";
        Assert.True(env.Validation.IsError);
        Assert.Equal("env var names must match A-Z, 0-9, and underscores", env.Validation.Message);

        env.TextValue = secret;
        Assert.True(env.Validation.IsWarning);
        Assert.Contains("keys set", env.Validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, env.ValidationText, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, env.ToString(), StringComparison.Ordinal);

        env.TextValue = "OTHER_TOKEN";
        Assert.True(env.Validation.IsOk);
    }

    [Fact]
    public void A_url_is_checked_for_a_scheme_a_host_and_no_credentials()
    {
        var url = Build().Field("llm.base_url");

        url.TextValue = "localhost:11434";
        Assert.True(url.Validation.IsError);
        Assert.Equal("expected a URL with scheme and host", url.Validation.Message);

        url.TextValue = "https://" + "someone:" + "pw@host.example.test";
        Assert.True(url.Validation.IsError);
        Assert.Equal("URL must not embed credentials", url.Validation.Message);
        Assert.DoesNotContain("someone", url.ValidationText, StringComparison.Ordinal);

        url.TextValue = "ftp://files.example.test";
        Assert.True(url.Validation.IsWarning);

        url.TextValue = "http://localhost:11434";
        Assert.True(url.Validation.IsOk);
    }

    [Fact]
    public void A_field_that_cannot_be_edited_is_never_checked()
    {
        var built = Build(
            yaml: "gateway:\n  api_port: 70000\nllm:\n  base_url: https://h.example.test/[REDACTED]\n",
            raw: "gateway: {api_port: 70000}\nllm:\n  base_url: https://u:p@h.example.test/secret\n");

        Assert.False(built.Field("gateway.api_port").IsEditable);
        Assert.True(built.Field("gateway.api_port").Validation.IsOk);
        Assert.False(built.Field("llm.base_url").IsEditable);
        Assert.True(built.Field("llm.base_url").Validation.IsOk);
    }

    [Fact]
    public void A_field_that_changes_raises_the_properties_its_row_binds_to()
    {
        var port = Build().Field("gateway.api_port");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)port).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        port.NumberValue = 70000;

        Assert.Contains(nameof(FormField.Validation), raised);
        Assert.Contains(nameof(FormField.ShowValidation), raised);
        Assert.Contains(nameof(FormField.ValidationText), raised);
        Assert.Contains(nameof(FormField.ValidationTone), raised);
    }

    [Fact]
    public void Changed_means_different_from_what_was_loaded_for_every_kind()
    {
        var built = Build();
        var host = built.Field("gateway.host");
        var port = built.Field("gateway.api_port");
        var skip = built.Field("gateway.tls_skip_verify");

        Assert.False(host.IsChanged || port.IsChanged || skip.IsChanged);

        host.TextValue = "localhost";
        port.NumberValue = 4000.4;
        skip.BoolValue = true;
        Assert.True(host.IsChanged && port.IsChanged && skip.IsChanged);
        Assert.Equal("4000", port.CurrentRawValue);

        host.TextValue = "127.0.0.1";
        port.NumberValue = 18970;
        skip.BoolValue = false;
        Assert.False(host.IsChanged || port.IsChanged || skip.IsChanged);
    }
}
