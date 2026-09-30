using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

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

/// <summary>
/// The two ways this app knows to upgrade a Windows install. They are not interchangeable: which
/// one works is decided by how DefenseClaw was installed, not by preference.
/// </summary>
public enum UpgradeChannel
{
    /// <summary>
    /// The release's <c>DefenseClawSetup-x64.exe</c>, run over the top of the current install with
    /// <c>/quiet /norestart INSTALLSCOPE=user</c>. Live-verified on this machine (0.8.7 → 0.8.10).
    /// The default and the recommendation on native Setup-based installs.
    /// </summary>
    SetupInstaller = 0,

    /// <summary>
    /// The release's <c>defenseclaw-upgrade.ps1</c> resolver. Correct only where the POSIX-style
    /// venv layout it assumes actually exists — see <see cref="UpgradeRunner.DetectResolverLayout"/>.
    /// </summary>
    ResolverScript,
}

/// <summary>
/// What the on-disk layout says about whether the resolver script can work here, and which
/// channel this app therefore recommends. Purely a filesystem probe — nothing is downloaded.
/// </summary>
public sealed record ResolverLayoutStatus
{
    /// <summary>The channel the UI defaults to, given what was found on disk.</summary>
    public required UpgradeChannel RecommendedChannel { get; init; }

    /// <summary>
    /// True only when <c>%USERPROFILE%\.defenseclaw\.venv\Scripts\python.exe</c> is present — the
    /// one path whose absence the resolver fails on.
    /// </summary>
    public required bool ResolverLayoutPresent { get; init; }

    /// <summary>One paragraph, always set, shown under the channel picker.</summary>
    public required string Detail { get; init; }
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
/// One verified upgrade asset — a resolver script or a Setup installer — sitting under
/// <c>%LOCALAPPDATA%\DefenseClaw.App\upgrades\&lt;version&gt;\</c>. Only ever produced after the
/// size sanity check and the checksum comparison both passed.
/// </summary>
public sealed record StagedUpgradeAsset
{
    /// <summary>Which channel staged this, and therefore how <c>RunAsync</c> will launch it.</summary>
    public required UpgradeChannel Channel { get; init; }

    /// <summary>Release-asset file name, e.g. <c>DefenseClawSetup-x64.exe</c>. Also the file name on disk.</summary>
    public required string AssetName { get; init; }

    /// <summary>Release tag the asset came from, e.g. <c>0.8.10</c>.</summary>
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

    /// <summary>
    /// Human size, always with the exact byte count alongside it: the byte count is what the stub
    /// check and the release page are compared on, and a rounded "258 MB" hides a 133-byte file.
    /// Scales to MB because the installer channel stages ~270,000,000-byte files, where a KB
    /// figure is unreadable.
    /// </summary>
    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => string.Create(
            CultureInfo.CurrentCulture, $"{SizeBytes / (1024.0 * 1024.0):0.#} MB ({SizeBytes:N0} bytes)"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{SizeBytes / 1024.0:0.#} KB ({SizeBytes:N0} bytes)"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{SizeBytes:N0} bytes"),
    };
}

/// <summary>
/// Outcome of one <see cref="UpgradeRunner.DownloadAndVerifyAsync"/> or
/// <see cref="UpgradeRunner.DownloadAndVerifyInstallerAsync"/> call.
/// </summary>
public sealed record UpgradeStagingResult
{
    public UpgradeStagingOutcome Outcome { get; init; }

    /// <summary>Non-null exactly when <see cref="Succeeded"/>.</summary>
    public StagedUpgradeAsset? Asset { get; init; }

    /// <summary>One line, always set — safe to show whether this succeeded or failed.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Long-form explanation for the failure InfoBar. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    public string? ComputedSha256 { get; init; }

    public string? ExpectedSha256 { get; init; }

    public long? SizeBytes { get; init; }

    public bool Succeeded => Outcome == UpgradeStagingOutcome.Verified && Asset is not null;
}

/// <summary>How an upgrade run ended, on either channel.</summary>
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
/// Downloads, verifies and runs a Windows upgrade from the <b>target</b> release's own assets,
/// on one of two channels — see <see cref="UpgradeChannel"/>.
/// <para>
/// <b>Two channels, and which one is canonical depends on the install layout.</b>
/// </para>
/// <para>
/// <b>1. <see cref="UpgradeChannel.SetupInstaller"/> — <c>DefenseClawSetup-x64.exe</c>.</b> Run
/// with <c>/quiet /norestart INSTALLSCOPE=user</c> from a non-elevated session, it stops the
/// gateway, installs over the top in user scope, restarts the gateway, and leaves
/// <c>%USERPROFILE%\.defenseclaw</c> (config.yaml, .env, audit.db, inventory.db) intact. All of
/// that was verified live on this machine's 0.8.7 → 0.8.10 upgrade. This is the canonical channel
/// on native Setup-based installs, which put managed Python under
/// <c>%LOCALAPPDATA%\Programs\DefenseClaw\runtime</c>.
/// </para>
/// <para>
/// <b>2. <see cref="UpgradeChannel.ResolverScript"/> — <c>defenseclaw-upgrade.ps1</c>.</b> A
/// ~2900-line manifest-aware resolver that cosign-verifies the signed release contract before
/// touching anything, journals the upgrade in two phases with automatic rollback, and resumes
/// after a crash. It is the canonical channel <i>only</i> where the POSIX-style venv layout it
/// assumes actually exists. On a Setup-based install it does not: run live here (0.8.7 → 0.8.9)
/// it stopped with exactly <c>Upgrade resolver stopped: Managed Python not found at
/// C:\Users\&lt;user&gt;\.defenseclaw\.venv\Scripts\python.exe.</c> 0.8.10's copy of the script is
/// byte-identical to 0.8.9's (SHA-256
/// <c>e830172b08c86a62991d8f3fa916dffd3e9ad90fca809f5e685e38d8b4555c2c</c>), so the bug is still
/// upstream. <see cref="DetectResolverLayout"/> probes for that venv and picks the default.
/// </para>
/// <para>
/// Neither channel uses the installed CLI's own <c>defenseclaw upgrade</c> verb: on 0.8.7 it
/// fails with "no canonical release-managed gateway". This class never invokes it.
/// </para>
/// <para>
/// <b>What is verified here, and what is not.</b> On both channels this class checks two things
/// the downloaded thing cannot check about itself: that it is not one of the 133-byte placeholder
/// stubs some releases shipped (see <see cref="MinimumPlausibleScriptBytes"/> and
/// <see cref="MinimumPlausibleInstallerBytes"/>), and that its SHA-256 matches the entry in the
/// same release's sigstore-signed <c>checksums.txt</c>. Beyond that the two diverge: the
/// resolver's manifest, artifacts and signatures are its own cosign-backed job, which is why
/// <see cref="DetectCosign"/> gates <i>that</i> channel and only that one. The installer channel
/// never shells out to cosign and is not gated on it. The Setup exe is also not Authenticode-signed
/// through 0.8.10 — the window's trust panel reports that; nothing here claims otherwise.
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

    /// <summary>
    /// The Setup installer asset. Same constant as
    /// <see cref="ProvenanceInspector.SetupAssetName"/>, aliased here so the staging code does not
    /// reach across into the trust inspector for a name it launches as a process.
    /// </summary>
    public const string InstallerAssetName = ProvenanceInspector.SetupAssetName;

    public const string ChecksumsAssetName = "checksums.txt";

    /// <summary>
    /// Anything smaller than this is a stub, not a resolver. The real 0.8.9 script is 216,432
    /// bytes; the known-bad placeholders are 133 bytes — and their hashes are faithfully listed
    /// in the correctly signed checksums.txt, so a hash check alone would wave them through.
    /// </summary>
    public const long MinimumPlausibleScriptBytes = 10 * 1024;

    /// <summary>Upper sanity bound; the script is a PowerShell file, not an artifact.</summary>
    public const long MaximumPlausibleScriptBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Stub floor for the installer. The real 0.8.10 Setup exe is 270,013,440 bytes; the
    /// placeholder stubs are 133. The floor exists precisely because the stubs' hashes <i>do</i>
    /// appear in the correctly signed checksums.txt, so hash equality alone waves them through.
    /// </summary>
    public const long MinimumPlausibleInstallerBytes = 50L * 1024 * 1024;

    /// <summary>Upper sanity bound; refused from Content-Length before the body is read at all.</summary>
    public const long MaximumPlausibleInstallerBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The flag that makes the resolver non-interactive. Every other default is left alone.</summary>
    public const string NonInteractiveFlag = "-Yes";

    /// <summary>Read buffer for the ~270 MB installer stream. Never buffered whole in memory.</summary>
    private const int InstallerCopyBufferBytes = 128 * 1024;

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
    /// The exact flags the Setup exe is launched with. These three, in a non-elevated session, are
    /// what was live-verified on this machine's 0.8.7 → 0.8.10 upgrade: <c>/quiet</c> for no UI,
    /// <c>/norestart</c> so the installer never reboots the box on its own, and
    /// <c>INSTALLSCOPE=user</c> so it installs where the current install already lives rather than
    /// asking for elevation. The confirm overlay renders this same list, so the screen cannot
    /// drift from what executes.
    /// </summary>
    public static IReadOnlyList<string> BuildInstallerArgv() => ["/quiet", "/norestart", "INSTALLSCOPE=user"];

    /// <summary>
    /// <c>%USERPROFILE%\.defenseclaw\.venv\Scripts\python.exe</c> — the one path the resolver
    /// script fails on when it is missing.
    /// </summary>
    public static string ResolverVenvPythonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".defenseclaw",
        ".venv",
        "Scripts",
        "python.exe");

    /// <summary>
    /// <c>%LOCALAPPDATA%\Programs\DefenseClaw\runtime</c> — where a native Setup install puts
    /// managed Python instead.
    /// </summary>
    public static string SetupRuntimeDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "DefenseClaw",
        "runtime");

    /// <summary>
    /// Decides which channel this install should default to, by probing for the single path whose
    /// absence the resolver script dies on.
    /// <para>
    /// This is not a guess. Run live on this machine (0.8.7 → 0.8.9), the resolver stopped with
    /// exactly: <c>Upgrade resolver stopped: Managed Python not found at
    /// C:\Users\&lt;user&gt;\.defenseclaw\.venv\Scripts\python.exe.</c> The script assumes a
    /// POSIX-style venv under <c>%USERPROFILE%\.defenseclaw\.venv</c>; native Setup installs put
    /// managed Python under <c>%LOCALAPPDATA%\Programs\DefenseClaw\runtime</c> instead. 0.8.10's
    /// copy of the script is byte-identical to 0.8.9's, so the failure is not something a newer
    /// release has fixed.
    /// </para>
    /// </summary>
    /// <param name="fileExists">Filesystem probe; overridable for tests.</param>
    /// <param name="directoryExists">Directory probe; overridable for tests.</param>
    public static ResolverLayoutStatus DetectResolverLayout(
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        var probeFile = fileExists ?? File.Exists;
        var probeDirectory = directoryExists ?? Directory.Exists;

        var venvPython = ResolverVenvPythonPath();
        if (probeFile(venvPython))
        {
            return new ResolverLayoutStatus
            {
                RecommendedChannel = UpgradeChannel.ResolverScript,
                ResolverLayoutPresent = true,
                Detail =
                    $"The venv layout the resolver script expects is present ({venvPython}), so " +
                    "defenseclaw-upgrade.ps1 can run here — with its cosign-verified release contract, its " +
                    "two-phase journal and its automatic rollback. The Setup installer remains available as " +
                    "the blunter alternative.",
            };
        }

        var runtimeDirectory = SetupRuntimeDirectory();
        var runtimePresent = probeDirectory(runtimeDirectory);

        return new ResolverLayoutStatus
        {
            RecommendedChannel = UpgradeChannel.SetupInstaller,
            ResolverLayoutPresent = false,
            Detail =
                $"{venvPython} does not exist on this machine. Run live here (0.8.7 → 0.8.9), the resolver " +
                "script stopped with exactly: \"Upgrade resolver stopped: Managed Python not found at " +
                $"{venvPython}.\" It assumes a POSIX-style venv layout this install does not have" +
                (runtimePresent
                    ? $" — the managed runtime is under {runtimeDirectory}, which is where a native Setup " +
                      "install puts it."
                    : $"; native Setup installs put the managed runtime under {runtimeDirectory} instead.") +
                " 0.8.10's defenseclaw-upgrade.ps1 is byte-identical to 0.8.9's, so a newer release does not " +
                "fix it. On this layout the Setup exe, run over the top, is the channel that works.",
        };
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

    /// <summary>Display form of the resolver command line. For humans; the runner never re-parses it.</summary>
    public static string DescribeCommand(string scriptPath) =>
        DescribeCommand(UpgradeChannel.ResolverScript, scriptPath);

    /// <summary>
    /// Display form of the full command line for either channel. For humans; the runner never
    /// re-parses it. Built from the same <c>BuildArgv</c>/<c>BuildInstallerArgv</c> the run uses,
    /// so the confirm overlay cannot drift from what executes.
    /// </summary>
    public static string DescribeCommand(UpgradeChannel channel, string stagedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);

        // The installer is its own executable — there is no interpreter in front of it. The path
        // is quoted unconditionally because it sits under a user profile, which routinely has a
        // space in it.
        return channel == UpgradeChannel.SetupInstaller
            ? $"\"{stagedPath}\" {string.Join(' ', BuildInstallerArgv())}"
            : string.Join(' ', new[] { ResolvePowerShellPath() }.Concat(BuildArgv(stagedPath)).Select(Quote));
    }

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
    /// <see cref="DetectCosign"/> on the thread pool. The probe walks every PATH entry, and one dead network entry
    /// makes a single <c>File.Exists</c> take ~40 s, so anything on the UI thread awaits this instead of calling
    /// the synchronous form.
    /// </summary>
    public static Task<CosignStatus> DetectCosignAsync(
        IEnumerable<string>? processPath = null,
        IEnumerable<string>? offPathDirectories = null,
        Func<string, bool>? fileExists = null) =>
        Task.Run(() => DetectCosign(processPath, offPathDirectories, fileExists));

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

        PruneStaging(version);

        var script = new StagedUpgradeAsset
        {
            Channel = UpgradeChannel.ResolverScript,
            AssetName = ScriptAssetName,
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
            Asset = script,
            ComputedSha256 = computed,
            ExpectedSha256 = expected,
            SizeBytes = bytes.Length,
            Summary = $"Verified: SHA-256 matches release {version}'s {ChecksumsAssetName} entry.",
        };
    }

    /// <summary>
    /// Fetches the target release's Setup installer, verifies its SHA-256 against the same
    /// release's checksums.txt, and only then promotes it into <see cref="StagingRoot"/>.
    /// <para>
    /// <b>Order matters.</b> checksums.txt is fetched <i>first</i>. It is a few kilobytes, and if
    /// GitHub is rate-limiting or the release simply has no entry for the installer, that is
    /// discovered in one cheap request rather than after a ~270 MB download that can then only be
    /// thrown away.
    /// </para>
    /// <para>
    /// <b>Nothing is buffered whole.</b> The body streams to
    /// <c>&lt;asset&gt;.partial</c> through a <see cref="IncrementalHash"/>, so the hash is
    /// computed in the same pass and the ~270 MB never lands in memory. (The byte[] path used by
    /// <see cref="DownloadAndVerifyAsync"/> is fine there — that asset is ~216 KB.) The partial
    /// file is deleted on every failure path and on exceptions; only a full size-and-hash match
    /// promotes it to its final name.
    /// </para>
    /// </summary>
    /// <param name="version">Release tag, e.g. <c>0.8.10</c>.</param>
    /// <param name="checksumsSigstoreSigned">
    /// Whether the release also carries checksums.txt.sig/.pem, recorded on the staged record so
    /// the UI can say what the hash comparison is anchored to.
    /// </param>
    /// <param name="progress">
    /// Bytes read so far and the Content-Length when the server sent one. Raised on the download
    /// worker thread — marshal before touching UI state.
    /// </param>
    public async Task<UpgradeStagingResult> DownloadAndVerifyInstallerAsync(
        string version,
        bool checksumsSigstoreSigned = false,
        IProgress<(long BytesRead, long? TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var installerUrl = AssetUrl(version, InstallerAssetName);
        var checksumsUrl = AssetUrl(version, ChecksumsAssetName);

        var (checksums, checksumsError) = await DownloadAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        if (checksums is null)
        {
            return new UpgradeStagingResult
            {
                Outcome = checksumsError!.Outcome == UpgradeStagingOutcome.RateLimited
                    ? UpgradeStagingOutcome.RateLimited
                    : UpgradeStagingOutcome.ChecksumUnavailable,
                Summary = $"Aborted before downloading: {ChecksumsAssetName} could not be read.",
                ErrorMessage =
                    $"{ChecksumsAssetName} for release {version} could not be read: {checksumsError.ErrorMessage} " +
                    "The installer was not downloaded — without the checksum entry there would be nothing to " +
                    "verify the download against.",
            };
        }

        var expected = FindChecksumEntry(Encoding.UTF8.GetString(checksums), InstallerAssetName);
        if (expected is null)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.ChecksumUnavailable,
                Summary = $"Aborted before downloading: {ChecksumsAssetName} carries no entry for {InstallerAssetName}.",
                ErrorMessage =
                    $"Release {version} publishes {ChecksumsAssetName}, but it has no line for " +
                    $"{InstallerAssetName}, so a download could not be anchored to anything signed. Nothing was " +
                    "downloaded.",
            };
        }

        string partialPath;
        string filePath;
        try
        {
            var directory = Path.Combine(StagingRoot, SanitizeVersionForPath(version));
            Directory.CreateDirectory(directory);
            filePath = Path.Combine(directory, InstallerAssetName);
            partialPath = filePath + ".partial";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.WriteFailed,
                ExpectedSha256 = expected,
                Summary = "The staging directory could not be created, so nothing was downloaded.",
                ErrorMessage = $"Creating a staging directory under {StagingRoot} failed: {ex.Message}",
            };
        }

        var promoted = false;
        try
        {
            var (response, downloadError) = await SendAsync(
                installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return downloadError!;
            }

            long totalRead;
            string computed;

            using (response)
            {
                // Refuse an absurd Content-Length before a single byte of body is read. The size
                // is enforced again during the copy, because a server is free to send more than
                // it advertised — or to advertise nothing at all.
                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > MaximumPlausibleInstallerBytes)
                {
                    return new UpgradeStagingResult
                    {
                        Outcome = UpgradeStagingOutcome.DownloadFailed,
                        ExpectedSha256 = expected,
                        SizeBytes = declaredLength,
                        Summary = $"Aborted: {InstallerAssetName} advertises {declaredLength:N0} bytes.",
                        ErrorMessage =
                            $"{InstallerAssetName} on release {version} advertises {declaredLength:N0} bytes, past " +
                            $"the {MaximumPlausibleInstallerBytes:N0}-byte sanity ceiling. The body was never read " +
                            "and nothing was written to disk.",
                    };
                }

                try
                {
                    (totalRead, computed) = await StreamToPartialAsync(
                        response, partialPath, declaredLength, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (InstallerTooLargeException ex)
                {
                    return new UpgradeStagingResult
                    {
                        Outcome = UpgradeStagingOutcome.DownloadFailed,
                        ExpectedSha256 = expected,
                        SizeBytes = ex.BytesRead,
                        Summary = $"Aborted: {InstallerAssetName} exceeded the size ceiling mid-download.",
                        ErrorMessage =
                            $"{InstallerAssetName} on release {version} passed {MaximumPlausibleInstallerBytes:N0} " +
                            "bytes while streaming, so the download was cut off. The partial file was deleted.",
                    };
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    return new UpgradeStagingResult
                    {
                        Outcome = UpgradeStagingOutcome.DownloadFailed,
                        ExpectedSha256 = expected,
                        Summary = "The download was interrupted.",
                        ErrorMessage = $"Reading {installerUrl} failed partway: {ex.Message} The partial file was deleted.",
                    };
                }
                catch (UnauthorizedAccessException ex)
                {
                    return new UpgradeStagingResult
                    {
                        Outcome = UpgradeStagingOutcome.WriteFailed,
                        ExpectedSha256 = expected,
                        Summary = "The download could not be written to the staging directory.",
                        ErrorMessage = $"Writing to {partialPath} failed: {ex.Message}",
                    };
                }
            }

            if (totalRead < MinimumPlausibleInstallerBytes)
            {
                return new UpgradeStagingResult
                {
                    Outcome = UpgradeStagingOutcome.StubDetected,
                    ComputedSha256 = computed,
                    ExpectedSha256 = expected,
                    SizeBytes = totalRead,
                    Summary = $"Aborted: {InstallerAssetName} came back as only {totalRead:N0} bytes.",
                    ErrorMessage =
                        $"{InstallerAssetName} on release {version} is {totalRead:N0} bytes; the real 0.8.10 " +
                        "installer is 270,013,440 bytes. This is the placeholder-stub pattern seen on some " +
                        "releases — the known-bad stubs are 133 bytes, and their hashes are still faithfully " +
                        $"listed in the correctly signed {ChecksumsAssetName}, so a hash check alone would wave " +
                        "them through. The partial download was deleted. Check the release page before upgrading.",
                };
            }

            if (!string.Equals(expected, computed, StringComparison.OrdinalIgnoreCase))
            {
                return new UpgradeStagingResult
                {
                    Outcome = UpgradeStagingOutcome.ChecksumMismatch,
                    ComputedSha256 = computed,
                    ExpectedSha256 = expected,
                    SizeBytes = totalRead,
                    Summary = "Aborted: the download's SHA-256 does not match the release's checksums.txt.",
                    ErrorMessage =
                        $"{InstallerAssetName} downloaded from release {version} hashes to {computed}, but " +
                        $"{ChecksumsAssetName} lists {expected}. The partial download was deleted. Do not run this " +
                        "installer; re-check later, and treat a persistent mismatch as a supply-chain problem " +
                        "worth reporting.",
                };
            }

            try
            {
                File.Move(partialPath, filePath, overwrite: true);
                promoted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new UpgradeStagingResult
                {
                    Outcome = UpgradeStagingOutcome.WriteFailed,
                    ComputedSha256 = computed,
                    ExpectedSha256 = expected,
                    SizeBytes = totalRead,
                    Summary = "The installer verified, but could not be moved into place.",
                    ErrorMessage = $"Verification passed, but renaming {partialPath} to {filePath} failed: {ex.Message}",
                };
            }

            PruneStaging(version);

            var asset = new StagedUpgradeAsset
            {
                Channel = UpgradeChannel.SetupInstaller,
                AssetName = InstallerAssetName,
                Version = version,
                FilePath = filePath,
                SourceUrl = installerUrl.ToString(),
                ChecksumsUrl = checksumsUrl.ToString(),
                SizeBytes = totalRead,
                Sha256 = computed,
                ExpectedSha256 = expected,
                ChecksumsSigstoreSigned = checksumsSigstoreSigned,
                StagedAt = DateTimeOffset.UtcNow,
            };

            return new UpgradeStagingResult
            {
                Outcome = UpgradeStagingOutcome.Verified,
                Asset = asset,
                ComputedSha256 = computed,
                ExpectedSha256 = expected,
                SizeBytes = totalRead,
                Summary = $"Verified: SHA-256 matches release {version}'s {ChecksumsAssetName} entry.",
            };
        }
        finally
        {
            // Every path that is not a promotion — refusal, mismatch, write error, cancellation,
            // an exception nobody here catches — leaves the partial behind otherwise.
            if (!promoted)
            {
                TryDelete(partialPath);
            }
        }
    }

    /// <summary>
    /// Streams a response body to <paramref name="partialPath"/> while hashing it in the same
    /// pass. Returns the byte count and the lowercase hex SHA-256.
    /// </summary>
    private static async Task<(long TotalRead, string Sha256)> StreamToPartialAsync(
        HttpResponseMessage response,
        string partialPath,
        long? declaredLength,
        IProgress<(long BytesRead, long? TotalBytes)>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[InstallerCopyBufferBytes];
        long totalRead = 0;

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var destination = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            InstallerCopyBufferBytes,
            useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
                if (totalRead > MaximumPlausibleInstallerBytes)
                {
                    throw new InstallerTooLargeException(totalRead);
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((totalRead, declaredLength));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return (totalRead, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// Deletes every other version's directory under <see cref="StagingRoot"/>, keeping <paramref name="keepVersion"/>'s.
    /// Each Setup installer is ~270 MB and nothing else ever removed one, so the folder grew by that much per
    /// upgrade. Called once a download has verified and been promoted, and again after a successful run.
    /// <para>
    /// Contained: only direct child directories of the staging root whose names are the kind
    /// <see cref="SanitizeVersionForPath"/> produces are candidates, a directory that is a link (junction or
    /// symlink) is skipped rather than followed, and a root that is a drive root is refused — nothing outside
    /// the root can be reached by a crafted name or link. Best effort: a file in use (an installer still
    /// running) or a permission error leaves that directory for next time, and nothing here throws.
    /// </para>
    /// </summary>
    /// <returns>How many version directories were removed.</returns>
    public int PruneStaging(string keepVersion)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StagingRoot));
            if (Path.GetPathRoot(root) is { } driveRoot && string.Equals(
                    Path.TrimEndingDirectorySeparator(driveRoot), root, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!Directory.Exists(root))
            {
                return 0;
            }

            var keep = SanitizeVersionForPath(keepVersion);
            var removed = 0;

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var full = Path.GetFullPath(directory);
                var name = Path.GetFileName(full);

                if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(SanitizeVersionForPath(name), name, StringComparison.Ordinal) ||
                    new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(full, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or not ours to delete: leave it; the next prune tries again.
                }
            }

            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The partial is inert — it is never launched, and the next attempt overwrites it.
        }
    }

    /// <summary>
    /// Runs a staged, verified asset through <see cref="CliRunner"/> — the resolver via
    /// <c>powershell.exe</c>, or the Setup exe as its own executable.
    /// <para>
    /// Re-hashes the file first, on <b>both</b> channels: staging and running are separate user
    /// actions, possibly minutes apart, and a verification that is not re-checked at launch is a
    /// verification of whatever used to be at that path. Re-hashing 270 MB costs roughly a second
    /// — nothing next to the 270 MB download that produced it.
    /// </para>
    /// </summary>
    public async Task<UpgradeRunResult> RunAsync(
        StagedUpgradeAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (IsRunning)
        {
            return new UpgradeRunResult { FailureReason = "An upgrade run is already in flight." };
        }

        var noun = asset.Channel == UpgradeChannel.SetupInstaller ? "installer" : "script";

        if (!File.Exists(asset.FilePath))
        {
            return new UpgradeRunResult
            {
                FailureReason =
                    $"The staged {noun} is no longer at {asset.FilePath}. Download and verify it again.",
            };
        }

        string actualHash;
        try
        {
            actualHash = await ComputeFileSha256Async(asset.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpgradeRunResult { FailureReason = $"The staged {noun} could not be re-read: {ex.Message}" };
        }

        if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new UpgradeRunResult
            {
                FailureReason =
                    $"The staged {noun} changed on disk since it was verified (now {actualHash}, was {asset.Sha256}). " +
                    "Nothing was run. Download and verify it again.",
            };
        }

        // The installer is launched directly; the resolver needs an interpreter in front of it.
        var (executable, argv) = asset.Channel == UpgradeChannel.SetupInstaller
            ? (asset.FilePath, BuildInstallerArgv())
            : (ResolvePowerShellPath(), BuildArgv(asset.FilePath));

        CliInvocation? captured = null;

        void OnStarted(object? sender, CliInvocation invocation)
        {
            // Another panel can shell out while this is starting, and this fires on the runner's
            // thread. The resolver's argv carries the staged path and is distinctive on its own,
            // but the installer's is three generic flags — so match on executable *and* argv,
            // which together are unique to this call on either channel.
            if (captured is not null ||
                !string.Equals(invocation.Executable, executable, StringComparison.OrdinalIgnoreCase) ||
                !invocation.Argv.SequenceEqual(argv, StringComparer.Ordinal))
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
            // Opted out of both of CliRunner's process-lifecycle guards, on both channels:
            //  - No timeout. The 270 MB Setup installer and the ~2900-line resolver
            //    legitimately run for minutes with almost no output, and a timeout here would
            //    be a kill signal aimed at an installer that is midway through replacing this
            //    app's own binaries.
            //  - Survives shutdown. Exiting the tray must not kill msiexec/Setup mid-flight;
            //    a half-applied install is strictly worse than one that finishes after the app
            //    has gone. The run simply carries on unsupervised.
            // The caller's token is still honoured — that is an explicit act, not an accident of
            // the app exiting — but UpgradeSectionViewModel passes CancellationToken.None here, so
            // neither closing the Updates window nor disposing its view-model can end the run. Both
            // used to cancel that token, and it was the only remaining way to kill an install midway.
            var invocation = await _cli
                .RunExecutableAsync(executable, argv, stdinSecret: null, cancellationToken, CliRunOptions.Installer)
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

    /// <summary>
    /// Issues the GET and turns every non-success shape into the same
    /// <see cref="UpgradeStagingResult"/> vocabulary both channels report in. On success the
    /// caller owns the response and must dispose it — the installer channel needs the body as a
    /// stream, so this cannot close it.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, UpgradeStagingResult? Error)> SendAsync(
        Uri url,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
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

        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            response.Dispose();
            return (null, Failure(UpgradeStagingOutcome.RateLimited,
                "GitHub rate-limited the download.",
                $"GitHub returned HTTP {(int)response.StatusCode} for {url}. This is the same " +
                "unauthenticated rate limit the version check hits (60 requests/hour/IP); try again later."));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                "That release does not publish this asset.",
                $"GitHub returned HTTP 404 for {url}. The release exists but does not carry that asset, " +
                "or the tag is not what this app thinks it is."));
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            return (null, Failure(UpgradeStagingOutcome.DownloadFailed,
                $"GitHub returned HTTP {statusCode}.",
                $"GitHub returned HTTP {statusCode} for {url}."));
        }

        return (response, null);
    }

    /// <summary>Buffers a small asset whole. Never used for the installer — see <see cref="StreamToPartialAsync"/>.</summary>
    private async Task<(byte[]? Bytes, UpgradeStagingResult? Error)> DownloadAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var (response, error) = await SendAsync(
            url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return (null, error);
        }

        using (response)
        {
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return (bytes, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
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
                    candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
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

    // The shared splitter also strips the quotes around an entry, which the shell does and a bare Split does not.
    private static IEnumerable<string> SplitPath(string? raw) => DefenseClawPaths.SplitPathList(raw);

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

    /// <summary>
    /// Cuts a runaway installer download short from inside the copy loop. Private and never
    /// surfaced: <see cref="DownloadAndVerifyInstallerAsync"/> turns it into an ordinary
    /// <see cref="UpgradeStagingOutcome.DownloadFailed"/> result.
    /// </summary>
    private sealed class InstallerTooLargeException : Exception
    {
        public InstallerTooLargeException(long bytesRead)
            : base($"The download passed the {MaximumPlausibleInstallerBytes:N0}-byte ceiling at {bytesRead:N0} bytes.")
        {
            BytesRead = bytesRead;
        }

        public long BytesRead { get; }
    }
}
