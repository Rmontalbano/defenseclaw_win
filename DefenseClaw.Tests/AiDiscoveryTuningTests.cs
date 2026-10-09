using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="AiDiscoveryTuning"/> and <see cref="AiDiscoverySettings"/> (CUST-271): the TUI's AI Discovery wizard as validation, a diff and an
/// argv over the flags of <c>defenseclaw agent discovery enable</c>. The flags and the ranges are held against the help screen the installed
/// 0.8.10 CLI prints (<c>Fixtures/runtime-0.8.10/agent-discovery-enable.txt</c>, captured with <c>--help</c>; nothing was run).
/// </summary>
public class AiDiscoveryTuningTests
{
    private static readonly string EnableHelp = Flat(FixtureFiles.ReadText("runtime-0.8.10/agent-discovery-enable.txt"));
    private static readonly string DisableHelp = Flat(FixtureFiles.ReadText("runtime-0.8.10/agent-discovery-disable.txt"));

    /// <summary>The screen as one run of words: Click wraps its help to the terminal, which can break a phrase over two lines.</summary>
    private static string Flat(string screen) => System.Text.RegularExpressions.Regex.Replace(screen, @"\s+", " ");

    private static readonly AiDiscoverySettings Off = AiDiscoverySettings.Defaults;

    private static readonly AiDiscoverySettings On = Off with { Enabled = true };

    // ---- the settings, seeded from config ----

    [Fact]
    public void A_config_that_says_nothing_is_the_runtimes_defaults_and_discovery_off()
    {
        foreach (var yaml in new[] { string.Empty, "gateway: {}\n", "ai_discovery:\n", "ai_discovery: {}\n" })
        {
            var settings = AiDiscoverySettings.FromYaml(yaml);

            Assert.Equal(AiDiscoverySettings.Defaults.Enabled, settings.Enabled);
            Assert.Equal("enhanced", settings.Mode);
            Assert.Equal(5, settings.ScanIntervalMin);
            Assert.Equal(60, settings.ProcessIntervalS);
            Assert.Equal(new[] { "~" }, settings.ScanRoots);
            Assert.Equal(1000, settings.MaxFilesPerScan);
            Assert.Equal(524288, settings.MaxFileBytes);
            Assert.True(settings.IncludeShellHistory && settings.IncludePackageManifests && settings.IncludeEnvVarNames && settings.IncludeNetworkDomains);
            Assert.False(settings.AllowWorkspaceSignatures || settings.StoreRawLocalPaths || settings.Enabled);
        }
    }

    [Fact]
    public void The_defaults_are_the_ones_the_installed_cli_prints_in_its_help()
    {
        Assert.Contains("Range 1..1440; default 5.", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("Range 5..3600; default 60.", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("(default 1000)", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("(default 524288)", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("[1<=x<=1440]", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("[5<=x<=3600]", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("[10<=x<=100000]", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("[4096<=x<=16777216]", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("--mode [passive|enhanced]", EnableHelp, StringComparison.Ordinal);

        Assert.Equal((1, 1440), AiDiscoveryTuning.ScanIntervalRange);
        Assert.Equal((5, 3600), AiDiscoveryTuning.ProcessIntervalRange);
        Assert.Equal((10, 100000), AiDiscoveryTuning.MaxFilesRange);
        Assert.Equal((4096L, 16777216L), AiDiscoveryTuning.MaxFileBytesRange);
        Assert.Equal(new[] { "passive", "enhanced" }, AiDiscoveryTuning.Modes);
    }

    [Fact]
    public void What_config_says_wins_over_each_default()
    {
        var settings = AiDiscoverySettings.FromYaml("""
            ai_discovery:
              enabled: yes
              mode: passive
              scan_interval_min: 15
              process_interval_s: 120
              scan_roots:
                - C:\Work
                - D:\Code
              max_files_per_scan: 250
              max_file_bytes: 65536
              include_shell_history: no
              include_package_manifests: off
              include_env_var_names: false
              include_network_domains: false
              allow_workspace_signatures: true
              store_raw_local_paths: on
            """);

        Assert.True(settings.Enabled);
        Assert.Equal("passive", settings.Mode);
        Assert.Equal(15, settings.ScanIntervalMin);
        Assert.Equal(120, settings.ProcessIntervalS);
        Assert.Equal(new[] { @"C:\Work", @"D:\Code" }, settings.ScanRoots);
        Assert.Equal(250, settings.MaxFilesPerScan);
        Assert.Equal(65536, settings.MaxFileBytes);
        Assert.False(settings.IncludeShellHistory || settings.IncludePackageManifests || settings.IncludeEnvVarNames || settings.IncludeNetworkDomains);
        Assert.True(settings.AllowWorkspaceSignatures && settings.StoreRawLocalPaths);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("")]
    [InlineData("PASSIVE-ish")]
    public void A_stored_mode_the_cli_would_refuse_reads_as_enhanced_like_the_tui_reads_it(string mode) =>
        Assert.Equal("enhanced", AiDiscoverySettings.FromYaml($"ai_discovery:\n  mode: \"{mode}\"\n").Mode);

    [Fact]
    public void A_mode_in_another_letter_case_is_the_mode()
    {
        Assert.Equal("passive", AiDiscoverySettings.FromYaml("ai_discovery:\n  mode: Passive\n").Mode);
    }

    [Fact]
    public void Scan_roots_written_as_one_comma_separated_string_are_read_as_the_cli_splits_them()
    {
        Assert.Equal(new[] { "~", @"C:\Work" }, AiDiscoverySettings.FromYaml("ai_discovery:\n  scan_roots: \"~, C:\\\\Work\"\n").ScanRoots);
    }

    [Fact]
    public void A_number_that_is_not_one_falls_back_to_the_default()
    {
        var settings = AiDiscoverySettings.FromYaml("ai_discovery:\n  scan_interval_min: soon\n  max_file_bytes: lots\n  max_files_per_scan: 99999999999\n");

        Assert.Equal(5, settings.ScanIntervalMin);
        Assert.Equal(524288, settings.MaxFileBytes);
        Assert.Equal(1000, settings.MaxFilesPerScan);
    }

    // ---- validation: what the CLI would refuse ----

    [Fact]
    public void The_defaults_have_no_problem()
    {
        Assert.Empty(AiDiscoveryTuning.Problems(Off));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    public void The_scan_interval_is_one_minute_to_a_day(int minutes, bool ok) =>
        Assert.Equal(ok, !AiDiscoveryTuning.Problems(Off with { ScanIntervalMin = minutes }).ContainsKey("scan_interval_min"));

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void The_process_poll_is_five_seconds_to_an_hour(int seconds, bool ok) =>
        Assert.Equal(ok, !AiDiscoveryTuning.Problems(Off with { ProcessIntervalS = seconds }).ContainsKey("process_interval_s"));

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(100000, true)]
    [InlineData(100001, false)]
    public void The_file_cap_is_ten_to_a_hundred_thousand(int files, bool ok) =>
        Assert.Equal(ok, !AiDiscoveryTuning.Problems(Off with { MaxFilesPerScan = files }).ContainsKey("max_files_per_scan"));

    [Theory]
    [InlineData(4095L, false)]
    [InlineData(4096L, true)]
    [InlineData(16777216L, true)]
    [InlineData(16777217L, false)]
    public void The_byte_cap_is_four_kib_to_sixteen_mib(long bytes, bool ok) =>
        Assert.Equal(ok, !AiDiscoveryTuning.Problems(Off with { MaxFileBytes = bytes }).ContainsKey("max_file_bytes"));

    [Fact]
    public void A_problem_names_the_range_in_a_sentence()
    {
        var problems = AiDiscoveryTuning.Problems(Off with { ScanIntervalMin = 0 });

        Assert.Equal("Scan interval must be a whole number from 1 through 1,440.", problems["scan_interval_min"]);
    }

    [Fact]
    public void A_mode_the_cli_does_not_list_is_a_problem()
    {
        Assert.True(AiDiscoveryTuning.Problems(Off with { Mode = "turbo" }).ContainsKey("mode"));
    }

    [Fact]
    public void Scan_roots_need_a_folder_and_no_comma_since_a_comma_would_split_it_in_two()
    {
        Assert.True(AiDiscoveryTuning.Problems(Off with { ScanRoots = Array.Empty<string>() }).ContainsKey("scan_roots"));
        Assert.Contains("\"C:\\a,b\"", AiDiscoveryTuning.Problems(Off with { ScanRoots = new[] { @"C:\a,b" } })["scan_roots"], StringComparison.Ordinal);
        Assert.True(AiDiscoveryTuning.Problems(Off with { ScanRoots = new[] { "C:\\a\u0007" } }).ContainsKey("scan_roots"));
        Assert.Empty(AiDiscoveryTuning.Problems(Off with { ScanRoots = new[] { "~", @"C:\Work" } }));
    }

    // ---- the diff ----

    [Fact]
    public void Identical_settings_have_no_changes_and_the_enabled_switch_is_never_one_of_them()
    {
        Assert.Empty(AiDiscoveryTuning.Diff(Off, Off));
        Assert.Empty(AiDiscoveryTuning.Diff(Off, On));
    }

    [Fact]
    public void A_cadence_change_is_one_change_with_its_flag_and_both_values()
    {
        var change = Assert.Single(AiDiscoveryTuning.Diff(On, On with { ScanIntervalMin = 10 }));

        Assert.Equal("scan_interval_min", change.Key);
        Assert.Equal("--scan-interval-min", change.Flag);
        Assert.Equal("5", change.Before);
        Assert.Equal("10", change.After);
        Assert.Equal(new[] { "--scan-interval-min", "10" }, change.Args);
        Assert.Equal("Scan interval (minutes): 5 to 10", change.Describe());
    }

    [Fact]
    public void Changes_come_in_the_order_the_tui_sends_its_flags()
    {
        var after = On with
        {
            StoreRawLocalPaths = true,
            IncludeShellHistory = false,
            MaxFileBytes = 4096,
            ScanRoots = new[] { @"C:\Work" },
            ProcessIntervalS = 30,
            Mode = "passive",
            ScanIntervalMin = 2,
            MaxFilesPerScan = 50,
            IncludeNetworkDomains = false,
            AllowWorkspaceSignatures = true,
            IncludeEnvVarNames = false,
            IncludePackageManifests = false,
        };

        var changes = AiDiscoveryTuning.Diff(On, after);

        Assert.Equal(AiDiscoveryTuning.Flags, changes.Select(c => c.Flag));
    }

    [Fact]
    public void A_switch_turned_off_sends_its_negative_form_and_one_turned_on_its_positive_form()
    {
        var changes = AiDiscoveryTuning.Diff(On, On with { IncludeShellHistory = false, StoreRawLocalPaths = true });

        Assert.Equal(new[] { "--no-include-shell-history" }, changes[0].Args);
        Assert.Equal(new[] { "--store-raw-local-paths" }, changes[1].Args);
        Assert.Equal("--include-shell-history", changes[0].Flag);
    }

    [Fact]
    public void Roots_compare_element_by_element_so_the_same_folders_are_no_change_and_a_new_order_is()
    {
        var two = On with { ScanRoots = new[] { "~", @"C:\Work" } };

        Assert.Empty(AiDiscoveryTuning.Diff(two, two with { ScanRoots = new[] { "~", @"C:\Work" } }));
        var change = Assert.Single(AiDiscoveryTuning.Diff(two, two with { ScanRoots = new[] { @"C:\Work", "~" } }));
        Assert.Equal(new[] { "--scan-roots", @"C:\Work,~" }, change.Args);
        Assert.Equal(@"~, C:\Work", change.Before);
    }

    // ---- the argv: a run only when something changes ----

    [Fact]
    public void A_cadence_change_on_a_running_discovery_is_enable_yes_and_that_one_flag()
    {
        var argv = AiDiscoveryTuning.Argv(On, On with { ScanIntervalMin = 10 }, new TuningRollout());

        Assert.Equal("agent discovery enable --yes --scan-interval-min 10", string.Join(' ', argv!));
    }

    [Fact]
    public void Nothing_to_apply_is_no_command_at_all_not_an_enable_that_prints_already_enabled()
    {
        Assert.Null(AiDiscoveryTuning.Argv(On, On, new TuningRollout()));
        Assert.Null(AiDiscoveryTuning.Argv(On, On, new TuningRollout(Restart: false)));
        Assert.Null(AiDiscoveryTuning.Argv(Off, Off, new TuningRollout()));
    }

    [Fact]
    public void Turning_it_on_is_enable_with_whatever_else_changed_and_turning_it_on_unchanged_is_a_bare_enable()
    {
        Assert.Equal(
            "agent discovery enable --yes",
            string.Join(' ', AiDiscoveryTuning.Argv(Off, On, new TuningRollout())!));
        Assert.Equal(
            "agent discovery enable --yes --mode passive --max-files-per-scan 200",
            string.Join(' ', AiDiscoveryTuning.Argv(Off, On with { Mode = "passive", MaxFilesPerScan = 200 }, new TuningRollout())!));
    }

    [Fact]
    public void Turning_it_off_is_disable_and_the_tuning_edits_made_meanwhile_are_not_sent()
    {
        Assert.Equal(
            "agent discovery disable --yes",
            string.Join(' ', AiDiscoveryTuning.Argv(On, Off with { ScanIntervalMin = 99 }, new TuningRollout())!));
        Assert.Equal(
            "agent discovery disable --yes --no-restart",
            string.Join(' ', AiDiscoveryTuning.Argv(On, Off, new TuningRollout(Restart: false))!));
    }

    [Fact]
    public void Off_to_off_is_nothing_whatever_else_was_edited_since_the_cli_cannot_write_a_setting_and_leave_it_off()
    {
        Assert.Null(AiDiscoveryTuning.Argv(Off, Off with { ScanIntervalMin = 99, Mode = "passive" }, new TuningRollout()));
    }

    [Fact]
    public void Without_a_restart_there_is_no_service_to_ask_so_no_scan_either_and_without_a_scan_only_that_is_off()
    {
        var after = On with { ProcessIntervalS = 30 };

        Assert.Equal(
            "agent discovery enable --yes --process-interval-s 30 --no-restart --no-scan",
            string.Join(' ', AiDiscoveryTuning.Argv(On, after, new TuningRollout(Restart: false))!));
        Assert.Equal(
            "agent discovery enable --yes --process-interval-s 30 --no-scan",
            string.Join(' ', AiDiscoveryTuning.Argv(On, after, new TuningRollout(Restart: true, ScanAfter: false))!));
        Assert.Equal(
            "agent discovery enable --yes --process-interval-s 30",
            string.Join(' ', AiDiscoveryTuning.Argv(On, after, new TuningRollout())!));
    }

    [Fact]
    public void Changes_the_caller_filtered_out_are_the_ones_left_off_the_command()
    {
        var after = On with { ScanIntervalMin = 10, ProcessIntervalS = 30 };
        var only = AiDiscoveryTuning.Diff(On, after).Where(c => c.Key == "process_interval_s").ToArray();

        Assert.Equal(
            "agent discovery enable --yes --process-interval-s 30",
            string.Join(' ', AiDiscoveryTuning.Argv(On, after, only, new TuningRollout())!));
    }

    // ---- against the CLI's own help ----

    [Fact]
    public void Every_flag_the_builder_can_send_is_one_the_installed_enable_command_lists()
    {
        foreach (var flag in AiDiscoveryTuning.Flags)
        {
            Assert.Contains(flag, EnableHelp, StringComparison.Ordinal);
            if (flag.StartsWith("--include-", StringComparison.Ordinal) || flag is "--allow-workspace-signatures" or "--store-raw-local-paths")
            {
                Assert.Contains(AiDiscoveryTuning.SwitchFlag(flag, on: false), EnableHelp, StringComparison.Ordinal);
            }
        }

        Assert.Contains("--yes", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("--restart / --no-restart", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("--scan / --no-scan", EnableHelp, StringComparison.Ordinal);
        Assert.Contains("--yes", DisableHelp, StringComparison.Ordinal);
        Assert.Contains("--restart / --no-restart", DisableHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_whole_form_changed_at_once_still_builds_one_command_every_part_of_which_is_listed_by_the_cli()
    {
        var everything = On with
        {
            Mode = "passive",
            ScanIntervalMin = 7,
            ProcessIntervalS = 45,
            ScanRoots = new[] { @"C:\Work", @"D:\Code" },
            MaxFilesPerScan = 300,
            MaxFileBytes = 8192,
            IncludeShellHistory = false,
            IncludePackageManifests = false,
            IncludeEnvVarNames = false,
            IncludeNetworkDomains = false,
            AllowWorkspaceSignatures = true,
            StoreRawLocalPaths = true,
        };

        var argv = AiDiscoveryTuning.Argv(On, everything, new TuningRollout())!;

        Assert.Equal(
            "agent discovery enable --yes --mode passive --scan-interval-min 7 --process-interval-s 45 --scan-roots C:\\Work,D:\\Code " +
            "--max-files-per-scan 300 --max-file-bytes 8192 --no-include-shell-history --no-include-package-manifests " +
            "--no-include-env-var-names --no-include-network-domains --allow-workspace-signatures --store-raw-local-paths",
            string.Join(' ', argv));

        foreach (var token in argv.Where(t => t.StartsWith("--", StringComparison.Ordinal)))
        {
            Assert.Contains(token, EnableHelp, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_command_it_builds_is_a_read_or_on_a_read_only_installation_it_is_refused()
    {
        var commands = new[]
        {
            AiDiscoveryTuning.Argv(On, On with { ScanIntervalMin = 10 }, new TuningRollout())!,
            AiDiscoveryTuning.Argv(Off, On, new TuningRollout())!,
            AiDiscoveryTuning.Argv(On, Off, new TuningRollout())!,
        };

        foreach (var argv in commands)
        {
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(argv));
            Assert.False(InstallationGate.IsReadOnly("defenseclaw", argv));
            Assert.False(CommandTiers.IsUnreviewedRead(argv));
        }
    }
}
