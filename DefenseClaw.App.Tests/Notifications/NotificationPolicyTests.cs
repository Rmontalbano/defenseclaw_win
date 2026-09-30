using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Notifications;

/// <summary>
/// The rules behind the tray's finding toasts (CUST-202), with no tray, no clock and no database: what is announced, in what
/// words, and where the persisted high-water mark goes. The Mac's rules: CRITICAL and HIGH toggles, severity and target only, a
/// mark that moves whether or not a finding was announced.
/// </summary>
public sealed class AlertToastPolicyTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static AlertQueueItem Finding(string id, int minute, AuditSeverity severity, string? target = "/synthetic/target") =>
        new(id, severity, "scan-finding", target, "claudecode", Base.AddMinutes(minute));

    private static NotificationSettings Settings(long mark = 0, bool critical = true, bool high = true) =>
        new() { Critical = critical, High = high, HighWaterUnixNano = mark };

    private static long Nanos(int minute) => AlertToastPolicy.UnixNanos(Base.AddMinutes(minute));

    // ------------------------------------------------------------------ the mark

    [Fact]
    public void A_timestamp_is_unix_nanoseconds_and_one_that_could_not_be_read_is_zero()
    {
        Assert.Equal(1_000_000_000L, AlertToastPolicy.UnixNanos(DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.Equal(1_790_769_600_000_000_000L, AlertToastPolicy.UnixNanos(Base));
        Assert.Equal(0L, AlertToastPolicy.UnixNanos(DateTimeOffset.MinValue));
        Assert.Equal(0L, AlertToastPolicy.UnixNanos(DateTimeOffset.UnixEpoch.AddDays(-1)));
    }

    [Fact]
    public void A_fresh_installs_first_look_only_sets_the_mark_its_backlog_is_history_not_news()
    {
        var window = new[] { Finding("a", 1, AuditSeverity.Critical), Finding("b", 5, AuditSeverity.High), Finding("c", 3, AuditSeverity.Critical) };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: 0), AlertToastPhase.Launch);

        Assert.Null(decision.Toast);
        Assert.Equal(Nanos(5), decision.HighWaterUnixNano);
    }

    [Fact]
    public void Nothing_newer_than_the_mark_says_nothing_and_leaves_the_mark_where_it_was()
    {
        var window = new[] { Finding("a", 1, AuditSeverity.Critical), Finding("b", 2, AuditSeverity.High) };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(2)), AlertToastPhase.Live);

        Assert.Null(decision.Toast);
        Assert.Equal(Nanos(2), decision.HighWaterUnixNano);
    }

    [Fact]
    public void The_mark_never_moves_backwards_even_when_the_window_has_nothing_as_new()
    {
        var decision = AlertToastPolicy.Decide(Array.Empty<AlertQueueItem>(), Settings(mark: Nanos(9)), AlertToastPhase.Live);

        Assert.Null(decision.Toast);
        Assert.Equal(Nanos(9), decision.HighWaterUnixNano);
    }

    [Fact]
    public void The_window_may_come_in_any_order_and_only_what_is_newer_than_the_mark_counts()
    {
        var window = new[]
        {
            Finding("old", 1, AuditSeverity.Critical),
            Finding("new-1", 7, AuditSeverity.Critical),
            Finding("also-old", 2, AuditSeverity.Critical),
            Finding("new-2", 6, AuditSeverity.Critical),
        };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(3)), AlertToastPhase.Launch);

        Assert.Equal("2 new CRITICAL findings while DefenseClaw was closed", decision.Toast!.Body);
        Assert.Equal(Nanos(7), decision.HighWaterUnixNano);
    }

    // ------------------------------------------------------------------ findings that arrived while the app was closed

    [Fact]
    public void Findings_that_arrived_while_the_app_was_closed_are_announced_once_as_one_toast()
    {
        var window = new[]
        {
            Finding("a", 10, AuditSeverity.Critical),
            Finding("b", 11, AuditSeverity.Critical),
            Finding("c", 12, AuditSeverity.Critical),
            Finding("seen", 1, AuditSeverity.Critical),
        };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5)), AlertToastPhase.Launch);

        var toast = decision.Toast!;
        Assert.Equal("DefenseClaw", toast.Title);
        Assert.Equal("3 new CRITICAL findings while DefenseClaw was closed", toast.Body);
        Assert.Equal(AuditSeverity.Critical, toast.Floor);
        Assert.Equal(ToastLevel.Error, toast.Level);
        Assert.Equal(Nanos(12), decision.HighWaterUnixNano);
    }

    [Fact]
    public void One_critical_reads_as_a_singular_and_a_mix_names_both_severities()
    {
        var one = AlertToastPolicy.Decide(new[] { Finding("a", 10, AuditSeverity.Critical) }, Settings(mark: Nanos(5)), AlertToastPhase.Launch);
        Assert.Equal("1 new CRITICAL finding while DefenseClaw was closed", one.Toast!.Body);

        var mixed = AlertToastPolicy.Decide(
            new[] { Finding("a", 10, AuditSeverity.Critical), Finding("b", 11, AuditSeverity.High), Finding("c", 12, AuditSeverity.High) },
            Settings(mark: Nanos(5)),
            AlertToastPhase.Launch);
        Assert.Equal("1 new CRITICAL and 2 new HIGH findings while DefenseClaw was closed", mixed.Toast!.Body);
        Assert.Equal(AuditSeverity.High, mixed.Toast.Floor);
        Assert.Equal(ToastLevel.Error, mixed.Toast.Level);

        var highOnly = AlertToastPolicy.Decide(new[] { Finding("a", 10, AuditSeverity.High) }, Settings(mark: Nanos(5)), AlertToastPhase.Launch);
        Assert.Equal("1 new HIGH finding while DefenseClaw was closed", highOnly.Toast!.Body);
        Assert.Equal(ToastLevel.Warning, highOnly.Toast.Level);
    }

    // ------------------------------------------------------------------ the toggles

    [Fact]
    public void The_critical_and_high_toggles_each_decide_their_own_severity()
    {
        var window = new[] { Finding("c", 10, AuditSeverity.Critical), Finding("h", 11, AuditSeverity.High) };

        var highOff = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5), high: false), AlertToastPhase.Launch);
        Assert.Equal("1 new CRITICAL finding while DefenseClaw was closed", highOff.Toast!.Body);
        Assert.Equal(AuditSeverity.Critical, highOff.Toast.Floor);

        var criticalOff = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5), critical: false), AlertToastPhase.Launch);
        Assert.Equal("1 new HIGH finding while DefenseClaw was closed", criticalOff.Toast!.Body);
    }

    [Fact]
    public void A_severity_switched_off_is_not_announced_but_the_mark_still_passes_it_so_switching_it_on_later_is_not_retroactive()
    {
        var window = new[] { Finding("c", 10, AuditSeverity.Critical), Finding("h", 11, AuditSeverity.High) };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5), critical: false, high: false), AlertToastPhase.Live);

        Assert.Null(decision.Toast);
        Assert.Equal(Nanos(11), decision.HighWaterUnixNano);
    }

    [Fact]
    public void Medium_and_low_findings_never_toast_though_the_mark_passes_them()
    {
        var window = new[] { Finding("m", 10, AuditSeverity.Medium), Finding("l", 11, AuditSeverity.Low) };

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5)), AlertToastPhase.Live);

        Assert.Null(decision.Toast);
        Assert.Equal(Nanos(11), decision.HighWaterUnixNano);
    }

    // ------------------------------------------------------------------ live toasts: severity and target only

    [Fact]
    public void A_single_live_finding_is_told_as_its_severity_and_its_target_and_nothing_else()
    {
        var decision = AlertToastPolicy.Decide(new[] { Finding("a", 10, AuditSeverity.High, target: @"C:\work\app\build.ps1") }, Settings(mark: Nanos(5)), AlertToastPhase.Live);

        var toast = decision.Toast!;
        Assert.Equal("HIGH finding", toast.Title);
        Assert.Equal(@"Target: C:\work\app\build.ps1", toast.Body);
        Assert.Equal(AuditSeverity.High, toast.Floor);
        Assert.Equal(new AlertsFilter(AuditSeverity.High), toast.Filter);
    }

    [Fact]
    public void A_finding_with_no_target_sends_the_operator_to_alerts_instead_of_printing_nothing()
    {
        var none = AlertToastPolicy.Decide(new[] { Finding("a", 10, AuditSeverity.Critical, target: null) }, Settings(mark: Nanos(5)), AlertToastPhase.Live);
        var blank = AlertToastPolicy.Decide(new[] { Finding("a", 10, AuditSeverity.Critical, target: "  \r\n ") }, Settings(mark: Nanos(5)), AlertToastPhase.Live);

        Assert.Equal("Open Alerts for detail.", none.Toast!.Body);
        Assert.Equal("Open Alerts for detail.", blank.Toast!.Body);
    }

    [Fact]
    public void Several_live_findings_are_one_counted_toast_not_a_balloon_each()
    {
        var window = Enumerable.Range(10, 40).Select(i => Finding("f" + i, i, i % 2 == 0 ? AuditSeverity.Critical : AuditSeverity.High)).ToArray();

        var decision = AlertToastPolicy.Decide(window, Settings(mark: Nanos(5)), AlertToastPhase.Live);

        var toast = decision.Toast!;
        Assert.Equal("New findings", toast.Title);
        Assert.Equal("20 new CRITICAL and 20 new HIGH findings — open Alerts for detail.", toast.Body);
        Assert.Equal(AuditSeverity.High, toast.Floor);
    }

    [Fact]
    public void A_target_is_one_printable_line_cut_to_the_limit()
    {
        Assert.Equal("Target: a b c", AlertToastPolicy.TargetLine("a\r\nb\t\tc\u0007"));

        var line = AlertToastPolicy.TargetLine(new string('x', 500));
        Assert.Equal("Target: ".Length + AlertToastPolicy.TargetLimit, line.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);

        Assert.Equal("Target: " + new string('y', AlertToastPolicy.TargetLimit), AlertToastPolicy.TargetLine(new string('y', AlertToastPolicy.TargetLimit)));
    }

    [Fact]
    public void Nothing_but_severity_and_target_can_reach_a_toast()
    {
        // The queue row carries an action and a connector as well; neither, and nothing of a finding's payload, is in any
        // toast's words. (The row type has no payload at all, which is the point: the policy cannot leak what it never reads.)
        var item = new AlertQueueItem("id-1", AuditSeverity.Critical, "hook_decision-SECRETACTION", "/t", "connector-SECRET", Base.AddMinutes(10));

        foreach (var phase in new[] { AlertToastPhase.Launch, AlertToastPhase.Live, AlertToastPhase.Backlog })
        {
            var toast = AlertToastPolicy.Decide(new[] { item }, Settings(mark: phase == AlertToastPhase.Backlog ? 0 : Nanos(5)), phase).Toast!;

            Assert.DoesNotContain("SECRET", toast.Title + toast.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("id-1", toast.Title + toast.Body, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ reset

    [Fact]
    public void After_a_reset_everything_outstanding_is_announced_as_one_toast_even_though_the_mark_is_zero()
    {
        var window = Enumerable.Range(1, 12).Select(i => Finding("c" + i, i, AuditSeverity.Critical))
            .Concat(Enumerable.Range(20, 88).Select(i => Finding("h" + i, i, AuditSeverity.High)))
            .ToArray();

        var decision = AlertToastPolicy.Decide(window, Settings(mark: 0), AlertToastPhase.Backlog);

        var toast = decision.Toast!;
        Assert.Equal("Unacknowledged findings", toast.Title);
        Assert.Equal("12 CRITICAL and 88 HIGH findings unacknowledged", toast.Body);
        Assert.Equal(AuditSeverity.High, toast.Floor);
        Assert.Equal(Nanos(107), decision.HighWaterUnixNano);
    }

    [Fact]
    public void After_a_reset_with_nothing_outstanding_there_is_no_toast()
    {
        var decision = AlertToastPolicy.Decide(new[] { Finding("m", 1, AuditSeverity.Medium) }, Settings(mark: 0), AlertToastPhase.Backlog);

        Assert.Null(decision.Toast);
    }
}

/// <summary>
/// "Gateway offline / recovered" toasts fire only for a gateway this session has seen reachable (the Mac's <c>wasReachable</c>),
/// once per edge, and never for the operator's own start, stop or restart.
/// </summary>
public sealed class GatewayToastTrackerTests
{
    private static GatewaySnapshot State(AppGatewayState state, string detail = "", int port = 18970) =>
        new() { State = state, Detail = detail, ApiPort = port, PolledAt = DateTimeOffset.UtcNow };

    [Fact]
    public void A_launch_into_a_stopped_gateway_is_not_news_and_neither_is_it_coming_up()
    {
        var tracker = new GatewayToastTracker();

        Assert.Null(tracker.Observe(State(AppGatewayState.Unknown), suppress: false, enabled: true));
        Assert.Null(tracker.Observe(State(AppGatewayState.GatewayStopped, "nothing is listening"), suppress: false, enabled: true));
        Assert.Null(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true));
        Assert.False(tracker.IsLost);
    }

    [Fact]
    public void Losing_a_gateway_that_was_seen_running_toasts_once_and_its_return_toasts_once()
    {
        var tracker = new GatewayToastTracker();
        Assert.Null(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true));

        var lost = tracker.Observe(State(AppGatewayState.GatewayStopped, "Gateway is not answering on 127.0.0.1:18970."), suppress: false, enabled: true);
        Assert.Equal("DefenseClaw gateway offline", lost!.Title);
        Assert.Equal("Gateway is not answering on 127.0.0.1:18970.", lost.Body);
        Assert.Equal(ToastLevel.Warning, lost.Level);
        Assert.True(tracker.IsLost);

        // Still down on the next polls: an edge, not a level.
        Assert.Null(tracker.Observe(State(AppGatewayState.GatewayStopped), suppress: false, enabled: true));
        Assert.Null(tracker.Observe(State(AppGatewayState.Degraded), suppress: false, enabled: true));

        var back = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true);
        Assert.Equal("DefenseClaw gateway recovered", back!.Title);
        Assert.Equal("The gateway is reachable again on port 18970.", back.Body);
        Assert.Equal(ToastLevel.Info, back.Level);
        Assert.False(tracker.IsLost);

        Assert.Null(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true));
    }

    [Fact]
    public void A_degraded_answer_counts_as_losing_the_gateway()
    {
        var tracker = new GatewayToastTracker();
        _ = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true);

        Assert.NotNull(tracker.Observe(State(AppGatewayState.Degraded, "the gateway answered, but not cleanly"), suppress: false, enabled: true));
    }

    [Fact]
    public void The_operators_own_gateway_action_moves_the_state_but_says_nothing()
    {
        var tracker = new GatewayToastTracker();
        _ = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true);

        // Stop: no toast, but the gateway is now known to be down...
        Assert.Null(tracker.Observe(State(AppGatewayState.GatewayStopped), suppress: true, enabled: true));
        Assert.True(tracker.IsLost);

        // ...so its return after the command has finished is announced.
        Assert.NotNull(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true));
    }

    [Fact]
    public void With_the_toggle_off_the_edges_pass_silently_and_switching_it_on_announces_only_later_ones()
    {
        var tracker = new GatewayToastTracker();
        _ = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: false);
        Assert.Null(tracker.Observe(State(AppGatewayState.GatewayStopped), suppress: false, enabled: false));
        Assert.Null(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: false));

        Assert.NotNull(tracker.Observe(State(AppGatewayState.GatewayStopped), suppress: false, enabled: true));
    }

    [Theory]
    [InlineData(AppGatewayState.NotInstalled)]
    [InlineData(AppGatewayState.NotInitialized)]
    [InlineData(AppGatewayState.WslGatewayDetected)]
    [InlineData(AppGatewayState.Unknown)]
    public void Other_states_are_not_the_native_gateway_coming_or_going(AppGatewayState state)
    {
        var tracker = new GatewayToastTracker();
        _ = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true);

        Assert.Null(tracker.Observe(State(state), suppress: false, enabled: true));
        Assert.Null(tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true));
    }

    [Fact]
    public void A_recovery_with_no_port_or_a_loss_with_no_detail_still_reads_as_a_sentence()
    {
        var tracker = new GatewayToastTracker();
        _ = tracker.Observe(State(AppGatewayState.Running), suppress: false, enabled: true);

        Assert.Equal("Lost contact with the gateway.", tracker.Observe(State(AppGatewayState.GatewayStopped, detail: " "), suppress: false, enabled: true)!.Body);
        Assert.Equal("The gateway is reachable again.", tracker.Observe(State(AppGatewayState.Running, port: 0), suppress: false, enabled: true)!.Body);
    }
}
