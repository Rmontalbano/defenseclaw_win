using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// G10I-05: install.ps1 1.0.10 <c>Repair-DamagedPackages</c> (scripts/install.ps1@1.0.10:2380-2410) and what the transcript says when it fails, plus the notice
/// of a rollback whose route-only settings could not be put back (G10I-02, :5080-5082). The transcripts are synthetic; every path is invented.
/// </summary>
public sealed class UvRecordRepairTranscriptTests
{
    private const string Cache = @"C:\synthetic\uvcache";

    private const string StagingLast = "  x Could not install the DefenseClaw 1.0.10 Python package; nothing was changed";

    private static string StillDiffer(string names, string where, string cache) =>
        $"  x The Python packages {names} in {where} still differ from their wheels after a fresh download; delete the uv cache {cache} and run the installer again";

    private static string Transcript(string eol, params string[] lines) =>
        string.Join(eol, new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261123090000",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            @"Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\synthetic\staging\1.0.10\install.ps1 -Version 1.0.10",
            "**********************",
            "--- Preparing DefenseClaw 1.0.10 (windows/amd64)  [09:00:01Z]",
            "  > Installed: 1.0.9",
            "  + Release signature verified",
            "  + Assets match checksums.txt",
            "  > Building the Python environment (a first install can take several minutes)",
        }.Concat(lines).Append("**********************").Append(string.Empty));

    public static IEnumerable<object[]> Eols() => new[] { new object[] { "\n" }, new object[] { "\r\n" } };

    [Theory]
    [MemberData(nameof(Eols))]
    public void Damaged_packages_in_the_staging_environment_are_a_refusal_whose_step_is_deleting_the_uv_cache(string eol)
    {
        var facts = InstallTranscript.Parse(Transcript(eol,
            "  ! The installed files of litellm, tiktoken in the staging environment differ from their wheels (the uv cache they come from was changed); downloading them again",
            StillDiffer("litellm, tiktoken", "the staging environment", Cache),
            StagingLast));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.UvCacheDamaged, refusal.Kind);
        Assert.Equal(
            @"The Python packages litellm, tiktoken in the staging environment still differ from their wheels after a fresh download. Delete the uv cache C:\synthetic\uvcache. Nothing was changed.",
            refusal.Reason);
        Assert.Equal(refusal.Reason, facts.Reason);
        Assert.Equal(@"Delete the uv cache folder C:\synthetic\uvcache, then upgrade again. uv downloads the packages again on the next run.", refusal.NextStep);
        Assert.DoesNotContain("run the installer again", refusal.Reason + refusal.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.False(facts.PreviousRestored);
    }

    // A console that wraps at a fixed width breaks a long line into physical lines (a break may fall inside a word); the parser joins them as it does for the other refusals.
    private static string[] Wrapped(string line, int width)
    {
        var pieces = new List<string>();
        for (var i = 0; i < line.Length; i += width)
        {
            var piece = line.Substring(i, Math.Min(width, line.Length - i));
            if (i > 0)
            {
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(piece, @"^\s*[xX+>!]\s"), "The wrap must not start a piece like a line of its own: " + piece);
            }

            pieces.Add(piece);
        }

        return pieces.ToArray();
    }

    [Theory]
    [MemberData(nameof(Eols))]
    public void A_console_wrapped_uv_still_differ_line_is_the_same_refusal_as_the_unwrapped_one(string eol)
    {
        var line = StillDiffer("litellm, tiktoken", "the staging environment", Cache);
        var plain = InstallTranscript.Parse(Transcript(eol, line, StagingLast));
        var wrapped = InstallTranscript.Parse(Transcript(eol, Wrapped(line, 90).Append(StagingLast).ToArray()));

        var refusal = Assert.IsType<InstallerRefusal>(wrapped.Refusal);
        Assert.Equal(InstallerRefusalKind.UvCacheDamaged, refusal.Kind);
        Assert.Equal(plain.Refusal!.Reason, refusal.Reason);
        Assert.Equal(plain.Refusal.NextStep, refusal.NextStep);
    }

    [Fact]
    public void A_console_wrapped_could_not_check_line_keeps_the_nothing_changed_kind_with_its_cause()
    {
        var line = "  x Could not check the Python packages in the staging environment at C:\\synthetic\\a-very-long-folder-name\\envs\\staging against their wheels (exit 2)";
        var facts = InstallTranscript.Parse(Transcript("\n", Wrapped(line, 80).Append(StagingLast).ToArray()));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.NothingChanged, refusal.Kind);
        Assert.StartsWith("Could not check the Python packages in the staging environment", refusal.Reason, StringComparison.Ordinal);
        Assert.EndsWith("(exit 2) · Could not install the DefenseClaw 1.0.10 Python package; nothing was changed", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_long_or_empty_cache_path_falls_back_to_the_folder_named_in_the_log()
    {
        foreach (var cache in new[] { @"C:\synthetic\" + new string('d', 500), string.Empty })
        {
            var facts = InstallTranscript.Parse(Transcript("\r\n", StillDiffer("litellm", "the staging environment", cache), StagingLast));

            var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
            Assert.Equal(InstallerRefusalKind.UvCacheDamaged, refusal.Kind);
            Assert.Contains("Delete the uv cache folder named in the install log. Nothing was changed.", refusal.Reason, StringComparison.Ordinal);
            Assert.Equal("Delete the uv cache folder named in the install log, then upgrade again. uv downloads the packages again on the next run.", refusal.NextStep);
            Assert.DoesNotContain(new string('d', 50), refusal.Reason + refusal.NextStep, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Over_long_package_names_are_not_repeated_and_a_cache_path_up_to_the_bound_is_kept_whole()
    {
        var whole = @"C:\synthetic\" + new string('d', 380);
        var kept = InstallTranscript.Parse(Transcript("\n", StillDiffer("litellm", "the staging environment", whole), StagingLast));
        Assert.Contains(whole, kept.Refusal!.Reason, StringComparison.Ordinal);
        Assert.Contains(whole, kept.Refusal.NextStep, StringComparison.Ordinal);

        var names = string.Join(", ", Enumerable.Repeat("a-package-name", 60));
        var lengthy = InstallTranscript.Parse(Transcript("\n", StillDiffer(names, "the staging environment", Cache), StagingLast));
        Assert.Equal(InstallerRefusalKind.UvCacheDamaged, lengthy.Refusal!.Kind);
        Assert.DoesNotContain("a-package-name", lengthy.Refusal.Reason, StringComparison.Ordinal);
        Assert.True(lengthy.Refusal.Reason.Length < 600);
    }

    [Fact]
    public void A_check_that_could_not_run_keeps_the_nothing_changed_kind_and_says_cause_then_outcome()
    {
        var facts = InstallTranscript.Parse(Transcript("\n",
            "  x Could not check the Python packages in the staging environment against their wheels (exit 2)",
            StagingLast));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.NothingChanged, refusal.Kind);
        Assert.Equal(InstallTranscript.GenericNextStep, refusal.NextStep);
        Assert.Equal(
            "Could not check the Python packages in the staging environment against their wheels (exit 2) · Could not install the DefenseClaw 1.0.10 Python package; nothing was changed",
            refusal.Reason);
        Assert.Equal(refusal.Reason, facts.Reason);
    }

    [Fact]
    public void The_staging_stop_without_either_uv_line_is_the_plain_nothing_changed_refusal_as_before()
    {
        var facts = InstallTranscript.Parse(Transcript("\n", StagingLast));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.NothingChanged, refusal.Kind);
        Assert.Equal("Could not install the DefenseClaw 1.0.10 Python package; nothing was changed", refusal.Reason);
    }

    [Fact]
    public void Damaged_packages_in_the_final_environment_are_a_restored_run_not_a_refusal()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n",
            "  > Installing DefenseClaw 1.0.10",
            StillDiffer("litellm", "the final environment", Cache),
            "  x Installing 1.0.10 failed; restoring 1.0.9",
            @"  x DefenseClaw 1.0.10 was not installed. Your previous install is back. Log: C:\synthetic\logs\install-20261123T090000.log"));

        Assert.Null(facts.Refusal);
        Assert.True(facts.PreviousRestored);
        Assert.StartsWith(
            @"The Python packages litellm in the final environment still differ from their wheels after a fresh download; delete the uv cache C:\synthetic\uvcache and run the installer again · ",
            facts.Reason,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ G10I-02: the route-only notice and the CLI lines that are not keyed on

    private const string NotPutBack =
        "  ! Could not rewrite the agent hook entries, or put back the route-only model settings, of DefenseClaw 1.0.10 for 1.0.9 (see above). " +
        "A route-only agent then gets 401 until the DefenseClaw base URL and route header are removed from its settings. After the rollback, " +
        @"'defenseclaw uninstall' may leave those DefenseClaw entries in the agent settings, such as the cmd.exe entries that run defenseclaw-hook.exe in %USERPROFILE%\.claude\settings.json; remove them by hand";

    private static string Rollback(params string[] lines) =>
        string.Join("\r\n", new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261123090000",
            @"Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\synthetic\staging\1.0.10\install.ps1 -Rollback",
            "**********************",
            "--- Rolling back to DefenseClaw 1.0.9  [09:00:01Z]",
        }.Concat(lines).Append("**********************").Append(string.Empty));

    [Fact]
    public void The_route_only_warning_is_one_notice_with_the_log_in_place_of_see_above()
    {
        var facts = InstallTranscript.Parse(Rollback(NotPutBack, "  + Now running DefenseClaw 1.0.9. Run 'defenseclaw rollback' again to return to 1.0.10."));

        var notice = Assert.Single(facts.Notices);
        Assert.Equal(InstallerNoticeKind.AgentSettingsNotPutBack, notice.Kind);
        Assert.Equal("Agent settings", notice.Title);
        Assert.StartsWith("Could not rewrite the agent hook entries, or put back the route-only model settings, of DefenseClaw 1.0.10 for 1.0.9 (see the install log). ", notice.Text, StringComparison.Ordinal);
        Assert.Contains("A route-only agent then gets 401", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(see above)", notice.Text, StringComparison.Ordinal);
        Assert.True(notice.Text.Length <= InstallScriptPlan.MaxFailureReasonChars);
        Assert.Null(facts.Refusal);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void A_console_wrapped_route_only_warning_is_the_same_single_notice(int width)
    {
        var tail = "  + Now running DefenseClaw 1.0.9. Run 'defenseclaw rollback' again to return to 1.0.10.";
        var plain = Assert.Single(InstallTranscript.Parse(Rollback(NotPutBack, tail)).Notices);

        var facts = InstallTranscript.Parse(Rollback(Wrapped(NotPutBack, width).Append(tail).ToArray()));

        var notice = Assert.Single(facts.Notices);
        Assert.Equal(InstallerNoticeKind.AgentSettingsNotPutBack, notice.Kind);
        Assert.Equal(plain.Text, notice.Text);
        Assert.Contains("A route-only agent then gets 401", notice.Text, StringComparison.Ordinal);
        Assert.Null(facts.Refusal);
    }

    [Fact]
    public void The_route_only_warning_is_bounded_when_the_line_is_huge()
    {
        var facts = InstallTranscript.Parse(Rollback(NotPutBack + new string('z', 5000)));

        var notice = Assert.Single(facts.Notices);
        Assert.True(notice.Text.Length <= InstallScriptPlan.MaxFailureReasonChars);
        Assert.Contains("(see the install log)", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_warning_line_there_is_no_notice_and_the_1_0_9_hook_warning_is_not_one()
    {
        Assert.Empty(InstallTranscript.Parse(Rollback("  + Now running DefenseClaw 1.0.9.")).Notices);

        var old = InstallTranscript.Parse(Rollback(
            "  ! Could not rewrite the agent hook entries of DefenseClaw 1.0.9 in the form 1.0.8 reads (see above). After the rollback, 'defenseclaw uninstall' may leave those " +
            "DefenseClaw entries in the agent settings; remove them by hand"));
        Assert.Empty(old.Notices);
        Assert.Null(old.Refusal);
    }

    [Fact]
    public void The_per_agent_route_only_lines_of_a_successful_rollback_change_nothing()
    {
        string[] common = ["  + Now running DefenseClaw 1.0.9. Run 'defenseclaw rollback' again to return to 1.0.10."];
        string[] cli =
        [
            "  ✓ claudecode: put back the model settings route-only changed; restart the agent",
            "  OK codex: put back the model settings route-only changed; restart the agent",
        ];
        var plain = InstallTranscript.Parse(Rollback(common));
        var withCli = InstallTranscript.Parse(Rollback(cli.Concat(common).ToArray()));

        Assert.Empty(withCli.Notices);
        Assert.Null(withCli.Refusal);
        Assert.Equal(plain.Reason, withCli.Reason);
        Assert.Equal(plain.PreviousRestored, withCli.PreviousRestored);
        Assert.Equal(plain.GatewayUnhealthy, withCli.GatewayUnhealthy);
    }

    // ------------------------------------------------------------------ a 1.0.9 run reads as before

    private static string Upgrade(string from, string to) =>
        string.Join("\r\n", new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261123090000",
            $@"Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\synthetic\staging\{to}\install.ps1 -Version {to}",
            "**********************",
            $"--- Preparing DefenseClaw {to} (windows/amd64)  [09:00:01Z]",
            $"  > Installed: {from}",
            "  + Release signature verified",
            "  + Assets match checksums.txt",
            $"  + Installed DefenseClaw {to}",
            $"  Upgraded from {from}. Undo with: defenseclaw rollback",
            "  ! Protection is off in the kept config (guardrail.enabled = false), so agent hooks are not guarded",
            "  Turn it back on with: defenseclaw setup guardrail",
            "**********************",
            string.Empty,
        });

    [Fact]
    public void A_clean_1_0_9_upgrade_reads_the_same_as_a_1_0_10_one()
    {
        var old = InstallTranscript.Parse(Upgrade("1.0.8", "1.0.9"));
        var now = InstallTranscript.Parse(Upgrade("1.0.9", "1.0.10"));

        Assert.Null(old.Refusal);
        Assert.Null(old.Reason);
        Assert.Equal(TranscriptAssetCheck.SignatureVerified, old.AssetCheck);
        Assert.Equal(old.Reason, now.Reason);
        Assert.Equal(old.Refusal, now.Refusal);
        Assert.Equal(old.AssetCheck, now.AssetCheck);
        Assert.Equal(old.LocalAssets, now.LocalAssets);
        Assert.Equal(old.PreviousRestored, now.PreviousRestored);
        Assert.Equal(old.RestoreIncomplete, now.RestoreIncomplete);
        Assert.Equal(old.GatewayUnhealthy, now.GatewayUnhealthy);
        Assert.Equal(old.GatewayLeftStopped, now.GatewayLeftStopped);
        Assert.Equal(old.GuardrailOff, now.GuardrailOff);
        Assert.Equal(InstallerNoticeKind.ProtectionOff, Assert.Single(old.Notices).Kind);
        Assert.True(old.Notices.SequenceEqual(now.Notices));
    }
}
