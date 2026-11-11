using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.Tests;

/// <summary>
/// The <c>claude_desktop</c> block of an AI Discovery signal (runtime 1.0.8). Fixtures are synthetic, written from the key list in
/// <c>ai_discovery_claude_desktop.go@1.0.8:98-123</c>.
/// </summary>
public class ClaudeDesktopDiscoveryTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private const string Full1p = """
        {"installed":true,"installed_source":"app_bundle","version":"4.4212.0","cowork_enabled":true,
         "cowork_enabled_source":"cowork_sessions","engine_version":"2.1.309","vm_bundle_present":true,"vm_bundle_bytes":123456789,
         "cowork_plugins":["finance","sales-tools"],"deployment":"1p"}
        """;

    [Fact]
    public void Full_1p_block_reads_every_key()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json(Full1p));

        Assert.NotNull(info);
        Assert.True(info.Installed);
        Assert.Equal("app_bundle", info.InstalledSource);
        Assert.Equal("4.4212.0", info.Version);
        Assert.True(info.CoworkEnabled);
        Assert.Equal("cowork_sessions", info.CoworkEnabledSource);
        Assert.Equal("2.1.309", info.EngineVersion);
        Assert.True(info.VmBundlePresent);
        Assert.Equal(123456789L, info.VmBundleBytes);
        Assert.Equal(new[] { "finance", "sales-tools" }, info.CoworkPlugins);
        Assert.Equal("1p", info.Deployment);
        Assert.False(info.IsThirdParty);
        Assert.False(info.HasDefenseClawPlugin);
    }

    [Fact]
    public void Third_party_block_is_marked_and_finds_the_defenseclaw_plugin()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json(
            """{"installed":true,"installed_source":"msix_package","cowork_enabled":true,"engine_version":"2.1.317","vm_bundle_present":false,"cowork_plugins":["defenseclaw","finance"],"deployment":"3p"}"""));

        Assert.NotNull(info);
        Assert.True(info.IsThirdParty);
        Assert.Equal("", info.Version);
        Assert.Equal("2.1.317", info.EngineVersion);
        Assert.True(info.HasDefenseClawPlugin);
    }

    [Fact]
    public void Minimal_block_leaves_the_rest_empty()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json("""{"installed":false,"cowork_enabled":null,"vm_bundle_present":false}"""));

        Assert.NotNull(info);
        Assert.False(info.Installed);
        Assert.Null(info.CoworkEnabled);
        Assert.False(info.VmBundlePresent);
        Assert.Equal("", info.InstalledSource);
        Assert.Equal("", info.Version);
        Assert.Equal("", info.EngineVersion);
        Assert.Equal("", info.Deployment);
        Assert.Equal(0L, info.VmBundleBytes);
        Assert.Empty(info.CoworkPlugins);
    }

    [Fact]
    public void Cowork_enabled_false_is_off_not_unknown()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json("""{"installed":true,"cowork_enabled":false}"""));

        Assert.NotNull(info);
        Assert.False(info.CoworkEnabled);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"installed\":true}]")]
    [InlineData("\"claude\"")]
    [InlineData("5")]
    [InlineData("true")]
    public void A_block_that_is_not_an_object_is_null(string text)
    {
        Assert.Null(ClaudeDesktopDiscovery.Parse(Json(text)));
    }

    [Fact]
    public void An_undefined_element_is_null()
    {
        Assert.Null(ClaudeDesktopDiscovery.Parse(default));
    }

    [Fact]
    public void Values_of_the_wrong_type_count_as_missing()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json(
            """{"installed":"yes","installed_source":3,"version":4,"cowork_enabled":"true","cowork_enabled_source":[],"engine_version":{},"vm_bundle_present":1,"vm_bundle_bytes":"big","cowork_plugins":"finance","deployment":false}"""));

        Assert.NotNull(info);
        Assert.False(info.Installed);
        Assert.Equal("", info.InstalledSource);
        Assert.Equal("", info.Version);
        Assert.Null(info.CoworkEnabled);
        Assert.Equal("", info.CoworkEnabledSource);
        Assert.Equal("", info.EngineVersion);
        Assert.False(info.VmBundlePresent);
        Assert.Equal(0L, info.VmBundleBytes);
        Assert.Empty(info.CoworkPlugins);
        Assert.Equal("", info.Deployment);
    }

    [Fact]
    public void A_fractional_byte_count_counts_as_missing()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json("""{"installed":true,"vm_bundle_bytes":1.5}"""));

        Assert.NotNull(info);
        Assert.Equal(0L, info.VmBundleBytes);
    }

    [Theory]
    [InlineData("2p")]
    [InlineData("1P")]
    [InlineData("")]
    [InlineData("bedrock")]
    public void An_unknown_deployment_is_empty(string value)
    {
        var info = ClaudeDesktopDiscovery.Parse(Json("{\"installed\":true,\"deployment\":\"" + value + "\"}"));

        Assert.NotNull(info);
        Assert.Equal("", info.Deployment);
        Assert.False(info.IsThirdParty);
    }

    [Fact]
    public void Plugins_drop_non_strings_trim_and_dedupe_keeping_order()
    {
        var info = ClaudeDesktopDiscovery.Parse(Json(
            """{"installed":true,"cowork_plugins":["  finance ", 7, null, "", "   ", "sales-tools", "finance", {"a":1}, ["x"], true, "legal"]}"""));

        Assert.NotNull(info);
        Assert.Equal(new[] { "finance", "sales-tools", "legal" }, info.CoworkPlugins);
    }

    [Fact]
    public void Plugins_stop_at_the_cap()
    {
        var names = string.Join(",", Enumerable.Range(0, 100).Select(i => "\"plugin-" + i.ToString("D3", CultureInfo.InvariantCulture) + "\""));
        var info = ClaudeDesktopDiscovery.Parse(Json("{\"installed\":true,\"cowork_plugins\":[" + names + "]}"));

        Assert.NotNull(info);
        Assert.Equal(ClaudeDesktopDiscovery.MaxPlugins, info.CoworkPlugins.Count);
        Assert.Equal("plugin-000", info.CoworkPlugins[0]);
        Assert.Equal("plugin-063", info.CoworkPlugins[^1]);
    }

    [Theory]
    [InlineData("defenseclaw", true)]
    [InlineData("DefenseClaw", true)]
    [InlineData("  DEFENSECLAW ", true)]
    [InlineData("defenseclaw-extra", false)]
    [InlineData("finance", false)]
    public void Defenseclaw_plugin_is_found_without_regard_to_case(string plugin, bool expected)
    {
        var info = new ClaudeDesktopInfo { CoworkPlugins = [plugin] };

        Assert.Equal(expected, info.HasDefenseClawPlugin);
    }

    [Fact]
    public void No_plugins_means_no_defenseclaw_plugin()
    {
        Assert.False(new ClaudeDesktopInfo().HasDefenseClawPlugin);
    }

    [Theory]
    [InlineData("1p", "Claude account")]
    [InlineData("3p", "third-party inference")]
    [InlineData("", "deployment not reported")]
    [InlineData(null, "deployment not reported")]
    [InlineData("bedrock", "deployment not reported")]
    public void Deployment_text_uses_the_cli_words(string? deployment, string expected)
    {
        Assert.Equal(expected, ClaudeDesktopDiscovery.DeploymentText(deployment));
    }

    [Fact]
    public void Cowork_text_has_three_states()
    {
        Assert.Equal("on", ClaudeDesktopDiscovery.CoworkText(true));
        Assert.Equal("off", ClaudeDesktopDiscovery.CoworkText(false));
        Assert.Equal("unknown", ClaudeDesktopDiscovery.CoworkText(null));
    }

    [Theory]
    [InlineData("2.1.309", "claude-desktop", "2.1.309 (Claude Desktop engine)")]
    [InlineData("", "claude-desktop", "Claude Desktop engine")]
    [InlineData(null, "claude-desktop", "Claude Desktop engine")]
    [InlineData("2.1.309", "cli", "2.1.309")]
    [InlineData("2.1.309", "", "2.1.309")]
    [InlineData("2.1.309", null, "2.1.309")]
    [InlineData("2.1.309", "Claude-Desktop", "2.1.309")]
    [InlineData(null, "cli", null)]
    [InlineData(null, null, null)]
    public void Engine_version_text_follows_the_source(string? version, string? source, string? expected)
    {
        Assert.Equal(expected, ClaudeDesktopDiscovery.EngineVersionText(version, source));
    }

    [Fact]
    public void Records_with_equal_plugin_lists_are_equal()
    {
        var a = ClaudeDesktopDiscovery.Parse(Json(Full1p));
        var b = ClaudeDesktopDiscovery.Parse(Json(Full1p));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a.CoworkPlugins, b.CoworkPlugins);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Records_differ_when_a_plugin_or_a_field_differs()
    {
        var a = ClaudeDesktopDiscovery.Parse(Json(Full1p))!;
        var otherPlugins = a with { CoworkPlugins = ["finance"] };
        var otherVersion = a with { Version = "4.4213.0" };

        Assert.NotEqual(a, otherPlugins);
        Assert.NotEqual(a, otherVersion);
        Assert.False(a.Equals(null));
    }
}
