using DefenseClaw.Core.Cli;

namespace DefenseClaw.Core.Observability;

/// <summary>How a read of <c>observability plan --format json</c> ended.</summary>
public enum ObservabilityPlanStatus
{
    /// <summary>A plan arrived and could be read.</summary>
    Ok,

    /// <summary>There is no <c>defenseclaw</c> to ask (nothing was started).</summary>
    NotInstalled,

    /// <summary>It did not finish within the time allowed, and was stopped.</summary>
    TimedOut,

    /// <summary>It ran and failed (a non-zero exit, a run that could not start or was stopped).</summary>
    Failed,

    /// <summary>It exited 0 and what it printed is not a plan.</summary>
    Malformed,
}

/// <summary>The outcome of one read: a plan, or the one sentence that says why there is none.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Plan">Set exactly when <paramref name="Status"/> is <see cref="ObservabilityPlanStatus.Ok"/>.</param>
/// <param name="Message">One sentence for the operator; empty for a plan. It names the command and what happened to it, and never carries the command's output.</param>
/// <param name="ReadAt">When the read finished.</param>
public sealed record ObservabilityPlanRead(ObservabilityPlanStatus Status, ObservabilityPlan? Plan, string Message, DateTimeOffset ReadAt)
{
    /// <summary>True when <see cref="Plan"/> is set.</summary>
    public bool IsOk => Plan is not null;
}

/// <summary>What the command printed and how it ended, as the runner hands it back (a failed run is data, not an exception).</summary>
/// <param name="ExitCode">The exit code; null when the process did not exit by itself (it was stopped, or never started).</param>
/// <param name="FailureReason">Why the run did not end normally (<c>timed out after 30 s</c>, <c>cancelled</c>); null for one that did.</param>
/// <param name="Stdout">What it wrote to standard output (standard error is not the plan's).</param>
/// <param name="Truncated">True when the runner had to drop output to stay inside its budget, so <paramref name="Stdout"/> is not the whole document.</param>
public sealed record PlanCommandOutput(int? ExitCode, string? FailureReason, string Stdout, bool Truncated = false);

/// <summary>
/// Runs <c>defenseclaw</c> with <paramref name="argv"/> (always <see cref="ObservabilityPlanReader.Argv"/>, which is read-only by
/// <see cref="CommandTiers.Classify"/>), within <paramref name="timeout"/>. May throw <see cref="CliNotFoundException"/> when there is no CLI; must honour the token.
/// </summary>
public delegate Task<PlanCommandOutput> PlanCommandRunner(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>
/// Reads the compiled observability plan: runs <c>defenseclaw observability plan --format json</c> (a read-only command: it writes nothing and prints
/// no secret - its rows name destinations by name and never by address), parses the document, and remembers the answer.
/// <para>
/// <b>Off the caller's thread, and bounded.</b> The run starts on the thread pool, so a caller on the UI thread never waits on a process
/// start, and it is bounded by <see cref="Timeout"/>: a CLI that hangs is stopped and is <see cref="ObservabilityPlanStatus.TimedOut"/>, not a stuck card.
/// Concurrent callers share one run (single flight); a caller's token only stops that caller waiting, as with <see cref="Runtime.RuntimeDetector"/>.
/// </para>
/// <para>
/// <b>Not every tick.</b> The answer is reused until one of four things happens: it is older than <see cref="MaxAge"/> (five minutes); the caller
/// forces a read (the Refresh button); <see cref="Invalidate"/> is called; or the <c>configToken</c> the reader was built with has changed - the
/// app hands it the parsed config.yaml, which is replaced wholesale whenever the file changes, so a plan is never served for a configuration it
/// was not compiled from. A failed read is remembered too, for <see cref="FailureRetryAfter"/> (a minute), so a missing CLI costs one attempt
/// a minute and not one per poll; Refresh, a config change and <see cref="Invalidate"/> end that wait.
/// </para>
/// <para>
/// A read that was running when the configuration changed describes the old one: it is discarded and the command is run again (at most twice
/// more), and the callers waiting for it get the newer answer.
/// </para>
/// </summary>
public sealed class ObservabilityPlanReader
{
    /// <summary>The command: <c>observability plan --format json</c>. A read-only leaf in both runtimes' command trees; a test holds it to that.</summary>
    public static IReadOnlyList<string> Argv { get; } = Array.AsReadOnly(new[] { "observability", "plan", "--format", "json" });

    /// <summary>How long the command may run (a Python start-up and one call to the Go helper take a couple of seconds).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How old a plan may be before the next unforced read runs the command again.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How long a failure is served before the next unforced read tries again.</summary>
    public static readonly TimeSpan DefaultFailureRetryAfter = TimeSpan.FromSeconds(60);

    private const int MaxRestarts = 2;

    private readonly PlanCommandRunner _runner;
    private readonly Func<object?>? _configToken;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ObservabilityPlanRead? _current;
    private object? _currentToken;
    private long _currentVersion;
    private long _stamp;
    private long _version;
    private Task<ObservabilityPlanRead>? _flight;

    /// <param name="runner">Runs the command.</param>
    /// <param name="configToken">
    /// Names the configuration the answer is for: any object whose identity changes when the configuration does (the parsed config.yaml). A plan read
    /// under one token is stale under another. Null: only age, force and <see cref="Invalidate"/> end an answer.
    /// </param>
    /// <param name="timeProvider">The clock for ages; the system one when null.</param>
    public ObservabilityPlanReader(PlanCommandRunner runner, Func<object?>? configToken = null, TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _configToken = configToken;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A reader that runs the command through <paramref name="cli"/> - as <c>defenseclaw</c>, so it lands in Activity like every command - with the output kept whole.</summary>
    public static ObservabilityPlanReader ForCli(CliRunner cli, Func<object?>? configToken = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cli);

        // The one fact this class stands on, checked where it would be broken rather than assumed.
        if (CommandTiers.Classify(Argv) != CommandTier.ReadOnly)
        {
            throw new InvalidOperationException("The observability plan command is not a read.");
        }

        return new ObservabilityPlanReader(
            async (argv, timeout, cancellationToken) =>
                OutputOf(await cli.RunAsync(argv, cancellationToken: cancellationToken, options: CliRunOptions.JsonRead with { Timeout = timeout }).ConfigureAwait(false)),
            configToken,
            timeProvider);
    }

    /// <summary>What a finished run printed to standard output (standard error is the CLI's warnings, not the plan) and how it ended.</summary>
    internal static PlanCommandOutput OutputOf(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var stdout = string.Join('\n', invocation.OutputLines.Where(static l => l.Stream == CliStream.StandardOutput).Select(static l => l.Text));
        return new PlanCommandOutput(invocation.ExitCode, invocation.FailureReason, stdout, invocation.IsOutputTruncated);
    }

    /// <summary>The longest the command may run. Settable for tests.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>See <see cref="DefaultMaxAge"/>.</summary>
    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    /// <summary>See <see cref="DefaultFailureRetryAfter"/>.</summary>
    public TimeSpan FailureRetryAfter { get; init; } = DefaultFailureRetryAfter;

    /// <summary>The last answer, a plan or the reason there is none; null before the first read finishes. Never blocks.</summary>
    public ObservabilityPlanRead? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Ends the current answer: the next <see cref="GetAsync"/> runs the command, even one that is already running, whose answer is then thrown away.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
        }
    }

    /// <summary>
    /// The plan, from the last answer when that is still good (see the class remarks), else from a new run. Concurrent calls share one run;
    /// <paramref name="cancellationToken"/> stops this caller waiting and never stops the run. Never throws for a failed run: that is the
    /// <see cref="ObservabilityPlanRead"/>.
    /// </summary>
    /// <param name="force">Run the command now, whatever the age of the last answer (the Refresh button).</param>
    public Task<ObservabilityPlanRead> GetAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        // The token is asked for outside the lock: it may take a lock of its own.
        var token = SafeToken();
        Task<ObservabilityPlanRead> flight;
        lock (_gate)
        {
            if (!force && _current is { } cached && IsFresh(cached, token))
            {
                return Task.FromResult(cached);
            }

            flight = _flight is { IsCompleted: false } running ? running : (_flight = Task.Run(ReadAsync, CancellationToken.None));
        }

        return flight.WaitAsync(cancellationToken);
    }

    private bool IsFresh(ObservabilityPlanRead cached, object? token) =>
        _currentVersion == _version &&
        ReferenceEquals(_currentToken, token) &&
        _time.GetElapsedTime(_stamp) < (cached.IsOk ? MaxAge : FailureRetryAfter);

    private object? SafeToken()
    {
        try
        {
            return _configToken?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<ObservabilityPlanRead> ReadAsync()
    {
        for (var restarts = 0; ; restarts++)
        {
            long version;
            lock (_gate)
            {
                version = _version;
            }

            var token = SafeToken();
            var result = await RunOnceAsync().ConfigureAwait(false);
            var tokenAfter = SafeToken();

            lock (_gate)
            {
                // The configuration moved while the command ran: what it printed describes the one before. Run it again rather than serve that.
                if ((version != _version || !ReferenceEquals(token, tokenAfter)) && restarts < MaxRestarts)
                {
                    continue;
                }

                _current = result;
                _currentToken = token;
                _currentVersion = version;
                _stamp = _time.GetTimestamp();
                return result;
            }
        }
    }

    private async Task<ObservabilityPlanRead> RunOnceAsync()
    {
        using var limit = new CancellationTokenSource(Timeout);
        PlanCommandOutput output;
        try
        {
            output = await _runner(Argv, Timeout, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return Failure(ObservabilityPlanStatus.TimedOut, TimedOutMessage());
        }
        catch (OperationCanceledException)
        {
            return Failure(ObservabilityPlanStatus.Failed, "the command was cancelled");
        }
        catch (CliNotFoundException ex)
        {
            return Failure(ObservabilityPlanStatus.NotInstalled, $"'{ex.ExecutableName}' was not found");
        }
#pragma warning disable CA1031 // A runner that throws (a launcher bug, a vanished file) is a failed read, never an exception into a panel; only the type is reported, as its message may quote the command line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Failure(ObservabilityPlanStatus.Failed, $"the command could not be run ({ex.GetType().Name})");
        }

        return Interpret(output, limit.IsCancellationRequested);
    }

    private ObservabilityPlanRead Interpret(PlanCommandOutput output, bool timedOut)
    {
        if (output.FailureReason is { Length: > 0 } reason)
        {
            return timedOut || reason.StartsWith("timed out", StringComparison.OrdinalIgnoreCase)
                ? Failure(ObservabilityPlanStatus.TimedOut, TimedOutMessage())
                : Failure(ObservabilityPlanStatus.Failed, $"the command did not finish ({EndpointDisplay.ScrubText(reason, 80)})");
        }

        if (output.ExitCode != 0)
        {
            return Failure(
                ObservabilityPlanStatus.Failed,
                $"the command exited {(output.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "without a code")}");
        }

        if (output.Truncated)
        {
            return Failure(ObservabilityPlanStatus.Malformed, "the command printed more than the app keeps");
        }

        var parsed = ObservabilityPlanParser.Parse(output.Stdout);
        return parsed.Plan is { } plan
            ? new ObservabilityPlanRead(ObservabilityPlanStatus.Ok, plan, string.Empty, _time.GetUtcNow())
            : Failure(ObservabilityPlanStatus.Malformed, $"the command did not print a plan ({parsed.Error.TrimEnd('.')})");
    }

    private string TimedOutMessage() =>
        Timeout.TotalSeconds >= 1
            ? $"the command did not finish in {((long)Math.Ceiling(Timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)} s"
            : $"the command did not finish in {((long)Math.Ceiling(Timeout.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)} ms";

    private ObservabilityPlanRead Failure(ObservabilityPlanStatus status, string message) => new(status, null, message, _time.GetUtcNow());
}
