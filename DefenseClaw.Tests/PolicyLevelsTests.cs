using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.Tests;

/// <summary>
/// The levels and the rules of "weaker" (<see cref="PolicyLevels"/>) against the vectors of the pinned runtime's own tests
/// (<c>cli/tests/test_policy_catalog_levels.py</c>, <c>tui/test_policy_state.py</c>, <c>tui/test_protection_levels.py</c>, source commit 95159fd):
/// the port is only as good as it agrees with them.
/// </summary>
public sealed class PolicyLevelsTests
{
    private const string Default = "/p/guardrail/default";
    private const string Strict = "/p/guardrail/strict";
    private const string Permissive = "/p/guardrail/permissive";

    // ---- resolve_levels: the gateway's guardrailLevelThresholds -------------------------------------------------------------

    [Theory]
    [InlineData(Default, "", "", null, null, 4, 2, "pack", "pack")]
    [InlineData(Strict, "", "", "", "", 2, 1, "pack", "pack")]
    [InlineData(Permissive, "", "", null, null, 4, 3, "pack", "pack")]
    [InlineData("/p/guardrail/protected-codex/strict", "", "", null, null, 2, 1, "pack", "pack")]
    // Default pack + global block_at HIGH: block 3, alert stays at the pack's 2.
    [InlineData(Default, "HIGH", "", null, null, 3, 2, "global", "pack")]
    // Strict pack + connector block_at CRITICAL: block 4, alert keeps strict's 1.
    [InlineData(Strict, "", "", "CRITICAL", "", 4, 1, "override", "pack")]
    // The connector's own value beats the global one; any case is read.
    [InlineData(Default, "high", "low", "medium", "", 2, 1, "override", "global")]
    // Clamp: an alert level above the block level alerts from the block level.
    [InlineData(Strict, "", "CRITICAL", null, null, 2, 2, "pack", "global")]
    [InlineData(Default, "MEDIUM", "HIGH", "", "", 2, 2, "global", "global")]
    // A value the gateway ignores counts as unset.
    [InlineData(Permissive, "SEVERE", "", "", "3", 4, 3, "pack", "pack")]
    public void Levels_resolve_exactly_as_the_gateway_does(string pack, string globalBlock, string globalAlert, string? ownBlock, string? ownAlert, int block, int alert, string blockSource, string alertSource)
    {
        (string, string)? own = ownBlock is null ? null : (ownBlock, ownAlert ?? string.Empty);

        var levels = PolicyLevels.ResolveLevels(pack, (globalBlock, globalAlert), own);

        Assert.Equal((block, alert, blockSource, alertSource), (levels.BlockRank, levels.AlertRank, levels.BlockSource, levels.AlertSource));
        Assert.True(levels.AlertRank <= levels.BlockRank);
    }

    [Fact]
    public void Labels_sources_and_the_clamp_flag_read_as_the_runtime_prints_them()
    {
        var clamped = PolicyLevels.ResolveLevels(Strict, (string.Empty, "CRITICAL"));
        Assert.Equal(("MEDIUM+", "MEDIUM+", true), (clamped.BlockAt, clamped.AlertAt, clamped.AlertClamped));
        Assert.Equal("CRITICAL", PolicyLevels.LevelLabel(clamped.WantedAlertRank));

        Assert.False(PolicyLevels.ResolveLevels(Default).AlertClamped);
        Assert.Equal("pack", PolicyLevels.ResolveLevels(Default).Source);
        Assert.Equal("global", PolicyLevels.ResolveLevels(Default, (string.Empty, "LOW")).Source);
        Assert.Equal("override", PolicyLevels.ResolveLevels(Default, ("HIGH", string.Empty), (string.Empty, "LOW")).Source);

        Assert.Equal(new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW", string.Empty }, new[] { 4, 3, 2, 1, 0 }.Select(PolicyLevels.LevelName));
        Assert.Equal("HIGH", PolicyLevels.LevelValue(" high "));
        Assert.Equal(string.Empty, PolicyLevels.LevelValue("HIGH+"));
        Assert.Equal(string.Empty, PolicyLevels.LevelValue(null));
    }

    [Theory]
    [InlineData("", "default")]
    [InlineData("C:\\Users\\operator\\.defenseclaw\\policies\\guardrail\\strict", "strict")]
    [InlineData("C:\\Users\\operator\\.defenseclaw\\policies\\guardrail\\Permissive\\", "permissive")]
    [InlineData("/p/guardrail/protected-codex/strict", "strict")]
    [InlineData("/p/guardrail/balanced", "default")]
    [InlineData("/p/guardrail/my-pack", "default")]
    public void A_rule_pack_folders_name_decides_its_profile(string folder, string expected) =>
        Assert.Equal(expected, PolicyLevels.PackProfile(folder));

    // ---- what a finding gets ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Block_wins_over_approval_which_wins_over_alert()
    {
        // block CRITICAL, approval from HIGH, alert from MEDIUM
        Assert.Equal(
            new[] { ("CRITICAL", "block"), ("HIGH", "ask"), ("MEDIUM", "alert"), ("LOW", "allow") },
            PolicyLevels.SeverityActions("CRITICAL", "MEDIUM+", "HIGH+"));

        // approval off, strict levels
        Assert.Equal(
            new[] { ("CRITICAL", "block"), ("HIGH", "block"), ("MEDIUM", "block"), ("LOW", "alert") },
            PolicyLevels.SeverityActions("MEDIUM+", "LOW+", "off"));

        Assert.Equal("The global default blocks CRITICAL, asks a human for HIGH+ and alerts on MEDIUM+.", PolicyLevels.PostureSummary("global", "action", "CRITICAL", "MEDIUM+", "HIGH+"));
        Assert.Equal("codex logs only; in action mode it would block CRITICAL and alert on HIGH+.", PolicyLevels.PostureSummary("codex", "observe", "CRITICAL", "HIGH+", "off"));
    }

    [Fact]
    public void Observe_mode_says_what_would_happen_instead_of_what_does()
    {
        var lines = PolicyLevels.MatrixLines("observe", "CRITICAL", "MEDIUM+", "HIGH+");

        Assert.Equal("  CRITICAL  log (would block)", lines[0]);
        Assert.Equal("  HIGH      log (would ask a human)", lines[1]);
        Assert.Equal("  MEDIUM    alert", lines[2]);
        Assert.Equal("  LOW       allow", lines[3]);
        Assert.Equal("  CRITICAL  block", PolicyLevels.MatrixLines("action", "CRITICAL", "MEDIUM+", "HIGH+")[0]);
    }

    // ---- "weaker" ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_kind_of_change_knows_when_it_protects_less()
    {
        // action to observe stops every block; observe to action does not weaken
        Assert.True(PolicyLevels.ModeWeakens("action", "observe"));
        Assert.False(PolicyLevels.ModeWeakens("observe", "action"));
        Assert.False(PolicyLevels.ModeWeakens("action", "action"));

        // a raised threshold catches fewer severities; "none" is the weakest
        Assert.True(PolicyLevels.ThresholdWeakens("MEDIUM+", "HIGH+"));
        Assert.True(PolicyLevels.ThresholdWeakens("CRITICAL", "none"));
        Assert.False(PolicyLevels.ThresholdWeakens("HIGH+", "MEDIUM+"));
        Assert.False(PolicyLevels.ThresholdWeakens("HIGH+", "HIGH+"));
        Assert.False(PolicyLevels.ThresholdWeakens("?", "CRITICAL"));

        // approval off, or from a higher severity
        Assert.True(PolicyLevels.HiltWeakens("HIGH+", "off"));
        Assert.True(PolicyLevels.HiltWeakens("LOW+", "HIGH+"));
        Assert.False(PolicyLevels.HiltWeakens("off", "HIGH+"));
        Assert.False(PolicyLevels.HiltWeakens("HIGH+", "MEDIUM+"));

        // a looser preset
        Assert.True(PolicyLevels.PackWeakens(new[] { "strict" }, "default"));
        Assert.False(PolicyLevels.PackWeakens(new[] { "default" }, "strict"));
        Assert.False(PolicyLevels.PackWeakens(new[] { "mine" }, "permissive"));
        Assert.False(PolicyLevels.PackWeakens(new[] { "strict" }, "my-own-pack"));
    }

    [Fact]
    public void An_action_that_drops_a_step_is_named_by_its_severity()
    {
        var before = PolicyLevels.SeverityActions("CRITICAL", "MEDIUM+", "HIGH+");
        var after = PolicyLevels.SeverityActions("CRITICAL", "MEDIUM+", "off");

        Assert.Equal(new[] { "HIGH" }, PolicyLevels.ActionsWeaken(before, after));
        Assert.Empty(PolicyLevels.ActionsWeaken(after, before));
    }

    private static NamedPolicy Policy(string name, bool active = false, string block = "CRITICAL", string alert = "MEDIUM+", string install = "HIGH+", string firewall = "deny", bool? hilt = null) =>
        new(name, $"{name} policy", name is "default" or "strict" or "permissive", active, $"/policies/{name}.yaml", block, alert, install, firewall, hilt, 0, false, false, false);

    [Fact]
    public void Replacing_a_policy_names_every_way_it_protects_less()
    {
        var current = Policy("default", active: true);
        var strict = Policy("strict", block: "MEDIUM+", alert: "LOW+", install: "MEDIUM+");
        var permissive = Policy("permissive", alert: "HIGH+", install: "CRITICAL", firewall: "allow");

        Assert.Empty(PolicyLevels.PolicyWeakenings(current, strict));
        Assert.Equal(3, PolicyLevels.PolicyWeakenings(current, permissive).Count);
        Assert.Contains("the firewall allows by default instead of denying", PolicyLevels.PolicyWeakenings(current, permissive));
        Assert.NotEmpty(PolicyLevels.PolicyWeakenings(strict, current));
        Assert.Equal(new[] { "human approval is turned off" }, PolicyLevels.PolicyWeakenings(Policy("a", hilt: true), Policy("b", hilt: false)));
        Assert.Empty(PolicyLevels.PolicyWeakenings(null, permissive));
    }

    [Fact]
    public void A_policys_side_effects_and_comparison_are_the_runtimes()
    {
        var policy = new NamedPolicy("p", string.Empty, false, false, "/p.yaml", "HIGH+", "LOW+", "none", string.Empty, null, 3, true, true, false);

        Assert.Equal(new[] { "adds its webhooks to yours", "changes Cisco AI Defense settings", "3 scanner overrides" }, PolicyLevels.PolicySideEffects(policy));
        Assert.Equal(new[] { "1 scanner override" }, PolicyLevels.PolicySideEffects(policy with { AddsWebhooks = false, SetsCisco = false, ScannerOverrides = 1 }));

        var rows = PolicyLevels.PolicyComparison(Policy("default", active: true), Policy("strict", block: "MEDIUM+", hilt: true));
        Assert.Equal(("Block at", "CRITICAL", "MEDIUM+"), rows[0]);
        Assert.Equal(("Human approval", "inherit", "on"), rows[4]);
        Assert.Equal(("Block at", "-", "CRITICAL"), PolicyLevels.PolicyComparison(null, Policy("default"))[0]);
    }

    [Theory]
    [InlineData("HIGH", "HIGH+")]
    [InlineData("3", "HIGH+")]
    [InlineData("critical", "CRITICAL")]
    [InlineData("LOW", "LOW+")]
    [InlineData("9", "none")]
    [InlineData("", "none")]
    [InlineData("nonsense", "none")]
    public void A_threshold_is_shown_on_the_catalog_scale(string value, string expected) =>
        Assert.Equal(expected, PolicyLevels.ThresholdLabel(value));

    [Fact]
    public void Approval_is_off_or_the_lowest_severity_that_asks()
    {
        Assert.Equal("off", PolicyLevels.HiltLabel(false, "HIGH"));
        Assert.Equal("HIGH+", PolicyLevels.HiltLabel(true, "high"));
        Assert.Equal("HIGH+", PolicyLevels.HiltLabel(true, null));
        Assert.Equal("HIGH+", PolicyLevels.HiltLabel(true, "garbage"));
        Assert.Equal("LOW+", PolicyLevels.HiltLabel(true, "LOW"));
        Assert.Equal("action", PolicyLevels.ModeLabel(" Action "));
        Assert.Equal("observe", PolicyLevels.ModeLabel("enforce"));
    }

    [Fact]
    public void Fit_cuts_the_way_the_runtime_does()
    {
        Assert.Equal("abc", PolicyLevels.Fit("abc", 0));
        Assert.Equal("abc", PolicyLevels.Fit("abc", 3));
        Assert.Equal("ab\u2026", PolicyLevels.Fit("abcd", 3));
        Assert.Equal("a\u2026", PolicyLevels.Fit("a bcd", 3));
    }
}
