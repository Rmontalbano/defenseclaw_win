using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

public class YamlSectionEditorTests
{
    private static string[] Path(params string[] segments) => segments;

    // ------------------------------------------------------------------ lists: what is refused

    [Fact]
    public void A_list_with_a_comment_between_items_is_refused_for_writing()
    {
        const string section = "guardrail:\n  roots:\n    - a\n    # keep this one\n    - b\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("guardrail", "roots"), new[] { "a", "b", "c" }));
    }

    [Fact]
    public void A_list_with_a_comment_between_items_is_reported_unsupported_so_the_form_shows_it_read_only()
    {
        const string section = "guardrail:\n  roots:\n    - a\n    # keep this one\n    - b\n";

        var lookup = YamlSectionEditor.FindList(section, Path("guardrail", "roots"));

        Assert.True(lookup.Found);
        Assert.False(lookup.Ambiguous);
        Assert.True(lookup.Unsupported);
        Assert.Null(lookup.Items);
    }

    [Theory]
    [InlineData("guardrail:\n  roots:\n    # first, a comment\n    - a\n    - b\n")]
    [InlineData("guardrail:\n  roots:\n    - a # trailing note\n    - b\n")]
    [InlineData("guardrail:\n  roots:\n    - a\n    - b # trailing note\n")]
    public void A_comment_before_or_on_an_item_refuses_the_rewrite(string section)
    {
        Assert.Null(YamlSectionEditor.TrySetList(section, Path("guardrail", "roots"), new[] { "x" }));
        Assert.True(YamlSectionEditor.FindList(section, Path("guardrail", "roots")).Unsupported);
    }

    [Fact]
    public void A_comment_after_the_last_item_is_left_where_it_is()
    {
        const string section = "guardrail:\n  roots:\n    - a\n    - b\n\n  # next setting\n  mode: observe\n";

        var patched = YamlSectionEditor.TrySetList(section, Path("guardrail", "roots"), new[] { "a", "b", "c" });

        Assert.Equal("guardrail:\n  roots:\n    - a\n    - b\n    - c\n\n  # next setting\n  mode: observe\n", patched);
    }

    [Theory]
    [InlineData("- name: x")]
    [InlineData("- - nested")]
    [InlineData("- [a, b]")]
    [InlineData("- {a: 1}")]
    [InlineData("- |")]
    [InlineData("- >")]
    [InlineData("- *alias")]
    [InlineData("- &anchor value")]
    [InlineData("- !!str tagged")]
    [InlineData("-")]
    [InlineData("- 'unterminated")]
    public void List_items_that_are_not_plain_scalars_refuse_the_rewrite(string item)
    {
        var section = $"s:\n  roots:\n    - a\n    {item}\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "a" }));
        Assert.True(YamlSectionEditor.FindList(section, Path("s", "roots")).Unsupported);
    }

    [Fact]
    public void A_list_of_mappings_is_not_a_list_this_editor_can_rewrite()
    {
        const string section = "s:\n  routes:\n    - name: a\n      url: https://h\n    - name: b\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "routes"), new[] { "a" }));
    }

    [Fact]
    public void An_inline_flow_list_is_not_offered_for_editing()
    {
        const string section = "s:\n  roots: [a, b]\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "x" }));
        Assert.False(YamlSectionEditor.FindList(section, Path("s", "roots")).Found);
    }

    [Fact]
    public void A_key_that_holds_a_mapping_is_not_a_list()
    {
        const string section = "s:\n  roots:\n    a: 1\n    b: 2\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "x" }));
        Assert.True(YamlSectionEditor.FindList(section, Path("s", "roots")).Unsupported);
    }

    [Fact]
    public void Items_with_ragged_indentation_refuse_the_rewrite()
    {
        const string section = "s:\n  roots:\n    - a\n      - b\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "x" }));
    }

    [Fact]
    public void A_replacement_item_containing_a_line_break_is_refused()
    {
        const string section = "s:\n  roots:\n    - a\n";

        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "a\nb" }));
        Assert.Null(YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "a\rb" }));
    }

    [Fact]
    public void A_list_path_that_is_missing_or_ambiguous_is_refused()
    {
        const string missing = "s:\n  roots:\n    - a\n";
        const string ambiguous = "s:\n  roots:\n    - a\n  roots:\n    - b\n";

        Assert.Null(YamlSectionEditor.TrySetList(missing, Path("s", "other"), new[] { "x" }));
        Assert.Null(YamlSectionEditor.TrySetList(ambiguous, Path("s", "roots"), new[] { "x" }));
        Assert.False(YamlSectionEditor.FindList(missing, Path("s", "other")).Found);

        var lookup = YamlSectionEditor.FindList(ambiguous, Path("s", "roots"));
        Assert.True(lookup.Found);
        Assert.True(lookup.Ambiguous);
    }

    // ------------------------------------------------------------------ lists: what is written

    [Fact]
    public void A_simple_list_is_read_and_replaced_keeping_its_indentation()
    {
        const string section = "ai_discovery:\n  mode: enhanced\n  scan_roots:\n  - '~'\n  - C:\\src\n  enabled: true\n";

        var lookup = YamlSectionEditor.FindList(section, Path("ai_discovery", "scan_roots"));
        Assert.True(lookup.Found);
        Assert.False(lookup.Unsupported);
        Assert.Equal(new[] { "~", "C:\\src" }, lookup.Items);

        var patched = YamlSectionEditor.TrySetList(section, Path("ai_discovery", "scan_roots"), new[] { "~", "D:\\repos", "it's" });

        Assert.Equal(
            "ai_discovery:\n  mode: enhanced\n  scan_roots:\n  - '~'\n  - D:\\repos\n  - it's\n  enabled: true\n",
            patched);
    }

    [Fact]
    public void An_empty_inline_list_can_be_filled_and_a_filled_list_can_be_emptied()
    {
        const string empty = "s:\n  roots: []\n  mode: x\n";
        var filled = YamlSectionEditor.TrySetList(empty, Path("s", "roots"), new[] { "a", "b" });
        Assert.Equal("s:\n  roots:\n  - a\n  - b\n  mode: x\n", filled);

        var emptied = YamlSectionEditor.TrySetList(filled!, Path("s", "roots"), Array.Empty<string>());
        Assert.Equal(empty, emptied);
    }

    [Fact]
    public void Blank_lines_between_items_are_dropped_but_blank_lines_after_the_list_stay()
    {
        const string section = "s:\n  roots:\n    - a\n\n    - b\n\n  mode: x\n";

        var patched = YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "a", "b", "c" });

        Assert.Equal("s:\n  roots:\n    - a\n    - b\n    - c\n\n  mode: x\n", patched);
    }

    [Fact]
    public void An_item_that_needs_quoting_is_quoted_in_the_list()
    {
        const string section = "s:\n  roots:\n    - a\n";

        var patched = YamlSectionEditor.TrySetList(section, Path("s", "roots"), new[] { "a", "yes", "12", "D:", "x: y" });

        Assert.Equal("s:\n  roots:\n    - a\n    - 'yes'\n    - '12'\n    - 'D:'\n    - 'x: y'\n", patched);
    }

    [Fact]
    public void Reading_an_item_undoes_the_quoting_the_editor_applies()
    {
        const string section = "s:\n  roots:\n    - 'it''s'\n    - \"dq\"\n    - plain\n";

        var lookup = YamlSectionEditor.FindList(section, Path("s", "roots"));

        Assert.Equal(new[] { "it's", "dq", "plain" }, lookup.Items);
    }

    // ------------------------------------------------------------------ line endings

    [Fact]
    public void A_list_written_at_the_end_of_a_file_without_a_trailing_newline_keeps_items_on_separate_lines()
    {
        const string section = "a:\n  list: []";

        var patched = YamlSectionEditor.TrySetList(section, Path("a", "list"), new[] { "x", "w" });

        // The key line has no terminator; the items still get the section's own, and the file still ends without one.
        Assert.Equal("a:\n  list:\n  - x\n  - w", patched);
    }

    [Fact]
    public void A_single_line_section_without_a_newline_gets_lf_separated_items()
    {
        var patched = YamlSectionEditor.TrySetList("list: []", Path("list"), new[] { "x", "w" });

        Assert.Equal("list:\n- x\n- w", patched);
    }

    [Fact]
    public void A_list_block_that_ends_the_file_without_a_newline_still_ends_it_without_one()
    {
        const string section = "a:\n  list:\n    - x\n    - y";

        var patched = YamlSectionEditor.TrySetList(section, Path("a", "list"), new[] { "p", "q", "r" });

        Assert.Equal("a:\n  list:\n    - p\n    - q\n    - r", patched);
    }

    [Fact]
    public void A_crlf_scalar_edit_keeps_every_line_crlf()
    {
        const string section = "a:\r\n  mode: observe\r\n  roots:\r\n    - x\r\n";

        var patched = YamlSectionEditor.TrySetScalar(section, Path("a", "mode"), "action");

        Assert.Equal("a:\r\n  mode: action\r\n  roots:\r\n    - x\r\n", patched);
    }

    [Fact]
    public void A_crlf_list_edit_writes_crlf_between_the_new_items()
    {
        const string section = "a:\r\n  roots:\r\n    - x\r\n  mode: observe\r\n";

        var patched = YamlSectionEditor.TrySetList(section, Path("a", "roots"), new[] { "x", "w", "z" });

        Assert.Equal("a:\r\n  roots:\r\n    - x\r\n    - w\r\n    - z\r\n  mode: observe\r\n", patched);
        Assert.DoesNotContain("\n", patched!.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_list_written_into_a_crlf_file_stays_crlf()
    {
        const string section = "a:\r\n  roots:\r\n    - x\r\n  mode: observe\r\n";

        var patched = YamlSectionEditor.TrySetList(section, Path("a", "roots"), Array.Empty<string>());

        Assert.Equal("a:\r\n  roots: []\r\n  mode: observe\r\n", patched);
    }

    [Fact]
    public void A_crlf_file_that_ends_without_a_newline_borrows_crlf_for_the_new_items()
    {
        const string section = "a:\r\n  list: []";

        var patched = YamlSectionEditor.TrySetList(section, Path("a", "list"), new[] { "x", "w" });

        Assert.Equal("a:\r\n  list:\r\n  - x\r\n  - w", patched);
    }

    // ------------------------------------------------------------------ scalars: what is refused

    [Theory]
    [InlineData("description: |\n      line one\n      line two\n")]
    [InlineData("description: >\n      folded text\n")]
    [InlineData("description: |-\n      kept\n")]
    public void A_block_scalar_is_refused_and_reported_unsupported(string body)
    {
        var section = "s:\n  " + body + "  mode: x\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "description"), "new"));

        var lookup = YamlSectionEditor.FindScalar(section, Path("s", "description"));
        Assert.True(lookup.Found);
        Assert.True(lookup.Unsupported);
    }

    [Theory]
    [InlineData("&base value")]
    [InlineData("*base")]
    [InlineData("!!str tagged")]
    [InlineData("!custom value")]
    public void An_anchor_alias_or_tag_is_refused(string value)
    {
        var section = $"s:\n  key: {value}\n  mode: x\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "key"), "new"));
        Assert.True(YamlSectionEditor.FindScalar(section, Path("s", "key")).Unsupported);
    }

    [Fact]
    public void An_alias_used_as_a_value_is_refused_even_when_its_anchor_sits_on_a_mapping()
    {
        const string section = "s:\n  defaults: &d\n    a: 1\n  copy: *d\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "copy"), "x"));
    }

    [Fact]
    public void A_trailing_comment_refuses_the_edit_rather_than_eating_the_comment()
    {
        const string section = "s:\n  mode: observe # keep watching\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "mode"), "action"));
        Assert.True(YamlSectionEditor.FindScalar(section, Path("s", "mode")).Unsupported);
    }

    [Fact]
    public void A_value_continued_on_the_next_line_is_refused()
    {
        const string section = "s:\n  message: first part\n    second part\n  mode: x\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "message"), "new"));
        Assert.True(YamlSectionEditor.FindScalar(section, Path("s", "message")).Unsupported);
    }

    [Fact]
    public void A_missing_or_ambiguous_scalar_path_is_refused()
    {
        const string section = "s:\n  mode: a\n  mode: b\n  only: 1\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "absent"), "x"));
        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "mode"), "x"));

        Assert.False(YamlSectionEditor.FindScalar(section, Path("s", "absent")).Found);
        var lookup = YamlSectionEditor.FindScalar(section, Path("s", "mode"));
        Assert.True(lookup.Found);
        Assert.True(lookup.Ambiguous);
    }

    [Fact]
    public void A_key_that_holds_a_mapping_is_not_a_scalar()
    {
        const string section = "s:\n  nested:\n    a: 1\n";

        Assert.False(YamlSectionEditor.FindScalar(section, Path("s", "nested")).Found);
        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "nested"), "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void An_empty_or_multi_line_replacement_is_refused(string replacement)
    {
        const string section = "s:\n  mode: observe\n";

        Assert.Null(YamlSectionEditor.TrySetScalar(section, Path("s", "mode"), replacement));
    }

    [Fact]
    public void The_same_key_under_two_parents_is_told_apart_by_its_full_path()
    {
        const string section = "s:\n  a:\n    mode: one\n  b:\n    mode: two\n";

        var patched = YamlSectionEditor.TrySetScalar(section, Path("s", "b", "mode"), "changed");

        Assert.Equal("s:\n  a:\n    mode: one\n  b:\n    mode: changed\n", patched);
        Assert.Equal("one", YamlSectionEditor.UnquoteScalar(YamlSectionEditor.FindScalar(section, Path("s", "a", "mode")).RawValue!));
    }

    // ------------------------------------------------------------------ scalars: what is written

    [Fact]
    public void A_scalar_edit_changes_only_its_own_line()
    {
        const string section = "# leading comment\ns:\n  host: 127.0.0.1\n  mode: 'observe'\n\n  # about port\n  port: 4000\n";

        var patched = YamlSectionEditor.TrySetScalar(section, Path("s", "mode"), "action");

        Assert.Equal("# leading comment\ns:\n  host: 127.0.0.1\n  mode: action\n\n  # about port\n  port: 4000\n", patched);
    }

    [Fact]
    public void The_raw_value_is_reported_exactly_as_written()
    {
        const string section = "s:\n  a: 'quoted'\n  b: plain\n  c: 42\n";

        Assert.Equal("'quoted'", YamlSectionEditor.FindScalar(section, Path("s", "a")).RawValue);
        Assert.Equal("plain", YamlSectionEditor.FindScalar(section, Path("s", "b")).RawValue);
        Assert.Equal("42", YamlSectionEditor.FindScalar(section, Path("s", "c")).RawValue);
    }

    // ------------------------------------------------------------------ quoting

    [Theory]
    [InlineData("Blocked by policy:")]
    [InlineData("D:")]
    [InlineData("host:")]
    public void A_trailing_colon_is_quoted_because_it_would_read_as_a_mapping_key(string value)
    {
        Assert.Equal($"'{value}'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("0.85")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1e3")]
    [InlineData("1E-3")]
    [InlineData("0x1F")]
    [InlineData("0o17")]
    [InlineData("0b101")]
    [InlineData("1_000")]
    [InlineData("1:30")]
    [InlineData("1:30:15")]
    [InlineData(".inf")]
    [InlineData("-.inf")]
    [InlineData(".Inf")]
    [InlineData(".nan")]
    [InlineData(".NaN")]
    public void Anything_a_parser_may_read_as_a_number_is_quoted(string value)
    {
        Assert.Equal($"'{value}'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("yes")]
    [InlineData("No")]
    [InlineData("on")]
    [InlineData("OFF")]
    [InlineData("y")]
    [InlineData("N")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("~")]
    public void Boolean_and_null_words_are_quoted(string value)
    {
        Assert.Equal($"'{value}'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value));
    }

    [Theory]
    [InlineData("2026-09-29")]
    [InlineData("2026-9-29")]
    [InlineData("2026-09-29T10:00:00Z")]
    [InlineData("2026-09-29 10:00:00")]
    public void Dates_and_timestamps_are_quoted(string value)
    {
        Assert.Equal($"'{value}'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value));
    }

    [Theory]
    [InlineData("observe")]
    [InlineData("gpt-4o")]
    [InlineData("hello world")]
    [InlineData("https://h/path")]
    [InlineData("C:\\Users\\me\\repo")]
    [InlineData("1.2.3")]
    [InlineData("2026-09")]
    [InlineData("nullable")]
    [InlineData("yes please")]
    [InlineData("it's fine")]
    [InlineData("a:b")]
    [InlineData("a#b")]
    public void Ordinary_strings_are_written_plain(string value)
    {
        Assert.Equal(value, YamlSectionEditor.FormatScalar(FormFieldKind.String, value));
    }

    [Theory]
    [InlineData("#comment")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("!tag")]
    [InlineData("|block")]
    [InlineData(">fold")]
    [InlineData("%directive")]
    [InlineData("@at")]
    [InlineData("`tick")]
    [InlineData("\"dq")]
    [InlineData("'sq")]
    [InlineData("-dash")]
    [InlineData("?q")]
    [InlineData(":colon")]
    [InlineData(",comma")]
    [InlineData("[flow")]
    [InlineData("]flow")]
    [InlineData("{map")]
    [InlineData("}map")]
    public void A_leading_yaml_indicator_character_is_quoted(string value)
    {
        Assert.StartsWith("'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("a: b")]
    [InlineData("a #b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void Whitespace_and_separators_force_quoting(string value)
    {
        Assert.StartsWith("'", YamlSectionEditor.FormatScalar(FormFieldKind.String, value), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_string_is_written_as_two_quotes_and_an_apostrophe_is_doubled()
    {
        Assert.Equal("''", YamlSectionEditor.FormatScalar(FormFieldKind.String, string.Empty));
        Assert.Equal("''", YamlSectionEditor.FormatScalar(FormFieldKind.String, null));
        Assert.Equal("'''quoted'''", YamlSectionEditor.FormatScalar(FormFieldKind.String, "'quoted'"));
    }

    [Fact]
    public void Booleans_and_integers_are_formatted_as_their_own_types()
    {
        Assert.Equal("true", YamlSectionEditor.FormatScalar(FormFieldKind.Bool, true));
        Assert.Equal("false", YamlSectionEditor.FormatScalar(FormFieldKind.Bool, false));
        Assert.Equal("false", YamlSectionEditor.FormatScalar(FormFieldKind.Bool, "true"));
        Assert.Equal("4096", YamlSectionEditor.FormatScalar(FormFieldKind.Int, 4096));
        Assert.Equal("-5", YamlSectionEditor.FormatScalar(FormFieldKind.Int, -5));
        Assert.Equal("5000000000", YamlSectionEditor.FormatScalar(FormFieldKind.Int, 5_000_000_000L));
        Assert.Equal("0", YamlSectionEditor.FormatScalar(FormFieldKind.Int, "12"));
    }

    [Theory]
    [InlineData("observe")]
    [InlineData("it's")]
    [InlineData("'quoted'")]
    [InlineData("yes")]
    [InlineData("12")]
    [InlineData("D:")]
    [InlineData("a: b")]
    [InlineData(" spaced ")]
    [InlineData("")]
    public void Unquoting_undoes_what_formatting_did(string value)
    {
        var formatted = YamlSectionEditor.FormatScalar(FormFieldKind.String, value);

        Assert.Equal(value, YamlSectionEditor.UnquoteScalar(formatted));
    }

    [Fact]
    public void Unquoting_handles_double_quotes_and_leaves_bare_text_alone()
    {
        Assert.Equal("a\"b", YamlSectionEditor.UnquoteScalar("\"a\\\"b\""));
        Assert.Equal("plain", YamlSectionEditor.UnquoteScalar("plain"));
        Assert.Equal("'", YamlSectionEditor.UnquoteScalar("'"));
    }

    [Theory]
    [InlineData("0.85", true)]
    [InlineData("42", true)]
    [InlineData("yes", true)]
    [InlineData("null", true)]
    [InlineData("~", true)]
    [InlineData("2026-01-01", true)]
    [InlineData(".inf", true)]
    [InlineData("observe", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("", false)]
    public void Plain_values_that_a_parser_would_not_read_as_strings_are_recognised(string raw, bool expected)
    {
        Assert.Equal(expected, YamlSectionEditor.LooksLikeNonStringPlainScalar(raw));
    }

    // ------------------------------------------------------------------ round trip through the document

    private const string Config =
        "# DefenseClaw config\r\n" +
        "config_version: 8\r\n" +
        "claw:\r\n" +
        "  mode: claudecode   # active connector\r\n" +
        "\r\n" +
        "guardrail:\r\n" +
        "  enabled: true\r\n" +
        "  scanner_mode: local\r\n" +
        "  scan_roots:\r\n" +
        "  - '~'\r\n" +
        "  - C:\\src\r\n" +
        "\r\n" +
        "# gateway settings\r\n" +
        "gateway:\r\n" +
        "  api_port: 18970\r\n" +
        "  token_env: DEFENSECLAW_GATEWAY_TOKEN\r\n" +
        "observability: {}\r\n";

    [Fact]
    public void A_scalar_round_trip_leaves_every_other_section_byte_identical()
    {
        var before = ConfigStore.Parse(Config);
        var patchedSection = YamlSectionEditor.TrySetScalar(
            before.SectionText("guardrail")!, Path("guardrail", "scanner_mode"), "remote");
        Assert.NotNull(patchedSection);

        var newRaw = before.WithSectionReplaced("guardrail", patchedSection);
        var after = ConfigStore.Parse(newRaw);

        foreach (var (name, text) in before.Sections)
        {
            if (name != "guardrail")
            {
                Assert.Equal(text, after.SectionText(name));
            }
        }

        Assert.Equal(before.Sections.Keys, after.Sections.Keys);
        Assert.Equal(Config.Replace("  scanner_mode: local\r\n", "  scanner_mode: remote\r\n", StringComparison.Ordinal), newRaw);
        Assert.Equal("remote", after.Config.Guardrail.ScannerMode);
    }

    [Fact]
    public void A_list_round_trip_leaves_every_other_section_byte_identical()
    {
        var before = ConfigStore.Parse(Config);
        var patchedSection = YamlSectionEditor.TrySetList(
            before.SectionText("guardrail")!, Path("guardrail", "scan_roots"), new[] { "~", "C:\\src", "D:\\repos" });
        Assert.NotNull(patchedSection);

        var newRaw = before.WithSectionReplaced("guardrail", patchedSection);
        var after = ConfigStore.Parse(newRaw);

        foreach (var (name, text) in before.Sections)
        {
            if (name != "guardrail")
            {
                Assert.Equal(text, after.SectionText(name));
            }
        }

        Assert.Equal(
            Config.Replace("  - C:\\src\r\n", "  - C:\\src\r\n  - D:\\repos\r\n", StringComparison.Ordinal),
            newRaw);
        Assert.DoesNotContain("\n", newRaw.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void An_edit_next_to_a_trailing_comment_on_another_key_still_works_and_keeps_the_comment()
    {
        var before = ConfigStore.Parse(Config);

        // claw.mode carries a trailing comment, so it is refused ...
        Assert.Null(YamlSectionEditor.TrySetScalar(before.SectionText("claw")!, Path("claw", "mode"), "codex"));

        // ... while a sibling section edit leaves that comment exactly where it was.
        var patched = YamlSectionEditor.TrySetScalar(before.SectionText("gateway")!, Path("gateway", "api_port"), "18971");
        var newRaw = before.WithSectionReplaced("gateway", patched!);
        Assert.Contains("  mode: claudecode   # active connector\r\n", newRaw, StringComparison.Ordinal);
        Assert.Contains("  api_port: 18971\r\n", newRaw, StringComparison.Ordinal);
    }
}
