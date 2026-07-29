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
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// Drives the Updates window's upgrade section: cosign preflight, download-and-verify, the
/// confirm overlay, the live resolver console, and the post-run version confirmation.
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
/// runner hands back on <see cref="UpgradeRunner.InvocationStarted"/> and ticks
/// <see cref="CliInvocation.Snapshot"/> on a dispatcher timer, appending the delta.
/// </para>
/// </summary>
public sealed partial class UpgradeSectionViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OutputTick = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly UpgradeRunner _runner;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private StagedUpgradeScript? _staged;
    private CliInvocation? _invocation;
    private int _syncedOutputCount;
    private int _foreignInvocationsInFlight;
    private bool _checksumsSigstoreSigned;
    private string? _versionBeforeUpgrade;
    private bool _disposed;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _targetVersion = "—";

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

    public UpgradeSectionViewModel(AppServices services, UpgradeRunner runner)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer { Interval = OutputTick };
        _timer.Tick += (_, _) => PullOutput();

        _runner.InvocationStarted += OnUpgradeInvocationStarted;
        _services.Cli.InvocationStarted += OnAnyInvocationStarted;
        _services.Cli.InvocationCompleted += OnAnyInvocationCompleted;

        RefreshCosign();
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

    /// <summary>
    /// Why this section shells out to a downloaded script rather than to the CLI that is already
    /// installed. Shown next to the buttons, because it looks like the wrong choice until you
    /// know it is not.
    /// </summary>
    public string ChannelNote =>
        "The canonical Windows upgrade channel is the target release's own defenseclaw-upgrade.ps1 — a " +
        "manifest-aware resolver that cosign-verifies the signed release contract before touching anything. " +
        "The installed CLI's own 'defenseclaw upgrade' verb is not used: on 0.8.7 it fails with " +
        "\"no canonical release-managed gateway\".";

    /// <summary>What the resolver does once it is past its own cosign verification.</summary>
    public string ResolverPlan =>
        "1. Verifies the release's signed contract with cosign — before changing anything on disk.\n" +
        "2. Stops the DefenseClaw gateway and opens a two-phase upgrade journal.\n" +
        "3. Replaces the CLI, gateway and scanner binaries, and the Claude Code hook runtime that " +
        "every Claude Code session loads its hooks from.\n" +
        "4. Restarts the gateway and closes the journal.";

    public string RollbackStory =>
        "The upgrade is journaled in two phases. If a phase fails, the resolver rolls back to the version " +
        "installed right now, on its own. If the machine dies mid-upgrade, re-running the same script " +
        "resumes from the journal rather than starting over. Nothing here is a background task: this app " +
        "runs the script in the foreground and shows you every line it prints.";

    public string StaleAssumptionsWarning =>
        "This app keeps its old-version assumptions — panel layouts, CLI flags, health fields — until you " +
        "restart it. Restart DefenseClaw for Windows after the upgrade finishes.";

    /// <summary>True while this section, or anything else in the app, has a subprocess in flight.</summary>
    public bool IsBusy => IsStaging || IsRunning || _foreignInvocationsInFlight > 0;

    public bool CanDownload => IsUpdateAvailable && !IsBusy;

    /// <summary>The run button. cosign is a hard gate: the script refuses without it.</summary>
    public bool CanOpenConfirm => IsStaged && IsCosignReady && !IsBusy;

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
                    $"Still reporting {now}. The resolver exited 0, so read the output above and the release " +
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

    /// <summary>Re-probes for cosign — the button next to the badge, for after a winget install.</summary>
    [RelayCommand]
    public void RefreshCosign()
    {
        var status = UpgradeRunner.DetectCosign();

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
    /// Step one: fetch the target release's own upgrade script, refuse a stub, verify its
    /// SHA-256 against the release's checksums.txt, and stage it. Downloads only — nothing on
    /// this machine changes.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAndVerifyAsync()
    {
        if (!CanDownload)
        {
            return;
        }

        ClearStaging();
        IsStaging = true;
        StagingSummary = $"Downloading {UpgradeRunner.ScriptAssetName} from release {TargetVersion}…";
        VerificationBadgeKey = "Neutral";
        RaiseState();

        try
        {
            var result = await _runner
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
            RaiseState();
        }
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

    /// <summary>Reveals the staged script in Explorer — read-only, and the file is right there.</summary>
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
    /// Step two, and the only mutation this window performs: runs the staged, re-verified
    /// resolver through the CLI runner.
    /// </summary>
    [RelayCommand]
    private async Task RunUpgradeAsync()
    {
        if (!CanRunUpgrade || _staged is null)
        {
            return;
        }

        IsConfirmVisible = false;
        Output.Clear();
        _syncedOutputCount = 0;
        _invocation = null;
        _versionBeforeUpgrade = _services.Monitor.Current.BinaryVersion;
        HasOutput = false;
        IsConsoleVisible = true;
        HasRun = false;
        HasVersionConfirmation = false;
        VersionConfirmation = string.Empty;
        ShowRollbackGuidance = false;
        ResultMessage = "The resolver is running. Every line it prints appears below and in the Activity panel.";
        ExitBadgeText = "running";
        ExitBadgeKey = "Neutral";
        IsRunning = true;
        RaiseState();

        _timer.Start();

        try
        {
            var result = await _runner.RunAsync(_staged, _cts.Token).ConfigureAwait(true);
            PullOutput();
            await ApplyRunResultAsync(result).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ExitBadgeText = "cancelled";
            ExitBadgeKey = "Warn";
            ResultMessage = "The run was cancelled because the window closed. Check the Activity panel for how " +
                            "far the resolver got, and re-run it from a terminal if it was mid-phase.";
        }
        finally
        {
            _timer.Stop();
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
            ResultMessage = "The resolver ended without reporting an exit code. Treat the install as unknown: " +
                            "check 'defenseclaw --version' in a terminal before relying on it.";
            ShowRollbackGuidance = true;
            return;
        }

        ExitBadgeText = "exit " + code.ToString(CultureInfo.CurrentCulture);

        if (code == 0)
        {
            ExitBadgeKey = "Ok";
            ResultMessage = "The resolver exited 0. Re-polling the gateway to confirm the running version…";

            try
            {
                await _services.Monitor.RefreshAsync(_cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var running = _services.Monitor.Current.BinaryVersion;
            HasVersionConfirmation = true;
            VersionConfirmation = string.IsNullOrWhiteSpace(running)
                ? "The gateway is not answering yet, so the new version could not be read from /health. " +
                  "Give it a moment and press \"Check again\"."
                : $"{_versionBeforeUpgrade ?? "unknown"}  →  {running}";

            ResultMessage = "The resolver exited 0. " + StaleAssumptionsWarning;
            ShowRollbackGuidance = false;
            UpgradeSucceeded?.Invoke(this, EventArgs.Empty);
            return;
        }

        ExitBadgeKey = "Bad";
        ShowRollbackGuidance = true;
        ResultMessage =
            $"The resolver exited {code}. The output above is kept exactly as it was produced, and the same " +
            "invocation is in the Activity panel.";
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

        var script = result.Script!;
        _staged = script;
        IsStaged = true;
        StagedPath = script.FilePath;
        StagedSizeText = script.SizeText;
        CommandPreview = UpgradeRunner.DescribeCommand(script.FilePath);
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
        VerificationBadgeKey = "Neutral";
    }

    private void OnUpgradeInvocationStarted(object? sender, CliInvocation invocation) =>
        _invocation = invocation;

    private void OnAnyInvocationStarted(object? sender, CliInvocation invocation)
    {
        Interlocked.Increment(ref _foreignInvocationsInFlight);
        PostState();
    }

    private void OnAnyInvocationCompleted(object? sender, CliInvocation invocation)
    {
        Interlocked.Decrement(ref _foreignInvocationsInFlight);
        PostState();
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

    private void PullOutput()
    {
        if (_invocation is null)
        {
            return;
        }

        var snapshot = _invocation.Snapshot();
        for (var i = _syncedOutputCount; i < snapshot.OutputLines.Count; i++)
        {
            var line = snapshot.OutputLines[i];
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }

        if (snapshot.OutputLines.Count != _syncedOutputCount)
        {
            _syncedOutputCount = snapshot.OutputLines.Count;
            HasOutput = Output.Count > 0;
        }
    }

    // The gates below are computed from several flags at once, so every flag that feeds them
    // re-raises them. Without this a button's enabled state would depend on which code path
    // happened to set the flag — exactly the kind of drift that leaves a run button live while
    // something else is still in flight.
    partial void OnIsStagingChanged(bool value) => RaiseState();

    partial void OnIsRunningChanged(bool value) => RaiseState();

    partial void OnIsStagedChanged(bool value) => RaiseState();

    partial void OnIsCosignReadyChanged(bool value) => RaiseState();

    partial void OnIsConfirmVisibleChanged(bool value) => RaiseState();

    partial void OnIsUpdateAvailableChanged(bool value) => RaiseState();

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanOpenConfirm));
        OnPropertyChanged(nameof(CanRunUpgrade));
    }
}
