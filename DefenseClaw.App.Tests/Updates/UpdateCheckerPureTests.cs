using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.Updates;

public class UpdateCheckerPureTests
{
    // ------------------------------------------------------------------ Compare

    [Theory]
    [InlineData("0.8.10", "v0.8.10")]
    [InlineData("v0.8.10", "0.8.10")]
    [InlineData("V0.8.10", "v0.8.10")]
    [InlineData("0.8.10", "0.8.10")]
    [InlineData("  0.8.10 ", "v0.8.10")]
    [InlineData("0.8.10-rc1", "v0.8.10")]
    [InlineData("0.8.10+build.5", "v0.8.10")]
    public void The_same_version_however_it_is_spelled_is_up_to_date(string installed, string tag)
    {
        var (state, detail) = UpdateChecker.Compare(installed, tag);

        Assert.Equal(UpdateCheckState.UpToDate, state);
        Assert.Contains("matches the latest release", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.8.7", "v0.8.10")]
    [InlineData("0.8.9", "v0.8.10")]
    [InlineData("0.8.10", "v0.9.0")]
    [InlineData("0.8.10", "v1.0.0")]
    [InlineData("0.8.10", "v0.8.10.1")]
    [InlineData("1.2", "v1.10")]
    public void An_older_installed_version_is_an_update_and_versions_compare_as_numbers_not_text(string installed, string tag)
    {
        var (state, detail) = UpdateChecker.Compare(installed, tag);

        Assert.Equal(UpdateCheckState.UpdateAvailable, state);
        Assert.Contains(tag, detail, StringComparison.Ordinal);
        Assert.Contains(installed, detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.9.0", "v0.8.10")]
    [InlineData("0.8.11", "v0.8.10")]
    [InlineData("1.0.0-rc1", "v0.8.10")]
    [InlineData("0.8.10.1", "v0.8.10")]
    public void A_newer_installed_version_is_not_an_update(string installed, string tag)
    {
        var (state, detail) = UpdateChecker.Compare(installed, tag);

        Assert.Equal(UpdateCheckState.UpToDate, state);
        Assert.Contains("same as or newer", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.8.10", null)]
    [InlineData("0.8.10", "")]
    [InlineData("0.8.10", "   ")]
    public void A_latest_release_without_a_tag_is_a_failed_check_never_up_to_date(string installed, string? tag)
    {
        var (state, detail) = UpdateChecker.Compare(installed, tag);

        Assert.Equal(UpdateCheckState.CheckFailed, state);
        Assert.Contains("no version tag", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_installed_version_is_a_failed_check_never_up_to_date(string? installed)
    {
        var (state, detail) = UpdateChecker.Compare(installed, "v0.8.10");

        Assert.Equal(UpdateCheckState.CheckFailed, state);
        Assert.Contains("installed version", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown", "v0.8.10")]
    [InlineData("0.8.10", "latest")]
    [InlineData("dev", "nightly")]
    [InlineData("1", "2")]
    public void Versions_that_cannot_be_compared_are_a_failed_check_not_a_guess(string installed, string tag)
    {
        var (state, detail) = UpdateChecker.Compare(installed, tag);

        Assert.Equal(UpdateCheckState.CheckFailed, state);
        Assert.Contains("could not be compared reliably", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_text_that_is_not_a_version_still_matches()
    {
        var (state, _) = UpdateChecker.Compare("nightly", "nightly");

        Assert.Equal(UpdateCheckState.UpToDate, state);
    }

    // ------------------------------------------------------------------ --version-json

    [Fact]
    public void The_real_version_json_is_read()
    {
        Assert.Equal(
            "0.8.10",
            UpdateChecker.TryParseVersionJson("""{"name":"defenseclaw-cli","schema_version":1,"version":"0.8.10"}"""));
    }

    [Fact]
    public void Extra_fields_whitespace_and_surrounding_newlines_are_tolerated()
    {
        const string text = "\r\n  {\r\n    \"schema_version\": 2,\r\n    \"build\": {\"commit\": \"abc\"},\r\n    \"version\": \"  0.9.1-rc2  \",\r\n    \"more\": [1, 2]\r\n  }\r\n";

        Assert.Equal("0.9.1-rc2", UpdateChecker.TryParseVersionJson(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("defenseclaw, version 0.8.10")]
    [InlineData("{")]
    [InlineData("[\"0.8.10\"]")]
    [InlineData("\"0.8.10\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"name": "defenseclaw-cli"}""")]
    [InlineData("""{"version": null}""")]
    [InlineData("""{"version": ""}""")]
    [InlineData("""{"version": "   "}""")]
    [InlineData("""{"version": 8}""")]
    [InlineData("""{"version": {"major": 0}}""")]
    [InlineData("""{"version": ["0.8.10"]}""")]
    public void Anything_that_is_not_an_object_with_a_non_blank_string_version_is_a_miss(string? text)
    {
        Assert.Null(UpdateChecker.TryParseVersionJson(text!));
    }

    [Fact]
    public void An_error_message_a_cli_prints_for_an_unknown_flag_is_a_miss_so_the_caller_falls_back()
    {
        const string usage = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\nTry 'defenseclaw --help' for help.\n\nError: No such option: --version-json";

        Assert.Null(UpdateChecker.TryParseVersionJson(usage));
    }
}
