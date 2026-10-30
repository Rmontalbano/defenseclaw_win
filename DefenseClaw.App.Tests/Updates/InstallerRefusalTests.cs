using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-399: what install.ps1 1.0.5 says beyond its exit code. A refusal before anything changed (exit 1, scripts/install.ps1:5203-5206 at tag 1.0.5) is
/// "refused; nothing was changed", not "did not finish, restored"; the notices it prints after it finished (the outage notice after "Upgraded from",
/// :5923-5926; protection off, :5930-5934; Codex hooks after a rollback, :5191-5192) reach the verdict; and the rollback pre-flight says up front
/// what the installer will do about a changed Codex. The transcripts are synthetic, modelled on 1.0.5's sentences; the user is <c>operator</c>.
/// </summary>
public sealed class InstallerRefusalTests : IDisposable
{
    private const string CodexExe = @"C:\Users\operator\AppData\Local\Programs\codex\bin\codex.exe";

    private const string StaleRefusal =
        "  x Codex was updated after DefenseClaw set up Codex 0.46.0 (" + CodexExe + " changed). DefenseClaw 1.0.4 does not start with an agent program it " +
        "did not set up, so the rollback would stop half-way. Nothing was changed. Put that version back and run 'defenseclaw rollback' again, or stay on DefenseClaw 1.0.5";

    private const string OutageNotice =
        "  Gateway outages now let calls run with a warning (the default and permissive policies). Run `defenseclaw guardrail fail-mode closed` to keep blocking them.";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static IEnumerable<object[]> Eols() => new[] { new object[] { "\n" }, new object[] { "\r\n" } };

    private static string Transcript(string eol, string host, params string[] lines) =>
        string.Join(eol, new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            "Start time: 20261030120000",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            @"Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\staging\1.0.5\install.ps1 " + host,
            "**********************",
        }.Concat(lines).Append("**********************").Append(string.Empty));

    private static string RefusedRollback(string eol = "\r\n") => Transcript(eol, "-Rollback",
        "--- Rolling back to DefenseClaw 1.0.4  [12:00:01Z]",
        StaleRefusal);

    private static InstallScriptRunResult Run(int? exit, bool rollback, InstallTranscriptFacts transcript, string? log = @"C:\Users\operator\.defenseclaw\logs\install-20261030T120000.log") => new()
    {
        Started = true,
        ExitCode = exit,
        Rollback = rollback,
        LogPath = log,
        Transcript = transcript,
        InstallerReason = transcript.Reason,
        Exit = InstallScriptPlan.MapExit(exit, rollback, transcript),
    };

    private static PostFlightFacts Facts(bool running) =>
        new(@"C:\Users\operator\.local\bin\defenseclaw.exe", true, "defenseclaw-cli 1.0.5", "Policy model", running, running ? "Running" : "Gateway stopped")
        {
            GatewayOwnerVerified = running ? true : null,
            GatewayVersion = running ? "1.0.5" : null,
            CliVersion = "1.0.5",
            Telemetry = new TelemetryFacts(TelemetryCondition.Healthy, "Running; no destination is failing."),
            Audit = new AuditPersistence(AuditPersistenceState.Persisted, "The gateway's start was recorded in audit.db."),
        };

    // ------------------------------------------------------------------ the refusal

    [Theory]
    [MemberData(nameof(Eols))]
    public void The_stale_codex_rollback_refusal_is_read_with_its_reason_and_next_step_and_no_home_folder(string eol)
    {
        var facts = InstallTranscript.Parse(RefusedRollback(eol));

        var refusal = Assert.IsType<InstallerRefusal>(facts.Refusal);
        Assert.Equal(InstallerRefusalKind.StaleAgentRollback, refusal.Kind);
        Assert.Equal("Put that version back and run 'defenseclaw rollback' again, or stay on DefenseClaw 1.0.5", refusal.NextStep);
        Assert.Contains("Nothing was changed.", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains(@"~\AppData\Local\Programs\codex\bin\codex.exe changed", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(facts.PreviousRestored);
        Assert.Empty(facts.Notices);
    }

    [Theory]
    [InlineData("  x Rollback failed part-way; see the log")]
    [InlineData("  x Rollback cancelled; nothing was changed")]
    [InlineData("  x The gateway did not stop; nothing was changed")]
    [InlineData("  x DefenseClaw 1.0.4 does not start, so the rollback would stop half-way. Nothing was changed.")]
    public void Older_or_other_failure_lines_are_not_read_as_the_1_0_5_refusal(string line)
    {
        // The refusal is keyed on 1.0.5's exact sentence; 1.0.4 prints none of it, and a near miss is not it.
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Rollback", "--- Rolling back to DefenseClaw 1.0.3  [12:00:01Z]", line));

        Assert.Null(facts.Refusal);
        Assert.Equal(InstallOutcome.FailedRestored, InstallScriptPlan.MapExit(1, rollback: true, facts).Outcome);
    }

    [Fact]
    public void Exit_1_with_the_refusal_maps_to_refused_with_nothing_changed_and_only_exit_1_does()
    {
        var facts = InstallTranscript.Parse(RefusedRollback());

        var rollback = InstallScriptPlan.MapExit(1, rollback: true, facts);
        Assert.Equal(InstallOutcome.Refused, rollback.Outcome);
        Assert.Equal("Refused", rollback.Label);
        Assert.Equal("Warn", rollback.ToneKey);
        Assert.False(rollback.ChangedMachine);
        Assert.True(rollback.OffersRollback);
        Assert.Equal("The rollback was refused before it changed anything (exit 1). Nothing was changed.", rollback.Message);

        var upgrade = InstallScriptPlan.MapExit(1, rollback: false, facts);
        Assert.Equal(InstallOutcome.Refused, upgrade.Outcome);
        Assert.False(upgrade.OffersRollback);

        Assert.Equal(InstallOutcome.Success, InstallScriptPlan.MapExit(0, rollback: true, facts).Outcome);
        Assert.Equal(InstallOutcome.Failed, InstallScriptPlan.MapExit(5, rollback: true, facts).Outcome);
        Assert.Equal(InstallScriptPlan.MapExit(1, rollback: true), InstallScriptPlan.MapExit(1, rollback: true, InstallTranscriptFacts.Empty));
        Assert.Equal(InstallScriptPlan.MapExit(1, rollback: true), InstallScriptPlan.MapExit(1, rollback: true, null));
    }

    [Fact]
    public void A_refused_rollback_is_an_installer_refusal_that_changed_nothing_whatever_the_machine_says()
    {
        var verdict = UpgradeVerdicts.Classify(Run(1, rollback: true, InstallTranscript.Parse(RefusedRollback())), Facts(running: true), null);

        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);
        Assert.Equal("Rollback refused; nothing changed", verdict.Label);
        Assert.Equal("Warn", verdict.ToneKey);
        Assert.Contains("nothing on this machine was changed", verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("did not finish", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_upgrade_is_refused_too()
    {
        var verdict = UpgradeVerdicts.Classify(Run(1, rollback: false, InstallTranscript.Parse(RefusedRollback())), null, "1.0.5");

        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);
        Assert.Equal("Refused; nothing changed", verdict.Label);
        Assert.Contains("nothing on this machine was changed", verdict.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exit_1_with_no_install_log_says_the_installer_stopped_before_its_log_and_points_at_its_console(bool rollback)
    {
        // install.ps1 takes the install lock before it starts its transcript (1.0.5 :5487-5536), so "another install or doctor run is taking over
        // the install lock" (:5522) is never in a log.
        var verdict = UpgradeVerdicts.Classify(Run(1, rollback, InstallTranscriptFacts.Empty, log: null), Facts(running: true), rollback ? null : "1.0.5");

        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);
        Assert.Contains("No install log was found for this run", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("taking over the install lock", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("console window", verdict.Summary, StringComparison.Ordinal);

        // With a log, the old sentence stands.
        var logged = UpgradeVerdicts.Classify(Run(1, rollback, InstallTranscriptFacts.Empty), Facts(running: true), rollback ? null : "1.0.5");
        Assert.Contains("install log before doing anything else", logged.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_install_row_says_refused_and_keeps_it_over_the_exit_codes_restored()
    {
        using var services = TestServices.Create(_temp);
        var run = services.Cli.BeginExternalRun("powershell.exe", ["-NoProfile", "-File", @"C:\staging\install.ps1", "-Rollback"], "note");
        services.Cli.CompleteExternalRun(run, 1, null, null);
        var started = run.StartedAt.LocalDateTime;
        _ = Directory.CreateDirectory(_temp.File("logs"));
        _ = _temp.WriteFile(
            Path.Combine("logs", $"install-{started:yyyyMMdd'T'HHmmss}.log"),
            RefusedRollback().Replace("Start time: 20261030120000", $"Start time: {started:yyyyMMddHHmmss}", StringComparison.Ordinal));

        var summary = InstallRunReader.Read(_temp.Path, services.Cli.Activity);

        Assert.NotNull(summary);
        Assert.Equal(InstallRunKind.Rollback, summary!.Kind);
        Assert.Equal(InstallRunOutcome.Refused, summary.Outcome);
        Assert.Equal("Warn", InstallRunReader.ToneKey(summary.Outcome));
        var text = InstallRunReader.Describe(summary, DateTimeOffset.Now);
        Assert.Contains("refused; nothing was changed", text, StringComparison.Ordinal);
        Assert.Contains("Installer said: Codex was updated after DefenseClaw set up Codex 0.46.0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("restored", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the notices

    [Theory]
    [MemberData(nameof(Eols))]
    public void The_outage_notice_after_upgraded_from_is_read_in_the_installers_words(string eol)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, "-Version 1.0.5",
            "  DefenseClaw 1.0.5 is installed.",
            "  Upgraded from 1.0.3. Undo with: defenseclaw rollback",
            OutageNotice,
            string.Empty));

        var notice = Assert.Single(facts.Notices);
        Assert.Equal(InstallerNoticeKind.OutageOpen, notice.Kind);
        Assert.Equal("Gateway outages", notice.Title);
        Assert.Equal(OutageNotice.Trim(), notice.Text);
        Assert.False(facts.GuardrailOff);
    }

    [Fact]
    public void The_outage_sentence_without_an_upgraded_from_line_before_it_is_not_the_notice()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Version 1.0.5", "  DefenseClaw 1.0.5 is installed.", OutageNotice));

        Assert.Empty(facts.Notices);
    }

    [Theory]
    [MemberData(nameof(Eols))]
    public void Protection_off_in_the_kept_config_is_a_notice_with_its_command_and_marks_the_gateway_as_left_stopped(string eol)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, "-Version 1.0.5",
            "  DefenseClaw 1.0.5 is installed.",
            "  Upgraded from 1.0.4. Undo with: defenseclaw rollback",
            "  ! Protection is off in the kept config (guardrail.enabled = false), so agent hooks are not guarded",
            "  Turn it back on with: defenseclaw setup guardrail"));

        Assert.True(facts.GuardrailOff);
        Assert.False(facts.GatewayLeftStopped);
        var notice = Assert.Single(facts.Notices);
        Assert.Equal(InstallerNoticeKind.ProtectionOff, notice.Kind);
        Assert.Equal(
            "Protection is off in the kept config (guardrail.enabled = false), so agent hooks are not guarded. Turn it back on with: defenseclaw setup guardrail",
            notice.Text);

        // The verdict: installed, the gateway left stopped on purpose, not "gateway not running" as a failure.
        var verdict = UpgradeVerdicts.Classify(Run(0, rollback: false, facts), Facts(running: false), "1.0.5");
        Assert.Equal(UpgradeResultState.NeedsAttention, verdict.State);
        Assert.Equal("Installed; protection is off", verdict.Label);
        Assert.Equal("Warn", verdict.ToneKey);
        Assert.Contains("defenseclaw setup guardrail", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rollback_to_1_0_3_after_a_codex_update_notes_that_codex_hooks_need_setup_without_the_home_folder()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", "-Rollback",
            "--- Rolling back to DefenseClaw 1.0.3  [12:00:01Z]",
            "  ! Codex was updated after DefenseClaw set up Codex 0.46.0 (" + CodexExe + " changed). DefenseClaw 1.0.3 starts without Codex hooks until you run " +
            "'defenseclaw setup codex' after the rollback",
            "  + Now running DefenseClaw 1.0.3. Run 'defenseclaw rollback' again to return to 1.0.5."));

        var notice = Assert.Single(facts.Notices);
        Assert.Equal(InstallerNoticeKind.CodexNeedsSetup, notice.Kind);
        Assert.Equal("Codex hooks", notice.Title);
        Assert.EndsWith("DefenseClaw 1.0.3 starts without Codex hooks until you run 'defenseclaw setup codex' after the rollback.", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", notice.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(facts.Refusal);
    }

    // ------------------------------------------------------------------ the rollback pre-flight

    private static readonly StaleAgentProgram Updated = new("codex", "Codex 0.46.0", CodexExe, Missing: false);

    [Fact]
    public void A_1_0_5_installer_rolling_back_to_1_0_4_says_it_selects_the_updated_codex_or_refuses_with_nothing_changed()
    {
        var note = RollbackAgentPreflight.Note("1.0.5", "1.0.5", "1.0.4", [Updated]);

        Assert.Equal(
            "Codex was updated after DefenseClaw 1.0.4 set up Codex 0.46.0. Before it changes anything, the installer selects the updated Codex for DefenseClaw 1.0.4, " +
            "as 'defenseclaw setup codex' would; if it cannot, it refuses the rollback and nothing is changed.",
            note);
    }

    [Theory]
    [InlineData("1.0.3")]
    [InlineData("1.0.0")]
    [InlineData("0.8.10")]
    public void Rolling_back_to_a_release_before_1_0_4_says_codex_hooks_need_setup_afterwards(string backTo)
    {
        var note = RollbackAgentPreflight.Note("1.0.5", "1.0.5", backTo, [Updated with { Missing = true, Label = "Codex" }]);

        Assert.Equal(
            $"Codex, which DefenseClaw {backTo} set up, is no longer where it was. The rollback finishes, but DefenseClaw {backTo} starts without Codex hooks " +
            "until you run 'defenseclaw setup codex' after it.",
            note);
    }

    [Theory]
    [InlineData("1.0.4", "1.0.4", "1.0.3")]
    [InlineData("1.0.5", "1.0.4", "1.0.5")]
    [InlineData(null, "1.0.5", "1.0.4")]
    [InlineData("1.0.5", "1.0.5", null)]
    public void An_older_installer_a_roll_forward_or_an_unknown_target_says_nothing(string? installer, string? installed, string? backTo)
    {
        Assert.Null(RollbackAgentPreflight.Note(installer, installed, backTo, [Updated]));
    }

    [Fact]
    public void Nothing_stale_says_nothing_and_read_finds_nothing_without_previous_data()
    {
        Assert.Null(RollbackAgentPreflight.Note("1.0.5", "1.0.5", "1.0.4", []));
        Assert.Null(RollbackAgentPreflight.Read(_temp.Path, "1.0.5", "1.0.5", DateTimeOffset.Now));
    }
}
