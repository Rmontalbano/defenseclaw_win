using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>
/// The release a gateway is checked against: downloads <c>defenseclaw-&lt;version&gt;-windows-amd64.zip</c> and <c>checksums.txt</c> from the
/// upstream release (the URLs <see cref="UpgradeRunner.AssetUrl"/> builds), compares the zip's SHA-256 with its line in checksums.txt, verifies the
/// signature on checksums.txt with cosign when cosign is installed (<see cref="ChecksumsSignatureVerifier"/>, the Updates window's check; a signature
/// cosign rejects stops here), and hashes <c>defenseclaw-gateway.exe</c> inside the zip. Nothing is extracted to disk: the entry is hashed from the
/// zip's stream, and the zip itself is deleted. Never elevated.
/// <para>
/// <b>Not verified live.</b> The asset name and the entry name are the ones documented for 1.0.0; a release that names them otherwise is
/// reported as "no zip" / "no gateway in the zip", and the feature then refuses rather than guesses.
/// </para>
/// </summary>
internal sealed class ReleaseGatewaySource : IReleaseGatewaySource
{
    /// <summary>The largest zip taken (the Setup installer is ~270 MB; the gateway zip is far smaller).</summary>
    public const long MaxZipBytes = 400L * 1024 * 1024;

    private readonly HttpClient _small;
    private readonly HttpClient _large;
    private readonly ChecksumsSignatureVerifier _verifier;
    private readonly Func<Task<string?>> _cosignPath;
    private readonly string _scratch;

    /// <param name="small">Capped client for checksums.txt.</param>
    /// <param name="large">Client without the body cap, for the zip (streamed to a scratch file, never held in memory).</param>
    /// <param name="verifier">The signature check on checksums.txt.</param>
    /// <param name="cosignPath">Where cosign is, or null.</param>
    /// <param name="scratch">A folder in the app's own staging area.</param>
    public ReleaseGatewaySource(HttpClient small, HttpClient large, ChecksumsSignatureVerifier verifier, Func<Task<string?>> cosignPath, string scratch)
    {
        _small = small ?? throw new ArgumentNullException(nameof(small));
        _large = large ?? throw new ArgumentNullException(nameof(large));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _cosignPath = cosignPath ?? throw new ArgumentNullException(nameof(cosignPath));
        _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));
    }

    /// <summary>The source the running app uses: the Updates window's HTTP client and signature check, a streaming client for the zip, a scratch folder of its own.</summary>
    public static ReleaseGatewaySource CreateDefault(DefenseClaw.Core.Cli.CliRunner cli)
    {
        ArgumentNullException.ThrowIfNull(cli);

        var scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DefenseClaw.App",
            "admin-gateway",
            "staging");
        var small = UpdateChecker.CreateHttpClient(TimeSpan.FromSeconds(30));
        var large = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        large.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateChecker.UserAgentValue);
        return new ReleaseGatewaySource(
            small,
            large,
            ChecksumsSignatureVerifier.Create(cli, small, scratch),
            async () => (await UpgradeRunner.DetectCosignAsync().ConfigureAwait(false)).Path,
            scratch);
    }

    public static string ZipName(string version) => $"defenseclaw-{version.TrimStart('v', 'V')}-windows-amd64.zip";

    public async Task<(ReleaseGateway? Release, string? Problem)> GetAsync(string version, CancellationToken cancellationToken)
    {
        if (!UpgradeRunner.IsValidReleaseVersion(version))
        {
            return (null, "The installed version is not a plain release number, so there is no release to check it against.");
        }

        var zipName = ZipName(version);
        string? zipPath = null;
        try
        {
            var checksums = await FetchSmallAsync(UpgradeRunner.AssetUrl(version, UpgradeRunner.ChecksumsAssetName), cancellationToken).ConfigureAwait(false);
            if (checksums is null)
            {
                return (null, $"checksums.txt for release {version} could not be read, so the gateway cannot be checked against it.");
            }

            var expectedZip = UpgradeRunner.FindChecksumEntry(Encoding.UTF8.GetString(checksums), zipName);
            if (expectedZip is null)
            {
                return (null, $"Release {version} lists no {zipName} in checksums.txt, so the gateway cannot be checked against it.");
            }

            var cosign = await _cosignPath().ConfigureAwait(false);
            var signature = await _verifier.VerifyAsync(version, checksums, cosign, cancellationToken).ConfigureAwait(false);
            if (signature.BlocksUpgrade)
            {
                return (null, "The signature on checksums.txt did not verify: " + signature.Detail);
            }

            Directory.CreateDirectory(_scratch);
            zipPath = Path.Combine(_scratch, "gw-" + Guid.NewGuid().ToString("N")[..12] + ".zip");
            var (zipSha, downloadProblem) = await DownloadAsync(UpgradeRunner.AssetUrl(version, zipName), zipPath, cancellationToken).ConfigureAwait(false);
            if (zipSha is null)
            {
                return (null, downloadProblem);
            }

            if (!string.Equals(zipSha, expectedZip, StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"{zipName} hashes to {zipSha}, but checksums.txt lists {expectedZip}. Not used.");
            }

            var (gatewaySha, entryProblem) = HashGatewayEntry(zipPath);
            return gatewaySha is null
                ? (null, entryProblem)
                : (new ReleaseGateway(version, gatewaySha, zipSha, signature.Label, signature.State.ToString()), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            return (null, "The release could not be checked: " + ex.Message);
        }
        finally
        {
            if (zipPath is not null)
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left in the app's scratch folder.
                }
            }
        }
    }

    private async Task<byte[]?> FetchSmallAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await _small.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    private async Task<(string? Sha256, string? Problem)> DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var response = await _large.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"{Path.GetFileName(url.LocalPath)} could not be downloaded ({(int)response.StatusCode}).");
        }

        if (response.Content.Headers.ContentLength is > MaxZipBytes)
        {
            return (null, $"{Path.GetFileName(url.LocalPath)} is larger than the {MaxZipBytes / (1024 * 1024)} MiB this check accepts.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = 0L;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxZipBytes)
                {
                    return (null, "The download is larger than this check accepts.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), null);
    }

    /// <summary>Hashes the one <c>defenseclaw-gateway.exe</c> in the zip from its entry stream. Exactly one entry of that name, of a plausible size.</summary>
    internal static (string? Sha256, string? Problem) HashGatewayEntry(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Where(static e => string.Equals(Path.GetFileName(e.FullName), AdminLayout.GatewayFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1)
        {
            return (null, entries.Length == 0
                ? $"The release zip has no {AdminLayout.GatewayFileName}."
                : $"The release zip has {entries.Length} files named {AdminLayout.GatewayFileName}; which one is the gateway is not guessed.");
        }

        var entry = entries[0];
        if (entry.Length is < 1 or > AdminCopyInspector.MaxGatewayBytes)
        {
            return (null, $"{AdminLayout.GatewayFileName} in the release zip has an implausible size.");
        }

        using var stream = entry.Open();
        return (Hex.HashOf(stream), null);
    }
}

/// <summary>Builds the real source (HTTP clients, a scratch path) only when the first release check is asked for.</summary>
internal sealed class LazyReleaseGatewaySource : IReleaseGatewaySource
{
    private readonly Lazy<IReleaseGatewaySource> _inner;

    public LazyReleaseGatewaySource(Func<IReleaseGatewaySource> create)
    {
        _inner = new Lazy<IReleaseGatewaySource>(create ?? throw new ArgumentNullException(nameof(create)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<(ReleaseGateway? Release, string? Problem)> GetAsync(string version, CancellationToken cancellationToken) =>
        _inner.Value.GetAsync(version, cancellationToken);
}
