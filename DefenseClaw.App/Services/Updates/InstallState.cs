using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Updates;

/// <summary>What kind of run an install log or an Activity entry describes.</summary>
public enum InstallRunKind
{
    Unknown,
    Install,
    Upgrade,
    Downgrade,
    Reinstall,
    ReplaceSetup,
    Rollback,
}

/// <summary>How a run ended, as the Last install row shows it.</summary>
public enum InstallRunOutcome
{
    Unknown,
    Running,
    Installed,
    Failed,

    /// <summary>The run failed and the installer put the previous install back.</summary>
    Restored,
}

/// <summary>
/// What the newest install run did: from and to versions (null when unknown), when, how it ended, where its transcript is, and whether the
/// config was already current. Built from the install log, the app's own Activity record of the run, or both.
/// </summary>
public sealed record InstallRunSummary(
    InstallRunKind Kind,
    string? FromVersion,
    string? ToVersion,
    DateTimeOffset? StartedAt,
    InstallRunOutcome Outcome,
    string? LogPath,
    bool ConfigAlreadyCurrent,
    bool FromActivity)
{
    /// <summary>The installer's own last failure line (<c>  x ...</c>) from the transcript, capped; null when the run did not fail or none was written.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Reads the transcript <c>install.ps1</c> leaves in <c>~\.defenseclaw\logs\install-*.log</c> (a PowerShell transcript) and the app's Activity
/// record of the same run. Tolerant by design: the installer's wording is its own, an unrecognised log yields "unknown" parts and never throws.
/// </summary>
public static class InstallRunReader
{
    /// <summary>The largest transcript this app reads; a real one is a few kilobytes.</summary>
    public const long MaxLogBytes = 2 * 1024 * 1024;

    /// <summary>The two moments of one run that may differ: the log is created when the installer starts, Activity when the app launches it.</summary>
    private static readonly TimeSpan SameRunWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex StartTime = new(@"^Start time:\s*(\d{14})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex FileStamp = new(@"install-(\d{8}T\d{6})\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex HostApp = new(@"^Host Application:(?<line>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex RequestedVersion = new(@"-Version\s+v?(?<v>\d[\w.+\-]{0,30})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex InstalledNow = new(@"^\s*\+?\s*Installed DefenseClaw (?<v>\d[\w.+\-]*?)\.?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PriorInstalled = new(@"^\s*>\s*Installed:\s*(?<v>\d[\w.+\-]*?)\.?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Changed = new(@"\b(?<what>Upgraded|Downgraded) from (?<v>\d[\w.+\-]*?)\.?(?:\s|$)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ReplacedSetup = new(@"\bReplaced DefenseClaw Setup (?<v>\d[\w.+\-]*?)(?:;|\s|$)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex NotInstalled = new(@"DefenseClaw (?<v>\d[\w.+\-]*?) was not installed\.(?<rest>[^\r\n]*)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex FailedRestoring = new(@"(Installing \S+ failed; restoring|did not become healthy[^\r\n]*restoring)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ConfigCurrent = new(@"Configuration is current \(config_version \d+\)", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The newest <c>install-*.log</c> (by write time, then name), or null; never throws.</summary>
    public static string? FindNewestLog(string dataDirectory)
    {
        try
        {
            var directory = InstallScriptPlan.LogDirectory(dataDirectory);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return new DirectoryInfo(directory)
                .EnumerateFiles("install-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads and parses one log. A file that cannot be read or is not a transcript gives a summary of unknowns, never an exception.</summary>
    public static InstallRunSummary ReadLog(string path)
    {
        string text;
        DateTimeOffset? written = null;
        try
        {
            var info = new FileInfo(path);
            written = info.LastWriteTime;
            if (info.Length > MaxLogBytes)
            {
                return Parse(string.Empty, path, written);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            text = string.Empty;
        }

        return Parse(text, path, written);
    }

    /// <summary>Parses a transcript's text. <paramref name="fileTime"/> stands in for the start time when the text has none.</summary>
    public static InstallRunSummary Parse(string? text, string? path, DateTimeOffset? fileTime = null)
    {
        text ??= string.Empty;
        try
        {
            return ParseCore(text, path, fileTime);
        }
        catch (RegexMatchTimeoutException)
        {
            return new InstallRunSummary(InstallRunKind.Unknown, null, null, StartOf(string.Empty, path, fileTime), InstallRunOutcome.Unknown, path, false, false);
        }
    }

    private static InstallRunSummary ParseCore(string text, string? path, DateTimeOffset? fileTime)
    {
        var rollback = false;
        string? requested = null;
        if (HostApp.Match(text) is { Success: true } host)
        {
            var line = host.Groups["line"].Value;
            rollback = line.Contains("-Rollback", StringComparison.OrdinalIgnoreCase);
            requested = RequestedVersion.Match(line) is { Success: true } rv ? rv.Groups["v"].Value : null;
        }

        var prior = PriorInstalled.Match(text) is { Success: true } pm ? pm.Groups["v"].Value : null;
        var installed = LastMatch(InstalledNow, text);
        var kind = rollback ? InstallRunKind.Rollback : InstallRunKind.Unknown;
        string? from = prior;
        string? to = installed ?? requested;

        if (Changed.Match(text) is { Success: true } changed && !rollback)
        {
            from = changed.Groups["v"].Value;
            kind = string.Equals(changed.Groups["what"].Value, "Downgraded", StringComparison.Ordinal) ? InstallRunKind.Downgrade : InstallRunKind.Upgrade;
        }
        else if (ReplacedSetup.Match(text) is { Success: true } setup && !rollback)
        {
            from = setup.Groups["v"].Value;
            kind = InstallRunKind.ReplaceSetup;
        }
        else if (!rollback && installed is not null)
        {
            // Installed with neither "Upgraded from" nor "Replaced": a first install, or the same version over itself.
            kind = prior is null ? InstallRunKind.Install : string.Equals(prior, installed, StringComparison.OrdinalIgnoreCase) ? InstallRunKind.Reinstall : InstallRunKind.Upgrade;
        }

        var outcome = InstallRunOutcome.Unknown;
        if (NotInstalled.Match(text) is { Success: true } failed)
        {
            outcome = failed.Groups["rest"].Value.Contains("previous install is back", StringComparison.OrdinalIgnoreCase)
                ? InstallRunOutcome.Restored
                : InstallRunOutcome.Failed;
            to ??= failed.Groups["v"].Value;
        }
        else if (installed is not null)
        {
            outcome = InstallRunOutcome.Installed;
        }
        else if (FailedRestoring.IsMatch(text))
        {
            outcome = InstallRunOutcome.Restored;
        }

        return new InstallRunSummary(kind, from, to, StartOf(text, path, fileTime), outcome, path, ConfigCurrent.IsMatch(text), FromActivity: false)
        {
            FailureReason = outcome == InstallRunOutcome.Installed ? null : InstallScriptPlan.ExtractFailureReason(text),
        };
    }

    private static string? LastMatch(Regex regex, string text)
    {
        string? last = null;
        foreach (Match m in regex.Matches(text))
        {
            last = m.Groups["v"].Value;
        }

        return last;
    }

    private static DateTimeOffset? StartOf(string text, string? path, DateTimeOffset? fileTime)
    {
        if (StartTime.Match(text) is { Success: true } m &&
            DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Local));
        }

        if (path is not null && FileStamp.Match(path) is { Success: true } f &&
            DateTime.TryParseExact(f.Groups[1].Value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var named))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(named, DateTimeKind.Local));
        }

        return fileTime;
    }

    /// <summary>The newest finished-or-running install.ps1 run in the app's Activity ring, or null.</summary>
    public static CliInvocation? NewestActivityRun(IEnumerable<CliInvocation> activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity
            .Where(i => i.Argv.Any(a => a.EndsWith("install.ps1", StringComparison.OrdinalIgnoreCase)) &&
                        i.Argv.Any(a => string.Equals(a, "-File", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The one summary the card shows: the log's, with the outcome taken from the Activity entry's exit code when it is the same run (the
    /// installer's documented exit codes are more reliable than its wording), or the Activity entry alone when the log is missing or older.
    /// </summary>
    public static InstallRunSummary? Combine(InstallRunSummary? log, CliInvocation? activity)
    {
        if (activity is null)
        {
            return log;
        }

        var activityRollback = activity.Argv.Any(a => string.Equals(a, "-Rollback", StringComparison.OrdinalIgnoreCase));
        var outcome = ActivityOutcome(activity);

        if (log?.StartedAt is { } logStart && (activity.StartedAt - logStart).Duration() <= SameRunWindow)
        {
            return log with
            {
                Outcome = outcome == InstallRunOutcome.Unknown ? log.Outcome : outcome,
                Kind = log.Kind == InstallRunKind.Unknown && activityRollback ? InstallRunKind.Rollback : log.Kind,
                FromActivity = true,
            };
        }

        // The log is for an older run (or none was found): the Activity entry is the newer record.
        if (log?.StartedAt is { } older && older > activity.StartedAt)
        {
            return log;
        }

        var version = ArgAfter(activity.Argv, "-Version");
        return new InstallRunSummary(
            activityRollback ? InstallRunKind.Rollback : InstallRunKind.Unknown,
            null,
            version,
            activity.StartedAt,
            outcome,
            null,
            false,
            FromActivity: true);
    }

    private static InstallRunOutcome ActivityOutcome(CliInvocation activity)
    {
        if (activity.FinishedAt is null)
        {
            return InstallRunOutcome.Running;
        }

        if (activity.ExitCode is null)
        {
            return InstallRunOutcome.Unknown;
        }

        var rollback = activity.Argv.Any(a => string.Equals(a, "-Rollback", StringComparison.OrdinalIgnoreCase));
        return InstallScriptPlan.MapExit(activity.ExitCode, rollback).Outcome switch
        {
            InstallOutcome.Success or InstallOutcome.InstalledNeedsAttention => InstallRunOutcome.Installed,
            InstallOutcome.FailedRestored => InstallRunOutcome.Restored,
            InstallOutcome.UsageError or InstallOutcome.Failed or InstallOutcome.CouldNotStart => InstallRunOutcome.Failed,
            _ => InstallRunOutcome.Unknown,
        };
    }

    private static string? ArgAfter(IReadOnlyList<string> argv, string name)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
        {
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return InstallScriptPlan.TryNormalizeVersion(argv[i + 1], out var v) ? v : null;
            }
        }

        return null;
    }

    /// <summary>The newest log, parsed, combined with the app's Activity record. Null when there is neither.</summary>
    public static InstallRunSummary? Read(string dataDirectory, IEnumerable<CliInvocation> activity)
    {
        var path = FindNewestLog(dataDirectory);
        var log = path is null ? null : ReadLog(path);
        return Combine(log, NewestActivityRun(activity));
    }

    // ------------------------------------------------------------------------------------------------ display

    /// <summary>"12 Oct 11:58" (the year is added when it is not the current one), in local time.</summary>
    public static string FormatWhen(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToLocalTime();
        var format = local.Year == now.ToLocalTime().Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>The Last install row's text.</summary>
    public static string Describe(InstallRunSummary run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        var what = run.Kind switch
        {
            InstallRunKind.Install => "Install",
            InstallRunKind.Upgrade => "Upgrade",
            InstallRunKind.Downgrade => "Downgrade",
            InstallRunKind.Reinstall => "Reinstall",
            InstallRunKind.ReplaceSetup => "Replaced Setup",
            InstallRunKind.Rollback => "Rollback",
            _ => "Install run",
        };
        var outcome = run.Outcome switch
        {
            InstallRunOutcome.Installed => "installed",
            InstallRunOutcome.Failed => "failed",
            InstallRunOutcome.Restored => "failed, previous install restored",
            InstallRunOutcome.Running => "still running",
            _ => "unknown",
        };
        var when = run.StartedAt is { } at ? FormatWhen(at, now) : "unknown time";
        var text = $"{what} {run.FromVersion ?? "unknown"} → {run.ToVersion ?? "unknown"}, {when}: {outcome}";
        if (run.Outcome is InstallRunOutcome.Failed or InstallRunOutcome.Restored or InstallRunOutcome.Unknown && run.FailureReason is { Length: > 0 } reason)
        {
            text += ". Installer said: " + reason;
            if (InstallScriptPlan.HintFor(reason) is { } hint)
            {
                text += " " + hint;
            }
        }

        return text;
    }

    /// <summary>Ok / Warn / Bad / Neutral for the row's badge.</summary>
    public static string ToneKey(InstallRunOutcome outcome) => outcome switch
    {
        InstallRunOutcome.Installed => "Ok",
        InstallRunOutcome.Restored => "Warn",
        InstallRunOutcome.Failed => "Bad",
        _ => "Neutral",
    };
}

/// <summary>Small readers for the files beside the install: <c>config.yaml</c>'s version and <c>previous\VERSION</c>.</summary>
public static class InstallFiles
{
    private static readonly Regex ConfigVersionLine = new(@"^config_version:\s*(\d{1,6})\s*(#.*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Version = new(@"^v?(\d[0-9A-Za-z.+\-]{0,38})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The largest config.yaml this reads; a real one is a few kilobytes.</summary>
    public const long MaxConfigBytes = 4 * 1024 * 1024;

    /// <summary><c>config_version</c> from <c>config.yaml</c>, or null when the file is missing, large, or has no such line. Never throws.</summary>
    public static int? ReadConfigVersion(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "config.yaml");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var m = ConfigVersionLine.Match(reader.ReadToEnd());
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The version in <c>previous\VERSION</c> (a leading v dropped), or null when absent, unreadable or not a version.</summary>
    public static string? ReadPreviousVersion(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "previous", "VERSION");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024)
            {
                return null;
            }

            var text = File.ReadAllText(path, Encoding.UTF8).Trim().TrimStart('﻿');
            return Version.Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

/// <summary>
/// Everything the "Install state" section reads off the machine's files in one pass: the config version, the migration, the newest install run,
/// and what Roll back would go back to. Read fresh every time (nothing is cached between reads); no process is started.
/// </summary>
public sealed record InstallStateSnapshot(
    string ConfigVersionText,
    MigrationSummary? Migration,
    string MigrationText,
    string LastInstallText,
    string LastInstallToneKey,
    string? LastInstallLogPath,
    string? PreviousVersion,
    RollbackAvailability Rollback)
{
    public static InstallStateSnapshot Read(string dataDirectory, IEnumerable<CliInvocation> activity, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(activity);

        var config = InstallFiles.ReadConfigVersion(dataDirectory);
        var configText = config is { } c
            ? "Config version " + c.ToString(CultureInfo.InvariantCulture)
            : "Config version unknown (no config_version line in config.yaml)";

        var migration = InstallScriptPlan.ReadMigrationSummary(dataDirectory, now);
        var run = InstallRunReader.Read(dataDirectory, activity);

        var migrationText = migration?.Text ?? "No migration-v9.json was found.";
        if (migration is not null && run is { ConfigAlreadyCurrent: true } &&
            (migration.MigratedAt is not { } migrated || run.StartedAt is not { } started || started > migrated))
        {
            migrationText += (migrationText.EndsWith('.') ? " " : ". ") + "Last upgrade made no config changes.";
        }

        return new InstallStateSnapshot(
            configText,
            migration,
            migrationText,
            run is null ? "No install run was found (no install-*.log, nothing in Activity)." : InstallRunReader.Describe(run, now),
            run is null ? "Neutral" : InstallRunReader.ToneKey(run.Outcome),
            run?.LogPath,
            InstallFiles.ReadPreviousVersion(dataDirectory),
            InstallScriptPlan.CheckRollback(dataDirectory));
    }
}
