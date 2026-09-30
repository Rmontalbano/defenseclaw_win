using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The background update check (CUST-206): when it asks, what it keeps when the lookup fails, what it persists, and the once-per-release
/// toast. The release check itself is a fake (<see cref="Checks"/>), so what is asserted here is the watcher's behaviour around
/// <c>UpdateChecker.CheckAsync</c>, which keeps its own tests; nothing here reaches GitHub, the cache file or the real settings file.
/// </summary>
public sealed class UpdateWatcherTests : IDisposable
{
    private const string Repo = "https://github.com/cisco-ai-defense/defenseclaw/";

    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    // ------------------------------------------------------------------ fakes

    private static UpdateCheckResult Available(string latest = "v0.8.11", string installed = "0.8.10", string? html = null) =>
        new()
        {
            State = UpdateCheckState.UpdateAvailable,
            InstalledVersion = installed,
            LatestVersion = latest,
            HtmlUrl = html ?? Repo + "releases/tag/" + latest,
            CheckedAt = DateTimeOffset.UtcNow,
        };

    private static UpdateCheckResult UpToDate(string latest = "v0.8.10", string installed = "0.8.10") =>
        new()
        {
            State = UpdateCheckState.UpToDate,
            InstalledVersion = installed,
            LatestVersion = latest,
            HtmlUrl = Repo + "releases/tag/" + latest,
            CheckedAt = DateTimeOffset.UtcNow,
        };

    private static UpdateCheckResult Failed(string why = "GitHub was unreachable: no route") =>
        new() { State = UpdateCheckState.CheckFailed, ErrorMessage = why, Detail = why, CheckedAt = DateTimeOffset.UtcNow };

    /// <summary>What UpdateChecker returns when the live fetch failed but a stale cache entry compares fine: a verdict, with the failure on it.</summary>
    private static UpdateCheckResult StaleFallback(string latest) =>
        Available(latest) with { ErrorMessage = "GitHub's rate limit was hit.", IsRateLimited = true, FromCache = true };

    /// <summary>The release check: records every call (and whether it asked for a forced refresh) and answers from <see cref="Behavior"/>.</summary>
    private sealed class Checks
    {
        private readonly object _gate = new();
        private readonly List<bool> _calls = new();

        public Func<bool, Task<UpdateCheckResult>> Behavior { get; set; } = _ => Task.FromResult(UpToDate());

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count;
                }
            }
        }

        public bool[] Forced
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public void Answer(UpdateCheckResult result) => Behavior = _ => Task.FromResult(result);

        public Task<UpdateCheckResult> Run(bool force, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _calls.Add(force);
            }

            return Behavior(force);
        }
    }

    /// <summary>The loop's sleeps, released by hand: a test decides when "six hours" have passed.</summary>
    private sealed class ManualDelay
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan Span, TaskCompletionSource Release)> _pending = new();
        private readonly List<TimeSpan> _requested = new();

        public TimeSpan[] Requested
        {
            get
            {
                lock (_gate)
                {
                    return _requested.ToArray();
                }
            }
        }

        public Task Delay(TimeSpan span, CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(() => release.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                _pending.Add((span, release));
                _requested.Add(span);
            }

            return release.Task;
        }

        /// <summary>Completes every sleep that is waiting now.</summary>
        public void ReleaseAll()
        {
            List<TaskCompletionSource> toRelease;
            lock (_gate)
            {
                toRelease = _pending.Select(p => p.Release).ToList();
                _pending.Clear();
            }

            foreach (var release in toRelease)
            {
                _ = release.TrySetResult();
            }
        }
    }

    private sealed class Rig
    {
        public required UpdateWatcher Watcher { get; init; }

        public required Checks Checks { get; init; }

        public required FakeSnapshotSource Source { get; init; }

        public required AppSettingsStore Settings { get; init; }

        public required ManualDelay Delay { get; init; }

        public required ManualClock Clock { get; init; }

        public List<string> Announced { get; } = new();

        public int Changes { get; set; }
    }

    private Rig NewRig(string? settingsPath = null, bool polled = true, Action<Checks>? configure = null)
    {
        var checks = new Checks();
        configure?.Invoke(checks);

        var settings = AppSettingsStore.OpenFresh(settingsPath ?? _temp.File("settings-" + Guid.NewGuid().ToString("N") + ".json"));
        var source = new FakeSnapshotSource();
        if (polled)
        {
            source.Current = GatewaySnapshot.Initial with { PolledAt = DateTimeOffset.UtcNow, BinaryVersion = "0.8.10" };
        }

        var delay = new ManualDelay();
        var clock = new ManualClock();
        var watcher = new UpdateWatcher(settings, source, checks.Run, time: clock, delay: delay.Delay, post: action => action());
        _disposables.Add(watcher);

        var rig = new Rig { Watcher = watcher, Checks = checks, Source = source, Settings = settings, Delay = delay, Clock = clock };
        watcher.Changed += (_, _) => rig.Changes++;
        watcher.NewVersionAvailable += (_, e) => rig.Announced.Add(e.Version);
        return rig;
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + because);
            }

            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ the pure parts

    [Theory]
    [InlineData("v0.8.11", "0.8.11")]
    [InlineData("V0.8.11", "0.8.11")]
    [InlineData("0.8.11", "0.8.11")]
    [InlineData("  v0.8.11-rc1 ", "0.8.11-rc1")]
    public void A_release_tag_is_shown_without_its_leading_v(string tag, string shown) =>
        Assert.Equal(shown, UpdateWatcher.DisplayVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_tag_has_no_version_and_is_never_the_same_as_anything(string? tag)
    {
        Assert.Null(UpdateWatcher.DisplayVersion(tag));
        Assert.False(UpdateWatcher.SameVersion(tag, tag));
        Assert.False(UpdateWatcher.SameVersion(tag, "0.8.11"));
    }

    [Theory]
    [InlineData("v0.8.11", "0.8.11", true)]
    [InlineData("0.8.11", "V0.8.11", true)]
    [InlineData("0.8.11", "0.8.12", false)]
    [InlineData("0.8.11", "0.8.11.1", false)]
    public void Two_spellings_of_one_release_are_the_same_version(string a, string b, bool same) =>
        Assert.Equal(same, UpdateWatcher.SameVersion(a, b));

    [Fact]
    public void The_announcement_is_the_banner_title_and_the_toast()
    {
        Assert.Equal("DefenseClaw 0.8.11 is available", UpdateWatcher.Announcement("0.8.11"));
        Assert.Equal("DefenseClaw 0.8.11 is available", new UpdateAnnouncedEventArgs("0.8.11", null).Text);
    }

    [Fact]
    public void The_next_check_is_six_hours_after_the_last_one_that_got_an_answer()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        // A check just done: a full interval. One done four hours ago: the two that are left. One never done: a full interval.
        Assert.Equal(TimeSpan.FromHours(6), UpdateWatcher.NextDelay(now, now.ToUnixTimeSeconds(), failures: 0));
        Assert.Equal(TimeSpan.FromHours(2), UpdateWatcher.NextDelay(now, now.AddHours(-4).ToUnixTimeSeconds(), failures: 0));
        Assert.Equal(UpdateWatcher.Interval, UpdateWatcher.NextDelay(now, 0, failures: 0));
    }

    [Fact]
    public void An_overdue_stamp_waits_a_minute_not_zero_and_a_stamp_from_the_future_waits_one_interval()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(UpdateWatcher.MinimumWait, UpdateWatcher.NextDelay(now, now.AddDays(-3).ToUnixTimeSeconds(), failures: 0));
        Assert.Equal(UpdateWatcher.Interval, UpdateWatcher.NextDelay(now, now.AddDays(40).ToUnixTimeSeconds(), failures: 0));
        Assert.Equal(UpdateWatcher.Interval, UpdateWatcher.NextDelay(now, long.MaxValue, failures: 0));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 45)]
    [InlineData(4, 135)]
    [InlineData(5, 360)]
    [InlineData(6, 360)]
    [InlineData(50, 360)]
    public void A_failed_check_is_retried_sooner_with_growing_gaps_capped_at_the_interval(int failures, int minutes)
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(minutes), UpdateWatcher.NextDelay(now, now.ToUnixTimeSeconds(), failures));
    }

    // ------------------------------------------------------------------ opening the release notes

    [Fact]
    public void Release_notes_open_only_a_link_into_this_repository_and_what_opens_is_the_normalised_address()
    {
        var opened = new List<string>();

        Assert.True(UpdateWatcher.TryOpenReleasePage(Repo + "releases/tag/v0.8.11", opened.Add));

        Assert.Equal(Repo + "releases/tag/v0.8.11", Assert.Single(opened));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("http://github.com/cisco-ai-defense/defenseclaw/releases/tag/v1")]
    [InlineData("https://evil.example/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://github.com@evil.example/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/../../evil/repo")]
    public void Any_other_link_is_refused_without_opening_anything(string? url)
    {
        var opened = new List<string>();

        Assert.False(UpdateWatcher.TryOpenReleasePage(url, opened.Add));

        Assert.Empty(opened);
    }

    [Fact]
    public void A_shell_that_cannot_open_the_page_is_a_no_not_a_crash()
    {
        Assert.False(UpdateWatcher.TryOpenReleasePage(Repo + "releases", _ => throw new Win32Exception(1155, "No application is associated")));
        Assert.False(UpdateWatcher.TryOpenReleasePage(Repo + "releases", _ => throw new InvalidOperationException("no browser")));
    }

    // ------------------------------------------------------------------ one check

    [Fact]
    public void Nothing_is_known_before_the_first_check()
    {
        var rig = NewRig();

        Assert.Equal(UpdateCheckState.Unknown, rig.Watcher.Latest.State);
        Assert.False(rig.Watcher.UpdateAvailable);
        Assert.False(rig.Watcher.ShowBanner);
        Assert.Null(rig.Watcher.AvailableVersion);
        Assert.Equal(0, rig.Checks.Count);
    }

    [Fact]
    public async Task A_newer_release_is_available_and_shows_the_banner()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));

        var result = await rig.Watcher.CheckNowAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, result.State);
        Assert.True(rig.Watcher.UpdateAvailable);
        Assert.True(rig.Watcher.ShowBanner);
        Assert.Equal("0.8.11", rig.Watcher.AvailableVersion);
        Assert.Equal("0.8.10", rig.Watcher.InstalledVersion);
        Assert.Equal(Repo + "releases/tag/v0.8.11", rig.Watcher.ReleaseUrl);
        Assert.Equal(1, rig.Changes);
        Assert.False(rig.Watcher.LastCheckFailed);
    }

    [Fact]
    public async Task Being_up_to_date_shows_nothing_and_says_nothing()
    {
        var rig = NewRig(configure: c => c.Answer(UpToDate()));

        _ = await rig.Watcher.CheckNowAsync();

        Assert.False(rig.Watcher.UpdateAvailable);
        Assert.False(rig.Watcher.ShowBanner);
        Assert.Equal(0, rig.Changes);
        Assert.Empty(rig.Announced);
    }

    [Fact]
    public async Task A_link_outside_the_repository_is_not_offered_as_release_notes()
    {
        var rig = NewRig(configure: c => c.Answer(Available(html: "https://evil.example/cisco-ai-defense/defenseclaw/releases/tag/v0.8.11")));

        _ = await rig.Watcher.CheckNowAsync();

        Assert.True(rig.Watcher.ShowBanner);
        Assert.Null(rig.Watcher.ReleaseUrl);
    }

    [Fact]
    public async Task An_automatic_check_never_forces_a_refresh_only_an_explicit_one_does()
    {
        var rig = NewRig();

        _ = await rig.Watcher.CheckNowAsync();
        Assert.Equal(new[] { false }, rig.Checks.Forced);

        _ = await rig.Watcher.CheckNowAsync(forceRefresh: true);
        Assert.Equal(new[] { false, true }, rig.Checks.Forced);
    }

    [Fact]
    public async Task A_check_that_is_already_running_is_joined_not_repeated()
    {
        var release = new TaskCompletionSource<UpdateCheckResult>();
        var rig = NewRig(configure: c => c.Behavior = _ => release.Task);

        var first = rig.Watcher.CheckNowAsync();
        await Eventually(() => rig.Checks.Count == 1, "the first check to start");
        var second = rig.Watcher.CheckNowAsync();
        var third = rig.Watcher.CheckNowAsync(forceRefresh: true);

        release.SetResult(Available());
        _ = await Task.WhenAll(first, second, third);

        Assert.Equal(1, rig.Checks.Count);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public async Task A_second_check_after_the_first_finished_runs_again()
    {
        var rig = NewRig();

        _ = await rig.Watcher.CheckNowAsync();
        _ = await rig.Watcher.CheckNowAsync();

        Assert.Equal(2, rig.Checks.Count);
    }

    // ------------------------------------------------------------------ a failed lookup keeps the previous result

    [Fact]
    public async Task A_failed_lookup_keeps_the_previous_banner()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        _ = await rig.Watcher.CheckNowAsync();
        var changes = rig.Changes;

        rig.Checks.Answer(Failed());
        _ = await rig.Watcher.CheckNowAsync();

        Assert.True(rig.Watcher.ShowBanner);
        Assert.Equal("0.8.11", rig.Watcher.AvailableVersion);
        Assert.Equal(UpdateCheckState.UpdateAvailable, rig.Watcher.Latest.State);
        Assert.True(rig.Watcher.LastCheckFailed);
        Assert.Contains("unreachable", rig.Watcher.LastFailure, StringComparison.Ordinal);
        Assert.Equal(changes, rig.Changes);
    }

    [Fact]
    public async Task A_failed_lookup_does_not_count_as_a_check_and_a_good_one_clears_the_failure()
    {
        var rig = NewRig(configure: c => c.Answer(Failed()));

        _ = await rig.Watcher.CheckNowAsync();
        Assert.Equal(0, rig.Settings.Current.Updates.LastCheckUnix);
        Assert.True(rig.Watcher.LastCheckFailed);

        rig.Checks.Answer(UpToDate());
        _ = await rig.Watcher.CheckNowAsync();
        Assert.Equal(rig.Clock.GetUtcNow().ToUnixTimeSeconds(), rig.Settings.Current.Updates.LastCheckUnix);
        Assert.False(rig.Watcher.LastCheckFailed);
        Assert.Null(rig.Watcher.LastFailure);
    }

    [Fact]
    public async Task A_check_that_throws_is_a_failed_check_and_never_escapes()
    {
        var rig = NewRig(configure: c => c.Answer(Available()));
        _ = await rig.Watcher.CheckNowAsync();

        rig.Checks.Behavior = _ => throw new HttpRequestException("socket closed");
        var result = await rig.Watcher.CheckNowAsync();

        Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        Assert.True(rig.Watcher.LastCheckFailed);
        Assert.True(rig.Watcher.ShowBanner);
    }

    [Fact]
    public async Task A_stale_cache_verdict_is_used_but_is_not_a_check_that_got_an_answer()
    {
        var rig = NewRig(configure: c => c.Answer(StaleFallback("v0.8.12")));

        _ = await rig.Watcher.CheckNowAsync();

        // The comparison stands (the cache is the best there is) ...
        Assert.True(rig.Watcher.ShowBanner);
        Assert.Equal("0.8.12", rig.Watcher.AvailableVersion);

        // ... but GitHub did not answer: no stamp, and the failure is on record so the next attempt comes sooner.
        Assert.Equal(0, rig.Settings.Current.Updates.LastCheckUnix);
        Assert.True(rig.Watcher.LastCheckFailed);
    }

    [Fact]
    public async Task A_good_check_after_an_update_was_installed_takes_the_banner_away()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        _ = await rig.Watcher.CheckNowAsync();
        Assert.True(rig.Watcher.ShowBanner);

        rig.Checks.Answer(UpToDate("v0.8.11", installed: "0.8.11"));
        _ = await rig.Watcher.CheckNowAsync();

        Assert.False(rig.Watcher.ShowBanner);
        Assert.False(rig.Watcher.UpdateAvailable);
        Assert.Equal(2, rig.Changes);
    }

    [Fact]
    public async Task Checking_again_without_a_difference_raises_nothing()
    {
        var rig = NewRig(configure: c => c.Answer(Available()));

        _ = await rig.Watcher.CheckNowAsync();
        _ = await rig.Watcher.CheckNowAsync();
        _ = await rig.Watcher.CheckNowAsync();

        Assert.Equal(1, rig.Changes);
    }

    // ------------------------------------------------------------------ persisted

    [Fact]
    public async Task A_check_that_got_an_answer_writes_its_time_into_the_updates_section()
    {
        var path = _temp.File("stamp.json");
        var rig = NewRig(path, configure: c => c.Answer(UpToDate()));
        rig.Clock.Advance(TimeSpan.FromHours(3));

        _ = await rig.Watcher.CheckNowAsync();

        var stamp = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(stamp, rig.Settings.Current.Updates.LastCheckUnix);
        Assert.Equal(stamp, AppSettingsStore.OpenFresh(path).Current.Updates.LastCheckUnix);
    }

    [Fact]
    public void The_three_update_settings_round_trip_through_the_file_and_a_blank_version_reads_as_none()
    {
        var path = _temp.File("round-trip.json");
        var written = new UpdateSettings { LastCheckUnix = 1_790_000_000, DismissedVersion = "0.9.1", NotifiedVersion = "0.9.2" };
        Assert.True(AppSettingsStore.OpenFresh(path).Update(s => s with { Updates = written }));

        Assert.Equal(written, AppSettingsStore.OpenFresh(path).Current.Updates);

        File.WriteAllText(path, """{ "updates": { "lastCheckUnix": 5, "dismissedVersion": " ", "notifiedVersion": "" } }""");
        var blank = AppSettingsStore.OpenFresh(path).Current.Updates;
        Assert.Equal(5, blank.LastCheckUnix);
        Assert.Null(blank.DismissedVersion);
        Assert.Null(blank.NotifiedVersion);
    }

    [Fact]
    public async Task Dismissing_hides_the_banner_for_that_release_and_survives_a_restart()
    {
        var path = _temp.File("dismiss.json");
        var rig = NewRig(path, configure: c => c.Answer(Available("v0.8.11")));
        _ = await rig.Watcher.CheckNowAsync();

        rig.Watcher.Dismiss();

        Assert.False(rig.Watcher.ShowBanner);
        Assert.True(rig.Watcher.IsDismissed);
        Assert.True(rig.Watcher.UpdateAvailable); // still true: dismissing is about the banner, not about the release
        Assert.Equal(2, rig.Changes);
        Assert.Equal("0.8.11", AppSettingsStore.OpenFresh(path).Current.Updates.DismissedVersion);

        // A new process: the same release is found again and stays quiet.
        var restarted = NewRig(path, configure: c => c.Answer(Available("v0.8.11")));
        _ = await restarted.Watcher.CheckNowAsync();
        Assert.False(restarted.Watcher.ShowBanner);
        Assert.Equal(0, restarted.Changes);
    }

    [Fact]
    public async Task A_dismissed_release_stays_dismissed_however_the_tag_is_spelled()
    {
        var path = _temp.File("spelling.json");
        var rig = NewRig(path, configure: c => c.Answer(Available("v0.8.11")));
        _ = rig.Settings.Update(s => s with { Updates = s.Updates with { DismissedVersion = "V0.8.11" } });

        _ = await rig.Watcher.CheckNowAsync();

        Assert.False(rig.Watcher.ShowBanner);
    }

    [Fact]
    public async Task A_newer_release_shows_the_banner_again_after_an_older_one_was_dismissed()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        _ = await rig.Watcher.CheckNowAsync();
        rig.Watcher.Dismiss();
        Assert.False(rig.Watcher.ShowBanner);

        rig.Checks.Answer(Available("v0.8.12"));
        _ = await rig.Watcher.CheckNowAsync();

        Assert.True(rig.Watcher.ShowBanner);
        Assert.Equal("0.8.12", rig.Watcher.AvailableVersion);
    }

    [Fact]
    public void Dismissing_with_nothing_to_dismiss_does_nothing()
    {
        var rig = NewRig();

        rig.Watcher.Dismiss();

        Assert.Null(rig.Settings.Current.Updates.DismissedVersion);
        Assert.Equal(0, rig.Changes);
    }

    [Fact]
    public async Task Another_writer_clearing_the_dismissal_brings_the_banner_back_once_the_watcher_is_running()
    {
        var rig = NewRig(polled: false, configure: c => c.Answer(Available("v0.8.11")));
        rig.Watcher.Start();
        _ = await rig.Watcher.CheckNowAsync();
        rig.Watcher.Dismiss();
        Assert.False(rig.Watcher.ShowBanner);
        var changes = rig.Changes;

        _ = rig.Settings.Update(s => s with { Updates = s.Updates with { DismissedVersion = null } });

        Assert.True(rig.Watcher.ShowBanner);
        Assert.Equal(changes + 1, rig.Changes);
    }

    // ------------------------------------------------------------------ one toast per release

    [Fact]
    public async Task A_new_release_is_announced_once_however_many_checks_see_it()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));

        _ = await rig.Watcher.CheckNowAsync();
        _ = await rig.Watcher.CheckNowAsync();
        _ = await rig.Watcher.CheckNowAsync(forceRefresh: true);

        Assert.Equal(new[] { "0.8.11" }, rig.Announced);
    }

    [Fact]
    public async Task The_announcement_carries_the_vetted_release_page()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        UpdateAnnouncedEventArgs? seen = null;
        rig.Watcher.NewVersionAvailable += (_, e) => seen = e;

        _ = await rig.Watcher.CheckNowAsync();

        Assert.NotNull(seen);
        Assert.Equal("DefenseClaw 0.8.11 is available", seen.Text);
        Assert.Equal(Repo + "releases/tag/v0.8.11", seen.ReleaseUrl);
    }

    [Fact]
    public async Task The_next_release_is_announced_too()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        _ = await rig.Watcher.CheckNowAsync();

        rig.Checks.Answer(Available("v0.8.12"));
        _ = await rig.Watcher.CheckNowAsync();

        Assert.Equal(new[] { "0.8.11", "0.8.12" }, rig.Announced);
    }

    [Fact]
    public async Task A_restart_does_not_announce_a_release_that_was_announced_before()
    {
        var path = _temp.File("toast.json");
        var first = NewRig(path, configure: c => c.Answer(Available("v0.8.11")));
        _ = await first.Watcher.CheckNowAsync();
        Assert.Single(first.Announced);
        Assert.Equal("0.8.11", AppSettingsStore.OpenFresh(path).Current.Updates.NotifiedVersion);

        var restarted = NewRig(path, configure: c => c.Answer(Available("v0.8.11")));
        _ = await restarted.Watcher.CheckNowAsync();

        Assert.Empty(restarted.Announced);
        Assert.True(restarted.Watcher.ShowBanner); // the banner still says so; only the toast is once
    }

    [Fact]
    public async Task A_release_the_operator_dismissed_is_not_announced()
    {
        var rig = NewRig();
        _ = rig.Settings.Update(s => s with { Updates = s.Updates with { DismissedVersion = "0.8.11" } });
        rig.Checks.Answer(Available("v0.8.11"));

        _ = await rig.Watcher.CheckNowAsync();

        Assert.Empty(rig.Announced);
    }

    [Fact]
    public async Task A_failed_lookup_and_an_up_to_date_answer_announce_nothing()
    {
        var rig = NewRig(configure: c => c.Answer(Failed()));

        _ = await rig.Watcher.CheckNowAsync();
        rig.Checks.Answer(UpToDate());
        _ = await rig.Watcher.CheckNowAsync();

        Assert.Empty(rig.Announced);
        Assert.Null(rig.Settings.Current.Updates.NotifiedVersion);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_silence_the_others_or_the_check()
    {
        var rig = NewRig(configure: c => c.Answer(Available()));
        rig.Watcher.Changed += (_, _) => throw new InvalidOperationException("a bad handler");
        rig.Watcher.NewVersionAvailable += (_, _) => throw new InvalidOperationException("a bad handler");

        _ = await rig.Watcher.CheckNowAsync();

        Assert.Equal(1, rig.Changes);
        Assert.Single(rig.Announced);
    }

    // ------------------------------------------------------------------ the loop

    [Fact]
    public async Task Start_returns_at_once_and_runs_no_check_on_the_callers_thread()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource<UpdateCheckResult>();
        var callerThread = Environment.CurrentManagedThreadId;
        var checkThread = -1;
        var rig = NewRig(configure: c => c.Behavior = _ =>
        {
            checkThread = Environment.CurrentManagedThreadId;
            _ = entered.TrySetResult();
            return release.Task;
        });

        rig.Watcher.Start();

        // The check is running (blocked), and Start has returned: it did none of the work itself.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(callerThread, checkThread);

        release.SetResult(UpToDate());
        await Eventually(() => rig.Delay.Requested.Length > 0, "the loop to sleep after its first check");
    }

    [Fact]
    public async Task The_launch_check_waits_for_the_gateways_first_poll_so_the_installed_version_is_known_without_the_cli()
    {
        var rig = NewRig(polled: false, configure: c => c.Answer(UpToDate()));

        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the launch check to start waiting for the first poll");
        Assert.Equal(UpdateWatcher.StartupPollWait, rig.Delay.Requested[0]);
        await Task.Delay(100);
        Assert.Equal(0, rig.Checks.Count);

        rig.Source.Publish(GatewaySnapshot.Initial with { PolledAt = DateTimeOffset.UtcNow, BinaryVersion = "0.8.10" });

        await Eventually(() => rig.Checks.Count == 1, "the launch check after the first poll");
    }

    [Fact]
    public async Task A_gateway_that_never_answers_delays_the_launch_check_only_by_the_bounded_wait()
    {
        var rig = NewRig(polled: false, configure: c => c.Answer(UpToDate()));

        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the wait for the first poll");
        Assert.Equal(0, rig.Checks.Count);

        rig.Delay.ReleaseAll(); // StartupPollWait elapsed

        await Eventually(() => rig.Checks.Count == 1, "the launch check once the wait has elapsed");
    }

    [Fact]
    public async Task A_snapshot_that_is_not_a_poll_does_not_end_the_wait()
    {
        var rig = NewRig(polled: false, configure: c => c.Answer(UpToDate()));

        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the wait for the first poll");
        rig.Source.Publish(GatewaySnapshot.Initial with { BinaryVersion = "0.8.10" }); // PolledAt still unset
        await Task.Delay(100);

        Assert.Equal(0, rig.Checks.Count);
    }

    [Fact]
    public async Task After_the_launch_check_it_checks_every_six_hours_and_each_check_is_served_through_the_checkers_cache()
    {
        var rig = NewRig(configure: c => c.Answer(UpToDate()));

        rig.Watcher.Start();
        await Eventually(() => rig.Checks.Count == 1, "the launch check");
        await Eventually(() => rig.Delay.Requested.Length == 1, "the sleep after it");
        Assert.Equal(UpdateWatcher.Interval, rig.Delay.Requested[0]);

        rig.Delay.ReleaseAll();
        await Eventually(() => rig.Checks.Count == 2, "the periodic check");
        await Eventually(() => rig.Delay.Requested.Length == 2, "the next sleep");
        Assert.Equal(UpdateWatcher.Interval, rig.Delay.Requested[1]);

        Assert.All(rig.Checks.Forced, forced => Assert.False(forced));
    }

    [Fact]
    public async Task A_check_that_gets_an_answer_after_failures_puts_the_next_one_a_whole_interval_away()
    {
        var rig = NewRig(configure: c => c.Answer(Failed()));

        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the sleep after the failed launch check");
        Assert.Equal(UpdateWatcher.FirstRetry, rig.Delay.Requested[0]);

        rig.Checks.Answer(UpToDate());
        rig.Delay.ReleaseAll();
        await Eventually(() => rig.Delay.Requested.Length == 2, "the sleep after the good check");

        Assert.Equal(UpdateWatcher.Interval, rig.Delay.Requested[1]);
        Assert.Equal(rig.Clock.GetUtcNow().ToUnixTimeSeconds(), rig.Settings.Current.Updates.LastCheckUnix);
    }

    [Fact]
    public async Task A_failed_launch_check_is_retried_in_minutes_not_hours_and_the_gap_grows()
    {
        var rig = NewRig(configure: c => c.Answer(Failed()));

        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the first retry sleep");
        rig.Delay.ReleaseAll();
        await Eventually(() => rig.Delay.Requested.Length == 2, "the second retry sleep");
        rig.Delay.ReleaseAll();
        await Eventually(() => rig.Delay.Requested.Length == 3, "the third retry sleep");

        Assert.Equal(
            new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45) },
            rig.Delay.Requested);
    }

    [Fact]
    public async Task Start_twice_runs_one_loop()
    {
        var rig = NewRig(configure: c => c.Answer(UpToDate()));

        rig.Watcher.Start();
        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length >= 1, "the sleep after the launch check");
        await Task.Delay(100);

        Assert.Equal(1, rig.Checks.Count);
        Assert.Single(rig.Delay.Requested);
    }

    [Fact]
    public async Task An_upgrade_that_changes_the_installed_version_is_noticed_and_the_banner_follows()
    {
        var rig = NewRig(configure: c => c.Answer(Available("v0.8.11")));
        rig.Watcher.Start();
        await Eventually(() => rig.Watcher.ShowBanner, "the launch check to show the banner");
        var before = rig.Checks.Count;

        // The Updates window upgraded the runtime; the gateway comes back reporting the new build.
        rig.Checks.Answer(UpToDate("v0.8.11", installed: "0.8.11"));
        rig.Source.Publish(rig.Source.Current with { BinaryVersion = "0.8.11" });

        await Eventually(() => !rig.Watcher.ShowBanner, "the banner to go away");
        Assert.True(rig.Checks.Count > before);
    }

    [Fact]
    public async Task The_same_version_reported_again_and_a_blank_one_do_not_trigger_a_check()
    {
        var rig = NewRig(configure: c => c.Answer(UpToDate()));
        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the sleep after the launch check");
        var before = rig.Checks.Count;

        rig.Source.Publish(rig.Source.Current with { BinaryVersion = "0.8.10", Detail = "something else changed" });
        rig.Source.Publish(rig.Source.Current with { BinaryVersion = null });
        rig.Source.Publish(rig.Source.Current with { BinaryVersion = "  " });
        await Task.Delay(100);

        Assert.Equal(before, rig.Checks.Count);
    }

    [Fact]
    public async Task Disposing_stops_the_loop_and_gives_every_subscription_back()
    {
        var rig = NewRig(configure: c => c.Answer(UpToDate()));
        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the sleep after the launch check");
        Assert.Equal(1, rig.Source.StateChangedSubscribers);

        rig.Watcher.Dispose();
        rig.Delay.ReleaseAll();
        await Task.Delay(100);

        Assert.Equal(1, rig.Checks.Count);
        Assert.Equal(0, rig.Source.StateChangedSubscribers);
    }

    [Fact]
    public async Task Disposing_during_the_wait_for_the_first_poll_gives_the_subscription_back_and_checks_nothing()
    {
        var rig = NewRig(polled: false);
        rig.Watcher.Start();
        await Eventually(() => rig.Delay.Requested.Length == 1, "the wait for the first poll");
        Assert.True(rig.Source.StateChangedSubscribers >= 1);

        rig.Watcher.Dispose();
        await Eventually(() => rig.Source.StateChangedSubscribers == 0, "the subscriptions to be given back");

        Assert.Equal(0, rig.Checks.Count);
    }

    [Fact]
    public async Task Disposing_releases_what_the_check_owns_and_a_check_after_that_answers_with_what_is_known()
    {
        var released = 0;
        var settings = AppSettingsStore.OpenFresh(_temp.File("dispose.json"));
        var checks = new Checks();
        checks.Answer(Available());
        var watcher = new UpdateWatcher(settings, new FakeSnapshotSource(), checks.Run, onDispose: () => released++, post: a => a());
        _ = await watcher.CheckNowAsync();

        watcher.Dispose();
        watcher.Dispose();
        var after = await watcher.CheckNowAsync();

        Assert.Equal(1, released);
        Assert.Equal(1, checks.Count);
        Assert.Equal(UpdateCheckState.UpdateAvailable, after.State);
    }

    [Fact]
    public async Task The_watcher_of_an_isolated_composition_answers_not_checked_and_never_reaches_github()
    {
        using var services = TestServices.Create(_temp);

        var result = await services.UpdateWatcher.CheckNowAsync();

        Assert.Equal(UpdateCheckState.Unknown, result.State);
        Assert.False(services.UpdateWatcher.ShowBanner);
    }

    [Fact]
    public void Disposing_the_composition_disposes_its_watcher()
    {
        var released = 0;
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            updateWatcherFactory: s => new UpdateWatcher(
                s.Settings, s.Monitor, (_, _) => Task.FromResult(UpdateCheckResult.NotCheckedYet), onDispose: () => released++));

        services.Dispose();

        Assert.Equal(1, released);
    }
}
