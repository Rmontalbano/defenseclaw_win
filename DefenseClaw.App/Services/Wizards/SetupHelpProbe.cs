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
/// <see cref="Clear"/> (the hub's Refresh) or the process ends.
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

    /// <summary>A single help screen has never taken close to this; it guards a hung child.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly DefenseClawPaths _paths;

    // Lazy, not a bare Task: ConcurrentDictionary.GetOrAdd may run its value factory on several
    // threads for one key, and here the factory spawns a process. ExecutionAndPublication makes
    // sure exactly one probe runs per entry no matter how many callers race.
    private readonly ConcurrentDictionary<string, Lazy<Task<HelpProbeResult>>> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(MaxParallelProbes, MaxParallelProbes);

    public SetupHelpProbe(DefenseClawPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Number of help screens held right now: successful reads plus any still in flight.
    /// Failed probes are evicted, so this is "read so far", never "attempted". Surfaced in the
    /// hub's footer note.
    /// </summary>
    public int CachedProbeCount => _cache.Count;

    /// <summary>
    /// Forgets every cached help screen so the next <see cref="HelpAsync"/> re-reads the CLI —
    /// what the hub's "Re-read catalog" means. Probes already running finish and answer the
    /// callers that started them, but are not re-cached (they were removed with the rest), so
    /// a stale answer cannot outlive the refresh that asked to forget it.
    /// </summary>
    public void Clear() => _cache.Clear();

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

    private async Task<HelpProbeResult> RunAsync(IReadOnlyList<string> path)
    {
        var executable = _paths.FindExecutable("defenseclaw");
        if (executable is null)
        {
            return new HelpProbeResult(string.Empty, "defenseclaw is not on PATH or in the installer's bin directory.");
        }

        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(executable, path, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static async Task<HelpProbeResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> path,
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

        startInfo.ArgumentList.Add("setup");
        foreach (var segment in path)
        {
            startInfo.ArgumentList.Add(segment);
        }

        startInfo.ArgumentList.Add("--help");

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
