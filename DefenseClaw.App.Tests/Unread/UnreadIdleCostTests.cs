using System.Reflection;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// The "no idle-CPU regression" acceptance of the sidebar's "new since last visit" counts (CUST-265), proved with counters rather than with
/// timings: the service owns no timer, thread or watcher; it is attached to nothing until the window listens and can be seen; each alert tick
/// costs one probe sample and, only if the database changed, one bounded statement; and nothing it does is counted by - or adds to - the
/// readers the rest of the app already runs. Every number here is a count of calls, so it means the same on a slow machine as on a fast one.
/// </summary>
public sealed class UnreadIdleCostTests : IDisposable
{
    private const string Audit = UnreadCountsService.AuditId;

    private readonly List<UnreadScene> _scenes = new();

    public void Dispose()
    {
        foreach (var scene in _scenes)
        {
            scene.Dispose();
        }
    }

    private UnreadScene Scene(int auditRows = 0)
    {
        var scene = new UnreadScene(auditRows);
        _scenes.Add(scene);
        return scene;
    }

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>One alert tick, and the wait for the pass it starts (a tick that finds a pass still running is skipped, so the next is not sent until this one is done).</summary>
    private static async Task TickAsync(UnreadScene scene)
    {
        await UnreadScene.WaitUntilAsync(() => !scene.Service.IsPassRunning, "the pass before it to finish");
        var before = scene.Service.PassCount;
        scene.Tick.Tick();
        await UnreadScene.WaitUntilAsync(() => scene.Service.PassCount > before, "the pass the tick started");
        await UnreadScene.WaitUntilAsync(() => !scene.Service.IsPassRunning, "the pass the tick started to finish");
    }

    // ------------------------------------------------------------------ nothing is owned

    [Fact]
    public void The_service_and_its_reader_own_no_timer_no_thread_and_no_watcher()
    {
        var forbidden = new[]
        {
            typeof(System.Threading.Timer),
            typeof(System.Timers.Timer),
            typeof(DispatcherTimer),
            typeof(PeriodicTimer),
            typeof(Thread),
            typeof(FileSystemWatcher),
        };

        foreach (var type in new[] { typeof(UnreadCountsService), typeof(AuditHeadReader), typeof(AiDiscoveryHead), typeof(SeenSettings) })
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                Assert.DoesNotContain(forbidden, kind => kind.IsAssignableFrom(field.FieldType));
            }
        }
    }

    // ------------------------------------------------------------------ nothing is attached until the window listens

    [Fact]
    public void Nothing_subscribed_means_nothing_attached_however_often_the_monitor_ticks_and_runs_start()
    {
        var scene = Scene(auditRows: 5);

        Assert.False(scene.Service.IsRunning);
        Assert.Equal(0, scene.Tick.CadenceSubscribers);
        Assert.Equal(0, Subscribers(scene.Services.Cli, "InvocationStarted"));

        for (var i = 0; i < 5; i++)
        {
            scene.Tick.Tick();
        }

        scene.RunCommands(3);

        Assert.Equal(0, scene.Service.PassCount);
        Assert.Equal(0, scene.Reader.ReadCount);
        Assert.Equal(0, scene.Reader.StatementCount);
        Assert.Equal(0, scene.CountOf(UnreadCountsService.ActivityId));
    }

    [Fact]
    public async Task Subscribing_attaches_to_exactly_the_alert_tick_and_the_runners_start_event_and_nothing_else()
    {
        var scene = Scene(auditRows: 5);

        await scene.StartAsync();

        Assert.Equal(1, scene.Tick.CadenceSubscribers);
        Assert.Equal(0, scene.Tick.StateChangedSubscribers);
        Assert.Equal(1, Subscribers(scene.Services.Cli, "InvocationStarted"));
        Assert.Equal(0, Subscribers(scene.Services.Cli, "InvocationCompleted"));
        Assert.Equal(0, Subscribers(scene.Services.Cli, "OutputReceived"));
    }

    [Fact]
    public async Task A_window_that_cannot_be_seen_has_nothing_attached_and_no_tick_costs_anything()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();
        var passes = scene.Service.PassCount;

        scene.Service.SetInteractive(false);

        // The tray: not running, detached from both, and the hidden window's ticks and runs are not counted by anything.
        Assert.False(scene.Service.IsRunning);
        Assert.Equal(0, scene.Tick.CadenceSubscribers);
        Assert.Equal(0, Subscribers(scene.Services.Cli, "InvocationStarted"));

        var reads = scene.Reader.ReadCount;
        var statements = scene.Reader.StatementCount;
        scene.AddAuditRows(3);
        for (var i = 0; i < 10; i++)
        {
            scene.Tick.Tick();
        }

        scene.RunCommands(2);
        await Task.Delay(100);

        Assert.Equal(passes, scene.Service.PassCount);
        Assert.Equal(reads, scene.Reader.ReadCount);
        Assert.Equal(statements, scene.Reader.StatementCount);

        // Back in view: one catch-up pass, like every panel's, and the counts are right.
        scene.Service.SetInteractive(true);
        await UnreadScene.WaitUntilAsync(() => scene.Service.PassCount == passes + 1, "the catch-up pass");
        await UnreadScene.WaitUntilAsync(() => scene.CountOf(Audit) == 3, "the three rows that arrived while it was hidden");
        Assert.Equal(2, scene.CountOf(UnreadCountsService.ActivityId));
        Assert.Equal(1, scene.Tick.CadenceSubscribers);
    }

    [Fact]
    public async Task Leaving_the_last_subscriber_detaches_and_a_disposed_service_holds_nothing()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();

        scene.Stop();
        Assert.False(scene.Service.IsRunning);
        Assert.Equal(0, scene.Tick.CadenceSubscribers);
        Assert.Equal(0, Subscribers(scene.Services.Cli, "InvocationStarted"));

        await scene.StartAsync();
        Assert.True(scene.Service.IsRunning);

        scene.Service.Dispose();
        Assert.False(scene.Service.IsRunning);
        Assert.Equal(0, scene.Tick.CadenceSubscribers);
        Assert.Equal(0, Subscribers(scene.Services.Cli, "InvocationStarted"));

        // Disposed, a subscription is not taken.
        scene.Service.Changed += (_, _) => { };
        Assert.False(scene.Service.IsRunning);
        Assert.Equal(0, scene.Tick.CadenceSubscribers);
    }

    // ------------------------------------------------------------------ what a tick costs

    [Fact]
    public async Task Ticks_on_an_unchanged_database_cost_one_probe_sample_each_and_run_no_statement()
    {
        var scene = Scene(auditRows: 25);
        var probe = scene.Services.AuditChanges;
        await scene.StartAsync();

        var statements = scene.Reader.StatementCount;
        var unchanged = scene.Reader.UnchangedReads;
        var reads = scene.Reader.ReadCount;
        var samples = probe.SampleCount;
        var opens = probe.OpenCount;
        var passes = scene.Service.PassCount;
        const int ticks = 12;

        for (var i = 0; i < ticks; i++)
        {
            await TickAsync(scene);
        }

        // Twelve ticks, twelve passes, twelve probe samples: nothing was committed, so not one statement ran.
        Assert.Equal(passes + ticks, scene.Service.PassCount);
        Assert.Equal(reads + ticks, scene.Reader.ReadCount);
        Assert.Equal(samples + ticks, probe.SampleCount);
        Assert.Equal(statements, scene.Reader.StatementCount);
        Assert.Equal(unchanged + ticks, scene.Reader.UnchangedReads);
        Assert.Equal(opens, probe.OpenCount);
        Assert.Equal(0, scene.CountOf(Audit));
    }

    [Fact]
    public async Task A_commit_makes_the_next_tick_run_one_statement_and_the_one_after_none()
    {
        var scene = Scene(auditRows: 25);
        await scene.StartAsync();
        var statements = scene.Reader.StatementCount;

        scene.AddAuditRows(3);
        await TickAsync(scene);

        Assert.Equal(statements + 1, scene.Reader.StatementCount);
        Assert.Equal(3, scene.CountOf(Audit));

        await TickAsync(scene);
        await TickAsync(scene);

        Assert.Equal(statements + 1, scene.Reader.StatementCount);
        Assert.Equal(3, scene.CountOf(Audit));
    }

    [Fact]
    public async Task A_tick_adds_nothing_to_the_readers_the_rest_of_the_app_runs()
    {
        var scene = Scene(auditRows: 25);
        await scene.StartAsync();
        var services = scene.Services;

        // The counters of every other reader of audit.db the composition owns (the Audit panel's, the alert queue's).
        long Others() =>
            services.Audit.PageQueryCount + services.Audit.CountQueryCount + services.Audit.RowsDecoded + services.Audit.UnchangedReads +
            services.AlertQueue.ReadCount + services.AlertQueue.RowsDecoded + services.AlertQueue.UnchangedReads;

        var before = Others();
        scene.AddAuditRows(2);
        for (var i = 0; i < 6; i++)
        {
            await TickAsync(scene);
        }

        Assert.Equal(before, Others());
        Assert.Equal(2, scene.CountOf(Audit));
    }

    [Fact]
    public async Task The_panel_on_screen_is_not_read_at_all_by_a_tick()
    {
        var scene = Scene(auditRows: 25);
        await scene.StartAsync();
        await scene.Service.PanelShown(Audit);
        var reads = scene.Reader.ReadCount;
        var samples = scene.Services.AuditChanges.SampleCount;

        scene.AddAuditRows(5);
        for (var i = 0; i < 4; i++)
        {
            await TickAsync(scene);
        }

        Assert.Equal(reads, scene.Reader.ReadCount);
        Assert.Equal(samples, scene.Services.AuditChanges.SampleCount);
        Assert.Equal(0, scene.CountOf(Audit));
    }

    [Fact]
    public async Task A_visit_costs_one_head_read_and_none_when_the_database_did_not_move()
    {
        var scene = Scene(auditRows: 25);
        await scene.StartAsync();
        var statements = scene.Reader.StatementCount;

        // Shown and left with nothing committed in between: the same question twice, answered once.
        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Audit);
        Assert.True(scene.Reader.StatementCount <= statements + 1);

        var afterFirstVisit = scene.Reader.StatementCount;
        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Audit);
        Assert.Equal(afterFirstVisit, scene.Reader.StatementCount);
    }

    [Fact]
    public async Task Activity_is_counted_with_no_read_of_any_kind()
    {
        var scene = Scene(auditRows: 25);
        await scene.StartAsync();
        var reads = scene.Reader.ReadCount;
        var samples = scene.Services.AuditChanges.SampleCount;

        scene.RunCommands(20);

        Assert.Equal(20, scene.CountOf(UnreadCountsService.ActivityId));
        Assert.Equal(reads, scene.Reader.ReadCount);
        Assert.Equal(samples, scene.Services.AuditChanges.SampleCount);
    }

    [Fact]
    public async Task A_burst_of_ticks_costs_at_most_one_pass_each_and_leaves_nothing_queued_behind()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();
        var passes = scene.Service.PassCount;
        var statements = scene.Reader.StatementCount;

        // 200 ticks in a tight loop (the real ones are 30 s apart): a tick that finds a pass reading is skipped, one that does not starts one.
        for (var i = 0; i < 200; i++)
        {
            scene.Tick.Tick();
        }

        await UnreadScene.WaitUntilAsync(() => scene.Service.PassCount > passes && !scene.Service.IsPassRunning, "the burst's passes to finish");
        await Task.Delay(100);
        await UnreadScene.WaitUntilAsync(() => !scene.Service.IsPassRunning, "nothing to be left running");

        Assert.InRange(scene.Service.PassCount - passes, 1, 200);

        // Whatever ran, the database had not changed: not one statement.
        Assert.Equal(statements, scene.Reader.StatementCount);
    }

    // ------------------------------------------------------------------ the composition

    [Fact]
    public void The_real_composition_is_idle_until_the_window_listens_and_idle_again_when_it_stops()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        Assert.False(services.UnreadCounts.IsRunning);
        Assert.Equal(0, services.Monitor.AlertCadenceSubscriberCount);
        Assert.Equal(0, Subscribers(services.Cli, "InvocationStarted"));

        EventHandler<UnreadChangedEventArgs> handler = (_, _) => { };
        services.UnreadCounts.Changed += handler;
        Assert.True(services.UnreadCounts.IsRunning);
        Assert.Equal(1, services.Monitor.AlertCadenceSubscriberCount);
        Assert.Equal(1, Subscribers(services.Cli, "InvocationStarted"));

        services.UnreadCounts.SetInteractive(false);
        Assert.Equal(0, services.Monitor.AlertCadenceSubscriberCount);

        services.UnreadCounts.SetInteractive(true);
        services.UnreadCounts.Changed -= handler;
        Assert.False(services.UnreadCounts.IsRunning);
        Assert.Equal(0, services.Monitor.AlertCadenceSubscriberCount);
        Assert.Equal(0, Subscribers(services.Cli, "InvocationStarted"));
    }
}
