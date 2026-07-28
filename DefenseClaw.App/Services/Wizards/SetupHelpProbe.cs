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
/// Runs <c>defenseclaw setup … --help</c> and caches the text for the life of the process.
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
/// out with <see cref="MaxParallelProbes"/> in flight and caches per app run.
/// </para>
/// </summary>
public sealed class SetupHelpProbe
{
    /// <summary>Bounded so a catalog warm-up cannot spawn thirty interpreters at once.</summary>
    public const int MaxParallelProbes = 6;

    /// <summary>A single help screen has never taken close to this; it guards a hung child.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly DefenseClawPaths _paths;
    private readonly ConcurrentDictionary<string, Task<HelpProbeResult>> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(MaxParallelProbes, MaxParallelProbes);

    public SetupHelpProbe(DefenseClawPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>Number of distinct help screens read so far. Surfaced in the hub's footer note.</summary>
    public int CachedProbeCount => _cache.Count;

    /// <summary>
    /// <c>defenseclaw setup <paramref name="path"/> --help</c>, cached by argument path.
    /// Pass an empty array for the top-level <c>setup --help</c>.
    /// </summary>
    public Task<HelpProbeResult> HelpAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var key = string.Join(' ', path);
        return _cache.GetOrAdd(key, _ => RunAsync(path, cancellationToken));
    }

    private async Task<HelpProbeResult> RunAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        var executable = _paths.FindExecutable("defenseclaw");
        if (executable is null)
        {
            return new HelpProbeResult(string.Empty, "defenseclaw is not on PATH or in the installer's bin directory.");
        }

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(executable, path, cancellationToken).ConfigureAwait(false);
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
