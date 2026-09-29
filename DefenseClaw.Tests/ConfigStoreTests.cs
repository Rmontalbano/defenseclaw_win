using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Parses a replica of the real 0.8.7 config.yaml (Fixtures/config.yaml), keyed to the
/// structure the live install writes.
/// </summary>
public class ConfigStoreTests
{
    private static ConfigDocument LoadFixture() =>
        ConfigStore.Parse(FixtureFiles.ReadText(FixtureFiles.ConfigYaml), FixtureFiles.ConfigYaml);

    [Fact]
    public void Parses_top_level_scalars()
    {
        var config = LoadFixture().Config;

        Assert.Equal(8, config.ConfigVersion);
        Assert.Equal("claudecode", config.Claw.Mode);
    }

    [Fact]
    public void Parses_gateway_section()
    {
        var config = LoadFixture().Config;

        Assert.Equal("DEFENSECLAW_GATEWAY_TOKEN", config.Gateway.TokenEnv);
        Assert.Null(config.Gateway.Token);
    }

    [Fact]
    public void Gateway_api_port_defaults_to_18970_when_absent()
    {
        // The real config.yaml carries no api_port; the sidecar still listens on 18970.
        Assert.Equal(18970, LoadFixture().Config.Gateway.ApiPort);
        Assert.False(LoadFixture().Config.Gateway.HasExplicitApiPort);
    }

    [Fact]
    public void Explicit_api_port_overrides_the_default()
    {
        var config = ConfigStore.Parse("gateway:\n  api_port: 19999\n").Config;

        Assert.Equal(19999, config.Gateway.ApiPort);
        Assert.True(config.Gateway.HasExplicitApiPort);
    }

    [Fact]
    public void Parses_guardrail_section_including_per_connector_settings()
    {
        var guardrail = LoadFixture().Config.Guardrail;

        Assert.Equal("claudecode", guardrail.Connector);
        Assert.True(guardrail.Enabled);
        Assert.Equal("local", guardrail.ScannerMode);
        Assert.Equal("regex_only", guardrail.DetectionStrategyCompletion);

        var connector = Assert.Contains("claudecode", (IReadOnlyDictionary<string, GuardrailConnectorSettings>)guardrail.Connectors);
        Assert.Equal("observe", connector.Mode);
        Assert.Equal("open", connector.HookFailMode);
        Assert.Equal(string.Empty, connector.BlockMessage);
        Assert.Equal(string.Empty, connector.RulePackDir);
        Assert.False(connector.HasFailModeMismatch);
    }

    [Fact]
    public void Flags_the_observe_plus_fail_closed_mismatch()
    {
        var settings = new GuardrailConnectorSettings { Mode = "observe", HookFailMode = "closed" };

        Assert.True(settings.HasFailModeMismatch);
    }

    [Fact]
    public void Parses_credential_env_var_names_not_values()
    {
        var config = LoadFixture().Config;

        Assert.Equal("CISCO_AI_DEFENSE_API_KEY", config.CiscoAiDefense.ApiKeyEnv);
        Assert.Equal("DEFENSECLAW_LLM_KEY", config.Llm.ApiKeyEnv);
    }

    [Fact]
    public void Parses_ai_discovery_section()
    {
        var discovery = LoadFixture().Config.AiDiscovery;

        Assert.True(discovery.Enabled);
        Assert.Equal("enhanced", discovery.Mode);
        Assert.Equal(5, discovery.ScanIntervalMin);
        Assert.Equal(60, discovery.ProcessIntervalS);
        Assert.Equal(new[] { "~" }, discovery.ScanRoots);
        Assert.Empty(discovery.SignaturePacks);
        Assert.False(discovery.AllowWorkspaceSignatures);
        Assert.True(discovery.IncludeShellHistory);
        Assert.True(discovery.IncludePackageManifests);
        Assert.True(discovery.IncludeEnvVarNames);
        Assert.True(discovery.IncludeNetworkDomains);
        Assert.Equal(1000, discovery.MaxFilesPerScan);
        Assert.Equal(524288, discovery.MaxFileBytes);
        Assert.False(discovery.StoreRawLocalPaths);
        Assert.False(discovery.RequireTrustedBinaryPaths);
        Assert.Empty(discovery.TrustedBinaryPrefixes);
    }

    [Fact]
    public void Keeps_the_raw_text_verbatim()
    {
        var expected = FixtureFiles.ReadText(FixtureFiles.ConfigYaml);

        Assert.Equal(expected, LoadFixture().RawText);
    }

    [Fact]
    public void Splits_every_top_level_section_into_raw_blocks()
    {
        var document = LoadFixture();

        Assert.Equal(
            new[]
            {
                "ai_discovery", "cisco_ai_defense", "claw", "config_version",
                "gateway", "guardrail", "llm", "observability",
            },
            document.Sections.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Contains("token_env: DEFENSECLAW_GATEWAY_TOKEN", document.SectionText("gateway")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_blocks_concatenate_back_into_the_original_file()
    {
        var document = LoadFixture();

        Assert.Equal(document.RawText, string.Concat(document.Sections.Values));
    }

    [Fact]
    public void Unknown_sections_survive_as_raw_text()
    {
        var yaml = """
            gateway:
              token_env: DEFENSECLAW_GATEWAY_TOKEN
            future_feature:
              # a setting this build has never heard of
              knob: 42
              nested:
                deeper: true
            """;

        var document = ConfigStore.Parse(yaml);

        var unknown = Assert.Contains("future_feature", document.UnknownSections);
        Assert.Contains("knob: 42", unknown, StringComparison.Ordinal);
        Assert.Contains("deeper: true", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway", document.UnknownSections.Keys);
    }

    [Fact]
    public void WithSectionReplaced_touches_only_that_section()
    {
        var document = LoadFixture();

        var updated = document.WithSectionReplaced("claw", "claw:\n  mode: codex\n");

        Assert.Contains("mode: codex", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: claudecode", updated, StringComparison.Ordinal);
        // Everything else is byte-identical.
        Assert.Contains("token_env: DEFENSECLAW_GATEWAY_TOKEN", updated, StringComparison.Ordinal);
        Assert.Contains("max_file_bytes: 524288", updated, StringComparison.Ordinal);
        Assert.Contains("api_key_env: CISCO_AI_DEFENSE_API_KEY", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSectionReplaced_appends_a_missing_section()
    {
        var document = ConfigStore.Parse("claw:\n  mode: claudecode\n");

        var updated = document.WithSectionReplaced("gateway", "gateway:\n  api_port: 18970");

        Assert.Equal("claw:\n  mode: claudecode\ngateway:\n  api_port: 18970\n", updated);
    }

    [Fact]
    public void Missing_config_file_yields_defaults_rather_than_throwing()
    {
        using var temp = new TempDirectory();
        var store = new ConfigStore(new DefenseClawPaths(dataDirectory: temp.Path));

        var document = store.Load();

        Assert.Equal(string.Empty, document.RawText);
        Assert.Equal(18970, document.Config.Gateway.ApiPort);
        Assert.Equal("DEFENSECLAW_GATEWAY_TOKEN", document.Config.Gateway.TokenEnv);
    }

    [Fact]
    public void Loads_from_disk_through_the_paths_object()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", FixtureFiles.ReadText(FixtureFiles.ConfigYaml));
        var store = new ConfigStore(new DefenseClawPaths(dataDirectory: temp.Path));

        var document = store.Load();

        Assert.Equal("claudecode", document.Config.Claw.Mode);
        Assert.Equal(temp.File("config.yaml"), document.Path);
    }

    [Fact]
    public void Malformed_yaml_raises_ConfigParseException()
    {
        var ex = Assert.Throws<ConfigParseException>(() =>
            ConfigStore.Parse("gateway:\n\ttoken_env: tabs-are-illegal\n", "bad.yaml"));

        Assert.Equal("bad.yaml", ex.Path);
    }

    // ----------------------------------------------------------------------------------
    // Empty sections. `gateway:` with no children is valid YAML meaning null, and YamlDotNet
    // assigns that null over the property initializer. Every consumer used to dereference the
    // section unguarded (TokenResolver reads config.Gateway.TokenEnv on the watcher thread), so
    // one commented-out block in config.yaml was a crash. The model must never hand out a null.
    // ----------------------------------------------------------------------------------

    private static void AssertNothingIsNull(DefenseClawConfig config)
    {
        Assert.NotNull(config.Claw);
        Assert.NotNull(config.Gateway);
        Assert.NotNull(config.Guardrail);
        Assert.NotNull(config.CiscoAiDefense);
        Assert.NotNull(config.Llm);
        Assert.NotNull(config.AiDiscovery);
        Assert.NotNull(config.Guardrail.Connectors);
        Assert.All(config.Guardrail.Connectors.Values, Assert.NotNull);
        Assert.NotNull(config.AiDiscovery.ScanRoots);
        Assert.NotNull(config.AiDiscovery.SignaturePacks);
        Assert.NotNull(config.AiDiscovery.DisabledSignatureIds);
        Assert.NotNull(config.AiDiscovery.TrustedBinaryPrefixes);
        Assert.All(config.AiDiscovery.ScanRoots, Assert.NotNull);
        Assert.All(config.AiDiscovery.SignaturePacks, Assert.NotNull);
        Assert.All(config.AiDiscovery.DisabledSignatureIds, Assert.NotNull);
        Assert.All(config.AiDiscovery.TrustedBinaryPrefixes, Assert.NotNull);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing but a comment\n")]
    [InlineData("gateway:\n")]
    [InlineData("guardrail:\n")]
    [InlineData("claw:\n")]
    [InlineData("llm:\n")]
    [InlineData("cisco_ai_defense:\n")]
    [InlineData("ai_discovery:\n")]
    [InlineData("gateway:\nguardrail:\nclaw:\nllm:\ncisco_ai_defense:\nai_discovery:\n")]
    [InlineData("guardrail:\n  connectors:\n")]
    [InlineData("guardrail:\n  connectors:\n    claudecode:\n")]
    [InlineData("guardrail:\n  connectors:\n    claudecode:\n    codex:\n      mode: observe\n")]
    [InlineData("ai_discovery:\n  scan_roots:\n  signature_packs:\n  disabled_signature_ids:\n  trusted_binary_prefixes:\n")]
    [InlineData("ai_discovery:\n  scan_roots:\n    -\n    - ~\n")]
    public void Empty_sections_parse_to_defaults_never_null(string yaml)
    {
        var config = ConfigStore.Parse(yaml).Config;

        AssertNothingIsNull(config);

        // Defaults still apply where nothing was said.
        Assert.Equal(GatewaySection.DefaultApiPort, config.Gateway.ApiPort);
        Assert.False(config.Gateway.HasExplicitApiPort);
        Assert.Equal(GatewaySection.DefaultTokenEnv, config.Gateway.TokenEnv);
        Assert.Null(config.Gateway.Token);
    }

    [Theory]
    [InlineData("gateway:\n")]
    [InlineData("gateway:\n  token_env:\n")]
    [InlineData("guardrail:\n  connectors:\n    claudecode:\n")]
    [InlineData("gateway:\nguardrail:\nclaw:\nllm:\ncisco_ai_defense:\nai_discovery:\n")]
    public void Token_resolution_survives_an_empty_section(string yaml)
    {
        var config = ConfigStore.Parse(yaml).Config;
        var resolver = new TokenResolver(
            () => new Dictionary<string, string>(),
            new DictionaryEnvironmentReader(new Dictionary<string, string>
            {
                [GatewaySection.DefaultTokenEnv] = "from-the-environment",
            }));

        var resolution = resolver.Resolve(config);

        Assert.True(resolution.Found);
        Assert.Equal(TokenSource.Environment, resolution.Source);
        Assert.Equal(GatewaySection.DefaultTokenEnv, resolution.VariableName);
    }

    [Fact]
    public void An_empty_connector_entry_keeps_its_key_with_default_settings()
    {
        var config = ConfigStore.Parse("guardrail:\n  connectors:\n    claudecode:\n").Config;

        Assert.True(config.Guardrail.Connectors.TryGetValue("claudecode", out var settings));
        Assert.NotNull(settings);

        Assert.Null(settings.Mode);
        Assert.Null(settings.HookFailMode);
        Assert.False(settings.HasFailModeMismatch);
    }

    [Fact]
    public void Connector_names_match_case_insensitively_after_parsing()
    {
        // The declared comparer is OrdinalIgnoreCase, but YamlDotNet assigns a dictionary of its
        // own; the setter has to restore the comparer or a parsed config answers differently
        // from one built in code.
        var config = ConfigStore.Parse("guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n").Config;

        Assert.True(config.Guardrail.Connectors.TryGetValue("ClaudeCode", out var settings));
        Assert.Equal("observe", settings!.Mode);
    }

    [Fact]
    public void Null_list_items_are_dropped_and_real_ones_kept()
    {
        var config = ConfigStore.Parse("ai_discovery:\n  scan_roots:\n    -\n    - C:\\src\n    - ~\n").Config;

        Assert.Equal(new[] { "C:\\src" }, config.AiDiscovery.ScanRoots);
    }

    [Fact]
    public void Empty_scalar_values_fall_back_to_the_documented_defaults()
    {
        var gateway = ConfigStore.Parse("gateway:\n  api_port:\n  token_env:\n  token:\n").Config.Gateway;

        Assert.Equal(GatewaySection.DefaultApiPort, gateway.ApiPort);
        Assert.False(gateway.HasExplicitApiPort);
        Assert.Equal(GatewaySection.DefaultTokenEnv, gateway.TokenEnv);
        Assert.Null(gateway.Token);
    }

    [Fact]
    public void A_model_built_in_code_cannot_be_given_a_null_section()
    {
        // The guarantee lives in the setters, not only in Parse: nothing that assigns null
        // (the deserializer, a future editor, a test double) can leave a section null.
        var config = new DefenseClawConfig
        {
            Gateway = null!,
            Guardrail = null!,
            AiDiscovery = null!,
        };
        config.Guardrail.Connectors = null!;
        config.AiDiscovery.ScanRoots = null!;

        AssertNothingIsNull(config);
    }
}
