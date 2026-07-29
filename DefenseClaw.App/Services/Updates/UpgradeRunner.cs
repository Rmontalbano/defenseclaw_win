using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Updates;

/// <summary>Where <c>cosign</c> was found, if anywhere — the upgrade script's hard prerequisite.</summary>
public enum CosignAvailability
{
    /// <summary>Not found anywhere this app knows to look.</summary>
    Missing = 0,

    /// <summary>
    /// Found on disk, but not on the PATH this process would hand to <c>powershell.exe</c>.
    /// The resolver shells out to a bare <c>cosign</c>, so this is still a refusal — usually it
    /// means winget installed it after this app started.
    /// </summary>
    FoundOffPath,

    /// <summary>On the PATH a child process would inherit. This is the only usable state.</summary>
    OnPath,
}

/// <summary>Result of probing for cosign, with the guidance the UI shows when it is not usable.</summary>
public sealed record CosignStatus
{
    public CosignAvailability Availability { get; init; }

    /// <summary>Full path of the cosign executable that answered, when one did.</summary>
    public string? Path { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>Non-null exactly when <see cref="IsUsable"/> is false.</summary>
    public string? InstallGuidance { get; init; }

    /// <summary>True only when a child process would resolve a bare <c>cosign</c> invocation.</summary>
    public bool IsUsable => Availability == CosignAvailability.OnPath;
}

/// <summary>Why a staging attempt ended the way it did. Only <see cref="Verified"/> stages a file.</summary>
public enum UpgradeStagingOutcome
{
    /// <summary>Downloaded, size-sane, and its SHA-256 matched the release's checksums.txt entry.</summary>
    Verified = 0,

    /// <summary>The download is implausibly small — the 133-byte placeholder-stub pattern.</summary>
    StubDetected,

    /// <summary>The download's SHA-256 did not match the release's checksums.txt entry.</summary>
    ChecksumMismatch,

    /// <summary>checksums.txt was unreachable, or carried no entry for the upgrade script.</summary>
    ChecksumUnavailable,

    /// <summary>GitHub was unreachable, returned an error, or the asset does not exist.</summary>
    DownloadFailed,

    /// <summary>GitHub rate-limited the request. Ordinary "try later", never an exception.</summary>
    RateLimited,

    /// <summary>Everything verified, but the bytes could not be written to the staging directory.</summary>
    WriteFailed,
}

/// <summary>
/// One verified upgrade script, sitting under
/// <c>%LOCALAPPDATA%\DefenseClaw.App\upgrades\&lt;version&gt;\</c>. Only ever produced after the
/// size sanity check and the checksum comparison both passed.
/// </summary>
public sealed record StagedUpgradeScript
{
    /// <summary>Release tag the script came from, e.g. <c>0.8.9</c>.</summary>
    public required string Version { get; init; }

    public required string FilePath { get; init; }

    public required string SourceUrl { get; init; }

    public required string ChecksumsUrl { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 computed over the downloaded bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The entry read out of the release's checksums.txt. Equal to <see cref="Sha256"/>.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>True when the release also carries checksums.txt.sig and checksums.txt.pem.</summary>
    public bool ChecksumsSigstoreSigned { get; init; }

    public DateTimeOffset StagedAt { get; init; }

    public string SizeText => SizeBytes >= 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{SizeBytes / 1024.0:0.#} KB ({SizeBytes:N0} bytes)")
        : string.Create(CultureInfo.CurrentCulture, $"{SizeBytes:N0} bytes");
}

/// <summary>Outcome of one <see cref="UpgradeRunner.DownloadAndVerifyAsync"/> call.</summary>
public sealed record UpgradeStagingResult
{
    public UpgradeStagingOutcome Outcome { get; init; }

    /// <summary>Non-null exactly when <see cref="Succeeded"/>.</summary>
    public StagedUpgradeScript? Script { get; init; }

    /// <summary>One line, always set — safe to show whether this succeeded or failed.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Long-form explanation for the failure InfoBar. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    public string? ComputedSha256 { get; init; }

    public string? ExpectedSha256 { get; init; }

    public long? SizeBytes { get; init; }

    public bool Succeeded => Outcome == UpgradeStagingOutcome.Verified && Script is not null;
}

/// <summary>How a resolver run ended.</summary>
public sealed record UpgradeRunResult
{
    /// <summary>The recorded invocation — argv, output and exit code, same object Activity holds.</summary>
    public CliInvocation? Invocation { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Set when the process could not be started, was cancelled, or was refused pre-flight.</summary>
    public string? FailureReason { get; init; }

    public bool Succeeded => FailureReason is null && ExitCode == 0;
}

/// <summary>
/// Downloads, verifies and runs the canonical Windows upgrade channel: the <b>target</b>
/// release's own <c>defenseclaw-upgrade.ps1</c> asset.
/// <para>
/// <b>Why that script and not <c>defenseclaw upgrade</c>.</b> The installed CLI's own upgrade
/// verb fails on 0.8.7 with "no canonical release-managed gateway"; the release asset is the
/// supported path. It is a ~2900-line manifest-aware resolver that cosign-verifies the signed
/// release contract <i>before</i> touching anything, journals the upgrade in two phases with
/// automatic rollback, and resumes after a crash. This class never invokes the installed CLI's
/// upgrade verb.
/// </para>
/// <para>
/// <b>What is verified here, and what is not.</b> This class checks two things the resolver
/// cannot check about itself: that the download is not one of the 133-byte placeholder stubs
/// some releases shipped (see <see cref="MinimumPlausibleScriptBytes"/>), and that its SHA-256
/// matches the entry in the same release's sigstore-signed <c>checksums.txt</c>. Everything
/// beyond that — the manifest, the artifacts, the signatures over them — is the resolver's own
/// cosign-backed job, which is why <see cref="DetectCosign"/> gates the run.
/// </para>
/// <para>
/// <b>Never automatic.</b> Staging and running are two separate calls, each behind a distinct
/// user action, and the run goes through <see cref="CliRunner.RunExecutableAsync"/> so the exact
/// argv, the streaming output and the exit code land in the Activity panel like every other
/// mutation this app makes.
/// </para>
/// </summary>
public sealed class UpgradeRunner
{
    public const string ScriptAssetName = "defenseclaw-upgrade.ps1";

    public const string ChecksumsAssetName = "checksums.txt";

    /// <summary>
    /// Anything smaller than this is a stub, not a resolver. The real 0.8.9 script is 216,432
    /// bytes; the known-bad placeholders are 133 bytes — and their hashes are faithfully listed
    /// in the correctly signed checksums.txt, so a hash check alone would wave them through.
    /// </summary>
    public const long MinimumPlausibleScriptBytes = 10 * 1024;

    /// <summary>Upper sanity bound; the script is a PowerShell file, not an artifact.</summary>
    public const long MaximumPlausibleScriptBytes = 8 * 1024 * 1024;

    /// <summary>The flag that makes the resolver non-interactive. Every other default is left alone.</summary>
    public const string NonInteractiveFlag = "-Yes";

    private static readonly string[] CosignFileNames = ["cosign.exe", "cosign"];

    private readonly CliRunner _cli;
    private readonly HttpClient _http;

    public UpgradeRunner(CliRunner cli, HttpClient http, string? stagingRoot = null)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        StagingRoot = stagingRoot ?? DefaultStagingRoot();
    }

    /// <summary>
    /// Raised the moment the resolver process is recorded, before it has produced output, so the
    /// window can bind its console to the live <see cref="CliInvocation"/> and tick
    /// <see cref="CliInvocation.Snapshot"/> as lines arrive.
    /// </summary>
    public event EventHandler<CliInvocation>? InvocationStarted;

    /// <summary><c>%LOCALAPPDATA%\DefenseClaw.App\upgrades</c> unless overridden.</summary>
    public string StagingRoot { get; }

    /// <summary>True between the start and end of a resolver run started by this instance.</summary>
    public bool IsRunning { get; private set; }

    public static string DefaultStagingRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "upgrades");

    /// <summary>Release-asset URL for <paramref name="assetName"/> at <paramref name="version"/>.</summary>
    public static Uri AssetUrl(string version, string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        return new Uri(
            $"https://github.com/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}/releases/download/" +
            $"{Uri.EscapeDataString(version.Trim())}/{Uri.EscapeDataString(assetName)}");
    }

    /// <summary>
    /// The exact argument list the resolver is launched with. The confirm overlay renders this
    /// same list, so the screen cannot drift from what executes.
    /// </summary>
    public static IReadOnlyList<string> BuildArgv(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        return ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, NonInteractiveFlag];
    }

    /// <summary>
    /// Windows PowerShell 5.1, by absolute path. Resolved rather than trusted to PATH so the
    /// overlay can show the operator exactly which binary will run.
    /// </summary>
    public static string ResolvePowerShellPath()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    /// <summary>Display form of the full command line. For humans; the runner never re-parses it.</summary>
    public static string DescribeCommand(string scriptPath) =>
        string.Join(' ', new[] { ResolvePowerShellPath() }.Concat(BuildArgv(scriptPath)).Select(Quote));

    /// <summary>
    /// Probes for cosign the way the resolver will: a bare <c>cosign</c> on the PATH a child
    /// process inherits. Falls back to the persisted user/machine PATH and winget's package
    /// directory purely to tell "not installed" apart from "installed since this app started",
    /// which are very different pieces of advice.
    /// </summary>
    /// <param name="processPath">PATH a child would inherit. Defaults to this process's PATH.</param>
    /// <param name="offPathDirectories">Extra directories to probe. Defaults to the winget package dirs.</param>
    /// <param name="fileExists">Filesystem probe; overridable for tests.</param>
    public static CosignStatus DetectCosign(
        IEnumerable<string>? processPath = null,
        IEnumerable<string>? offPathDirectories = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var pathDirectories = (processPath ?? SplitPath(Environment.GetEnvironmentVariable("PATH"))).ToArray();

        var onPath = ProbeDirectories(pathDirectories, exists);
        if (onPath is not null)
        {
            return new CosignStatus
            {
                Availability = CosignAvailability.OnPath,
                Path = onPath,
                Detail = $"cosign is on PATH ({onPath}). The upgrade resolver can verify the signed release contract.",
            };
        }

        var offPath = ProbeDirectories(offPathDirectories ?? DefaultOffPathDirectories(), exists);
        if (offPath is not null)
        {
            return new CosignStatus
            {
                Availability = CosignAvailability.FoundOffPath,
                Path = offPath,
                Detail = $"cosign exists at {offPath}, but it is not on the PATH this app would hand to " +
                         "powershell.exe — so the resolver would still refuse to run.",
                InstallGuidance =
                    "cosign was installed after this app started. Restart DefenseClaw for Windows (or the " +
                    "whole session) so the updated PATH is inherited, then re-check.",
            };
        }

        return new CosignStatus
        {
            Availability = CosignAvailability.Missing,
            Detail = "cosign was not found. The upgrade script verifies the signed release contract with it " +
                     "before touching anything, and refuses to run without it.",
            InstallGuidance =
                "Install it with:  winget install Sigstore.Cosign\n" +
                "The winget package lays the binary down as cosign-windows-amd64.exe, so copy it to cosign.exe " +
                "in the same folder — the script invokes a bare 'cosign'. Then restart this app so the new PATH " +
                "is picked up, and re-check.",
        };
    }

    /// <summary>
    /// Fetches the target release's upgrade script, sanity-checks its size, verifies its SHA-256
    /// against the same release's checksums.txt, and only then writes it under
    /// <see cref="StagingRoot"/>. Nothing reaches disk unverified.
    /// </summary>
    /// <param name="version">Release tag, e.g. <c>0.8.9</c>.</param>
    /// <param name="checksumsSigstoreSigned">
    /// Whether the release also carries checksums.txt.sig/.pem — the caller already knows this
    /// from <see cref="ProvenanceInspector"/>, and it is recorded on the staged record so the UI
    /// can say what the hash comparison is actually anchored to.
    /// </param>
    public async Task<UpgradeStagingResult> DownloadAndVerifyAsync(
        string version,
        bool checksumsSigstoreSigned = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var scriptUrl = AssetUrl(version, ScriptAssetName);
        var checksumsUrl = AssetUrl(version, ChecksumsAssetName);

        var (bytes, downloadError) = await DownloadAsync(scriptUrl, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return downloadError!;
        }

        if (bytes.Length < MinimumPlausibleScriptBytes)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.StubDetected,
                SizeBytes = bytes.Length,
                Summary = $"Aborted: {ScriptAssetName} came back as only {bytes.Length:N0} bytes.",
                ErrorMessage =
                    $"{ScriptAssetName} on release {version} is {bytes.Length:N0} bytes; the real resolver is " +
                    $"around 216 KB. This is the placeholder-stub pattern seen on some releases — a tiny file " +
                    "published under a real asset name, whose hash is still faithfully listed in the correctly " +
                    "signed checksums.txt. Nothing was written to disk. Check the release page before upgrading.",
            };
        }

        if (bytes.Length > MaximumPlausibleScriptBytes)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.DownloadFailed,
                SizeBytes = bytes.Length,
                Summary = $"Aborted: {ScriptAssetName} came back as {bytes.Length:N0} bytes.",
                ErrorMessage =
                    $"{ScriptAssetName} is {bytes.Length:N0} bytes, far larger than any published upgrade " +
                    "script. Nothing was written to disk.",
            };
        }

        var computed = ComputeSha256(bytes);

        var (checksums, checksumsError) = await DownloadAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        if (checksums is null)
        {
            return new UpgradeStagingResult
            {
                Outcome = checksumsError!.Outcome == UpgradeStagingOutcome.RateLimited
                    ? UpgradeStagingOutcome.RateLimited
                    : UpgradeStagingOutcome.ChecksumUnavailable,
                ComputedSha256 = computed,
                SizeBytes = bytes.Length,
                Summary = $"Aborted: {ChecksumsAssetName} could not be read, so the download is unverified.",
                ErrorMessage =
                    $"The script downloaded ({bytes.Length:N0} bytes, SHA-256 {computed}), but {ChecksumsAssetName} " +
                    $"for release {version} could not be read: {checksumsError.ErrorMessage} Nothing was written to " +
                    "disk — an unverified resolver is exactly what this step exists to refuse.",
            };
        }

        var expected = FindChecksumEntry(Encoding.UTF8.GetString(checksums), ScriptAssetName);
        if (expected is null)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.ChecksumUnavailable,
                ComputedSha256 = computed,
                SizeBytes = bytes.Length,
                Summary = $"Aborted: {ChecksumsAssetName} carries no entry for {ScriptAssetName}.",
                ErrorMessage =
                    $"Release {version} publishes {ChecksumsAssetName}, but it has no line for {ScriptAssetName}, " +
                    $"so the downloaded bytes (SHA-256 {computed}) cannot be anchored to anything signed. Nothing " +
                    "was written to disk.",
            };
        }

        if (!string.Equals(expected, computed, StringComparison.OrdinalIgnoreCase))
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.ChecksumMismatch,
                ComputedSha256 = computed,
                ExpectedSha256 = expected,
                SizeBytes = bytes.Length,
                Summary = "Aborted: the download's SHA-256 does not match the release's checksums.txt.",
                ErrorMessage =
                    $"{ScriptAssetName} downloaded from release {version} hashes to {computed}, but " +
                    $"{ChecksumsAssetName} lists {expected}. Nothing was written to disk. Do not run this script; " +
                    "re-check later, and treat a persistent mismatch as a supply-chain problem worth reporting.",
            };
        }

        string filePath;
        try
        {
            var directory = Path.Combine(StagingRoot, SanitizeVersionForPath(version));
            Directory.CreateDirectory(directory);
            filePath = Path.Combine(directory, ScriptAssetName);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.WriteFailed,
                ComputedSha256 = computed,
                ExpectedSha256 = expected,
                SizeBytes = bytes.Length,
                Summary = "The script verified, but could not be written to the staging directory.",
                ErrorMessage = $"Verification passed, but writing to {StagingRoot} failed: {ex.Message}",
            };
        }

        var script = new StagedUpgradeScript
        {
            Version = version,
            FilePath = filePath,
            SourceUrl = scriptUrl.ToString(),
            ChecksumsUrl = checksumsUrl.ToString(),
            SizeBytes = bytes.Length,
            Sha256 = computed,
            ExpectedSha256 = expected,
            ChecksumsSigstoreSigned = checksumsSigstoreSigned,
            StagedAt = DateTimeOffset.UtcNow,
        };

        return new UpgradeStagingResult
        {
            Outcome = UpgradeStagingOutcome.Verified,
            Script = script,
            ComputedSha256 = computed,
            ExpectedSha256 = expected,
            SizeBytes = bytes.Length,
            Summary = $"Verified: SHA-256 matches release {version}'s {ChecksumsAssetName} entry.",
        };
    }

    /// <summary>
    /// Runs a staged, verified resolver through <see cref="CliRunner"/>.
    /// <para>
    /// Re-hashes the file first: staging and running are separate user actions, possibly minutes
    /// apart, and a verification that is not re-checked at launch is a verification of whatever
    /// used to be at that path.
    /// </para>
    /// </summary>
    public async Task<UpgradeRunResult> RunAsync(
        StagedUpgradeScript script,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (IsRunning)
        {
            return new UpgradeRunResult { FailureReason = "An upgrade run is already in flight." };
        }

        if (!File.Exists(script.FilePath))
        {
            return new UpgradeRunResult
            {
                FailureReason = $"The staged script is no longer at {script.FilePath}. Download and verify it again.",
            };
        }

        string actualHash;
        try
        {
            actualHash = await ComputeFileSha256Async(script.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpgradeRunResult { FailureReason = $"The staged script could not be re-read: {ex.Message}" };
        }

        if (!string.Equals(actualHash, script.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new UpgradeRunResult
            {
                FailureReason =
                    $"The staged script changed on disk since it was verified (now {actualHash}, was {script.Sha256}). " +
                    "Nothing was run. Download and verify it again.",
            };
        }

        var argv = BuildArgv(script.FilePath);
        var powershell = ResolvePowerShellPath();
        CliInvocation? captured = null;

        void OnStarted(object? sender, CliInvocation invocation)
        {
            // Another panel can shell out while this is starting, and this fires on the runner's
            // thread — match on the argv this call is about to pass, exactly as the wizard does.
            if (captured is not null || !invocation.Argv.SequenceEqual(argv, StringComparer.Ordinal))
            {
                return;
            }

            captured = invocation;
            InvocationStarted?.Invoke(this, invocation);
        }

        IsRunning = true;
        _cli.InvocationStarted += OnStarted;

        try
        {
            var invocation = await _cli
                .RunExecutableAsync(powershell, argv, stdinSecret: null, cancellationToken)
                .ConfigureAwait(false);

            return new UpgradeRunResult
            {
                Invocation = invocation,
                ExitCode = invocation.ExitCode,
                FailureReason = invocation.FailureReason,
            };
        }
        catch (SecretInArgumentException ex)
        {
            // Defence in depth: nothing here is secret-derived, so this firing means a bug.
            return new UpgradeRunResult { Invocation = captured, FailureReason = ex.Message };
        }
        finally
        {
            _cli.InvocationStarted -= OnStarted;
            IsRunning = false;
        }
    }

    /// <summary>Lowercase hex SHA-256 of a file on disk.</summary>
    public static async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Reads one <c>&lt;sha256&gt;␠␠&lt;filename&gt;</c> entry out of a checksums file. Tolerates
    /// the <c>*</c> binary-mode marker and either slash direction in the name.
    /// </summary>
    internal static string? FindChecksumEntry(string checksumsText, string assetName)
    {
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[^1].TrimStart('*');
            var bare = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

            if (string.Equals(bare, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task<(byte[]? Bytes, UpgradeStagingResult? Error)> DownloadAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                $"{url} could not be reached.",
                $"GitHub was unreachable while fetching {url}: {ex.Message}"));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                $"{url} timed out.",
                $"The request for {url} timed out: {ex.Message}"));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return (null, Failure(UpgradeStagingOutcome.RateLimited,
                    "GitHub rate-limited the download.",
                    $"GitHub returned HTTP {(int)response.StatusCode} for {url}. This is the same " +
                    "unauthenticated rate limit the version check hits (60 requests/hour/IP); try again later."));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                    "That release does not publish this asset.",
                    $"GitHub returned HTTP 404 for {url}. The release exists but does not carry that asset, " +
                    "or the tag is not what this app thinks it is."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                    $"GitHub returned HTTP {(int)response.StatusCode}.",
                    $"GitHub returned HTTP {(int)response.StatusCode} for {url}."));
            }

            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return (bytes, null);
            }
            catch (HttpRequestException ex)
            {
                return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                    "The download was interrupted.",
                    $"Reading {url} failed partway: {ex.Message}"));
            }
        }
    }

    private static UpgradeStagingResult Failure(UpgradeStagingOutcome outcome, string summary, string error) =>
        new() { Outcome = outcome, Summary = summary, ErrorMessage = error };

    private static string? ProbeDirectories(IEnumerable<string> directories, Func<string, bool> fileExists)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var fileName in CosignFileNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), fileName);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with invalid characters; skip it rather than fail the probe.
                    break;
                }

                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where cosign lands when it is installed but this process's PATH predates it: winget's
    /// package and shim directories, plus the persisted user/machine PATH.
    /// </summary>
    private static IEnumerable<string> DefaultOffPathDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetRoot = Path.Combine(localAppData, "Microsoft", "WinGet");

        yield return Path.Combine(wingetRoot, "Links");

        var packages = Path.Combine(wingetRoot, "Packages");
        if (Directory.Exists(packages))
        {
            string[] cosignPackages;
            try
            {
                cosignPackages = Directory.GetDirectories(packages, "Sigstore.Cosign*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cosignPackages = [];
            }

            foreach (var directory in cosignPackages)
            {
                yield return directory;
            }
        }

        foreach (var directory in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
        {
            yield return directory;
        }

        foreach (var directory in SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> SplitPath(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? []
            : raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Keeps a tag like <c>v0.8.9</c> or a malformed one from escaping the staging root.</summary>
    private static string SanitizeVersionForPath(string version)
    {
        var cleaned = new StringBuilder(version.Length);
        foreach (var c in version.Trim())
        {
            cleaned.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        var text = cleaned.ToString().Trim('.');
        return text.Length == 0 ? "unknown" : text;
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
