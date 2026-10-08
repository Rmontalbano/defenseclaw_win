using System.Globalization;
using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel's Live | Archive source (CUST-299) over synthetic schema-29 databases: a "live" audit.db inside the scratch data
/// directory and an "archive" in a folder of its own. Nothing here touches a real install.
/// </summary>
public sealed class AuditArchiveSourceTests : IDisposable
{
    private static readonly DateTimeOffset ArchiveNewest = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _live = new();
    private readonly TempDirectory _archiveDir = new();
    private readonly AppServices _services;

    public AuditArchiveSourceTests()
    {
        AuditTestDatabase.Create(Path.Combine(_live.Path, "audit.db"), 40);
        _services = TestServices.Create(_live);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _live.Dispose();
        _archiveDir.Dispose();
    }

    private string ArchivePath => Path.Combine(_archiveDir.Path, "audit-2026-03-02.db");

    /// <summary>A 0.8.10-shaped archive: the genuine DDL, schema_version 29, <paramref name="rows"/> old events (every one about the same target).</summary>
    private string MakeArchive(int rows = 25)
    {
        AuditTestDatabase.Create(ArchivePath, rows, newest: ArchiveNewest, idPrefix: "arc-");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ArchivePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit_events SET target = 'pkg-a'; INSERT INTO schema_version (version, applied_at) VALUES (29, '2026-02-01T00:00:00Z')";
            _ = command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return ArchivePath;
    }

    private void Configure(string? path) =>
        Assert.True(_services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = path } }));

    private AuditPanelViewModel Panel() => new(_services);

    /// <summary>Leaves the panel and comes back to it, which is when it re-reads the archive path from settings.</summary>
    private static void Reactivate(AuditPanelViewModel panel)
    {
        panel.SetActive(false);
        panel.SetActive(true);
    }

    private static async Task ShowArchiveAsync(AuditPanelViewModel panel)
    {
        panel.SourceKey = AuditPanelViewModel.SourceArchive;
        await panel.LastLoad;
    }

    private string[] ArchiveFolder() =>
        Directory.GetFileSystemEntries(_archiveDir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // ------------------------------------------------------------------ the switch

    [Fact]
    public async Task Without_an_archive_in_settings_there_is_no_switch_and_the_panel_is_the_live_log()
    {
        var panel = Panel();
        await panel.InitializeAsync();

        Assert.False(panel.HasArchive);
        Assert.False(panel.ShowSourceSwitch);
        Assert.False(panel.IsArchive);
        Assert.False(panel.ShowArchiveBanner);
        Assert.Equal(40, panel.Rows.Count);
    }

    [Fact]
    public async Task An_archive_in_settings_shows_the_switch_and_choosing_it_lists_the_archives_events_under_the_banner()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        Assert.True(panel.ShowSourceSwitch);
        Assert.Equal(40, panel.Rows.Count);
        Assert.StartsWith("evt-", panel.Rows[0].Id, StringComparison.Ordinal);

        await ShowArchiveAsync(panel);

        Assert.True(panel.IsArchive);
        Assert.True(panel.IsReadOnlySource);
        Assert.True(panel.ShowArchiveBanner);
        Assert.False(panel.ShowArchiveProblem);
        Assert.Equal(
            "Archived history, up to " + ArchiveNewest.AddSeconds(-1).ToLocalTime().ToString("f", CultureInfo.CurrentCulture),
            panel.ArchiveBanner);
        Assert.Equal(25, panel.Rows.Count);
        Assert.All(panel.Rows, row => Assert.StartsWith("arc-", row.Id, StringComparison.Ordinal));
        Assert.Contains("of 25 matching events", panel.ResultSummary, StringComparison.Ordinal);

        // The archive is months old: the live view's 24 hours would show nothing, so it opens on all time.
        Assert.Same(TimeRangeOption.AllTime, panel.SelectedRange);
        Assert.False(panel.IsEmpty);
        Assert.False(panel.IsLoading);

        // The filter lists were read from the archive.
        Assert.Contains("guardrail.evaluation", panel.Buckets);
    }

    [Fact]
    public async Task Switching_back_to_live_restores_the_live_events_the_default_range_and_hides_the_banner()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);
        Assert.Equal(25, panel.Rows.Count);

        panel.SourceKey = AuditPanelViewModel.SourceLive;
        await panel.LastLoad;

        Assert.False(panel.IsArchive);
        Assert.False(panel.IsReadOnlySource);
        Assert.False(panel.ShowArchiveBanner);
        Assert.Equal(40, panel.Rows.Count);
        Assert.All(panel.Rows, row => Assert.StartsWith("evt-", row.Id, StringComparison.Ordinal));
        Assert.Same(TimeRangeOption.Day, panel.SelectedRange);
        Assert.True(panel.ShowSourceSwitch);

        // ... and the archive can be chosen again.
        await ShowArchiveAsync(panel);
        Assert.Equal(25, panel.Rows.Count);
    }

    [Fact]
    public async Task Filters_and_paging_work_over_the_archive_and_do_not_leak_into_live()
    {
        Configure(MakeArchive(rows: 130));
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.True(panel.HasMore);
        panel.LoadMoreCommand.Execute(null);
        UiThread.WaitFor(() => panel.Rows.Count == 130 && !panel.IsLoading, "second archive page");

        panel.SearchText = "synthetic event 12";
        await panel.LastLoad;
        Assert.NotEmpty(panel.Rows);
        Assert.All(panel.Rows, row => Assert.Contains("synthetic event 12", row.Details, StringComparison.Ordinal));

        panel.SourceKey = AuditPanelViewModel.SourceLive;
        await panel.LastLoad;
        Assert.Equal(string.Empty, panel.SearchText);
        Assert.Equal(40, panel.Rows.Count);
    }

    [Fact]
    public async Task A_deep_link_to_the_audit_panel_means_the_live_log()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        panel.Accept(new AuditPreset("risk"));
        await panel.LastLoad;

        Assert.False(panel.IsArchive);
        Assert.Equal(AuditPanelViewModel.PresetRisk, panel.ActivePreset);
    }

    [Fact]
    public async Task Clearing_the_archive_in_settings_while_it_is_on_screen_returns_the_panel_to_live()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        Configure(null);
        Reactivate(panel);

        Assert.False(panel.HasArchive);
        Assert.False(panel.ShowSourceSwitch);
        Assert.False(panel.IsArchive);
        await panel.LastLoad;
        Assert.Equal(40, panel.Rows.Count);
    }

    [Fact]
    public async Task Pointing_settings_at_another_archive_while_it_is_on_screen_reloads_from_the_new_file()
    {
        Configure(MakeArchive(rows: 25));
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);
        Assert.Equal(25, panel.Rows.Count);

        var second = Path.Combine(_archiveDir.Path, "audit-other.db");
        AuditTestDatabase.Create(second, 7, newest: ArchiveNewest.AddDays(-30), idPrefix: "oth-");
        Configure(second);
        Reactivate(panel);
        await panel.LastLoad;

        Assert.True(panel.IsArchive);
        Assert.Equal(7, panel.Rows.Count);
        Assert.All(panel.Rows, row => Assert.StartsWith("oth-", row.Id, StringComparison.Ordinal));
        Assert.Equal(
            "Archived history, up to " + ArchiveNewest.AddDays(-30).AddSeconds(-1).ToLocalTime().ToString("f", CultureInfo.CurrentCulture),
            panel.ArchiveBanner);
    }

    // ------------------------------------------------------------------ errors are states, not crashes

    [Fact]
    public async Task A_missing_archive_is_an_error_state_with_the_reason_and_live_still_works()
    {
        Configure(Path.Combine(_archiveDir.Path, "gone.db"));
        var panel = Panel();
        await panel.InitializeAsync();

        await ShowArchiveAsync(panel);

        Assert.True(panel.IsArchive);
        Assert.True(panel.ShowArchiveProblem);
        Assert.False(panel.ShowArchiveBanner);
        Assert.Contains("does not exist", panel.ArchiveProblem, StringComparison.Ordinal);
        Assert.Empty(panel.Rows);
        Assert.True(panel.IsEmpty);
        Assert.Equal("Archive unavailable", panel.EmptyTitle);
        Assert.Equal(panel.ArchiveProblem, panel.EmptyDetail);
        Assert.False(panel.IsLoading);

        panel.SourceKey = AuditPanelViewModel.SourceLive;
        await panel.LastLoad;
        Assert.False(panel.HasArchiveProblem);
        Assert.Equal(40, panel.Rows.Count);
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_and_a_damaged_database_are_error_states()
    {
        var notDb = _archiveDir.WriteFile("notes.db", "definitely not a sqlite database, just some text");
        Configure(notDb);
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);
        Assert.Contains("not a SQLite database", panel.ArchiveProblem, StringComparison.Ordinal);
        Assert.Empty(panel.Rows);

        var bytes = File.ReadAllBytes(MakeArchive());
        for (var i = 100; i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }

        var damaged = Path.Combine(_archiveDir.Path, "damaged.db");
        File.WriteAllBytes(damaged, bytes);
        Configure(damaged);
        Reactivate(panel);
        await panel.LastLoad;

        Assert.True(panel.IsArchive);
        Assert.True(panel.ShowArchiveProblem);
        Assert.False(string.IsNullOrWhiteSpace(panel.ArchiveProblem));
        Assert.Empty(panel.Rows);
    }

    [Fact]
    public async Task An_archive_inside_the_live_data_folder_is_refused_with_the_reason_even_if_settings_names_it()
    {
        var inside = Path.Combine(_live.Path, "audit-old.db");
        File.Copy(MakeArchive(), inside);
        Configure(inside);
        var panel = Panel();
        await panel.InitializeAsync();

        await ShowArchiveAsync(panel);

        Assert.Contains("outside the live DefenseClaw folder", panel.ArchiveProblem, StringComparison.Ordinal);
        Assert.Empty(panel.Rows);
    }

    [Fact]
    public async Task An_archive_that_disappears_while_it_is_on_screen_becomes_an_error_on_refresh_and_recovers_when_it_is_back()
    {
        var path = MakeArchive();
        Configure(path);
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);
        Assert.Equal(25, panel.Rows.Count);

        var moved = path + ".moved";
        File.Move(path, moved);
        panel.RefreshCommand.Execute(null);
        await panel.LastLoad;
        Assert.True(panel.ShowArchiveProblem);
        Assert.Empty(panel.Rows);

        File.Move(moved, path);
        panel.RefreshCommand.Execute(null);
        await panel.LastLoad;
        Assert.False(panel.HasArchiveProblem);
        Assert.Equal(25, panel.Rows.Count);
    }

    [Fact]
    public async Task A_file_the_system_will_not_let_us_read_is_an_error_state_not_an_exception()
    {
        var path = MakeArchive();
        Configure(path);
        var panel = Panel();
        await panel.InitializeAsync();

        // Another program holds it with no sharing: the header read and the open both fail.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ShowArchiveAsync(panel);
            Assert.True(panel.ShowArchiveProblem);
            Assert.Empty(panel.Rows);
        }

        panel.RefreshCommand.Execute(null);
        await panel.LastLoad;
        Assert.False(panel.HasArchiveProblem);
        Assert.Equal(25, panel.Rows.Count);
    }

    // ------------------------------------------------------------------ read-only

    [Fact]
    public async Task Using_the_archive_leaves_the_file_and_its_folder_byte_for_byte_as_they_were()
    {
        var path = MakeArchive();
        Configure(path);
        var before = ArchiveFolder();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var written = File.GetLastWriteTimeUtc(path);

        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        // Everything the panel can do with it: filter, search, preset, run, select a row (related events), export.
        panel.SelectedSeverity = SeverityOption.All[^1];
        await panel.LastLoad;
        panel.SearchText = "synthetic";
        await panel.LastLoad;
        panel.ActivePreset = AuditPanelViewModel.PresetScans;
        await panel.LastLoad;
        panel.ResetFiltersCommand.Execute(null);
        await panel.LastLoad;
        panel.SelectedRow = panel.Rows[3];
        await panel.LastCorrelation;
        var exported = _archiveDir.File("export.json");
        panel.ExportPathPicker = () => exported;
        panel.ExportCommand.Execute(null);
        await panel.LastExport;
        SqliteConnection.ClearAllPools();

        var after = ArchiveFolder();
        Assert.DoesNotContain(after, name => name.EndsWith("-wal", StringComparison.Ordinal)
            || name.EndsWith("-shm", StringComparison.Ordinal)
            || name.EndsWith("-journal", StringComparison.Ordinal));
        Assert.Equal(before.Append("export.json").Order(StringComparer.Ordinal), after);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task The_inspector_relates_an_archived_event_to_other_archived_events()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        panel.SelectedRow = panel.Rows[0];
        await panel.LastCorrelation;

        Assert.NotEmpty(panel.RelatedEvents);
        Assert.All(panel.RelatedEvents, row => Assert.StartsWith("arc-", row.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_still_works_from_the_archive_and_writes_the_archives_events()
    {
        Configure(MakeArchive());
        var panel = Panel();
        await panel.InitializeAsync();
        await ShowArchiveAsync(panel);

        var csv = _archiveDir.File("archive.csv");
        panel.ExportPathPicker = () => csv;
        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        Assert.StartsWith("Exported 25 matching events", panel.ExportNote, StringComparison.Ordinal);
        var text = File.ReadAllText(csv);
        Assert.Contains("arc-000000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evt-0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_actions_are_off_exactly_while_the_archive_is_on_screen()
    {
        var panel = Panel();
        Assert.False(panel.IsReadOnlySource);

        panel.SourceKey = AuditPanelViewModel.SourceArchive;
        Assert.True(panel.IsReadOnlySource);

        panel.SourceKey = AuditPanelViewModel.SourceLive;
        Assert.False(panel.IsReadOnlySource);
    }
}
