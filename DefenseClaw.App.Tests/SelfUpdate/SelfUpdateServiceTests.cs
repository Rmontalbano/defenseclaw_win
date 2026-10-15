using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>
/// The whole apply path with the restart and the "successor started" signal injected: stage, swap, hand-off, rollback when the successor never
/// reports, the Add/Remove Programs entry. No process is started, no real exe replaced, no real registry touched.
/// </summary>
public sealed class SelfUpdateServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _appDir;
    private readonly string _dataRoot;
    private readonly string _appExe;
    private readonly string _helperExe;
    private readonly List<IDisposable> _disposables = [];

    public SelfUpdateServiceTests()
    {
        _appDir = Path.Combine(_temp.Path, "Programs", "DefenseClawWin");
        _dataRoot = Path.Combine(_temp.Path, "data");
        _ = Directory.CreateDirectory(_appDir);
        _appExe = Path.Combine(_appDir, SelfUpdatePackage.AppExeName);
        _helperExe = Path.Combine(_appDir, SelfUpdatePackage.HelperExeName);
        File.WriteAllText(_appExe, "old-app");
        File.WriteAllText(_helperExe, "old-helper");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private sealed class FakeLauncher : IAppProcessLauncher
    {
        public List<(string Exe, string[] Args)> Started { get; } = [];

        public void Start(string executable, IReadOnlyList<string> arguments) => Started.Add((executable, arguments.ToArray()));
    }

    private sealed class FakeSignal(bool arrives) : IStartedSignal
    {
        public string Name { get; } = SelfUpdateHandshake.NewName();

        public TimeSpan? WaitedFor { get; private set; }

        public bool Disposed { get; private set; }

        public bool Wait(TimeSpan timeout)
        {
            WaitedFor = timeout;
            return arrives;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeUninstallKeys : IUninstallKeyStore
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = [];

        public string? GetString(string subKey, string name) => Values.GetValueOrDefault((subKey, name));

        public bool SetString(string subKey, string name, string value)
        {
            if (!Values.Keys.Any(k => k.Key == subKey))
            {
                return false;
            }

            Values[(subKey, name)] = value;
            return true;
        }
    }

    private sealed record Rig(SelfUpdateService Service, AppRestart Restart, FakeLauncher Launcher, FakeSignal Signal, FakeUninstallKeys Keys, List<string> Restarts);

    private Rig Build(bool signalArrives, string running = "1.1.0", Func<IReadOnlyList<CliInvocation>>? activity = null)
    {
        var zip = SelfUpdateKit.GoodZip();
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0", zip);
        var client = new HttpClient(github);
        var launcher = new FakeLauncher();
        var restart = new AppRestart(activity ?? (() => []), launcher, () => _appExe + ".old", () => 4242);
        var installer = new SelfUpdateInstaller(() => client, _appExe, _dataRoot, _ => "1.2.0", () => restart.BlockedReason);
        var store = new SelfUpdateStore(Path.Combine(_dataRoot, "state.json"));
        var watcher = new AppUpdateWatcher((_, _) => Task.FromResult(AppUpdateCheck.NotChecked), store, post: a => a());
        var signal = new FakeSignal(signalArrives);
        var keys = new FakeUninstallKeys();
        Assert.True(AppVersion.TryParse(running, out var runningVersion));
        var service = new SelfUpdateService(watcher, store, installer, restart, runningVersion, _appExe, keys, () => signal, delay: _ => Task.CompletedTask);
        _disposables.Add(service);
        return new Rig(service, restart, launcher, signal, keys, []);
    }

    private static string Text(string path) => File.ReadAllText(path);

    [Fact]
    public async Task A_successor_that_reports_in_lets_the_old_copy_exit_and_the_new_files_stay()
    {
        var rig = Build(signalArrives: true);
        Task<(bool Launched, string? Error)>? launch = null;
        rig.Restart.RestartRequested += (_, _) => launch = rig.Restart.TryLaunchAsync();

        var failure = await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None);
        var outcome = await launch!;

        Assert.Null(failure);
        Assert.True(outcome.Launched);
        Assert.Equal("new-app", Text(_appExe));
        Assert.Equal("new-helper", Text(_helperExe));
        Assert.Equal("old-app", Text(_appExe + ".old"));

        // The NEW exe at the app's own path is started (not the renamed running one), with the restart flag and the signal name.
        var (exe, args) = Assert.Single(rig.Launcher.Started);
        Assert.Equal(_appExe, exe);
        Assert.Contains("--after-restart", args);
        Assert.Equal(rig.Signal.Name, args[Array.IndexOf(args, SelfUpdateHandshake.Flag) + 1]);
        Assert.NotNull(rig.Signal.WaitedFor);
        Assert.Null(rig.Restart.PendingHandoff);
    }

    [Fact]
    public async Task A_successor_that_never_reports_in_is_rolled_back_and_the_old_copy_stays()
    {
        var rig = Build(signalArrives: false);
        Task<(bool Launched, string? Error)>? launch = null;
        rig.Restart.RestartRequested += (_, _) => launch = rig.Restart.TryLaunchAsync();

        var failure = await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None);
        var outcome = await launch!;

        Assert.Null(failure);
        Assert.False(outcome.Launched);
        Assert.Contains("did not report that it started", outcome.Error);
        Assert.Contains("previous version was restored", outcome.Error);
        Assert.Equal("old-app", Text(_appExe));
        Assert.Equal("old-helper", Text(_helperExe));
        Assert.False(File.Exists(_appExe + ".old"));
        Assert.Null(new SelfUpdateInstaller(() => new HttpClient(), _appExe, _dataRoot).ReadMarker());
        Assert.Null(rig.Restart.PendingHandoff);
        Assert.False(rig.Service.HasInstalledUpdate);
    }

    [Fact]
    public async Task A_busy_state_changing_run_refuses_before_anything_is_downloaded()
    {
        var busy = InvocationFactory.CreateFor("powershell.exe", ["-File", "install.ps1"]);
        var rig = Build(signalArrives: true, activity: () => [busy]);

        var failure = await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None);

        Assert.Contains("still running", failure);
        Assert.Equal("old-app", Text(_appExe));
        Assert.Empty(Directory.GetFiles(_appDir, "*.old"));
    }

    [Fact]
    public async Task A_restart_the_shell_refuses_undoes_the_swap()
    {
        var rig = Build(signalArrives: true);

        // No RestartRequested handler: Request() says restarting is not available, which must not leave swapped files behind.
        var failure = await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None);

        Assert.Contains("not available", failure);
        Assert.Equal("old-app", Text(_appExe));
        Assert.Equal("old-helper", Text(_helperExe));
        Assert.Null(rig.Restart.PendingHandoff);
        Assert.False(rig.Service.HasInstalledUpdate);
    }

    [Fact]
    public async Task A_restart_the_operator_declines_leaves_the_update_installed_for_the_next_start()
    {
        var rig = Build(signalArrives: true);
        rig.Restart.RestartRequested += (_, _) => { /* the shell's exit checks said no */ };

        var failure = await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None);

        Assert.Null(failure);
        Assert.True(rig.Service.HasInstalledUpdate);
        Assert.Equal("new-app", Text(_appExe));
        Assert.NotNull(rig.Restart.PendingHandoff);
        Assert.Equal("An update is already installed and waiting for the app to restart.", await rig.Service.InstallAsync(SelfUpdateKit.Release("1.2.0"), null, CancellationToken.None));
    }

    [Fact]
    public async Task The_review_shows_the_expected_hash_and_the_admin_helper_note_without_downloading_the_zip()
    {
        var rig = Build(signalArrives: true);
        var github = new SelfUpdateKit.FakeGitHub().ServeRelease("1.2.0");
        var client = new HttpClient(github);
        var installer = new SelfUpdateInstaller(() => client, _appExe, _dataRoot, _ => "1.2.0");
        var service = new SelfUpdateService(
            rig.Service.Watcher, rig.Service.Store, installer, rig.Restart, rig.Service.Running, _appExe, rig.Keys);

        var (sha, problem, notes) = await service.PrepareReviewAsync(SelfUpdateKit.Release("1.2.0"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(SelfUpdateKit.Sha(SelfUpdateKit.GoodZip()), sha);
        Assert.Equal([SelfUpdateKit.AssetUrl("1.2.0", "checksums.txt")], github.Urls);
        Assert.Contains(notes, n => n.Contains("administrator-mode restart"));
    }

    // ------------------------------------------------------------------ after the restart

    [Fact]
    public async Task The_new_version_confirms_cleans_up_updates_add_remove_programs_and_says_so()
    {
        var rig = Build(signalArrives: true, running: "1.2.0");
        File.WriteAllText(_appExe + ".old", "old-app");
        File.WriteAllText(_helperExe + ".old", "old-helper");
        _ = Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, SelfUpdateInstaller.MarkerName), $$"""{"from":"1.1.0","to":"1.2.0","at":"2026-10-20T12:00:00Z","appDirectory":"{{_appDir.Replace("\\", "\\\\")}}"}""");
        rig.Keys.Values[(InstallerRegistration.SubKey, "InstallLocation")] = _appDir + "\\";
        rig.Keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")] = "1.1.0";
        var notices = new List<string>();
        rig.Service.Notice += (_, text) => notices.Add(text);

        await rig.Service.CompletePendingUpdateAsync();

        Assert.False(File.Exists(_appExe + ".old"));
        Assert.False(File.Exists(Path.Combine(_dataRoot, SelfUpdateInstaller.MarkerName)));
        Assert.Equal("1.2.0", rig.Keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")]);
        Assert.True(rig.Service.UpdatedInstallerEntry);
        Assert.Equal(["Updated to DefenseClaw for Windows 1.2.0."], notices);
    }

    [Fact]
    public async Task A_locked_old_file_is_retried_until_the_previous_process_lets_go()
    {
        var rig = Build(signalArrives: true, running: "1.2.0");
        File.WriteAllText(_appExe + ".old", "old-app");
        var holding = new FileStream(_appExe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);
        var delays = 0;
        var service = new SelfUpdateService(
            rig.Service.Watcher, rig.Service.Store, new SelfUpdateInstaller(() => new HttpClient(), _appExe, _dataRoot), rig.Restart, rig.Service.Running, _appExe, rig.Keys,
            delay: _ =>
            {
                if (++delays == 2)
                {
                    holding.Dispose();
                }

                return Task.CompletedTask;
            });

        await service.CompletePendingUpdateAsync();

        Assert.False(File.Exists(_appExe + ".old"));
        Assert.Equal(2, delays);
        holding.Dispose();
    }

    // ------------------------------------------------------------------ the hand-off flag and Add/Remove Programs

    [Fact]
    public void The_successors_signal_flag_is_read_only_in_the_exact_form_we_make()
    {
        var name = SelfUpdateHandshake.NewName();

        Assert.Equal(name, SelfUpdateHandshake.ParseName(["--after-restart", "7", "--update-signal", name]));
        Assert.Null(SelfUpdateHandshake.ParseName(["--update-signal", "Global\\anything"]));
        Assert.Null(SelfUpdateHandshake.ParseName(["--update-signal"]));
        Assert.Null(SelfUpdateHandshake.ParseName(["--minimized"]));
    }

    [Fact]
    public void The_signal_is_set_through_the_seam_when_the_flag_is_valid()
    {
        var name = SelfUpdateHandshake.NewName();
        string? set = null;

        Assert.True(SelfUpdateHandshake.SignalStarted(["--update-signal", name], n => { set = n; return true; }));
        Assert.Equal(name, set);
        Assert.False(SelfUpdateHandshake.SignalStarted(["--minimized"], n => true));
    }

    [Fact]
    public void A_real_named_event_round_trips_between_the_two_ends()
    {
        using var signal = new NamedStartedSignal();

        Assert.False(signal.Wait(TimeSpan.Zero));
        Assert.True(SelfUpdateHandshake.SignalStarted(["--update-signal", signal.Name]));
        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_signal_for_an_event_nobody_created_is_not_created_by_the_successor()
    {
        Assert.False(SelfUpdateHandshake.SignalStarted(["--update-signal", SelfUpdateHandshake.NewName()]));
    }

    [Fact]
    public void Add_remove_programs_is_updated_only_for_this_installers_entry_in_this_folder()
    {
        var keys = new FakeUninstallKeys();
        var version = new AppVersion(1, 2, 0, null);
        keys.Values[(InstallerRegistration.SubKey, "InstallLocation")] = _appDir + "\\";
        keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")] = "1.1.0";

        Assert.True(InstallerRegistration.UpdateDisplayVersion(keys, _appDir, version));
        Assert.Equal("1.2.0", keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")]);

        // Another folder (a portable copy next to an installed one) does not touch the installer's entry.
        keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")] = "1.1.0";
        Assert.False(InstallerRegistration.UpdateDisplayVersion(keys, Path.Combine(_temp.Path, "elsewhere"), version));
        Assert.Equal("1.1.0", keys.Values[(InstallerRegistration.SubKey, "DisplayVersion")]);
    }

    [Fact]
    public void Add_remove_programs_with_no_entry_is_left_alone_and_never_names_ciscos_key()
    {
        var keys = new FakeUninstallKeys();

        Assert.False(InstallerRegistration.UpdateDisplayVersion(keys, _appDir, new AppVersion(1, 2, 0, null)));
        Assert.Empty(keys.Values);
        Assert.Equal("{290C7D59-D43A-4CF8-BE38-60332DD563EC}_is1", InstallerRegistration.SubKey);
        Assert.NotEqual("DefenseClaw", InstallerRegistration.SubKey);
    }

    // ------------------------------------------------------------------ the view-model

    [Fact]
    public void The_view_model_starts_idle_with_the_honest_trust_sentence()
    {
        var rig = Build(signalArrives: true);
        var vm = rig.Service.ViewModel;

        Assert.Equal("Not checked yet", vm.StatusText);
        Assert.False(vm.ShowBanner);
        Assert.Contains("not Authenticode-signed", vm.TrustText);
        Assert.Equal("1.1.0", vm.RunningText);
        Assert.Equal(SelfUpdatePhase.Idle, vm.Phase);
    }
}
