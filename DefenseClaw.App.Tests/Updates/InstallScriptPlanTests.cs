using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-346, the parts that need no window and no process: which channel a release is upgraded through, the exact argv, the pre-flight rows, what
/// each exit code means, which log belongs to a run, and what migration-v9.json is summarised as. Synthetic data throughout.
/// </summary>
public sealed class InstallScriptPlanTests : IDisposable
{
    private const string Staged = @"C:\Users\synthetic\AppData\Local\DefenseClaw.App\upgrades\1.0.0\install.ps1";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------ channel selection

    [Theory]
    [InlineData("0.8.10", UpgradeChannel.SetupInstaller)]
    [InlineData("v0.8.10", UpgradeChannel.SetupInstaller)]
    [InlineData("0.9.9", UpgradeChannel.SetupInstaller)]
    [InlineData("1.0.0", UpgradeChannel.InstallScript)]
    [InlineData("v1.0.0", UpgradeChannel.InstallScript)]
    [InlineData("1.0.1", UpgradeChannel.InstallScript)]
    [InlineData("2.3.4", UpgradeChannel.InstallScript)]
    public void A_release_at_1_0_0_or_later_is_upgraded_with_install_ps1_whatever_the_layout_says(string version, UpgradeChannel expected)
    {
        var assets = new[] { "DefenseClawSetup-x64.exe", "checksums.txt" };

        Assert.Equal(expected, UpgradeRunner.SelectChannel(version, assets, UpgradeChannel.SetupInstaller));
    }

    [Fact]
    public void A_release_with_no_setup_exe_but_an_install_script_is_upgraded_with_install_ps1_even_below_1_0_0()
    {
        var channel = UpgradeRunner.SelectChannel("0.9.0", new[] { "install.ps1", "checksums.txt" }, UpgradeChannel.SetupInstaller);

        Assert.Equal(UpgradeChannel.InstallScript, channel);
    }

    [Fact]
    public void A_release_that_ships_the_setup_exe_keeps_the_layout_default_and_an_unknown_asset_list_decides_nothing()
    {
        Assert.Equal(
            UpgradeChannel.SetupInstaller,
            UpgradeRunner.SelectChannel("0.8.10", new[] { "DefenseClawSetup-x64.exe", "install.ps1" }, UpgradeChannel.SetupInstaller));
        Assert.Equal(
            UpgradeChannel.ResolverScript,
            UpgradeRunner.SelectChannel("0.8.10", new[] { "DefenseClawSetup-x64.exe" }, UpgradeChannel.ResolverScript));
        Assert.Equal(UpgradeChannel.SetupInstaller, UpgradeRunner.SelectChannel("0.8.10", null, UpgradeChannel.SetupInstaller));
        Assert.Equal(UpgradeChannel.SetupInstaller, UpgradeRunner.SelectChannel("0.8.10", Array.Empty<string>(), UpgradeChannel.SetupInstaller));
        Assert.Equal(UpgradeChannel.SetupInstaller, UpgradeRunner.SelectChannel(null, null, UpgradeChannel.SetupInstaller));
        Assert.Equal(UpgradeChannel.SetupInstaller, UpgradeRunner.SelectChannel("not a version", null, UpgradeChannel.SetupInstaller));
    }

    // ------------------------------------------------------------------ the argv

    [Fact]
    public void The_argv_is_exactly_the_reviewed_command_and_has_no_yes_by_default()
    {
        var argv = InstallScriptPlan.BuildArgv(Staged, "1.0.0");

        Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Staged, "-Version", "1.0.0" }, argv);
        Assert.DoesNotContain("-Yes", argv);
    }

    [Fact]
    public void Yes_is_added_only_when_asked_for_and_a_v_prefix_is_dropped()
    {
        var argv = InstallScriptPlan.BuildArgv(Staged, "v1.0.0", yes: true);

        Assert.Equal("-Version", argv[^3]);
        Assert.Equal("1.0.0", argv[^2]);
        Assert.Equal("-Yes", argv[^1]);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0-rc1")]
    [InlineData("1.0.0.1")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0; calc")]
    [InlineData("1.0.0 -Yes")]
    [InlineData("..\\1.0.0")]
    [InlineData("1.0.0\n")]
    [InlineData("")]
    [InlineData("latest")]
    public void A_version_install_ps1_would_not_accept_never_reaches_an_argv(string version)
    {
        Assert.False(InstallScriptPlan.TryNormalizeVersion(version, out _));
        Assert.Throws<ArgumentException>(() => InstallScriptPlan.BuildArgv(Staged, version));
    }

    [Fact]
    public void The_rollback_argv_carries_rollback_and_no_version()
    {
        var argv = InstallScriptPlan.BuildRollbackArgv(Staged);
        var withYes = InstallScriptPlan.BuildRollbackArgv(Staged, yes: true);

        Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Staged, "-Rollback" }, argv);
        Assert.DoesNotContain("-Version", argv);
        Assert.Equal("-Yes", withYes[^1]);
    }

    [Fact]
    public void A_staged_path_with_spaces_is_one_argument_on_the_command_line_the_child_is_started_with()
    {
        var path = @"C:\Users\A Person\AppData\Local\DefenseClaw.App\upgrades\1.0.0\install.ps1";

        var line = WindowsCommandLine.Build(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", InstallScriptPlan.BuildArgv(path, "1.0.0"));

        Assert.Equal(
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + path + "\" -Version 1.0.0",
            line);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    public void Command_line_quoting_follows_the_rules_a_child_reads_back(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));
    }

    // ------------------------------------------------------------------ pre-flight

    private sealed class Probes : IUpgradeProbes
    {
        public List<string> Processes { get; } = new();

        public long? Free { get; set; } = 100L * 1024 * 1024 * 1024;

        public long Databases { get; set; } = 50L * 1024 * 1024;

        public IReadOnlyList<string> RunningProcessNames() => Processes;

        public long? FreeDiskBytes(string path) => Free;

        public long DatabaseBytes(string dataDirectory) => Databases;
    }

    private static PreflightRow Row(PreflightReport report, string id) => report.Rows.Single(r => r.Id == id);

    private PreflightReport Evaluate(Probes probes, string? blocked = null, PreflightVerification verification = PreflightVerification.Verified) =>
        InstallScriptPreflight.Evaluate(probes, blocked, _temp.Path, verification, "detail");

    [Fact]
    public void A_quiet_machine_with_a_verified_script_passes_every_row()
    {
        var report = Evaluate(new Probes());

        Assert.Equal(
            new[] { "installation", "codex", "claude-code", "processes", "pending-writes", "disk", "verification" },
            report.Rows.Select(r => r.Id).ToArray());
        Assert.All(report.Rows, r => Assert.Equal(PreflightStatus.Pass, r.Status));
        Assert.False(report.HasBlock);
        Assert.False(report.HasWarning);
    }

    [Fact]
    public void A_managed_or_read_only_installation_blocks_with_the_installations_own_reason()
    {
        var report = Evaluate(new Probes(), blocked: TestInstallations.ManagedReason);

        Assert.Equal(PreflightStatus.Block, Row(report, "installation").Status);
        Assert.Equal(TestInstallations.ManagedReason, Row(report, "installation").Reason);
        Assert.True(report.HasBlock);
        Assert.Equal(TestInstallations.ManagedReason, report.BlockReason);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("Codex")]
    public void A_running_codex_warns_to_quit_it_first(string process)
    {
        var probes = new Probes();
        probes.Processes.Add(process);

        var row = Row(Evaluate(probes), "codex");

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains("Quit it first", row.Reason, StringComparison.Ordinal);
        Assert.Contains("hook", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_always_on_codex_sandbox_service_alone_leaves_the_codex_row_passing()
    {
        var probes = new Probes();
        probes.Processes.Add("codex-windows-sandbox-service");

        Assert.Equal(PreflightStatus.Pass, Row(Evaluate(probes), "codex").Status);
    }

    [Fact]
    public void A_running_claude_exe_warns_to_quit_it_first_and_nothing_else_is_touched()
    {
        var probes = new Probes();
        probes.Processes.AddRange(new[] { "claude", "notepad" });

        var report = Evaluate(probes);

        Assert.Equal(PreflightStatus.Warn, Row(report, "claude-code").Status);
        Assert.Contains("Quit it first", Row(report, "claude-code").Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightStatus.Pass, Row(report, "codex").Status);
        Assert.False(report.HasBlock);
    }

    [Fact]
    public void Other_defenseclaw_programs_are_listed_and_are_fine_because_the_installer_stops_the_gateway()
    {
        var probes = new Probes();
        probes.Processes.AddRange(new[] { "defenseclaw-gateway", "DefenseClaw.App", "defenseclaw-gateway" });

        var row = Row(Evaluate(probes), "processes");

        Assert.Equal(PreflightStatus.Pass, row.Status);
        Assert.Contains("defenseclaw-gateway x2", row.Reason, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw.App", row.Reason, StringComparison.Ordinal);
        Assert.Contains("stops the gateway", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Less_than_twice_the_size_of_the_databases_in_free_space_blocks()
    {
        var probes = new Probes { Databases = 600L * 1024 * 1024, Free = 1000L * 1024 * 1024 };

        var row = Row(Evaluate(probes), "disk");

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains("2x", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_twice_the_size_passes_and_an_unreadable_drive_only_warns()
    {
        var exact = new Probes { Databases = 100, Free = 200 };
        var unknown = new Probes { Free = null };

        Assert.Equal(PreflightStatus.Pass, Row(Evaluate(exact), "disk").Status);
        Assert.Equal(PreflightStatus.Warn, Row(Evaluate(unknown), "disk").Status);
    }

    [Theory]
    [InlineData(PreflightVerification.Verified, PreflightStatus.Pass)]
    [InlineData(PreflightVerification.HashOnly, PreflightStatus.Warn)]
    [InlineData(PreflightVerification.NotStarted, PreflightStatus.Warn)]
    [InlineData(PreflightVerification.Failed, PreflightStatus.Block)]
    public void The_verification_row_follows_where_the_staged_script_stands(PreflightVerification verification, PreflightStatus expected)
    {
        Assert.Equal(expected, Row(Evaluate(new Probes(), verification: verification), "verification").Status);
    }

    // ------------------------------------------------------------------ exit codes

    [Fact]
    public void Exit_0_is_a_success_and_offers_a_rollback()
    {
        var info = InstallScriptPlan.MapExit(0);

        Assert.Equal(InstallOutcome.Success, info.Outcome);
        Assert.Equal("Ok", info.ToneKey);
        Assert.True(info.OffersRollback);
    }

    [Fact]
    public void Exit_2_is_a_usage_error_that_changed_nothing_and_offers_no_rollback()
    {
        var info = InstallScriptPlan.MapExit(2);

        Assert.Equal(InstallOutcome.UsageError, info.Outcome);
        Assert.False(info.ChangedMachine);
        Assert.False(info.OffersRollback);
        Assert.Contains("Nothing was changed", info.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(-1073741510)]
    public void Any_other_exit_is_a_failure_that_points_at_the_log_and_offers_a_rollback(int code)
    {
        var info = InstallScriptPlan.MapExit(code);

        Assert.True(info.Outcome is InstallOutcome.Failed or InstallOutcome.FailedRestored);
        Assert.Equal("Bad", info.ToneKey);
        Assert.True(info.OffersRollback);
        Assert.Contains("install log", info.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Exits_3_and_4_mean_installed_with_a_follow_up_not_failed(int code)
    {
        var info = InstallScriptPlan.MapExit(code);

        Assert.Equal(InstallOutcome.InstalledNeedsAttention, info.Outcome);
        Assert.Equal("Warn", info.ToneKey);
    }

    [Fact]
    public void An_unreadable_exit_code_is_unknown_not_a_success()
    {
        Assert.Equal(InstallOutcome.Unknown, InstallScriptPlan.MapExit(null).Outcome);
    }

    // ------------------------------------------------------------------ the log

    [Fact]
    public void The_newest_install_log_written_after_the_start_is_the_runs_log()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var started = DateTimeOffset.UtcNow;

        var old = Path.Combine(logs, "install-20200101T000000.log");
        var first = Path.Combine(logs, "install-20261012T101000.log");
        var second = Path.Combine(logs, "install-20261012T101500.log");
        var other = Path.Combine(logs, "gateway.log");
        foreach (var file in new[] { old, first, second, other })
        {
            File.WriteAllText(file, "synthetic");
        }

        File.SetLastWriteTimeUtc(old, started.UtcDateTime - TimeSpan.FromHours(3));
        File.SetLastWriteTimeUtc(first, started.UtcDateTime + TimeSpan.FromSeconds(5));
        File.SetLastWriteTimeUtc(second, started.UtcDateTime + TimeSpan.FromSeconds(30));
        File.SetLastWriteTimeUtc(other, started.UtcDateTime + TimeSpan.FromMinutes(5));

        Assert.Equal(second, InstallScriptPlan.FindInstallLog(_temp.Path, started));
    }

    [Fact]
    public void A_log_older_than_the_start_and_a_missing_folder_find_nothing()
    {
        Assert.Null(InstallScriptPlan.FindInstallLog(_temp.Path, DateTimeOffset.UtcNow));

        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var old = Path.Combine(logs, "install-20200101T000000.log");
        File.WriteAllText(old, "synthetic");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromHours(1));

        Assert.Null(InstallScriptPlan.FindInstallLog(_temp.Path, DateTimeOffset.UtcNow));
    }

    // ------------------------------------------------------------------ migration-v9.json and rollback

    [Fact]
    public void The_migration_summary_counts_moved_values_and_conflicts_from_a_synthetic_file()
    {
        _ = _temp.WriteFile(
            "migration-v9.json",
            "{\"schema_version\":1,\"from_version\":8,\"to_version\":9,\"migrated_at\":\"2026-10-12T10:00:00Z\"," +
            "\"moved\":[{\"source\":\"s\",\"from\":\"a.b\",\"to\":\"c.d\",\"value\":\"v1\"},{\"source\":\"s\",\"from\":\"e\",\"to\":\"f\",\"value\":\"v2\"},{\"source\":\"s\",\"from\":\"g\",\"to\":\"h\",\"value\":\"v3\"}]," +
            "\"conflicts\":[{\"key\":\"x\"}],\"removed\":[\"old.key\",\"older.key\"],\"actions_rows_moved\":7,\"notes\":[\"synthetic note\"]}");

        var summary = InstallScriptPlan.ReadMigrationSummary(_temp.Path);

        Assert.NotNull(summary);
        Assert.Equal(3, summary!.Moved);
        Assert.Equal(1, summary.Conflicts);
        Assert.Equal(2, summary.Removed);
        Assert.Equal(7, summary.ActionRowsMoved);
        Assert.Contains("3 values moved", summary.Text, StringComparison.Ordinal);
        Assert.Contains("1 conflict", summary.Text, StringComparison.Ordinal);
        Assert.Contains("2 removed", summary.Text, StringComparison.Ordinal);
        Assert.Contains("7 action rows moved", summary.Text, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_temp.Path, "migration-v9.json"), summary.FilePath);
    }

    [Fact]
    public void A_migration_file_of_an_unknown_shape_is_described_by_its_field_count_and_a_broken_or_absent_one_does_not_throw()
    {
        Assert.Null(InstallScriptPlan.ReadMigrationSummary(_temp.Path));

        _ = _temp.WriteFile("migration-v9.json", "{\"alpha\":1,\"beta\":2}");
        var unknown = InstallScriptPlan.ReadMigrationSummary(_temp.Path);
        Assert.NotNull(unknown);
        Assert.Null(unknown!.Moved);
        Assert.Contains("2 top-level field(s)", unknown.Text, StringComparison.Ordinal);

        _ = _temp.WriteFile("migration-v9.json", "{ not json");
        var broken = InstallScriptPlan.ReadMigrationSummary(_temp.Path);
        Assert.NotNull(broken);
        Assert.Contains("could not be read", broken!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_needs_a_previous_install_and_the_installer_refuses_a_setup_install_it_cannot_restore()
    {
        Assert.False(InstallScriptPlan.CheckRollback(_temp.Path).IsAvailable);

        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous"));
        _ = _temp.WriteFile(Path.Combine("previous", "VERSION"), "0.8.10");
        Assert.True(InstallScriptPlan.CheckRollback(_temp.Path).IsAvailable);

        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous", "legacy-setup"));
        var setup = InstallScriptPlan.CheckRollback(_temp.Path);
        Assert.False(setup.IsAvailable);
        Assert.Contains("Setup", setup.Note, StringComparison.Ordinal);
        Assert.Contains("defenseclaw uninstall", setup.Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what the upgrade leaves behind

    private sealed class Registry : ILeftoverProbes
    {
        public string? Run { get; set; }

        public UninstallEntry? Entry { get; set; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RunValue(string name) => name == PostUpgradeLeftovers.RunValueName ? Run : null;

        public UninstallEntry? FindUninstallEntry(string keyName) => keyName == PostUpgradeLeftovers.UninstallKeyName ? Entry : null;

        public bool FileExists(string path) => Files.Contains(path);
    }

    private const string RemovedStartup = @"C:\Users\synthetic\AppData\Local\Programs\DefenseClaw\bin\defenseclaw-startup.exe";

    [Theory]
    [InlineData(RemovedStartup)]
    [InlineData("\"" + RemovedStartup + "\" --minimized")]
    public void A_sign_in_entry_that_points_to_the_removed_startup_program_warns_and_points_at_the_apps_autostart_setting(string value)
    {
        var rows = PostUpgradeLeftovers.Evaluate(new Registry { Run = value });

        var row = Assert.Single(rows);
        Assert.Equal(PostUpgradeLeftovers.AutostartId, row.Id);
        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains(RemovedStartup, row.Reason, StringComparison.Ordinal);
        Assert.Contains("will not start when you sign in", row.Reason, StringComparison.Ordinal);
        Assert.Contains("auto-start setting", row.Reason, StringComparison.Ordinal);
        Assert.Contains("does not change", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sign_in_entry_whose_program_still_exists_or_no_entry_at_all_is_not_a_leftover()
    {
        var present = new Registry { Run = RemovedStartup };
        _ = present.Files.Add(RemovedStartup);

        Assert.Empty(PostUpgradeLeftovers.Evaluate(present));
        Assert.Empty(PostUpgradeLeftovers.Evaluate(new Registry()));
    }

    [Fact]
    public void The_setups_add_remove_programs_entry_warns_not_to_uninstall_it_from_windows_settings()
    {
        var rows = PostUpgradeLeftovers.Evaluate(new Registry
        {
            Entry = new UninstallEntry(
                "DefenseClaw (Unsigned / Unverified Build) 0.8.9",
                @"""C:\Users\synthetic\AppData\Local\Package Cache\{synthetic}\DefenseClawSetup-x64.exe"" /uninstall"),
        });

        var row = Assert.Single(rows);
        Assert.Equal(PostUpgradeLeftovers.UninstallEntryId, row.Id);
        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains("0.8.9", row.Reason, StringComparison.Ordinal);
        Assert.Contains("DefenseClawSetup-x64.exe", row.Reason, StringComparison.Ordinal);
        Assert.Contains("Do not uninstall it from Windows Settings", row.Reason, StringComparison.Ordinal);
        Assert.Contains("defenseclaw uninstall", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_leftovers_together_give_two_warning_rows_and_the_probes_are_only_read()
    {
        var rows = PostUpgradeLeftovers.Evaluate(new Registry { Run = RemovedStartup, Entry = new UninstallEntry("DefenseClaw 0.8.9", null) });

        Assert.Equal(new[] { PostUpgradeLeftovers.AutostartId, PostUpgradeLeftovers.UninstallEntryId }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.Equal(PreflightStatus.Warn, r.Status));
    }

    [Fact]
    public void The_what_changes_list_names_every_effect_the_issue_lists()
    {
        var text = string.Join("\n", InstallScriptPlan.WhatChanges);

        foreach (var needle in new[] { "audit history", "previous\\data", "config.yaml.v8.bak", "migration-v9.json", "legacy-setup", ".local\\bin", "Rollback" })
        {
            Assert.Contains(needle, text, StringComparison.Ordinal);
        }
    }

}
