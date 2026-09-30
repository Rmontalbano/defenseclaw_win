namespace DefenseClaw.Core.Cli;

/// <summary>
/// The neutral working directory the Python <c>defenseclaw</c> CLI runs in: <c>%LOCALAPPDATA%\DefenseClaw.App\cwd</c>,
/// created on first use and left empty.
/// <para>
/// Empty on purpose. Click on Windows glob-expands every argument against the working directory, so a directory
/// holding files turns <c>skill block -- x*</c> into <c>skill block -- x1 x2</c>. Nothing in this app writes
/// there, and no DefenseClaw command is asked to (they address <c>~/.defenseclaw</c> absolutely); if something
/// ever does put files in it, <see cref="ArgvHazards"/> still describes the expansion truthfully, because it reads
/// this same directory.
/// </para>
/// </summary>
public static class CliWorkingDirectory
{
    /// <summary><c>%LOCALAPPDATA%\DefenseClaw.App\cwd</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "cwd");

    /// <summary>Creates <paramref name="path"/> if it is missing; false when it cannot be created (the caller falls back).</summary>
    public static bool TryEnsure(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            _ = Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
