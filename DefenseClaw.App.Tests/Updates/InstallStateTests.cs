using System.Globalization;
using System.IO;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The files behind the "Install state" section: migration-v9.json, the install-*.log transcript, config.yaml's version and previous\VERSION, all
/// synthetic. The transcripts follow install.ps1's own wording (Write-Info/Write-Ok/Write-Err, "Upgraded from X.", "Replaced DefenseClaw Setup X;").
/// </summary>
public sealed class InstallStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Transcript(string start, string host, params string[] lines) =>
        "**********************\r\nWindows PowerShell transcript start\r\n" +
        $"Start time: {start}\r\nUsername: SYNTH\\user\r\nHost Application: C:\\Windows\\powershell.exe {host}\r\n**********************\r\n\r\n" +
        string.Join("\r\n", lines) + "\r\n**********************\r\nWindows PowerShell transcript end\r\n";

    private string Log(string name, string text, DateTime? written = null)
    {
        _ = Directory.CreateDirectory(_temp.File("logs"));
        var path = _temp.WriteFile(Path.Combine("logs", name), text);
        if (written is { } w)
        {
            File.SetLastWriteTimeUtc(path, w);
        }

        return path;
    }

    private void WritePrevious(string content)
    {
        _ = Directory.CreateDirectory(_temp.File("previous"));
        _ = _temp.WriteFile(Path.Combine("previous", "VERSION"), content);
    }

    private static string Local(DateTimeOffset utc, string format = "d MMM HH:mm") => utc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ migration-v9.json

    [Fact]
    public void The_migration_row_says_which_migration_and_when()
    {
        _ = _temp.WriteFile(
            "migration-v9.json",
            "{\"schema_version\":1,\"from_version\":8,\"to_version\":9,\"migrated_at\":\"2026-10-12T18:58:44Z\",\"moved\":[{\"source\":\"config\"}]," +
            "\"conflicts\":[],\"removed\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\"],\"actions_rows_moved\":0,\"notes\":[]}");

        var summary = InstallScriptPlan.ReadMigrationSummary(_temp.Path, Now);

        Assert.NotNull(summary);
        Assert.Equal(8, summary!.FromVersion);
        Assert.Equal(9, summary.ToVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T18:58:44Z", CultureInfo.InvariantCulture), summary.MigratedAt);
        var when = Local(DateTimeOffset.Parse("2026-10-12T18:58:44Z", CultureInfo.InvariantCulture));
        Assert.Equal($"config 8 → 9 on {when}: 1 value moved, 0 conflicts, 8 removed", summary.Text);
    }

    [Fact]
    public void A_migration_file_without_versions_or_a_time_still_reads_and_a_year_is_shown_when_it_is_not_this_one()
    {
        _ = _temp.WriteFile("migration-v9.json", "{\"moved\":[],\"conflicts\":[1,2],\"removed\":[],\"migrated_at\":\"not a time\"}");
        var bare = InstallScriptPlan.ReadMigrationSummary(_temp.Path, Now);
        Assert.Equal("config migration: 0 values moved, 2 conflicts, 0 removed", bare!.Text);
        Assert.Null(bare.MigratedAt);

        _ = _temp.WriteFile("migration-v9.json", "{\"from_version\":8,\"to_version\":9,\"migrated_at\":\"2025-03-04T12:00:00Z\",\"moved\":[]}");
        var old = InstallScriptPlan.ReadMigrationSummary(_temp.Path, Now);
        Assert.Contains("2025", old!.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ install logs

    [Fact]
    public void An_upgrade_log_gives_from_to_when_and_outcome()
    {
        var text = Transcript(
            "20261012164027", "-NoProfile -File C:\\x\\install.ps1 -Version 1.0.1 -Yes",
            "--- Preparing DefenseClaw 1.0.1 (windows/amd64)  [23:40:27Z]", "  > Installed: 1.0.0", "  OK Configuration is current (config_version 9).",
            "  + Installed DefenseClaw 1.0.1", "  DefenseClaw 1.0.1 is installed.", "  Upgraded from 1.0.0. Undo with: defenseclaw rollback");

        var run = InstallRunReader.Parse(text, @"C:\logs\install-20261012T164027.log");

        Assert.Equal(InstallRunKind.Upgrade, run.Kind);
        Assert.Equal("1.0.0", run.FromVersion);
        Assert.Equal("1.0.1", run.ToVersion);
        Assert.Equal(InstallRunOutcome.Installed, run.Outcome);
        Assert.True(run.ConfigAlreadyCurrent);
        Assert.Equal(new DateTime(2026, 10, 12, 16, 40, 27), run.StartedAt!.Value.LocalDateTime);
        Assert.Equal("Upgrade 1.0.0 → 1.0.1, 12 Oct 16:40: installed", InstallRunReader.Describe(run, Now));
        Assert.Equal("Ok", InstallRunReader.ToneKey(run.Outcome));
    }

    [Fact]
    public void A_downgrade_a_setup_replacement_and_a_first_install_are_told_apart()
    {
        var down = InstallRunReader.Parse(
            Transcript("20261012100000", "-Version 1.0.0", "  + Installed DefenseClaw 1.0.0", "  Downgraded from 1.0.1. Undo with: defenseclaw rollback"), null);
        Assert.Equal(InstallRunKind.Downgrade, down.Kind);
        Assert.Equal("1.0.1", down.FromVersion);
        Assert.Equal("1.0.0", down.ToVersion);

        var setup = InstallRunReader.Parse(
            Transcript("20261012115550", "-Version 1.0.0", "  > Installed: 0.8.10", "  + Installed DefenseClaw 1.0.0", "  Replaced DefenseClaw Setup 0.8.10; your config and data were kept."), null);
        Assert.Equal(InstallRunKind.ReplaceSetup, setup.Kind);
        Assert.Equal("0.8.10", setup.FromVersion);
        Assert.Equal("1.0.0", setup.ToVersion);
        Assert.False(setup.ConfigAlreadyCurrent);

        var fresh = InstallRunReader.Parse(Transcript("20261012115550", "-Version 1.0.0", "  + Installed DefenseClaw 1.0.0"), null);
        Assert.Equal(InstallRunKind.Install, fresh.Kind);
        Assert.Null(fresh.FromVersion);

        var same = InstallRunReader.Parse(Transcript("20261012115550", "-Version 1.0.0", "  > Installed: 1.0.0", "  + Installed DefenseClaw 1.0.0"), null);
        Assert.Equal(InstallRunKind.Reinstall, same.Kind);
    }

    [Fact]
    public void A_failed_run_is_failed_and_one_that_put_the_previous_install_back_is_restored()
    {
        var restored = InstallRunReader.Parse(
            Transcript(
                "20261012164027", "-Version 1.0.1", "  > Installed: 1.0.0", "  x Installing 1.0.1 failed; restoring 1.0.0",
                "DefenseClaw 1.0.1 was not installed. Your previous install is back. Log: C:\\logs\\install-1.log"), null);
        Assert.Equal(InstallRunOutcome.Restored, restored.Outcome);
        Assert.Equal("1.0.0", restored.FromVersion);
        Assert.Equal("1.0.1", restored.ToVersion);
        Assert.Equal("Warn", InstallRunReader.ToneKey(restored.Outcome));

        var failed = InstallRunReader.Parse(
            Transcript("20261012164027", "-Version 1.0.1", "  > Installed: 1.0.0", "DefenseClaw 1.0.1 was not installed. Nothing was left installed."), null);
        Assert.Equal(InstallRunOutcome.Failed, failed.Outcome);
        Assert.Equal("Bad", InstallRunReader.ToneKey(failed.Outcome));

        var unhealthy = InstallRunReader.Parse(Transcript("20261012164027", "-Version 1.0.1", "  x The 1.0.1 gateway did not become healthy within 90 s [x]; restoring 1.0.0"), null);
        Assert.Equal(InstallRunOutcome.Restored, unhealthy.Outcome);
    }

    [Fact]
    public void A_rollback_log_is_a_rollback()
    {
        var run = InstallRunReader.Parse(Transcript("20261012170000", "-File C:\\x\\install.ps1 -Rollback", "  > Installed: 1.0.1", "  + Installed DefenseClaw 1.0.0"), null);

        Assert.Equal(InstallRunKind.Rollback, run.Kind);
        Assert.Equal("1.0.1", run.FromVersion);
        Assert.Equal("1.0.0", run.ToVersion);
        Assert.Equal(InstallRunOutcome.Installed, run.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("Traceback (most recent call last):\n  File \"x\", line 1\nValueError")]
    [InlineData("\0\0\0 \u0001\u0002 binary \uFFFD\uFFFD")]
    [InlineData("Upgraded from")]
    [InlineData("Installed DefenseClaw")]
    [InlineData("Start time: 99999999999999")]
    public void Garbage_yields_unknown_and_never_throws(string text)
    {
        var run = InstallRunReader.Parse(text, @"C:\logs\not-a-log.txt");

        Assert.Equal(InstallRunOutcome.Unknown, run.Outcome);
        Assert.Null(run.FromVersion);
        Assert.Null(run.ToVersion);
        Assert.Equal(InstallRunKind.Unknown, run.Kind);
        Assert.Contains("unknown → unknown", InstallRunReader.Describe(run, Now), StringComparison.Ordinal);
        Assert.EndsWith("unknown", InstallRunReader.Describe(run, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_unreadable_or_oversize_file_is_unknown_and_the_name_supplies_the_time()
    {
        var missing = InstallRunReader.ReadLog(_temp.File(Path.Combine("logs", "install-20261001T080102.log")));
        Assert.Equal(InstallRunOutcome.Unknown, missing.Outcome);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 1, 2), missing.StartedAt!.Value.LocalDateTime);

        var big = _temp.File(Path.Combine("logs", "install-20261002T080102.log"));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        using (var stream = File.Create(big))
        {
            stream.SetLength(InstallRunReader.MaxLogBytes + 1);
        }

        Assert.Equal(InstallRunOutcome.Unknown, InstallRunReader.ReadLog(big).Outcome);

        // A log the installer still has open is read, not refused.
        var path = Log("install-20261003T080102.log", Transcript("20261003080102", "-Version 1.0.1", "  + Installed DefenseClaw 1.0.1"));
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(InstallRunOutcome.Installed, InstallRunReader.ReadLog(path).Outcome);
    }

    [Fact]
    public void The_newest_log_wins_and_a_folder_without_logs_is_fine()
    {
        Assert.Null(InstallRunReader.FindNewestLog(_temp.Path));

        _ = Log("install-20261012T115550.log", "old", DateTime.UtcNow.AddHours(-5));
        var newest = Log("install-20261012T164027.log", "new", DateTime.UtcNow.AddHours(-1));
        _ = Log("other.log", "ignored", DateTime.UtcNow);

        Assert.Equal(newest, InstallRunReader.FindNewestLog(_temp.Path));
    }

    // ------------------------------------------------------------------ the app's own Activity record

    private static IReadOnlyList<string> Argv(string version, bool rollback = false) =>
        rollback
            ? ["-NoProfile", "-File", @"C:\staging\install.ps1", "-Rollback"]
            : ["-NoProfile", "-File", @"C:\staging\install.ps1", "-Version", version];

    [Fact]
    public void Activity_supplies_the_outcome_from_the_exit_code_and_the_whole_row_when_the_log_is_missing()
    {
        using var services = TestServices.Create(_temp);
        var failed = services.Cli.BeginExternalRun("powershell.exe", Argv("1.0.1"), "note");
        services.Cli.CompleteExternalRun(failed, 1, null, null);

        // No log at all: Activity alone, no log link.
        var alone = InstallRunReader.Read(_temp.Path, services.Cli.Activity);
        Assert.NotNull(alone);
        Assert.True(alone!.FromActivity);
        Assert.Equal(InstallRunOutcome.Restored, alone.Outcome);
        Assert.Equal("1.0.1", alone.ToVersion);
        Assert.Null(alone.LogPath);

        // A log for the same run: the log's versions, Activity's exit code, and the log's link.
        var started = failed.StartedAt.LocalDateTime;
        var log = Log(
            $"install-{started:yyyyMMdd'T'HHmmss}.log",
            Transcript($"{started:yyyyMMddHHmmss}", "-Version 1.0.1", "  > Installed: 1.0.0", "  + Installed DefenseClaw 1.0.1"));
        var both = InstallRunReader.Read(_temp.Path, services.Cli.Activity);
        Assert.Equal(InstallRunOutcome.Restored, both!.Outcome);
        Assert.Equal("1.0.0", both.FromVersion);
        Assert.Equal(log, both.LogPath);
    }

    [Fact]
    public void An_older_activity_entry_does_not_override_a_newer_log_and_a_running_one_says_so()
    {
        using var services = TestServices.Create(_temp);
        var running = services.Cli.BeginExternalRun("powershell.exe", Argv("1.0.1"), "note");
        var future = DateTime.Now.AddHours(2);
        var log = Log(
            $"install-{future:yyyyMMdd'T'HHmmss}.log",
            Transcript($"{future:yyyyMMddHHmmss}", "-Version 1.0.2", "  + Installed DefenseClaw 1.0.2"));

        var newer = InstallRunReader.Read(_temp.Path, services.Cli.Activity);
        Assert.Equal("1.0.2", newer!.ToVersion);
        Assert.Equal(log, newer.LogPath);

        File.Delete(log);
        var onlyActivity = InstallRunReader.Read(_temp.Path, services.Cli.Activity);
        Assert.Equal(InstallRunOutcome.Running, onlyActivity!.Outcome);
        Assert.Contains("still running", InstallRunReader.Describe(onlyActivity, Now), StringComparison.Ordinal);
        services.Cli.CompleteExternalRun(running, 0, null, null);

        var rollback = services.Cli.BeginExternalRun("powershell.exe", Argv("", rollback: true), "note");
        services.Cli.CompleteExternalRun(rollback, 0, null, null);
        Assert.Equal(InstallRunKind.Rollback, InstallRunReader.Read(_temp.Path, services.Cli.Activity)!.Kind);
        Assert.Null(InstallRunReader.Read(_temp.Path, []));
    }

    // ------------------------------------------------------------------ the snapshot

    [Fact]
    public void The_snapshot_reads_the_config_version_the_migration_the_last_run_and_the_rollback_target()
    {
        _ = _temp.WriteFile("config.yaml", "# synthetic\r\nconfig_version: 9  # current\r\nmode: observe\r\n");
        _ = _temp.WriteFile("migration-v9.json", "{\"from_version\":8,\"to_version\":9,\"migrated_at\":\"2026-10-12T18:58:44Z\",\"moved\":[1],\"conflicts\":[],\"removed\":[1,2]}");
        WritePrevious("1.0.0\r\n");
        _ = Log(
            "install-20261012T164027.log",
            Transcript(
                "20261013164027", "-Version 1.0.1", "  > Installed: 1.0.0", "  OK Configuration is current (config_version 9).",
                "  + Installed DefenseClaw 1.0.1", "  Upgraded from 1.0.0. Undo with: defenseclaw rollback"));

        var state = InstallStateSnapshot.Read(_temp.Path, [], Now);

        Assert.Equal("Config version 9", state.ConfigVersionText);
        Assert.DoesNotContain("schema", state.ConfigVersionText, StringComparison.Ordinal);
        Assert.StartsWith("config 8 → 9 on ", state.MigrationText, StringComparison.Ordinal);
        Assert.Contains(": 1 value moved, 0 conflicts, 2 removed", state.MigrationText, StringComparison.Ordinal);
        Assert.EndsWith(" Last upgrade made no config changes.", state.MigrationText, StringComparison.Ordinal);
        Assert.Matches(@"\. Last upgrade made no config changes\.$", state.MigrationText);
        Assert.StartsWith("Upgrade 1.0.0 → 1.0.1, ", state.LastInstallText, StringComparison.Ordinal);
        Assert.EndsWith("installed", state.LastInstallText, StringComparison.Ordinal);
        Assert.EndsWith("install-20261012T164027.log", state.LastInstallLogPath, StringComparison.Ordinal);
        Assert.Equal("1.0.0", state.PreviousVersion);
        Assert.True(state.Rollback.IsAvailable);
    }

    [Fact]
    public void No_config_changes_note_needs_the_log_to_say_so_and_to_be_newer_than_the_migration()
    {
        _ = _temp.WriteFile("migration-v9.json", "{\"from_version\":8,\"to_version\":9,\"migrated_at\":\"2026-10-12T18:58:44Z\",\"moved\":[]}");

        // The run that migrated has no "Configuration is current" line.
        _ = Log("install-20261012T115550.log", Transcript("20261012115550", "-Version 1.0.0", "  + Installed DefenseClaw 1.0.0"));
        Assert.DoesNotContain("no config changes", InstallStateSnapshot.Read(_temp.Path, [], Now).MigrationText, StringComparison.Ordinal);

        // A later one that found the config current says so; one that started before the migration does not claim it.
        var later = DateTimeOffset.Parse("2026-10-12T18:58:44Z", CultureInfo.InvariantCulture).ToLocalTime().AddHours(1).LocalDateTime;
        var path = Log($"install-{later:yyyyMMdd'T'HHmmss}.log", Transcript($"{later:yyyyMMddHHmmss}", "-Version 1.0.1", "  OK Configuration is current (config_version 9).", "  + Installed DefenseClaw 1.0.1"));
        Assert.Contains("Last upgrade made no config changes.", InstallStateSnapshot.Read(_temp.Path, [], Now).MigrationText, StringComparison.Ordinal);

        File.Delete(path);
        var earlier = later.AddHours(-3);
        _ = Log($"install-{earlier:yyyyMMdd'T'HHmmss}.log", Transcript($"{earlier:yyyyMMddHHmmss}", "-Version 1.0.1", "  OK Configuration is current (config_version 9)."));
        Assert.DoesNotContain("no config changes", InstallStateSnapshot.Read(_temp.Path, [], Now).MigrationText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_data_directory_reads_as_unknowns_with_no_rollback_target()
    {
        var state = InstallStateSnapshot.Read(_temp.Path, [], Now);

        Assert.StartsWith("Config version unknown", state.ConfigVersionText, StringComparison.Ordinal);
        Assert.Equal("No migration-v9.json was found.", state.MigrationText);
        Assert.StartsWith("No install run was found", state.LastInstallText, StringComparison.Ordinal);
        Assert.Null(state.LastInstallLogPath);
        Assert.Null(state.PreviousVersion);
        Assert.False(state.Rollback.IsAvailable);
        Assert.Equal("No previous install is kept.", state.Rollback.Note);
    }

    [Theory]
    [InlineData("config_version: 9", 9)]
    [InlineData("a: 1\nconfig_version: 8\n", 8)]
    [InlineData("  config_version: 9", null)]
    [InlineData("config_version: nine", null)]
    [InlineData("config_version: 99999999999", null)]
    [InlineData("", null)]
    public void The_config_version_is_read_from_the_top_level_line_only(string yaml, int? expected)
    {
        _ = _temp.WriteFile("config.yaml", yaml);

        Assert.Equal(expected, InstallFiles.ReadConfigVersion(_temp.Path));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v0.8.10\n", "0.8.10")]
    [InlineData("\uFEFF1.0.0-rc1", "1.0.0-rc1")]
    [InlineData("", null)]
    [InlineData("garbage here", null)]
    [InlineData("<script>", null)]
    public void The_previous_version_is_the_trimmed_text_of_previous_VERSION_or_null(string content, string? expected)
    {
        WritePrevious(content);

        Assert.Equal(expected, InstallFiles.ReadPreviousVersion(_temp.Path));
    }
}
