using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Config;

/// <summary>
/// What one file looked like on disk when it was looked at: where it is, whether it was there, how long it was and when it was last
/// written (UTC). Never what was in it.
/// <para>
/// That is on purpose and is the whole difference from <see cref="FileSignature"/>, which hashes small files so the config watcher can tell
/// two same-length saves apart. A stamp is taken for <c>.env</c>, which holds the gateway token and every key <c>keys set</c> stored, so
/// this type has no member that could carry a byte of content or a hash of it, and taking one is a metadata call (<see cref="FileInfo"/>):
/// the file is not opened, so it cannot be read, locked or held up by an editor that has it open.
/// </para>
/// </summary>
/// <param name="Path">The path asked about, as given.</param>
/// <param name="Exists">False for a file that is not there. A directory of that name counts as not there.</param>
/// <param name="Length">Bytes; 0 for a file that is not there, -1 when the file system would not say (an access error).</param>
/// <param name="LastWriteUtc">Last write time in UTC; <c>default</c> for a file that is not there.</param>
public readonly record struct FileStamp(string Path, bool Exists, long Length, DateTime LastWriteUtc)
{
    /// <summary>Stats <paramref name="path"/>. Never throws and never opens the file.</summary>
    public static FileStamp Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(path, true, info.Length, info.LastWriteTimeUtc)
                : new FileStamp(path, false, 0, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            // The file system would not say (or the file went between the two calls). One more answer for the same file, and any difference
            // from the last stamp counts as a change: a list is read again too often, never trusted too long.
            return new FileStamp(path, false, -1, default);
        }
    }
}

/// <summary>
/// The disk signature of the two files that decide what a DefenseClaw installation is configured to do: <c>config.yaml</c> and
/// <c>.env</c> (the Mac companion's <c>InstallationContext.diskSignature</c>: path, modified time and size of each). A panel takes one
/// when a read starts and keeps it with the rows; a later signature that differs says the rows were read from a configuration that has
/// since moved, so they may be out of date (<c>CatalogTrust</c>).
/// <para>
/// Metadata only: see <see cref="FileStamp"/>. The cost is two stat calls, so it is taken when a read starts and ends, when a change is
/// requested and when a panel comes back on screen - never on a timer and never per property read. What tells a running app that the files
/// moved is <c>AppServices.ConfigReloaded</c>, which the existing <see cref="ConfigChangeToken"/> raises for <i>both</i> files; this
/// signature is how a panel decides whether that reload postdates its read, and what covers the gap before the watcher has spoken.
/// </para>
/// <para>
/// Length and last-write time cannot tell two saves apart that have the same length and land inside one file-system clock tick. That is
/// the Mac's rule as well, and it only matters for an edit made within a few milliseconds of the read that is on screen.
/// </para>
/// </summary>
public readonly record struct ConfigDiskSignature(FileStamp Config, FileStamp Env)
{
    /// <summary>The signature of the installation <paramref name="paths"/> describes.</summary>
    public static ConfigDiskSignature Capture(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Capture(paths.ConfigFilePath, paths.EnvFilePath);
    }

    public static ConfigDiskSignature Capture(string configPath, string envPath) =>
        new(FileStamp.Capture(configPath), FileStamp.Capture(envPath));
}
