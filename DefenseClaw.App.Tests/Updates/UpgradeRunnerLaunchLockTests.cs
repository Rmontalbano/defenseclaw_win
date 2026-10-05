using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The file that was hashed is the file that starts: the staged file is opened without write or delete sharing for the re-hash and held until the
/// process has started, so nothing can swap it in between. The "installer" here is a copy of a harmless system program (hostname.exe, which ignores
/// the installer's switches) and the "resolver" a script of this test's own that writes a marker file and sleeps; no DefenseClaw command runs.
/// </summary>
public sealed class UpgradeRunnerLaunchLockTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public UpgradeRunnerLaunchLockTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private UpgradeRunner Runner() =>
        new(_services.Cli, new HttpClient(new StubHttpHandler()), _temp.File("staging"));

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private StagedUpgradeAsset Stage(UpgradeChannel channel, string path)
    {
        var hash = HashOf(path);
        return new StagedUpgradeAsset
        {
            Channel = channel,
            AssetName = Path.GetFileName(path),
            Version = "0.8.10",
            FilePath = path,
            SourceUrl = "https://example.invalid/asset",
            ChecksumsUrl = "https://example.invalid/checksums.txt",
            SizeBytes = new FileInfo(path).Length,
            Sha256 = hash,
            ExpectedSha256 = hash,
        };
    }

    private StagedUpgradeAsset StageHarmlessExe()
    {
        var directory = Directory.CreateDirectory(_temp.File(Path.Combine("staging", "0.8.10"))).FullName;
        var path = Path.Combine(directory, UpgradeRunner.InstallerAssetName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), path);
        return Stage(UpgradeChannel.SetupInstaller, path);
    }

    /// <summary>
    /// Free means nobody holds it: it opens for writing with no sharing at all, and it can be deleted. Retried for a moment, because a virus scanner or
    /// the loader can keep a just-used file for a few milliseconds that this code does not control.
    /// </summary>
    private static void AssertFree(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.True(exclusive.CanWrite);
                }

                File.Delete(path);
                Assert.False(File.Exists(path));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(100);
            }
        }
    }

    // ------------------------------------------------------------------ before the process starts

    [Fact]
    public async Task Between_the_hash_and_the_start_the_staged_file_cannot_be_written_or_deleted_but_can_still_be_read()
    {
        var asset = StageHarmlessExe();
        var runner = Runner();
        bool? writeRefused = null;
        bool? deleteRefused = null;
        bool? readAllowed = null;

        // Raised on the runner's thread right before Process.Start: after the hash, before the launch.
        runner.InvocationStarted += (_, _) =>
        {
            try
            {
                using var writer = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writeRefused = false;
            }
            catch (IOException)
            {
                writeRefused = true;
            }

            try
            {
                File.Delete(asset.FilePath);
                deleteRefused = false;
            }
            catch (IOException)
            {
                deleteRefused = true;
            }

            try
            {
                using var reader = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                readAllowed = true;
            }
            catch (IOException)
            {
                readAllowed = false;
            }
        };

        var result = await runner.RunAsync(asset);

        Assert.True(writeRefused, "the staged file was writable between its hash and its start");
        Assert.True(deleteRefused, "the staged file was deletable between its hash and its start");
        Assert.True(readAllowed, "reading is what starting the process needs, and must stay possible");
        Assert.NotNull(result.Invocation);
        Assert.Null(result.FailureReason);
        Assert.True(File.Exists(asset.FilePath));
    }

    [Fact]
    public async Task The_file_is_free_again_once_the_run_is_over()
    {
        var asset = StageHarmlessExe();

        var result = await Runner().RunAsync(asset);

        Assert.NotNull(result.Invocation);
        AssertFree(asset.FilePath);
    }

    [Fact]
    public async Task A_file_someone_has_open_for_writing_is_not_run_at_all()
    {
        var asset = StageHarmlessExe();
        using var writer = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = await Runner().RunAsync(asset);

        Assert.Null(result.Invocation);
        Assert.False(result.Succeeded);
        Assert.Contains("could not be re-read", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public async Task A_file_that_changed_is_refused_and_the_lock_is_let_go()
    {
        var asset = StageHarmlessExe();
        await File.AppendAllTextAsync(asset.FilePath, "tampered");

        var result = await Runner().RunAsync(asset);

        Assert.Contains("changed on disk since it was verified", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(result.Invocation);
        Assert.Empty(_services.Cli.Activity);
        AssertFree(asset.FilePath);
    }

    [Fact]
    public async Task A_cancelled_run_lets_go_of_the_file()
    {
        var asset = StageHarmlessExe();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().RunAsync(asset, cts.Token));

        Assert.Empty(_services.Cli.Activity);
        AssertFree(asset.FilePath);
    }

    // ------------------------------------------------------------------ after it has started

    [Fact]
    public async Task The_file_is_let_go_the_moment_the_process_has_started_not_when_it_ends_and_the_script_still_runs()
    {
        var marker = _temp.File("ran.marker");
        var directory = Directory.CreateDirectory(_temp.File(Path.Combine("staging", "0.8.10"))).FullName;
        var script = Path.Combine(directory, UpgradeRunner.ScriptAssetName);
        await File.WriteAllTextAsync(
            script,
            "param([switch]$Yes)\r\n" +
            $"Set-Content -LiteralPath '{marker}' -Value 'ran'\r\n" +
            "Start-Sleep -Seconds 8\r\n");
        var asset = Stage(UpgradeChannel.ResolverScript, script);

        var run = Task.Run(() => Runner().RunAsync(asset));

        // The script has begun (so PowerShell read it while the hold was, or had just been, in place), and the run is still going.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!File.Exists(marker) && !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(marker), "the script never ran");
        Assert.False(run.IsCompleted, "the run ended before it could be observed");

        // Held for the whole run, this would stay locked until the process ends; released at the start, it is free while the process is still alive.
        var freedWhileRunning = false;
        while (!run.IsCompleted && !freedWhileRunning)
        {
            try
            {
                using var exclusive = new FileStream(script, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                freedWhileRunning = !run.IsCompleted;
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }

        Assert.True(freedWhileRunning, "the staged script was still locked while its process was running");

        var result = await run;
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureReason);
    }
}
