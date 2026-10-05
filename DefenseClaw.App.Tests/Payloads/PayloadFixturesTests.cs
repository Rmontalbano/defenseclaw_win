using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// <see cref="PayloadFixtures.Read"/> joins a caller's name onto the fixtures folder with <see cref="Path.Combine(string[])"/>,
/// which discards everything before an absolute path and happily follows <c>..</c>. The names are the tests' own literals
/// today; this holds the helper to "a bare file name" so a future caller cannot read (or be handed) a file from anywhere else.
/// </summary>
public sealed class PayloadFixturesTests
{
    [Fact]
    public void A_bare_fixture_name_is_read_from_the_fixtures_folder()
    {
        var text = PayloadFixtures.Read("registry-list.empty.json");

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\registry-list.empty.json")]
    [InlineData("../registry-list.empty.json")]
    [InlineData(@"sub\registry-list.empty.json")]
    [InlineData("sub/registry-list.empty.json")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"C:win.ini")]
    [InlineData("/etc/hosts")]
    [InlineData(@"\\server\share\registry-list.empty.json")]
    public void A_name_that_is_not_a_bare_file_name_is_refused(string name)
    {
        var refused = Assert.Throws<ArgumentException>(() => PayloadFixtures.Read(name));

        Assert.Equal("fileName", refused.ParamName);
    }

    [Fact]
    public void A_missing_bare_name_is_a_missing_file_not_a_refusal()
    {
        _ = Assert.Throws<FileNotFoundException>(() => PayloadFixtures.Read("no-such-fixture.json"));
    }
}
