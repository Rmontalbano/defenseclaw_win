using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>
/// The two things CUST-352 adds to a gateway start, restart and stop: the second look a few seconds after a start that exited 0 (the 1.0 gateway
/// listens and then exits when a connector's setup fails), and the "Codex is running" warning on every review. The runner, the reviews, the polls
/// and the delay are fakes: no process starts, no window opens and nobody waits nine seconds.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GatewayLifecycleDurabilityTests : IDisposable
{
    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly List<string> _events = new();
    private readonly List<GatewayLifecycleNotice> _notices = new();
    private readonly List<(string Title, string Message)> _activity = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly List<CommandReview> _reviews = new();
    private int _exit;

    /// <summary>What the monitor says when the lifecycle polls: the state after the run, and after the wait.</summary>
    private GatewaySnapshot _current = new() { State = AppGatewayState.GatewayStopped, Install = InstallState.GatewayStopped, Detail = "stopped" };

    private Func<GatewaySnapshot>? _afterWait;
    private bool _codexRunning;
    private bool _confirm = true;

    public GatewayLifecycleDurabilityTests()
    {
        _services = TestServices.Create(_temp, "gateway:\n  api_port: 39871\n");
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private GatewayLifecycle Lifecycle(TimeSpan? check = null)
    {
        var lifecycle = new GatewayLifecycle(
            () => _current,
            _services.Installation,
            argv =>
            {
                _events.Add("run " + string.Join(' ', argv));
                var invocation = InvocationFactory.CreateFor(GatewayControl.Executable, argv.ToArray());
                InvocationFactory.Finish(invocation, _exit);
                return Task.FromResult(invocation);
            },
            () =>
            {
                _events.Add("poll");
                return Task.CompletedTask;
            },
            () => _events.Add("user-stopped"),
            administrator: null,
            codexRunning: () => _codexRunning,
            recordActivity: (title, message) => _activity.Add((title, message)))
        {
            Confirmer = (_, action) =>
            {
                _events.Add("plain-review " + action);
                return _confirm;
            },
            ReviewConfirmer = (_, review) =>
            {
                _events.Add("codex-review");
                _reviews.Add(review);
                return _confirm;
            },
            Delay = span =>
            {
                _delays.Add(span);
                _events.Add("wait");
                if (_afterWait is not null)
                {
                    _current = _afterWait();
                }

                return Task.CompletedTask;
            },
            PostStartCheck = check ?? TimeSpan.FromSeconds(9),
        };
        lifecycle.Notice += (_, notice) => _notices.Add(notice);
        return lifecycle;
    }

    private static GatewaySnapshot Up() => new() { State = AppGatewayState.Running, Install = InstallState.Running, Detail = "ok" };

    private static GatewaySnapshot Down(GatewayExitReason? reason = null) =>
        new() { State = AppGatewayState.GatewayStopped, Install = InstallState.GatewayStopped, Detail = "stopped", LastFailure = reason };

    // ---- the second look -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_start_that_is_still_up_after_the_wait_succeeds_and_says_so_only_then()
    {
        _afterWait = Up;

        await Lifecycle().RunAsync(GatewayAction.Start);

        var notice = Assert.Single(_notices);
        Assert.Equal(ToastLevel.Info, notice.Level);
        Assert.Equal("defenseclaw-gateway start succeeded.", notice.Message);
        Assert.Equal(new[] { "plain-review Start", "run start", "poll", "wait", "poll" }, _events);
        Assert.Equal(TimeSpan.FromSeconds(9), Assert.Single(_delays));
        Assert.Empty(_activity);
    }

    [Fact]
    public async Task A_start_that_is_down_again_is_a_warning_with_the_reason_and_the_succeeded_notice_never_comes()
    {
        _afterWait = () => Down(new GatewayExitReason.StaleAgentExecutable("codex", OldExe));

        await Lifecycle().RunAsync(GatewayAction.Start);

        var notice = Assert.Single(_notices);
        Assert.Equal(ToastLevel.Warning, notice.Level);
        Assert.Equal("Start gateway", notice.Title);
        Assert.Contains("succeeded, but the gateway is not running again", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Codex's executable moved after a Codex update.", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_notices, n => n.Message == "defenseclaw-gateway start succeeded.");

        var recorded = Assert.Single(_activity);
        Assert.Equal("Start gateway", recorded.Title);
        Assert.Equal(notice.Message, recorded.Message);
    }

    [Fact]
    public async Task A_restart_is_judged_the_same_way_and_a_down_gateway_with_no_reason_says_where_to_look()
    {
        _afterWait = () => Down();

        await Lifecycle().RunAsync(GatewayAction.Restart);

        var notice = Assert.Single(_notices);
        Assert.Equal(ToastLevel.Warning, notice.Level);
        Assert.Contains("defenseclaw-gateway restart succeeded, but the gateway is not running again", notice.Message, StringComparison.Ordinal);
        Assert.Contains("gateway.log", notice.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppGatewayState.Degraded)]
    [InlineData(AppGatewayState.Unknown)]
    public async Task A_gateway_that_has_not_answered_cleanly_yet_is_not_called_a_failure(AppGatewayState state)
    {
        _afterWait = () => new GatewaySnapshot { State = state, Detail = "starting" };

        await Lifecycle().RunAsync(GatewayAction.Start);

        var notice = Assert.Single(_notices);
        Assert.Equal(ToastLevel.Info, notice.Level);
        Assert.Empty(_activity);
    }

    [Fact]
    public async Task A_failed_start_and_a_stop_are_not_looked_at_again()
    {
        _exit = 1;
        await Lifecycle().RunAsync(GatewayAction.Start);
        Assert.Equal(ToastLevel.Warning, Assert.Single(_notices).Level);
        Assert.Contains("Exit code 1", _notices[0].Message, StringComparison.Ordinal);
        Assert.Empty(_delays);

        _notices.Clear();
        _exit = 0;
        _current = Up();
        await Lifecycle().RunAsync(GatewayAction.Stop);
        Assert.Equal("defenseclaw-gateway stop succeeded.", Assert.Single(_notices).Message);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task A_lifecycle_built_without_the_check_behaves_as_it_always_did()
    {
        await Lifecycle(TimeSpan.Zero).RunAsync(GatewayAction.Start);

        Assert.Equal("defenseclaw-gateway start succeeded.", Assert.Single(_notices).Message);
        Assert.Equal(new[] { "plain-review Start", "run start", "poll" }, _events);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task The_action_counts_as_running_while_it_waits_so_a_second_press_is_ignored()
    {
        var release = new TaskCompletionSource();
        var lifecycle = Lifecycle();
        lifecycle.Delay = async _ => await release.Task;
        _current = Up();

        var first = lifecycle.RunAsync(GatewayAction.Restart);
        await Task.Delay(50);
        Assert.True(lifecycle.IsRunning);
        await lifecycle.RunAsync(GatewayAction.Restart);
        Assert.Single(_events, e => e == "run restart");

        release.SetResult();
        await first;
        Assert.False(lifecycle.IsRunning);
    }

    // ---- Codex is running ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(GatewayAction.Start)]
    [InlineData(GatewayAction.Restart)]
    [InlineData(GatewayAction.Stop)]
    public async Task While_codex_runs_the_review_carries_the_warning_and_the_operator_can_still_go_ahead(GatewayAction action)
    {
        _codexRunning = true;
        _current = action == GatewayAction.Start ? Down() : Up();
        _afterWait = Up;

        await Lifecycle().RunAsync(action);

        var review = Assert.Single(_reviews);
        var warning = Assert.Single(review.Warnings);
        Assert.Equal("Codex is running", warning.Title);
        Assert.Equal(
            "Codex is running. DefenseClaw rewrites Codex's config.toml when the gateway starts, and Codex rewrites it too; a collision can leave a stuck pending write " +
            "that stops the gateway. Quit Codex first.",
            warning.Message);
        Assert.Equal(new[] { GatewayControl.Verb(action) }, review.Steps[0].Argv);
        Assert.DoesNotContain(_events, e => e.StartsWith("plain-review", StringComparison.Ordinal));
        Assert.Contains("run " + GatewayControl.Verb(action), _events);
    }

    [Fact]
    public async Task Declining_the_codex_review_runs_nothing()
    {
        _codexRunning = true;
        _confirm = false;

        await Lifecycle().RunAsync(GatewayAction.Start);

        Assert.Equal(new[] { "codex-review" }, _events);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task While_codex_is_not_running_the_review_is_the_gateway_dialog_as_before()
    {
        _codexRunning = false;
        _afterWait = Up;

        await Lifecycle().RunAsync(GatewayAction.Start);

        Assert.Empty(_reviews);
        Assert.Contains("plain-review Start", _events);
    }

    [Fact]
    public void The_real_composition_asks_the_services_probe_and_an_isolated_one_sees_no_codex()
    {
        Assert.False(_services.AgentProcesses.CodexRunning());

        _services.AgentProcesses = new AgentProcessProbe(() => new[] { "explorer", "Codex" });
        Assert.True(_services.AgentProcesses.CodexRunning());
        Assert.NotNull(_services.AgentProcesses.CodexWarning());
    }
}
