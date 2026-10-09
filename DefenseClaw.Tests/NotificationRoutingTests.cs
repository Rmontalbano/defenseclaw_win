using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="NotificationRouting"/> (CUST-271): the Notifications Routing wizard of the 0.8.10 TUI as data and argv - six slots seeded from
/// config.yaml, one <c>setup notifications-set</c> per slot that changed, nothing when none did, and the gateway restarted once. The slot list is
/// held against the CLI's own help screen (<c>Fixtures/runtime-0.8.10/setup-notifications-set.txt</c>, captured with <c>--help</c>; nothing was run).
/// </summary>
public class NotificationRoutingTests
{
    private static readonly string Help = FixtureFiles.ReadText("runtime-0.8.10/setup-notifications-set.txt");

    private static IReadOnlyDictionary<string, bool> Wanted(NotificationRoutingState state, params (string Slot, bool On)[] flips)
    {
        var wanted = new Dictionary<string, bool>(state.Values, StringComparer.Ordinal);
        foreach (var (slot, on) in flips)
        {
            wanted[slot] = on;
        }

        return wanted;
    }

    // ---- the slots ----

    [Fact]
    public void The_six_slots_are_the_tuis_in_its_order_with_the_runtimes_defaults()
    {
        Assert.Equal(
            new[] { "block_enforced", "block_would_block", "hitl_approval", "sources.hook", "sources.guardrail", "sources.asset_policy" },
            NotificationRouting.Slots.Select(s => s.Id));
        Assert.Equal(
            new[] { true, false, true, true, true, true },
            NotificationRouting.Slots.Select(s => s.DefaultOn));
        Assert.Equal(
            new[] { "Block (enforced)", "Block (would-block / observe)", "HITL approval", "Source: hooks", "Source: guardrail", "Source: asset policy" },
            NotificationRouting.Slots.Select(s => s.Label));
        Assert.Equal(3, NotificationRouting.Slots.Count(s => s.Kind == NotificationSlotKind.Category));
        Assert.Equal(3, NotificationRouting.Slots.Count(s => s.Kind == NotificationSlotKind.Source));
    }

    [Fact]
    public void Every_slot_is_one_the_installed_clis_help_lists_and_its_defaults_are_the_ones_it_prints()
    {
        foreach (var slot in NotificationRouting.Slots)
        {
            Assert.Contains(slot.Id, Help, StringComparison.Ordinal);
        }

        // "Real blocks (default: on)", "would-block ... (default: off)", "Human-in-the-loop prompts (default: on)".
        Assert.Contains("block_enforced       Real blocks (default: on).", Help, StringComparison.Ordinal);
        Assert.Contains("block_would_block    Observe-mode would-block / would-ask toasts (default: off).", Help, StringComparison.Ordinal);
        Assert.Contains("hitl_approval        Human-in-the-loop prompts (default: on).", Help, StringComparison.Ordinal);
        Assert.Contains("--no-restart", Help, StringComparison.Ordinal);
    }

    // ---- seeded from config ----

    [Fact]
    public void A_config_that_says_nothing_is_every_slots_default_and_the_switch_on()
    {
        foreach (var yaml in new[] { string.Empty, "gateway:\n  api_port: 18970\n", "notifications:\n", "notifications: {}\n" })
        {
            var state = NotificationRouting.FromYaml(yaml);

            Assert.True(state.MasterEnabled);
            Assert.False(state.MasterIsExplicit);
            Assert.Equal(NotificationRouting.Slots.Select(s => s.DefaultOn), NotificationRouting.Slots.Select(s => state[s.Id]));
        }
    }

    [Fact]
    public void What_config_says_wins_over_the_default_for_each_slot_and_the_switch()
    {
        var state = NotificationRouting.FromYaml("""
            notifications:
              enabled: false
              block_enforced: no
              block_would_block: yes
              hitl_approval: off
              sources:
                hook: false
                guardrail: on
            """);

        Assert.False(state.MasterEnabled);
        Assert.True(state.MasterIsExplicit);
        Assert.False(state["block_enforced"]);
        Assert.True(state["block_would_block"]);
        Assert.False(state["hitl_approval"]);
        Assert.False(state["sources.hook"]);
        Assert.True(state["sources.guardrail"]);
        Assert.True(state["sources.asset_policy"]); // not named: its default
    }

    [Fact]
    public void A_value_that_is_not_a_boolean_word_is_ignored_for_the_slots_default()
    {
        var state = NotificationRouting.FromYaml("notifications:\n  block_enforced: maybe\n  enabled: perhaps\n");

        Assert.True(state["block_enforced"]);
        Assert.True(state.MasterEnabled);
        Assert.False(state.MasterIsExplicit);
    }

    [Fact]
    public void An_unknown_slot_id_is_off_and_not_found()
    {
        var state = NotificationRouting.FromYaml(string.Empty);

        Assert.False(state["nonsense"]);
        Assert.Null(NotificationRouting.Find("nonsense"));
        Assert.NotNull(NotificationRouting.Find("sources.hook"));
    }

    // ---- the diff: nothing to apply, or one change per slot that moved ----

    [Fact]
    public void Nothing_wanted_that_differs_is_nothing_to_apply()
    {
        var state = NotificationRouting.FromYaml("notifications:\n  block_would_block: true\n");

        Assert.Empty(NotificationRouting.Diff(state, state.Values));
        Assert.Empty(NotificationRouting.Diff(state, new Dictionary<string, bool>()));
    }

    [Fact]
    public void Two_flips_are_two_changes_in_slot_order_whatever_order_they_were_made_in()
    {
        var state = NotificationRouting.FromYaml(string.Empty);

        var changes = NotificationRouting.Diff(state, Wanted(state, ("sources.hook", false), ("block_would_block", true)));

        Assert.Equal(new[] { "block_would_block", "sources.hook" }, changes.Select(c => c.Slot.Id));
        Assert.Equal(new[] { "on", "off" }, changes.Select(c => c.Value));
        Assert.Equal(
            new[] { "Block (would-block / observe): off to on", "Source: hooks: on to off" },
            changes.Select(c => c.Describe()));
    }

    [Fact]
    public void A_slot_flipped_and_flipped_back_is_not_a_change()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var flipped = Wanted(state, ("hitl_approval", false));
        var back = Wanted(state, ("hitl_approval", false), ("hitl_approval", true));

        Assert.Single(NotificationRouting.Diff(state, flipped));
        Assert.Empty(NotificationRouting.Diff(state, back));
    }

    // ---- the argv ----

    [Fact]
    public void One_invocation_per_change_the_dotted_slot_and_the_value_and_only_the_last_restarts()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var changes = NotificationRouting.Diff(state, Wanted(state, ("block_would_block", true), ("hitl_approval", false), ("sources.hook", false)));

        var argvs = NotificationRouting.Argvs(changes, restartAfter: true);

        Assert.Equal(
            new[]
            {
                "setup notifications-set block_would_block on --no-restart",
                "setup notifications-set hitl_approval off --no-restart",
                "setup notifications-set sources.hook off",
            },
            argvs.Select(a => string.Join(' ', a)));
    }

    [Fact]
    public void A_single_change_restarts_once_by_itself_unless_told_not_to()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var changes = NotificationRouting.Diff(state, Wanted(state, ("hitl_approval", false)));

        Assert.Equal("setup notifications-set hitl_approval off", string.Join(' ', Assert.Single(NotificationRouting.Argvs(changes, restartAfter: true))));
        Assert.Equal("setup notifications-set hitl_approval off --no-restart", string.Join(' ', Assert.Single(NotificationRouting.Argvs(changes, restartAfter: false))));
    }

    [Fact]
    public void Without_a_restart_none_of_them_restarts()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var changes = NotificationRouting.Diff(state, Wanted(state, ("block_would_block", true), ("sources.guardrail", false)));

        Assert.All(NotificationRouting.Argvs(changes, restartAfter: false), argv => Assert.Equal("--no-restart", argv[^1]));
    }

    [Fact]
    public void A_cli_whose_help_has_no_no_restart_gets_no_flag_and_each_step_restarts_by_itself()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var changes = NotificationRouting.Diff(state, Wanted(state, ("block_would_block", true), ("sources.guardrail", false)));

        Assert.All(NotificationRouting.Argvs(changes, restartAfter: true, canSkipRestart: false), argv => Assert.DoesNotContain("--no-restart", argv));
        Assert.All(NotificationRouting.Argvs(changes, restartAfter: false, canSkipRestart: false), argv => Assert.DoesNotContain("--no-restart", argv));
    }

    [Fact]
    public void No_changes_build_no_invocation()
    {
        Assert.Empty(NotificationRouting.Argvs(Array.Empty<NotificationChange>(), restartAfter: true));
    }

    // ---- what the rest of the app does with those argv ----

    [Fact]
    public void Every_step_changes_state_and_is_refused_on_a_read_only_installation_never_a_read()
    {
        var state = NotificationRouting.FromYaml(string.Empty);
        var changes = NotificationRouting.Diff(state, Wanted(state, ("block_would_block", true), ("block_enforced", false)));

        foreach (var argv in NotificationRouting.Argvs(changes, restartAfter: true))
        {
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(argv));
            Assert.False(InstallationGate.IsReadOnly("defenseclaw", argv));
            Assert.False(CommandTiers.IsUnreviewedRead(argv));
        }
    }
}
