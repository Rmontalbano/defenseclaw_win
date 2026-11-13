using System.Text.Json;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-452, install and upgrade half for DefenseClaw 1.0.9: the connector-recovery hint and pre-flight row by installed runtime (G9I-01), the uv cache
/// refusal and its pre-flight row (G9I-02), the Setup-beside-1.0 leftover row (G9I-04) and the agent-session warning from 0.8.x (G9I-05). Synthetic data throughout.
/// </summary>
public sealed class ConnectorRecoveryUpgradeTests : IDisposable
{
    private const string Id = "0123456789abcdef01234567";
    private const string FakeData = @"C:\Users\operator\.defenseclaw";

    // install.ps1@1.0.9:915-918, with synthetic roots.
    private const string Refusal109 =
        @"Version change to 1.0.9 refused: active connector recovery remains in C:\Users\operator\.defenseclaw. " +
        "Finish it with the current DefenseClaw version (the command names any agent config change it cannot finish), then retry. " +
        "Do not remove or replay its receipts. Command: & 'C:\\Users\\operator\\.local\\bin\\defenseclaw-gateway.exe' connector recover-config --data-dir 'C:\\Users\\operator\\.defenseclaw'";

    private const string Refusal108 =
        @"Version change to 1.0.8 refused: active connector recovery remains in C:\Users\operator\.defenseclaw. " +
        "Resolve it with the current DefenseClaw version before retrying; do not remove or replay its receipts.";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class Probes : IUpgradeProbes
    {
        public string? UvEnv { get; set; }

        public bool? LongPaths { get; set; } = false;

        public bool HasPending { get; set; }

        public bool ReceiptsOnly { get; set; }

        public IReadOnlyList<string> RunningProcessNames() => [];

        public long? FreeDiskBytes(string path) => 100L * 1024 * 1024 * 1024;

        public long DatabaseBytes(string dataDirectory) => 0;

        public string? UvCacheDirEnvironment() => UvEnv;

        public bool? LongPathsEnabled() => LongPaths;

        public IReadOnlyList<CasPendingWrite> PendingConfigWrites(string dataDirectory) =>
            HasPending ? CasReceipts.FindPending(dataDirectory, DateTimeOffset.UtcNow, TimeSpan.Zero) : [];

        public IReadOnlyList<string> RecoveryReceiptNames(string dataDirectory) =>
            ReceiptsOnly ? [".defenseclaw-cas-v2-" + Id + ".done"] : HasPending ? [".defenseclaw-cas-v2-" + Id + ".intent"] : [];
    }

    private PreflightReport Evaluate(Probes probes, string? target, string? installed, bool recoverConfig = false, string? dataDirectory = null) =>
        InstallScriptPreflight.Evaluate(probes, null, dataDirectory ?? _temp.Path, PreflightVerification.Verified, "detail", target, installed, recoverConfig);

    private void WriteReceipt()
    {
        var target = @"C:\synthetic\home\.codex\config.toml";
        File.WriteAllText(_temp.File($".defenseclaw-cas-v2-{Id}.intent"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["phase"] = "staging",
            ["logical_path"] = target,
            ["input_path"] = target,
            ["target_path"] = target,
            ["stage"] = new Dictionary<string, object?> { ["name"] = "config.toml" },
            ["old"] = new Dictionary<string, object?> { ["name"] = "config.toml" },
        }));
    }

    // ------------------------------------------------------------------ G9I-01: the hint

    [Fact]
    public void On_1_0_8_the_hint_is_today_s_text_for_both_refusal_lines_without_the_command()
    {
        Assert.Equal(InstallScriptPlan.ConnectorRecoveryHint, InstallScriptPlan.HintFor(Refusal108));
        Assert.Equal(InstallScriptPlan.ConnectorRecoveryHint, InstallScriptPlan.HintFor(Refusal108, recoverConfigInstalled: false));
        Assert.Equal("Open Overview and use \"Clear the stuck pending write\", then run the upgrade again.", InstallScriptPlan.ConnectorRecoveryHint);
    }

    [Theory]
    [InlineData(Refusal109)]
    [InlineData(@"Version change to 1.0.9 refused: active connector recovery remains in ~\.defenseclaw. Finish it with the current DefenseClaw version (the command names any agent config change it cannot finish), then retry. Do not remove or replay its receipts. Command: & '~\.local\bin\defenseclaw-gateway.exe' connector re…")]
    public void On_1_0_8_with_the_1_0_9_installer_s_line_the_move_aside_stays_and_the_command_is_said_to_need_1_0_9(string line)
    {
        var hint = InstallScriptPlan.HintFor(line, recoverConfigInstalled: false)!;

        Assert.StartsWith(InstallScriptPlan.ConnectorRecoveryHint, hint, StringComparison.Ordinal);
        Assert.Contains("needs DefenseClaw 1.0.9", hint, StringComparison.Ordinal);
        Assert.Contains("does not have it", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Refusal109)]
    [InlineData(Refusal108)]
    public void On_1_0_9_the_hint_says_to_run_the_recovery_and_never_to_clear_the_write(string line)
    {
        var hint = InstallScriptPlan.HintFor(line, recoverConfigInstalled: true)!;

        Assert.Contains("Finish the interrupted change", hint, StringComparison.Ordinal);
        Assert.Contains("connector recover-config", hint, StringComparison.Ordinal);
        Assert.Contains("run the upgrade again", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear the stuck pending write", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_hints_do_not_depend_on_the_installed_runtime()
    {
        Assert.Equal(InstallScriptPlan.AcceptCurrentInstallHint, InstallScriptPlan.HintFor("retry with -AcceptCurrentInstall", true));
        Assert.Null(InstallScriptPlan.HintFor("something else failed", true));
        Assert.Null(InstallScriptPlan.HintFor(null, true));
    }

    // ------------------------------------------------------------------ G9I-01: the pre-flight row

    [Fact]
    public void The_pending_writes_row_is_unchanged_for_an_installed_1_0_8()
    {
        WriteReceipt();

        var row = Evaluate(new Probes { HasPending = true }, "1.0.9", "1.0.8").Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.EndsWith("DefenseClaw's installer refuses to change version while they remain. Open Overview and use \"Clear the stuck pending write\", then check again.", row.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightActionIds.OpenOverview, row.ActionId);
    }

    [Fact]
    public void On_1_0_9_the_pending_writes_row_sends_the_user_to_finish_the_change()
    {
        WriteReceipt();

        var row = Evaluate(new Probes { HasPending = true }, "1.0.9", "1.0.9", recoverConfig: true).Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains("Open Overview and use \"Finish the interrupted change\"", row.Reason, StringComparison.Ordinal);
        Assert.Contains("Do not remove or move the receipts yourself", row.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear the stuck pending write", row.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightActionIds.OpenOverview, row.ActionId);
    }

    [Fact]
    public void Recovery_files_that_are_not_a_pending_write_name_the_command_on_1_0_9_and_keep_today_s_words_before()
    {
        var older = Evaluate(new Probes { ReceiptsOnly = true }, "1.0.9", "1.0.8").Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);
        var newer = Evaluate(new Probes { ReceiptsOnly = true }, "1.0.9", "1.0.9", recoverConfig: true).Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);

        Assert.EndsWith("and they are not a pending write this app can clear.", older.Reason, StringComparison.Ordinal);
        Assert.Contains("defenseclaw-gateway connector recover-config --data-dir '" + _temp.Path + "'", newer.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("can clear", newer.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightStatus.Block, newer.Status);
    }

    // ------------------------------------------------------------------ G9I-02: the uv cache refusal

    private static readonly string DeepProfile = @"C:\Users\synthetic-operator-with-a-very-long-profile-name-abcdefghijklmnopqrstuvwxyz-0123456789-0123456789";
    private static readonly string DeepCache = DeepProfile + @"\.defenseclaw\.uv\cache";

    private static string UvLine(string cache) =>
        $"The uv cache folder {cache} is {cache.Length} characters long. uv builds one package (win-unicode-console) from source in it and needs 151 more, " +
        "which is past the Windows limit of 260 characters, so the install would stop with WinError 206 (the filename or extension is too long). Nothing was changed. " +
        "Pick a short folder that only your account can write to, for example C:\\dcuv, and run the installer again with it: $env:UV_CACHE_DIR = 'C:\\dcuv'. " +
        "Windows long path support (LongPathsEnabled, set by an administrator) lifts the limit as well. Remove the folder yourself when you no longer need it";

    private static string Transcript(params string[] lines) =>
        string.Join("\r\n", new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261112090000",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            "Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\staging\\1.0.9\\install.ps1 -Version 1.0.9 -Yes",
            "**********************",
            "--- Preparing DefenseClaw 1.0.9 (windows/amd64)  [09:00:01Z]",
            "  > Installed: 1.0.8",
        }.Concat(lines).Append("**********************").Append(string.Empty));

    [Fact]
    public void The_uv_line_is_a_refusal_that_changed_nothing_with_the_whole_fix_and_a_consistent_length()
    {
        var line = UvLine(DeepCache);
        Assert.True(line.Length > InstallScriptPlan.MaxFailureReasonChars);

        var facts = InstallTranscript.Parse(Transcript("  x " + line));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.UvCacheTooDeep, refusal.Kind);
        Assert.Equal(line, refusal.Reason);
        Assert.Equal(line, facts.Reason);
        Assert.Contains("$env:UV_CACHE_DIR = 'C:\\dcuv'", facts.Reason, StringComparison.Ordinal);
        Assert.Contains("LongPathsEnabled", facts.Reason, StringComparison.Ordinal);

        // The path keeps its profile folder, so the number in "is N characters long" is the length of the path that is shown.
        Assert.Contains(DeepProfile, facts.Reason, StringComparison.Ordinal);
        Assert.Contains($"is {DeepCache.Length} characters long", facts.Reason, StringComparison.Ordinal);
        Assert.Contains("UV_CACHE_DIR", refusal.NextStep, StringComparison.Ordinal);
        Assert.Contains("restart this app", refusal.NextStep, StringComparison.Ordinal);
        Assert.Contains("long path", refusal.NextStep, StringComparison.Ordinal);

        var exit = InstallScriptPlan.MapExit(1, false, facts);
        Assert.Equal(InstallOutcome.Refused, exit.Outcome);
        Assert.False(exit.ChangedMachine);
        Assert.Contains("Nothing was changed", exit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_uv_line_is_recognised_when_the_console_wrapped_it_and_only_on_an_x_line()
    {
        var line = UvLine(DeepCache);
        var wrapped = string.Join("\r\n", Enumerable.Range(0, (line.Length + 99) / 100).Select(i => line.Substring(i * 100, Math.Min(100, line.Length - i * 100))));

        var facts = InstallTranscript.Parse(Transcript("  x " + wrapped));

        Assert.Equal(InstallerRefusalKind.UvCacheTooDeep, facts.Refusal?.Kind);
        Assert.Null(InstallTranscript.Parse(Transcript("  > " + line)).Refusal);
    }

    [Fact]
    public void A_run_that_restored_something_is_not_called_a_uv_refusal()
    {
        var facts = InstallTranscript.Parse(Transcript("  x " + UvLine(DeepCache), "DefenseClaw 1.0.9 was not installed. Your previous install is back."));

        Assert.Null(facts.Refusal);
    }

    [Theory]
    [InlineData("1.0.9", null, false, true)]
    [InlineData("1.0.9", null, true, false)]
    [InlineData("1.0.9", null, null, true)]
    [InlineData("1.0.8", null, false, false)]
    [InlineData("1.0.7", null, false, false)]
    public void The_uv_row_fires_for_a_target_with_the_check_when_the_default_cache_is_deep_and_long_paths_are_off(string target, string? env, bool? longPaths, bool shown)
    {
        var deepData = DeepProfile + @"\.defenseclaw";
        var report = Evaluate(new Probes { UvEnv = env, LongPaths = longPaths }, target, "1.0.8", dataDirectory: deepData);

        Assert.Equal(shown, report.Rows.Any(r => r.Id == UvCacheDepth.RowId));
        Assert.False(report.HasBlock);
    }

    [Fact]
    public void The_uv_row_states_the_installer_s_arithmetic_and_says_a_new_variable_needs_a_restart()
    {
        var deepData = DeepProfile + @"\.defenseclaw";

        var row = Evaluate(new Probes(), "1.0.9", "1.0.8", dataDirectory: deepData).Rows.Single(r => r.Id == UvCacheDepth.RowId);

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains($"The uv cache folder {DeepCache} is {DeepCache.Length} characters long", row.Reason, StringComparison.Ordinal);
        Assert.Contains("needs 151 more", row.Reason, StringComparison.Ordinal);
        Assert.Contains("restart this app", row.Reason, StringComparison.Ordinal);
        Assert.Contains("not seen until you restart it", row.Reason, StringComparison.Ordinal);
        Assert.Contains("LongPathsEnabled", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_replaces_the_default_folder_and_is_made_absolute_with_the_trailing_backslash_trimmed()
    {
        var deepData = DeepProfile + @"\.defenseclaw";
        Assert.DoesNotContain(Evaluate(new Probes { UvEnv = @"C:\dcuv\" }, "1.0.9", "1.0.8", dataDirectory: deepData).Rows, r => r.Id == UvCacheDepth.RowId);

        var longEnv = @"C:\" + new string('a', 105);   // 3 + 105 = 108: 108 + 151 = 259 passes
        Assert.DoesNotContain(Evaluate(new Probes { UvEnv = longEnv }, "1.0.9", "1.0.8").Rows, r => r.Id == UvCacheDepth.RowId);
        Assert.Contains(Evaluate(new Probes { UvEnv = longEnv + "b" }, "1.0.9", "1.0.8").Rows, r => r.Id == UvCacheDepth.RowId);
        Assert.Contains(Evaluate(new Probes { UvEnv = longEnv + "b\\" }, "1.0.9", "1.0.8").Rows, r => r.Id == UvCacheDepth.RowId);
        Assert.Equal(109, UvCacheDepth.CacheFolder(longEnv + "b\\", FakeData)!.Length);
    }

    // ------------------------------------------------------------------ G9I-04: Setup beside 1.0

    private sealed class Leftovers : ILeftoverProbes
    {
        public bool StatePresent { get; set; }

        public string? RunValue(string name) => null;

        public UninstallEntry? FindUninstallEntry(string keyName) => new("DefenseClaw 0.8.9", @"C:\synthetic\setup\unins000.exe");

        public bool FileExists(string path) => StatePresent && string.Equals(path, PostUpgradeLeftovers.SetupInstallStatePath(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_programs_row_keeps_today_s_text_for_an_older_runtime_or_when_install_state_is_gone(bool statePresent, bool runtime109)
    {
        var row = Assert.Single(PostUpgradeLeftovers.Evaluate(new Leftovers { StatePresent = statePresent }, false, runtime109));

        Assert.Contains("Do not uninstall it from Windows Settings on a 1.0 machine", row.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("beside 1.0", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void On_1_0_9_with_install_state_present_the_row_says_setup_is_still_there_and_what_uninstall_does()
    {
        var row = Assert.Single(PostUpgradeLeftovers.Evaluate(new Leftovers { StatePresent = true }, false, uninstallRemovesSetup: true));

        Assert.Equal(PostUpgradeLeftovers.UninstallEntryId, row.Id);
        Assert.Contains("DefenseClaw Setup 0.8.x is still installed beside 1.0", row.Reason, StringComparison.Ordinal);
        Assert.Contains("'defenseclaw uninstall' removes it (its data folder is kept)", row.Reason, StringComparison.Ordinal);
        Assert.Contains("stops this release's gateway and removes the hooks of the connectors it has configured, so run setup again afterwards", row.Reason, StringComparison.Ordinal);
        Assert.Contains("'defenseclaw init' will not set up hooks while Setup's hooks remain", row.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not uninstall it from Windows Settings", row.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ G9I-05: agent sessions from 0.8.x

    [Theory]
    [InlineData("0.8.10", "1.0.9", true)]
    [InlineData("0.8.9", "1.0.8", true)]
    [InlineData("1.0.8", "1.0.9", false)]
    [InlineData("1.0.2", "1.0.9", false)]
    [InlineData("1.0.9", "1.0.9", false)]
    [InlineData("0.8.10", "1.0.7", false)]
    public void A_0_8_source_gets_the_agent_session_warning_for_a_target_without_a_stated_range(string installed, string target, bool warns)
    {
        var row = UpgradeSourceRange.Row(target, installed);

        Assert.Equal(warns, row is not null);
        if (row is null)
        {
            return;
        }

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains("End every agent session before you upgrade and start new ones afterwards", row.Reason, StringComparison.Ordinal);
        if (target == "1.0.9")
        {
            Assert.DoesNotContain("support upgrades from", row.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("no longer tested", row.Reason, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ the owner's machine

    [Fact]
    public void Installed_1_0_9_with_nothing_pending_a_short_path_and_no_leftovers_adds_no_row()
    {
        var report = Evaluate(new Probes(), "1.0.9", "1.0.9", recoverConfig: true, dataDirectory: FakeData);

        Assert.DoesNotContain(report.Rows, r => r.Id is UvCacheDepth.RowId or "upgrade-source");
        var pending = report.Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);
        Assert.Equal(PreflightStatus.Pass, pending.Status);
        Assert.Equal("No pending config writes.", pending.Reason);
        Assert.Empty(PostUpgradeLeftovers.Evaluate(new NoLeftovers(), false, true));
    }

    private sealed class NoLeftovers : ILeftoverProbes
    {
        public string? RunValue(string name) => null;

        public UninstallEntry? FindUninstallEntry(string keyName) => null;

        public bool FileExists(string path) => true;
    }
}
