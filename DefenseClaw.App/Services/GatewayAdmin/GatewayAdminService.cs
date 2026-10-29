using System.IO;
using System.Diagnostics;
using System.Globalization;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>One elevated run of the helper: the verb, and what makes it different from another.</summary>
/// <param name="Verb">The only argument the helper is started with.</param>
/// <param name="BootstrapsHelper">The first run: the helper is started from the file next to the app (held open and checked against the hash the operator was shown) so that it can install itself in Program Files.</param>
/// <param name="WritesManifest">The app writes the manifest (what it checked about the installed gateway) just before this step.</param>
/// <param name="Purpose">One sentence for the review.</param>
internal sealed record AdminStep(AdminVerb Verb, bool BootstrapsHelper, bool WritesManifest, string Purpose);

/// <summary>What would run elevated, decided before anything is asked: either a refusal with its reason, or the steps and the review to show.</summary>
internal sealed record AdminPlan(
    string? Refusal,
    IReadOnlyList<AdminStep> Steps,
    CommandReview? Review,
    ReleaseGateway? Release = null,
    string? StagedHelperSha256 = null)
{
    public static AdminPlan Refused(string reason) => new(reason, Array.Empty<AdminStep>(), null);

    public bool IsRefused => Refusal is not null;
}

internal sealed record AdminStepOutcome(bool Succeeded, bool Cancelled, string Message);

/// <summary>What <see cref="GatewayLifecycle"/> needs of the administrator feature: a switch, a plan to review, and a step to run.</summary>
internal interface IGatewayAdministrator
{
    /// <summary>The last status read (never blocks).</summary>
    GatewayAdminStatus Status { get; }

    /// <summary>The gateway actions go through the helper: the mode is on and this PC can do it.</summary>
    bool IsActive { get; }

    /// <summary>Why an ordinary (non-elevated) start or restart must not be attempted, or null. The Windows exit 77.</summary>
    string? NormalPathBlockedReason(GatewayAction action);

    Task<AdminPlan> PlanAsync(GatewayAction action, CancellationToken cancellationToken);

    Task<AdminPlan> PlanPrepareAsync(CancellationToken cancellationToken);

    Task<AdminPlan> PlanRepairOwnershipAsync(CancellationToken cancellationToken);

    Task<AdminStepOutcome> ExecuteAsync(AdminPlan plan, AdminStep step, CancellationToken cancellationToken);
}

/// <summary>Everything the service touches outside itself, so a test supplies fakes for all of it and nothing elevates.</summary>
internal sealed class GatewayAdminDependencies
{
    public required Func<GateDecisionLike> Supported { get; init; }

    public required Func<bool> ModeOn { get; init; }

    public required Action<bool> SaveMode { get; init; }

    public required Func<GatewaySnapshot> Snapshot { get; init; }

    /// <summary>The installed DefenseClaw version (the release the gateway is checked against), or null while unknown.</summary>
    public required Func<string?> InstalledVersion { get; init; }

    public required IAdminLayoutSource Layout { get; init; }

    public required IAdminFileSystem Fs { get; init; }

    public required IAdminAccountProbe Account { get; init; }

    public required IProcessElevationProbe Elevation { get; init; }

    public required IAdminElevator Elevator { get; init; }

    public required IReleaseGatewaySource Release { get; init; }

    public required IManifestWriter Manifest { get; init; }

    public required IHelperStage Stage { get; init; }

    /// <summary>The names of this process's environment variables (the helper refuses loader switches, so the app says so first).</summary>
    public Func<IEnumerable<string>> EnvironmentNames { get; init; } = static () =>
        Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(static k => k.ToString() ?? string.Empty);

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>A runtime gate's answer, without dragging the Core type into the dependencies.</summary>
internal readonly record struct GateDecisionLike(bool IsAvailable, string Reason);

/// <summary>
/// "Run gateway as administrator" (the Mac app's feature, adapted): the status the Runtime card and the toggles show, and the plans the shared
/// <see cref="GatewayLifecycle"/> reviews and runs. This class never elevates by itself - the only call that can raise a prompt is
/// <see cref="IAdminElevator.RunAsync"/>, reached from <see cref="ExecuteAsync"/> after a plan was reviewed and confirmed - and it passes the
/// helper nothing but a verb.
/// </summary>
internal sealed class GatewayAdminService : IGatewayAdministrator, IGatewayElevationStatus
{
    private readonly GatewayAdminDependencies _d;
    private readonly AdminCopyInspector _inspector;
    private readonly Dictionary<string, ReleaseGateway> _releases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private GatewayAdminStatus _status = GatewayAdminStatus.Unknown;
    private int _refreshing;

    public GatewayAdminService(GatewayAdminDependencies dependencies)
    {
        _d = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _inspector = new AdminCopyInspector(dependencies.Fs);
    }

    /// <summary>Raised (on a pool thread) after every status read.</summary>
    public event EventHandler? Changed;

    /// <summary>The last status read; <see cref="GatewayAdminStatus.Unknown"/> until the first.</summary>
    public GatewayAdminStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool IsActive => Status.IsActive;

    /// <summary>
    /// Elevated only when the gateway on the API port is our Program Files copy (the image is the per-user copy path, and the status check found the
    /// copy admin-only and unchanged) with an elevated token. NotElevated for any other running gateway; Unknown when none runs or it cannot be told.
    /// </summary>
    public GatewayElevation Current
    {
        get
        {
            var status = Status;
            var snapshot = _d.Snapshot();
            if (!status.Supported || !snapshot.IsRunning || snapshot.PortOwner is not { } owner)
            {
                return GatewayElevation.Unknown;
            }

            var layout = _d.Layout.Get();
            var isCopy = layout is not null && IsOurCopy(layout, snapshot, owner) && status.CopyTrusted;
            return isCopy && status.GatewayRunningElevated == true
                ? GatewayElevation.Elevated
                : status.GatewayRunningElevated is null && !isCopy ? GatewayElevation.Unknown : GatewayElevation.NotElevated;
        }
    }

    // ---- the switch -----------------------------------------------------------------------------------

    /// <summary>
    /// Turns the mode on or off. It saves the setting and starts or stops nothing: the gateway keeps running as it is, and the next start, restart
    /// or stop goes the way the switch says. Refused (false) when the runtime or the account cannot do it.
    /// </summary>
    public bool TrySetMode(bool on)
    {
        var status = Status;
        if (on && !status.CanToggle)
        {
            return false;
        }

        _d.SaveMode(on);
        RequestRefresh();
        return true;
    }

    // ---- the status -------------------------------------------------------------------------------------

    /// <summary>Looks again, now, on a pool thread, and publishes the result.</summary>
    public Task<GatewayAdminStatus> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var status = ReadStatus();
            lock (_gate)
            {
                _status = status;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return status;
        }, cancellationToken);

    /// <summary>A refresh that may overlap an earlier one is dropped. Does nothing at all on a runtime without the feature, so 0.8.10 never reads Program Files.</summary>
    public void RequestRefresh()
    {
        if (!_d.Supported().IsAvailable)
        {
            var unsupported = ReadStatus();
            lock (_gate)
            {
                _status = unsupported;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _ = await RefreshAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A failed look must not take the process down; it stays the previous status.
            catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                Trace.TraceWarning("Gateway administrator status could not be read: " + ex.Message);
            }
            finally
            {
                _ = Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    internal GatewayAdminStatus ReadStatus()
    {
        var gate = _d.Supported();
        var modeOn = _d.ModeOn();
        if (!gate.IsAvailable)
        {
            return new GatewayAdminStatus
            {
                Supported = false,
                UnsupportedReason = gate.Reason,
                ModeOn = modeOn,
                CheckedAt = _d.Clock.GetUtcNow(),
            };
        }

        var layout = _d.Layout.Get();
        var account = _d.Account.Read();
        var (eligibility, reason) = (layout, account) switch
        {
            (null, _) => (AdminEligibility.LayoutUnavailable, "The signed-in user's profile or the Program Files folder could not be determined."),
            (_, AdminAccountKind.StandardUser) => (AdminEligibility.NeedsLocalAdministrator, "Needs a local administrator account (with User Account Control on). This account is a standard user: Windows would ask for another account's password, and the gateway would then run as that account."),
            (_, AdminAccountKind.Unknown) => (AdminEligibility.NeedsLocalAdministrator, "Could not tell whether this account is a local administrator."),
            _ => (AdminEligibility.Eligible, string.Empty),
        };

        if (layout is null || eligibility != AdminEligibility.Eligible)
        {
            return new GatewayAdminStatus
            {
                Supported = true,
                ModeOn = modeOn,
                Eligibility = eligibility,
                EligibilityReason = reason,
                CheckedAt = _d.Clock.GetUtcNow(),
            };
        }

        var report = _inspector.Report(layout);
        var foreign = StateOwnership.Foreign(layout, _d.Fs);
        var stage = _d.Stage.Locate(out _);

        return new GatewayAdminStatus
        {
            Supported = true,
            ModeOn = modeOn,
            Eligibility = AdminEligibility.Eligible,
            HelperStaged = stage is not null,
            HelperInstalled = report.HelperInstalled,
            HelperTrusted = report.HelperTrusted,
            CopyPresent = report.CopyPresent,
            CopyTrusted = report.CopyTrusted,
            CopyMatchesInstalled = report.CopyMatchesInstalled,
            CopyVersion = report.CopyVersion,
            GatewayRunningElevated = RunningElevated(layout),
            OwnershipProblem = foreign.Count > 0,
            OwnershipFiles = foreign,
            Problems = report.Problems,
            CheckedAt = _d.Clock.GetUtcNow(),
        };
    }

    private bool? RunningElevated(AdminLayout layout)
    {
        var snapshot = _d.Snapshot();
        if (!snapshot.IsRunning || snapshot.PortOwner is not { } owner)
        {
            return null;
        }

        // The token says it when Windows lets a limited handle read it; otherwise the image says it (only the helper starts the copy).
        return _d.Elevation.IsElevated(owner.Pid) ?? (IsOurCopy(layout, snapshot, owner) ? true : null);
    }

    /// <summary>
    /// The gateway on the port is the administrator copy: its image is the copy's path, or (when Windows would not give an unelevated app the
    /// image of an elevated process) the peer check matched it by the helper's records (CUST-362).
    /// </summary>
    private static bool IsOurCopy(AdminLayout layout, GatewaySnapshot snapshot, DefenseClaw.Core.Net.PortOwner owner) =>
        layout.IsGatewayCopy(owner.ImagePath) ||
        (string.IsNullOrWhiteSpace(owner.ImagePath) && snapshot.PeerRoute == DefenseClaw.Core.Gateway.GatewayPeerRoute.AdministratorCopyByRecord);

    // ---- refusing the ordinary path ------------------------------------------------------------------------

    public string? NormalPathBlockedReason(GatewayAction action)
    {
        var status = Status;
        return status.Supported && status.OwnershipProblem && action is GatewayAction.Start or GatewayAction.Restart
            ? GatewayAdminStatus.OwnershipSentence + " Files: " + string.Join(", ", status.OwnershipFiles) + "."
            : null;
    }

    // ---- plans ---------------------------------------------------------------------------------------------

    public async Task<AdminPlan> PlanAsync(GatewayAction action, CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (CommonRefusal(status) is { } refusal)
        {
            return AdminPlan.Refused(refusal);
        }

        var layout = _d.Layout.Get()!;
        if (action == GatewayAction.Stop)
        {
            return status.HelperTrusted && status.CopyTrusted
                ? Build(
                    layout,
                    "Stop gateway as administrator?",
                    "This stops the gateway the administrator copy started. Hooks that call it follow the configured fail mode while it is down.",
                    new[] { new AdminStep(AdminVerb.Stop, false, false, "Stop the gateway with the administrator copy.") },
                    null,
                    null,
                    status)
                : AdminPlan.Refused("There is no trusted administrator copy of the gateway, so there is nothing to stop as administrator. If an elevated gateway was started some other way, stop it from an elevated terminal.");
        }

        if (status.OwnershipProblem)
        {
            return AdminPlan.Refused(GatewayAdminStatus.OwnershipSentence + " Files: " + string.Join(", ", status.OwnershipFiles) + ".");
        }

        var copyUsable = status.HelperTrusted && status.CopyTrusted;
        var needsRelease = !copyUsable || (action == GatewayAction.Restart && status.CopyMatchesInstalled != true);

        if (action == GatewayAction.Start && copyUsable && status.CopyMatchesInstalled != true)
        {
            // The Mac's sentence: only Restart refreshes the copy.
            return AdminPlan.Refused("The installed gateway differs from the administrator copy. Choose Restart Gateway as Administrator to use the installed version. No gateway action was performed.");
        }

        ReleaseGateway? release = null;
        string? stagedSha = null;
        if (needsRelease)
        {
            var (checkedRelease, problem) = await CheckInstalledAgainstReleaseAsync(layout, cancellationToken).ConfigureAwait(false);
            if (checkedRelease is null)
            {
                return AdminPlan.Refused(problem!);
            }

            release = checkedRelease;
        }

        var steps = new List<AdminStep>();
        if (!status.HelperTrusted)
        {
            if (_d.Stage.Locate(out var stageProblem) is not { } staged)
            {
                return AdminPlan.Refused(stageProblem ?? "The administrator helper (DefenseClaw.GatewayAdmin.exe) is not next to the app.");
            }

            stagedSha = staged.Sha256;
            steps.Add(new AdminStep(
                AdminVerb.InstallCopy,
                BootstrapsHelper: true,
                WritesManifest: true,
                "Install the administrator helper and a copy of the installed gateway in Program Files."));
        }
        else if (!status.CopyTrusted && action == GatewayAction.Start)
        {
            steps.Add(new AdminStep(AdminVerb.InstallCopy, false, true, "Copy the installed gateway into Program Files."));
        }

        if (action == GatewayAction.Start)
        {
            steps.Add(new AdminStep(AdminVerb.Start, false, false, "Start the administrator copy of the gateway."));
        }
        else
        {
            steps.Add(new AdminStep(AdminVerb.Restart, false, needsRelease && steps.Count == 0, "Stop the running gateway, refresh the copy from the installed gateway, start it again."));
        }

        var title = action == GatewayAction.Start ? "Start gateway as administrator?" : "Restart gateway as administrator?";
        var summary = action == GatewayAction.Start
            ? "This starts the DefenseClaw gateway with administrator rights."
            : "This restarts the DefenseClaw gateway with administrator rights. Hooks that call it follow the configured fail mode for the moment it is down.";
        return Build(layout, title, summary, steps, release, stagedSha, status);
    }

    /// <summary>"Prepare administrator copy": install the helper and the copy, start nothing.</summary>
    public async Task<AdminPlan> PlanPrepareAsync(CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (CommonRefusal(status) is { } refusal)
        {
            return AdminPlan.Refused(refusal);
        }

        if (status.HelperTrusted && status.CopyTrusted && status.CopyMatchesInstalled == true)
        {
            return AdminPlan.Refused("The administrator copy is already the installed gateway. Nothing to prepare.");
        }

        var layout = _d.Layout.Get()!;
        var (release, problem) = await CheckInstalledAgainstReleaseAsync(layout, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return AdminPlan.Refused(problem!);
        }

        string? stagedSha = null;
        var bootstrap = !status.HelperTrusted;
        if (bootstrap)
        {
            if (_d.Stage.Locate(out var stageProblem) is not { } staged)
            {
                return AdminPlan.Refused(stageProblem ?? "The administrator helper (DefenseClaw.GatewayAdmin.exe) is not next to the app.");
            }

            stagedSha = staged.Sha256;
        }

        return Build(
            layout,
            "Prepare the administrator copy?",
            "This puts the helper and a copy of the installed gateway in Program Files. It starts and stops nothing.",
            new[] { new AdminStep(AdminVerb.InstallCopy, bootstrap, true, "Copy the installed gateway into Program Files.") },
            release,
            stagedSha,
            status);
    }

    public async Task<AdminPlan> PlanRepairOwnershipAsync(CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (CommonRefusal(status) is { } refusal)
        {
            return AdminPlan.Refused(refusal);
        }

        if (!status.OwnershipProblem)
        {
            return AdminPlan.Refused("No file in your .defenseclaw folder is owned by Administrators. Nothing to repair.");
        }

        if (!status.HelperTrusted)
        {
            // Repair is the one verb that needs no copy, but it does need the helper in its place.
            if (_d.Stage.Locate(out var stageProblem) is null)
            {
                return AdminPlan.Refused(stageProblem ?? "The administrator helper (DefenseClaw.GatewayAdmin.exe) is not next to the app.");
            }

            return AdminPlan.Refused("The administrator helper is not installed yet. Choose Prepare administrator copy first; it installs the helper.");
        }

        var layout = _d.Layout.Get()!;
        return Build(
            layout,
            "Repair ownership?",
            "This gives the files in " + layout.DataDirectory + " that are owned by the Administrators group back to you: " + string.Join(", ", status.OwnershipFiles) +
            " and anything below them. Links are not followed and nothing outside that folder is touched.",
            new[] { new AdminStep(AdminVerb.RepairOwnership, false, false, "Give the files back to your account.") },
            null,
            null,
            status);
    }

    /// <summary>The refusals every plan shares: not supported, not this kind of account, the environment, the helper's ancestors.</summary>
    private string? CommonRefusal(GatewayAdminStatus status)
    {
        if (!status.Supported)
        {
            return status.UnsupportedReason;
        }

        if (status.Eligibility != AdminEligibility.Eligible)
        {
            return status.EligibilityReason;
        }

        var dangerous = GatewayEnvironment.DangerousVariables(_d.EnvironmentNames());
        return dangerous.Count > 0
            ? "This app was started with " + string.Join(", ", dangerous) + " set. Those load code of your choosing into a .NET program, so the administrator helper refuses to run with them. Start DefenseClaw without them."
            : null;
    }

    private async Task<(ReleaseGateway? Release, string? Problem)> CheckInstalledAgainstReleaseAsync(AdminLayout layout, CancellationToken cancellationToken)
    {
        var (installedSha, installedProblem) = await Task.Run(() => _inspector.HashInstalledGateway(layout), cancellationToken).ConfigureAwait(false);
        if (installedSha is null)
        {
            return (null, installedProblem);
        }

        var version = _d.InstalledVersion();
        if (string.IsNullOrWhiteSpace(version))
        {
            return (null, "The installed DefenseClaw version is not known yet, so the gateway cannot be checked against its release. Try again in a moment.");
        }

        ReleaseGateway? release;
        lock (_gate)
        {
            _releases.TryGetValue(version, out release);
        }

        if (release is null)
        {
            var (fetched, problem) = await _d.Release.GetAsync(version, cancellationToken).ConfigureAwait(false);
            if (fetched is null)
            {
                return (null, problem ?? "The release could not be checked.");
            }

            release = fetched;
            lock (_gate)
            {
                _releases[version] = release;
            }
        }

        return string.Equals(release.GatewaySha256, installedSha, StringComparison.OrdinalIgnoreCase)
            ? (release, null)
            : (null, $"The installed gateway (SHA-256 {Hex.Short(installedSha)}) is not the defenseclaw-gateway.exe in the {release.Version} release (SHA-256 {Hex.Short(release.GatewaySha256)}), " +
                     "for example a build from source or a modified file. It is not copied to a place that runs with administrator rights.");
    }

    private AdminPlan Build(
        AdminLayout layout,
        string title,
        string summary,
        IReadOnlyList<AdminStep> steps,
        ReleaseGateway? release,
        string? stagedSha,
        GatewayAdminStatus status)
    {
        var prompts = steps.Count == 1 ? "one Windows prompt" : $"{steps.Count} Windows prompts, one per step";
        var running = new List<string> { summary, $"Windows asks you to approve it with {prompts}." };
        if (steps.Any(static s => s.Verb is AdminVerb.Start or AdminVerb.Restart or AdminVerb.Stop))
        {
            running.Add(
                $"What runs with administrator rights: {layout.GatewayCopyPath}" +
                (release is not null ? $" (a copy of your installed gateway, DefenseClaw {release.Version}, SHA-256 {Hex.Short(release.GatewaySha256)})" : status.CopyTrusted ? " (the copy the helper installed)" : string.Empty) +
                $", started by the helper {layout.HelperExe}. It reads and writes your data in {layout.DataDirectory}, and the files it creates stay yours.");
        }

        if (steps.Any(static s => s.BootstrapsHelper))
        {
            running.Add($"The helper itself is installed first, from the file next to this app (SHA-256 {Hex.Short(stagedSha, 24)}); it asks once more in its own window.");
        }

        if (release is not null)
        {
            running.Add($"The gateway was checked against the {release.Version} release: {release.SignatureLabel}.");
        }

        var warnings = new List<CommandReviewWarning>
        {
            new(
                "Runs with administrator rights",
                "The gateway then has a full administrator token, like running with sudo: a gateway bug or a scanner it runs has more reach. " +
                "It matters only for runtime monitoring's agent-actions plane."),
        };

        var executable = layout.HelperExe;
        var reviewSteps = new List<CommandReviewStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            reviewSteps.Add(new CommandReviewStep(
                new[] { AdminVerbs.Name(steps[i].Verb) },
                steps[i].Purpose,
                CommandTier.StateChanging,
                steps[i].BootstrapsHelper && _d.Stage.Locate(out _) is { } staged ? staged.Path : executable,
                steps.Count > 1 ? i + 1 : 0));
        }

        var review = new CommandReview
        {
            Title = title,
            Summary = string.Join(' ', running),
            Steps = reviewSteps,
            Warnings = warnings,
            ConfirmLabel = title.TrimEnd('?'),
        };
        return new AdminPlan(null, steps, review, release, stagedSha);
    }

    // ---- running a step ------------------------------------------------------------------------------------

    public async Task<AdminStepOutcome> ExecuteAsync(AdminPlan plan, AdminStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);

        var layout = _d.Layout.Get();
        if (layout is null)
        {
            return new AdminStepOutcome(false, false, "The signed-in user's profile or the Program Files folder could not be determined.");
        }

        ElevationRequest request;
        if (step.BootstrapsHelper)
        {
            var staged = _d.Stage.Locate(out var problem);
            if (staged is null)
            {
                return new AdminStepOutcome(false, false, problem ?? "The administrator helper is not next to the app.");
            }

            if (plan.StagedHelperSha256 is null || !string.Equals(staged.Sha256, plan.StagedHelperSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new AdminStepOutcome(false, false, "The helper next to the app is not the file you reviewed. Nothing was started.");
            }

            request = new ElevationRequest(staged.Path, step.Verb, staged.Sha256);
        }
        else
        {
            var helper = await Task.Run(() => _inspector.InspectHelper(layout), cancellationToken).ConfigureAwait(false);
            if (!helper.Trusted)
            {
                return new AdminStepOutcome(false, false, "The administrator helper in Program Files is not the admin-only file it installed, so it is not started. " + string.Join(" ", helper.Problems));
            }

            request = new ElevationRequest(layout.HelperExe, step.Verb);
        }

        if (step.WritesManifest)
        {
            if (plan.Release is not { } release)
            {
                return new AdminStepOutcome(false, false, "There is no checked release for this step. Nothing was started.");
            }

            try
            {
                _d.Manifest.Write(
                    layout,
                    new ReleaseManifest(
                        ReleaseManifest.CurrentSchema,
                        layout.UserSid,
                        release.Version,
                        release.GatewaySha256,
                        release.ZipSha256,
                        release.SignatureState,
                        _d.Clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AdminStepOutcome(false, false, "The manifest could not be written: " + ex.Message);
            }
        }

        var started = _d.Clock.GetUtcNow().UtcDateTime;
        var outcome = await _d.Elevator.RunAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await RefreshAsync(CancellationToken.None).ConfigureAwait(false);

        switch (outcome.Result)
        {
            case ElevationResult.Cancelled:
                return new AdminStepOutcome(false, true, "Cancelled at the Windows prompt. Nothing was changed.");
            case ElevationResult.Failed:
                return new AdminStepOutcome(false, false, "The helper could not be started: " + (outcome.Error ?? "unknown error") + ".");
        }

        var words = ReadResultWords(layout, step.Verb, started);
        return outcome.ExitCode switch
        {
            AdminExit.Ok => new AdminStepOutcome(true, false, words ?? DoneSentence(step.Verb)),
            AdminExit.Declined => new AdminStepOutcome(false, true, "Declined in the helper's own confirmation window. Nothing was changed."),
            AdminExit.Refused => new AdminStepOutcome(false, false, "The helper refused: " + (words ?? "see the Windows prompt text; no details were left.")),
            AdminExit.Usage => new AdminStepOutcome(false, false, "The helper did not accept the command (a different version?)."),
            { } code => new AdminStepOutcome(false, false, $"The helper failed (exit code {code}): " + (words ?? "no details were left.")),
            null => new AdminStepOutcome(false, false, "The helper did not report an exit code."),
        };
    }

    private string? ReadResultWords(AdminLayout layout, AdminVerb verb, DateTime startedUtc)
    {
        var result = AdminRunResult.TryParse(_d.Fs.ReadText(layout.ResultPath, 256 * 1024));
        if (result is null || result.Verb != AdminVerbs.Name(verb))
        {
            return null;
        }

        if (!DateTime.TryParse(result.FinishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finished) || finished < startedUtc.AddSeconds(-2))
        {
            return null;
        }

        // The words are the helper's; they are shown, never run or followed.
        return string.Join(' ', result.Lines.Where(static l => !string.IsNullOrWhiteSpace(l)).TakeLast(6));
    }

    private static string DoneSentence(AdminVerb verb) => verb switch
    {
        AdminVerb.InstallCopy => "The administrator copy is ready.",
        AdminVerb.Start => "The gateway was started with administrator rights.",
        AdminVerb.Restart => "The gateway was restarted with administrator rights.",
        AdminVerb.Stop => "The gateway was stopped.",
        AdminVerb.RepairOwnership => "Ownership was repaired.",
        _ => "Done.",
    };
}
