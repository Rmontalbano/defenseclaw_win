using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>keys list --json</c> as the Credentials card reads it: the shape is 0.8.10's <c>cmd_keys._status_to_dict</c>, and the
/// missing-required count must agree with the TUI's <c>missing_credential_rows</c>. Every name below is synthetic.
/// </summary>
public sealed class CredentialListParserTests
{
    private const string Sample = """
        [
          {"env_name": "EXAMPLE_JUDGE_KEY", "canonical_env_name": "EXAMPLE_JUDGE_KEY", "feature": "LLM judge",
           "description": "Key for the judge model", "requirement": "required", "source": "unset", "auto_detected": false, "set": false},
          {"env_name": "EXAMPLE_SCANNER_KEY", "canonical_env_name": "EXAMPLE_SCANNER_KEY", "feature": "Scanner",
           "description": "Scanner key", "requirement": "REQUIRED", "source": "dotenv", "auto_detected": false, "set": true},
          {"env_name": "EXAMPLE_OPTIONAL_KEY", "canonical_env_name": "EXAMPLE_OPTIONAL_KEY", "feature": "Telemetry",
           "description": "Optional", "requirement": "optional", "source": "unset", "auto_detected": false, "set": false},
          {"env_name": "EXAMPLE_UNUSED_KEY", "canonical_env_name": "EXAMPLE_UNUSED_KEY", "feature": "Other",
           "description": "Not used", "requirement": "not_used", "source": "env", "auto_detected": true, "set": true}
        ]
        """;

    [Fact]
    public void Reads_every_column_and_counts_missing_required_like_the_tui()
    {
        Assert.True(CredentialListParser.TryParse(Sample, out var rows, out var error), error);

        Assert.Equal(4, rows.Count);
        Assert.Equal("EXAMPLE_JUDGE_KEY", rows[0].EnvName);
        Assert.Equal("LLM judge", rows[0].Feature);
        Assert.Equal("required", rows[1].Requirement); // lower-cased, as the TUI reads it
        Assert.Equal("dotenv", rows[1].Source);
        Assert.True(rows[1].IsSet);
        Assert.Equal("not_used", rows[3].Requirement);

        // Required and unset only: an unset optional or a set required key does not count.
        Assert.Equal(1, rows.Count(r => r.IsMissingRequired));
        Assert.True(rows[0].IsMissingRequired);
    }

    [Theory]
    [InlineData("deprecation notice\n")]
    [InlineData("[warn] config drift detected\n")] // a banner that itself starts with a bracket must not hide the array
    [InlineData("first line\nsecond line\n")]
    public void Tolerates_noise_before_the_array(string noise)
    {
        Assert.True(CredentialListParser.TryParse(noise + Sample, out var rows, out var error), error);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Accepts_the_go_style_pascal_case_keys_the_tui_also_reads()
    {
        const string go = """[{"EnvName": "EXAMPLE_KEY", "Feature": "F", "Requirement": "Required", "Source": "env", "Set": true}]""";

        Assert.True(CredentialListParser.TryParse(go, out var rows, out _));

        var row = Assert.Single(rows);
        Assert.Equal("EXAMPLE_KEY", row.EnvName);
        Assert.Equal("required", row.Requirement);
        Assert.True(row.IsSet);
        Assert.False(row.IsMissingRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_output_is_no_rows_not_an_error(string text)
    {
        Assert.True(CredentialListParser.TryParse(text, out var rows, out var error));

        Assert.Empty(rows);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"env_name": "X"}""")] // an object, not a list
    [InlineData("[ {unterminated")]
    public void Output_that_is_not_a_list_is_an_error_with_no_rows(string text)
    {
        Assert.False(CredentialListParser.TryParse(text, out var rows, out var error));

        Assert.Empty(rows);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void A_row_has_nowhere_to_hold_a_value_even_when_the_cli_is_asked_to_print_one()
    {
        // `--show-values` adds value_masked; the model has no member for it, so it cannot reach the UI or Activity.
        const string withValue = """[{"env_name": "EXAMPLE_KEY", "requirement": "required", "set": true, "value_masked": "sk-1…9xyz"}]""";

        Assert.True(CredentialListParser.TryParse(withValue, out var rows, out _));

        Assert.DoesNotContain(typeof(CredentialRow).GetProperties(), p => p.Name.Contains("Value", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sk-1", rows[0].ToString(), StringComparison.Ordinal);
    }
}
