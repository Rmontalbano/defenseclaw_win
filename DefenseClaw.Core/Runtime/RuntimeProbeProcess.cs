using System.Diagnostics;
using System.Text;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Runtime;

/// <summary>
/// Builds the paths for a selected runtime, and the probe runner that talks to it.
/// </summary>
public static class RuntimeEnvironment
{
    /// <summary>
    /// The paths for <paramref name="selection"/>. For <see cref="RuntimeSelection.Installed"/> this is exactly <c>new DefenseClawPaths()</c>
    /// and nothing else: the default path is not merely equivalent, it is the same call.
    /// <list type="bullet">
    /// <item><description><b>Cli</b>: the data directory is the chosen <c>DEFENSECLAW_HOME</c>; executables are looked up only in the CLI's own
    /// folder (never PATH, so the gateway beside it is the one used and the installed one is not); the CLI override is pinned.</description></item>
    /// <item><description><b>Container</b>: the data directory is the host copy, read-only; PATH is kept, because <c>docker</c> is found on it.</description></item>
    /// </list>
    /// </summary>
    public static DefenseClawPaths CreatePaths(RuntimeSelection? selection)
    {
        if (selection is null || selection.IsDefault)
        {
            return new DefenseClawPaths();
        }

        switch (selection.Kind)
        {
            case RuntimeKind.Cli when selection.CliPath is { } cli && selection.HomeDirectory is { } home:
            {
                var paths = new DefenseClawPaths(
                    dataDirectory: home,
                    binDirectory: Path.GetDirectoryName(cli),
                    searchPath: Array.Empty<string>(),
                    runtime: selection);
                paths.SetCliPathOverride(cli);
                return paths;
            }

            case RuntimeKind.Container when selection.HostDataFolder is { } data:
                return new DefenseClawPaths(dataDirectory: data, runtime: selection);

            default:
                // An incomplete selection never half-applies: the installed runtime is used, and Settings says why it was refused.
                return new DefenseClawPaths();
        }
    }

    /// <summary>
    /// The cache key for the runtime <paramref name="paths"/> drives: the CLI file's stamp, or for a container its name in a
    /// time bucket of <paramref name="containerWindow"/>.
    /// </summary>
    public static string? Fingerprint(DefenseClawPaths paths, TimeProvider? time = null, TimeSpan? containerWindow = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Runtime is { Kind: RuntimeKind.Container, ContainerName: { } name })
        {
            var window = containerWindow ?? RuntimeDetector.DefaultUnknownRetryAfter * 4;
            var now = (time ?? TimeProvider.System).GetUtcNow();
            return RuntimeFingerprint.ForContainer(name, now.ToUnixTimeSeconds() / Math.Max(1, (long)window.TotalSeconds));
        }

        return RuntimeFingerprint.ForFile(paths.CliPath);
    }

    /// <summary>Where answers come from, for <see cref="RuntimeIdentity.Source"/>.</summary>
    public static string Source(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Runtime is { Kind: RuntimeKind.Container, ContainerName: { } name }
            ? "docker exec " + name
            : paths.CliPath ?? "defenseclaw";
    }

    /// <summary>
    /// The production probe runner: starts the selected runtime's <c>defenseclaw</c> with the (read-only) probe arguments and
    /// returns what it printed. Nothing is recorded in the Activity ring.
    /// </summary>
    public static RuntimeProbeRunner CreateProbeRunner(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return (arguments, token) => RunProbeAsync(paths, arguments, token);
    }

    private static async Task<RuntimeProbeOutput> RunProbeAsync(DefenseClawPaths paths, IReadOnlyList<string> arguments, CancellationToken token)
    {
        // The probes are help and version screens only. This is the whole of what the runner will launch, so a bug elsewhere
        // cannot turn a capability check into a command.
        if (!IsReadOnlyProbe(arguments))
        {
            return RuntimeProbeOutput.Fail("Not a read-only probe.");
        }

        var container = paths.Runtime.Kind == RuntimeKind.Container;
        var executable = await paths.FindExecutableAsync(container ? "docker" : DefenseClawPaths.CliExecutableName).ConfigureAwait(false);
        if (executable is null)
        {
            return RuntimeProbeOutput.Fail(container ? "docker was not found on PATH." : "The DefenseClaw CLI was not found.");
        }

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

        var effective = container
            ? RuntimeLaunch.ContainerArguments(paths.Runtime, DefenseClawPaths.CliExecutableName, arguments, [], usesStdin: false)
            : arguments.ToList();
        foreach (var argument in effective)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Click wraps to the terminal width and falls back to 80 with no console; pinning it keeps the layout stable.
        startInfo.Environment["COLUMNS"] = "80";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        if (paths.Runtime is { Kind: RuntimeKind.Cli, HomeDirectory: { } home })
        {
            startInfo.Environment[DefenseClawPaths.HomeVariableName] = home;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return RuntimeProbeOutput.Fail("The process could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return RuntimeProbeOutput.Fail(ex.Message);
        }

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);

            // Click prints usage and exits 0 for --help. A non-zero exit that still printed a usage screen (an older Click) is an answer too;
            // one that printed "No such command" is the answer "this runtime does not have it", which is not an error worth keeping text for.
            return process.ExitCode == 0 ? RuntimeProbeOutput.Ok(text) : RuntimeProbeOutput.Fail($"exit {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (IOException ex)
        {
            return RuntimeProbeOutput.Fail(ex.Message);
        }
    }

    /// <summary>True for exactly the arguments the detector sends: words, then <c>--help</c> or just <c>--version-json</c>.</summary>
    internal static bool IsReadOnlyProbe(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return false;
        }

        if (arguments.Count == 1 && arguments[0] == "--version-json")
        {
            return true;
        }

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            var word = arguments[i];
            if (word.Length == 0 || word[0] == '-')
            {
                return false;
            }
        }

        return arguments[^1] == "--help";
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
