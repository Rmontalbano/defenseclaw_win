using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// Runtime 1.0.10 connector facts (G10C-06, G10C-08, G10C-09 text), synthetic throughout: an agent that updated itself in place (the gateway keeps running and
/// does not start that connector), the OpenClaw plugin's call-in (<c>openclaw_plugin</c>), and the per-user outage rule's sentence by runtime.
/// </summary>
public sealed class AgentUpdatedAndOpenClawHealthTests
{
    private const string CodexChanged =
        "selected Codex executable digest does not match protected evidence: C:/x/codex.exe (the Codex executable changed since setup (a Codex update does this); run: defenseclaw setup codex --yes)";

    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ServiceState Block(string detailsJson) =>
        JsonSerializer.Deserialize<ServiceState>("{\"state\":\"running\",\"details\":" + detailsJson + "}", Web)!;

    private static string Details(string name, string reason) =>
        "{\"connectors_not_started\":[\"" + name + "\"],\"connectors_not_started_reasons\":{\"" + name + "\":\"" + reason + "\"}}";

    private static RuntimeCapabilities Capabilities(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    // ------------------------------------------------------------------ the agent updated itself

    [Theory]
    [InlineData("codex")]
    [InlineData("amp")]
    [InlineData("hermes")]
    [InlineData("opencode")]
    public void The_four_agents_that_update_themselves_are_told_by_the_runtimes_words(string name)
    {
        var skip = Assert.Single(ConnectorStartHealth.From(Block(Details(name, CodexChanged))));

        Assert.Equal(ConnectorSkipKind.AgentUpdated, skip.Kind);
        Assert.Equal(name, skip.Name);
    }

    [Fact]
    public void Another_connector_with_the_same_words_and_a_generic_failure_are_not_the_agent_updated_kind()
    {
        Assert.Equal(ConnectorSkipKind.SetupFailed, Assert.Single(ConnectorStartHealth.From(Block(Details("claudecode", CodexChanged)))).Kind);
        Assert.Equal(ConnectorSkipKind.SetupFailed, Assert.Single(ConnectorStartHealth.From(Block(Details("codex", "setup failed: disk full")))).Kind);
    }

    [Fact]
    public void The_not_installed_reason_wins_over_the_words()
    {
        var reason = ConnectorStartHealth.NotInstalledReasonPrefix + " (the Codex executable changed since setup)";

        Assert.Equal(ConnectorSkipKind.AgentNotInstalled, Assert.Single(ConnectorStartHealth.From(Block(Details("codex", reason)))).Kind);
    }

    // ------------------------------------------------------------------ openclaw_plugin

    [Fact]
    public void The_openclaw_plugin_key_is_read_and_an_absent_one_is_null()
    {
        var present = JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":1,\"openclaw_plugin\":{\"last_seen_at\":\"2026-11-20T10:00:00Z\",\"hook_mode\":\"native\"}}", Web)!;
        var absent = JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":1}", Web)!;

        Assert.Equal("native", present.OpenClawPlugin!.HookMode);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), present.OpenClawPlugin.LastSeen);
        Assert.Null(absent.OpenClawPlugin);
    }

    [Fact]
    public void A_last_seen_of_the_wrong_shape_never_fails_the_health_parse()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":1,\"openclaw_plugin\":{\"last_seen_at\":\"not a time\"}}", Web)!;

        Assert.Null(health.OpenClawPlugin!.LastSeen);
        Assert.False(OpenClawPluginHealth.IsStale(health.OpenClawPlugin, TimeSpan.FromHours(1), DateTimeOffset.UtcNow, out _));
    }

    [Theory]
    [InlineData(179, false)]
    [InlineData(180, false)]
    [InlineData(181, true)]
    public void A_call_in_is_fresh_for_180_seconds(int secondsAgo, bool stale)
    {
        var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var plugin = new OpenClawPluginHealth { LastSeenAt = now.AddSeconds(-secondsAgo).ToString("o", System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Equal(stale, OpenClawPluginHealth.IsStale(plugin, TimeSpan.FromHours(1), now, out var age));
        Assert.Equal(TimeSpan.FromSeconds(secondsAgo), age);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(3600, true)]
    public void An_absent_key_is_a_fault_only_after_the_gateway_has_run_90_seconds(int uptimeSeconds, bool stale)
    {
        Assert.Equal(stale, OpenClawPluginHealth.IsStale(null, TimeSpan.FromSeconds(uptimeSeconds), DateTimeOffset.UtcNow, out var age));
        Assert.Null(age);
    }

    // ------------------------------------------------------------------ the outage rule by runtime

    [Fact]
    public void The_outage_rule_names_the_openclaw_and_zeptoclaw_exception_only_from_1_0_10_and_1_0_9_is_word_for_word()
    {
        const string clause = "(except OpenClaw under the strict policy and ZeptoClaw model requests, which stay closed)";

        Assert.Equal(HookFailModeRule.SavedWinsOutageRule, HookFailModeRule.SavedWinsOutageRuleFor(Capabilities("1.0.6")));
        Assert.DoesNotContain(clause, HookFailModeRule.SavedWinsOutageRuleFor(Capabilities("1.0.9")), StringComparison.Ordinal);

        var now = HookFailModeRule.SavedWinsOutageRuleFor(Capabilities("1.0.10"));
        Assert.StartsWith("On a per-user install an explicit saved mode wins " + clause + ". When unset, the gateway-outage mode follows the policy tier", now, StringComparison.Ordinal);
        Assert.Contains(HookFailModeRule.JudgeEscalationRule, now, StringComparison.Ordinal);
        Assert.Equal(
            HookFailModeRule.SavedWinsOutageRule.Replace("wins.", "wins " + clause + ".", StringComparison.Ordinal) + " " + HookFailModeRule.JudgeEscalationRule,
            now);
    }
}
