using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// Drives the Updates window: installed-vs-latest comparison, the trust panel, the copyable
/// upgrade guidance, and — through <see cref="Upgrade"/> — the in-app upgrade flow.
/// <para>
/// The check and trust halves are still read-only. The one path that changes this machine is
/// <see cref="UpgradeSectionViewModel"/>, and it does so the way the rest of the app does: by
/// shelling out through <see cref="DefenseClaw.Core.Cli.CliRunner"/> with the exact argv shown
/// to the operator first.
/// </para>
/// </summary>
public sealed partial class UpdatesWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly HttpClient _http;
    private readonly UpdateChecker _updateChecker;
    private readonly ProvenanceInspector _provenanceInspector;
    private readonly UpgradeRunner _upgradeRunner;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
    private string _stateLabel = "Checking…";

    [ObservableProperty]
    private string _stateBadgeKey = "Neutral";

    [ObservableProperty]
    private string _stateDetail = "Contacting GitHub…";

    [ObservableProperty]
    private string _installedVersionText = "unknown";

    [ObservableProperty]
    private string _latestVersionText = "—";

    [ObservableProperty]
    private string _releaseName = string.Empty;

    [ObservableProperty]
    private string _publishedAtText = "—";

    [ObservableProperty]
    private string? _htmlUrl;

    [ObservableProperty]
    private bool _isRateLimited;

    [ObservableProperty]
    private bool _fromCache;

    [ObservableProperty]
    private string _cacheNote = string.Empty;

    [ObservableProperty]
    private string _assetsSummary = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _authenticodeLabel = "Not checked yet";

    [ObservableProperty]
    private string _authenticodeDetail = string.Empty;

    [ObservableProperty]
    private string _authenticodeBadgeKey = "Neutral";

    [ObservableProperty]
    private string _sigstoreLabel = "Not checked yet";

    [ObservableProperty]
    private string _sigstoreDetail = string.Empty;

    [ObservableProperty]
    private string _sigstoreBadgeKey = "Neutral";

    [ObservableProperty]
    private string _checksumsEntryCountText = string.Empty;

    [ObservableProperty]
    private bool _hasChecksumsEntryCount;

    [ObservableProperty]
    private bool _hasStubWarnings;

    [ObservableProperty]
    private string _stubBadgeKey = "Ok";

    [ObservableProperty]
    private string _stubSummary = "No stub assets detected.";

    [ObservableProperty]
    private string? _upgradeScriptCommandText;

    [ObservableProperty]
    private bool _hasUpgradeScriptAsset;

    [ObservableProperty]
    private string? _setupCommandText;

    [ObservableProperty]
    private bool _hasSetupAsset;

    public UpdatesWindowViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _http = UpdateChecker.CreateHttpClient();
        _updateChecker = new UpdateChecker(_services, _http, ownsHttpClient: true);
        _provenanceInspector = new ProvenanceInspector(_http, ownsHttpClient: false);
        _upgradeRunner = new UpgradeRunner(_services.Cli, _http);

        Upgrade = new UpgradeSectionViewModel(_services, _upgradeRunner);
        Upgrade.UpgradeSucceeded += OnUpgradeSucceeded;
    }

    /// <summary>Individual stub-asset warnings for the trust panel's detail list.</summary>
    public ObservableCollection<StubAssetWarning> StubWarnings { get; } = new();

    /// <summary>The in-app upgrade flow: cosign preflight, verify, confirm, run.</summary>
    public UpgradeSectionViewModel Upgrade { get; }

    /// <summary>
    /// Static context about the known-broken 0.8.6→0.8.7 in-place upgrade, shown next to the
    /// upgrade commands rather than acted on: this app never runs an upgrade itself.
    /// </summary>
    public string UpgradeCaveat =>
        "The 0.8.6 -> 0.8.7 in-place upgrade path was broken upstream (a migration-cursor catch-22). " +
        "Back up %USERPROFILE%\\.defenseclaw before upgrading. If the upgrade script fails partway, " +
        "the documented recovery is reinstalling from the Setup exe over a fresh 'defenseclaw init'.";

    private bool CanOpenReleasePage => !string.IsNullOrEmpty(HtmlUrl);

    /// <summary>Runs the first check. Called by the window right after construction.</summary>
    public Task InitializeAsync() => RunCheckAsync(forceRefresh: false);

    [RelayCommand]
    private Task Refresh() => RunCheckAsync(forceRefresh: true);

    [RelayCommand(CanExecute = nameof(CanOpenReleasePage))]
    private void OpenReleasePage()
    {
        if (string.IsNullOrEmpty(HtmlUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(HtmlUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No handler registered for the URL, or the shell prompt was dismissed.
        }
        catch (InvalidOperationException)
        {
            // No default browser associated with http(s) on this machine.
        }
    }

    [RelayCommand]
    private void CopyUpgradeScriptCommand() => CopyToClipboard(UpgradeScriptCommandText);

    [RelayCommand]
    private void CopySetupCommand() => CopyToClipboard(SetupCommandText);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Upgrade.UpgradeSucceeded -= OnUpgradeSucceeded;
        Upgrade.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _provenanceInspector.Dispose();
        _updateChecker.Dispose();
    }

    /// <summary>
    /// Re-runs the check after a successful upgrade so the installed version is re-resolved from
    /// the new binaries. Not forced: the release data is already cached, and this is about the
    /// local side of the comparison, not GitHub's.
    /// </summary>
    private void OnUpgradeSucceeded(object? sender, EventArgs e) => _ = RunCheckAsync(forceRefresh: false);

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    private async Task RunCheckAsync(bool forceRefresh)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        StateLabel = "Checking…";
        StateDetail = forceRefresh ? "Checking GitHub for a newer release…" : "Checking for updates…";
        StateBadgeKey = "Neutral";

        try
        {
            var result = await _updateChecker.CheckAsync(forceRefresh, _cts.Token).ConfigureAwait(true);
            ApplyResult(result);

            if (result.Assets.Count > 0)
            {
                var provenance = await _provenanceInspector
                    .InspectAsync(result.Assets, _cts.Token)
                    .ConfigureAwait(true);
                ApplyProvenance(provenance);
            }
            else
            {
                ClearProvenance();
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-check; there is nothing left to show it to.
        }
        finally
        {
            IsChecking = false;
            HasChecked = true;
        }
    }

    private void ApplyResult(UpdateCheckResult result)
    {
        InstalledVersionText = result.InstalledVersion ?? "unknown";
        LatestVersionText = result.LatestVersion ?? "—";
        ReleaseName = result.ReleaseName ?? string.Empty;
        PublishedAtText = result.PublishedAt is { } published
            ? published.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)
            : "—";
        HtmlUrl = result.HtmlUrl;
        IsRateLimited = result.IsRateLimited;
        FromCache = result.FromCache;
        CacheNote = result.FromCache
            ? $"Showing cached data from {result.CheckedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
            : string.Empty;
        AssetsSummary = result.Assets.Count == 1 ? "1 asset" : $"{result.Assets.Count} assets";
        ErrorMessage = result.ErrorMessage ?? string.Empty;
        HasError = result.State == UpdateCheckState.CheckFailed;

        (StateLabel, StateBadgeKey) = result.State switch
        {
            UpdateCheckState.UpToDate => ("Up to date", "Ok"),
            UpdateCheckState.UpdateAvailable => ("Update available", "Warn"),
            UpdateCheckState.CheckFailed when result.IsRateLimited => ("Rate-limited — try later", "Neutral"),
            UpdateCheckState.CheckFailed => ("Check failed", "Bad"),
            _ => ("Unknown", "Neutral"),
        };

        StateDetail = result.Detail;

        var scriptAsset = result.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "defenseclaw-upgrade.ps1", StringComparison.OrdinalIgnoreCase));
        HasUpgradeScriptAsset = scriptAsset is not null;
        UpgradeScriptCommandText = scriptAsset is { DownloadUrl.Length: > 0 }
            ? $"Invoke-WebRequest -Uri \"{scriptAsset.DownloadUrl}\" -OutFile defenseclaw-upgrade.ps1; .\\defenseclaw-upgrade.ps1"
            : "No defenseclaw-upgrade.ps1 asset was found on this release.";

        var setupAsset = result.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, ProvenanceInspector.SetupAssetName, StringComparison.OrdinalIgnoreCase));
        HasSetupAsset = setupAsset is not null;
        SetupCommandText = setupAsset is { DownloadUrl.Length: > 0 }
            ? $"Start-Process \"{setupAsset.DownloadUrl}\"  # opens the Setup download in your browser"
            : "No Setup exe asset was found on this release.";

        Upgrade.ApplyCheck(result);

        OpenReleasePageCommand.NotifyCanExecuteChanged();
    }

    private void ApplyProvenance(ProvenanceReport report)
    {
        Upgrade.ApplyProvenance(report);

        AuthenticodeLabel = report.InstallerAuthenticode switch
        {
            AuthenticodeStatus.Signed => "Authenticode-signed",
            AuthenticodeStatus.NotSigned => "Not Authenticode-signed",
            _ => "Unknown",
        };
        AuthenticodeDetail = report.InstallerAuthenticodeDetail;
        AuthenticodeBadgeKey = report.InstallerAuthenticode switch
        {
            AuthenticodeStatus.Signed => "Ok",
            AuthenticodeStatus.NotSigned => "Warn",
            _ => "Neutral",
        };

        if (report.SigstoreSigningPresent)
        {
            SigstoreLabel = "checksums.txt signed with sigstore";
            SigstoreBadgeKey = "Ok";
            SigstoreDetail =
                "checksums.txt, its sigstore certificate (.pem) and signature (.sig) are all present on this release.";
        }
        else if (report.ChecksumsAssetPresent)
        {
            SigstoreLabel = "checksums.txt present, signing incomplete";
            SigstoreBadgeKey = "Warn";
            var missing = new List<string>();
            if (!report.ChecksumsCertificatePresent)
            {
                missing.Add(".pem certificate");
            }

            if (!report.ChecksumsSignaturePresent)
            {
                missing.Add(".sig signature");
            }

            SigstoreDetail =
                $"checksums.txt is present, but its {string.Join(" and ", missing)} could not be found among the release assets.";
        }
        else
        {
            SigstoreLabel = "No checksums.txt on this release";
            SigstoreBadgeKey = "Bad";
            SigstoreDetail = "Without checksums.txt there is nothing to verify a downloaded artifact against.";
        }

        HasChecksumsEntryCount = report.ChecksumsEntryCount is not null;
        ChecksumsEntryCountText = report.ChecksumsEntryCount is { } count
            ? $"{count} checksum entries"
            : string.Empty;

        StubWarnings.Clear();
        foreach (var warning in report.StubWarnings)
        {
            StubWarnings.Add(warning);
        }

        HasStubWarnings = report.HasStubWarnings;
        StubBadgeKey = report.HasStubWarnings ? "Bad" : "Ok";
        StubSummary = report.HasStubWarnings
            ? $"{report.StubWarnings.Count} asset(s) look like placeholder stubs — see below."
            : "No stub assets detected among this release's assets.";

        if (report.ErrorMessage is { Length: > 0 } provenanceError)
        {
            ErrorMessage = string.IsNullOrEmpty(ErrorMessage) ? provenanceError : $"{ErrorMessage} {provenanceError}";
            HasError = true;
        }
    }

    private void ClearProvenance()
    {
        Upgrade.ApplyProvenance(new ProvenanceReport());

        AuthenticodeLabel = "Unknown";
        AuthenticodeDetail = "No release assets to inspect.";
        AuthenticodeBadgeKey = "Neutral";
        SigstoreLabel = "Unknown";
        SigstoreDetail = "No release assets to inspect.";
        SigstoreBadgeKey = "Neutral";
        HasChecksumsEntryCount = false;
        ChecksumsEntryCountText = string.Empty;
        StubWarnings.Clear();
        HasStubWarnings = false;
        StubBadgeKey = "Neutral";
        StubSummary = "No release assets to inspect.";
    }
}
