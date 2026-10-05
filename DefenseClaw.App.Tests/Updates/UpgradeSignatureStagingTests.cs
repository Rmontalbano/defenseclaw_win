using System.Security.Cryptography;
using System.Text;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The signature on checksums.txt in the staging flow, on both channels: the bytes cosign is asked about are the bytes the hash is compared
/// against, a signature cosign rejects stops everything before anything is staged (and, on the installer channel, before the ~270 MB are downloaded),
/// and every other state is recorded on the staged asset as exactly what it is.
/// </summary>
public sealed class UpgradeSignatureStagingTests : IDisposable
{
    private const string Script = UpgradeRunner.ScriptAssetName;
    private const string Installer = UpgradeRunner.InstallerAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;
    private const string Bundle = ChecksumsSignatureVerifier.BundleAssetName;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();
    private readonly FakeCosign _cosign = new();

    public UpgradeSignatureStagingTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private string StagingRoot => _temp.File("staging");

    private UpgradeRunner Runner() =>
        new(_services.Cli, new HttpClient(_http), StagingRoot, _cosign.VerifierOver(_http, StagingRoot));

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

    private (byte[] Script, string ChecksumsText) ServeScript(bool bundle = true)
    {
        var script = Bytes(20 * 1024);
        var checksums = $"{Sha256(script)}  {Script}\n";
        _http.Serve(Script, script).Serve(Checksums, checksums);
        if (bundle)
        {
            _http.Serve(Bundle, "{\"synthetic\":\"bundle\"}");
        }

        return (script, checksums);
    }

    // ------------------------------------------------------------------ the resolver script

    [Fact]
    public async Task A_script_staged_after_cosign_verified_the_signature_says_so_and_the_bytes_verified_are_the_bytes_hashed()
    {
        var (script, checksums) = ServeScript();
        string? checksumsCosignSaw = null;
        _cosign.OnVerify = argv => checksumsCosignSaw = File.ReadAllText(argv[^1]);

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.True(result.Succeeded);
        var asset = result.Asset!;
        Assert.True(asset.ChecksumsSignature.IsVerified);
        Assert.Equal(ChecksumsSignatureState.Verified, result.Signature!.State);
        Assert.Contains("cosign verified that file's sigstore signature", result.Summary, StringComparison.Ordinal);
        Assert.Equal(Sha256(script), asset.Sha256);

        // The file cosign was handed is the checksums.txt the comparison used: not a second download that could differ.
        Assert.Equal(checksums, checksumsCosignSaw);
        Assert.Equal(1, _http.Requested.Count(r => r == Checksums));
        Assert.Single(StagedFiles());
    }

    [Fact]
    public async Task A_script_staged_without_cosign_says_the_signature_was_not_verified_never_that_it_was()
    {
        ServeScript();

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", cosignPath: null);

        Assert.True(result.Succeeded);
        Assert.False(result.Asset!.ChecksumsSignature.IsVerified);
        Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, result.Asset.ChecksumsSignature.State);
        Assert.Contains("its sigstore signature was not verified (cosign not installed)", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cosign verified", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_script_whose_release_publishes_no_signature_says_that()
    {
        ServeScript(bundle: false);

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.True(result.Succeeded);
        Assert.Equal(ChecksumsSignatureState.NotPublished, result.Asset!.ChecksumsSignature.State);
        Assert.Contains("the release publishes no signature for that file", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_signature_cosign_rejects_stops_the_script_before_it_is_staged()
    {
        ServeScript();
        _cosign.VerifyExitCode = 1;
        _cosign.VerifyOutput = new[] { "Error: no matching signatures" };

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.SignatureRejected, result.Outcome);
        Assert.Null(result.Asset);
        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.Signature!.State);
        Assert.StartsWith("Aborted: cosign could not verify the sigstore signature on release 0.8.10's checksums.txt", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Do not run this release's installer", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("no matching signatures", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("nothing was written to disk", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_rejected_signature_is_reported_even_when_the_hash_also_does_not_match()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, Bytes(20 * 1024, seed: 9)).Serve(Checksums, $"{Sha256(script)}  {Script}\n").Serve(Bundle, "{}");
        _cosign.VerifyExitCode = 1;

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.Equal(UpgradeStagingOutcome.SignatureRejected, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_hash_mismatch_still_carries_what_was_found_out_about_the_signature()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, Bytes(20 * 1024, seed: 9)).Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", cosignPath: null);

        Assert.Equal(UpgradeStagingOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(ChecksumsSignatureState.NotPublished, result.Signature!.State);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_cosign_that_cannot_run_does_not_stop_the_upgrade_and_is_recorded_as_exactly_that()
    {
        ServeScript();
        _cosign.VersionLine = "GitVersion:    v1.13.1";

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.True(result.Succeeded);
        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.Asset!.ChecksumsSignature.State);
        Assert.Contains("its sigstore signature was not verified (cosign could not run)", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_checksums_entry_ends_the_staging_before_any_signature_is_asked_about()
    {
        _http.Serve(Script, Bytes(20 * 1024)).Serve(Checksums, $"{Sha256(Bytes(10))}  something-else.exe\n").Serve(Bundle, "{}");

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.DoesNotContain(Bundle, _http.Requested);
        Assert.Empty(_cosign.Calls);
    }

    // ------------------------------------------------------------------ the Setup installer

    [Fact]
    public async Task A_rejected_signature_stops_the_installer_before_the_big_download_and_leaves_nothing()
    {
        _http.Serve(Checksums, $"{Sha256(Bytes(1024))}  {Installer}\n").Serve(Bundle, "{}").Serve(Installer, Bytes(1024));
        _cosign.VerifyExitCode = 1;
        _cosign.VerifyOutput = new[] { "Error: certificate identity mismatch" };

        var result = await Runner().DownloadAndVerifyInstallerAsync("0.8.10", FakeCosign.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.SignatureRejected, result.Outcome);
        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.Signature!.State);
        Assert.Contains($"{Installer} was not downloaded", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("certificate identity mismatch", result.ErrorMessage, StringComparison.Ordinal);

        // checksums.txt, then the signature, and never the installer.
        Assert.Equal(new[] { Checksums, Bundle }, _http.Requested);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task An_installer_whose_checksums_were_verified_is_staged_with_that_recorded_and_the_signature_is_checked_before_the_download()
    {
        var installer = Bytes((int)UpgradeRunner.MinimumPlausibleInstallerBytes + 1024);
        _http.Serve(Checksums, $"{Sha256(installer)}  {Installer}\n").Serve(Bundle, "{}").Serve(Installer, installer);

        var result = await Runner().DownloadAndVerifyInstallerAsync("0.8.10", FakeCosign.Path);

        Assert.True(result.Succeeded);
        Assert.True(result.Asset!.ChecksumsSignature.IsVerified);
        Assert.Contains("cosign verified that file's sigstore signature", result.Summary, StringComparison.Ordinal);
        Assert.Equal(new[] { Checksums, Bundle, Installer }, _http.Requested);
        Assert.Equal(Sha256(installer), result.Asset.Sha256);
    }

    [Fact]
    public async Task An_installer_staged_without_cosign_is_staged_with_the_signature_marked_not_verified()
    {
        var installer = Bytes((int)UpgradeRunner.MinimumPlausibleInstallerBytes + 1024);
        _http.Serve(Checksums, $"{Sha256(installer)}  {Installer}\n").Serve(Bundle, "{}").Serve(Installer, installer);

        var result = await Runner().DownloadAndVerifyInstallerAsync("0.8.10", cosignPath: null);

        Assert.True(result.Succeeded);
        Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, result.Asset!.ChecksumsSignature.State);
        Assert.Contains("not verified (cosign not installed)", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_missing_installer_entry_in_checksums_ends_the_staging_with_no_signature_check_and_no_download()
    {
        _http.Serve(Checksums, $"{Sha256(Bytes(10))}  {Script}\n").Serve(Bundle, "{}").Serve(Installer, Bytes(1024));

        var result = await Runner().DownloadAndVerifyInstallerAsync("0.8.10", FakeCosign.Path);

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Equal(new[] { Checksums }, _http.Requested);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task The_staging_messages_never_call_checksums_signed()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, Bytes(133)).Serve(Checksums, $"{Sha256(Bytes(133))}  {Script}\n");

        var stub = await Runner().DownloadAndVerifyAsync("0.8.10");

        Assert.Equal(UpgradeStagingOutcome.StubDetected, stub.Outcome);
        Assert.DoesNotContain("signed", stub.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var noEntry = new StubHttpHandler().Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  other.exe\n");
        var runner = new UpgradeRunner(_services.Cli, new HttpClient(noEntry), StagingRoot, _cosign.VerifierOver(noEntry, StagingRoot));
        var result = await runner.DownloadAndVerifyAsync("0.8.10");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.DoesNotContain("anything signed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("cannot be compared with anything", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cosign_is_handed_checksums_txt_exactly_as_served_with_its_CRLF_line_endings_and_non_ASCII_text()
    {
        // checksums.txt with CRLF line ends and a multi-byte comment: cosign is handed the bytes as served, not a decoded and re-encoded copy.
        var script = Bytes(20 * 1024);
        var served = Encoding.UTF8.GetBytes($"# synthetic é✓\r\n{Sha256(script)}  {Script}\r\n");
        _http.Serve(Script, script).Serve(Checksums, served).Serve(Bundle, "{}");
        byte[]? seen = null;
        _cosign.OnVerify = argv => seen = File.ReadAllBytes(argv[^1]);

        var result = await Runner().DownloadAndVerifyAsync("0.8.10", FakeCosign.Path);

        Assert.True(result.Succeeded);
        Assert.Equal(served, seen);
    }
}
