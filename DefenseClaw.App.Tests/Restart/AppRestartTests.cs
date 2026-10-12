using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Restart;

/// <summary>"Restart this app": argument handling, the wait-then-guard order, the refusal and the command line. Nothing here starts a process.</summary>
public sealed class AppRestartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcw-restart-" + Guid.NewGuid().ToString("N"));
    private readonly CliRunner _cli;

    public AppRestartTests()
    {
        _ = Directory.CreateDirectory(_dir);
        _cli = new CliRunner(TestServices.IsolatedPaths(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeLauncher : IAppProcessLauncher
    {
        public List<(string Exe, string[] Args)> Started { get; } = new();

        public Exception? Throw { get; set; }

        public void Start(string executable, IReadOnlyList<string> arguments)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Started.Add((executable, arguments.ToArray()));
        }
    }

    private AppRestart Create(FakeLauncher launcher) =>
        new(() => _cli.Activity, launcher, () => @"C:\fake\DefenseClaw.App.exe", () => 4242);

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("7", 7)]
    public void A_plain_pid_after_the_flag_is_read(string value, int expected) =>
        Assert.Equal(expected, AppRestart.ParseAfterRestart(new[] { "--minimized", "--after-restart", value }));

    [Fact]
    public void The_flag_is_matched_case_insensitively() =>
        Assert.Equal(9, AppRestart.ParseAfterRestart(new[] { "--AFTER-RESTART", "9" }));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("0x10")]
    [InlineData("1e3")]
    [InlineData("99999999999")]
    public void Anything_but_a_positive_integer_is_ignored(string value) =>
        Assert.Null(AppRestart.ParseAfterRestart(new[] { "--after-restart", value }));

    [Fact]
    public void A_missing_flag_or_value_is_ignored()
    {
        Assert.Null(AppRestart.ParseAfterRestart(Array.Empty<string>()));
        Assert.Null(AppRestart.ParseAfterRestart(new[] { "--minimized" }));
        Assert.Null(AppRestart.ParseAfterRestart(new[] { "--after-restart" }));
    }

    [Fact]
    public void The_wait_happens_before_the_guard_is_taken()
    {
        var events = new List<string>();
        var result = AppRestart.AcquireAfterRestart(
            new[] { "--after-restart", "321" },
            (pid, bound) =>
            {
                events.Add($"wait {pid} {bound.TotalSeconds}");
                return true;
            },
            () =>
            {
                events.Add("guard");
                return "guard-object";
            });

        Assert.Equal("guard-object", result);
        Assert.Equal(new[] { "wait 321 20", "guard" }, events);
    }

    [Fact]
    public void A_predecessor_that_outlives_the_bound_still_falls_through_to_the_guard()
    {
        var events = new List<string>();
        _ = AppRestart.AcquireAfterRestart(
            new[] { "--after-restart", "321" },
            (_, _) =>
            {
                events.Add("wait");
                return false;
            },
            () =>
            {
                events.Add("guard");
                return 0;
            });

        Assert.Equal(new[] { "wait", "guard" }, events);
    }

    [Fact]
    public void An_ordinary_launch_does_not_wait()
    {
        var waited = false;
        _ = AppRestart.AcquireAfterRestart(new[] { "--minimized", "--after-restart", "junk" }, (_, _) => waited = true, () => 0);
        Assert.False(waited);
    }

    [Fact]
    public void Waiting_for_a_process_that_does_not_exist_or_for_oneself_returns_at_once()
    {
        Assert.True(AppRestart.WaitForProcessExit(Environment.ProcessId, TimeSpan.FromMilliseconds(1)));
        Assert.True(AppRestart.WaitForProcessExit(int.MaxValue - 1, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void A_normal_restart_launches_without_minimized_and_names_this_process()
    {
        var launcher = new FakeLauncher();
        var restart = Create(launcher);

        Assert.True(restart.TryLaunch(out var error));
        Assert.Null(error);
        var started = Assert.Single(launcher.Started);
        Assert.Equal(@"C:\fake\DefenseClaw.App.exe", started.Exe);
        Assert.Equal(new[] { "--after-restart", "4242" }, started.Args);
    }

    [Fact]
    public void A_tray_only_session_stays_in_the_tray()
    {
        var launcher = new FakeLauncher();
        var restart = Create(launcher);
        restart.StartedMinimized = true;
        restart.WindowShown = () => false;

        Assert.True(restart.TryLaunch(out _));
        Assert.Equal(new[] { "--minimized", "--after-restart", "4242" }, launcher.Started[0].Args);
    }

    [Fact]
    public void A_minimized_start_with_the_window_now_open_comes_back_with_its_window()
    {
        var launcher = new FakeLauncher();
        var restart = Create(launcher);
        restart.StartedMinimized = true;
        restart.WindowShown = () => true;

        Assert.True(restart.TryLaunch(out _));
        Assert.Equal(new[] { "--after-restart", "4242" }, launcher.Started[0].Args);
    }

    [Fact]
    public void A_running_state_changing_command_refuses_and_starts_nothing()
    {
        var launcher = new FakeLauncher();
        var restart = Create(launcher);
        var raised = 0;
        restart.RestartRequested += (_, _) => raised++;

        var run = _cli.BeginExternalRun("powershell.exe", new[] { "-File", "install.ps1" }, "running");
        Assert.NotNull(restart.BlockedReason);
        Assert.Equal(restart.BlockedReason, restart.Request());
        Assert.False(restart.TryLaunch(out var error));
        Assert.Equal(restart.BlockedReason, error);
        Assert.Empty(launcher.Started);
        Assert.Equal(0, raised);

        _cli.CompleteExternalRun(run, 0, null, "done");
        Assert.Null(restart.BlockedReason);
        Assert.Null(restart.Request());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_running_read_does_not_block()
    {
        var restart = Create(new FakeLauncher());
        var run = _cli.BeginExternalRun("defenseclaw", new[] { "status" }, "reading");
        Assert.Null(restart.BlockedReason);
        _cli.CompleteExternalRun(run, 0, null, null);
    }

    [Fact]
    public void A_request_nobody_handles_says_so()
    {
        Assert.NotNull(Create(new FakeLauncher()).Request());
    }

    [Fact]
    public void A_launcher_failure_is_reported_not_thrown()
    {
        var launcher = new FakeLauncher { Throw = new Win32Exception(5, "denied") };
        var restart = Create(launcher);

        Assert.False(restart.TryLaunch(out var error));
        Assert.Contains("denied", error);
    }

    [Fact]
    public void Without_a_program_path_nothing_is_started()
    {
        var launcher = new FakeLauncher();
        var restart = new AppRestart(() => _cli.Activity, launcher, () => null, () => 1);

        Assert.False(restart.TryLaunch(out var error));
        Assert.NotNull(error);
        Assert.Empty(launcher.Started);
    }
}
