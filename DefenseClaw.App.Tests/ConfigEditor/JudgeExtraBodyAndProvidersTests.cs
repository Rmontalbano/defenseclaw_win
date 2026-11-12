using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The 1.0.8 keys in the Config editor: <c>guardrail.judge.extra_body</c> (config.go@1.0.8:2494-2529, schema maxProperties 32), the
/// <c>openai-compatible</c> / <c>custom-openai</c> providers (provider.go@1.0.8:482-489) and the retired <c>openshell.middleware.enabled</c>
/// (cmd_config.py@1.0.8:1182-1189). Each is there only for a runtime that has it; a profile without the flags is the catalogue as it was.
/// </summary>
public class JudgeExtraBodyAndProvidersTests
{
    private static readonly ConfigChoiceProfile Older = ConfigChoiceProfile.ForHost();
    private static readonly ConfigChoiceProfile Newer = ConfigChoiceProfile.ForHost(judgeExtraBody: true, openAiCompatibleProviders: true, openShellMiddlewareRetired: true);

    private static YamlNode Node(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Root(string yaml) => (YamlMappingNode)Node(yaml);

    // ------------------------------------------------------------------ extra_body keys

    [Theory]
    [InlineData("chat_template_kwargs")]
    [InlineData("top_k")]
    [InlineData("enable.thinking")]
    [InlineData("Seed-1")]
    public void An_ordinary_field_name_is_accepted(string key)
    {
        Assert.True(ConfigFieldValidator.ValidateExtraBodyKey(key).IsOk);
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("Max-Tokens")]
    [InlineData("MAX.TOKENS")]
    [InlineData("model")]
    [InlineData("Tool_Choice")]
    [InlineData("stream_options")]
    [InlineData("top-p")]
    [InlineData("base_url")]
    [InlineData("Endpoint")]
    public void A_name_the_judge_owns_is_refused_in_any_spelling(string key)
    {
        var result = ConfigFieldValidator.ValidateExtraBodyKey(key);

        Assert.True(result.IsError);
        Assert.Contains("is reserved", result.Message, StringComparison.Ordinal);
        Assert.Contains(key, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x_api_key")]
    [InlineData("Authorization")]
    [InlineData("my_token")]
    [InlineData("client_secret")]
    [InlineData("Password")]
    [InlineData("extra_headers")]
    [InlineData("ca_certificate")]
    [InlineData("credentials")]
    [InlineData("proxy_url")]
    public void A_credential_like_name_is_refused(string key)
    {
        Assert.True(ConfigFieldValidator.ValidateExtraBodyKey(key).IsError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("naïve")]
    [InlineData("a/b")]
    public void A_name_outside_ascii_letters_digits_and_separators_is_refused(string key)
    {
        var result = ConfigFieldValidator.ValidateExtraBodyKey(key);

        Assert.True(result.IsError);
        Assert.Contains("invalid key", result.Message, StringComparison.Ordinal);
        Assert.Contains("use ASCII letters, digits, _, - or .", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Thirty_two_fields_are_fine_and_thirty_three_are_not()
    {
        var ok = Enumerable.Range(0, 32).Select(i => "field" + i).ToArray();
        var tooMany = Enumerable.Range(0, 33).Select(i => "field" + i).ToArray();

        Assert.True(ConfigFieldValidator.ValidateExtraBody(ok).IsOk);
        var result = ConfigFieldValidator.ValidateExtraBody(tooMany);
        Assert.True(result.IsError);
        Assert.Contains("32", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_bad_name_in_sorted_order_is_the_one_reported()
    {
        var result = ConfigFieldValidator.ValidateExtraBody(new[] { "zeta_token", "alpha_secret" });

        Assert.Contains("alpha_secret", result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ extra_body node

    [Fact]
    public void A_mapping_with_a_nested_value_is_accepted_and_nothing_is_accepted_too()
    {
        Assert.True(ConfigFieldValidator.ValidateExtraBodyNode(Node("chat_template_kwargs:\n  enable_thinking: false\n")).IsOk);
        Assert.True(ConfigFieldValidator.ValidateExtraBodyNode(null).IsOk);
        Assert.True(ConfigFieldValidator.ValidateExtraBodyNode(Node("null")).IsOk);
        Assert.True(ConfigFieldValidator.ValidateExtraBodyNode(Node("{}")).IsOk);
    }

    [Theory]
    [InlineData("just text")]
    [InlineData("42")]
    [InlineData("- a\n- b\n")]
    public void A_value_that_is_not_a_mapping_is_refused(string yaml)
    {
        var result = ConfigFieldValidator.ValidateExtraBodyNode(Node(yaml));

        Assert.True(result.IsError);
        Assert.Contains("must be a mapping", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_with_a_reserved_name_is_refused()
    {
        Assert.True(ConfigFieldValidator.ValidateExtraBodyNode(Node("max_tokens: 5\n")).IsError);
    }

    // ------------------------------------------------------------------ the catalogue

    [Fact]
    public void The_extra_body_key_is_offered_only_to_a_runtime_that_has_it()
    {
        Assert.Null(ConfigFieldCatalog.Resolve("guardrail.judge.extra_body", Older));
        Assert.Null(ConfigFieldCatalog.Resolve("guardrail.judge.extra_body"));
        var info = ConfigFieldCatalog.Resolve("guardrail.judge.extra_body", Newer)!;

        Assert.Contains("chat_template_kwargs: {enable_thinking: false}", info.Hint, StringComparison.Ordinal);
        Assert.Contains("base URL", info.Hint, StringComparison.Ordinal);
        Assert.Contains("Releases before 1.0.8 refuse it; unset it before you go back.", info.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_providers_are_in_the_lists_only_with_the_flag()
    {
        Assert.Equal(ConfigFieldCatalog.LlmProviders, ConfigFieldCatalog.Resolve("llm.provider", Older)!.Options);
        Assert.Equal(ConfigFieldCatalog.LlmOverrideProviders, ConfigFieldCatalog.Resolve("guardrail.judge.llm.provider", Older)!.Options);

        var llm = ConfigFieldCatalog.Resolve("llm.provider", Newer)!;
        var judge = ConfigFieldCatalog.Resolve("guardrail.judge.llm.provider", Newer)!;
        Assert.Contains("openai-compatible", llm.Options!);
        Assert.Contains("custom-openai", llm.Options!);
        Assert.Contains("openai-compatible", judge.Options!);
        Assert.Equal(string.Empty, judge.Options![0]);
        Assert.Equal(ConfigFieldCatalog.LlmProviders, llm.Options!.Take(ConfigFieldCatalog.LlmProviders.Count));
        Assert.Contains("base URL", llm.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("base URL", ConfigFieldCatalog.Resolve("llm.provider", Older)!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_middleware_key_is_read_only_with_the_flag_and_unknown_without_it()
    {
        Assert.Null(ConfigFieldCatalog.Resolve("openshell.middleware.enabled", Older));

        var info = ConfigFieldCatalog.Resolve("openshell.middleware.enabled", Newer)!;
        Assert.Equal("Retired in 1.0.8: it had no effect. config set refuses it; remove it from config.yaml. Existing files with the key still load.", info.ReadOnlyReason);
    }

    [Fact]
    public void Each_flag_alone_changes_only_its_own_key()
    {
        var extraOnly = ConfigChoiceProfile.ForHost(judgeExtraBody: true);

        Assert.NotNull(ConfigFieldCatalog.Resolve("guardrail.judge.extra_body", extraOnly));
        Assert.Null(ConfigFieldCatalog.Resolve("openshell.middleware.enabled", extraOnly));
        Assert.Equal(ConfigFieldCatalog.LlmProviders, ConfigFieldCatalog.Resolve("llm.provider", extraOnly)!.Options);
    }

    [Fact]
    public void A_profile_without_the_flags_is_the_catalogue_as_it_was()
    {
        var spelledOut = ConfigChoiceProfile.ForHost(judgeExtraBody: false, openAiCompatibleProviders: false, openShellMiddlewareRetired: false);
        foreach (var path in ConfigFieldCatalog.ExactPaths)
        {
            var a = ConfigFieldCatalog.Resolve(path, Older);
            var b = ConfigFieldCatalog.Resolve(path, spelledOut);
            Assert.Equal(a?.Hint, b?.Hint);
            Assert.Equal(a?.Options, b?.Options);
            Assert.Equal(a?.ReadOnlyReason, b?.ReadOnlyReason);
        }

        var known = ConfigFieldCatalog.ExactPaths.Where(p => ConfigFieldCatalog.Resolve(p, Older) is not null).ToArray();
        Assert.DoesNotContain("guardrail.judge.extra_body", known);
        Assert.DoesNotContain("openshell.middleware.enabled", known);
        Assert.DoesNotContain("openai-compatible", ConfigFieldCatalog.LlmProviders);
        Assert.DoesNotContain("custom-openai", ConfigFieldCatalog.LlmOverrideProviders);
    }

    // ------------------------------------------------------------------ base_url for the new providers

    [Theory]
    [InlineData("openai-compatible")]
    [InlineData("custom-openai")]
    [InlineData("Custom-OpenAI")]
    public void A_compatible_provider_without_a_base_url_is_a_warning(string provider)
    {
        var result = ConfigFieldValidator.ValidateCompatibleProvider(provider, "  ");

        Assert.True(result.IsWarning);
        Assert.Contains("requires base_url", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_url_of_its_own_or_an_inherited_one_clears_the_warning_and_other_providers_are_not_asked()
    {
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider("openai-compatible", "http://localhost:8000/v1").IsOk);
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider("openai-compatible", "", "http://localhost:8000/v1").IsOk);
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider("openai-compatible", "/").IsWarning);
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider("openai", "").IsOk);
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider("", "").IsOk);
        Assert.True(ConfigFieldValidator.ValidateCompatibleProvider(null, null).IsOk);
    }

    // ------------------------------------------------------------------ the form's findings

    private const string Yaml = """
        llm:
          provider: openai-compatible
          model: m
        guardrail:
          judge:
            extra_body:
              max_tokens: 5
            llm:
              provider: custom-openai
        openshell:
          middleware:
            enabled: true
        """;

    [Fact]
    public void The_form_reports_each_finding_for_a_runtime_that_has_the_keys()
    {
        var lines = ConfigFieldFindings.For(Root(Yaml), Newer);

        Assert.Contains(lines, l => l.StartsWith("guardrail.judge.extra_body:", StringComparison.Ordinal) && l.Contains("max_tokens", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("llm:", StringComparison.Ordinal) && l.Contains("requires base_url", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("guardrail.judge.llm:", StringComparison.Ordinal) && l.Contains("requires base_url", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("openshell", StringComparison.Ordinal));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void A_base_url_on_the_unified_llm_covers_the_override_blocks()
    {
        var yaml = "llm:\n  provider: openai-compatible\n  base_url: http://localhost:8000\nguardrail:\n  judge:\n    llm:\n      provider: custom-openai\n";

        Assert.Empty(ConfigFieldFindings.For(Root(yaml), Newer));
    }

    [Fact]
    public void A_clean_config_has_no_findings_and_a_runtime_without_the_keys_has_none_at_all()
    {
        Assert.Empty(ConfigFieldFindings.For(Root("llm:\n  provider: openai\n"), Newer));
        Assert.Empty(ConfigFieldFindings.For(Root(Yaml), Older));
        Assert.Empty(ConfigFieldFindings.For(Root(Yaml), ConfigChoiceProfile.Windows));
    }

    [Fact]
    public void The_built_form_carries_the_findings_among_its_warnings_and_the_middleware_field_is_read_only()
    {
        var file = LineEndings.Normalize(Yaml) + "\n";
        var newer = ConfigFormBuilder.Build(file, ConfigStore.Parse(file), _ => { }, _ => { }, Newer);
        var older = ConfigFormBuilder.Build(file, ConfigStore.Parse(file), _ => { }, _ => { }, Older);

        Assert.Equal(3, newer.Warnings.Count);
        Assert.Empty(older.Warnings);

        var middleware = Flatten(newer.Sections).Single(f => f.Path == "openshell.middleware.enabled");
        Assert.False(middleware.IsEditable);
        Assert.Contains("Retired in 1.0.8", middleware.DisabledReason, StringComparison.Ordinal);
    }

    private static IEnumerable<FormField> Flatten(IEnumerable<FormGroup> groups) =>
        groups.SelectMany(g => g.Fields.Concat(Flatten(g.SubGroups)));

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void The_retired_middleware_key_is_information_not_a_warning_whatever_its_value(string value)
    {
        var yaml = "openshell:" + Environment.NewLine + "  middleware:" + Environment.NewLine + "    enabled: " + value + Environment.NewLine;

        Assert.Empty(ConfigFieldFindings.For(Root(yaml), Newer));

        var built = ConfigFormBuilder.Build(yaml, ConfigStore.Parse(yaml), _ => { }, _ => { }, Newer);
        Assert.Empty(built.Warnings);
        var field = Flatten(built.Sections).Single(f => f.Path == "openshell.middleware.enabled");
        Assert.False(field.IsEditable);
        Assert.Contains("Retired in 1.0.8", field.DisabledReason, StringComparison.Ordinal);
    }
}
