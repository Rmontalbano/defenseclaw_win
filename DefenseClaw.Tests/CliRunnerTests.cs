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

    // ----------------------------------------------------------------------------------
    // Retained-output cap. The activity ring is bounded by entry count only, so without a
    // second cap here one chatty command pins its whole transcript for the life of the app.
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="lineCount"/> distinguishable lines and has cmd type them back,
    /// which is the cheapest way to drive a real capture path with a large transcript.
    /// </summary>
    private static string TranscriptFile(TempDirectory temp, int lineCount, int width = 0)
    {
        var padding = width > 0 ? new string('x', width) : string.Empty;
        var text = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, lineCount).Select(i => $"line-{i:00000}{padding}"));

        return temp.Write("transcript.txt", text);
    }

    [Fact]
    public async Task Chatty_output_is_capped_and_the_oldest_lines_are_dropped()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 3_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        var lines = invocation.OutputLines;

        // Retained is bounded, and the tail — where the diagnosis lives — is what survives.
        Assert.True(invocation.IsOutputTruncated);
        Assert.True(invocation.DroppedOutputLineCount > 0);
        Assert.True(
            lines.Count <= CliInvocation.MaxRetainedOutputLines + 1,
            $"retained {lines.Count} lines, cap is {CliInvocation.MaxRetainedOutputLines} (+1 marker)");
        Assert.Contains(lines, l => l.Text.Contains("line-03000", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Text.Contains("line-00001", StringComparison.Ordinal));

        // Every line ever captured is still accounted for, dropped or retained. (>= rather
        // than ==: whether `type` emits a final newline for a file that lacks one is a cmd
        // detail, not something this cap should be pinned to.)
        Assert.True(invocation.OutputCursor >= 3_000);
        Assert.Equal(
            invocation.OutputCursor,
            invocation.DroppedOutputLineCount + lines.Count - 1);
    }

    [Fact]
    public async Task Dropping_output_emits_an_explicit_truncation_marker()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 3_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        // Honest reporting: output is never dropped silently, and the marker says how much.
        var marker = invocation.OutputLines[0];
        Assert.Equal(CliStream.Notice, marker.Stream);
        Assert.Contains("output truncated", marker.Text, StringComparison.Ordinal);
        Assert.Contains($"{invocation.DroppedOutputLineCount} earlier", marker.Text, StringComparison.Ordinal);

        // The marker is a notice, not a fabricated stdout/stderr line: the call sites that
        // reduce an invocation to a user-facing error by filtering on a stream must not pick
        // it up, and the ones that concatenate stdout and parse it must not see it either.
        Assert.DoesNotContain(
            invocation.OutputLines.Where(l => l.Stream is CliStream.StandardOutput or CliStream.StandardError),
            l => l.Text.Contains("output truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_byte_cap_binds_before_the_line_cap_on_wide_output()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // 200 lines is far below the line cap, but 2 KB per line blows the byte budget —
        // which is the case a line-count-only cap would miss entirely.
        var file = TranscriptFile(temp, lineCount: 200, width: 2_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        Assert.True(invocation.IsOutputTruncated);
        Assert.True(
            invocation.OutputLines.Count < 200,
            "the byte cap should have trimmed well before 200 lines were retained");
        Assert.Contains(
            invocation.OutputLines,
            l => l.Text.Contains("line-00200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncation_never_touches_argv_the_exit_code_or_the_failure_reason()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 3_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        Assert.True(invocation.IsOutputTruncated);
        Assert.Equal(0, invocation.ExitCode);
        Assert.True(invocation.Succeeded);
        Assert.Null(invocation.FailureReason);
        Assert.Equal(new[] { "/c", "type", file }, invocation.Argv);
        Assert.NotNull(invocation.Duration);
    }

    [Fact]
    public async Task Snapshot_carries_truncation_state_with_it()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 3_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        var snapshot = invocation.Snapshot();

        Assert.True(snapshot.IsOutputTruncated);
        Assert.Equal(invocation.DroppedOutputLineCount, snapshot.DroppedOutputLineCount);
        Assert.Equal(invocation.OutputCursor, snapshot.OutputCursor);
        Assert.Equal(invocation.OutputLines.Count, snapshot.OutputLines.Count);
        Assert.Equal(CliStream.Notice, snapshot.OutputLines[0].Stream);
    }

    // ----------------------------------------------------------------------------------
    // Incremental reads. Live consoles poll several times a second; they must be able to
    // pull only what arrived since their last read rather than re-copying the transcript.
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task CopyNewLines_from_zero_returns_the_whole_transcript()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 50);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        var drained = new List<CliOutputLine>();
        var cursor = invocation.CopyNewLines(0, drained);

        Assert.False(invocation.IsOutputTruncated);
        Assert.Equal(invocation.OutputCursor, cursor);
        Assert.Equal(invocation.OutputLines.Select(l => l.Text), drained.Select(l => l.Text));
    }

    [Fact]
    public async Task CopyNewLines_returns_nothing_when_the_cursor_is_already_current()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "once" });

        var drained = new List<CliOutputLine>();
        var cursor = invocation.CopyNewLines(0, drained);
        var firstPass = drained.Count;
        Assert.True(firstPass > 0);

        // A second tick with no new output must add nothing — the bug this API replaces was
        // a console that re-read (and could re-append) lines it had already shown.
        drained.Clear();
        var second = invocation.CopyNewLines(cursor, drained);

        Assert.Empty(drained);
        Assert.Equal(cursor, second);

        // Defensive: a cursor from some other invocation must not replay history either.
        drained.Clear();
        invocation.CopyNewLines(cursor + 5_000, drained);
        Assert.Empty(drained);
    }

    [Fact]
    public async Task CopyNewLines_tells_a_lagging_reader_exactly_what_it_missed()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 3_000);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        Assert.True(invocation.IsOutputTruncated);

        // A reader still holding cursor 0 fell behind a trim. It is told, rather than
        // silently handed a transcript that starts in the middle of the run.
        var drained = new List<CliOutputLine>();
        invocation.CopyNewLines(0, drained);

        Assert.Equal(CliStream.Notice, drained[0].Stream);
        Assert.Contains("output truncated", drained[0].Text, StringComparison.Ordinal);
        Assert.Contains($"{invocation.DroppedOutputLineCount} earlier", drained[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(drained, l => l.Text.Contains("line-00001", StringComparison.Ordinal));
        Assert.Contains(drained, l => l.Text.Contains("line-03000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incremental_reads_during_a_live_run_lose_and_duplicate_nothing()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 400);

        // The runner only hands the invocation back when the process exits, so grab the live
        // instance the way the panels do — the whole point is reading it while it mutates.
        CliInvocation? live = null;
        runner.InvocationStarted += (_, i) => live = i;

        var run = runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        // Poll on this thread while the capture callbacks append on theirs.
        var drained = new List<CliOutputLine>();
        var buffer = new List<CliOutputLine>();
        var cursor = 0;
        while (!run.IsCompleted)
        {
            if (live is { } invocation)
            {
                buffer.Clear();
                cursor = invocation.CopyNewLines(cursor, buffer);
                drained.AddRange(buffer);
            }

            await Task.Yield();
        }

        var finished = await run;
        buffer.Clear();
        finished.CopyNewLines(cursor, buffer);
        drained.AddRange(buffer);

        Assert.False(finished.IsOutputTruncated);
        Assert.Equal(finished.OutputCursor, drained.Count);
        Assert.Equal(finished.OutputLines.Select(l => l.Text), drained.Select(l => l.Text));

        // No line arrived twice, however the ticks happened to interleave with the appends.
        var payload = drained.Where(l => l.Text.StartsWith("line-", StringComparison.Ordinal)).ToList();
        Assert.Equal(payload.Count, payload.Select(l => l.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Incremental_reads_are_safe_from_a_second_thread()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 400);

        CliInvocation? live = null;
        runner.InvocationStarted += (_, i) => live = i;

        using var stop = new CancellationTokenSource();
        var drained = new List<CliOutputLine>();
        var cursor = 0;

        // Drains on a pool thread with no coordination beyond the invocation's own lock,
        // concurrently with the stdout callback thread appending into it.
        var reader = Task.Run(
            async () =>
            {
                var buffer = new List<CliOutputLine>();
                while (!stop.IsCancellationRequested)
                {
                    if (live is { } invocation)
                    {
                        buffer.Clear();
                        cursor = invocation.CopyNewLines(cursor, buffer);
                        drained.AddRange(buffer);
                    }

                    await Task.Yield();
                }
            },
            CancellationToken.None);

        var finished = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        stop.Cancel();
        await reader;

        var tail = new List<CliOutputLine>();
        finished.CopyNewLines(cursor, tail);
        drained.AddRange(tail);

        Assert.Equal(finished.OutputCursor, drained.Count);
        Assert.Equal(finished.OutputLines.Select(l => l.Text), drained.Select(l => l.Text));
    }
}
