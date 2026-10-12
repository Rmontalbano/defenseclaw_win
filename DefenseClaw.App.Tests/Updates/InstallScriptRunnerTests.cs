using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-346: staging install.ps1 (every verification failure blocks), and running it - with a launcher that records the start flags and starts
/// nothing, a session that records when monitoring was paused and the handles released, and a synthetic data directory.
/// </summary>
public sealed class InstallScriptRunnerTests : IDisposable
{
    private const string Script = UpgradeRunner.InstallScriptAssetName;
    private const string Installer = UpgradeRunner.InstallerAssetName;
    private const string Checksums = UpgradeRunner.ChecksumsAssetName;
    private const string Bundle = ChecksumsSignatureVerifier.BundleAssetName;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly StubHttpHandler _http = new();
    private readonly FakeCosign _cosign = new();

    public InstallScriptRunnerTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private string StagingRoot => _temp.File("staging");

    private UpgradeRunner Upgrade() => new(_services.Cli, new HttpClient(_http), StagingRoot, _cosign.VerifierOver(_http, StagingRoot));

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

    private byte[] ServeScript(bool bundle = true)
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  {Script}\n");
        if (bundle)
        {
            _http.Serve(Bundle, "{\"synthetic\":\"bundle\"}");
        }

        return script;
    }

    // ------------------------------------------------------------------ staging: what blocks the run

    [Fact]
    public async Task A_script_whose_hash_matches_and_whose_signature_cosign_verified_is_staged_as_the_install_script_channel()
    {
        var script = ServeScript();

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0", FakeCosign.Path);

        Assert.True(result.Succeeded);
        var asset = result.Asset!;
        Assert.Equal(UpgradeChannel.InstallScript, asset.Channel);
        Assert.Equal(Script, asset.AssetName);
        Assert.Equal(Sha256(script), asset.Sha256);
        Assert.True(asset.ChecksumsSignature.IsVerified);
        Assert.Equal(Path.Combine(StagingRoot, "1.0.0", Script), asset.FilePath);
        Assert.Equal(script, await File.ReadAllBytesAsync(asset.FilePath));
    }

    [Fact]
    public async Task A_signature_cosign_rejects_stages_nothing()
    {
        _ = ServeScript();
        _cosign.VerifyExitCode = 1;
        _cosign.VerifyOutput = new[] { "Error: none of the expected identities matched" };

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0", FakeCosign.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.SignatureRejected, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_hash_that_does_not_match_checksums_txt_stages_nothing()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, Bytes(20 * 1024, seed: 9)).Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0");

        Assert.Equal(UpgradeStagingOutcome.ChecksumMismatch, result.Outcome);
        Assert.Null(result.Asset);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_release_without_the_script_asset_stages_nothing()
    {
        _http.Serve(Checksums, "0000  other.txt\n");

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0");

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.DownloadFailed, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_checksums_file_with_no_line_for_the_script_stages_nothing()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Script, script).Serve(Checksums, $"{Sha256(script)}  something-else.exe\n");

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0");

        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_placeholder_stub_is_refused()
    {
        var stub = Bytes(133);
        _http.Serve(Script, stub).Serve(Checksums, $"{Sha256(stub)}  {Script}\n");

        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0");

        Assert.Equal(UpgradeStagingOutcome.StubDetected, result.Outcome);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task A_version_that_is_not_a_plain_version_requests_nothing()
    {
        var result = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0/../../evil");

        Assert.False(result.Succeeded);
        Assert.Empty(_http.Requested);
    }

    // ------------------------------------------------------------------ forcing the Setup channel at a release that has no Setup exe

    [Fact]
    public async Task The_setup_channel_against_a_release_with_no_setup_exe_says_so_plainly_and_points_at_install_ps1()
    {
        var script = Bytes(20 * 1024);
        _http.Serve(Checksums, $"{Sha256(script)}  {Script}\n");

        var result = await Upgrade().DownloadAndVerifyInstallerAsync("1.0.0");

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeStagingOutcome.ChecksumUnavailable, result.Outcome);
        Assert.Contains("ships no DefenseClawSetup-x64.exe", result.Summary, StringComparison.Ordinal);
        Assert.Contains(Script, result.Summary, StringComparison.Ordinal);
        Assert.Contains("does not ship", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("install.ps1 channel", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("not a verification failure", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Verification failed", result.Summary, StringComparison.Ordinal);

        // Nothing but the small checksums file was asked for, and nothing was written.
        Assert.Equal(new[] { Checksums }, _http.Requested);
        Assert.Empty(StagedFiles());
        Assert.DoesNotContain(Installer, _http.Requested);
    }

    // ------------------------------------------------------------------ the run

    internal sealed class Events
    {
        public List<string> Log { get; } = new();
    }

    internal sealed class FakeProcess : IInstallerProcess
    {
        private readonly int? _exit;

        public FakeProcess(int? exit) => _exit = exit;

        public int ProcessId => 4242;

        public Task<int?> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(_exit);

        public void Dispose()
        {
        }
    }

    internal sealed class FakeLauncher : IInstallerLauncher
    {
        private readonly Events _events;

        public FakeLauncher(Events events) => _events = events;

        public List<InstallerLaunchRequest> Requests { get; } = new();

        public int? Exit { get; set; }

        public Exception? Failure { get; set; }

        public Action? OnStart { get; set; }

        public IInstallerProcess Start(InstallerLaunchRequest request)
        {
            _events.Log.Add("start");
            Requests.Add(request);
            OnStart?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }

            return new FakeProcess(Exit);
        }
    }

    internal sealed class FakeSession : IUpgradeSession
    {
        private readonly Events _events;

        public FakeSession(Events events) => _events = events;

        public void Begin() => _events.Log.Add("pause+release");

        public void End() => _events.Log.Add("resume");
    }

    private sealed record Rig(InstallScriptRunner Runner, FakeLauncher Launcher, Events Events, string Staged, string Sha);

    private async Task<Rig> BuildAsync(Dictionary<string, string?>? environment = null)
    {
        var script = ServeScript();
        var staged = await Upgrade().DownloadAndVerifyInstallScriptAsync("1.0.0");
        Assert.True(staged.Succeeded);

        var events = new Events();
        var launcher = new FakeLauncher(events) { Exit = 0 };
        var runner = new InstallScriptRunner(
            _services.Cli,
            launcher,
            new FakeSession(events),
            environment: () => environment ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = @"C:\synthetic\bin" });
        return new Rig(runner, launcher, events, staged.Asset!.FilePath, Sha256(script));
    }

    private InstallScriptRunRequest Request(Rig rig, bool yes = false, bool rollback = false) =>
        new(rig.Staged, rig.Sha, "1.0.0", yes, rollback, _temp.Path);

    [Fact]
    public async Task The_run_pauses_monitoring_and_releases_handles_before_the_start_and_resumes_after_the_exit()
    {
        var rig = await BuildAsync();

        var result = await rig.Runner.RunAsync(Request(rig));

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "pause+release", "start", "resume" }, rig.Events.Log);
    }

    [Fact]
    public async Task The_installer_is_started_in_a_new_console_that_leaves_the_apps_job_with_the_reviewed_argv_and_the_staging_folder_as_its_directory()
    {
        var rig = await BuildAsync();

        _ = await rig.Runner.RunAsync(Request(rig, yes: true));

        var launch = Assert.Single(rig.Launcher.Requests);
        Assert.True(launch.NewConsole);
        Assert.True(launch.BreakAwayFromJob);
        Assert.Equal(InstallScriptPlan.BuildArgv(rig.Staged, "1.0.0", yes: true), launch.Argv);
        Assert.Equal(Path.GetDirectoryName(rig.Staged), launch.WorkingDirectory);
        Assert.EndsWith("powershell.exe", launch.Executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Secrets_are_stripped_from_the_installers_environment_and_the_rest_is_kept()
    {
        var rig = await BuildAsync(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\synthetic\bin",
            ["DEFENSECLAW_HOME"] = @"C:\synthetic\home",
            ["DEFENSECLAW_GATEWAY_TOKEN"] = "synthetic-not-a-token",
            ["OPENAI_API_KEY"] = "synthetic-not-a-key",
            ["SOME_SERVICE_SECRET"] = "synthetic",
        });

        _ = await rig.Runner.RunAsync(Request(rig));

        var environment = Assert.Single(rig.Launcher.Requests).Environment;
        Assert.Equal(@"C:\synthetic\bin", environment["PATH"]);
        Assert.Equal(@"C:\synthetic\home", environment["DEFENSECLAW_HOME"]);
        Assert.DoesNotContain("DEFENSECLAW_GATEWAY_TOKEN", environment.Keys);
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        Assert.DoesNotContain("SOME_SERVICE_SECRET", environment.Keys);
    }

    [Fact]
    public async Task A_rollback_runs_the_same_staged_script_with_rollback_and_no_version_through_the_same_path()
    {
        var rig = await BuildAsync();

        var result = await rig.Runner.RunAsync(Request(rig, rollback: true));

        var launch = Assert.Single(rig.Launcher.Requests);
        Assert.Equal(InstallScriptPlan.BuildRollbackArgv(rig.Staged), launch.Argv);
        Assert.True(launch.NewConsole);
        Assert.True(result.Rollback);
        Assert.Equal(new[] { "pause+release", "start", "resume" }, rig.Events.Log);
    }

    [Fact]
    public async Task The_run_is_recorded_in_activity_with_its_argv_exit_code_and_the_log_written_after_the_start()
    {
        var rig = await BuildAsync();
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var older = Path.Combine(logs, "install-20200101T000000.log");
        File.WriteAllText(older, "old");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow - TimeSpan.FromHours(2));
        var written = Path.Combine(logs, "install-20261012T120000.log");
        rig.Launcher.OnStart = () => File.WriteAllText(written, "synthetic transcript");
        rig.Launcher.Exit = 3;

        var result = await rig.Runner.RunAsync(Request(rig));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(written, result.LogPath);
        Assert.Equal(InstallOutcome.InstalledNeedsAttention, result.Exit.Outcome);
        var entry = Assert.Single(_services.Cli.Activity, i => i.Argv.Contains("-File"));
        Assert.Same(entry, result.Invocation);
        Assert.False(entry.IsRunning);
        Assert.Equal(3, entry.ExitCode);
        Assert.NotNull(entry.Duration);
        Assert.Contains(entry.OutputLines, l => l.Text.Contains(written, StringComparison.Ordinal));
        Assert.Equal(InstallScriptPlan.BuildArgv(rig.Staged, "1.0.0"), entry.Argv);
    }

    [Fact]
    public async Task An_installer_that_cannot_be_started_is_recorded_as_failed_and_the_app_still_resumes()
    {
        var rig = await BuildAsync();
        rig.Launcher.Failure = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");

        var result = await rig.Runner.RunAsync(Request(rig));

        Assert.False(result.Started);
        Assert.Equal(InstallOutcome.CouldNotStart, result.Exit.Outcome);
        Assert.Equal(new[] { "pause+release", "start", "resume" }, rig.Events.Log);
        var entry = Assert.Single(_services.Cli.Activity, i => i.Argv.Contains("-File"));
        Assert.False(entry.IsRunning);
        Assert.StartsWith("not started", entry.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_staged_file_changed_since_it_was_verified_is_never_started()
    {
        var rig = await BuildAsync();
        await File.AppendAllTextAsync(rig.Staged, "\n# tampered");

        var result = await rig.Runner.RunAsync(Request(rig));

        Assert.False(result.Started);
        Assert.Contains("changed on disk", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(rig.Launcher.Requests);
        Assert.Empty(rig.Events.Log);
        Assert.DoesNotContain(_services.Cli.Activity, i => i.Argv.Contains("-File"));
    }

    [Fact]
    public async Task A_version_install_ps1_would_refuse_is_never_started()
    {
        var rig = await BuildAsync();

        var result = await rig.Runner.RunAsync(Request(rig) with { Version = "1.0.0; calc" });

        Assert.False(result.Started);
        Assert.Empty(rig.Launcher.Requests);
        Assert.Empty(rig.Events.Log);
    }

    [Fact]
    public async Task Only_one_run_is_accepted_at_a_time()
    {
        var rig = await BuildAsync();
        var gate = new TaskCompletionSource<int?>();
        var blocking = new BlockingLauncher(gate.Task);
        var runner = new InstallScriptRunner(_services.Cli, blocking, new FakeSession(rig.Events), environment: () => new Dictionary<string, string?>());

        var first = runner.RunAsync(Request(rig));
        var deadline = DateTime.UtcNow + TestTimeouts.Ceiling;
        while (!blocking.Started)
        {
            Assert.True(DateTime.UtcNow < deadline, "the first run never started");
            await Task.Delay(10);
        }

        Assert.True(runner.IsRunning);

        var second = await runner.RunAsync(Request(rig));
        Assert.False(second.Started);
        Assert.Contains("already in flight", second.FailureReason, StringComparison.Ordinal);

        gate.SetResult(0);
        var done = await first;
        Assert.Equal(0, done.ExitCode);
        Assert.False(runner.IsRunning);
        Assert.Equal(1, blocking.Starts);
    }

    private sealed class BlockingLauncher : IInstallerLauncher
    {
        private readonly Task<int?> _exit;
        private int _starts;

        public BlockingLauncher(Task<int?> exit) => _exit = exit;

        public bool Started => Volatile.Read(ref _starts) > 0;

        public int Starts => Volatile.Read(ref _starts);

        public IInstallerProcess Start(InstallerLaunchRequest request)
        {
            _ = Interlocked.Increment(ref _starts);
            return new Waiting(_exit);
        }

        private sealed class Waiting : IInstallerProcess
        {
            private readonly Task<int?> _exit;

            public Waiting(Task<int?> exit) => _exit = exit;

            public int ProcessId => 1;

            public Task<int?> WaitForExitAsync(CancellationToken cancellationToken) => _exit;

            public void Dispose()
            {
            }
        }
    }

    // ------------------------------------------------------------------ the app's own session

    [Fact]
    public void The_apps_session_pauses_monitoring_it_found_running_and_resumes_it_but_leaves_the_operators_own_pause_alone()
    {
        var session = new AppUpgradeSession(_services);

        Assert.False(_services.Monitor.IsPaused);
        session.Begin();
        Assert.True(_services.Monitor.IsPaused);
        session.End();
        Assert.False(_services.Monitor.IsPaused);

        _ = _services.Monitor.SetPaused(true);
        var second = new AppUpgradeSession(_services);
        second.Begin();
        second.End();
        Assert.True(_services.Monitor.IsPaused);
        _ = _services.Monitor.SetPaused(false);
    }

    [Fact]
    public void The_apps_session_gives_back_the_pooled_handles_on_the_data_directorys_databases()
    {
        var db = Path.Combine(_temp.Path, "audit.db");
        using (var create = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            create.Open();
            using var command = create.CreateCommand();
            command.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);";
            _ = command.ExecuteNonQuery();
        }

        // What every reader does: a pooled read-only connection, closed and so kept open by the pool.
        using (var read = new SqliteConnection(DefenseClaw.Core.Audit.AuditReader.BuildReadOnlyConnectionString(db)))
        {
            read.Open();
            using var command = read.CreateCommand();
            command.CommandText = "SELECT count(*) FROM t";
            _ = command.ExecuteScalar();
        }

        Assert.Throws<IOException>(() => File.Delete(db));

        var session = new AppUpgradeSession(_services);
        session.Begin();
        try
        {
            File.Move(db, db + ".moved");
        }
        finally
        {
            session.End();
        }

        Assert.True(File.Exists(db + ".moved"));
    }
}
