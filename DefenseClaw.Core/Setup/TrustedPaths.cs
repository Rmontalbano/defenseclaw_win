using System.Text.Json;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// One row of <c>defenseclaw setup trusted-paths list --json</c> (0.8.10 <c>cmd_setup._collect_trusted_prefixes</c>): a directory connector
/// binaries may be run from while DefenseClaw looks for them. Unlike the TUI, which reads the same function in-process, the app asks the CLI,
/// and the CLI has had this <c>--json</c> since the group was added.
/// </summary>
/// <param name="Path">The entry as it was written (config.yaml, .env or the environment).</param>
/// <param name="Resolved">The absolute path the CLI checks and the one <c>remove</c> is given.</param>
/// <param name="Source"><c>default</c> (built in), <c>config</c> (ai_discovery.trusted_binary_prefixes), <c>legacy .env</c>, or <c>env</c> (only the process environment).</param>
/// <param name="Status"><c>ok</c>, <c>missing</c>, <c>not-a-dir</c>, <c>unsafe-permissions</c> or <c>error</c>.</param>
/// <param name="Removable">True for an entry the operator added to config.yaml or .env: <c>remove</c> can take it out. Never true for a built-in.</param>
public sealed record TrustedPath(string Path, string Resolved, string Source, string Status, bool Removable)
{
    public const string DefaultSource = "default";
    public const string EnvironmentSource = "env";

    /// <summary>A built-in default: protected, and the CLI refuses to remove it.</summary>
    public bool IsBuiltIn => string.Equals(Source, DefaultSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>Only the running process's environment says so (not persisted anywhere this editor can change).</summary>
    public bool IsEnvironmentOnly => string.Equals(Source, EnvironmentSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the directory is in good order: it exists, is a directory, and no one else can write to it.</summary>
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>The TUI's "Owned" column: "yes" for what the operator added.</summary>
    public string Owned => Removable ? "yes" : "-";
}

/// <summary>
/// Reads <c>defenseclaw setup trusted-paths list --json</c>. A row needs the resolved path and where it came from; whether it can be removed
/// is true only when the CLI says so (a field that is missing or not a boolean is "not removable"). The allow-list is never empty in a working
/// install (the built-in defaults are always in it), and nothing printed is a failed read: "could not read the allow-list" is not "no trusted paths".
/// </summary>
public static class TrustedPathParser
{
    public static bool TryParse(string? text, out IReadOnlyList<TrustedPath> rows, out string error) =>
        SetupJson.TryReadRows(text, "The trusted-path list", Read, out rows, out error);

    private static TrustedPath Read(JsonElement element)
    {
        var resolved = SetupJson.RequiredName(element, "resolved");
        var path = SetupJson.StringOf(element, "path");
        return new TrustedPath(
            Path: path.Length > 0 ? path : resolved,
            Resolved: resolved,
            Source: DisplayNames.Visible(SetupJson.RequiredText(element, "source")),
            Status: DisplayNames.Visible(SetupJson.StringOf(element, "status").Trim() is { Length: > 0 } status ? status : "unknown"),
            Removable: SetupJson.Flag(element, "removable"));
    }
}

/// <summary>A connector whose program sits in a directory that is not trusted, from the last discovery scan.</summary>
/// <param name="Connector">The connector's name as discovery records it (<c>claudecode</c>, <c>codex</c>, ...).</param>
/// <param name="Directory">The directory its program is in: the one to trust.</param>
public sealed record UntrustedConnector(string Connector, string Directory);

/// <summary>Which folders the editors will suggest trusting: the ones discovery names for a connector's program, and the one a failed setup asks to have trusted.</summary>
public static class TrustedFolders
{
    /// <summary>The longest folder suggested: a Windows path is at most 260 characters, and a long-path one rarely reaches twice that.</summary>
    public const int MaxOfferedLength = 520;

    /// <summary>
    /// True for a folder the editors will put in front of the add wizard as a suggestion: an absolute path of at most <see cref="MaxOfferedLength"/>
    /// characters that the CLI could not read as an option (no leading dash) and that has no control or direction character in it. A suggestion is a
    /// value the operator has to be able to read and would have typed; anything else is left out, not shown escaped as if it were that folder.
    /// </summary>
    public static bool IsOffered(string? folder) =>
        folder is { Length: > 2 and <= MaxOfferedLength }
        && folder[0] != '-'
        && Path.IsPathRooted(folder)
        && string.Equals(DisplayNames.Visible(folder), folder, StringComparison.Ordinal);
}

/// <summary>What <see cref="UntrustedConnectorScan"/> found.</summary>
/// <param name="Read">True when the discovery file was read; false says <paramref name="Note"/> why not.</param>
/// <param name="ScannedAt">When the scan ran; null when the file does not say.</param>
/// <param name="Connectors">The connectors whose program was skipped for being in an untrusted directory, by connector.</param>
/// <param name="Note">Why nothing could be said, or empty.</param>
public sealed record UntrustedConnectorReport(bool Read, DateTimeOffset? ScannedAt, IReadOnlyList<UntrustedConnector> Connectors, string Note)
{
    public static UntrustedConnectorReport Unavailable(string note) => new(false, null, Array.Empty<UntrustedConnector>(), note);
}

/// <summary>
/// The TUI's proactive highlight on the Trusted Paths editor (<c>untrusted_connector_dirs</c>): which connectors are in a directory that is not
/// trusted. The TUI runs discovery again in-process; the app has no read-only command that does, so it reads what the last scan left in
/// <c>agent_discovery.json</c> (the file AI Discovery reads) - the same <c>error</c> and <c>binary_path</c> of each connector the TUI looks at - and
/// says when that scan was. A directory trusted since is the caller's to leave out (it knows the allow-list).
/// </summary>
public static class UntrustedConnectorScan
{
    /// <summary>The text <c>agent_discovery.UNTRUSTED_PREFIX_ERROR</c> records on a connector whose program is outside every trusted directory.</summary>
    public const string UntrustedPrefixError = "binary path is not in a trusted install prefix";

    /// <summary>Reads the text of <c>agent_discovery.json</c>. Never throws for text that is not that file: it is "not read", with a reason.</summary>
    public static UntrustedConnectorReport Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return UntrustedConnectorReport.Unavailable("The discovery file is empty, so connectors outside a trusted folder cannot be listed.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Object)
            {
                return UntrustedConnectorReport.Unavailable("The discovery file has no list of connectors, so connectors outside a trusted folder cannot be listed.");
            }

            DateTimeOffset? scanned = DateTimeOffset.TryParse(
                SetupJson.StringOf(root, "scanned_at"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var when)
                ? when
                : null;

            var found = new List<UntrustedConnector>();
            foreach (var agent in agents.EnumerateObject().OrderBy(static a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (agent.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var binary = SetupJson.StringOf(agent.Value, "binary_path").Trim();
                if (binary.Length == 0 || !string.Equals(SetupJson.StringOf(agent.Value, "error").Trim(), UntrustedPrefixError, StringComparison.Ordinal))
                {
                    continue;
                }

                var directory = ParentOf(binary);
                if (TrustedFolders.IsOffered(directory))
                {
                    var name = SetupJson.StringOf(agent.Value, "name").Trim();
                    found.Add(new UntrustedConnector(DisplayNames.Visible(name.Length > 0 ? name : agent.Name), directory));
                }
            }

            return new UntrustedConnectorReport(true, scanned, found, string.Empty);
        }
        catch (JsonException)
        {
            return UntrustedConnectorReport.Unavailable("The discovery file could not be read, so connectors outside a trusted folder cannot be listed.");
        }
    }

    /// <summary>The directory a binary is in, as the TUI's <c>os.path.dirname(os.path.realpath(binary))</c> (without resolving links).</summary>
    private static string ParentOf(string binaryPath)
    {
        var cut = binaryPath.LastIndexOfAny(['\\', '/']);
        return cut > 0 ? binaryPath[..cut] : string.Empty;
    }
}
