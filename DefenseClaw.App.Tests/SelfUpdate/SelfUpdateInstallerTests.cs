using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>Download, verification, swap, rollback and clean-up over a temp folder with fake files. Nothing real is replaced and nothing leaves the process.</summary>
public sealed class SelfUpdateInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _appDir;
    private readonly string _dataRoot;
    private readonly string _appExe;
    private readonly string _helperExe;

    public SelfUpdateInstallerTests()
    {
        _appDir = Path.Combine(_temp.Path, "Programs", "DefenseClawWin");
        _dataRoot = Path.Combine(_temp.Path, "data");
        _ = Directory.CreateDirectory(_appDir);
        _appExe = Path.Combine(_appDir, SelfUpdatePackage.AppExeName);
        _helperExe = Path.Combine(_appDir, SelfUpdatePackage.HelperExeName);
        File.WriteAllText(_appExe, "old-app");
        File.WriteAllText(_helperExe, "old-helper");
    }

    public void Dispose() => _temp.Dispose();

    private SelfUpdateInstaller Installer(
        SelfUpdateKit.FakeGitHub github,
        Func<string, string?>? versionOf = null,
        Func<string?>? blocked = null,
        SelfUpdateLimits? limits = null)
    {
        var client = new HttpClient(github);
        return new SelfUpdateInstaller(() => client, _appExe, _dataRoot, versionOf ?? (_ => "1.2.0+abc"), blocked, limits);
    }

    private static (SelfUpdateRelease Release, SelfUpdateKit.FakeGitHub Github) Served(byte[]? zip = null, string? digest = "auto")
    {
        zip ??= SelfUpdateKit.GoodZip();
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", zip, digest);
        return (SelfUpdateKit.Release("1.2.0", zip, digest), github);
    }

    // ------------------------------------------------------------------ staging

    [Fact]
    public async Task A_good_release_is_downloaded_verified_and_unpacked()
    {
        var (release, github) = Served();

        var result = await Installer(github).StageAsync(release, null, CancellationToken.None);

        Assert.Null(result.Refusal);
        var staged = result.Staged!;
        Assert.Equal("new-app", File.ReadAllText(staged.NewApp));
        Assert.Equal("new-helper", File.ReadAllText(staged.NewHelper));
        Assert.Equal(SelfUpdateKit.Sha(SelfUpdateKit.GoodZip()), staged.Sha256);
        Assert.Contains(Path.Combine("self-update", "1.2.0"), staged.Directory);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
    }

    [Fact]
    public async Task A_checksum_mismatch_is_refused_and_nothing_is_replaced()
    {
        var zip = SelfUpdateKit.GoodZip();
        var github = new SelfUpdateKit.FakeGitHub();
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", "checksums.txt"), $"{new string('a', 64)}  {SelfUpdateKit.ZipName("1.2.0")}\n");
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", SelfUpdateKit.ZipName("1.2.0")), zip);

        var result = await Installer(github).StageAsync(SelfUpdateKit.Release("1.2.0", zip, digest: null), null, CancellationToken.None);

        Assert.Null(result.Staged);
        Assert.Contains("does not match checksums.txt", result.Refusal);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
    }

    [Fact]
    public async Task A_digest_that_disagrees_with_checksums_is_refused_before_the_big_download()
    {
        var zip = SelfUpdateKit.GoodZip();
        var wrongDigest = "sha256:" + new string('b', 64);
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", zip, wrongDigest);

        var result = await Installer(github).StageAsync(SelfUpdateKit.Release("1.2.0", zip, wrongDigest), null, CancellationToken.None);

        Assert.Contains("disagree", result.Refusal);
        Assert.DoesNotContain(SelfUpdateKit.AssetUrl("1.2.0", SelfUpdateKit.ZipName("1.2.0")), github.Urls);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_digest_is_refused()
    {
        // checksums.txt matches the (tampered) bytes; GitHub's digest is for the real zip.
        var real = SelfUpdateKit.GoodZip();
        var tampered = SelfUpdateKit.GoodZip(appText: "evil-app");
        var github = new SelfUpdateKit.FakeGitHub();
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", "checksums.txt"), SelfUpdateKit.Checksums("1.2.0", tampered));
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", SelfUpdateKit.ZipName("1.2.0")), tampered);

        var result = await Installer(github).StageAsync(SelfUpdateKit.Release("1.2.0", real), null, CancellationToken.None);

        Assert.Null(result.Staged);
        Assert.Contains("disagree", result.Refusal);
    }

    [Fact]
    public async Task A_missing_digest_is_noted_and_checksums_alone_decide()
    {
        var (release, github) = Served(digest: null);

        var result = await Installer(github).StageAsync(release, null, CancellationToken.None);

        Assert.Null(result.Refusal);
        Assert.Contains(result.Staged!.Notes, n => n.Contains("no digest"));
    }

    [Fact]
    public async Task A_digest_that_is_not_sha256_is_refused()
    {
        var (release, github) = Served(digest: "sha512:abcd");

        var result = await Installer(github).StageAsync(release, null, CancellationToken.None);

        Assert.Contains("not a SHA-256", result.Refusal);
    }

    [Fact]
    public async Task Checksums_that_do_not_list_the_zip_are_refused()
    {
        var zip = SelfUpdateKit.GoodZip();
        var github = new SelfUpdateKit.FakeGitHub();
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", "checksums.txt"), $"{SelfUpdateKit.Sha(zip)}  something-else.zip\n");
        _ = github.Serve(SelfUpdateKit.AssetUrl("1.2.0", SelfUpdateKit.ZipName("1.2.0")), zip);

        var result = await Installer(github).StageAsync(SelfUpdateKit.Release("1.2.0", zip), null, CancellationToken.None);

        Assert.Contains("does not list", result.Refusal);
    }

    public static TheoryData<string> BadEntrySets => new()
    {
        "extra",
        "path",
        "missing-helper",
        "duplicate-case",
        "folder",
    };

    [Theory]
    [MemberData(nameof(BadEntrySets))]
    public async Task A_zip_that_is_not_exactly_the_two_exes_at_its_root_is_refused(string kind)
    {
        var app = SelfUpdateKit.Bytes("new-app");
        var helper = SelfUpdateKit.Bytes("new-helper");
        var zip = kind switch
        {
            "extra" => SelfUpdateKit.Zip((SelfUpdatePackage.AppExeName, app), (SelfUpdatePackage.HelperExeName, helper), ("readme.txt", app)),
            "path" => SelfUpdateKit.Zip((SelfUpdatePackage.AppExeName, app), ("..\\" + SelfUpdatePackage.HelperExeName, helper)),
            "missing-helper" => SelfUpdateKit.Zip((SelfUpdatePackage.AppExeName, app)),
            "duplicate-case" => SelfUpdateKit.Zip((SelfUpdatePackage.AppExeName, app), ("defenseclaw.app.exe", app)),
            _ => SelfUpdateKit.Zip((SelfUpdatePackage.AppExeName, app), ("sub/" + SelfUpdatePackage.HelperExeName, helper)),
        };
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", zip);

        var result = await Installer(github).StageAsync(SelfUpdateKit.Release("1.2.0", zip), null, CancellationToken.None);

        Assert.Null(result.Staged);
        Assert.NotNull(result.Refusal);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
    }

    [Fact]
    public async Task A_wrong_product_version_is_refused()
    {
        var (release, github) = Served();

        var result = await Installer(github, versionOf: path => Path.GetFileName(path) == SelfUpdatePackage.AppExeName ? "1.1.9" : "1.2.0").StageAsync(release, null, CancellationToken.None);

        Assert.Null(result.Staged);
        Assert.Contains("is version 1.1.9, not the release's 1.2.0", result.Refusal);
    }

    [Fact]
    public async Task An_unreadable_product_version_is_refused()
    {
        var (release, github) = Served();

        var result = await Installer(github, versionOf: _ => null).StageAsync(release, null, CancellationToken.None);

        Assert.Contains("not a version number", result.Refusal);
    }

    [Fact]
    public async Task An_oversize_download_is_refused()
    {
        var (release, github) = Served();

        var result = await Installer(github, limits: new SelfUpdateLimits { ZipBytes = 10 }).StageAsync(release, null, CancellationToken.None);

        Assert.Contains("limit", result.Refusal);
    }

    [Fact]
    public async Task A_zip_entry_that_inflates_past_its_cap_is_stopped()
    {
        var zip = SelfUpdateKit.GoodZip(appText: new string('x', 5000));
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", zip);

        var result = await Installer(github, limits: new SelfUpdateLimits { AppExeBytes = 1000 }).StageAsync(SelfUpdateKit.Release("1.2.0", zip), null, CancellationToken.None);

        Assert.Null(result.Staged);
        Assert.Contains("MiB limit", result.Refusal ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"..\" + SelfUpdatePackage.AppExeName)]
    [InlineData("../" + SelfUpdatePackage.AppExeName)]
    [InlineData(@"sub\..\..\evil.exe")]
    [InlineData(@"C:\evil.exe")]
    [InlineData("evil.exe")]
    public void Unpacking_on_its_own_never_writes_an_entry_outside_the_folder_or_under_another_name(string hostile)
    {
        var zipPath = Path.Combine(_temp.Path, "hostile.zip");
        File.WriteAllBytes(zipPath, SelfUpdateKit.Zip((hostile, SelfUpdateKit.Bytes("evil")), (SelfUpdatePackage.HelperExeName, SelfUpdateKit.Bytes("helper"))));
        var destination = Directory.CreateDirectory(Path.Combine(_temp.Path, "unpack", "inner")).FullName;

        var problem = SelfUpdatePackage.Extract(zipPath, destination, SelfUpdateLimits.Default);

        Assert.Contains("unexpected entry", problem ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "unpack"), "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.exe")));
    }

    [Fact]
    public async Task A_busy_state_changing_run_blocks_the_update()
    {
        var (release, github) = Served();

        var result = await Installer(github, blocked: () => "A command that changes DefenseClaw is still running.").StageAsync(release, null, CancellationToken.None);

        Assert.Contains("still running", result.Refusal);
        Assert.Empty(github.Urls);
    }

    [Fact]
    public async Task A_release_missing_its_zip_is_refused()
    {
        var github = new SelfUpdateKit.FakeGitHub();
        var release = SelfUpdateRelease.Parse(SelfUpdateKit.ReleaseJson("1.2.0", includeZip: false), out _)!;

        var result = await Installer(github).StageAsync(release, null, CancellationToken.None);

        Assert.Contains("no DefenseClawWin-1.2.0-win-x64.zip", result.Refusal);
    }

    [Fact]
    public void An_exe_with_another_name_is_not_updated_in_place()
    {
        var other = Path.Combine(_appDir, "Renamed.exe");
        File.WriteAllText(other, "x");
        var installer = new SelfUpdateInstaller(() => new HttpClient(new SelfUpdateKit.FakeGitHub()), other, _dataRoot);

        Assert.Contains("not DefenseClaw.App.exe", installer.PreconditionRefusal());
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_refused_with_the_reason()
    {
        var missing = Path.Combine(_temp.Path, "gone", SelfUpdatePackage.AppExeName);
        var installer = new SelfUpdateInstaller(() => new HttpClient(new SelfUpdateKit.FakeGitHub()), missing, _dataRoot);

        Assert.Contains("not writable", installer.PreconditionRefusal());
    }

    [Fact]
    public void A_build_output_folder_is_flagged_not_refused()
    {
        var publish = Path.Combine(_temp.Path, "repo", "publish");
        _ = Directory.CreateDirectory(publish);
        var exe = Path.Combine(publish, SelfUpdatePackage.AppExeName);
        File.WriteAllText(exe, "x");
        var installer = new SelfUpdateInstaller(() => new HttpClient(new SelfUpdateKit.FakeGitHub()), exe, _dataRoot);

        Assert.True(installer.IsDevelopmentFolder);
        Assert.Null(installer.PreconditionRefusal());
    }

    // ------------------------------------------------------------------ swap, rollback, clean-up

    private async Task<(SelfUpdateInstaller Installer, StagedUpdate Staged)> Staged()
    {
        var (release, github) = Served();
        var installer = Installer(github);
        var staged = (await installer.StageAsync(release, null, CancellationToken.None)).Staged!;
        return (installer, staged);
    }

    [Fact]
    public async Task The_swap_keeps_the_old_files_beside_the_new_ones_and_writes_the_marker()
    {
        var (installer, staged) = await Staged();

        var (swap, refusal) = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        Assert.Null(refusal);
        Assert.NotNull(swap);
        Assert.Equal("new-app", File.ReadAllText(_appExe));
        Assert.Equal("new-helper", File.ReadAllText(_helperExe));
        Assert.Equal("old-app", File.ReadAllText(_appExe + ".old"));
        Assert.Equal("old-helper", File.ReadAllText(_helperExe + ".old"));
        var marker = installer.ReadMarker()!;
        Assert.Equal("1.1.0", marker.From);
        Assert.Equal("1.2.0", marker.To);
    }

    [Fact]
    public async Task A_rollback_puts_everything_back_and_removes_the_marker()
    {
        var (installer, staged) = await Staged();
        var (swap, _) = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        var problems = swap!.Rollback();

        Assert.Empty(problems);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
        Assert.Equal("old-helper", File.ReadAllText(_helperExe));
        Assert.False(File.Exists(_appExe + ".old"));
        Assert.False(File.Exists(_helperExe + ".old"));
        Assert.Null(installer.ReadMarker());
    }

    [Fact]
    public async Task A_swap_that_fails_midway_undoes_itself()
    {
        var (installer, staged) = await Staged();
        File.Delete(staged.NewHelper);

        var (swap, refusal) = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        Assert.Null(swap);
        Assert.Contains("Replacing the program files failed", refusal);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
        Assert.Equal("old-helper", File.ReadAllText(_helperExe));
        Assert.False(File.Exists(_appExe + ".old"));
        Assert.Null(installer.ReadMarker());
    }

    [Fact]
    public async Task A_missing_helper_beside_the_app_is_simply_added()
    {
        var (installer, staged) = await Staged();
        File.Delete(_helperExe);

        var (swap, _) = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        Assert.Equal("new-helper", File.ReadAllText(_helperExe));
        Assert.False(File.Exists(_helperExe + ".old"));
        Assert.Empty(swap!.Rollback());
        Assert.False(File.Exists(_helperExe));
    }

    [Fact]
    public async Task The_next_start_as_the_new_version_removes_the_old_files_marker_and_staging()
    {
        var (installer, staged) = await Staged();
        _ = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        var result = installer.CompletePendingUpdate(new AppVersion(1, 2, 0, null));

        Assert.Equal(PostUpdateOutcome.Updated, result.Outcome);
        Assert.Empty(result.StillLocked);
        Assert.False(File.Exists(_appExe + ".old"));
        Assert.False(File.Exists(_helperExe + ".old"));
        Assert.Null(installer.ReadMarker());
        Assert.False(Directory.Exists(staged.Directory));
        Assert.Equal("new-app", File.ReadAllText(_appExe));
    }

    [Fact]
    public async Task An_old_file_still_held_by_the_previous_process_is_reported_and_the_marker_stays()
    {
        var (installer, staged) = await Staged();
        _ = installer.Swap(staged, new AppVersion(1, 1, 0, null));
        using var holding = new FileStream(_appExe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = installer.CompletePendingUpdate(new AppVersion(1, 2, 0, null));

        Assert.Single(result.StillLocked);
        Assert.NotNull(installer.ReadMarker());
    }

    [Fact]
    public async Task A_marker_for_another_version_means_that_update_did_not_take()
    {
        var (installer, staged) = await Staged();
        _ = installer.Swap(staged, new AppVersion(1, 1, 0, null));

        var result = installer.CompletePendingUpdate(new AppVersion(1, 1, 0, null));

        Assert.Equal(PostUpdateOutcome.NotApplied, result.Outcome);
        Assert.Equal("1.2.0", result.Version);
        Assert.Null(installer.ReadMarker());
    }

    [Fact]
    public void Old_files_without_a_marker_are_cleaned_up()
    {
        File.WriteAllText(_appExe + ".old", "x");
        File.WriteAllText(_helperExe + ".old", "x");
        var installer = Installer(new SelfUpdateKit.FakeGitHub());

        var result = installer.CompletePendingUpdate(new AppVersion(1, 2, 0, null));

        Assert.Equal(PostUpdateOutcome.CleanedLeftovers, result.Outcome);
        Assert.False(File.Exists(_appExe + ".old"));
        Assert.False(File.Exists(_helperExe + ".old"));
    }

    [Fact]
    public void A_normal_start_with_nothing_pending_changes_nothing()
    {
        var installer = Installer(new SelfUpdateKit.FakeGitHub());

        Assert.Equal(PostUpdateOutcome.None, installer.CompletePendingUpdate(new AppVersion(1, 2, 0, null)).Outcome);
        Assert.Equal("old-app", File.ReadAllText(_appExe));
    }

    [Fact]
    public void Checksums_lines_are_read_in_both_marker_styles_and_ambiguity_is_refused()
    {
        var hash = new string('c', 64);
        Assert.Equal(hash, SelfUpdatePackage.FindChecksum($"{hash.ToUpperInvariant()} *a.zip\n", "a.zip", out _));
        Assert.Null(SelfUpdatePackage.FindChecksum($"{hash}  a.zip\n{new string('d', 64)}  a.zip\n", "a.zip", out var twice));
        Assert.Contains("twice", twice);
        Assert.Null(SelfUpdatePackage.FindChecksum($"{hash}  a.zip.sig\n", "a.zip", out _));
        Assert.Equal(hash, SelfUpdatePackage.FindChecksum($"{hash}  a.zip\r\n", "a.zip", out _));
    }

    [Fact]
    public void The_trust_sentence_is_honest_about_signing()
    {
        Assert.Contains("not Authenticode-signed", SelfUpdateInstaller.TrustSentence);
        Assert.Contains("SHA-256", SelfUpdateInstaller.TrustSentence);
        Assert.Contains("digest", SelfUpdateInstaller.TrustSentence, StringComparison.Ordinal);
        Assert.DoesNotContain("signed by", SelfUpdateInstaller.TrustSentence, StringComparison.OrdinalIgnoreCase);
    }
}
