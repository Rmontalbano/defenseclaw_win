using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>
/// The rows of "What needs attention" for the gateway's durability (CUST-352), the argv of every button on them, and the sentences the tray flyout
/// and the toast share. Pure: snapshots are built by hand, nothing reads a disk or starts a process.
/// </summary>
public sealed class DurabilityAttentionTests
{
    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";
    private const string Toml = @"C:\Users\tester\.codex\config.toml";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

    private static CasPendingWrite Pending(string target = Toml) =>
        new("068ff1aa22bb33cc44dd55ee", @"C:\dc", new[] { @"C:\dc\.defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent" }, new[] { @"C:\Users\tester\.codex\.tmp-cas-v2-stage-1" }, target, "staging+prepared", Now.AddMinutes(-12));

    private static DurabilityFacts Facts(
        bool pinnedMissing = false,
        bool pinnedThere = false,
        bool pending = false,
        string? failMode = null,
        bool quarantine = false) =>
        new(
            pinnedMissing || pinnedThere ? new AgentExecutablePin("codex", OldExe, "hook_contract_lock.json", Exists: pinnedThere) : null,
            pending ? new[] { Pending() } : Array.Empty<CasPendingWrite>(),
            failMode is null ? Array.Empty<ConnectorFailMode>() : new[] { new ConnectorFailMode("codex", failMode) },
            quarantine ? new CasQuarantineInfo(@"C:\app\cas-quarantine\20261015T214000Z", Now.AddMinutes(-20), 3) : null);

    private static GatewaySnapshot Snapshot(AppGatewayState state, DurabilityFacts? facts, GatewayExitReason? failure = null) => new()
    {
        State = state,
        Install = state == AppGatewayState.Running ? InstallState.Running : InstallState.GatewayStopped,
        Detail = state == AppGatewayState.Running ? "ok" : "Nothing is listening on port 18970.",
        Durability = facts,
        LastFailure = failure,
        PolledAt = Now,
    };

    private static IReadOnlyList<AttentionRow> Rows(GatewaySnapshot snapshot, Func<string, string?>? blocked = null) =>
        DurabilityAttention.Rows(snapshot, blocked ?? (_ => null), Now);

    // ---- fail-mode matrix ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AppGatewayState.GatewayStopped, "closed", true)]
    [InlineData(AppGatewayState.GatewayStopped, "open", false)]
    [InlineData(AppGatewayState.Running, "closed", false)]
    [InlineData(AppGatewayState.Running, "open", false)]
    [InlineData(AppGatewayState.Degraded, "closed", false)]
    [InlineData(AppGatewayState.Unknown, "closed", false)]
    [InlineData(AppGatewayState.NotInstalled, "closed", false)]
    public void An_agent_is_blocked_only_when_its_hooks_fail_closed_and_the_gateway_is_stopped(AppGatewayState state, string mode, bool blocked)
    {
        var snapshot = Snapshot(state, Facts(failMode: mode));

        Assert.Equal(blocked, snapshot.IsAgentBlocked);
        Assert.Equal(blocked, Rows(snapshot).Any(r => r.Title.EndsWith("blocked: fail mode is closed and the gateway is down", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_blocked_row_is_critical_names_the_agent_and_offers_start_and_fail_open()
    {
        var row = Assert.Single(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed"))));

        Assert.Equal("Codex is blocked: fail mode is closed and the gateway is down", row.Title);
        Assert.Equal("Critical", row.SeverityKey);
        Assert.True(row.OffersStart);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.SwitchToFailOpen, action.Id);
        Assert.Equal("Switch to fail open", action.Label);
        Assert.True(action.Enabled);
        Assert.Contains("Codex's hooks fail closed", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Start gateway is available", row.ToString(), StringComparison.Ordinal);
        Assert.Contains("Switch to fail open is available", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_agents_are_named_together_and_the_title_is_the_toast_title_too()
    {
        var facts = new DurabilityFacts(null, Array.Empty<CasPendingWrite>(), new[] { new ConnectorFailMode("claudecode", "closed"), new ConnectorFailMode("codex", "closed") }, null);

        Assert.Equal("Claude Code and Codex are blocked: fail mode is closed and the gateway is down", AgentBlock.Title(facts));
        Assert.Equal("Codex is blocked: fail mode is closed and the gateway is down", AgentBlock.Title(Facts(failMode: "closed")));
    }

    // ---- incident A: the moved executable --------------------------------------------------------------------------------

    [Fact]
    public void A_pinned_executable_that_is_gone_is_a_high_row_while_the_gateway_still_runs_and_names_the_next_start()
    {
        var row = Assert.Single(Rows(Snapshot(AppGatewayState.Running, Facts(pinnedMissing: true))));

        Assert.Equal("Codex's executable moved after an update", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains(OldExe, row.Detail, StringComparison.Ordinal);
        Assert.Contains("will fail the next time it starts", row.Detail, StringComparison.Ordinal);
        Assert.Equal(AttentionActionIds.ReselectCodex, Assert.Single(row.Actions.Items).Id);
        Assert.Equal("Re-select Codex", row.Actions.Items[0].Label);
    }

    [Fact]
    public void The_same_problem_on_a_stopped_gateway_is_critical_and_says_it_is_why()
    {
        var stale = new GatewayExitReason.StaleAgentExecutable("codex", OldExe);
        var row = Assert.Single(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pinnedMissing: true), stale)));

        Assert.Equal("Critical", row.SeverityKey);
        Assert.Contains("That is why the gateway stops at start", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_executable_that_exists_is_no_row_even_if_the_old_log_still_says_it_was_stale()
    {
        var stale = new GatewayExitReason.StaleAgentExecutable("codex", OldExe);

        Assert.Empty(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pinnedThere: true), stale)));
    }

    [Fact]
    public void The_log_alone_is_enough_when_the_lock_names_no_executable()
    {
        var stale = new GatewayExitReason.StaleAgentExecutable("codex", OldExe);

        var row = Assert.Single(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(), stale)));

        Assert.Contains(OldExe, row.Detail, StringComparison.Ordinal);
    }

    // ---- incident B: the stuck write ---------------------------------------------------------------------------------------

    [Fact]
    public void A_stuck_write_is_a_high_row_with_its_target_age_and_the_clear_action_and_says_there_is_no_upstream_command()
    {
        var row = Assert.Single(Rows(Snapshot(AppGatewayState.Running, Facts(pending: true))));

        Assert.Equal("A write to Codex's config.toml is stuck", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains(Toml, row.Detail, StringComparison.Ordinal);
        Assert.Contains("12 minutes", row.Detail, StringComparison.Ordinal);
        Assert.Contains("068ff1aa", row.Detail, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw has no command to recover from this", row.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing is deleted", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(AttentionActionIds.ClearStuckWrite, action.Id);
        Assert.Equal("Clear the stuck pending write", action.Label);
    }

    [Fact]
    public void A_stuck_write_that_the_log_names_as_the_reason_the_gateway_stops_is_critical()
    {
        var stuck = new GatewayExitReason.StuckConfigWrite("codex", Toml);

        var row = Assert.Single(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pending: true), stuck)));

        Assert.Equal("Critical", row.SeverityKey);
        Assert.Contains("why the gateway stops at start", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_moved_aside_are_an_info_row_with_put_back_and_the_setup_rerun()
    {
        var row = Assert.Single(Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(quarantine: true))));

        Assert.Equal("Info", row.SeverityKey);
        Assert.Equal(new[] { AttentionActionIds.PutBack, AttentionActionIds.ReselectCodex }, row.Actions.Items.Select(a => a.Id).ToArray());
        Assert.Contains("3 file(s)", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_with_nothing_wrong_and_one_with_no_facts_yet_add_no_rows()
    {
        Assert.Empty(Rows(Snapshot(AppGatewayState.Running, Facts())));
        Assert.Empty(Rows(Snapshot(AppGatewayState.Unknown, null)));
    }

    // ---- the buttons: reasons and argv -----------------------------------------------------------------------------------------

    [Fact]
    public void A_button_the_installation_forbids_is_off_with_that_sentence_as_its_tip()
    {
        const string Managed = "This installation is managed by your organization.";
        var rows = Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pinnedMissing: true, pending: true, failMode: "closed", quarantine: true)), _ => Managed);

        var actions = rows.SelectMany(r => r.Actions.Items).ToArray();
        Assert.True(actions.Length >= 5);
        Assert.All(actions, a =>
        {
            Assert.False(a.Enabled);
            Assert.Equal(Managed, a.Tip);
        });
    }

    [Fact]
    public void Rows_with_the_same_content_are_equal_so_an_idle_poll_redraws_nothing()
    {
        var first = Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pending: true, failMode: "closed")));
        var second = Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pending: true, failMode: "closed")));

        Assert.Equal(first, second);
        Assert.NotEqual(first, Rows(Snapshot(AppGatewayState.GatewayStopped, Facts(pending: true, failMode: "closed")), _ => "off"));
    }

    [Fact]
    public void Re_select_always_passes_the_mode_and_fail_open_has_no_connector_flag()
    {
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "observe" }, DurabilityAttention.ReselectArgv("observe"));
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "action" }, DurabilityAttention.ReselectArgv("action"));
        Assert.Equal(new[] { "guardrail", "fail-mode", "open", "--yes" }, DurabilityAttention.FailOpenArgv);
        Assert.DoesNotContain("--connector", DurabilityAttention.FailOpenArgv);
    }

    [Theory]
    [InlineData("2026-10-15T20:00:00Z [guardrail] direct-upstream mode: codex policy_mode=observe enforcement=false", "action", "observe")]
    [InlineData("2026-10-15T20:00:00Z [guardrail] direct-upstream mode: codex policy_mode=action enforcement=true", "observe", "action")]
    [InlineData("nothing useful here", "observe", "observe")]
    [InlineData("nothing useful here", "action", "action")]
    [InlineData("nothing useful here", "enforce", "action")]
    [InlineData("nothing useful here", null, "observe")]
    [InlineData("nothing useful here", "banana", "observe")]
    public void The_mode_is_what_the_gateway_logged_then_config_then_observe(string line, string? configured, string expected)
    {
        var (mode, source) = DurabilityAttention.ResolveCodexMode(new[] { line }, configured);

        Assert.Equal(expected, mode);
        Assert.False(string.IsNullOrWhiteSpace(source));
    }

    // ---- what the stopped row and the flyout add --------------------------------------------------------------------------------

    [Fact]
    public void The_stopped_sentence_is_the_reason_in_plain_words_and_only_for_a_stopped_gateway()
    {
        var stale = new GatewayExitReason.StaleAgentExecutable("codex", OldExe);

        Assert.Equal(
            "The gateway stops at start: Codex's executable moved after a Codex update.",
            DurabilityAttention.StoppedSentence(Snapshot(AppGatewayState.GatewayStopped, null, stale)));
        Assert.Equal(string.Empty, DurabilityAttention.StoppedSentence(Snapshot(AppGatewayState.Running, null, stale)));
        Assert.Equal(string.Empty, DurabilityAttention.StoppedSentence(Snapshot(AppGatewayState.GatewayStopped, null)));
        Assert.Contains(
            "stuck pending write",
            DurabilityAttention.StoppedSentence(Snapshot(AppGatewayState.GatewayStopped, null, new GatewayExitReason.StuckConfigWrite("codex", Toml))),
            StringComparison.Ordinal);
    }

    // ---- the toast -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_blocked_toast_comes_once_per_episode_and_again_after_the_agent_was_unblocked()
    {
        var tracker = new AgentBlockedToastTracker();
        var blocked = Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed"));

        var toast = tracker.Observe(blocked, suppress: false, enabled: true);
        Assert.NotNull(toast);
        Assert.Equal("Codex is blocked: fail mode is closed and the gateway is down", toast!.Title);
        Assert.Equal(ToastLevel.Error, toast.Level);
        Assert.Null(tracker.Observe(blocked, false, true));

        Assert.Null(tracker.Observe(Snapshot(AppGatewayState.Running, Facts(failMode: "closed")), false, true));
        Assert.NotNull(tracker.Observe(blocked, false, true));
    }

    [Fact]
    public void The_blocked_toast_waits_for_the_operators_own_action_and_for_the_setting_without_using_up_the_episode()
    {
        var tracker = new AgentBlockedToastTracker();
        var blocked = Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed"));

        Assert.Null(tracker.Observe(blocked, suppress: true, enabled: true));
        Assert.Null(tracker.Observe(blocked, suppress: false, enabled: false));
        Assert.NotNull(tracker.Observe(blocked, suppress: false, enabled: true));
    }

    [Fact]
    public void A_launch_into_a_blocked_state_is_announced_and_switching_to_open_ends_the_episode()
    {
        var tracker = new AgentBlockedToastTracker();

        Assert.NotNull(tracker.Observe(Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed")), false, true));
        Assert.Null(tracker.Observe(Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "open")), false, true));
        Assert.NotNull(tracker.Observe(Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed")), false, true));
    }

    // ---- the snapshot ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_failure_and_the_facts_are_part_of_what_a_surface_renders()
    {
        var plain = Snapshot(AppGatewayState.GatewayStopped, Facts());

        Assert.True(plain.RendersSameAs(Snapshot(AppGatewayState.GatewayStopped, Facts())));
        Assert.False(plain.RendersSameAs(plain with { LastFailure = new GatewayExitReason.Other("x") }));
        Assert.False(plain.RendersSameAs(Snapshot(AppGatewayState.GatewayStopped, Facts(failMode: "closed"))));
        Assert.False(plain.RendersSameAs(Snapshot(AppGatewayState.GatewayStopped, Facts(pinnedMissing: true))));
    }
}
