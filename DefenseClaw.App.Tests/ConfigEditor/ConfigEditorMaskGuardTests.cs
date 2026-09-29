using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The view-model side of "a masked value never comes back": a typed placeholder is refused, a patch that
/// raises the number of placeholders is refused, and neither leaves a trace in the RAW text. The masked source
/// view is supplied through <see cref="DefenseClaw.App.ViewModels.ConfigEditor.ConfigEditorWindowViewModel.FormSourceOverride"/>,
/// so no CLI is involved.
/// <para>
/// Every test that reads or compares config text runs twice, against an LF file and against a CRLF one (theory data
/// from <see cref="LineEndings"/>): the file the DefenseClaw CLI writes on Windows is CRLF, and the same guards must
/// hold there. Expectations are written with LF literals and converted through <see cref="ConfigEditorHarness.L"/>, so
/// they mean the same thing however this source file was checked out.
/// </para>
/// </summary>
public sealed class ConfigEditorMaskGuardTests
{
    public static TheoryData<string, string> TypedMasks { get; } = LineEndings.Each(
        "[REDACTED]",
        "[redacted]",
        "[REDACTED_URL]",
        "https://h.example.test/[REDACTED]",
        "***",
        "abcd***wxyz",
        "***REDACTED***");

    public static TheoryData<string, string> MaskSpellings { get; } = LineEndings.Each("***", "[REDACTED_URL]", "abc***def");

    // ------------------------------------------------------------------ loading

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Loading_builds_the_form_from_the_masked_source_and_flags_secret_references(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.Null(vm.FormUnavailableReason);
        Assert.Equal(5, vm.Sections.Count);
        Assert.True(vm.IsFormEditable);
        Assert.True(vm.HasSecretReferences);
        Assert.Contains("secret references", vm.WindowTitle, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Without_the_cli_the_form_is_unavailable_but_raw_still_loads(string eol)
    {
        // No override: the real read path runs, against a runner whose PATH is empty. It ends in
        // CliNotFoundException before any process exists, so nothing outside the scratch directory is touched.
        using var harness = await ConfigEditorHarness.LoadAsync(eol, withoutCli: true);
        var vm = harness.ViewModel;

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Empty(vm.Sections);
        Assert.NotNull(vm.FormUnavailableReason);
        Assert.Contains("not found", vm.FormUnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowFormEmptyState);
    }

    // ------------------------------------------------------------------ typed mask-like values are refused

    [Theory]
    [MemberData(nameof(TypedMasks))]
    public async Task A_typed_mask_placeholder_is_refused_and_never_reaches_the_raw_text(string typed, string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.Field("llm.model").TextValue = typed;

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.NotNull(vm.FieldErrorMessage);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_typed_mask_placeholder_is_refused_for_an_env_name_field_too(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.Field("gateway.token_env").TextValue = "[REDACTED]";

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Fields_built_read_only_cannot_reach_the_raw_text_by_any_route(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.Field("gateway.token").TextValue = "typed-over-the-secret";
        harness.Field("llm.endpoint").TextValue = "https://elsewhere.example.test/x";
        harness.Field("llm.headers.Authorization").TextValue = "typed";
        harness.Field("llm.headers.Accept").TextValue = "text/plain";
        harness.Field("config_version").NumberValue = 99;
        harness.Field("llm.temperature").TextValue = "0.9";

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_typed_placeholder_is_refused_for_an_editable_list_and_the_list_that_holds_masks_is_locked(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.List("guardrail.scan_roots").Items.Add("[REDACTED]");
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);

        harness.List("guardrail.allowed_hosts").Items.Add("another.example.test");
        harness.List("guardrail.api_keys").Items.Add("key-three");
        Assert.Equal(harness.Raw, vm.RawText);
    }

    // ------------------------------------------------------------------ legitimate edits still go through

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_string_edit_changes_only_its_own_line_and_clears_a_previous_refusal(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.Field("guardrail.mode").TextValue = "[REDACTED]";
        Assert.NotNull(vm.FieldErrorMessage);

        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Null(vm.FieldErrorMessage);
        Assert.True(vm.IsRawModified);
        Assert.Equal(Replaced(harness, "  mode: observe\n", "  mode: action\n"), vm.RawText);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
        Assert.True(vm.IsFormEditable);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Integer_and_boolean_edits_are_written_in_their_own_types(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.Field("gateway.api_port").NumberValue = 4000;
        harness.Field("guardrail.enabled").BoolValue = false;
        harness.Field("llm.max_tokens").NumberValue = 8192;

        Assert.Null(vm.FieldErrorMessage);
        Assert.Contains(harness.L("  api_port: 4000\n"), vm.RawText, StringComparison.Ordinal);
        Assert.Contains(harness.L("  enabled: false\n"), vm.RawText, StringComparison.Ordinal);
        Assert.Contains(harness.L("  max_tokens: 8192\n"), vm.RawText, StringComparison.Ordinal);
        Assert.Contains(harness.L("  token: sample-not-a-real-token\n"), vm.RawText, StringComparison.Ordinal);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_list_edit_rewrites_only_that_list(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        harness.List("guardrail.scan_roots").Items.Add("E:\\work");

        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(Replaced(harness, "  - D:\\repos\n", "  - D:\\repos\n  - E:\\work\n"), vm.RawText);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Every_section_other_than_the_edited_one_is_byte_identical_after_an_edit(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var before = ConfigStore.Parse(harness.Raw);

        harness.Field("gateway.host").TextValue = "localhost";

        var after = ConfigStore.Parse(vm.RawText);
        Assert.NotEqual(before.SectionText("gateway"), after.SectionText("gateway"));
        foreach (var (name, text) in before.Sections.Where(s => s.Key != "gateway"))
        {
            Assert.Equal(text, after.SectionText(name));
        }
    }

    // ------------------------------------------------------------------ PublishPatchedSection: the last guard

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_raises_the_number_of_mask_placeholders_is_refused(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;
        var patched = section.Replace(harness.L("  mode: observe\n"), harness.L("  mode: '[REDACTED]'\n"), StringComparison.Ordinal);
        Assert.NotEqual(section, patched);

        // The read-back check is told the patch is fine: only the placeholder count can stop it.
        vm.PublishPatchedSection("guardrail", "Mode", patched, _ => true);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("would write masked text into config.yaml", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MaskSpellings))]
    public async Task Any_masking_spelling_in_a_patch_is_counted_as_a_placeholder(string marker, string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("gateway")!;
        var patched = section.Replace(harness.L("  host: 127.0.0.1\n"), harness.L($"  host: '{marker}'\n"), StringComparison.Ordinal);
        Assert.NotEqual(section, patched);

        vm.PublishPatchedSection("gateway", "Host", patched, _ => true);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("masked text", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    private const string RawWithOnePlaceholderLf =
        "gateway:\n" +
        "  host: 127.0.0.1\n" +
        "guardrail:\n" +
        "  mode: observe\n" +
        "  block_message: '[REDACTED]'\n";

    private const string MaskedGuardrailOnlyLf = "guardrail:\n  mode: observe\n";

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_keeps_the_placeholder_count_the_same_is_allowed(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol, RawWithOnePlaceholderLf, MaskedGuardrailOnlyLf);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;
        var patched = section.Replace("mode: observe", "mode: action", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Mode", patched, s => s.Contains("mode: action", StringComparison.Ordinal));

        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(harness.Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal), vm.RawText);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_adds_a_second_placeholder_next_to_an_existing_one_is_refused(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol, RawWithOnePlaceholderLf, MaskedGuardrailOnlyLf);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;
        var patched = section.Replace("mode: observe", "mode: '[REDACTED]'", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Mode", patched, _ => true);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("masked text", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_removes_a_placeholder_is_allowed(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol, RawWithOnePlaceholderLf, MaskedGuardrailOnlyLf);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;
        var patched = section.Replace("'[REDACTED]'", "'blocked by policy'", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Block message", patched, _ => true);

        Assert.Null(vm.FieldErrorMessage);
        Assert.DoesNotContain("REDACTED", vm.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_whose_result_does_not_parse_is_refused(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        vm.PublishPatchedSection("guardrail", "Mode", harness.L("guardrail:\n  mode: [unclosed\n"), _ => true);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("would not parse as YAML", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_does_not_read_back_as_the_value_that_was_entered_is_refused(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;

        vm.PublishPatchedSection(
            "guardrail", "Mode", section.Replace("mode: observe", "mode: action", StringComparison.Ordinal), _ => false);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("did not read back as expected", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_patch_that_smuggles_in_another_top_level_key_is_refused(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(harness.Raw).SectionText("guardrail")!;

        vm.PublishPatchedSection("guardrail", "Mode", section + harness.L("injected_section:\n  key: value\n"), _ => true);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.Contains("did not read back as expected", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ RAW edits in flight

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_form_edit_is_refused_while_raw_has_changes_form_has_not_picked_up_and_works_after_a_rebuild(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var edited = harness.Raw + harness.L("# typed in RAW\n");
        vm.RawText = edited;
        Assert.False(vm.IsFormEditable);

        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Equal(edited, vm.RawText);
        Assert.Contains("RAW tab has changes FORM has not picked up", vm.FieldErrorMessage, StringComparison.Ordinal);

        vm.NotifyFormTabSelected();
        Assert.True(vm.IsFormEditable);

        harness.Field("guardrail.mode").TextValue = "action";
        Assert.Null(vm.FieldErrorMessage);
        Assert.Contains(harness.L("  mode: action\n"), vm.RawText, StringComparison.Ordinal);
        Assert.EndsWith(harness.L("# typed in RAW\n"), vm.RawText, StringComparison.Ordinal);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
    }

    /// <summary>The on-disk sample with one LF-written fragment swapped for another, both taken in the file's own line ending.</summary>
    private static string Replaced(ConfigEditorHarness harness, string lfFrom, string lfTo)
    {
        var from = harness.L(lfFrom);
        Assert.Contains(from, harness.Raw, StringComparison.Ordinal);
        return harness.Raw.Replace(from, harness.L(lfTo), StringComparison.Ordinal);
    }
}
