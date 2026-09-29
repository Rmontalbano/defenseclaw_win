using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The view-model side of "a masked value never comes back": a typed placeholder is refused, a patch that
/// raises the number of placeholders is refused, and neither leaves a trace in the RAW text. The masked source
/// view is supplied through <see cref="ConfigEditorWindowViewModel.FormSourceOverride"/>, so no CLI is involved.
/// </summary>
public sealed class ConfigEditorMaskGuardTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly AppServices _services;

        private Harness(TempDirectory temp, AppServices services, ConfigEditorWindowViewModel vm)
        {
            _temp = temp;
            _services = services;
            ViewModel = vm;
        }

        public ConfigEditorWindowViewModel ViewModel { get; }

        public string ConfigPath => _temp.File("config.yaml");

        public static async Task<Harness> LoadAsync(
            string raw = ConfigSamples.Raw,
            string? maskedSource = ConfigSamples.MaskedSource)
        {
            var temp = new TempDirectory();
            var services = TestServices.Create(temp, raw);
            var vm = new ConfigEditorWindowViewModel(services);
            if (maskedSource is not null)
            {
                vm.FormSourceOverride = _ => Task.FromResult(maskedSource);
            }

            await vm.LoadAsync();
            return new Harness(temp, services, vm);
        }

        public FormField Field(string dottedPath) => Groups().SelectMany(g => g.Fields).Single(f => f.Path == dottedPath);

        public FormListField List(string dottedPath) => Groups().SelectMany(g => g.Lists).Single(l => l.Path == dottedPath);

        private IEnumerable<FormGroup> Groups()
        {
            var stack = new Stack<FormGroup>(ViewModel.Sections);
            while (stack.Count > 0)
            {
                var group = stack.Pop();
                yield return group;
                foreach (var nested in group.SubGroups)
                {
                    stack.Push(nested);
                }
            }
        }

        public void Dispose()
        {
            _services.Dispose();
            _temp.Dispose();
        }
    }

    // ------------------------------------------------------------------ loading

    [Fact]
    public async Task Loading_builds_the_form_from_the_masked_source_and_flags_secret_references()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.Null(vm.FormUnavailableReason);
        Assert.Equal(5, vm.Sections.Count);
        Assert.True(vm.IsFormEditable);
        Assert.True(vm.HasSecretReferences);
        Assert.Contains("secret references", vm.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_cli_the_form_is_unavailable_but_raw_still_loads()
    {
        // No override: the real read path runs, against a runner whose PATH is empty. It ends in
        // CliNotFoundException before any process exists, so nothing outside the scratch directory is touched.
        using var harness = await Harness.LoadAsync(maskedSource: null);
        var vm = harness.ViewModel;

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Empty(vm.Sections);
        Assert.NotNull(vm.FormUnavailableReason);
        Assert.Contains("not found", vm.FormUnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowFormEmptyState);
    }

    // ------------------------------------------------------------------ typed mask-like values are refused

    [Theory]
    [InlineData("[REDACTED]")]
    [InlineData("[redacted]")]
    [InlineData("[REDACTED_URL]")]
    [InlineData("https://h.example.test/[REDACTED]")]
    [InlineData("***")]
    [InlineData("abcd***wxyz")]
    [InlineData("***REDACTED***")]
    public async Task A_typed_mask_placeholder_is_refused_and_never_reaches_the_raw_text(string typed)
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("llm.model").TextValue = typed;

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
        Assert.NotNull(vm.FieldErrorMessage);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_mask_placeholder_is_refused_for_an_env_name_field_too()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.token_env").TextValue = "[REDACTED]";

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fields_built_read_only_cannot_reach_the_raw_text_by_any_route()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.token").TextValue = "typed-over-the-secret";
        harness.Field("llm.endpoint").TextValue = "https://elsewhere.example.test/x";
        harness.Field("llm.headers.Authorization").TextValue = "typed";
        harness.Field("llm.headers.Accept").TextValue = "text/plain";
        harness.Field("config_version").NumberValue = 99;
        harness.Field("llm.temperature").TextValue = "0.9";

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.False(vm.IsRawModified);
    }

    [Fact]
    public async Task A_typed_placeholder_is_refused_for_an_editable_list_and_the_list_that_holds_masks_is_locked()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.List("guardrail.scan_roots").Items.Add("[REDACTED]");
        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("never writes masked text back", vm.FieldErrorMessage, StringComparison.Ordinal);

        harness.List("guardrail.allowed_hosts").Items.Add("another.example.test");
        harness.List("guardrail.api_keys").Items.Add("key-three");
        Assert.Equal(ConfigSamples.Raw, vm.RawText);
    }

    // ------------------------------------------------------------------ legitimate edits still go through

    [Fact]
    public async Task A_string_edit_changes_only_its_own_line_and_clears_a_previous_refusal()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("guardrail.mode").TextValue = "[REDACTED]";
        Assert.NotNull(vm.FieldErrorMessage);

        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Null(vm.FieldErrorMessage);
        Assert.True(vm.IsRawModified);
        Assert.Equal(ConfigSamples.Raw.Replace("  mode: observe\n", "  mode: action\n", StringComparison.Ordinal), vm.RawText);
        Assert.True(vm.IsFormEditable);
    }

    [Fact]
    public async Task Integer_and_boolean_edits_are_written_in_their_own_types()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.Field("gateway.api_port").NumberValue = 4000;
        harness.Field("guardrail.enabled").BoolValue = false;
        harness.Field("llm.max_tokens").NumberValue = 8192;

        Assert.Null(vm.FieldErrorMessage);
        Assert.Contains("  api_port: 4000\n", vm.RawText, StringComparison.Ordinal);
        Assert.Contains("  enabled: false\n", vm.RawText, StringComparison.Ordinal);
        Assert.Contains("  max_tokens: 8192\n", vm.RawText, StringComparison.Ordinal);
        Assert.Contains("  token: sample-not-a-real-token\n", vm.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_edit_rewrites_only_that_list()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        harness.List("guardrail.scan_roots").Items.Add("E:\\work");

        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(
            ConfigSamples.Raw.Replace("  - D:\\repos\n", "  - D:\\repos\n  - E:\\work\n", StringComparison.Ordinal),
            vm.RawText);
    }

    [Fact]
    public async Task Every_section_other_than_the_edited_one_is_byte_identical_after_an_edit()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var before = ConfigStore.Parse(ConfigSamples.Raw);

        harness.Field("gateway.host").TextValue = "localhost";

        var after = ConfigStore.Parse(vm.RawText);
        Assert.NotEqual(before.SectionText("gateway"), after.SectionText("gateway"));
        foreach (var (name, text) in before.Sections.Where(s => s.Key != "gateway"))
        {
            Assert.Equal(text, after.SectionText(name));
        }
    }

    // ------------------------------------------------------------------ PublishPatchedSection: the last guard

    [Fact]
    public async Task A_patch_that_raises_the_number_of_mask_placeholders_is_refused()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(ConfigSamples.Raw).SectionText("guardrail")!;
        var patched = section.Replace("  mode: observe\n", "  mode: '[REDACTED]'\n", StringComparison.Ordinal);
        Assert.NotEqual(section, patched);

        // The read-back check is told the patch is fine: only the placeholder count can stop it.
        vm.PublishPatchedSection("guardrail", "Mode", patched, _ => true);

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("would write masked text into config.yaml", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("***")]
    [InlineData("[REDACTED_URL]")]
    [InlineData("abc***def")]
    public async Task Any_masking_spelling_in_a_patch_is_counted_as_a_placeholder(string marker)
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(ConfigSamples.Raw).SectionText("gateway")!;
        var patched = section.Replace("  host: 127.0.0.1\n", $"  host: '{marker}'\n", StringComparison.Ordinal);

        vm.PublishPatchedSection("gateway", "Host", patched, _ => true);

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("masked text", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    private const string RawWithOnePlaceholder = """
        gateway:
          host: 127.0.0.1
        guardrail:
          mode: observe
          block_message: '[REDACTED]'

        """;

    [Fact]
    public async Task A_patch_that_keeps_the_placeholder_count_the_same_is_allowed()
    {
        using var harness = await Harness.LoadAsync(RawWithOnePlaceholder, "guardrail:\n  mode: observe\n");
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(RawWithOnePlaceholder).SectionText("guardrail")!;
        var patched = section.Replace("mode: observe", "mode: action", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Mode", patched, s => s.Contains("mode: action", StringComparison.Ordinal));

        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(RawWithOnePlaceholder.Replace("mode: observe", "mode: action", StringComparison.Ordinal), vm.RawText);
    }

    [Fact]
    public async Task A_patch_that_adds_a_second_placeholder_next_to_an_existing_one_is_refused()
    {
        using var harness = await Harness.LoadAsync(RawWithOnePlaceholder, "guardrail:\n  mode: observe\n");
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(RawWithOnePlaceholder).SectionText("guardrail")!;
        var patched = section.Replace("mode: observe", "mode: '[REDACTED]'", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Mode", patched, _ => true);

        Assert.Equal(RawWithOnePlaceholder, vm.RawText);
        Assert.Contains("masked text", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_that_removes_a_placeholder_is_allowed()
    {
        using var harness = await Harness.LoadAsync(RawWithOnePlaceholder, "guardrail:\n  mode: observe\n");
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(RawWithOnePlaceholder).SectionText("guardrail")!;
        var patched = section.Replace("'[REDACTED]'", "'blocked by policy'", StringComparison.Ordinal);

        vm.PublishPatchedSection("guardrail", "Block message", patched, _ => true);

        Assert.Null(vm.FieldErrorMessage);
        Assert.DoesNotContain("REDACTED", vm.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_whose_result_does_not_parse_is_refused()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;

        vm.PublishPatchedSection("guardrail", "Mode", "guardrail:\n  mode: [unclosed\n", _ => true);

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("would not parse as YAML", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_that_does_not_read_back_as_the_value_that_was_entered_is_refused()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(ConfigSamples.Raw).SectionText("guardrail")!;

        vm.PublishPatchedSection(
            "guardrail", "Mode", section.Replace("mode: observe", "mode: action", StringComparison.Ordinal), _ => false);

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("did not read back as expected", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_that_smuggles_in_another_top_level_key_is_refused()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var section = ConfigStore.Parse(ConfigSamples.Raw).SectionText("guardrail")!;

        vm.PublishPatchedSection("guardrail", "Mode", section + "injected_section:\n  key: value\n", _ => true);

        Assert.Equal(ConfigSamples.Raw, vm.RawText);
        Assert.Contains("did not read back as expected", vm.FieldErrorMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ RAW edits in flight

    [Fact]
    public async Task A_form_edit_is_refused_while_raw_has_changes_form_has_not_picked_up_and_works_after_a_rebuild()
    {
        using var harness = await Harness.LoadAsync();
        var vm = harness.ViewModel;
        var edited = ConfigSamples.Raw + "# typed in RAW\n";
        vm.RawText = edited;
        Assert.False(vm.IsFormEditable);

        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Equal(edited, vm.RawText);
        Assert.Contains("RAW tab has changes FORM has not picked up", vm.FieldErrorMessage, StringComparison.Ordinal);

        vm.NotifyFormTabSelected();
        Assert.True(vm.IsFormEditable);

        harness.Field("guardrail.mode").TextValue = "action";
        Assert.Null(vm.FieldErrorMessage);
        Assert.Contains("  mode: action\n", vm.RawText, StringComparison.Ordinal);
        Assert.EndsWith("# typed in RAW\n", vm.RawText, StringComparison.Ordinal);
    }
}
