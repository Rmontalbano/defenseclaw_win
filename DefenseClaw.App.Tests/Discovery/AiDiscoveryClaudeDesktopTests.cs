using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// Claude Desktop and Cowork in AI Discovery (runtime 1.0.8): the <c>claude_desktop</c> block read from a state-file signal, the entries a
/// card shows, and the gate that keeps a runtime without the feature exactly as it was. Synthetic data only; the block's keys are the ones
/// the runtime writes (<c>internal/inventory/ai_discovery_claude_desktop.go</c>, tag 1.0.8).
/// </summary>
public sealed class AiDiscoveryClaudeDesktopTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    internal const string FirstPartyBlock =
        "{\"installed\":true,\"installed_source\":\"install_folder\",\"version\":\"4.4212.0\",\"cowork_enabled\":true," +
        "\"cowork_enabled_source\":\"cowork_sessions\",\"engine_version\":\"2.1.309\",\"vm_bundle_present\":true,\"vm_bundle_bytes\":1048576," +
        "\"cowork_plugins\":[\"acme-notes\",\"defenseclaw\"],\"deployment\":\"1p\"}";

    internal const string ThirdPartyBlock =
        "{\"installed\":true,\"installed_source\":\"data_folder\",\"cowork_enabled\":null,\"vm_bundle_present\":false," +
        "\"cowork_plugins\":[\"acme-notes\"],\"deployment\":\"3p\"}";

    internal static string SignalJson(string detector, string? folder, string? block, string state = "seen") =>
        "{\"signal_id\":\"sig-" + detector + folder + "\",\"signature_id\":\"claude-desktop\",\"name\":\"Claude Desktop\",\"vendor\":\"Anthropic\"," +
        "\"product\":\"Claude Desktop\",\"category\":\"desktop_app\",\"detector\":\"" + detector + "\",\"confidence\":0.9,\"state\":\"" + state + "\"," +
        "\"evidence\":[{\"type\":\"" + (folder is null ? "application" : "claude_desktop") + "\"" +
        (folder is null ? string.Empty : ",\"basename\":\"" + folder + "\"") + ",\"quality\":0.9}]" +
        (block is null ? string.Empty : ",\"claude_desktop\":" + block) + "}";

    internal static DiscoverySignalRecord Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DiscoverySignalParser.FromState(document.RootElement);
    }

    private static IReadOnlyList<DiscoverySignalRecord> ThreeSignals() =>
    [
        Parse(SignalJson("claude_desktop", "Claude-3p", ThirdPartyBlock)),
        Parse(SignalJson("application", null, null)),
        Parse(SignalJson("claude_desktop", "Claude", FirstPartyBlock)),
    ];

    [Fact]
    public void FromState_reads_the_block_of_a_claude_desktop_signal()
    {
        var signal = Parse(SignalJson("claude_desktop", "Claude-3p", ThirdPartyBlock));

        Assert.Equal("Anthropic", signal.Vendor);
        Assert.Equal("Claude Desktop", signal.Product);
        Assert.Equal("claude_desktop", signal.Detector);
        var evidence = Assert.Single(signal.Evidence);
        Assert.Equal("claude_desktop", evidence.Type);
        Assert.Equal("Claude-3p", evidence.Basename);
        var block = signal.ClaudeDesktop;
        Assert.NotNull(block);
        Assert.True(block.Installed);
        Assert.Equal("3p", block.Deployment);
        Assert.Null(block.CoworkEnabled);
        Assert.Equal(new[] { "acme-notes" }, block.CoworkPlugins);
    }

    [Fact]
    public void A_signal_without_the_block_has_none()
    {
        Assert.Null(Parse(SignalJson("application", null, null)).ClaudeDesktop);
    }

    [Fact]
    public void One_card_lists_the_three_signals_and_two_entries_with_1p_first()
    {
        var cards = AiDiscoveryPanelViewModel.BuildCards(ThreeSignals(), Now, claudeDesktop: true).ToList();

        var card = Assert.Single(cards);
        Assert.Equal(3, card.SignalLines.Count);
        Assert.True(card.HasClaudeDesktop);
        Assert.Equal(2, card.ClaudeDesktopEntries.Count);
        Assert.Equal("Claude Desktop · Claude account", card.ClaudeDesktopEntries[0].Title);
        Assert.Equal("Claude Desktop · third-party inference", card.ClaudeDesktopEntries[1].Title);
        Assert.NotEqual(card.ClaudeDesktopEntries[0].Title, card.ClaudeDesktopEntries[1].Title);
    }

    [Fact]
    public void An_entry_words_the_app_cowork_engine_and_data_folder()
    {
        var card = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(ThreeSignals(), Now, claudeDesktop: true));
        var first = card.ClaudeDesktopEntries[0];
        var third = card.ClaudeDesktopEntries[1];

        Assert.Equal("app 4.4212.0", first.AppText);
        Assert.Equal("Cowork on", first.CoworkText);
        Assert.Equal("engine 2.1.309", first.EngineText);
        Assert.Equal("data folder Claude", first.DataFolderText);
        Assert.Equal("app installed (version not reported)", third.AppText);
        Assert.Equal("Cowork unknown", third.CoworkText);
        Assert.Equal("no engine reported", third.EngineText);
        Assert.Equal("data folder Claude-3p", third.DataFolderText);
        Assert.DoesNotContain("protect", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(first.HasOpenCoworkSetup);
    }

    [Fact]
    public void The_defenseclaw_plugin_is_marked_only_when_it_is_listed()
    {
        var card = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(ThreeSignals(), Now, claudeDesktop: true));
        var first = card.ClaudeDesktopEntries[0];
        var third = card.ClaudeDesktopEntries[1];

        Assert.True(first.HasDefenseClawPlugin);
        Assert.Equal(new[] { "acme-notes", "defenseclaw" }, first.Plugins.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { false, true }, first.Plugins.Select(p => p.IsDefenseClaw).ToArray());
        Assert.Equal("defenseclaw (DefenseClaw plugin)", first.Plugins[1].Label);

        Assert.False(third.HasDefenseClawPlugin);
        Assert.DoesNotContain(third.Plugins, p => p.IsDefenseClaw);
    }

    [Fact]
    public void An_entry_with_no_plugins_says_none_were_reported_and_never_that_the_plugin_is_missing()
    {
        var block = "{\"installed\":true,\"cowork_enabled\":false,\"vm_bundle_present\":false,\"deployment\":\"1p\"}";
        var entry = Assert.Single(ClaudeDesktopEntry.For([Parse(SignalJson("claude_desktop", "Claude", block))]));

        Assert.Empty(entry.Plugins);
        Assert.True(entry.HasNoPlugins);
        Assert.Equal("no Cowork plugins reported", entry.PluginsText);
        Assert.Equal("Cowork off", entry.CoworkText);
        Assert.DoesNotContain("missing", entry.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_block_with_no_deployment_is_titled_as_not_reported()
    {
        var entry = Assert.Single(ClaudeDesktopEntry.For([Parse(SignalJson("claude_desktop", "claude_abc", "{\"installed\":false,\"vm_bundle_present\":false}"))]));

        Assert.Equal("Claude Desktop · deployment not reported", entry.Title);
        Assert.Equal("app not found", entry.AppText);
    }

    [Fact]
    public void With_claude_desktop_off_the_card_has_no_entries_and_is_the_one_it_was()
    {
        var signals = ThreeSignals();

        var on = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(signals, Now, claudeDesktop: true));
        var off = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(signals, Now, claudeDesktop: false));
        var oldCall = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(signals, Now));

        Assert.Empty(off.ClaudeDesktopEntries);
        Assert.False(off.HasClaudeDesktop);
        Assert.Empty(oldCall.ClaudeDesktopEntries);
        Assert.Equal(on.HeaderDisplay, off.HeaderDisplay);
        Assert.Equal(on.SignalLines, off.SignalLines);
        Assert.Equal(3, off.SignalLines.Count);
    }

    [Fact]
    public void The_search_finds_a_card_by_an_entry_title_or_a_plugin_name_only_when_entries_are_on()
    {
        var on = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(ThreeSignals(), Now, claudeDesktop: true));
        var off = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(ThreeSignals(), Now, claudeDesktop: false));

        Assert.True(on.Matches("third-party inference"));
        Assert.True(on.Matches("acme-notes"));
        Assert.False(off.Matches("third-party inference"));
        Assert.False(off.Matches("acme-notes"));
    }

    [Fact]
    public void An_agent_row_shows_its_version_unless_a_display_text_is_set()
    {
        var plain = new AgentDiscoveryRow("claudecode", true, true, true, "2.1.0", null, null, null);

        Assert.Equal("2.1.0", plain.VersionDisplay);
        Assert.Contains("version 2.1.0", plain.ToString(), StringComparison.Ordinal);
        Assert.Null(new AgentDiscoveryRow("codex", false, false, false, null, null, null, null).VersionDisplay);
        Assert.Equal("2.1.0 (Claude Desktop engine)", (plain with { VersionDisplay = "2.1.0 (Claude Desktop engine)" }).VersionDisplay);
    }
}
