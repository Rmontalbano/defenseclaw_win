using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The config keys 1.0.4 added or changed (CUST-377), verified against upstream schemas/config/v8 and config.py at 1.0.4: in the catalogue for a runtime that has
/// them, absent (exactly as before) for one that does not. And the restart queue's prediction for a gateway that reloads config.yaml itself.
/// </summary>
public sealed class Config104KeysTests
{
    private static readonly ConfigChoiceProfile Older = ConfigChoiceProfile.ForHost();
    private static readonly ConfigChoiceProfile Current = ConfigChoiceProfile.ForHost(keys104: true);

    [Theory]
    [InlineData("scanners.mcp_scanner.unverified_server")]
    [InlineData("openshell.trusted_ca_bundle")]
    [InlineData("guardrail.block_at")]
    [InlineData("guardrail.alert_at")]
    [InlineData("guardrail.connectors.claudecode.block_at")]
    [InlineData("guardrail.connectors.codex.alert_at")]
    public void The_new_keys_are_known_only_to_a_runtime_that_has_them(string path)
    {
        Assert.Null(ConfigFieldCatalog.Resolve(path, Older));
        Assert.Null(ConfigFieldCatalog.Resolve(path));
        Assert.NotNull(ConfigFieldCatalog.Resolve(path, Current));
    }

    [Fact]
    public void Unverified_server_is_a_warn_or_block_choice_and_says_what_each_does()
    {
        var info = ConfigFieldCatalog.Resolve("scanners.mcp_scanner.unverified_server", Current)!;

        Assert.True(info.IsChoice);
        Assert.Equal(["warn", "block"], info.Options);
        Assert.Contains("warn", info.Hint, StringComparison.Ordinal);
        Assert.Contains("block", info.Hint, StringComparison.Ordinal);
        Assert.Null(info.ReadOnlyReason);
    }

    [Fact]
    public void The_trusted_ca_bundle_is_a_plain_path_field_with_the_schemas_meaning()
    {
        var info = ConfigFieldCatalog.Resolve("openshell.trusted_ca_bundle", Current)!;

        Assert.False(info.IsChoice);
        Assert.Contains("PEM", info.Hint, StringComparison.Ordinal);
        Assert.Contains("~/", info.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Severities_are_read_in_any_case_and_the_levels_are_the_runtimes_four_with_a_blank_to_inherit()
    {
        var blockAt = ConfigFieldCatalog.Resolve("guardrail.block_at", Current)!;
        var hilt = ConfigFieldCatalog.Resolve("guardrail.hilt.min_severity", Current)!;
        var connectorHilt = ConfigFieldCatalog.Resolve("guardrail.connectors.codex.hilt.min_severity", Current)!;

        Assert.Equal(["", "CRITICAL", "HIGH", "MEDIUM", "LOW"], blockAt.Options);
        Assert.Contains("any case", blockAt.Hint, StringComparison.Ordinal);
        Assert.Contains("any case", hilt.Hint, StringComparison.Ordinal);
        Assert.Contains("any case", connectorHilt.Hint, StringComparison.Ordinal);

        // A value already in the file in another case stays selectable as written.
        Assert.Contains(ConfigFieldCatalog.ChoiceItems(blockAt.Options!, "high"), item => item.Value == "high");
    }

    [Fact]
    public void A_runtime_without_the_keys_keeps_the_hilt_hint_exactly_as_it_was()
    {
        var older = ConfigFieldCatalog.Resolve("guardrail.hilt.min_severity", Older)!;

        Assert.Equal("Minimum severity for approval prompts.", older.Hint);
        Assert.Equal(["HIGH", "MEDIUM", "LOW", "CRITICAL"], older.Options);
    }

    [Theory]
    [InlineData("scanners.mcp_scanner.unverified_server")]
    [InlineData("openshell.trusted_ca_bundle")]
    [InlineData("guardrail.block_at")]
    public void The_exact_keys_do_not_appear_in_the_catalogue_listing_twice(string path)
    {
        Assert.Single(ConfigFieldCatalog.ExactPaths, p => p == path);
    }

    // ------------------------------------------------------------------ the restart prediction

    private const string Before = "gateway:\n  api_port: 18970\nguardrail:\n  mode: observe\n  port: 4000\nllm:\n  model: a\n";

    [Fact]
    public void Without_hot_reload_every_changed_value_queues_a_restart_as_before()
    {
        var reason = RestartQueueRules.ConfigSaveReason(Before, Before.Replace("model: a", "model: b", StringComparison.Ordinal));

        Assert.Equal("config.yaml saved in the config editor (llm)", reason);
    }

    [Fact]
    public void With_hot_reload_a_change_to_hot_keys_queues_nothing()
    {
        var after = Before.Replace("model: a", "model: b", StringComparison.Ordinal).Replace("mode: observe", "mode: action", StringComparison.Ordinal);

        Assert.Null(RestartQueueRules.ConfigSaveReason(Before, after, gatewayHotReloads: true));
    }

    [Fact]
    public void With_hot_reload_only_the_sections_with_a_restart_key_are_named()
    {
        var after = Before
            .Replace("model: a", "model: b", StringComparison.Ordinal)
            .Replace("mode: observe", "mode: action", StringComparison.Ordinal)
            .Replace("port: 4000", "port: 4001", StringComparison.Ordinal)
            .Replace("api_port: 18970", "api_port: 18971", StringComparison.Ordinal);

        var reason = RestartQueueRules.ConfigSaveReason(Before, after, gatewayHotReloads: true);

        Assert.Equal("config.yaml saved in the config editor (gateway, guardrail)", reason);
        Assert.DoesNotContain("llm", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void With_hot_reload_a_comment_or_layout_change_still_queues_nothing()
    {
        Assert.Null(RestartQueueRules.ConfigSaveReason(Before, "# note\n" + Before, gatewayHotReloads: true));
    }

    [Fact]
    public void With_hot_reload_text_that_cannot_be_compared_keeps_the_old_answer()
    {
        Assert.Equal(
            "config.yaml saved in the config editor",
            RestartQueueRules.ConfigSaveReason("a: 1\n", "a: [unclosed\n", gatewayHotReloads: true));
    }

    [Fact]
    public void A_restore_names_itself_the_same_way_under_hot_reload()
    {
        Assert.Equal(
            "config.yaml restored from a backup in the config editor (guardrail)",
            RestartQueueRules.ConfigSaveReason(Before, Before.Replace("port: 4000", "port: 4001", StringComparison.Ordinal), RestartQueueRules.ConfigRestoredText, gatewayHotReloads: true));
    }
}
