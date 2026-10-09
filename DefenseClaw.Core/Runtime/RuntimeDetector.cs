namespace DefenseClaw.Core.Runtime;

/// <summary>What one read-only probe returned. A failure is data (<see cref="Text"/> null), never an exception.</summary>
/// <param name="Text">Combined stdout and stderr, or null when the probe did not answer (could not start, timed out, failed with no usage text).</param>
/// <param name="Error">Why, when <paramref name="Text"/> is null.</param>
public sealed record RuntimeProbeOutput(string? Text, string? Error = null)
{
    public static RuntimeProbeOutput Ok(string text) => new(text);

    public static RuntimeProbeOutput Fail(string error) => new(null, error);
}

/// <summary>
/// Runs one probe: <c>defenseclaw</c> followed by <paramref name="arguments"/> (always <c>--help</c> or <c>--version-json</c>,
/// so read-only by construction), against whichever runtime is selected. Must honour the token and must not throw for an
/// ordinary failure.
/// </summary>
public delegate Task<RuntimeProbeOutput> RuntimeProbeRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// Finds out what the connected runtime is and what it can do, and remembers the answer until the runtime changes.
/// <para>
/// <b>Cache.</b> The key is <see cref="Fingerprint"/>: the CLI path, its size and its modified time (and for a container, its
/// name). A known answer is reused for as long as the key is unchanged and re-probed the moment it moves, which is what an
/// upgrade, a reinstall or a swapped executable all do. An answer of "unknown" is not trusted: the next refresh after
/// <see cref="UnknownRetryAfter"/> probes again, because a failure is a statement about a moment (the CLI was busy, the container
/// was starting), not about the runtime.
/// </para>
/// <para>
/// <b>Time-boxed.</b> Every probe is bounded by <see cref="ProbeTimeout"/> and the whole refresh by <see cref="TotalTimeout"/>; a
/// timeout, an unparseable answer, or a missing CLI yields <see cref="RuntimeSnapshot.Failed"/>, so features stay hidden.
/// </para>
/// <para>
/// <b>Never blocks a reader.</b> <see cref="Current"/> is the last answer and is available at once (before the first probe it is
/// <see cref="RuntimeSnapshot.NotProbed"/>). <see cref="RefreshAsync"/> is single-flight: concurrent callers share one probe round.
/// </para>
/// <para>
/// Probes use <c>--help</c> and <c>--version-json</c> only. They do not go through the Activity ring (a help screen mutates
/// nothing and a refresh would bury the mutations Activity exists to record), the same call <c>SetupHelpProbe</c> made.
/// </para>
/// </summary>
public sealed class RuntimeDetector
{
    /// <summary>One probe may take this long (a Python start-up is under a second; a container <c>docker exec</c> a little more).</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A whole refresh may take this long.</summary>
    public static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long an "unknown" answer is served before the next refresh asks again.</summary>
    public static readonly TimeSpan DefaultUnknownRetryAfter = TimeSpan.FromSeconds(15);

    /// <summary>A probe round is capped at this many probes in flight; they are short and a Python start-up costs CPU.</summary>
    public const int MaxParallelProbes = 3;

    private readonly RuntimeProbeRunner _runner;
    private readonly Func<string?> _fingerprint;
    private readonly Func<string> _source;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private RuntimeSnapshot _current = RuntimeSnapshot.NotProbed;
    private Task<RuntimeSnapshot>? _flight;

    /// <param name="runner">Runs one probe against the selected runtime.</param>
    /// <param name="fingerprint">
    /// The cache key for the runtime as it is now (see <see cref="RuntimeFingerprint"/>), or null when there is no runtime to
    /// probe (the CLI is not installed): the answer is then "unknown", with no process started.
    /// </param>
    /// <param name="source">
    /// Where answers come from, for <see cref="RuntimeIdentity.Source"/>: the CLI path or <c>docker exec NAME</c>. A function, read on
    /// the probing thread only, because answering may be a PATH lookup that must not run in a constructor.
    /// </param>
    /// <param name="timeProvider">Clock for the retry window; the system one when null.</param>
    public RuntimeDetector(RuntimeProbeRunner runner, Func<string?> fingerprint, Func<string> source, TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The same, with a fixed source text.</summary>
    public RuntimeDetector(RuntimeProbeRunner runner, Func<string?> fingerprint, string source, TimeProvider? timeProvider = null)
        : this(runner, fingerprint, () => source ?? string.Empty, timeProvider)
    {
    }

    /// <summary>Per-probe ceiling. Settable for tests.</summary>
    public TimeSpan ProbeTimeout { get; init; } = DefaultProbeTimeout;

    /// <summary>Whole-refresh ceiling. Settable for tests.</summary>
    public TimeSpan TotalTimeout { get; init; } = DefaultTotalTimeout;

    /// <summary>See <see cref="DefaultUnknownRetryAfter"/>.</summary>
    public TimeSpan UnknownRetryAfter { get; init; } = DefaultUnknownRetryAfter;

    /// <summary>
    /// Raised after a refresh produced a snapshot that differs from the previous one (identity, capabilities or reason). On the
    /// thread that finished the probe; a UI subscriber marshals.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The last answer. Never blocks; <see cref="RuntimeSnapshot.NotProbed"/> until the first refresh finishes.</summary>
    public RuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The capabilities of <see cref="Current"/>.</summary>
    public RuntimeCapabilities Capabilities => Current.Capabilities;

    /// <summary>
    /// Brings <see cref="Current"/> up to date. Returns the existing answer without probing when the runtime's fingerprint is
    /// unchanged and the answer was known (or unknown less than <see cref="UnknownRetryAfter"/> ago), unless
    /// <paramref name="force"/>. Concurrent calls share one round; the caller's token only stops that caller waiting.
    /// </summary>
    public Task<RuntimeSnapshot> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Task<RuntimeSnapshot> flight;
        lock (_gate)
        {
            // The fingerprint (a PATH lookup, a file stamp) and the probes both run on the pool: a caller on the UI thread never waits on a disk.
            flight = _flight is { IsCompleted: false } running ? running : (_flight = Task.Run(() => RoundAsync(force)));
        }

        return flight.WaitAsync(cancellationToken);
    }

    private Task<RuntimeSnapshot> RoundAsync(bool force)
    {
        var fingerprint = SafeFingerprint();
        RuntimeSnapshot current;
        lock (_gate)
        {
            current = _current;
        }

        return !force && IsReusable(current, fingerprint) ? Task.FromResult(current) : ProbeAsync(fingerprint);
    }

    private bool IsReusable(RuntimeSnapshot current, string? fingerprint)
    {
        if (ReferenceEquals(current, RuntimeSnapshot.NotProbed) || !string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return current.IsKnown || _time.GetUtcNow() - current.ProbedAt < UnknownRetryAfter;
    }

    private string? SafeFingerprint()
    {
        try
        {
            return _fingerprint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<RuntimeSnapshot> ProbeAsync(string? fingerprint)
    {
        RuntimeSnapshot snapshot;
        if (fingerprint is null)
        {
            snapshot = RuntimeSnapshot.Failed("The DefenseClaw CLI was not found.", null, _time.GetUtcNow());
        }
        else
        {
            using var total = new CancellationTokenSource(TotalTimeout);
            try
            {
                var source = _source();
                var screens = await CollectAsync(source, total.Token).ConfigureAwait(false);
                snapshot = RuntimeProbe.Evaluate(screens, source, fingerprint, _time.GetUtcNow());
            }
            catch (OperationCanceledException)
            {
                snapshot = RuntimeSnapshot.Failed("Probing the runtime timed out.", fingerprint, _time.GetUtcNow());
            }
#pragma warning disable CA1031 // A runner that throws (a launcher bug, a vanished file) is "unknown", never an exception into a panel.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                snapshot = RuntimeSnapshot.Failed($"Probing the runtime failed ({ex.GetType().Name}).", fingerprint, _time.GetUtcNow());
            }
        }

        bool changed;
        lock (_gate)
        {
            changed = !Same(_current, snapshot);
            _current = snapshot;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return snapshot;
    }

    private static bool Same(RuntimeSnapshot a, RuntimeSnapshot b) =>
        Equals(a.Identity, b.Identity) &&
        string.Equals(a.UnknownReason, b.UnknownReason, StringComparison.Ordinal) &&
        a.Capabilities.IsKnown == b.Capabilities.IsKnown &&
        a.Capabilities.Present.SequenceEqual(b.Capabilities.Present) &&
        a.Capabilities.Notes.SequenceEqual(b.Capabilities.Notes, StringComparer.Ordinal) &&
        a.Capabilities.SetupCommands.Order(StringComparer.Ordinal).SequenceEqual(b.Capabilities.SetupCommands.Order(StringComparer.Ordinal)) &&
        a.Capabilities.AiRuntimeCommands.Order(StringComparer.Ordinal).SequenceEqual(b.Capabilities.AiRuntimeCommands.Order(StringComparer.Ordinal));

    private async Task<RuntimeProbeScreens> CollectAsync(string source, CancellationToken token)
    {
        using var throttle = new SemaphoreSlim(MaxParallelProbes, MaxParallelProbes);

        async Task<string?> Run(params string[] arguments)
        {
            await throttle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var one = CancellationTokenSource.CreateLinkedTokenSource(token);
                one.CancelAfter(ProbeTimeout);
                try
                {
                    var output = await _runner(arguments, one.Token).ConfigureAwait(false);
                    return output.Text;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null; // this one probe timed out; the round carries on
                }
            }
            finally
            {
                _ = throttle.Release();
            }
        }

        // The version document first: without it nothing else matters, and a CLI that is gone or wedged costs one probe, not all of them.
        var version = await Run("--version-json").ConfigureAwait(false);
        if (RuntimeProbe.ParseVersionJson(version, source) is null)
        {
            return new RuntimeProbeScreens(version, null, null, null, null, null, null, null, null, null);
        }

        var rootTask = Run("--help");
        var setupTask = Run("setup", "--help");
        var guardrailTask = Run("guardrail", "--help");
        var configTask = Run("config", "--help");

        // 0.8.10 has this screen too (it lists disable, enable, scan, setup and status); what it lists decides whether the runtime planes exist.
        var discoveryTask = Run("agent", "discovery", "--help");
        await Task.WhenAll(rootTask, setupTask, guardrailTask, configTask, discoveryTask).ConfigureAwait(false);

        var root = RuntimeProbe.ParseCommands(rootTask.Result);
        var setup = RuntimeProbe.ParseCommands(setupTask.Result);
        var discovery = RuntimeProbe.ParseCommands(discoveryTask.Result);

        // Second round, only for what the first one says exists: a runtime without 'acp' is never asked about it, and the runtime planes'
        // own screen is asked for when (and only when) 'agent discovery --help' lists 'runtime'. 0.8.10's does not, so it costs that
        // runtime nothing.
        var acpTask = root.Contains("acp") ? Run("acp", "--help") : Task.FromResult<string?>(null);
        var sandboxTask = root.Contains("sandbox") ? Run("sandbox", "--help") : Task.FromResult<string?>(null);
        var redactionTask = setup.Contains("redaction") ? Run("setup", "redaction", "--help") : Task.FromResult<string?>(null);
        var discoveryRuntimeTask = discovery.Contains("runtime") ? Run("agent", "discovery", "runtime", "--help") : Task.FromResult<string?>(null);
        await Task.WhenAll(acpTask, sandboxTask, redactionTask, discoveryRuntimeTask).ConfigureAwait(false);

        return new RuntimeProbeScreens(
            version,
            rootTask.Result,
            setupTask.Result,
            guardrailTask.Result,
            configTask.Result,
            sandboxTask.Result,
            acpTask.Result,
            redactionTask.Result,
            discoveryTask.Result,
            discoveryRuntimeTask.Result);
    }
}

/// <summary>The cache key for a runtime: changes when the CLI file does.</summary>
public static class RuntimeFingerprint
{
    /// <summary>
    /// <c>path|length|last-write ticks</c> for a CLI file, or null when it cannot be read (missing, access denied). A reinstall to
    /// the same version still moves the write time; an upgrade moves at least the version the probe then reads.
    /// </summary>
    public static string? ForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A container has no file to stamp, so its key is its name plus the time bucket the caller passes (the detector's retry
    /// window): the answer is trusted for that long and asked again after.
    /// </summary>
    public static string ForContainer(string name, long bucket) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"container:{name}|{bucket}");
}
