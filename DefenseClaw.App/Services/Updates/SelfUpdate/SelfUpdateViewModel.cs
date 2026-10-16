using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>Where the reviewed install is.</summary>
public enum SelfUpdatePhase
{
    Idle,

    /// <summary>The review is open and the checksums are being read.</summary>
    Loading,

    /// <summary>The review shows version, size, SHA-256 and the trust sentence; waiting for the operator.</summary>
    Review,

    /// <summary>Downloading, verifying, replacing.</summary>
    Installing,

    /// <summary>Files are replaced and the restart is under way (or waiting for the operator to allow it).</summary>
    Restarting,

    /// <summary>Refused or failed; <see cref="SelfUpdateViewModel.ErrorText"/> says why.</summary>
    Failed,
}

/// <summary>
/// What the banner, the review window and the Settings card show for updating this app, and the commands behind them. All of it follows
/// <see cref="AppUpdateWatcher"/> (whose events arrive on the UI thread); nothing here installs without <see cref="InstallCommand"/>.
/// </summary>
public sealed partial class SelfUpdateViewModel : ObservableObject, IDisposable
{
    private readonly SelfUpdateService _service;
    private CancellationTokenSource? _cts;

    internal SelfUpdateViewModel(SelfUpdateService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        service.Watcher.Changed += OnWatcherChanged;
        service.Watcher.Checked += OnWatcherChanged;
        Refresh();
    }

    /// <summary>The view opens the review window. Raised on the UI thread by <see cref="UpdateNowCommand"/>.</summary>
    public event EventHandler? ReviewRequested;

    /// <summary>The review window may close (cancelled, or the restart is under way).</summary>
    public event EventHandler? ReviewClosed;

    [ObservableProperty]
    private string _runningText = "Unknown";

    [ObservableProperty]
    private string _statusText = "Not checked yet";

    /// <summary>The tone key the badge styles use: Ok, Medium, High, Neutral.</summary>
    [ObservableProperty]
    private string _statusTone = "Neutral";

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _lastCheckedText = "Never";

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    /// <summary>A newer installable release is known (banner or not, skipped or not): Settings can offer the review.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _canReview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private SelfUpdatePhase _phase;

    [ObservableProperty]
    private string _reviewTitle = string.Empty;

    [ObservableProperty]
    private string _notesText = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _shaText = string.Empty;

    [ObservableProperty]
    private string _notesAboutInstall = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>The honest trust sentence, shown in the review whatever else it says.</summary>
    public string TrustText => SelfUpdateInstaller.TrustSentence;

    public bool IsBusy => Phase is SelfUpdatePhase.Loading or SelfUpdatePhase.Installing or SelfUpdatePhase.Restarting;

    public bool HasError => ErrorText.Length > 0;

    public bool HasInstallNotes => NotesAboutInstall.Length > 0;

    partial void OnPhaseChanged(SelfUpdatePhase value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnNotesAboutInstallChanged(string value) => OnPropertyChanged(nameof(HasInstallNotes));

    // ------------------------------------------------------------------ checking

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNowAsync()
    {
        IsChecking = true;
        try
        {
            _ = await _service.CheckManuallyAsync().ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }

    private bool CanCheck() => !IsChecking;

    private void OnWatcherChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Re-reads the watcher's answer into the properties. On the UI thread.</summary>
    public void Refresh()
    {
        var latest = _service.Watcher.Latest;
        RunningText = _service.Running?.ToString() ?? "Unknown";

        var stamp = _service.Store.Current.LastCheckUnix;
        LastCheckedText = stamp > 0 && stamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? SettingsPanelViewModel.FormatLastChecked(DateTimeOffset.FromUnixTimeSeconds(stamp), DateTimeOffset.UtcNow)
            : "Never";

        var available = _service.Watcher.Available;
        CanReview = available is not null && Phase is SelfUpdatePhase.Idle or SelfUpdatePhase.Failed;
        switch (latest.State)
        {
            case AppUpdateState.UpdateAvailable when available is { } release:
                StatusText = _service.Watcher.IsSkipped ? $"{release.Version} available (skipped)" : $"{release.Version} available";
                StatusTone = "Medium";
                DetailText = latest.Message;
                break;
            case AppUpdateState.UpToDate:
                StatusText = "Up to date";
                StatusTone = latest.LiveFailed ? "Neutral" : "Ok";
                DetailText = latest.LiveFailed ? latest.Message : string.Empty;
                break;
            case AppUpdateState.DevBuild:
                StatusText = "Development build";
                StatusTone = "Neutral";
                DetailText = latest.Message;
                break;
            case AppUpdateState.Failed:
                StatusText = "Could not check";
                StatusTone = "High";
                DetailText = latest.Message;
                break;
            default:
                StatusText = "Not checked yet";
                StatusTone = "Neutral";
                DetailText = string.Empty;
                break;
        }

        if (_service.Watcher.ShowBanner && available is { } offered)
        {
            ShowBanner = true;
            BannerTitle = $"DefenseClaw for Windows {offered.Version} is available";
            BannerMessage = $"Installed: {RunningText}. " + ReleaseNotesText.FirstLine(ReleaseNotesText.ToPlain(offered.Notes));
        }
        else
        {
            ShowBanner = false;
        }
    }

    // ------------------------------------------------------------------ banner choices

    [RelayCommand]
    private void Later() => _service.Watcher.Later();

    [RelayCommand]
    private void Skip() => _service.Watcher.Skip();

    // ------------------------------------------------------------------ the reviewed install

    /// <summary>"Update now": opens the review. Nothing is downloaded but the small checksums file until Install is pressed.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        if (_service.Offered is not { } release)
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        ErrorText = string.Empty;
        ProgressText = string.Empty;
        ReviewTitle = $"DefenseClaw for Windows {release.Version}";
        NotesText = ReleaseNotesText.ToPlain(release.Notes);
        SizeText = release.Zip is { Size: > 0 } zip ? FormatSize(zip.Size) : "unknown";
        ShaText = "Reading checksums.txt…";
        NotesAboutInstall = string.Empty;
        Phase = SelfUpdatePhase.Loading;
        ReviewRequested?.Invoke(this, EventArgs.Empty);

        (string? Sha256, string? Problem, IReadOnlyList<string> Notes) prepared;
        try
        {
            prepared = await _service.PrepareReviewAsync(release, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Phase = SelfUpdatePhase.Idle;
            return;
        }

        NotesAboutInstall = string.Join(Environment.NewLine, prepared.Notes);
        if (prepared.Problem is { } problem)
        {
            ShaText = string.Empty;
            ErrorText = problem;
            Phase = SelfUpdatePhase.Failed;
            Refresh();
            return;
        }

        ShaText = prepared.Sha256 ?? string.Empty;
        Phase = SelfUpdatePhase.Review;
    }

    private bool CanUpdateNow() => Phase is SelfUpdatePhase.Idle or SelfUpdatePhase.Failed && _service.Offered is not null;

    /// <summary>"Install and restart": downloads, verifies, replaces and restarts, after the review.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_service.Offered is not { } release)
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Phase = SelfUpdatePhase.Installing;
        ErrorText = string.Empty;
        var progress = new Progress<string>(text => ProgressText = text);
        string? failure;
        try
        {
            failure = await _service.InstallAsync(release, progress, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            failure = "The update was cancelled; nothing was installed.";
        }

        if (failure is null)
        {
            Phase = SelfUpdatePhase.Restarting;
            ProgressText = "The new version is installed. The app restarts now; if it cannot restart right away, use Restart app in the tray menu.";
            ReviewClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        ErrorText = failure;
        Phase = SelfUpdatePhase.Failed;
        Refresh();
    }

    private bool CanInstall() => Phase == SelfUpdatePhase.Review;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        if (Phase != SelfUpdatePhase.Restarting)
        {
            Phase = SelfUpdatePhase.Idle;
        }

        ReviewClosed?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    /// <summary>The release page, for "Release notes": only a link into this app's repository.</summary>
    [RelayCommand]
    private void OpenReleasePage() => _ = UpdateWatcher.TryOpenReleasePage(_service.Watcher.Available?.HtmlUrl, null);

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):0.#} MiB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KiB");

    public void Dispose()
    {
        _service.Watcher.Changed -= OnWatcherChanged;
        _service.Watcher.Checked -= OnWatcherChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
