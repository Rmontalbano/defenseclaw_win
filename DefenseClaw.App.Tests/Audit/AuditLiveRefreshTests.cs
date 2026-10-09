using System.Collections.Specialized;
using System.Text.RegularExpressions;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-262: the Audit panel stays current while it is on screen. The acceptance - a new block event appears within one poll interval, and an idle
/// poll costs one trivial statement - is held here against the real change probe and the real reader, over a synthetic database from the real DDL.
/// The poll is driven directly (<see cref="AuditPanelViewModel.PollLiveAsync"/>, the body of the timer's tick) with the timer off and the clock fixed,
/// so nothing ticks behind a test's back; the timer itself, and the scroll position the view keeps, are <see cref="AuditLiveRefreshViewTests"/>.
/// </summary>
public sealed class AuditLiveRefreshTests : IDisposable
{
    private static readonly Regex UpdatedAt = new(@"^Updated \d{2}:\d{2}:\d{2}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private AppServices Services => _services!;

    private AuditReader Reader => Services.Audit;

    private AuditChangeProbe Probe => Services.AuditChanges;

    /// <summary>A panel on screen over <paramref name="quiet"/> INFO hook decisions 1, 2, 3 ... seconds before the fixed clock's "now".</summary>
    private async Task<AuditPanelViewModel> OpenAsync(int quiet, bool actionableOnly = true)
    {
        AuditTestDatabase.Create(DbPath, quiet, newest: _clock.GetUtcNow());
        SqlitePools.Release(_temp.Path);
        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = actionableOnly };
        panel.SetActive(true);
        await panel.InitializeAsync();
        return panel;
    }

    private void Add(string id, double secondsAgo, string action = "hook_decision", string severity = "INFO", string? details = "synthetic extra", string? connector = "claudecode") =>
        AuditEventWriter.Add(DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, severity, details, connector);

    private static string[] Ids(AuditPanelViewModel panel) => panel.Rows.Select(r => r.Id).ToArray();

    // ------------------------------------------------------------------ the acceptance

    [Fact]
    public async Task A_new_block_event_appears_in_the_next_poll_and_nothing_that_was_listed_moves()
    {
        var panel = await OpenAsync(quiet: 30);
        Add("block-0", 20.5, action: "install-blocked");
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "block-0" }, Ids(panel));
        Assert.Equal(30, panel.HiddenCount);
        var before = panel.Rows.ToArray();
        panel.SelectedRow = before[0];
        var changes = new List<NotifyCollectionChangedAction>();
        panel.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        // A block event, and a quiet one beside it that the actionable view leaves out.
        Add("block-1", 0.5, action: "install-blocked");
        Add("quiet-1", 0.6);
        await panel.PollLiveAsync();

        Assert.Equal(new[] { "block-1", "block-0" }, Ids(panel));
        Assert.Same(before[0], panel.Rows[1]);
        Assert.Same(before[0], panel.SelectedRow);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, changes);

        // The thirty quiet events the poll read again were counted when the list was built; only the new one is added to the count.
        Assert.Equal(31, panel.HiddenCount);
        Assert.Equal("2 actionable events · 31 low-signal hidden · Last 24 hours", panel.ResultSummary);
        Assert.False(panel.IsEmpty);
        Assert.Equal(string.Empty, panel.LiveNote);
    }

    [Fact]
    public async Task An_idle_poll_costs_one_probe_sample_and_no_query_at_all()
    {
        var panel = await OpenAsync(quiet: 40, actionableOnly: false);
        var rows = panel.Rows.ToArray();
        var samples = Probe.SampleCount;
        var pages = Reader.PageQueryCount;
        var counts = Reader.CountQueryCount;
        var decoded = Reader.RowsDecoded;
        var unchanged = Reader.UnchangedReads;
        var opens = Probe.OpenCount;

        for (var i = 0; i < 5; i++)
        {
            await panel.PollLiveAsync();
        }

        // Five polls, five samples of the kept connection: no page, no count, no row decoded, not even a remembered answer asked for.
        Assert.Equal(samples + 5, Probe.SampleCount);
        Assert.Equal(opens, Probe.OpenCount);
        Assert.Equal(pages, Reader.PageQueryCount);
        Assert.Equal(counts, Reader.CountQueryCount);
        Assert.Equal(decoded, Reader.RowsDecoded);
        Assert.Equal(unchanged, Reader.UnchangedReads);
        Assert.True(rows.SequenceEqual(panel.Rows));
        Assert.Matches(UpdatedAt, panel.LiveStatusText);
        Assert.True(panel.HasLiveStatus);
    }

    [Fact]
    public async Task A_change_costs_one_page_and_no_count_and_adjusts_the_total_by_what_it_added()
    {
        var panel = await OpenAsync(quiet: 30, actionableOnly: false);
        Assert.Contains("30 of 30 matching events", panel.ResultSummary, StringComparison.Ordinal);
        var pages = Reader.PageQueryCount;
        var counts = Reader.CountQueryCount;

        Add("new-a", 0.4);
        Add("new-b", 0.5);
        await panel.PollLiveAsync();

        Assert.Equal(pages + 1, Reader.PageQueryCount);
        Assert.Equal(counts, Reader.CountQueryCount);
        Assert.Equal(32, panel.Rows.Count);
        Assert.Equal(new[] { "new-a", "new-b", "evt-000000" }, Ids(panel).Take(3).ToArray());
        Assert.Contains("32 of 32 matching events", panel.ResultSummary, StringComparison.Ordinal);

        // ... and the next poll, with nothing new, is idle again.
        var samples = Probe.SampleCount;
        await panel.PollLiveAsync();
        Assert.Equal(samples + 1, Probe.SampleCount);
        Assert.Equal(pages + 1, Reader.PageQueryCount);
    }

    // ------------------------------------------------------------------ which rows a poll adds, and where

    [Fact]
    public async Task A_row_seen_once_is_never_added_twice()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);

        Add("once", 0.5);
        await panel.PollLiveAsync();
        Add("unrelated", 0.4);
        await panel.PollLiveAsync();
        await panel.PollLiveAsync();

        Assert.Equal(12, panel.Rows.Count);
        Assert.Equal(1, Ids(panel).Count(id => id == "once"));
        Assert.Equal(panel.Rows.Count, Ids(panel).Distinct().Count());
    }

    [Fact]
    public async Task A_late_row_older_than_the_newest_goes_where_the_queries_order_puts_it()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);

        // The gateway stamped it 5.5 s ago and committed it now (a scan stamps its findings when it began): between evt-000004 (5 s) and evt-000005 (6 s).
        Add("late", 5.5);
        await panel.PollLiveAsync();

        var ids = Ids(panel);
        Assert.Equal(11, ids.Length);
        Assert.Equal(5, Array.IndexOf(ids, "late"));
        Assert.Equal("evt-000004", ids[4]);
        Assert.Equal("evt-000005", ids[6]);
    }

    [Fact]
    public async Task Several_new_rows_arrive_in_order_whatever_order_they_were_committed_in()
    {
        var panel = await OpenAsync(quiet: 5, actionableOnly: false);

        Add("n-3", 0.3);
        Add("n-1", 0.1);
        Add("n-2", 0.2);
        await panel.PollLiveAsync();

        Assert.Equal(new[] { "n-1", "n-2", "n-3", "evt-000000" }, Ids(panel).Take(4).ToArray());
    }

    [Fact]
    public async Task A_row_older_than_the_last_one_a_load_read_is_left_for_load_more_and_never_listed_twice()
    {
        var panel = await OpenAsync(quiet: 250, actionableOnly: false);
        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.True(panel.HasMore);

        // Stamped 200.5 s ago: below the hundred rows the list holds (the oldest is 100 s old), so it is "Load more"'s, not the poll's.
        Add("far-back", 200.5);
        // Stamped 50.5 s ago: inside what the list holds, so the poll lists it, in place.
        Add("late-inside", 50.5);
        await panel.PollLiveAsync();

        Assert.DoesNotContain("far-back", Ids(panel));
        Assert.Equal(50, Array.IndexOf(Ids(panel), "late-inside"));
        Assert.Equal(AuditPanelViewModel.PageSize + 1, panel.Rows.Count);

        while (panel.HasMore)
        {
            await panel.LoadMoreCommand.ExecuteAsync(null);
        }

        // Now it is read, once, and the list is the whole window with nothing repeated.
        var ids = Ids(panel);
        Assert.Equal(252, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(1, ids.Count(id => id == "far-back"));
    }

    [Fact]
    public async Task Load_more_after_a_poll_continues_from_the_same_place()
    {
        var panel = await OpenAsync(quiet: 250, actionableOnly: false);
        Add("fresh", 0.5);
        await panel.PollLiveAsync();
        Assert.Equal(AuditPanelViewModel.PageSize + 1, panel.Rows.Count);

        await panel.LoadMoreCommand.ExecuteAsync(null);

        // The next page starts after the hundredth quiet row, as it would have: no repeat of the new row, no gap.
        Assert.Equal(AuditPanelViewModel.PageSize * 2 + 1, panel.Rows.Count);
        Assert.Equal("fresh", panel.Rows[0].Id);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"evt-{i:000000}"), Ids(panel).Skip(1));
    }

    [Fact]
    public async Task A_poll_adds_only_what_the_lists_own_filters_would_have_listed()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);
        panel.SearchText = "needle";
        await panel.LastLoad;
        Assert.Empty(panel.Rows);

        Add("hay", 0.5, details: "just hay");
        Add("pin", 0.6, details: "found the needle here");
        await panel.PollLiveAsync();

        Assert.Equal(new[] { "pin" }, Ids(panel));
    }

    [Fact]
    public async Task The_platform_only_view_takes_only_platform_rows()
    {
        AuditTestDatabase.Create(DbPath, 6, connectorFor: i => i % 2 == 0 ? null : "claudecode", newest: _clock.GetUtcNow());
        SqlitePools.Release(_temp.Path);
        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false, SelectedConnector = ConnectorOption.PlatformOnlyOption };
        panel.SetActive(true);
        await panel.InitializeAsync();
        Assert.Equal(3, panel.Rows.Count);

        Add("platform", 0.5, connector: null);
        Add("scoped", 0.6, connector: "claudecode");
        await panel.PollLiveAsync();

        Assert.Equal("platform", panel.Rows[0].Id);
        Assert.DoesNotContain("scoped", Ids(panel));
        Assert.Equal(4, panel.Rows.Count);
    }

    // ------------------------------------------------------------------ the actionable view under a poll

    [Fact]
    public async Task A_low_signal_event_is_counted_once_however_many_polls_read_it()
    {
        var panel = await OpenAsync(quiet: 20);
        Assert.Equal(20, panel.HiddenCount);

        Add("quiet-a", 0.5);
        await panel.PollLiveAsync();
        Assert.Equal(21, panel.HiddenCount);

        // The next poll's window holds quiet-a again, and the twenty before it: none is counted a second time.
        Add("quiet-b", 0.4);
        await panel.PollLiveAsync();
        await panel.PollLiveAsync();
        Assert.Equal(22, panel.HiddenCount);
    }

    [Fact]
    public async Task A_poll_that_adds_nothing_to_the_list_leaves_it_and_its_caption_alone()
    {
        var panel = await OpenAsync(quiet: 20);
        var summary = panel.ResultSummary;
        var changes = new List<NotifyCollectionChangedAction>();
        panel.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        // The database moved, and the actionable view has nothing to add: the quiet event only counts.
        Add("quiet", 0.5);
        await panel.PollLiveAsync();

        Assert.Empty(changes);
        Assert.Empty(panel.Rows);
        Assert.Equal(21, panel.HiddenCount);
        Assert.NotEqual(summary, panel.ResultSummary);
        Assert.Equal("No actionable events", panel.EmptyTitle);
        Assert.Contains("21 low-signal events are hidden", panel.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_actionable_event_replaces_the_empty_state()
    {
        var panel = await OpenAsync(quiet: 5);
        Assert.True(panel.IsEmpty);

        Add("first", 0.5, action: "scan-failed");
        await panel.PollLiveAsync();

        Assert.False(panel.IsEmpty);
        Assert.Equal(new[] { "first" }, Ids(panel));
    }

    // ------------------------------------------------------------------ bursts, storms and the cap

    [Fact]
    public async Task A_burst_larger_than_a_page_is_merged_whole_in_order()
    {
        var panel = await OpenAsync(quiet: 20, actionableOnly: false);
        var now = _clock.GetUtcNow();
        AuditEventWriter.AddMany(DbPath, 150, i => ($"burst-{i:000}", now.AddSeconds(-i * 0.001), "hook_decision", "INFO", "synthetic burst", "claudecode"));
        var pages = Reader.PageQueryCount;

        await panel.PollLiveAsync();

        Assert.Equal(170, panel.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 150).Select(i => $"burst-{i:000}"), Ids(panel).Take(150));
        Assert.Equal(pages + 1, Reader.PageQueryCount);
    }

    [Fact]
    public async Task A_storm_of_more_than_a_poll_will_merge_starts_the_list_over_from_the_newest_page()
    {
        var panel = await OpenAsync(quiet: 20, actionableOnly: false);
        var now = _clock.GetUtcNow();
        AuditEventWriter.AddMany(DbPath, AuditPanelViewModel.LiveWindowLimit + 100, i => ($"storm-{i:0000}", now.AddSeconds(-i * 0.001), "hook_decision", "INFO", "synthetic storm", "claudecode"));

        await panel.PollLiveAsync();

        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.Equal("storm-0000", panel.Rows[0].Id);
        Assert.True(panel.HasMore);
        Assert.Equal(string.Empty, panel.LiveNote);

        // ... and what is listed is current: the next poll is idle.
        var pages = Reader.PageQueryCount;
        await panel.PollLiveAsync();
        Assert.Equal(pages, Reader.PageQueryCount);
    }

    [Fact]
    public async Task At_the_cap_the_oldest_row_goes_and_the_list_says_it_is_a_window()
    {
        var panel = await OpenAsync(quiet: AuditPanelViewModel.MaxRows, actionableOnly: false);
        for (var page = 1; page < AuditPanelViewModel.MaxRows / AuditPanelViewModel.PageSize; page++)
        {
            await panel.LoadMoreCommand.ExecuteAsync(null);
        }

        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.False(panel.IsRowCapReached);
        Assert.Equal("evt-001999", panel.Rows[^1].Id);

        Add("newest", 0.5);
        await panel.PollLiveAsync();

        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.Equal("newest", panel.Rows[0].Id);
        Assert.Equal("evt-001998", panel.Rows[^1].Id);
        Assert.True(panel.IsRowCapReached);
        Assert.False(panel.HasMore);
    }

    // ------------------------------------------------------------------ only while it is on screen

    [Fact]
    public async Task A_panel_that_is_not_on_screen_does_not_look_and_looks_once_when_it_comes_back()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);
        panel.SetActive(false);
        Assert.False(panel.IsLiveTimerRunning);
        var samples = Probe.SampleCount;

        Add("while-away", 0.5);
        await panel.PollLiveAsync();

        Assert.Equal(samples, Probe.SampleCount);
        Assert.DoesNotContain("while-away", Ids(panel));

        // Coming back is the catch-up poll the activation contract asks for.
        panel.SetActive(true);
        await panel.LastLivePoll;

        Assert.Equal("while-away", panel.Rows[0].Id);
    }

    [Fact]
    public async Task The_archive_is_a_file_that_does_not_change_and_is_never_polled()
    {
        var panel = await OpenAsync(quiet: 4, actionableOnly: false);
        using var archiveDirectory = new TempDirectory();
        var archive = Path.Combine(archiveDirectory.Path, "audit-archive.db");
        AuditTestDatabase.Create(archive, 5, newest: new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero), idPrefix: "arc-");
        Assert.True(Services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = archive } }));
        panel.SetActive(false);
        panel.SetActive(true);

        panel.SourceKey = AuditPanelViewModel.SourceArchive;
        await panel.LastLoad;
        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(string.Empty, panel.LiveStatusText);
        var samples = Probe.SampleCount;

        Add("live-row", 0.5);
        await panel.PollLiveAsync();

        Assert.Equal(samples, Probe.SampleCount);
        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(string.Empty, panel.LiveStatusText);
    }

    [Fact]
    public async Task A_database_that_appears_after_the_panel_opened_is_picked_up()
    {
        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false };
        panel.SetActive(true);
        await panel.InitializeAsync();
        Assert.True(panel.IsEmpty);
        Assert.Equal("No audit database yet", panel.EmptyTitle);

        // The gateway records its first events.
        AuditTestDatabase.Create(DbPath, 7, newest: _clock.GetUtcNow());
        SqlitePools.Release(_temp.Path);
        await panel.PollLiveAsync();

        Assert.Equal(7, panel.Rows.Count);
        Assert.False(panel.IsEmpty);
        Assert.True(panel.HasLiveStatus);
    }

    // ------------------------------------------------------------------ a failure keeps what is on screen

    [Fact]
    public async Task A_failed_poll_keeps_the_rows_says_so_and_backs_off_until_one_works()
    {
        var panel = await OpenAsync(quiet: 8, actionableOnly: false);
        var rows = panel.Rows.ToArray();
        var calls = 0;
        panel.LiveStampSource = _ =>
        {
            calls++;
            throw new IOException("the disk is busy");
        };

        await panel.PollLiveAsync();

        Assert.Equal(1, calls);
        Assert.True(panel.HasLiveNote);
        Assert.Contains("Live refresh paused (the disk is busy)", panel.LiveNote, StringComparison.Ordinal);
        Assert.Contains("trying again in 5 s", panel.LiveNote, StringComparison.Ordinal);
        Assert.True(rows.SequenceEqual(panel.Rows));

        // Backing off: ticks inside the wait do not even sample.
        await panel.PollLiveAsync();
        _clock.Advance(TimeSpan.FromSeconds(4));
        await panel.PollLiveAsync();
        Assert.Equal(1, calls);

        // The wait doubles: 5, 10, 20, 40, then a minute at most.
        _clock.Advance(TimeSpan.FromSeconds(2));
        await panel.PollLiveAsync();
        Assert.Equal(2, calls);
        Assert.Contains("trying again in 10 s", panel.LiveNote, StringComparison.Ordinal);

        foreach (var expected in new[] { "20 s", "40 s", "60 s", "60 s" })
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            await panel.PollLiveAsync();
            Assert.Contains("trying again in " + expected, panel.LiveNote, StringComparison.Ordinal);
        }

        // The disk comes back: the poll after the wait works, merges what arrived meanwhile, and the note is gone.
        panel.LiveStampSource = null;
        Add("while-it-was-down", 0.5);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await panel.PollLiveAsync();

        Assert.False(panel.HasLiveNote);
        Assert.Equal("while-it-was-down", panel.Rows[0].Id);
        Assert.Matches(UpdatedAt, panel.LiveStatusText);
    }

    [Fact]
    public async Task A_stamp_that_cannot_be_taken_is_a_failure_not_a_change()
    {
        var panel = await OpenAsync(quiet: 8, actionableOnly: false);
        var rows = panel.Rows.ToArray();
        var pages = Reader.PageQueryCount;
        panel.LiveStampSource = _ => Task.FromResult(AuditStamp.Unknown);

        await panel.PollLiveAsync();

        Assert.Contains("audit.db could not be sampled", panel.LiveNote, StringComparison.Ordinal);
        Assert.Equal(pages, Reader.PageQueryCount);
        Assert.True(rows.SequenceEqual(panel.Rows));
    }

    // ------------------------------------------------------------------ the operator's own work comes first

    [Fact]
    public async Task A_poll_that_a_newer_load_overtakes_adds_nothing()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);
        Add("polled-in", 0.5, details: "synthetic event polled");
        var pages = Reader.PageQueryCount;

        // Hold the gate every load queues behind; the poll starts (the database moved), the operator types a search, and the search's load takes over.
        await panel.LoadGate.WaitAsync();
        var poll = panel.PollLiveAsync();
        panel.SearchText = "synthetic event";
        _ = panel.LoadGate.Release();
        await poll;
        await panel.LastLoad;

        // The search's list is the answer, read once: the poll's own page was never read.
        Assert.Equal(pages + 1, Reader.PageQueryCount);
        Assert.Equal(11, panel.Rows.Count);
        Assert.False(panel.HasLiveNote);
    }

    [Fact]
    public async Task A_tick_that_finds_a_load_running_does_nothing()
    {
        var panel = await OpenAsync(quiet: 10, actionableOnly: false);
        var samples = Probe.SampleCount;

        panel.IsLoading = true;
        await panel.PollLiveAsync();
        panel.IsLoading = false;

        Assert.Equal(samples, Probe.SampleCount);
    }

    // ------------------------------------------------------------------ the caption

    [Fact]
    public async Task The_caption_says_when_the_list_was_last_known_to_be_current()
    {
        var panel = await OpenAsync(quiet: 5, actionableOnly: false);
        var first = panel.LiveStatusText;
        Assert.Matches(UpdatedAt, first);

        _clock.Advance(TimeSpan.FromSeconds(75));
        await panel.PollLiveAsync();

        Assert.NotEqual(first, panel.LiveStatusText);
        Assert.Matches(UpdatedAt, panel.LiveStatusText);
    }
}
