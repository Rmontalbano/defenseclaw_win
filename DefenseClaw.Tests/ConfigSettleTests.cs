using DefenseClaw.Core.Config;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The two-read settle in <see cref="ConfigChangeToken"/> (a half-written file is never reported) and the retry policy
/// <see cref="ConfigReloadBackoff"/>. Both follow the TUI's config watcher (0.8.10 services/config_watch.py).
/// </summary>
public sealed class ConfigSettleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_truncate_then_write_save_is_reported_once_and_only_as_the_finished_file()
    {
        using var temp = new TempDirectory();
        var config = temp.Write("config.yaml", "config_version: 8\n");
        var env = temp.Write(".env", "A=1\n");

        // A settle far longer than the gap between the two writes, so even a slow runner sees one generation.
        using var token = new ConfigChangeToken(config, env, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        var raised = new List<string>();
        using var signalled = new ManualResetEventSlim(false);
        token.Changed += (_, e) =>
        {
            lock (raised)
            {
                raised.Add(File.ReadAllText(e.Path));
            }

            signalled.Set();
        };

        File.WriteAllText(config, string.Empty);
        Thread.Sleep(250);
        File.WriteAllText(config, "config_version: 8\nclaw:\n  mode: codex\n");

        Assert.True(signalled.Wait(Timeout), "the finished file was never reported");
        Thread.Sleep(1500);

        lock (raised)
        {
            var only = Assert.Single(raised);
            Assert.Contains("mode: codex", only, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_change_is_not_reported_before_it_has_held_for_the_settle_delay()
    {
        using var temp = new TempDirectory();
        var config = temp.Write("config.yaml", "config_version: 8\n");
        var env = temp.Write(".env", "A=1\n");

        using var token = new ConfigChangeToken(config, env, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        using var signalled = new ManualResetEventSlim(false);
        token.Changed += (_, _) => signalled.Set();

        File.WriteAllText(config, "config_version: 9\n");

        Assert.False(signalled.Wait(TimeSpan.FromMilliseconds(800)), "reported on the first read");
        Assert.True(signalled.Wait(Timeout), "never reported");
    }

    [Fact]
    public void Poll_does_not_wait_for_the_settle()
    {
        using var temp = new TempDirectory();
        var config = temp.Write("config.yaml", "config_version: 8\n");
        var env = temp.Write(".env", "A=1\n");

        using var token = new ConfigChangeToken(config, env, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        File.WriteAllText(config, "config_version: 9\n");
        token.Poll();

        Assert.True(token.HasChanged);
    }

    [Fact]
    public void A_failed_generation_is_announced_once_and_retried_on_a_doubling_delay_capped_at_four_seconds()
    {
        var backoff = new ConfigReloadBackoff(TimeSpan.FromMilliseconds(300));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(backoff.ShouldAttempt("bad-1", t0));
        var d1 = backoff.RecordFailure("bad-1", t0, out var first1);
        Assert.True(first1);
        Assert.Equal(TimeSpan.FromMilliseconds(300), d1);

        // Not due yet for the same generation; a different one is not held back.
        Assert.False(backoff.ShouldAttempt("bad-1", t0.AddMilliseconds(100)));
        Assert.True(backoff.ShouldAttempt("bad-2", t0.AddMilliseconds(100)));
        Assert.True(backoff.ShouldAttempt("bad-1", t0.AddMilliseconds(400)));

        var d2 = backoff.RecordFailure("bad-1", t0.AddMilliseconds(400), out var first2);
        Assert.False(first2);
        Assert.Equal(TimeSpan.FromMilliseconds(600), d2);

        var d3 = backoff.RecordFailure("bad-1", t0.AddSeconds(2), out _);
        var d4 = backoff.RecordFailure("bad-1", t0.AddSeconds(4), out _);
        var d5 = backoff.RecordFailure("bad-1", t0.AddSeconds(8), out _);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), d3);
        Assert.Equal(TimeSpan.FromMilliseconds(2400), d4);
        Assert.Equal(ConfigReloadBackoff.MaxDelay, d5);

        // The attempts run out, and the generation stays held back rather than being reparsed forever.
        Assert.Null(backoff.RecordFailure("bad-1", t0.AddSeconds(16), out _));
    }

    [Fact]
    public void A_new_bad_generation_is_announced_again_and_a_success_resets_everything()
    {
        var backoff = new ConfigReloadBackoff(TimeSpan.FromMilliseconds(300));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _ = backoff.RecordFailure("bad-1", t0, out _);
        _ = backoff.RecordFailure("bad-2", t0, out var firstOfSecond);
        Assert.True(firstOfSecond);

        backoff.RecordSuccess();
        Assert.True(backoff.ShouldAttempt("bad-2", t0));
        _ = backoff.RecordFailure("bad-2", t0, out var firstAgain);
        Assert.True(firstAgain);
    }
}
