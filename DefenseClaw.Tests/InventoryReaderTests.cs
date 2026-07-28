using DefenseClaw.Core.Inventory;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// inventory.db has no published schema contract, so the reader discovers it at runtime.
/// These tests use a synthetic database shaped like the live one (ai_signals, ai_scans,
/// and an ai_components_v view) without copying any real inventory data.
/// </summary>
public class InventoryReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new("dcw-inventory");
    private readonly InventoryReader _reader;

    public InventoryReaderTests()
    {
        var path = _directory.File("inventory.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ai_signals (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    name TEXT,
                    confidence REAL,
                    last_seen TEXT
                );
                CREATE TABLE ai_scans (id TEXT PRIMARY KEY, started_at TEXT);
                CREATE VIEW ai_components_v AS SELECT id, name, confidence FROM ai_signals;

                INSERT INTO ai_signals VALUES ('sig-1', 'sdk', 'anthropic-sdk', 0.9, '2026-07-28T12:00:00Z');
                INSERT INTO ai_signals VALUES ('sig-2', 'agent', 'claude-code', 0.75, '2026-07-28T12:05:00Z');
                INSERT INTO ai_signals VALUES ('sig-3', 'sdk', NULL, NULL, NULL);
                INSERT INTO ai_scans VALUES ('scan-1', '2026-07-28T12:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        _reader = new InventoryReader(path);
    }

    [Fact]
    public async Task Discovers_tables_and_views_at_runtime()
    {
        var tables = await _reader.ListTablesAsync();

        Assert.Equal(new[] { "ai_components_v", "ai_scans", "ai_signals" }, tables.Select(t => t.Name));
        Assert.True(tables.Single(t => t.Name == "ai_components_v").IsView);
        Assert.False(tables.Single(t => t.Name == "ai_signals").IsView);
    }

    [Fact]
    public async Task Discovers_columns()
    {
        var columns = await _reader.ListColumnsAsync("ai_signals");

        Assert.Equal(new[] { "id", "kind", "name", "confidence", "last_seen" }, columns.Select(c => c.Name));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.True(columns[1].NotNull);
        Assert.Equal("REAL", columns[3].DeclaredType);
    }

    [Fact]
    public async Task Browses_rows_generically()
    {
        var result = await _reader.BrowseAsync("ai_signals", limit: 2, orderByColumn: "id");

        Assert.Equal(new[] { "id", "kind", "name", "confidence", "last_seen" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("sig-1", result.Rows[0]["id"]);
        Assert.Equal("anthropic-sdk", result.Rows[0]["name"]);
    }

    [Fact]
    public async Task Nulls_come_back_as_null()
    {
        var result = await _reader.BrowseAsync("ai_signals", orderByColumn: "id", descending: true, limit: 1);

        Assert.Null(result.Rows[0]["name"]);
        Assert.Null(result.Rows[0]["confidence"]);
    }

    [Fact]
    public async Task Paging_uses_limit_and_offset()
    {
        var page = await _reader.BrowseAsync("ai_signals", limit: 1, offset: 1, orderByColumn: "id");

        Assert.Single(page.Rows);
        Assert.Equal("sig-2", page.Rows[0]["id"]);
    }

    [Fact]
    public async Task Views_are_browsable_too()
    {
        var result = await _reader.BrowseAsync("ai_components_v");

        Assert.Equal(new[] { "id", "name", "confidence" }, result.Columns);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public async Task Counts_rows()
    {
        Assert.Equal(3, await _reader.CountAsync("ai_signals"));
        Assert.Equal(1, await _reader.CountAsync("ai_scans"));
    }

    [Fact]
    public async Task Table_names_are_matched_case_insensitively()
    {
        Assert.Equal(3, await _reader.CountAsync("AI_SIGNALS"));
    }

    [Fact]
    public async Task Unknown_tables_are_rejected_rather_than_interpolated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.CountAsync("ai_signals; DROP TABLE ai_scans"));
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.BrowseAsync("no_such_table"));

        // The injection attempt did nothing.
        Assert.Equal(1, await _reader.CountAsync("ai_scans"));
    }

    [Fact]
    public async Task Unknown_order_by_columns_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _reader.BrowseAsync("ai_signals", orderByColumn: "id; DROP TABLE ai_scans"));
    }

    [Fact]
    public void Connection_string_is_read_only()
    {
        Assert.False(new InventoryReader(@"C:\nope\inventory.db").Exists);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}
