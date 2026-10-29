using System.Security.Cryptography;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-359: after an upgrade, does the gateway's history actually reach audit.db? On fixture databases built from the real DDL (synthetic rows
/// only); the contention case holds a write lock on a copy, the way the retention reaper starved the audit writer on the Mac. Nothing here deletes,
/// prunes or re-journals a database, and no answer calls one damaged.
/// </summary>
public sealed class AuditPersistenceCheckTests : IDisposable
{
    private static readonly DateTimeOffset GatewayStart = DateTimeOffset.UtcNow.AddMinutes(-3);

    private readonly TestAuditDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_start_record_of_the_gateway_is_seen()
    {
        _db.InsertEvent("old-1", GatewayStart.AddDays(-2), "scan");
        _db.InsertEvent("start-1", GatewayStart.AddSeconds(1), AuditPersistenceCheck.SidecarStartAction, severity: "INFO");

        var result = await AuditPersistenceCheck.CheckAsync(_db.Path, GatewayStart);

        Assert.Equal(AuditPersistenceState.Persisted, result.State);
        Assert.True(result.IsVerified);
        Assert.Equal(AuditPersistenceCheck.SidecarStartAction, result.NewestAction);
        Assert.Contains("start was recorded", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_newer_event_counts_and_the_newest_is_named()
    {
        _db.InsertEvent("e-1", GatewayStart.AddSeconds(30), "guardrail-verdict");
        _db.InsertEvent("e-2", GatewayStart.AddSeconds(90), "tool-call");

        var result = await AuditPersistenceCheck.CheckAsync(_db.Path, GatewayStart);

        Assert.Equal(AuditPersistenceState.Persisted, result.State);
        Assert.Equal("tool-call", result.NewestAction);
        Assert.Contains("has events written since the gateway started", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_older_events_means_nothing_new_was_written()
    {
        _db.InsertEvent("old-1", GatewayStart.AddHours(-5), AuditPersistenceCheck.SidecarStartAction);

        var result = await AuditPersistenceCheck.CheckAsync(_db.Path, GatewayStart);

        Assert.Equal(AuditPersistenceState.NoNewRow, result.State);
        Assert.True(result.IsDegraded);
    }

    [Fact]
    public async Task A_missing_database_and_an_empty_file_are_said_plainly()
    {
        var folder = Path.GetDirectoryName(_db.Path)!;
        Assert.Equal(AuditPersistenceState.Missing, (await AuditPersistenceCheck.CheckAsync(Path.Combine(folder, "absent.db"), GatewayStart)).State);

        var empty = Path.Combine(folder, "empty.db");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        var answer = await AuditPersistenceCheck.CheckAsync(empty, GatewayStart);
        Assert.Equal(AuditPersistenceState.NoNewRow, answer.State);
        Assert.Contains("no audit_events table", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_writer_holding_a_copy_longer_than_the_check_waits_is_busy_not_damage()
    {
        _db.InsertEvent("start-1", GatewayStart.AddSeconds(1), AuditPersistenceCheck.SidecarStartAction);
        SqlitePools.Release(Path.GetDirectoryName(_db.Path)!);
        var copy = Path.Combine(Path.GetDirectoryName(_db.Path)!, "audit-copy.db");
        File.Copy(_db.Path, copy);

        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            writer.Open();
            using (var exclusive = writer.CreateCommand())
            {
                exclusive.CommandText = "BEGIN EXCLUSIVE";
                _ = exclusive.ExecuteNonQuery();
            }

            var result = await AuditPersistenceCheck.CheckAsync(copy, GatewayStart, busyTimeoutSeconds: 1);

            Assert.Equal(AuditPersistenceState.Busy, result.State);
            Assert.True(result.IsDegraded);
            Assert.Contains("contention, not damage", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("corrupt", result.Text, StringComparison.OrdinalIgnoreCase);

            using var rollback = writer.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            _ = rollback.ExecuteNonQuery();
        }

        // Once the writer lets go, the same copy answers.
        Assert.Equal(AuditPersistenceState.Persisted, (await AuditPersistenceCheck.CheckAsync(copy, GatewayStart, busyTimeoutSeconds: 1)).State);
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_unreadable_and_never_called_corrupt()
    {
        var bogus = Path.Combine(Path.GetDirectoryName(_db.Path)!, "not-a-db.db");
        File.WriteAllText(bogus, new string('x', 4096));

        var result = await AuditPersistenceCheck.CheckAsync(bogus, GatewayStart);

        Assert.Equal(AuditPersistenceState.Unreadable, result.State);
        Assert.Contains("SQLite error", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("corrupt", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_check_changes_nothing_and_keeps_no_handle_open()
    {
        _db.InsertEvent("start-1", GatewayStart.AddSeconds(1), AuditPersistenceCheck.SidecarStartAction);
        SqlitePools.Release(Path.GetDirectoryName(_db.Path)!);
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_db.Path)));
        var written = File.GetLastWriteTimeUtc(_db.Path);

        _ = await AuditPersistenceCheck.CheckAsync(_db.Path, GatewayStart);

        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_db.Path))));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_db.Path));

        // Unpooled: the file can be moved away at once, as the installer moves audit.db.
        var moved = _db.Path + ".moved";
        File.Move(_db.Path, moved);
        File.Move(moved, _db.Path);
    }
}
