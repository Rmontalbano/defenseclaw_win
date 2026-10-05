using System.Text;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The verifier through the real <c>CliRunner</c>, with a batch file standing in for cosign (it answers <c>version</c>, records the arguments it
/// was given and exits with the code a test chose): the absolute path is what runs, the exit code decides the state, the files are on disk while it runs
/// and gone afterwards, and both calls are in Activity. The real cosign is never started and no signature is ever checked for real.
/// </summary>
public sealed class ChecksumsSignatureVerifierProcessTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();

    public ChecksumsSignatureVerifierProcessTests()
    {
        _services = TestServices.Create(_temp);
        _http.Serve(ChecksumsSignatureVerifier.BundleAssetName, "{\"synthetic\":\"bundle\"}");
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private string ToolDirectory => _temp.File("tools");

    private string FakeCosignPath => Path.Combine(ToolDirectory, "cosign.cmd");

    private string Scratch => _temp.File("scratch");

    private void InstallFakeCosign()
    {
        Directory.CreateDirectory(ToolDirectory);
        File.WriteAllText(
            FakeCosignPath,
            "@echo off\r\n" +
            "if \"%~1\"==\"version\" (\r\n" +
            "  echo GitVersion:    v2.6.3\r\n" +
            "  exit /b 0\r\n" +
            ")\r\n" +
            "> \"%~dp0called.txt\" echo %*\r\n" +
            "if exist \"%~dp0files-present.txt\" del \"%~dp0files-present.txt\"\r\n" +
            "if exist \"%~3\" if exist \"%~8\" (> \"%~dp0files-present.txt\" echo yes)\r\n" +
            "if exist \"%~dp0reject.flag\" (\r\n" +
            "  echo Error: none of the expected identities matched what was in the certificate 1>&2\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            "exit /b 0\r\n",
            new UTF8Encoding(false));
    }

    private Task<ChecksumsSignatureResult> VerifyAsync(string? cosignPath)
    {
        var verifier = ChecksumsSignatureVerifier.Create(_services.Cli, new HttpClient(_http), Scratch);
        return verifier.VerifyAsync("0.8.10", Encoding.UTF8.GetBytes("0123abcd  DefenseClawSetup-x64.exe\n"), cosignPath);
    }

    [Fact]
    public async Task A_cosign_that_exits_zero_is_a_verification_and_both_runs_are_in_activity_by_absolute_path()
    {
        InstallFakeCosign();

        var result = await VerifyAsync(FakeCosignPath);

        Assert.Equal(ChecksumsSignatureState.Verified, result.State);
        Assert.True(result.IsVerified);

        var runs = _services.Cli.Activity.OrderBy(i => i.StartedAt).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal(FakeCosignPath, run.Executable);
            Assert.Equal(0, run.ExitCode);
        });
        Assert.Equal(new[] { "version" }, runs[0].Argv);
        Assert.Equal("verify-blob", runs[1].Argv[0]);
        Assert.Contains("--certificate-identity", runs[1].Argv);
        Assert.Contains(ChecksumsSignatureVerifier.CertificateIdentity, runs[1].Argv);
        Assert.Contains(ChecksumsSignatureVerifier.OidcIssuer, runs[1].Argv);
    }

    [Fact]
    public async Task The_files_exist_while_cosign_runs_the_arguments_it_got_are_the_documented_ones_and_nothing_is_left_afterwards()
    {
        InstallFakeCosign();

        var result = await VerifyAsync(FakeCosignPath);

        Assert.True(result.IsVerified);
        var called = await File.ReadAllTextAsync(Path.Combine(ToolDirectory, "called.txt"));
        Assert.StartsWith("verify-blob --bundle ", called, StringComparison.Ordinal);
        Assert.Contains($"--certificate-identity {ChecksumsSignatureVerifier.CertificateIdentity}", called, StringComparison.Ordinal);
        Assert.Contains($"--certificate-oidc-issuer {ChecksumsSignatureVerifier.OidcIssuer}", called, StringComparison.Ordinal);
        Assert.Contains("checksums.txt.bundle", called, StringComparison.Ordinal);
        Assert.EndsWith("checksums.txt", called.Trim(), StringComparison.Ordinal);

        // The bundle (argument 3) and the checksums file (argument 8) were both on disk when cosign looked for them...
        Assert.True(File.Exists(Path.Combine(ToolDirectory, "files-present.txt")), "cosign did not find the bundle and checksums.txt where it was told they were");

        // ...and the scratch directory holds nothing once the verification is over.
        Assert.Empty(Directory.Exists(Scratch) ? Directory.GetFileSystemEntries(Scratch, "*", SearchOption.AllDirectories) : Array.Empty<string>());
    }

    [Fact]
    public async Task A_cosign_that_exits_non_zero_is_a_rejection_that_carries_what_it_printed()
    {
        InstallFakeCosign();
        File.WriteAllText(Path.Combine(ToolDirectory, "reject.flag"), "x");

        var result = await VerifyAsync(FakeCosignPath);

        Assert.Equal(ChecksumsSignatureState.DidNotVerify, result.State);
        Assert.True(result.BlocksUpgrade);
        Assert.Contains("none of the expected identities matched", result.CosignOutput, StringComparison.Ordinal);
        Assert.Contains(_services.Cli.Activity, i => i.Argv.Count > 0 && i.Argv[0] == "verify-blob" && i.ExitCode == 1);
    }

    [Fact]
    public async Task A_cosign_path_that_does_not_exist_cannot_run_and_is_not_a_rejection()
    {
        var result = await VerifyAsync(Path.Combine(ToolDirectory, "no-such-cosign.exe"));

        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.State);
        Assert.False(result.BlocksUpgrade);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public async Task An_argv_the_runner_refuses_because_it_holds_a_known_secret_is_a_cosign_that_could_not_run_not_an_exception()
    {
        InstallFakeCosign();
        // A value the runner knows is secret, and that the verify command line happens to contain.
        _services.Cli.RegisterSecret(new DefenseClaw.Core.Config.SecretValue("verify-blob"));

        var result = await VerifyAsync(FakeCosignPath);

        Assert.Equal(ChecksumsSignatureState.NotVerifiedCosignUnusable, result.State);
        Assert.False(result.BlocksUpgrade);
        Assert.False(result.IsVerified);
        Assert.Contains("could not be started", result.Detail, StringComparison.Ordinal);
        Assert.Contains("contains a secret", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-blob", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_cosign_at_all_runs_nothing()
    {
        var result = await VerifyAsync(cosignPath: null);

        Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, result.State);
        Assert.Empty(_services.Cli.Activity);
    }
}
