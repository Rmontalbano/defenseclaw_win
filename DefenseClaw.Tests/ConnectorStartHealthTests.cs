using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The connectors the gateway did not start, read from <c>/health</c> <c>guardrail.details</c> (CUST-448; the keys are health.go and agent_not_installed.go
/// at 1.0.7, the CLI's words are cmd_status.py). Synthetic fixtures; nothing here asks the runtime's version.
/// </summary>
public sealed class ConnectorStartHealthTests
{
    private const string NotInstalled =
        "the agent is not installed for this account (no codex executable found); reinstall it and run: defenseclaw-gateway restart, or remove the connector: defenseclaw setup remove codex";

    private static ServiceState Block(string detailsJson) =>
        JsonSerializer.Deserialize<ServiceState>("{\"state\":\"running\",\"details\":" + detailsJson + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void An_agent_that_is_not_installed_is_told_by_the_start_of_its_reason()
    {
        var details = "{\"connectors_not_started\":[\"codex\"],\"connectors_refused_by_agent_settings\":{\"codex\":\"" + NotInstalled + "\"}," +
                      "\"connectors_not_started_reasons\":{\"codex\":\"" + NotInstalled + "\"}}";

        var skip = Assert.Single(ConnectorStartHealth.From(Block(details)));

        Assert.Equal(new ConnectorSkip("codex", ConnectorSkipKind.AgentNotInstalled, NotInstalled), skip);
    }

    [Fact]
    public void A_reason_in_the_not_started_map_alone_is_enough_for_the_not_installed_kind()
    {
        var details = "{\"connectors_not_started\":[\"codex\"],\"connectors_not_started_reasons\":{\"codex\":\"" + NotInstalled + "\"}}";

        Assert.Equal(ConnectorSkipKind.AgentNotInstalled, Assert.Single(ConnectorStartHealth.From(Block(details))).Kind);
    }

    [Fact]
    public void A_refusal_by_the_agents_own_settings_keeps_the_gateways_sentence()
    {
        var details = "{\"connectors_not_started\":[\"claudecode\"],\"connectors_refused_by_agent_settings\":{\"claudecode\":\"Claude Code settings.json sets disableAllHooks=true, so it runs none of its hooks\"}}";

        var skip = Assert.Single(ConnectorStartHealth.From(Block(details)));

        Assert.Equal("claudecode", skip.Name);
        Assert.Equal(ConnectorSkipKind.RefusedByAgentSettings, skip.Kind);
        Assert.Contains("disableAllHooks=true", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_name_the_list_does_not_have_is_still_a_skip()
    {
        var skip = Assert.Single(ConnectorStartHealth.From(Block("{\"connectors_refused_by_agent_settings\":{\"codex\":\"hooks are off\"}}")));

        Assert.Equal(ConnectorSkipKind.RefusedByAgentSettings, skip.Kind);
    }

    [Fact]
    public void A_start_failure_uses_its_reason_or_the_clis_own_text_when_there_is_none()
    {
        var details = "{\"connectors_not_started\":[\"hermes\",\"devin\"],\"connectors_not_started_reasons\":{\"devin\":\"setup exploded [truncated; the gateway log has the rest]\"}}";

        var skips = ConnectorStartHealth.From(Block(details));

        Assert.Collection(
            skips,
            devin =>
            {
                Assert.Equal(("devin", ConnectorSkipKind.SetupFailed), (devin.Name, devin.Kind));
                Assert.Equal("setup exploded [truncated; the gateway log has the rest]", devin.Reason);
            },
            hermes =>
            {
                Assert.Equal(("hermes", ConnectorSkipKind.SetupFailed), (hermes.Name, hermes.Kind));
                Assert.Equal("setup failed when the gateway started, so it is not enforced; see gateway.log, then run: defenseclaw setup hermes", hermes.Reason);
            });
    }

    [Fact]
    public void The_result_is_sorted_by_name_with_each_name_once()
    {
        var skips = ConnectorStartHealth.From(Block("{\"connectors_not_started\":[\"zeta\",\"Alpha\",\"zeta\"],\"connectors_not_started_reasons\":{\"alpha\":\"x\"}}"));

        Assert.Equal(new[] { "alpha", "zeta" }, skips.Select(static s => s.Name));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"has space\"")]
    [InlineData("\"../escape\"")]
    [InlineData("\"-leading\"")]
    [InlineData("\"semi;colon\"")]
    [InlineData("\"codex\\u202e\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{}")]
    public void A_name_that_is_not_a_connector_name_is_dropped(string name)
    {
        Assert.Empty(ConnectorStartHealth.From(Block("{\"connectors_not_started\":[" + name + "]}")));
    }

    [Fact]
    public void A_name_longer_than_64_characters_is_dropped()
    {
        var name = new string('a', 65);

        Assert.Empty(ConnectorStartHealth.From(Block("{\"connectors_not_started\":[\"" + name + "\"]}")));
        Assert.Single(ConnectorStartHealth.From(Block("{\"connectors_not_started\":[\"" + new string('a', 64) + "\"]}")));
    }

    [Fact]
    public void A_reason_is_one_visible_line_cut_to_1024_characters()
    {
        var filler = new string('x', 3000);
        var details = "{\"connectors_not_started\":[\"codex\"],\"connectors_not_started_reasons\":{\"codex\":\"line one\\nline two\\u202eevil " + filler + "\"}}";

        var reason = Assert.Single(ConnectorStartHealth.From(Block(details))).Reason;

        Assert.Equal(ConnectorStartHealth.MaxReasonLength, reason.Length);
        Assert.StartsWith("line one\\nline two\\u202Eevil", reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', reason);
        Assert.DoesNotContain('‮', reason);
    }

    [Fact]
    public void A_reason_of_the_wrong_type_does_not_stop_the_skip()
    {
        var skip = Assert.Single(ConnectorStartHealth.From(Block("{\"connectors_not_started\":[\"codex\"],\"connectors_not_started_reasons\":{\"codex\":12}}")));

        Assert.Equal(ConnectorSkipKind.SetupFailed, skip.Kind);
        Assert.Contains("defenseclaw setup codex", skip.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"policy_mode\":\"observe\"}")]
    [InlineData("{\"connectors_not_started\":[]}")]
    [InlineData("{\"connectors_not_started\":\"codex\"}")]
    [InlineData("{\"connectors_not_started\":{\"codex\":1},\"connectors_refused_by_agent_settings\":[\"codex\"],\"connectors_not_started_reasons\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Missing_or_malformed_details_are_empty(string details) =>
        Assert.Empty(ConnectorStartHealth.From(Block(details)));

    [Fact]
    public void No_block_or_no_details_is_empty()
    {
        Assert.Empty(ConnectorStartHealth.From(null));
        Assert.Empty(ConnectorStartHealth.From(new ServiceState { State = "running" }));
    }
}
