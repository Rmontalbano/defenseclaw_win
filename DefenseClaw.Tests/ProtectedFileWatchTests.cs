using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The protected-file watch of DefenseClaw 1.0.6 (CUST-422), synthetic throughout and verified from source only: the version-judged capability, the audit row's
/// details text (<c>protectedFileChangeDetails</c>, internal/gateway/protected_files.go), <c>alerts --json</c> carrying it, config.yaml's block, and the restart rule.
/// </summary>
public sealed class ProtectedFileWatchTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private const string Details =
        "rule=persistence.protected_file_changed class=authorized_keys before=sha256:0123456789ab after=sha256:ba9876543210 " +
        "connector=codex session=ses-1 tool_invocation=call-7 agent_identity=agt-00000000000000aa";

    private static RuntimeSnapshot Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------------ capability

    [Theory]
    [InlineData("1.0.6", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.5", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected) =>
        Assert.Equal(expected, Probe(version).Capabilities.Has(RuntimeCapability.ProtectedFileWatch));

    [Fact]
    public void A_runtime_that_has_not_answered_or_cannot_list_its_commands_does_not_have_it()
    {
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.ProtectedFileWatch));
        var helpless = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"version\":\"1.0.6\"}", null, null, null, null, null, null, null), "test", "fp", DateTimeOffset.UnixEpoch);
        Assert.False(helpless.Capabilities.Has(RuntimeCapability.ProtectedFileWatch));
    }

    [Fact]
    public void About_names_and_explains_it()
    {
        Assert.Contains("1.0.6", RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.ProtectedFileWatch), StringComparison.Ordinal);
        Assert.Contains("1.0.6", RuntimeCapabilityCatalog.Marker(RuntimeCapability.ProtectedFileWatch), StringComparison.Ordinal);
        Assert.Contains(RuntimeCapability.ProtectedFileWatch, RuntimeCapabilityCatalog.All);
    }

    // ------------------------------------------------------------------ the row's text

    [Fact]
    public void The_details_text_gives_every_fact_of_the_row()
    {
        var change = ProtectedFileAlert.Parse(@"C:\Users\operator\.ssh\authorized_keys", Details);

        Assert.Equal(@"C:\Users\operator\.ssh\authorized_keys", change.File);
        Assert.Equal("authorized_keys", change.Class);
        Assert.Equal("sha256:0123456789ab", change.Before);
        Assert.Equal("sha256:ba9876543210", change.After);
        Assert.Equal("codex", change.Connector);
        Assert.Equal("ses-1", change.Session);
        Assert.Equal("call-7", change.ToolCall);
        Assert.Equal("agt-00000000000000aa", change.AgentIdentity);
        Assert.Null(change.Restored);
        Assert.Empty(change.RestoreError);
    }

    [Fact]
    public void Restore_outcomes_are_read_including_a_quoted_reason()
    {
        Assert.True(ProtectedFileAlert.Parse("f", "rule=r restored=true").Restored);

        var failed = ProtectedFileAlert.Parse("f", "rule=r before=absent after=sha256:0123456789ab restored=false restore_error=\"the file \\\"changed\\\" again while it was restored\"");
        Assert.False(failed.Restored);
        Assert.Equal("the file \"changed\" again while it was restored", failed.RestoreError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not pairs at all")]
    [InlineData("=x ==y z=")]
    [InlineData("restore_error=\"never closed")]
    [InlineData("rule=r future_key=1 another=\"two words\"")]
    public void A_text_it_cannot_read_gives_the_file_and_no_invented_facts(string? details)
    {
        var change = ProtectedFileAlert.Parse("f", details);

        Assert.Equal("f", change.File);
        Assert.Empty(change.Before);
        Assert.Empty(change.Session);
    }

    [Fact]
    public void The_row_columns_fill_in_what_the_details_do_not_say_and_the_details_win_otherwise()
    {
        var bare = ProtectedFileAlert.Parse("f", "rule=r", connector: "codex", session: "ses-col", agentIdentity: "agt-col");
        Assert.Equal(("codex", "ses-col", "agt-col"), (bare.Connector, bare.Session, bare.AgentIdentity));

        var full = ProtectedFileAlert.Parse("f", Details, connector: "other", session: "other", agentIdentity: "other");
        Assert.Equal(("codex", "ses-1", "agt-00000000000000aa"), (full.Connector, full.Session, full.AgentIdentity));
    }

    [Theory]
    [InlineData("sha256:0123456789ab", "0123456789ab")]
    [InlineData("absent", "absent (no file)")]
    [InlineData("link:sha256:0123456789abcdef0123456789abcdef", "link 0123456789ab")]
    [InlineData("unreadable", "unreadable")]
    [InlineData("large:2097152:1730000000", "large:2097152:1730000000")]
    [InlineData("", "not recorded")]
    [InlineData(null, "not recorded")]
    public void A_state_reads_as_its_hash_prefix_or_its_word(string? digest, string expected) =>
        Assert.Equal(expected, ProtectedFileAlert.DigestLabel(digest));

    [Theory]
    [InlineData(@"C:\Users\operator\.ssh\authorized_keys", @"C:\Users\operator", @"~\.ssh\authorized_keys")]
    [InlineData(@"c:\users\OPERATOR\.ssh\authorized_keys", @"C:\Users\operator\", @"~\.ssh\authorized_keys")]
    [InlineData(@"C:\Users\operator2\.ssh\authorized_keys", @"C:\Users\operator", @"C:\Users\operator2\.ssh\authorized_keys")]
    [InlineData("", @"C:\Users\operator", "a protected file")]
    public void One_line_shows_the_home_folder_as_a_tilde(string file, string home, string expected) =>
        Assert.Equal(expected, ProtectedFileAlert.FileLabel(file, home));

    [Theory]
    [InlineData("protected-file-changed", true)]
    [InlineData("PROTECTED-FILE-CHANGED", true)]
    [InlineData("scan-finding", false)]
    [InlineData(null, false)]
    public void The_action_is_recognised(string? action, bool expected) => Assert.Equal(expected, ProtectedFileAlert.IsAction(action));

    // ------------------------------------------------------------------ alerts --json

    private const string Printed =
        """
        warning: a line the CLI printed first
        [
          {
            "action": "protected-file-changed",
            "actor": "defenseclaw",
            "connector": "codex",
            "details": "rule=persistence.protected_file_changed class=authorized_keys before=absent after=sha256:0123456789ab connector=codex session=ses-1 tool_invocation=call-7",
            "id": "01900000-0000-7000-8000-000000000001",
            "session_id": "ses-1",
            "severity": "HIGH",
            "target": "C:\\Users\\operator\\.ssh\\authorized_keys",
            "timestamp": "2026-11-01T10:00:00+00:00",
            "a_field_from_a_later_release": {"nested": [1, 2]}
          }
        ]
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_runtime_list_carries_the_row_and_an_unknown_field_changes_nothing(string lineEnding)
    {
        var result = RuntimeAlertSnapshotParser.Parse(Printed.ReplaceLineEndings(lineEnding));

        Assert.Equal(RuntimeAlertParseStatus.Ok, result.Status);
        var row = Assert.Single(result.Rows);
        Assert.Equal(ProtectedFileAlert.Action, row.Action);
        Assert.Equal(AuditSeverity.High, row.Severity);
        Assert.Equal("codex", row.Connector);
        Assert.Contains(row.Facts, f => f is { Name: "session_id", Value: "ses-1" });

        var change = ProtectedFileAlert.Parse(row.Target, row.Details);
        Assert.Equal(@"C:\Users\operator\.ssh\authorized_keys", change.File);
        Assert.Equal("absent", change.Before);
        Assert.Equal("call-7", change.ToolCall);
    }

    // ------------------------------------------------------------------ counts

    [Fact]
    public void The_counts_keep_the_watch_rows_wherever_they_fall_and_tally_them_like_any_other()
    {
        var at = DateTimeOffset.UnixEpoch;
        var window = new List<AlertQueueItem>();
        for (var i = 0; i < 5; i++)
        {
            window.Add(new AlertQueueItem("f" + i, AuditSeverity.Medium, "scan-finding", null, "codex", at.AddMinutes(-i)));
        }

        window.Add(new AlertQueueItem("p1", AuditSeverity.High, ProtectedFileAlert.Action, "f1", "codex", at.AddMinutes(-10)));
        window.Add(new AlertQueueItem("p2", AuditSeverity.High, ProtectedFileAlert.Action, "f2", null, at.AddMinutes(-11)));

        var counts = new AlertCounts(window, hasMore: false, newestLimit: 2);

        Assert.Equal(7, counts.Total);
        Assert.Equal(2, counts.High);
        Assert.Equal(2, counts.Newest.Count);
        Assert.Equal(["p1", "p2"], counts.ProtectedFileChanges.Select(i => i.Id));
        Assert.False(counts.SameAs(new AlertCounts(window.Take(6).ToList(), hasMore: false, newestLimit: 2)));
    }

    // ------------------------------------------------------------------ config.yaml

    private static ProtectedFilesSettings Read(string yaml) => ConfigStore.Parse(yaml).Config.Guardrail.ProtectedFiles;

    [Theory]
    [InlineData("guardrail:\n  enabled: true\n")]
    [InlineData("guardrail:\r\n  enabled: true\r\n")]
    [InlineData("guardrail:\n  protected_files:\n")]
    [InlineData("guardrail:\n  protected_files: {}\n")]
    [InlineData("guardrail:\n  protected_files: nonsense\n")]
    [InlineData("guardrail:\n  protected_files: [a, b]\n")]
    public void Unset_or_the_wrong_shape_is_the_default_and_the_file_still_reads(string yaml)
    {
        var settings = Read(yaml);

        Assert.Equal(ProtectedFilesSettings.Alert, settings.OnChange);
        Assert.False(settings.IsSet);
        Assert.Null(settings.Unrecognized);
        Assert.Equal([".ssh/authorized_keys", ".ssh/authorized_keys2"], settings.WatchedPaths);
    }

    [Theory]
    [InlineData("alert", "alert")]
    [InlineData("restore", "restore")]
    [InlineData("off", "off")]
    [InlineData("\"off\"", "off")]
    [InlineData("RESTORE", "restore")]
    [InlineData("\" off \"", "off")]
    public void The_modes_are_read_as_the_runtime_folds_them(string written, string expected)
    {
        foreach (var ending in new[] { "\n", "\r\n" })
        {
            var settings = Read($"guardrail:{ending}  protected_files:{ending}    on_change: {written}{ending}");
            Assert.Equal(expected, settings.OnChange);
            Assert.True(settings.IsSet);
            Assert.Null(settings.Unrecognized);
        }
    }

    [Theory]
    [InlineData("sometimes", "sometimes")]
    [InlineData("true", "true")]
    public void A_value_it_does_not_know_reads_as_alert_and_is_kept_to_say_so(string written, string kept)
    {
        var settings = Read($"guardrail:\n  protected_files:\n    on_change: {written}\n");

        Assert.Equal(ProtectedFilesSettings.Alert, settings.OnChange);
        Assert.Equal(kept, settings.Unrecognized);
    }

    [Fact]
    public void Configured_paths_replace_the_built_in_list_and_a_bad_entry_is_skipped()
    {
        var settings = Read("guardrail:\n  protected_files:\n    paths: [.ssh/authorized_keys, .config/app/, {a: b}, '']\n");

        Assert.True(settings.HasConfiguredPaths);
        Assert.Equal([".ssh/authorized_keys", ".config/app/"], settings.WatchedPaths);
        Assert.Equal(ProtectedFilesSettings.Default.WatchedPaths, Read("guardrail:\n  protected_files:\n    paths: []\n").WatchedPaths);
        Assert.Equal(ProtectedFilesSettings.Default.WatchedPaths, Read("guardrail:\n  protected_files:\n    paths: not-a-list\n").WatchedPaths);
    }

    // ------------------------------------------------------------------ restart

    [Theory]
    [InlineData("guardrail.protected_files")]
    [InlineData("guardrail.protected_files.on_change")]
    [InlineData("guardrail.protected_files.paths")]
    public void The_setting_needs_a_restart(string path) => Assert.True(ConfigReloadRules.NeedsRestart(path.Split('.')));

    [Fact]
    public void A_save_that_changes_the_setting_is_predicted_as_a_restart_and_not_a_hot_reload()
    {
        var verdict = ConfigReloadRules.Classify(
            "guardrail:\n  mode: observe\n",
            "guardrail:\n  mode: observe\n  protected_files:\n    on_change: restore\n");

        Assert.False(verdict.HotOnly);
        Assert.Contains(verdict.RestartPaths, p => p.StartsWith("guardrail.protected_files", StringComparison.Ordinal));
    }
}
