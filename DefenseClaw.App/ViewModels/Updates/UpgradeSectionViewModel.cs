using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// Drives the Updates window's upgrade section: channel choice, cosign preflight,
/// download-and-verify, the confirm overlay, the live console, and the post-run version
/// confirmation.
/// <para>
/// <b>Two channels.</b> <see cref="UpgradeChannel.SetupInstaller"/> stages and runs the release's
/// <c>DefenseClawSetup-x64.exe</c>; <see cref="UpgradeChannel.ResolverScript"/> stages and runs
/// its <c>defenseclaw-upgrade.ps1</c>. The default comes from
/// <see cref="UpgradeRunner.DetectResolverLayout"/>, which probes for the venv the resolver
/// assumes — on a Setup-based install that venv is absent and the script fails, so the installer
/// is recommended. Switching channels drops any staging, so a script verified for one channel can
/// never be run down the other.
/// </para>
/// <para>
/// <b>cosign gates one channel only.</b> The resolver shells out to cosign to verify the signed
/// release contract and refuses without it. The installer does not use cosign at all, so
/// requiring it there would be a gate on nothing.
/// </para>
/// <para>
/// <b>Three separate clicks, never one.</b> Checking, staging and running are distinct user
/// actions with distinct buttons; nothing here starts on its own, and the run is gated behind an
/// overlay that prints the exact argv first. The run itself goes through
/// <see cref="CliRunner.RunExecutableAsync"/>, so the invocation lands in the Activity panel with
/// argv, streaming output and exit code like every other mutation this app makes.
/// </para>
/// <para>
/// <b>Live output.</b> <see cref="CliRunner.OutputReceived"/> carries no invocation id, so — as
/// the Activity panel and the wizards do — this binds to the <see cref="CliInvocation"/> the
/// runner hands back on <see cref="UpgradeRunner.InvocationStarted"/> and polls it on a
/// dispatcher timer. It pulls the delta through <see cref="CliInvocation.CopyNewLines"/>
/// rather than re-snapshotting: an upgrade is the longest, noisiest run this app makes, and a
/// full transcript copy four times a second is the one place that cost is actually felt. A
/// quiet installer run legitimately produces almost no output at all.
/// </para>
/// <para>
/// <b>A run is never tied to this view-model's lifetime.</b> The Setup installer has no journal
/// and no rollback, and the resolver's rollback only covers a failure it observes itself, so
/// killing either mid-install can leave the machine with a half-replaced DefenseClaw — strictly
/// worse than an install that finishes with nobody watching. <see cref="RunUpgradeAsync"/> therefore
/// passes <see cref="CancellationToken.None"/> to <see cref="UpgradeRunner.RunAsync"/> and
/// <see cref="Dispose"/> does not touch it; <see cref="_cts"/> covers only the download/staging
/// and the post-run re-poll, which are safe to abandon. Nothing that closes the window can end a
/// run: <c>UpdatesWindow.OnClosing</c> refuses a close while <see cref="IsRunning"/>, and the closes
/// it cannot refuse (the owner <c>MainWindow</c> closing on tray Exit, <c>Application.Shutdown</c>)
/// only reach <see cref="Dispose"/>, which leaves the run alone. The runner's
/// <see cref="CliRunOptions.Installer"/> options in turn exempt the run from
/// <c>CliRunner.Shutdown</c> at app exit.
/// </para>
/// </summary>
public sealed partial class UpgradeSectionViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OutputTick = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly UpgradeRunner _runner;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Lifetime token for the work that is safe to abandon when the window goes — the download
    /// and verify, and the post-run gateway re-poll. Deliberately <b>never</b> handed to the
    /// upgrade run itself; see the type documentation. Read it through <see cref="LifetimeToken"/>
    /// after any await, because <see cref="CancellationTokenSource.Token"/> throws once this is
    /// disposed, and a run outlives <see cref="Dispose"/> by design.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Reused between ticks so the 250ms console poll allocates nothing in steady state —
    /// it is cleared and refilled with only the delta, never the whole transcript.
    /// </summary>
    private readonly List<CliOutputLine> _outputBuffer = new();

    private StagedUpgradeAsset? _staged;
    private CliInvocation? _invocation;

    /// <summary>
    /// Position in <see cref="_invocation"/>'s monotonic append sequence, not an index into
    /// its retained lines — see <see cref="CliInvocation.CopyNewLines"/>. Reset to 0 with the
    /// console when a new run starts.
    /// </summary>
    private int _outputCursor;

    /// <summary>
    /// Ids of the runner invocations — anything this app shelled out, this section's own run
    /// included — that have started and not yet finished. Tracked by id rather than as a bare
    /// counter: a counter seeded at zero goes to -1 when a command that was already running when
    /// the window opened completes, and <see cref="IsBusy"/> then under-reports for as long as the
    /// window lives. A completion is only counted against a start that was actually seen (or
    /// seeded from <see cref="CliRunner.Activity"/> in the constructor). Guarded by
    /// <see cref="_cliGate"/>, because the runner raises its events on subprocess threads.
    /// </summary>
    private readonly HashSet<string> _cliInFlight = new(StringComparer.Ordinal);

    private readonly object _cliGate = new();

    /// <summary>True once a run's close has been refused, so the banner can say the click was noticed.</summary>
    private bool _closeRefused;

    private bool _checksumsSigstoreSigned;
    private string? _versionBeforeUpgrade;

    /// <summary>The channel the in-flight (or last) run used. The console outlives a channel switch.</summary>
    private UpgradeChannel _ranChannel;

    /// <summary>Last rendered progress string, touched only on the download worker thread.</summary>
    private string _lastProgressText = string.Empty;

    private bool _disposed;

    /// <summary>Bumped per cosign probe; only the newest probe's answer is applied (a second click must not be overwritten by the first).</summary>
    private int _cosignProbeVersion;

    private readonly Func<Task<CosignStatus>> _cosignProbe;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _targetVersion = "—";

    /// <summary>
    /// Which channel the buttons act on. Seeded from <see cref="UpgradeRunner.DetectResolverLayout"/>
    /// at construction; the operator can override it, and switching clears any staging.
    /// </summary>
    [ObservableProperty]
    private UpgradeChannel _selectedChannel;

    /// <summary>Why that default was chosen, in the layout's own terms. Shown under the picker.</summary>
    [ObservableProperty]
    private string _channelReasonText = string.Empty;

    /// <summary>
    /// Percentage and megabytes for the installer download. Empty on the resolver channel — a
    /// ~216 KB fetch is over before a progress line is worth drawing.
    /// </summary>
    [ObservableProperty]
    private string _downloadProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasDownloadProgress;

    [ObservableProperty]
    private string _cosignLabel = "Checking…";

    [ObservableProperty]
    private string _cosignDetail = string.Empty;

    [ObservableProperty]
    private string _cosignBadgeKey = "Neutral";

    [ObservableProperty]
    private string _cosignGuidance = string.Empty;

    [ObservableProperty]
    private bool _hasCosignGuidance;

    [ObservableProperty]
    private bool _isCosignReady;

    [ObservableProperty]
    private bool _isStaging;

    [ObservableProperty]
    private bool _isStaged;

    [ObservableProperty]
    private string _stagingSummary = string.Empty;

    [ObservableProperty]
    private string _verificationBadgeKey = "Neutral";

    [ObservableProperty]
    private string _stagingError = string.Empty;

    [ObservableProperty]
    private bool _hasStagingError;

    [ObservableProperty]
    private string _stagedPath = string.Empty;

    [ObservableProperty]
    private string _stagedSizeText = string.Empty;

    [ObservableProperty]
    private string _computedHash = string.Empty;

    [ObservableProperty]
    private string _expectedHash = string.Empty;

    [ObservableProperty]
    private string _checksumsSourceNote = string.Empty;

    [ObservableProperty]
    private string _commandPreview = string.Empty;

    [ObservableProperty]
    private bool _isConfirmVisible;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasRun;

    [ObservableProperty]
    private bool _hasOutput;

    /// <summary>True from the moment a run starts; the console stays on screen afterwards.</summary>
    [ObservableProperty]
    private bool _isConsoleVisible;

    [ObservableProperty]
    private bool _isOutputPaused;

    [ObservableProperty]
    private string _exitBadgeText = string.Empty;

    [ObservableProperty]
    private string _exitBadgeKey = "Neutral";

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private bool _showRollbackGuidance;

    [ObservableProperty]
    private string _versionConfirmation = string.Empty;

    [ObservableProperty]
    private bool _hasVersionConfirmation;

    /// <param name="cosignProbe">Test seam: the cosign lookup. Defaults to <see cref="UpgradeRunner.DetectCosignAsync"/> (on the pool).</param>
    public UpgradeSectionViewModel(
        AppServices services,
        UpgradeRunner runner,
        Func<Task<CosignStatus>>? cosignProbe = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _cosignProbe = cosignProbe ?? (() => UpgradeRunner.DetectCosignAsync());
        _dispatcher = Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer { Interval = OutputTick };
        _timer.Tick += (_, _) => PullOutput();

        _runner.InvocationStarted += OnUpgradeInvocationStarted;
        _services.Cli.InvocationStarted += OnAnyInvocationStarted;
        _services.Cli.InvocationCompleted += OnAnyInvocationCompleted;

        // Seeded after subscribing, never before: a command that starts or finishes in between is
        // then seen by the handlers rather than lost between the snapshot and the subscription.
        SeedCliInFlight();

        // Probe the layout before anything else: it decides which channel the section opens on,
        // and the cosign badge below is only a gate on one of them.
        var layout = UpgradeRunner.DetectResolverLayout();
        SelectedChannel = layout.RecommendedChannel;
        _ranChannel = layout.RecommendedChannel;
        ChannelReasonText = layout.Detail;

        // Off the UI thread: the probe walks PATH, and one dead network entry stalls it for ~40 s. The badge reads
        // "Checking…" (its initial value) until the answer lands.
        _ = RefreshCosignAsync();
    }

    /// <summary>
    /// Raised after a resolver run exits 0, once the gateway has been re-polled. The window's
    /// view-model re-runs the version check on this so the installed version is re-resolved from
    /// the freshly upgraded binaries rather than from what this app remembered.
    /// </summary>
    public event EventHandler? UpgradeSucceeded;

    /// <summary>Resolver output, line by line, oldest first.</summary>
    public ObservableCollection<CliOutputRow> Output { get; } = new();

    /// <summary>The binary the confirm overlay names, resolved rather than trusted to PATH.</summary>
    public string PowerShellPath => UpgradeRunner.ResolvePowerShellPath();

    public string StagingRoot => _runner.StagingRoot;

    /// <summary>Convenience for bindings and gates: true when the Setup exe channel is selected.</summary>
    public bool IsInstallerChannel => SelectedChannel == UpgradeChannel.SetupInstaller;

    /// <summary>
    /// Radio-button bridge. WPF sets <c>false</c> on the button being unchecked as well as
    /// <c>true</c> on the one being checked, so only the <c>true</c> edge is acted on — otherwise
    /// the pair would fight over the selection.
    /// </summary>
    public bool IsSetupInstallerSelected
    {
        get => SelectedChannel == UpgradeChannel.SetupInstaller;
        set
        {
            if (value)
            {
                SelectedChannel = UpgradeChannel.SetupInstaller;
            }
        }
    }

    /// <summary>Radio-button bridge for the resolver channel. See <see cref="IsSetupInstallerSelected"/>.</summary>
    public bool IsResolverScriptSelected
    {
        get => SelectedChannel == UpgradeChannel.ResolverScript;
        set
        {
            if (value)
            {
                SelectedChannel = UpgradeChannel.ResolverScript;
            }
        }
    }

    /// <summary>
    /// Whether the cosign row means anything right now. cosign is a prerequisite of the
    /// <i>resolver script</i>, which shells out to it to verify the signed release contract. The
    /// Setup installer never invokes it, so on that channel the row is hidden rather than shown
    /// as a passed or failed gate it is not.
    /// </summary>
    public bool IsCosignRelevant => SelectedChannel == UpgradeChannel.ResolverScript;

    /// <summary>The cosign install instructions, suppressed on the channel that does not need them.</summary>
    public bool ShowCosignGuidance => IsCosignRelevant && HasCosignGuidance;

    /// <summary>
    /// Why this section does what it does, on the selected channel. Shown next to the buttons,
    /// because on a Setup-based install the recommended choice looks like the blunter one until
    /// you know the other one does not work here.
    /// </summary>
    public string ChannelNote => IsInstallerChannel
        ? "On a Setup-based install the Setup exe is the channel that actually works: " +
          $"{UpgradeRunner.InstallerAssetName} run over the top of the current install with " +
          "/quiet /norestart INSTALLSCOPE=user. The release's defenseclaw-upgrade.ps1 resolver assumes a venv " +
          $"layout this install does not have — run here it stopped with \"Upgrade resolver stopped: Managed " +
          $"Python not found at {UpgradeRunner.ResolverVenvPythonPath()}.\" — and 0.8.10's copy of that script " +
          "is byte-identical to 0.8.9's, so a newer release does not fix it."
        : "The resolver channel is the target release's own defenseclaw-upgrade.ps1 — a manifest-aware script " +
          "that cosign-verifies the signed release contract before touching anything, and journals the upgrade " +
          "with automatic rollback. It is the right channel only where the venv layout it assumes exists; see " +
          "the note under the picker. The installed CLI's own 'defenseclaw upgrade' verb is not used either " +
          "way: on 0.8.7 it fails with \"no canonical release-managed gateway\".";

    /// <summary>What the selected channel will actually do, step by step.</summary>
    public string ResolverPlan => IsInstallerChannel
        ? "1. The download's SHA-256 is verified against the release's sigstore-signed checksums.txt, with a " +
          "50 MB floor underneath it — the known-bad 133-byte placeholder stubs have their hashes faithfully " +
          "listed in that same signed file, so a hash match alone is not enough.\n" +
          "2. Runs the Setup exe with /quiet /norestart INSTALLSCOPE=user: it stops the DefenseClaw gateway, " +
          "installs over the top in user scope, and restarts the gateway.\n" +
          "3. %USERPROFILE%\\.defenseclaw — config.yaml, the environment file, audit.db and inventory.db — is " +
          "preserved. Verified live on this machine's 0.8.7 → 0.8.10 upgrade."
        : "1. Verifies the release's signed contract with cosign — before changing anything on disk.\n" +
          "2. Stops the DefenseClaw gateway and opens a two-phase upgrade journal.\n" +
          "3. Replaces the CLI, gateway and scanner binaries, and the Claude Code hook runtime that " +
          "every Claude Code session loads its hooks from.\n" +
          "4. Restarts the gateway and closes the journal.";

    /// <summary>What recovery looks like if the selected channel does not finish cleanly.</summary>
    public string RollbackStory => IsInstallerChannel
        ? "The installer has no journal. Unlike the resolver there is no automatic rollback: recovery is " +
          "re-running the previous release's Setup exe, which makes backing up %USERPROFILE%\\.defenseclaw " +
          "beforehand cheap insurance. Expect the tray shield to go gray and a \"Gateway lost\" toast partway " +
          "through — that is the installer stopping the gateway before it installs over the top, not a failure."
        : "The upgrade is journaled in two phases. If a phase fails, the resolver rolls back to the version " +
          "installed right now, on its own. If the machine dies mid-upgrade, re-running the same script " +
          "resumes from the journal rather than starting over. Nothing here is a background task: this app " +
          "runs the script in the foreground and shows you every line it prints.";

    /// <summary>The small print under the Step 1 button.</summary>
    public string StagingStepNote => IsInstallerChannel
        ? $"Reads the release's checksums.txt first — it is a few kilobytes, and a missing entry or a rate " +
          $"limit is worth finding before a ~270 MB download rather than after. Then streams " +
          $"{UpgradeRunner.InstallerAssetName} to disk, hashing as it goes, and keeps it only if it clears the " +
          "50 MB stub floor and its SHA-256 matches. Nothing on this machine changes."
        : "Fetches defenseclaw-upgrade.ps1 from the target release, refuses it if it is one of the placeholder " +
          "stubs (under 10 KB — the real resolver is around 216 KB), and compares its SHA-256 with that " +
          "release's checksums.txt. Nothing is written to disk unless both checks pass, and nothing on this " +
          "machine changes.";

    /// <summary>The small print under the Step 2 button.</summary>
    public string RunStepNote => IsInstallerChannel
        ? "Opens a confirmation showing the exact argv and what the installer will do. The button stays " +
          "disabled until a verified installer is staged and no other command this app issued is still " +
          "running. cosign is not required on this channel — the Setup exe does not use it."
        : "Opens a confirmation showing the exact argv, what the resolver will do, and how it rolls back. The " +
          "button stays disabled until a verified script is staged, cosign is usable, and no other command " +
          "this app issued is still running.";

    /// <summary>Header over the live console. Named for the channel that produced the output.</summary>
    public string ConsoleTitle => _ranChannel == UpgradeChannel.SetupInstaller
        ? "Installer output"
        : "Resolver output";

    /// <summary>Confirm-overlay heading.</summary>
    public string ConfirmTitle => IsInstallerChannel
        ? $"Run the {TargetVersion} Setup installer?"
        : $"Run the {TargetVersion} upgrade resolver?";

    public string StaleAssumptionsWarning =>
        "This app keeps its old-version assumptions — panel layouts, CLI flags, health fields — until you " +
        "restart it. Restart DefenseClaw for Windows after the upgrade finishes.";

    /// <summary>True while this section, or anything else in the app, has a subprocess in flight.</summary>
    public bool IsBusy => IsStaging || IsRunning || CliInFlightCount > 0;

    /// <summary>
    /// True from the moment a run is requested until its outcome is on screen. The window binds its
    /// "this window can't be closed yet" banner to <see cref="IsRunning"/> and refuses to close for
    /// exactly as long — see <c>UpdatesWindow.OnClosing</c>.
    /// </summary>
    public bool IsCloseBlocked => IsRunning;

    /// <summary>
    /// The banner text shown while <see cref="IsCloseBlocked"/>. Says what closing does and does not
    /// do, because "why won't this window close" is the first thing anyone asks and the honest
    /// answer has a second half: quitting the app from the tray is still possible, and does not
    /// stop the install either.
    /// </summary>
    public string CloseBlockedNotice =>
        (_closeRefused ? "That close was ignored. " : string.Empty) +
        $"The {RanNoun} is running, and stopping it partway through an install can leave DefenseClaw " +
        "half-replaced, so this window stays open until it finishes. The install carries on whether or " +
        "not this window is open — even if you quit DefenseClaw for Windows from the tray, which is the " +
        "way out if you must leave, at the cost of this live view of it.";

    /// <summary>
    /// Called by the window when it has just refused a close, so the banner can acknowledge the click
    /// instead of leaving the operator to wonder whether the button did anything.
    /// </summary>
    public void NoteCloseRefused()
    {
        _closeRefused = true;
        OnPropertyChanged(nameof(CloseBlockedNotice));
    }

    private int CliInFlightCount
    {
        get
        {
            lock (_cliGate)
            {
                return _cliInFlight.Count;
            }
        }
    }

    /// <summary>
    /// The token for work that may be abandoned when the window goes. After <see cref="Dispose"/> it
    /// is an already-cancelled token rather than <c>_cts.Token</c>, which would throw
    /// <see cref="ObjectDisposedException"/> from a continuation that outlived the window — the
    /// post-run re-poll of a run that finished after the window was closed under it.
    /// </summary>
    private CancellationToken LifetimeToken => _disposed ? new CancellationToken(canceled: true) : _cts.Token;

    public bool CanDownload => IsUpdateAvailable && !IsBusy;

    /// <summary>
    /// The run button. cosign is a hard gate on the resolver channel — that script refuses without
    /// it — and no gate at all on the installer channel, which never invokes cosign.
    /// </summary>
    public bool CanOpenConfirm => IsStaged && !IsBusy && (IsInstallerChannel || IsCosignReady);

    public bool CanRunUpgrade => CanOpenConfirm && IsConfirmVisible;

    /// <summary>
    /// Feeds the section the latest version-check result. Staging is dropped whenever the target
    /// version changes, so a stale script can never be run against a newer release.
    /// </summary>
    public void ApplyCheck(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var newTarget = result.LatestVersion ?? "—";
        if (!string.Equals(newTarget, TargetVersion, StringComparison.OrdinalIgnoreCase))
        {
            ClearStaging();
        }

        TargetVersion = newTarget;
        IsUpdateAvailable = result.IsUpdateAvailable;

        // After a successful run the check is re-run so the installed version comes from the new
        // binaries. That answer, not the one this app remembered, is what the confirmation shows.
        if (HasRun && ExitBadgeKey == "Ok" && _versionBeforeUpgrade is { Length: > 0 } before)
        {
            var now = result.InstalledVersion;
            HasVersionConfirmation = true;

            if (string.IsNullOrWhiteSpace(now))
            {
                VersionConfirmation =
                    $"Was {before}. The installed version could not be re-read yet — the gateway is not " +
                    "answering. Start it and press \"Check again\".";
            }
            else if (string.Equals(Bare(now), Bare(before), StringComparison.OrdinalIgnoreCase))
            {
                VersionConfirmation =
                    $"Still reporting {now}. The {RanNoun} exited 0, so read the output above and the release " +
                    "page before assuming the upgrade landed.";
            }
            else
            {
                VersionConfirmation = $"{before}  →  {now}";
            }
        }

        RaiseState();
    }

    /// <summary>
    /// Records what the hash comparison is anchored to. The checksums file is only meaningful
    /// because the release signs it; if the sidecars are missing, the UI says so rather than
    /// implying a signature that is not there.
    /// </summary>
    public void ApplyProvenance(ProvenanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _checksumsSigstoreSigned = report.SigstoreSigningPresent;
        ChecksumsSourceNote = report.SigstoreSigningPresent
            ? "Compared against checksums.txt from the same release, which ships a sigstore signature (.sig) " +
              "and certificate (.pem)."
            : "Compared against checksums.txt from the same release. Its sigstore .sig/.pem sidecars are not " +
              "present, so the checksum is an integrity check only — not a signed one.";
    }

    /// <summary>
    /// Detaches from the runner and abandons the download/staging and the post-run re-poll.
    /// <para>
    /// <b>Never ends an upgrade run.</b> The run was started with <see cref="CancellationToken.None"/>,
    /// not <see cref="_cts"/>, so cancelling that source here cannot reach the installer or the
    /// resolver. That matters because this is reachable while a run is in flight: the window refuses
    /// a user-initiated close during one, but the owner <c>MainWindow</c> closing on tray Exit closes
    /// its owned windows without raising <c>Closing</c> at all (observed in a throwaway WPF harness —
    /// see <c>UpdatesWindow</c>), so <c>OnClosed</c> and this method run regardless. The continuation
    /// of <see cref="RunUpgradeAsync"/> that eventually resumes finds <c>_disposed</c> set and does
    /// no further UI work.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _runner.InvocationStarted -= OnUpgradeInvocationStarted;
        _services.Cli.InvocationStarted -= OnAnyInvocationStarted;
        _services.Cli.InvocationCompleted -= OnAnyInvocationCompleted;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Re-probes for cosign — the button next to the badge, for after a winget install. The probe runs on the
    /// thread pool and the badge shows "Checking…" (and the resolver gate stays closed) until it answers; the
    /// command disables itself meanwhile, and a stale answer never overwrites a newer probe's.
    /// </summary>
    [RelayCommand]
    public async Task RefreshCosignAsync()
    {
        var version = ++_cosignProbeVersion;

        IsCosignReady = false;
        CosignLabel = "Checking…";
        CosignBadgeKey = "Neutral";
        RaiseState();

        var status = await _cosignProbe().ConfigureAwait(true);
        if (_disposed || version != _cosignProbeVersion)
        {
            return;
        }

        IsCosignReady = status.IsUsable;
        CosignDetail = status.Detail;
        CosignGuidance = status.InstallGuidance ?? string.Empty;
        HasCosignGuidance = status.InstallGuidance is { Length: > 0 };

        (CosignLabel, CosignBadgeKey) = status.Availability switch
        {
            CosignAvailability.OnPath => ("cosign ready", "Ok"),
            CosignAvailability.FoundOffPath => ("cosign not on PATH", "Warn"),
            _ => ("cosign missing", "Bad"),
        };

        RaiseState();
    }

    /// <summary>
    /// Step one: fetch the selected channel's asset from the target release, refuse a stub,
    /// verify its SHA-256 against the release's checksums.txt, and stage it. Downloads only —
    /// nothing on this machine changes.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAndVerifyAsync()
    {
        if (!CanDownload)
        {
            return;
        }

        var assetName = IsInstallerChannel ? UpgradeRunner.InstallerAssetName : UpgradeRunner.ScriptAssetName;

        ClearStaging();
        IsStaging = true;
        StagingSummary = $"Downloading {assetName} from release {TargetVersion}…";
        VerificationBadgeKey = "Neutral";
        _lastProgressText = string.Empty;
        DownloadProgressText = string.Empty;
        HasDownloadProgress = IsInstallerChannel;
        RaiseState();

        try
        {
            var result = IsInstallerChannel
                ? await _runner
                    .DownloadAndVerifyInstallerAsync(
                        TargetVersion,
                        _checksumsSigstoreSigned,
                        new DownloadProgressSink(this),
                        _cts.Token)
                    .ConfigureAwait(true)
                : await _runner
                    .DownloadAndVerifyAsync(TargetVersion, _checksumsSigstoreSigned, _cts.Token)
                    .ConfigureAwait(true);

            ApplyStaging(result);
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-download; there is nothing left to show it to.
        }
        finally
        {
            IsStaging = false;
            HasDownloadProgress = false;
            RaiseState();
        }
    }

    /// <summary>
    /// Renders one progress callback. The runner raises these on the download worker thread, so
    /// this marshals before touching observable state. Reports arrive roughly two thousand times
    /// over a 270 MB download; only a change in the rendered text is worth a dispatcher hop, which
    /// caps it at about a hundred.
    /// </summary>
    private void OnDownloadProgress(long bytesRead, long? totalBytes)
    {
        var text = FormatProgress(bytesRead, totalBytes);
        if (string.Equals(text, _lastProgressText, StringComparison.Ordinal))
        {
            return;
        }

        _lastProgressText = text;

        if (_dispatcher.CheckAccess())
        {
            DownloadProgressText = text;
            return;
        }

        _dispatcher.BeginInvoke(() => DownloadProgressText = text);
    }

    private static string FormatProgress(long bytesRead, long? totalBytes)
    {
        const double Megabyte = 1024.0 * 1024.0;

        // A server is free to send no Content-Length, in which case there is no honest percentage
        // to show — the megabyte count still is.
        if (totalBytes is not { } total || total <= 0)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytesRead / Megabyte:0.#} MB downloaded");
        }

        var percent = (int)Math.Clamp(bytesRead * 100.0 / total, 0, 100);
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{percent}% — {bytesRead / Megabyte:0.#} MB of {total / Megabyte:0.#} MB");
    }

    /// <summary>Opens the confirm overlay. Still nothing has run.</summary>
    [RelayCommand]
    private void OpenConfirm()
    {
        if (!CanOpenConfirm)
        {
            return;
        }

        IsConfirmVisible = true;
        RaiseState();
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmVisible = false;
        RaiseState();
    }

    [RelayCommand]
    private void CopyCommand()
    {
        if (CommandPreview.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(CommandPreview);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    /// <summary>Reveals the staged asset in Explorer — read-only, and the file is right there.</summary>
    [RelayCommand]
    private void ShowStagedFile()
    {
        if (_staged is null || !File.Exists(_staged.FilePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { "/select,", _staged.FilePath },
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No shell available; the path is displayed in the UI regardless.
        }
    }

    /// <summary>
    /// Step two, and the only mutation this window performs: runs the staged, re-verified asset
    /// through the CLI runner.
    /// <para>
    /// <b>Not cancellable from here, on purpose.</b> The run gets <see cref="CancellationToken.None"/>,
    /// not <see cref="_cts"/>: an installer that is killed midway has no journal to roll back from
    /// (see the type documentation for the full chain of paths that this closes). The runner's
    /// <c>CliRunOptions.Installer</c> already removes the timeout and the app-exit kill; this removes
    /// the last one — the window's own token. The pre-launch re-hash inside
    /// <see cref="UpgradeRunner.RunAsync"/> is no longer cancellable either, which costs nothing:
    /// it is roughly a second of reading a file we staged ourselves.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RunUpgradeAsync()
    {
        if (!CanRunUpgrade || _staged is null)
        {
            return;
        }

        _closeRefused = false;
        IsConfirmVisible = false;
        Output.Clear();
        _outputBuffer.Clear();
        _outputCursor = 0;
        _invocation = null;
        _ranChannel = _staged.Channel;
        _versionBeforeUpgrade = _services.Monitor.Current.BinaryVersion;
        HasOutput = false;
        IsConsoleVisible = true;
        HasRun = false;
        HasVersionConfirmation = false;
        VersionConfirmation = string.Empty;
        ShowRollbackGuidance = false;
        OnPropertyChanged(nameof(ConsoleTitle));

        ResultMessage = _ranChannel == UpgradeChannel.SetupInstaller
            ? "The installer is running. Expect the tray shield to go gray and a \"Gateway lost\" toast partway " +
              "through — that is the installer stopping the gateway before it installs over the top, not a " +
              "failure. A quiet install prints little or nothing to stdout, so the console below staying nearly " +
              "empty is normal rather than a sign it is stuck; the invocation and its exit code land in the " +
              "Activity panel either way."
            : "The resolver is running. Every line it prints appears below and in the Activity panel.";

        ExitBadgeText = "running";
        ExitBadgeKey = "Neutral";
        IsRunning = true;
        RaiseState();

        _timer.Start();

        try
        {
            var result = await _runner.RunAsync(_staged, CancellationToken.None).ConfigureAwait(true);

            if (_disposed)
            {
                // The window went away while the run was in flight — only the owner closing on tray
                // Exit or app shutdown can do that, since a user-initiated close is refused. The
                // installer was left alone; there is no longer anything to show its outcome to, and
                // the Activity panel already holds the record.
                return;
            }

            PullOutput();
            await ApplyRunResultAsync(result).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Nothing here passes a token that can be cancelled, so this is not expected. It is kept
            // so that a stray cancellation from the runner cannot escape as an unhandled fault — and
            // worded so as not to claim the install stopped, which nothing here can know.
            ExitBadgeText = "interrupted";
            ExitBadgeKey = "Warn";
            ResultMessage = $"The {RanNoun} run was interrupted before it reported an outcome, and it may " +
                            "still be running. Check the Activity panel, then 'defenseclaw --version' in a " +
                            "terminal, before starting it again.";
            ShowRollbackGuidance = true;
        }
        finally
        {
            _timer.Stop();
            _closeRefused = false;
            IsRunning = false;
            HasRun = true;
            RaiseState();
        }
    }

    /// <summary>Strips a leading v and any pre-release suffix, for the old-vs-new comparison only.</summary>
    private static string Bare(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(['-', '+']);
        return cut >= 0 ? trimmed[..cut] : trimmed;
    }

    /// <summary>What to call the thing that just ran. Follows the run, not the current selection.</summary>
    private string RanNoun => _ranChannel == UpgradeChannel.SetupInstaller ? "installer" : "resolver";

    private async Task ApplyRunResultAsync(UpgradeRunResult result)
    {
        if (result.FailureReason is { Length: > 0 } failure)
        {
            ExitBadgeText = "not run";
            ExitBadgeKey = "Bad";
            ResultMessage = failure;
            ShowRollbackGuidance = false;
            return;
        }

        if (result.ExitCode is not { } code)
        {
            ExitBadgeText = "exit unknown";
            ExitBadgeKey = "Neutral";
            ResultMessage = $"The {RanNoun} ended without reporting an exit code. Treat the install as unknown: " +
                            "check 'defenseclaw --version' in a terminal before relying on it.";
            ShowRollbackGuidance = true;
            return;
        }

        ExitBadgeText = "exit " + code.ToString(CultureInfo.CurrentCulture);

        if (code == 0)
        {
            ExitBadgeKey = "Ok";
            ResultMessage = $"The {RanNoun} exited 0. Re-polling the gateway to confirm the running version…";

            try
            {
                await _services.Monitor.RefreshAsync(LifetimeToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed)
            {
                // Closed during the re-poll: the run has already finished, so this only abandons the
                // version read-back. Nothing left to show it to.
                return;
            }

            var running = _services.Monitor.Current.BinaryVersion;
            HasVersionConfirmation = true;
            VersionConfirmation = string.IsNullOrWhiteSpace(running)
                ? "The gateway is not answering yet, so the new version could not be read from /health. " +
                  "Give it a moment and press \"Check again\"."
                : $"{_versionBeforeUpgrade ?? "unknown"}  →  {running}";

            ResultMessage = $"The {RanNoun} exited 0. " + StaleAssumptionsWarning;
            ShowRollbackGuidance = false;
            OnUpgradeLanded();
            UpgradeSucceeded?.Invoke(this, EventArgs.Empty);
            return;
        }

        ExitBadgeKey = "Bad";
        ShowRollbackGuidance = true;
        ResultMessage =
            $"The {RanNoun} exited {code}. The output above is kept exactly as it was produced, and the same " +
            "invocation is in the Activity panel.";
    }

    /// <summary>
    /// What a successful upgrade obliges the rest of the app to do. The wizard catalog is a cache over the
    /// installed CLI's <c>--help</c>, so it is re-read (if it was ever opened) instead of showing the old
    /// version's targets and flags; and the staging folder keeps only the version just run, since each Setup
    /// installer is ~270 MB. Both are off the UI thread and neither can fail the upgrade.
    /// </summary>
    private void OnUpgradeLanded()
    {
        _ = WizardCatalog.NotifyCliChangedAsync();

        if (_staged?.Version is { Length: > 0 } version)
        {
            _ = Task.Run(() => _runner.PruneStaging(version));
        }
    }

    private void ApplyStaging(UpgradeStagingResult result)
    {
        ComputedHash = result.ComputedSha256 ?? string.Empty;
        ExpectedHash = result.ExpectedSha256 ?? string.Empty;
        StagingSummary = result.Summary;
        StagingError = result.ErrorMessage ?? string.Empty;
        HasStagingError = result.ErrorMessage is { Length: > 0 };

        if (!result.Succeeded)
        {
            _staged = null;
            IsStaged = false;
            StagedPath = string.Empty;
            StagedSizeText = result.SizeBytes is { } size
                ? string.Create(CultureInfo.CurrentCulture, $"{size:N0} bytes")
                : string.Empty;
            CommandPreview = string.Empty;
            VerificationBadgeKey = result.Outcome == UpgradeStagingOutcome.RateLimited ? "Neutral" : "Bad";
            return;
        }

        var asset = result.Asset!;
        _staged = asset;
        IsStaged = true;
        StagedPath = asset.FilePath;
        StagedSizeText = asset.SizeText;
        CommandPreview = UpgradeRunner.DescribeCommand(asset.Channel, asset.FilePath);
        VerificationBadgeKey = "Ok";
    }

    private void ClearStaging()
    {
        _staged = null;
        IsStaged = false;
        IsConfirmVisible = false;
        StagedPath = string.Empty;
        StagedSizeText = string.Empty;
        ComputedHash = string.Empty;
        ExpectedHash = string.Empty;
        CommandPreview = string.Empty;
        StagingSummary = string.Empty;
        StagingError = string.Empty;
        HasStagingError = false;
        DownloadProgressText = string.Empty;
        HasDownloadProgress = false;
        VerificationBadgeKey = "Neutral";
    }

    private void OnUpgradeInvocationStarted(object? sender, CliInvocation invocation) =>
        _invocation = invocation;

    private void OnAnyInvocationStarted(object? sender, CliInvocation invocation)
    {
        lock (_cliGate)
        {
            _ = _cliInFlight.Add(invocation.Id);
        }

        PostState();
    }

    private void OnAnyInvocationCompleted(object? sender, CliInvocation invocation)
    {
        // A completion for an id that was never seen starting is simply not counted — which is what
        // keeps a command that was already running when the window opened, and was missed by the
        // seed, from driving the in-flight count negative.
        lock (_cliGate)
        {
            _ = _cliInFlight.Remove(invocation.Id);
        }

        PostState();
    }

    /// <summary>
    /// Picks up the commands that were already running when this section was created, so a window
    /// opened mid-command is busy for exactly as long as that command is. Safe against the runner's
    /// own events because they share <see cref="_cliGate"/>, and because
    /// <see cref="CliInvocation.FinishedAt"/> is set <i>before</i> <c>InvocationCompleted</c> is
    /// raised: read under the gate, an invocation whose completion has already been handled reads as
    /// finished and is skipped, and one whose completion has not been handled yet is added and then
    /// removed by that handler. Either way the id ends up in the set exactly while it is running.
    /// </summary>
    private void SeedCliInFlight()
    {
        var recent = _services.Cli.Activity;

        lock (_cliGate)
        {
            foreach (var invocation in recent)
            {
                if (invocation.IsRunning)
                {
                    _ = _cliInFlight.Add(invocation.Id);
                }
            }
        }
    }

    /// <summary>Runner events arrive on a subprocess thread; command state lives on the UI one.</summary>
    private void PostState()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RaiseState();
            return;
        }

        _dispatcher.BeginInvoke(RaiseState);
    }

    /// <summary>
    /// Drains whatever the installer printed since the last tick into the console.
    /// <para>
    /// Runs on the UI thread four times a second for the whole length of an upgrade, so it
    /// pulls the delta rather than a snapshot: <see cref="CliInvocation.CopyNewLines"/> costs
    /// one lock plus the lines that actually arrived, where the previous
    /// <c>Snapshot()</c>-and-diff pass re-copied the entire accumulated transcript on every
    /// tick — worst exactly when the run is long and noisy and the operator is watching.
    /// </para>
    /// <para>
    /// <see cref="_outputCursor"/> is a position in the invocation's monotonic append
    /// sequence, not an index into its retained list, so it stays correct if the invocation
    /// trims its own output mid-run; the drop is reported inline as a notice line rather
    /// than silently skipping or replaying lines.
    /// </para>
    /// </summary>
    private void PullOutput()
    {
        if (_invocation is null)
        {
            return;
        }

        _outputBuffer.Clear();
        _outputCursor = _invocation.CopyNewLines(_outputCursor, _outputBuffer);
        if (_outputBuffer.Count == 0)
        {
            return;
        }

        foreach (var line in _outputBuffer)
        {
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }

        HasOutput = Output.Count > 0;
    }

    // The gates below are computed from several flags at once, so every flag that feeds them
    // re-raises them. Without this a button's enabled state would depend on which code path
    // happened to set the flag — exactly the kind of drift that leaves a run button live while
    // something else is still in flight.
    partial void OnIsStagingChanged(bool value) => RaiseState();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCloseBlocked));
        OnPropertyChanged(nameof(CloseBlockedNotice));
        RaiseState();
    }

    partial void OnIsStagedChanged(bool value) => RaiseState();

    partial void OnIsCosignReadyChanged(bool value) => RaiseState();

    partial void OnIsConfirmVisibleChanged(bool value) => RaiseState();

    partial void OnIsUpdateAvailableChanged(bool value) => RaiseState();

    /// <summary>
    /// A channel switch drops the staging on purpose. The two channels stage different assets
    /// with different verification floors, and a Setup exe verified as an installer must never be
    /// runnable as a resolver script (or the reverse) just because a radio button moved.
    /// </summary>
    partial void OnSelectedChannelChanged(UpgradeChannel value)
    {
        ClearStaging();
        RaiseChannelText();
        RaiseState();
    }

    partial void OnTargetVersionChanged(string value) => OnPropertyChanged(nameof(ConfirmTitle));

    partial void OnHasCosignGuidanceChanged(bool value) => OnPropertyChanged(nameof(ShowCosignGuidance));

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanOpenConfirm));
        OnPropertyChanged(nameof(CanRunUpgrade));
    }

    /// <summary>
    /// Everything whose wording depends on which channel is selected. Raised on a switch, and on
    /// nothing else — these are pure functions of <see cref="SelectedChannel"/>.
    /// </summary>
    private void RaiseChannelText()
    {
        OnPropertyChanged(nameof(IsInstallerChannel));
        OnPropertyChanged(nameof(IsSetupInstallerSelected));
        OnPropertyChanged(nameof(IsResolverScriptSelected));
        OnPropertyChanged(nameof(IsCosignRelevant));
        OnPropertyChanged(nameof(ShowCosignGuidance));
        OnPropertyChanged(nameof(ChannelNote));
        OnPropertyChanged(nameof(ResolverPlan));
        OnPropertyChanged(nameof(RollbackStory));
        OnPropertyChanged(nameof(StagingStepNote));
        OnPropertyChanged(nameof(RunStepNote));
        OnPropertyChanged(nameof(ConfirmTitle));
    }

    /// <summary>
    /// Forwards the runner's progress callbacks. A dedicated sink rather than
    /// <see cref="Progress{T}"/> because the throttling has to happen on the calling thread —
    /// <see cref="Progress{T}"/> would post all ~2000 reports to the dispatcher before anything
    /// could decide most of them say the same thing.
    /// </summary>
    private sealed class DownloadProgressSink : IProgress<(long BytesRead, long? TotalBytes)>
    {
        private readonly UpgradeSectionViewModel _owner;

        public DownloadProgressSink(UpgradeSectionViewModel owner) => _owner = owner;

        public void Report((long BytesRead, long? TotalBytes) value) =>
            _owner.OnDownloadProgress(value.BytesRead, value.TotalBytes);
    }
}
