using System.Net;
using System.Text;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The signature on a release's checksums.txt, as far as the app actually checked it. Cosign is a fake that starts no process; what these tests pin is
/// that "verified" is said only for cosign's exit code 0 on the exact bytes in hand, that every other state says what it is (published but not verified,
/// not published, rejected), and that only a rejection blocks.
/// </summary>
public sealed class ChecksumsSignatureVerifierTests : IDisposable
{
    private const string Version = "0.8.10";
    private const string UpstreamIdentity = "https://github.com/cisco-ai-defense/defenseclaw/.github/workflows/release.yaml@refs/heads/main";

    private static readonly byte[] Checksums = Encoding.UTF8.GetBytes("0123abcd  DefenseClawSetup-x64.exe\n");
    private static readonly byte[] Bundle = Encoding.UTF8.GetBytes("{\"synthetic\":\"bundle\"}");
    private static readonly byte[] Signature = Encoding.UTF8.GetBytes("c3ludGhldGljLXNpZw==");
    private static readonly byte[] Certificate = Encoding.UTF8.GetBytes("c3ludGhldGljLWNlcnQ=");

    private readonly TempDirectory _temp = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeCosign _cosign = new();

    public void Dispose() => _temp.Dispose();

    private string Scratch => _temp.File("scratch");

    private ChecksumsSignatureVerifier Verifier() => _cosign.VerifierOver(_http, Scratch);

    private Task<ChecksumsSignatureResult> VerifyAsync(string? cosignPath = FakeCosign.Path, byte[]? checksums = null, CancellationToken token = default) =>
        Verifier().VerifyAsync(Version, checksums ?? Checksums, cosignPath, token);

    private void PublishBundle() => _http.Serve(ChecksumsSignatureVerifier.BundleAssetName, Bundle);

    private void PublishPair() =>
        _http.Serve(ChecksumsSignatureVerifier.SignatureAssetName, Signature).Serve(ChecksumsSignatureVerifier.CertificateAssetName, Certificate);

    private string[] ScratchLeftovers() =>
        Directory.Exists(Scratch) ? Directory.GetFileSystemEntries(Scratch, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    // ------------------------------------------------------------------ what upstream signs, and how

    [Fact]
    public void The_identity_is_the_upstream_release_workflow_on_main_and_the_issuer_is_github_actions()
    {
        Assert.Equal(UpstreamIdentity, ChecksumsSignatureVerifier.CertificateIdentity);
        Assert.Equal("https://token.actions.githubusercontent.com", ChecksumsSignatureVerifier.OidcIssuer);
    }

    [Fact]
    public void A_bundle_is_verified_with_the_exact_identity_and_issuer_and_the_checksums_file_last()
    {
        var argv = ChecksumsSignatureVerifier.BuildVerifyArgv(@"D:\s\checksums.txt", @"D:\s\checksums.txt.bundle", null, null);

        Assert.Equal(
            new[]
            {
                "verify-blob", "--bundle", @"D:\s\checksums.txt.bundle",
                "--certificate-identity", UpstreamIdentity,
                "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
                @"D:\s\checksums.txt",
            },
            argv);
    }

    [Fact]
    public void A_signature_with_its_certificate_is_verified_when_there_is_no_bundle()
    {
        var argv = ChecksumsSignatureVerifier.BuildVerifyArgv(@"D:\s\checksums.txt", null, @"D:\s\checksums.txt.sig", @"D:\s\checksums.txt.pem");

        Assert.Equal(
            new[]
            {
                "verify-blob", "--certificate", @"D:\s\checksums.txt.pem", "--signature", @"D:\s\checksums.txt.sig",
                "--certificate-identity", UpstreamIdentity,
                "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
                @"D:\s\checksums.txt",
            },
            argv);
    }

    [Fact]
    public void A_verification_needs_a_bundle_or_a_signature_with_its_certificate()
    {
        Assert.Throws<ArgumentException>(() => ChecksumsSignatureVerifier.BuildVerifyArgv(@"D:\s\checksums.txt", null, @"D:\s\checksums.txt.sig", null));
        Assert.Throws<ArgumentException>(() => ChecksumsSignatureVerifier.BuildVerifyArgv(@"D:\s\checksums.txt", null, null, null));
    }

    // ------------------------------------------------------------------ verified: exit 0 and nothing else

    [Fact]
    public async Task Cosign_exiting_zero_on_a_published_bundle_is_the_only_way_to_verified()
    {
        PublishBundle();

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.Verified, result.State);
        Assert.True(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Equal("checksums.txt signature verified (cosign)", result.Label);
        Assert.Equal("Ok", result.BadgeKey);
        Assert.Contains(UpstreamIdentity, result.Detail, StringComparison.Ordinal);
        Assert.Contains("verify-blob --bundle", result.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cosign_is_run_by_its_absolute_path_after_asking_its_version_with_the_bundle_the_identity_and_the_very_bytes_it_was_given()
    {
        PublishBundle();
        string? checksumsAtCall = null;
        string? bundleAtCall = null;
        string[]? verifyArgv = null;
        _cosign.OnVerify = argv =>
        {
            verifyArgv = argv;
            checksumsAtCall = File.ReadAllText(argv[^1]);
            bundleAtCall = File.ReadAllText(argv[2]);
        };

        _ = await VerifyAsync();

        Assert.Equal(2, _cosign.Calls.Count);
        Assert.All(_cosign.Calls, c => Assert.Equal(FakeCosign.Path, c.Path));
        Assert.Equal(new[] { "version" }, _cosign.Calls[0].Argv);
        Assert.Equal("verify-blob", _cosign.Calls[1].Argv[0]);
        Assert.Equal("--bundle", verifyArgv![1]);
        Assert.Equal(UpstreamIdentity, verifyArgv[4]);
        Assert.Equal(Encoding.UTF8.GetString(Checksums), checksumsAtCall);
        Assert.Equal(Encoding.UTF8.GetString(Bundle), bundleAtCall);
        Assert.Equal("checksums.txt", Path.GetFileName(verifyArgv[^1]));
        Assert.Equal(ChecksumsSignatureVerifier.VerifyTimeout, _cosign.Calls[1].Timeout);
    }

    [Fact]
    public async Task The_certificate_and_signature_files_are_used_when_the_release_has_no_bundle()
    {
        PublishPair();
        string[]? verifyArgv = null;
        string? signatureAtCall = null;
        string? certificateAtCall = null;
        _cosign.OnVerify = argv =>
        {
            verifyArgv = argv;
            certificateAtCall = File.ReadAllText(argv[2]);
            signatureAtCall = File.ReadAllText(argv[4]);
        };

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.Verified, result.State);
        Assert.Equal("--certificate", verifyArgv![1]);
        Assert.Equal("--signature", verifyArgv[3]);
        Assert.EndsWith("checksums.txt.pem", verifyArgv[2], StringComparison.Ordinal);
        Assert.EndsWith("checksums.txt.sig", verifyArgv[4], StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(Certificate), certificateAtCall);
        Assert.Equal(Encoding.UTF8.GetString(Signature), signatureAtCall);
    }

    [Fact]
    public async Task The_bundle_is_preferred_and_the_pair_is_not_even_asked_for_when_it_is_there()
    {
        PublishBundle();
        PublishPair();

        _ = await VerifyAsync();

        Assert.Equal(new[] { ChecksumsSignatureVerifier.BundleAssetName }, _http.Requested);
    }

    [Fact]
    public async Task The_scratch_files_are_removed_after_a_verification_a_rejection_and_a_failure()
    {
        PublishBundle();

        _ = await VerifyAsync();
        Assert.Empty(ScratchLeftovers());

        _cosign.VerifyExitCode = 1;
        _ = await VerifyAsync();
        Assert.Empty(ScratchLeftovers());

        _cosign.VerifyFailure = "timed out";
        _ = await VerifyAsync();
        Assert.Empty(ScratchLeftovers());
    }

    [Fact]
    public async Task Scratch_directories_an_earlier_run_left_behind_are_removed_and_a_recent_one_or_another_folder_is_not()
    {
        PublishBundle();
        var stale = Directory.CreateDirectory(Path.Combine(Scratch, ChecksumsSignatureVerifier.ScratchPrefix + "stale0001")).FullName;
        File.WriteAllText(Path.Combine(stale, "checksums.txt"), "left behind");
        Directory.SetCreationTimeUtc(stale, DateTime.UtcNow - ChecksumsSignatureVerifier.StaleScratchAge - TimeSpan.FromMinutes(5));
        var recent = Directory.CreateDirectory(Path.Combine(Scratch, ChecksumsSignatureVerifier.ScratchPrefix + "recent001")).FullName;
        var versionFolder = Directory.CreateDirectory(Path.Combine(Scratch, "0.8.9")).FullName;
        Directory.SetCreationTimeUtc(versionFolder, DateTime.UtcNow - TimeSpan.FromDays(30));

        _ = await VerifyAsync();

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(versionFolder));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(-1)]
    public async Task Any_exit_code_but_zero_is_not_verified(int exitCode)
    {
        PublishBundle();
        _cosign.VerifyExitCode = exitCode;

        var result = await VerifyAsync();

        Assert.False(result.IsVerified);
        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.State);
    }

    // ------------------------------------------------------------------ did not verify: blocks

    [Fact]
    public async Task A_signature_cosign_rejects_is_reported_as_not_verifying_with_what_cosign_said_and_it_blocks_the_upgrade()
    {
        PublishBundle();
        _cosign.VerifyExitCode = 1;
        _cosign.VerifyOutput = new[] { "Error: none of the expected identities matched what was in the certificate", "main.go:62: error during command execution" };

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.State);
        Assert.True(result.BlocksUpgrade);
        Assert.False(result.IsVerified);
        Assert.Equal("signature did not verify", result.Label);
        Assert.Equal("Bad", result.BadgeKey);
        Assert.Contains("Do not run this release's installer", result.Detail, StringComparison.Ordinal);
        Assert.Contains("none of the expected identities matched", result.CosignOutput, StringComparison.Ordinal);
        Assert.Contains("none of the expected identities matched", result.Detail, StringComparison.Ordinal);
        Assert.Contains("exit code 1", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cosign_that_started_and_did_not_finish_blocks_too_because_a_check_begun_must_not_become_no_check()
    {
        PublishBundle();
        _cosign.VerifyFailure = "timed out after 120 s — process tree killed";
        _cosign.VerifyOutput = new[] { "Error: error getting trusted root: dial tcp: lookup tuf-repo-cdn.sigstore.dev: no such host" };

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.State);
        Assert.True(result.BlocksUpgrade);
        Assert.False(result.IsVerified);
        Assert.Equal("signature did not verify", result.Label);
        Assert.Contains("did not finish", result.Detail, StringComparison.Ordinal);
        Assert.Contains("timed out after 120 s", result.Detail, StringComparison.Ordinal);
        Assert.Contains("the upgrade is stopped", result.Detail, StringComparison.Ordinal);
        Assert.Contains("check the network", result.Detail, StringComparison.Ordinal);
        Assert.Contains("no such host", result.CosignOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_with_no_output_still_says_so()
    {
        PublishBundle();
        _cosign.VerifyExitCode = 1;

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.State);
        Assert.Null(result.CosignOutput);
        Assert.DoesNotContain("cosign said", result.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ published, not verified: does not block

    [Fact]
    public async Task Without_cosign_a_published_signature_is_present_not_verified_and_cosign_is_never_run()
    {
        PublishBundle();

        var result = await VerifyAsync(cosignPath: null);

        Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.True(result.IsPublishedButNotVerified);
        Assert.Equal("signature present, not verified (cosign not installed)", result.Label);
        Assert.Equal("Warn", result.BadgeKey);
        Assert.Contains("winget install Sigstore.Cosign", result.Detail, StringComparison.Ordinal);
        Assert.Contains("not that the file is the one the project signed", result.Detail, StringComparison.Ordinal);
        Assert.Empty(_cosign.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_cosign_path_is_no_cosign(string? path)
    {
        PublishPair();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, (await VerifyAsync(path)).State);
        Assert.Empty(_cosign.Calls);
    }

    [Theory]
    [InlineData("GitVersion:    v1.13.1")]
    [InlineData("GitVersion:    1.9.0")]
    [InlineData("GitVersion:    v0.7.2-rc1")]
    public async Task A_cosign_older_than_two_is_not_used_and_says_why(string versionLine)
    {
        PublishBundle();
        _cosign.VersionLine = versionLine;

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.State);
        Assert.False(result.BlocksUpgrade);
        Assert.Equal("signature present, not verified (cosign could not run)", result.Label);
        Assert.Contains("older than 2.0", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _cosign.VerifyCalls);
    }

    [Theory]
    [InlineData("GitVersion:    v2.0.0")]
    [InlineData("GitVersion:    v2.6.3")]
    [InlineData("GitVersion:    v3.0.1")]
    [InlineData("GitVersion:    2.4.1-dev+abc")]
    public async Task Cosign_two_or_newer_is_used(string versionLine)
    {
        PublishBundle();
        _cosign.VersionLine = versionLine;

        Assert.Equal(ChecksumsSignatureState.Verified, (await VerifyAsync()).State);
        Assert.Equal(1, _cosign.VerifyCalls);
    }

    [Fact]
    public async Task Cosign_three_reads_the_bundle_the_same_way()
    {
        PublishBundle();
        _cosign.VersionLine = "GitVersion:    v3.1.2";
        string[]? verifyArgv = null;
        _cosign.OnVerify = argv => verifyArgv = argv;

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.Verified, result.State);
        Assert.Equal("--bundle", verifyArgv![1]);
        Assert.DoesNotContain("--signature", verifyArgv);
        Assert.DoesNotContain("--certificate", verifyArgv);
    }

    [Fact]
    public async Task Cosign_three_cannot_check_a_detached_signature_so_a_release_with_only_that_is_not_verified_and_says_why_without_blocking()
    {
        // cosign 3 removed --certificate and --signature from verify-blob: asking would fail with "unknown flag", which is not a rejected signature.
        PublishPair();
        _cosign.VersionLine = "GitVersion:    v3.1.2";

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Contains("cosign 3.x accepts only a bundle", result.Detail, StringComparison.Ordinal);
        Assert.Contains("no checksums.txt.bundle", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _cosign.VerifyCalls);
    }

    [Fact]
    public async Task Cosign_two_still_checks_a_detached_signature_and_certificate()
    {
        PublishPair();
        _cosign.VersionLine = "GitVersion:    v2.6.3";

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.Verified, result.State);
        Assert.Equal(1, _cosign.VerifyCalls);
    }

    [Fact]
    public async Task A_version_that_cannot_be_read_a_version_command_that_fails_and_one_that_does_not_start_are_all_cosign_that_cannot_run()
    {
        PublishBundle();

        _cosign.VersionLine = "something else entirely";
        var unreadable = await VerifyAsync();

        _cosign.VersionLine = "GitVersion:    v2.6.3";
        _cosign.VersionExitCode = 3;
        var failing = await VerifyAsync();

        _cosign.VersionExitCode = 0;
        _cosign.VersionFailure = "The system cannot find the file specified";
        var notStarted = await VerifyAsync();

        Assert.All(new[] { unreadable, failing, notStarted }, r =>
        {
            Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, r.State);
            Assert.False(r.BlocksUpgrade);
        });
        Assert.Contains("could not be read", unreadable.Detail, StringComparison.Ordinal);
        Assert.Contains("exited with code 3", failing.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot find the file", notStarted.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _cosign.VerifyCalls);
    }

    [Fact]
    public async Task A_cosign_that_could_not_be_started_is_no_cosign_and_does_not_block()
    {
        PublishBundle();
        _cosign.VerifyFailure = "The system cannot find the file specified";
        _cosign.VerifyStarted = false;

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Contains("could not be started", result.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot find the file", result.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ not published, or not fetchable

    [Fact]
    public async Task A_release_with_no_signature_files_has_no_signature_to_verify_and_cosign_is_not_run()
    {
        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotPublished, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Equal("no signature published for checksums.txt", result.Label);
        Assert.Equal(
            new[] { ChecksumsSignatureVerifier.BundleAssetName, ChecksumsSignatureVerifier.SignatureAssetName, ChecksumsSignatureVerifier.CertificateAssetName },
            _http.Requested);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_signature_without_its_certificate_is_not_a_published_signature()
    {
        _http.Serve(ChecksumsSignatureVerifier.SignatureAssetName, Signature);

        Assert.Equal(ChecksumsSignatureState.NotPublished, (await VerifyAsync()).State);

        var other = new StubHttpHandler().Serve(ChecksumsSignatureVerifier.CertificateAssetName, Certificate);
        var result = await _cosign.VerifierOver(other, Scratch).VerifyAsync(Version, Checksums, FakeCosign.Path);
        Assert.Equal(ChecksumsSignatureState.NotPublished, result.State);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task An_empty_signature_file_is_no_signature()
    {
        _http.Serve(ChecksumsSignatureVerifier.BundleAssetName, Array.Empty<byte>());

        Assert.Equal(ChecksumsSignatureState.NotPublished, (await VerifyAsync()).State);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Signature_files_that_cannot_be_fetched_are_not_a_published_nor_an_absent_signature(HttpStatusCode status)
    {
        _http.ServeStatus(ChecksumsSignatureVerifier.BundleAssetName, status);

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedUnavailable, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Contains("could not be fetched", result.Detail, StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)status}", result.Detail, StringComparison.Ordinal);
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_network_error_on_the_certificate_is_unavailable_too_even_without_cosign()
    {
        _http.Serve(ChecksumsSignatureVerifier.SignatureAssetName, Signature).Throw(ChecksumsSignatureVerifier.CertificateAssetName, new HttpRequestException("no route to host"));

        var result = await VerifyAsync(cosignPath: null);

        Assert.Equal(ChecksumsSignatureState.NotVerifiedUnavailable, result.State);
        Assert.Contains("no route to host", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signature_file_larger_than_the_cap_is_refused_unread()
    {
        _http.Serve(ChecksumsSignatureVerifier.BundleAssetName, new byte[ChecksumsSignatureVerifier.MaxSidecarBytes + 1]);

        var result = await VerifyAsync();

        Assert.Equal(ChecksumsSignatureState.NotVerifiedUnavailable, result.State);
        Assert.Contains("larger than", result.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the caller's token

    [Fact]
    public async Task A_cancelled_check_is_a_cancellation_not_a_state()
    {
        PublishBundle();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifyAsync(token: cts.Token));
        Assert.Empty(_cosign.Calls);
    }

    [Fact]
    public async Task A_cancellation_that_arrives_while_cosign_runs_is_a_cancellation_too()
    {
        PublishBundle();
        using var cts = new CancellationTokenSource();
        _cosign.OnVerify = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifyAsync(token: cts.Token));
    }

    // ------------------------------------------------------------------ labels

    [Fact]
    public void Verified_is_a_word_only_the_verified_state_uses()
    {
        foreach (var state in Enum.GetValues<ChecksumsSignatureState>())
        {
            var result = new ChecksumsSignatureResult { State = state };
            var says = result.Label.Contains("verified (cosign)", StringComparison.Ordinal);
            Assert.Equal(state == ChecksumsSignatureState.Verified, says);
            Assert.Equal(state == ChecksumsSignatureState.Verified, result.IsVerified);
        }
    }

    [Fact]
    public void Only_a_rejection_blocks_and_every_not_verified_state_says_so_in_its_label()
    {
        var blockers = Enum.GetValues<ChecksumsSignatureState>().Where(s => new ChecksumsSignatureResult { State = s }.BlocksUpgrade).ToArray();
        Assert.Equal(new[] { ChecksumsSignatureState.DidNotVerify }, blockers);

        foreach (var state in new[]
                 {
                     ChecksumsSignatureState.NotVerifiedNoCosign,
                     ChecksumsSignatureState.NotVerifiedCosignUnusable,
                     ChecksumsSignatureState.NotVerifiedUnavailable,
                 })
        {
            Assert.Contains("not verified", new ChecksumsSignatureResult { State = state }.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_checked_yet_makes_no_claim()
    {
        var result = ChecksumsSignatureResult.NotChecked;

        Assert.Equal(ChecksumsSignatureState.NotChecked, result.State);
        Assert.False(result.IsVerified);
        Assert.False(result.BlocksUpgrade);
        Assert.Equal("Neutral", result.BadgeKey);
        Assert.Equal("signature not checked yet", result.Label);
    }
}
