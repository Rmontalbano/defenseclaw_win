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

    /// <summary>
    /// What install.ps1 1.0.6's rollback will do while OpenCode V2 is set up here, or null (CUST-424). It asks the installed CLI before it changes anything
    /// (<c>Assert-OlderReleaseGuardsAgents</c>, scripts/install.ps1:639-652, called at :5218; <c>check_opencode_rollback</c>, cli/defenseclaw/migrations.py:3400) and refuses a
    /// target before 1.0.6. Nothing to say for an older installer or installed CLI (it has no such check, :643), a rollback that goes forward again (:5215), or a later target.
    /// </summary>
    /// <param name="installerVersion">The version of the install.ps1 the rollback runs.</param>
    /// <param name="installedVersion">The version installed now, when known.</param>
    /// <param name="backTo">The version in <c>previous\VERSION</c>.</param>
    /// <param name="v2">The guarded OpenCode V2 (<see cref="OpenCodeInstallFacts.V2"/>), or null.</param>
    public static string? OpenCodeNote(string? installerVersion, string? installedVersion, string? backTo, OpenCodeInstall? v2)
    {
        if (v2 is null || !IsAtLeast(installerVersion, OpenCodeInstallEvidence.FirstV2Release) ||
            !InstallScriptPlan.TryNormalizeVersion(backTo, out var target) || !Version.TryParse(target, out var targetVersion) ||
            targetVersion >= OpenCodeInstallEvidence.FirstV2Release)
        {
            return null;
        }

        if (InstallScriptPlan.TryNormalizeVersion(installedVersion, out var current) && Version.TryParse(current, out var currentVersion) &&
            (currentVersion < OpenCodeInstallEvidence.FirstV2Release || currentVersion < targetVersion))
        {
            return null;
        }

        return $"OpenCode V2 ({v2.Version}) is set up here, and releases before {OpenCodeInstallEvidence.FirstV2Release.ToString(3)} cannot guard it: DefenseClaw {target} guards OpenCode V1 only. " +
               "The installer refuses this rollback and nothing is changed. To roll back, put OpenCode V1 back first (" + OpenCodeInstallEvidence.PutV1BackCommand + "), " +
               "then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.";
    }

    /// <summary>
    /// Reads <c>previous\data</c> under <paramref name="dataDirectory"/> and returns <see cref="Note"/>, with <see cref="OpenCodeNote"/> beside it. Hashes the program, so call
    /// it off the UI thread.
    /// </summary>
    /// <param name="openCode">What the files say about OpenCode; <see cref="OpenCodeInstallEvidence.Read"/> when null.</param>
    public static string? Read(string dataDirectory, string? installerVersion, string? installedVersion, DateTimeOffset now, Func<OpenCodeInstallFacts>? openCode = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        // Nothing is read for an installer that does not check.
        if (!Checks(installerVersion))
        {
            return null;
        }

        var backTo = InstallFiles.ReadPreviousVersion(dataDirectory);
        var stale = RollbackAgentEvidence.Find(Path.Combine(dataDirectory, "previous", "data"), now);
        var codex = Note(installerVersion, installedVersion, backTo, stale);

        // Nothing more is read for an installer without the OpenCode check.
        var guards = IsAtLeast(installerVersion, OpenCodeInstallEvidence.FirstV2Release)
            ? OpenCodeNote(installerVersion, installedVersion, backTo, (openCode?.Invoke() ?? OpenCodeInstallEvidence.Read(dataDirectory)).V2)
            : null;
        return codex is not null && guards is not null ? codex + " " + guards : codex ?? guards;
    }

    private static bool Checks(string? installerVersion) => IsAtLeast(installerVersion, FirstCheckingInstaller);

    private static bool IsAtLeast(string? installerVersion, Version floor) =>
        InstallScriptPlan.TryNormalizeVersion(installerVersion, out var v) && Version.TryParse(v, out var version) && version >= floor;
}
