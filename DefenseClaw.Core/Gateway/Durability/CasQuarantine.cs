using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>What <see cref="CasQuarantine"/> did, in a sentence for the review's result and Activity.</summary>
public sealed record CasQuarantineResult(bool Succeeded, string Message, string? Directory, int Files);

/// <summary>A quarantine folder that still holds files that have not been put back.</summary>
public sealed record CasQuarantineInfo(string Directory, DateTimeOffset CreatedUtc, int Files);

/// <summary>
/// Moves a stuck pending write (<see cref="CasPendingWrite"/>) out of the way - never deletes it - and puts it back on request. The files go to
/// <c>&lt;root&gt;\&lt;timestamp&gt;\</c> with a <c>manifest.json</c> that records, for each, where it came from, its size and its SHA-256, so
/// "Put back" can restore exactly what was moved and nothing else. Only receipt files of the sets named, and staging files beside their target, are
/// ever moved; a finished set, another id or the target itself is never touched. Both operations refuse while the gateway runs.
/// </summary>
public static class CasQuarantine
{
    public const string ManifestName = "manifest.json";

    /// <summary>A staging file larger than this is left where it is and reported: it is not a config write.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Manifest
    {
        public int Version { get; set; } = 1;

        public DateTimeOffset CreatedUtc { get; set; }

        public string? StateDirectory { get; set; }

        public DateTimeOffset? RestoredUtc { get; set; }

        public List<Entry> Entries { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Kind { get; set; } = "receipt";

        public string Set { get; set; } = string.Empty;

        public string Original { get; set; } = string.Empty;

        public string Stored { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public bool Moved { get; set; }

        public DateTimeOffset? RestoredUtc { get; set; }
    }

    /// <summary>
    /// Moves the pending sets <paramref name="ids"/> that are still pending now (re-read here, so a set that finished in the meantime is left alone).
    /// </summary>
    /// <param name="stateDirectory">Where the receipts are (the data directory).</param>
    /// <param name="ids">The 24-character set ids the operator reviewed.</param>
    /// <param name="quarantineRoot">The folder the dated folder goes in.</param>
    /// <param name="now">The clock, for the folder name and the age test.</param>
    /// <param name="gatewayRunning">True when the gateway is up now: nothing is moved.</param>
    public static CasQuarantineResult MoveAside(
        string stateDirectory,
        IReadOnlyCollection<string> ids,
        string quarantineRoot,
        DateTimeOffset now,
        bool gatewayRunning,
        TimeSpan? minimumAge = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrEmpty(quarantineRoot);

        if (gatewayRunning)
        {
            return new CasQuarantineResult(false, "The gateway is running, so nothing was moved. Stop it first: it may be writing these files right now.", null, 0);
        }

        var wanted = new HashSet<string>(ids.Select(i => i.ToLowerInvariant()), StringComparer.Ordinal);
        var sets = CasReceipts.FindPending(stateDirectory, now, minimumAge).Where(p => wanted.Contains(p.Id)).ToArray();
        if (sets.Length == 0)
        {
            return new CasQuarantineResult(false, "Nothing was moved: the pending write is no longer there (it finished, or was already moved).", null, 0);
        }

        try
        {
            var directory = NewFolder(quarantineRoot, now);
            var manifest = new Manifest { CreatedUtc = now, StateDirectory = stateDirectory };
            var skipped = new List<string>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var set in sets)
            {
                foreach (var original in set.AllFiles)
                {
                    var info = new FileInfo(original);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    if (info.Length > MaxFileBytes)
                    {
                        skipped.Add($"{info.Name} is too large to be a config write");
                        continue;
                    }

                    var stored = info.Name;
                    for (var n = 2; !taken.Add(stored); n++)
                    {
                        stored = $"{n}-{info.Name}";
                    }

                    manifest.Entries.Add(new Entry
                    {
                        Kind = set.ReceiptFiles.Contains(original, StringComparer.OrdinalIgnoreCase) ? "receipt" : "staging",
                        Set = set.Id,
                        Original = original,
                        Stored = stored,
                        Size = info.Length,
                        Sha256 = Hash(original),
                    });
                }
            }

            if (manifest.Entries.Count == 0)
            {
                return new CasQuarantineResult(false, "Nothing was moved: " + (skipped.Count > 0 ? string.Join("; ", skipped) : "the files are gone."), null, 0);
            }

            // The manifest first: if the process dies half-way, what was moved is still written down.
            Save(directory, manifest);

            foreach (var entry in manifest.Entries)
            {
                Move(entry.Original, Path.Combine(directory, entry.Stored));
                entry.Moved = true;
            }

            Save(directory, manifest);
            var tail = skipped.Count > 0 ? " Left in place: " + string.Join("; ", skipped) + "." : string.Empty;
            return new CasQuarantineResult(
                true,
                string.Create(CultureInfo.InvariantCulture, $"Moved {manifest.Entries.Count} file(s) of {sets.Length} stuck write(s) to {directory}. Nothing was deleted.") + tail,
                directory,
                manifest.Entries.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CasQuarantineResult(false, "Not everything could be moved: " + ex.Message + " Files already moved are listed in the folder's manifest.json; Put back restores them.", null, 0);
        }
    }

    /// <summary>Restores what the manifest in <paramref name="quarantineDirectory"/> says was moved, to where it came from, and only if it is unchanged and the place is free.</summary>
    public static CasQuarantineResult PutBack(string quarantineDirectory, bool gatewayRunning)
    {
        ArgumentException.ThrowIfNullOrEmpty(quarantineDirectory);

        if (gatewayRunning)
        {
            return new CasQuarantineResult(false, "The gateway is running, so nothing was put back. Stop it first.", quarantineDirectory, 0);
        }

        try
        {
            var manifest = Load(quarantineDirectory);
            if (manifest is null)
            {
                return new CasQuarantineResult(false, "That folder has no readable manifest.json, so there is nothing to put back.", quarantineDirectory, 0);
            }

            var restored = 0;
            var notes = new List<string>();
            foreach (var entry in manifest.Entries.Where(e => e.Moved && e.RestoredUtc is null))
            {
                // A manifest is a file on disk like any other: it can only ever send back what this class moves (a bare stored name, and an
                // original named like a receipt or a staging file), never an arbitrary file to an arbitrary place.
                var originalName = Path.GetFileName(entry.Original);
                if (entry.Stored.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 ||
                    !(originalName.StartsWith(CasReceipts.ReceiptPrefix, StringComparison.OrdinalIgnoreCase) ||
                      originalName.StartsWith(CasReceipts.TempPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    notes.Add($"{entry.Stored} is not a file this app moved");
                    continue;
                }

                var stored = Path.Combine(quarantineDirectory, entry.Stored);
                if (!File.Exists(stored))
                {
                    notes.Add($"{entry.Stored} is no longer in the folder");
                    continue;
                }

                if (File.Exists(entry.Original))
                {
                    notes.Add($"{Path.GetFileName(entry.Original)} exists again at its old place");
                    continue;
                }

                if (!string.Equals(Hash(stored), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"{entry.Stored} has changed since it was moved");
                    continue;
                }

                Move(stored, entry.Original);
                entry.RestoredUtc = DateTimeOffset.UtcNow;
                restored++;
            }

            if (manifest.Entries.Where(e => e.Moved).All(e => e.RestoredUtc is not null))
            {
                manifest.RestoredUtc = DateTimeOffset.UtcNow;
            }

            Save(quarantineDirectory, manifest);
            var message = string.Create(CultureInfo.InvariantCulture, $"Put back {restored} file(s).") + (notes.Count > 0 ? " Left in the folder: " + string.Join("; ", notes) + "." : string.Empty);
            return new CasQuarantineResult(restored > 0 || notes.Count == 0, message, quarantineDirectory, restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CasQuarantineResult(false, "Not everything could be put back: " + ex.Message, quarantineDirectory, 0);
        }
    }

    /// <summary>The newest quarantine folder under <paramref name="quarantineRoot"/> that still holds files to put back, or null.</summary>
    public static CasQuarantineInfo? LatestUnrestored(string quarantineRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(quarantineRoot);

        try
        {
            if (!Directory.Exists(quarantineRoot))
            {
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(quarantineRoot).OrderByDescending(d => d, StringComparer.Ordinal).Take(20))
            {
                var manifest = Load(directory);
                var open = manifest?.Entries.Count(e => e.Moved && e.RestoredUtc is null) ?? 0;
                if (manifest is not null && open > 0)
                {
                    return new CasQuarantineInfo(directory, manifest.CreatedUtc, open);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return null;
    }

    private static string NewFolder(string root, DateTimeOffset now)
    {
        var name = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, name);
        for (var n = 2; Directory.Exists(path); n++)
        {
            path = Path.Combine(root, $"{name}-{n}");
        }

        _ = Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// <see cref="File.Move(string, string)"/>, tried a few times: a virus scanner or the search indexer holds a file it has just seen for a moment, and
    /// a move at that instant fails with "access denied" for no reason that lasts.
    /// </summary>
    private static void Move(string from, string to, bool overwrite = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Manifest? Load(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Manifest>(stream, Json);
    }

    private static void Save(string directory, Manifest manifest)
    {
        var path = Path.Combine(directory, ManifestName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Json));
        Move(temp, path, overwrite: true);
    }
}
