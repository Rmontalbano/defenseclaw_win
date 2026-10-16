using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>The most this app will download or unpack for an update. Far above a real release; a hostile or broken one stops at them.</summary>
internal sealed record SelfUpdateLimits
{
    public static readonly SelfUpdateLimits Default = new();

    /// <summary>checksums.txt: a handful of lines.</summary>
    public long ChecksumsBytes { get; init; } = 64 * 1024;

    /// <summary>The zip as downloaded (the single-file app is about 190 MB unpacked).</summary>
    public long ZipBytes { get; init; } = 400L * 1024 * 1024;

    /// <summary>DefenseClaw.App.exe once unpacked.</summary>
    public long AppExeBytes { get; init; } = 600L * 1024 * 1024;

    /// <summary>DefenseClaw.GatewayAdmin.exe once unpacked.</summary>
    public long HelperExeBytes { get; init; } = 100L * 1024 * 1024;
}

/// <summary>The checks on a downloaded update that need no network and no running app: the checksums file and the zip.</summary>
internal static class SelfUpdatePackage
{
    public const string AppExeName = "DefenseClaw.App.exe";

    public const string HelperExeName = "DefenseClaw.GatewayAdmin.exe";

    /// <summary>
    /// The SHA-256 (lower-case hex) <paramref name="checksums"/> lists for <paramref name="fileName"/>: lines of <c>sha256  filename</c>
    /// (text or binary marker). Null with a reason when the file is not listed, is listed with two different hashes, or the hash is not 64 hex.
    /// </summary>
    public static string? FindChecksum(string checksums, string fileName, out string? problem)
    {
        problem = null;
        string? found = null;
        foreach (var raw in checksums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var space = line.IndexOfAny([' ', '\t']);
            if (space != 64)
            {
                continue;
            }

            var name = line[space..].TrimStart(' ', '\t', '*');
            if (!string.Equals(name, fileName, StringComparison.Ordinal))
            {
                continue;
            }

            var hash = line[..64].ToLowerInvariant();
            if (!IsHex64(hash))
            {
                problem = $"checksums.txt lists {fileName} with something that is not a SHA-256.";
                return null;
            }

            if (found is not null && found != hash)
            {
                problem = $"checksums.txt lists {fileName} twice with different hashes.";
                return null;
            }

            found = hash;
        }

        problem ??= found is null ? $"checksums.txt does not list {fileName}." : null;
        return found;
    }

    /// <summary>GitHub's <c>sha256:&lt;hex&gt;</c> digest as lower-case hex; null when it is some other form.</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return IsHex64(hex) ? hex : null;
    }

    public static bool IsHex64(string text) => text.Length == 64 && text.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Opens the zip and requires exactly <see cref="AppExeName"/> and <see cref="HelperExeName"/> at its root: no other entry, no folder,
    /// no path separator or drive, no duplicate, nothing declared over its size cap. Null when it is acceptable, otherwise why not.
    /// </summary>
    public static string? ValidateEntries(string zipPath, SelfUpdateLimits limits)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count != 2)
            {
                return $"The zip has {archive.Entries.Count} entries; it must hold exactly {AppExeName} and {HelperExeName}.";
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Contains("..", StringComparison.Ordinal))
                {
                    return $"The zip has an entry with a path ('{Shown(name)}'); only files at the root are allowed.";
                }

                var cap = name.Equals(AppExeName, StringComparison.Ordinal) ? limits.AppExeBytes
                    : name.Equals(HelperExeName, StringComparison.Ordinal) ? limits.HelperExeBytes
                    : -1;
                if (cap < 0)
                {
                    return $"The zip has an unexpected entry '{Shown(name)}'; it must hold exactly {AppExeName} and {HelperExeName}.";
                }

                if (!seen.Add(name))
                {
                    return $"The zip lists '{name}' twice.";
                }

                if (entry.Length > cap)
                {
                    return $"{name} in the zip is {entry.Length / (1024 * 1024)} MiB unpacked, over the {cap / (1024 * 1024)} MiB limit.";
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return $"The zip could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// Unpacks the two validated entries into <paramref name="destination"/> (which must exist), counting what is really written: an entry that
    /// inflates past its cap is stopped, whatever its header said. Only the two known names are written, and only directly inside
    /// <paramref name="destination"/>: the target is built from our own constant, never from the entry's name, so no entry can land
    /// elsewhere even if <see cref="ValidateEntries"/> was skipped. Null on success, otherwise why not.
    /// </summary>
    public static string? Extract(string zipPath, string destination, SelfUpdateLimits limits)
    {
        try
        {
            var root = Path.GetFullPath(destination);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var (fileName, cap) = entry.FullName switch
                {
                    AppExeName => (AppExeName, limits.AppExeBytes),
                    HelperExeName => (HelperExeName, limits.HelperExeBytes),
                    _ => (null, 0L),
                };
                if (fileName is null)
                {
                    return $"The zip has an unexpected entry '{Shown(entry.FullName)}'; it must hold exactly {AppExeName} and {HelperExeName}.";
                }

                var target = Path.GetFullPath(Path.Combine(root, fileName));
                if (!string.Equals(Path.GetDirectoryName(target), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                {
                    return $"{fileName} would unpack outside the update folder.";
                }

                using var source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > cap)
                    {
                        return $"{entry.FullName} inflates past the {cap / (1024 * 1024)} MiB limit.";
                    }

                    output.Write(buffer, 0, read);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return $"The zip could not be unpacked: {ex.Message}";
        }
    }

    /// <summary>Null when <paramref name="productVersion"/> is the release's version (build metadata ignored), otherwise why not.</summary>
    public static string? CheckProductVersion(string fileName, string? productVersion, AppVersion expected)
    {
        if (!AppVersion.TryParse(productVersion, out var actual))
        {
            return $"{fileName} reports the version '{Shown(productVersion ?? string.Empty)}', which is not a version number.";
        }

        return actual.CompareTo(expected) == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{fileName} is version {actual}, not the release's {expected}.");
    }

    private static string Shown(string text) => text.Length > 60 ? text[..60] + "…" : text;
}
