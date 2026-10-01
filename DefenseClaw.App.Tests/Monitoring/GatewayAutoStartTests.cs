using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The opt-in automatic gateway start (CUST-211) over a fake snapshot source and a fake runner: it never starts a real gateway. One
/// attempt per launch, only when the port refuses the connection and the executable is found, and every other condition leaves it alone.
/// </summary>
public sealed class GatewayAutoStartTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSnapshotSource _source = new();
    private readonly List<IReadOnlyList<string>> _runs = new();
    private readonly List<GatewayAutoStartNoticeEventArgs> _notices = new();
    private readonly List<GatewayAutoStart> _created = new();
    private int _refreshes;

    public void Dispose()
    {
        foreach (var created in _created)
        {
            created.Dispose();
        }

        _temp.Dispose();
    }

    private static GatewaySnapshot Stopped(bool refused = true) => new()
    {
        State = AppGatewayState.GatewayStopped,
        HealthStatus = DefenseClaw.Core.Gateway.GatewayStatus.Unreachable,
        PortRefused = refused,
        Install = DefenseClaw.Core.Install.InstallState.GatewayStopped,
    };

    private AppSettingsStore Settings(bool autoStart = true, bool notifyGateway = true)
    {
        var store = AppSettingsStore.OpenFresh(_temp.File("settings-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.True(store.Update(s => s with
        {
            Startup = s.Startup with { GatewayAutoStart = autoStart },
            Notifications = s.Notifications with { Gateway = notifyGateway },
        }));
        return store;
    }

    private GatewayAutoStart Build(
        AppSettingsStore? settings = null,
        bool executableFound = true,
        int exitCode = 0,
        Exception? throws = null)
    {
        var service = new GatewayAutoStart(
            settings ?? Settings(),
            _source,
            (argv, _) =>
            {
                lock (_runs)
                {
                    _runs.Add(argv.ToArray());
                }

                if (throws is not null)
                {
                    throw throws;
                }

                var invocation = InvocationFactory.Create(argv: argv.ToArray());
                InvocationFactory.Finish(invocation, exitCode);
                return Task.FromResult(invocation);
            },
            () => executableFound,
            () =>
            {
                Interlocked.Increment(ref _refreshes);
                return Task.CompletedTask;
            },
            action => action());
        service.NoticeRaised += (_, e) =>
        {
            lock (_notices)
            {
                _notices.Add(e);
            }
        };
        _created.Add(service);
        return service;
    }

    private int RunCount
    {
        get
        {
            lock (_runs)
            {
                return _runs.Count;
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }

    /// <summary>The start must not happen: gives a (never started) background attempt a moment to show itself, then checks nothing ran.</summary>
    private void AssertNeverRuns(GatewayAutoStart service)
    {
        Assert.True(service.HasDecided);
        Thread.Sleep(100);
        Assert.Equal(0, RunCount);
        Assert.Equal(GatewayAutoStartOutcome.None, service.LastOutcome);
    }

    // ------------------------------------------------------------------ the one start

    [Fact]
    public void A_refused_port_with_the_executable_found_starts_the_gateway_once_with_the_start_verb_only()
    {
        var service = Build();
        _source.Current = Stopped();

        service.Start();
        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Started, "the start");

        var run = Assert.Single(_runs);
        Assert.Equal(new[] { "start" }, run);
        Assert.Equal(1, Volatile.Read(ref _refreshes));
        Assert.Equal(GatewayControl.Argv(GatewayAction.Start), run);
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(run));
    }

    [Fact]
    public void It_decides_on_the_first_settled_snapshot_after_the_unknown_ones()
    {
        var service = Build();
        service.Start();
        _source.Publish(GatewaySnapshot.Initial);
        Assert.False(service.HasDecided);
        Assert.Equal(0, RunCount);

        _source.Publish(Stopped());
        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Started, "the start");
    }

    [Fact]
    public void Only_one_attempt_is_made_per_launch_however_many_snapshots_follow()
    {
        var service = Build();
        service.Start();
        _source.Publish(Stopped());
        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Started, "the start");

        _source.Publish(Stopped() with { Detail = "again" });
        _source.Publish(new GatewaySnapshot { State = AppGatewayState.Running });
        _source.Publish(Stopped() with { Detail = "and again" });
        Thread.Sleep(100);

        Assert.Equal(1, RunCount);
    }

    [Fact]
    public void A_failed_start_is_reported_once_and_not_retried()
    {
        var service = Build(exitCode: 1);
        service.Start();
        _source.Publish(Stopped());
        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Failed, "the failure");

        _source.Publish(Stopped() with { Detail = "still down" });
        _source.Publish(Stopped() with { Detail = "still down 2" });
        Thread.Sleep(100);

        Assert.Equal(1, RunCount);
        var notice = Assert.Single(_notices);
        Assert.True(notice.IsFailure);
        Assert.Contains("exit code 1", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_that_cannot_be_launched_is_a_failure_reported_once()
    {
        var service = Build(throws: new InvalidOperationException("no such file"));
        service.Start();
        _source.Publish(Stopped());
        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Failed, "the failure");

        Assert.Single(_notices);
        Assert.Equal(1, RunCount);
    }

    [Fact]
    public void A_successful_start_is_toasted_when_gateway_notifications_are_on_and_silent_when_off()
    {
        var loud = Build();
        loud.Start();
        _source.Publish(Stopped());
        WaitUntil(() => loud.LastOutcome == GatewayAutoStartOutcome.Started, "the start");
        var notice = Assert.Single(_notices);
        Assert.False(notice.IsFailure);

        _notices.Clear();
        _runs.Clear();
        var quietSource = new FakeSnapshotSource();
        var quiet = new GatewayAutoStart(
            Settings(notifyGateway: false),
            quietSource,
            (argv, _) =>
            {
                var invocation = InvocationFactory.Create(argv: argv.ToArray());
                InvocationFactory.Finish(invocation, 0);
                lock (_runs)
                {
                    _runs.Add(argv.ToArray());
                }

                return Task.FromResult(invocation);
            },
            () => true,
            post: a => a());
        _created.Add(quiet);
        quiet.NoticeRaised += (_, e) => _notices.Add(e);
        quiet.Start();
        quietSource.Publish(Stopped());
        WaitUntil(() => quiet.LastOutcome == GatewayAutoStartOutcome.Started, "the quiet start");

        Assert.Empty(_notices);
    }

    // ------------------------------------------------------------------ the never-start conditions

    [Fact]
    public void A_timeout_is_not_a_refusal()
    {
        var service = Build();
        service.Start();
        _source.Publish(Stopped(refused: false));

        AssertNeverRuns(service);
        Assert.Contains("refuse", service.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_degraded_gateway_is_never_started()
    {
        var service = Build();
        service.Start();

        // 401, a 5xx, a malformed /health: something answered, so the state is Degraded, whatever the refusal flag says.
        _source.Publish(new GatewaySnapshot
        {
            State = AppGatewayState.Degraded,
            HealthStatus = DefenseClaw.Core.Gateway.GatewayStatus.Unauthorized,
            PortRefused = false,
        });

        AssertNeverRuns(service);
    }

    [Fact]
    public void A_degraded_state_is_never_started_even_if_the_flag_were_set()
    {
        var service = Build();
        service.Start();
        _source.Publish(new GatewaySnapshot { State = AppGatewayState.Degraded, PortRefused = true });

        AssertNeverRuns(service);
    }

    [Fact]
    public void A_wsl_relay_holding_the_port_is_never_started_over()
    {
        var service = Build();
        service.Start();
        _source.Publish(new GatewaySnapshot { State = AppGatewayState.WslGatewayDetected, WslGatewayDetected = true, PortRefused = true });

        AssertNeverRuns(service);
        Assert.Contains("WSL", service.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_snapshot_that_names_a_wsl_relay_is_not_started_either()
    {
        var service = Build();
        service.Start();
        _source.Publish(Stopped() with { WslGatewayDetected = true });

        AssertNeverRuns(service);
    }

    [Fact]
    public void Paused_monitoring_never_starts_it()
    {
        var service = Build();
        service.Start();
        _source.Publish(Stopped() with { IsPaused = true });

        AssertNeverRuns(service);
        Assert.Contains("paused", service.LastReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AppGatewayState.NotInstalled)]
    [InlineData(AppGatewayState.NotInitialized)]
    [InlineData(AppGatewayState.Running)]
    public void Only_a_stopped_gateway_is_started(AppGatewayState state)
    {
        var service = Build();
        service.Start();
        _source.Publish(new GatewaySnapshot { State = state, PortRefused = true });

        AssertNeverRuns(service);
    }

    [Fact]
    public void A_missing_executable_means_no_attempt()
    {
        var service = Build(executableFound: false);
        service.Start();
        _source.Publish(Stopped());

        WaitUntil(() => service.LastReason.Contains("not found", StringComparison.Ordinal), "the lookup");
        Assert.Equal(0, RunCount);
        Assert.Equal(GatewayAutoStartOutcome.None, service.LastOutcome);
    }

    [Fact]
    public void The_setting_off_never_starts_it()
    {
        var service = Build(Settings(autoStart: false));
        service.Start();
        _source.Publish(Stopped());

        AssertNeverRuns(service);
    }

    [Fact]
    public void Turning_the_setting_on_after_the_first_look_does_not_start_it_this_launch()
    {
        var settings = Settings(autoStart: false);
        var service = Build(settings);
        service.Start();
        _source.Publish(Stopped());
        Assert.True(service.HasDecided);

        Assert.True(settings.Update(s => s with { Startup = s.Startup with { GatewayAutoStart = true } }));
        _source.Publish(Stopped() with { Detail = "later" });

        AssertNeverRuns(service);
    }

    [Fact]
    public void A_manual_stop_wins_for_the_session()
    {
        var service = Build();
        service.MarkUserStopped();
        service.Start();
        _source.Publish(Stopped());

        Assert.True(service.UserStopped);
        AssertNeverRuns(service);
        Assert.Contains("stopped by the operator", service.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_already_current_when_it_starts_is_considered_at_once()
    {
        _source.Current = Stopped();
        var service = Build();

        service.Start();

        WaitUntil(() => service.LastOutcome == GatewayAutoStartOutcome.Started, "the start");
    }

    [Fact]
    public void A_disposed_service_makes_no_attempt()
    {
        var service = Build();
        service.Start();
        service.Dispose();

        _source.Publish(Stopped());

        Assert.False(service.HasDecided);
        Assert.Equal(0, RunCount);
    }

    // ------------------------------------------------------------------ the consent review

    [Fact]
    public void The_consent_review_is_the_exact_start_command_with_the_policy_text()
    {
        var review = GatewayAutoStart.ConsentReview();

        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw-gateway", step.Executable);
        Assert.Equal(new[] { "start" }, step.Argv);
        Assert.Equal(CommandTier.StateChanging, step.Tier);
        Assert.Equal(GatewayAutoStart.PolicyText, review.Summary);
        Assert.Contains("times out", review.Summary, StringComparison.Ordinal);
        Assert.Contains("WSL", review.Summary, StringComparison.Ordinal);
        Assert.Contains("paused", review.Summary, StringComparison.Ordinal);
        Assert.Contains("stop the gateway yourself", review.Summary, StringComparison.Ordinal);
    }
}

/// <summary>The monitor's <see cref="GatewaySnapshot.PortRefused"/>: true only for a stopped gateway whose port refused the connection.</summary>
public sealed class GatewaySnapshotRefusalTests
{
    private const string Bin = @"C:\fake\install\bin";

    private static AppServices Create(TempDirectory temp, int port)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {port}\n");
        var paths = new DefenseClaw.Core.Paths.DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        return AppServices.CreateIsolated(paths, temp.File("claude-settings.json"));
    }

    [Fact]
    public async Task A_closed_port_is_stopped_and_refused()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        using var temp = new TempDirectory();
        using var services = Create(temp, port);
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.Equal(AppGatewayState.GatewayStopped, snapshot.State);
        Assert.True(snapshot.PortRefused);
    }

    [Fact]
    public async Task Something_answering_on_the_port_is_not_refused()
    {
        using var temp = new TempDirectory();
        using var listener = new RawHttpListener(_ => RawHttpListener.Response(401, "{}"));
        using var services = Create(temp, listener.Port);
        using var monitor = new GatewayMonitor(services, new ManualClock());

        var snapshot = await monitor.RefreshAsync();

        Assert.NotEqual(AppGatewayState.GatewayStopped, snapshot.State);
        Assert.False(snapshot.PortRefused);
    }
}
