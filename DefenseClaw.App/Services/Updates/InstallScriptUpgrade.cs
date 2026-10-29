using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// The pure parts of the in-app upgrade to DefenseClaw 1.0 through the release's <c>install.ps1</c> (CUST-346): the argv, what the installer
/// changes, its exit codes, its log, and the migration summary it leaves behind. Nothing here starts a process or touches the machine except by
/// reading files it is pointed at.
/// </summary>
public static class InstallScriptPlan
{
    /// <summary>install.ps1's own version check (<c>Test-Version</c>): three dotted numbers, no leading zeros, nothing else.</summary>
    private static readonly Regex StrictVersion = new(
        @"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>What the installer changes on this machine, in the words the card and the review use.</summary>
    public static IReadOnlyList<string> WhatChanges { get; } =
    [
        "New audit history: the old audit.db is kept in %USERPROFILE%\\.defenseclaw\\previous\\data and the 1.0 database starts empty.",
        "Config moves to version 9. The old file is kept as config.yaml.v8.bak and the migration is written to migration-v9.json.",
        "The Setup install is removed; its files are kept in %USERPROFILE%\\.defenseclaw\\previous\\legacy-setup.",
        "The CLI moves to %USERPROFILE%\\.local\\bin (added to your user PATH).",
        "The gateway is stopped, replaced and started again; the installer restores the previous install by itself if the upgrade, the migration or the health check fails.",
        "Rollback is available afterwards, with one limit: the installer cannot restore a DefenseClaw Setup install automatically (see the Roll back note).",
    ];

    /// <summary>
    /// The version install.ps1 is told to install, in the one shape it accepts (<c>X.Y.Z</c>; a leading <c>v</c> is dropped), or false. A tag
    /// with a prerelease tail is refused here even though the app can name it elsewhere: the script would exit 2 on it.
    /// </summary>
    public static bool TryNormalizeVersion(string? tag, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag;
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        if (!StrictVersion.IsMatch(trimmed))
        {
            return false;
        }

        version = trimmed;
        return true;
    }

    /// <summary>
    /// The exact arguments <c>powershell.exe</c> is started with: <c>-NoProfile -ExecutionPolicy Bypass -File &lt;staged&gt; -Version X.Y.Z</c>, plus
    /// <c>-Yes</c> only when the operator opted in (without it the installer asks its own questions in its console).
    /// </summary>
    /// <exception cref="ArgumentException">An empty path, or a version install.ps1 would not accept.</exception>
    public static IReadOnlyList<string> BuildArgv(string stagedScriptPath, string version, bool yes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedScriptPath);
        if (!TryNormalizeVersion(version, out var normalized))
        {
            throw new ArgumentException("The version is not X.Y.Z, so install.ps1 would refuse it.", nameof(version));
        }

        var argv = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", stagedScriptPath, "-Version", normalized };
        if (yes)
        {
            argv.Add("-Yes");
        }

        return argv;
    }

    /// <summary>The rollback command: the same staged script with <c>-Rollback</c> (and <c>-Yes</c> only on opt-in). No version is passed.</summary>
    public static IReadOnlyList<string> BuildRollbackArgv(string stagedScriptPath, bool yes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedScriptPath);

        var argv = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", stagedScriptPath, "-Rollback" };
        if (yes)
        {
            argv.Add("-Yes");
        }

        return argv;
    }

    /// <summary>How the run ended, from install.ps1's documented exit codes (<c>-Help</c>: 0 installed, 1 restored, 2 usage, 3 and 4 installed with a follow-up).</summary>
    public static InstallExitInfo MapExit(int? exitCode, bool rollback = false)
    {
        var what = rollback ? "The rollback" : "The upgrade";
        return exitCode switch
        {
            0 => new InstallExitInfo(InstallOutcome.Success, "Succeeded", "Ok", $"{what} finished (exit 0).", ChangedMachine: true, OffersRollback: !rollback),
            2 => new InstallExitInfo(
                InstallOutcome.UsageError, "Not started", "Warn",
                "install.ps1 refused its arguments (exit 2) before it changed anything. Nothing was changed; read the console and the log.",
                ChangedMachine: false, OffersRollback: false),
            1 => new InstallExitInfo(
                InstallOutcome.FailedRestored, "Failed", "Bad",
                $"{what} did not finish (exit 1). The installer says it restores the previous install by itself; check the install log to see what it restored.",
                ChangedMachine: true, OffersRollback: !rollback),
            3 => new InstallExitInfo(
                InstallOutcome.InstalledNeedsAttention, "Installed, needs attention", "Warn",
                $"{what} installed DefenseClaw (exit 3), but a connector needs attention before it is guarded again. The install log says which.",
                ChangedMachine: true, OffersRollback: !rollback),
            4 => new InstallExitInfo(
                InstallOutcome.InstalledNeedsAttention, "Installed, needs attention", "Warn",
                $"{what} installed DefenseClaw (exit 4), but the first-run quickstart failed. The install log shows the command to re-run it.",
                ChangedMachine: true, OffersRollback: !rollback),
            null => new InstallExitInfo(
                InstallOutcome.Unknown, "Unknown", "Warn",
                $"{what} ended without an exit code this app could read. Check the console window and the install log before doing anything else.",
                ChangedMachine: true, OffersRollback: !rollback),
            var other => new InstallExitInfo(
                InstallOutcome.Failed, "Failed", "Bad",
                $"{what} failed (exit {(other ?? 0).ToString(CultureInfo.InvariantCulture)}). The installer restores the previous install automatically when it can; check the install log to see what state it left.",
                ChangedMachine: true, OffersRollback: !rollback),
        };
    }

    /// <summary><c>%USERPROFILE%\.defenseclaw\logs</c> under <paramref name="dataDirectory"/>.</summary>
    public static string LogDirectory(string dataDirectory) => Path.Combine(dataDirectory, "logs");

    /// <summary>
    /// The newest <c>install-*.log</c> in the logs folder that was written at or after <paramref name="startedAtUtc"/> (minus a two-second allowance for
    /// the clock the file system keeps), or null. The installer names it with its own start time, so "written after the start" is what ties a log to this run.
    /// </summary>
    public static string? FindInstallLog(string dataDirectory, DateTimeOffset startedAtUtc)
    {
        try
        {
            var directory = LogDirectory(dataDirectory);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var floor = startedAtUtc.UtcDateTime - TimeSpan.FromSeconds(5);
            return new DirectoryInfo(directory)
                .EnumerateFiles("install-*.log")
                .Where(f => f.LastWriteTimeUtc >= floor)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The longest failure reason this app shows.</summary>
    public const int MaxFailureReasonChars = 400;

    /// <summary>The installer's phrase for an unresolved CAS receipt; the hint below keys on it.</summary>
    public const string ConnectorRecoveryPhrase = "active connector recovery remains";

    public const string ConnectorRecoveryHint = "Open Overview and use \"Clear the stuck pending write\", then run the upgrade again.";

    private static readonly Regex FailureLine = new(@"^  x (?<r>\S.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The last failure line (<c>  x &lt;reason&gt;</c>) of an install transcript, without its prefix, capped at <see cref="MaxFailureReasonChars"/>; null when
    /// there is none. Nothing else of the transcript is returned (its header names the user and machine).
    /// </summary>
    public static string? ExtractFailureReason(string? transcript)
    {
        if (string.IsNullOrEmpty(transcript))
        {
            return null;
        }

        try
        {
            string? last = null;
            foreach (Match m in FailureLine.Matches(transcript.Replace("\r\n", "\n").Replace('\r', '\n')))
            {
                last = m.Groups["r"].Value.Trim();
            }

            if (string.IsNullOrEmpty(last))
            {
                return null;
            }

            if (last.Length <= MaxFailureReasonChars)
            {
                return last;
            }

            // The retry command is at the end of the line, so a cut would drop the switch the hint keys on: say it after the cut.
            var keepFlag = last.Contains(AcceptCurrentInstallFlag, StringComparison.Ordinal);
            var suffix = keepFlag ? " (" + AcceptCurrentInstallFlag + ")" : string.Empty;
            return last[..(MaxFailureReasonChars - 1 - suffix.Length)].TrimEnd() + "…" + suffix;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Reads <paramref name="logPath"/> (size-capped, shared) and returns <see cref="ExtractFailureReason"/>; null on any read problem.</summary>
    public static string? ReadFailureReason(string? logPath)
    {
        if (string.IsNullOrEmpty(logPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length > InstallRunReader.MaxLogBytes)
            {
                return null;
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ExtractFailureReason(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The switch install.ps1 1.0.4 added (<c>-AcceptCurrentInstall</c>; the exit codes are unchanged). An interrupted install fails with exit 1 and a line that
    /// ends in the command to run again, with this switch when the earlier run's record has to be settled against what is on the machine now.
    /// </summary>
    public const string AcceptCurrentInstallFlag = "-AcceptCurrentInstall";

    public const string AcceptCurrentInstallHint =
        "The installer finished nothing and says to run it again with -AcceptCurrentInstall, which keeps the config and connectors as they are now instead of " +
        "restoring the earlier install's. Use it only if you changed them on purpose; run the command it printed in a console, because this app does not add the switch.";

    /// <summary>
    /// The one-line hint for a reason that names an unresolved connector recovery, or that tells the operator to run the installer again with
    /// <see cref="AcceptCurrentInstallFlag"/>; null otherwise.
    /// </summary>
    public static string? HintFor(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        if (reason.Contains(ConnectorRecoveryPhrase, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectorRecoveryHint;
        }

        return reason.Contains(AcceptCurrentInstallFlag, StringComparison.Ordinal) ? AcceptCurrentInstallHint : null;
    }

    /// <summary>Where install.ps1 leaves what it migrated; the first file that exists wins.</summary>
    public static IReadOnlyList<string> MigrationFileCandidates(string dataDirectory) =>
    [
        Path.Combine(dataDirectory, "migration-v9.json"),
        Path.Combine(dataDirectory, "migrations", "migration-v9.json"),
        Path.Combine(dataDirectory, "previous", "migration-v9.json"),
    ];

    /// <summary>The largest migration file this app reads; the real one is a few kilobytes.</summary>
    public const long MaxMigrationFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// A short summary of <c>migration-v9.json</c> (counts of moved values and of conflicts), or null when there is no such file. The file's layout
    /// is the installer's own; this reads it tolerantly (an array counts its items, an object its members, a number is the count) and, when it
    /// finds neither a moved nor a conflict entry, says only how many top-level fields there are rather than guess.
    /// </summary>
    public static MigrationSummary? ReadMigrationSummary(string dataDirectory, DateTimeOffset? now = null)
    {
        string? path;
        try
        {
            path = MigrationFileCandidates(dataDirectory).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (path is null)
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > MaxMigrationFileBytes)
            {
                return new MigrationSummary(path, null, null, 0, "migration-v9.json is larger than this app reads; open it to read it.");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MigrationSummary(path, null, null, 0, "migration-v9.json is not a JSON object; open it to read it.");
            }

            int? moved = null;
            int? conflicts = null;
            int? removed = null;
            int? actionRows = null;
            int? fromVersion = null;
            int? toVersion = null;
            DateTimeOffset? migratedAt = null;
            var fields = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                fields++;
                switch (property.Name.ToLowerInvariant())
                {
                    case "moved":
                        moved = CountOf(property.Value);
                        break;
                    case "conflicts":
                        conflicts = CountOf(property.Value);
                        break;
                    case "removed":
                        removed = CountOf(property.Value);
                        break;
                    case "actions_rows_moved":
                        actionRows = CountOf(property.Value);
                        break;
                    case "from_version":
                        fromVersion = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var f) ? f : null;
                        break;
                    case "to_version":
                        toVersion = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var t) ? t : null;
                        break;
                    case "migrated_at":
                        migratedAt = property.Value.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                        break;
                }
            }

            var parts = new List<string>();
            if (moved is { } m)
            {
                parts.Add(Counted(m, "value moved", "values moved"));
            }

            if (conflicts is { } c)
            {
                parts.Add(Counted(c, "conflict", "conflicts"));
            }

            if (removed is { } r)
            {
                parts.Add(Counted(r, "removed", "removed"));
            }

            if (actionRows is { } a && a > 0)
            {
                parts.Add(Counted(a, "action row moved", "action rows moved"));
            }

            string text;
            if (parts.Count == 0)
            {
                text = $"migration-v9.json has {fields} top-level field(s); open it to read them.";
            }
            else
            {
                var what = fromVersion is { } fv && toVersion is { } tv
                    ? $"config {fv.ToString(CultureInfo.InvariantCulture)} → {tv.ToString(CultureInfo.InvariantCulture)}"
                    : "config migration";
                var when = migratedAt is { } at ? " on " + InstallRunReader.FormatWhen(at, now ?? DateTimeOffset.Now) : string.Empty;
                text = $"{what}{when}: {string.Join(", ", parts)}";
            }

            return new MigrationSummary(path, moved, conflicts, fields, text) { Removed = removed, ActionRowsMoved = actionRows, FromVersion = fromVersion, ToVersion = toVersion, MigratedAt = migratedAt };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new MigrationSummary(path, null, null, 0, "migration-v9.json could not be read (" + ex.GetType().Name + "); open it to read it.");
        }
    }

    private static string Counted(int n, string one, string many) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static int? CountOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength(),
        JsonValueKind.Object => value.EnumerateObject().Count(),
        JsonValueKind.Number when value.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>
    /// Whether <c>-Rollback</c> can work: it needs <c>previous\VERSION</c>, and the installer refuses when <c>previous\legacy-setup</c> exists (a Setup
    /// install it cannot restore automatically). Read-only.
    /// </summary>
    public static RollbackAvailability CheckRollback(string dataDirectory)
    {
        try
        {
            var previous = Path.Combine(dataDirectory, "previous");
            if (!File.Exists(Path.Combine(previous, "VERSION")))
            {
                return new RollbackAvailability(false, "No previous install is kept.");
            }

            if (Directory.Exists(Path.Combine(previous, "legacy-setup")))
            {
                return new RollbackAvailability(
                    false,
                    "The previous install is DefenseClaw Setup, which the installer cannot restore automatically. To go back: run 'defenseclaw uninstall', " +
                    "then run that release's DefenseClawSetup-x64.exe. Its files and your pre-upgrade data are in " + previous + ".");
            }

            return new RollbackAvailability(true, "The installer will swap back to the previous install.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RollbackAvailability(false, "The previous install could not be inspected: " + ex.Message);
        }
    }
}

/// <summary>How an install.ps1 run ended, in the app's terms.</summary>
public enum InstallOutcome
{
    Success,
    UsageError,
    FailedRestored,
    Failed,
    InstalledNeedsAttention,
    CouldNotStart,
    Unknown,
}

/// <summary>What an exit code means: a label, a tone key (Ok / Warn / Bad), a sentence, and whether the machine may have been changed.</summary>
public sealed record InstallExitInfo(InstallOutcome Outcome, string Label, string ToneKey, string Message, bool ChangedMachine, bool OffersRollback);

/// <summary>The counts read from <c>migration-v9.json</c>; null counts were not found in the file.</summary>
public sealed record MigrationSummary(string FilePath, int? Moved, int? Conflicts, int FieldCount, string Text)
{
    public int? Removed { get; init; }

    public int? ActionRowsMoved { get; init; }

    /// <summary>The config versions the migration went between (<c>from_version</c>, <c>to_version</c>), when the file says.</summary>
    public int? FromVersion { get; init; }

    public int? ToVersion { get; init; }

    /// <summary>When the installer migrated (<c>migrated_at</c>, UTC in the file), when the file says.</summary>
    public DateTimeOffset? MigratedAt { get; init; }
}

/// <summary>An Add/Remove Programs entry as the post-flight needs it.</summary>
public sealed record UninstallEntry(string? DisplayName, string? UninstallString);

/// <summary>The registry and file facts the leftover rows read. A seam: the app reads HKCU, the tests answer from memory.</summary>
public interface ILeftoverProbes
{
    /// <summary>The value <paramref name="name"/> under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, or null.</summary>
    string? RunValue(string name);

    /// <summary>The Add/Remove Programs entry <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;keyName&gt;</c>, or null.</summary>
    UninstallEntry? FindUninstallEntry(string keyName);

    bool FileExists(string path);
}

/// <summary>Reads the real HKCU keys, read-only. Never writes.</summary>
public sealed class RegistryLeftoverProbes : ILeftoverProbes
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public string? RunValue(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public UninstallEntry? FindUninstallEntry(string keyName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey + "\\" + keyName, writable: false);
            return key is null
                ? null
                : new UninstallEntry(key.GetValue("DisplayName") as string, key.GetValue("UninstallString") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Two things the 1.0 installer leaves behind from a Setup install, shown as read-only warnings after an upgrade (CUST-346). This app changes neither.
/// </summary>
public static class PostUpgradeLeftovers
{
    public const string AutostartId = "autostart";
    public const string UninstallEntryId = "uninstall-entry";

    /// <summary>The Run value DefenseClaw Setup 0.8.x registered for the gateway.</summary>
    public const string RunValueName = "DefenseClawGateway";

    /// <summary>The Add/Remove Programs key DefenseClaw Setup 0.8.x registered.</summary>
    public const string UninstallKeyName = "DefenseClaw";

    public static IReadOnlyList<PreflightRow> Evaluate(ILeftoverProbes probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var rows = new List<PreflightRow>();

        if (probes.RunValue(RunValueName) is { Length: > 0 } run && ExecutableOf(run) is { } target && !probes.FileExists(target))
        {
            rows.Add(new PreflightRow(
                AutostartId,
                "Gateway autostart",
                PreflightStatus.Warn,
                $"The Windows sign-in entry \"{RunValueName}\" still points to {target}, which the upgrade removed, and DefenseClaw 1.0 adds no replacement. " +
                "The gateway will not start when you sign in. Turn on this app's auto-start setting (Settings, Startup) to start it instead. " +
                "This app does not change that registry value."));
        }

        if (probes.FindUninstallEntry(UninstallKeyName) is { } entry)
        {
            var name = string.IsNullOrWhiteSpace(entry.DisplayName) ? "DefenseClaw" : entry.DisplayName.Trim();
            var cached = string.IsNullOrWhiteSpace(entry.UninstallString) ? "a cached 0.x Setup program" : "the cached 0.x Setup program (" + entry.UninstallString.Trim() + ")";
            rows.Add(new PreflightRow(
                UninstallEntryId,
                "Add/Remove Programs entry",
                PreflightStatus.Warn,
                $"Windows Settings still lists \"{name}\", and its uninstaller points to {cached}. Do not uninstall it from Windows Settings on a 1.0 machine: " +
                "it is the old Setup's entry, not the 1.0 install's. To remove DefenseClaw 1.0 use 'defenseclaw uninstall'. This app does not change that entry."));
        }

        return rows;
    }

    /// <summary>The program a Run value starts: the quoted path, or the text up to <c>.exe</c>; null when there is none.</summary>
    internal static string? ExecutableOf(string runValue)
    {
        var text = runValue.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? text[..(exe + 4)] : text;
    }
}

/// <summary>Whether the installer's rollback can run, and if not, why (shown next to the button).</summary>
public sealed record RollbackAvailability(bool IsAvailable, string Note);

// ---------------------------------------------------------------------------------------------------------------------
// Pre-flight
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>One pre-flight row's verdict.</summary>
public enum PreflightStatus
{
    Pass,
    Warn,

    /// <summary>The run cannot start while this holds.</summary>
    Block,
}

/// <summary>One line of the checklist: what was checked, the verdict, and the reason in a sentence.</summary>
public sealed record PreflightRow(string Id, string Title, PreflightStatus Status, string Reason)
{
    /// <summary>The id of the one action the row offers (<see cref="PreflightActionIds"/>), or null for text only.</summary>
    public string? ActionId { get; init; }

    /// <summary>The label of that action's button.</summary>
    public string? ActionLabel { get; init; }

    public bool HasAction => ActionId is not null;

    /// <summary>The tone key the badge maps to: Ok / Warn / Bad.</summary>
    public string ToneKey => Status switch
    {
        PreflightStatus.Pass => "Ok",
        PreflightStatus.Warn => "Warn",
        _ => "Bad",
    };

    public string StatusLabel => Status switch
    {
        PreflightStatus.Pass => "Pass",
        PreflightStatus.Warn => "Warning",
        _ => "Blocked",
    };
}

/// <summary>The actions a pre-flight row can carry.</summary>
public static class PreflightActionIds
{
    /// <summary>Show the Overview panel (where "Clear the stuck pending write" lives).</summary>
    public const string OpenOverview = "open-overview";
}

/// <summary>The checklist as a whole.</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightRow> Rows)
{
    public bool HasBlock => Rows.Any(r => r.Status == PreflightStatus.Block);

    public bool HasWarning => Rows.Any(r => r.Status == PreflightStatus.Warn);

    /// <summary>The first blocking reason, for a tooltip; null when nothing blocks.</summary>
    public string? BlockReason => Rows.FirstOrDefault(r => r.Status == PreflightStatus.Block)?.Reason;
}

/// <summary>Where the staged install.ps1 stands, as the checklist needs it.</summary>
public enum PreflightVerification
{
    /// <summary>Nothing downloaded yet.</summary>
    NotStarted,

    /// <summary>SHA-256 matches checksums.txt and the signature on it was verified by cosign.</summary>
    Verified,

    /// <summary>SHA-256 matches, but the signature on checksums.txt was not verified (no cosign, not published, files unavailable).</summary>
    HashOnly,

    /// <summary>A check failed; nothing is staged.</summary>
    Failed,
}

/// <summary>The machine facts the checklist reads. A seam: the production probes read the machine, the tests inject answers.</summary>
public interface IUpgradeProbes
{
    /// <summary>Names (no extension, any case) of the processes running now.</summary>
    IReadOnlyList<string> RunningProcessNames();

    /// <summary>
    /// The processes running now, with the image path where the name alone does not say whose process it is (the Codex desktop app's main process). A probe
    /// that only knows names gets this for free: every process without a path.
    /// </summary>
    IReadOnlyList<RunningProcess> RunningProcesses() => RunningProcessNames().Select(static name => new RunningProcess(name)).ToArray();

    /// <summary>Free bytes on the volume that holds <paramref name="path"/>; null when unknown.</summary>
    long? FreeDiskBytes(string path);

    /// <summary>Total size of the <c>*.db</c> files directly in <paramref name="dataDirectory"/>.</summary>
    long DatabaseBytes(string dataDirectory);

    /// <summary>
    /// The pending guarded config writes (CAS v2 receipt sets) in <paramref name="dataDirectory"/>, of any age: the installer refuses to change
    /// version while any receipt remains. May throw I/O errors; the checklist turns that into a warning.
    /// </summary>
    IReadOnlyList<CasPendingWrite> PendingConfigWrites(string dataDirectory) =>
        CasReceipts.FindPending(dataDirectory, DateTimeOffset.UtcNow, TimeSpan.Zero);

    /// <summary>
    /// The names of every <c>.defenseclaw-cas-v2-*</c> file directly in <paramref name="dataDirectory"/>: exactly what install.ps1 refuses on, which
    /// also covers finished or garbage-collection receipts that <see cref="PendingConfigWrites"/> does not list. May throw I/O errors.
    /// </summary>
    IReadOnlyList<string> RecoveryReceiptNames(string dataDirectory) =>
        Directory.Exists(dataDirectory)
            ? Directory.EnumerateFiles(dataDirectory, CasReceipts.ReceiptPrefix + "*").Select(f => Path.GetFileName(f)).ToArray()
            : Array.Empty<string>();
}

/// <summary>The probes over the real machine (read-only).</summary>
public sealed class SystemUpgradeProbes : IUpgradeProbes
{
    public IReadOnlyList<string> RunningProcessNames() => AgentProcessProbe.RunningProcessNames();

    public IReadOnlyList<RunningProcess> RunningProcesses() => AgentProcessProbe.RunningProcesses();

    public long? FreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public long DatabaseBytes(string dataDirectory)
    {
        try
        {
            return Directory.Exists(dataDirectory)
                ? new DirectoryInfo(dataDirectory).EnumerateFiles("*.db").Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>The read-only checklist that runs before the upgrade is offered (CUST-346).</summary>
public static class InstallScriptPreflight
{
    public const string InstallationId = "installation";
    public const string CodexId = "codex";
    public const string ClaudeCodeId = "claude-code";
    public const string ProcessesId = "processes";
    public const string DiskId = "disk";
    public const string VerificationId = "verification";
    public const string PendingWritesId = "pending-writes";

    /// <summary>The free space the upgrade wants, as a multiple of the databases it moves and may copy.</summary>
    public const int DiskFactor = 2;

    /// <param name="probes">What is running and how much disk there is.</param>
    /// <param name="installationBlockedReason">The installation guard's sentence (managed, read-only, developer runtime), or null.</param>
    /// <param name="dataDirectory">The DefenseClaw data directory (<c>~\.defenseclaw</c>).</param>
    /// <param name="verification">Where the staged install.ps1 stands.</param>
    /// <param name="verificationDetail">One sentence about it, shown as the row's reason.</param>
    public static PreflightReport Evaluate(
        IUpgradeProbes probes,
        string? installationBlockedReason,
        string dataDirectory,
        PreflightVerification verification,
        string verificationDetail)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        var rows = new List<PreflightRow>();

        // The installation guard comes first and is the only row that always blocks.
        rows.Add(installationBlockedReason is { } blocked
            ? new PreflightRow(InstallationId, "Installation", PreflightStatus.Block, blocked)
            : new PreflightRow(InstallationId, "Installation", PreflightStatus.Pass, "This is a user-owned installation that this app may change."));

        IReadOnlyList<RunningProcess> processes;
        try
        {
            processes = probes.RunningProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            processes = Array.Empty<RunningProcess>();
        }

        var running = processes.Select(static p => p.Name).ToArray();

        // A Codex agent (codex.exe, or the Codex desktop app's main process), not the app's always-on sandbox service (CUST-358).
        var codex = processes.Where(AgentProcessProbe.IsCodexProcess).ToArray();
        rows.Add(codex.Length > 0
            ? new PreflightRow(CodexId, "Codex", PreflightStatus.Warn,
                "Codex is running. Quit it first: the installer rewrites its hook configuration, and a running Codex keeps the old one.")
            : new PreflightRow(CodexId, "Codex", PreflightStatus.Pass, "Codex is not running."));

        var claude = running.Where(AgentProcessProbe.IsClaudeName).ToArray();
        rows.Add(claude.Length > 0
            ? new PreflightRow(ClaudeCodeId, "Claude Code", PreflightStatus.Warn,
                "claude.exe is running (Claude Code, or the Claude desktop app, which has the same process name). Quit it first: the installer rewrites " +
                "Claude Code's hook configuration, and a running session keeps the old hooks.")
            : new PreflightRow(ClaudeCodeId, "Claude Code", PreflightStatus.Pass, "Claude Code is not running."));

        var others = running
            .Where(n => n.StartsWith("defenseclaw", StringComparison.OrdinalIgnoreCase))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        rows.Add(others.Length > 0
            ? new PreflightRow(ProcessesId, "DefenseClaw processes", PreflightStatus.Pass,
                $"Running: {string.Join(", ", others)}. That is fine: the installer stops the gateway itself and starts it again. " +
                "If a file is held open it reports which program, and this app releases its own database handles before it starts.")
            : new PreflightRow(ProcessesId, "DefenseClaw processes", PreflightStatus.Pass, "No other DefenseClaw program is running."));

        rows.Add(PendingWritesRow(probes, dataDirectory));

        long databases;
        long? free;
        try
        {
            databases = probes.DatabaseBytes(dataDirectory);
            free = probes.FreeDiskBytes(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            databases = 0;
            free = null;
        }

        var needed = databases * DiskFactor;
        rows.Add(free is null
            ? new PreflightRow(DiskId, "Disk space", PreflightStatus.Warn, "The free space on the data drive could not be read.")
            : free.Value < needed
                ? new PreflightRow(DiskId, "Disk space", PreflightStatus.Block,
                    $"{FormatBytes(free.Value)} free, but the upgrade wants {DiskFactor}x the size of the databases ({FormatBytes(databases)}), {FormatBytes(needed)}. Free some space first.")
                : new PreflightRow(DiskId, "Disk space", PreflightStatus.Pass,
                    $"{FormatBytes(free.Value)} free; the databases are {FormatBytes(databases)} (needs {FormatBytes(needed)})."));

        rows.Add(verification switch
        {
            PreflightVerification.Verified => new PreflightRow(VerificationId, "Release verification", PreflightStatus.Pass, verificationDetail),
            PreflightVerification.HashOnly => new PreflightRow(VerificationId, "Release verification", PreflightStatus.Warn, verificationDetail),
            PreflightVerification.Failed => new PreflightRow(VerificationId, "Release verification", PreflightStatus.Block, verificationDetail),
            _ => new PreflightRow(VerificationId, "Release verification", PreflightStatus.Warn, verificationDetail),
        });

        return new PreflightReport(rows);
    }

    /// <summary>
    /// install.ps1's <c>Assert-NoActiveConnectorRecovery</c> throws when any <c>.defenseclaw-cas-v2-*</c> receipt is in the data directory, whatever its
    /// age, so one pending set blocks. A directory that cannot be read only warns: the installer is the judge.
    /// </summary>
    private static PreflightRow PendingWritesRow(IUpgradeProbes probes, string dataDirectory)
    {
        const string title = "Pending config writes";
        IReadOnlyList<CasPendingWrite> pending;
        IReadOnlyList<string> receipts;
        try
        {
            pending = probes.PendingConfigWrites(dataDirectory);
            receipts = probes.RecoveryReceiptNames(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PreflightRow(PendingWritesId, title, PreflightStatus.Warn,
                "Could not check for pending config writes (" + ex.Message + "). The installer refuses to change version while any remain.");
        }

        if (pending.Count == 0 && receipts.Count == 0)
        {
            return new PreflightRow(PendingWritesId, title, PreflightStatus.Pass, "No pending config writes.");
        }

        if (pending.Count == 0)
        {
            // Receipts the app does not list as a pending write (finished or garbage-collection records) still make the installer refuse.
            var shown = string.Join(", ", receipts.Order(StringComparer.OrdinalIgnoreCase).Take(3)) + (receipts.Count > 3 ? ", ..." : string.Empty);
            return new PreflightRow(PendingWritesId, title, PreflightStatus.Block,
                $"{receipts.Count.ToString(CultureInfo.InvariantCulture)} DefenseClaw recovery file{(receipts.Count == 1 ? string.Empty : "s")} ({shown}) " +
                $"remain in {dataDirectory}. DefenseClaw's installer refuses to change version while any remain, and they are not a pending write this app can clear.");
        }

        var targets = pending
            .Select(p => p.TargetPath is { Length: > 0 } t ? Path.GetFileName(t) : "an unreadable target")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var count = pending.Count;
        return new PreflightRow(PendingWritesId, title, PreflightStatus.Block,
            $"{count.ToString(CultureInfo.InvariantCulture)} pending config write{(count == 1 ? string.Empty : "s")} ({string.Join(", ", targets)}). " +
            "DefenseClaw's installer refuses to change version while they remain. Open Overview and use \"Clear the stuck pending write\", then check again.")
        {
            ActionId = PreflightActionIds.OpenOverview,
            ActionLabel = "Open Overview",
        };
    }

    /// <summary>Bytes as "812 KB", "1.4 GB".</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):0.#} MB"),
        >= 1L << 10 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 10):0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes"),
    };
}
