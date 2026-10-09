using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary><see cref="CliRunner.RecordRefusal"/> (CUST-312): the Activity entry for a command this app confirmed and then declined to start.</summary>
public sealed class CliRunnerRefusalTests
{
    private const string Reason = "Changes are off: config.yaml or .env changed after this list was read. Refresh to act on current data.";

    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    [Fact]
    public void A_refusal_is_one_finished_entry_that_never_ran_and_says_why()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = 0;
        var completed = 0;
        runner.InvocationStarted += (_, _) => started++;
        runner.InvocationCompleted += (_, _) => completed++;

        var entry = runner.RecordRefusal("defenseclaw", new[] { "skill", "block", "--", "example-skill" }, Reason);

        Assert.Same(entry, Assert.Single(runner.Activity));
        Assert.Equal("defenseclaw", entry.Executable);
        Assert.Equal(new[] { "skill", "block", "--", "example-skill" }, entry.Argv);
        Assert.False(entry.IsRunning);
        Assert.NotNull(entry.FinishedAt);
        Assert.Null(entry.ExitCode); // nothing ran, so there is no exit code to report
        Assert.False(entry.Succeeded);
        Assert.Equal(CliRunner.RefusedPrefix + " — " + Reason, entry.FailureReason);

        var line = Assert.Single(entry.OutputLines);
        Assert.Equal(CliStream.Notice, line.Stream);
        Assert.Contains("nothing was run and nothing was changed", line.Text, StringComparison.Ordinal);
        Assert.EndsWith(Reason, line.Text, StringComparison.Ordinal);
        Assert.Equal((1, 1), (started, completed));
    }

    [Fact]
    public void A_refusal_goes_to_the_top_of_activity_like_any_other_run()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var first = runner.RecordHandOff("defenseclaw", new[] { "keys", "set", "EXAMPLE_KEY" }, "opened in a console");

        var refused = runner.RecordRefusal("defenseclaw", new[] { "mcp", "block", "--", "example" }, Reason);

        Assert.Equal(new[] { refused, first }, runner.Activity.ToArray());
    }

    [Fact]
    public void A_known_secret_on_the_argv_is_refused_and_nothing_is_recorded()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        runner.RegisterSecret(new SecretValue("leaky-value-12345678"));

        _ = Assert.Throws<SecretInArgumentException>(
            () => runner.RecordRefusal("defenseclaw", new[] { "skill", "block", "--reason", "leaky-value-12345678" }, Reason));

        Assert.Empty(runner.Activity);
    }

    [Fact]
    public void A_refusal_needs_a_reason_and_a_command()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        _ = Assert.Throws<ArgumentException>(() => runner.RecordRefusal("defenseclaw", new[] { "skill", "block" }, " "));
        _ = Assert.Throws<ArgumentException>(() => runner.RecordRefusal(string.Empty, new[] { "skill", "block" }, Reason));
        _ = Assert.Throws<ArgumentNullException>(() => runner.RecordRefusal("defenseclaw", null!, Reason));
        Assert.Empty(runner.Activity);
    }
}
