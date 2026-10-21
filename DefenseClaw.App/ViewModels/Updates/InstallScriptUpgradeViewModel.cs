using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>What the post-flight panel reads off the machine after the installer exits.</summary>
public sealed record PostFlightFacts(
    string? CliPath,
    bool CliInExpectedDirectory,
    string RuntimeIdentity,
    string RuntimeFeatures,
    bool? GatewayRunning,
    string GatewayText);

/// <summary>Reads <see cref="PostFlightFacts"/>. A seam: tests answer from memory, the app asks the machine.</summary>
public interface IPostFlightProbe
{
    Task<PostFlightFacts> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The production post-flight: forgets the cached executable lookups and finds the CLI again, re-reads <c>--version-json</c> and the capabilities
/// through the runtime detector (the app's normal read path for them: a read, not an Activity entry), and polls the gateway once.
/// </summary>
internal sealed class AppPostFlightProbe : IPostFlightProbe
{
    private readonly AppServices _services;

    public AppPostFlightProbe(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The directory the 1.0 installer puts the CLI in: <c>%USERPROFILE%\.local\bin</c>.</summary>
    public static string ExpectedBinDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    public async Task<PostFlightFacts> ReadAsync(CancellationToken cancellationToken)
    {
        _services.Paths.InvalidateExecutableCache();
        var cli = await _services.Paths.FindExecutableAsync(DefenseClaw.Core.Paths.DefenseClawPaths.CliExecutableName).ConfigureAwait(false);

        var runtime = await _services.Runtime.RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);

        GatewaySnapshot gateway;
        try
        {
            gateway = await _services.Monitor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed poll is "unknown", not a fault of the panel.
        catch (Exception)
#pragma warning restore CA1031
        {
            gateway = _services.Monitor.Current;
        }

        var inExpected = cli is not null &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(cli) ?? string.Empty),
                Path.TrimEndingDirectorySeparator(ExpectedBinDirectory()),
                StringComparison.OrdinalIgnoreCase);

        return new PostFlightFacts(
            cli,
            inExpected,
            RuntimeSummary.Identity(runtime),
            RuntimeSummary.Features(runtime),
            gateway.IsRunning,
            gateway.StateLabel);
    }
}

/// <summary>What the shared review dialog is currently about.</summary>
public enum InstallReviewKind
{
    None,
    Upgrade,
    Rollback,
    StartGateway,
}

/// <summary>
/// The in-app upgrade to DefenseClaw 1.0 through the release's <c>install.ps1</c> (CUST-346), as one workflow in the Updates window: what changes, a
/// read-only pre-flight, download and verify, a reviewed run in a console of its own, then a post-flight panel with a reviewed Roll back.
/// <para>
/// Nothing here starts on its own: checking is read-only, downloading changes nothing on the machine, and the run, the rollback and the gateway start
/// each sit behind the shared <see cref="CommandReview"/> (state-changing, installation guard first). The run itself is
/// <see cref="InstallScriptRunner"/>'s, and is never tied to this view-model's lifetime.
/// </para>
/// </summary>
public sealed partial class InstallScriptUpgradeViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly UpgradeRunner _upgrade;
    private readonly InstallScriptRunner _runner;
    private readonly IUpgradeProbes _probes;
    private readonly IPostFlightProbe _postFlight;
    private readonly Func<Task<CosignStatus>> _cosignProbe;
    private readonly Action<string> _reveal;
    private readonly Action<string> _open;
    private readonly ILeftoverProbes _leftovers;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();

    // Bumped by every read of the install state; a read that finds it changed was overtaken by a newer one and drops its answer.
    private int _readGeneration;

    // What the last run in this window says about the Roll back button: install.ps1 offers none after a usage error or a rollback.
    private bool _resultOffersRollback = true;
    private string? _resultRollbackNote;
    private string? _previousVersion;
    private string _installedVersion = string.Empty;

    private StagedUpgradeAsset? _staged;
    private PreflightVerification _verification = PreflightVerification.NotStarted;
    private string _verificationDetail = "install.ps1 has not been downloaded yet. Download and verify it first.";
    private string? _migrationFile;
    private bool _disposed;

    [ObservableProperty]
    private bool _isApplicable;

    [ObservableProperty]
    private string _targetVersion = "—";

    [ObservableProperty]
    private bool _isStaging;

    [ObservableProperty]
    private bool _isStaged;

    [ObservableProperty]
    private string _stagingSummary = string.Empty;

    [ObservableProperty]
    private string _stagingError = string.Empty;

    [ObservableProperty]
    private bool _hasStagingError;

    [ObservableProperty]
    private string _stagedPath = string.Empty;

    [ObservableProperty]
    private string _stagedHash = string.Empty;

    [ObservableProperty]
    private bool _hasPreflightBlock;

    [ObservableProperty]
    private string _preflightSummary = "Checking…";

    [ObservableProperty]
    private bool _isCheckingPreflight;

    /// <summary>Opt-in <c>-Yes</c>: the installer then answers its own confirmation prompts. Off by default so the operator answers them in its console.</summary>
    [ObservableProperty]
    private bool _useYes;

    [ObservableProperty]
    private bool _isReviewVisible;

    [ObservableProperty]
    private CommandReview? _review;

    [ObservableProperty]
    private CommandReviewPhase _reviewPhase = CommandReviewPhase.Review;

    [ObservableProperty]
    private InstallReviewKind _reviewKind;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _runStatus = string.Empty;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _exitLabel = string.Empty;

    [ObservableProperty]
    private string _exitToneKey = "Neutral";

    [ObservableProperty]
    private string _exitMessage = string.Empty;

    /// <summary>One line pointing at the fix when the installer's reason names an unresolved connector recovery; empty otherwise.</summary>
    [ObservableProperty]
    private string _exitHint = string.Empty;

    [ObservableProperty]
    private bool _hasExitHint;

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private string _logPath = string.Empty;

    [ObservableProperty]
    private bool _hasLog;

    [ObservableProperty]
    private bool _isReadingPostFlight;

    [ObservableProperty]
    private bool _hasPostFlight;

    [ObservableProperty]
    private string _cliPathText = string.Empty;

    [ObservableProperty]
    private string _cliPathToneKey = "Neutral";

    [ObservableProperty]
    private string _runtimeText = string.Empty;

    [ObservableProperty]
    private string _featuresText = string.Empty;

    [ObservableProperty]
    private string _gatewayText = string.Empty;

    [ObservableProperty]
    private string _gatewayToneKey = "Neutral";

    [ObservableProperty]
    private bool _gatewayDown;

    [ObservableProperty]
    private string _migrationText = string.Empty;

    [ObservableProperty]
    private bool _hasMigration;

    [ObservableProperty]
    private bool _rollbackAvailable;

    [ObservableProperty]
    private string _configVersionText = string.Empty;

    [ObservableProperty]
    private string _lastInstallText = string.Empty;

    [ObservableProperty]
    private string _lastInstallToneKey = "Neutral";

    [ObservableProperty]
    private string _lastInstallLogPath = string.Empty;

    [ObservableProperty]
    private bool _hasLastInstallLog;

    /// <summary>"Read at 14:05": when the install state below was last read off the machine.</summary>
    [ObservableProperty]
    private string _readAtText = string.Empty;

    [ObservableProperty]
    private string _rollbackNote = string.Empty;

    [ObservableProperty]
    private string _reviewError = string.Empty;

    [ObservableProperty]
    private bool _hasReviewError;

    public InstallScriptUpgradeViewModel(
        AppServices services,
        UpgradeRunner upgrade,
        InstallScriptRunner runner,
        IUpgradeProbes? probes = null,
        IPostFlightProbe? postFlight = null,
        Func<Task<CosignStatus>>? cosignProbe = null,
        Action<string>? reveal = null,
        Action<string>? open = null,
        ILeftoverProbes? leftovers = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _leftovers = leftovers ?? new RegistryLeftoverProbes();
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _upgrade = upgrade ?? throw new ArgumentNullException(nameof(upgrade));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probes = probes ?? new SystemUpgradeProbes();
        _postFlight = postFlight ?? new AppPostFlightProbe(services);
        _cosignProbe = cosignProbe ?? (() => UpgradeRunner.DetectCosignAsync());
        _reveal = reveal ?? RevealInExplorer;
        _open = open ?? OpenWithShell;

        _services.Installation.Changed += OnInstallationChanged;
    }

    /// <summary>Raised after a run that installed DefenseClaw (exit 0, 3 or 4): the window re-runs its version check.</summary>
    public event EventHandler? UpgradeSucceeded;

    public ObservableCollection<PreflightRow> PreflightRows { get; } = new();

    public IReadOnlyList<string> WhatChanges => InstallScriptPlan.WhatChanges;

    /// <summary>The version install.ps1 is told to install (<c>X.Y.Z</c>), or empty when the target is not one it accepts.</summary>
    public string InstallVersion => InstallScriptPlan.TryNormalizeVersion(TargetVersion, out var v) ? v : string.Empty;

    public string StagingRoot => _upgrade.StagingRoot;

    /// <summary>The upgrade card's heading line.</summary>
    public string Headline => IsApplicable || !IsPostUpgradeMode ? $"Upgrade to DefenseClaw {TargetVersion}" : "This 1.0 install: checks and rollback";

    /// <summary>Why the section exists, in one paragraph: this release is installed by install.ps1 and has no Setup exe.</summary>
    public string ChannelNote =>
        $"DefenseClaw {TargetVersion} is installed by the release's install.ps1; it publishes no DefenseClawSetup-x64.exe. This app downloads it, " +
        "checks its SHA-256 against the release's checksums.txt (and that file's sigstore signature when cosign is installed), shows you the exact " +
        "command, and only then runs it in a console window of its own, where the installer asks its questions. Nothing runs until you confirm.";

    /// <summary>The installation guard's reason this upgrade cannot run, or null.</summary>
    public string? BlockedReason => _services.Installation.UpgradeBlockedReason;

    public bool HasBlockedReason => BlockedReason is not null;

    /// <summary>
    /// True on a machine that is already on 1.0 and still holds the install it replaced (<c>previous\</c>): the card then offers the post-upgrade
    /// checks and the reviewed Roll back, with no upgrade to run.
    /// </summary>
    [ObservableProperty]
    private bool _isPostUpgradeMode;

    /// <summary>Read-only warnings about what the upgrade left behind (the stale sign-in entry, the Setup's Add/Remove Programs entry).</summary>
    public ObservableCollection<PreflightRow> LeftoverRows { get; } = new();

    public bool HasLeftovers => LeftoverRows.Count > 0;

    /// <summary>The version whose install.ps1 is downloaded: the upgrade target, or (post-upgrade mode, for a rollback) the installed one.</summary>
    public string ScriptVersion =>
        InstallScriptPlan.TryNormalizeVersion(IsApplicable ? TargetVersion : _installedVersion, out var v) ? v : string.Empty;

    public bool CanDownload => (IsApplicable || IsPostUpgradeMode) && !IsStaging && !IsRunning && BlockedReason is null && ScriptVersion.Length > 0;

    /// <summary>The panel of facts read off the machine: after a run, or on a machine already on 1.0.</summary>
    public bool ShowPostFlight => HasResult || IsPostUpgradeMode;

    public bool CanReview => IsStaged && !IsRunning && !IsStaging && !HasPreflightBlock && BlockedReason is null;

    /// <summary>"Roll back to 1.0.0…": the button names the install it goes back to (<c>previous\VERSION</c>); plain "Roll back…" when that is not known.</summary>
    public string RollbackLabel => _previousVersion is { } v ? $"Roll back to {v}…" : "Roll back…";

    /// <summary>Why Roll back is off (also its tooltip), or null when it is on. The installation guard's reason comes before any other.</summary>
    public string? RollbackBlockedReason =>
        BlockedReason is { } blocked ? blocked
        : IsRunning ? "The installer is still running."
        : !RollbackAvailable ? RollbackNote
        : _staged is null ? "Download and verify install.ps1 first (Download and verify, above); the rollback runs that verified script."
        : null;

    public bool CanRollback => (HasResult || IsPostUpgradeMode) && !IsRunning && RollbackAvailable && _staged is not null && BlockedReason is null;

    /// <summary>Keeps the card on screen after a run, when the update check no longer lists an update.</summary>
    public bool IsVisible => IsApplicable || IsRunning || HasResult || IsPostUpgradeMode;

    partial void OnIsPostUpgradeModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPostFlight));
        OnPropertyChanged(nameof(ScriptVersion));
        RaiseState();
    }

    public bool IsConfirmable => !IsRunning && Review is { IsBlocked: false };

    partial void OnIsApplicableChanged(bool value) => RaiseState();

    partial void OnIsStagedChanged(bool value) => RaiseState();

    partial void OnIsStagingChanged(bool value) => RaiseState();

    partial void OnIsRunningChanged(bool value) => RaiseState();

    partial void OnHasResultChanged(bool value) => RaiseState();

    /// <summary>The "Run" half of the card: only when there is an upgrade to run.</summary>
    public bool ShowUpgradeSteps => IsApplicable;

    partial void OnHasPreflightBlockChanged(bool value) => RaiseState();

    partial void OnRollbackAvailableChanged(bool value) => RaiseState();

    partial void OnTargetVersionChanged(string value)
    {
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ChannelNote));
        OnPropertyChanged(nameof(InstallVersion));
    }

    partial void OnReviewChanged(CommandReview? value) => OnPropertyChanged(nameof(IsConfirmable));

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ShowPostFlight));
        OnPropertyChanged(nameof(ShowUpgradeSteps));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ScriptVersion));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(RollbackBlockedReason));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsConfirmable));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(HasBlockedReason));
        OpenUpgradeReviewCommand.NotifyCanExecuteChanged();
        DownloadAndVerifyCommand.NotifyCanExecuteChanged();
        OpenRollbackReviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RestartAppBlockedReason));
        RestartAppCommand.NotifyCanExecuteChanged();
    }

    private void OnInstallationChanged(object? sender, EventArgs e)
    {
        RaiseState();
        _ = RefreshPreflightAsync();
    }

    /// <summary>
    /// Feeds the section the latest release check. The channel is decided here, before any verification: 1.0.0 and later, or a release with no
    /// Setup exe, is upgraded with install.ps1. A new target drops the staged script.
    /// </summary>
    public void ApplyCheck(UpdateCheckResult result, UpgradeChannel layoutDefault)
    {
        ArgumentNullException.ThrowIfNull(result);

        var target = result.LatestVersion ?? "—";
        var channel = UpgradeRunner.SelectChannel(result.LatestVersion, result.Assets.Select(a => a.Name), layoutDefault);

        var installed = result.InstalledVersion ?? string.Empty;
        if (!string.Equals(target, TargetVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(installed, _installedVersion, StringComparison.OrdinalIgnoreCase))
        {
            ClearStaging();
        }

        _installedVersion = installed;
        TargetVersion = target;
        IsApplicable = result.IsUpdateAvailable && channel == UpgradeChannel.InstallScript;
        if (IsApplicable)
        {
            _ = RefreshPreflightAsync();
        }

        // Already on 1.0 and still holding the install it replaced: offer the post-upgrade checks and the reviewed rollback.
        IsPostUpgradeMode = !IsApplicable && !IsRunning && !HasResult &&
            UpgradeRunner.TryParseReleaseVersion(installed, out var installedVersion) &&
            installedVersion >= UpgradeRunner.InstallScriptFirstVersion &&
            File.Exists(Path.Combine(_services.Paths.DataDirectory, "previous", "VERSION"));
        if (IsPostUpgradeMode)
        {
            // Every check re-reads the state (the first one after the window opens, Check again, the one after a run): nothing is kept from
            // the last time, because previous\, migration-v9.json and the install log are changed by the installer, not by this app.
            ApplyRollbackState(InstallScriptPlan.CheckRollback(_services.Paths.DataDirectory));
            _ = ReadPostFlightAsync();
        }
    }

    /// <summary>Re-reads the install state: the Refresh button, and the window being brought to the front again.</summary>
    [RelayCommand]
    public Task RefreshState() => ShowPostFlight ? ReadPostFlightAsync() : Task.CompletedTask;

    /// <summary>Sets the Roll back button's availability and its explanation, folding in what the last run in this window said.</summary>
    private void ApplyRollbackState(RollbackAvailability rollback)
    {
        RollbackAvailable = rollback.IsAvailable && (!HasResult || _resultOffersRollback);
        RollbackNote = HasResult && _resultRollbackNote is { } note ? note : rollback.Note;
        OnPropertyChanged(nameof(RollbackBlockedReason));
    }

    private void ClearStaging()
    {
        _staged = null;
        IsStaged = false;
        StagedPath = string.Empty;
        StagedHash = string.Empty;
        StagingSummary = string.Empty;
        StagingError = string.Empty;
        HasStagingError = false;
        _verification = PreflightVerification.NotStarted;
        _verificationDetail = "install.ps1 has not been downloaded yet. Download and verify it first.";
    }

    // ------------------------------------------------------------------------------------------------ pre-flight

    /// <summary>Runs the read-only checklist (off the UI thread) and shows the rows.</summary>
    [RelayCommand]
    public async Task RefreshPreflightAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsCheckingPreflight = true;
        try
        {
            var dataDirectory = _services.Paths.DataDirectory;
            var blocked = _services.Installation.UpgradeBlockedReason;
            var verification = _verification;
            var detail = _verificationDetail;

            var report = await Task.Run(() => InstallScriptPreflight.Evaluate(_probes, blocked, dataDirectory, verification, detail)).ConfigureAwait(true);

            PreflightRows.Clear();
            foreach (var row in report.Rows)
            {
                PreflightRows.Add(row);
            }

            HasPreflightBlock = report.HasBlock;
            PreflightSummary = report.HasBlock
                ? "Blocked: " + report.BlockReason
                : report.HasWarning
                    ? "Ready, with warnings. Read them before you run."
                    : "All checks passed.";
        }
        finally
        {
            IsCheckingPreflight = false;
        }
    }

    // ------------------------------------------------------------------------------------------------ download

    /// <summary>Downloads install.ps1 and checksums.txt (+ the signature files the verifier needs) into staging and verifies them. Changes nothing on the machine.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    public async Task DownloadAndVerifyAsync()
    {
        if (!CanDownload)
        {
            return;
        }

        ClearStaging();
        IsStaging = true;
        StagingSummary = $"Downloading install.ps1 from release {ScriptVersion}…";

        try
        {
            string? cosignPath = null;
            try
            {
                var cosign = await _cosignProbe().ConfigureAwait(true);
                cosignPath = cosign.Availability == CosignAvailability.Missing ? null : cosign.Path;
            }
            catch (OperationCanceledException)
            {
                // No cosign answer; the signature is then reported as not verified.
            }

            var result = await _upgrade.DownloadAndVerifyInstallScriptAsync(ScriptVersion, cosignPath, _cts.Token).ConfigureAwait(true);
            ApplyStaging(result);
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-download.
        }
        finally
        {
            IsStaging = false;
        }

        await RefreshPreflightAsync().ConfigureAwait(true);
    }

    private void ApplyStaging(UpgradeStagingResult result)
    {
        if (result.Succeeded && result.Asset is { } asset)
        {
            _staged = asset;
            IsStaged = true;
            StagedPath = asset.FilePath;
            StagedHash = asset.Sha256;
            StagingSummary = result.Summary;
            StagingError = string.Empty;
            HasStagingError = false;
            _verification = asset.ChecksumsSignature.IsVerified ? PreflightVerification.Verified : PreflightVerification.HashOnly;
            _verificationDetail = result.Summary;
            return;
        }

        _staged = null;
        IsStaged = false;
        StagingSummary = result.Summary;
        StagingError = result.ErrorMessage ?? result.Summary;
        HasStagingError = true;
        _verification = PreflightVerification.Failed;
        _verificationDetail = result.ErrorMessage ?? result.Summary;
    }

    // ------------------------------------------------------------------------------------------------ review

    private static string ShownExecutable => "powershell.exe";

    /// <summary>Builds the review of the upgrade (exact argv, state-changing, installation guard first).</summary>
    public CommandReview BuildUpgradeReview()
    {
        var staged = _staged ?? throw new InvalidOperationException("Nothing is staged.");
        var argv = InstallScriptPlan.BuildArgv(staged.FilePath, InstallVersion, UseYes);

        var warnings = new List<CommandReviewWarning>
        {
            CommandReviewWarning.GatewayRestart(
                "The installer stops the DefenseClaw gateway, replaces the install and starts the gateway again. Agents that use DefenseClaw hooks lose the " +
                "gateway for the length of the upgrade."),
            new CommandReviewWarning("Runs in its own window",
                "install.ps1 opens a console window of its own and keeps running if you close this app. " + (UseYes
                    ? "With -Yes it answers its own confirmation prompts."
                    : "It will ask you to confirm in that window (and may ask which agent to guard); answer there.")),
        };

        foreach (var row in PreflightRows.Where(r => r.Status == PreflightStatus.Warn))
        {
            warnings.Add(new CommandReviewWarning(row.Title, row.Reason));
        }

        return new CommandReview
        {
            Title = $"Upgrade DefenseClaw to {InstallVersion} with install.ps1?",
            Steps = [new CommandReviewStep(argv, "Runs the verified install.ps1 for this release.", CommandTier.StateChanging, ShownExecutable)],
            Summary = "Changes: " + string.Join(" ", InstallScriptPlan.WhatChanges),
            RestartsGateway = true,
            Warnings = warnings,
            ConfirmLabel = "Run install.ps1",
        }.GuardedBy(_services.Installation);
    }

    /// <summary>Builds the review of the rollback.</summary>
    public CommandReview BuildRollbackReview()
    {
        var staged = _staged ?? throw new InvalidOperationException("Nothing is staged.");
        var argv = InstallScriptPlan.BuildRollbackArgv(staged.FilePath, UseYes);

        return new CommandReview
        {
            Title = "Roll DefenseClaw back with install.ps1 -Rollback?",
            Steps = [new CommandReviewStep(argv, "Swaps back to the install the last upgrade replaced.", CommandTier.StateChanging, ShownExecutable)],
            Summary = "The installer exchanges the live install and previous\\ by renaming, restarts the gateway, and writes its own log.",
            RestartsGateway = true,
            Warnings =
            [
                CommandReviewWarning.GatewayRestart(),
                new("Runs in its own window", "install.ps1 -Rollback opens a console window of its own and may ask you to confirm there."),
            ],
            ConfirmLabel = "Run rollback",
        }.GuardedBy(_services.Installation);
    }

    /// <summary>Builds the review of <c>defenseclaw-gateway start</c> (the same argv and wording every other Start gateway control uses).</summary>
    public CommandReview BuildStartGatewayReview() => new CommandReview
    {
        Title = GatewayControl.Title(GatewayAction.Start) + "?",
        Steps = [new CommandReviewStep(GatewayControl.Argv(GatewayAction.Start), GatewayControl.Summary(GatewayAction.Start), CommandTier.StateChanging, GatewayControl.Executable)],
        Summary = GatewayControl.ReviewNote(GatewayAction.Start),
        ConfirmLabel = GatewayControl.Title(GatewayAction.Start),
    }.GuardedBy(_services.Installation);

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void OpenUpgradeReview() => ShowReview(InstallReviewKind.Upgrade, BuildUpgradeReview());

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private void OpenRollbackReview() => ShowReview(InstallReviewKind.Rollback, BuildRollbackReview());

    [RelayCommand]
    private void OpenStartGatewayReview() => ShowReview(InstallReviewKind.StartGateway, BuildStartGatewayReview());

    private void ShowReview(InstallReviewKind kind, CommandReview review)
    {
        HasReviewError = false;
        ReviewError = string.Empty;
        ReviewKind = kind;
        Review = review;
        ReviewPhase = CommandReviewPhase.Review;
        IsReviewVisible = true;
    }

    [RelayCommand]
    private void CancelReview()
    {
        if (ReviewPhase == CommandReviewPhase.Running)
        {
            return;
        }

        IsReviewVisible = false;
        ReviewKind = InstallReviewKind.None;
        Review = null;
    }

    /// <summary>The review's confirm button: runs what was reviewed.</summary>
    [RelayCommand]
    public async Task ConfirmReviewAsync()
    {
        if (Review is not { } review || ReviewPhase != CommandReviewPhase.Review)
        {
            return;
        }

        var kind = ReviewKind;

        // The guard is asked again at the moment of the run: the installation can have gone read-only while the dialog was open.
        var step = review.Steps[0];
        if (_services.Installation.ReasonFor(step.Executable, step.Argv) is { } refusal)
        {
            _ = _services.Cli.RecordRefusal(step.Executable, step.Argv, refusal);
            CancelReview();
            return;
        }

        if (kind == InstallReviewKind.StartGateway)
        {
            await StartGatewayAsync().ConfigureAwait(true);
            return;
        }

        if (_staged is null)
        {
            CancelReview();
            return;
        }

        IsReviewVisible = false;
        ReviewPhase = CommandReviewPhase.Running;
        await RunInstallerAsync(kind == InstallReviewKind.Rollback).ConfigureAwait(true);
        ReviewPhase = CommandReviewPhase.Review;
        ReviewKind = InstallReviewKind.None;
        Review = null;
    }

    private async Task StartGatewayAsync()
    {
        ReviewPhase = CommandReviewPhase.Running;
        try
        {
            var invocation = await _services.Cli.RunGatewayAsync(GatewayControl.Argv(GatewayAction.Start)).ConfigureAwait(true);
            GatewayText = invocation.Succeeded
                ? "Gateway start requested; checking its health…"
                : "The gateway did not start (see the Activity panel).";
        }
        catch (CliNotFoundException ex)
        {
            GatewayText = ex.Message;
        }
        finally
        {
            ReviewPhase = CommandReviewPhase.Review;
            IsReviewVisible = false;
            ReviewKind = InstallReviewKind.None;
            Review = null;
        }

        await ReadPostFlightAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------------------ run

    private async Task RunInstallerAsync(bool rollback)
    {
        var staged = _staged!;
        HasResult = false;
        HasPostFlight = false;
        IsRunning = true;
        RunStatus = rollback
            ? "install.ps1 -Rollback is running in its own console window. Answer its questions there."
            : $"install.ps1 is upgrading to {InstallVersion} in its own console window. Answer its questions there; this app has paused monitoring and released its database handles.";

        InstallScriptRunResult result;
        try
        {
            result = await _runner.RunAsync(new InstallScriptRunRequest(
                staged.FilePath,
                staged.Sha256,
                InstallVersion,
                UseYes,
                rollback,
                _services.Paths.DataDirectory)).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            RunStatus = string.Empty;
        }

        ShowResult(result);

        if (result.Started)
        {
            await ReadPostFlightAsync().ConfigureAwait(true);
            if (result.Exit.Outcome is InstallOutcome.Success or InstallOutcome.InstalledNeedsAttention)
            {
                UpgradeSucceeded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ShowResult(InstallScriptRunResult result)
    {
        HasResult = true;
        ExitLabel = result.Started && result.ExitCode is { } code
            ? $"{result.Exit.Label} (exit {code.ToString(CultureInfo.InvariantCulture)})"
            : result.Exit.Label;
        ExitToneKey = result.Exit.ToneKey;
        ExitMessage = result.FailureReason is { } reason ? reason + " " + result.Exit.Message : result.Exit.Message;
        if (result.InstallerReason is { Length: > 0 } installerSaid)
        {
            ExitMessage = "The installer said: " + installerSaid + " " + ExitMessage;
        }

        ExitHint = InstallScriptPlan.HintFor(result.InstallerReason) ?? string.Empty;
        HasExitHint = ExitHint.Length > 0;
        DurationText = result.Started ? $"{result.Duration.TotalSeconds:0} s" : string.Empty;
        LogPath = result.LogPath ?? string.Empty;
        HasLog = result.LogPath is not null;

        _resultOffersRollback = result.Exit.OffersRollback;
        _resultRollbackNote = result.Exit.Outcome == InstallOutcome.UsageError
            ? "Nothing was changed, so there is nothing to roll back."
            : result.Rollback
                ? "The rollback has run. Running it again goes forward to the newer install."
                : null;
        ApplyRollbackState(InstallScriptPlan.CheckRollback(_services.Paths.DataDirectory));
    }

    private async Task ReadPostFlightAsync()
    {
        var generation = ++_readGeneration;
        IsReadingPostFlight = true;
        try
        {
            var facts = await _postFlight.ReadAsync(_disposed ? new CancellationToken(canceled: true) : _cts.Token).ConfigureAwait(true);

            // The files the installer leaves are read in one go, off the UI thread, after the CLI and the gateway answered.
            var dataDirectory = _services.Paths.DataDirectory;
            var activity = _services.Cli.Activity;
            var now = _time.GetLocalNow();
            var state = await Task.Run(() => InstallStateSnapshot.Read(dataDirectory, activity, now)).ConfigureAwait(true);
            var leftovers = await Task.Run(() => PostUpgradeLeftovers.Evaluate(_leftovers)).ConfigureAwait(true);
            if (generation != _readGeneration)
            {
                return;
            }

            CliPathText = facts.CliPath is null
                ? "defenseclaw was not found. Open a new terminal or restart this app so the updated PATH is read."
                : facts.CliInExpectedDirectory
                    ? facts.CliPath
                    : facts.CliPath + "  (expected under " + AppPostFlightProbe.ExpectedBinDirectory() + ")";
            CliPathToneKey = facts.CliPath is null ? "Bad" : facts.CliInExpectedDirectory ? "Ok" : "Warn";
            RuntimeText = facts.RuntimeIdentity;
            FeaturesText = facts.RuntimeFeatures;
            GatewayText = facts.GatewayText;
            GatewayDown = facts.GatewayRunning == false;
            GatewayToneKey = facts.GatewayRunning switch { true => "Ok", false => "Bad", _ => "Neutral" };

            ConfigVersionText = state.ConfigVersionText;
            _migrationFile = state.Migration?.FilePath;
            HasMigration = state.Migration is not null;
            MigrationText = state.MigrationText;
            LastInstallText = state.LastInstallText;
            LastInstallToneKey = state.LastInstallToneKey;
            LastInstallLogPath = state.LastInstallLogPath ?? string.Empty;
            HasLastInstallLog = state.LastInstallLogPath is not null;

            _previousVersion = state.PreviousVersion;
            OnPropertyChanged(nameof(RollbackLabel));
            ApplyRollbackState(state.Rollback);

            LeftoverRows.Clear();
            foreach (var row in leftovers)
            {
                LeftoverRows.Add(row);
            }

            OnPropertyChanged(nameof(HasLeftovers));
            ReadAtText = "Read at " + now.ToString("HH:mm", CultureInfo.CurrentCulture);
            HasPostFlight = true;
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            if (generation == _readGeneration)
            {
                IsReadingPostFlight = false;
            }

            OnPropertyChanged(nameof(CanStartGateway));
        }
    }

    // ------------------------------------------------------------------------------------------------ restart

    /// <summary>Why "Restart DefenseClaw for Windows" is off (this card's own run, or any state-changing run in Activity), or null. Also the button's tooltip.</summary>
    public string? RestartAppBlockedReason =>
        IsRunning ? "The installer is still running. Restart the app when it finishes." : _services.AppRestart.BlockedReason;

    /// <summary>What the last click said when it did not restart (the run that started since the bar was drawn).</summary>
    [ObservableProperty]
    private string _restartNote = string.Empty;

    private bool CanRestartApp() => RestartAppBlockedReason is null;

    /// <summary>Starts a new copy of this app and exits this one (the shell's usual exit checks first). See <see cref="AppRestart"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartApp))]
    private void RestartApp() => RestartNote = _services.AppRestart.Request() ?? string.Empty;

    /// <summary>The Start gateway button: the post-flight found the gateway down.</summary>
    public bool CanStartGateway => HasPostFlight && GatewayDown && !IsRunning && BlockedReason is null;

    partial void OnGatewayDownChanged(bool value) => OnPropertyChanged(nameof(CanStartGateway));

    partial void OnHasPostFlightChanged(bool value) => OnPropertyChanged(nameof(CanStartGateway));

    // ------------------------------------------------------------------------------------------------ files

    [RelayCommand]
    private void ShowStagedFile()
    {
        if (_staged is { } staged && File.Exists(staged.FilePath))
        {
            _reveal(staged.FilePath);
        }
    }

    /// <summary>Runs a pre-flight row's action. Only "open Overview" exists: it shows the panel and changes nothing.</summary>
    [RelayCommand]
    private void RunPreflightAction(string? actionId)
    {
        if (string.Equals(actionId, PreflightActionIds.OpenOverview, StringComparison.Ordinal))
        {
            _services.Navigation.Request(new NavigationRequest("overview"));
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (HasLog && File.Exists(LogPath))
        {
            _open(LogPath);
        }
    }

    [RelayCommand]
    private void OpenLastInstallLog()
    {
        if (HasLastInstallLog && File.Exists(LastInstallLogPath))
        {
            _open(LastInstallLogPath);
        }
    }

    [RelayCommand]
    private void ShowMigrationFile()
    {
        if (_migrationFile is { } path && File.Exists(path))
        {
            _reveal(path);
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No Explorer to show it in.
        }
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No handler for a .log file; the path is on screen.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Installation.Changed -= OnInstallationChanged;

        // Only the download and the post-run reads are abandoned; a running installer is its own process and never touched.
        _cts.Cancel();
        _cts.Dispose();
    }
}
