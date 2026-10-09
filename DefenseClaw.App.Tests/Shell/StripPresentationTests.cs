using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The status strip's wording rules as the pure functions they are (CUST-273): what a state word, a missing key, a connector roster, a redaction label
/// or a policy mode is called and how it is toned. The chips built from them are in <see cref="StatusStripChipTests"/>.
/// </summary>
public sealed class StripPresentationTests
{
    [Theory]
    [InlineData("running", true)]
    [InlineData("Running", true)]
    [InlineData("  enabled ", true)]
    [InlineData("active", true)]
    [InlineData("healthy", true)]
    [InlineData("ok", true)]
    [InlineData("allowed", true)]
    [InlineData("clean", true)]
    [InlineData("disabled", false)]
    [InlineData("error", false)]
    [InlineData("degraded", false)]
    [InlineData("stopped", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void A_state_word_is_healthy_when_it_is_one_the_TUI_draws_green(string state, bool healthy) =>
        Assert.Equal(healthy, StripPresentation.IsHealthyWord(state));

    [Fact]
    public void A_subsystem_chip_is_its_name_while_healthy_and_its_name_and_state_otherwise()
    {
        Assert.Equal("Guardrail", StripPresentation.SubsystemText("Guardrail", "running"));
        Assert.Equal("Guardrail: error", StripPresentation.SubsystemText("Guardrail", "error"));
        Assert.Equal("Watchdog: unknown", StripPresentation.SubsystemText("Watchdog", "unknown"));
    }

    [Fact]
    public void The_subsystem_sentence_says_the_state_and_when_paused_that_it_is_the_last_one_seen()
    {
        Assert.Equal("Guardrail: running", StripPresentation.SubsystemName("Guardrail", "running", paused: false));
        Assert.Equal("Guardrail: last seen running, monitoring paused", StripPresentation.SubsystemName("Guardrail", "running", paused: true));

        var live = StripPresentation.SubsystemDetail("Watchdog", "running", "2 skill dirs, 2 plugin dirs · since Jul 28 12:13", paused: false);
        Assert.Equal("Watchdog: running. 2 skill dirs, 2 plugin dirs · since Jul 28 12:13. From the gateway's /health, the same reading as the Overview's Services card.", live);

        var paused = StripPresentation.SubsystemDetail("Watchdog", "running", string.Empty, paused: true);
        Assert.Equal("Watchdog: running. Monitoring is paused, so this is the last reading, not the current state.", paused);
    }

    [Fact]
    public void The_keys_words_are_the_TUIs_and_singular_where_there_is_one()
    {
        Assert.Equal("Keys: missing A_KEY", StripPresentation.KeysText(new[] { "A_KEY" }));
        Assert.Equal("Keys: missing A_KEY, B_KEY", StripPresentation.KeysText(new[] { "A_KEY", "B_KEY" }));
        Assert.Equal("Keys: missing A_KEY, B_KEY (+1 more)", StripPresentation.KeysText(new[] { "A_KEY", "B_KEY", "C_KEY" }));
        Assert.Equal("Keys: missing A_KEY, B_KEY (+8 more)", StripPresentation.KeysText(Enumerable.Range(0, 10).Select(i => ((char)('A' + i)) + "_KEY").ToArray()));

        Assert.Equal("Keys: 1 required credential is not set: A_KEY", StripPresentation.KeysName(new[] { "A_KEY" }));
        Assert.Equal("Keys: 2 required credentials are not set: A_KEY, B_KEY", StripPresentation.KeysName(new[] { "A_KEY", "B_KEY" }));

        Assert.StartsWith("1 required credential is not set: A_KEY.", StripPresentation.KeysDetail(new[] { "A_KEY" }), StringComparison.Ordinal);
        Assert.StartsWith("3 required credentials are not set: A_KEY, B_KEY, C_KEY.", StripPresentation.KeysDetail(new[] { "A_KEY", "B_KEY", "C_KEY" }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_roster_has_no_blanks_and_no_repeats_without_case_and_the_connector_words_follow_it()
    {
        Assert.Equal(new[] { "Codex" }, StripPresentation.RosterOf(new GatewaySnapshot { ActiveConnectors = new[] { "Codex", "codex", "  ", "CODEX " } }));
        Assert.Equal("Connector: Codex", StripPresentation.ConnectorText(new GatewaySnapshot { ActiveConnectors = new[] { "Codex" } }, null));

        // The count is of the connectors, not of the entries: a repeat (the monitor's own list has none) does not make "All connectors (3)".
        var several = new GatewaySnapshot { ActiveConnectors = new[] { "claudecode", "codex", "Codex" } };
        Assert.Equal("All connectors (2)", StripPresentation.ConnectorText(several, null));
        Assert.Equal("All connectors (2)", StripPresentation.ConnectorText(several, "  "));
        Assert.Equal("codex (filtered)", StripPresentation.ConnectorText(several, "codex"));
    }

    [Fact]
    public void The_connector_tooltip_says_what_the_filter_is_and_how_to_change_it()
    {
        var several = new GatewaySnapshot { ActiveConnectors = new[] { "claudecode", "codex", "hermes" } };

        var all = StripPresentation.ConnectorDetail(several, null);
        Assert.Contains("All 3 connectors: claudecode, codex, hermes", all, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+M", all, StringComparison.Ordinal);

        var narrowed = StripPresentation.ConnectorDetail(several, "codex");
        Assert.Contains("narrowed to codex (of claudecode, codex, hermes)", narrowed, StringComparison.Ordinal);

        // One connector or none: the words the chip always had.
        Assert.Equal("No connector", StripPresentation.ConnectorDetail(new GatewaySnapshot(), null));
        Assert.Equal("Connector: claudecode", StripPresentation.ConnectorDetail(new GatewaySnapshot { ActiveConnectors = new[] { "claudecode" } }, null));
    }

    [Theory]
    [InlineData("per-route · unredacted", "Warn")]
    [InlineData("per-route · none,sensitive", "Warn")]
    [InlineData("per-route · sensitive,none", "Warn")]
    [InlineData("per-route · none", "Warn")]
    [InlineData("per-route · whole-content", "Ok")]
    [InlineData("per-route · sensitive,strict", "Ok")]
    [InlineData("per-route · nonexistent", "Ok")]
    [InlineData("per-route (loading)", "Neutral")]
    [InlineData("per-route (unavailable)", "Neutral")]
    public void The_redaction_tone_is_a_warning_exactly_when_some_route_sends_events_unredacted(string label, string tone) =>
        Assert.Equal(tone, StripPresentation.RedactionTone(label));

    [Fact]
    public void The_redaction_sentences_say_what_each_label_means()
    {
        Assert.Contains("has not been read yet", StripPresentation.RedactionDetail("per-route (loading)"), StringComparison.Ordinal);
        Assert.Contains("could not be read", StripPresentation.RedactionDetail("per-route (unavailable)"), StringComparison.Ordinal);
        Assert.Contains("sends them unredacted (profile none)", StripPresentation.RedactionDetail("per-route · unredacted"), StringComparison.Ordinal);

        var mixed = StripPresentation.RedactionDetail("per-route · none,sensitive");
        Assert.Contains("use: none,sensitive.", mixed, StringComparison.Ordinal);
        Assert.Contains("At least one route sends events unredacted", mixed, StringComparison.Ordinal);

        var redacted = StripPresentation.RedactionDetail("per-route · sensitive,strict");
        Assert.Contains("use: sensitive,strict.", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("unredacted", redacted, StringComparison.Ordinal);

        Assert.All(
            new[] { "per-route (loading)", "per-route (unavailable)", "per-route · unredacted", "per-route · sensitive" },
            label => Assert.Contains("Observability card", StripPresentation.RedactionDetail(label), StringComparison.Ordinal));
    }

    [Fact]
    public void The_policy_mode_is_the_gateways_when_it_reports_one_then_the_scoped_connectors_then_the_primary_ones()
    {
        var config = ConfigStore.Parse(
            "guardrail:\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: Observe\n    codex:\n      mode: action\nclaw:\n  mode: hermes\n").Config;

        // The gateway's word wins, lower-cased, and says it is the gateway's.
        var live = StripScene.Snapshot(policyMode: "ACTION");
        Assert.Equal(("action", true), StripPresentation.PolicyMode(live, config, "claudecode"));

        // Silent: the scoped connector's mode from config.yaml, else the primary connector's.
        var silent = StripScene.Snapshot(policyMode: null, enforcement: null);
        Assert.Equal(("action", false), StripPresentation.PolicyMode(silent, config, "codex"));
        Assert.Equal(("observe", false), StripPresentation.PolicyMode(silent, config, null));

        // A connector config.yaml does not know says nothing.
        Assert.Null(StripPresentation.PolicyMode(silent, config, "not-configured"));
    }

    [Fact]
    public void The_policy_sentence_says_the_mode_the_enforcement_and_where_it_was_read()
    {
        Assert.Equal(
            "Policy posture: observe. Enforcement is off: the guardrail observes only. Reported by the gateway (/health, guardrail policy_mode).",
            StripPresentation.PolicyDetail("observe", fromGateway: true, enforcing: false));
        Assert.Equal(
            "Policy posture: action. Enforcement is on. Reported by the gateway (/health, guardrail policy_mode).",
            StripPresentation.PolicyDetail("action", fromGateway: true, enforcing: true));
        Assert.Equal(
            "Policy posture: observe. From config.yaml: the gateway did not report a mode, so this is what was asked for, not necessarily what is running.",
            StripPresentation.PolicyDetail("observe", fromGateway: false, enforcing: null));
    }

    [Fact]
    public void Running_and_stale_are_worded_for_one_and_for_many()
    {
        Assert.Equal("Running", StripPresentation.RunningText(1));
        Assert.Equal("Running 4", StripPresentation.RunningText(4));
        Assert.Equal("1 command is running. Its output is in Activity.", StripPresentation.RunningDetail(1));
        Assert.Equal("4 commands are running. Their output is in Activity.", StripPresentation.RunningDetail(4));

        Assert.Equal(
            "No gateway poll has finished cleanly for 20 s, more than 3 times the 5 s check interval. What this strip shows may be out of date. Refresh polls the gateway now.",
            StripPresentation.StaleDetail(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)));
    }
}
