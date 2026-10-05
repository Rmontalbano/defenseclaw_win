using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Updates;

/// <summary>What is known about the sigstore signature on a release's <c>checksums.txt</c>, as far as this app actually checked it.</summary>
public enum ChecksumsSignatureState
{
    /// <summary>Nothing has been asked yet. Never shown as a claim about the release.</summary>
    NotChecked = 0,

    /// <summary>The release publishes no signature for checksums.txt: neither a <c>.bundle</c> nor a <c>.sig</c> together with its <c>.pem</c>.</summary>
    NotPublished,

    /// <summary>A signature is published and this app did not verify it, because cosign was not found.</summary>
    NotVerifiedNoCosign,

    /// <summary>A signature is published and cosign was found, but it could not be used (older than 2.0, or it would not start): the same as no cosign.</summary>
    NotVerifiedCosignUnusable,

    /// <summary>The signature files could not be fetched, so nothing was checked.</summary>
    NotVerifiedUnavailable,

    /// <summary><c>cosign verify-blob</c> exited 0: the signature is valid and was made by the upstream release workflow. The only state that says "verified".</summary>
    Verified,

    /// <summary>
    /// <c>cosign verify-blob</c> ran and did not come to a verification: it exited non-zero, or it started and did not finish in time (a network that
    /// will not let it reach sigstore looks like this). An upgrade is blocked on this state: a check that was started and did not succeed must not quietly
    /// become "no check".
    /// </summary>
    DidNotVerify,
}

/// <summary>
/// The outcome of checking the signature on one release's <c>checksums.txt</c>: a state, the words to show for it, and - when cosign
/// ran - the command and what it said. <see cref="Label"/> and <see cref="Detail"/> are the only text the app shows about the signature, so
/// "verified" appears exactly when <see cref="State"/> is <see cref="ChecksumsSignatureState.Verified"/>.
/// </summary>
public sealed record ChecksumsSignatureResult
{
    public ChecksumsSignatureState State { get; init; }

    /// <summary>One or two sentences: what was and was not checked, and what that means. Always set.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The cosign command line that ran (display form), when one did.</summary>
    public string? CommandLine { get; init; }

    /// <summary>The last lines cosign printed, when it rejected the signature; null otherwise.</summary>
    public string? CosignOutput { get; init; }

    public bool IsVerified => State == ChecksumsSignatureState.Verified;

    /// <summary>True when cosign ran and rejected the signature: the upgrade does not go on.</summary>
    public bool BlocksUpgrade => State == ChecksumsSignatureState.DidNotVerify;

    /// <summary>True when the release publishes a signature that this app did not (or could not) verify.</summary>
    public bool IsPublishedButNotVerified =>
        State is ChecksumsSignatureState.NotVerifiedNoCosign or ChecksumsSignatureState.NotVerifiedCosignUnusable or ChecksumsSignatureState.NotVerifiedUnavailable;

    /// <summary>The short phrase for a badge. Says "verified" only for <see cref="ChecksumsSignatureState.Verified"/>.</summary>
    public string Label => State switch
    {
        ChecksumsSignatureState.Verified => "checksums.txt signature verified (cosign)",
        ChecksumsSignatureState.NotVerifiedNoCosign => "signature present, not verified (cosign not installed)",
        ChecksumsSignatureState.NotVerifiedCosignUnusable => "signature present, not verified (cosign could not run)",
        ChecksumsSignatureState.NotVerifiedUnavailable => "signature not verified (signature files could not be fetched)",
        ChecksumsSignatureState.DidNotVerify => "signature did not verify",
        ChecksumsSignatureState.NotPublished => "no signature published for checksums.txt",
        _ => "signature not checked yet",
    };

    /// <summary>Ok / Warn / Bad / Neutral, the tone of a badge for this state.</summary>
    public string BadgeKey => State switch
    {
        ChecksumsSignatureState.Verified => "Ok",
        ChecksumsSignatureState.DidNotVerify => "Bad",
        ChecksumsSignatureState.NotChecked => "Neutral",
        _ => "Warn",
    };

    public static ChecksumsSignatureResult NotChecked { get; } = new()
    {
        State = ChecksumsSignatureState.NotChecked,
        Detail = "The signature on checksums.txt has not been checked.",
    };
}

/// <summary>
/// What one cosign run came to: the exit code (null when it did not finish), why it did not, what it printed, and whether the process was started at all
/// (a cosign that could not be started is not a cosign that failed the check).
/// </summary>
public sealed record CosignRun(int? ExitCode, string? FailureReason, IReadOnlyList<string> Output, bool Started = true);

/// <summary>Runs cosign. The app's version goes through <see cref="CliRunner"/> (so the run is in Activity); a test supplies its own.</summary>
public delegate Task<CosignRun> CosignExecutor(
    string cosignPath,
    IReadOnlyList<string> argv,
    TimeSpan timeout,
    CancellationToken cancellationToken);

/// <summary>
/// Verifies the sigstore signature on a release's <c>checksums.txt</c> with <c>cosign verify-blob</c> - the one thing the upgrade path used to
/// say it did and did not do. Without a verified signature, the SHA-256 comparison with checksums.txt shows only that a download matches a file
/// served from the same place (a compromised release, or a TLS-intercepting proxy, replaces both); with one, the file is tied to the upstream release
/// workflow that signed it.
/// <para>
/// <b>What is checked.</b> Upstream signs <c>checksums.txt</c> keyless in its <c>Release</c> workflow (<c>cosign sign-blob</c>, GitHub OIDC) and verifies it in
/// that workflow with <c>--certificate-identity https://github.com/&lt;repo&gt;/.github/workflows/release.yaml@refs/heads/main --certificate-oidc-issuer
/// https://token.actions.githubusercontent.com</c>; its own <c>install.ps1</c> checks the same identity (<see cref="CertificateIdentity"/>, <see cref="OidcIssuer"/>).
/// Both are passed here, as the exact identity rather than a pattern. The release carries <c>checksums.txt.bundle</c> (certificate, signature and
/// transparency-log entry in one file, which is what upstream's installer verifies) and the older <c>checksums.txt.sig</c> with <c>checksums.txt.pem</c>;
/// the bundle is used when it is there, the pair otherwise - and the pair only with cosign 2.x: cosign 3 dropped <c>--certificate</c> and <c>--signature</c>
/// from <c>verify-blob</c> (a bundle, a key or a trusted root is all it takes), so a release with no bundle cannot be checked by it and is reported as exactly that.
/// The workflow signs with cosign 2.6.3's <c>sign-blob --bundle</c> and no <c>--new-bundle-format</c>, which writes the older bundle format; cosign 3 sniffs the file
/// and falls back to its legacy loader (<c>checkNewBundle</c> in its <c>verify-blob</c>), so the same <c>--bundle</c> command line is right for both.
/// </para>
/// <para>
/// <b>Honest by construction.</b> <see cref="ChecksumsSignatureState.Verified"/> is returned only for cosign's exit code 0 on the very bytes passed in (they are
/// written to a scratch directory and verified from there - never a second download of the same name). cosign not found, too old (below 2.0, as upstream
/// requires), not startable, or the signature files not fetchable is "not verified" and says why; a cosign that ran and exited non-zero, or started and did not
/// finish, is <see cref="ChecksumsSignatureState.DidNotVerify"/>, which blocks the upgrade (a check that was begun and did not succeed must not quietly become no check).
/// Every cosign run is a <see cref="CliRunner"/> run with the absolute path, shown in Activity.
/// </para>
/// </summary>
public sealed partial class ChecksumsSignatureVerifier
{
    public const string ChecksumsFileName = "checksums.txt";
    public const string BundleAssetName = ChecksumsFileName + ".bundle";
    public const string SignatureAssetName = ChecksumsFileName + ".sig";
    public const string CertificateAssetName = ChecksumsFileName + ".pem";

    /// <summary>The OIDC issuer of the certificate: GitHub Actions' token service.</summary>
    public const string OidcIssuer = "https://token.actions.githubusercontent.com";

    /// <summary>
    /// The identity in the signing certificate of an upstream release: the <c>Release</c> workflow of the repository, run from <c>main</c>. Upstream's workflow
    /// says to keep that file's name and run it from main only, because installers verify this exact identity; a run from another branch
    /// (their dry runs) signs as a different identity and is correctly refused.
    /// </summary>
    public static string CertificateIdentity { get; } =
        $"https://github.com/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}/.github/workflows/release.yaml@refs/heads/main";

    /// <summary>The largest signature file taken: the real ones are 96 bytes, 2.6 KB and 8.9 KB.</summary>
    public const int MaxSidecarBytes = 256 * 1024;

    /// <summary>The last cosign major version whose <c>verify-blob</c> still takes <c>--certificate</c> and <c>--signature</c>; 3 takes only a bundle.</summary>
    public const int LastMajorWithDetachedSignatures = 2;

    /// <summary><c>cosign version</c> is local and instant.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The check fetches sigstore's trust root the first time cosign runs, so it gets room.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How many of cosign's last lines are kept when it rejects.</summary>
    private const int KeptOutputLines = 6;

    /// <summary>The start of a scratch directory's name. The staging prune (<c>UpgradeRunner.PruneStaging</c>) only touches version-named directories, so these are this class's to remove.</summary>
    public const string ScratchPrefix = ".verify-";

    /// <summary>A scratch directory older than this was left behind by a run that never finished (the app was closed or crashed mid-check).</summary>
    public static readonly TimeSpan StaleScratchAge = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly string _scratchRoot;
    private readonly CosignExecutor _execute;

    public ChecksumsSignatureVerifier(HttpClient http, string scratchRoot, CosignExecutor execute)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _scratchRoot = scratchRoot ?? throw new ArgumentNullException(nameof(scratchRoot));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>A verifier whose cosign runs go through <paramref name="cli"/>: the exact argv, output and exit code land in the Activity panel.</summary>
    public static ChecksumsSignatureVerifier Create(CliRunner cli, HttpClient http, string scratchRoot)
    {
        ArgumentNullException.ThrowIfNull(cli);

        return new ChecksumsSignatureVerifier(
            http,
            scratchRoot,
            async (path, argv, timeout, cancellationToken) =>
            {
                var started = false;
                try
                {
                    var invocation = await cli
                        .RunExecutableAsync(
                            path,
                            argv,
                            stdinSecret: null,
                            cancellationToken,
                            CliRunOptions.WithTimeout(timeout) with { OnProcessStarted = () => started = true })
                        .ConfigureAwait(false);
                    return new CosignRun(invocation.ExitCode, invocation.FailureReason, invocation.OutputLines.Select(l => l.Text).ToArray(), started);
                }
                catch (SecretInArgumentException ex)
                {
                    // The runner refuses an argv that carries a secret it knows (a path that happens to contain one): nothing was started, and that is
                    // a cosign that could not be used, not an exception for a caller that only asked about a signature.
                    return new CosignRun(null, ex.Message, Array.Empty<string>(), Started: false);
                }
            });
    }

    /// <summary>
    /// The arguments of the check: <c>verify-blob</c> with the signature (the bundle, or the certificate and signature files), the upstream
    /// identity and issuer, and the checksums file. The same list the Activity panel shows after the run.
    /// </summary>
    public static IReadOnlyList<string> BuildVerifyArgv(string checksumsPath, string? bundlePath, string? signaturePath, string? certificatePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(checksumsPath);

        var argv = new List<string> { "verify-blob" };
        if (bundlePath is { Length: > 0 })
        {
            argv.Add("--bundle");
            argv.Add(bundlePath);
        }
        else if (signaturePath is { Length: > 0 } && certificatePath is { Length: > 0 })
        {
            argv.Add("--certificate");
            argv.Add(certificatePath);
            argv.Add("--signature");
            argv.Add(signaturePath);
        }
        else
        {
            throw new ArgumentException("A bundle, or a signature with its certificate, is needed.", nameof(bundlePath));
        }

        argv.Add("--certificate-identity");
        argv.Add(CertificateIdentity);
        argv.Add("--certificate-oidc-issuer");
        argv.Add(OidcIssuer);
        argv.Add(checksumsPath);
        return argv;
    }

    /// <summary>
    /// Checks the signature on <paramref name="checksums"/>, the exact bytes of release <paramref name="version"/>'s <c>checksums.txt</c> that the caller is
    /// about to trust. Never throws for a network or cosign problem - those are states - but honours <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="cosignPath">Absolute path of cosign; null or empty when none was found (the signature is then reported as present or absent, never verified).</param>
    public async Task<ChecksumsSignatureResult> VerifyAsync(
        string version,
        byte[] checksums,
        string? cosignPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(checksums);
        cancellationToken.ThrowIfCancellationRequested();

        var files = await FetchSignatureFilesAsync(version, cancellationToken).ConfigureAwait(false);
        if (files.Problem is { } problem)
        {
            return Result(
                ChecksumsSignatureState.NotVerifiedUnavailable,
                $"The release's signature files could not be fetched ({problem}), so the signature on checksums.txt was not checked. " +
                "The download is compared with checksums.txt from the same release only.");
        }

        if (!files.IsPublished)
        {
            return Result(
                ChecksumsSignatureState.NotPublished,
                "The release publishes no sigstore signature for checksums.txt (it would need checksums.txt.bundle, or checksums.txt.sig together with checksums.txt.pem), " +
                "so comparing the download with checksums.txt is an integrity check only: it shows the download matches that file, not that the file is the one the project signed.");
        }

        if (string.IsNullOrWhiteSpace(cosignPath))
        {
            return Result(
                ChecksumsSignatureState.NotVerifiedNoCosign,
                "The release publishes a sigstore signature for checksums.txt, but this app can only verify it with cosign, which was not found. " +
                "Without it the download is compared with checksums.txt from the same release, which shows the download matches that file and not that the file is the one the project signed. " +
                "Install cosign (winget install Sigstore.Cosign), restart this app and check again to verify the signature.");
        }

        var (major, unusable) = await ReadCosignMajorVersionAsync(cosignPath, cancellationToken).ConfigureAwait(false);
        if (unusable is null && files.Bundle is null && major > LastMajorWithDetachedSignatures)
        {
            unusable =
                $"this release publishes no checksums.txt.bundle, only a detached signature and certificate, and cosign {major}.x accepts only a bundle.";
        }

        if (unusable is not null)
        {
            return Result(
                ChecksumsSignatureState.NotVerifiedCosignUnusable,
                $"The release publishes a sigstore signature for checksums.txt, but cosign could not check it: {unusable} " +
                "The download is compared with checksums.txt from the same release only.");
        }

        return await RunVerifyAsync(version, checksums, files, cosignPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChecksumsSignatureResult> RunVerifyAsync(
        string version,
        byte[] checksums,
        SignatureFiles files,
        string cosignPath,
        CancellationToken cancellationToken)
    {
        PruneStaleScratch();

        var directory = Path.Combine(_scratchRoot, ScratchPrefix + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(directory);

            var checksumsPath = Path.Combine(directory, ChecksumsFileName);
            await File.WriteAllBytesAsync(checksumsPath, checksums, cancellationToken).ConfigureAwait(false);

            string? bundlePath = null;
            string? signaturePath = null;
            string? certificatePath = null;
            if (files.Bundle is { } bundle)
            {
                bundlePath = Path.Combine(directory, BundleAssetName);
                await File.WriteAllBytesAsync(bundlePath, bundle, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                signaturePath = Path.Combine(directory, SignatureAssetName);
                certificatePath = Path.Combine(directory, CertificateAssetName);
                await File.WriteAllBytesAsync(signaturePath, files.Signature!, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(certificatePath, files.Certificate!, cancellationToken).ConfigureAwait(false);
            }

            var argv = BuildVerifyArgv(checksumsPath, bundlePath, signaturePath, certificatePath);
            var commandLine = CommandReview.CommandLine(cosignPath, argv);

            var run = await _execute(cosignPath, argv, VerifyTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (run.ExitCode == 0)
            {
                return new ChecksumsSignatureResult
                {
                    State = ChecksumsSignatureState.Verified,
                    CommandLine = commandLine,
                    Detail =
                        $"cosign verified the sigstore signature on release {version}'s checksums.txt: it was signed by {CertificateIdentity} through GitHub Actions. " +
                        "That ties the file to the upstream release workflow. The installer itself is not Authenticode-signed; its SHA-256 is compared with this file.",
                };
            }

            if (run.ExitCode is { } code)
            {
                var said = string.Join(" | ", run.Output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(KeptOutputLines).Select(l => l.Trim()));
                return new ChecksumsSignatureResult
                {
                    State = ChecksumsSignatureState.DidNotVerify,
                    CommandLine = commandLine,
                    CosignOutput = said.Length == 0 ? null : said,
                    Detail =
                        $"cosign rejected the sigstore signature on release {version}'s checksums.txt (exit code {code}). Do not run this release's installer. " +
                        "If cosign's message is about reaching the sigstore services, fix that and check again; if it is about the signature or who signed it, treat the release as untrustworthy" +
                        (said.Length == 0 ? "." : $". cosign said: {said}"),
                };
            }

            // No exit code. If cosign never started it is no more use than a missing one; if it started and did not finish (timed out), the check was begun and did not
            // succeed, and the upgrade stops rather than carry on as though it had not been asked for.
            if (!run.Started)
            {
                return new ChecksumsSignatureResult
                {
                    State = ChecksumsSignatureState.NotVerifiedCosignUnusable,
                    CommandLine = commandLine,
                    Detail =
                        $"The release publishes a sigstore signature for checksums.txt, but cosign could not be started ({run.FailureReason ?? "no reason given"}). " +
                        "The download is compared with checksums.txt from the same release only.",
                };
            }

            var unfinished = string.Join(" | ", run.Output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(KeptOutputLines).Select(l => l.Trim()));
            return new ChecksumsSignatureResult
            {
                State = ChecksumsSignatureState.DidNotVerify,
                CommandLine = commandLine,
                CosignOutput = unfinished.Length == 0 ? null : unfinished,
                Detail =
                    $"cosign did not finish checking the sigstore signature on release {version}'s checksums.txt ({run.FailureReason ?? "no exit code"}), so it was not verified, and the upgrade is stopped. " +
                    "This usually means cosign could not reach the sigstore services: check the network (and any proxy) and try again." +
                    (unfinished.Length == 0 ? string.Empty : $" cosign said: {unfinished}"),
            };
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// cosign's major version, or why it cannot be used: it must be 2.0 or newer (the oldest upstream's installer accepts). The major version
    /// is returned because it decides which verification the command line may carry.
    /// </summary>
    private async Task<(int Major, string? Unusable)> ReadCosignMajorVersionAsync(string cosignPath, CancellationToken cancellationToken)
    {
        var run = await _execute(cosignPath, new[] { "version" }, VersionTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (run.ExitCode is null)
        {
            return (0, $"it did not start or finish ({run.FailureReason ?? "no exit code"}).");
        }

        if (run.ExitCode != 0)
        {
            return (0, $"'cosign version' exited with code {run.ExitCode}.");
        }

        var match = GitVersionPattern().Match(string.Join('\n', run.Output));
        if (!match.Success)
        {
            return (0, "its version could not be read from 'cosign version', and cosign 2.0 or newer is needed.");
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        return major >= 2
            ? (major, null)
            : (major, $"cosign {match.Groups["whole"].Value} is older than 2.0, which is the oldest that can check this signature.");
    }

    [GeneratedRegex(@"GitVersion:\s*v?(?<whole>(?<major>\d+)\.\d+[0-9A-Za-z.\-+]*)", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersionPattern();

    // ------------------------------------------------------------------ the signature files

    /// <summary>The signature files of one release: the bundle, or the signature with its certificate. <see cref="Problem"/> is set when a fetch failed for a reason other than "not there".</summary>
    private sealed record SignatureFiles(byte[]? Bundle, byte[]? Signature, byte[]? Certificate, string? Problem)
    {
        public bool IsPublished => Bundle is not null || (Signature is not null && Certificate is not null);
    }

    private async Task<SignatureFiles> FetchSignatureFilesAsync(string version, CancellationToken cancellationToken)
    {
        var bundle = await FetchSmallAsync(version, BundleAssetName, cancellationToken).ConfigureAwait(false);
        if (bundle.Problem is not null)
        {
            return new SignatureFiles(null, null, null, bundle.Problem);
        }

        if (bundle.Bytes is not null)
        {
            return new SignatureFiles(bundle.Bytes, null, null, null);
        }

        var signature = await FetchSmallAsync(version, SignatureAssetName, cancellationToken).ConfigureAwait(false);
        var certificate = await FetchSmallAsync(version, CertificateAssetName, cancellationToken).ConfigureAwait(false);
        return new SignatureFiles(null, signature.Bytes, certificate.Bytes, signature.Problem ?? certificate.Problem);
    }

    /// <summary>One small release asset: its bytes, or neither bytes nor problem when the release does not have it (404), or the problem.</summary>
    private async Task<(byte[]? Bytes, string? Problem)> FetchSmallAsync(string version, string assetName, CancellationToken cancellationToken)
    {
        var url = UpgradeRunner.AssetUrl(version, assetName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"{assetName}: HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxSidecarBytes)
            {
                return (null, $"{assetName} is larger than {MaxSidecarBytes:N0} bytes");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxSidecarBytes)
                {
                    return (null, $"{assetName} is larger than {MaxSidecarBytes:N0} bytes");
                }

                buffer.Write(chunk, 0, read);
            }

            // An empty file is no signature: cosign would reject it, and "published" must mean something.
            return buffer.Length == 0 ? (null, null) : (buffer.ToArray(), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"{assetName}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return (null, $"{assetName}: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"{assetName}: timed out ({ex.Message})");
        }
    }

    private static ChecksumsSignatureResult Result(ChecksumsSignatureState state, string detail) => new() { State = state, Detail = detail };

    /// <summary>Removes scratch directories an earlier run left behind. Best effort: a directory in use, or not ours to delete, stays for next time.</summary>
    private void PruneStaleScratch()
    {
        try
        {
            if (!Directory.Exists(_scratchRoot))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(_scratchRoot, ScratchPrefix + "*"))
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || DateTime.UtcNow - info.CreationTimeUtc < StaleScratchAge)
                {
                    continue;
                }

                TryDeleteDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Scratch files: the next run uses its own directory and the staging prune never touches a ".verify-" one.
        }
    }
}
