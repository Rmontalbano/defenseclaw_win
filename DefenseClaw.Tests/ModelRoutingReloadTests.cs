using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// What config.yaml keys of model routing and tokenomics restart the gateway: 1.0.9 and older restart for all of <c>routing</c>; 1.0.10 lists nine routing keys
/// (config_writer.RESTART_KEYS, configwrite.restartKeys) and applies the rest, tokenomics and pricing in place.
/// </summary>
public sealed class ModelRoutingReloadTests
{
    private static readonly string[] NineKeys =
    [
        "routing.port", "routing.version", "routing.remote", "routing.algorithm", "routing.enabled",
        "routing.classifier", "routing.models", "routing.signals", "routing.decisions",
    ];

    private static string[] Segments(string path) => path.Split('.');

    [Fact]
    public void The_nine_routing_keys_are_the_ones_the_runtime_lists()
    {
        Assert.Equal(NineKeys, ConfigReloadRules.RoutingRestartKeys);
    }

    [Theory]
    [InlineData("routing.port")]
    [InlineData("routing.version")]
    [InlineData("routing.remote")]
    [InlineData("routing.algorithm")]
    [InlineData("routing.enabled")]
    [InlineData("routing.classifier")]
    [InlineData("routing.models")]
    [InlineData("routing.models.0.alias")]
    [InlineData("routing.signals")]
    [InlineData("routing.decisions")]
    public void Each_of_the_nine_routing_keys_restarts_on_both_releases(string path)
    {
        Assert.True(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: false));
        Assert.True(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: true));
    }

    [Theory]
    [InlineData("routing.default_alias")]
    [InlineData("routing.min_confidence")]
    [InlineData("routing.sensitive.check")]
    [InlineData("routing.admin.rules")]
    [InlineData("routing.route_only.connectors")]
    public void Other_routing_keys_restart_on_1_0_9_and_are_hot_on_1_0_10(string path)
    {
        Assert.True(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: false));
        Assert.False(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: true));
    }

    [Theory]
    [InlineData("observability.tokenomics.enabled")]
    [InlineData("observability.tokenomics.local_store.enabled")]
    [InlineData("observability.pricing.version")]
    public void Tokenomics_and_pricing_are_hot_on_both(string path)
    {
        Assert.False(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: false));
        Assert.False(ConfigReloadRules.NeedsRestart(Segments(path), routingHotReload: true));
    }

    [Theory]
    [InlineData("routing.default_alias", true, false)]
    [InlineData("routing.enabled", true, true)]
    [InlineData("guardrail.mode", false, false)]
    [InlineData("guardrail.port", true, true)]
    [InlineData("data_dir", true, true)]
    [InlineData("unknown_section.key", true, true)]
    public void The_overloads_without_the_flag_mean_1_0_9_and_older(string path, bool restart109, bool restart1010)
    {
        var segments = Segments(path);
        Assert.Equal(restart109, ConfigReloadRules.NeedsRestart(segments));
        Assert.Equal(restart109, ConfigReloadRules.NeedsRestart(segments, routingHotReload: false));
        Assert.Equal(restart1010, ConfigReloadRules.NeedsRestart(segments, routingHotReload: true));
    }

    [Fact]
    public void Classify_without_the_flag_equals_today_and_with_it_applies_a_default_alias_change_in_place()
    {
        const string before = "routing:\n  default_alias: fast\n";
        const string after = "routing:\n  default_alias: smart\n";

        var old = ConfigReloadRules.Classify(before, after);
        Assert.False(old.HotOnly);
        Assert.Equal(["routing.default_alias"], old.RestartPaths);
        Assert.Equal(old.RestartPaths, ConfigReloadRules.Classify(before, after, routingHotReload: false).RestartPaths);

        var current = ConfigReloadRules.Classify(before, after, routingHotReload: true);
        Assert.True(current.HotOnly);
        Assert.Empty(current.RestartPaths);
    }

    [Fact]
    public void Classify_keeps_a_restart_when_enabled_changes_with_the_flag()
    {
        const string before = "routing:\n  enabled: false\n  default_alias: fast\n";
        const string after = "routing:\n  enabled: true\n  default_alias: smart\n";

        var verdict = ConfigReloadRules.Classify(before, after, routingHotReload: true);
        Assert.False(verdict.HotOnly);
        Assert.Equal(["routing.enabled"], verdict.RestartPaths);
    }
}
