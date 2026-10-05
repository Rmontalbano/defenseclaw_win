using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>The config.yaml settings the readiness checklist reads, at the paths 0.8.10's <c>build_readiness_checks</c> asks for.</summary>
public sealed class ReadinessConfigTests
{
    private static ReadinessConfig Read(string yaml) => ReadinessConfig.From(ConfigStore.Parse(yaml));

    [Fact]
    public void A_complete_llm_scanner_and_policy_config_reads_through()
    {
        var config = Read("""
            llm:
              provider: anthropic
              model: example-model
            scanners:
              skill_scanner:
                binary: skill-scanner
            asset_policy:
              enabled: true
              skill:
                registry_required: true
                registry: [one]
            """);

        Assert.Equal("anthropic", config.LlmProvider);
        Assert.Equal("example-model", config.LlmModel);
        Assert.True(config.ScannerConfigured);
        Assert.True(config.AssetPolicyEnabled);
        Assert.False(config.RegistryRequiredButEmpty);
    }

    [Fact]
    public void An_empty_config_reads_as_nothing_configured()
    {
        var config = Read(string.Empty);

        Assert.Equal(ReadinessConfig.Empty, config);
    }

    [Theory]
    [InlineData("bedrock", "bedrock", "eu-west-1")]
    [InlineData("vertex_ai", "vertex", "europe-west4")] // the provider id is vertex_ai, the block is llm.vertex
    [InlineData("azure", "azure", "westeurope")]
    public void A_regional_provider_reads_its_own_block_and_falls_back_to_llm_region(string provider, string block, string region)
    {
        Assert.Equal(region, Read($"llm:\n  provider: {provider}\n  {block}:\n    region: {region}\n").LlmRegion);
        Assert.Equal("us-east-1", Read($"llm:\n  provider: {provider}\n  region: us-east-1\n").LlmRegion);
        Assert.Equal(string.Empty, Read($"llm:\n  provider: {provider}\n").LlmRegion);
    }

    [Fact]
    public void An_azure_endpoint_and_an_instance_overlay_are_read()
    {
        var config = Read("llm:\n  provider: azure\n  instance_name: shared\n  azure:\n    endpoint: https://example.invalid/\n");

        Assert.Equal("https://example.invalid/", config.AzureEndpoint);
        Assert.Equal("shared", config.LlmInstanceName);
    }

    [Theory]
    [InlineData("scanners:\n  mcp_scanner:\n    binary: mcp-scanner\n", true)]
    [InlineData("scanners:\n  codeguard: /opt/codeguard\n", true)]
    [InlineData("scanners:\n  skill_scanner:\n    binary: ''\n", false)]
    [InlineData("scanners: {}\n", false)]
    public void Any_one_scanner_setting_counts_as_configured(string yaml, bool expected) =>
        Assert.Equal(expected, Read(yaml).ScannerConfigured);

    [Theory]
    [InlineData("asset_policy:\n  enabled: true\n  mcp:\n    registry_required: true\n", true)]
    [InlineData("asset_policy:\n  enabled: true\n  plugin:\n    registry_required: true\n    registry: []\n", true)]
    [InlineData("asset_policy:\n  enabled: true\n  skill:\n    registry_required: false\n", false)]
    [InlineData("asset_policy:\n  enabled: true\n  skill:\n    registry_required: true\n    registry: [a]\n", false)]
    public void Registry_required_but_empty_follows_the_tui(string yaml, bool expected) =>
        Assert.Equal(expected, Read(yaml).RegistryRequiredButEmpty);

    [Fact]
    public void A_section_that_is_a_scalar_reads_as_not_configured()
    {
        Assert.Equal(string.Empty, Read("scanners: none\nasset_policy: off\n").LlmProvider);
        Assert.False(Read("scanners: none\nasset_policy: off\n").ScannerConfigured);
    }
}
