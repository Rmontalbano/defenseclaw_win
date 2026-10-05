using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// What the Updates window says about the signature on checksums.txt, in the upgrade card (the step text, the verification detail, the plan the
/// confirm overlay shows) and in the trust panel: only what was actually checked, with "verified" said only when cosign verified it, and a rejection
/// leaving nothing staged and no way to run.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UpgradeSignatureViewModelTests : IDisposable
{
    private const string Script = UpgradeRunner.ScriptAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;
    private const string Bundle = ChecksumsSignatureVerifier.BundleAssetName;

    private static readonly CosignStatus Ready = new()
    {
        Availability = CosignAvailability.OnPath,
        Path = @"C:\tools\cosign.exe",
        Detail = "cosign is on PATH.",
    };

    private static readonly CosignStatus OffPath = new()
    {
        Availability = CosignAvailability.FoundOffPath,
        Path = @"C:\Users\operator\AppData\Local\Microsoft\WinGet\Links\cosign.exe",
        Detail = "cosign exists off PATH.",
        InstallGuidance = "Restart.",
    };

    private static readonly CosignStatus Missing = new()
    {
        Availability = CosignAvailability.Missing,
        Detail = "cosign was not found. The upgrade script verifies the signed release contract with it.",
        InstallGuidance = "Install it with:  winget install Sigstore.Cosign",
    };

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();
    private readonly FakeCosign _cosign = new();

    public UpgradeSignatureViewModelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private UpgradeSectionViewModel Section(Func<Task<CosignStatus>> probe, UpgradeChannel channel = UpgradeChannel.ResolverScript, bool serveScript = true, bool bundle = true)
    {
        if (serveScript)
        {
            var script = new byte[20 * 1024];
            new Random(5).NextBytes(script);
            _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        }

        if (bundle)
        {
            _http.Serve(Bundle, "{\"synthetic\":\"bundle\"}");
        }

        var staging = _temp.File("staging");
        var runner = new UpgradeRunner(_services.Cli, new HttpClient(_http), staging, _cosign.VerifierOver(_http, staging));
        var section = UiThread.Run(() =>
        {
            var built = new UpgradeSectionViewModel(_services, runner, probe)
            {
                SelectedChannel = channel,
            };
            built.ApplyCheck(new UpdateCheckResult { State = UpdateCheckState.UpdateAvailable, LatestVersion = "0.8.10", InstalledVersion = "0.8.9" });
            return built;
        });
        UiThread.WaitFor(() => section.CosignLabel != "Checking…", "the cosign probe to answer");
        return section;
    }

    private static async Task StageAsync(UpgradeSectionViewModel section)
    {
        await UiThread.Run(() => section.DownloadAndVerifyCommand.ExecuteAsync(null));
        UiThread.Run(() => UiThread.Settle());
    }

    // ------------------------------------------------------------------ the plan and the step texts, before anything is staged

    [Theory]
    [InlineData(UpgradeChannel.SetupInstaller)]
    [InlineData(UpgradeChannel.ResolverScript)]
    public void The_plan_and_the_step_notes_say_what_is_compared_and_that_the_signature_needs_cosign_and_never_claim_a_signed_file(UpgradeChannel channel)
    {
        var section = Section(() => Task.FromResult(Ready), channel);
        try
        {
            UiThread.Run(() =>
            {
                Assert.Contains("compared with its entry in checksums.txt from the same release", section.ResolverPlan, StringComparison.Ordinal);
                Assert.Contains("When cosign is installed", section.ResolverPlan, StringComparison.Ordinal);
                Assert.Contains("without cosign the signature is not verified", section.ResolverPlan, StringComparison.Ordinal);
                Assert.DoesNotContain("sigstore-signed", section.ResolverPlan, StringComparison.Ordinal);
                Assert.DoesNotContain("signed checksums", section.ResolverPlan, StringComparison.Ordinal);

                Assert.Contains("verifies", section.StagingStepNote, StringComparison.Ordinal);
                Assert.Contains("sigstore signature", section.StagingStepNote, StringComparison.Ordinal);
                Assert.DoesNotContain("does not use it", section.RunStepNote, StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public void The_installer_plan_says_the_exe_is_not_authenticode_signed()
    {
        var section = Section(() => Task.FromResult(Ready), UpgradeChannel.SetupInstaller);
        try
        {
            Assert.Contains("not Authenticode-signed", UiThread.Run(() => section.ResolverPlan), StringComparison.Ordinal);
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    // ------------------------------------------------------------------ cosign, optional on the installer channel and required on the resolver

    [Fact]
    public void On_the_installer_channel_a_missing_cosign_is_a_warning_that_says_what_it_costs_and_how_to_fix_it()
    {
        var section = Section(() => Task.FromResult(Missing), UpgradeChannel.SetupInstaller);
        try
        {
            UiThread.Run(() =>
            {
                Assert.Equal("cosign missing", section.CosignLabel);
                Assert.Equal("Warn", section.CosignBadgeKey);
                Assert.Contains("optional", section.CosignDetail, StringComparison.Ordinal);
                Assert.Contains("NOT verified", section.CosignDetail, StringComparison.Ordinal);
                Assert.Contains("winget install Sigstore.Cosign", section.CosignDetail, StringComparison.Ordinal);
                Assert.False(section.ShowCosignGuidance);
                Assert.False(section.IsCosignReady);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public void On_the_resolver_channel_a_missing_cosign_is_still_a_failed_gate_in_the_original_words()
    {
        var section = Section(() => Task.FromResult(Missing), UpgradeChannel.ResolverScript);
        try
        {
            UiThread.Run(() =>
            {
                Assert.Equal("cosign missing", section.CosignLabel);
                Assert.Equal("Bad", section.CosignBadgeKey);
                Assert.Equal(Missing.Detail, section.CosignDetail);
                Assert.True(section.ShowCosignGuidance);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public void Switching_channel_rewrites_the_cosign_words_for_the_channel_it_lands_on()
    {
        var section = Section(() => Task.FromResult(Missing), UpgradeChannel.ResolverScript);
        try
        {
            UiThread.Run(() => section.SelectedChannel = UpgradeChannel.SetupInstaller);
            UiThread.Run(() =>
            {
                Assert.Equal("Warn", section.CosignBadgeKey);
                Assert.Contains("optional", section.CosignDetail, StringComparison.Ordinal);
            });

            UiThread.Run(() => section.SelectedChannel = UpgradeChannel.ResolverScript);
            UiThread.Run(() =>
            {
                Assert.Equal("Bad", section.CosignBadgeKey);
                Assert.Equal(Missing.Detail, section.CosignDetail);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public void A_cosign_found_off_PATH_is_ready_on_the_installer_channel_because_it_is_run_by_its_absolute_path()
    {
        var section = Section(() => Task.FromResult(OffPath), UpgradeChannel.SetupInstaller);
        try
        {
            UiThread.Run(() =>
            {
                Assert.Equal("cosign ready", section.CosignLabel);
                Assert.Equal("Ok", section.CosignBadgeKey);
                Assert.Contains(OffPath.Path!, section.CosignDetail, StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    // ------------------------------------------------------------------ after a staging

    [Fact]
    public async Task A_verified_signature_is_said_in_the_detail_the_plan_and_the_trust_event()
    {
        var section = Section(() => Task.FromResult(Ready));
        var raised = new List<ChecksumsSignatureResult>();
        UiThread.Run(() => section.SignatureChecked += (_, result) => raised.Add(result));
        try
        {
            await StageAsync(section);

            UiThread.Run(() =>
            {
                Assert.True(section.IsStaged);
                Assert.Contains("cosign verified that file's sigstore signature", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.Contains("cosign verified that file's sigstore signature", section.StagingSummary, StringComparison.Ordinal);
                Assert.Contains("cosign verified the sigstore signature on that checksums.txt", section.ResolverPlan, StringComparison.Ordinal);
                Assert.DoesNotContain("NOT verified", section.ResolverPlan, StringComparison.Ordinal);
            });
            Assert.Equal(ChecksumsSignatureState.Verified, Assert.Single(raised).State);
            Assert.Equal(Ready.Path, Assert.Single(_cosign.Calls, c => c.Argv[0] == "verify-blob").Path);
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task Without_cosign_the_download_is_staged_and_every_text_says_the_signature_was_not_verified()
    {
        var section = Section(() => Task.FromResult(Missing));
        var raised = new List<ChecksumsSignatureResult>();
        UiThread.Run(() => section.SignatureChecked += (_, result) => raised.Add(result));
        try
        {
            await StageAsync(section);

            UiThread.Run(() =>
            {
                Assert.True(section.IsStaged);
                Assert.Contains("NOT verified (cosign is not installed)", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.Contains("not that the project signed it", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.Contains("not verified (cosign not installed)", section.StagingSummary, StringComparison.Ordinal);
                Assert.Contains("NOT verified (cosign is not installed)", section.ResolverPlan, StringComparison.Ordinal);
                Assert.DoesNotContain("cosign verified", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.DoesNotContain("cosign verified", section.StagingSummary, StringComparison.Ordinal);
            });
            Assert.Equal(ChecksumsSignatureState.NotVerifiedNoCosign, Assert.Single(raised).State);
            Assert.Empty(_cosign.Calls);
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task A_release_with_no_signature_is_staged_and_the_texts_say_it_has_none()
    {
        var section = Section(() => Task.FromResult(Ready), bundle: false);
        try
        {
            await StageAsync(section);

            UiThread.Run(() =>
            {
                Assert.True(section.IsStaged);
                Assert.Contains("publishes no sigstore signature", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.Contains("integrity check only", section.ChecksumsSourceNote, StringComparison.Ordinal);
                Assert.Contains("publishes no sigstore signature", section.ResolverPlan, StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task A_rejected_signature_stages_nothing_blocks_the_run_and_says_why()
    {
        var section = Section(() => Task.FromResult(Ready));
        _cosign.VerifyExitCode = 1;
        _cosign.VerifyOutput = new[] { "Error: no matching signatures" };
        var raised = new List<ChecksumsSignatureResult>();
        UiThread.Run(() => section.SignatureChecked += (_, result) => raised.Add(result));
        try
        {
            await StageAsync(section);

            UiThread.Run(() =>
            {
                Assert.False(section.IsStaged);
                Assert.False(section.CanOpenConfirm);
                Assert.True(section.HasStagingError);
                Assert.Contains("Do not run this release's installer", section.StagingError, StringComparison.Ordinal);
                Assert.Contains("no matching signatures", section.StagingError, StringComparison.Ordinal);
                Assert.Equal("Bad", section.VerificationBadgeKey);
                Assert.Contains("Aborted: cosign could not verify", section.StagingSummary, StringComparison.Ordinal);
                Assert.Equal(string.Empty, section.StagedPath);
            });
            Assert.Equal(ChecksumsSignatureState.DidNotVerify, Assert.Single(raised).State);
            Assert.False(Directory.Exists(_temp.File("staging")) && Directory.GetFiles(_temp.File("staging"), "*", SearchOption.AllDirectories).Length > 0);
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task A_cosign_found_off_PATH_is_used_by_its_absolute_path_for_the_signature_check()
    {
        var section = Section(() => Task.FromResult(OffPath));
        try
        {
            await StageAsync(section);

            Assert.All(_cosign.Calls, c => Assert.Equal(OffPath.Path, c.Path));
            Assert.Equal(2, _cosign.Calls.Count);
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task A_download_started_while_the_cosign_probe_is_still_out_waits_for_it_rather_than_taking_cosign_to_be_missing()
    {
        var probe = new TaskCompletionSource<CosignStatus>();
        var calls = 0;
        _http.Serve(Bundle, "{}");
        var script = new byte[20 * 1024];
        new Random(6).NextBytes(script);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        var staging = _temp.File("staging");
        var runner = new UpgradeRunner(_services.Cli, new HttpClient(_http), staging, _cosign.VerifierOver(_http, staging));
        var section = UiThread.Run(() =>
        {
            var built = new UpgradeSectionViewModel(_services, runner, () => { calls++; return probe.Task; }) { SelectedChannel = UpgradeChannel.ResolverScript };
            built.ApplyCheck(new UpdateCheckResult { State = UpdateCheckState.UpdateAvailable, LatestVersion = "0.8.10" });
            return built;
        });
        try
        {
            var download = UiThread.Run(() => section.DownloadAndVerifyCommand.ExecuteAsync(null));
            await Task.Delay(300);
            Assert.False(download.IsCompleted);
            Assert.Empty(_cosign.Calls);

            probe.SetResult(Ready);
            await download;

            Assert.Equal(1, calls);
            Assert.Equal(Ready.Path, Assert.Single(_cosign.Calls, c => c.Argv[0] == "verify-blob").Path);
            UiThread.Run(() => Assert.Contains("cosign verified", section.StagingSummary, StringComparison.Ordinal));
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task Staging_again_after_a_rejection_clears_the_error_when_the_next_one_verifies()
    {
        var section = Section(() => Task.FromResult(Ready));
        try
        {
            _cosign.VerifyExitCode = 1;
            await StageAsync(section);
            Assert.True(UiThread.Run(() => section.HasStagingError));

            _cosign.VerifyExitCode = 0;
            await StageAsync(section);

            UiThread.Run(() =>
            {
                Assert.True(section.IsStaged);
                Assert.False(section.HasStagingError);
                Assert.Equal("Ok", section.VerificationBadgeKey);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }
}
