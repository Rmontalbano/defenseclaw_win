using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-424: what install.ps1 1.0.6 says beyond its exit code. A guarded OpenCode V1 refuses the upgrade (scripts/install.ps1:5689-5692, migrations.py:3319); an
/// OpenCode V2 refuses a rollback to before 1.0.6 (install.ps1:639-652,5218, migrations.py:3400); a running OpenCode or Amp stops the run (install.ps1:1147-1176); and any
/// other last line can say nothing was changed. The transcripts are synthetic, built from 1.0.6's sentences, with the markers and wraps a real one may have: no
/// real 1.0.6 transcript has been read. The user is <c>operator</c>.
/// </summary>
public sealed class OpenCodeRefusalTests : IDisposable
{
    private const string OpenCodeExe = @"C:\Users\operator\AppData\Roaming\npm\opencode.cmd";

    private const string V1Sentence =
        "OpenCode 1.18.34 at " + OpenCodeExe + " is OpenCode V1, and DefenseClaw 1.0.6 guards OpenCode V2 only; nothing was changed, and the DefenseClaw you have keeps guarding it. " +
        "Install OpenCode V2 (npm uninstall -g opencode-ai && npm install -g @opencode/cli@2), then upgrade again. To upgrade without OpenCode instead, run 'defenseclaw setup remove opencode' first.";

    private const string V1NextStep =
        "Install OpenCode V2 (npm uninstall -g opencode-ai && npm install -g @opencode/cli@2), then upgrade again. To upgrade without OpenCode instead, run 'defenseclaw setup remove opencode' first.";

    private const string V2Sentence =
        "DefenseClaw 1.0.5 guards OpenCode V1 only and the OpenCode here is V2 (opencode v2.2.3): its gateway would not start, and OpenCode would run unguarded; nothing was changed. " +
        "The rollback restores that release's own configuration, so removing OpenCode here does not help: to roll back, put OpenCode V1 back first " +
        "(npm uninstall -g @opencode/cli && npm install -g opencode-ai@1.18), then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.";

    private const string V2NextStep =
        "The rollback restores that release's own configuration, so removing OpenCode here does not help: to roll back, put OpenCode V1 back first " +
        "(npm uninstall -g @opencode/cli && npm install -g opencode-ai@1.18), then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.";

    private const string MigrateRefused = "  x Your configuration cannot be migrated to 1.0.6; nothing was changed (see above)";
    private const string RollbackRefused = "  x Rollback cancelled; nothing was changed (see above)";

    private const string CloseStop =
        "  x Close OpenCode and Amp completely before installing or upgrading DefenseClaw (OpenCode V2 keeps a background server after its window closes: stop it with " +
        "'opencode service stop'), then run this installer again; gateway still running: opencode (PID 4242), amp (PID 77)";

    private const string CloseStop105 =
        "  x Close OpenCode and Amp completely before installing or upgrading DefenseClaw, then run this installer again; gateway still running: opencode (PID 4242)";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static IEnumerable<object[]> Eols() => new[] { new object[] { "\n" }, new object[] { "\r\n" } };

    public static IEnumerable<object[]> EolsAndMarkers() =>
        from eol in new[] { "\n", "\r\n" }
        from marker in new[] { "X", "\u2717", "\u0393\u00a3\u00f9", "\u00e2\u0153\u2014", "?" }
        select new object[] { eol, marker };

    public static IEnumerable<object[]> EolsAndWidths() =>
        from eol in new[] { "\n", "\r\n" }
        from width in new[] { 80, 97, 120 }
        select new object[] { eol, width };

    private static string Transcript(string eol, string host, params string[] lines) =>
        string.Join(eol, new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261102120000",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            @"Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\staging\1.0.6\install.ps1 " + host,
            "**********************",
        }.Concat(lines).Append("**********************").Append("Windows PowerShell transcript end").Append(string.Empty));

    /// <summary>What <c>Out-String</c> makes of a long line at a console this wide: cut every <paramref name="width"/> characters, wherever that falls.</summary>
    private static string[] HardWrapped(string line, int width) => line.Chunk(width).Select(static c => new string(c)).ToArray();

    private static string[] Upgrade(params string[] cliLines) =>
        new[] { "--- Preparing DefenseClaw 1.0.6 (windows/amd64)  [12:00:01Z]", "  > Installed: 1.0.5", "  + Assets match checksums.txt", "  > Building the Python environment" }
            .Concat(cliLines)
            .Append(MigrateRefused)
            .ToArray();

    private static InstallScriptRunResult Run(int? exit, bool rollback, InstallTranscriptFacts transcript) => new()
    {
        Started = true,
        ExitCode = exit,
        Rollback = rollback,
        LogPath = @"C:\Users\operator\.defenseclaw\logs\install-20261102T120000.log",
        Transcript = transcript,
        InstallerReason = transcript.Reason,
        Exit = InstallScriptPlan.MapExit(exit, rollback, transcript),
    };

    private static PostFlightFacts Facts() =>
        new(@"C:\Users\operator\.local\bin\defenseclaw.exe", true, "defenseclaw-cli 1.0.5", "Policy model", true, "Running")
        {
            GatewayOwnerVerified = true,
            GatewayVersion = "1.0.5",
            CliVersion = "1.0.5",
            Telemetry = new TelemetryFacts(TelemetryCondition.Healthy, "Running; no destination is failing."),
            Audit = new AuditPersistence(AuditPersistenceState.Persisted, "The gateway's start was recorded in audit.db."),
        };

    private static void AssertNeverClaimsChange(InstallTranscriptFacts facts, bool rollback)
    {
        var exit = InstallScriptPlan.MapExit(1, rollback, facts);
        Assert.Equal(InstallOutcome.Refused, exit.Outcome);
        Assert.False(exit.ChangedMachine);
        Assert.Equal(rollback, exit.OffersRollback);
        Assert.DoesNotContain("restores", exit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not finish", exit.Message, StringComparison.Ordinal);

        var verdict = UpgradeVerdicts.Classify(Run(1, rollback, facts), Facts(), rollback ? null : "1.0.6");
        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);
        Assert.Equal("Warn", verdict.ToneKey);
        Assert.Contains("nothing on this machine was changed", verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("did not finish", verdict.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the upgrade refused for OpenCode V1

    [Theory]
    [MemberData(nameof(EolsAndMarkers))]
    public void The_upgrade_refused_for_opencode_v1_names_it_with_its_remedy_whatever_the_marker(string eol, string marker)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, "-Version 1.0.6", Upgrade($"  {marker} {V1Sentence}")));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.OpenCodeV1Upgrade, refusal.Kind);
        Assert.Equal(
            @"OpenCode 1.18.34 at ~\AppData\Roaming\npm\opencode.cmd is OpenCode V1, and DefenseClaw 1.0.6 guards OpenCode V2 only; nothing was changed, and the DefenseClaw you have keeps guarding it.",
            refusal.Reason);
        Assert.Equal(V1NextStep, refusal.NextStep);
        Assert.Equal(refusal.Reason, facts.Reason);
        Assert.DoesNotContain("operator", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        AssertNeverClaimsChange(facts, rollback: false);
    }

    [Fact]
    public void The_sentence_decides_not_the_marker_so_it_can_have_none()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade("  " + V1Sentence)));

        Assert.Equal(InstallerRefusalKind.OpenCodeV1Upgrade, facts.Refusal!.Kind);
    }

    [Theory]
    [MemberData(nameof(EolsAndWidths))]
    public void A_cli_line_cut_at_the_console_width_is_still_read_with_no_cut_in_what_is_shown(string eol, int width)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, "-Version 1.0.6", Upgrade(HardWrapped("  X " + V1Sentence, width))));

        var refusal = facts.Refusal!;
        Assert.Equal(InstallerRefusalKind.OpenCodeV1Upgrade, refusal.Kind);
        Assert.Equal(V1NextStep, refusal.NextStep);
        Assert.Contains("is OpenCode V1, and DefenseClaw 1.0.6 guards OpenCode V2 only; nothing was changed", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains(@"~\AppData\Roaming\npm\opencode.cmd", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrap_at_a_word_with_the_space_trimmed_away_is_still_read_and_shown_with_its_spaces()
    {
        // Word-wrapped at about 100 columns with nothing left of the space at each break.
        var words = ("  X " + V1Sentence).Split(' ');
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var word in words)
        {
            if (line.Length + word.Length > 100)
            {
                lines.Add(line.TrimEnd());
                line = string.Empty;
            }

            line += word + " ";
        }

        lines.Add(line.TrimEnd());

        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade(lines.ToArray())));

        Assert.Equal(InstallerRefusalKind.OpenCodeV1Upgrade, facts.Refusal!.Kind);
        Assert.Equal(V1NextStep, facts.Refusal.NextStep);
        Assert.Contains("is OpenCode V1, and DefenseClaw 1.0.6 guards OpenCode V2 only; nothing was changed, and the DefenseClaw you have keeps guarding it.", facts.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_400_character_cap_on_the_reason_never_loses_the_remedy()
    {
        var deep = @"C:\Users\operator\AppData\Roaming\npm\" + string.Join('\\', Enumerable.Repeat("a-rather-long-folder-name", 20)) + @"\opencode.cmd";
        var sentence = V1Sentence.Replace(OpenCodeExe, deep, StringComparison.Ordinal);
        Assert.True(sentence.Length > InstallScriptPlan.MaxFailureReasonChars);

        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade("  X " + sentence)));

        Assert.Equal(InstallerRefusalKind.OpenCodeV1Upgrade, facts.Refusal!.Kind);
        Assert.True(facts.Reason!.Length <= InstallScriptPlan.MaxFailureReasonChars);
        Assert.Contains("OpenCode 1.18.34 at", facts.Reason, StringComparison.Ordinal);
        Assert.Equal(V1NextStep, facts.Refusal.NextStep);
    }

    [Fact]
    public void Another_migrate_check_failure_keeps_the_clis_line_as_the_reason_and_is_refused_too()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade(
            "  X guardrail.connectors names a connector this release removed; set up a supported connector (`defenseclaw setup <connector>`)")));

        var refusal = facts.Refusal!;
        Assert.Equal(InstallerRefusalKind.NothingChanged, refusal.Kind);
        Assert.StartsWith("guardrail.connectors names a connector this release removed", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCode", refusal.Reason, StringComparison.Ordinal);
        AssertNeverClaimsChange(facts, rollback: false);
    }

    [Fact]
    public void The_x_line_alone_is_refused_without_naming_opencode()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade()));

        Assert.Equal(InstallerRefusalKind.NothingChanged, facts.Refusal!.Kind);
        Assert.Equal("Your configuration cannot be migrated to 1.0.6; nothing was changed (see above)", facts.Reason);
        AssertNeverClaimsChange(facts, rollback: false);
    }

    [Fact]
    public void The_last_install_row_carries_the_reason_and_the_remedy()
    {
        var run = InstallRunReader.Parse(Transcript("\r\n", "-Version 1.0.6", Upgrade("  X " + V1Sentence)), @"C:\logs\install-20261102T120000.log");

        Assert.Equal(InstallRunOutcome.Refused, run.Outcome);
        var text = InstallRunReader.Describe(run, DateTimeOffset.Now);
        Assert.Contains("refused; nothing was changed", text, StringComparison.Ordinal);
        Assert.Contains("is OpenCode V1", text, StringComparison.Ordinal);
        Assert.Contains("npm install -g @opencode/cli@2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("restored", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the rollback refused for OpenCode V2

    private static string RefusedRollback(string eol, params string[] cliLines) => Transcript(
        eol,
        "-Rollback",
        new[] { "--- Rolling back to DefenseClaw 1.0.5  [12:00:01Z]" }.Concat(cliLines).Append(RollbackRefused).ToArray());

    [Theory]
    [MemberData(nameof(EolsAndMarkers))]
    public void The_rollback_refused_for_opencode_v2_says_why_and_how_with_a_rollback_it_can_run_again(string eol, string marker)
    {
        var facts = InstallTranscript.Parse(RefusedRollback(eol, $"  {marker} {V2Sentence}"));

        var refusal = facts.Refusal!;
        Assert.Equal(InstallerRefusalKind.OpenCodeV2Rollback, refusal.Kind);
        Assert.Equal(
            "DefenseClaw 1.0.5 guards OpenCode V1 only and the OpenCode here is V2 (opencode v2.2.3): its gateway would not start, and OpenCode would run unguarded; nothing was changed.",
            refusal.Reason);
        Assert.Equal(V2NextStep, refusal.NextStep);
        AssertNeverClaimsChange(facts, rollback: true);
    }

    [Theory]
    [MemberData(nameof(EolsAndWidths))]
    public void The_rollbacks_cli_line_goes_through_out_string_so_it_is_read_when_cut_at_the_console_width(string eol, int width)
    {
        // install.ps1:503,649: Get-NativeProbe's Out-String, then Write-Host of the result.
        var facts = InstallTranscript.Parse(RefusedRollback(eol, HardWrapped("  X " + V2Sentence, width)));

        Assert.Equal(InstallerRefusalKind.OpenCodeV2Rollback, facts.Refusal!.Kind);
        Assert.Equal(V2NextStep, facts.Refusal.NextStep);
    }

    [Fact]
    public void Without_a_readable_cli_line_the_rollback_refusal_still_explains_the_release_limit()
    {
        var facts = InstallTranscript.Parse(RefusedRollback("\r\n"));

        var refusal = facts.Refusal!;
        Assert.Equal(InstallerRefusalKind.OpenCodeV2Rollback, refusal.Kind);
        Assert.Equal("Rollback cancelled; nothing was changed (see above)", refusal.Reason);
        Assert.Contains("Releases before 1.0.6 cannot guard OpenCode V2", refusal.NextStep, StringComparison.Ordinal);
        AssertNeverClaimsChange(facts, rollback: true);
    }

    // ------------------------------------------------------------------ the stop while OpenCode or Amp runs

    [Theory]
    [MemberData(nameof(Eols))]
    public void The_stop_for_a_running_agent_is_refused_on_an_upgrade_and_on_a_rollback_and_names_the_service_stop(string eol)
    {
        foreach (var (host, rollback) in new[] { ("-Version 1.0.6", false), ("-Rollback", true) })
        {
            var facts = InstallTranscript.Parse(Transcript(eol, host, "  > Installed: 1.0.5", CloseStop));

            var refusal = facts.Refusal!;
            Assert.Equal(InstallerRefusalKind.AgentsRunning, refusal.Kind);
            Assert.Contains("gateway still running: opencode (PID 4242), amp (PID 77)", refusal.Reason, StringComparison.Ordinal);
            Assert.Equal(
                "Close OpenCode and Amp completely (OpenCode V2 keeps a background server after its window closes: stop it with 'opencode service stop'), then run this installer again",
                refusal.NextStep);
            AssertNeverClaimsChange(facts, rollback);
        }
    }

    [Fact]
    public void The_stop_is_read_when_it_wraps()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", HardWrapped(CloseStop, 100)));

        Assert.Equal(InstallerRefusalKind.AgentsRunning, facts.Refusal!.Kind);
        Assert.Contains("opencode service stop", facts.Refusal.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void The_1_0_5_form_of_the_stop_has_no_service_step_to_name()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.5", CloseStop105));

        Assert.Equal(InstallerRefusalKind.AgentsRunning, facts.Refusal!.Kind);
        Assert.Equal("Close OpenCode and Amp completely, then run this installer again", facts.Refusal.NextStep);
    }

    [Theory]
    [InlineData("-Version 1.0.6 -AcceptCurrentInstall")]
    public void A_run_told_to_accept_the_current_install_may_have_stopped_the_older_gateway_so_it_is_not_called_refused(string host)
    {
        // install.ps1:5733 closes the older hooks and stops its gateway before the second look at :5746.
        var facts = InstallTranscript.Parse(Transcript("\r\n", host, CloseStop));

        Assert.Null(facts.Refusal);
        Assert.Equal(InstallOutcome.FailedRestored, InstallScriptPlan.MapExit(1, rollback: false, facts).Outcome);
    }

    [Fact]
    public void A_stop_after_the_accept_warning_is_not_called_refused_either()
    {
        var facts = InstallTranscript.Parse(Transcript(
            "\r\n",
            "-Version 1.0.6",
            "  ! An older installer added programs after the interrupted first install recorded none (x); they are accepted as they are now (-AcceptCurrentInstall). Its gateway stays running",
            CloseStop));

        Assert.Null(facts.Refusal);
    }

    // ------------------------------------------------------------------ the generic form, without over-matching

    [Theory]
    [InlineData("Rollback cancelled; nothing was changed")]
    [InlineData("Cancelled; nothing was changed")]
    [InlineData("The gateway did not stop; nothing was changed")]
    [InlineData("The release signature on checksums.txt did not verify; nothing was changed")]
    [InlineData("Your configuration is from a newer DefenseClaw than 1.0.6; nothing was changed")]
    [InlineData("Could not keep a whole rollback copy of C:\\x in C:\\y (an antivirus may have removed it); nothing was changed. Run the installer again")]
    [InlineData("Could not set the hooks' fail modes from your config, so they could fail open while the gateway is down; nothing was changed (see above)")]
    [InlineData("DefenseClaw self-update is disabled by enterprise policy; use the managed deployment channel. Nothing was changed.")]
    public void A_last_x_line_that_ends_by_saying_nothing_was_changed_is_refused(string line)
    {
        foreach (var eol in new[] { "\n", "\r\n" })
        {
            var facts = InstallTranscript.Parse(Transcript(eol, "-Version 1.0.6", "  x " + line));

            Assert.Equal(InstallerRefusalKind.NothingChanged, facts.Refusal!.Kind);
            Assert.Equal(InstallTranscript.Sanitize(line), facts.Reason);
            AssertNeverClaimsChange(facts, rollback: false);
        }
    }

    [Theory]
    [InlineData("  x Rollback failed part-way; see the log")]
    [InlineData("  x Installing 1.0.6 failed; restoring")]
    [InlineData("  x The upgrade failed; nothing was changed by the undo, run it again")]
    [InlineData("  x Whether nothing was changed is not known")]
    [InlineData("  x nothing was changed")]
    [InlineData("  ! The gateway did not stop; nothing was changed")]
    [InlineData("  + Staged; nothing was changed")]
    [InlineData("  Nothing was changed. 'defenseclaw migrate' (or the upgrade) applies them.")]
    public void Other_lines_are_not_read_as_a_refusal(string line)
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", line));

        Assert.Null(facts.Refusal);
        Assert.Equal(InstallOutcome.FailedRestored, InstallScriptPlan.MapExit(1, rollback: false, facts).Outcome);
    }

    [Theory]
    [InlineData("  x DefenseClaw 1.0.6 was not installed. Your previous install is back.")]
    [InlineData("  x Installing 1.0.6 failed; restoring")]
    public void A_nothing_changed_line_that_is_not_the_runs_last_word_is_not_a_refusal(string after)
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", "  x The gateway did not stop; nothing was changed", after));

        Assert.Null(facts.Refusal);
    }

    [Theory]
    [InlineData("  Your previous install is back.")]
    [InlineData("  ! Your previous install is not fully back (see above); run the installer again to finish restoring it.")]
    [InlineData("  ! The install is not finished, so every agent hook blocks tool calls until the gateway runs again. Run the installer again to finish it")]
    [InlineData("  ! The earlier install is not finished, but its gateway is still running and can rewrite its hooks, so the install lock is released")]
    public void A_run_that_says_it_restored_or_left_something_never_claims_nothing_changed(string said)
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", said, CloseStop));
        Assert.Null(facts.Refusal);

        var generic = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", said, "  x The gateway did not stop; nothing was changed"));
        Assert.Null(generic.Refusal);
    }

    [Fact]
    public void A_cli_sentence_with_a_different_x_line_is_not_the_v1_refusal()
    {
        // The CLI's line above is not enough: the run's last word has to be the installer's refusal.
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.6", "  X " + V1Sentence, "  x Installing 1.0.6 failed; restoring"));

        Assert.Null(facts.Refusal);
    }

    [Fact]
    public void A_run_that_installed_is_not_refused_on_the_last_install_row_whatever_an_x_line_says()
    {
        var run = InstallRunReader.Parse(
            Transcript("\r\n", "-Version 1.0.6", "  x Could not refresh a thing; nothing was changed", "  + Installed DefenseClaw 1.0.6"),
            @"C:\logs\install-20261102T120000.log");

        Assert.Equal(InstallRunOutcome.Installed, run.Outcome);
    }

    [Fact]
    public void The_stale_codex_refusal_of_1_0_5_keeps_its_own_kind()
    {
        var facts = InstallTranscript.Parse(Transcript(
            "\r\n",
            "-Rollback",
            "  x Codex was updated after DefenseClaw set up Codex 0.46.0 (C:\\x\\codex.exe changed). DefenseClaw 1.0.4 does not start with an agent program it did not set up, so the rollback would stop half-way. Nothing was changed. Put that version back and run 'defenseclaw rollback' again, or stay on DefenseClaw 1.0.5"));

        Assert.Equal(InstallerRefusalKind.StaleAgentRollback, facts.Refusal!.Kind);
    }

    // ------------------------------------------------------------------ the pre-flight rows

    private sealed class Probes : IUpgradeProbes
    {
        public List<string> Processes { get; } = new();

        public OpenCodeInstallFacts Facts { get; set; } = OpenCodeInstallFacts.NotConfigured;

        public int Reads { get; private set; }

        public IReadOnlyList<string> RunningProcessNames() => Processes;

        public long? FreeDiskBytes(string path) => 100L * 1024 * 1024 * 1024;

        public long DatabaseBytes(string dataDirectory) => 0;

        public OpenCodeInstallFacts OpenCode(string dataDirectory)
        {
            Reads++;
            return Facts;
        }
    }

    private PreflightReport Preflight(Probes probes, string? target) =>
        InstallScriptPreflight.Evaluate(probes, null, _temp.Path, PreflightVerification.Verified, "detail", target);

    private static readonly OpenCodeInstall V1 = new("1.18.34", @"C:\synthetic\opencode.exe");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.5")]
    [InlineData("1.0.0")]
    [InlineData("not a version")]
    public void A_target_before_1_0_6_has_neither_row_and_reads_nothing(string? target)
    {
        var probes = new Probes { Facts = new OpenCodeInstallFacts(true, null, V1) };
        probes.Processes.AddRange(["opencode", "amp"]);

        var report = Preflight(probes, target);

        Assert.DoesNotContain(report.Rows, r => r.Id is InstallScriptPreflight.PluginAgentsId or InstallScriptPreflight.OpenCodeV1Id);
        Assert.Equal(0, probes.Reads);
    }

    [Theory]
    [InlineData("1.0.6")]
    [InlineData("v1.0.7")]
    [InlineData("1.1.0")]
    public void With_neither_running_the_row_passes_and_nothing_warns(string target)
    {
        var report = Preflight(new Probes(), target);

        var row = report.Rows.Single(r => r.Id == InstallScriptPreflight.PluginAgentsId);
        Assert.Equal(PreflightStatus.Pass, row.Status);
        Assert.DoesNotContain(report.Rows, r => r.Id == InstallScriptPreflight.OpenCodeV1Id);
    }

    [Theory]
    [InlineData("opencode", "OpenCode is running", true)]
    [InlineData("OpenCode", "OpenCode is running", true)]
    [InlineData("amp", "Amp is running", false)]
    [InlineData("opencode-helper", null, false)]
    [InlineData("ampere", null, false)]
    public void A_running_program_warns_and_opencode_gets_the_service_stop_step(string process, string? says, bool serviceStep)
    {
        var probes = new Probes();
        probes.Processes.Add(process);

        var row = Preflight(probes, "1.0.6").Rows.Single(r => r.Id == InstallScriptPreflight.PluginAgentsId);

        if (says is null)
        {
            Assert.Equal(PreflightStatus.Pass, row.Status);
            return;
        }

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.StartsWith(says, row.Reason, StringComparison.Ordinal);
        Assert.Equal(serviceStep, row.Reason.Contains("opencode service stop", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_running_are_named_together_and_it_never_blocks()
    {
        var probes = new Probes();
        probes.Processes.AddRange(["amp", "opencode", "opencode"]);

        var report = Preflight(probes, "1.0.6");

        var row = report.Rows.Single(r => r.Id == InstallScriptPreflight.PluginAgentsId);
        Assert.StartsWith("OpenCode and Amp are running", row.Reason, StringComparison.Ordinal);
        Assert.False(report.HasBlock);
        Assert.True(report.HasWarning);
    }

    [Fact]
    public void A_guarded_v1_warns_only_with_its_remedy()
    {
        var report = Preflight(new Probes { Facts = new OpenCodeInstallFacts(true, null, V1) }, "1.0.6");

        var row = report.Rows.Single(r => r.Id == InstallScriptPreflight.OpenCodeV1Id);
        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.False(report.HasBlock);
        Assert.Contains("1.18.34", row.Reason, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw 1.0.6 guards OpenCode V2 only", row.Reason, StringComparison.Ordinal);
        Assert.Contains("npm uninstall -g opencode-ai && npm install -g @opencode/cli@2", row.Reason, StringComparison.Ordinal);
        Assert.Contains("defenseclaw setup remove opencode", row.Reason, StringComparison.Ordinal);
        Assert.Null(row.ActionId);
    }

    [Fact]
    public void A_v2_or_an_opencode_that_is_not_guarded_has_no_v1_row()
    {
        Assert.DoesNotContain(Preflight(new Probes { Facts = new OpenCodeInstallFacts(true, new OpenCodeInstall("2.2.3", "x"), null) }, "1.0.6").Rows, r => r.Id == InstallScriptPreflight.OpenCodeV1Id);
        Assert.DoesNotContain(Preflight(new Probes { Facts = new OpenCodeInstallFacts(false, null, V1) }, "1.0.6").Rows, r => r.Id == InstallScriptPreflight.OpenCodeV1Id);
    }

    // ------------------------------------------------------------------ the rollback note

    private static readonly OpenCodeInstall V2 = new("2.2.4", @"C:\synthetic\opencode.exe");

    [Fact]
    public void A_1_0_6_rollback_to_an_earlier_release_with_opencode_v2_says_up_front_that_it_will_be_refused()
    {
        var note = RollbackAgentPreflight.OpenCodeNote("1.0.6", "1.0.6", "1.0.5", V2);

        Assert.Equal(
            "OpenCode V2 (2.2.4) is set up here, and releases before 1.0.6 cannot guard it: DefenseClaw 1.0.5 guards OpenCode V1 only. The installer refuses this rollback and nothing is changed. " +
            "To roll back, put OpenCode V1 back first (npm uninstall -g @opencode/cli && npm install -g opencode-ai@1.18), then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.",
            note);
    }

    [Theory]
    [InlineData("1.0.5", "1.0.5", "1.0.4")] // an installer without the check
    [InlineData("1.0.6", "1.0.5", "1.0.4")] // the staged 1.0.6 script, but the installed CLI answers 2: install.ps1:643
    [InlineData("1.0.6", "1.0.6", "1.0.6")] // a target that guards V2
    [InlineData("1.0.7", "1.0.7", "1.0.6")]
    [InlineData("1.0.6", "1.0.5", "1.0.6")] // a rollback that goes forward
    [InlineData(null, "1.0.6", "1.0.5")]
    [InlineData("1.0.6", "1.0.6", null)]
    [InlineData("1.0.6", "1.0.6", "not a version")]
    public void The_note_says_nothing_where_the_installer_would_not_refuse(string? installer, string? installed, string? backTo)
    {
        Assert.Null(RollbackAgentPreflight.OpenCodeNote(installer, installed, backTo, V2));
    }

    [Fact]
    public void No_opencode_v2_says_nothing()
    {
        Assert.Null(RollbackAgentPreflight.OpenCodeNote("1.0.6", "1.0.6", "1.0.5", null));
    }

    private void KeepPrevious(string version)
    {
        _ = Directory.CreateDirectory(_temp.File("previous"));
        _ = _temp.WriteFile(Path.Combine("previous", "VERSION"), version);
    }

    [Fact]
    public void Read_puts_the_note_beside_the_codex_one_and_reads_opencode_only_for_an_installer_that_checks()
    {
        KeepPrevious("1.0.5");
        var reads = 0;
        var facts = () =>
        {
            reads++;
            return new OpenCodeInstallFacts(true, V2, null);
        };

        var note = RollbackAgentPreflight.Read(_temp.Path, "1.0.6", "1.0.6", DateTimeOffset.Now, facts);
        Assert.StartsWith("OpenCode V2 (2.2.4) is set up here", note, StringComparison.Ordinal);
        Assert.Equal(1, reads);

        Assert.Null(RollbackAgentPreflight.Read(_temp.Path, "1.0.5", "1.0.5", DateTimeOffset.Now, facts));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Read_says_nothing_for_files_that_are_missing_or_malformed()
    {
        KeepPrevious("1.0.5");
        Assert.Null(RollbackAgentPreflight.Read(_temp.Path, "1.0.6", "1.0.6", DateTimeOffset.Now));

        _ = _temp.WriteFile("config.yaml", "guardrail:\n  connectors:\n    opencode: {}\n");
        _ = _temp.WriteFile("agent_discovery.json", "{ not json");
        var note = RollbackAgentPreflight.Read(_temp.Path, "1.0.6", "1.0.6", DateTimeOffset.Now, () => OpenCodeInstallEvidence.Read(_temp.Path, [_temp.File("no-npm-here")]));
        Assert.Null(note);
    }

    [Fact]
    public void Read_finds_a_recorded_v2_in_the_real_files()
    {
        KeepPrevious("1.0.5");
        _ = _temp.WriteFile("config.yaml", "guardrail:\n  connectors:\n    opencode: {}\n");
        var binary = _temp.WriteFile("opencode.exe", "synthetic");
        _ = _temp.WriteFile("agent_discovery.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            agents = new { opencode = new { installed = true, version = "2.2.4", binary_path = binary } },
        }));

        var note = RollbackAgentPreflight.Read(_temp.Path, "1.0.6", "1.0.6", DateTimeOffset.Now, () => OpenCodeInstallEvidence.Read(_temp.Path, []));

        Assert.StartsWith("OpenCode V2 (2.2.4)", note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the audit log

    private string Log(string name, string text, DateTime writtenUtc)
    {
        _ = Directory.CreateDirectory(_temp.File("logs"));
        var path = _temp.WriteFile(Path.Combine("logs", name), text);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private const string AuditText = "audit: live-agent identity unproven for PID 4242: process owner unreadable";

    [Fact]
    public void The_newest_log_is_never_the_audit_log_beside_it()
    {
        var now = DateTime.UtcNow;
        var transcript = Log("install-20261102T120000.log", "  + Installed DefenseClaw 1.0.6", now.AddMinutes(-2));
        _ = Log("install-20261102T120000-audit.log", AuditText, now);

        Assert.Equal(transcript, InstallRunReader.FindNewestLog(_temp.Path));
    }

    [Fact]
    public void The_audit_log_alone_is_no_log()
    {
        var now = DateTime.UtcNow;
        _ = Log("install-20261102T120000-audit.log", AuditText, now);
        _ = Log("install-20261102T120001-AUDIT.LOG", AuditText, now);

        Assert.Null(InstallRunReader.FindNewestLog(_temp.Path));
        Assert.Null(InstallScriptPlan.FindInstallLog(_temp.Path, now.AddMinutes(-1)));
    }

    [Fact]
    public void A_runs_log_found_by_write_time_is_never_the_audit_log_beside_it()
    {
        // Neither name falls inside the run, so the newest written since its start decides; the audit log is the newer.
        var now = DateTimeOffset.UtcNow;
        var transcript = Log("install-20200101T000000.log", "  + Installed DefenseClaw 1.0.6", now.UtcDateTime.AddSeconds(-1));
        _ = Log("install-20200101T000000-audit.log", AuditText, now.UtcDateTime);

        Assert.Equal(transcript, InstallScriptPlan.FindInstallLog(_temp.Path, now.AddMinutes(-1), now));
    }

    [Fact]
    public void A_runs_verdict_reads_its_transcript_not_the_audit_log()
    {
        using var services = TestServices.Create(_temp);
        var run = services.Cli.BeginExternalRun("powershell.exe", ["-NoProfile", "-File", @"C:\staging\install.ps1", "-Version", "1.0.6"], "note");
        services.Cli.CompleteExternalRun(run, 1, null, null);
        var started = run.StartedAt.LocalDateTime;
        var stem = $"install-{started:yyyyMMdd'T'HHmmss}";
        var transcript = Transcript("\r\n", "-Version 1.0.6", "  > Installed: 1.0.5", CloseStop)
            .Replace("Start time: 20261102120000", $"Start time: {started:yyyyMMddHHmmss}", StringComparison.Ordinal);
        _ = Log(stem + ".log", transcript, DateTime.UtcNow.AddSeconds(-5));
        _ = Log(stem + "-audit.log", AuditText, DateTime.UtcNow);

        var summary = InstallRunReader.Read(_temp.Path, services.Cli.Activity);

        Assert.Equal(_temp.File(Path.Combine("logs", stem + ".log")), summary!.LogPath);
        Assert.Equal(InstallRunOutcome.Refused, summary.Outcome);
    }
}
