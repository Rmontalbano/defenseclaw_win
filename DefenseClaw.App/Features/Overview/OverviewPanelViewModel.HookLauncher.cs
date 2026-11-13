using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" for a Windows hook launcher or hook guard that is not the file the installer recorded, or has no recorded copy to compare with
/// (runtime 1.0.9, doctor's "&lt;Agent&gt; hooks" row, <see cref="HookLauncherDoctorFix"/>). Same rules as the PowerShell bridge row
/// (<see cref="CurrentHookBridgeRepairs"/>): read only from the cached doctor result, never from one older than <see cref="DoctorStaleAfter"/> (or with no
/// capture time), and it says when doctor ran. FAIL is High, the unverified WARN is Medium. There is no button: only the installer can put the launcher back
/// (setup cannot recreate it), and the Updates window installs a newer release or rolls back, never the installed version again.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The installer step doctor gives, in words (hook_integrity.py LAUNCHER_REINSTALL_STEP); not a command the app offers to copy or run.</summary>
    internal const string HookLauncherReinstallText = "Run the DefenseClaw installer again the way you installed it; setup cannot recreate the launcher.";

    private IReadOnlyList<(HookLauncherFinding Finding, DoctorCheckRow Check)> CurrentHookLauncherFindings()
    {
        if (_doctorSnapshot is not { } doctor || doctor.IsStale(DateTimeOffset.UtcNow, DoctorStaleAfter))
        {
            return [];
        }

        var found = new List<(HookLauncherFinding, DoctorCheckRow)>();
        foreach (var check in doctor.Checks)
        {
            if (HookLauncherDoctorFix.From(check) is { } finding && found.All(other => other.Item1.Key != finding.Key))
            {
                found.Add((finding, check));
            }
        }

        return found;
    }

    /// <summary>Adds one row per finding. Called from <see cref="AppendOverviewHealthAttention"/>.</summary>
    private void AppendHookLauncherAttention(List<AttentionRow> rows)
    {
        if (_doctorSnapshot is not { } doctor)
        {
            return;
        }

        var ran = doctor.CapturedAt is { } at ? $" (doctor ran {Relative(at)})" : string.Empty;
        foreach (var (finding, check) in CurrentHookLauncherFindings())
        {
            var shown = CapHookBridgeDetail($"Doctor: {DisplayNames.Visible(check.Label)}: {DisplayNames.Visible(check.Detail)}");
            var what = finding.Program == "guard" ? "hook guard" : "hook launcher";
            rows.Add(finding.Mismatch
                ? new AttentionRow
                {
                    Title = $"{finding.Agent}: the DefenseClaw {what} is not the installed copy",
                    Detail = $"Doctor found that the {what} does not match the copy the installer recorded, so " +
                             (finding.Program == "guard"
                                 ? $"{finding.Agent} prompts and tool calls may run without DefenseClaw. "
                                 : $"every {finding.Agent} hook call fails. ") +
                             $"{HookLauncherReinstallText} {shown}{ran}",
                    SeverityKey = "High",
                }
                : new AttentionRow
                {
                    Title = $"{finding.Agent}: the DefenseClaw {what} is not verified",
                    Detail = $"Doctor could not check the {what} because the installer recorded no copy of it to compare with. Running the installer again records one. {shown}{ran}",
                    SeverityKey = "Medium",
                });
        }
    }
}
