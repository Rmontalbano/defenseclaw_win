using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

public class ConfigFormBuilderTests
{
    private sealed class Built
    {
        public ConfigFormBuilder.BuildResult Result { get; set; } = null!;

        public List<FormField> FieldCommits { get; } = new();

        public List<FormListField> ListCommits { get; } = new();

        public FormSection Section(string key) => Result.Sections.Single(s => s.Key == key);

        public FormField Field(string dottedPath) => AllGroups(Result.Sections)
            .SelectMany(g => g.Fields)
            .Single(f => f.Path == dottedPath);

        public FormListField List(string dottedPath) => AllGroups(Result.Sections)
            .SelectMany(g => g.Lists)
            .Single(l => l.Path == dottedPath);

        private static IEnumerable<FormGroup> AllGroups(IEnumerable<FormGroup> groups)
        {
            foreach (var group in groups)
            {
                yield return group;
                foreach (var nested in AllGroups(group.SubGroups))
                {
                    yield return nested;
                }
            }
        }
    }

    private static Built Build(string source = ConfigSamples.MaskedSource, string raw = ConfigSamples.Raw)
    {
        var built = new Built();
        built.Result = ConfigFormBuilder.Build(source, ConfigStore.Parse(raw), built.FieldCommits.Add, built.ListCommits.Add);
        return built;
    }

    // ------------------------------------------------------------------ sections

    [Fact]
    public void Top_level_keys_become_sections_and_the_schema_version_is_moved_last()
    {
        var built = Build();

        Assert.Equal(
            new[] { "gateway", "guardrail", "llm", "observability", "config_version" },
            built.Result.Sections.Select(s => s.Key).ToArray());
        Assert.Empty(built.Result.Warnings);
        Assert.All(built.Result.Sections, s => Assert.True(s.IsKnownToCore));
        Assert.All(built.Result.Sections, s => Assert.True(s.ExistsInRawConfig));
    }

    [Fact]
    public void An_empty_mapping_is_a_section_with_nothing_to_render()
    {
        var section = Build().Section("observability");

        Assert.True(section.IsEmpty);
        Assert.False(section.HasContent);
        Assert.Empty(section.Fields);
        Assert.Empty(section.Lists);
        Assert.Empty(section.SubGroups);
        Assert.Empty(section.RawBlocks);
        Assert.True(section.ExistsInRawConfig);
    }

    [Fact]
    public void A_section_that_is_only_in_the_cli_view_is_marked_as_absent_from_the_raw_file()
    {
        var built = Build(raw: "config_version: 8\ngateway:\n  host: 127.0.0.1\n");

        var llm = built.Section("llm");
        Assert.False(llm.ExistsInRawConfig);
        Assert.False(built.Field("llm.model").IsEditable);
        Assert.Contains("Not present in config.yaml", built.Field("llm.model").DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_names_and_dotted_paths_are_derived_from_the_keys()
    {
        var built = Build();

        Assert.Equal("Gateway", built.Section("gateway").DisplayName);
        var port = built.Field("gateway.api_port");
        Assert.Equal("Api Port", port.DisplayName);
        Assert.Equal("api_port", port.Key);
        Assert.Equal("gateway.api_port", port.Path);
    }

    // ------------------------------------------------------------------ secrets, masks, headers

    [Fact]
    public void A_secret_shaped_key_holding_a_placeholder_is_a_read_only_secret_field()
    {
        var token = Build().Field("gateway.token");

        Assert.Equal(FormFieldKind.Secret, token.Kind);
        Assert.True(token.IsSecret);
        Assert.True(token.IsMasked);
        Assert.False(token.IsEditable);
        Assert.True(token.ShowReadOnlyNote);
        Assert.StartsWith("Secret", token.DisabledReason, StringComparison.Ordinal);
        Assert.Equal(SecretValue.Redacted, token.MaskedText);
    }

    [Fact]
    public void A_secret_field_never_announces_or_shows_its_value()
    {
        var token = Build().Field("gateway.token");

        Assert.Contains("secret, masked and read-only", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("REDACTED", token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_secret_shows_not_set_rather_than_a_mask()
    {
        var built = Build(
            source: "gateway:\n  token: ''\n",
            raw: "gateway:\n  token: ''\n");

        var token = built.Field("gateway.token");
        Assert.Equal(FormFieldKind.Secret, token.Kind);
        Assert.False(token.IsEditable);
        Assert.Equal("(not set)", token.MaskedText);
    }

    [Fact]
    public void An_env_name_key_is_an_ordinary_editable_field()
    {
        var env = Build().Field("gateway.token_env");

        Assert.Equal(FormFieldKind.EnvName, env.Kind);
        Assert.True(env.IsEditable);
        Assert.False(env.IsMasked);
        Assert.False(env.IsSecret);
        Assert.Equal("DEFENSECLAW_GATEWAY_TOKEN", env.OriginalValue);
    }

    [Fact]
    public void A_url_with_a_masked_path_is_read_only_and_says_why()
    {
        var endpoint = Build().Field("llm.endpoint");

        Assert.Equal(FormFieldKind.String, endpoint.Kind);
        Assert.True(endpoint.IsMasked);
        Assert.False(endpoint.IsEditable);
        Assert.Contains("masked", endpoint.DisabledReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("masked and read-only", endpoint.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'[REDACTED_URL]'")]
    [InlineData("https://h/[REDACTED]")]
    [InlineData("'***'")]
    [InlineData("abcd***wxyz")]
    public void Every_masking_spelling_makes_a_non_secret_key_read_only(string value)
    {
        var built = Build(
            source: $"llm:\n  base_url: {value}\n",
            raw: "llm:\n  base_url: https://real.example.test/v1\n");

        var field = built.Field("llm.base_url");
        Assert.True(field.IsMasked);
        Assert.False(field.IsEditable);
    }

    [Fact]
    public void Every_value_under_a_headers_map_is_read_only_even_when_it_does_not_look_masked()
    {
        var built = Build();

        var headers = built.Section("llm").SubGroups.Single(g => g.Key == "headers");
        Assert.Equal(3, headers.Fields.Count);
        Assert.All(headers.Fields, f =>
        {
            Assert.True(f.IsMasked);
            Assert.False(f.IsEditable);
        });

        var accept = built.Field("llm.headers.Accept");
        Assert.Equal("application/json", accept.TextValue);
        Assert.Contains("Header values are masked", accept.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extra_headers_map_is_treated_the_same_as_headers()
    {
        var built = Build(
            source: "guardrail:\n  extra_headers:\n    X-Trace: plain-looking\n",
            raw: "guardrail:\n  extra_headers:\n    X-Trace: plain-looking\n");

        var field = built.Field("guardrail.extra_headers.X-Trace");
        Assert.True(field.IsMasked);
        Assert.False(field.IsEditable);
    }

    [Fact]
    public void A_list_of_secrets_is_locked_as_a_whole()
    {
        var keys = Build().List("guardrail.api_keys");

        Assert.False(keys.IsEditable);
        Assert.True(keys.ShowReadOnlyNote);
        Assert.StartsWith("Secret", keys.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_masked_item_locks_the_whole_list()
    {
        var hosts = Build().List("guardrail.allowed_hosts");

        Assert.False(hosts.IsEditable);
        Assert.Contains("masked", hosts.DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plain_list_stays_editable_and_carries_its_items()
    {
        var roots = Build().List("guardrail.scan_roots");

        Assert.True(roots.IsEditable);
        Assert.Equal(new[] { "C:\\src", "D:\\repos" }, roots.Items);
    }

    [Fact]
    public void A_list_of_strings_under_a_headers_map_is_locked()
    {
        var built = Build(
            source: "llm:\n  headers:\n    accept:\n    - a\n    - b\n",
            raw: "llm:\n  headers:\n    accept:\n    - a\n    - b\n");

        var list = built.List("llm.headers.accept");
        Assert.False(list.IsEditable);
        Assert.Contains("Header values are masked", list.DisabledReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what is NOT a secret / what is managed

    [Fact]
    public void A_number_under_a_secret_shaped_key_is_an_ordinary_setting()
    {
        var maxTokens = Build().Field("llm.max_tokens");

        // "max_tokens" contains "token", but 4096 is unmasked text: config show masks a secret, so this is a number.
        Assert.Equal(FormFieldKind.Int, maxTokens.Kind);
        Assert.False(maxTokens.IsSecret);
        Assert.False(maxTokens.IsMasked);
        Assert.True(maxTokens.IsEditable);
        Assert.Equal(4096, maxTokens.OriginalValue);
        Assert.Equal(4096d, maxTokens.NumberValue);
    }

    [Fact]
    public void A_quoted_value_under_a_secret_shaped_key_stays_a_secret()
    {
        var built = Build(
            source: "llm:\n  max_tokens: '4096'\n",
            raw: "llm:\n  max_tokens: '4096'\n");

        // Only an unquoted number is trusted to be a number; anything else under such a key errs on the read-only side.
        var field = built.Field("llm.max_tokens");
        Assert.Equal(FormFieldKind.Secret, field.Kind);
        Assert.False(field.IsEditable);
    }

    [Fact]
    public void The_config_schema_version_is_shown_but_never_editable()
    {
        var version = Build().Field("config_version");

        Assert.Equal(FormFieldKind.Int, version.Kind);
        Assert.Equal(8, version.OriginalValue);
        Assert.False(version.IsEditable);
        Assert.False(version.IsMasked);
        Assert.Contains("managed by DefenseClaw", version.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Booleans_integers_and_strings_that_can_be_located_are_editable()
    {
        var built = Build();

        Assert.Equal(FormFieldKind.Bool, built.Field("guardrail.enabled").Kind);
        Assert.True(built.Field("guardrail.enabled").IsEditable);
        Assert.Equal(FormFieldKind.Int, built.Field("gateway.api_port").Kind);
        Assert.True(built.Field("gateway.api_port").IsEditable);
        Assert.Equal(FormFieldKind.String, built.Field("llm.model").Kind);
        Assert.True(built.Field("llm.model").IsEditable);
    }

    [Fact]
    public void A_plain_float_is_read_only_because_writing_it_back_would_change_its_type()
    {
        var temperature = Build().Field("llm.temperature");

        Assert.Equal(FormFieldKind.String, temperature.Kind);
        Assert.False(temperature.IsEditable);
        Assert.Contains("number, date or null/yes/no", temperature.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_the_raw_file_writes_inline_or_with_a_comment_is_read_only_with_a_reason()
    {
        var inline = Build(
            source: "gateway:\n  host: 127.0.0.1\n",
            raw: "gateway: {host: 127.0.0.1}\n");
        Assert.False(inline.Field("gateway.host").IsEditable);
        Assert.Contains("not on a line of its own", inline.Field("gateway.host").DisabledReason, StringComparison.Ordinal);

        var commented = Build(
            source: "gateway:\n  host: 127.0.0.1\n",
            raw: "gateway:\n  host: 127.0.0.1 # loopback\n");
        Assert.False(commented.Field("gateway.host").IsEditable);
        Assert.Contains("trailing comment", commented.Field("gateway.host").DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_appears_twice_in_the_raw_file_is_read_only()
    {
        var built = Build(
            source: "gateway:\n  host: b\n",
            raw: "gateway:\n  host: a\n  host: b\n");

        Assert.False(built.Field("gateway.host").IsEditable);
        Assert.Contains("more than once", built.Field("gateway.host").DisabledReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ complex nodes, bad input

    [Fact]
    public void A_list_of_mappings_renders_as_a_read_only_block()
    {
        var built = Build(
            source: "observability:\n  routes:\n  - name: a\n    url: https://h.example.test\n  - name: b\n",
            raw: "observability:\n  routes:\n  - name: a\n    url: https://h.example.test\n  - name: b\n");

        var block = Assert.Single(built.Section("observability").RawBlocks);
        Assert.Equal("observability.routes", block.Path);
        Assert.Contains("name: a", block.Yaml, StringComparison.Ordinal);
        Assert.Contains("read-only YAML block", block.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_past_the_depth_limit_becomes_a_raw_block_instead_of_failing_the_section()
    {
        var source = "root:\n";
        var indent = "  ";
        for (var level = 1; level <= 10; level++)
        {
            source += $"{indent}l{level}:\n";
            indent += "  ";
        }

        source += $"{indent}leaf: 1\n";

        var built = Build(source: source, raw: source);

        var blocks = AllRawBlocks(built.Section("root")).ToList();
        Assert.NotEmpty(blocks);
        Assert.Contains(blocks, b => b.Reason.Contains("too deeply", StringComparison.Ordinal));
    }

    [Fact]
    public void Unparseable_source_yields_a_warning_and_no_sections()
    {
        var built = Build(source: "gateway: [unclosed\n", raw: "gateway:\n  host: x\n");

        Assert.Empty(built.Result.Sections);
        var warning = Assert.Single(built.Result.Warnings);
        Assert.Contains("Could not parse", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("- just\n- a list\n")]
    [InlineData("plain scalar\n")]
    public void Source_without_a_mapping_at_its_root_yields_a_warning_and_no_sections(string source)
    {
        var built = Build(source: source, raw: "gateway:\n  host: x\n");

        Assert.Empty(built.Result.Sections);
        Assert.Single(built.Result.Warnings);
    }

    // ------------------------------------------------------------------ the commit guard on the built fields

    [Fact]
    public void A_read_only_field_never_commits_and_an_editable_one_does()
    {
        var built = Build();

        built.Field("gateway.token").TextValue = "typed-over-the-mask";
        built.Field("llm.endpoint").TextValue = "https://elsewhere.example.test";
        built.Field("llm.headers.Accept").TextValue = "text/plain";
        built.Field("config_version").NumberValue = 9;
        Assert.Empty(built.FieldCommits);
        Assert.False(built.Field("gateway.token").IsDirty);

        var model = built.Field("llm.model");
        model.TextValue = "gpt-4.1";
        Assert.Same(model, Assert.Single(built.FieldCommits));
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void A_field_does_not_commit_while_it_is_being_constructed()
    {
        var commits = new List<FormField>();

        _ = new FormField("mode", "Mode", "guardrail.mode", FormFieldKind.String, "observe", true, null, commits.Add);
        _ = new FormField("enabled", "Enabled", "guardrail.enabled", FormFieldKind.Bool, true, true, null, commits.Add);
        _ = new FormField("port", "Port", "gateway.api_port", FormFieldKind.Int, 18970, true, null, commits.Add);

        Assert.Empty(commits);
    }

    [Fact]
    public void A_field_formats_its_current_value_for_the_kind_it_is()
    {
        var text = new FormField("mode", "Mode", "guardrail.mode", FormFieldKind.String, "observe", true, null, _ => { });
        text.TextValue = "yes";
        Assert.Equal("'yes'", text.CurrentRawValue);

        var flag = new FormField("enabled", "Enabled", "guardrail.enabled", FormFieldKind.Bool, false, true, null, _ => { });
        flag.BoolValue = true;
        Assert.Equal("true", flag.CurrentRawValue);

        var number = new FormField("port", "Port", "gateway.api_port", FormFieldKind.Int, 1, true, null, _ => { });
        number.NumberValue = 4000.4;
        Assert.Equal("4000", number.CurrentRawValue);
    }

    [Fact]
    public void A_read_only_list_ignores_add_and_remove_and_an_editable_one_commits()
    {
        var commits = new List<FormListField>();
        var locked = new FormListField("api_keys", "Api Keys", "guardrail.api_keys", new[] { "a" }, false, "locked", commits.Add);
        locked.NewItemText = "b";
        locked.AddItemCommand.Execute(null);
        locked.RemoveItemCommand.Execute("a");
        Assert.Equal(new[] { "a" }, locked.Items);
        Assert.Empty(commits);

        var open = new FormListField("scan_roots", "Scan Roots", "guardrail.scan_roots", new[] { "a" }, true, null, commits.Add);
        open.NewItemText = "  b  ";
        open.AddItemCommand.Execute(null);
        Assert.Equal(new[] { "a", "b" }, open.Items);
        Assert.Equal(string.Empty, open.NewItemText);
        Assert.Same(open, Assert.Single(commits));
        Assert.True(open.IsDirty);

        open.NewItemText = "   ";
        open.AddItemCommand.Execute(null);
        Assert.Equal(2, open.Items.Count);
    }

    private static IEnumerable<RawBlockNode> AllRawBlocks(FormGroup group)
    {
        foreach (var block in group.RawBlocks)
        {
            yield return block;
        }

        foreach (var sub in group.SubGroups)
        {
            foreach (var block in AllRawBlocks(sub))
            {
                yield return block;
            }
        }
    }
}
