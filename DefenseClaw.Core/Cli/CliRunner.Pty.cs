using System.Diagnostics;
using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// The pseudo-console half of the runner (CUST-221, <see cref="SecretPtyRunner"/>): a command whose hidden prompt reads the console, run in a
/// console of its own so that the app can type the answer.
/// <para>
/// It is the same kind of run as any other and goes through the same door: the read-only installation guard, the argv check (a value is never
/// on a command line), an entry in <see cref="Activity"/> with <see cref="InvocationStarted"/> and <see cref="InvocationCompleted"/>, the
/// in-flight set (so <see cref="Cancel(CliInvocation, out string)"/> and <see cref="Shutdown"/> reach it), and the three ways a run is stopped
/// - the caller's token, the operator, the app exiting - plus its time limits, each ending the whole process tree. What differs is the child
/// (started by <see cref="PseudoConsole"/>, not by <see cref="Process"/>), where its output comes from (the console, drawn by <see cref="VtScreen"/>)
/// and that this class types into it. The skeleton below follows <c>RunGatedExecutableAsync</c> and <c>SuperviseAsync</c> on purpose; a change to
/// how those record, finish or stop a run belongs here too.
/// </para>
/// </summary>
public sealed partial class CliRunner
{
    /// <summary>How long the output must have been quiet before a prompt is answered: the child has stopped writing and is waiting.</summary>
    private static readonly TimeSpan PtySettle = TimeSpan.FromMilliseconds(80);

    /// <summary>How often the run looks at the child's output, at its exit and at its limits.</summary>
    private static readonly TimeSpan PtyTick = TimeSpan.FromMilliseconds(25);

    /// <summary>After the child has gone, how long the last of its output may take to arrive through the console.</summary>
    private static readonly TimeSpan PtyQuietAfterExit = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan PtyDrainLimit = TimeSpan.FromSeconds(2);

    private const string PtyNotice =
        "Run in a pseudo-console: this app typed the value at the CLI's hidden prompt. It is not on the command line and it is not recorded here; " +
        "the CLI's own masked preview of it is hidden too.";

    /// <summary>
    /// Runs <paramref name="executable"/> in a pseudo-console and types each answer at its prompt. See <see cref="SecretPtyRunner"/>, which is the way in.
    /// </summary>
    /// <param name="executable">A program name looked up like any CLI (<paramref name="byName"/>), or a full path.</param>
    /// <param name="args">The arguments. They are checked for every value: a value on a command line is refused (<see cref="SecretInArgumentException"/>).</param>
    /// <param name="answers">What to type, and at which prompt.</param>
    /// <param name="promptTimeout">How long the program has to show a prompt (from its start, and from each value typed to the next prompt).</param>
    /// <param name="completionTimeout">How long it has to finish once the last value is typed.</param>
    internal async Task<SecretPtyResult> RunInPseudoConsoleAsync(
        string executable,
        bool byName,
        IReadOnlyList<string> args,
        IReadOnlyList<PtyAnswer> answers,
        TimeSpan promptTimeout,
        TimeSpan completionTimeout,
        CancellationToken cancellationToken,
        CliRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(answers);

        var secrets = answers.Where(a => !a.Value.IsEmpty).Select(a => a.Value).ToArray();

        // A value must never reach a command line, whatever else decides.
        GuardPtyArguments(args, secrets);

        // The one enforcement point of a read-only installation: a change is refused, recorded as refused, and nothing is started.
        if (RefuseOnReadOnlyInstallation(executable, args, secrets.FirstOrDefault(), options) is { } refused)
        {
            return new SecretPtyResult(SecretPtyOutcome.Refused, refused, 0, answers.Count, refused.FailureReason ?? "refused");
        }

        // The developer container runtime runs the CLI in `docker exec`, which has no console to attach a pseudo-console to from here.
        if (byName && _paths.Runtime.Kind == RuntimeKind.Container && RuntimeLaunch.RunsInContainer(executable))
        {
            return new SecretPtyResult(
                SecretPtyOutcome.Unavailable,
                null,
                0,
                answers.Count,
                "The selected runtime runs in a container, which this app cannot type a value into.");
        }

        // On the caller's context, as RunNamedAsync does: InvocationStarted is raised from here on that thread, and a UI subscriber that adds a row relies on it.
        var path = byName
            ? await _paths.FindExecutableAsync(executable).ConfigureAwait(true)
                ?? throw new CliNotFoundException(executable, _paths.CandidatesFor(executable))
            : executable;

        var timeout = ResolveTimeout(path, args, options);
        var invocation = new CliInvocation(path, args.ToArray(), DateTimeOffset.UtcNow)
        {
            UsedPromptSecret = true,
        };
        invocation.Append(new CliOutputLine(invocation.StartedAt, CliStream.Notice, PtyNotice));

        Record(invocation);
        var run = TryRegister(survivesShutdown: false, invocation);

        var outcome = SecretPtyOutcome.Cancelled;
        var typed = 0;

        try
        {
            InvocationStarted?.Invoke(this, invocation);

            if (run is null)
            {
                invocation.FailureReason = "not started — DefenseClaw for Windows is exiting, so no new commands are launched";
            }
            else
            {
                (outcome, typed) = await SupervisePseudoConsoleAsync(
                        invocation,
                        run,
                        path,
                        args,
                        answers,
                        secrets,
                        cancellationToken,
                        timeout,
                        promptTimeout,
                        completionTimeout)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever happened - an exit, a kill, a start failure, a throwing handler - the entry is finished and the completion event raised once.
            try
            {
                invocation.FinishedAt = DateTimeOffset.UtcNow;
                InvocationCompleted?.Invoke(this, invocation);
            }
            finally
            {
                if (run is not null)
                {
                    lock (_gate)
                    {
                        _ = _inFlight.Remove(run);
                    }

                    _ = run.Completed.TrySetResult();
                }
            }
        }

        return new SecretPtyResult(outcome, invocation, typed, answers.Count, DescribePtyResult(outcome, invocation, typed, answers.Count));
    }

    /// <summary>
    /// A value is refused in the arguments the way <see cref="GuardArguments"/> refuses one: the run's own values are checked beside the registered
    /// secrets (that method takes a run's values as environment entries, and only reads their values).
    /// </summary>
    private void GuardPtyArguments(IReadOnlyList<string> args, IReadOnlyList<SecretValue> secrets) =>
        GuardArguments(args, null, secrets.Select(s => new EnvironmentEntry("PROMPT", s)).ToArray());

    private async Task<(SecretPtyOutcome Outcome, int Typed)> SupervisePseudoConsoleAsync(
        CliInvocation invocation,
        InFlightRun run,
        string path,
        IReadOnlyList<string> args,
        IReadOnlyList<PtyAnswer> answers,
        SecretValue[] secrets,
        CancellationToken callerToken,
        TimeSpan? timeout,
        TimeSpan promptTimeout,
        TimeSpan completionTimeout)
    {
        var conversation = new PtyConversation(answers);

        // One token for everything that may end the run: the caller's, an operator's Cancel, the app exiting, and the limit.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _shutdown.Token, run.CancelToken);
        if (timeout is { } limit)
        {
            stop.CancelAfter(limit);
        }

        using var job = WindowsJob.TryCreate();
        PseudoConsole? console = null;
        var outcome = SecretPtyOutcome.Completed;
        var settled = false;

        try
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();

                // Off the caller's thread, which is the UI's: creating the pipes, the console and the process is tens of milliseconds, and a
                // start that fails can wait for a console to close.
                var workingDirectory = WorkingDirectoryFor(path);
                var environment = PtyEnvironment(path);
                console = await Task.Run(() => PseudoConsole.Start(path, args, workingDirectory, environment, job), stop.Token).ConfigureAwait(false);
            }
            catch (PseudoConsoleException ex)
            {
                invocation.FailureReason = "could not be started in a pseudo-console: " + ex.Message;
                settled = true;
                return (SecretPtyOutcome.Unavailable, 0);
            }
            catch (OperationCanceledException)
            {
                var kind = ClassifyStop(callerToken, run);
                invocation.FailureReason = DescribeStop(kind, timeout, processStarted: false);
                settled = true;
                return (OutcomeOf(kind), 0);
            }

            invocation.ProcessId = console.ProcessId;
            if (job is not null)
            {
                run.AttachJob(job);
            }

            if (console.Process is { } process)
            {
                run.Attach(process);
            }

            try
            {
                outcome = await ConverseAsync(console, conversation, invocation, secrets, promptTimeout, completionTimeout, stop.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                var kind = ClassifyStop(callerToken, run);
                invocation.FailureReason = DescribeStop(kind, timeout, processStarted: true);
                console.Kill();
                await DrainAsync(console, conversation, invocation, secrets, TimeSpan.FromMilliseconds(100), KillSettleTimeout).ConfigureAwait(false);
                outcome = OutcomeOf(kind);
            }

            settled = true;
        }
        finally
        {
            // Detached before the Process is disposed, so Shutdown never reaches for a handle that is about to be closed.
            run.Detach();

            if (!settled && console is not null)
            {
                // An exception nobody here anticipated is on its way out. It must not leave the child running with nobody holding it.
                console.Kill();
            }

            run.DetachJob();
            console?.Dispose();
        }

        return (outcome, conversation.Typed);
    }

    /// <summary>
    /// The conversation: draw what the child writes, type the answer when its prompt shows and the output has gone quiet, and watch for the child's
    /// exit and for the two limits. Returns how the run ended; a limit ends it with the tree killed and the reason on the invocation.
    /// </summary>
    private async Task<SecretPtyOutcome> ConverseAsync(
        PseudoConsole console,
        PtyConversation conversation,
        CliInvocation invocation,
        SecretValue[] secrets,
        TimeSpan promptTimeout,
        TimeSpan completionTimeout,
        CancellationToken stopToken)
    {
        var clock = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var lastTyped = TimeSpan.Zero;

        while (true)
        {
            if (DrawAvailable(console, conversation, invocation, secrets))
            {
                quiet.Restart();
            }

            if (conversation.TakeInput(quiet.Elapsed >= PtySettle) is { } keys)
            {
                try
                {
                    console.Write(keys);
                }
                catch (IOException)
                {
                    // The child ended before it read them; its exit, below, says how.
                }

                lastTyped = clock.Elapsed;
            }

            if (console.HasExited)
            {
                break;
            }

            stopToken.ThrowIfCancellationRequested();

            if (!conversation.AllTyped && clock.Elapsed - lastTyped > promptTimeout)
            {
                invocation.FailureReason =
                    $"the command did not show its hidden prompt within {SecretPtyRunner.Seconds(promptTimeout)} s - process tree killed" +
                    (conversation.Queries.Count > 0 ? " (the console was asked for " + string.Join(", ", conversation.Queries.Distinct()) + ")" : string.Empty);
                console.Kill();
                await DrainAsync(console, conversation, invocation, secrets, TimeSpan.FromMilliseconds(100), KillSettleTimeout).ConfigureAwait(false);
                return SecretPtyOutcome.PromptNotSeen;
            }

            if (conversation.AllTyped && clock.Elapsed - lastTyped > completionTimeout)
            {
                invocation.FailureReason =
                    $"the command did not finish within {SecretPtyRunner.Seconds(completionTimeout)} s of the value being typed - process tree killed";
                console.Kill();
                await DrainAsync(console, conversation, invocation, secrets, TimeSpan.FromMilliseconds(100), KillSettleTimeout).ConfigureAwait(false);
                return SecretPtyOutcome.TimedOut;
            }

            await Task.Delay(PtyTick, stopToken).ConfigureAwait(false);
        }

        await DrainAsync(console, conversation, invocation, secrets, PtyQuietAfterExit, PtyDrainLimit).ConfigureAwait(false);
        invocation.ExitCode = console.ExitCode;
        return SecretPtyOutcome.Completed;
    }

    /// <summary>
    /// The end of a run: what the child wrote last is still on its way through the console, so read until it has been quiet for
    /// <paramref name="quietFor"/> (at most <paramref name="limit"/>), close the console, read the pipe to its end and put the rest of the
    /// screen into the transcript.
    /// </summary>
    private async Task DrainAsync(
        PseudoConsole console,
        PtyConversation conversation,
        CliInvocation invocation,
        SecretValue[] secrets,
        TimeSpan quietFor,
        TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        while (clock.Elapsed < limit && quiet.Elapsed < quietFor)
        {
            if (DrawAvailable(console, conversation, invocation, secrets))
            {
                quiet.Restart();
            }

            await Task.Delay(PtyTick).ConfigureAwait(false);
        }

        await console.CloseAsync(limit).ConfigureAwait(false);
        _ = DrawAvailable(console, conversation, invocation, secrets);

        foreach (var line in conversation.Finish())
        {
            AppendPtyLine(invocation, line, secrets);
        }

        if (console.OutputTruncated)
        {
            invocation.Append(new CliOutputLine(
                DateTimeOffset.UtcNow,
                CliStream.Notice,
                $"[output truncated] the command wrote more than {PseudoConsole.MaxOutputCharacters:N0} characters; the rest was dropped."));
        }
    }

    private bool DrawAvailable(PseudoConsole console, PtyConversation conversation, CliInvocation invocation, SecretValue[] secrets)
    {
        var drew = false;
        while (console.Output.TryRead(out var chunk))
        {
            drew = true;
            foreach (var line in conversation.Feed(chunk))
            {
                AppendPtyLine(invocation, line, secrets);
            }
        }

        return drew;
    }

    /// <summary>One finished line of the console into the transcript and the live stream. The conversation has taken this run's values out of it; the runner's registered secrets (the gateway token) go too.</summary>
    private void AppendPtyLine(CliInvocation invocation, string text, SecretValue[] secrets)
    {
        var line = new CliOutputLine(DateTimeOffset.UtcNow, CliStream.StandardOutput, Scrub(text, secrets));
        invocation.Append(line);
        OutputReceived?.Invoke(this, line);
    }

    /// <summary>
    /// The environment the child gets: this app's, with the selected installation's identity applied last, as <c>SuperviseAsync</c> does for every
    /// DefenseClaw child (a run in a pseudo-console has no overlay, so there is no baseline to restore).
    /// </summary>
    private IDictionary<string, string?> PtyEnvironment(string executablePath)
    {
        // Only the environment is wanted from this start info; PseudoConsole starts the program.
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };

        if (IsDefenseClawExecutable(executablePath))
        {
            if (_paths.Runtime is { Kind: RuntimeKind.Cli, HomeDirectory: { } runtimeHome })
            {
                startInfo.Environment[DefenseClawPaths.HomeVariableName] = runtimeHome;
            }

            ApplyInstallationIdentity(startInfo, baseline: null);
        }

        return startInfo.Environment;
    }

    private static SecretPtyOutcome OutcomeOf(StopKind kind) =>
        kind == StopKind.TimedOut ? SecretPtyOutcome.TimedOut : SecretPtyOutcome.Cancelled;

    private static string DescribePtyResult(SecretPtyOutcome outcome, CliInvocation invocation, int typed, int expected) => outcome switch
    {
        SecretPtyOutcome.Completed when typed < expected =>
            invocation.ExitCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"The command ended (exit {code}) without asking for the value, so nothing was typed.")
                : "The command ended without asking for the value, so nothing was typed.",
        SecretPtyOutcome.Completed when invocation.ExitCode is 0 => "The command finished (exit 0).",
        SecretPtyOutcome.Completed => string.Create(CultureInfo.InvariantCulture, $"The command exited with code {invocation.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}."),
        _ => invocation.FailureReason ?? outcome.ToString(),
    };
}
