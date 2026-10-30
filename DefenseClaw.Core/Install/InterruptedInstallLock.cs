using System.Globalization;
using System.IO;

namespace DefenseClaw.Core.Install;

/// <summary>What <see cref="InterruptedInstallLock.Read"/> found.</summary>
/// <param name="Path">The lock, <c>&lt;data dir&gt;\.install.lock</c>.</param>
/// <param name="HolderProcessId">The pid install.ps1 wrote into the lock, or null when there is none or it is not a number.</param>
public sealed record InstallLockFinding(string Path, int? HolderProcessId)
{
    /// <summary>
    /// True when the lock is a folder whose pid names no running process: its installer is proven gone, which is what 1.0.5's hooks and doctor call a
    /// stale lock (<c>installLockStale</c>, <c>_install_lock_state</c>). False for a lock without a pid, a plain file, and a pid some other process now has
    /// (the runtime then compares process identities this app does not read, so it is not called gone here).
    /// </summary>
    public bool HolderGone { get; init; }

    /// <summary>
    /// The first interrupted-install record found beside the lock (<c>previous.new</c>, <c>.repair</c>, <c>.rollback-hold</c>, <c>.lifecycle-txn.json</c>), or
    /// null. With one, 1.0.5 keeps the lock for the next installer run, which finishes or undoes that install; doctor leaves it alone.
    /// </summary>
    public string? RecoveryRecord { get; init; }

    /// <summary>A lock 1.0.5 ignores and <c>doctor --fix</c> removes: its installer is gone and no interrupted install keeps it.</summary>
    public bool IsStale => HolderGone && RecoveryRecord is null;

    /// <summary>A lock an interrupted install keeps after its installer is gone: only the installer's next run clears it.</summary>
    public bool IsRecovery => HolderGone && RecoveryRecord is not null;
}

/// <summary>
/// An <c>.install.lock</c> that no installer holds. install.ps1 (and install.sh) take <c>&lt;data dir&gt;\.install.lock</c>, a folder with a <c>pid</c> file, for
/// the length of an install; from 1.0.4 every per-user hook reads it and blocks while it exists (<c>hookInstallInProgress</c>, <c>gateway_cold_start.go</c>),
/// so one an interrupted install left behind keeps the agents blocked. The installer clears such a lock itself on its next run: it keeps it only when the pid in
/// it is a running <c>powershell</c> or <c>pwsh</c> process other than itself (<c>install.ps1</c>, "Another DefenseClaw install is running"). This applies the
/// same test and reads nothing else; it never removes the lock. From 1.0.5 a lock whose installer is proven gone no longer blocks a hook unless an
/// interrupted install keeps it; the finding says which (<see cref="InstallLockFinding.IsStale"/>, <see cref="InstallLockFinding.IsRecovery"/>).
/// </summary>
public static class InterruptedInstallLock
{
    /// <summary>The lock's name in the data directory.</summary>
    public const string LockName = ".install.lock";

    /// <summary>
    /// The records install.ps1 and install.sh leave beside the lock when an upgrade stops between the binary swap and its finish
    /// (<c>installRecoveryRecords</c>, hookexec/install_lock_state.go at 1.0.5).
    /// </summary>
    public static readonly IReadOnlyList<string> RecoveryRecords = new[] { "previous.new", ".repair", ".rollback-hold", ".lifecycle-txn.json" };

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

            var holderName = holder is { } running ? processName(running) : null;
            if (holderName is { } name &&
                (name.Equals("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new InstallLockFinding(path, holder)
            {
                HolderGone = holder is > 0 && holderName is null,
                RecoveryRecord = FindRecoveryRecord(dataDirectory),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A lock that cannot be read says nothing here: the hooks treat that as held, but the app cannot tell why, so it stays quiet.
            return null;
        }
    }

    /// <summary>The first <see cref="RecoveryRecords"/> entry present in <paramref name="dataDirectory"/> (a file or a folder), or null.</summary>
    private static string? FindRecoveryRecord(string dataDirectory)
    {
        foreach (var name in RecoveryRecords)
        {
            var record = System.IO.Path.Combine(dataDirectory, name);
            if (File.Exists(record) || Directory.Exists(record))
            {
                return record;
            }
        }

        return null;
    }

    private static bool Old(DateTime lastWriteUtc, DateTimeOffset now) =>
        now - new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)) >= Grace;
}
