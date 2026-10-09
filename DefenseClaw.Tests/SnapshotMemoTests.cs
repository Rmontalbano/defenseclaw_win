using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// What a reader remembers of its last answers: a few, by key, each under the stamp taken before it was read, never past the age limit,
/// never under an unknown stamp. Pure logic; no database.
/// </summary>
public sealed class SnapshotMemoTests
{
    private static AuditStamp Stamp(long epoch, long version) => new(epoch, version);

    [Fact]
    public void An_answer_comes_back_for_the_same_key_under_a_matching_stamp_and_not_for_any_other()
    {
        var memo = new SnapshotMemo<string, string>(capacity: 2);

        memo.Store("page-1", Stamp(1, 7), "first");

        Assert.True(memo.TryGet("page-1", Stamp(1, 7), out var found));
        Assert.Equal("first", found);
        Assert.False(memo.TryGet("page-1", Stamp(1, 8), out _), "the database moved");
        Assert.False(memo.TryGet("page-1", Stamp(2, 7), out _), "another connection");
        Assert.False(memo.TryGet("page-2", Stamp(1, 7), out _), "another key");
    }

    [Fact]
    public void An_unknown_stamp_is_never_stored_and_never_matches()
    {
        var memo = new SnapshotMemo<string, string>();

        memo.Store("k", AuditStamp.Unknown, "answer");
        memo.Store("k", Stamp(1, 1), "known");

        Assert.False(memo.TryGet("k", AuditStamp.Unknown, out _));
        Assert.True(memo.TryGet("k", Stamp(1, 1), out var known));
        Assert.Equal("known", known);
    }

    [Fact]
    public void Storing_a_key_again_replaces_it_and_the_least_recently_used_key_makes_room()
    {
        var memo = new SnapshotMemo<string, int>(capacity: 2);
        var stamp = Stamp(1, 1);

        memo.Store("a", stamp, 1);
        memo.Store("b", stamp, 2);
        Assert.True(memo.TryGet("a", stamp, out _));   // a is now the more recently used

        memo.Store("c", stamp, 3);                      // b is the one that goes

        Assert.True(memo.TryGet("a", stamp, out var a));
        Assert.True(memo.TryGet("c", stamp, out var c));
        Assert.False(memo.TryGet("b", stamp, out _));
        Assert.Equal((1, 3), (a, c));

        memo.Store("a", Stamp(1, 2), 10);
        Assert.False(memo.TryGet("a", stamp, out _), "the older stamp is gone with the older answer");
        Assert.True(memo.TryGet("a", Stamp(1, 2), out var newer));
        Assert.Equal(10, newer);
    }

    [Fact]
    public void An_answer_is_not_trusted_past_its_age_whatever_the_stamp_says()
    {
        var clock = new ManualClock();
        var memo = new SnapshotMemo<string, string>(maxAge: TimeSpan.FromMinutes(5), time: clock);
        memo.Store("k", Stamp(1, 1), "answer");

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(memo.TryGet("k", Stamp(1, 1), out _));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(memo.TryGet("k", Stamp(1, 1), out _));
    }

    [Fact]
    public void A_wall_clock_step_does_not_age_or_revive_an_answer()
    {
        var clock = new ManualClock();
        var memo = new SnapshotMemo<string, string>(maxAge: TimeSpan.FromMinutes(5), time: clock);
        memo.Store("k", Stamp(1, 1), "answer");

        // An NTP correction moves the wall clock a day either way; the age is on the monotonic clock.
        clock.StepWallClock(TimeSpan.FromDays(1));
        Assert.True(memo.TryGet("k", Stamp(1, 1), out _));
        clock.StepWallClock(TimeSpan.FromDays(-2));
        Assert.True(memo.TryGet("k", Stamp(1, 1), out _));
    }

    [Fact]
    public void Clear_forgets_everything_and_a_capacity_below_one_is_refused()
    {
        var memo = new SnapshotMemo<string, string>();
        memo.Store("k", Stamp(1, 1), "answer");

        memo.Clear();

        Assert.False(memo.TryGet("k", Stamp(1, 1), out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotMemo<string, string>(capacity: 0));
    }

    [Fact]
    public async Task Concurrent_readers_and_writers_never_throw_and_never_return_another_keys_answer()
    {
        var memo = new SnapshotMemo<int, int>(capacity: 4);
        var stamp = Stamp(1, 1);

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var wrong = 0;
            for (var i = 0; i < 2000; i++)
            {
                var key = (worker + i) % 6;
                memo.Store(key, stamp, key * 100);
                if (memo.TryGet(key, stamp, out var value) && value != key * 100)
                {
                    wrong++;
                }
            }

            return wrong;
        })).ToArray();

        var results = await Task.WhenAll(workers);

        Assert.All(results, wrong => Assert.Equal(0, wrong));
    }
}
