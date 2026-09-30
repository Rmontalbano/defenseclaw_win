using System.Diagnostics;
using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// Each Setup installer staged under <c>%LOCALAPPDATA%\DefenseClaw.App\upgrades\&lt;version&gt;</c> is ~270 MB and nothing ever
/// removed one. After a verified staging (and a successful run) only the current version's directory is kept — and
/// nothing outside the staging root can be reached by the pruning, whatever the directory names or links look like.
/// </summary>
public sealed class UpgradeStagingPruneTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();

    public UpgradeStagingPruneTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private string StagingRoot => _temp.File("staging");

    private UpgradeRunner Runner() => new(_services.Cli, new HttpClient(_http), StagingRoot);

    /// <summary>A staged version directory with the kind of files a staging leaves (an installer, a script, a partial).</summary>
    private string Stage(string version)
    {
        var directory = Path.Combine(StagingRoot, version);
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UpgradeRunner.InstallerAssetName), "stub installer " + version);
        File.WriteAllText(Path.Combine(directory, UpgradeRunner.ScriptAssetName), "stub script " + version);
        File.WriteAllText(Path.Combine(directory, UpgradeRunner.InstallerAssetName + ".partial"), "half");
        return directory;
    }

    private string[] Remaining() =>
        Directory.Exists(StagingRoot)
            ? Directory.GetDirectories(StagingRoot).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    [Fact]
    public void Older_versions_are_removed_and_the_current_one_is_left_intact()
    {
        var current = Stage("v0.8.10");
        _ = Stage("v0.8.9");
        _ = Stage("v0.8.8");

        var removed = Runner().PruneStaging("v0.8.10");

        Assert.Equal(2, removed);
        Assert.Equal(new[] { "v0.8.10" }, Remaining());
        Assert.Equal("stub installer v0.8.10", File.ReadAllText(Path.Combine(current, UpgradeRunner.InstallerAssetName)));
        Assert.True(File.Exists(Path.Combine(current, UpgradeRunner.ScriptAssetName)));
    }

    [Fact]
    public void Versions_without_a_leading_v_are_pruned_the_same_way()
    {
        _ = Stage("0.8.8");
        _ = Stage("0.8.9");
        _ = Stage("0.8.10");

        Assert.Equal(2, Runner().PruneStaging("0.8.10"));
        Assert.Equal(new[] { "0.8.10" }, Remaining());
    }

    [Fact]
    public void Pruning_twice_or_with_nothing_staged_is_harmless()
    {
        Assert.Equal(0, Runner().PruneStaging("v0.8.10"));

        _ = Stage("v0.8.10");
        Assert.Equal(0, Runner().PruneStaging("v0.8.10"));
        Assert.Equal(new[] { "v0.8.10" }, Remaining());
    }

    [Fact]
    public void Only_version_shaped_directories_directly_under_the_root_are_candidates()
    {
        _ = Stage("v0.8.9");
        var foreign = Path.Combine(StagingRoot, "my notes");
        _ = Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "not ours");
        File.WriteAllText(Path.Combine(StagingRoot, "loose.txt"), "a file, not a directory");

        _ = Runner().PruneStaging("v0.8.10");

        Assert.Equal(new[] { "my notes" }, Remaining());
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(StagingRoot, "loose.txt")));
    }

    [Fact]
    public void A_link_inside_the_root_is_skipped_and_what_it_points_at_survives()
    {
        _ = Stage("v0.8.9");
        var outside = _temp.File("outside");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "not staged");

        var link = Path.Combine(StagingRoot, "v0.8.7");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        made.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction could not be created for the test");

        _ = Runner().PruneStaging("v0.8.10");

        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.Equal(new[] { "v0.8.7" }, Remaining());
    }

    [Fact]
    public void A_directory_that_cannot_be_deleted_is_left_and_the_rest_still_go()
    {
        var busy = Stage("v0.8.8");
        _ = Stage("v0.8.9");

        // An installer that is still running holds its file open.
        using var holding = new FileStream(
            Path.Combine(busy, UpgradeRunner.InstallerAssetName), FileMode.Open, FileAccess.Read, FileShare.Read);

        var removed = Runner().PruneStaging("v0.8.10");

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "v0.8.8" }, Remaining());
    }

    [Fact]
    public async Task A_verified_staging_prunes_the_versions_before_it()
    {
        _ = Stage("v0.8.8");
        _ = Stage("v0.8.9");
        var script = new byte[20 * 1024];
        new Random(3).NextBytes(script);
        var sha = Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant();
        _ = _http.Serve(UpgradeRunner.ScriptAssetName, script)
            .Serve(UpgradeRunner.ChecksumsAssetName, $"{sha}  {UpgradeRunner.ScriptAssetName}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "v0.8.10" }, Remaining());
        Assert.True(File.Exists(result.Asset!.FilePath));
    }

    [Fact]
    public async Task A_failed_staging_leaves_older_versions_alone()
    {
        _ = Stage("v0.8.9");
        var script = new byte[20 * 1024];
        new Random(3).NextBytes(script);
        _ = _http.Serve(UpgradeRunner.ScriptAssetName, script)
            .Serve(UpgradeRunner.ChecksumsAssetName, $"{new string('0', 64)}  {UpgradeRunner.ScriptAssetName}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(new[] { "v0.8.9" }, Remaining());
    }
}
