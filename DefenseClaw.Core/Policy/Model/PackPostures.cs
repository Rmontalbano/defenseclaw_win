namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// The manifest posture (<c>defenseclaw-pack.json</c>, see <see cref="RulePackPosture"/>) of each rule-pack folder the Policy view uses, read ONCE per catalog
/// refresh on the background step and carried on the catalog. <see cref="PolicyLevels.PackProfile"/> and <see cref="PolicyLevels.ResolveLevels"/> take it as input,
/// so building a row, a picker preview or a "what changes" line never touches the disk and never waits on a slow or unreachable folder on the UI thread.
/// A folder with no entry here (none was read, the manifest is missing or unusable, or it is a folder that does not exist yet such as a composed pack's target)
/// has the posture of its name, which is what an unreadable manifest gives.
/// </summary>
public sealed class PackPostures
{
    private readonly IReadOnlyDictionary<string, string> _byFolder;

    private PackPostures(IReadOnlyDictionary<string, string> byFolder) => _byFolder = byFolder;

    /// <summary>No manifest read: every folder has the posture of its name.</summary>
    public static PackPostures None { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The number of folders whose manifest named a posture.</summary>
    public int Count => _byFolder.Count;

    /// <summary>
    /// Reads each distinct, non-empty folder once with <paramref name="readPosture"/> (the folder's manifest posture, or null). Does file reads: call it off the UI thread.
    /// </summary>
    /// <param name="folders">The pack folders in use.</param>
    /// <param name="readPosture">Gives a folder's manifest posture, or null when it has none (<see cref="IPolicyDataFiles.ReadPackPosture"/>).</param>
    public static PackPostures Read(IEnumerable<string?> folders, Func<string, string?> readPosture)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(readPosture);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var key = (folder ?? string.Empty).Trim();
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            if (readPosture(key) is { Length: > 0 } posture)
            {
                found[key] = posture;
            }
        }

        return found.Count == 0 ? None : new PackPostures(found);
    }

    /// <summary>The posture of <paramref name="folder"/>: the manifest's when one was read, else its name's. Never reads a file.</summary>
    public string ProfileOf(string? folder)
    {
        var key = (folder ?? string.Empty).Trim();
        return key.Length > 0 && _byFolder.TryGetValue(key, out var posture) ? posture : RulePackPosture.FromFolderName(folder);
    }
}
