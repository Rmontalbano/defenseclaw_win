using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// The sidebar's "new since last visit" counts (CUST-265), per panel, over fixtures: a real <c>audit.db</c> built from the real DDL with invented
/// rows, the real runner's in-memory list, and the state file's component times. What each count is made of, when its marker moves, that it is
/// hidden on the panel on screen and capped at 99, and - the acceptance - that a new audit row raises the Audit count, opening the panel clears
/// it, and the marker survives a restart.
/// </summary>
public sealed class UnreadCountsServiceTests : IDisposable
{
    private const string Audit = UnreadCountsService.AuditId;
    private const string Activity = UnreadCountsService.ActivityId;
    private const string Ai = UnreadCountsService.AiDiscoveryId;

    /// <summary>A fixed instant for the scenes that count AI Discovery components, whose times are written into the test and not stamped by the runner.</summary>
    private static readonly DateTimeOffset Fixed = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly List<UnreadScene> _scenes = new();

    public void Dispose()
    {
        foreach (var scene in _scenes)
        {
            scene.Dispose();
        }
    }

    private UnreadScene Scene(int auditRows = 0, bool withProbe = true)
    {
        var scene = new UnreadScene(auditRows, withProbe);
        _scenes.Add(scene);
        return scene;
    }

    /// <summary>A file in which every component was first seen well before <paramref name="now"/>: nothing in it is new.</summary>
    private static AiDiscoveryHead OldFile(DateTimeOffset now) => new(new[] { now.AddDays(-30).UtcTicks, now.AddDays(-3).UtcTicks });

    // ================================================================== Audit

    [Fact]
    public async Task A_panel_never_looked_at_gets_its_marker_at_first_sight_and_its_history_is_not_called_new()
    {
        var scene = Scene(auditRows: 40);

        await scene.StartAsync();

        Assert.Equal(0, scene.CountOf(Audit));
        Assert.True(scene.Service.TryGetMarker(Audit, out var marker));
        Assert.Equal(40, marker);

        // It is written down, so the next run starts from the same place.
        await scene.Service.Persisted;
        Assert.True(AppSettingsStore.OpenFresh(scene.SettingsPath).Current.Seen.TryGet(Audit, out var saved));
        Assert.Equal(40, saved);
    }

    [Fact]
    public async Task A_new_audit_row_raises_the_badge_and_opening_the_panel_clears_it()
    {
        var scene = Scene(auditRows: 10);
        await scene.StartAsync();
        Assert.Equal(0, scene.CountOf(Audit));

        scene.AddAuditRows(1);
        await scene.PassAsync();

        Assert.Equal(1, scene.CountOf(Audit));
        Assert.Equal(1, scene.Published[^1][Audit]);

        scene.AddAuditRows(2);
        await scene.PassAsync();
        Assert.Equal(3, scene.CountOf(Audit));

        // Opened: gone at once, and the marker is the head.
        await scene.Service.PanelShown(Audit);
        Assert.Equal(0, scene.CountOf(Audit));
        Assert.Equal(0, scene.Published[^1][Audit]);
        Assert.True(scene.Service.TryGetMarker(Audit, out var marker));
        Assert.Equal(13, marker);
    }

    [Fact]
    public async Task The_panel_on_screen_is_not_counted_and_what_arrived_while_it_was_open_was_seen_when_it_is_left()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();
        await scene.Service.PanelShown(Audit);

        var reads = scene.Reader.ReadCount;
        scene.AddAuditRows(4);
        await scene.PassAsync();

        // Hidden on the panel on screen, and not even read: the panel shows its own rows, live.
        Assert.Equal(0, scene.CountOf(Audit));
        Assert.Equal(reads, scene.Reader.ReadCount);

        await scene.Service.PanelLeft(Audit);
        await scene.PassAsync();

        // The four rows were on the panel that was open; leaving does not turn them into "new".
        Assert.Equal(0, scene.CountOf(Audit));
        Assert.True(scene.Service.TryGetMarker(Audit, out var marker));
        Assert.Equal(9, marker);
    }

    [Fact]
    public async Task Rows_that_arrive_while_the_panel_is_closed_are_counted_and_the_next_visit_clears_them()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();
        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Audit);

        scene.AddAuditRows(3);
        await scene.PassAsync();
        Assert.Equal(3, scene.CountOf(Audit));

        await scene.Service.PanelShown(Audit);
        Assert.Equal(0, scene.CountOf(Audit));
        await scene.Service.PanelLeft(Audit);
        await scene.PassAsync();
        Assert.Equal(0, scene.CountOf(Audit));

        scene.AddAuditRows(1);
        await scene.PassAsync();
        Assert.Equal(1, scene.CountOf(Audit));
    }

    [Fact]
    public async Task The_count_is_capped_one_past_99_so_the_capsule_reads_99_plus_and_stops_growing()
    {
        var scene = Scene(auditRows: 3);
        await scene.StartAsync();

        scene.AddAuditRows(99);
        await scene.PassAsync();
        Assert.Equal(99, scene.CountOf(Audit));
        Assert.Equal("99", UnreadPresentation.Badge(scene.CountOf(Audit)));

        scene.AddAuditRows(1);
        await scene.PassAsync();
        Assert.Equal(100, scene.CountOf(Audit));
        Assert.Equal("99+", UnreadPresentation.Badge(scene.CountOf(Audit)));

        scene.AddAuditRows(250);
        await scene.PassAsync();
        Assert.Equal(UnreadCountsService.CountLimit, scene.CountOf(Audit));
        Assert.Equal("99+ new since last visit", UnreadPresentation.Sentence(scene.CountOf(Audit)));
    }

    [Fact]
    public async Task A_row_committed_late_with_an_old_timestamp_counts_because_the_marker_is_the_order_of_insertion()
    {
        var scene = Scene(auditRows: 8);
        await scene.StartAsync();

        scene.AddLateRow();
        await scene.PassAsync();

        Assert.Equal(1, scene.CountOf(Audit));
    }

    [Fact]
    public async Task The_marker_survives_a_restart_and_the_rows_that_arrived_while_the_app_was_closed_are_new()
    {
        var scene = Scene(auditRows: 12);
        await scene.StartAsync();

        // A visit: shown and left.
        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Audit);
        await scene.Service.Persisted;
        scene.Service.Dispose();

        // The gateway keeps writing while DefenseClaw for Windows is not running.
        scene.AddAuditRows(6);

        // The next run: a new process, a store that shares nothing with the first, a service that has never seen the database.
        var next = scene.Restart(out var store);
        Assert.True(store.Current.Seen.TryGet(Audit, out var remembered));
        Assert.Equal(12, remembered);

        await scene.StartAsync(next);

        Assert.Equal(6, next.CountOf(Audit));
        Assert.True(next.TryGetMarker(Audit, out var marker));
        Assert.Equal(12, marker);
    }

    [Fact]
    public async Task A_marker_that_moves_on_a_visit_is_what_the_next_run_starts_from()
    {
        var scene = Scene(auditRows: 4);
        await scene.StartAsync();
        scene.AddAuditRows(5);
        await scene.PassAsync();
        Assert.Equal(5, scene.CountOf(Audit));

        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Audit);
        await scene.Service.Persisted;
        scene.Service.Dispose();

        var next = scene.Restart(out _);
        await scene.StartAsync(next);

        Assert.Equal(0, next.CountOf(Audit));
    }

    [Fact]
    public async Task A_table_that_started_again_is_a_fresh_start_not_a_phantom_count()
    {
        var scene = Scene(auditRows: 30);
        await scene.StartAsync();
        Assert.True(scene.Service.TryGetMarker(Audit, out var before));
        Assert.Equal(30, before);

        // Emptied and refilled (or renumbered by a VACUUM): the head is below the marker, and there is no way to say which rows are new.
        scene.Exec("DELETE FROM audit_events");
        scene.AddAuditRows(3);
        await scene.PassAsync();

        Assert.Equal(0, scene.CountOf(Audit));
        Assert.True(scene.Service.TryGetMarker(Audit, out var after));
        Assert.Equal(3, after);

        scene.AddAuditRows(2);
        await scene.PassAsync();
        Assert.Equal(2, scene.CountOf(Audit));
    }

    [Fact]
    public async Task No_database_yet_is_nothing_new_and_the_rows_that_appear_are_counted_from_the_first_look()
    {
        var scene = Scene();
        File.Delete(scene.AuditPath);
        await scene.StartAsync();
        Assert.Equal(0, scene.CountOf(Audit));
        Assert.False(scene.Service.TryGetMarker(Audit, out _));

        // The gateway creates it with its first events.
        AuditTestDatabase.Create(scene.AuditPath, 0);
        scene.AddAuditRows(3);
        await scene.PassAsync();

        // First sight of the table: its head becomes the marker (these three are the history), and the next row is news.
        Assert.Equal(0, scene.CountOf(Audit));
        scene.AddAuditRows(1);
        await scene.PassAsync();
        Assert.Equal(1, scene.CountOf(Audit));
    }

    [Fact]
    public async Task A_database_that_cannot_be_read_keeps_the_last_count_instead_of_calling_it_all_clear()
    {
        var scene = Scene(auditRows: 5, withProbe: false);
        await scene.StartAsync();
        scene.AddAuditRows(2);
        await scene.PassAsync();
        Assert.Equal(2, scene.CountOf(Audit));
        var announced = scene.PublishedCount;

        // Somebody replaces the database with junk (or it is mid-write): the read fails.
        SqlitePools.Release(scene.Temp.Path);
        File.WriteAllBytes(scene.AuditPath, Enumerable.Repeat((byte)0xFF, 8192).ToArray());

        await scene.PassAsync();

        Assert.Equal(2, scene.CountOf(Audit));
        Assert.Equal(announced, scene.PublishedCount);
    }

    [Fact]
    public async Task Two_visits_in_a_row_leave_the_marker_at_the_later_head_whichever_read_finishes_last()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();

        var shown = scene.Service.PanelShown(Audit);
        scene.AddAuditRows(3);
        var left = scene.Service.PanelLeft(Audit);
        await Task.WhenAll(shown, left);

        // The second question was asked after the three rows were committed; its answer is the one that stands.
        Assert.True(scene.Service.TryGetMarker(Audit, out var marker));
        Assert.Equal(8, marker);
        await scene.PassAsync();
        Assert.Equal(0, scene.CountOf(Audit));
    }

    [Fact]
    public async Task The_capsule_never_flashes_what_arrived_during_a_visit_while_the_visit_ends()
    {
        var scene = Scene(auditRows: 5);
        await scene.StartAsync();

        for (var i = 0; i < 15; i++)
        {
            await scene.Service.PanelShown(Audit);
            scene.AddAuditRows(2);

            // Left, its head read still in flight - and a tick's pass lands in the same moment. The marker is about to jump over the two rows, so
            // the sidebar must not say "2 new" for the instant in between.
            var left = scene.Service.PanelLeft(Audit);
            await scene.PassAsync();
            await left;
        }

        lock (scene.Published)
        {
            Assert.All(scene.Published, counts => Assert.Equal(0, counts[Audit]));
        }

        Assert.Equal(0, scene.CountOf(Audit));
    }

    // ================================================================== Activity

    [Fact]
    public async Task Activity_counts_the_runs_in_the_runners_list_that_started_after_the_last_visit()
    {
        var scene = Scene();
        await scene.StartAsync();

        // Never visited: everything in this session's list is new.
        Assert.Equal(0, scene.CountOf(Activity));
        scene.RunCommands(3);
        Assert.Equal(3, scene.CountOf(Activity));
        Assert.Equal(3, scene.Published[^1][Activity]);

        await UnreadScene.ClockMovesOnAsync();
        await scene.Service.PanelShown(Activity);
        await scene.Service.PanelLeft(Activity);
        Assert.Equal(0, scene.CountOf(Activity));
        Assert.True(scene.Service.TryGetMarker(Activity, out _));

        await UnreadScene.ClockMovesOnAsync();
        scene.RunCommands(2);
        Assert.Equal(2, scene.CountOf(Activity));
    }

    [Fact]
    public async Task A_run_raises_the_count_at_once_through_the_runners_own_start_event_not_on_a_tick()
    {
        var scene = Scene();
        await scene.StartAsync();
        await scene.Service.PanelShown(Activity);
        await scene.Service.PanelLeft(Activity);
        await UnreadScene.ClockMovesOnAsync();
        var passes = scene.Service.PassCount;

        scene.RunCommands(1);

        Assert.Equal(1, scene.CountOf(Activity));
        Assert.Equal(passes, scene.Service.PassCount);
    }

    [Fact]
    public async Task The_panel_on_screen_shows_no_count_and_what_ran_while_it_was_open_was_seen()
    {
        var scene = Scene();
        await scene.StartAsync();
        await scene.Service.PanelShown(Activity);
        await UnreadScene.ClockMovesOnAsync();

        scene.RunCommands(3);
        Assert.Equal(0, scene.CountOf(Activity));

        // Left: the marker is the moment of leaving, which is after those runs.
        await UnreadScene.ClockMovesOnAsync();
        await scene.Service.PanelLeft(Activity);
        Assert.Equal(0, scene.CountOf(Activity));
    }

    [Fact]
    public async Task Clearing_the_list_takes_the_count_down_at_the_next_recount()
    {
        var scene = Scene();
        await scene.StartAsync();
        scene.RunCommands(5);
        Assert.Equal(5, scene.CountOf(Activity));

        scene.Services.Cli.ClearActivity();
        await scene.PassAsync();

        Assert.Equal(0, scene.CountOf(Activity));
    }

    [Fact]
    public async Task More_than_99_runs_read_99_plus()
    {
        var scene = Scene();
        await scene.StartAsync();

        scene.RunCommands(150);

        Assert.Equal(UnreadCountsService.CountLimit, scene.CountOf(Activity));
        Assert.Equal("99+", UnreadPresentation.Badge(scene.CountOf(Activity)));
    }

    [Fact]
    public async Task The_activity_marker_survives_a_restart_and_the_new_sessions_runs_are_after_it()
    {
        var scene = Scene();
        await scene.StartAsync();
        scene.RunCommands(3);
        await UnreadScene.ClockMovesOnAsync();
        await scene.Service.PanelShown(Activity);
        await scene.Service.PanelLeft(Activity);
        await scene.Service.Persisted;
        Assert.True(scene.Service.TryGetMarker(Activity, out var visited));
        scene.Service.Dispose();

        // The list is in memory: a new run starts empty. The marker is not.
        scene.Services.Cli.ClearActivity();
        var next = scene.Restart(out var store);
        Assert.True(store.Current.Seen.TryGet(Activity, out var remembered));
        Assert.Equal(visited, remembered);

        await scene.StartAsync(next);
        Assert.Equal(0, next.CountOf(Activity));

        await UnreadScene.ClockMovesOnAsync();
        scene.RunCommands(2);
        Assert.Equal(2, next.CountOf(Activity));
    }

    // ================================================================== AI Discovery

    [Fact]
    public async Task Nothing_reported_yet_is_nothing_to_say()
    {
        var scene = Scene();
        await scene.StartAsync();

        Assert.Equal(0, scene.CountOf(Ai));
        Assert.False(scene.Service.TryGetMarker(Ai, out _));
    }

    [Fact]
    public async Task The_first_report_sets_a_marker_instead_of_calling_everything_in_the_file_new()
    {
        var scene = Scene();
        scene.Now = Fixed;
        await scene.StartAsync();

        scene.Service.ReportAiDiscovery(OldFile(Fixed));

        Assert.Equal(0, scene.CountOf(Ai));
        Assert.True(scene.Service.TryGetMarker(Ai, out var marker));
        Assert.Equal(Fixed.UtcTicks, marker);
    }

    [Fact]
    public async Task A_component_first_seen_after_the_marker_is_new_until_the_panel_has_shown_it()
    {
        var scene = Scene();
        scene.Now = Fixed;
        await scene.StartAsync();
        scene.Service.ReportAiDiscovery(OldFile(Fixed));
        Assert.Equal(0, scene.CountOf(Ai));

        // A scan finds two things after the baseline; the Overview's next read of the file reports them.
        scene.Service.ReportAiDiscovery(new AiDiscoveryHead(new[] { Fixed.AddDays(-3).UtcTicks, Fixed.AddMinutes(10).UtcTicks, Fixed.AddMinutes(20).UtcTicks }));

        Assert.Equal(2, scene.CountOf(Ai));
        Assert.Equal(2, scene.Published[^1][Ai]);
    }

    [Fact]
    public async Task What_the_panel_shows_is_what_has_been_seen_and_something_found_after_its_last_load_is_still_new()
    {
        var scene = Scene();
        scene.Now = Fixed;
        await scene.StartAsync();
        scene.Service.ReportAiDiscovery(new AiDiscoveryHead(new[] { Fixed.AddMinutes(5).UtcTicks, Fixed.AddMinutes(30).UtcTicks }));
        Assert.Equal(2, scene.CountOf(Ai));

        // The panel opens and loads the file as it was ten minutes in: it lists the first component, not yet the second.
        await scene.Service.PanelShown(Ai);
        scene.Service.NoteAiDiscoveryDisplayed(Fixed.AddMinutes(10));
        Assert.Equal(0, scene.CountOf(Ai));
        Assert.True(scene.Service.TryGetMarker(Ai, out var marker));
        Assert.Equal(Fixed.AddMinutes(10).UtcTicks, marker);

        // It is left without another load: the second component was never on the panel, so it is still new.
        await scene.Service.PanelLeft(Ai);
        Assert.Equal(1, scene.CountOf(Ai));
        Assert.Equal(1, scene.Published[^1][Ai]);
    }

    [Fact]
    public async Task The_ai_marker_only_moves_forward_so_an_older_load_does_not_take_back_the_baseline()
    {
        var scene = Scene();
        scene.Now = Fixed;
        await scene.StartAsync();
        scene.Service.ReportAiDiscovery(OldFile(Fixed));
        Assert.True(scene.Service.TryGetMarker(Ai, out var baseline));

        await scene.Service.PanelShown(Ai);
        scene.Service.NoteAiDiscoveryDisplayed(Fixed.AddHours(-5));
        await scene.Service.PanelLeft(Ai);

        Assert.True(scene.Service.TryGetMarker(Ai, out var after));
        Assert.Equal(baseline, after);
    }

    [Fact]
    public async Task The_ai_marker_survives_a_restart_and_a_component_found_while_closed_is_new_at_the_next_report()
    {
        var scene = Scene();
        scene.Now = Fixed;
        await scene.StartAsync();
        scene.Service.ReportAiDiscovery(OldFile(Fixed));
        await scene.Service.PanelShown(Ai);
        scene.Service.NoteAiDiscoveryDisplayed(Fixed.AddMinutes(1));
        await scene.Service.PanelLeft(Ai);
        await scene.Service.Persisted;
        scene.Service.Dispose();

        scene.Now = Fixed.AddDays(1);
        var next = scene.Restart(out var store);
        Assert.True(store.Current.Seen.TryGet(Ai, out var remembered));
        Assert.Equal(Fixed.AddMinutes(1).UtcTicks, remembered);

        await scene.StartAsync(next);
        next.ReportAiDiscovery(new AiDiscoveryHead(new[] { Fixed.AddDays(-4).UtcTicks, Fixed.AddHours(21).UtcTicks }));

        Assert.Equal(1, next.CountOf(Ai));
    }

    // ================================================================== all three

    [Fact]
    public async Task Each_panel_is_counted_on_its_own_and_only_the_one_on_screen_is_hidden()
    {
        var scene = Scene(auditRows: 5);
        scene.Now = Fixed;
        await scene.StartAsync();
        scene.Service.ReportAiDiscovery(OldFile(Fixed));

        scene.AddAuditRows(2);
        scene.RunCommands(3);
        scene.Service.ReportAiDiscovery(new AiDiscoveryHead(new[] { Fixed.AddDays(-3).UtcTicks, Fixed.AddMinutes(1).UtcTicks }));
        await scene.PassAsync();

        Assert.Equal(2, scene.CountOf(Audit));
        Assert.Equal(3, scene.CountOf(Activity));
        Assert.Equal(1, scene.CountOf(Ai));

        await scene.Service.PanelShown(Activity);
        Assert.Equal(2, scene.CountOf(Audit));
        Assert.Equal(0, scene.CountOf(Activity));
        Assert.Equal(1, scene.CountOf(Ai));

        // Another panel comes up and the first is left: the two announcements may cross, and only the panel that is current is hidden.
        await scene.Service.PanelShown(Audit);
        await scene.Service.PanelLeft(Activity);
        Assert.Equal(0, scene.CountOf(Audit));
        await scene.PassAsync();
        Assert.Equal(0, scene.CountOf(Audit));
        Assert.Equal(1, scene.CountOf(Ai));
    }

    [Fact]
    public async Task A_panel_that_is_not_tracked_is_ignored_by_both_visits()
    {
        var scene = Scene(auditRows: 3);
        await scene.StartAsync();
        scene.AddAuditRows(2);
        await scene.PassAsync();

        await scene.Service.PanelShown("settings");
        await scene.Service.PanelShown("logs");
        await scene.Service.PanelLeft("overview");

        Assert.Equal(2, scene.CountOf(Audit));
        Assert.False(UnreadCountsService.IsTracked("logs"));
        Assert.Equal(new[] { Audit, Activity, Ai }, UnreadCountsService.TrackedPanels);
        Assert.False(scene.Service.TryGetMarker("logs", out _));
    }

    [Fact]
    public async Task Changed_is_raised_only_when_what_the_sidebar_shows_changes()
    {
        var scene = Scene(auditRows: 3);
        await scene.StartAsync();
        var announced = scene.PublishedCount;

        // Passes that find nothing new say nothing.
        await scene.PassAsync();
        await scene.PassAsync();
        Assert.Equal(announced, scene.PublishedCount);

        scene.AddAuditRows(1);
        await scene.PassAsync();
        Assert.Equal(announced + 1, scene.PublishedCount);

        await scene.PassAsync();
        Assert.Equal(announced + 1, scene.PublishedCount);

        // Every announcement carries all three panels.
        Assert.Equal(new[] { Audit, Activity, Ai }.Order(), scene.Published[^1].Keys.Order());
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_silence_the_others_or_stop_the_counting()
    {
        var scene = Scene(auditRows: 3);
        var second = 0;
        scene.Service.Changed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        scene.Service.Changed += (_, _) => second++;
        await scene.PassAsync();

        scene.AddAuditRows(1);
        await scene.PassAsync();

        Assert.True(second >= 1);
        Assert.Equal(1, scene.CountOf(Audit));
    }

    [Fact]
    public async Task The_current_counts_are_readable_without_a_subscriber_for_a_window_built_later()
    {
        var scene = Scene(auditRows: 3);
        await scene.StartAsync();
        scene.AddAuditRows(2);
        await scene.PassAsync();
        scene.Stop();
        Assert.False(scene.Service.IsRunning);

        Assert.Equal(2, scene.Service.Current[Audit]);
        Assert.Equal(2, scene.Service.CountOf(Audit));
        Assert.Equal(0, scene.Service.CountOf("no-such-panel"));
    }

    [Fact]
    public void A_new_service_holds_the_markers_the_file_holds_for_the_panels_it_tracks()
    {
        var scene = Scene();
        File.WriteAllText(scene.SettingsPath, """{ "seen": { "audit": 31, "activity": 640000000000000000, "logs": 5 } }""");

        var service = scene.Build(AppSettingsStore.OpenFresh(scene.SettingsPath), scene.NewReader());

        Assert.True(service.TryGetMarker(Audit, out var audit));
        Assert.Equal(31, audit);
        Assert.True(service.TryGetMarker(Activity, out var activity));
        Assert.Equal(640_000_000_000_000_000, activity);
        Assert.False(service.TryGetMarker(Ai, out _));

        // A panel this build does not track is not held.
        Assert.False(service.TryGetMarker("logs", out _));
    }

    [Fact]
    public async Task A_marker_this_build_does_not_track_is_still_in_the_file_after_the_ones_it_does_are_written()
    {
        var scene = Scene(auditRows: 4);
        File.WriteAllText(scene.SettingsPath, """{ "seen": { "logs": 5 } }""");
        var service = scene.Build(AppSettingsStore.OpenFresh(scene.SettingsPath), scene.NewReader());
        await scene.StartAsync(service);

        await service.PanelShown(Audit);
        await service.PanelLeft(Audit);
        await service.Persisted;

        var json = JsonNode.Parse(File.ReadAllText(scene.SettingsPath))!;
        Assert.Equal(5, (long)json["seen"]!["logs"]!);
        Assert.Equal(4, (long)json["seen"]!["audit"]!);
    }
}
