using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

/// <summary>
/// The helper's behaviour for each verb, over interfaces for every effect (the identity, the disk, the operator's confirmation, the process, the
/// token, the owner API), so the whole of it runs in tests against fakes with no elevation and no real file in Program Files.
/// <para>
/// The order is fixed and the same for every verb that changes something: read who is calling (never from the command line); refuse unless the
/// token is the elevated half of the calling user's own split token and the parent process is that user's; refuse unless the helper runs from
/// its admin-only place in Program Files (the one exception is <c>install-copy</c> putting itself there); then do the verb. Everything refused
/// leaves the machine as it was and says why.
/// </para>
/// </summary>
internal sealed class AdminHelper
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(90);
    public const int MaxOutputBytes = 1024 * 1024;

    private readonly ICallerIdentity _identity;
    private readonly IAdminFileSystem _fs;
    private readonly IConsent _consent;
    private readonly IProcessRunner _runner;
    private readonly ITokenOwner _tokenOwner;
    private readonly IRunningGateway _running;
    private readonly IOwnerFileSystem _owners;
    private readonly IClock _clock;
    private readonly AdminCopyInspector _inspector;

    public AdminHelper(
        ICallerIdentity identity,
        IAdminFileSystem fs,
        IConsent consent,
        IProcessRunner runner,
        ITokenOwner tokenOwner,
        IRunningGateway running,
        IOwnerFileSystem owners,
        IClock clock)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tokenOwner = tokenOwner ?? throw new ArgumentNullException(nameof(tokenOwner));
        _running = running ?? throw new ArgumentNullException(nameof(running));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _inspector = new AdminCopyInspector(fs);
    }

    /// <summary>Runs <paramref name="verb"/>. Never throws for a refusal or a failure; those are exit codes with lines.</summary>
    public HelperResult Run(AdminVerb verb)
    {
        var lines = new List<string>();
        AdminLayout? layout = null;
        var canRecord = false;

        HelperResult Finish(int code, string? line = null)
        {
            if (line is not null)
            {
                lines.Add(line);
            }

            if (canRecord && layout is not null && AdminVerbs.Changes(verb))
            {
                TryRecord(layout, verb, code, lines);
            }

            return new HelperResult(code, lines);
        }

        CallerIdentity caller;
        try
        {
            caller = _identity.Read();
            layout = AdminLayout.Create(caller.ProgramFiles, caller.UserSid, caller.ProfileDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Finish(AdminExit.Refused, "Refused: could not establish who is asking. " + ex.Message);
        }

        if (GatewayEnvironment.DangerousVariables(caller.EnvironmentNames) is { Count: > 0 } dangerous)
        {
            return Finish(AdminExit.Refused, "Refused: this process was started with " + string.Join(", ", dangerous) + ", which would load code the user chose into an administrator process.");
        }

        if (verb == AdminVerb.Status)
        {
            lines.Add(_inspector.Report(layout).ToJson());
            return Finish(AdminExit.Ok);
        }

        if (!caller.IsElevated)
        {
            return Finish(AdminExit.Refused, "Refused: this helper changes nothing unless it runs elevated.");
        }

        if (!caller.IsSplitTokenElevation)
        {
            return Finish(AdminExit.Refused, "Refused: the elevated account is not the signed-in user's own administrator token. Sign in as a local administrator and approve the prompt as yourself; an administrator's password typed into the prompt for a standard user is not accepted.");
        }

        if (!string.Equals(caller.ParentUserSid, caller.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return Finish(AdminExit.Refused, "Refused: the program that started the helper does not run as the account the helper is elevated as, or is gone.");
        }

        var atHome = AdminLayout.SamePath(caller.ImagePath, layout.HelperExe);
        if (atHome)
        {
            var helper = _inspector.InspectHelper(layout);
            if (!helper.Trusted)
            {
                return Finish(AdminExit.Refused, "Refused: the helper in Program Files is not the admin-only, recorded file it must be. " + string.Join(" ", helper.Problems));
            }
        }
        else if (verb != AdminVerb.InstallCopy)
        {
            return Finish(AdminExit.Refused, "Refused: this helper does not run from " + layout.HelperDirectory + ". Choose Prepare administrator copy once; that installs it there.");
        }

        canRecord = atHome;
        try
        {
            switch (verb)
            {
                case AdminVerb.InstallCopy:
                    if (!atHome)
                    {
                        var bootstrap = InstallHelper(layout, caller, lines);
                        if (bootstrap != AdminExit.Ok)
                        {
                            return Finish(bootstrap);
                        }

                        canRecord = true;
                    }

                    return Finish(InstallGatewayCopy(layout, caller, lines));
                case AdminVerb.Start:
                    return Finish(StartGateway(layout, caller, lines));
                case AdminVerb.Stop:
                    return Finish(StopGateway(layout, caller, lines));
                case AdminVerb.Restart:
                    return Finish(RestartGateway(layout, caller, lines));
                case AdminVerb.RepairOwnership:
                    return Finish(RepairOwnership(layout, lines));
                default:
                    return Finish(AdminExit.Usage, "Refused: unknown verb.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Finish(AdminExit.Failed, "Failed: " + ex.Message);
        }
    }

    // ---- install-copy: the helper itself ------------------------------------------------------------------

    /// <summary>
    /// First use: the helper is running from somewhere the user could write (the folder the app was unpacked to). It holds its own file open
    /// with read-only sharing, so nobody can change it while it is read, hashes it, shows the operator what it is about to install, and copies
    /// that same open file into Program Files; the copy is then hashed again and must equal the hash of the file it came from. The next run
    /// starts from Program Files, where no unelevated program can replace it.
    /// </summary>
    private int InstallHelper(AdminLayout layout, CallerIdentity caller, List<string> lines)
    {
        using var source = _fs.OpenReadExclusive(caller.ImagePath);
        var sha = Hex.HashOf(source);

        var existing = _inspector.InspectHelper(layout);
        if (existing.Trusted && string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("The helper in Program Files is already this file.");
            return AdminExit.Ok;
        }

        if (!_consent.Confirm(
                "Install the DefenseClaw administrator helper",
                $"DefenseClaw is about to copy this program into Program Files so it can run the gateway with administrator rights.\n\n" +
                $"From: {caller.ImagePath}\nTo: {layout.HelperExe}\nSHA-256: {sha}\n\n" +
                "It will then run only from there. Only continue if you started this from DefenseClaw and the SHA-256 is the one it showed you."))
        {
            lines.Add("Declined: nothing was installed.");
            return AdminExit.Declined;
        }

        _fs.EnsureAdminFolder(layout.HelperDirectory);

        source.Position = 0;
        var tempPath = CopyToTemp(source, layout.HelperDirectory);
        try
        {
            if (!VerifyTemp(tempPath, sha) || !SourceUnchanged(source, sha))
            {
                lines.Add("Refused: the helper changed while it was being copied. Nothing was installed.");
                _fs.DeleteFile(tempPath);
                return AdminExit.Refused;
            }

            _fs.Replace(tempPath, layout.HelperExe);
        }
        catch
        {
            _fs.DeleteFile(tempPath);
            throw;
        }

        _fs.WriteText(layout.HelperRecordPath, new HelperRecord(HelperRecord.CurrentSchema, sha, _clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).ToJson());

        var after = _inspector.InspectHelper(layout);
        if (!after.Trusted)
        {
            lines.Add("Refused: the installed helper does not pass its own check. " + string.Join(" ", after.Problems));
            return AdminExit.Refused;
        }

        lines.Add($"Installed the helper in {layout.HelperDirectory} (SHA-256 {Hex.Short(sha)}).");
        return AdminExit.Ok;
    }

    // ---- install-copy: the gateway ---------------------------------------------------------------------------

    /// <summary>
    /// Puts the user's installed gateway into Program Files if, and only if, it is the gateway the app checked: the manifest the app wrote
    /// (fresh, for this user) names its SHA-256, the file is read through a handle that blocks writers, and the copy hashes to the same value.
    /// Anything that writes new executable bytes into Program Files asks the operator first in a window of its own.
    /// </summary>
    private int InstallGatewayCopy(AdminLayout layout, CallerIdentity caller, List<string> lines)
    {
        var (sourceNode, pathProblem) = _inspector.CheckInstalledGatewayPath(layout);
        if (sourceNode is null)
        {
            lines.Add("Refused: " + pathProblem);
            return AdminExit.Refused;
        }

        using var source = _fs.OpenReadExclusive(layout.InstalledGatewayPath);
        var sha = Hex.HashOf(source);

        // Nothing to write when the copy already is these bytes: no manifest is needed and the operator is not asked.
        var existing = _inspector.InspectGatewayCopy(layout);
        if (existing.Trusted && string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("The administrator copy is already the installed gateway.");
            return AdminExit.Ok;
        }

        var manifest = ReleaseManifest.TryParse(_fs.ReadText(layout.ManifestPath, 16 * 1024));
        if (manifest is null)
        {
            lines.Add("Refused: there is no manifest from the app for this copy. Use Prepare administrator copy in DefenseClaw first.");
            return AdminExit.Refused;
        }

        if (manifest.Problem(layout.UserSid, _clock.UtcNow) is { } stale)
        {
            lines.Add("Refused: " + stale);
            return AdminExit.Refused;
        }

        if (!string.Equals(sha, manifest.GatewaySha256, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Refused: the installed gateway (SHA-256 {Hex.Short(sha)}) is not the one the app checked ({Hex.Short(manifest.GatewaySha256)}).");
            return AdminExit.Refused;
        }

        if (!_consent.Confirm(
                "Copy the DefenseClaw gateway for administrator use",
                $"DefenseClaw is about to copy the gateway you installed into Program Files, and from then on run it with administrator rights.\n\n" +
                $"Version: {manifest.Version ?? "unknown"}\nFrom: {layout.InstalledGatewayPath}\nTo: {layout.GatewayCopyPath}\nSHA-256: {sha}\n" +
                $"Checked by DefenseClaw against the release: {manifest.SignatureState}\n\n" +
                "Only continue if you started this from DefenseClaw and the SHA-256 is the one it showed you."))
        {
            lines.Add("Declined: nothing was copied.");
            return AdminExit.Declined;
        }

        _fs.EnsureAdminFolder(layout.UserDirectory);

        source.Position = 0;
        var tempPath = CopyToTemp(source, layout.UserDirectory);
        try
        {
            if (!VerifyTemp(tempPath, sha) || !SourceUnchanged(source, sha) || SourceIdentityChanged(layout, sourceNode))
            {
                lines.Add("Refused: the installed gateway changed while it was being copied. Nothing was installed.");
                _fs.DeleteFile(tempPath);
                return AdminExit.Refused;
            }

            try
            {
                _fs.Replace(tempPath, layout.GatewayCopyPath);
            }
            catch (IOException ex)
            {
                lines.Add("Refused: the old administrator copy could not be replaced (is the gateway it started still running? stop it first): " + ex.Message);
                _fs.DeleteFile(tempPath);
                return AdminExit.Refused;
            }
        }
        catch
        {
            _fs.DeleteFile(tempPath);
            throw;
        }

        _fs.WriteText(
            layout.CopyRecordPath,
            CopyRecord.Create(sha, manifest.Version, _clock.UtcNow, sha, manifest.SignatureState).ToJson());

        var after = _inspector.InspectGatewayCopy(layout);
        if (!after.Trusted)
        {
            lines.Add("Refused: the administrator copy does not pass its check. " + string.Join(" ", after.Problems));
            _fs.DeleteFile(layout.GatewayCopyPath);
            return AdminExit.Refused;
        }

        lines.Add($"Copied the gateway to {layout.GatewayCopyPath} (SHA-256 {Hex.Short(sha)}).");
        return AdminExit.Ok;
    }

    private string CopyToTemp(Stream source, string folder)
    {
        using var temp = _fs.CreateTemp(folder, out var tempPath);
        try
        {
            source.CopyTo(temp);
        }
        catch
        {
            temp.Dispose();
            _fs.DeleteFile(tempPath);
            throw;
        }

        return tempPath;
    }

    /// <summary>The temporary copy, read back, hashes to <paramref name="sha"/>.</summary>
    private bool VerifyTemp(string tempPath, string sha)
    {
        using var stream = _fs.OpenReadExclusive(tempPath);
        return string.Equals(Hex.HashOf(stream), sha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The source, read again from the start through the same handle, still hashes to <paramref name="sha"/>.</summary>
    private static bool SourceUnchanged(Stream source, string sha)
    {
        source.Position = 0;
        return string.Equals(Hex.HashOf(source), sha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installed gateway's size or time changed since it was first looked at.</summary>
    private bool SourceIdentityChanged(AdminLayout layout, FsNode before)
    {
        var after = _fs.Stat(layout.InstalledGatewayPath);
        return !after.Exists || after.IsReparsePoint || after.Length != before.Length || after.LastWriteUtc != before.LastWriteUtc;
    }

    // ---- start / stop / restart ---------------------------------------------------------------------------------

    private int StartGateway(AdminLayout layout, CallerIdentity caller, List<string> lines)
    {
        var copy = RequireTrustedCopy(layout, lines);
        if (copy is null)
        {
            return AdminExit.Refused;
        }

        var (installed, problem) = _inspector.HashInstalledGateway(layout);
        if (installed is null)
        {
            lines.Add("Refused: " + problem);
            return AdminExit.Refused;
        }

        if (!string.Equals(installed, copy.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("Refused: The installed gateway differs from the administrator copy. Choose Restart Gateway as Administrator to use the installed version. No gateway action was performed.");
            return AdminExit.Refused;
        }

        if (StateOwnership.Foreign(layout, _fs) is { Count: > 0 } foreign)
        {
            lines.Add("Refused: " + string.Join(", ", foreign) + " in your .defenseclaw folder is owned by Administrators or another account, which DefenseClaw 1.x will not use. Run Repair ownership first. No gateway action was performed.");
            return AdminExit.Refused;
        }

        if (_running.IsRunning(layout))
        {
            lines.Add("Refused: a gateway is already running. Stop it or use Restart Gateway as Administrator. No gateway action was performed.");
            return AdminExit.Refused;
        }

        return RunGateway(layout, caller, "start", StartTimeout, lines);
    }

    private int StopGateway(AdminLayout layout, CallerIdentity caller, List<string> lines)
    {
        if (RequireTrustedCopy(layout, lines) is null)
        {
            return AdminExit.Refused;
        }

        return RunGateway(layout, caller, "stop", StopTimeout, lines);
    }

    /// <summary>
    /// Mac order: stop the old one (it must stop), then make the copy the installed gateway, then start. Only restart refreshes the copy. When a
    /// gateway is running and there is no usable copy to stop it with, the copy is made first (nothing is running from a copy that does not exist).
    /// </summary>
    private int RestartGateway(AdminLayout layout, CallerIdentity caller, List<string> lines)
    {
        var existing = _inspector.InspectGatewayCopy(layout);
        var running = _running.IsRunning(layout);

        if (running && existing.Trusted)
        {
            var stopped = RunGateway(layout, caller, "stop", StopTimeout, lines);
            if (stopped != AdminExit.Ok)
            {
                lines.Add("The previous gateway did not stop; nothing else was changed.");
                return stopped;
            }

            var refreshed = InstallGatewayCopy(layout, caller, lines);
            return refreshed != AdminExit.Ok ? refreshed : StartGateway(layout, caller, lines);
        }

        var refresh = InstallGatewayCopy(layout, caller, lines);
        if (refresh != AdminExit.Ok)
        {
            return refresh;
        }

        if (running)
        {
            var stopped = RunGateway(layout, caller, "stop", StopTimeout, lines);
            if (stopped != AdminExit.Ok)
            {
                lines.Add("The previous gateway did not stop; it was not restarted.");
                return stopped;
            }
        }

        return StartGateway(layout, caller, lines);
    }

    private CopyInspection? RequireTrustedCopy(AdminLayout layout, List<string> lines)
    {
        var copy = _inspector.InspectGatewayCopy(layout);
        if (!copy.Exists)
        {
            lines.Add("Refused: there is no administrator copy of the gateway yet. Choose Prepare administrator copy first. No gateway action was performed.");
            return null;
        }

        if (!copy.Trusted)
        {
            lines.Add("Refused: the administrator copy is not the file the helper installed. " + string.Join(" ", copy.Problems) + " No gateway action was performed.");
            return null;
        }

        return copy;
    }

    /// <summary>Runs the administrator copy with one verb, the rebuilt environment, and the default token owner set to the user for the length of the run.</summary>
    private int RunGateway(AdminLayout layout, CallerIdentity caller, string verb, TimeSpan timeout, List<string> lines)
    {
        var environment = GatewayEnvironment.Build(layout, caller.SystemRoot, caller.UserName);
        var request = new ProcessRequest(layout.GatewayCopyPath, new[] { verb }, layout.UserDirectory, environment, timeout, MaxOutputBytes);

        ProcessOutcome outcome;
        using (_tokenOwner.Use(layout.UserSid))
        {
            outcome = _runner.Run(request);
        }

        lines.AddRange(outcome.Lines);
        if (!outcome.Started)
        {
            lines.Add("Failed: the gateway could not be started. " + outcome.Failure);
            return AdminExit.Failed;
        }

        if (outcome.TimedOut)
        {
            lines.Add($"Failed: `defenseclaw-gateway {verb}` did not finish in {timeout.TotalSeconds:0} seconds.");
            return AdminExit.Failed;
        }

        lines.Add($"defenseclaw-gateway {verb} exited with code {outcome.ExitCode}.");
        return outcome.ExitCode == 0 ? AdminExit.Ok : AdminExit.Failed;
    }

    // ---- repair-ownership ---------------------------------------------------------------------------------------

    private int RepairOwnership(AdminLayout layout, List<string> lines)
    {
        var root = _owners.Probe(layout.DataDirectory);
        if (root is null || !root.IsDirectory)
        {
            lines.Add("Refused: there is no .defenseclaw folder in your profile to repair.");
            return AdminExit.Refused;
        }

        if (root.IsReparsePoint)
        {
            lines.Add("Refused: your .defenseclaw folder is a link or junction; ownership is not changed through one.");
            return AdminExit.Refused;
        }

        var report = OwnershipRepair.Run(layout, _owners);
        lines.Add(
            $"Looked at {report.Examined} items under {layout.DataDirectory}: {report.Fixed} given back to you, {report.AlreadyUser} already yours, " +
            $"{report.LeftForeign} owned by another account (left alone), {report.SkippedLinks} links skipped.");
        foreach (var failure in report.FirstFailures)
        {
            lines.Add("Could not change " + failure);
        }

        if (report.Truncated)
        {
            lines.Add("The folder is larger than the repair will walk; run it again.");
        }

        return report.Success ? AdminExit.Ok : AdminExit.Failed;
    }

    // ---- result -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Leaves the result in the helper's own admin-only folder for the app to show. Never in a folder the user can write to: an elevated
    /// process that wrote there could be pointed at any file with a junction.
    /// </summary>
    private void TryRecord(AdminLayout layout, AdminVerb verb, int code, List<string> lines)
    {
        try
        {
            _fs.EnsureAdminFolder(layout.UserDirectory);
            var kept = lines.Count > AdminRunResult.MaxLines ? lines.Skip(lines.Count - AdminRunResult.MaxLines).ToArray() : lines.ToArray();
            var result = new AdminRunResult(
                AdminRunResult.CurrentSchema,
                AdminVerbs.Name(verb),
                code,
                _clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                kept);
            _fs.WriteText(layout.ResultPath, result.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit code still reaches the app; the words are a convenience.
        }
    }
}
