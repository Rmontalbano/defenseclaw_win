using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-268 in the editor: a FORM value with an error is held in its box (never written to the buffer) and turns Review &amp; Save off; a warning
/// is published like any edit; a choice keeps a value its list does not have. Loaded through <see cref="ConfigEditorHarness"/> - a real file in a
/// scratch folder, the masked source supplied in place of the CLI - and run against an LF and a CRLF file where the text of the buffer matters.
/// <para>
/// CUST-308's reason comes first for Save's tooltip, and the config.yaml / .env change notice (the editor's side of CUST-312) still never reloads
/// over an edit - which now includes a held value.
/// </para>
/// </summary>
public sealed class ConfigEditorFieldValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string Raw = """
        claw:
          mode: claudecode
        gateway:
          host: 127.0.0.1
          api_port: 18970
          tls_skip_verify: false
          token_env: DEFENSECLAW_GATEWAY_TOKEN
        guardrail:
          mode: observe
          hook_fail_mode: open
        llm:
          provider: openai
          model: gpt-4o
          base_url: http://localhost:11434
        skill_actions:
          critical:
            file: quarantine
            runtime: disable
            install: block
          high:
            file: quarantine
            runtime: disable
            install: block
          medium:
            file: none
            runtime: enable
            install: none
        config_version: 8
        """;

    /// <summary>The file, ending in a line break like a real config.yaml (a raw-string literal does not), as both the file and the masked source the CLI would print.</summary>
    private static string FileText(string? raw = null) => LineEndings.Normalize(raw ?? Raw) + "\n";

    private static Task<ConfigEditorHarness> LoadAsync(string eol = LineEndings.Lf, string? raw = null) =>
        ConfigEditorHarness.LoadAsync(eol, FileText(raw), FileText(raw));

    private static string Sk() => "s" + "k-" + new string('q', 40);

    private static void AssertSaveIs(ConfigEditorWindowViewModel vm, bool enabled)
    {
        Assert.Equal(enabled, vm.SaveCommand.CanExecute(null));
        Assert.Equal(enabled, vm.ReviewAndSaveCommand.CanExecute(null));
        Assert.Equal(enabled, vm.ConfirmReviewedSaveCommand.CanExecute(null));
    }

    private sealed class CannedPrompt
    {
        public CannedPrompt(ConfigEditorWindowViewModel vm, UnsavedChangesChoice answer)
        {
            vm.UnsavedChangesPrompt = request =>
            {
                Asked.Add(request);
                return Task.FromResult(answer);
            };
        }

        public List<UnsavedChangesRequest> Asked { get; } = new();
    }

    // ------------------------------------------------------------------ acceptance: 70000 in a port

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Seventy_thousand_in_a_port_shows_an_error_holds_the_value_back_and_turns_save_off(string eol)
    {
        using var harness = await LoadAsync(eol);
        var vm = harness.ViewModel;
        var port = harness.Field("gateway.api_port");
        AssertSaveIs(vm, true);
        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Equal("Review and save (Ctrl+S)", vm.SaveToolTip);

        port.NumberValue = 70000;

        Assert.True(port.Validation.IsError);
        Assert.Equal("Error: port must be between 1 and 65535", port.ValidationText);
        Assert.True(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, false);
        Assert.Equal("gateway.api_port: port must be between 1 and 65535", vm.FieldErrorsSummary);
        Assert.Equal("Fix the field error before saving — gateway.api_port: port must be between 1 and 65535", vm.FieldErrorsReason);
        Assert.Equal(vm.FieldErrorsReason, vm.SaveToolTip);
        Assert.DoesNotContain("70000", vm.FieldErrorsSummary, StringComparison.Ordinal);

        // Held back: the buffer is the file, nothing says "modified", and no refusal banner is raised for it.
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.Null(vm.FieldErrorMessage);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Fixing_it_publishes_the_good_value_and_turns_save_back_on(string eol)
    {
        using var harness = await LoadAsync(eol);
        var vm = harness.ViewModel;
        var port = harness.Field("gateway.api_port");
        port.NumberValue = 70000;

        port.NumberValue = 8080;

        Assert.True(port.Validation.IsOk);
        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Equal(string.Empty, vm.FieldErrorsSummary);
        Assert.Null(vm.FieldErrorsReason);
        AssertSaveIs(vm, true);
        Assert.Equal("Review and save (Ctrl+S)", vm.SaveToolTip);
        Assert.Equal(harness.Raw.Replace(harness.L("  api_port: 18970\n"), harness.L("  api_port: 8080\n"), StringComparison.Ordinal), vm.RawText);
        Assert.True(vm.IsRawModified);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
    }

    [Fact]
    public async Task Putting_the_old_value_back_clears_the_error_and_leaves_nothing_to_save()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var port = harness.Field("gateway.api_port");

        port.NumberValue = 70000;
        port.NumberValue = 18970;

        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task While_a_value_is_held_the_review_is_not_offered_even_to_a_caller_that_skips_can_execute()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        harness.Field("gateway.host").TextValue = "localhost";
        harness.Field("gateway.api_port").NumberValue = 70000;

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.ConfirmReviewedSaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewing);
        Assert.Null(vm.Review);
        Assert.False(vm.ShowSaveResultBanner);
        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(harness.ConfigPath)!, "config.yaml.bak-*"));
    }

    [Fact]
    public async Task A_value_still_in_its_box_that_turns_out_to_be_wrong_is_caught_when_the_review_is_asked_for()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        AssertSaveIs(vm, true);

        // What the window does at the start of a save: commit the focused FORM box, which is where the 70000 was typed.
        vm.CommitPendingEdits = () => harness.Field("gateway.api_port").NumberValue = 70000;
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewing);
        Assert.Null(vm.Review);
        Assert.True(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, false);
    }

    [Fact]
    public async Task Other_edits_still_go_through_while_one_value_is_held_and_the_held_one_is_not_among_them()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.api_port").NumberValue = 70000;
        harness.Field("gateway.host").TextValue = "localhost";

        Assert.Contains(harness.L("  host: localhost\n"), vm.RawText, StringComparison.Ordinal);
        Assert.Contains(harness.L("  api_port: 18970\n"), vm.RawText, StringComparison.Ordinal);
        Assert.True(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, false);
    }

    // ------------------------------------------------------------------ more than one, and the order of reasons

    [Fact]
    public async Task Every_held_value_is_listed_in_form_order_and_save_stays_off_until_the_last_is_fixed()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.token_env").TextValue = "my_token";
        harness.Field("gateway.api_port").NumberValue = 70000;

        Assert.Equal(
            "gateway.api_port: port must be between 1 and 65535" + Environment.NewLine +
            "gateway.token_env: env var names must match A-Z, 0-9, and underscores",
            vm.FieldErrorsSummary);
        Assert.StartsWith("Fix the 2 field errors before saving — gateway.api_port: ", vm.FieldErrorsReason, StringComparison.Ordinal);

        harness.Field("gateway.api_port").NumberValue = 8080;
        Assert.Equal("gateway.token_env: env var names must match A-Z, 0-9, and underscores", vm.FieldErrorsSummary);
        Assert.StartsWith("Fix the field error before saving — gateway.token_env: ", vm.FieldErrorsReason, StringComparison.Ordinal);
        AssertSaveIs(vm, false);

        harness.Field("gateway.token_env").TextValue = "OTHER_TOKEN";
        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);
    }

    [Fact]
    public async Task A_long_list_of_errors_is_cut_and_says_how_many_more()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        foreach (var severity in new[] { "critical", "high", "medium" })
        {
            foreach (var column in new[] { "file", "runtime", "install" })
            {
                harness.Field($"skill_actions.{severity}.{column}").TextValue = "bogus";
            }
        }

        var lines = vm.FieldErrorsSummary.Split(Environment.NewLine);
        Assert.Equal(7, lines.Length);
        Assert.Equal("skill_actions.critical.file: choose one of: none, quarantine", lines[0]);
        Assert.Equal("and 3 more", lines[^1]);
        Assert.StartsWith("Fix the 9 field errors before saving", vm.FieldErrorsReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installations_reason_comes_before_the_fields_and_the_fields_come_back_when_it_goes()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        harness.Field("gateway.api_port").NumberValue = 70000;
        Assert.Equal(vm.FieldErrorsReason, vm.SaveToolTip);

        harness.Services.Installation.Replace(TestInstallations.Managed(_temp.Path));

        Assert.Equal(TestInstallations.ManagedReason, vm.SaveToolTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
        AssertSaveIs(vm, false);

        // The field error is still true; it is just not the sentence the button gives.
        Assert.True(vm.HasBlockingFieldErrors);

        harness.Services.Installation.Replace(TestInstallations.UserDefault());

        Assert.Null(vm.ChangesBlockedReason);
        Assert.Equal(vm.FieldErrorsReason, vm.SaveToolTip);
        AssertSaveIs(vm, false);

        harness.Field("gateway.api_port").NumberValue = 18970;
        AssertSaveIs(vm, true);
    }

    [Fact]
    public async Task A_managed_installation_alone_still_says_only_its_own_sentence()
    {
        using var harness = await LoadAsync();
        harness.Services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var vm = harness.ViewModel;

        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Equal(TestInstallations.ManagedReason, vm.SaveToolTip);
        AssertSaveIs(vm, false);
    }

    // ------------------------------------------------------------------ warnings do not block

    [Fact]
    public async Task Turning_certificate_verification_off_warns_and_goes_through()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.tls_skip_verify").BoolValue = true;

        Assert.True(harness.Field("gateway.tls_skip_verify").Validation.IsWarning);
        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);
        Assert.Contains(harness.L("  tls_skip_verify: true\n"), vm.RawText, StringComparison.Ordinal);
        Assert.Null(vm.FieldErrorMessage);

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsReviewing);
    }

    [Fact]
    public async Task A_secret_typed_where_a_name_belongs_warns_goes_through_and_is_hidden_in_the_review()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var token = harness.Field("gateway.token_env");
        var secret = new string('q', 40);

        token.TextValue = secret;

        Assert.True(token.Validation.IsWarning);
        Assert.Contains("keys set", token.ValidationText, StringComparison.Ordinal);
        Assert.Contains(".env", token.ValidationText, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, token.ValidationText, StringComparison.Ordinal);
        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);
        Assert.Contains(secret, vm.RawText, StringComparison.Ordinal);

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewing);
        var shown = string.Join('\n', vm.Review!.Lines.Select(l => l.Text));
        Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
        Assert.Contains("token_env: " + ConfigDiffReviewBuilder.Hidden, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_hides_a_secret_in_an_env_key_whatever_its_shape_and_leaves_a_name_alone()
    {
        var secret = new string('q', 40);

        var masked = ConfigDiffReviewBuilder.MaskLines(new[]
        {
            "  token_env: " + secret,
            "  api_key_env: '" + secret + "'",
            "  api_key_env: OPENAI_API_KEY",
            "  token_env: ''",
        });

        Assert.Equal("  token_env: " + ConfigDiffReviewBuilder.Hidden, masked[0]);
        Assert.Equal("  api_key_env: " + ConfigDiffReviewBuilder.Hidden, masked[1]);
        Assert.Equal("  api_key_env: OPENAI_API_KEY", masked[2]);
        Assert.Equal("  token_env: ''", masked[3]);
    }

    [Fact]
    public async Task An_ordinary_env_var_name_is_not_hidden_in_the_review()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.token_env").TextValue = "ANOTHER_GATEWAY_TOKEN";
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        var shown = string.Join('\n', vm.Review!.Lines.Select(l => l.Text));
        Assert.Contains("token_env: ANOTHER_GATEWAY_TOKEN", shown, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the other rules, through the editor

    [Fact]
    public async Task A_url_without_a_scheme_is_held_back_and_a_good_one_goes_through()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var url = harness.Field("llm.base_url");

        url.TextValue = "localhost:11500";
        Assert.True(vm.HasBlockingFieldErrors);
        Assert.Equal("llm.base_url: expected a URL with scheme and host", vm.FieldErrorsSummary);
        Assert.Equal(harness.Raw, vm.RawText);

        url.TextValue = "https://" + "someone:" + "pw@host.example.test";
        Assert.Equal("llm.base_url: URL must not embed credentials", vm.FieldErrorsSummary);
        Assert.DoesNotContain("someone", vm.SaveToolTip, StringComparison.Ordinal);
        Assert.Equal(harness.Raw, vm.RawText);

        url.TextValue = "http://localhost:11500";
        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Contains(harness.L("  base_url: http://localhost:11500\n"), vm.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_negative_timeout_is_held_back()
    {
        using var harness = await LoadAsync(raw: Raw.Replace("model: gpt-4o", "model: gpt-4o\n  timeout: 30", StringComparison.Ordinal));
        var vm = harness.ViewModel;

        harness.Field("llm.timeout").NumberValue = -1;

        Assert.Equal("llm.timeout: value must be zero or greater", vm.FieldErrorsSummary);
        AssertSaveIs(vm, false);
    }

    // ------------------------------------------------------------------ choices, through the editor

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_choice_changes_one_line_and_only_to_a_listed_value(string eol)
    {
        using var harness = await LoadAsync(eol);
        var vm = harness.ViewModel;
        var mode = harness.Field("guardrail.mode");

        mode.SelectedChoice = "action";

        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Equal(harness.Raw.Replace(harness.L("  mode: observe\n"), harness.L("  mode: action\n"), StringComparison.Ordinal), vm.RawText);

        mode.TextValue = "enforce";
        Assert.True(vm.HasBlockingFieldErrors);
        Assert.Equal("guardrail.mode: choose one of: observe, action", vm.FieldErrorsSummary);
        Assert.Contains(harness.L("  mode: action\n"), vm.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_value_the_list_does_not_have_survives_load_edit_elsewhere_and_save_untouched(string eol)
    {
        var withOpenClaw = Raw.Replace("mode: claudecode", "mode: openclaw", StringComparison.Ordinal);
        using var harness = await LoadAsync(eol, withOpenClaw);
        var vm = harness.ViewModel;
        var mode = harness.Field("claw.mode");

        // Opening the form changed nothing, and the field says so.
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.Equal("openclaw", mode.SelectedChoice);
        Assert.Contains(mode.Choices, c => c.Value == "openclaw");
        Assert.DoesNotContain(mode.ChoiceValues, v => v is "openclaw" or "zeptoclaw");
        Assert.True(mode.Validation.IsWarning);
        Assert.False(vm.HasBlockingFieldErrors);

        // An edit somewhere else leaves it exactly as it was.
        harness.Field("gateway.host").TextValue = "localhost";
        Assert.Contains(harness.L("  mode: openclaw\n"), vm.RawText, StringComparison.Ordinal);
        AssertSaveIs(vm, true);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains(harness.L("  mode: openclaw\n"), File.ReadAllText(harness.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_another_value_and_choosing_the_old_one_back_ends_where_it_began()
    {
        var withOpenClaw = Raw.Replace("mode: claudecode", "mode: openclaw", StringComparison.Ordinal);
        using var harness = await LoadAsync(raw: withOpenClaw);
        var vm = harness.ViewModel;
        var mode = harness.Field("claw.mode");

        mode.SelectedChoice = "codex";
        Assert.Contains(harness.L("  mode: codex\n"), vm.RawText, StringComparison.Ordinal);
        Assert.True(vm.IsRawModified);

        mode.SelectedChoice = "openclaw";
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.False(vm.HasBlockingFieldErrors);
    }

    [Fact]
    public async Task A_questionable_value_that_is_already_in_the_file_never_stops_an_unrelated_save()
    {
        var questionable = Raw
            .Replace("api_port: 18970", "api_port: 70000", StringComparison.Ordinal)
            .Replace("base_url: http://localhost:11434", "base_url: localhost:11434", StringComparison.Ordinal);
        using var harness = await LoadAsync(raw: questionable);
        var vm = harness.ViewModel;

        Assert.True(harness.Field("gateway.api_port").Validation.IsWarning);
        Assert.True(harness.Field("llm.base_url").Validation.IsWarning);
        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);

        harness.Field("llm.model").TextValue = "gpt-4.1";
        AssertSaveIs(vm, true);
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsReviewing);
    }

    [Fact]
    public async Task The_guardrail_hook_fail_mode_is_read_only_in_the_editor()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var failMode = harness.Field("guardrail.hook_fail_mode");

        failMode.TextValue = "closed";

        Assert.False(failMode.IsEditable);
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("Setup panel", failMode.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_mask_placeholder_in_a_choice_is_refused_as_before_not_held_as_an_error()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("guardrail.mode").TextValue = "[REDACTED]";

        Assert.NotNull(vm.FieldErrorMessage);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);
        Assert.Equal(harness.Raw, vm.RawText);
    }

    // ------------------------------------------------------------------ which runtime's connectors

    [Theory]
    [InlineData(null, false)]
    [InlineData("0.8.10", false)]
    [InlineData("95159fd", true)]
    public async Task The_connector_lists_follow_the_runtime_the_probe_found(string? runtime, bool listsKiro)
    {
        var raw = FileText(Raw.Replace("mode: claudecode", "mode: kiro", StringComparison.Ordinal));
        using var harness = await ConfigEditorHarness.LoadAsync(
            raw: raw,
            maskedSource: raw,
            runtimeProbeRunner: runtime is null ? null : RuntimeFixtureRunner.For(runtime));
        var mode = harness.Field("claw.mode");

        Assert.Equal(listsKiro, mode.ChoiceValues.Contains("kiro"));
        Assert.Equal(listsKiro, mode.Validation.IsOk);
        Assert.Equal(!listsKiro, mode.Validation.IsWarning);
        Assert.DoesNotContain("openclaw", mode.ChoiceValues);
        Assert.DoesNotContain("zeptoclaw", mode.ChoiceValues);
        Assert.Contains("claudecode", mode.ChoiceValues);
        Assert.Equal("kiro", mode.SelectedChoice);
    }

    // ------------------------------------------------------------------ a held value is an edit

    [Fact]
    public async Task A_held_value_counts_as_unsaved_so_closing_asks_and_cannot_offer_a_save()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);
        harness.Field("gateway.api_port").NumberValue = 70000;

        Assert.True(vm.CloseNeedsConfirmation());
        Assert.False(await vm.ConfirmCloseAsync());

        var request = Assert.Single(prompt.Asked);
        Assert.False(request.CanSave);
        Assert.Null(request.SaveLabel);
        Assert.Equal(UnsavedChangesChoice.Cancel, request.DefaultChoice);
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.True(vm.HasBlockingFieldErrors);
    }

    [Fact]
    public async Task Discarding_a_held_value_lets_the_close_go_ahead()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Discard);
        harness.Field("gateway.api_port").NumberValue = 70000;

        Assert.True(await vm.ConfirmCloseAsync());
    }

    [Fact]
    public async Task A_value_still_in_its_box_is_committed_before_closing_decides_and_an_error_in_it_counts()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        vm.CommitPendingEdits = () => harness.Field("gateway.api_port").NumberValue = 70000;

        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.CloseNeedsConfirmation());
        Assert.True(vm.HasBlockingFieldErrors);
    }

    [Fact]
    public async Task A_file_that_changes_under_a_held_value_is_never_reloaded_over_it()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        harness.Field("gateway.api_port").NumberValue = 70000;

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();

        Assert.True(vm.ShowExternalChangeNotice);
        Assert.True(vm.HasBlockingFieldErrors);
        Assert.Equal(harness.Raw, vm.RawText);
    }

    [Fact]
    public async Task Reload_asks_about_a_held_value_and_drops_it_with_the_tree_it_belonged_to()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Discard);
        harness.Field("gateway.api_port").NumberValue = 70000;

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Single(prompt.Asked);
        Assert.False(vm.HasBlockingFieldErrors);
        Assert.Equal(string.Empty, vm.FieldErrorsSummary);
        Assert.False(vm.HasUnsavedChanges);
        AssertSaveIs(vm, true);
        Assert.True(harness.Field("gateway.api_port").Validation.IsOk);
        Assert.Equal(18970d, harness.Field("gateway.api_port").NumberValue);
    }

    [Fact]
    public async Task Rebuilding_the_form_after_a_RAW_edit_drops_the_held_values_with_their_boxes()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        harness.Field("gateway.api_port").NumberValue = 70000;
        Assert.True(vm.IsFormEditable);

        vm.RawText = harness.Raw + harness.L("# a note typed in RAW\n");
        Assert.False(vm.IsFormEditable);
        Assert.True(vm.HasBlockingFieldErrors);

        vm.NotifyFormTabSelected();

        Assert.True(vm.IsFormEditable);
        Assert.False(vm.HasBlockingFieldErrors);
        AssertSaveIs(vm, true);
        Assert.True(harness.Field("gateway.api_port").Validation.IsOk);
        Assert.EndsWith(harness.L("# a note typed in RAW\n"), vm.RawText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the editor's own notifications

    [Fact]
    public async Task What_the_window_binds_to_is_raised_when_a_value_is_held_and_when_it_is_fixed()
    {
        using var harness = await LoadAsync();
        var vm = harness.ViewModel;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var canExecuteChanged = 0;
        vm.ReviewAndSaveCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        harness.Field("gateway.api_port").NumberValue = 70000;

        Assert.Contains(nameof(ConfigEditorWindowViewModel.HasBlockingFieldErrors), raised);
        Assert.Contains(nameof(ConfigEditorWindowViewModel.FieldErrorsSummary), raised);
        Assert.Contains(nameof(ConfigEditorWindowViewModel.SaveToolTip), raised);
        Assert.Contains(nameof(ConfigEditorWindowViewModel.HasUnsavedChanges), raised);
        Assert.True(canExecuteChanged > 0);

        raised.Clear();
        canExecuteChanged = 0;
        harness.Field("gateway.api_port").NumberValue = 18970;

        Assert.Contains(nameof(ConfigEditorWindowViewModel.HasBlockingFieldErrors), raised);
        Assert.True(canExecuteChanged > 0);
    }
}
