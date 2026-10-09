using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-284 on the Audit panel. A Refresh over a database that did not change decodes no row and leaves the list, the selection and the
/// scroll position alone; a row whose payload is too large to load is listed with the reason and counted ("1 event too large to
/// display"), and a page of nothing but such rows is never the "No matching events" state. Synthetic rows from the real DDL; the clock is
/// fixed so a refresh can never straddle the minute the time window starts on.
/// </summary>
public sealed class AuditPanelSnapshotTests : IDisposable
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

    /// <summary>A panel over <paramref name="rows"/> events 1, 2, 3 ... seconds before the fixed clock's "now".</summary>
    private AuditPanelViewModel PanelOver(int rows)
    {
        AuditTestDatabase.Create(DbPath, rows, newest: _clock.GetUtcNow());
        SqliteConnection.ClearAllPools();
        _services = TestServices.Create(_temp);
        return new AuditPanelViewModel(_services) { TimeSource = _clock, ActionableOnly = false };
    }

    private void AddRow(string id, DateTimeOffset at, string? details = "synthetic extra", string? structured = null) =>
        AddRowTo(DbPath, id, at, details, structured);

    private static void AddRowTo(string path, string id, DateTimeOffset at, string? details = "synthetic extra", string? structured = null)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, connector, event_name)
                VALUES ($id, $ts, 'hook_decision', '', 'audit_logger', $details, 'HIGH', $structured, 'guardrail.evaluation', 'claudecode', 'evt')
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ts", at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            command.Parameters.AddWithValue("$structured", (object?)structured ?? DBNull.Value);
            _ = command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static string Big(int bytes) => new('d', bytes);

    /// <summary>A value comfortably over the reader's 256 KiB limit.</summary>
    private const int OverLimit = 270_000;

    // ------------------------------------------------------------------ two refreshes, no change: the second decodes nothing

    [Fact]
    public async Task Two_refreshes_with_no_database_change_decode_no_rows_and_leave_the_list_alone()
    {
        var panel = PanelOver(120);
        await panel.InitializeAsync();

        var rows = panel.Rows.ToArray();
        panel.SelectedRow = rows[3];
        var decoded = Services.Audit.RowsDecoded;
        var unchanged = Services.Audit.UnchangedReads;
        Assert.Equal(AuditPanelViewModel.PageSize, rows.Length);
        Assert.True(decoded > 0);

        await panel.RefreshCommand.ExecuteAsync(null);
        await panel.RefreshCommand.ExecuteAsync(null);

        // Nothing decoded; the page and the total were each answered from the last result, twice.
        Assert.Equal(decoded, Services.Audit.RowsDecoded);
        Assert.Equal(unchanged + 4, Services.Audit.UnchangedReads);

        // And on screen: the very same row objects, the same selection, no note, not loading.
        Assert.True(rows.SequenceEqual(panel.Rows));
        Assert.Same(rows[3], panel.SelectedRow);
        Assert.Equal(string.Empty, panel.StatusNote);
        Assert.False(panel.IsLoading);
        Assert.Contains("of 120 matching events", panel.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refresh_after_a_commit_reads_again_and_lists_the_new_row()
    {
        var panel = PanelOver(10);
        await panel.InitializeAsync();
        var decoded = Services.Audit.RowsDecoded;

        AddRow("newest", _clock.GetUtcNow());
        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("newest", panel.Rows[0].Id);
        Assert.Equal(11, panel.Rows.Count);
        Assert.True(Services.Audit.RowsDecoded > decoded);
        Assert.Contains("of 11 matching events", panel.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refresh_that_reads_again_and_finds_the_same_rows_still_leaves_the_list_alone()
    {
        var panel = PanelOver(30);
        await panel.InitializeAsync();
        var rows = panel.Rows.ToArray();
        panel.SelectedRow = rows[5];
        var decoded = Services.Audit.RowsDecoded;

        // The database moved (the probe knows), but only outside the window: the same thirty events are the answer.
        AddRow("ancient", _clock.GetUtcNow().AddDays(-3));
        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.True(Services.Audit.RowsDecoded > decoded, "the reader had to read again");
        Assert.True(rows.SequenceEqual(panel.Rows), "the list is the one that was already on screen");
        Assert.Same(rows[5], panel.SelectedRow);
    }

    [Fact]
    public async Task A_refresh_after_load_more_starts_over_from_the_newest_page_as_it_always_did()
    {
        var panel = PanelOver(250);
        await panel.InitializeAsync();
        await panel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2 * AuditPanelViewModel.PageSize, panel.Rows.Count);

        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.True(panel.HasMore);

        // ... and the one after that, with nothing more done, is the unchanged case again.
        var rows = panel.Rows.ToArray();
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.True(rows.SequenceEqual(panel.Rows));
    }

    [Fact]
    public async Task A_new_time_range_that_finds_the_same_rows_still_says_which_range_it_is()
    {
        var panel = PanelOver(30);
        await panel.InitializeAsync();
        Assert.Contains("Last 24 hours", panel.ResultSummary, StringComparison.Ordinal);

        // Thirty events from the last minutes are the answer to "24 hours" and to "7 days" alike; the summary must not keep the old name.
        panel.SelectedRange = TimeRangeOption.All[2];
        await panel.LastLoad;

        Assert.Equal("Last 7 days", panel.SelectedRange.Label);
        Assert.Contains("Last 7 days", panel.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Last 24 hours", panel.ResultSummary, StringComparison.Ordinal);
        Assert.Equal(30, panel.Rows.Count);
    }

    [Fact]
    public async Task Changing_a_filter_and_changing_it_back_finds_the_remembered_answer()
    {
        var panel = PanelOver(60);
        await panel.InitializeAsync();
        var decoded = Services.Audit.RowsDecoded;

        panel.SearchText = "synthetic event 1";
        await panel.LastLoad;
        Assert.NotEmpty(panel.Rows);
        Assert.All(panel.Rows, row => Assert.Contains("synthetic event 1", row.Details, StringComparison.Ordinal));
        var afterSearch = Services.Audit.RowsDecoded;
        Assert.True(afterSearch > decoded);

        panel.SearchText = string.Empty;
        await panel.LastLoad;

        // The unfiltered page was remembered while the search was on screen: showing it again decodes nothing.
        Assert.Equal(afterSearch, Services.Audit.RowsDecoded);
        Assert.Equal(60, panel.Rows.Count);
    }

    // ------------------------------------------------------------------ the time window

    [Fact]
    public async Task The_window_starts_on_the_whole_minute_it_falls_in()
    {
        var panel = PanelOver(0);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var now = _clock.GetUtcNow();
        Assert.Equal(30, now.Second);

        // "The last 24 hours" at 12:00:30 starts at 12:00:00 yesterday: ten seconds past it is in, ten seconds before it is out.
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddDays(-1);
        AddRow("inside", start.AddSeconds(10));
        AddRow("outside", start.AddSeconds(-10));
        AddRow("recent", now.AddMinutes(-5));

        await panel.InitializeAsync();

        Assert.Equal(new[] { "recent", "inside" }, panel.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Refreshes_inside_a_minute_share_an_answer_and_one_past_it_asks_again()
    {
        var panel = PanelOver(20);
        await panel.InitializeAsync();
        var decoded = Services.Audit.RowsDecoded;

        _clock.Advance(TimeSpan.FromSeconds(20));
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(decoded, Services.Audit.RowsDecoded);

        // The window now starts a minute later: a different question, so the database is read - and finds the same rows.
        _clock.Advance(TimeSpan.FromSeconds(70));
        var rows = panel.Rows.ToArray();
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.True(Services.Audit.RowsDecoded > decoded);
        Assert.True(rows.SequenceEqual(panel.Rows));
    }

    // ------------------------------------------------------------------ too large to display

    [Fact]
    public async Task A_newest_row_over_the_limit_is_listed_with_the_reason_not_shown_as_no_events()
    {
        var panel = PanelOver(0);
        AddRow("huge", _clock.GetUtcNow().AddSeconds(-5), details: Big(300_000));

        await panel.InitializeAsync();

        // The acceptance: one oversized row and nothing else is "1 event too large to display", never the empty state.
        var row = Assert.Single(panel.Rows);
        Assert.False(panel.IsEmpty);
        Assert.Equal("1 event too large to display", panel.StatusNote);
        Assert.True(row.IsOversized);
        Assert.Equal("huge", row.Id);
        Assert.Equal("Too large to display: details is 300,000 bytes, over the 256 KB limit", row.Summary);
        Assert.Equal(row.OversizedNotice, row.Summary);
        Assert.Contains(row.Fields, field => field.Name == "details" && field.Value.Contains("300,000 bytes, over the 256 KB limit", StringComparison.Ordinal));
        Assert.Contains("1 of 1 matching events", panel.ResultSummary, StringComparison.Ordinal);

        // The rest of the row is there.
        Assert.Equal("hook_decision", row.Action);
        Assert.Equal("HIGH", row.Severity);
        Assert.Equal("claudecode", row.Connector);
    }

    [Fact]
    public async Task A_structured_json_over_the_limit_says_so_where_the_json_would_be()
    {
        var panel = PanelOver(0);
        var json = "{\"pad\":\"" + Big(300_000) + "\"}";
        AddRow("wide", _clock.GetUtcNow().AddSeconds(-5), details: "kept", structured: json);

        await panel.InitializeAsync();

        var row = Assert.Single(panel.Rows);
        Assert.Equal("1 event too large to display", panel.StatusNote);
        Assert.Equal("kept", row.Summary);

        // The JSON box scrolls sideways rather than wraps: the placeholder is broken into lines that fit it.
        Assert.Equal("(not shown: structured_json is\n300,010 bytes, over the 256 KB limit)", row.StructuredJson);
    }

    [Fact]
    public async Task The_note_counts_every_listed_event_that_is_too_large_and_leaves_the_readable_ones_alone()
    {
        var panel = PanelOver(3);
        AddRow("big-1", _clock.GetUtcNow().AddSeconds(-30), details: Big(OverLimit));
        AddRow("big-2", _clock.GetUtcNow().AddSeconds(-31), details: "x", structured: "{\"pad\":\"" + Big(OverLimit) + "\"}");

        await panel.InitializeAsync();

        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal("2 events too large to display", panel.StatusNote);
        Assert.Equal(new[] { "big-1", "big-2" }, panel.Rows.Where(r => r.IsOversized).Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.All(panel.Rows.Where(r => !r.IsOversized), row => Assert.Contains("synthetic event", row.Summary, StringComparison.Ordinal));
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public async Task The_note_follows_the_filter_it_is_about_the_rows_on_screen()
    {
        var panel = PanelOver(4);
        AddRow("big", _clock.GetUtcNow().AddSeconds(-30), details: Big(OverLimit));
        await panel.InitializeAsync();
        Assert.Equal("1 event too large to display", panel.StatusNote);

        panel.SearchText = "synthetic event";
        await panel.LastLoad;
        Assert.Equal(string.Empty, panel.StatusNote);
        Assert.Equal(4, panel.Rows.Count);
    }

    [Fact]
    public async Task A_refresh_of_an_unchanged_database_keeps_the_note()
    {
        var panel = PanelOver(2);
        AddRow("big", _clock.GetUtcNow().AddSeconds(-30), details: Big(OverLimit));
        await panel.InitializeAsync();

        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("1 event too large to display", panel.StatusNote);
        Assert.Equal(3, panel.Rows.Count);
    }

    [Fact]
    public async Task The_export_writes_the_whole_value_the_panel_could_not_show()
    {
        var panel = PanelOver(0);
        AddRow("huge", _clock.GetUtcNow().AddSeconds(-5), details: Big(300_000));
        await panel.InitializeAsync();
        var path = Path.Combine(_temp.Path, "out.json");
        panel.ExportPathPicker = () => path;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var exported = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(300_000, exported.GetProperty("details").GetString()!.Length);
        Assert.Contains("Exported 1 matching events", panel.ExportNote, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the archive (CUST-299) still works, and says the same

    [Fact]
    public async Task An_archive_lists_a_too_large_row_with_its_reason_creates_nothing_beside_it_and_a_refresh_leaves_the_list_alone()
    {
        using var archiveDirectory = new TempDirectory();
        var archive = Path.Combine(archiveDirectory.Path, "audit-archive.db");
        var archiveNewest = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
        AuditTestDatabase.Create(archive, 6, newest: archiveNewest, idPrefix: "arc-");
        AddRowTo(archive, "arc-huge", archiveNewest.AddMinutes(1), details: Big(280_000));
        var before = Directory.GetFileSystemEntries(archiveDirectory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive));

        var panel = PanelOver(5);
        Assert.True(Services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = archive } }));
        await panel.InitializeAsync();
        Assert.Equal(string.Empty, panel.StatusNote);

        panel.SourceKey = AuditPanelViewModel.SourceArchive;
        await panel.LastLoad;

        Assert.True(panel.IsArchive);
        Assert.Equal(7, panel.Rows.Count);
        var huge = Assert.Single(panel.Rows, row => row.IsOversized);
        Assert.Equal("arc-huge", huge.Id);
        Assert.Equal("1 event too large to display", panel.StatusNote);

        // Refresh re-checks the file and reads it again, finds the very same events, and leaves what is on screen where it is.
        var rows = panel.Rows.ToArray();
        panel.SelectedRow = rows[2];
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.True(rows.SequenceEqual(panel.Rows));
        Assert.Same(rows[2], panel.SelectedRow);
        Assert.Equal("1 event too large to display", panel.StatusNote);

        // Going back to Live shows the live log, with its own (empty) note.
        panel.SourceKey = AuditPanelViewModel.SourceLive;
        await panel.LastLoad;
        Assert.False(panel.IsArchive);
        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(string.Empty, panel.StatusNote);

        // Nothing was written or created beside the archive.
        Assert.Equal(before, Directory.GetFileSystemEntries(archiveDirectory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive)));
    }
}
