using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// Read-only advisories around the 1.0.7 / 1.0.8 upgrade: the config keys a release refuses, what a rollback leaves behind, the upgrade source range, a linked
/// data folder, and the Cowork plugin that 'defenseclaw uninstall' cannot remove. Synthetic values throughout.
/// </summary>
public sealed class UpgradeAdvisoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class Probes : IUpgradeProbes
    {
        public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Home { get; set; }

        public IReadOnlyList<string> RunningProcessNames() => [];

        public long? FreeDiskBytes(string path) => 100L * 1024 * 1024 * 1024;

        public long DatabaseBytes(string dataDirectory) => 1024;

        public string? DefenseClawHome() => Home;

        public bool IsReparsePoint(string path) => Links.Contains(path);
    }

    private sealed class Leftovers : ILeftoverProbes
    {
        public string? RunValue(string name) => null;

        public UninstallEntry? FindUninstallEntry(string keyName) => new("DefenseClaw", @"C:\synthetic\setup\unins000.exe");

        public bool FileExists(string path) => true;
    }

    private PreflightReport Evaluate(Probes probes, string? target, string? installed) =>
        InstallScriptPreflight.Evaluate(probes, null, _temp.Path, PreflightVerification.Verified, "detail", target, installed);

    private static string[] QuietIds { get; } = ["installation", "codex", "claude-code", "processes", "pending-writes", "disk", "verification"];

    // ------------------------------------------------------------------ config key floors

    [Theory]
    [InlineData("decision_model:\n  mode: cascade\n", "decision_model", "1.0.6")]
    [InlineData("observability:\n  local:\n    max_store_mb: 64\n", "observability.local.max_store_mb", "1.0.6")]
    [InlineData("guardrail:\n  judge:\n    max_parallel: 4\n", "guardrail.judge.max_parallel", "1.0.6")]
    [InlineData("guardrail:\n  protected_files:\n    - a\n", "guardrail.protected_files", "1.0.6")]
    [InlineData("guardrail:\n  judge:\n    extra_body:\n      k: v\n", "guardrail.judge.extra_body", "1.0.8")]
    public void Each_key_is_refused_before_its_first_release_and_accepted_from_it(string yaml, string key, string floor)
    {
        var config = ConfigYamlReader.Parse(yaml);
        var first = Version.Parse(floor);

        Assert.Equal([key], ConfigKeyFloors.RefusedBy(config, new Version(first.Major, first.Minor, first.Build - 1)));
        Assert.Empty(ConfigKeyFloors.RefusedBy(config, first));
        Assert.Empty(ConfigKeyFloors.RefusedBy(config, new Version(first.Major, first.Minor, first.Build + 1)));
    }

    [Fact]
    public void Hook_connectors_inside_decision_model_is_refused_alone_from_1_0_6_and_folded_into_the_block_before()
    {
        var config = ConfigYamlReader.Parse("decision_model:\n  hook_connectors:\n    - claudecode\n");

        Assert.Equal(["decision_model.hook_connectors"], ConfigKeyFloors.RefusedBy(config, new Version(1, 0, 6)));
        Assert.Equal(["decision_model"], ConfigKeyFloors.RefusedBy(config, new Version(1, 0, 5)));
        Assert.Empty(ConfigKeyFloors.RefusedBy(config, new Version(1, 0, 7)));
    }

    [Fact]
    public void An_empty_config_refuses_nothing_and_the_unset_command_lists_the_keys()
    {
        Assert.Empty(ConfigKeyFloors.RefusedBy(ConfigYamlReader.Empty, new Version(1, 0, 0)));
        Assert.Equal("defenseclaw config unset decision_model guardrail.judge.extra_body", ConfigKeyFloors.UnsetCommand(["decision_model", "guardrail.judge.extra_body"]));
    }

    // ------------------------------------------------------------------ rollback

    private const string ExtraBodyYaml = "guardrail:\n  judge:\n    extra_body:\n      k: v\n";

    [Fact]
    public void Rollback_names_the_keys_the_older_release_does_not_accept_and_where_todays_config_is_kept()
    {
        var result = RollbackConfigKeys.Evaluate(ExtraBodyYaml, "1.0.7");

        Assert.NotNull(result);
        Assert.Equal(["guardrail.judge.extra_body"], result.Keys);
        Assert.Contains("DefenseClaw 1.0.7 does not accept guardrail.judge.extra_body", result.Note, StringComparison.Ordinal);
        Assert.Contains("previous\\data", result.Note, StringComparison.Ordinal);
        Assert.StartsWith("Rollback puts back the config.yaml saved when you upgraded.", result.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("config unset", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_says_nothing_when_the_target_accepts_the_keys_or_none_is_set()
    {
        Assert.Null(RollbackConfigKeys.Evaluate(ExtraBodyYaml, "1.0.8"));
        Assert.Null(RollbackConfigKeys.Evaluate("guardrail:\n  mode: observe\n", "1.0.7"));
        Assert.Null(RollbackConfigKeys.Evaluate(null, "1.0.7"));
        Assert.Null(RollbackConfigKeys.Evaluate(ExtraBodyYaml, "not-a-version"));
    }

    // ------------------------------------------------------------------ upgrade source range

    [Theory]
    [InlineData("0.8.10", true, true)]
    [InlineData("1.0.1", true, false)]
    [InlineData("1.0.2", false, false)]
    [InlineData("1.0.7", false, false)]
    [InlineData("v1.0.5", false, false)]
    public void Target_1_0_8_warns_about_a_source_outside_1_0_2_to_1_0_7(string installed, bool warns, bool sessionsAdvice)
    {
        var report = Evaluate(new Probes(), "1.0.8", installed);

        if (!warns)
        {
            // A 1.0.8 target also adds the OpenCode/Amp rows, so only the absence of this row is asserted.
            Assert.DoesNotContain(report.Rows, r => r.Id == UpgradeSourceRange.RowId);
            return;
        }

        var row = report.Rows.Single(r => r.Id == "upgrade-source");
        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Equal("Upgrade path", row.Title);
        Assert.Contains("support upgrades from 1.0.2 to 1.0.7, not from " + installed + ". The upgrade still runs, but this path is no longer tested.", row.Reason, StringComparison.Ordinal);
        Assert.Equal(sessionsAdvice, row.Reason.Contains("End every agent session", StringComparison.Ordinal));
        Assert.False(report.HasBlock);
    }

    [Theory]
    [InlineData("1.0.7", "0.8.10")]
    [InlineData("1.0.8", null)]
    [InlineData("1.0.8", "")]
    [InlineData("1.0.8", "unknown")]
    public void No_source_row_for_a_target_without_a_range_or_an_unknown_installed_version(string target, string? installed)
    {
        Assert.DoesNotContain(Evaluate(new Probes(), target, installed).Rows, r => r.Id == "upgrade-source");
    }

    // ------------------------------------------------------------------ data folder link

    [Fact]
    public void A_linked_data_folder_warns_for_a_target_that_refuses_it()
    {
        var probes = new Probes();
        probes.Links.Add(_temp.Path);

        var row = Evaluate(probes, "1.0.7", "1.0.6").Rows.Single(r => r.Id == "data-folder-link");

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Equal("Data folder", row.Title);
        Assert.StartsWith(_temp.Path + " is a link (junction or symbolic link). From DefenseClaw 1.0.7", row.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Restore it as a regular folder at that path, then upgrade.", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_linked_DEFENSECLAW_HOME_warns_too()
    {
        var probes = new Probes { Home = @"D:\synthetic\dc-home" };
        probes.Links.Add(@"D:\synthetic\dc-home");

        Assert.Contains(@"D:\synthetic\dc-home is a link", Evaluate(probes, "1.0.8", "1.0.7").Rows.Single(r => r.Id == "data-folder-link").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_link_row_for_an_older_target_or_a_regular_folder()
    {
        var linked = new Probes();
        linked.Links.Add(_temp.Path);

        Assert.DoesNotContain(Evaluate(linked, "1.0.6", "1.0.5").Rows, r => r.Id == "data-folder-link");
        Assert.DoesNotContain(Evaluate(new Probes { Home = @"D:\synthetic\dc-home" }, "1.0.7", "1.0.6").Rows, r => r.Id == "data-folder-link");
        Assert.DoesNotContain(Evaluate(linked, null, null).Rows, r => r.Id == "data-folder-link");
    }

    [Fact]
    public void A_missing_folder_is_not_a_link_with_the_real_probe()
    {
        IUpgradeProbes real = new SystemUpgradeProbes();

        Assert.False(real.IsReparsePoint(Path.Combine(_temp.Path, "does-not-exist")));
        Assert.False(real.IsReparsePoint(_temp.Path));
    }

    [Fact]
    public void Older_runtimes_get_the_same_rows_as_before_on_a_quiet_machine()
    {
        var report = Evaluate(new Probes(), "1.0.6", "1.0.5");

        Assert.Equal(["installation", "codex", "claude-code", "opencode-amp", "processes", "pending-writes", "disk", "verification"], report.Rows.Select(r => r.Id).ToArray());
        Assert.All(report.Rows, r => Assert.Equal(PreflightStatus.Pass, r.Status));
        Assert.Equal(InstallScriptPreflight.Evaluate(new Probes(), null, _temp.Path, PreflightVerification.Verified, "detail", "1.0.6").Rows, report.Rows);
    }

    // ------------------------------------------------------------------ Cowork uninstall sentence

    private string DataWithSetupPrevious()
    {
        var previous = Path.Combine(_temp.Path, "previous");
        _ = Directory.CreateDirectory(Path.Combine(previous, "legacy-setup"));
        File.WriteAllText(Path.Combine(previous, "VERSION"), "0.8.10");
        return _temp.Path;
    }

    [Fact]
    public void The_cowork_sentence_is_added_to_the_setup_rollback_note_and_the_leftover_row_when_asked()
    {
        var data = DataWithSetupPrevious();
        var sentence = DefenseClaw.Core.ClaudeCode.CoworkProtectionFacts.RemovePluginStep;

        Assert.EndsWith(" " + sentence, InstallScriptPlan.CheckRollback(data, coworkPluginLeft: true).Note, StringComparison.Ordinal);
        Assert.DoesNotContain("Cowork", InstallScriptPlan.CheckRollback(data).Note, StringComparison.Ordinal);
        Assert.Contains(sentence, PostUpgradeLeftovers.Evaluate(new Leftovers(), coworkPluginLeft: true).Single().Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Cowork", PostUpgradeLeftovers.Evaluate(new Leftovers()).Single().Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_cowork_sentence_is_the_installed_releases_as_in_the_uninstall_review(bool runsAfterRemoval)
    {
        // 1.0.8 (false): calls are blocked until the plugin is removed. 1.0.9 (true): a 1.0.9-built plugin lets calls run, older ones may block.
        var data = DataWithSetupPrevious();
        var expected = DefenseClaw.Core.ClaudeCode.CoworkProtectionFacts.RemovePluginStepFor(runsAfterRemoval);
        var other = DefenseClaw.Core.ClaudeCode.CoworkProtectionFacts.RemovePluginStepFor(!runsAfterRemoval);

        var note = InstallScriptPlan.CheckRollback(data, coworkPluginLeft: true, coworkPluginRunsAfterRemoval: runsAfterRemoval).Note;
        Assert.EndsWith(" " + expected, note, StringComparison.Ordinal);
        Assert.DoesNotContain(other, note, StringComparison.Ordinal);

        foreach (var removesSetup in new[] { false, true })
        {
            var probes = new Leftovers();
            var reason = PostUpgradeLeftovers.Evaluate(probes, coworkPluginLeft: true, uninstallRemovesSetup: removesSetup, coworkPluginRunsAfterRemoval: runsAfterRemoval).Single().Reason;
            Assert.Contains(expected, reason, StringComparison.Ordinal);
            Assert.DoesNotContain(other, reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_cowork_sentence_needs_both_the_capability_and_the_state_file()
    {
        var data = _temp.Path;
        Assert.False(CoworkUninstall.Applies(true, data));
        Assert.False(CoworkUninstall.Applies(false, data));

        _ = Directory.CreateDirectory(Path.Combine(data, "cowork"));
        File.WriteAllText(Path.Combine(data, "cowork", "plugin-state.json"), "{}");

        Assert.True(CoworkUninstall.Applies(true, data));
        Assert.False(CoworkUninstall.Applies(false, data));
    }
}
