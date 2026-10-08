using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Cli;

/// <summary>Which DefenseClaw executable a registry entry's argv is handed to.</summary>
public enum TuiBinary
{
    /// <summary><c>defenseclaw</c>, the Python CLI.</summary>
    Cli,

    /// <summary>
    /// <c>defenseclaw-gateway</c>. An entry for it always has an argv: the gateway run with no arguments starts a second sidecar daemon,
    /// so a catalogue never holds a bare one (a test and the generator both enforce it).
    /// </summary>
    Gateway,
}

/// <summary>
/// One entry of a DefenseClaw runtime's TUI command registry (<c>defenseclaw/tui/registry_data.py</c>): the tuple the TUI's own
/// command palette is built from, plus whether the runtime itself can run it on Windows. Generated data, see
/// <c>tools/gen-tui-registry.py</c>; the app adds the review tier and the run policy on top (<c>CuratedCommand</c>), none of it here.
/// </summary>
/// <param name="Name">What the TUI calls it (<c>scan skill --all</c>); unique within a registry, and several names may share one argv.</param>
/// <param name="Binary">The executable the argv is for.</param>
/// <param name="Argv">The arguments, without the executable. Never a shell line; the only options in it are <see cref="TuiRegistryCatalogues.ReviewedFlags"/>.</param>
/// <param name="Description">The TUI's one-line description.</param>
/// <param name="Category">The TUI's category, lower case: setup, info, enforce, policy, scan, daemon, sandbox, install or other.</param>
/// <param name="NeedsArgument">True when the TUI asks for more text after the name (a skill name, a URL).</param>
/// <param name="ArgumentHint">What that text is, in the TUI's words (<c>&lt;skill-name&gt;</c>); empty exactly when <paramref name="NeedsArgument"/> is false.</param>
/// <param name="WindowsUnavailable">Null when Windows runs it. Otherwise the runtime's own reason it does not (its platform table's sentence).</param>
public sealed record TuiRegistryEntry(
    string Name,
    TuiBinary Binary,
    IReadOnlyList<string> Argv,
    string Description,
    string Category,
    bool NeedsArgument,
    string ArgumentHint,
    string? WindowsUnavailable)
{
    /// <summary>The name of the Python CLI's executable.</summary>
    public const string CliExecutable = "defenseclaw";

    /// <summary>The name of the Go gateway's executable.</summary>
    public const string GatewayExecutable = "defenseclaw-gateway";

    /// <summary><see cref="CliExecutable"/> or <see cref="GatewayExecutable"/>, the name <paramref name="Argv"/> is handed to.</summary>
    public string Executable => Binary == TuiBinary.Gateway ? GatewayExecutable : CliExecutable;

    /// <summary>True when the runtime's own platform table lets Windows run it.</summary>
    public bool IsAvailableOnWindows => WindowsUnavailable is null;
}

/// <summary>
/// The TUI command registry of one runtime, in the order the registry lists it. <see cref="Entries"/> is everything the registry has;
/// <see cref="OnWindows"/> is what this app offers; <see cref="HiddenOnWindows"/> is the rest, each with its reason, so the palette can say
/// how many it left out and why.
/// </summary>
public sealed class TuiRegistryCatalogue
{
    private readonly Dictionary<string, TuiRegistryEntry> _byName;

    internal TuiRegistryCatalogue(string runtime, string source, string sourceSha256, int sourceEntries, IReadOnlyList<TuiRegistryEntry> entries)
    {
        Runtime = runtime;
        Source = source;
        SourceSha256 = sourceSha256;
        SourceEntries = sourceEntries;
        Entries = entries;
        OnWindows = entries.Where(e => e.IsAvailableOnWindows).ToArray();
        HiddenOnWindows = entries.Where(e => !e.IsAvailableOnWindows).ToArray();
        _byName = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    }

    /// <summary>The runtime the registry belongs to, as the generated header words it ("DefenseClaw 0.8.10, the installed Windows build").</summary>
    public string Runtime { get; }

    /// <summary>The file it was read from, relative to its package or repository (<c>defenseclaw/tui/registry_data.py</c>).</summary>
    public string Source { get; }

    /// <summary>SHA-256 of that file with LF line endings, so a refresh can tell whether the source moved.</summary>
    public string SourceSha256 { get; }

    /// <summary>How many tuples the source file held when it was read; <see cref="Entries"/> has exactly that many.</summary>
    public int SourceEntries { get; }

    /// <summary>Every entry the runtime's registry lists.</summary>
    public IReadOnlyList<TuiRegistryEntry> Entries { get; }

    /// <summary>The entries Windows runs, in registry order.</summary>
    public IReadOnlyList<TuiRegistryEntry> OnWindows { get; }

    /// <summary>The entries it does not, each carrying its reason.</summary>
    public IReadOnlyList<TuiRegistryEntry> HiddenOnWindows { get; }

    /// <summary>Why entries are hidden, one line per distinct reason with how many it covers, the largest group first.</summary>
    public IReadOnlyList<(string Reason, int Count)> HiddenReasons =>
        HiddenOnWindows
            .GroupBy(e => e.WindowsUnavailable!, StringComparer.Ordinal)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Reason, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The entry the TUI calls <paramref name="name"/>, or null.</summary>
    public TuiRegistryEntry? Find(string name) => name is not null && _byName.TryGetValue(name, out var entry) ? entry : null;
}

/// <summary>
/// The command catalogues, one per runtime the app knows, and the choice between them. The TUI registry is not the same across
/// runtimes (the pinned newer source lists 253 entries, with connectors and runtime planes the installed 0.8.10 does not have, and
/// 232 of them on Windows against 210), so the one the app shows follows what the connected runtime reports about itself: a runtime
/// with <see cref="RuntimeCapability.TuiRegistry"/> gets <see cref="Extended"/>, everything else - including a runtime that has not
/// answered yet - gets <see cref="Baseline"/>, which is what 0.8.10 has and so is never gated.
/// </summary>
public static class TuiRegistryCatalogues
{
    /// <summary>
    /// The only options an entry's argv may carry. Each was read in the registry of both runtimes (what it does, in the runtime's own help)
    /// and none carries or prints a credential: <c>--show</c> prints settings with the key masked, <c>--section</c> names a config section,
    /// the rest are switches. The TUI's own test for a secret flag is a name containing key, token, secret, password, credential or value;
    /// none does. A regenerated catalogue with a flag outside this set fails its test until someone has read what the flag does and added it here.
    /// </summary>
    public static IReadOnlySet<string> ReviewedFlags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "--all", "--dry-run", "--effective", "--fix", "--help", "--json", "--no-enable-host-plane", "--no-tui", "--non-interactive",
        "--refresh", "--section", "--show", "--verify", "--yes",
    };

    /// <summary>The installed DefenseClaw 0.8.10's registry (231 entries).</summary>
    public static TuiRegistryCatalogue Baseline { get; } = new(
        TuiRegistryData.BaselineRuntime,
        TuiRegistryData.BaselineSource,
        TuiRegistryData.BaselineSourceSha256,
        TuiRegistryData.BaselineSourceEntries,
        TuiRegistryData.Baseline);

    /// <summary>The pinned newer source's registry (253 entries; see <see cref="RuntimeCapabilityCatalog.VerifiedCommit"/>).</summary>
    public static TuiRegistryCatalogue Extended { get; } = new(
        TuiRegistryData.ExtendedRuntime,
        TuiRegistryData.ExtendedSource,
        TuiRegistryData.ExtendedSourceSha256,
        TuiRegistryData.ExtendedSourceEntries,
        TuiRegistryData.Extended);

    /// <summary>The catalogue for a runtime with these capabilities: <see cref="Extended"/> when it has <see cref="RuntimeCapability.TuiRegistry"/>, otherwise <see cref="Baseline"/>.</summary>
    public static TuiRegistryCatalogue For(RuntimeCapabilities? capabilities) =>
        capabilities is not null && capabilities.Has(RuntimeCapability.TuiRegistry) ? Extended : Baseline;

    /// <summary>
    /// Why <paramref name="argv"/> must not be run from a catalogue, or null when it may: it is empty, a token is empty, has whitespace or
    /// shell syntax, or an option that is not one of <see cref="ReviewedFlags"/> (which keeps every flag that carries a credential
    /// out, whatever a future registry adds). The argv is checked before any value the operator types is added to it.
    /// </summary>
    public static string? ArgvProblem(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0)
        {
            return "There is no command.";
        }

        foreach (var token in argv)
        {
            if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.IndexOfAny(ShellCharacters) >= 0)
            {
                return "A word in the command is empty or has a space or shell syntax in it.";
            }

            if (token.StartsWith('-') && !ReviewedFlags.Contains(token))
            {
                return $"The option {token} is not one the command catalogue has been reviewed for.";
            }
        }

        return null;
    }

    private static readonly char[] ShellCharacters = { ';', '&', '|', '<', '>', '`', '$', '(', ')', '%', '"', '\'', '\\', '*', '?', '~', '^', '=', '\n', '\r' };
}
