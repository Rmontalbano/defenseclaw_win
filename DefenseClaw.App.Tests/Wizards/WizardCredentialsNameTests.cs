using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// <see cref="WizardCredentials.IsValidName"/> is the only gate between a variable name and the <c>cmd /c</c> line
/// <see cref="WizardCredentials.OpenKeysSetTerminal"/> builds, so it has to be an exact match - not "a match up to a final newline"
/// (.NET's <c>$</c> anchor), which is what it was.
/// </summary>
public sealed class WizardCredentialsNameTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("_")]
    [InlineData("DEFENSECLAW_LLM_KEY")]
    [InlineData("splunk_Access_Token2")]
    public void A_plain_environment_variable_name_is_valid(string name) =>
        Assert.True(WizardCredentials.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1ABC")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("A=1")]
    [InlineData("A&calc")]
    [InlineData("A\"B")]
    [InlineData("%PATH%")]
    [InlineData("A.B")]
    [InlineData("ÄB")]
    public void Anything_else_is_refused(string name) =>
        Assert.False(WizardCredentials.IsValidName(name));

    [Theory]
    [InlineData("NAME\n")]
    [InlineData("NAME\r\n")]
    [InlineData("NAME\n\n")]
    [InlineData("NAME\0")]
    [InlineData("\nNAME")]
    public void A_line_break_after_the_name_is_not_part_of_a_valid_name(string name) =>
        Assert.False(WizardCredentials.IsValidName(name));

    [Fact]
    public void A_name_longer_than_128_characters_is_refused()
    {
        Assert.True(WizardCredentials.IsValidName(new string('A', 128)));
        Assert.False(WizardCredentials.IsValidName(new string('A', 129)));
    }

    [Fact]
    public void Null_is_refused() =>
        Assert.False(WizardCredentials.IsValidName(null));

    [Fact]
    public void Opening_a_console_for_a_name_with_a_trailing_newline_is_refused_for_the_name()
    {
        // Nothing can be found and nothing can start: were the name check ever to let "NAME\n" through, the answer would be the
        // "not on PATH" sentence instead (and still no process), which is what the assertion tells apart.
        var paths = new DefenseClaw.Core.Paths.DefenseClawPaths(
            dataDirectory: @"C:\nonexistent\.defenseclaw",
            binDirectory: @"C:\nonexistent\bin",
            searchPath: Array.Empty<string>(),
            fileExists: _ => false,
            persistedSearchPath: Array.Empty<string>);
        var credentials = new WizardCredentials(paths);

        var refusal = credentials.OpenKeysSetTerminal("NAME\n");

        Assert.NotNull(refusal);
        Assert.Contains("variable name", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("not on PATH", refusal, StringComparison.Ordinal);
    }
}
