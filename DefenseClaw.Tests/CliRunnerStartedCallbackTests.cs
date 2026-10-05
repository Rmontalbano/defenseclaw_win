using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="CliRunOptions.OnProcessStarted"/>: the moment a caller that held something open for the launch lets go. cmd.exe is the stand-in program,
/// as in the rest of the runner's tests; the real defenseclaw is never invoked.
/// </summary>
public sealed class CliRunnerStartedCallbackTests
{
    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    [Fact]
    public async Task The_callback_runs_once_after_the_child_has_started_and_before_the_run_is_over()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var calls = 0;
        bool? stillRunningInside = null;
        CliInvocation? recorded = null;
        runner.InvocationStarted += (_, invocation) => recorded = invocation;

        var options = CliRunOptions.Default with
        {
            OnProcessStarted = () =>
            {
                calls++;
                stillRunningInside = recorded is { IsRunning: true, ExitCode: null };
            },
        };

        // A child that lives for a second or two, so "before the run is over" is not a race with a program that finishes at once.
        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "ping -n 3 127.0.0.1 >nul" }, options: options);

        Assert.Equal(1, calls);
        Assert.True(stillRunningInside);
        Assert.Equal(0, invocation.ExitCode);
    }

    [Fact]
    public async Task The_callback_is_not_called_when_the_child_could_not_be_started()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var calls = 0;

        var invocation = await runner.RunExecutableAsync(
            Path.Combine(temp.Path, "no-such-program.exe"),
            Array.Empty<string>(),
            options: CliRunOptions.Default with { OnProcessStarted = () => calls++ });

        Assert.Equal(0, calls);
        Assert.Null(invocation.ExitCode);
        Assert.NotNull(invocation.FailureReason);
    }

    [Fact]
    public async Task A_callback_that_throws_cannot_fail_or_orphan_the_run()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "exit", "7" },
            options: CliRunOptions.Default with { OnProcessStarted = () => throw new InvalidOperationException("a bug in the caller") });

        Assert.Equal(7, invocation.ExitCode);
        Assert.Null(invocation.FailureReason);
    }

    [Fact]
    public async Task The_installer_preset_keeps_its_timeout_and_survival_when_a_callback_is_added()
    {
        var options = CliRunOptions.Installer with { OnProcessStarted = () => { } };

        Assert.True(options.SurvivesShutdown);
        Assert.Equal(Timeout.InfiniteTimeSpan, options.Timeout);
        Assert.NotNull(options.OnProcessStarted);
        Assert.Null(CliRunOptions.Installer.OnProcessStarted);

        // And a run with them behaves like any other.
        using var temp = new TempDirectory();
        var invocation = await Runner(temp.Path).RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" }, options: options);
        Assert.Equal(0, invocation.ExitCode);
    }

    [Fact]
    public async Task A_run_with_no_callback_is_unchanged()
    {
        using var temp = new TempDirectory();

        var invocation = await Runner(temp.Path).RunExecutableAsync(CmdPath, new[] { "/c", "exit", "3" });

        Assert.Equal(3, invocation.ExitCode);
    }
}
