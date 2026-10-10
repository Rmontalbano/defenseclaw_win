using System.Globalization;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-279: the block tally survives a restart. Every test works on a synthetic audit.db and a cache folder of its own under a temp
/// directory (the store is handed the folder; nothing here can reach the real <c>%LOCALAPPDATA%</c> or <c>~/.defenseclaw</c>).
/// </summary>
public sealed class HookTotalsCacheTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TestTimeouts.Ceiling;

    private readonly TestAuditDatabase _database = new();
    private readonly TempDirectory _cacheRoot = new("dcw-hooktotals-cache");
    private int _n;

    public void Dispose()
    {
        _database.Dispose();
        _cacheRoot.Dispose();
    }

    private string CacheDirectory => _cacheRoot.File("cache");

    private HookTotalsCacheStore Store() => new(CacheDirectory);

    private ConnectorHookTotalsReader Reader(int chunk = 10, int? maxChunks = null)
    {
        var reader = new ConnectorHookTotalsReader(_database.Path, chunk, Store());
        if (maxChunks is { } limit)
        {
            reader.MaxChunksPerRead = limit;
        }

        return reader;
    }

    private static string Hook(string action) =>
        $"connector=claudecode result=ok action={action} raw_action={action} severity=NONE mode=action";

    private void Add(int allow, int block, string connector = "claudecode")
    {
        for (var i = 0; i < allow + block; i++)
        {
            _database.InsertEvent(
                $"row-{_n++:D6}",
                Base.AddSeconds(_n),
                "connector-hook",
                connector: connector,
                details: Hook(i < block ? "block" : "allow"));
        }
    }

    private async Task<ConnectorHookTotals> CatchUpAsync(ConnectorHookTotalsReader reader)
    {
        ConnectorHookTotals totals;
        do
        {
            totals = await reader.ReadAsync(Patience);
        }
        while (!totals.BlocksComplete);

        return totals;
    }

    // ---- Resuming ----

    [Fact]
    public async Task A_second_launch_has_exact_blocks_in_one_read_without_scanning_again()
    {
        Add(allow: 60, block: 15);
        var first = await CatchUpAsync(Reader(maxChunks: 1));
        Assert.Equal(15, first.Fleet.Blocks);
        Assert.True(File.Exists(Store().FilePath));

        // A new reader (a new process) allowed one 10-row chunk per read: a rescan of 75 rows could not finish in one read.
        var second = await Reader(maxChunks: 1).ReadAsync(Patience);

        Assert.True(second.BlocksComplete);
        Assert.Equal(15, second.Fleet.Blocks);
        Assert.Equal(75, second.Fleet.Calls);
        Assert.Equal(75, second.BlocksScanned);
    }

    [Fact]
    public async Task A_second_launch_scans_only_the_rows_added_since()
    {
        Add(allow: 40, block: 5);
        _ = await CatchUpAsync(Reader(maxChunks: 1));

        Add(allow: 3, block: 4);   // landed while the app was closed
        var second = await Reader(maxChunks: 1).ReadAsync(Patience);

        Assert.True(second.BlocksComplete);
        Assert.Equal(9, second.Fleet.Blocks);
        Assert.Equal(52, second.Fleet.Calls);
    }

    [Fact]
    public async Task The_tally_is_saved_after_every_completed_slice_not_only_at_the_end()
    {
        Add(allow: 50, block: 0);

        _ = await Reader(maxChunks: 1).ReadAsync(Patience);   // one slice of 10 rows, catch-up unfinished

        var saved = Store().Load();
        Assert.NotNull(saved);
        Assert.Equal(10, saved!.Scanned);
    }

    // ---- Discarding ----

    [Fact]
    public async Task A_database_rebuilt_in_place_has_a_new_creation_time_and_the_cache_is_discarded()
    {
        Add(allow: 30, block: 6);
        _ = await CatchUpAsync(Reader());
        Retarget(fingerprint => Regex.Replace(fingerprint, "\"CreatedUtcTicks\":\\d+", "\"CreatedUtcTicks\":1"));

        var reader = Reader(maxChunks: 1);
        var first = await reader.ReadAsync(Patience);

        Assert.False(first.BlocksComplete);   // started over: ten rows of thirty-six
        Assert.Equal(6, (await CatchUpAsync(reader)).Fleet.Blocks);
    }

    [Fact]
    public async Task Another_database_path_or_schema_version_discards_the_cache()
    {
        Add(allow: 30, block: 6);
        _ = await CatchUpAsync(Reader());

        Retarget(text => Regex.Replace(text, "\"Path\":\"[^\"]*\"", "\"Path\":\"C:\\\\elsewhere\\\\audit.db\""));
        Assert.False((await Reader(maxChunks: 1).ReadAsync(Patience)).BlocksComplete);

        _ = await CatchUpAsync(Reader());   // rewrites the cache for this database
        Retarget(text => Regex.Replace(text, "\"SchemaVersion\":\\d+", "\"SchemaVersion\":-1"));
        Assert.False((await Reader(maxChunks: 1).ReadAsync(Patience)).BlocksComplete);
    }

    [Fact]
    public async Task Rows_pruned_by_retention_make_the_cache_ahead_of_the_database_and_it_is_discarded()
    {
        Add(allow: 40, block: 10);
        _ = await CatchUpAsync(Reader());

        using (var connection = _database.OpenWritable())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM audit_events WHERE rowid IN (SELECT rowid FROM audit_events WHERE action = 'connector-hook' ORDER BY rowid LIMIT 20)";
            _ = command.ExecuteNonQuery();
        }

        var totals = await CatchUpAsync(Reader());

        Assert.Equal(30, totals.Fleet.Calls);
        Assert.Equal(totals.Fleet.Calls, totals.BlocksScanned);
        Assert.Equal(CountBlocksInDatabase(), totals.Fleet.Blocks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all {{{")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":99,\"Path\":\"x\",\"Blocks\":{},\"MarkTimestamp\":\"\",\"MarkRowId\":-1}")]
    [InlineData("null")]
    public async Task A_corrupt_cache_falls_back_to_a_full_catch_up_without_an_error(string content)
    {
        Add(allow: 30, block: 6);
        _ = Directory.CreateDirectory(CacheDirectory);
        await File.WriteAllTextAsync(Store().FilePath, content);

        var reader = Reader(maxChunks: 1);
        var first = await reader.ReadAsync(Patience);

        Assert.False(first.BlocksComplete);
        Assert.Equal(6, (await CatchUpAsync(reader)).Fleet.Blocks);
        Assert.NotNull(Store().Load());   // and the cache is whole again
    }

    [Fact]
    public async Task A_deleted_cache_is_a_full_catch_up_and_is_written_again()
    {
        Add(allow: 30, block: 6);
        _ = await CatchUpAsync(Reader());
        File.Delete(Store().FilePath);

        var reader = Reader(maxChunks: 1);
        Assert.False((await reader.ReadAsync(Patience)).BlocksComplete);
        Assert.Equal(6, (await CatchUpAsync(reader)).Fleet.Blocks);
        Assert.True(File.Exists(Store().FilePath));
    }

    [Fact]
    public async Task A_cache_folder_that_cannot_be_written_does_not_fail_the_read()
    {
        Add(allow: 10, block: 2);
        var blocker = _cacheRoot.File("blocked");
        await File.WriteAllTextAsync(blocker, "a file where the folder should be");

        var reader = new ConnectorHookTotalsReader(_database.Path, 10, new HookTotalsCacheStore(Path.Combine(blocker, "cache")));
        var totals = await CatchUpAsync(reader);

        Assert.Equal(2, totals.Fleet.Blocks);
    }

    // ---- The store ----

    [Fact]
    public void The_store_round_trips_and_leaves_no_temp_file_behind()
    {
        var state = new HookTotalsCacheState(
            new HookTotalsFingerprint(_database.Path, 123, 7),
            "2026-09-30T12:00:00.0000000Z",
            42,
            100,
            new Dictionary<string, long> { ["claudecode"] = 9, ["codex"] = 1 });

        var store = Store();
        Assert.True(store.Save(state));
        Assert.True(store.Save(state));   // replacing an existing file

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded!.Fingerprint.Matches(state.Fingerprint));
        Assert.Equal(42, loaded.MarkRowId);
        Assert.Equal(100, loaded.Scanned);
        Assert.Equal(9, loaded.Blocks["CLAUDECODE"]);
        var files = Directory.GetFiles(CacheDirectory);
        Assert.Single(files);
        Assert.Equal(HookTotalsCacheStore.FileName, Path.GetFileName(files[0]));
    }

    [Fact]
    public void The_store_refuses_a_folder_under_the_DefenseClaw_data_directory()
    {
        var home = Path.Combine(_cacheRoot.Path, ".defenseclaw", "cache");

        _ = Assert.Throws<ArgumentException>(() => new HookTotalsCacheStore(home));
    }

    [Fact]
    public async Task A_reader_without_a_cache_writes_nothing()
    {
        Add(allow: 10, block: 2);

        _ = await new ConnectorHookTotalsReader(_database.Path, 10).ReadAsync(Patience);

        Assert.False(Directory.Exists(CacheDirectory));
    }

    // ---- helpers ----

    private void Retarget(Func<string, string> edit)
    {
        var path = Store().FilePath;
        File.WriteAllText(path, edit(File.ReadAllText(path)));
    }

    private long CountBlocksInDatabase()
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action = 'connector-hook' AND details LIKE '%action=block%'";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
