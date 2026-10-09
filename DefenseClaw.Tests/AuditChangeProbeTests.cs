using System.Security.Cryptography;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The change probe every audit.db reader shares: <c>PRAGMA data_version</c> on one kept read-only connection for the live database, the
/// file's identity for an archive. What matters is that it is exact (a commit by anyone else always moves it, in both journal modes),
/// cheap (one connection however many samples), honest when it cannot tell (unknown matches nothing) and read-only. Synthetic databases
/// from the real DDL only.
/// </summary>
public sealed class AuditChangeProbeTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private AuditChangeProbe Probe() => new(_database.Path);

    private int _rows;

    private void Commit() => _database.InsertEvent("row-" + _rows++, Base.AddSeconds(_rows), "hook_decision", "INFO", "guardrail.evaluation");

    private void UseWal()
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        Assert.Equal("wal", Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ unknown is not "unchanged"

    [Fact]
    public void A_missing_database_is_unknown_and_nothing_is_created()
    {
        var path = Path.Combine(Path.GetTempPath(), "dcw-probe-missing-" + Guid.NewGuid().ToString("n"), "audit.db");
        using var probe = new AuditChangeProbe(path);

        var stamp = probe.Sample();

        Assert.False(stamp.IsKnown);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.False(probe.IsOpen);
        Assert.Equal(0, probe.OpenCount);
    }

    [Fact]
    public void An_unknown_stamp_matches_nothing_not_even_itself()
    {
        using var probe = Probe();
        var known = probe.Sample();

        Assert.True(known.IsKnown);
        Assert.False(AuditStamp.Unknown.Matches(AuditStamp.Unknown));
        Assert.False(AuditStamp.Unknown.Matches(known));
        Assert.False(known.Matches(AuditStamp.Unknown));
        Assert.True(known.Matches(known));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_unknown_not_a_crash()
    {
        // The fixture's pooled writer would hold the file open; let it go so the file can be overwritten.
        SqlitePools.Release(_database.Path);
        File.WriteAllText(_database.Path, "this is not a sqlite database, just text that is long enough to be a page header of nothing");
        using var probe = Probe();

        Assert.False(probe.Sample().IsKnown);
        Assert.False(probe.Sample().IsKnown);
        Assert.False(probe.IsOpen);
    }

    // ------------------------------------------------------------------ exact

    [Fact]
    public void Samples_of_an_unchanged_database_match_and_share_one_connection()
    {
        Commit();
        using var probe = Probe();

        var first = probe.Sample();
        var second = probe.Sample();
        for (var i = 0; i < 500; i++)
        {
            Assert.True(probe.Sample().Matches(first));
        }

        Assert.True(first.Matches(second));
        Assert.Equal(1, probe.OpenCount);
        Assert.Equal(502, probe.SampleCount);
        Assert.True(probe.IsOpen);
    }

    [Fact]
    public void A_commit_by_another_connection_moves_the_stamp_in_rollback_journal_mode()
    {
        Commit();
        using var probe = Probe();

        var before = probe.Sample();
        Commit();
        var after = probe.Sample();
        var again = probe.Sample();

        Assert.False(before.Matches(after));
        Assert.True(after.Matches(again));
        Assert.Equal(1, probe.OpenCount);
    }

    [Fact]
    public void Every_commit_moves_the_stamp_in_WAL_mode()
    {
        UseWal();
        Commit();
        using var probe = Probe();

        var last = probe.Sample();
        for (var i = 0; i < 40; i++)
        {
            Commit();
            var now = probe.Sample();
            Assert.False(last.Matches(now), $"commit {i} was not noticed");
            Assert.True(now.Matches(probe.Sample()));
            last = now;
        }

        Assert.Equal(1, probe.OpenCount);
    }

    [Fact]
    public void A_commit_between_two_samples_is_seen_by_every_older_stamp()
    {
        // Two readers sharing the probe: A samples, a commit lands, B samples. A's stamp must still be stale afterwards, which is what
        // lets each reader keep its own last stamp over one shared connection.
        UseWal();
        Commit();
        using var probe = Probe();

        var a = probe.Sample();
        Commit();
        var b = probe.Sample();
        var now = probe.Sample();

        Assert.False(a.Matches(b));
        Assert.False(a.Matches(now));
        Assert.True(b.Matches(now));
    }

    [Fact]
    public void An_update_that_changes_no_row_count_is_still_a_change()
    {
        Commit();
        using var probe = Probe();
        var before = probe.Sample();

        using (var connection = _database.OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit_events SET severity = 'HIGH'";
            _ = command.ExecuteNonQuery();
        }

        Assert.False(before.Matches(probe.Sample()));
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public void Release_gives_the_file_back_and_the_next_sample_starts_a_new_epoch()
    {
        Commit();
        using var probe = Probe();
        var before = probe.Sample();

        probe.Release();
        Assert.False(probe.IsOpen);

        var after = probe.Sample();
        Assert.True(after.IsKnown);
        Assert.False(before.Matches(after));
        Assert.NotEqual(before.Epoch, after.Epoch);
        Assert.True(after.Matches(probe.Sample()));
        Assert.Equal(2, probe.OpenCount);
    }

    [Fact]
    public void A_disposed_probe_answers_unknown_and_releases_the_file()
    {
        Commit();
        var probe = Probe();
        Assert.True(probe.Sample().IsKnown);

        probe.Dispose();
        probe.Dispose();

        Assert.False(probe.Sample().IsKnown);
        Assert.False(probe.IsOpen);

        // The kept connection is gone, so the file can be renamed (a Windows handle without share-delete would refuse).
        SqlitePools.Release(_database.Path);
        File.Move(_database.Path, _database.Path + ".moved");
        File.Move(_database.Path + ".moved", _database.Path);
    }

    [Fact]
    public async Task SampleAsync_gives_the_same_stamp_and_honours_a_cancelled_token()
    {
        Commit();
        using var probe = Probe();

        var asynchronous = await probe.SampleAsync();

        Assert.True(asynchronous.Matches(probe.Sample()));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.SampleAsync(cancelled.Token));
    }

    [Fact]
    public async Task Samples_from_many_threads_while_a_writer_commits_neither_fail_nor_open_a_second_connection()
    {
        UseWal();
        Commit();
        using var probe = Probe();
        var baseline = probe.Sample();

        var samplers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            var bad = 0;
            for (var i = 0; i < 300; i++)
            {
                if (!probe.Sample().IsKnown)
                {
                    bad++;
                }
            }

            return bad;
        })).ToArray();

        for (var i = 0; i < 30; i++)
        {
            Commit();
        }

        var failures = await Task.WhenAll(samplers);

        Assert.All(failures, bad => Assert.Equal(0, bad));
        Assert.False(baseline.Matches(probe.Sample()));
        Assert.Equal(1, probe.OpenCount);
    }

    // ------------------------------------------------------------------ read-only

    [Fact]
    public void The_kept_connection_holds_no_read_transaction_so_it_never_holds_back_a_checkpoint_or_a_writer()
    {
        UseWal();
        for (var i = 0; i < 10; i++)
        {
            Commit();
        }

        using var probe = Probe();
        _ = probe.Sample();
        Assert.True(probe.IsOpen);

        // A full checkpoint that truncates the WAL can only finish if no reader is pinned to an old snapshot; "busy = 1" would mean the
        // probe's connection is sitting inside a read transaction.
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, new FileInfo(_database.Path + "-wal").Length);

        // ... and the probe, still open, sees the next commit.
        var before = probe.Sample();
        reader.Close();
        Commit();
        Assert.False(before.Matches(probe.Sample()));
    }

    [Fact]
    public void The_probe_never_writes_the_database_file()
    {
        for (var i = 0; i < 20; i++)
        {
            Commit();
        }

        var hash = Hash(_database.Path);
        var written = File.GetLastWriteTimeUtc(_database.Path);
        using var probe = Probe();

        for (var i = 0; i < 100; i++)
        {
            _ = probe.Sample();
        }

        probe.Dispose();
        Assert.Equal(hash, Hash(_database.Path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_database.Path));
    }

    /// <summary>The file's SHA-256, read the way a reader that must not get in anyone's way does (the fixture's pooled writer still has it open).</summary>
    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    // ------------------------------------------------------------------ the archive: a file stat, nothing opened

    [Fact]
    public void An_immutable_probe_stamps_the_file_and_opens_and_creates_nothing()
    {
        Commit();
        var directory = Path.GetDirectoryName(_database.Path)!;
        var before = Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        using var probe = new AuditChangeProbe(_database.Path, immutable: true);

        var first = probe.Sample();
        var second = probe.Sample();

        Assert.True(first.IsKnown);
        Assert.True(first.Matches(second));
        Assert.True(probe.IsImmutable);
        Assert.False(probe.IsOpen);
        Assert.Equal(0, probe.OpenCount);
        Assert.Equal(before, Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void An_immutable_probe_notices_a_replaced_file_and_a_missing_one()
    {
        Commit();
        using var probe = new AuditChangeProbe(_database.Path, immutable: true);
        var original = probe.Sample();

        // Somebody swaps in another archive at the same path.
        using (var stream = new FileStream(_database.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[4096]);
        }

        var replaced = probe.Sample();
        Assert.True(replaced.IsKnown);
        Assert.False(original.Matches(replaced));
        Assert.True(replaced.Matches(probe.Sample()));

        var path = Path.Combine(Path.GetTempPath(), "dcw-probe-archive-missing-" + Guid.NewGuid().ToString("n") + ".db");
        using var absent = new AuditChangeProbe(path, immutable: true);
        Assert.False(absent.Sample().IsKnown);
    }
}
