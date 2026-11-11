using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's Discovered AI agents card with Claude Desktop (runtime 1.0.8): the 1p and 3p signals share one row whose vendor names the
/// deployments, and the Claude Code row says it runs on Claude Desktop's engine. Without the options the card is as it was.
/// </summary>
public sealed class OverviewClaudeDesktopTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string Signal(string folder, string deployment) =>
        "{\"name\":\"Claude Desktop\",\"vendor\":\"Anthropic\",\"product\":\"Claude Desktop\",\"category\":\"desktop_app\",\"detector\":\"claude_desktop\"," +
        "\"confidence\":0.9,\"state\":\"seen\",\"evidence\":[{\"type\":\"claude_desktop\",\"basename\":\"" + folder + "\"}]," +
        "\"claude_desktop\":{\"installed\":true,\"vm_bundle_present\":false,\"deployment\":\"" + deployment + "\"}}";

    private const string ClaudeCodeSignal =
        "{\"name\":\"Claude Code\",\"vendor\":\"Anthropic\",\"category\":\"ai_cli\",\"supported_connector\":\"claudecode\",\"confidence\":0.95,\"state\":\"seen\"}";

    private static string State(params string[] signals) => "{\"signals\":[" + string.Join(",", signals) + "]}";

    [Fact]
    public void The_1p_and_3p_signals_make_one_row_and_the_vendor_names_both_deployments()
    {
        var json = State(Signal("Claude-3p", "3p"), Signal("Claude", "1p"));

        var agents = OverviewAgentReader.Parse(json, Now, new OverviewAgentOptions(true, false));

        var row = Assert.Single(agents.Rows);
        Assert.Equal("Claude Desktop", row.Name);
        Assert.Equal("Anthropic · Claude account, third-party inference", row.Vendor);
    }

    [Fact]
    public void One_deployment_is_named_alone()
    {
        var third = OverviewAgentReader.Parse(State(Signal("Claude-3p", "3p")), Now, new OverviewAgentOptions(true, false));
        var first = OverviewAgentReader.Parse(State(Signal("Claude", "1p")), Now, new OverviewAgentOptions(true, false));

        Assert.Equal("Anthropic · third-party inference", Assert.Single(third.Rows).Vendor);
        Assert.Equal("Anthropic · Claude account", Assert.Single(first.Rows).Vendor);
    }

    [Fact]
    public void Without_options_or_with_them_off_the_row_is_the_one_it_was()
    {
        var json = State(Signal("Claude-3p", "3p"), Signal("Claude", "1p"));

        var plain = OverviewAgentReader.Parse(json, Now);
        var off = OverviewAgentReader.Parse(json, Now, new OverviewAgentOptions(false, false));
        var nul = OverviewAgentReader.Parse(json, Now, null);

        Assert.Equal("Anthropic", Assert.Single(plain.Rows).Vendor);
        Assert.Equal(plain.Rows, off.Rows);
        Assert.Equal(plain.Rows, nul.Rows);
        Assert.Equal(plain.Active, off.Active);
    }

    [Fact]
    public void A_claude_desktop_signal_without_a_block_gets_no_suffix()
    {
        var json = State("{\"name\":\"Claude Desktop\",\"vendor\":\"Anthropic\",\"product\":\"Claude Desktop\",\"detector\":\"application\",\"confidence\":0.8,\"state\":\"seen\"}");

        var row = Assert.Single(OverviewAgentReader.Parse(json, Now, new OverviewAgentOptions(true, false)).Rows);

        Assert.Equal("Anthropic", row.Vendor);
    }

    [Fact]
    public void The_claude_code_row_says_it_runs_on_the_desktop_engine_only_when_asked()
    {
        var json = State(ClaudeCodeSignal);

        var on = Assert.Single(OverviewAgentReader.Parse(json, Now, new OverviewAgentOptions(true, true)).Rows);
        var off = Assert.Single(OverviewAgentReader.Parse(json, Now, new OverviewAgentOptions(true, false)).Rows);

        Assert.Equal("Anthropic (claudecode) · Claude Desktop engine", on.Vendor);
        Assert.Equal("Anthropic (claudecode)", off.Vendor);
    }

    [Theory]
    [InlineData("{\"agents\":{\"claudecode\":{\"installed\":true,\"source\":\"claude-desktop\"}}}", true)]
    [InlineData("{\"agents\":{\"claudecode\":{\"installed\":false,\"source\":\"claude-desktop\"}}}", false)]
    [InlineData("{\"agents\":{\"claudecode\":{\"installed\":true,\"source\":\"cli\"}}}", false)]
    [InlineData("{\"agents\":{\"claudecode\":{\"installed\":true}}}", false)]
    [InlineData("{\"agents\":{\"codex\":{\"installed\":true,\"source\":\"claude-desktop\"}}}", false)]
    [InlineData("{\"agents\":{\"claudecode\":{\"installed\":\"true\",\"source\":\"claude-desktop\"}}}", false)]
    [InlineData("{\"agents\":[]}", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void ClaudeCodeOnDesktopEngine_is_true_only_for_an_installed_claude_desktop_source_and_never_throws(string json, bool expected)
    {
        Assert.Equal(expected, OverviewAgentReader.ClaudeCodeOnDesktopEngine(json));
    }
}
