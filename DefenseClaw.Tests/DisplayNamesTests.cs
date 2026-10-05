using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Tests;

/// <summary>
/// A name that came from outside (a skill, a registry entry …) is shown where an operator decides something from it. These pin what is
/// spelled out, what is left alone, and that the text handed to the CLI is not part of any of it.
/// </summary>
public sealed class DisplayNamesTests
{
    [Theory]
    [InlineData("pdf-tools")]
    [InlineData("my skill")]
    [InlineData("C:\\Users\\operator\\skills\\pdf")]
    [InlineData("--dangerous")]
    [InlineData("a_b.c-d~e")]
    [InlineData("")]
    public void Printable_ASCII_is_shown_as_it_is(string name) => Assert.Equal(name, DisplayNames.Visible(name));

    [Fact]
    public void Printable_ASCII_comes_back_as_the_same_string_object_and_null_is_empty()
    {
        var name = "pdf-tools";

        Assert.Same(name, DisplayNames.Visible(name));
        Assert.Equal(string.Empty, DisplayNames.Visible(null));
    }

    [Theory]
    [InlineData("pdf\u202Etools", "pdf\\u202Etools")]
    [InlineData("pdf\u202Dtools", "pdf\\u202Dtools")]
    [InlineData("a\u2066b\u2069", "a\\u2066b\\u2069")]
    [InlineData("a\u200Eb\u200Fc", "a\\u200Eb\\u200Fc")]
    [InlineData("pdf\u200Btools", "pdf\\u200Btools")]
    [InlineData("pdf\u200C\u200Dtools", "pdf\\u200C\\u200Dtools")]
    [InlineData("\uFEFFpdf", "\\uFEFFpdf")]
    [InlineData("pdf\u00ADtools", "pdf\\u00ADtools")]
    [InlineData("pdf\u2060tools", "pdf\\u2060tools")]
    public void Bidirectional_and_zero_width_characters_are_spelled_out(string name, string expected) =>
        Assert.Equal(expected, DisplayNames.Visible(name));

    [Theory]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\r\nb", "a\\r\\nb")]
    [InlineData("a\tb", "a\\tb")]
    [InlineData("a\0b", "a\\u0000b")]
    [InlineData("a\u001Bb", "a\\u001Bb")]
    [InlineData("a\u007Fb", "a\\u007Fb")]
    [InlineData("a\u0085b", "a\\u0085b")]
    [InlineData("a\u2028b\u2029c", "a\\u2028b\\u2029c")]
    [InlineData("a\u000Bb\u000Cc", "a\\u000Bb\\u000Cc")]
    public void Control_characters_and_line_breaks_are_spelled_out_and_the_name_stays_on_one_line(string name, string expected)
    {
        var shown = DisplayNames.Visible(name);

        Assert.Equal(expected, shown);
        Assert.DoesNotContain('\n', shown);
        Assert.DoesNotContain('\r', shown);
    }

    [Theory]
    [InlineData("pdf\u00A0tools", "pdf\\u00A0tools")]
    [InlineData("pdf\u2003tools", "pdf\\u2003tools")]
    [InlineData("pdf\u3000tools", "pdf\\u3000tools")]
    [InlineData("pdf\uE000tools", "pdf\\uE000tools")]
    public void A_space_that_is_not_a_space_and_a_private_use_character_are_spelled_out(string name, string expected) =>
        Assert.Equal(expected, DisplayNames.Visible(name));

    [Fact]
    public void An_unpaired_surrogate_is_spelled_out_and_a_pair_is_not_split()
    {
        // Built here, not in InlineData: the test runner would turn a lone surrogate into U+FFFD on its way through.
        Assert.Equal("pdf\\uD800tools", DisplayNames.Visible("pdf" + (char)0xD800 + "tools"));
        Assert.Equal("pdf\\uDC00", DisplayNames.Visible("pdf" + (char)0xDC00));
        Assert.Equal("\\uD83Dx", DisplayNames.Visible((char)0xD83D + "x"));
        Assert.Equal("U+D800 private-use or unassigned character", DisplayNames.DescribeUnusual("a" + (char)0xD800));
    }

    [Fact]
    public void A_format_character_beyond_the_BMP_is_spelled_out_with_eight_digits_and_a_letter_or_emoji_there_is_kept()
    {
        // U+E0001 LANGUAGE TAG is an invisible format character; U+1F600 is an emoji.
        Assert.Equal("a\\U000E0001b", DisplayNames.Visible("a" + char.ConvertFromUtf32(0xE0001) + "b"));
        Assert.Equal("a" + char.ConvertFromUtf32(0x1F600) + "b", DisplayNames.Visible("a" + char.ConvertFromUtf32(0x1F600) + "b"));
    }

    [Theory]
    [InlineData("caf\u00E9")]
    [InlineData("\u65E5\u672C\u8A9E-tools")]
    [InlineData("\u0430\u0431\u0432")]
    [InlineData("pdf\u2011tools")]
    [InlineData("\u201Cquoted\u201D \u2014 and \u2026")]
    public void Ordinary_non_ASCII_text_is_kept_because_escaping_it_would_hide_what_the_name_says(string name) =>
        Assert.Equal(name, DisplayNames.Visible(name));

    [Fact]
    public void Showing_a_name_twice_changes_nothing_the_second_time()
    {
        var name = "a\u202Eb\nc\u200Bd\u00A0e" + char.ConvertFromUtf32(0xE0001);

        var once = DisplayNames.Visible(name);

        Assert.Equal(once, DisplayNames.Visible(once));
    }

    // ---- what is unusual about a name ----

    [Theory]
    [InlineData("pdf-tools", false)]
    [InlineData("my skill ~!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("pdf\u202Etools", true)]
    [InlineData("pdf\u200Btools", true)]
    [InlineData("a\nb", true)]
    [InlineData("a\tb", true)]
    [InlineData("caf\u00E9", true)]
    [InlineData("p\u0430f", true)]
    [InlineData("pdf\u2011tools", true)]
    [InlineData("a\u007Fb", true)]
    public void A_name_is_unusual_when_it_holds_anything_but_printable_ASCII(string? name, bool expected) =>
        Assert.Equal(expected, DisplayNames.IsUnusual(name));

    [Fact]
    public void An_ordinary_name_has_nothing_to_describe()
    {
        Assert.Null(DisplayNames.DescribeUnusual("pdf-tools"));
        Assert.Null(DisplayNames.DescribeUnusual(null));
        Assert.Null(DisplayNames.DescribeUnusual(string.Empty));
    }

    [Fact]
    public void The_description_names_each_distinct_character_once_in_the_order_it_appears_with_a_word_for_its_kind()
    {
        Assert.Equal("U+202E bidirectional control", DisplayNames.DescribeUnusual("pdf\u202Etools"));
        Assert.Equal("U+200B zero-width or invisible", DisplayNames.DescribeUnusual("pdf\u200Btools\u200B\u200B"));
        Assert.Equal("U+000A line break, U+0430 non-ASCII character", DisplayNames.DescribeUnusual("a\nb\u0430c\nd\u0430"));
        Assert.Equal("U+0009 control character", DisplayNames.DescribeUnusual("a\tb"));
        Assert.Equal("U+00A0 space that is not U+0020", DisplayNames.DescribeUnusual("a\u00A0b"));
        Assert.Equal("U+2011 non-ASCII character", DisplayNames.DescribeUnusual("pdf\u2011tools"));
        Assert.Equal("U+1F600 non-ASCII character", DisplayNames.DescribeUnusual("a" + char.ConvertFromUtf32(0x1F600)));
        Assert.Equal("U+E0001 invisible format character", DisplayNames.DescribeUnusual(char.ConvertFromUtf32(0xE0001)));
        Assert.Equal("U+E000 private-use or unassigned character", DisplayNames.DescribeUnusual("\uE000"));
    }

    [Fact]
    public void A_long_list_of_different_characters_is_cut_short_with_an_ellipsis()
    {
        var name = string.Concat(Enumerable.Range(0x0430, 20).Select(c => (char)c));

        var described = DisplayNames.DescribeUnusual(name)!;

        Assert.Equal(DisplayNames.MaxDescribed, described.Split(", ").Length - 1);
        Assert.EndsWith(", …", described, StringComparison.Ordinal);
        Assert.StartsWith("U+0430 non-ASCII character, U+0431", described, StringComparison.Ordinal);
    }

    // ---- the display line of an Activity entry ----

    [Fact]
    public void An_activity_entry_shows_a_hostile_name_spelled_out_and_keeps_the_real_argv()
    {
        var name = "pdf\u202Etools\nrm";
        var invocation = new CliInvocation("defenseclaw", new[] { "skill", "block", "--", name }, DateTimeOffset.UtcNow);

        Assert.Equal("defenseclaw skill block -- pdf\\u202Etools\\nrm", invocation.CommandLine);
        Assert.DoesNotContain('\n', invocation.CommandLine);
        Assert.DoesNotContain('\u202E', invocation.CommandLine);

        // What runs is what was asked for, character for character, and so is what the Copy button pastes.
        Assert.Equal(name, invocation.Argv[^1]);
        Assert.Contains('\u202E', invocation.PowerShellCommandLine);
    }

    [Fact]
    public void An_activity_entry_still_quotes_a_spaced_name_and_an_empty_one()
    {
        var invocation = new CliInvocation("defenseclaw", new[] { "skill", "block", "--", "my skill", string.Empty }, DateTimeOffset.UtcNow);

        Assert.Equal("defenseclaw skill block -- \"my skill\" \"\"", invocation.CommandLine);
    }
}
