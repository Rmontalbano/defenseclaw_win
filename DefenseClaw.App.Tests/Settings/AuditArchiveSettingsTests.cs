using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// Settings -> Audit archive (CUST-299): the path is checked before it is saved, a refused path says why and saves nothing, and
/// Clear forgets the path without touching the file.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AuditArchiveSettingsTests : IDisposable
{
    private readonly TempDirectory _live = new();
    private readonly TempDirectory _archiveDir = new();
    private readonly AppServices _services;

    public AuditArchiveSettingsTests()
    {
        _services = TestServices.Create(_live);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_live.Path);
        SqlitePools.Release(_archiveDir.Path);
        _live.Dispose();
        _archiveDir.Dispose();
    }

    private string MakeArchive(string name = "audit-old.db")
    {
        var path = Path.Combine(_archiveDir.Path, name);
        AuditTestDatabase.Create(path, 12, newest: new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero), idPrefix: "arc-");
        return path;
    }

    private SettingsPanelViewModel Build(FakePlatform? platform = null) =>
        UiThread.Run(() => new SettingsPanelViewModel(_services, hooks: null, (platform ?? new FakePlatform()).Build()));

    private string? SavedPath() => AppSettingsStore.OpenFresh(_services.Settings.FilePath).Current.Archive.Path;

    [Fact]
    public async Task A_valid_archive_is_checked_saved_and_shown_in_the_form()
    {
        var path = MakeArchive();
        var vm = Build();
        Assert.Equal(string.Empty, vm.ArchivePathText);
        Assert.False(vm.ClearArchiveCommand.CanExecute(null));

        vm.ArchivePathText = "  " + path + "  ";
        await vm.ApplyArchiveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.ArchiveProblem);
        Assert.StartsWith("Saved.", vm.ArchiveMessage, StringComparison.Ordinal);
        Assert.Contains("Newest archived event", vm.ArchiveMessage, StringComparison.Ordinal);
        Assert.Equal(path, SavedPath());
        Assert.Equal(path, _services.Settings.Current.Archive.Path);
        Assert.True(vm.ClearArchiveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_path_inside_the_live_data_folder_is_refused_with_the_reason_and_nothing_is_saved()
    {
        var inside = Path.Combine(_live.Path, "audit-copy.db");
        File.Copy(MakeArchive(), inside);
        var vm = Build();

        vm.ArchivePathText = inside;
        await vm.ApplyArchiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasArchiveProblem);
        Assert.Contains("outside the live DefenseClaw folder", vm.ArchiveProblem, StringComparison.Ordinal);
        Assert.Null(SavedPath());
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\audit.db")]
    [InlineData("missing")]
    [InlineData("folder")]
    [InlineData("text")]
    public async Task A_blank_relative_missing_folder_or_non_sqlite_path_is_refused_and_keeps_what_was_saved(string kind)
    {
        var good = MakeArchive();
        var vm = Build();
        vm.ArchivePathText = good;
        await vm.ApplyArchiveCommand.ExecuteAsync(null);
        Assert.Equal(good, SavedPath());

        vm.ArchivePathText = kind switch
        {
            "missing" => Path.Combine(_archiveDir.Path, "nope.db"),
            "folder" => _archiveDir.Path,
            "text" => _archiveDir.WriteFile("notes.db", "plain text, not sqlite at all"),
            _ => kind,
        };
        await vm.ApplyArchiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasArchiveProblem);
        Assert.False(string.IsNullOrWhiteSpace(vm.ArchiveProblem));
        Assert.Equal(good, SavedPath());
    }

    [Fact]
    public async Task A_sqlite_file_that_is_not_a_defenseclaw_audit_database_is_refused()
    {
        var other = Path.Combine(_archiveDir.Path, "other.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = other, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (a INTEGER)";
            _ = command.ExecuteNonQuery();
        }

        var vm = Build();
        vm.ArchivePathText = other;
        await vm.ApplyArchiveCommand.ExecuteAsync(null);

        Assert.Contains("audit_events", vm.ArchiveProblem, StringComparison.Ordinal);
        Assert.Null(SavedPath());
    }

    [Fact]
    public async Task Clear_forgets_the_path_and_leaves_the_file_alone()
    {
        var path = MakeArchive();
        var vm = Build();
        vm.ArchivePathText = path;
        await vm.ApplyArchiveCommand.ExecuteAsync(null);

        vm.ClearArchiveCommand.Execute(null);

        Assert.Null(SavedPath());
        Assert.Equal(string.Empty, vm.ArchivePathText);
        Assert.False(vm.ClearArchiveCommand.CanExecute(null));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Browse_fills_the_field_without_saving_and_starts_in_the_current_folder()
    {
        var path = MakeArchive();
        var platform = new FakePlatform { PickedArchive = path };
        var vm = Build(platform);
        vm.ArchivePathText = Path.Combine(_archiveDir.Path, "old-choice.db");

        vm.BrowseArchiveCommand.Execute(null);

        Assert.Equal(_archiveDir.Path, platform.ArchivePickStart);
        Assert.Equal(path, vm.ArchivePathText);
        Assert.Null(SavedPath());

        platform.PickedArchive = null;
        vm.BrowseArchiveCommand.Execute(null);
        Assert.Equal(path, vm.ArchivePathText);
    }

    [Fact]
    public void A_saved_archive_is_in_the_form_when_the_page_opens()
    {
        var path = MakeArchive();
        Assert.True(_services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = path } }));

        var vm = Build();

        Assert.Equal(path, vm.ArchivePathText);
        Assert.True(vm.ClearArchiveCommand.CanExecute(null));
    }
}
