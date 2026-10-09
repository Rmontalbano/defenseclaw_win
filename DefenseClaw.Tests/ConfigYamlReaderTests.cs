using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ConfigYamlReader"/> (CUST-271): the read-only lookup the notification routing, the AI Discovery tuning and the Setup cards' state
/// lines use to ask config.yaml's text a question the typed model cannot answer - above all "does the file name this key at all", because the
/// runtime's default for a key it does not name is not the same as <c>false</c>.
/// </summary>
public class ConfigYamlReaderTests
{
    private static ConfigYamlReader Reader(string yaml) => ConfigYamlReader.Parse(yaml);

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("On", true)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData("OFF", false)]
    public void A_boolean_is_read_the_way_the_runtimes_loader_reads_it(string word, bool expected) =>
        Assert.Equal(expected, Reader($"a:\n  b: {word}\n").Bool("a", "b"));

    [Theory]
    [InlineData("maybe")]
    [InlineData("y")]
    [InlineData("n")]
    [InlineData("1")]
    [InlineData("''")]
    public void A_word_that_is_not_a_boolean_is_no_answer_so_the_caller_uses_the_default(string word) =>
        Assert.Null(Reader($"a:\n  b: {word}\n").Bool("a", "b"));

    [Fact]
    public void A_key_the_file_does_not_name_is_absent_and_a_key_with_no_value_is_present_but_empty()
    {
        var reader = Reader("a:\n  empty:\n  full: 7\n");

        Assert.False(reader.Has("a", "missing"));
        Assert.True(reader.Has("a", "empty"));
        Assert.True(reader.Has("a", "full"));
        Assert.Null(reader.Text("a", "empty"));
        Assert.Equal(7, reader.Integer("a", "full"));
        Assert.Null(reader.Integer("a", "missing"));
        Assert.Null(reader.Integer("a", "empty"));
    }

    [Fact]
    public void Keys_are_matched_exactly_the_runtimes_loader_is_case_sensitive()
    {
        var reader = Reader("Notifications:\n  enabled: true\n");

        Assert.False(reader.Has("notifications"));
        Assert.True(reader.Has("Notifications", "enabled"));
    }

    [Fact]
    public void A_list_is_read_as_its_scalars_and_a_string_is_split_on_commas_like_the_cli_splits_it()
    {
        var reader = Reader("""
            roots_list:
              - "~"
              - C:\Work
              -
              - ''
            roots_text: "~, D:\\Code ,"
            roots_empty:
            nested:
              a: 1
            """);

        Assert.Equal(new[] { "~", @"C:\Work" }, reader.List("roots_list"));
        Assert.Equal(new[] { "~", @"D:\Code" }, reader.List("roots_text"));
        Assert.Empty(reader.List("roots_empty")!);
        Assert.Null(reader.List("missing"));
        Assert.Null(reader.List("nested"));
    }

    [Fact]
    public void Keys_lists_a_mappings_names_in_file_order_and_nothing_for_anything_else()
    {
        var reader = Reader("guardrail:\n  connectors:\n    codex: {}\n    claudecode:\n      mode: observe\n  enabled: true\n");

        Assert.Equal(new[] { "codex", "claudecode" }, reader.Keys("guardrail", "connectors"));
        Assert.Equal(new[] { "guardrail" }, reader.Keys());
        Assert.Empty(reader.Keys("guardrail", "enabled"));
        Assert.Empty(reader.Keys("missing", "path"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("- a\n- b\n")]
    [InlineData("just text")]
    [InlineData("a: [unclosed")]
    [InlineData("a: b: c: d")]
    public void Text_that_is_not_a_mapping_says_nothing_and_never_throws(string yaml)
    {
        var reader = Reader(yaml);

        Assert.False(reader.Has("a"));
        Assert.Null(reader.Text("a"));
        Assert.Null(reader.Bool("a"));
        Assert.Empty(reader.Keys());
    }

    [Fact]
    public void Null_and_oversized_text_says_nothing()
    {
        Assert.False(ConfigYamlReader.Parse(null).Has("a"));
        Assert.False(ConfigYamlReader.Parse("a: " + new string('x', ConfigYamlReader.MaxLength)).Has("a"));
        Assert.False(ConfigYamlReader.From(null).Has("a"));
    }
}
