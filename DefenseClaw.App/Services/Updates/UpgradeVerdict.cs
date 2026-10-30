using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.App.ViewModels.Updates;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Services.Updates;

/// <summary>What the gateway's <c>/health</c> <c>telemetry</c> block says about its event history (1.0.4: <c>renderObservabilityV8Health</c>).</summary>
public enum TelemetryCondition
{
    /// <summary>No telemetry block: the gateway is not answering, or this runtime does not report one.</summary>
    NotReported,

    /// <summary><c>running</c>, and no optional destination is failing.</summary>
    Healthy,

    /// <summary><c>running</c>, with <c>optional_destination_state: degraded</c>: an exporter (OTLP, Splunk...) is failing; the local history is not.</summary>
    OptionalDegraded,

    /// <summary><c>error</c>: the local SQLite history, its retention, its loss journal or the health snapshot is failing.</summary>
    LocalHistoryFailing,

    /// <summary><c>disabled</c> or <c>stopped</c>.</summary>
    Off,

    /// <summary><c>starting</c> or <c>reconnecting</c>, or a state this app does not know.</summary>
    Transitional,
}

/// <summary>The telemetry verdict and its sentence. Built from closed tokens only: a free-text <c>last_error</c> is never shown.</summary>
public sealed partial record TelemetryFacts(TelemetryCondition Condition, string Text)
{
    public static TelemetryFacts NotReported { get; } = new(TelemetryCondition.NotReported, "Not reported (the gateway is not answering, or this runtime's /health has no telemetry block).");

    /// <summary>True for an exporter failing or a local history failing: the result card's audit/telemetry-degraded state.</summary>
    public bool IsDegraded => Condition is TelemetryCondition.OptionalDegraded or TelemetryCondition.LocalHistoryFailing;

    public string ToneKey => Condition switch
    {
        TelemetryCondition.Healthy => "Ok",
        TelemetryCondition.OptionalDegraded or TelemetryCondition.Off => "Warn",
        TelemetryCondition.LocalHistoryFailing => "Bad",
        _ => "Neutral",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PlainToken();

    /// <summary>The verdict for <paramref name="health"/> (null: not reported).</summary>
    public static TelemetryFacts From(GatewayHealth? health)
    {
        if (health?.Telemetry is not { } telemetry)
        {
            return NotReported;
        }

        var details = telemetry.Details is { ValueKind: JsonValueKind.Object } d ? d : (JsonElement?)null;
        var state = Token(telemetry.State);
        switch (state)
        {
            case "running":
                if (string.Equals(Token(details, "optional_destination_state"), "degraded", StringComparison.Ordinal))
                {
                    var count = Count(details, "optional_destination_failure_count");
                    return new TelemetryFacts(
                        TelemetryCondition.OptionalDegraded,
                        "Running, but " + (count is { } n ? n.ToString(CultureInfo.InvariantCulture) + " optional destination(s) are" : "an optional destination is") +
                        " failing (an exporter, not the local audit history). The Overview's Observability card names them.");
                }

                return new TelemetryFacts(TelemetryCondition.Healthy, "Running; no destination is failing.");

            case "error":
                var reasons = new List<string>();
                if (Token(details, "event_history_failure") is { Length: > 0 } history)
                {
                    reasons.Add("event history: " + history);
                }

                if (details is { } block && DestinationHealthReader.ReadRetention(block) is { NeedsAttention: true } retention)
                {
                    reasons.Add("retention: " + retention.ControllerLabel);
                }

                if (Token(details, "local_write_loss_journal") is { Length: > 0 } journal)
                {
                    reasons.Add("loss journal: " + journal);
                }

                if (string.Equals(Token(details, "snapshot_state"), "unavailable", StringComparison.Ordinal))
                {
                    reasons.Add("health snapshot unavailable");
                }

                return new TelemetryFacts(
                    TelemetryCondition.LocalHistoryFailing,
                    "Error: the gateway reports that its local event history is not being kept as it should" +
                    (reasons.Count > 0 ? " (" + string.Join("; ", reasons) + ")" : string.Empty) +
                    ". That is the gateway's own report about writing, not a finding about the database file.");

            case "disabled":
            case "stopped":
                return new TelemetryFacts(TelemetryCondition.Off, "Telemetry is " + state + ".");

            default:
                return new TelemetryFacts(TelemetryCondition.Transitional, state.Length > 0 ? "Telemetry is " + state + "; Refresh in a moment." : "Telemetry state not readable.");
        }
    }

    private static string Token(string? value)
    {
        var token = (value ?? string.Empty).Trim().ToLowerInvariant();
        return PlainToken().IsMatch(token) ? token : string.Empty;
    }

    private static string Token(JsonElement? parent, string member) =>
        parent is { } element && element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? Token(value.GetString()) : string.Empty;

    private static long? Count(JsonElement? parent, string member) =>
        parent is { } element && element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;
}

/// <summary>Where the installed bytes are known to come from (CUST-359). A version number alone never makes a build official.</summary>
public enum ReleaseIdentityKind
{
    /// <summary>The installer verified the release's sigstore signature on checksums.txt and every asset against it.</summary>
    OfficialSigned,

    /// <summary>The installer checked every asset against the release's checksums.txt; the signature was not verified (no cosign).</summary>
    OfficialChecksums,

    /// <summary>The assets came from a local folder (<c>-Local</c>).</summary>
    LocalAssets,

    /// <summary>The runtime reports a version that is not a release number (<c>1.0.4.dev3+g12ab</c>, <c>-dirty</c>...).</summary>
    DeveloperBuild,

    /// <summary>No record of how the bytes were checked.</summary>
    NotVerified,
}

/// <summary>The release identity and its sentence.</summary>
public sealed record ReleaseIdentity(ReleaseIdentityKind Kind, string Text)
{
    public string ToneKey => Kind switch
    {
        ReleaseIdentityKind.OfficialSigned => "Ok",
        ReleaseIdentityKind.LocalAssets or ReleaseIdentityKind.DeveloperBuild => "Warn",
        _ => "Neutral",
    };

    /// <summary>
    /// From the run's transcript (<see cref="InstallTranscriptFacts.AssetCheck"/>, <see cref="InstallTranscriptFacts.LocalAssets"/>), what this app verified
    /// of install.ps1 itself (<paramref name="staged"/>) and the versions the CLI and the gateway report.
    /// </summary>
    public static ReleaseIdentity Of(InstallTranscriptFacts transcript, PreflightVerification staged, string? cliVersion, string? gatewayVersion)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var versions = new[] { cliVersion, gatewayVersion }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var odd = versions.FirstOrDefault(v => !InstallScriptPlan.TryNormalizeVersion(v, out _));
        if (odd is not null)
        {
            return new ReleaseIdentity(
                ReleaseIdentityKind.DeveloperBuild,
                $"Developer or locally modified build: the runtime reports {odd}, which is not a release number. Not an official release, whatever the install log says.");
        }

        var shown = versions.Length switch
        {
            0 => "this install",
            1 => versions[0],
            _ => string.Join(" (CLI) and ", versions) + " (gateway)",
        };

        if (transcript.LocalAssets)
        {
            return new ReleaseIdentity(
                ReleaseIdentityKind.LocalAssets,
                $"{shown} was installed from local release assets (-Local), not downloaded from the release page. The installer checked them against the " +
                "checksums.txt beside them, which proves they match that list, not that they are the official release.");
        }

        var script = staged switch
        {
            PreflightVerification.Verified => " This app verified install.ps1 the same way before running it.",
            PreflightVerification.HashOnly => " This app checked install.ps1 against checksums.txt (signature not verified) before running it.",
            _ => string.Empty,
        };

        return transcript.AssetCheck switch
        {
            TranscriptAssetCheck.SignatureVerified => new ReleaseIdentity(
                ReleaseIdentityKind.OfficialSigned,
                $"Official release {shown}: the installer verified the sigstore signature on the release's checksums.txt and every asset against it." + script),
            TranscriptAssetCheck.ChecksumsOnly => new ReleaseIdentity(
                ReleaseIdentityKind.OfficialChecksums,
                $"Release {shown}: every asset matched the release's checksums.txt, but its signature was not verified (cosign is not installed), so the signer is not proven." + script),
            _ => new ReleaseIdentity(
                ReleaseIdentityKind.NotVerified,
                $"Reports {shown}; the install log has no record of how the release bytes were checked, and a version number alone does not make a build official." + script),
        };
    }
}

/// <summary>How a run ended once the post-flight has looked (CUST-359): the outcomes the result card tells apart.</summary>
public enum UpgradeResultState
{
    /// <summary>Installed; the gateway answers as the verified gateway with the new version; its events reach audit.db; telemetry is healthy.</summary>
    Success,

    /// <summary>Installed, with a follow-up: exit 3 or 4, a version that does not match, or a gateway the installer left stopped on purpose.</summary>
    NeedsAttention,

    /// <summary>The installer refused, could not start, or failed without putting the previous install back.</summary>
    InstallerFailed,

    /// <summary>The previous install is live: the installer restored it after a failure, or the operator's rollback ran.</summary>
    RolledBack,

    /// <summary>The gateway did not come up healthy (the installer said so, or the post-flight cannot reach the verified gateway).</summary>
    GatewayStartFailed,

    /// <summary>Installed and the gateway is healthy, but its events are not seen in audit.db or telemetry reports a failure.</summary>
    AuditDegraded,
}

/// <summary>The result card's verdict: a state, a badge label and tone, and one paragraph.</summary>
public sealed record UpgradeVerdict(UpgradeResultState State, string Label, string ToneKey, string Summary);

/// <summary>
/// Pure: the verdict on one run from what the run returned and what the post-flight read. The summary says what the state means; the installer's
/// own reason and the exit code's sentence are the card's line above it (<c>ExitMessage</c>), so they are not repeated here.
/// </summary>
public static class UpgradeVerdicts
{
    public static UpgradeVerdict Classify(InstallScriptRunResult run, PostFlightFacts? facts, string? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(run);

        var transcript = run.Transcript;
        if (!run.Started)
        {
            return new UpgradeVerdict(UpgradeResultState.InstallerFailed, run.Exit.Label, run.Exit.ToneKey, "The installer did not run, so nothing on this machine was changed.");
        }

        if (run.Rollback)
        {
            if (run.Exit.Outcome == InstallOutcome.Refused)
            {
                return new UpgradeVerdict(UpgradeResultState.InstallerFailed, "Rollback refused; nothing changed", "Warn",
                    "install.ps1 -Rollback refused before it changed anything: nothing on this machine was changed, and the current install is still the live one. " +
                    "The installer's reason and next step are above.");
            }

            if (run.Exit.Outcome != InstallOutcome.Success)
            {
                return new UpgradeVerdict(UpgradeResultState.InstallerFailed, "Rollback failed", "Bad",
                    run.LogPath is null && run.ExitCode == 1
                        ? "install.ps1 -Rollback did not finish. " + InstallScriptPlan.NoLogNote
                        : "install.ps1 -Rollback did not finish. Read the install log before doing anything else.");
            }

            if (transcript.GatewayUnhealthy || (facts is not null && facts.GatewayRunning == false && !transcript.GatewayLeftStopped))
            {
                return new UpgradeVerdict(UpgradeResultState.GatewayStartFailed, "Rolled back; gateway not running", "Bad",
                    "The rollback put the previous install back, but its gateway is not running, so agent hooks are not guarded. Start the gateway.");
            }

            return new UpgradeVerdict(UpgradeResultState.RolledBack, "Rolled back", "Warn",
                "The rollback finished: the install the last upgrade replaced is live again. Running it again goes forward to the newer install.");
        }

        switch (run.Exit.Outcome)
        {
            case InstallOutcome.UsageError:
            case InstallOutcome.CouldNotStart:
                return new UpgradeVerdict(UpgradeResultState.InstallerFailed, run.Exit.Label, run.Exit.ToneKey,
                    "The installer refused before it changed anything; nothing on this machine was changed.");

            case InstallOutcome.Refused:
                return new UpgradeVerdict(UpgradeResultState.InstallerFailed, "Refused; nothing changed", "Warn",
                    "The installer refused before it changed anything; nothing on this machine was changed. The installer's reason and next step are above.");

            case InstallOutcome.FailedRestored:
            case InstallOutcome.Failed:
            case InstallOutcome.Unknown:
                if (transcript.GatewayUnhealthy)
                {
                    return new UpgradeVerdict(UpgradeResultState.GatewayStartFailed, "Gateway did not start", "Bad",
                        ("The new gateway did not become healthy, so the upgrade did not finish. " + RestoreSentence(transcript)).TrimEnd());
                }

                if (transcript.PreviousRestored && !transcript.RestoreIncomplete)
                {
                    return new UpgradeVerdict(UpgradeResultState.RolledBack, "Failed; previous install restored", "Warn",
                        "The upgrade failed and the installer put the previous install back: the earlier version is live again.");
                }

                return new UpgradeVerdict(UpgradeResultState.InstallerFailed, transcript.RestoreIncomplete ? "Failed; restore incomplete" : "Failed", "Bad",
                    transcript.RestoreIncomplete
                        ? "The upgrade failed and the installer says the previous install is not fully back; run the installer again to finish restoring it."
                        : run.LogPath is null && run.ExitCode == 1
                            ? "The installer failed. " + InstallScriptPlan.NoLogNote
                            : "The installer failed, and its log does not say that it put the previous install back. Read the install log before doing anything else.");
        }

        // Installed (exit 0, 3 or 4): now the machine decides.
        if (facts is null)
        {
            return new UpgradeVerdict(UpgradeResultState.NeedsAttention, "Installed; not checked yet", "Warn",
                "The installer reports success; the checks below have not read the machine yet.");
        }

        if (facts.GatewayRunning != true)
        {
            // The guardrail is off in the kept config: the installer left the gateway stopped and says so in its own words (install.ps1:5930-5934).
            if (transcript.GuardrailOff)
            {
                return new UpgradeVerdict(UpgradeResultState.NeedsAttention, "Installed; protection is off", "Warn",
                    "Installed. Protection is off in the kept config (guardrail.enabled = false), so the installer did not start the gateway and agent hooks " +
                    "are not guarded. Turn it back on with: defenseclaw setup guardrail");
            }

            return transcript.GatewayLeftStopped
                ? new UpgradeVerdict(UpgradeResultState.NeedsAttention, "Installed; gateway not started", "Warn",
                    "Installed. The gateway was not running before the upgrade, so the installer did not start it; agent hooks are not guarded until it runs.")
                : new UpgradeVerdict(UpgradeResultState.GatewayStartFailed, "Installed; gateway not running", "Bad",
                    "The installer reports success, but the gateway is not answering (" + facts.GatewayText + "). Start it, then check again.");
        }

        if (facts.GatewayOwnerVerified != true)
        {
            return new UpgradeVerdict(UpgradeResultState.GatewayStartFailed, "Installed; gateway not verified", "Bad",
                "Something answers on the gateway's port, but its owner did not verify as this install's gateway, so its health and version are not trusted. " +
                "A 200 from /health alone is not proof the new gateway is the one running.");
        }

        var mismatches = new List<string>();
        if (InstallScriptPlan.TryNormalizeVersion(expectedVersion, out var expected))
        {
            if (facts.CliVersion is { Length: > 0 } cli && !SameVersion(cli, expected))
            {
                mismatches.Add($"the CLI reports {cli}");
            }

            if (facts.GatewayVersion is { Length: > 0 } gateway && !SameVersion(gateway, expected))
            {
                mismatches.Add($"the gateway reports {gateway}");
            }
        }

        if (facts.Telemetry.IsDegraded || facts.Audit.IsDegraded)
        {
            var what = facts.Telemetry.IsDegraded && facts.Audit.IsDegraded ? "audit and telemetry" : facts.Audit.IsDegraded ? "audit" : "telemetry";
            return new UpgradeVerdict(UpgradeResultState.AuditDegraded, "Installed; " + what + " degraded", "Warn",
                "Installed, and the verified gateway answers, but " +
                (facts.Audit.IsDegraded ? "its events were not seen in audit.db" : "it reports a telemetry failure") +
                (facts.Telemetry.IsDegraded && facts.Audit.IsDegraded ? ", and it reports a telemetry failure" : string.Empty) +
                ". The Audit and Telemetry rows below say what was found." +
                (mismatches.Count > 0 ? " Also: " + string.Join(", ", mismatches) + $", not {expected}." : string.Empty));
        }

        if (mismatches.Count > 0)
        {
            return new UpgradeVerdict(UpgradeResultState.NeedsAttention, "Installed; version differs", "Warn",
                $"The installer reports success, but {string.Join(" and ", mismatches)}, not {expected}. Open a new terminal or restart this app so the updated PATH is read, then check again.");
        }

        if (run.Exit.Outcome == InstallOutcome.InstalledNeedsAttention)
        {
            return new UpgradeVerdict(UpgradeResultState.NeedsAttention, run.Exit.Label, "Warn", run.Exit.Message);
        }

        return new UpgradeVerdict(UpgradeResultState.Success, "Installed and verified", "Ok",
            $"DefenseClaw {(expected.Length > 0 ? expected : facts.GatewayVersion ?? string.Empty)} is installed: the verified gateway answers with that version, " +
            "its events reach audit.db, and telemetry reports no failure.");
    }

    private static string RestoreSentence(InstallTranscriptFacts transcript) =>
        transcript.RestoreIncomplete ? "The installer says the previous install is not fully back; run the installer again to finish restoring it. "
        : transcript.PreviousRestored ? "The installer put the previous install back. "
        : string.Empty;

    private static bool SameVersion(string reported, string expected) =>
        InstallScriptPlan.TryNormalizeVersion(reported, out var normalized) && string.Equals(normalized, expected, StringComparison.Ordinal);
}
