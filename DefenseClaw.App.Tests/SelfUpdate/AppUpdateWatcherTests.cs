using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>The banner's rules: what it offers, Later, Skip this version (persisted), one toast per release, development builds.</summary>
public sealed class AppUpdateWatcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppUpdateWatcher> _watchers = [];

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _temp.Dispose();
    }

    private static AppUpdateCheck Available(string version) => new()
    {
        State = AppUpdateState.UpdateAvailable,
        Running = "1.0.0",
        Release = SelfUpdateKit.Release(version),
        Message = $"{version} is available",
    };

    private AppUpdateWatcher Watch(SelfUpdateStore store, Func<AppUpdateCheck> answer)
    {
        var watcher = new AppUpdateWatcher((_, _) => Task.FromResult(answer()), store, post: action => action());
        _watchers.Add(watcher);
        return watcher;
    }

    [Fact]
    public async Task An_available_release_shows_the_banner_and_announces_once()
    {
        var store = new SelfUpdateStore(_temp.File("s.json"));
        var watcher = Watch(store, () => Available("1.2.0"));
        var announced = new List<string>();
        watcher.Announced += (_, v) => announced.Add(v);

        _ = await watcher.CheckNowAsync(manual: false);
        _ = await watcher.CheckNowAsync(manual: false);

        Assert.True(watcher.ShowBanner);
        Assert.Equal(["1.2.0"], announced);
        Assert.Equal("1.2.0", store.Current.NotifiedVersion);
    }

    [Fact]
    public async Task A_toast_already_shown_in_an_earlier_run_is_not_repeated()
    {
        var path = _temp.File("s.json");
        var first = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        _ = await first.CheckNowAsync(manual: false);

        var second = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        var announced = new List<string>();
        second.Announced += (_, v) => announced.Add(v);
        _ = await second.CheckNowAsync(manual: false);

        Assert.Empty(announced);
        Assert.True(second.ShowBanner);
    }

    [Fact]
    public async Task Later_hides_the_banner_for_this_run_only()
    {
        var path = _temp.File("s.json");
        var watcher = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        _ = await watcher.CheckNowAsync(manual: false);

        watcher.Later();

        Assert.False(watcher.ShowBanner);
        Assert.NotNull(watcher.Available);

        var nextRun = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        _ = await nextRun.CheckNowAsync(manual: false);
        Assert.True(nextRun.ShowBanner);
    }

    [Fact]
    public async Task Skip_this_version_is_remembered_and_a_newer_release_offers_again()
    {
        var path = _temp.File("s.json");
        var watcher = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        _ = await watcher.CheckNowAsync(manual: false);

        watcher.Skip();

        Assert.False(watcher.ShowBanner);
        Assert.True(watcher.IsSkipped);
        Assert.Equal("1.2.0", new SelfUpdateStore(path).Current.SkippedVersion);

        var nextRun = Watch(new SelfUpdateStore(path), () => Available("1.2.0"));
        _ = await nextRun.CheckNowAsync(manual: false);
        Assert.False(nextRun.ShowBanner);
        Assert.NotNull(nextRun.Available);

        var newer = Watch(new SelfUpdateStore(path), () => Available("1.3.0"));
        _ = await newer.CheckNowAsync(manual: false);
        Assert.True(newer.ShowBanner);
    }

    [Fact]
    public async Task A_development_build_never_shows_a_banner()
    {
        var watcher = Watch(
            new SelfUpdateStore(_temp.File("s.json")),
            () => new AppUpdateCheck { State = AppUpdateState.DevBuild, Release = SelfUpdateKit.Release("1.2.0"), Message = "Development build" });

        _ = await watcher.CheckNowAsync(manual: false);

        Assert.False(watcher.ShowBanner);
        Assert.Null(watcher.Available);
    }

    [Fact]
    public async Task A_failed_look_keeps_the_last_good_answer()
    {
        var results = new Queue<AppUpdateCheck>([Available("1.2.0"), new AppUpdateCheck { State = AppUpdateState.Failed, Message = "GitHub was unreachable", LiveFailed = true }]);
        var watcher = Watch(new SelfUpdateStore(_temp.File("s.json")), results.Dequeue);

        _ = await watcher.CheckNowAsync(manual: false);
        _ = await watcher.CheckNowAsync(manual: true);

        Assert.True(watcher.ShowBanner);
        Assert.True(watcher.Latest.LiveFailed);
    }

    [Fact]
    public async Task The_view_is_told_when_the_banner_changes_and_after_every_check()
    {
        var watcher = Watch(new SelfUpdateStore(_temp.File("s.json")), () => Available("1.2.0"));
        var changed = 0;
        var checks = 0;
        watcher.Changed += (_, _) => changed++;
        watcher.Checked += (_, _) => checks++;

        _ = await watcher.CheckNowAsync(manual: false);
        _ = await watcher.CheckNowAsync(manual: false);
        watcher.Later();

        Assert.Equal(2, checks);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task The_loop_waits_before_the_launch_check_and_then_follows_the_persisted_stamp()
    {
        var store = new SelfUpdateStore(_temp.File("s.json"));
        _ = store.Update(s => s with { LastCheckUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var delays = new List<TimeSpan>();
        var asked = new SemaphoreSlim(0);
        var watcher = new AppUpdateWatcher(
            (_, _) =>
            {
                _ = asked.Release();
                return Task.FromResult(Available("1.2.0"));
            },
            store,
            delay: (span, token) =>
            {
                delays.Add(span);
                return delays.Count >= 2 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
            },
            post: action => action());
        _watchers.Add(watcher);

        watcher.Start();
        Assert.True(await asked.WaitAsync(TestTimeouts.Ceiling));
        SpinWait.SpinUntil(() => delays.Count >= 2, TestTimeouts.Ceiling);

        Assert.Equal(AppUpdateWatcher.StartupDelay, delays[0]);
        Assert.InRange(delays[1], TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }
}
