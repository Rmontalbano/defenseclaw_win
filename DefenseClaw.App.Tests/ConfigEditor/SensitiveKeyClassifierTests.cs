using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

public class SensitiveKeyClassifierTests
{
    [Theory]
    [InlineData("token_env")]
    [InlineData("api_key_env")]
    [InlineData("API_KEY_ENV")]
    [InlineData("judge_api_key_env")]
    public void Env_name_keys_hold_a_variable_name(string key)
    {
        Assert.True(SensitiveKeyClassifier.IsEnvNameKey(key));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("envtoken")]
    [InlineData("environment")]
    [InlineData("env_token")]
    public void Other_keys_are_not_env_name_keys(string key)
    {
        Assert.False(SensitiveKeyClassifier.IsEnvNameKey(key));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("client_secret")]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("credentials")]
    [InlineData("authorization")]
    [InlineData("bearer_value")]
    [InlineData("tls_cert_pem")]
    public void Keys_that_carry_a_literal_secret_are_secret_keys(string key)
    {
        Assert.True(SensitiveKeyClassifier.IsSecretKey(key));
    }

    [Theory]
    [InlineData("token_env")]
    [InlineData("api_key_env")]
    [InlineData("client_secret_env")]
    public void An_env_name_key_is_never_a_secret_key_even_though_it_matches_the_pattern(string key)
    {
        Assert.False(SensitiveKeyClassifier.IsSecretKey(key));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("api_port")]
    [InlineData("scan_roots")]
    [InlineData("pem_directory")]
    [InlineData("endpoint")]
    public void Ordinary_keys_are_not_secret_keys(string key)
    {
        Assert.False(SensitiveKeyClassifier.IsSecretKey(key));
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("Headers")]
    [InlineData("extra_headers")]
    [InlineData("EXTRA_HEADERS")]
    public void Header_map_keys_are_recognised(string key)
    {
        Assert.True(SensitiveKeyClassifier.IsHeaderMapKey(key));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("x-headers")]
    [InlineData("headers_env")]
    [InlineData("")]
    public void Other_keys_are_not_header_map_keys(string key)
    {
        Assert.False(SensitiveKeyClassifier.IsHeaderMapKey(key));
    }

    [Theory]
    [InlineData("[REDACTED]")]
    [InlineData("[redacted]")]
    [InlineData("[REDACTED_URL]")]
    [InlineData("https://h/[REDACTED]")]
    [InlineData("https://[REDACTED]@h/x")]
    [InlineData("***REDACTED***")]
    [InlineData("***")]
    [InlineData("****")]
    [InlineData("abcd***wxyz")]
    public void Masking_placeholders_are_masked_values(string value)
    {
        Assert.True(SensitiveKeyClassifier.IsMaskedValue(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-live-abcdef")]
    [InlineData("4096")]
    [InlineData("REDACTED")]
    [InlineData("**")]
    [InlineData("a*b*c")]
    [InlineData("https://api.example.com/v1")]
    public void Ordinary_text_is_not_a_masked_value(string? value)
    {
        Assert.False(SensitiveKeyClassifier.IsMaskedValue(value));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("mode: observe", 0)]
    [InlineData("token: '[REDACTED]'", 1)]
    [InlineData("endpoint: https://h/[REDACTED]", 1)]
    [InlineData("a: '[REDACTED]'\nb: [REDACTED_URL]\nc: ***", 3)]
    [InlineData("a: [redacted]\nb: [Redacted_Url]", 2)]
    public void Mask_markers_are_counted(string? text, int expected)
    {
        Assert.Equal(expected, SensitiveKeyClassifier.CountMaskMarkers(text));
    }

    [Fact]
    public void Removing_a_placeholder_lowers_the_count_and_adding_one_raises_it()
    {
        var before = "gateway:\n  token: '[REDACTED]'\n  host: h\n";
        var without = "gateway:\n  token: real\n  host: h\n";
        var more = "gateway:\n  token: '[REDACTED]'\n  host: '[REDACTED]'\n";

        Assert.True(SensitiveKeyClassifier.CountMaskMarkers(without) < SensitiveKeyClassifier.CountMaskMarkers(before));
        Assert.True(SensitiveKeyClassifier.CountMaskMarkers(more) > SensitiveKeyClassifier.CountMaskMarkers(before));
    }

    [Theory]
    [InlineData("gateway:\n  token_env: DC_TOKEN\n", true)]
    [InlineData("llm:\n  api_key: sk-abc\n", true)]
    [InlineData("list:\n  - password: hunter2\n", true)]
    [InlineData("    deeply:\n      nested_secret: x\n", true)]
    [InlineData("guardrail:\n  mode: observe\n  scan_roots:\n  - '~'\n", false)]
    [InlineData("# token: commented out\nmode: observe\n", false)]
    [InlineData("", false)]
    public void Sensitive_references_are_found_by_key_name_at_any_depth(string yaml, bool expected)
    {
        Assert.Equal(expected, SensitiveKeyClassifier.ContainsSensitiveReferences(yaml));
    }

    [Fact]
    public void Sensitive_reference_scan_handles_crlf_text()
    {
        Assert.True(SensitiveKeyClassifier.ContainsSensitiveReferences("gateway:\r\n  token_env: DC_TOKEN\r\n"));
        Assert.False(SensitiveKeyClassifier.ContainsSensitiveReferences("gateway:\r\n  host: h\r\n"));
    }
}
