using System.Net;
using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>Version comparison, release parsing and asset selection, the check's throttle and ETag, and the notes text.</summary>
public sealed class AppUpdateCheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();

    public void Dispose() => _temp.Dispose();

    private static AppVersion V(string text) => AppVersion.TryParse(text, out var v) ? v : throw new InvalidOperationException(text);

    private SelfUpdateStore NewStore() => new(_temp.File("state.json"));

    // ------------------------------------------------------------------ versions

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0", "1.0.0-dev", 1)]
    [InlineData("1.0.0-dev", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("v1.2.3+abc123", "1.2.3", 0)]
    public void Versions_compare_by_semantic_precedence(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(V(left).CompareTo(V(right))));

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("one.two.three")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0 beta")]
    public void Text_that_is_not_a_version_does_not_parse(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void A_dev_suffix_marks_a_development_build()
    {
        Assert.True(V("1.0.0-dev").IsDev);
        Assert.True(V("1.0.0-dev.4+sha").IsDev);
        Assert.False(V("1.0.0-rc.1").IsDev);
        Assert.False(V("1.0.0").IsDev);
        Assert.True(V("0.0.0").IsDev);
    }

    [Fact]
    public void The_sdk_revision_suffix_after_the_plus_is_ignored()
    {
        Assert.Equal(0, V("1.2.0+abc123.0123456789abcdef0123456789abcdef01234567").CompareTo(V("1.2.0")));
        Assert.Equal("1.2.0", V("1.2.0+abc123.0123456789abcdef").ToString());
        Assert.True(V("0.0.0-dev+abc.def").IsDev);
    }

    [Fact]
    public void The_running_version_comes_from_the_assembly()
    {
        // The test host is not the app, but the call must not throw and must strip +sha: the app's own assembly has an informational version.
        _ = AppVersion.Running();
        Assert.Equal("1.2.3", V("1.2.3+deadbeef").ToString());
    }

    // ------------------------------------------------------------------ releases

    [Fact]
    public void The_two_assets_are_picked_by_exact_name_from_this_repository()
    {
        var release = SelfUpdateKit.Release("1.2.0");

        Assert.Equal(V("1.2.0"), release.Version);
        Assert.Equal("DefenseClawWin-1.2.0-win-x64.zip", release.Zip!.Name);
        Assert.StartsWith("sha256:", release.Zip.Digest);
        Assert.Equal("checksums.txt", release.Checksums!.Name);
        Assert.Null(release.NotInstallableReason);
    }

    [Fact]
    public void A_look_alike_zip_is_not_the_asset()
    {
        var json = SelfUpdateKit.ReleaseJson("1.2.0", zipNameOverride: "DefenseClawWin-1.2.0-win-x64.zip.sig");
        var release = SelfUpdateRelease.Parse(json, out _)!;

        Assert.Null(release.Zip);
        Assert.Contains("no DefenseClawWin-1.2.0-win-x64.zip", release.NotInstallableReason);
    }

    [Fact]
    public void An_asset_link_outside_the_repository_is_dropped()
    {
        var json = SelfUpdateKit.ReleaseJson("1.2.0", zipUrlOverride: "https://evil.example/Rmontalbano/defenseclaw_win/releases/download/v1.2.0/DefenseClawWin-1.2.0-win-x64.zip");

        Assert.Null(SelfUpdateRelease.Parse(json, out _)!.Zip);
    }

    [Fact]
    public void An_asset_link_to_another_tag_is_dropped()
    {
        var json = SelfUpdateKit.ReleaseJson("1.2.0", zipUrlOverride: SelfUpdateKit.AssetUrl("1.1.0", "DefenseClawWin-1.2.0-win-x64.zip"));

        Assert.Null(SelfUpdateRelease.Parse(json, out _)!.Zip);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Drafts_and_prereleases_are_ignored(bool prerelease, bool draft)
    {
        Assert.Null(SelfUpdateRelease.Parse(SelfUpdateKit.ReleaseJson("1.2.0", prerelease: prerelease, draft: draft), out var problem));
        Assert.Contains("ignored", problem);
    }

    [Fact]
    public void A_tag_that_is_not_vX_Y_Z_is_not_a_release()
    {
        Assert.Null(SelfUpdateRelease.Parse("""{"tag_name":"nightly","assets":[]}""", out _));
        Assert.Null(SelfUpdateRelease.Parse("""{"tag_name":"v1.2.0-rc.1","assets":[]}""", out _));
        Assert.Null(SelfUpdateRelease.Parse("not json", out _));
    }

    // ------------------------------------------------------------------ checking

    private AppUpdateChecker Checker(SelfUpdateKit.FakeGitHub github, string running, SelfUpdateStore? store = null) =>
        new(store ?? NewStore(), V(running), new HttpClient(github), _clock);

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = Checker(github, "1.1.0");

        var check = await checker.CheckAsync(manual: false);

        Assert.Equal(AppUpdateState.UpdateAvailable, check.State);
        Assert.Equal(V("1.2.0"), check.Release!.Version);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    public async Task The_same_or_a_newer_running_version_is_up_to_date(string running)
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = Checker(github, running);

        Assert.Equal(AppUpdateState.UpToDate, (await checker.CheckAsync(manual: true)).State);
    }

    [Fact]
    public async Task A_dev_build_is_never_offered_but_a_manual_check_says_what_is_latest()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = Checker(github, "1.0.0-dev");

        var check = await checker.CheckAsync(manual: true);

        Assert.Equal(AppUpdateState.DevBuild, check.State);
        Assert.False(check.IsUpdateAvailable);
        Assert.Contains("Development build", check.Message);
        Assert.Contains("latest release is 1.2.0", check.Message);
    }

    [Fact]
    public async Task A_newer_release_without_its_files_is_not_offered_and_says_why()
    {
        var github = new SelfUpdateKit.FakeGitHub();
        _ = github.Serve(SelfUpdateRelease.LatestEndpoint.ToString(), SelfUpdateKit.ReleaseJson("1.2.0", includeChecksums: false));
        using var checker = Checker(github, "1.0.0");

        var check = await checker.CheckAsync(manual: true);

        Assert.Equal(AppUpdateState.Failed, check.State);
        Assert.Contains("checksums.txt", check.Message);
    }

    [Fact]
    public async Task An_automatic_check_inside_six_hours_reads_the_stored_release_without_asking()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        var store = NewStore();
        using var checker = Checker(github, "1.0.0", store);
        _ = await checker.CheckAsync(manual: false);
        Assert.Single(github.Urls);

        _clock.Advance(TimeSpan.FromHours(5));
        var again = await checker.CheckAsync(manual: false);

        Assert.Single(github.Urls);
        Assert.True(again.FromCache);
        Assert.Equal(AppUpdateState.UpdateAvailable, again.State);
    }

    [Fact]
    public async Task After_six_hours_it_asks_again()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = Checker(github, "1.0.0");
        _ = await checker.CheckAsync(manual: false);

        _clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        _ = await checker.CheckAsync(manual: false);

        Assert.Equal(2, github.Urls.Count);
    }

    [Fact]
    public async Task A_manual_check_always_asks_and_sends_the_etag()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", etag: "\"abc\"");
        using var checker = Checker(github, "1.0.0");
        _ = await checker.CheckAsync(manual: false);

        var second = await checker.CheckAsync(manual: true);

        Assert.Equal(2, github.Requests.Count);
        Assert.Contains(github.Requests[1].Headers.IfNoneMatch, t => t.ToString() == "\"abc\"");
        Assert.Empty(github.Requests[0].Headers.IfNoneMatch);
        Assert.True(second.FromCache);
        Assert.Equal(AppUpdateState.UpdateAvailable, second.State);
    }

    [Fact]
    public async Task The_throttle_and_the_etag_survive_a_restart()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", etag: "\"abc\"");
        using (var first = Checker(github, "1.0.0"))
        {
            _ = await first.CheckAsync(manual: false);
        }

        using var second = Checker(github, "1.0.0");
        var check = await second.CheckAsync(manual: false);

        Assert.Single(github.Urls);
        Assert.True(check.FromCache);
        Assert.Equal("\"abc\"", NewStore().Current.ETag);
    }

    [Fact]
    public async Task A_rate_limit_is_a_failure_that_keeps_the_last_answer()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = Checker(github, "1.0.0");
        _ = await checker.CheckAsync(manual: false);
        _ = github.ServeStatus(SelfUpdateRelease.LatestEndpoint.ToString(), HttpStatusCode.Forbidden);

        var limited = await checker.CheckAsync(manual: true);

        Assert.True(limited.LiveFailed);
        Assert.Contains("rate limit", limited.Message);
        Assert.Equal(AppUpdateState.UpdateAvailable, limited.State);
    }

    [Fact]
    public async Task An_unreachable_github_with_nothing_stored_is_a_failure_not_up_to_date()
    {
        var github = new SelfUpdateKit.FakeGitHub().Throw(SelfUpdateRelease.LatestEndpoint.ToString(), new HttpRequestException("no route"));
        using var checker = Checker(github, "1.0.0");

        var check = await checker.CheckAsync(manual: true);

        Assert.Equal(AppUpdateState.Failed, check.State);
        Assert.Contains("unreachable", check.Message);
    }

    [Fact]
    public async Task No_published_release_is_said_plainly()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeStatus(SelfUpdateRelease.LatestEndpoint.ToString(), HttpStatusCode.NotFound);
        using var checker = Checker(github, "1.0.0");

        var check = await checker.CheckAsync(manual: true);

        // Its own outcome, not a failure: the check worked and there is nothing to install yet.
        Assert.Equal(AppUpdateState.NoRelease, check.State);
        Assert.Contains("No release", check.Message);
        Assert.Null(check.Release);
        Assert.False(check.LiveFailed);
        Assert.False(check.IsUpdateAvailable);
    }

    [Fact]
    public async Task No_published_release_counts_as_a_check_and_forgets_a_release_that_has_been_withdrawn()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        var store = NewStore();
        using var checker = new AppUpdateChecker(store, V("1.0.0"), new HttpClient(github), _clock);
        Assert.Equal(AppUpdateState.UpdateAvailable, (await checker.CheckAsync(manual: true)).State);
        Assert.NotNull(store.Current.ReleaseJson);

        _ = github.ServeStatus(SelfUpdateRelease.LatestEndpoint.ToString(), HttpStatusCode.NotFound);
        _clock.Advance(TimeSpan.FromHours(7));
        var check = await checker.CheckAsync(manual: false);

        Assert.Equal(AppUpdateState.NoRelease, check.State);
        Assert.Null(store.Current.ReleaseJson);
        Assert.Null(store.Current.ETag);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), store.Current.LastCheckUnix);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_real_failure_from_github_is_still_could_not_check_and_not_no_release(HttpStatusCode status)
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeStatus(SelfUpdateRelease.LatestEndpoint.ToString(), status);
        using var checker = Checker(github, "1.0.0");

        var check = await checker.CheckAsync(manual: true);

        Assert.Equal(AppUpdateState.Failed, check.State);
        Assert.DoesNotContain("No release", check.Message);
    }

    [Fact]
    public async Task An_unreadable_own_version_fails_the_check()
    {
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        using var checker = new AppUpdateChecker(NewStore(), null, new HttpClient(github), _clock);

        Assert.Equal(AppUpdateState.Failed, (await checker.CheckAsync(manual: true)).State);
    }

    // ------------------------------------------------------------------ notes

    [Fact]
    public void Release_notes_are_read_as_plain_text()
    {
        var plain = ReleaseNotesText.ToPlain("## Highlights\n- **Faster** start with [docs](https://x.example/y)\n![img](https://x.example/a.png)\n<b>bold</b>\n```\ncode here\n```\n\n\n\nEnd");

        Assert.DoesNotContain("#", plain);
        Assert.DoesNotContain("**", plain);
        Assert.DoesNotContain("https://", plain);
        Assert.DoesNotContain("<b>", plain);
        Assert.Contains("• Faster start with docs", plain);
        Assert.Contains("code here", plain);
        Assert.DoesNotContain("\n\n\n", plain);
        Assert.Equal("Highlights", ReleaseNotesText.FirstLine(plain));
    }

    [Fact]
    public void Missing_or_oversize_notes_are_handled()
    {
        Assert.Equal("This release has no notes.", ReleaseNotesText.ToPlain(null));
        var long_ = ReleaseNotesText.ToPlain(string.Join("\n", Enumerable.Repeat("line of notes", 2000)));
        Assert.True(long_.Length < ReleaseNotesText.MaxChars + 100);
        Assert.Contains("release page has the rest", long_);
    }
}
