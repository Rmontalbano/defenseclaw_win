using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.App.Views.Updates;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-346 end to end over fakes: the Updates window's install.ps1 workflow (channel, pre-flight, download and verify, review, run, post-flight,
/// rollback, the leftovers) and how it looks. Nothing starts a process; the launcher records what it was asked.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallScriptUpgradeViewTests : IDisposable
{
    private const string Script = UpgradeRunner.InstallScriptAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;
    private const string Bundle = ChecksumsSignatureVerifier.BundleAssetName;
    private const string RemovedStartup = @"C:\Users\synthetic\AppData\Local\Programs\DefenseClaw\bin\defenseclaw-startup.exe";

    private static readonly CosignStatus Ready = new()
    {
        Availability = CosignAvailability.OnPath,
        Path = FakeCosign.Path,
        Detail = "cosign is on PATH.",
    };

    private readonly TempDirectory _temp = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeCosign _cosign = new();
    private readonly InstallScriptRunnerTests.Events _events = new();
    private readonly List<AppServices> _owned = new();

    public void Dispose()
    {
        foreach (var services in _owned)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private sealed class Probes : IUpgradeProbes
    {
        public List<string> Processes { get; } = new();

        public long? Free { get; set; } = 100L * 1024 * 1024 * 1024;

        public long Databases { get; set; } = 50L * 1024 * 1024;

        public IReadOnlyList<string> RunningProcessNames() => Processes;

        public long? FreeDiskBytes(string path) => Free;

        public long DatabaseBytes(string dataDirectory) => Databases;
    }

    private sealed class PostFlight : IPostFlightProbe
    {
        public PostFlightFacts Facts { get; set; } = new(
            @"C:\Users\synthetic\.local\bin\defenseclaw.exe",
            true,
            @"defenseclaw-cli 1.0.0 (C:\Users\synthetic\.local\bin\defenseclaw.exe)",
            "Policy model, ACP guard",
            false,
            "Stopped");

        public int Reads { get; private set; }

        public Task<PostFlightFacts> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Facts);
        }
    }

    private sealed class Leftovers : ILeftoverProbes
    {
        public string? Run { get; set; } = RemovedStartup;

        public UninstallEntry? Entry { get; set; } = new("DefenseClaw (Unsigned / Unverified Build) 0.8.9", @"C:\Cache\DefenseClawSetup-x64.exe /uninstall");

        public string? RunValue(string name) => Run;

        public UninstallEntry? FindUninstallEntry(string keyName) => Entry;

        public bool FileExists(string path) => false;
    }

    private sealed record Rig(
        InstallScriptUpgradeViewModel Vm,
        AppServices Services,
        InstallScriptRunnerTests.FakeLauncher Launcher,
        Probes Probes,
        PostFlight PostFlight);

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private Rig Build(InstallationContext? installation = null, bool serveScript = true, TimeProvider? time = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _owned.Add(services);

        if (serveScript)
        {
            var script = Bytes(20 * 1024);
            _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n").Serve(Bundle, "{\"synthetic\":\"bundle\"}");
        }

        var staging = _temp.File("staging");
        var upgrade = new UpgradeRunner(services.Cli, new HttpClient(_http), staging, _cosign.VerifierOver(_http, staging));
        var launcher = new InstallScriptRunnerTests.FakeLauncher(_events) { Exit = 0 };
        var runner = new InstallScriptRunner(
            services.Cli,
            launcher,
            new InstallScriptRunnerTests.FakeSession(_events),
            environment: () => new Dictionary<string, string?> { ["PATH"] = @"C:\synthetic\bin" });
        var probes = new Probes();
        var postFlight = new PostFlight();

        var vm = UiThread.Run(() => new InstallScriptUpgradeViewModel(
            services,
            upgrade,
            runner,
            probes,
            postFlight,
            () => Task.FromResult(Ready),
            reveal: _ => { },
            open: _ => { },
            leftovers: new Leftovers(),
            time: time));
        return new Rig(vm, services, launcher, probes, postFlight);
    }

    private static UpdateCheckResult Check(string latest, string installed, bool withSetupExe, bool update = true) => new()
    {
        State = update ? UpdateCheckState.UpdateAvailable : UpdateCheckState.UpToDate,
        LatestVersion = latest,
        InstalledVersion = installed,
        Assets = (withSetupExe
            ? new[] { "DefenseClawSetup-x64.exe", "checksums.txt" }
            : new[] { "install.ps1", "checksums.txt", "checksums.txt.bundle" })
            .Select(n => new ReleaseAsset { Name = n, Size = 4096 })
            .ToArray(),
    };

    private static void ApplyAndWait(Rig rig, UpdateCheckResult result)
    {
        UiThread.Run(() => rig.Vm.ApplyCheck(result, UpgradeChannel.SetupInstaller));
        if (rig.Vm.IsApplicable)
        {
            UiThread.WaitFor(() => rig.Vm.PreflightRows.Count == 7, "the pre-flight rows");
        }
    }

    private static async Task StageAsync(Rig rig)
    {
        await UiThread.Run(() => rig.Vm.DownloadAndVerifyAsync());
        UiThread.Run(UiThread.Settle);
    }

    // ------------------------------------------------------------------ the channel is chosen before any verification

    [Fact]
    public void A_1_0_0_target_with_no_setup_exe_switches_to_install_ps1_and_offers_no_setup_verification_at_all()
    {
        var rig = Build();

        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsApplicable);
            Assert.True(rig.Vm.IsVisible);
            Assert.True(rig.Vm.ShowUpgradeSteps);
            Assert.Equal("1.0.0", rig.Vm.InstallVersion);
            Assert.Equal("Upgrade to DefenseClaw 1.0.0", rig.Vm.Headline);
            Assert.Contains("publishes no DefenseClawSetup-x64.exe", rig.Vm.ChannelNote, StringComparison.Ordinal);
        });
        Assert.Empty(_http.Requested);
    }

    [Fact]
    public void A_0_8_x_target_that_ships_the_setup_exe_leaves_the_setup_channel_alone()
    {
        var rig = Build();

        ApplyAndWait(rig, Check("0.8.11", "0.8.10", withSetupExe: true));

        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.IsApplicable);
            Assert.False(rig.Vm.IsVisible);
        });
    }

    [Fact]
    public async Task The_windows_view_model_hides_the_setup_exe_card_for_1_0_0_and_shows_it_for_0_8_x()
    {
        var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        try
        {
            var shown = UiThread.Run(() =>
            {
                using var window = new UpdatesWindowViewModel(services);
                window.InstallScript.ApplyCheck(Check("1.0.0", "0.8.10", withSetupExe: false), UpgradeChannel.SetupInstaller);
                var legacyWhen10 = window.ShowLegacyUpgrade;
                window.InstallScript.ApplyCheck(Check("0.8.11", "0.8.10", withSetupExe: true), UpgradeChannel.SetupInstaller);
                return (legacyWhen10, window.ShowLegacyUpgrade);
            });

            Assert.False(shown.legacyWhen10);
            Assert.True(shown.Item2);
        }
        finally
        {
            await Task.Yield();
            temp.Dispose();
        }
    }

    // ------------------------------------------------------------------ the whole workflow

    [Fact]
    public async Task Download_review_run_and_post_flight_walk_through_with_the_exact_argv_and_no_yes_by_default()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.10");
        File.WriteAllText(
            Path.Combine(_temp.Path, "migration-v9.json"),
            "{\"schema_version\":1,\"moved\":[{\"source\":\"s\"},{\"source\":\"t\"}],\"conflicts\":[],\"removed\":[],\"actions_rows_moved\":4,\"notes\":[]}");

        await StageAsync(rig);

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsStaged);
            Assert.True(rig.Vm.CanReview);
            Assert.Contains("cosign verified", rig.Vm.StagingSummary, StringComparison.Ordinal);
        });

        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsReviewVisible);
            var step = Assert.Single(rig.Vm.Review!.Steps);
            Assert.Equal("powershell.exe", step.Executable);
            Assert.Equal(CommandTier.StateChanging, step.Tier);
            Assert.Equal(CommandTier.StateChanging, rig.Vm.Review.Tier);
            Assert.DoesNotContain("-Yes", step.Argv);
            Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }, step.Argv.Take(4).ToArray());
            Assert.EndsWith(@"1.0.0\install.ps1", step.Argv[4], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { "-Version", "1.0.0" }, step.Argv.Skip(5).ToArray());
            Assert.True(rig.Vm.Review.RestartsGateway);
            Assert.False(rig.Vm.Review.IsBlocked);
        });
        Assert.Empty(rig.Launcher.Requests);

        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);

        Assert.Equal(new[] { "pause+release", "start", "resume" }, _events.Log);
        var launch = Assert.Single(rig.Launcher.Requests);
        Assert.True(launch.NewConsole);
        Assert.True(launch.BreakAwayFromJob);

        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.IsReviewVisible);
            Assert.True(rig.Vm.HasResult);
            Assert.Equal("Succeeded (exit 0)", rig.Vm.ExitLabel);
            Assert.Equal("Ok", rig.Vm.ExitToneKey);
            Assert.True(rig.Vm.HasPostFlight);
            Assert.Contains("defenseclaw-cli 1.0.0", rig.Vm.RuntimeText, StringComparison.Ordinal);
            Assert.Contains("2 values moved", rig.Vm.MigrationText, StringComparison.Ordinal);
            Assert.Contains("4 action rows moved", rig.Vm.MigrationText, StringComparison.Ordinal);
            Assert.True(rig.Vm.HasMigration);
            Assert.True(rig.Vm.GatewayDown);
            Assert.True(rig.Vm.CanStartGateway);
            Assert.Equal(
                new[] { PostUpgradeLeftovers.AutostartId, PostUpgradeLeftovers.UninstallEntryId },
                rig.Vm.LeftoverRows.Select(r => r.Id).ToArray());
        });
        Assert.Equal(1, rig.PostFlight.Reads);
    }

    [Fact]
    public async Task The_opt_in_yes_toggle_adds_yes_to_the_reviewed_command_and_to_nothing_else()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        await StageAsync(rig);

        UiThread.Run(() =>
        {
            rig.Vm.UseYes = true;
            rig.Vm.OpenUpgradeReviewCommand.Execute(null);
            var argv = rig.Vm.Review!.Steps[0].Argv;
            Assert.Equal("-Yes", argv[^1]);
            Assert.Equal(1, argv.Count(a => a == "-Yes"));
        });
    }

    [Fact]
    public async Task Cancelling_the_review_runs_nothing()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        await StageAsync(rig);

        UiThread.Run(() =>
        {
            rig.Vm.OpenUpgradeReviewCommand.Execute(null);
            rig.Vm.CancelReviewCommand.Execute(null);
            Assert.False(rig.Vm.IsReviewVisible);
        });

        Assert.Empty(rig.Launcher.Requests);
        Assert.Empty(_events.Log);
    }

    [Fact]
    public async Task A_failed_run_offers_a_rollback_review_that_runs_the_staged_script_with_rollback()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.10");
        await StageAsync(rig);
        rig.Launcher.Exit = 1;

        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);

        UiThread.Run(() =>
        {
            Assert.Equal("Bad", rig.Vm.ExitToneKey);
            Assert.Contains("install log", rig.Vm.ExitMessage, StringComparison.Ordinal);
            Assert.True(rig.Vm.CanRollback);
            rig.Vm.OpenRollbackReviewCommand.Execute(null);
            var step = rig.Vm.Review!.Steps[0];
            Assert.Equal("-Rollback", step.Argv[^1]);
            Assert.DoesNotContain("-Version", step.Argv);
            Assert.Equal(CommandTier.StateChanging, step.Tier);
        });

        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);
        Assert.Equal(2, rig.Launcher.Requests.Count);
        Assert.Equal("-Rollback", rig.Launcher.Requests[1].Argv[^1]);
    }

    [Fact]
    public async Task A_usage_error_says_nothing_was_changed_and_offers_no_rollback()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.10");
        await StageAsync(rig);
        rig.Launcher.Exit = 2;

        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);

        UiThread.Run(() =>
        {
            Assert.Contains("Nothing was changed", rig.Vm.ExitMessage, StringComparison.Ordinal);
            Assert.False(rig.Vm.CanRollback);
        });
    }

    [Fact]
    public async Task The_post_flight_start_gateway_button_is_a_reviewed_gateway_start()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        await StageAsync(rig);
        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);

        UiThread.Run(() =>
        {
            rig.Vm.OpenStartGatewayReviewCommand.Execute(null);
            var step = rig.Vm.Review!.Steps[0];
            Assert.Equal("defenseclaw-gateway", step.Executable);
            Assert.Equal(new[] { "start" }, step.Argv.ToArray());
            Assert.Equal(CommandTier.StateChanging, step.Tier);
            Assert.Equal(InstallReviewKind.StartGateway, rig.Vm.ReviewKind);
            rig.Vm.CancelReviewCommand.Execute(null);
        });
    }

    // ------------------------------------------------------------------ gates

    [Fact]
    public void A_managed_installation_blocks_the_pre_flight_the_download_and_the_review()
    {
        var rig = Build(TestInstallations.Managed());

        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));

        UiThread.Run(() =>
        {
            var row = rig.Vm.PreflightRows.Single(r => r.Id == InstallScriptPreflight.InstallationId);
            Assert.Equal(PreflightStatus.Block, row.Status);
            Assert.True(rig.Vm.HasPreflightBlock);
            Assert.False(rig.Vm.CanDownload);
            Assert.False(rig.Vm.CanReview);
            Assert.True(rig.Vm.HasBlockedReason);
            Assert.StartsWith("Blocked:", rig.Vm.PreflightSummary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_blocking_pre_flight_row_keeps_a_verified_script_from_being_reviewed()
    {
        var rig = Build();
        rig.Probes.Free = 1024;
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        await StageAsync(rig);

        UiThread.WaitFor(() => rig.Vm.HasPreflightBlock, "the disk row to block");
        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsStaged);
            Assert.False(rig.Vm.CanReview);
        });
    }

    [Fact]
    public async Task Verification_failures_show_the_reason_block_the_run_and_stage_nothing()
    {
        var rig = Build(serveScript: false);
        var script = Bytes(20 * 1024);
        _http.Serve(Script, Bytes(20 * 1024).Select(b => (byte)(b ^ 1)).ToArray()).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));

        await StageAsync(rig);

        UiThread.WaitFor(() => rig.Vm.PreflightRows.Any(r => r.Id == InstallScriptPreflight.VerificationId && r.Status == PreflightStatus.Block), "the verification row to block");
        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.IsStaged);
            Assert.True(rig.Vm.HasStagingError);
            Assert.Contains("does not match", rig.Vm.StagingError + rig.Vm.StagingSummary, StringComparison.Ordinal);
            Assert.False(rig.Vm.CanReview);
        });
    }

    // ------------------------------------------------------------------ a machine that is already on 1.0

    [Fact]
    public async Task On_a_machine_already_on_1_0_the_card_offers_the_checks_the_leftovers_and_a_rollback_with_no_upgrade()
    {
        var rig = Build();
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.10");

        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.0", "1.0.0", withSetupExe: false, update: false), UpgradeChannel.SetupInstaller));
        UiThread.WaitFor(() => rig.Vm.HasPostFlight, "the read-only post-flight");

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsPostUpgradeMode);
            Assert.False(rig.Vm.IsApplicable);
            Assert.True(rig.Vm.IsVisible);
            Assert.True(rig.Vm.ShowPostFlight);
            Assert.False(rig.Vm.ShowUpgradeSteps);
            Assert.False(rig.Vm.HasResult);
            Assert.True(rig.Vm.RollbackAvailable);
            Assert.Equal(2, rig.Vm.LeftoverRows.Count);
            Assert.True(rig.Vm.CanDownload);
            Assert.False(rig.Vm.CanRollback);
            Assert.Equal("1.0.0", rig.Vm.ScriptVersion);
        });

        await StageAsync(rig);

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.CanRollback);
            rig.Vm.OpenRollbackReviewCommand.Execute(null);
            Assert.Equal("-Rollback", rig.Vm.Review!.Steps[0].Argv[^1]);
            Assert.EndsWith(@"1.0.0\install.ps1", rig.Vm.Review.Steps[0].Argv[4], StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_previous_setup_install_is_shown_with_the_installers_own_refusal_and_no_rollback_button()
    {
        var rig = Build();
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous", "legacy-setup"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.9");

        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.0", "1.0.0", withSetupExe: false, update: false), UpgradeChannel.SetupInstaller));
        UiThread.WaitFor(() => rig.Vm.HasPostFlight, "the read-only post-flight");

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.IsPostUpgradeMode);
            Assert.False(rig.Vm.RollbackAvailable);
            Assert.False(rig.Vm.CanRollback);
            Assert.Contains("cannot restore", rig.Vm.RollbackNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_1_0_machine_with_no_previous_install_shows_no_card()
    {
        var rig = Build();

        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.0", "1.0.0", withSetupExe: false, update: false), UpgradeChannel.SetupInstaller));

        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.IsPostUpgradeMode);
            Assert.False(rig.Vm.IsVisible);
        });
    }

    // ------------------------------------------------------------------ the install state is read fresh

    private sealed class ClockAt : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 14, 5, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private void KeepPrevious(string version, bool legacy = false)
    {
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), version);
        if (legacy)
        {
            _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous", "legacy-setup"));
        }
    }

    private static void ApplyOn1_0AndWait(Rig rig, int reads)
    {
        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.1", "1.0.1", withSetupExe: false, update: false), UpgradeChannel.SetupInstaller));
        UiThread.WaitFor(() => rig.PostFlight.Reads >= reads && !rig.Vm.IsReadingPostFlight, "the install state read");
    }

    [Fact]
    public void The_rollback_button_names_its_target_and_the_state_rows_show_the_real_config_version_and_last_install()
    {
        var clock = new ClockAt();
        var rig = Build(time: clock);
        KeepPrevious("1.0.0");
        File.WriteAllText(Path.Combine(_temp.Path, "config.yaml"), "config_version: 9\n");
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs"));
        File.WriteAllText(
            Path.Combine(_temp.Path, "logs", "install-20261012T164027.log"),
            "Start time: 20261012164027\r\nHost Application: powershell -Version 1.0.1\r\n  + Installed DefenseClaw 1.0.1\r\n  Upgraded from 1.0.0. Undo with: defenseclaw rollback\r\n");

        ApplyOn1_0AndWait(rig, 1);

        UiThread.Run(() =>
        {
            Assert.Equal("Roll back to 1.0.0…", rig.Vm.RollbackLabel);
            Assert.Equal("Config version 9", rig.Vm.ConfigVersionText);
            Assert.StartsWith("Upgrade 1.0.0 → 1.0.1, ", rig.Vm.LastInstallText, StringComparison.Ordinal);
            Assert.EndsWith("installed", rig.Vm.LastInstallText, StringComparison.Ordinal);
            Assert.Equal("Ok", rig.Vm.LastInstallToneKey);
            Assert.True(rig.Vm.HasLastInstallLog);
            Assert.Equal("Read at 14:05", rig.Vm.ReadAtText);
            Assert.Equal("Download and verify install.ps1 first (Download and verify, above); the rollback runs that verified script.", rig.Vm.RollbackBlockedReason);
        });
    }

    [Fact]
    public async Task Rollback_is_enabled_once_the_script_is_verified_and_has_no_reason_then()
    {
        var rig = Build();
        KeepPrevious("1.0.0");
        ApplyOn1_0AndWait(rig, 1);

        await StageAsync(rig);

        UiThread.Run(() =>
        {
            Assert.True(rig.Vm.CanRollback);
            Assert.Null(rig.Vm.RollbackBlockedReason);
        });
    }

    [Fact]
    public void A_kept_setup_install_keeps_the_installers_explanation_and_a_missing_previous_install_says_none_is_kept()
    {
        var rig = Build();
        KeepPrevious("0.8.9", legacy: true);
        ApplyOn1_0AndWait(rig, 1);

        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.CanRollback);
            Assert.Equal("Roll back to 0.8.9…", rig.Vm.RollbackLabel);
            Assert.Contains("cannot restore", rig.Vm.RollbackBlockedReason, StringComparison.Ordinal);
        });

        // The installer removes previous\ (or another tool does) while the window is open: Refresh sees it.
        Directory.Delete(Path.Combine(_temp.Path, "previous"), recursive: true);
        UiThread.Run(() => rig.Vm.RefreshStateCommand.Execute(null));
        UiThread.WaitFor(() => rig.PostFlight.Reads >= 2 && !rig.Vm.IsReadingPostFlight, "the refresh");

        UiThread.Run(() =>
        {
            Assert.Equal("Roll back…", rig.Vm.RollbackLabel);
            Assert.False(rig.Vm.RollbackAvailable);
            Assert.Equal("No previous install is kept.", rig.Vm.RollbackNote);
            Assert.Equal("No previous install is kept.", rig.Vm.RollbackBlockedReason);
        });
    }

    [Fact]
    public void The_installation_guards_reason_comes_before_any_rollback_reason()
    {
        var rig = Build(TestInstallations.Managed());
        KeepPrevious("1.0.0");
        ApplyOn1_0AndWait(rig, 1);

        UiThread.Run(() =>
        {
            Assert.False(rig.Vm.CanRollback);
            Assert.NotNull(rig.Vm.BlockedReason);
            Assert.Equal(rig.Vm.BlockedReason, rig.Vm.RollbackBlockedReason);
        });
    }

    [Fact]
    public void Every_check_and_the_refresh_button_read_the_state_again_and_the_clock_moves()
    {
        var clock = new ClockAt();
        var rig = Build(time: clock);
        KeepPrevious("1.0.0");
        File.WriteAllText(Path.Combine(_temp.Path, "migration-v9.json"), "{\"moved\":[1],\"conflicts\":[],\"removed\":[]}");
        ApplyOn1_0AndWait(rig, 1);
        UiThread.Run(() => Assert.Contains("1 value moved", rig.Vm.MigrationText, StringComparison.Ordinal));

        // "Check again" while the window stays open: the card does not keep what it read before.
        File.WriteAllText(Path.Combine(_temp.Path, "migration-v9.json"), "{\"moved\":[1,2],\"conflicts\":[],\"removed\":[]}");
        clock.Now = clock.Now.AddMinutes(7);
        ApplyOn1_0AndWait(rig, 2);
        UiThread.Run(() =>
        {
            Assert.Contains("2 values moved", rig.Vm.MigrationText, StringComparison.Ordinal);
            Assert.Equal("Read at 14:12", rig.Vm.ReadAtText);
        });

        // Refresh, and the window being brought forward (the same command).
        File.WriteAllText(Path.Combine(_temp.Path, "config.yaml"), "config_version: 10\n");
        clock.Now = clock.Now.AddMinutes(3);
        UiThread.Run(() => rig.Vm.RefreshStateCommand.Execute(null));
        UiThread.WaitFor(() => rig.PostFlight.Reads >= 3 && !rig.Vm.IsReadingPostFlight, "the refresh");
        UiThread.Run(() =>
        {
            Assert.Equal("Config version 10", rig.Vm.ConfigVersionText);
            Assert.Equal("Read at 14:15", rig.Vm.ReadAtText);
        });
    }

    [Fact]
    public void Refresh_does_nothing_on_a_card_that_shows_no_install_state()
    {
        var rig = Build();

        UiThread.Run(() => rig.Vm.RefreshStateCommand.Execute(null));
        UiThread.Run(UiThread.Settle);

        Assert.Equal(0, rig.PostFlight.Reads);
    }

    [Fact]
    public async Task A_finished_run_reads_the_state_and_shows_the_run_as_the_last_install_from_activity()
    {
        var rig = Build();
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        KeepPrevious("0.8.10");
        await StageAsync(rig);

        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);
        UiThread.WaitFor(() => rig.Vm.HasPostFlight && !rig.Vm.IsReadingPostFlight, "the post-run read");

        UiThread.Run(() =>
        {
            Assert.Equal(1, rig.PostFlight.Reads);
            Assert.StartsWith("Install run unknown → 1.0.0, ", rig.Vm.LastInstallText, StringComparison.Ordinal);
            Assert.EndsWith("installed", rig.Vm.LastInstallText, StringComparison.Ordinal);
            Assert.False(rig.Vm.HasLastInstallLog);
            Assert.Equal("Roll back to 0.8.10…", rig.Vm.RollbackLabel);
        });
    }

    // ------------------------------------------------------------------ how it looks

    private static OffscreenHost Host(FrameworkElement content, double height) => new(content, 900, height);

    [Fact]
    public async Task The_card_renders_the_pre_flight_the_review_and_the_post_flight()
    {
        var rig = Build();
        rig.Probes.Processes.AddRange(new[] { "claude", "defenseclaw-gateway" });
        ApplyAndWait(rig, Check("1.0.0", "0.8.10", withSetupExe: false));
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        File.WriteAllText(Path.Combine(_temp.Path, "previous", "VERSION"), "0.8.10");
        File.WriteAllText(Path.Combine(_temp.Path, "migration-v9.json"), "{\"moved\":[1,2,3],\"conflicts\":[1],\"removed\":[],\"actions_rows_moved\":9}");

        using var theme = UiThread.FluentTheme();

        // 1. Pre-flight with a warning, before anything is downloaded.
        UiThread.Run(() =>
        {
            using var host = Host(new InstallScriptUpgradeCard { DataContext = rig.Vm }, 1250);
            RenderTo.Png(host, "cust346-1-preflight");
            var texts = VisibleTexts(host);
            Assert.Contains("What changes", texts);
            Assert.Contains("Pre-flight (read-only)", texts);
            Assert.Contains(texts, t => t.Contains("Quit it first", StringComparison.Ordinal));
            Assert.Contains("Download & verify install.ps1", ButtonTexts(host));
        });

        await StageAsync(rig);

        // 2. The review dialog.
        UiThread.Run(() =>
        {
            rig.Vm.OpenUpgradeReviewCommand.Execute(null);
            using var host = Host(new InstallScriptReviewOverlay { DataContext = rig.Vm }, 820);
            RenderTo.Png(host, "cust346-2-review");
            var texts = VisibleTexts(host);
            Assert.Contains(texts, t => t.Contains("install.ps1", StringComparison.Ordinal) && t.Contains("-Version 1.0.0", StringComparison.Ordinal));
            Assert.Contains("Run install.ps1", ButtonTexts(host));
            rig.Vm.CancelReviewCommand.Execute(null);
        });

        // 3. After the run, with the leftovers.
        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);
        UiThread.Run(() =>
        {
            using var host = Host(new InstallScriptUpgradeCard { DataContext = rig.Vm }, 1900);
            RenderTo.Png(host, "cust346-3-postflight");
            var texts = VisibleTexts(host);
            Assert.Contains("Install state (read-only)", texts);
            Assert.Contains("Gateway autostart", texts);
            Assert.Contains("Add/Remove Programs entry", texts);
            Assert.Contains(texts, t => t.Contains("3 values moved", StringComparison.Ordinal));
            var buttons = ButtonTexts(host);
            Assert.Contains("Start gateway…", buttons);
            Assert.Contains("Roll back to 0.8.10…", buttons);
            Assert.Contains("Refresh", buttons);
            Assert.Contains("Config", texts);
            Assert.Contains("Last install", texts);
            Assert.Contains(texts, t => t.StartsWith("Read at ", StringComparison.Ordinal));
        });
    }

    private static List<string> VisibleTexts(OffscreenHost host) =>
        VisualTree.Descendants<TextBlock>(host.Content).Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text)
            .Concat(VisualTree.Descendants<TextBox>(host.Content).Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text))
            .ToList();

    private static List<string> ButtonTexts(OffscreenHost host) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(host.Content).Where(b => b.IsVisible && b.Content is string).Select(b => (string)b.Content).ToList();
}
