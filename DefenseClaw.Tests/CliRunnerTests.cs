using System.Diagnostics;
using System.Text.Json;
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
    public async Task Decodes_child_output_as_utf8()
    {
        // The DefenseClaw CLI writes UTF-8 when piped (its bullet is e2 80 a2). `type` copies file bytes
        // to stdout verbatim, so this proves the decode, not the child's own console encoding.
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "glyphs.txt");
        await File.WriteAllBytesAsync(file, new System.Text.UTF8Encoding(false).GetBytes("status • ok — ✓" + Environment.NewLine));
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains(invocation.OutputLines, l => l.Text == "status • ok — ✓");
    }

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
    // The stand-in for "a child that will not exit" is `cmd /c ping -n <seconds + 1> 127.0.0.1`:
    // as long as the longest wait in this class (TestTimeouts.Ceiling) plus a minute if left alone,
    // so that a slow machine, which makes a test wait for a condition for longer than a person
    // would, never finds the child gone by itself (it used to live 29 s, and every wait was
    // shorter than that by luck). And — importantly — a real tree. cmd is the runner's child and
    // ping is cmd's, so a kill that stops at the direct child leaves ping running, which the
    // tree tests detect by watching for the ping process itself.
    // ----------------------------------------------------------------------------------

    private static readonly string[] LongPing =
    {
        "/c", "ping", "-n", ((int)(TestTimeouts.Ceiling + TimeSpan.FromMinutes(1)).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1",
    };

    private static readonly string DefenseClawCli = Path.Combine("C:\\", "bin", "defenseclaw.exe");

    private static readonly string GatewayCli = Path.Combine("C:\\", "bin", "defenseclaw-gateway.exe");

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
    /// <para>
    /// Only a ping below the child this run started counts (its parent is the cmd.exe the runner started), never a ping that merely
    /// appeared: the machine is also running other pings (the same suite in another checkout, another developer's tests, a person's own)
    /// and other test classes' of this process, and a test that adopts one of those as "its" ping and later waits for it, or kills it,
    /// ends a run that is not its own, so the other test sees "Already finished" or a dead process tree.
    /// </para>
    /// </summary>
    /// <param name="invocation">The run's invocation (the runner's newest activity entry right after it was started).</param>
    /// <param name="run">The run itself, to stop waiting when it is over.</param>
    private static async Task<int> WaitForPingAsync(CliInvocation invocation, Task run)
    {
        var ping = await FindPingAsync(invocation, run);
        if (ping == 0)
        {
            Assert.Fail("No ping grandchild appeared before the run ended.");
        }

        return ping;
    }

    /// <summary><see cref="WaitForPingAsync"/> that says "none" (0) instead of failing: for a test whose own timeout may win the race to the ping.</summary>
    private static async Task<int> FindPingAsync(CliInvocation invocation, Task run)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TestTimeouts.Ceiling && !run.IsCompleted)
        {
            if (invocation.ProcessId is { } child && ProcessTree.DescendantsNamed("ping.exe", child) is { Count: > 0 } pings)
            {
                return pings.Min();
            }

            await Task.Delay(25);
        }

        return 0;
    }

    /// <summary>
    /// Waits for the process to be gone: a condition (the kill was issued and the run has reported it, but Windows takes its time to end a
    /// process tree on a busy machine), so the bound is only a hang's, <see cref="TestTimeouts.Ceiling"/>, and it is measured on the monotonic clock.
    /// </summary>
    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan? within = null)
    {
        var clock = Stopwatch.StartNew();
        var limit = within ?? TestTimeouts.Ceiling;
        while (clock.Elapsed < limit)
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

        // The timeout is a promise the other way round: the run does not end before it (this one is the product's, and exact enough to keep).
        // That the run ended by it and not by the ping running out is what the reason and the missing exit code below say. How long a busy
        // machine takes to kill the tree after the timeout is a condition, not a promise, so its bound is only a hang's.
        Assert.True(
            stopwatch.Elapsed < TestTimeouts.Ceiling,
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
        // The run's timeout is armed before the launch, and the ping has to exist when it fires for there to be a tree to kill. On a busy machine
        // cmd.exe can take longer than a short timeout to start its ping; a timeout that wins that race ends the run with no ping seen and
        // proves nothing about trees, so the test goes again with a timeout four times as long (3 s, 12 s, 48 s) before it calls that a failure.
        var attempts = new List<string>();
        for (var timeout = TimeSpan.FromSeconds(3); timeout < TestTimeouts.Ceiling; timeout *= 4)
        {
            using var temp = new TempDirectory();
            var runner = Runner(temp.Path);
            var run = runner.RunExecutableAsync(CmdPath, LongPing, options: CliRunOptions.WithTimeout(timeout));
            var grandchild = await FindPingAsync(runner.Activity[0], run);
            if (grandchild == 0)
            {
                var ended = await run.WaitAsync(TestTimeouts.Ceiling);
                attempts.Add($"{timeout.TotalSeconds:0} s: the run ended with no ping seen ({ended.FailureReason})");
                continue;
            }

            try
            {
                var invocation = await run.WaitAsync(TestTimeouts.Ceiling);

                Assert.Contains("timed out", invocation.FailureReason, StringComparison.Ordinal);
                Assert.True(
                    await WaitUntilGoneAsync(grandchild),
                    "ping, the grandchild, survived the kill: only the direct child was terminated");
                return;
            }
            finally
            {
                KillIfAlive(grandchild);
            }
        }

        Assert.Fail("The timeout fired before the child had started a ping, every time: " + string.Join("; ", attempts));
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_whole_tree_and_records_cancelled()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        using var cts = new CancellationTokenSource();

        var run = runner.RunExecutableAsync(CmdPath, LongPing, cancellationToken: cts.Token);
        var grandchild = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            cts.Cancel();
            var invocation = await run.WaitAsync(TestTimeouts.Ceiling);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TestTimeouts.Ceiling,
                $"cancelling took {stopwatch.Elapsed.TotalSeconds:0.0} s to end the run");
            Assert.StartsWith("cancelled", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Null(invocation.ExitCode);
            Assert.False(invocation.Succeeded);
            Assert.False(invocation.IsRunning);
            Assert.NotNull(invocation.FinishedAt);

            Assert.True(
                await WaitUntilGoneAsync(grandchild),
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
        var grandchild = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            await Task.Delay(300); // long enough for the write to have filled the pipe and blocked
            Assert.False(run.IsCompleted, "the run ended before it was cancelled");

            cts.Cancel();
            var invocation = await run.WaitAsync(TestTimeouts.Ceiling);

            Assert.StartsWith("cancelled", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
            Assert.Null(invocation.ExitCode);
            Assert.False(invocation.IsRunning);
            Assert.NotNull(invocation.FinishedAt);
            Assert.Equal(1, Volatile.Read(ref completedEvents));
            Assert.True(
                await WaitUntilGoneAsync(grandchild),
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

        var doomed = runner.RunExecutableAsync(CmdPath, LongPing);
        var doomedPing = await WaitForPingAsync(runner.Activity[0], doomed);

        using var release = new CancellationTokenSource();
        var exempt = runner.RunExecutableAsync(
            CmdPath,
            LongPing,
            cancellationToken: release.Token,
            options: CliRunOptions.Installer);
        var exemptPing = await WaitForPingAsync(runner.Activity[0], exempt);

        try
        {
            // The product's promise is the bound: Shutdown blocks at most that long, and not for the exempt run (a child meant to outlive the app),
            // so it comes back as soon as the killed run has reported. The bound given is generous - the killed run reporting is a condition
            // that a busy machine takes seconds over, not a promise - and a Shutdown that did wait for the exempt run would be told apart
            // below, by that run being over.
            var bound = TestTimeouts.Ceiling;
            var stopwatch = Stopwatch.StartNew();
            var settled = runner.Shutdown(bound);
            stopwatch.Stop();

            Assert.True(settled, "Shutdown did not see the killed run finish inside its bound");
            Assert.True(
                stopwatch.Elapsed < bound,
                $"Shutdown took {stopwatch.Elapsed.TotalSeconds:0.0} s; app exit must not hang on a child");
            Assert.True(runner.IsShutDown);

            // The ordinary run is stopped, says why, and takes its whole tree with it.
            var doomedResult = await doomed.WaitAsync(TestTimeouts.Ceiling);
            Assert.Contains("exiting", doomedResult.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", doomedResult.FailureReason, StringComparison.Ordinal);
            Assert.Null(doomedResult.ExitCode);
            Assert.False(doomedResult.IsRunning);
            Assert.True(
                await WaitUntilGoneAsync(doomedPing),
                "the non-exempt run's grandchild survived Shutdown");

            // The exempt run — the upgrade installer's stand-in — is untouched: still running,
            // its process tree still alive.
            Assert.False(exempt.IsCompleted, "Shutdown ended a run that is marked as surviving it");
            Assert.True(IsAlive(exemptPing), "Shutdown killed the exempt run's process tree");

            // Exemption covers app exit only. Its caller's own token still ends it, exactly as
            // for any other run — which is also how this test cleans up after itself.
            release.Cancel();
            var exemptResult = await exempt.WaitAsync(TestTimeouts.Ceiling);
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

        var run = runner.RunExecutableAsync(CmdPath, LongPing);
        var ping = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            runner.Dispose();

            var invocation = await run.WaitAsync(TestTimeouts.Ceiling);
            Assert.Contains("exiting", invocation.FailureReason, StringComparison.Ordinal);
            Assert.False(invocation.IsRunning);
            Assert.True(
                await WaitUntilGoneAsync(ping),
                "Dispose left the child's process tree running");
            Assert.True(runner.IsShutDown);
        }
        finally
        {
            KillIfAlive(ping);
        }
    }

    // ----------------------------------------------------------------------------------
    // Operator cancel (CliRunner.Cancel): the Activity panel's Cancel button holds the
    // invocation, not the token the run was started with. It must kill a running child's whole
    // tree and say "cancelled"; refuse the upgrade installer (SurvivesShutdown); and be a
    // harmless no-op on a run that already finished. Nothing here sleeps to "let something
    // happen": each test waits for the ping grandchild to exist (the same signal the lifecycle
    // tests use) and then acts, and every wait has a generous ceiling rather than a fixed delay.
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Captures every invocation the runner starts, in order. The started event fires
    /// synchronously inside <c>RunExecutableAsync</c> before the child exists, so a test can get
    /// hold of the live instance without polling <see cref="CliRunner.Activity"/>.
    /// </summary>
    private static List<CliInvocation> CaptureStarted(CliRunner runner)
    {
        var started = new List<CliInvocation>();
        runner.InvocationStarted += (_, invocation) =>
        {
            lock (started)
            {
                started.Add(invocation);
            }
        };

        return started;
    }

    [Fact]
    public async Task Cancel_kills_a_running_childs_whole_tree_and_records_cancelled()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = CaptureStarted(runner);
        var completedEvents = 0;
        runner.InvocationCompleted += (_, _) => Interlocked.Increment(ref completedEvents);

        var run = runner.RunExecutableAsync(CmdPath, LongPing);
        var ping = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            CliInvocation invocation;
            lock (started)
            {
                invocation = Assert.Single(started);
            }

            Assert.True(invocation.IsRunning);
            Assert.False(invocation.SurvivesShutdown);
            Assert.False(invocation.CancelRequested);

            var stopwatch = Stopwatch.StartNew();
            var accepted = runner.Cancel(invocation, out var reason);
            Assert.True(accepted, reason);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.True(invocation.CancelRequested);

            var result = await run.WaitAsync(TestTimeouts.Ceiling);
            stopwatch.Stop();

            // The same live instance, ended by the cancel and not by the ping running out (it has minutes left).
            Assert.Same(invocation, result);
            Assert.True(
                stopwatch.Elapsed < TestTimeouts.Ceiling,
                $"the cancel took {stopwatch.Elapsed.TotalSeconds:0.0} s to end the run");

            // Recorded exactly like a caller-token cancellation: a reason, no exit code, and never
            // the artefact exit value a killed process reports.
            Assert.StartsWith("cancelled", result.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", result.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain("exiting", result.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain("timed out", result.FailureReason, StringComparison.Ordinal);
            Assert.Null(result.ExitCode);
            Assert.False(result.Succeeded);
            Assert.False(result.IsRunning);
            Assert.NotNull(result.FinishedAt);
            Assert.False(runner.Activity[0].IsRunning);
            Assert.Equal(1, Volatile.Read(ref completedEvents));

            // Not just cmd.exe: ping, cmd's child, went with it.
            Assert.True(
                await WaitUntilGoneAsync(ping),
                "ping, the grandchild, survived Cancel: only the direct child was terminated");
        }
        finally
        {
            KillIfAlive(ping);
        }
    }

    [Fact]
    public async Task Cancel_matches_runs_by_instance_and_leaves_a_sibling_running()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = CaptureStarted(runner);

        var first = runner.RunExecutableAsync(CmdPath, LongPing);
        var firstPing = await WaitForPingAsync(runner.Activity[0], first);

        var second = runner.RunExecutableAsync(CmdPath, LongPing);
        var secondPing = await WaitForPingAsync(runner.Activity[0], second);

        try
        {
            CliInvocation firstInvocation;
            CliInvocation secondInvocation;
            lock (started)
            {
                Assert.Equal(2, started.Count);
                firstInvocation = started[0];
                secondInvocation = started[1];
            }

            Assert.True(runner.Cancel(firstInvocation, out var reason), reason);

            var firstResult = await first.WaitAsync(TestTimeouts.Ceiling);
            Assert.StartsWith("cancelled", firstResult.FailureReason, StringComparison.Ordinal);
            Assert.True(
                await WaitUntilGoneAsync(firstPing),
                "the cancelled run's grandchild survived");

            // The sibling is untouched: still running, its tree alive, nothing recorded against it.
            Assert.False(second.IsCompleted, "cancelling one run ended a different one");
            Assert.True(secondInvocation.IsRunning);
            Assert.False(secondInvocation.CancelRequested);
            Assert.Null(secondInvocation.FailureReason);
            Assert.True(IsAlive(secondPing), "cancelling one run killed another run's process tree");

            // By id this time — which is also how the test cleans up after itself.
            Assert.True(runner.Cancel(secondInvocation.Id, out var secondReason), secondReason);
            var secondResult = await second.WaitAsync(TestTimeouts.Ceiling);
            Assert.StartsWith("cancelled", secondResult.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            KillIfAlive(firstPing);
            KillIfAlive(secondPing);
        }
    }

    [Fact]
    public async Task Cancel_refuses_a_run_that_survives_shutdown_and_leaves_it_running()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = CaptureStarted(runner);

        // The upgrade installer's stand-in. Its caller's token is how the test ends it, because
        // that is the only thing that is allowed to.
        using var release = new CancellationTokenSource();
        var run = runner.RunExecutableAsync(
            CmdPath,
            LongPing,
            cancellationToken: release.Token,
            options: CliRunOptions.Installer);
        var ping = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            CliInvocation invocation;
            lock (started)
            {
                invocation = Assert.Single(started);
            }

            Assert.True(invocation.SurvivesShutdown);

            var accepted = runner.Cancel(invocation, out var reason);

            Assert.False(accepted);
            Assert.Contains("survives", reason, StringComparison.OrdinalIgnoreCase);

            // Refused by id as well, and with the same answer.
            Assert.False(runner.Cancel(invocation.Id, out var byIdReason));
            Assert.Contains("survives", byIdReason, StringComparison.OrdinalIgnoreCase);

            // Nothing was signalled: still running, not flagged, process tree alive.
            Assert.False(run.IsCompleted, "a refused Cancel ended the exempt run anyway");
            Assert.True(invocation.IsRunning);
            Assert.False(invocation.CancelRequested);
            Assert.Null(invocation.FailureReason);
            Assert.True(IsAlive(ping), "a refused Cancel killed the exempt run's process tree");

            // The caller's own token is unaffected by any of this and still ends it.
            release.Cancel();
            var result = await run.WaitAsync(TestTimeouts.Ceiling);
            Assert.StartsWith("cancelled", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            KillIfAlive(ping);
        }
    }

    [Fact]
    public async Task Cancel_on_a_finished_run_is_a_no_op_that_leaves_its_record_alone()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "7" });
        Assert.False(invocation.IsRunning);
        Assert.Equal(7, invocation.ExitCode);

        var accepted = runner.Cancel(invocation, out var reason);

        Assert.False(accepted);
        Assert.Contains("already finished", reason, StringComparison.OrdinalIgnoreCase);

        // Not re-labelled: the recorded outcome is what the child said, not "cancelled".
        Assert.Equal(7, invocation.ExitCode);
        Assert.Null(invocation.FailureReason);
        Assert.False(invocation.CancelRequested);

        // A second attempt, and the by-id form, say the same thing.
        Assert.False(runner.Cancel(invocation, out _));
        Assert.False(runner.Cancel(invocation.Id, out var byIdReason));
        Assert.Contains("already finished", byIdReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_after_a_run_was_cancelled_is_also_a_no_op()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var started = CaptureStarted(runner);

        var run = runner.RunExecutableAsync(CmdPath, LongPing);
        var ping = await WaitForPingAsync(runner.Activity[0], run);

        try
        {
            CliInvocation invocation;
            lock (started)
            {
                invocation = Assert.Single(started);
            }

            Assert.True(runner.Cancel(invocation, out var firstReason), firstReason);
            var result = await run.WaitAsync(TestTimeouts.Ceiling);
            var recorded = result.FailureReason;
            Assert.StartsWith("cancelled", recorded, StringComparison.Ordinal);

            Assert.False(runner.Cancel(invocation, out var secondReason));
            Assert.Contains("already finished", secondReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(recorded, invocation.FailureReason);
        }
        finally
        {
            KillIfAlive(ping);
        }
    }

    [Fact]
    public async Task Cancel_of_an_unknown_id_is_refused_without_throwing_and_bad_arguments_throw()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var other = Runner(temp.Path);

        // An id that is not in the activity list — never existed, or aged out of the ring.
        Assert.False(runner.Cancel("no-such-invocation-id", out var unknownReason));
        Assert.False(string.IsNullOrWhiteSpace(unknownReason));

        // An invocation that belongs to a different runner is not this runner's to cancel; it is
        // finished here, so the answer is the same harmless "nothing to cancel".
        var foreign = await other.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });
        Assert.False(runner.Cancel(foreign, out var foreignReason));
        Assert.False(string.IsNullOrWhiteSpace(foreignReason));
        Assert.False(runner.Cancel(foreign.Id, out var foreignIdReason));
        Assert.False(string.IsNullOrWhiteSpace(foreignIdReason));

        // A missing argument is a programming error, not an ordinary "no".
        Assert.Throws<ArgumentNullException>(() => runner.Cancel((CliInvocation)null!, out _));
        Assert.Throws<ArgumentException>(() => runner.Cancel(string.Empty, out _));
    }

    [Fact]
    public async Task Snapshot_carries_the_survives_shutdown_and_cancel_flags()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var ordinary = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });
        var installer = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "exit", "0" },
            options: CliRunOptions.Installer);

        Assert.False(ordinary.SurvivesShutdown);
        Assert.False(ordinary.Snapshot().SurvivesShutdown);
        Assert.True(installer.SurvivesShutdown);
        Assert.True(installer.Snapshot().SurvivesShutdown);
    }

    // ----------------------------------------------------------------------------------
    // RetainFullOutput / JsonRead. The ordinary retention cap drops the OLDEST lines, which for
    // a JSON document means the opening bracket: a long `<noun> list --json` then fails to parse
    // even though the CLI was healthy. Calls that parse stdout opt into far higher ceilings.
    // The ceilings are still ceilings — a runaway process must not pin an unbounded transcript.
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task JsonRead_is_the_default_timeout_plus_full_output_retention()
    {
        Assert.True(CliRunOptions.JsonRead.RetainFullOutput);
        Assert.Null(CliRunOptions.JsonRead.Timeout);
        Assert.False(CliRunOptions.JsonRead.SurvivesShutdown);

        // Nothing else opts in by accident.
        Assert.False(CliRunOptions.Default.RetainFullOutput);
        Assert.False(CliRunOptions.LongRunning.RetainFullOutput);
        Assert.False(CliRunOptions.NoTimeout.RetainFullOutput);
        Assert.False(CliRunOptions.Installer.RetainFullOutput);

        // The inferred timeout tiers still apply to it: a list is a default-tier call.
        Assert.Equal(
            CliRunner.DefaultTimeout,
            CliRunner.ResolveTimeout(DefenseClawCli, new[] { "skill", "list", "--json" }, CliRunOptions.JsonRead));

        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var ordinary = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });
        var full = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" }, options: CliRunOptions.JsonRead);

        Assert.False(ordinary.RetainsFullOutput);
        Assert.Equal(CliInvocation.MaxRetainedOutputLines, ordinary.RetainedLineLimit);
        Assert.Equal(CliInvocation.MaxRetainedOutputBytes, ordinary.RetainedByteLimit);
        Assert.True(full.RetainsFullOutput);
        Assert.Equal(CliInvocation.MaxFullOutputLines, full.RetainedLineLimit);
        Assert.Equal(CliInvocation.MaxFullOutputBytes, full.RetainedByteLimit);
        Assert.True(full.Snapshot().RetainsFullOutput);
    }

    [Fact]
    public async Task A_JsonRead_run_retains_output_past_the_ordinary_caps()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var file = TranscriptFile(temp, lineCount: 5_000);

        var ordinary = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        var full = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "type", file },
            options: CliRunOptions.JsonRead);

        // Same command, same output: the ordinary run loses its head, the JsonRead run loses nothing.
        Assert.True(ordinary.IsOutputTruncated);
        Assert.DoesNotContain(ordinary.OutputLines, l => l.Text.Contains("line-00001", StringComparison.Ordinal));

        Assert.False(full.IsOutputTruncated);
        Assert.Equal(0, full.DroppedOutputLineCount);
        Assert.True(full.OutputLines.Count >= 5_000, $"retained {full.OutputLines.Count} of 5000 lines");
        Assert.DoesNotContain(full.OutputLines, l => l.Stream == CliStream.Notice);
        Assert.Contains(full.OutputLines, l => l.Text.Contains("line-00001", StringComparison.Ordinal));
        Assert.Contains(full.OutputLines, l => l.Text.Contains("line-05000", StringComparison.Ordinal));
        Assert.Equal(full.OutputCursor, full.OutputLines.Count);
        Assert.True(full.Succeeded);
    }

    [Fact]
    public async Task A_JSON_document_longer_than_the_ordinary_cap_parses_from_a_JsonRead_run_only()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // Shaped like a big `skill list --json`: an indented array of objects, well past 2,000 lines.
        var items = Enumerable.Range(1, 400).Select(i => new
        {
            name = $"skill-{i:0000}",
            description = "A stand-in skill",
            enabled = i % 2 == 0,
            origin = "test",
            scan = new { clean = true, max_severity = "NONE", total_findings = 0 },
        });
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(json.Split('\n').Length > CliInvocation.MaxRetainedOutputLines, "the fixture must exceed the ordinary cap");
        var file = temp.Write("list.json", json);

        static string StdoutOf(CliInvocation invocation) =>
            string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

        var ordinary = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "type", file });
        var full = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "type", file },
            options: CliRunOptions.JsonRead);

        // The failure this option exists to prevent: a truncated head is not JSON.
        Assert.True(ordinary.IsOutputTruncated);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(StdoutOf(ordinary)));

        Assert.False(full.IsOutputTruncated);
        using var document = JsonDocument.Parse(StdoutOf(full));
        Assert.Equal(400, document.RootElement.GetArrayLength());
        Assert.Equal("skill-0400", document.RootElement[399].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_full_output_byte_ceiling_still_applies()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // Each retained line is charged its UTF-16 text plus a fixed overhead, so ~8 KB per line here.
        // Enough lines to pass the 16 MiB ceiling by a clear margin, far below the 200,000-line one:
        // it is the byte ceiling that has to bind.
        const int Width = 4_000;
        var lineCount = (int)(CliInvocation.MaxFullOutputBytes / ((Width * 2) + 64)) + 300;
        Assert.True(lineCount < CliInvocation.MaxFullOutputLines);
        var file = TranscriptFile(temp, lineCount, Width);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "type", file },
            options: CliRunOptions.JsonRead);

        var lines = invocation.OutputLines;

        // Bounded, honestly reported, and the tail — where the diagnosis lives — is what survives.
        Assert.True(invocation.IsOutputTruncated);
        Assert.True(invocation.DroppedOutputLineCount > 0);
        Assert.Equal(CliStream.Notice, lines[0].Stream);
        Assert.Contains("output truncated", lines[0].Text, StringComparison.Ordinal);
        Assert.Contains($"{invocation.DroppedOutputLineCount} earlier", lines[0].Text, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Text.Contains($"line-{lineCount:00000}", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Text.Contains("line-00001x", StringComparison.Ordinal));

        // Not silently the ordinary cap: 256 KiB would keep ~32 of these lines; the full ceiling keeps
        // well over a thousand.
        Assert.True(lines.Count > 1_000, $"retained only {lines.Count} wide lines");
        Assert.True(lines.Count < lineCount);
        Assert.Equal(0, invocation.ExitCode);
    }

    [Fact]
    public async Task The_full_output_line_ceiling_still_applies()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // One-character lines cost ~66 bytes each, so the 200,000-line ceiling is reached
        // long before the 16 MiB one: this is the line ceiling binding.
        var lineCount = CliInvocation.MaxFullOutputLines + 300;
        var file = temp.Write("short-lines.txt", string.Join(Environment.NewLine, Enumerable.Repeat("x", lineCount)));

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "type", file },
            options: CliRunOptions.JsonRead);

        var lines = invocation.OutputLines;

        Assert.True(invocation.IsOutputTruncated);
        Assert.True(invocation.DroppedOutputLineCount > 0);
        Assert.True(
            lines.Count <= CliInvocation.MaxFullOutputLines + 1,
            $"retained {lines.Count} lines, ceiling is {CliInvocation.MaxFullOutputLines} (+1 marker)");
        Assert.Equal(CliStream.Notice, lines[0].Stream);

        // Everything captured is still accounted for, dropped or retained. (>= : whether `type` ends
        // a file with a newline is a cmd detail this ceiling should not be pinned to.)
        Assert.True(invocation.OutputCursor >= lineCount);
        Assert.Equal(invocation.OutputCursor, invocation.DroppedOutputLineCount + lines.Count - 1);
        Assert.Equal(0, invocation.ExitCode);
    }
}
