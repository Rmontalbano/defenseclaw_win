using System.Diagnostics;
using System.Text.Json;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// Which <c>audit.db</c> a saved tally belongs to: its full path, when the file was created (a rebuilt database is a new file) and its SQLite
/// <c>schema_version</c> (a migration may have renumbered rows or changed the columns the scan reads).
/// </summary>
public sealed record HookTotalsFingerprint(string Path, long CreatedUtcTicks, long SchemaVersion)
{
    public bool Matches(HookTotalsFingerprint? other) =>
        other is not null &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
        CreatedUtcTicks == other.CreatedUtcTicks &&
        SchemaVersion == other.SchemaVersion;
}

/// <summary>What the block scan had reached: the per-connector enforced-block tally, the watermark it had read up to, and how many hook rows that covered.</summary>
public sealed record HookTotalsCacheState(
    HookTotalsFingerprint Fingerprint,
    string MarkTimestamp,
    long MarkRowId,
    long Scanned,
    IReadOnlyDictionary<string, long> Blocks);

/// <summary>
/// The block tally of <see cref="ConnectorHookTotalsReader"/> kept on disk between launches (CUST-279), so a second launch resumes from the
/// watermark instead of re-reading every hook row of a multi-gigabyte database.
/// <para>
/// <b>Only ever the app's own cache folder</b> (<c>%LOCALAPPDATA%\DefenseClaw.App\cache</c>); a directory under a <c>.defenseclaw</c> folder is
/// refused. <b>Best effort:</b> a cache that cannot be read, is corrupt, or is of another version loads as "nothing", and a write that fails is
/// logged and dropped; neither can make a read fail. <b>Atomic:</b> written to a temp file beside the target, then moved over it, so a crash leaves
/// the old file or the new one, never half of one.
/// </para>
/// </summary>
public sealed class HookTotalsCacheStore
{
    public const string FileName = "hook-totals.json";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _writeLock = new();

    /// <param name="directory">The folder the cache file lives in; created on the first save.</param>
    public HookTotalsCacheStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var full = Path.GetFullPath(directory);
        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.Equals(segment, ".defenseclaw", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The hook-totals cache never lives under the DefenseClaw data directory.", nameof(directory));
            }
        }

        Directory = full;
        FilePath = Path.Combine(full, FileName);
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>The saved state, or null when there is none, it is unreadable, corrupt, of another format, or inconsistent.</summary>
    public HookTotalsCacheState? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath), Json);
            if (dto is null || dto.Version != FormatVersion || string.IsNullOrEmpty(dto.Path) || dto.Blocks is null ||
                dto.Scanned < 0 || dto.MarkRowId < -1 || dto.MarkTimestamp is null)
            {
                return null;
            }

            var blocks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (connector, count) in dto.Blocks)
            {
                if (count < 0 || count > dto.Scanned)
                {
                    return null;
                }

                blocks[connector] = count;
            }

            return new HookTotalsCacheState(
                new HookTotalsFingerprint(dto.Path, dto.CreatedUtcTicks, dto.SchemaVersion),
                dto.MarkTimestamp,
                dto.MarkRowId,
                dto.Scanned,
                blocks);
        }
#pragma warning disable CA1031 // A cache is an optimisation: whatever is wrong with it, the answer is a full catch-up.
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"hook totals cache: ignored an unreadable cache: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Writes <paramref name="state"/> atomically (temp file, then replace). Returns false, having changed nothing, when the write failed.</summary>
    public bool Save(HookTotalsCacheState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var dto = new Dto
        {
            Version = FormatVersion,
            Path = state.Fingerprint.Path,
            CreatedUtcTicks = state.Fingerprint.CreatedUtcTicks,
            SchemaVersion = state.Fingerprint.SchemaVersion,
            MarkTimestamp = state.MarkTimestamp,
            MarkRowId = state.MarkRowId,
            Scanned = state.Scanned,
            Blocks = new Dictionary<string, long>(state.Blocks, StringComparer.OrdinalIgnoreCase),
        };

        var temp = FilePath + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            lock (_writeLock)
            {
                _ = System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
                File.Move(temp, FilePath, overwrite: true);
            }

            return true;
        }
#pragma warning disable CA1031 // See Load: a failed write costs the next launch a catch-up, nothing else.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"hook totals cache: could not write the cache: {ex.GetType().Name}");
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next save overwrites a temp of the same name.
        }
    }

    private sealed class Dto
    {
        public int Version { get; set; }

        public string? Path { get; set; }

        public long CreatedUtcTicks { get; set; }

        public long SchemaVersion { get; set; }

        public string? MarkTimestamp { get; set; }

        public long MarkRowId { get; set; }

        public long Scanned { get; set; }

        public Dictionary<string, long>? Blocks { get; set; }
    }
}
