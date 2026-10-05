using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary><see cref="CliRunner.RecordHandOff"/>: the Activity entry for a command the operator runs in a console of their own. Nothing is started.</summary>
public sealed class CliRunnerHandOffTests
{
    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    [Fact]
    public void A_hand_off_is_one_finished_entry_with_its_note_and_no_exit_code()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = 0;
        var completed = 0;
        runner.InvocationStarted += (_, _) => started++;
        runner.InvocationCompleted += (_, _) => completed++;

        var entry = runner.RecordHandOff(@"C:\Tools\defenseclaw.exe", new[] { "keys", "set", "EXAMPLE_KEY" }, "opened in a console");

        Assert.Same(entry, Assert.Single(runner.Activity));
        Assert.Equal(new[] { "keys", "set", "EXAMPLE_KEY" }, entry.Argv);
        Assert.False(entry.IsRunning);
        Assert.Null(entry.ExitCode);
        Assert.Null(entry.FailureReason);
        Assert.Equal("opened in a console", Assert.Single(entry.OutputLines).Text);
        Assert.Equal((1, 1), (started, completed));
    }

    [Fact]
    public void A_known_secret_on_the_argv_is_refused_and_nothing_is_recorded()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        runner.RegisterSecret(new SecretValue("leaky-value-12345678"));

        _ = Assert.Throws<SecretInArgumentException>(
            () => runner.RecordHandOff(@"C:\Tools\defenseclaw.exe", new[] { "keys", "set", "leaky-value-12345678" }, "note"));

        Assert.Empty(runner.Activity);
    }
}
