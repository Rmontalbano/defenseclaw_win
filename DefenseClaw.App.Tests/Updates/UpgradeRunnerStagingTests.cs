using System.Net;
using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The download-and-verify half of an upgrade, against a stub server. What matters is what is NOT written:
/// a stub, a mismatch or a missing checksum entry must leave nothing on disk that could later be run. The run
/// step is only exercised through its refusals — an untouched, verified file would start a real process.
/// </summary>
public sealed class UpgradeRunnerStagingTests : IDisposable
{
    private const string Script = UpgradeRunner.ScriptAssetName;
    private const string Installer = UpgradeRunner.InstallerAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();

    public UpgradeRunnerStagingTests()
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

    private static byte[] Bytes(int length, byte seed = 7)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string[] StagedFiles() =>
        Directory.Exists(StagingRoot) ? Directory.GetFiles(StagingRoot, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    // ------------------------------------------------------------------ the resolver script

    [Fact]
    public async Task A_script_whose_hash_matches_checksums_txt_is_staged_where_the_result_says()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10", checksumsSigstoreSigned: true);

        Assert.True(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.Verified, result.Outcome);
        var asset = result.Asset!;
        Assert.Equal(UpgradeChannel.ResolverScript, asset.Channel);
        Assert.Equal(Sha256(script), asset.Sha256);
        Assert.Equal(Sha256(script), asset.ExpectedSha256);
        Assert.True(asset.ChecksumsSigstoreSigned);
        Assert.Equal(script.Length, asset.SizeBytes);
        Assert.Equal(Path.Combine(StagingRoot, "v0.8.10", Script), asset.FilePath);
        Assert.Equal(script, await File.ReadAllBytesAsync(asset.FilePath));
    }

    [Fact]
    public async Task A_placeholder_stub_is_refused_before_anything_else_is_fetched_and_nothing_is_written()
    {
        var stub = Bytes(133);
        _http.Serve(Script, stub).Serve(Checksums, $"{Sha256(stub)}  {Script}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.StubDetected, result.Outcome);
        Assert.Equal(133, result.SizeBytes);
        Assert.Null(result.Asset);
        Assert.Equal(new[] { Script }, _http.Requested);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_script_that_does_not_hash_to_its_checksum_entry_is_refused_and_nothing_is_written()
    {
        var script = Bytes(20 * 1024);
        var tampered = Bytes(20 * 1024, seed: 9);
        _http.Serve(Script, tampered).Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(Sha256(tampered), result.ComputedSha256);
        Assert.Equal(Sha256(script), result.ExpectedSha256);
        Assert.Contains("Do not run this script", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_checksums_file_with_no_entry_for_the_script_leaves_the_download_unanchored_and_unwritten()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  something-else.exe\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Contains("no line for", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_missing_checksums_file_is_unverified_not_trusted()
    {
        _http.Serve(Script, Bytes(20 * 1024));

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public async Task Rate_limiting_on_either_download_is_reported_as_such(HttpStatusCode status)
    {
        var script = Bytes(20 * 1024);

        _http.ServeStatus(Script, status);
        Assert.Equal(UpgradeStagingOutcome.RateLimited, (await Runner().DownloadAndVerifyAsync("v0.8.10")).Outcome);

        var other = new StubHttpHandler().Serve(Script, script).ServeStatus(Checksums, status);
        var runner = new UpgradeRunner(_services.Cli, new HttpClient(other), StagingRoot);
        Assert.Equal(UpgradeStagingOutcome.RateLimited, (await runner.DownloadAndVerifyAsync("v0.8.10")).Outcome);
        Assert.Empty(StagedFiles());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_script_the_release_does_not_serve_is_a_failed_download(HttpStatusCode status)
    {
        _http.ServeStatus(Script, status);

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.DownloadFailed, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task An_unreachable_server_is_a_failed_download_not_an_exception()
    {
        _http.Throw(Script, new HttpRequestException("no route to host"));

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.DownloadFailed, result.Outcome);
        Assert.Contains("no route to host", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_far_larger_than_any_published_one_is_refused()
    {
        _http.Serve(Script, Bytes((int)UpgradeRunner.MaximumPlausibleScriptBytes + 1));

        var result = await Runner().DownloadAndVerifyAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.DownloadFailed, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_version_tag_cannot_walk_out_of_the_staging_root()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Runner().DownloadAndVerifyAsync("v0.8.9/../../../escaped");

        Assert.True(result.Succeeded);
        var staged = Path.GetFullPath(result.Asset!.FilePath);
        Assert.StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, staged, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StagingRoot, Path.GetDirectoryName(Path.GetDirectoryName(staged)));
    }

    // ------------------------------------------------------------------ the Setup installer

    [Fact]
    public async Task The_installer_checksum_is_fetched_first_so_a_missing_one_costs_one_small_request()
    {
        _http.Serve(Installer, Bytes(1024));

        var result = await Runner().DownloadAndVerifyInstallerAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Equal(new[] { Checksums }, _http.Requested);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_rate_limited_checksum_fetch_ends_the_installer_download_before_it_starts()
    {
        _http.ServeStatus(Checksums, HttpStatusCode.Forbidden).Serve(Installer, Bytes(1024));

        var result = await Runner().DownloadAndVerifyInstallerAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.RateLimited, result.Outcome);
        Assert.Equal(new[] { Checksums }, _http.Requested);
    }

    [Fact]
    public async Task A_checksums_file_with_no_installer_line_means_no_download_at_all()
    {
        _http.Serve(Checksums, $"{Sha256(Bytes(10))}  {Script}\n").Serve(Installer, Bytes(1024));

        var result = await Runner().DownloadAndVerifyInstallerAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.DoesNotContain(Installer, _http.Requested);
    }

    [Fact]
    public async Task A_133_byte_installer_stub_is_refused_even_though_its_hash_is_in_checksums_txt_and_no_partial_is_left()
    {
        var stub = Bytes(133);
        _http.Serve(Checksums, $"{Sha256(stub)}  {Installer}\n").Serve(Installer, stub);

        var result = await Runner().DownloadAndVerifyInstallerAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.StubDetected, result.Outcome);
        Assert.Equal(Sha256(stub), result.ComputedSha256);
        Assert.Equal(Sha256(stub), result.ExpectedSha256);
        Assert.Null(result.Asset);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task An_installer_the_release_does_not_serve_is_a_failed_download_with_nothing_left_behind()
    {
        _http.Serve(Checksums, $"{Sha256(Bytes(10))}  {Installer}\n");

        var result = await Runner().DownloadAndVerifyInstallerAsync("v0.8.10");

        Assert.Equal(UpgradeStagingOutcome.DownloadFailed, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    // ------------------------------------------------------------------ the run step's refusals

    [Fact]
    public async Task A_staged_file_that_changed_since_it_was_verified_is_not_run()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        var runner = Runner();
        var staged = (await runner.DownloadAndVerifyAsync("v0.8.10")).Asset!;

        await File.WriteAllBytesAsync(staged.FilePath, Bytes(20 * 1024, seed: 99));
        var run = await runner.RunAsync(staged);

        Assert.False(run.Succeeded);
        Assert.Null(run.Invocation);
        Assert.Null(run.ExitCode);
        Assert.Contains("changed on disk since it was verified", run.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Nothing was run", run.FailureReason, StringComparison.Ordinal);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task A_staged_file_that_is_gone_is_not_run()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        var runner = Runner();
        var staged = (await runner.DownloadAndVerifyAsync("v0.8.10")).Asset!;

        File.Delete(staged.FilePath);
        var run = await runner.RunAsync(staged);

        Assert.False(run.Succeeded);
        Assert.Null(run.Invocation);
        Assert.Contains("no longer at", run.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hashing_a_file_gives_lower_case_sha256()
    {
        var bytes = Bytes(5000);
        var path = _temp.File("blob.bin");
        await File.WriteAllBytesAsync(path, bytes);

        Assert.Equal(Sha256(bytes), await UpgradeRunner.ComputeFileSha256Async(path));
    }
}
