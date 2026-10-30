using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// The post-flight's verdict (CUST-359, the 1.0.4 addendum): not "the installer exited 0" and not "/health said 200", but the gateway that answers is
/// the verified one with the new version, its events reach audit.db, and telemetry reports no failure; and where the installed bytes are known to
/// come from. The result card tells apart an installer failure, a rollback, a gateway that did not start, degraded audit or telemetry, and success.
/// Everything here is derived from what the run returned and what the post-flight read; nothing is run.
/// </summary>
public sealed partial class InstallScriptUpgradeViewModel
{
    // The run this window last saw end: what the verdict is about. Null before the first run in this window.
    private InstallScriptRunResult? _lastRun;

    /// <summary>The verdict state, or null before a run in this window.</summary>
    [ObservableProperty]
    private UpgradeResultState? _verdictState;

    [ObservableProperty]
    private string _verdictLabel = string.Empty;

    [ObservableProperty]
    private string _verdictToneKey = "Neutral";

    [ObservableProperty]
    private string _verdictSummary = string.Empty;

    [ObservableProperty]
    private bool _hasVerdict;

    [ObservableProperty]
    private string _telemetryText = string.Empty;

    [ObservableProperty]
    private string _telemetryToneKey = "Neutral";

    [ObservableProperty]
    private string _auditText = string.Empty;

    [ObservableProperty]
    private string _auditToneKey = "Neutral";

    [ObservableProperty]
    private string _releaseText = string.Empty;

    [ObservableProperty]
    private string _releaseToneKey = "Neutral";

    /// <summary>Sets the rows and the verdict from one post-flight read. <paramref name="state"/> is the install state read in the same pass.</summary>
    private void ApplyChecks(PostFlightFacts facts, InstallStateSnapshot state)
    {
        GatewayText = DescribeGateway(facts);
        GatewayToneKey = facts.GatewayRunning switch
        {
            true when facts.GatewayOwnerVerified == true => "Ok",
            true => "Warn",
            false => "Bad",
            _ => "Neutral",
        };

        TelemetryText = facts.Telemetry.Text;
        TelemetryToneKey = facts.Telemetry.ToneKey;
        AuditText = facts.Audit.Text;
        AuditToneKey = facts.Audit.State switch
        {
            AuditPersistenceState.Persisted => "Ok",
            AuditPersistenceState.NotChecked => "Neutral",
            _ => "Warn",
        };

        // The transcript of the run this window started, else the one the Last install row describes.
        var transcript = _lastRun is { Started: true } run ? run.Transcript : state.LastRun?.Transcript ?? InstallTranscriptFacts.Empty;
        var release = ReleaseIdentity.Of(transcript, _lastRun is { Started: true } ? _verification : PreflightVerification.NotStarted, facts.CliVersion, facts.GatewayVersion);
        ReleaseText = release.Text;
        ReleaseToneKey = release.ToneKey;
        ApplyNotices(transcript);

        if (HasResult && _lastRun is { } last)
        {
            var verdict = UpgradeVerdicts.Classify(last, facts, last.Rollback ? null : InstallVersion);
            VerdictState = verdict.State;
            VerdictLabel = verdict.Label;
            VerdictToneKey = verdict.ToneKey;
            VerdictSummary = verdict.Summary;
            HasVerdict = true;
        }
    }

    /// <summary>"Running; owner verified; 1.0.4; started 14:03", or why the gateway's answer is not trusted.</summary>
    private static string DescribeGateway(PostFlightFacts facts)
    {
        if (facts.GatewayRunning != true)
        {
            return facts.GatewayText;
        }

        if (facts.GatewayOwnerVerified != true)
        {
            return facts.GatewayText + "; the port owner did not verify as this install's gateway, so its health is not trusted";
        }

        var parts = new List<string> { facts.GatewayText, "owner verified" };
        if (facts.GatewayVersion is { Length: > 0 } version)
        {
            parts.Add(version);
        }

        if (facts.GatewayStartedAt is { } started)
        {
            parts.Add("started " + started.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        return string.Join("; ", parts);
    }

    /// <summary>Forgets the verdict of an earlier run (a new run is starting).</summary>
    private void ClearVerdict()
    {
        HasVerdict = false;
        VerdictState = null;
        VerdictLabel = string.Empty;
        VerdictToneKey = "Neutral";
        VerdictSummary = string.Empty;
    }
}
