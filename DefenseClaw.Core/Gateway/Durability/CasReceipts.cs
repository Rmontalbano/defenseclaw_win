using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>
/// One guarded (compare-and-swap, "CAS v2") write of an agent's config file that began and never finished. DefenseClaw 1.0 records each such
/// write as a set of receipt files in its data directory (<c>.defenseclaw-cas-v2-&lt;24 hex&gt;.intent</c>, <c>.intent.allocation</c>,
/// <c>.intent.prepared</c>, and <c>.intent.terminal</c> or <c>.intent.complete</c> when it ends) and stages the new content in
/// <c>.tmp-cas-v2-*</c> files beside the target. A set with no <c>.terminal</c> and no <c>.complete</c> is pending. If the agent rewrites the
/// target in the meantime the set can never finish, and every later write of that file is refused (<c>requested recovery leaf is occupied by an
/// unrecognized inode</c>) - there is no upstream recovery command (CUST-352, incident B).
/// </summary>
public sealed class CasPendingWrite : IEquatable<CasPendingWrite>
{
    public CasPendingWrite(
        string id,
        string stateDirectory,
        IReadOnlyList<string> receiptFiles,
        IReadOnlyList<string> tempFiles,
        string? targetPath,
        string phases,
        DateTimeOffset lastWriteUtc)
    {
        Id = id;
        StateDirectory = stateDirectory;
        ReceiptFiles = receiptFiles;
        TempFiles = tempFiles;
        TargetPath = targetPath;
        Phases = phases;
        LastWriteUtc = lastWriteUtc;
    }

    /// <summary>The 24 hex characters in the receipt names (lower case).</summary>
    public string Id { get; }

    /// <summary>The folder the receipts are in (the DefenseClaw data directory).</summary>
    public string StateDirectory { get; }

    /// <summary>Full paths of the receipt files of this set, and only of this set.</summary>
    public IReadOnlyList<string> ReceiptFiles { get; }

    /// <summary>Full paths of the staging files beside the target that this set (or nothing at all) owns.</summary>
    public IReadOnlyList<string> TempFiles { get; }

    /// <summary>The file the write was for (<c>...\.codex\config.toml</c>), or null when no receipt could be read.</summary>
    public string? TargetPath { get; }

    /// <summary>The phases present, e.g. <c>staging+prepared</c>.</summary>
    public string Phases { get; }

    /// <summary>When the set was last written (the newest of its files).</summary>
    public DateTimeOffset LastWriteUtc { get; }

    /// <summary>True when the target is under a <c>.codex</c> folder: the write was Codex's config.toml.</summary>
    public bool IsCodexConfig =>
        TargetPath is not null &&
        (TargetPath.Contains(@"\.codex\", StringComparison.OrdinalIgnoreCase) || TargetPath.Contains("/.codex/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything that would be moved, receipts first.</summary>
    public IEnumerable<string> AllFiles => ReceiptFiles.Concat(TempFiles);

    private string Fingerprint =>
        string.Join('|', Id, StateDirectory, TargetPath, Phases, LastWriteUtc.UtcTicks.ToString(CultureInfo.InvariantCulture), string.Join(',', ReceiptFiles), string.Join(',', TempFiles));

    public bool Equals(CasPendingWrite? other) => other is not null && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as CasPendingWrite);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
}

/// <summary>Finds pending CAS v2 writes. Reads directory listings and the small receipt files; changes nothing.</summary>
public static partial class CasReceipts
{
    public const string ReceiptPrefix = ".defenseclaw-cas-v2-";

    public const string TempPrefix = ".tmp-cas-v2-";

    /// <summary>A set is only called stuck once it has sat this long: a write in flight is pending for a moment.</summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromMinutes(2);

    private const int MaxEntries = 512;

    private const long MaxReceiptBytes = 64 * 1024;

    // A name with a ".gc-<48 hex>" tail belongs to a retired set; it does not match, so it is never counted or moved.
    [GeneratedRegex(@"^\.defenseclaw-cas-v2-(?<id>[0-9a-fA-F]{24})\.intent(?<phase>\.allocation|\.prepared|\.terminal|\.complete)?$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ReceiptName();

    /// <summary>
    /// The pending sets in <paramref name="stateDirectory"/> whose newest file is at least <paramref name="minimumAge"/> old at <paramref name="now"/>
    /// (null: <see cref="DefaultMinimumAge"/>), oldest first. A set that has a <c>.terminal</c> or <c>.complete</c> receipt is finished and never listed.
    /// An unreadable folder is an empty answer.
    /// </summary>
    public static IReadOnlyList<CasPendingWrite> FindPending(string stateDirectory, DateTimeOffset now, TimeSpan? minimumAge = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);

        var minAge = minimumAge ?? DefaultMinimumAge;
        var groups = new Dictionary<string, List<(string Path, string Phase, DateTimeOffset Written)>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(stateDirectory))
            {
                return Array.Empty<CasPendingWrite>();
            }

            var seen = 0;
            foreach (var path in Directory.EnumerateFiles(stateDirectory, ReceiptPrefix + "*"))
            {
                if (++seen > MaxEntries)
                {
                    break;
                }

                var name = Path.GetFileName(path);
                var match = ReceiptName().Match(name);
                if (!match.Success)
                {
                    continue;
                }

                var phase = match.Groups["phase"].Success ? match.Groups["phase"].Value[1..].ToLowerInvariant() : "staging";
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var id = match.Groups["id"].Value.ToLowerInvariant();
                if (!groups.TryGetValue(id, out var list))
                {
                    groups[id] = list = new();
                }

                list.Add((path, phase, written));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<CasPendingWrite>();
        }

        // Only a set that is still pending is read: finished ones are many, small and of no interest. Names the pending sets stage are
        // known to belong to one of them; a staging file that none claims is an orphan (see TempFilesFor).
        var candidates = groups
            .Where(g => !g.Value.Any(f => f.Phase is "terminal" or "complete") && now - g.Value.Max(f => f.Written) >= minAge)
            .ToArray();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new Dictionary<string, Receipt?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, files) in candidates)
        {
            var receipt = ReadReceipt(files);
            parsed[id] = receipt;
            foreach (var name in receipt?.StagingNames ?? Array.Empty<string>())
            {
                _ = referenced.Add(name);
            }
        }

        var pending = new List<CasPendingWrite>();
        foreach (var (id, files) in candidates)
        {
            var newest = files.Max(f => f.Written);
            var receipt = parsed[id];
            var temps = TempFilesFor(receipt, referenced, now, minAge);
            var order = new[] { "staging", "allocation", "prepared" };
            pending.Add(new CasPendingWrite(
                id,
                stateDirectory,
                files.OrderBy(f => Array.IndexOf(order, f.Phase)).Select(f => f.Path).ToArray(),
                temps,
                receipt?.Target,
                string.Join('+', files.Select(f => f.Phase).Distinct().OrderBy(p => Array.IndexOf(order, p))),
                newest));
        }

        return pending.OrderBy(p => p.LastWriteUtc).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> TempFilesFor(Receipt? receipt, HashSet<string> referenced, DateTimeOffset now, TimeSpan minAge)
    {
        if (receipt?.Target is not { Length: > 0 } target)
        {
            return Array.Empty<string>();
        }

        string? folder;
        try
        {
            folder = Path.GetDirectoryName(target);
        }
        catch (ArgumentException)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrEmpty(folder))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return result;
            }

            var seen = 0;
            foreach (var path in Directory.EnumerateFiles(folder, TempPrefix + "*"))
            {
                if (++seen > MaxEntries)
                {
                    break;
                }

                var name = Path.GetFileName(path);
                var owned = receipt.StagingNames.Contains(name, StringComparer.OrdinalIgnoreCase);

                // No receipt owns it: junk left by a set that never got as far as naming it, but only once it is as old as the stuck set.
                var orphan = !referenced.Contains(name) && now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) >= minAge;
                if (owned || orphan)
                {
                    result.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        return result;
    }

    private sealed record Receipt(string? Target, IReadOnlyList<string> StagingNames);

    // The staging receipt is the fullest; the others are read when it is missing or damaged.
    private static Receipt? ReadReceipt(List<(string Path, string Phase, DateTimeOffset Written)> files)
    {
        var order = new[] { "staging", "prepared", "allocation", "terminal", "complete" };
        foreach (var file in files.OrderBy(f => Array.IndexOf(order, f.Phase)))
        {
            try
            {
                using var document = JsonDocument.Parse(SharedFile.ReadAllText(file.Path, MaxReceiptBytes));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var target = FirstString(root, "target_path", "input_path", "logical_path");
                var names = new List<string>();
                foreach (var property in new[] { "stage_final_name", "tombstone_name", "terminal_marker_name" })
                {
                    AddName(names, FirstString(root, property));
                }

                foreach (var artifact in new[] { "stage", "stage_marker", "final_marker", "old", "terminal_marker" })
                {
                    if (root.TryGetProperty(artifact, out var element) && element.ValueKind == JsonValueKind.Object)
                    {
                        AddName(names, FirstString(element, "name"));
                    }
                }

                return new Receipt(target, names);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next phase's file.
            }
        }

        return null;
    }

    // Only a bare staging name counts: a receipt that names a path (or the target itself) must never make this app touch it.
    private static void AddName(List<string> names, string? name)
    {
        if (name is { Length: > 0 } &&
            name.StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase) &&
            name.IndexOfAny(new[] { '\\', '/', ':' }) < 0 &&
            name.IndexOf("..", StringComparison.Ordinal) < 0)
        {
            names.Add(name);
        }
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }
}
