using System.Text.Json;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-359 (1.0.4 addendum): the result card's verdict once the post-flight has looked - installer failure, rollback, gateway start failure,
/// audit or telemetry degraded, and success - from the run's exit, its transcript facts and what the machine says. Plus the telemetry and
/// release-identity readings it rests on. Pure; synthetic values throughout.
/// </summary>
public sealed class UpgradeVerdictTests
{
    private static readonly AuditPersistence Persisted = new(AuditPersistenceState.Persisted, "The gateway's start was recorded in audit.db.");

    private static InstallScriptRunResult Run(int? exit, bool rollback = false, InstallTranscriptFacts? transcript = null, string? reason = null) => new()
    {
        Started = true,
        ExitCode = exit,
        Rollback = rollback,
        Exit = InstallScriptPlan.MapExit(exit, rollback),
        Transcript = transcript ?? InstallTranscriptFacts.Empty,
        InstallerReason = reason,
    };

    private static PostFlightFacts Facts(
        bool running = true,
        bool? verified = true,
        string version = "1.0.4",
        TelemetryFacts? telemetry = null,
        AuditPersistence? audit = null) =>
        new(@"C:\Users\operator\.local\bin\defenseclaw.exe", true, "defenseclaw-cli " + version, "Policy model", running, running ? "Running" : "Gateway stopped")
        {
            GatewayOwnerVerified = running ? verified : null,
            GatewayVersion = verified == true ? version : null,
            CliVersion = version,
            Telemetry = telemetry ?? new TelemetryFacts(TelemetryCondition.Healthy, "Running; no destination is failing."),
            Audit = audit ?? Persisted,
        };

    private static UpgradeVerdict Classify(InstallScriptRunResult run, PostFlightFacts? facts) => UpgradeVerdicts.Classify(run, facts, run.Rollback ? null : "1.0.4");

    // ------------------------------------------------------------------ success

    [Fact]
    public void Installed_with_the_verified_gateway_its_events_in_audit_db_and_healthy_telemetry_is_success()
    {
        var verdict = Classify(Run(0), Facts());

        Assert.Equal(UpgradeResultState.Success, verdict.State);
        Assert.Equal("Ok", verdict.ToneKey);
        Assert.Contains("1.0.4", verdict.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ installer failure

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(null)]
    public void A_refusal_or_a_failure_without_a_restore_is_an_installer_failure(int? exit)
    {
        var verdict = Classify(Run(exit, reason: "Could not get checksums.txt for 1.0.4"), Facts());

        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);

        // The reason is the card's line above the verdict; the verdict does not repeat it.
        Assert.DoesNotContain("Could not get checksums.txt", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains(exit == 2 ? "nothing on this machine was changed" : "Read the install log", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installer_that_never_started_is_an_installer_failure()
    {
        var run = new InstallScriptRunResult
        {
            FailureReason = "The staged install.ps1 changed on disk.",
            Exit = new InstallExitInfo(InstallOutcome.CouldNotStart, "Not started", "Warn", "Nothing was run.", false, false),
        };

        Assert.Equal(UpgradeResultState.InstallerFailed, Classify(run, null).State);
    }

    [Fact]
    public void A_restore_the_installer_could_not_prove_is_an_installer_failure_that_says_so()
    {
        var verdict = Classify(Run(1, transcript: new InstallTranscriptFacts { RestoreIncomplete = true }), Facts());

        Assert.Equal(UpgradeResultState.InstallerFailed, verdict.State);
        Assert.Contains("not fully back", verdict.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ rollback

    [Fact]
    public void A_failure_the_installer_restored_is_a_rollback()
    {
        var verdict = Classify(Run(1, transcript: new InstallTranscriptFacts { PreviousRestored = true }), Facts(version: "1.0.3"));

        Assert.Equal(UpgradeResultState.RolledBack, verdict.State);
        Assert.Equal("Failed; previous install restored", verdict.Label);
    }

    [Fact]
    public void The_operators_rollback_that_finished_is_a_rollback_and_one_that_failed_is_an_installer_failure()
    {
        Assert.Equal(UpgradeResultState.RolledBack, Classify(Run(0, rollback: true), Facts(version: "1.0.3")).State);
        Assert.Equal(UpgradeResultState.InstallerFailed, Classify(Run(1, rollback: true), Facts(version: "1.0.3")).State);
    }

    // ------------------------------------------------------------------ gateway start failure

    [Fact]
    public void A_gateway_that_did_not_become_healthy_is_a_gateway_start_failure_even_though_the_installer_restored()
    {
        var verdict = Classify(Run(1, transcript: new InstallTranscriptFacts { GatewayUnhealthy = true, PreviousRestored = true }), Facts(version: "1.0.3"));

        Assert.Equal(UpgradeResultState.GatewayStartFailed, verdict.State);
        Assert.Contains("put the previous install back", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_0_with_no_gateway_answering_is_a_gateway_start_failure()
    {
        Assert.Equal(UpgradeResultState.GatewayStartFailed, Classify(Run(0), Facts(running: false)).State);
    }

    [Fact]
    public void A_200_from_a_port_owner_that_did_not_verify_is_not_success()
    {
        var verdict = Classify(Run(0), Facts(verified: false));

        Assert.Equal(UpgradeResultState.GatewayStartFailed, verdict.State);
        Assert.Contains("not trusted", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rollback_whose_gateway_is_down_is_a_gateway_start_failure()
    {
        Assert.Equal(UpgradeResultState.GatewayStartFailed, Classify(Run(0, rollback: true), Facts(running: false, version: "1.0.3")).State);
    }

    // ------------------------------------------------------------------ audit / telemetry degraded

    [Theory]
    [InlineData(AuditPersistenceState.NoNewRow)]
    [InlineData(AuditPersistenceState.Busy)]
    [InlineData(AuditPersistenceState.Missing)]
    [InlineData(AuditPersistenceState.Unreadable)]
    public void A_healthy_gateway_whose_events_are_not_seen_in_audit_db_is_audit_degraded(AuditPersistenceState state)
    {
        var verdict = Classify(Run(0), Facts(audit: new AuditPersistence(state, "synthetic")));

        Assert.Equal(UpgradeResultState.AuditDegraded, verdict.State);
        Assert.Equal("Installed; audit degraded", verdict.Label);
        Assert.DoesNotContain("corrupt", verdict.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TelemetryCondition.LocalHistoryFailing)]
    [InlineData(TelemetryCondition.OptionalDegraded)]
    public void A_telemetry_failure_is_degraded_too(TelemetryCondition condition)
    {
        var verdict = Classify(Run(0), Facts(telemetry: new TelemetryFacts(condition, "synthetic")));

        Assert.Equal(UpgradeResultState.AuditDegraded, verdict.State);
        Assert.Equal("Installed; telemetry degraded", verdict.Label);
    }

    // ------------------------------------------------------------------ installed, needs attention

    [Fact]
    public void A_gateway_the_installer_left_stopped_on_purpose_needs_attention_not_a_start_failure()
    {
        var verdict = Classify(Run(0, transcript: new InstallTranscriptFacts { GatewayLeftStopped = true }), Facts(running: false));

        Assert.Equal(UpgradeResultState.NeedsAttention, verdict.State);
        Assert.Contains("did not start it", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cli_that_still_reports_the_old_version_needs_attention()
    {
        var facts = Facts() with { CliVersion = "1.0.3" };

        var verdict = Classify(Run(0), facts);

        Assert.Equal(UpgradeResultState.NeedsAttention, verdict.State);
        Assert.Contains("the CLI reports 1.0.3", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_3_with_everything_healthy_needs_attention()
    {
        Assert.Equal(UpgradeResultState.NeedsAttention, Classify(Run(3), Facts()).State);
    }

    [Fact]
    public void Before_the_post_flight_has_read_an_installed_run_is_not_called_success()
    {
        Assert.Equal(UpgradeResultState.NeedsAttention, Classify(Run(0), null).State);
    }

    // ------------------------------------------------------------------ telemetry, from /health (1.0.4 renderObservabilityV8Health)

    private static GatewayHealth Health(string telemetry) =>
        JsonSerializer.Deserialize<GatewayHealth>("{\"telemetry\":" + telemetry + "}")!;

    [Fact]
    public void Telemetry_running_with_no_failing_destination_is_healthy()
    {
        var facts = TelemetryFacts.From(Health("{\"state\":\"running\",\"details\":{\"optional_destination_state\":\"healthy\",\"retention_state\":\"healthy\"}}"));

        Assert.Equal(TelemetryCondition.Healthy, facts.Condition);
        Assert.False(facts.IsDegraded);
    }

    [Fact]
    public void A_failing_exporter_is_optional_degraded_with_its_count()
    {
        var facts = TelemetryFacts.From(Health("{\"state\":\"running\",\"details\":{\"optional_destination_state\":\"degraded\",\"optional_destination_failure_count\":2}}"));

        Assert.Equal(TelemetryCondition.OptionalDegraded, facts.Condition);
        Assert.Contains("2 optional destination(s)", facts.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_names_the_gateways_own_codes_and_never_calls_it_corruption()
    {
        var facts = TelemetryFacts.From(Health(
            "{\"state\":\"error\",\"last_error\":\"Post https://operator:synthpass@collector.example.test failed\",\"details\":" +
            "{\"retention_state\":\"degraded\",\"retention_failure\":\"sqlite_busy\",\"event_history_failure\":\"sqlite_write_failed\"}}"));

        Assert.Equal(TelemetryCondition.LocalHistoryFailing, facts.Condition);
        Assert.Contains("event history: sqlite_write_failed", facts.Text, StringComparison.Ordinal);
        Assert.Contains("retention: degraded (sqlite_busy)", facts.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("corrupt", facts.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthpass", facts.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_telemetry_block_is_not_reported_and_off_is_off()
    {
        Assert.Equal(TelemetryCondition.NotReported, TelemetryFacts.From(null).Condition);
        Assert.Equal(TelemetryCondition.NotReported, TelemetryFacts.From(new GatewayHealth()).Condition);
        Assert.Equal(TelemetryCondition.Off, TelemetryFacts.From(Health("{\"state\":\"disabled\"}")).Condition);
    }

    // ------------------------------------------------------------------ release identity

    [Fact]
    public void A_signature_the_installer_verified_is_the_official_release()
    {
        var release = ReleaseIdentity.Of(new InstallTranscriptFacts { AssetCheck = TranscriptAssetCheck.SignatureVerified }, PreflightVerification.Verified, "1.0.4", "1.0.4");

        Assert.Equal(ReleaseIdentityKind.OfficialSigned, release.Kind);
        Assert.Contains("Official release 1.0.4", release.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Checksums_without_a_signature_are_not_called_signed()
    {
        var release = ReleaseIdentity.Of(new InstallTranscriptFacts { AssetCheck = TranscriptAssetCheck.ChecksumsOnly }, PreflightVerification.HashOnly, "1.0.4", null);

        Assert.Equal(ReleaseIdentityKind.OfficialChecksums, release.Kind);
        Assert.Contains("not verified", release.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_assets_are_not_the_official_release_even_when_checksums_match()
    {
        var release = ReleaseIdentity.Of(
            new InstallTranscriptFacts { AssetCheck = TranscriptAssetCheck.ChecksumsOnly, LocalAssets = true }, PreflightVerification.Verified, "1.0.4", "1.0.4");

        Assert.Equal(ReleaseIdentityKind.LocalAssets, release.Kind);
        Assert.Equal("Warn", release.ToneKey);
    }

    [Theory]
    [InlineData("1.0.4.dev3+g12ab34c")]
    [InlineData("1.0.4-dirty")]
    public void A_version_that_is_not_a_release_number_is_a_developer_build_whatever_the_log_says(string version)
    {
        var release = ReleaseIdentity.Of(new InstallTranscriptFacts { AssetCheck = TranscriptAssetCheck.SignatureVerified }, PreflightVerification.Verified, version, "1.0.4");

        Assert.Equal(ReleaseIdentityKind.DeveloperBuild, release.Kind);
        Assert.Contains(version, release.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_number_alone_is_not_verified()
    {
        var release = ReleaseIdentity.Of(InstallTranscriptFacts.Empty, PreflightVerification.NotStarted, "1.0.4", "1.0.4");

        Assert.Equal(ReleaseIdentityKind.NotVerified, release.Kind);
        Assert.Contains("version number alone", release.Text, StringComparison.Ordinal);
    }
}
