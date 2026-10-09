using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-262 on the Audit panel: "Actionable only" opens on by default, as the 0.8.10 TUI's Audit panel does, pages through the same rule the Events
/// view uses (<see cref="DefenseClaw.Core.Audit.ActionableRule"/>), says how many events it leaves out and why a list is empty, stands down while a
/// search or a preset (or any other request for something specific) is on, and does not change what a list without it does. Synthetic rows from the
/// real DDL; the clock is fixed so a refresh can never straddle the minute the time window starts on.
/// </summary>
public sealed class AuditActionableTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private AppServices Services => _services!;

    /// <summary>A panel over <paramref name="quiet"/> INFO hook decisions 1, 2, 3 ... seconds before the fixed clock's "now" ("synthetic event N").</summary>
    private AuditPanelViewModel PanelOver(int quiet)
    {
        AuditTestDatabase.Create(DbPath, quiet, newest: _clock.GetUtcNow());
        SqliteConnection.ClearAllPools();
        _services = TestServices.Create(_temp);
        return new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false };
    }

    /// <summary>One event <paramref name="secondsAgo"/> seconds before "now" (a half second off the quiet rows', so nothing ties).</summary>
    private void Add(string id, double secondsAgo, string action = "hook_decision", string severity = "INFO", string? details = "synthetic extra", string? runId = null, string target = "") =>
        AuditEventWriter.Add(DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, severity, details, runId: runId, target: target);

    private static string[] Ids(AuditPanelViewModel panel) => panel.Rows.Select(r => r.Id).ToArray();

    private static string Big(int bytes) => new('d', bytes);

    // ------------------------------------------------------------------ the default

    [Fact]
    public async Task The_panel_opens_on_the_actionable_events_and_says_how_many_it_left_out()
    {
        var panel = PanelOver(quiet: 40);
        Add("high-1", 3.5, severity: "HIGH", details: "matched a rule");
        Add("block-1", 7.5, action: "block-plugin");
        Add("fail-1", 12.5, action: "scan", severity: "LOW", details: "the scanner failed to start");

        await panel.InitializeAsync();

        Assert.True(AuditPanelViewModel.ActionableByDefault);
        Assert.True(panel.ActionableOnly);
        Assert.True(panel.IsActionableApplied);
        Assert.Equal(new[] { "high-1", "block-1", "fail-1" }, Ids(panel));
        Assert.Equal(40, panel.HiddenCount);
        Assert.True(panel.HasHidden);
        Assert.Equal("40 low-signal hidden", panel.HiddenText);
        Assert.Equal("3 actionable events · 40 low-signal hidden · Last 24 hours", panel.ResultSummary);
        Assert.False(panel.IsEmpty);
        Assert.False(panel.HasMore);

        // The total counts every event of the window, low-signal or not, so the narrowed view does not ask for it.
        Assert.Equal(0, Services.Audit.CountQueryCount);
    }

    [Fact]
    public async Task Turning_it_off_lists_every_event_with_the_total_and_turning_it_on_narrows_again()
    {
        var panel = PanelOver(quiet: 40);
        Add("high-1", 3.5, severity: "HIGH");
        await panel.InitializeAsync();
        Assert.Equal(new[] { "high-1" }, Ids(panel));

        panel.ActionableOnly = false;
        await panel.LastLoad;

        Assert.False(panel.IsActionableApplied);
        Assert.Equal(41, panel.Rows.Count);
        Assert.Equal(0, panel.HiddenCount);
        Assert.False(panel.HasHidden);
        Assert.Equal(string.Empty, panel.HiddenText);
        Assert.Equal("41 of 41 matching events · Last 24 hours", panel.ResultSummary);

        panel.ActionableOnly = true;
        await panel.LastLoad;

        Assert.Equal(new[] { "high-1" }, Ids(panel));
        Assert.Equal(40, panel.HiddenCount);
    }

    [Fact]
    public async Task The_rule_is_the_TUIs_a_word_in_any_column_it_searches_is_enough_and_an_unlisted_severity_is_never_hidden()
    {
        var panel = PanelOver(quiet: 10);
        Add("by-action", 1.5, action: "install-rejected");
        Add("by-target", 2.5, target: "skill/denied-skill");
        Add("fatal", 3.5, severity: "FATAL");
        Add("error", 4.5, severity: "ERROR");
        Add("warn", 5.5, severity: "WARN");
        Add("hook", 6.5, details: "observe connector=claudecode would_block=false");
        Add("medium", 7.5, severity: "MEDIUM", details: "scan completed");

        await panel.InitializeAsync();

        // A word in the action or the target, a severity the rule does not list, and a hook row whose details say would_block=false are all shown;
        // MEDIUM with nothing alarming in it is not.
        Assert.Equal(new[] { "by-action", "by-target", "fatal", "error", "warn", "hook" }, Ids(panel));
        Assert.Equal(11, panel.HiddenCount);
    }

    // ------------------------------------------------------------------ suspended while something specific is asked for

    [Theory]
    [InlineData("search", "a search is on")]
    [InlineData("action", "an action filter is set")]
    [InlineData("run", "one run's events are shown")]
    [InlineData("preset", "the scans view is chosen")]
    [InlineData("severity", "a minimum severity is set")]
    public async Task A_filter_that_asks_for_something_specific_suspends_it_while_it_is_on_and_clearing_it_brings_it_back(string filter, string why)
    {
        var panel = PanelOver(quiet: 12);
        Add("loud", 3.5, severity: "HIGH", details: "synthetic loud", runId: "run-9");
        Add("in-run", 4.5, details: "quiet but in the run", runId: "run-1");
        Add("scan-done", 5.5, action: "scan-completed", details: "quiet scan");
        await panel.InitializeAsync();
        Assert.Equal(new[] { "loud" }, Ids(panel).Where(id => id == "loud").ToArray());
        Assert.DoesNotContain("evt-000001", Ids(panel));
        Assert.True(panel.IsActionableApplied);
        Assert.True(panel.CanChangeActionable);
        Assert.Null(panel.ActionableSuspendedBy);

        switch (filter)
        {
            case "search":
                panel.SearchText = "synthetic event";
                break;
            case "action":
                panel.ActionFilter = "hook_decision";
                break;
            case "run":
                panel.RunFilter = "run-1";
                break;
            case "preset":
                panel.ActivePreset = AuditPanelViewModel.PresetScans;
                break;
            default:
                panel.SelectedSeverity = SeverityOption.All.Single(o => o.Value == DefenseClaw.Core.Audit.AuditSeverity.Info);
                break;
        }

        await panel.LastLoad;

        // The choice is kept; the view is off while it is on, so a quiet event shows - and nothing is counted as hidden.
        Assert.True(panel.ActionableOnly);
        Assert.False(panel.IsActionableApplied);
        Assert.False(panel.CanChangeActionable);
        Assert.Equal(why, panel.ActionableSuspendedBy);
        Assert.Contains("paused while " + why, panel.ActionableToolTip, StringComparison.Ordinal);
        Assert.Equal(0, panel.HiddenCount);
        Assert.NotEmpty(panel.Rows);
        Assert.Contains(panel.Rows, row => row.Severity == "INFO");

        switch (filter)
        {
            case "search":
                panel.SearchText = string.Empty;
                break;
            case "action":
                panel.ActionFilter = string.Empty;
                break;
            case "run":
                panel.RunFilter = string.Empty;
                break;
            case "preset":
                panel.ActivePreset = AuditPanelViewModel.PresetAll;
                break;
            default:
                panel.SelectedSeverity = SeverityOption.Any;
                break;
        }

        await panel.LastLoad;

        Assert.True(panel.IsActionableApplied);
        Assert.Null(panel.ActionableSuspendedBy);
        Assert.Equal(new[] { "loud" }, Ids(panel));
        Assert.Equal(14, panel.HiddenCount);
        Assert.DoesNotContain("paused", panel.ActionableToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_blocks_link_from_Overview_lands_on_every_block_not_only_the_blocks_the_rule_would_keep()
    {
        var panel = PanelOver(quiet: 5);
        Add("blocked", 3.5, action: "install-blocked", severity: "INFO", details: "enforced");
        Add("enforced", 4.5, action: "enforce-policy", severity: "INFO", details: "quiet enforcement");
        await panel.InitializeAsync();
        Assert.Equal(new[] { "blocked" }, Ids(panel));

        panel.Accept(new AuditPreset("blocks"));
        await panel.LastLoad;

        // The Blocks preset reads "block", "reject", "enforce" and "quarantine" in the action or the details: the quiet enforcement is in it.
        Assert.False(panel.IsActionableApplied);
        Assert.Equal(new[] { "blocked", "enforced" }, Ids(panel));
    }

    [Fact]
    public async Task Reset_filters_clears_what_suspends_the_view_and_leaves_the_switch_as_the_operator_set_it()
    {
        var panel = PanelOver(quiet: 8);
        Add("loud", 3.5, severity: "HIGH");
        await panel.InitializeAsync();
        panel.SearchText = "synthetic event";
        await panel.LastLoad;
        Assert.False(panel.IsActionableApplied);

        // On: clearing the search brings the actionable view back.
        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;
        Assert.True(panel.ActionableOnly);
        Assert.Equal(new[] { "loud" }, Ids(panel));

        // Off: the switch is a view setting beside the filters, not one of them, so Reset does not turn it back on.
        panel.ActionableOnly = false;
        await panel.LastLoad;
        panel.SearchText = "synthetic event";
        await panel.LastLoad;
        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;
        Assert.False(panel.ActionableOnly);
        Assert.Equal(9, panel.Rows.Count);
    }

    [Fact]
    public void Setting_the_switch_before_the_first_load_starts_no_load()
    {
        AuditTestDatabase.Create(DbPath, 20, newest: _clock.GetUtcNow());
        _services = TestServices.Create(_temp);

        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false };

        // The first load reads the switch as it is: there is no list to redo, so nothing was read.
        Assert.Equal(0, Services.Audit.PageQueryCount);
        Assert.Empty(panel.Rows);
        Assert.False(panel.ActionableOnly);
    }

    // ------------------------------------------------------------------ what an empty list says

    [Fact]
    public async Task A_window_with_nothing_actionable_says_what_it_is_hiding_and_where_the_switch_is()
    {
        var panel = PanelOver(quiet: 12);

        await panel.InitializeAsync();

        Assert.True(panel.IsEmpty);
        Assert.Empty(panel.Rows);
        Assert.Equal("No actionable events", panel.EmptyTitle);
        Assert.Equal("12 low-signal events are hidden. Turn off \"Actionable only\" to see them.", panel.EmptyDetail);
        Assert.Equal("0 actionable events · 12 low-signal hidden · Last 24 hours", panel.ResultSummary);
    }

    [Fact]
    public async Task A_single_hidden_event_is_said_in_the_singular()
    {
        var panel = PanelOver(quiet: 1);

        await panel.InitializeAsync();

        Assert.Equal("1 low-signal event is hidden. Turn off \"Actionable only\" to see it.", panel.EmptyDetail);
    }

    [Fact]
    public async Task An_empty_window_is_still_an_empty_window_whatever_the_switch_says()
    {
        var panel = PanelOver(quiet: 0);

        await panel.InitializeAsync();

        Assert.True(panel.IsEmpty);
        Assert.Equal("No actionable events", panel.EmptyTitle);
        Assert.Equal("Widen the time range, or clear a filter.", panel.EmptyDetail);
        Assert.False(panel.HasHidden);
    }

    // ------------------------------------------------------------------ paging

    [Fact]
    public async Task Load_more_keeps_finding_actionable_events_further_back_and_adds_to_what_is_left_out()
    {
        // A loud event every fifty seconds among a quiet one a second: a page of 100 reads at most twenty pages (2,000 rows, about 39 loud ones).
        var panel = PanelOver(quiet: 5000);
        var now = _clock.GetUtcNow();
        AuditEventWriter.AddMany(DbPath, 100, i => ($"loud-{i:000}", now.AddSeconds(-(i * 50) - 0.5), "hook_decision", "HIGH", "synthetic loud", "claudecode"));

        await panel.InitializeAsync();

        var first = panel.Rows.Count;
        Assert.InRange(first, 34, 44);
        Assert.True(panel.HasMore);
        Assert.StartsWith("loud-000", panel.Rows[0].Id, StringComparison.Ordinal);
        Assert.Contains("+ actionable events loaded", panel.ResultSummary, StringComparison.Ordinal);
        Assert.InRange(panel.HiddenCount, 1950, 1975);

        for (var calls = 0; panel.HasMore && calls < 20; calls++)
        {
            await panel.LoadMoreCommand.ExecuteAsync(null);
        }

        // Every loud event, newest first, once; and every quiet one was counted as left out - the window is 5,100 events.
        Assert.False(panel.HasMore);
        Assert.Equal(100, panel.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"loud-{i:000}"), Ids(panel));
        Assert.Equal(5000, panel.HiddenCount);
        Assert.Equal($"100 actionable events · {5000.ToString("N0", CultureInfo.CurrentCulture)} low-signal hidden · Last 24 hours", panel.ResultSummary);
    }

    // ------------------------------------------------------------------ an unchanged refresh

    [Fact]
    public async Task A_refresh_that_finds_the_same_actionable_events_keeps_the_rows_and_brings_the_count_up_to_date()
    {
        var panel = PanelOver(quiet: 5);
        Add("loud", 3.5, severity: "HIGH");
        await panel.InitializeAsync();
        var rows = panel.Rows.ToArray();
        panel.SelectedRow = rows[0];
        Assert.Equal(5, panel.HiddenCount);

        // Three more quiet events: the actionable ones are the same, the number left out is not.
        Add("quiet-a", 0.5);
        Add("quiet-b", 0.6);
        Add("quiet-c", 0.7);
        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.True(rows.SequenceEqual(panel.Rows));
        Assert.Same(rows[0], panel.SelectedRow);
        Assert.Equal(8, panel.HiddenCount);
        Assert.Equal("1 actionable event · 8 low-signal hidden · Last 24 hours", panel.ResultSummary);
    }

    // ------------------------------------------------------------------ rows the rule cannot read, and the export

    [Fact]
    public async Task An_event_whose_details_are_too_large_to_load_is_listed_because_what_it_says_is_unknown()
    {
        var panel = PanelOver(quiet: 3);
        Add("huge", 1.5, details: Big(300_000));

        await panel.InitializeAsync();

        var row = Assert.Single(panel.Rows);
        Assert.Equal("huge", row.Id);
        Assert.True(row.IsOversized);
        Assert.Equal("1 event too large to display", panel.StatusNote);
        Assert.Equal(3, panel.HiddenCount);
    }

    [Fact]
    public async Task The_export_holds_what_the_list_holds()
    {
        var panel = PanelOver(quiet: 30);
        Add("loud-1", 3.5, severity: "HIGH");
        Add("loud-2", 4.5, action: "scan-blocked");
        await panel.InitializeAsync();
        var path = Path.Combine(_temp.Path, "actionable.json");
        panel.ExportPathPicker = () => path;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            Assert.Equal(new[] { "loud-1", "loud-2" }, document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray());
        }

        Assert.StartsWith("Exported 2 actionable events to ", panel.ExportNote, StringComparison.Ordinal);

        // With the switch off the file is the whole window.
        panel.ActionableOnly = false;
        await panel.LastLoad;
        var everything = Path.Combine(_temp.Path, "everything.json");
        panel.ExportPathPicker = () => everything;
        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using var all = JsonDocument.Parse(await File.ReadAllTextAsync(everything));
        Assert.Equal(32, all.RootElement.GetArrayLength());
        Assert.StartsWith("Exported 32 matching events to ", panel.ExportNote, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the archive

    [Fact]
    public async Task The_archive_follows_the_same_switch_and_a_change_of_source_leaves_the_choice_alone()
    {
        var panel = PanelOver(quiet: 4);
        using var archiveDirectory = new TempDirectory();
        var archive = Path.Combine(archiveDirectory.Path, "audit-archive.db");
        var archiveNewest = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
        AuditTestDatabase.Create(archive, 6, newest: archiveNewest, idPrefix: "arc-");
        AuditEventWriter.Add(archive, "arc-loud", archiveNewest.AddMinutes(1), severity: "CRITICAL");
        Assert.True(Services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = archive } }));
        await panel.InitializeAsync();

        // On (the default): the archive lists its one actionable event and counts the six it leaves out.
        panel.SourceKey = AuditPanelViewModel.SourceArchive;
        await panel.LastLoad;
        Assert.True(panel.ActionableOnly);
        Assert.Equal(new[] { "arc-loud" }, Ids(panel));
        Assert.Equal(6, panel.HiddenCount);

        // Off in the archive: it stays off back in Live.
        panel.ActionableOnly = false;
        await panel.LastLoad;
        Assert.Equal(7, panel.Rows.Count);
        panel.SourceKey = AuditPanelViewModel.SourceLive;
        await panel.LastLoad;
        Assert.False(panel.ActionableOnly);
        Assert.Equal(4, panel.Rows.Count);
        Assert.Equal(0, panel.HiddenCount);
    }
}
