using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The configuration and policy change history against a database built from the real DDL (the <c>activity_events</c> table and
/// its index, the bucket index on <c>audit_events</c>) and synthetic rows only. The live <c>activity_events</c> is empty and the
/// audit table is 6.7 GB, so what matters there is the empty read and the plan; both are asserted.
/// </summary>
public sealed class MutationReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private MutationReader Reader() => new(_database.Path);

    private void Activity(
        string id,
        int minute,
        string action = "policy.update",
        string actor = "admin",
        string targetType = "policy",
        string targetId = "default",
        string? reason = null,
        string? before = null,
        string? after = null,
        string? diff = null,
        string? from = null,
        string? to = null)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_events
                (id, timestamp, actor, action, target_type, target_id, reason, before_json, after_json, diff_json, version_from, version_to)
            VALUES ($id, $timestamp, $actor, $action, $targetType, $targetId, $reason, $before, $after, $diff, $from, $to)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$targetType", targetType);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("$diff", (object?)diff ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private void Change(string id, int minute, string action = "config.change.applied", string bucket = "compliance.activity", string? connector = null) =>
        _database.InsertEvent(id, Base.AddMinutes(minute), action, "INFO", bucket, connector, details: "details of " + id, structuredJson: "{\"k\":\"" + id + "\"}", actor: "gateway_api");

    private void Exec(string sql)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_missing_database_is_nothing_recorded()
    {
        var result = await new MutationReader(Path.Combine(Path.GetTempPath(), "dcw-no-such-" + Guid.NewGuid().ToString("N") + ".db")).ReadAsync();

        Assert.Equal(MutationStatus.NoDatabase, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task An_empty_activity_table_and_no_change_rows_reads_as_ok_and_empty()
    {
        _database.InsertEvent("telemetry", Base, "span", "INFO", "telemetry.ingest");

        var result = await Reader().ReadAsync();

        Assert.Equal(MutationStatus.Ok, result.Status);
        Assert.Empty(result.Items);
        Assert.True(result.HasActivityTable);
    }

    [Fact]
    public async Task Activity_events_are_read_with_their_before_after_diff_and_versions()
    {
        Activity("a1", 5, reason: "tighten", before: "{\"mode\":\"observe\"}", after: "{\"mode\":\"action\"}", diff: "[{\"path\":\"mode\"}]", from: "3", to: "4");

        var item = Assert.Single((await Reader().ReadAsync()).Items);

        Assert.Equal(MutationSource.ActivityEvent, item.Source);
        Assert.Equal("admin", item.Actor);
        Assert.Equal("policy.update", item.Action);
        Assert.Equal("policy", item.TargetType);
        Assert.Equal("default", item.Target);
        Assert.Equal("tighten", item.Reason);
        Assert.Equal("{\"mode\":\"observe\"}", item.BeforeJson);
        Assert.Equal("{\"mode\":\"action\"}", item.AfterJson);
        Assert.Equal("[{\"path\":\"mode\"}]", item.DiffJson);
        Assert.Equal("3", item.VersionFrom);
        Assert.Equal("4", item.VersionTo);
        Assert.Equal(Base.AddMinutes(5), item.Timestamp);
        Assert.True(item.HasBeforeAfter);
        Assert.True(item.HasDiff);
        Assert.Null(item.Connector);
    }

    [Fact]
    public async Task Canonical_audit_rows_are_read_and_authentication_failures_and_other_buckets_are_not()
    {
        Change("applied", 1);
        Change("quarantine", 2, "quarantine", "enforcement.action", "claudecode");
        Change("auth", 3, MutationReader.AuthFailureAction);
        _database.InsertEvent("scan", Base.AddMinutes(4), "scan-finding", "HIGH", "security.finding", "claudecode", eventName: "finding.observed");
        _database.InsertEvent("pre-bucket", Base.AddMinutes(5), "config.change.applied", "INFO");

        var result = await Reader().ReadAsync();

        Assert.Equal(new[] { "quarantine", "applied" }, result.Items.Select(i => i.Id));
        var quarantine = result.Items[0];
        Assert.Equal(MutationSource.Audit, quarantine.Source);
        Assert.Equal("enforcement.action", quarantine.Bucket);
        Assert.Equal("claudecode", quarantine.Connector);
        Assert.Equal("gateway_api", quarantine.Actor);
        Assert.Equal("details of quarantine", quarantine.Reason);
        Assert.Equal("{\"k\":\"quarantine\"}", quarantine.StructuredJson);
        Assert.False(quarantine.HasBeforeAfter);
    }

    [Fact]
    public async Task Both_sources_are_merged_newest_first()
    {
        Activity("old", 1);
        Change("mid", 2);
        Activity("new", 3);
        Change("oldest", 0, bucket: "enforcement.action", action: "block");

        var result = await Reader().ReadAsync();

        Assert.Equal(new[] { "new", "mid", "old", "oldest" }, result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_database_without_activity_events_still_gives_the_audit_rows()
    {
        Change("applied", 1);
        Exec("DROP TABLE activity_events");

        var result = await Reader().ReadAsync();

        Assert.Equal(MutationStatus.Ok, result.Status);
        Assert.False(result.HasActivityTable);
        Assert.Equal("applied", Assert.Single(result.Items).Id);
        Assert.DoesNotContain(await Reader().ExplainAsync(), line => line.Contains("idx_activity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_database_with_neither_table_is_nothing_recorded()
    {
        Exec("DROP TABLE activity_events");
        Exec("DROP TABLE audit_events");

        var result = await Reader().ReadAsync();

        Assert.Equal(MutationStatus.NoDatabase, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task A_database_that_predates_buckets_contributes_no_audit_rows()
    {
        Activity("a1", 1);
        Change("applied", 2);
        Exec("DROP INDEX idx_audit_bucket_timestamp");
        Exec("ALTER TABLE audit_events DROP COLUMN bucket");

        var result = await Reader().ReadAsync();

        Assert.Equal("a1", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task The_window_keeps_the_newest_rows_and_says_when_there_were_more()
    {
        for (var i = 0; i < 5; i++)
        {
            Activity("a" + i, i);
        }

        for (var i = 0; i < 5; i++)
        {
            Change("c" + i, 10 + i);
        }

        var result = await Reader().ReadAsync(limit: 4);

        Assert.True(result.HasMore);
        Assert.Equal(new[] { "c4", "c3", "c2", "c1" }, result.Items.Select(i => i.Id));
        Assert.False((await Reader().ReadAsync(limit: 100)).HasMore);
    }

    [Fact]
    public async Task A_payload_over_the_limit_is_not_read_and_not_cut_it_is_unavailable_with_its_size()
    {
        // Cut at the limit it would be a broken document that reads as a different change; left in the database it is a change that says
        // "before_json is 262,644 bytes, over the 256 KB limit" (see OversizedPayloadTests for the rest of the cases).
        Activity("big", 1, before: new string('x', MutationReader.PayloadLimit + 500), after: "{\"mode\":\"action\"}");

        var item = Assert.Single((await Reader().ReadAsync()).Items);

        Assert.Equal(string.Empty, item.BeforeJson);
        Assert.Equal("{\"mode\":\"action\"}", item.AfterJson);
        var oversized = Assert.Single(item.Oversized);
        Assert.Equal("before_json", oversized.Column);
        Assert.Equal(MutationReader.PayloadLimit + 500, oversized.Bytes);
    }

    [Fact]
    public async Task Every_statement_is_an_index_search_with_no_sort_and_no_scan()
    {
        Activity("a1", 1);
        Change("c1", 2);

        var plan = await Reader().ExplainAsync();

        // activity_events once, each audit bucket once.
        Assert.Contains(plan, line => line.Contains("idx_activity_timestamp", StringComparison.Ordinal));
        Assert.Equal(2, plan.Count(line => line.Contains("idx_audit_bucket_timestamp", StringComparison.Ordinal)));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.OrdinalIgnoreCase) && !line.Contains("INDEX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_read()
    {
        Activity("a1", 1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task The_database_is_opened_read_only()
    {
        Change("c1", 1);
        var reader = Reader();

        _ = await reader.ReadAsync();

        // A read-only reader leaves the file writable by its owner and takes no write lock: a writer can still go ahead.
        Change("c2", 2);
        Assert.Equal(2, (await reader.ReadAsync()).Items.Count);
        Assert.Equal(2, reader.ReadCount);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(limit: 0));
    }
}
