using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The things that write without a review of their own, on a read-only installation (CUST-308): the config editor, which writes config.yaml
/// itself rather than through the CLI (so the runner's guard never sees it), the automatic gateway start, and the runtime upgrade. Each asks the
/// same installation, with the same sentence, and a refusal leaves the disk exactly as it was. Every installation is synthetic and every path is
/// inside a scratch directory.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationWritersTests : IDisposable
{
    private const string Original = "config_version: 8\nguardrail:\n  mode: observe\n";
    private const string Edited = "config_version: 8\nguardrail:\n  mode: enforce\n";

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        return services;
    }

    // ------------------------------------------------------------------ the config editor's save service

    private string[] Backups() => Directory.GetFiles(_temp.Path, "config.yaml.bak-*");

    [Fact]
    public async Task Saving_on_a_read_only_installation_is_refused_before_anything_touches_the_disk()
    {
        var configPath = _temp.WriteFile("config.yaml", Original);
        var paths = TestServices.IsolatedPaths(_temp.Path, TestInstallations.ManagedAt(_temp.Path));
        var service = new ConfigSaveService(paths, cli: null);
        var signature = service.CaptureSignature();

        var outcome = await service.SaveAsync(Edited, signature);

        Assert.False(outcome.Success);
        Assert.Equal(SaveStage.WriteFailed, outcome.Stage);
        Assert.Equal("Nothing was saved: " + TestInstallations.ManagedReason, outcome.Message);
        Assert.Null(outcome.BackupPath);
        Assert.Equal(Original, File.ReadAllText(configPath));
        Assert.Empty(Backups());
        Assert.Empty(Directory.GetFiles(_temp.Path, ".config.yaml.tmp-*"));
    }

    [Fact]
    public async Task Saving_asks_the_runners_live_answer_so_a_config_edited_to_managed_while_the_editor_is_open_is_refused()
    {
        var services = Create();
        var configPath = _temp.WriteFile("config.yaml", Original);
        var service = new ConfigSaveService(services.Paths, services.Cli);
        var signature = service.CaptureSignature();

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        var outcome = await service.SaveAsync(Edited, signature);

        Assert.False(outcome.Success);
        Assert.StartsWith("Nothing was saved: ", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, outcome.Message, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(configPath));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task Restoring_a_backup_on_a_read_only_installation_is_refused_and_leaves_config_yaml_alone()
    {
        var configPath = _temp.WriteFile("config.yaml", Edited);
        var backup = _temp.WriteFile("config.yaml.bak-synthetic", Original);
        var paths = TestServices.IsolatedPaths(_temp.Path, TestInstallations.ManagedAt(_temp.Path));
        var service = new ConfigSaveService(paths, cli: null);

        var outcome = await service.RestoreFromBackupAsync(backup);

        Assert.False(outcome.Success);
        Assert.Equal("Nothing was restored: " + TestInstallations.ManagedReason, outcome.Message);
        Assert.Equal(Edited, File.ReadAllText(configPath));
    }

    // ------------------------------------------------------------------ the config editor window

    [Fact]
    public async Task The_config_editor_on_a_read_only_installation_still_loads_and_edits_but_its_save_and_restore_are_off_with_the_sentence()
    {
        using var h = await ConfigEditorHarness.LoadAsync();
        h.Services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var vm = h.ViewModel;

        // Reading and editing the buffer are not held back.
        Assert.False(vm.LoadFailed);
        vm.RawText = h.Raw + h.L("# an edit\n");

        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
        Assert.True(vm.HasChangesBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.SaveToolTip);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ReviewAndSaveCommand.CanExecute(null));
        Assert.False(vm.ConfirmReviewedSaveCommand.CanExecute(null));

        // Saving by any route writes nothing and makes no backup.
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.ConfirmReviewedSaveCommand.ExecuteAsync(null);
        Assert.Equal(h.Raw, File.ReadAllText(h.ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(h.ConfigPath)!, "config.yaml.bak-*"));
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task The_config_editor_on_a_writable_installation_can_save()
    {
        using var h = await ConfigEditorHarness.LoadAsync();
        var vm = h.ViewModel;

        Assert.Null(vm.ChangesBlockedReason);
        Assert.False(vm.HasChangesBlockedReason);
        Assert.Equal("Review and save (Ctrl+S)", vm.SaveToolTip);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.ReviewAndSaveCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ the automatic gateway start

    private sealed class AutoStartHarness : IDisposable
    {
        private readonly TempDirectory _temp;

        public AutoStartHarness(TempDirectory temp, Func<string?>? blocked)
        {
            _temp = temp;
            var settings = AppSettingsStore.OpenFresh(temp.File("settings-" + Guid.NewGuid().ToString("N") + ".json"));
            Assert.True(settings.Update(s => s with { Startup = s.Startup with { GatewayAutoStart = true } }));
            Source = new FakeSnapshotSource();
            Service = new GatewayAutoStart(
                settings,
                Source,
                (argv, _) =>
                {
                    lock (Runs)
                    {
                        Runs.Add(argv.ToArray());
                    }

                    var invocation = InvocationFactory.Create(argv: argv.ToArray());
                    InvocationFactory.Finish(invocation, 0);
                    return Task.FromResult(invocation);
                },
                () => true,
                () => Task.CompletedTask,
                action => action(),
                blocked);
        }

        public FakeSnapshotSource Source { get; }

        public GatewayAutoStart Service { get; }

        public List<IReadOnlyList<string>> Runs { get; } = new();

        public void Dispose() => Service.Dispose();
    }

    private static GatewaySnapshot Stopped() => new()
    {
        State = AppGatewayState.GatewayStopped,
        HealthStatus = DefenseClaw.Core.Gateway.GatewayStatus.Unreachable,
        PortRefused = true,
        Install = DefenseClaw.Core.Install.InstallState.GatewayStopped,
    };

    [Fact]
    public void The_automatic_start_never_starts_a_gateway_on_a_read_only_installation_and_says_why()
    {
        var h = new AutoStartHarness(_temp, () => TestInstallations.ManagedReason);
        _disposables.Add(h);

        h.Service.Start();
        h.Source.Publish(Stopped());
        Thread.Sleep(100);

        Assert.True(h.Service.HasDecided);
        Assert.Empty(h.Runs);
        Assert.Equal(GatewayAutoStartOutcome.None, h.Service.LastOutcome);
        Assert.Contains("read-only", h.Service.LastReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, h.Service.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_automatic_start_still_starts_a_gateway_on_a_writable_installation()
    {
        var h = new AutoStartHarness(_temp, () => null);
        _disposables.Add(h);

        h.Service.Start();
        h.Source.Publish(Stopped());
        var deadline = Environment.TickCount64 + 30_000;
        while (h.Service.LastOutcome != GatewayAutoStartOutcome.Started && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.Equal(GatewayAutoStartOutcome.Started, h.Service.LastOutcome);
        Assert.Equal(new[] { "start" }, Assert.Single(h.Runs));
    }

    // ------------------------------------------------------------------ the runtime upgrade

    private const string Script = UpgradeRunner.ScriptAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;
    private const string Bundle = ChecksumsSignatureVerifier.BundleAssetName;

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private UpgradeSectionViewModel Section(AppServices services)
    {
        var http = new StubHttpHandler();
        var script = new byte[20 * 1024];
        new Random(5).NextBytes(script);
        _ = http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n").Serve(Bundle, "{\"synthetic\":\"bundle\"}");

        var staging = _temp.File("staging");
        var runner = new UpgradeRunner(services.Cli, new HttpClient(http), staging, new FakeCosign().VerifierOver(http, staging));
        var cosign = new CosignStatus { Availability = CosignAvailability.OnPath, Path = @"C:\tools\cosign.exe", Detail = "cosign is on PATH." };
        var section = UiThread.Run(() =>
        {
            var built = new UpgradeSectionViewModel(services, runner, () => Task.FromResult(cosign)) { SelectedChannel = UpgradeChannel.ResolverScript };
            built.ApplyCheck(new UpdateCheckResult { State = UpdateCheckState.UpdateAvailable, LatestVersion = "0.8.10", InstalledVersion = "0.8.9" });
            return built;
        });
        UiThread.WaitFor(() => section.CosignLabel != "Checking…", "the cosign probe to answer");
        _disposables.Add(new ActionDisposable(() => UiThread.Run(section.Dispose)));
        return section;
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _dispose;

        public ActionDisposable(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    [Fact]
    public void On_a_writable_installation_the_upgrade_can_be_downloaded()
    {
        var section = Section(Create());

        UiThread.Run(() =>
        {
            Assert.Null(section.UpgradeBlockedReason);
            Assert.False(section.HasUpgradeBlockedReason);
            Assert.True(section.CanDownload);
        });
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("invalid")]
    public void On_a_read_only_installation_the_upgrade_is_not_offered_and_says_why(string which)
    {
        var context = which == "managed" ? TestInstallations.ManagedAt(_temp.Path) : TestInstallations.InvalidAt(_temp.Path);
        var section = Section(Create(context));

        UiThread.Run(() =>
        {
            Assert.True(section.HasUpgradeBlockedReason);
            Assert.Equal("The runtime is not upgraded from here. " + context.BlockedReason, section.UpgradeBlockedReason);
            Assert.False(section.CanDownload);
            Assert.False(section.CanOpenConfirm);
            Assert.False(section.CanRunUpgrade);
        });
    }

    [Fact]
    public void A_developer_runtime_is_not_upgraded_from_here_either_because_setup_would_not_touch_it()
    {
        var section = Section(Create(TestInstallations.DeveloperRuntime()));

        UiThread.Run(() =>
        {
            Assert.True(section.HasUpgradeBlockedReason);
            Assert.Contains("developer runtime", section.UpgradeBlockedReason, StringComparison.Ordinal);
            Assert.False(section.CanDownload);
        });
    }

    [Fact]
    public void An_open_upgrade_window_follows_the_installation_when_it_turns_read_only_and_back()
    {
        var services = Create();
        var section = Section(services);

        UiThread.Run(() =>
        {
            var changed = new List<string?>();
            ((System.ComponentModel.INotifyPropertyChanged)section).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(section.CanDownload);

            services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

            Assert.Contains(nameof(UpgradeSectionViewModel.CanDownload), changed);
            Assert.Contains(nameof(UpgradeSectionViewModel.UpgradeBlockedReason), changed);
            Assert.False(section.CanDownload);

            services.Installation.Replace(TestInstallations.UserDefault());

            Assert.True(section.CanDownload);
            Assert.Null(section.UpgradeBlockedReason);
        });
    }

    [Fact]
    public async Task A_staged_upgrade_cannot_be_run_once_the_installation_is_read_only()
    {
        var services = Create();
        var section = Section(services);
        await UiThread.Run(() => section.DownloadAndVerifyCommand.ExecuteAsync(null));
        UiThread.Run(() => UiThread.Settle());

        UiThread.Run(() =>
        {
            Assert.True(section.IsStaged);
            Assert.True(section.CanOpenConfirm);

            services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

            Assert.False(section.CanOpenConfirm);
            Assert.False(section.CanRunUpgrade);
        });
    }
}
