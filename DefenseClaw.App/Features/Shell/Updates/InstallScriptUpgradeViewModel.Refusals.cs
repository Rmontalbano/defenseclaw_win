using System.Collections.ObjectModel;
using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// What install.ps1 1.0.5 says beyond its exit code (CUST-399): a refusal before it changed anything (exit 1, which alone reads as "did not finish,
/// restored"), the notices it prints after it finished (the gateway-outage notice, protection off, Codex hooks after a rollback), and what its rollback
/// will do about a Codex that changed since the older install set it up. All of it is read from the transcript and the files; nothing is run.
/// </summary>
public sealed partial class InstallScriptUpgradeViewModel
{
    // What the 1.0.5 installer's rollback will do about a changed Codex (RollbackAgentPreflight), from the last install-state read; null when nothing.
    private string? _rollbackAgentNote;

    /// <summary>The installer's notices from the run's transcript, as read-only rows under the verdict.</summary>
    public ObservableCollection<PreflightRow> InstallerNoticeRows { get; } = new();

    public bool HasInstallerNotices => InstallerNoticeRows.Count > 0;

    /// <summary>Shows <paramref name="transcript"/>'s notices; an empty transcript clears them.</summary>
    private void ApplyNotices(InstallTranscriptFacts transcript)
    {
        InstallerNoticeRows.Clear();
        foreach (var notice in transcript.Notices)
        {
            InstallerNoticeRows.Add(new PreflightRow("notice-" + notice.Kind.ToString().ToLowerInvariant(), notice.Title, PreflightStatus.Warn, notice.Text));
        }

        OnPropertyChanged(nameof(HasInstallerNotices));
    }

    /// <summary>The installer's own next step for a refusal, as the hint under the result line; null for any other run.</summary>
    private static string? RefusalHint(InstallScriptRunResult result) =>
        result.Exit.Outcome == InstallOutcome.Refused && result.Transcript.Refusal is { } refusal
            ? "Nothing was changed. The installer's next step: " + refusal.NextStep + "." +
              (result.Rollback ? " Roll back here runs the same installer rollback as 'defenseclaw rollback'." : string.Empty)
            : null;

    /// <summary>What the Roll back note says after a run in this window; null to keep the install state's own note.</summary>
    private static string? ResultRollbackNote(InstallScriptRunResult result) => result.Exit.Outcome switch
    {
        InstallOutcome.UsageError => "Nothing was changed, so there is nothing to roll back.",
        InstallOutcome.Refused when !result.Rollback => "Nothing was changed, so there is nothing to roll back.",
        InstallOutcome.Refused => "The rollback was refused before it changed anything. Do what the installer named, then roll back again.",
        InstallOutcome.Success or InstallOutcome.InstalledNeedsAttention when result.Rollback =>
            "The rollback has run. Running it again goes forward to the newer install.",
        _ when result.Rollback => "The rollback did not finish. Read the install log (or the installer's console window) before you run it again.",
        _ => null,
    };
}
