namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>What is known of one path.</summary>
/// <param name="Path">The path asked about.</param>
/// <param name="Exists">False when there is nothing there (every other field is then default).</param>
/// <param name="IsDirectory">A folder.</param>
/// <param name="IsReparsePoint">A junction, symbolic link or any other reparse point: never followed.</param>
/// <param name="Length">Bytes, for a file.</param>
/// <param name="LastWriteUtc">Last write time.</param>
/// <param name="Security">Owner and access list; null when they could not be read (which is a fault, not a pass).</param>
public sealed record FsNode(
    string Path,
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    DateTime LastWriteUtc,
    NodeSecurity? Security)
{
    public static FsNode Missing(string path) => new(path, false, false, false, 0, default, null);
}

/// <summary>
/// The disk, as far as the helper and the app look at it. Reading is shared by both sides (the app's status and peer check, the helper's
/// refusals); the changing members are used by the helper only. The real implementation is <c>WindowsAdminFileSystem</c> in the helper;
/// every test uses an in-memory fake, so no test touches Program Files.
/// </summary>
public interface IAdminFileSystem
{
    /// <summary>Looks at <paramref name="path"/> without following a reparse point at its end. Never throws for a missing path.</summary>
    FsNode Stat(string path);

    /// <summary>
    /// Opens a file for reading with <c>FileShare.Read</c> only, so nobody can write to it, replace it or delete it while it is open, and
    /// throws <see cref="IOException"/> when the handle does not point at <paramref name="path"/> itself (a link or junction was swapped in).
    /// </summary>
    Stream OpenReadExclusive(string path);

    /// <summary>Creates the folder (and its parents) with an admin-only access list: owner Administrators, full control for SYSTEM and Administrators, read and execute for Users.</summary>
    void EnsureAdminFolder(string path);

    /// <summary>Creates a new empty file named for one copy attempt inside <paramref name="folder"/> and returns it open for writing.</summary>
    Stream CreateTemp(string folder, out string tempPath);

    /// <summary>Moves <paramref name="tempPath"/> over <paramref name="finalPath"/> (replacing it) in one step.</summary>
    void Replace(string tempPath, string finalPath);

    /// <summary>Deletes a file; a missing file is not an error.</summary>
    void DeleteFile(string path);

    /// <summary>Writes a small text file with the folder's access list (replacing it).</summary>
    void WriteText(string path, string text);

    /// <summary>Reads a small text file, at most <paramref name="maxBytes"/>; null when it is missing, too large or unreadable.</summary>
    string? ReadText(string path, int maxBytes);
}
