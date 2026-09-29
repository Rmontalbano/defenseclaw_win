using System.Diagnostics;
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

        // 'sort' reads stdin and echoes it back. The echo is scrubbed on the way into the
        // transcript, so a redacted line is the proof that the child received the value —
        // without it ever appearing in argv.
        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "sort" }, stdinSecret: secret);

        Assert.True(invocation.UsedStdinSecret);
        Assert.DoesNotContain("piped-through-stdin", string.Join(' ', invocation.Argv), StringComparison.Ordinal);
        Assert.Contains(
            invocation.OutputLines,
            l => l.Text.Contains(SecretValue.Redacted, StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------------------------
    // The per-call stdin secret is scrubbed from that run's output. Scrub used to consult only
    // RegisterSecret'd values, so a child that echoed its stdin (a CLI reporting "invalid key:
    // <key>") put the secret in the Activity panel, in the wizard console and in OutputReceived
    // — the exact places the wizard UI promises it never appears.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("sort")]
    [InlineData("sort 1>&2")]
    public async Task The_stdin_secret_is_scrubbed_from_output_the_child_echoes(string shellCommand)
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        const string Echoed = "echoed-back-secret-987654";
        var streamed = new List<CliOutputLine>();
        runner.OutputReceived += (_, line) =>
        {
            lock (streamed)
            {
                streamed.Add(line);
            }
        };

        // `sort` (to stdout) and `sort 1>&2` (to stderr) both write back exactly what they read.
        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", shellCommand },
            stdinSecret: new SecretValue(Echoed));

        Assert.True(invocation.Succeeded);

        var retained = string.Join('\n', invocation.OutputLines.Select(l => l.Text));
        Assert.DoesNotContain(Echoed, retained, StringComparison.Ordinal);
        Assert.Contains(SecretValue.Redacted, retained, StringComparison.Ordinal);

        // Nor through the other two ways to read it: the live stream and a snapshot copy.
        lock (streamed)
        {
            Assert.DoesNotContain(streamed, l => l.Text.Contains(Echoed, StringComparison.Ordinal));
            Assert.Contains(streamed, l => l.Text.Contains(SecretValue.Redacted, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            invocation.Snapshot().OutputLines,
            l => l.Text.Contains(Echoed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registered_and_per_call_secrets_are_both_scrubbed_from_one_run()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        runner.RegisterSecret(new SecretValue("registered-secret-13579"));

        // `type` prints a registered secret wrapped in a message (a CLI rarely echoes a bare
        // value); `sort` then echoes the per-call secret it was piped. Both must be redacted,
        // and the surrounding text must survive.
        var file = temp.Write("wrapped.txt", "invalid key: registered-secret-13579 (rejected)\r\n");
        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", $"type {file} & sort" },
            stdinSecret: new SecretValue("call-secret-24680"));

        var lines = invocation.OutputLines.Select(l => l.Text).ToList();
        Assert.Contains($"invalid key: {SecretValue.Redacted} (rejected)", lines);
        Assert.Contains(SecretValue.Redacted, lines);
        Assert.DoesNotContain(lines, l => l.Contains("registered-secret-13579", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("call-secret-24680", StringComparison.Ordinal));
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

    // ----------------------------------------------------------------------------------
    // Process lifecycle: timeout, cancellation and app-exit shutdown. A hung child used to
    // wedge whatever awaited it, and nothing ever killed a child when the app quit.
    //
    // The stand-in for "a child that will not exit" is `cmd /c ping -n 30 127.0.0.1`: about
    // 29 s if left alone, and — importantly — a real tree. cmd is the runner's child and
    // ping is cmd's, so a kill that stops at the direct child leaves ping running, which the
    // tree tests detect by watching for the ping process itself.
    // ----------------------------------------------------------------------------------

    private static readonly string[] LongPing = { "/c", "ping", "-n", "30", "127.0.0.1" };

    private static readonly string DefenseClawCli = Path.Combine("C:\\", "bin", "defenseclaw.exe");

    private static readonly string GatewayCli = Path.Combine("C:\\", "bin", "defenseclaw-gateway.exe");

    /// <summary>Ids of every ping.exe running right now. Disposes the Process objects it opens.</summary>
    private static HashSet<int> PingPids()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("ping"))
        {
            using (process)
            {
                _ = ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id: it is gone.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Last-resort cleanup so a failing assertion never leaves a 30-second ping behind.</summary>
    private static void KillIfAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill; either way there is nothing left to clean up.
        }
    }

    /// <summary>
    /// Waits for the ping grandchild of a run in flight to appear and returns its id — which is
    /// also the signal that the whole tree is up, so a test can act on a live child rather than
    /// racing process start-up.
    /// </summary>
    private static async Task<int> WaitForNewPingAsync(HashSet<int> known, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !run.IsCompleted)
        {
            foreach (var pid in PingPids())
            {
                if (!known.Contains(pid))
                {
                    return pid;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail("No ping grandchild appeared before the run ended.");
        return 0;
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return !IsAlive(pid);
    }

    [Fact]
    public async Task A_hung_child_is_killed_at_the_timeout_and_the_reason_is_recorded()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var completedEvents = 0;
        runner.InvocationCompleted += (_, _) => Interlocked.Increment(ref completedEvents);

        var stopwatch = Stopwatch.StartNew();
        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            LongPing,
            options: CliRunOptions.WithTimeout(TimeSpan.FromSeconds(2)));
        stopwatch.Stop();

        // Timeout plus generous slack — and nowhere near the ~29 s the ping would have taken.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"the run took {stopwatch.Elapsed.TotalSeconds:0.0} s; a 2 s timeout should have ended it");
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5),
            $"the run ended after {stopwatch.Elapsed.TotalSeconds:0.0} s, before its 2 s timeout");

        Assert.Contains("timed out after 2 s", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Null(invocation.ExitCode);
        Assert.False(invocation.Succeeded);

        // Never left "running" in the Activity panel, and completed exactly once.
        Assert.False(invocation.IsRunning);
        Assert.NotNull(invocation.FinishedAt);
        Assert.False(runner.Activity[0].IsRunning);
        Assert.Equal(1, Volatile.Read(ref completedEvents));
    }

    [Fact]
    public async Task Timeout_kills_the_grandchild_too()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var known = PingPids();

        var run = runner.RunExecutableAsync(
            CmdPath,
            LongPing,
            options: CliRunOptions.WithTimeout(TimeSpan.FromSeconds(3)));
        var grandchild = await WaitForNewPingAsync(known, run);

        try
        {
            var invocation = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Contains("timed out", invocation.FailureReason, StringComparison.Ordinal);
            Assert.True(
                await WaitUntilGoneAsync(grandchild, TimeSpan.FromSeconds(5)),
                "ping, the grandchild, survived the kill: only the direct child was terminated");
        }
        finally
        {
            KillIfAlive(grandchild);
        }
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_whole_tree_and_records_cancelled()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var known = PingPids();
        using var cts = new CancellationTokenSource();

        var run = runner.RunExecutableAsync(CmdPath, LongPing, cancellationToken: cts.Token);
        var grandchild = await WaitForNewPingAsync(known, run);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            cts.Cancel();
            var invocation = await run.WaitAsync(TimeSpan.FromSeconds(20));
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"cancelling took {stopwatch.Elapsed.TotalSeconds:0.0} s to end the run");
            Assert.StartsWith("cancelled", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Null(invocation.ExitCode);
            Assert.False(invocation.Succeeded);
            Assert.False(invocation.IsRunning);
            Assert.NotNull(invocation.FinishedAt);

            Assert.True(
                await WaitUntilGoneAsync(grandchild, TimeSpan.FromSeconds(5)),
                "ping, the grandchild, survived cancellation");
        }
        finally
        {
            KillIfAlive(grandchild);
        }
    }

    [Fact]
    public async Task Cancelling_while_the_stdin_secret_is_still_being_written_still_ends_the_run_cleanly()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var known = PingPids();
        var completedEvents = 0;
        runner.InvocationCompleted += (_, _) => Interlocked.Increment(ref completedEvents);
        using var cts = new CancellationTokenSource();

        // `ping` never reads stdin, so a secret far larger than the pipe buffer leaves the runner
        // parked inside StreamWriter.WriteAsync — the window in which a cancellation surfaces as an
        // OperationCanceledException from the stdin write rather than from WaitForExit. That
        // exception used to be able to escape the run: child left alive, invocation never
        // finished, InvocationCompleted never raised (so a caller counting in-flight runs, like the
        // Updates window, counted one forever).
        var secret = new SecretValue(new string('s', 4 * 1024 * 1024));
        var run = runner.RunExecutableAsync(CmdPath, LongPing, secret, cts.Token);
        var grandchild = await WaitForNewPingAsync(known, run);

        try
        {
            await Task.Delay(300); // long enough for the write to have filled the pipe and blocked
            Assert.False(run.IsCompleted, "the run ended before it was cancelled");

            cts.Cancel();
            var invocation = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.StartsWith("cancelled", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Null(invocation.ExitCode);
            Assert.False(invocation.IsRunning);
            Assert.NotNull(invocation.FinishedAt);
            Assert.Equal(1, Volatile.Read(ref completedEvents));
            Assert.True(
                await WaitUntilGoneAsync(grandchild, TimeSpan.FromSeconds(5)),
                "ping, the grandchild, survived a cancellation that arrived during the stdin write");
        }
        finally
        {
            KillIfAlive(grandchild);
        }
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_never_launches_the_child()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var invocation = await runner.RunExecutableAsync(CmdPath, LongPing, cancellationToken: cts.Token);

        Assert.StartsWith("cancelled", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Contains("nothing was started", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Null(invocation.ExitCode);
        Assert.Empty(invocation.OutputLines);
        Assert.False(invocation.IsRunning);
        Assert.NotNull(invocation.FinishedAt);
    }

    [Fact]
    public async Task The_default_timeout_is_generous_and_does_not_touch_a_fast_command()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // The ceiling is real and it is well above anything a status/list call takes.
        Assert.Equal(TimeSpan.FromSeconds(120), CliRunner.DefaultTimeout);

        var implicitDefault = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "quick" });
        var explicitCeiling = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", "quick" },
            options: CliRunOptions.WithTimeout(TimeSpan.FromSeconds(30)));

        foreach (var invocation in new[] { implicitDefault, explicitCeiling })
        {
            Assert.True(invocation.Succeeded);
            Assert.Equal(0, invocation.ExitCode);
            Assert.Null(invocation.FailureReason);
            Assert.Contains(
                invocation.OutputLines,
                l => l.Text.Contains("quick", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Timeouts_are_inferred_in_tiers_for_the_defenseclaw_cli_only()
    {
        // Wizards run `defenseclaw setup <target> …`; they are the reason the long tier exists.
        Assert.Equal(CliRunner.LongRunningTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "setup", "claude-code" }));

        // Work-doing verbs get the extended tier.
        Assert.Equal(CliRunner.ExtendedTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "doctor" }));
        Assert.Equal(CliRunner.ExtendedTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "agent", "discover" }));
        Assert.Equal(CliRunner.ExtendedTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "plugin", "install", "some-plugin" }));

        // Everything else — status, list, allow/block — is the default.
        Assert.Equal(CliRunner.DefaultTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "status" }));
        Assert.Equal(CliRunner.DefaultTimeout, CliRunner.ResolveTimeout(DefenseClawCli, new[] { "plugin", "list", "--json" }));
        Assert.Equal(CliRunner.DefaultTimeout, CliRunner.ResolveTimeout(DefenseClawCli, Array.Empty<string>()));

        // The gateway binary never gets a longer leash: a hung `start`/`stop` is the wedged
        // tray toggle this whole ceiling exists to bound.
        Assert.Equal(CliRunner.DefaultTimeout, CliRunner.ResolveTimeout(GatewayCli, new[] { "start" }));
        Assert.Equal(CliRunner.DefaultTimeout, CliRunner.ResolveTimeout(GatewayCli, new[] { "setup" }));

        Assert.True(CliRunner.DefaultTimeout < CliRunner.ExtendedTimeout);
        Assert.True(CliRunner.ExtendedTimeout < CliRunner.LongRunningTimeout);
    }

    [Fact]
    public async Task Explicit_options_override_the_inferred_timeout()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            CliRunner.ResolveTimeout(DefenseClawCli, new[] { "setup" }, CliRunOptions.WithTimeout(TimeSpan.FromSeconds(5))));
        Assert.Equal(
            CliRunner.LongRunningTimeout,
            CliRunner.ResolveTimeout(DefenseClawCli, new[] { "status" }, CliRunOptions.LongRunning));

        // "No timeout" is spelled out, never a side effect: null here means unbounded.
        Assert.Null(CliRunner.ResolveTimeout(DefenseClawCli, new[] { "setup" }, CliRunOptions.NoTimeout));
        Assert.Null(CliRunner.ResolveTimeout(DefenseClawCli, new[] { "status" }, CliRunOptions.Installer));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CliRunner.ResolveTimeout(DefenseClawCli, new[] { "status" }, CliRunOptions.WithTimeout(TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CliRunner.ResolveTimeout(DefenseClawCli, new[] { "status" }, CliRunOptions.WithTimeout(TimeSpan.FromSeconds(-1))));

        // A bad option is a plain argument error and leaves nothing half-recorded behind.
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunExecutableAsync(
                CmdPath,
                new[] { "/c", "exit", "0" },
                options: CliRunOptions.WithTimeout(TimeSpan.Zero)));
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task Shutdown_kills_in_flight_runs_but_leaves_exempt_ones_alone()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var known = PingPids();

        var doomed = runner.RunExecutableAsync(CmdPath, LongPing);
        var doomedPing = await WaitForNewPingAsync(known, doomed);
        _ = known.Add(doomedPing);

        using var release = new CancellationTokenSource();
        var exempt = runner.RunExecutableAsync(
            CmdPath,
            LongPing,
            cancellationToken: release.Token,
            options: CliRunOptions.Installer);
        var exemptPing = await WaitForNewPingAsync(known, exempt);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var settled = runner.Shutdown(TimeSpan.FromSeconds(10));
            stopwatch.Stop();

            Assert.True(settled, "Shutdown did not see the killed run finish inside its bound");
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(8),
                $"Shutdown took {stopwatch.Elapsed.TotalSeconds:0.0} s; app exit must not hang on a child");
            Assert.True(runner.IsShutDown);

            // The ordinary run is stopped, says why, and takes its whole tree with it.
            var doomedResult = await doomed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("exiting", doomedResult.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", doomedResult.FailureReason, StringComparison.Ordinal);
            Assert.Null(doomedResult.ExitCode);
            Assert.False(doomedResult.IsRunning);
            Assert.True(
                await WaitUntilGoneAsync(doomedPing, TimeSpan.FromSeconds(5)),
                "the non-exempt run's grandchild survived Shutdown");

            // The exempt run — the upgrade installer's stand-in — is untouched: still running,
            // its process tree still alive.
            Assert.False(exempt.IsCompleted, "Shutdown ended a run that is marked as surviving it");
            Assert.True(IsAlive(exemptPing), "Shutdown killed the exempt run's process tree");

            // Exemption covers app exit only. Its caller's own token still ends it, exactly as
            // for any other run — which is also how this test cleans up after itself.
            release.Cancel();
            var exemptResult = await exempt.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.StartsWith("cancelled", exemptResult.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain("exiting", exemptResult.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            KillIfAlive(doomedPing);
            KillIfAlive(exemptPing);
        }
    }

    [Fact]
    public async Task After_shutdown_new_runs_are_refused_unless_they_are_exempt()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // Nothing in flight: shutdown is immediate and reports that everything settled.
        Assert.True(runner.Shutdown());
        Assert.True(runner.IsShutDown);

        var refused = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "late" });

        Assert.StartsWith("not started", refused.FailureReason, StringComparison.Ordinal);
        Assert.Null(refused.ExitCode);
        Assert.Empty(refused.OutputLines);
        Assert.False(refused.IsRunning);
        Assert.NotNull(refused.FinishedAt);

        // The upgrade path is allowed through: an installer the operator started must not be
        // turned away because the tray began exiting a moment earlier.
        var allowed = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", "late" },
            options: CliRunOptions.Installer);

        Assert.True(allowed.Succeeded);
        Assert.Null(allowed.FailureReason);

        // Idempotent: a second shutdown, as AppServices.Dispose issues after OnExit, is a no-op.
        Assert.True(runner.Shutdown());
    }

    [Fact]
    public async Task Dispose_is_a_shutdown()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var known = PingPids();

        var run = runner.RunExecutableAsync(CmdPath, LongPing);
        var ping = await WaitForNewPingAsync(known, run);

        try
        {
            runner.Dispose();

            var invocation = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("exiting", invocation.FailureReason, StringComparison.Ordinal);
            Assert.False(invocation.IsRunning);
            Assert.True(
                await WaitUntilGoneAsync(ping, TimeSpan.FromSeconds(5)),
                "Dispose left the child's process tree running");
            Assert.True(runner.IsShutDown);
        }
        finally
        {
            KillIfAlive(ping);
        }
    }
}
