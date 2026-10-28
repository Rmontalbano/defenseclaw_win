using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// While a runtime upgrade runs (<see cref="CliRunner.EnterUpgradeRun"/>) the runner refuses every run that is not a read, on a writable
/// installation too, with <see cref="CliRunner.UpgradeInProgressReason"/> (the Mac's <c>runtimeUpgradeRunID</c> guard); reads still run, and the
/// refusal ends with the scope. Children here are copies of <c>cmd.exe</c>; every value is synthetic.
/// </summary>
public sealed class UpgradeRunGuardTests
{
    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CliRunner Runner(TempDirectory temp) =>
        new(
            new DefenseClawPaths(
                dataDirectory: temp.Path,
                binDirectory: Path.Combine(temp.Path, "no-such-bin"),
                searchPath: Array.Empty<string>()),
            neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"),
            installation: () => InstallationContext.Unmanaged(@"C:\data\.defenseclaw"));

    [Fact]
    public async Task A_change_is_refused_while_an_upgrade_runs_and_allowed_again_afterwards()
    {
        using var temp = new TempDirectory();
        var marker = temp.File("ran.txt");
        var runner = Runner(temp);

        var scope = runner.EnterUpgradeRun();
        Assert.True(runner.IsUpgradeRunning);
        var refused = await runner.RunExecutableAsync(CmdPath, ["/c", "echo", "x", ">", marker]);

        Assert.Null(refused.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix, refused.FailureReason, StringComparison.Ordinal);
        Assert.Contains(CliRunner.UpgradeInProgressReason, refused.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));

        scope.Dispose();
        Assert.False(runner.IsUpgradeRunning);
        var allowed = await runner.RunExecutableAsync(CmdPath, ["/c", "echo", "x", ">", marker]);
        Assert.Equal(0, allowed.ExitCode);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Reads_still_run_while_an_upgrade_runs()
    {
        using var temp = new TempDirectory();
        var fake = Path.Combine(temp.Path, "defenseclaw.exe");
        File.Copy(CmdPath, fake);
        var runner = Runner(temp);

        using var scope = runner.EnterUpgradeRun();
        var read = await runner.RunExecutableAsync(fake, ["/c", "echo", "skill", "list", "--dry-run"]);

        Assert.Equal(0, read.ExitCode);
    }

    [Fact]
    public void Nested_scopes_end_the_guard_only_when_the_last_closes_and_the_event_fires_on_each_edge()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var raised = 0;
        runner.UpgradeRunChanged += (_, _) => raised++;

        var first = runner.EnterUpgradeRun();
        var second = runner.EnterUpgradeRun();
        first.Dispose();
        first.Dispose();
        Assert.True(runner.IsUpgradeRunning);
        second.Dispose();

        Assert.False(runner.IsUpgradeRunning);
        Assert.Equal(2, raised);
    }
}
