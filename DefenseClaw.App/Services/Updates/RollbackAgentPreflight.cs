using System.IO;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// What install.ps1 1.0.5's rollback will do about an agent program that changed since the older install set it up, said before the operator opens the
/// review (CUST-399). The installer checks it before it changes anything (scripts/install.ps1:5183-5207 in 1.0.5): a target of 0.8.10 or 1.0.0-1.0.3 rolls
/// back with a warning that Codex hooks need <c>defenseclaw setup codex</c>; for 1.0.4 or later it first selects the updated program for the older release
/// (GAP-2513) and refuses, with nothing changed, when it cannot. Pure apart from <see cref="Read"/>, which reads files and hashes the program.
/// </summary>
public static class RollbackAgentPreflight
{
    /// <summary>The first install.ps1 that checks the older install's agent programs before a rollback.</summary>
    public static readonly Version FirstCheckingInstaller = new(1, 0, 5);

    // install.ps1's own test for the releases that start without a connector whose setup fails ($backTo -match '^(?:0\.8\.10|1\.0\.[0-3])$').
    private static readonly Regex StartsWithoutCodex = new(@"^(?:0\.8\.10|1\.0\.[0-3])$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The note, or null when there is nothing to say: an installer before 1.0.5 (it does not check), a rollback that goes forward again (the installed
    /// version is older than the kept one), or no stale program.
    /// </summary>
    /// <param name="installerVersion">The version of the install.ps1 the rollback runs (the installed release's).</param>
    /// <param name="installedVersion">The version installed now, when known.</param>
    /// <param name="backTo">The version in <c>previous\VERSION</c>.</param>
    /// <param name="stale">What <see cref="RollbackAgentEvidence.Find"/> found in <c>previous\data</c>.</param>
    public static string? Note(string? installerVersion, string? installedVersion, string? backTo, IReadOnlyList<StaleAgentProgram> stale)
    {
        ArgumentNullException.ThrowIfNull(stale);
        if (stale.Count == 0 || !Checks(installerVersion) ||
            !InstallScriptPlan.TryNormalizeVersion(backTo, out var target) || !Version.TryParse(target, out var targetVersion))
        {
            return null;
        }

        if (InstallScriptPlan.TryNormalizeVersion(installedVersion, out var current) && Version.TryParse(current, out var currentVersion) && currentVersion < targetVersion)
        {
            return null;
        }

        var what = string.Join("; ", stale.Select(s => s.Missing
            ? $"{s.Label}, which DefenseClaw {target} set up, is no longer where it was"
            : $"Codex was updated after DefenseClaw {target} set up {s.Label}"));

        return StartsWithoutCodex.IsMatch(target)
            ? $"{what}. The rollback finishes, but DefenseClaw {target} starts without Codex hooks until you run 'defenseclaw setup codex' after it."
            : $"{what}. Before it changes anything, the installer selects the updated Codex for DefenseClaw {target}, as 'defenseclaw setup codex' would; " +
              "if it cannot, it refuses the rollback and nothing is changed.";
    }

    /// <summary>Reads <c>previous\data</c> under <paramref name="dataDirectory"/> and returns <see cref="Note"/>. Hashes the program, so call it off the UI thread.</summary>
    public static string? Read(string dataDirectory, string? installerVersion, string? installedVersion, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        // Nothing is read for an installer that does not check.
        if (!Checks(installerVersion))
        {
            return null;
        }

        var backTo = InstallFiles.ReadPreviousVersion(dataDirectory);
        var stale = RollbackAgentEvidence.Find(Path.Combine(dataDirectory, "previous", "data"), now);
        return Note(installerVersion, installedVersion, backTo, stale);
    }

    private static bool Checks(string? installerVersion) =>
        InstallScriptPlan.TryNormalizeVersion(installerVersion, out var v) && Version.TryParse(v, out var version) && version >= FirstCheckingInstaller;
}
