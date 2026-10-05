using System.Net.Http;
using System.Text.Json;

namespace DefenseClaw.App.Services.Updates;

/// <summary>Authenticode status as reported by the installer's own provenance sidecar.</summary>
public enum AuthenticodeStatus
{
    /// <summary>No provenance sidecar was found, or it did not report a recognizable status.</summary>
    Unknown = 0,

    /// <summary>The sidecar says the installer is not Authenticode-signed.</summary>
    NotSigned,

    /// <summary>The sidecar says the installer is Authenticode-signed.</summary>
    Signed,
}

/// <summary>One asset whose size contradicts its name — the placeholder-stub pattern.</summary>
public sealed record StubAssetWarning(string AssetName, long Size, string Reason);

/// <summary>Everything the trust panel shows, for one release.</summary>
public sealed record ProvenanceReport
{
    public AuthenticodeStatus InstallerAuthenticode { get; init; } = AuthenticodeStatus.Unknown;

    public string InstallerAuthenticodeDetail { get; init; } = string.Empty;

    /// <summary>True when the sidecar asset itself could be found and read at all.</summary>
    public bool ProvenanceSidecarFound { get; init; }

    public bool ChecksumsAssetPresent { get; init; }

    public bool ChecksumsSignaturePresent { get; init; }

    public bool ChecksumsCertificatePresent { get; init; }

    /// <summary>True when the release lists <c>checksums.txt.bundle</c>: the certificate, signature and transparency-log entry in one file.</summary>
    public bool ChecksumsBundlePresent { get; init; }

    /// <summary>Line count of checksums.txt, when it was readable. Null otherwise.</summary>
    public int? ChecksumsEntryCount { get; init; }

    /// <summary>
    /// True when the checksums file and a way to verify it are all <b>published</b>: the bundle, or the signature with its certificate. This says the
    /// files are on the release - nothing here has checked them. Whether the signature is valid is <see cref="ChecksumsSignatureVerifier"/>'s answer,
    /// and only cosign can give it.
    /// </summary>
    public bool SignatureFilesPublished =>
        ChecksumsAssetPresent && (ChecksumsBundlePresent || (ChecksumsSignaturePresent && ChecksumsCertificatePresent));

    public IReadOnlyList<StubAssetWarning> StubWarnings { get; init; } = [];

    public bool HasStubWarnings => StubWarnings.Count > 0;

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>Set when a sidecar fetch failed; the rest of the report still reflects what was learned.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Reads only the small, known-name sidecar files off a GitHub release — never the
/// installer, the wheel, or any other binary artifact.
/// <para>
/// This exists because a release's <c>checksums.txt</c> is not proof the artifacts
/// are real: some 0.8.x releases shipped 133-byte ASCII placeholder stubs under real artifact
/// names, and those stubs' hashes were faithfully listed in the checksum file. A hash that
/// matches its entry, even under a valid signature, still looks fine to a check that never
/// looks at size. So this inspector flags size/name mismatches directly from the asset list
/// GitHub already returned — no download needed for that part — and only fetches bytes for
/// two specific small sidecars to read their contents.
/// </para>
/// <para>
/// <b>It verifies no signature.</b> It reports which signature files the release <i>publishes</i>
/// (<see cref="ProvenanceReport.SignatureFilesPublished"/>); that they are valid, and who signed them, is
/// cosign's answer, asked by <see cref="ChecksumsSignatureVerifier"/> on the bytes the upgrade is about to trust.
/// </para>
/// </summary>
public sealed class ProvenanceInspector : IDisposable
{
    /// <summary>Assets at or below this size, named like a real artifact, are flagged as stubs.</summary>
    public const long StubSizeThresholdBytes = 1024;

    /// <summary>Extensions that imply "this should be a real build artifact".</summary>
    public static readonly IReadOnlyList<string> RealArtifactExtensions =
        [".exe", ".zip", ".tar.gz", ".whl", ".dcwheel", ".dcgateway"];

    public const string SetupAssetName = "DefenseClawSetup-x64.exe";
    public const string ProvenanceAssetName = SetupAssetName + ".provenance.json";
    public const string ChecksumsAssetName = "checksums.txt";
    public const string ChecksumsSignatureAssetName = ChecksumsAssetName + ".sig";
    public const string ChecksumsCertificateAssetName = ChecksumsAssetName + ".pem";
    public const string ChecksumsBundleAssetName = ChecksumsAssetName + ".bundle";

    /// <summary>Sidecars this inspector will ever request bytes for. Never an installer or archive.</summary>
    private static readonly IReadOnlyList<string> DownloadableSidecarNames =
        [ProvenanceAssetName, ChecksumsAssetName];

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ProvenanceInspector(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<ProvenanceReport> InspectAsync(
        IReadOnlyList<ReleaseAsset> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var stubWarnings = DetectStubs(assets);

        var checksumsAsset = FindAsset(assets, ChecksumsAssetName);
        var sigAsset = FindAsset(assets, ChecksumsSignatureAssetName);
        var pemAsset = FindAsset(assets, ChecksumsCertificateAssetName);
        var bundleAsset = FindAsset(assets, ChecksumsBundleAssetName);
        var provenanceAsset = FindAsset(assets, ProvenanceAssetName);

        var authenticode = AuthenticodeStatus.Unknown;
        var authenticodeDetail = $"No {ProvenanceAssetName} asset on this release; " +
                                  "Authenticode status could not be determined.";
        var provenanceFound = false;
        int? checksumsEntryCount = null;
        string? error = null;

        if (provenanceAsset is { DownloadUrl.Length: > 0 })
        {
            try
            {
                var json = await DownloadSidecarAsync(provenanceAsset, cancellationToken).ConfigureAwait(false);
                provenanceFound = true;
                (authenticode, authenticodeDetail) = ParseProvenance(json);
            }
            catch (HttpRequestException ex)
            {
                error = $"Could not read {ProvenanceAssetName}: {ex.Message}";
                authenticodeDetail = error;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                error = $"Timed out reading {ProvenanceAssetName}: {ex.Message}";
                authenticodeDetail = error;
            }
        }

        if (checksumsAsset is { DownloadUrl.Length: > 0 })
        {
            try
            {
                var text = await DownloadSidecarAsync(checksumsAsset, cancellationToken).ConfigureAwait(false);
                checksumsEntryCount = text
                    .Split('\n')
                    .Count(line => !string.IsNullOrWhiteSpace(line));
            }
            catch (HttpRequestException ex)
            {
                error ??= $"Could not read {ChecksumsAssetName}: {ex.Message}";
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                error ??= $"Timed out reading {ChecksumsAssetName}: {ex.Message}";
            }
        }

        return new ProvenanceReport
        {
            InstallerAuthenticode = authenticode,
            InstallerAuthenticodeDetail = authenticodeDetail,
            ProvenanceSidecarFound = provenanceFound,
            ChecksumsAssetPresent = checksumsAsset is not null,
            ChecksumsSignaturePresent = sigAsset is not null,
            ChecksumsCertificatePresent = pemAsset is not null,
            ChecksumsBundlePresent = bundleAsset is not null,
            ChecksumsEntryCount = checksumsEntryCount,
            StubWarnings = stubWarnings,
            Assets = assets,
            ErrorMessage = error,
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private static IReadOnlyList<StubAssetWarning> DetectStubs(IReadOnlyList<ReleaseAsset> assets)
    {
        var warnings = new List<StubAssetWarning>();

        foreach (var asset in assets)
        {
            if (asset.Size <= 0 || asset.Size >= StubSizeThresholdBytes)
            {
                continue;
            }

            var looksLikeRealArtifact = RealArtifactExtensions.Any(
                ext => asset.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

            if (!looksLikeRealArtifact)
            {
                continue;
            }

            warnings.Add(new StubAssetWarning(
                asset.Name,
                asset.Size,
                $"{asset.Name} is only {asset.Size} bytes but its name implies a real build artifact. " +
                "This matches the placeholder-stub pattern seen on some releases: a tiny ASCII file " +
                "whose hash is still listed in the release's checksums.txt, so a hash comparison alone would pass it."));
        }

        return warnings;
    }

    private static ReleaseAsset? FindAsset(IReadOnlyList<ReleaseAsset> assets, string name) =>
        assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetches one sidecar's text. Refuses anything not on the fixed allow-list of small,
    /// known-name sidecars — this is the one place a URL from the GitHub response turns into a
    /// network call, and it must never be reachable for an installer or archive asset.
    /// </summary>
    private async Task<string> DownloadSidecarAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        if (!DownloadableSidecarNames.Contains(asset.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to download '{asset.Name}': it is not one of the known small sidecar files.");
        }

        if (string.IsNullOrEmpty(asset.DownloadUrl))
        {
            throw new InvalidOperationException($"Asset '{asset.Name}' has no download URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the provenance sidecar's signing status. The shape is not assumed to be fixed —
    /// this release's sidecar says <c>"unsigned": true</c>, but a future signed build's sidecar
    /// may use different keys, so several plausible keys are tried before giving up honestly.
    /// </summary>
    private static (AuthenticodeStatus Status, string Detail) ParseProvenance(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var unsigned = TryGetBool(root, "unsigned");
            var statusText =
                TryGetString(root, "authenticode") ??
                TryGetString(root, "authenticode_status") ??
                TryGetString(root, "signature_status") ??
                TryGetString(root, "signing_status");

            if (unsigned == true || IsNotSignedText(statusText))
            {
                return (AuthenticodeStatus.NotSigned,
                    "The installer's own provenance sidecar reports it is not Authenticode-signed. " +
                    "Windows SmartScreen will warn on first run.");
            }

            if (unsigned == false || IsSignedText(statusText))
            {
                return (AuthenticodeStatus.Signed,
                    "The installer's provenance sidecar reports an Authenticode signature.");
            }

            return (AuthenticodeStatus.Unknown,
                "The provenance sidecar was read but did not report a recognizable signing status.");
        }
        catch (JsonException)
        {
            return (AuthenticodeStatus.Unknown, "The provenance sidecar could not be parsed as JSON.");
        }
    }

    private static bool IsNotSignedText(string? text) =>
        text is not null &&
        (string.Equals(text, "NotSigned", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(text, "unsigned", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(text, "not_signed", StringComparison.OrdinalIgnoreCase));

    private static bool IsSignedText(string? text) =>
        text is not null &&
        (string.Equals(text, "Signed", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(text, "signed", StringComparison.OrdinalIgnoreCase));

    private static bool? TryGetBool(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? TryGetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
