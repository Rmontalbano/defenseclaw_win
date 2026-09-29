using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The line-break normaliser the text parsers start with. The CLI's stdout is CRLF on Windows, so "a stray
/// <c>\r</c>" is the ordinary case for them, not an edge.
/// </summary>
public class CliTextTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\r\nb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\n\r\nb\r\n", "a\n\nb\n")]
    [InlineData("a\n\rb", "a\n\nb")]
    [InlineData("\r\n", "\n")]
    [InlineData("no breaks at all", "no breaks at all")]
    [InlineData("", "")]
    public void Every_kind_of_line_break_becomes_one_lf(string text, string expected)
    {
        Assert.Equal(expected, CliText.NormalizeLineEndings(text));
    }

    [Fact]
    public void Text_without_a_carriage_return_is_returned_as_is()
    {
        var text = "one\ntwo\n";

        Assert.Same(text, CliText.NormalizeLineEndings(text));
    }

    [Fact]
    public void Null_text_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CliText.NormalizeLineEndings(null!));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_test_helpers_agree_with_the_production_normaliser(string eol)
    {
        const string text = "alpha\nbeta\n\ngamma\n";

        Assert.Equal(text, CliText.NormalizeLineEndings(LineEndings.With(text, eol)));
        Assert.True(LineEndings.IsUniform(LineEndings.With(text, eol), eol));
    }
}
