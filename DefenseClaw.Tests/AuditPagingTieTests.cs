using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Rows that tie on timestamp must be visited once each across pages (CUST-378: the Mac's later build orders ties by rowid). The keyset is
/// (timestamp, id, rowid): ids are unique in the gateway's own writes, so the id decides every tie it can; <c>id TEXT PRIMARY KEY</c> admits any
/// number of NULLs, and a run of NULL-id rows on one timestamp ties on id as well - the rowid decides those. Synthetic rows only.
/// </summary>
public sealed class AuditPagingTieTests : IDisposable
{
    private static readonly DateTimeOffset Shared = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly AuditReader _reader;

    public AuditPagingTieTests()
    {
        _reader = new AuditReader(_database.Path);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>An event with no id at all (the schema allows it), distinguished by its details.</summary>
    private void InsertWithoutId(string details, DateTimeOffset timestamp)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit_events (id, timestamp, action, actor, details, severity) VALUES (NULL, $timestamp, 'scan', 'audit_logger', $details, 'INFO')";
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$details", details);
        command.ExecuteNonQuery();
    }

    private async Task<List<string>> WalkAsync(int pageSize, bool ascending)
    {
        var seen = new List<string>();
        AuditCursor? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await _reader.QueryAsync(new AuditQuery { Limit = pageSize, Ascending = ascending, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Details ?? e.Id));
            if (!page.HasMore)
            {
                return seen;
            }

            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("paging did not end");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Rows_with_no_id_on_one_timestamp_are_each_visited_once_in_rowid_order(int pageSize)
    {
        _database.InsertEvent("before", Shared.AddMinutes(-1), "scan", "INFO", "asset.scan", details: "before");
        for (var i = 1; i <= 5; i++)
        {
            InsertWithoutId("null-" + i, Shared);
        }

        _database.InsertEvent("after", Shared.AddMinutes(1), "scan", "INFO", "asset.scan", details: "after");

        // Newest first; within the tie the later row (higher rowid) comes first, as the Mac's "timestamp DESC, rowid DESC" has it.
        var expected = new[] { "after", "null-5", "null-4", "null-3", "null-2", "null-1", "before" };

        Assert.Equal(expected, await WalkAsync(pageSize, ascending: false));
        Assert.Equal(expected.Reverse(), await WalkAsync(pageSize, ascending: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task A_tie_of_rows_with_ids_and_rows_without_is_visited_once_each(int pageSize)
    {
        _database.InsertEvent("id-a", Shared, "scan", "INFO", "asset.scan", details: "id-a");
        InsertWithoutId("null-1", Shared);
        _database.InsertEvent("id-b", Shared, "scan", "INFO", "asset.scan", details: "id-b");
        InsertWithoutId("null-2", Shared);

        var descending = await WalkAsync(pageSize, ascending: false);
        var ascending = await WalkAsync(pageSize, ascending: true);

        Assert.Equal(4, descending.Count);
        Assert.Equal(new[] { "id-a", "id-b", "null-1", "null-2" }, descending.Order(StringComparer.Ordinal));
        Assert.Equal(descending.Count, descending.Distinct().Count());
        Assert.Equal(descending.AsEnumerable().Reverse(), ascending);
    }

    [Fact]
    public async Task Rows_with_ids_still_page_in_id_order_within_a_tie()
    {
        foreach (var id in new[] { "tie-a", "tie-b", "tie-c", "tie-d" })
        {
            _database.InsertEvent(id, Shared, "scan", "INFO", "asset.scan", details: id);
        }

        Assert.Equal(new[] { "tie-d", "tie-c", "tie-b", "tie-a" }, await WalkAsync(1, ascending: false));
        Assert.Equal(new[] { "tie-a", "tie-b", "tie-c", "tie-d" }, await WalkAsync(3, ascending: true));
    }

    [Fact]
    public void A_cursor_round_trips_with_and_without_a_rowid_and_the_old_token_still_reads()
    {
        var withRowId = new AuditCursor(1_790_000_000_123_456_789, "evt:with:colons", 42);
        Assert.True(AuditCursor.TryParse(withRowId.ToToken(), out var parsed));
        Assert.Equal(withRowId, parsed);

        var plain = new AuditCursor(1_790_000_000_123_456_789, "evt-1");
        Assert.Equal("1790000000123456789:evt-1", plain.ToToken());
        Assert.True(AuditCursor.TryParse(plain.ToToken(), out var old));
        Assert.Equal(0, old.RowId);
        
        Assert.Equal(plain, old);

        Assert.False(AuditCursor.TryParse("12@x:evt", out _));
    }
}
