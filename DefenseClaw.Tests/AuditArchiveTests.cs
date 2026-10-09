using System.Security.Cryptography;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The read-only archive source (CUST-299): a synthetic schema-29 <c>audit.db</c>, kept outside the live data folder, is checked and
/// read through an immutable connection that must leave the file and its folder exactly as they were.
/// </summary>
public sealed class AuditArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _archive = new();
    private readonly TempDirectory _live = new("dcw-live");
    private readonly string _folder;

    public AuditArchiveTests()
    {
        _folder = System.IO.Path.GetDirectoryName(_archive.Path)!;
        _archive.InsertEvent("old-1", Base.AddMinutes(1), "hook_decision", "INFO", "guardrail.evaluation", "claudecode", details: "first");
        _archive.InsertEvent("old-2", Base.AddMinutes(2), "scan-finding", "HIGH", "security.finding", "claudecode", details: "second", target: "pkg-a");
        _archive.InsertEvent("old-3", Base.AddMinutes(3), "sidecar-stop", "INFO", "platform.health", null, details: "third");

        // What 0.8.10 records: schema version 29.
        using (var connection = _archive.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES (29, '2026-02-01T00:00:00Z')";
            _ = command.ExecuteNonQuery();
        }

        SqlitePools.Release(_folder);
    }

    public void Dispose()
    {
        SqlitePools.Release(_folder);
        _archive.Dispose();
        _live.Dispose();
    }

    private string[] Listing() => Directory.GetFileSystemEntries(_folder).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private string Hash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_archive.Path)));

    [Fact]
    public async Task A_schema_29_archive_is_accepted_and_reports_its_newest_event_and_version()
    {
        var check = await AuditArchive.InspectAsync(_archive.Path, _live.Path);

        Assert.True(check.IsUsable, check.Problem);
        Assert.Equal(Base.AddMinutes(3), check.Newest);
        Assert.Equal(29, check.SchemaVersion);
        Assert.Equal(System.IO.Path.GetFullPath(_archive.Path), check.FullPath);
    }

    [Fact]
    public async Task The_immutable_reader_returns_the_archives_rows_newest_first_with_counts_and_filter_lists()
    {
        var reader = AuditArchive.OpenReader(_archive.Path);

        var page = await reader.QueryAsync(new AuditQuery { Limit = 10 });
        Assert.Equal(new[] { "old-3", "old-2", "old-1" }, page.Events.Select(e => e.Id));
        Assert.Equal(3, await reader.CountAsync(new AuditQuery { Limit = 10 }));
        Assert.Contains("security.finding", await reader.ListBucketsAsync());
        Assert.Equal("old-2", (await reader.GetByIdAsync("old-2"))?.Id);
        Assert.True(reader.IsImmutable);

        var related = new AuditCorrelationReader(_archive.Path, immutable: true);
        Assert.True(related.IsImmutable);
        _ = await related.RelatedAsync(page.Events.First(e => e.Id == "old-2"));
    }

    [Fact]
    public async Task Reading_an_archive_changes_neither_the_file_nor_its_folder()
    {
        var before = Listing();
        var hash = Hash();
        var modified = File.GetLastWriteTimeUtc(_archive.Path);

        var check = await AuditArchive.InspectAsync(_archive.Path, _live.Path);
        var reader = AuditArchive.OpenReader(_archive.Path);
        _ = await reader.QueryAsync(new AuditQuery { Limit = 10 });
        _ = await reader.CountBySeverityAsync(new AuditQuery { Limit = 10 });
        _ = await reader.ListConnectorsAsync();
        _ = await reader.ListActionsAsync();
        _ = await new AuditCorrelationReader(_archive.Path, immutable: true).RelatedAsync(
            (await reader.GetByIdAsync("old-2"))!);
        SqlitePools.Release(_folder);

        Assert.True(check.IsUsable, check.Problem);
        Assert.Equal(before, Listing());
        Assert.DoesNotContain(Listing(), name => name.EndsWith("-wal", StringComparison.Ordinal)
            || name.EndsWith("-shm", StringComparison.Ordinal)
            || name.EndsWith("-journal", StringComparison.Ordinal));
        Assert.Equal(hash, Hash());
        Assert.Equal(modified, File.GetLastWriteTimeUtc(_archive.Path));
    }

    [Fact]
    public async Task An_archive_left_in_wal_mode_is_still_read_without_creating_sidecars()
    {
        using (var connection = _archive.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL";
            _ = command.ExecuteScalar();
        }

        SqlitePools.Release(_folder);
        var before = Listing();

        var check = await AuditArchive.InspectAsync(_archive.Path, _live.Path);
        var page = await AuditArchive.OpenReader(_archive.Path).QueryAsync(new AuditQuery { Limit = 10 });
        SqlitePools.Release(_folder);

        Assert.True(check.IsUsable, check.Problem);
        Assert.Equal(3, page.Events.Count);
        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task A_read_only_file_is_readable()
    {
        File.SetAttributes(_archive.Path, FileAttributes.ReadOnly);
        try
        {
            var check = await AuditArchive.InspectAsync(_archive.Path, _live.Path);
            Assert.True(check.IsUsable, check.Problem);
        }
        finally
        {
            File.SetAttributes(_archive.Path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task A_path_inside_the_live_data_folder_is_refused_with_the_reason()
    {
        var inside = _live.File("audit-old.db");
        File.Copy(_archive.Path, inside);

        var check = await AuditArchive.InspectAsync(inside, _live.Path);
        Assert.False(check.IsUsable);
        Assert.Contains("outside the live DefenseClaw folder", check.Problem, StringComparison.Ordinal);

        // The same file, spelled with a different case and a dot segment.
        var spelled = _live.Path.ToUpperInvariant() + System.IO.Path.DirectorySeparatorChar + "." + System.IO.Path.DirectorySeparatorChar + "AUDIT-OLD.DB";
        Assert.False((await AuditArchive.InspectAsync(spelled, _live.Path)).IsUsable);

        // The live audit.db itself.
        var live = _live.File("audit.db");
        File.Copy(_archive.Path, live);
        Assert.False((await AuditArchive.InspectAsync(live, _live.Path)).IsUsable);
    }

    [Fact]
    public async Task A_folder_named_like_the_live_one_but_not_inside_it_is_allowed()
    {
        // "<live>-archive" shares a prefix with "<live>" but is a sibling, not a child.
        var sibling = _live.Path + "-archive";
        _ = Directory.CreateDirectory(sibling);
        try
        {
            var copy = System.IO.Path.Combine(sibling, "audit.db");
            File.Copy(_archive.Path, copy);
            Assert.True((await AuditArchive.InspectAsync(copy, _live.Path)).IsUsable);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("audit.db")]
    [InlineData("..\\audit.db")]
    public async Task A_blank_or_relative_path_is_refused(string path)
    {
        var check = await AuditArchive.InspectAsync(path, _live.Path);
        Assert.False(check.IsUsable);
        Assert.False(string.IsNullOrWhiteSpace(check.Problem));
    }

    [Fact]
    public async Task A_missing_file_a_folder_and_a_non_sqlite_file_are_each_refused_with_their_own_reason()
    {
        var missing = await AuditArchive.InspectAsync(System.IO.Path.Combine(_folder, "nope.db"), _live.Path);
        Assert.Contains("does not exist", missing.Problem, StringComparison.Ordinal);

        var folder = await AuditArchive.InspectAsync(_folder, _live.Path);
        Assert.Contains("folder", folder.Problem, StringComparison.Ordinal);

        var text = System.IO.Path.Combine(_folder, "notes.db");
        File.WriteAllText(text, "this is not a database, but it is longer than sixteen bytes");
        var notDb = await AuditArchive.InspectAsync(text, _live.Path);
        Assert.Contains("not a SQLite database", notDb.Problem, StringComparison.Ordinal);

        var tiny = System.IO.Path.Combine(_folder, "tiny.db");
        File.WriteAllText(tiny, "x");
        Assert.Contains("not a SQLite database", (await AuditArchive.InspectAsync(tiny, _live.Path)).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sqlite_file_without_the_audit_tables_is_refused_with_the_engines_message()
    {
        var other = System.IO.Path.Combine(_folder, "other.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = other, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (a INTEGER)";
            _ = command.ExecuteNonQuery();
        }

        var check = await AuditArchive.InspectAsync(other, _live.Path);

        Assert.False(check.IsUsable);
        Assert.Contains("audit_events", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_database_is_an_error_not_an_exception()
    {
        SqlitePools.Release(_folder);
        var bytes = File.ReadAllBytes(_archive.Path);
        var corrupt = System.IO.Path.Combine(_folder, "corrupt.db");

        // A valid header, then garbage over the pages that hold the tables.
        var damaged = (byte[])bytes.Clone();
        for (var i = 100; i < damaged.Length; i++)
        {
            damaged[i] = 0xFF;
        }

        File.WriteAllBytes(corrupt, damaged);

        var check = await AuditArchive.InspectAsync(corrupt, _live.Path);

        Assert.False(check.IsUsable);
        Assert.False(string.IsNullOrWhiteSpace(check.Problem));
    }

    [Fact]
    public async Task An_archive_with_no_events_is_usable_and_has_no_newest_timestamp()
    {
        using var empty = new TestAuditDatabase();

        var check = await AuditArchive.InspectAsync(empty.Path, _live.Path);

        Assert.True(check.IsUsable, check.Problem);
        Assert.Null(check.Newest);
    }

    [Fact]
    public void The_immutable_connection_string_is_a_read_only_immutable_file_uri_that_pools_nothing()
    {
        var text = AuditReader.BuildImmutableConnectionString(@"C:\Archive Folder\audit #1.db");

        Assert.Contains("file:///C:/Archive%20Folder/audit%20%231.db?mode=ro&immutable=1", text, StringComparison.Ordinal);
        Assert.Contains("Mode=ReadOnly", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pooling=False", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_archive_whose_name_has_spaces_and_a_hash_can_be_read()
    {
        var odd = System.IO.Path.Combine(_folder, "my archive #1.db");
        SqlitePools.Release(_folder);
        File.Copy(_archive.Path, odd);

        var check = await AuditArchive.InspectAsync(odd, _live.Path);

        Assert.True(check.IsUsable, check.Problem);
        Assert.Equal(Base.AddMinutes(3), check.Newest);
    }
}
