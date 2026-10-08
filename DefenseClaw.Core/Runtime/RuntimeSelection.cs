using System.Text.RegularExpressions;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Runtime;

/// <summary>Which DefenseClaw the app drives.</summary>
public enum RuntimeKind
{
    /// <summary>The one installed on this PC, found the usual way. What every normal user has, and the only choice unless Settings → Advanced says otherwise.</summary>
    Installed = 0,

    /// <summary>An explicit <c>defenseclaw.exe</c> with its own <c>DEFENSECLAW_HOME</c> (and optionally its own gateway port): a side-by-side install for development.</summary>
    Cli,

    /// <summary>A runtime inside a Docker container: commands go through <c>docker exec</c>, the gateway is the published loopback port, and the app reads a host copy of the container's data folder.</summary>
    Container,
}

/// <summary>
/// The developer's choice of runtime (Settings → Advanced). A value type with a default that is the normal installed runtime:
/// <see cref="Installed"/> changes nothing anywhere.
/// <para>
/// <b>Everything is loopback and secret-free.</b> The gateway URL must be <c>http</c> or <c>https</c> on <c>127.0.0.1</c>,
/// <c>localhost</c> or <c>[::1]</c> with no credentials, query or fragment: the bearer token the app reads from the selected data
/// folder is sent to that port and no other host. Nothing here is a secret, so nothing here can reach argv as one; the container
/// wrapper passes the environment <i>names</i> only (see <see cref="RuntimeLaunch"/>).
/// </para>
/// </summary>
public sealed partial record RuntimeSelection
{
    /// <summary>Container names Docker accepts: a letter or digit, then letters, digits, underscore, dot and hyphen.</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();

    /// <summary>The normal installed runtime.</summary>
    public static RuntimeSelection Installed { get; } = new();

    private readonly string? _cliPath;
    private readonly string? _homeDirectory;
    private readonly string? _gatewayUrl;
    private readonly string? _containerName;
    private readonly string? _hostDataFolder;

    public RuntimeKind Kind { get; init; }

    /// <summary>Full path to <c>defenseclaw.exe</c> (<see cref="RuntimeKind.Cli"/>).</summary>
    public string? CliPath { get => _cliPath; init => _cliPath = Clean(value); }

    /// <summary>The <c>DEFENSECLAW_HOME</c> the CLI and gateway run with (<see cref="RuntimeKind.Cli"/>).</summary>
    public string? HomeDirectory { get => _homeDirectory; init => _homeDirectory = Clean(value); }

    /// <summary>Gateway REST address, loopback only. Required for a container; optional for a CLI (then <c>gateway.api_port</c> in its config.yaml is used).</summary>
    public string? GatewayUrl { get => _gatewayUrl; init => _gatewayUrl = Clean(value); }

    /// <summary>Docker container name (<see cref="RuntimeKind.Container"/>).</summary>
    public string? ContainerName { get => _containerName; init => _containerName = Clean(value); }

    /// <summary>A host folder holding a copy of the container's data directory. The app only reads it.</summary>
    public string? HostDataFolder { get => _hostDataFolder; init => _hostDataFolder = Clean(value); }

    /// <summary>True for the normal installed runtime (nothing is overridden).</summary>
    public bool IsDefault => Kind == RuntimeKind.Installed;

    /// <summary>The data directory this selection dictates, or null when the usual resolution applies.</summary>
    public string? DataDirectoryOverride => Kind switch
    {
        RuntimeKind.Cli => HomeDirectory,
        RuntimeKind.Container => HostDataFolder,
        _ => null,
    };

    /// <summary>A selection for a side-by-side CLI.</summary>
    public static RuntimeSelection ForCli(string? cliPath, string? homeDirectory, string? gatewayUrl) =>
        new() { Kind = RuntimeKind.Cli, CliPath = cliPath, HomeDirectory = homeDirectory, GatewayUrl = gatewayUrl };

    /// <summary>A selection for a container.</summary>
    public static RuntimeSelection ForContainer(string? containerName, string? gatewayUrl, string? hostDataFolder) =>
        new() { Kind = RuntimeKind.Container, ContainerName = containerName, GatewayUrl = gatewayUrl, HostDataFolder = hostDataFolder };

    /// <summary>
    /// Why this selection cannot be used, in a sentence for the operator, or null when it can. Checks the shape of every field
    /// and that the files and folders it names exist; it does not start anything. The default selection is always valid.
    /// </summary>
    public string? Validate(Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;

        switch (Kind)
        {
            case RuntimeKind.Installed:
                return null;

            case RuntimeKind.Cli:
                if (CliPath is null)
                {
                    return "Choose the defenseclaw.exe to run.";
                }

                if (DefenseClawPaths.CheckCliPathOverride(CliPath, fileExists) is { } cliProblem)
                {
                    return cliProblem;
                }

                if (HomeDirectory is null)
                {
                    return "Name the DEFENSECLAW_HOME folder this runtime keeps its files in (a side-by-side install must not share the installed one's).";
                }

                if (CheckAbsoluteFolder(HomeDirectory, "DEFENSECLAW_HOME") is { } homeProblem)
                {
                    return homeProblem;
                }

                return GatewayUrl is null ? null : CheckGatewayUrl(GatewayUrl, out _);

            case RuntimeKind.Container:
                if (ContainerName is null || !ContainerNamePattern().IsMatch(ContainerName))
                {
                    return "Name the Docker container (letters, digits, underscore, dot and hyphen).";
                }

                if (GatewayUrl is null)
                {
                    return "Give the gateway address the container publishes, for example http://127.0.0.1:18971.";
                }

                if (CheckGatewayUrl(GatewayUrl, out _) is { } urlProblem)
                {
                    return urlProblem;
                }

                if (HostDataFolder is null)
                {
                    return "Choose the host folder holding a copy of the container's data directory.";
                }

                if (CheckAbsoluteFolder(HostDataFolder, "The data folder") is { } folderProblem)
                {
                    return folderProblem;
                }

                return directoryExists(HostDataFolder) ? null : "The data folder does not exist.";

            default:
                return "Unknown runtime kind.";
        }
    }

    /// <summary>The gateway port <see cref="GatewayUrl"/> names, when it names one and is valid.</summary>
    public bool TryGetGatewayPort(out int port)
    {
        port = 0;
        return GatewayUrl is not null && CheckGatewayUrl(GatewayUrl, out port) is null;
    }

    /// <summary>One line for About and the Settings page: what is selected.</summary>
    public string Describe() => Kind switch
    {
        RuntimeKind.Cli => $"Side-by-side CLI {CliPath} (home {HomeDirectory})",
        RuntimeKind.Container => $"Container {ContainerName} (gateway {GatewayUrl}, data copy {HostDataFolder})",
        _ => "Installed runtime",
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CheckAbsoluteFolder(string path, string what)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetPathRoot(path) is { Length: > 0 }
                ? null
                : $"{what} must be a full path, starting with a drive letter or a network share.";
        }
        catch (ArgumentException)
        {
            return $"{what} is not a valid path.";
        }
    }

    /// <summary>Why <paramref name="url"/> is not an acceptable gateway address, or null (and the port).</summary>
    public static string? CheckGatewayUrl(string url, out int port)
    {
        port = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "The gateway address must look like http://127.0.0.1:18971.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath.Length > 1))
        {
            return "The gateway address is a host and a port only: no credentials, path or query.";
        }

        if (!uri.IsLoopback)
        {
            return "The gateway address must be on this PC (127.0.0.1, localhost or [::1]): the gateway token is only sent there.";
        }

        if (uri.IsDefaultPort)
        {
            return "Include the gateway's port, for example http://127.0.0.1:18971.";
        }

        port = uri.Port;
        return null;
    }
}

/// <summary>
/// How a command reaches a selected runtime. Pure.
/// </summary>
public static class RuntimeLaunch
{
    /// <summary>The executable names that run inside a container; anything else (the scanners) is not part of a container runtime.</summary>
    public static bool RunsInContainer(string toolName) =>
        string.Equals(toolName, "defenseclaw", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "defenseclaw-gateway", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The argument list for <c>docker</c> that runs <paramref name="tool"/> with <paramref name="arguments"/> in the selected
    /// container: <c>exec [-i] [-e NAME ...] CONTAINER TOOL ARGS...</c>.
    /// <para>
    /// <b>Secrets never go on argv.</b> <paramref name="environmentNames"/> are names only; <c>-e NAME</c> without a value makes
    /// docker copy the variable from its own environment, which is where the runner puts the value (the child's environment block,
    /// not its command line). <c>-i</c> is added only when something is written to stdin, so a run with no stdin does not hold the
    /// exec session open.
    /// </para>
    /// </summary>
    public static List<string> ContainerArguments(
        RuntimeSelection selection,
        string tool,
        IReadOnlyList<string> arguments,
        IEnumerable<string> environmentNames,
        bool usesStdin)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentNames);

        if (selection.Kind != RuntimeKind.Container || selection.ContainerName is not { } container)
        {
            throw new InvalidOperationException("The selected runtime is not a container.");
        }

        var wrapped = new List<string> { "exec" };
        if (usesStdin)
        {
            wrapped.Add("-i");
        }

        foreach (var name in environmentNames)
        {
            wrapped.Add("-e");
            wrapped.Add(name);
        }

        wrapped.Add(container);
        wrapped.Add(tool);
        wrapped.AddRange(arguments);
        return wrapped;
    }
}
