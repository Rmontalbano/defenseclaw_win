using System.Globalization;
using System.IO;

namespace DefenseClaw.Core.Install;

/// <summary>What <see cref="InterruptedInstallLock.Read"/> found.</summary>
/// <param name="Path">The lock, <c>&lt;data dir&gt;\.install.lock</c>.</param>
/// <param name="HolderProcessId">The pid install.ps1 wrote into the lock, or null when there is none or it is not a number.</param>
public sealed record InstallLockFinding(string Path, int? HolderProcessId);

/// <summary>
/// An <c>.install.lock</c> that no installer holds. install.ps1 (and install.sh) take <c>&lt;data dir&gt;\.install.lock</c>, a folder with a <c>pid</c> file, for
/// the length of an install; from 1.0.4 every per-user hook reads it and blocks while it exists (<c>hookInstallInProgress</c>, <c>gateway_cold_start.go</c>),
/// so one an interrupted install left behind keeps the agents blocked. The installer clears such a lock itself on its next run: it keeps it only when the pid in
/// it is a running <c>powershell</c> or <c>pwsh</c> process other than itself (<c>install.ps1</c>, "Another DefenseClaw install is running"). This applies the
/// same test and reads nothing else; it never removes the lock.
/// </summary>
public static class InterruptedInstallLock
{
    /// <summary>The lock's name in the data directory.</summary>
    public const string LockName = ".install.lock";

    /// <summary>A lock younger than this is an install that has just started (the pid file is written a moment after the folder), not a left-over one.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The lock when it is present, old enough and held by no installer; otherwise null. <paramref name="processName"/> answers a pid with the process's
    /// name, or null when no such process runs (a test passes its own; the app passes a lookup that never throws).
    /// </summary>
    public static InstallLockFinding? Read(string dataDirectory, DateTimeOffset now, Func<int, string?> processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(processName);

        try
        {
            var path = System.IO.Path.Combine(dataDirectory, LockName);
            var folder = new DirectoryInfo(path);
            if (!folder.Exists)
            {
                // The hooks block on a lock of any file type; a plain file is just as much a leftover, and no installer holds it.
                var file = new FileInfo(path);
                return file.Exists && Old(file.LastWriteTimeUtc, now) ? new InstallLockFinding(path, null) : null;
            }

            if (!Old(folder.LastWriteTimeUtc, now))
            {
                return null;
            }

            int? holder = null;
            var pidFile = System.IO.Path.Combine(path, "pid");
            if (File.Exists(pidFile) &&
                int.TryParse(File.ReadLines(pidFile).FirstOrDefault()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                holder = pid;
            }

            if (holder is { } running && processName(running) is { } name &&
                (name.Equals("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new InstallLockFinding(path, holder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A lock that cannot be read says nothing here: the hooks treat that as held, but the app cannot tell why, so it stays quiet.
            return null;
        }
    }

    private static bool Old(DateTime lastWriteUtc, DateTimeOffset now) =>
        now - new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)) >= Grace;
}
