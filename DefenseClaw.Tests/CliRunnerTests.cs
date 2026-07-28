using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Uses cmd.exe as a stand-in binary. The real defenseclaw.exe is never invoked: these
/// tests must not touch the live install's state.
/// </summary>
public class CliRunnerTests
{
    private const string Secret = "gateway-token-do-not-log-0123456789";

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Points PATH and the bin directory at empty locations so resolution can never find
    /// the real defenseclaw.exe that happens to be installed on the dev machine.
    /// </summary>
    private static CliRunner Runner(string dataDirectory, int capacity = 200) =>
        new(
            new DefenseClawPaths(
                dataDirectory: dataDirectory,
                binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
                searchPath: Array.Empty<string>()),
            capacity);

    [Fact]
    public async Task Records_argv_exactly_as_passed()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "hello-defenseclaw" });

        Assert.Equal(new[] { "/c", "echo", "hello-defenseclaw" }, invocation.Argv);
        Assert.Equal(CmdPath, invocation.Executable);
    }

    [Fact]
    public async Task Captures_stdout()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "hello-defenseclaw" });

        var output = invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).ToList();
        Assert.Contains(output, l => l.Text.Contains("hello-defenseclaw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Captures_stderr_separately()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo oops 1>&2" });

        Assert.Contains(
            invocation.OutputLines,
            l => l.Stream == CliStream.StandardError && l.Text.Contains("oops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Records_a_zero_exit_code()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Equal(0, invocation.ExitCode);
        Assert.True(invocation.Succeeded);
        Assert.False(invocation.IsRunning);
        Assert.NotNull(invocation.FinishedAt);
        Assert.NotNull(invocation.Duration);
    }

    [Fact]
    public async Task Records_a_nonzero_exit_code()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // The real CLI exits 1 with "DefenseClaw is not initialized" when ~/.defenseclaw
        // is missing; the app keys its init-wizard prompt off exactly this shape.
        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "1" });

        Assert.Equal(1, invocation.ExitCode);
        Assert.False(invocation.Succeeded);
    }

    [Fact]
    public async Task Streams_output_lines_live()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var streamed = new List<CliOutputLine>();
        runner.OutputReceived += (_, line) => streamed.Add(line);

        await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "streamed" });

        Assert.Contains(streamed, l => l.Text.Contains("streamed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Raises_started_and_completed_events()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        CliInvocation? started = null;
        CliInvocation? completed = null;
        runner.InvocationStarted += (_, i) => started = i;
        runner.InvocationCompleted += (_, i) => completed = i;

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Same(invocation, started);
        Assert.Same(invocation, completed);
    }

    [Fact]
    public async Task Activity_log_is_newest_first_and_bounded()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path, capacity: 2);

        await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "one" });
        await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "two" });
        await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "three" });

        var activity = runner.Activity;

        Assert.Equal(2, activity.Count);
        Assert.Equal("three", activity[0].Argv[^1]);
        Assert.Equal("two", activity[1].Argv[^1]);
    }

    [Fact]
    public async Task Activity_defaults_to_the_last_200_invocations()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        Assert.Equal(200, runner.ActivityCapacity);
        await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });
        Assert.Single(runner.Activity);

        runner.ClearActivity();
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task Secret_equal_to_an_argument_is_refused()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var secret = new SecretValue(Secret);

        var ex = await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", Secret }, stdinSecret: secret));

        Assert.Equal(2, ex.ArgumentIndex);
        // The diagnostic itself must not leak the value.
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task Secret_embedded_in_an_argument_is_refused()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var secret = new SecretValue(Secret);

        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", $"--token={Secret}" }, stdinSecret: secret));
    }

    [Fact]
    public async Task Registered_secrets_are_refused_even_without_a_stdin_secret()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        runner.RegisterSecret(new SecretValue(Secret));

        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", Secret }));
    }

    [Fact]
    public async Task Secrets_are_scrubbed_out_of_captured_output()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        runner.RegisterSecret(new SecretValue("leaky-value-12345678"));

        // The secret reaches stdout without ever being an argument: the real CLI can echo
        // a token back in a diagnostic, and the Activity panel must not show it.
        var file = temp.Write("payload.txt", "prefix-leaky-value-12345678");

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        var text = string.Join('\n', invocation.OutputLines.Select(l => l.Text));
        Assert.DoesNotContain("leaky-value-12345678", text, StringComparison.Ordinal);
        Assert.Contains(SecretValue.Redacted, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_reaches_the_child_on_stdin_only()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var secret = new SecretValue("piped-through-stdin");

        // 'sort' reads stdin and echoes it back, proving the child received the value
        // without it ever appearing in argv.
        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "sort" }, stdinSecret: secret);

        Assert.True(invocation.UsedStdinSecret);
        Assert.DoesNotContain("piped-through-stdin", string.Join(' ', invocation.Argv), StringComparison.Ordinal);
        Assert.Contains(
            invocation.OutputLines,
            l => l.Text.Contains("piped-through-stdin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandLine_is_a_readable_rendering_for_the_activity_panel()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "with space" });

        Assert.Contains("/c echo \"with space\"", invocation.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executable_reports_a_failure_rather_than_throwing()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(
            Path.Combine(temp.Path, "definitely-not-here.exe"),
            new[] { "--version" });

        Assert.Null(invocation.ExitCode);
        Assert.NotNull(invocation.FailureReason);
        Assert.NotNull(invocation.FinishedAt);
    }

    [Fact]
    public async Task Unresolvable_cli_name_throws_CliNotFoundException()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var ex = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(new[] { "status" }));

        Assert.Equal("defenseclaw", ex.ExecutableName);
    }

    [Fact]
    public async Task Snapshot_is_an_independent_copy()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "snap" });
        var snapshot = invocation.Snapshot();

        Assert.NotSame(invocation, snapshot);
        Assert.Equal(invocation.ExitCode, snapshot.ExitCode);
        Assert.Equal(invocation.Argv, snapshot.Argv);
        Assert.Equal(invocation.OutputLines.Count, snapshot.OutputLines.Count);
    }
}
