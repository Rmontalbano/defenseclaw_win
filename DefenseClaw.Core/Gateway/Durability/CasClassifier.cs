using System.Security.Cryptography;
using System.Text.Json;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>What a pending guarded write amounts to, judged by the hashes its receipt recorded (CUST-360).</summary>
public enum CasWriteState
{
    /// <summary>The target already holds the staged content: the write finished and only leftovers remain.</summary>
    Committed,

    /// <summary>The target still holds what it held before (or is absent as it was): the write never took effect.</summary>
    NeverApplied,

    /// <summary>The target matches neither the old nor the staged content: someone rewrote it since, so the pending write no longer describes it.</summary>
    Superseded,

    /// <summary>Something could not be read, parsed or verified; do not assume anything.</summary>
    Unknown,
}

/// <summary>A <see cref="CasWriteState"/> with a short human reason.</summary>
public sealed record CasWriteAssessment(CasWriteState State, string Reason);

public static partial class CasReceipts
{
    /// <summary>Files larger than this are never hashed (they are Unknown).</summary>
    public const long MaxHashBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Read-only classification of <paramref name="write"/>: the live target's SHA-256 against the receipt's <c>old.sha256</c> and <c>stage.sha256</c>, and
    /// every staging artifact that exists against its recorded hash. Never throws. <paramref name="sha256OfFile"/> returns the lower-case hex of a file, or
    /// null when it cannot be read (the real, bounded one when null).
    /// </summary>
    public static CasWriteAssessment Classify(CasPendingWrite write, Func<string, string?>? sha256OfFile = null)
    {
        try
        {
            return ClassifyCore(write, sha256OfFile ?? HashBounded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CasWriteAssessment(CasWriteState.Unknown, "the pending write could not be checked");
        }
    }

    private static CasWriteAssessment Unknown(string reason) => new(CasWriteState.Unknown, reason);

    private static CasWriteAssessment ClassifyCore(CasPendingWrite write, Func<string, string?> hash)
    {
        // The prepared receipt is the fullest; the plain one is the fallback.
        JsonDocument? document = null;
        foreach (var suffix in new[] { ".intent.prepared", ".intent" })
        {
            var file = write.ReceiptFiles.FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                continue;
            }

            try
            {
                var parsed = JsonDocument.Parse(SharedFile.ReadAllText(file, MaxReceiptBytes));
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    document = parsed;
                    break;
                }

                parsed.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall back to the next one.
            }
        }

        if (document is null)
        {
            return Unknown("the receipt could not be read");
        }

        using (document)
        {
            var root = document.RootElement;
            var target = FirstString(root, "target_path", "input_path", "logical_path") ?? write.TargetPath;
            if (string.IsNullOrEmpty(target))
            {
                return Unknown("the receipt names no target");
            }

            var oldHash = Sha(root, "old");
            var stageHash = Sha(root, "stage");
            var oldExists = !(root.TryGetProperty("old_exists", out var oe) && oe.ValueKind == JsonValueKind.False);
            var removes = root.TryGetProperty("remove", out var rm) && rm.ValueKind == JsonValueKind.True;

            // Artifacts first: a present one that does not match its record means the receipt cannot be trusted.
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                var checks = new List<(string? Name, string? Expected)>
                {
                    (Name(root, "stage_marker"), Sha(root, "stage_marker")),
                    (Name(root, "final_marker"), Sha(root, "final_marker")),
                    (Name(root, "terminal_marker"), Sha(root, "terminal_marker")),
                    (FirstString(root, "tombstone_name"), oldHash),
                    (Name(root, "stage"), stageHash),
                    (FirstString(root, "stage_final_name"), stageHash),
                };
                foreach (var (name, expected) in checks)
                {
                    if (name is null || !IsBare(name))
                    {
                        continue;
                    }

                    var path = Path.Combine(folder, name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var actual = hash(path);
                    if (actual is null)
                    {
                        return Unknown($"{name} could not be read");
                    }

                    if (expected is null || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return Unknown($"{name} does not match the receipt");
                    }
                }
            }

            if (!File.Exists(target))
            {
                return !oldExists
                    ? new CasWriteAssessment(CasWriteState.NeverApplied, "the file did not exist and still does not")
                    : removes
                        ? new CasWriteAssessment(CasWriteState.Committed, "the file was removed as the write intended")
                        : Unknown("the file is missing");
            }

            var live = hash(target);
            if (live is null)
            {
                return Unknown("the file could not be read or is too large to check");
            }

            if (oldHash is not null && string.Equals(live, oldHash, StringComparison.OrdinalIgnoreCase))
            {
                return new CasWriteAssessment(CasWriteState.NeverApplied, "the file still has its old content");
            }

            if (stageHash is not null && string.Equals(live, stageHash, StringComparison.OrdinalIgnoreCase))
            {
                return new CasWriteAssessment(CasWriteState.Committed, "the file already has the new content");
            }

            if (oldHash is null && stageHash is null)
            {
                return Unknown("the receipt records no hashes");
            }

            return new CasWriteAssessment(CasWriteState.Superseded, "the file matches neither the old nor the new content");
        }
    }

    private static bool IsBare(string name) => name.IndexOfAny(new[] { '\\', '/', ':' }) < 0 && !name.Contains("..", StringComparison.Ordinal);

    private static string? Name(JsonElement root, string artifact) =>
        root.TryGetProperty(artifact, out var e) && e.ValueKind == JsonValueKind.Object ? FirstString(e, "name") : null;

    private static string? Sha(JsonElement root, string artifact) =>
        root.TryGetProperty(artifact, out var e) && e.ValueKind == JsonValueKind.Object ? FirstString(e, "sha256") : null;

    private static string? HashBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (stream.Length > MaxHashBytes)
            {
                return null;
            }

            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxHashBytes)
                {
                    return null;
                }

                _ = sha.TransformBlock(buffer, 0, read, null, 0);
            }

            _ = sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
