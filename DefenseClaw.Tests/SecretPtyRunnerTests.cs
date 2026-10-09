using System.Diagnostics;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The pseudo-console route end to end (CUST-221), against stand-ins that touch nothing of DefenseClaw: the installed runtime's own Python
/// interpreter (or any <c>python.exe</c> on PATH) running a one-liner that calls <c>getpass.getpass</c> - exactly the path <c>keys set</c> takes
/// - and a PowerShell reader of console keys. The real <c>defenseclaw</c> is never started and every value is synthetic. The run goes through a
/// real <see cref="CliRunner"/> over an empty temporary installation, so what Activity records, what the guard refuses and what Cancel kills
/// is the real thing.
/// </summary>
public sealed class SecretPtyRunnerTests
{
    /// <summary>Twenty characters, so a stand-in that prints the length of what it read prints 20.</summary>
    private const string Synthetic = "synthetic-test-value";

    private static readonly TimeSpan Ceiling = TestTimeouts.Ceiling;

    private static CliRunner Runner(TempDirectory temp, Func<InstallationContext>? installation = null) =>
        new(
            new DefenseClawPaths(
                dataDirectory: temp.Path,
                binDirectory: Path.Combine(temp.Path, "no-such-bin"),
                searchPath: Array.Empty<string>()),
            neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"),
            installation: installation);

    private static SecretPtyRunner Pty(CliRunner runner, TimeSpan? promptTimeout = null, TimeSpan? completionTimeout = null) =>
        new(runner) { PromptTimeout = promptTimeout ?? Ceiling, CompletionTimeout = completionTimeout ?? Ceiling };

    private static PtyAnswer At(string prompt, string value = Synthetic) =>
        new(new Regex(@"(?:^|\s)" + Regex.Escape(prompt) + ":$", RegexOptions.CultureInvariant), new SecretValue(value));

    private static Task<SecretPtyResult> RunPython(SecretPtyRunner pty, string code, params PtyAnswer[] answers) =>
        pty.RunAsync(PythonStandIn.Path!, byName: false, PythonStandIn.Run(code), answers, CancellationToken.None).WaitAsync(Ceiling);

    private static string[] Output(CliInvocation invocation) =>
        invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text).ToArray();

    /// <summary>The Activity record must hold the value nowhere: not the argv, the command line, the environment chip, any line, any notice, the failure.</summary>
    private static void AssertHoldsNo(CliInvocation invocation, string value, params string[] alsoHidden)
    {
        var everything = new List<string> { invocation.CommandLine, invocation.PowerShellCommandLine, invocation.EnvironmentDisplay, invocation.FailureReason ?? string.Empty };
        everything.AddRange(invocation.Argv);
        everything.AddRange(invocation.OutputLines.Select(l => l.Text));

        foreach (var text in everything)
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            foreach (var hidden in alsoHidden)
            {
                Assert.DoesNotContain(hidden, text, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------ the premise

    [PythonFact]
    public async Task A_value_piped_to_the_prompt_is_never_read()
    {
        // The blocker this whole route exists for (CUST-191, SecretRoutes): getpass reads the console buffer, not stdin.
        var info = new ProcessStartInfo(PythonStandIn.Path!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in PythonStandIn.Run(PythonStandIn.ReadOneValue))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        try
        {
            await process.StandardInput.WriteLineAsync(Synthetic);
            await process.StandardInput.FlushAsync();

            // A wait that ends in "it did not finish" can only pass for the wrong reason on a machine too slow to start Python, never fail for it.
            var exited = true;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                exited = false;
            }

            Assert.False(exited, "the interpreter finished: its getpass read the piped value after all, and the premise of the pseudo-console route is gone");
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [PythonFact]
    public async Task The_value_reaches_a_prompt_that_reads_the_console_and_comes_back_as_its_length()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var started = 0;
        var completed = 0;
        var streamed = new List<string>();
        runner.InvocationStarted += (_, _) => Interlocked.Increment(ref started);
        runner.InvocationCompleted += (_, _) => Interlocked.Increment(ref completed);
        runner.OutputReceived += (_, line) =>
        {
            lock (streamed)
            {
                streamed.Add(line.Text);
            }
        };

        var result = await RunPython(Pty(runner), PythonStandIn.ReadOneValue, At("Value"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(SecretPtyOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.ValuesTyped);
        Assert.Equal(1, result.ValuesExpected);
        Assert.False(result.RouteFailed);

        var invocation = result.Invocation!;
        Assert.Same(invocation, runner.Activity[0]);
        Assert.Equal(0, invocation.ExitCode);
        Assert.Null(invocation.FailureReason);
        Assert.False(invocation.IsRunning);
        Assert.True(invocation.UsedPromptSecret);
        Assert.False(invocation.UsedStdinSecret);
        Assert.Empty(invocation.EnvironmentNames);
        Assert.Equal(PythonStandIn.Run(PythonStandIn.ReadOneValue), invocation.Argv);

        // The prompt, then what the child printed after it read: 20 characters arrived.
        Assert.Equal(new[] { "Value:", "20" }, Output(invocation));
        Assert.Contains(invocation.OutputLines, l => l.Stream == CliStream.Notice && l.Text.Contains("pseudo-console", StringComparison.Ordinal));
        AssertHoldsNo(invocation, Synthetic);

        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Equal(1, Volatile.Read(ref completed));
        lock (streamed)
        {
            Assert.Equal(new[] { "Value:", "20" }, streamed);
        }

        // A copy for another thread keeps what makes Rerun unavailable.
        Assert.True(invocation.Snapshot().UsedPromptSecret);
    }

    [Fact]
    public async Task A_console_only_reader_that_is_not_python_gets_the_value_too()
    {
        // Windows PowerShell reads keys with Console.ReadKey, which throws on redirected input: the same class of prompt, on every Windows machine.
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            return;
        }

        using var temp = new TempDirectory();
        var runner = Runner(temp);
        const string script =
            "[Console]::Write('Value: '); $n = 0; while (([Console]::ReadKey($true)).Key -ne 'Enter') { $n++ }; [Console]::WriteLine(); [Console]::WriteLine('length ' + $n)";

        var result = await Pty(runner)
            .RunAsync(powershell, byName: false, new[] { "-NoLogo", "-NoProfile", "-Command", script }, new[] { At("Value") }, CancellationToken.None)
            .WaitAsync(Ceiling);

        Assert.True(result.Succeeded, result.Message + " " + string.Join(" | ", Output(result.Invocation!)));
        Assert.Contains("length 20", Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, Synthetic);
    }

    // ------------------------------------------------------------------------------------------------------ nothing leaks

    [PythonFact]
    public async Task What_the_child_echoes_of_the_value_and_the_clis_preview_of_it_never_reaches_activity()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var streamed = new List<string>();
        runner.OutputReceived += (_, line) =>
        {
            lock (streamed)
            {
                streamed.Add(line.Text);
            }
        };
        const string code =
            "import getpass; v = getpass.getpass('Value: '); print('echo ' + v); " +
            "print('Saved EXAMPLE_KEY = ' + v[:4] + chr(0x2026) + v[-4:] + ' to somewhere')";

        var result = await RunPython(Pty(runner), code, At("Value"));

        Assert.True(result.Succeeded, result.Message);
        var preview = "synt\u2026alue";
        Assert.Equal(new[] { "Value:", "echo ***REDACTED***", "Saved EXAMPLE_KEY = ***REDACTED*** to somewhere" }, Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, Synthetic, preview);
        lock (streamed)
        {
            Assert.DoesNotContain(streamed, l => l.Contains(Synthetic, StringComparison.Ordinal) || l.Contains(preview, StringComparison.Ordinal));
        }
    }

    [ClickFact]
    public async Task The_prompt_keys_set_uses_is_answered_as_it_draws_it()
    {
        // keys set: click.prompt("  NAME", hide_input=True, confirmation_prompt=False, default="", show_default=False), then a line that confirms the
        // store with the four-and-four preview of the value. Click draws "  NAME:" with echo() and passes the last character to getpass().
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        const string code =
            "import click; v = click.prompt('  EXAMPLE_KEY', hide_input=True, confirmation_prompt=False, default='', show_default=False); " +
            "click.secho('  Saved EXAMPLE_KEY = ' + v[:4] + chr(0x2026) + v[-4:] + ' to the data folder', fg='green'); click.echo('  length ' + str(len(v)))";

        var result = await RunPython(Pty(runner), code, At("EXAMPLE_KEY"));

        Assert.True(result.Succeeded, result.Message + " " + string.Join(" | ", Output(result.Invocation!)));
        Assert.Equal(
            new[] { "  EXAMPLE_KEY:", "  Saved EXAMPLE_KEY = ***REDACTED*** to the data folder", "  length 20" },
            Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, Synthetic, "synt…alue");
    }

    [PythonFact]
    public async Task A_value_the_console_itself_echoes_is_scrubbed_from_the_transcript()
    {
        // input() reads a line in the console's cooked mode, which draws what is typed. The hidden prompt does not, but nothing here may depend on that.
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var result = await RunPython(Pty(runner), "v = input('Value: '); print(len(v))", At("Value"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new[] { "Value: ***REDACTED***", "20" }, Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, Synthetic);
    }

    [PythonTheory]
    [InlineData(3_000)]
    [InlineData(8_192)] // the longest value the Credentials card accepts
    public async Task Every_character_a_key_can_type_arrives_unchanged_whatever_the_length(int length)
    {
        // Punctuation the VT input parser and the shell both have views on, accented and CJK letters, in the length asked for.
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        const string punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ éß日本";
        var value = string.Concat(Enumerable.Range(0, (length / punctuation.Length) + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture) + punctuation))[..length].Trim();
        const string code =
            "import getpass, hashlib; v = getpass.getpass('Value: '); print(len(v), hashlib.sha256(v.encode('utf-8')).hexdigest())";
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        var result = await RunPython(Pty(runner), code, At("Value", value));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new[] { "Value:", $"{value.Length} {expected}" }, Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, value);
    }

    [PythonFact]
    public async Task A_value_that_is_also_a_registered_secret_is_scrubbed_like_one()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        runner.RegisterSecret(new SecretValue("synthetic-gateway-token"));

        // The stand-in builds the registered value at run time: written out in its code it would be on the command line, which is refused.
        const string code = "import getpass; getpass.getpass('Value: '); print('token ' + 'synthetic-gateway' + '-token')";

        var result = await RunPython(Pty(runner), code, At("Value"));

        Assert.Equal(new[] { "Value:", "token ***REDACTED***" }, Output(result.Invocation!));
    }

    [PythonFact]
    public async Task Several_prompts_are_each_answered_with_their_own_value()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        const string second = "synthetic-second-value-17";
        const string code = "import getpass; a = getpass.getpass('First: '); b = getpass.getpass('Second: '); print(len(a), len(b))";

        var result = await RunPython(Pty(runner), code, At("Second", second), At("First"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.ValuesTyped);
        Assert.Equal(
            new[] { "First:", "Second:", $"{Synthetic.Length} {second.Length}" },
            Output(result.Invocation!));
        AssertHoldsNo(result.Invocation!, Synthetic, second);
    }

    // ------------------------------------------------------------------------------------------------------ how a run ends

    [PythonFact]
    public async Task A_child_that_exits_non_zero_after_the_value_is_the_clis_answer_and_not_a_route_failure()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var result = await RunPython(Pty(runner), "import getpass, sys; getpass.getpass('Value: '); print('could not write the file'); sys.exit(3)", At("Value"));

        Assert.Equal(SecretPtyOutcome.Completed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(result.RouteFailed); // a console window would get the same answer
        Assert.Equal(1, result.ValuesTyped);
        Assert.Equal(3, result.Invocation!.ExitCode);
        Assert.Contains("code 3", result.Message, StringComparison.Ordinal);
        Assert.Contains("could not write the file", Output(result.Invocation));
    }

    [PythonFact]
    public async Task A_command_that_ends_without_asking_for_the_value_has_nothing_typed_and_did_not_succeed()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var result = await RunPython(Pty(runner), "print('nothing to ask')", At("Value"));

        Assert.Equal(SecretPtyOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ValuesTyped);
        Assert.Equal(0, result.Invocation!.ExitCode);
        Assert.False(result.Succeeded); // exit 0, but the value was never given: nothing was stored
        Assert.False(result.RouteFailed);
        Assert.Contains("without asking", result.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "nothing to ask" }, Output(result.Invocation));
    }

    [PythonFact]
    public async Task A_command_that_never_shows_the_prompt_is_stopped_with_nothing_typed_and_its_process_is_gone()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var pty = Pty(runner, promptTimeout: TimeSpan.FromSeconds(2));

        var result = await RunPython(pty, "import time; print('starting', flush=True); time.sleep(300)", At("Value"));

        Assert.Equal(SecretPtyOutcome.PromptNotSeen, result.Outcome);
        Assert.Equal(0, result.ValuesTyped);
        Assert.True(result.RouteFailed);
        var invocation = result.Invocation!;
        Assert.Contains("hidden prompt", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Contains("process tree killed", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Null(invocation.ExitCode);
        Assert.False(invocation.IsRunning);
        AssertHoldsNo(invocation, Synthetic);
        Assert.True(await WaitUntilGoneAsync(invocation.ProcessId!.Value), "the child survived the prompt timeout");
    }

    [PythonFact]
    public async Task A_command_that_does_not_finish_after_the_value_is_stopped()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var pty = Pty(runner, completionTimeout: TimeSpan.FromSeconds(2));

        var result = await RunPython(pty, "import getpass, time; getpass.getpass('Value: '); time.sleep(300)", At("Value"));

        Assert.Equal(SecretPtyOutcome.TimedOut, result.Outcome);
        Assert.Equal(1, result.ValuesTyped);
        Assert.False(result.RouteFailed); // the value was typed: a console window would not change what the command does
        Assert.Contains("did not finish", result.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.True(await WaitUntilGoneAsync(result.Invocation.ProcessId!.Value));
        AssertHoldsNo(result.Invocation, Synthetic);
    }

    [PythonFact]
    public async Task The_runs_own_time_limit_ends_it_like_any_other_runs()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var result = await runner.RunInPseudoConsoleAsync(
                PythonStandIn.Path!,
                byName: false,
                PythonStandIn.Run("import time; time.sleep(300)"),
                new[] { At("Value") },
                promptTimeout: Ceiling,
                completionTimeout: Ceiling,
                CancellationToken.None,
                CliRunOptions.WithTimeout(TimeSpan.FromSeconds(2)))
            .WaitAsync(Ceiling);

        Assert.Equal(SecretPtyOutcome.TimedOut, result.Outcome);
        Assert.StartsWith("timed out after 2 s", result.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.Null(result.Invocation.ExitCode);
        Assert.True(await WaitUntilGoneAsync(result.Invocation.ProcessId!.Value));
    }

    [PythonFact]
    public async Task Cancel_kills_the_whole_tree_and_records_cancelled()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        CliInvocation? started = null;
        runner.InvocationStarted += (_, invocation) => started = invocation;
        const string code =
            "import subprocess, sys, getpass, time; " +
            "p = subprocess.Popen([sys.executable, '-I', '-c', 'import time; time.sleep(300)']); " +
            "print('grandchild', p.pid, flush=True); getpass.getpass('Value: '); time.sleep(300)";

        var run = RunPython(Pty(runner), code, At("Value"));
        var grandchild = await WaitForGrandchildAsync(() => started, run);

        try
        {
            Assert.True(PythonStandIn.IsAlive(grandchild));
            var accepted = runner.Cancel(started!, out var reason);
            Assert.True(accepted, reason);

            var result = await run;

            Assert.Equal(SecretPtyOutcome.Cancelled, result.Outcome);
            Assert.StartsWith("cancelled", result.Invocation!.FailureReason, StringComparison.Ordinal);
            Assert.Contains("process tree killed", result.Invocation.FailureReason, StringComparison.Ordinal);
            Assert.Null(result.Invocation.ExitCode);
            Assert.False(result.Invocation.IsRunning);
            Assert.False(result.RouteFailed);
            AssertHoldsNo(result.Invocation, Synthetic);

            // Not just the interpreter: its child went with it.
            Assert.True(await WaitUntilGoneAsync(grandchild), "the grandchild survived Cancel");
            Assert.True(await WaitUntilGoneAsync(result.Invocation.ProcessId!.Value));
        }
        finally
        {
            KillIfAlive(grandchild);
        }
    }

    [PythonFact]
    public async Task The_caller_cancelling_ends_the_run_the_same_way()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        using var source = new CancellationTokenSource();

        var run = Pty(runner).RunAsync(PythonStandIn.Path!, byName: false, PythonStandIn.Run("import time; print('up', flush=True); time.sleep(300)"), new[] { At("Value") }, source.Token);
        await WaitForLineAsync(runner, "up", run);

        await source.CancelAsync();
        var result = await run.WaitAsync(Ceiling);

        Assert.Equal(SecretPtyOutcome.Cancelled, result.Outcome);
        Assert.StartsWith("cancelled", result.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.True(await WaitUntilGoneAsync(result.Invocation.ProcessId!.Value));
    }

    [PythonFact]
    public async Task The_app_exiting_ends_the_run_and_refuses_the_next()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var run = Pty(runner).RunAsync(PythonStandIn.Path!, byName: false, PythonStandIn.Run("import time; print('up', flush=True); time.sleep(300)"), new[] { At("Value") }, CancellationToken.None);
        await WaitForLineAsync(runner, "up", run);

        var finished = await Task.Run(() => runner.Shutdown(TimeSpan.FromSeconds(30)));
        var result = await run.WaitAsync(Ceiling);

        Assert.True(finished);
        Assert.Equal(SecretPtyOutcome.Cancelled, result.Outcome);
        Assert.Contains("exiting", result.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.True(await WaitUntilGoneAsync(result.Invocation.ProcessId!.Value));

        var after = await RunPython(Pty(runner), PythonStandIn.ReadOneValue, At("Value"));
        Assert.Equal(SecretPtyOutcome.Cancelled, after.Outcome);
        Assert.Contains("not started", after.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.Null(after.Invocation.ProcessId);
    }

    // ------------------------------------------------------------------------------------------------------ nothing starts

    [Fact]
    public async Task Without_a_pseudo_console_nothing_is_started_and_the_console_window_is_the_answer()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        var pty = new SecretPtyRunner(runner, () => false);

        var result = await pty.RunAsync(@"C:\Tools\prog.exe", byName: false, new[] { "x" }, new[] { At("Value") }, CancellationToken.None);

        Assert.Equal(SecretPtyOutcome.Unavailable, result.Outcome);
        Assert.True(result.RouteFailed);
        Assert.False(pty.IsSupported);
        Assert.Null(result.Invocation);
        Assert.Empty(runner.Activity);
        Assert.Contains("Windows 10", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_is_a_route_failure_with_its_reason_in_activity()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var result = await Pty(runner).RunAsync(Path.Combine(temp.Path, "no-such-program.exe"), byName: false, new[] { "x" }, new[] { At("Value") }, CancellationToken.None).WaitAsync(Ceiling);

        Assert.Equal(SecretPtyOutcome.Unavailable, result.Outcome);
        Assert.True(result.RouteFailed);
        Assert.Equal(0, result.ValuesTyped);
        Assert.Contains("could not be started", result.Invocation!.FailureReason, StringComparison.Ordinal);
        Assert.False(result.Invocation.IsRunning);
        Assert.Null(result.Invocation.ProcessId);
    }

    [Fact]
    public async Task A_read_only_installation_refuses_the_run_and_records_the_refusal()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, () => new InstallationMachine().WithSecureClientLayout().Resolve());

        var result = await Pty(runner).RunAsync(@"C:\Tools\defenseclaw.exe", byName: false, new[] { "keys", "set", "EXAMPLE_KEY" }, new[] { At("EXAMPLE_KEY") }, CancellationToken.None);

        Assert.Equal(SecretPtyOutcome.Refused, result.Outcome);
        Assert.False(result.RouteFailed); // a console window would be refused too
        Assert.Equal(0, result.ValuesTyped);
        var entry = Assert.Single(runner.Activity);
        Assert.Same(entry, result.Invocation);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
        Assert.Null(entry.ProcessId);
        Assert.Null(entry.ExitCode);
        AssertHoldsNo(entry, Synthetic);
    }

    [Fact]
    public async Task A_value_on_the_command_line_is_refused_before_anything_is_recorded()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        var thrown = await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            Pty(runner).RunAsync(PythonStandIn.Path ?? @"C:\Tools\prog.exe", byName: false, new[] { "keys", "set", "EXAMPLE_KEY", Synthetic }, new[] { At("EXAMPLE_KEY") }, CancellationToken.None));

        Assert.Equal(3, thrown.ArgumentIndex);
        Assert.DoesNotContain(Synthetic, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task A_registered_secret_on_the_command_line_is_refused_too()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);
        runner.RegisterSecret(new SecretValue("synthetic-gateway-token"));

        await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            Pty(runner).RunAsync(@"C:\Tools\prog.exe", byName: false, new[] { "synthetic-gateway-token" }, new[] { At("Value") }, CancellationToken.None));

        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task The_named_entry_point_says_the_cli_is_missing_instead_of_failing_obscurely()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        await Assert.ThrowsAsync<CliNotFoundException>(() => Pty(runner).SetKeyAsync("EXAMPLE_KEY", new SecretValue(Synthetic)));

        Assert.Empty(runner.Activity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("A&B")]
    [InlineData("1ABC")]
    public async Task Only_an_environment_variable_name_can_be_set(string name)
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp);

        await Assert.ThrowsAsync<ArgumentException>(() => Pty(runner).SetKeyAsync(name, new SecretValue(Synthetic)));
    }

    [Fact]
    public async Task An_empty_value_is_not_typed()
    {
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentException>(() => Pty(Runner(temp)).SetKeyAsync("EXAMPLE_KEY", new SecretValue(string.Empty)));
    }

    [Fact]
    public void The_argv_that_stores_a_value_carries_no_value()
    {
        var argv = SecretPtyRunner.SetKeyArgv("EXAMPLE_KEY");

        Assert.Equal(new[] { "keys", "set", "EXAMPLE_KEY" }, argv);
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(argv));
        Assert.False(CommandTiers.PrintsSecrets(argv));
    }

    [Theory]
    [InlineData("  EXAMPLE_KEY:", true)]
    [InlineData("  EXAMPLE_KEY: x", false)]
    [InlineData("  OTHER_EXAMPLE_KEY:", false)]
    public void The_keys_set_prompt_is_the_name_and_a_colon_at_the_end_of_the_row(string cursorLine, bool matches)
    {
        Assert.Equal(matches, SecretPtyRunner.PromptFor("EXAMPLE_KEY").IsMatch(cursorLine.TrimEnd()));
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static async Task<int> WaitForGrandchildAsync(Func<CliInvocation?> invocation, Task run)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Ceiling && !run.IsCompleted)
        {
            if (invocation() is { } current)
            {
                foreach (var line in current.OutputLines)
                {
                    if (line.Text.StartsWith("grandchild ", StringComparison.Ordinal) &&
                        int.TryParse(line.Text["grandchild ".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                    {
                        return pid;
                    }
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail("The stand-in never reported its child.");
        return 0;
    }

    private static async Task WaitForLineAsync(CliRunner runner, string text, Task run)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Ceiling && !run.IsCompleted)
        {
            if (runner.Activity.FirstOrDefault() is { } current && current.OutputLines.Any(l => l.Text == text))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"The run never printed '{text}'.");
    }

    private static async Task<bool> WaitUntilGoneAsync(int processId)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Ceiling)
        {
            if (!PythonStandIn.IsAlive(processId))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static void KillIfAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
