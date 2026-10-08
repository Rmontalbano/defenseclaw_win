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

    /// <summary>Which runtime is connected and what it can do (CUST-291): "defenseclaw-cli 1.0.0 (path)", from the probes, not from the release check.</summary>
    [ObservableProperty]
    private string _runtimeIdentityText = "Not detected";

    /// <summary>The newer features the connected runtime has, in a line.</summary>
    [ObservableProperty]
    private string _runtimeFeaturesText = "Features unknown until the runtime answers";

    public UpdatesWindowViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _http = UpdateChecker.CreateHttpClient();
        _updateChecker = new UpdateChecker(_services, _http, ownsHttpClient: true);
        _provenanceInspector = new ProvenanceInspector(_http, ownsHttpClient: false);
        _upgradeRunner = new UpgradeRunner(_services.Cli, _http);

        ShowRuntime();
        _ = RefreshRuntimeAsync();

        Upgrade = new UpgradeSectionViewModel(_services, _upgradeRunner);
        Upgrade.UpgradeSucceeded += OnUpgradeSucceeded;
        Upgrade.SignatureChecked += OnSignatureChecked;
    }

    /// <summary>Individual stub-asset warnings for the trust panel's detail list.</summary>
    public ObservableCollection<StubAssetWarning> StubWarnings { get; } = new();

    /// <summary>The in-app upgrade flow: cosign preflight, verify, confirm, run.</summary>
    public UpgradeSectionViewModel Upgrade { get; }

    /// <summary>
    /// What is known to be broken about upgrading, shown next to the copyable commands. History
    /// first, then the failure that matters on this machine's layout today.
    /// </summary>
    public string UpgradeCaveat =>
        "History: the 0.8.6 -> 0.8.7 in-place upgrade path was broken upstream (a migration-cursor catch-22). " +
        "Current: on Setup-based installs the defenseclaw-upgrade.ps1 resolver fails outright — run here it " +
        "stopped with \"Upgrade resolver stopped: Managed Python not found at " +
        "%USERPROFILE%\\.defenseclaw\\.venv\\Scripts\\python.exe\", because it assumes a venv layout these " +
        "installs do not have, and 0.8.10's copy of the script is byte-identical to 0.8.9's. The channel that " +
        "works is the Setup exe installed over the top with /quiet /norestart INSTALLSCOPE=user: it stops the " +
        "gateway, installs in user scope, restarts the gateway, and preserves %USERPROFILE%\\.defenseclaw " +
        "(config, audit, inventory, token) — verified live on a 0.8.7 -> 0.8.10 upgrade. Backing that directory " +
        "up first is still cheap insurance; the installer keeps no journal and rolls nothing back.";

    private bool CanOpenReleasePage => UpdateChecker.TrustedRepoUrl(HtmlUrl) is not null;

    /// <summary>Runs the first check. Called by the window right after construction.</summary>
    public Task InitializeAsync() => RunCheckAsync(forceRefresh: false);

    [RelayCommand]
    private Task Refresh() => RunCheckAsync(forceRefresh: true);

    [RelayCommand(CanExecute = nameof(CanOpenReleasePage))]
    private void OpenReleasePage()
    {
        // The link came from GitHub's JSON or the cache file, and UseShellExecute hands whatever it is to the shell:
        // only an https link into this repository is ever opened, and it is the parsed, normalised form that is.
        if (UpdateChecker.TrustedRepoUrl(HtmlUrl) is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        Upgrade.SignatureChecked -= OnSignatureChecked;
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

    /// <summary>
    /// A download-and-verify has found out what there is to know about the signature on checksums.txt (verified by cosign, published and not verified,
    /// not published, or rejected): the trust panel says the same words as the upgrade card, in place of "published, not verified yet".
    /// </summary>
    private void OnSignatureChecked(object? sender, ChecksumsSignatureResult result) => ApplySignature(result);

    /// <summary>The trust panel's signature row, in the words of what cosign found (or did not): <see cref="ChecksumsSignatureResult.Label"/> says "verified" only for a verified signature.</summary>
    internal void ApplySignature(ChecksumsSignatureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        SigstoreLabel = result.Label;
        SigstoreBadgeKey = result.BadgeKey;
        SigstoreDetail = result.Detail;
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = Views.Controls.DcClipboard.TrySetText(text);
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

    private void ShowRuntime()
    {
        var snapshot = _services.Runtime.Current;
        RuntimeIdentityText = DefenseClaw.Core.Runtime.RuntimeSummary.Identity(snapshot);
        RuntimeFeaturesText = DefenseClaw.Core.Runtime.RuntimeSummary.Features(snapshot);
    }

    /// <summary>Re-reads the runtime when the CLI file changed (one stamp otherwise) and shows the answer; never throws.</summary>
    private async Task RefreshRuntimeAsync()
    {
        try
        {
            _ = await _services.Runtime.RefreshAsync().ConfigureAwait(true);
            ShowRuntime();
        }
#pragma warning disable CA1031 // The detector turns failures into "unknown"; nothing here may fault the window.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void ApplyResult(UpdateCheckResult result)
    {
        ShowRuntime();
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

        // The Setup block is the primary suggestion now, and it is a real two-line command rather
        // than a browser hand-off: this is the sequence that was live-verified on 0.8.7 -> 0.8.10.
        var setupAsset = result.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, UpgradeRunner.InstallerAssetName, StringComparison.OrdinalIgnoreCase));
        HasSetupAsset = setupAsset is not null;
        SetupCommandText = BuildSetupCommand(setupAsset);

        // Demoted, and labelled: the resolver is still the documented channel upstream, so it stays
        // copyable — but it is not the one to reach for on this layout.
        var scriptAsset = result.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, UpgradeRunner.ScriptAssetName, StringComparison.OrdinalIgnoreCase));
        HasUpgradeScriptAsset = scriptAsset is not null;
        UpgradeScriptCommandText = BuildUpgradeScriptCommand(scriptAsset);

        Upgrade.ApplyCheck(result);

        OpenReleasePageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The copyable two-line Setup upgrade: download the exe, run it with the flags that were live-verified.
    /// The URL is single-quoted, because inside double quotes PowerShell expands <c>$(…)</c> and backticks, so a link
    /// that was anything but plain text would run when the operator pasted the command; and only a link into this
    /// repository is offered at all (<see cref="UpdateChecker.TrustedRepoUrl"/>).
    /// </summary>
    internal static string BuildSetupCommand(ReleaseAsset? setupAsset) =>
        UpdateChecker.TrustedRepoUrl(setupAsset?.DownloadUrl) is { } url
            ? $"curl.exe -LO {UpdateChecker.PowerShellSingleQuoted(url)}\n" +
              $".\\{UpgradeRunner.InstallerAssetName} {string.Join(' ', UpgradeRunner.BuildInstallerArgv())}"
            : "No Setup exe asset was found on this release.";

    /// <summary>The copyable resolver-script command, quoted and restricted like <see cref="BuildSetupCommand"/>.</summary>
    internal static string BuildUpgradeScriptCommand(ReleaseAsset? scriptAsset) =>
        UpdateChecker.TrustedRepoUrl(scriptAsset?.DownloadUrl) is { } url
            ? $"Invoke-WebRequest -Uri {UpdateChecker.PowerShellSingleQuoted(url)} -OutFile {UpgradeRunner.ScriptAssetName}; " +
              $".\\{UpgradeRunner.ScriptAssetName}\n" +
              "# (known broken on Setup-based installs — see the upgrade section)"
            : "No defenseclaw-upgrade.ps1 asset was found on this release.";

    internal void ApplyProvenance(ProvenanceReport report)
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

        // What this panel knows without running anything is which signature files the release PUBLISHES. It cannot say the signature is valid:
        // only cosign can, and that happens when Download & verify runs it (OnSignatureChecked then replaces these words with its answer).
        if (report.SignatureFilesPublished)
        {
            SigstoreLabel = "signature files published, not verified yet";
            SigstoreBadgeKey = "Neutral";
            SigstoreDetail =
                "checksums.txt and its sigstore signature files " + PublishedSignatureFiles(report) + " are on this release. Nothing here has checked that signature. " +
                "Download & verify below verifies it with cosign when cosign is installed (and says so here); without cosign it only compares the download's SHA-256 " +
                "with checksums.txt from the same release, which shows the download matches that file, not that the project signed it.";
        }
        else if (report.ChecksumsAssetPresent)
        {
            SigstoreLabel = "checksums.txt present, no signature published";
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
                $"checksums.txt is present, but the release lists no checksums.txt.bundle and is missing its {string.Join(" and ", missing)}, so there is no sigstore signature to verify. " +
                "A SHA-256 comparison against it is an integrity check only.";
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

    /// <summary>The signature files the release lists, as text: "(checksums.txt.bundle)" or "(checksums.txt.sig and .pem)".</summary>
    private static string PublishedSignatureFiles(ProvenanceReport report) =>
        report.ChecksumsBundlePresent && report.ChecksumsSignaturePresent && report.ChecksumsCertificatePresent
            ? $"({ProvenanceInspector.ChecksumsBundleAssetName}, .sig and .pem)"
            : report.ChecksumsBundlePresent
                ? $"({ProvenanceInspector.ChecksumsBundleAssetName})"
                : "(.sig and .pem)";

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
