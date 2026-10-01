using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>Outcome of one <c>--help</c> probe. Failure is data, not an exception.</summary>
/// <param name="Text">Combined stdout+stderr of the help screen.</param>
/// <param name="Error">Null on success; otherwise why the probe could not answer.</param>
public sealed record HelpProbeResult(string Text, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Runs <c>defenseclaw setup … --help</c> and caches successful help text until
/// <see cref="Clear"/> (the hub's Refresh) or the process ends - and, across processes, in
/// <see cref="SetupHelpDiskCache"/> for as long as the installed CLI is the same build.
/// <para>
/// <b>Why this does not go through <see cref="Core.Cli.CliRunner"/>.</b> The runner is the
/// app's write path: everything it executes lands in the Activity panel, whose contract is
/// "every DefenseClaw mutation this app makes", inside a 200-entry ring. Building the
/// catalog costs one probe per setup target — thirty-plus read-only invocations that would
/// bury the mutations Activity exists to record, on every first visit to the Setup hub.
/// A <c>--help</c> screen mutates nothing, carries no secret, and takes no input, so it is
/// treated as a read, like a REST GET or a SQLite query. Wizard <b>execution</b> still goes
/// through <see cref="Core.Cli.CliRunner"/>, argv and all.
/// </para>
/// <para>
/// Probes are ~800 ms each on 0.8.7 (Python start-up dominates), so the catalog fans them
/// out with <see cref="MaxParallelProbes"/> in flight and keeps the results (see below).
/// </para>
/// <para>
/// <b>Across launches.</b> A relaunch used to repeat the whole fan-out from zero. Successful screens
/// are now also written to <see cref="SetupHelpDiskCache"/> (under the app's own
/// <c>%LOCALAPPDATA%</c> folder, never <c>~/.defenseclaw</c>), keyed by the CLI's identity - its
/// resolved path, size and timestamp, and the version it reports via <c>--version-json</c> - so an
/// upgrade retires the file, and an unreadable or foreign file is simply ignored. The first launch
/// against a CLI behaves exactly as before (every screen is probed; the identity call runs alongside);
/// later launches against the same build answer from disk after that one identity call.
/// </para>
/// <para>
/// <b>What is and is not cached.</b> Only a probe that <i>succeeded</i> stays cached. A
/// failure — "not on PATH", a timeout, a non-zero exit — is an answer about a moment, not
/// about the CLI: the operator installs or repairs it and presses Refresh, and a cached
/// failure would make that a no-op for the rest of the process. Failed probes are evicted the
/// moment they finish, so the next request re-runs them, and <see cref="Clear"/> drops the
/// successes too.
/// </para>
/// <para>
/// <b>The caller's token never reaches the child.</b> One probe is shared by everyone who
/// asks for the same screen, so it must not be owned by whichever caller asked first: if that
/// caller cancelled (a panel closing mid-warm-up), every later reader of the cached task would
/// inherit a cancellation that was never theirs. The probe runs on its own bounded lifetime
/// (<see cref="ProbeTimeout"/>); a caller's token only stops <i>that caller</i> waiting.
/// </para>
/// </summary>
public sealed class SetupHelpProbe
{
    /// <summary>Bounded so a catalog warm-up cannot spawn thirty interpreters at once.</summary>
    public const int MaxParallelProbes = 6;

    /// <summary>
    /// First element of a path that asks for a screen of the CLI itself rather than of <c>setup</c> (the command palette's
    /// curated commands): <c>@cli doctor</c> is <c>defenseclaw doctor --help</c>. It is part of the cache key, so the two
    /// families never collide, and those screens do not count in <see cref="CachedProbeCount"/> (the Setup hub's footer).
    /// </summary>
    internal const string RootMarker = "@cli";

    /// <summary>A single help screen has never taken close to this; it guards a hung child.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly DefenseClawPaths _paths;
    private readonly SetupHelpDiskCache? _diskCache;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>> _runHelp;

    // Lazy, not a bare Task: ConcurrentDictionary.GetOrAdd may run its value factory on several
    // threads for one key, and here the factory spawns a process. ExecutionAndPublication makes
    // sure exactly one probe runs per entry no matter how many callers race.
    private readonly ConcurrentDictionary<string, Lazy<Task<HelpProbeResult>>> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(MaxParallelProbes, MaxParallelProbes);

    /// <summary>The production probe: real CLI, disk cache under the app's local data directory.</summary>
    public SetupHelpProbe(DefenseClawPaths paths)
        : this(paths, new SetupHelpDiskCache(SetupHelpDiskCache.DefaultFilePath(), ReadCliVersionAsync), ExecuteHelpAsync)
    {
    }

    /// <summary>
    /// Test seam: a probe over a fake runner and (optionally) a scratch-directory cache, so a test
    /// never starts a process or touches the real <c>%LOCALAPPDATA%</c> file. A null
    /// <paramref name="diskCache"/> is the in-memory-only behaviour this class had before.
    /// </summary>
    internal SetupHelpProbe(
        DefenseClawPaths paths,
        SetupHelpDiskCache? diskCache,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>> runHelp)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _diskCache = diskCache;
        _runHelp = runHelp ?? throw new ArgumentNullException(nameof(runHelp));
    }

    /// <summary>
    /// Number of help screens held right now: successful reads plus any still in flight.
    /// Failed probes are evicted, so this is "read so far", never "attempted". Surfaced in the
    /// hub's footer note.
    /// </summary>
    public int CachedProbeCount => _cache.Keys.Count(key => !key.StartsWith(RootMarker, StringComparison.Ordinal));

    /// <summary>
    /// Forgets every cached help screen so the next <see cref="HelpAsync"/> re-reads the CLI —
    /// what the hub's "Re-read catalog" means. Probes already running finish and answer the
    /// callers that started them, but are not re-cached (they were removed with the rest), so
    /// a stale answer cannot outlive the refresh that asked to forget it. The on-disk copy goes too
    /// (and is not read again this run): a refresh asks the CLI, not a file written earlier.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _diskCache?.Invalidate();
    }

    /// <summary>
    /// <c>defenseclaw setup <paramref name="path"/> --help</c>, cached by argument path while it
    /// keeps succeeding. Pass an empty array for the top-level <c>setup --help</c>.
    /// <paramref name="cancellationToken"/> only abandons this caller's wait (the returned task
    /// ends cancelled); the probe itself is shared and runs on.
    /// </summary>
    public Task<HelpProbeResult> HelpAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var key = string.Join(' ', path);
        var candidate = new Lazy<Task<HelpProbeResult>>(
            () => RunAsync(path),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var entry = _cache.GetOrAdd(key, candidate);
        var probe = entry.Value;

        if (ReferenceEquals(entry, candidate))
        {
            // Only the caller whose entry was stored watches it, so eviction is attached exactly
            // once. The pair form of TryRemove removes this very entry and nothing newer: a
            // Clear() followed by a fresh probe under the same key is left alone.
            _ = probe.ContinueWith(
                finished =>
                {
                    if (finished.Status != TaskStatus.RanToCompletion || !finished.Result.Succeeded)
                    {
                        _ = _cache.TryRemove(new KeyValuePair<string, Lazy<Task<HelpProbeResult>>>(key, entry));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return cancellationToken.CanBeCanceled ? probe.WaitAsync(cancellationToken) : probe;
    }

    /// <summary>
    /// <c>defenseclaw <paramref name="path"/> --help</c> (empty for the top-level screen), cached like <see cref="HelpAsync"/>.
    /// Only ever <c>--help</c>: it reads a screen and runs nothing.
    /// </summary>
    public Task<HelpProbeResult> CliHelpAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return HelpAsync(new[] { RootMarker }.Concat(path).ToArray(), cancellationToken);
    }

    private async Task<HelpProbeResult> RunAsync(IReadOnlyList<string> path)
    {
        var executable = _paths.FindExecutable("defenseclaw");
        if (executable is null)
        {
            return new HelpProbeResult(string.Empty, "defenseclaw is not on PATH or in the installer's bin directory.");
        }

        // Recorded before anything else, so an answer that lands after a Clear() is dropped, not stored.
        var key = string.Join(' ', path);
        var generation = _diskCache?.Generation ?? 0;

        if (_diskCache is not null)
        {
            // Answered from disk only when the installed CLI is the build that wrote the file; no
            // process is started for it. Either way this also starts the CLI's identity resolving,
            // so on a miss it runs alongside the probe below rather than ahead of it.
            var persisted = await _diskCache.TryGetAsync(executable, key).ConfigureAwait(false);
            if (persisted is not null)
            {
                return new HelpProbeResult(persisted, null);
            }
        }

        HelpProbeResult result;
        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            result = await _runHelp(executable, path, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
        }

        if (result.Succeeded)
        {
            // Filed in the background: persisting is bookkeeping, and a cold cache is still waiting on
            // the CLI's identity here (a second start-up alongside this probe). The caller gets its
            // answer now, exactly as it did before there was a disk cache.
            _diskCache?.Store(executable, key, result.Text, generation);
        }

        return result;
    }

    /// <summary>
    /// The CLI's own version, from <c>defenseclaw --version-json</c> (0.8.10+), or null when it
    /// cannot say - an older CLI that predates the flag, a failed start, unparseable output. Null
    /// means the disk cache is not used for that CLI, so nothing is ever keyed to a guess.
    /// </summary>
    internal static async Task<string?> ReadCliVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(executable, new[] { "--version-json" }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        // Stdout and stderr arrive combined; the JSON object is the line that starts with a brace.
        foreach (var line in result.Text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{') && Updates.UpdateChecker.TryParseVersionJson(trimmed) is { } version)
            {
                return version;
            }
        }

        return null;
    }

    internal static Task<HelpProbeResult> ExecuteHelpAsync(
        string executable,
        IReadOnlyList<string> path,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(path.Count + 2);
        if (path.Count > 0 && string.Equals(path[0], RootMarker, StringComparison.Ordinal))
        {
            arguments.AddRange(path.Skip(1));
        }
        else
        {
            arguments.Add("setup");
            arguments.AddRange(path);
        }

        arguments.Add("--help");
        return ExecuteAsync(executable, arguments, cancellationToken);
    }

    private static async Task<HelpProbeResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Click wraps to the terminal width; with no console attached it falls back to 80,
        // which is exactly the layout SetupHelpParser was written against. Pinning COLUMNS
        // keeps that stable no matter how the app was launched.
        startInfo.Environment["COLUMNS"] = "80";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new HelpProbeResult(string.Empty, "The defenseclaw process could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new HelpProbeResult(string.Empty, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new HelpProbeResult(string.Empty, ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new HelpProbeResult(string.Empty, "The help probe timed out.");
        }

        string text;
        try
        {
            text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        }
        catch (IOException ex)
        {
            return new HelpProbeResult(string.Empty, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new HelpProbeResult(string.Empty, "The help probe was cancelled.");
        }

        // Click prints usage to stdout and exits 0 for --help; a non-zero exit means the
        // target does not exist on this build, which is a real answer worth surfacing.
        return process.ExitCode == 0 || text.Contains("Usage:", StringComparison.Ordinal)
            ? new HelpProbeResult(text, null)
            : new HelpProbeResult(text, $"defenseclaw exited {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
