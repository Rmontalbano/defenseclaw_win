using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// What the catalog needs that no command of the pinned runtime prints: its built-in chain catalog, the rule families of a rule pack, and
/// the base of a composed pack. They are the runtime's own files, read where the runtime keeps them (never written, never executed);
/// a file that is missing or unreadable is an empty answer, as in the runtime's own catalog, not an error.
/// </summary>
public interface IPolicyDataFiles
{
    /// <summary>The runtime's built-in tool-call chains. <paramref name="policies"/> are the named policies the runtime listed: a built-in one's path leads to the runtime's data folder.</summary>
    IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies);

    /// <summary>The rule families of the pack in <paramref name="packFolder"/>: the built-in default families, replaced or extended by the pack's rule files.</summary>
    IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies);

    /// <summary>The pack a composed pack folder was built on (its <c>defenseclaw-pack.json</c>); null when it has none.</summary>
    string? ReadPackBase(string packFolder);
}

/// <summary>No files: the chains and families views then say the catalog was not found. For a runtime whose files this app cannot reach.</summary>
public sealed class NoPolicyDataFiles : IPolicyDataFiles
{
    public static NoPolicyDataFiles Instance { get; } = new();

    public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies) => Array.Empty<ToolChain>();

    public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies) => Array.Empty<RuleFamily>();

    public string? ReadPackBase(string packFolder) => null;
}

/// <summary>
/// The files from the local file system. The runtime's data folder (where its built-in policies, its default rule pack and its chain
/// catalog live) is found from the path the runtime itself reports for a built-in policy: <c>...\_data\policies\default.yaml</c> in an
/// installed runtime, <c>...\policies\default.yaml</c> in a source checkout, so the data folder is two levels up and the other files sit
/// at fixed places under it (<c>policies\guardrail\tool-chains.json</c>, <c>policies\guardrail\default</c>).
/// <para>
/// Every read is bounded: a file over <see cref="MaxFileBytes"/> is skipped, a pack with more than <see cref="MaxRuleFiles"/> rule files
/// is read up to that many, and no exception for an unreadable file leaves this type.
/// </para>
/// </summary>
public sealed class FileSystemPolicyData : IPolicyDataFiles
{
    /// <summary>The largest file read (the biggest rule file of the pinned runtime is about 90 KB).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024;

    /// <summary>The most rule files read from one pack.</summary>
    public const int MaxRuleFiles = 64;

    /// <summary>What each built-in family catches, in the order the runtime lists them (the runtime's <c>_FAMILY_DESCRIPTIONS</c>).</summary>
    private static readonly (string Name, string Description)[] BuiltInFamilies =
    {
        ("command", "Execution, reverse shells, destructive storage operations, persistence, privilege changes, credential access, security-control tampering, cloud, database, Kubernetes, and source-control effects"),
        ("sensitive-path", "Reads or writes involving SSH, cloud, Kubernetes, container, package-manager, Git, environment, browser-session, workload-identity, history, shell-profile, hook, and runtime-socket paths"),
        ("secret", "Provider credentials, API tokens, private keys, JWTs, authenticated connection strings, bearer tokens, and high-confidence secret assignments"),
        ("trust-exploit", "Instruction override, authority impersonation, jailbreak, prompt extraction, persona manipulation, delimiter abuse, and obfuscation signals"),
        ("c2", "Known exfiltration endpoints, cloud metadata SSRF forms, DNS tunneling, and DNS exfiltration indicators"),
        ("enterprise-data", "Structured payment, banking, contact, medical, birth-date, CSV, and JSON PII shapes"),
        ("cognitive-file", "Agent instruction, memory, configuration, gateway, and detector-state modification"),
    };

    public static FileSystemPolicyData Instance { get; } = new();

    /// <summary>
    /// The runtime's data folder from the named policies it listed; null when none of them is a built-in, unedited policy with a full path
    /// (an edited built-in is served from the user's folder, whose path says nothing about the runtime's).
    /// </summary>
    public static string? DataFolderFrom(IReadOnlyList<NamedPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var policy in policies)
        {
            if (!policy.IsBuiltIn || policy.IsEdited || policy.SourcePath.Length == 0)
            {
                continue;
            }

            // The runtime prints this path with both separators (C:\...\_data/policies/default.yaml).
            var path = policy.SourcePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string? policiesFolder;
            try
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    continue;
                }

                policiesFolder = Path.GetDirectoryName(path);
                if (policiesFolder is null || !string.Equals(Path.GetFileName(policiesFolder), "policies", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Path.GetDirectoryName(policiesFolder);
            }
            catch (ArgumentException)
            {
                // A path with characters Windows does not allow is not one to read.
            }
        }

        return null;
    }

    public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies)
    {
        if (DataFolderFrom(policies) is not { } data)
        {
            return Array.Empty<ToolChain>();
        }

        return PolicyCatalogJson.ParseToolChains(ReadText(Path.Combine(data, "policies", "guardrail", "tool-chains.json")));
    }

    public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies)
    {
        var bundled = DataFolderFrom(policies) is { } data ? Path.Combine(data, "policies", "guardrail", "default") : null;
        return Families(packFolder, bundled);
    }

    public string? ReadPackBase(string packFolder) =>
        packFolder.Length == 0 ? null : PolicyCatalogJson.ParsePackBase(ReadText(Path.Combine(packFolder, "defenseclaw-pack.json")));

    /// <summary>
    /// The rule families an effective pack enforces, as the runtime's <c>rule_families</c> computes them: the built-in (default pack)
    /// families come first, and each rule file of the pack with at least one enabled rule replaces the family named by its <c>category</c> or
    /// adds a new one. Built-in families keep the reference order; added ones follow in load order.
    /// </summary>
    /// <param name="packFolder">The pack's folder.</param>
    /// <param name="bundledDefaultFolder">The runtime's bundled default pack, or null when it is not known (the pack alone is then read).</param>
    public static IReadOnlyList<RuleFamily> Families(string packFolder, string? bundledDefaultFolder)
    {
        var families = new List<(string Name, int Declared, int Enabled)>();

        void Put(string category, int declared, int enabled)
        {
            var at = families.FindIndex(f => string.Equals(f.Name, category, StringComparison.Ordinal));
            if (at >= 0)
            {
                families[at] = (category, declared, enabled);
            }
            else
            {
                families.Add((category, declared, enabled));
            }
        }

        if (!string.IsNullOrEmpty(bundledDefaultFolder))
        {
            foreach (var (category, declared, enabled) in FamilyCounts(bundledDefaultFolder))
            {
                Put(category, declared, enabled);
            }
        }

        if (!string.IsNullOrEmpty(packFolder) && !SamePath(packFolder, bundledDefaultFolder))
        {
            foreach (var (category, declared, enabled) in FamilyCounts(packFolder))
            {
                if (enabled > 0)
                {
                    Put(category, declared, enabled);
                }
            }
        }

        var order = BuiltInFamilies.Select((f, i) => (f.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        return families
            .OrderBy(f => order.GetValueOrDefault(f.Name, BuiltInFamilies.Length))
            .Select(f => new RuleFamily(f.Name, f.Declared, f.Enabled, BuiltInFamilies.FirstOrDefault(b => string.Equals(b.Name, f.Name, StringComparison.Ordinal)).Description ?? string.Empty))
            .ToArray();
    }

    /// <summary><c>(category, declared, enabled)</c> per rule file, in load order (file names sorted).</summary>
    private static IEnumerable<(string Category, int Declared, int Enabled)> FamilyCounts(string packFolder)
    {
        var rulesFolder = Path.Combine(packFolder, "rules");
        string[] files;
        try
        {
            if (!Directory.Exists(rulesFolder))
            {
                yield break;
            }

            files = Directory.GetFiles(rulesFolder, "*.yaml")
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !string.Equals(name, "local-patterns.yaml", StringComparison.Ordinal) && !name.StartsWith('.');
                })
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Take(MaxRuleFiles)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (CountRules(file) is { } counted)
            {
                yield return counted;
            }
        }
    }

    private static (string Category, int Declared, int Enabled)? CountRules(string file)
    {
        var text = ReadText(file);
        if (text is null)
        {
            return null;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return null;
            }

            string? category = null;
            YamlSequenceNode? rules = null;
            foreach (var (key, value) in root.Children)
            {
                if (key is not YamlScalarNode { Value: { } name })
                {
                    continue;
                }

                if (name == "category" && value is YamlScalarNode { Value: { } text2 })
                {
                    category = text2.Trim();
                }
                else if (name == "rules" && value is YamlSequenceNode sequence)
                {
                    rules = sequence;
                }
            }

            // Only a mapping with a rules list is a rule file, and only one that names its family counts as one.
            if (rules is null || string.IsNullOrEmpty(category))
            {
                return null;
            }

            var declared = 0;
            var enabled = 0;
            foreach (var rule in rules.Children.OfType<YamlMappingNode>())
            {
                declared++;
                if (!IsDisabled(rule))
                {
                    enabled++;
                }
            }

            return (category, declared, enabled);
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A rule is enabled unless it says <c>enabled: false</c> (YAML 1.1 spellings of false included).</summary>
    private static bool IsDisabled(YamlMappingNode rule)
    {
        foreach (var (key, value) in rule.Children)
        {
            if (key is YamlScalarNode { Value: "enabled" } && value is YamlScalarNode { Value: { } text, Style: ScalarStyle.Plain })
            {
                return text.Trim().ToLowerInvariant() is "false" or "no" or "off";
            }
        }

        return false;
    }

    private static bool SamePath(string a, string? b)
    {
        if (string.IsNullOrEmpty(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The text of a small file; null when it is missing, too large or unreadable.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            return DefenseClaw.Core.IO.SharedFile.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
