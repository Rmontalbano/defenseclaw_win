using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>Where the paths of the administrator setup come from: the invoking user's SID, profile and Program Files. Null when they cannot be had.</summary>
internal interface IAdminLayoutSource
{
    AdminLayout? Get();
}

/// <summary>What kind of account the app runs as, for "can this PC do administrator mode at all".</summary>
public enum AdminAccountKind
{
    /// <summary>A split-token administrator running without elevation: the usual case, and the only one where one prompt elevates the same account.</summary>
    SplitTokenAdministrator,

    /// <summary>This app is itself elevated. Works, but there will be no prompt to read.</summary>
    AlreadyElevated,

    /// <summary>A standard user (or no UAC filtering): Windows would ask for another account's password, and the gateway would then run as that account.</summary>
    StandardUser,

    /// <summary>It could not be read.</summary>
    Unknown,
}

internal interface IAdminAccountProbe
{
    AdminAccountKind Read();
}

/// <summary>Whether a running process has an elevated token. Null when Windows would not say.</summary>
internal interface IProcessElevationProbe
{
    bool? IsElevated(int pid);
}

internal enum ElevationResult
{
    /// <summary>The helper ran and ended; <see cref="ElevationOutcome.ExitCode"/> is its.</summary>
    Completed,

    /// <summary>The operator dismissed the Windows prompt. Nothing ran.</summary>
    Cancelled,

    /// <summary>It could not be started or waited for.</summary>
    Failed,
}

/// <summary>One elevated run: the helper file, the one verb, and (for the first run) the hash the operator was shown.</summary>
/// <param name="Executable">The helper's path: its place in Program Files, or the file next to the app for the first run.</param>
/// <param name="Verb">The only argument it is started with.</param>
/// <param name="HoldAndVerifySha256">When set, the file is opened with read-only sharing, hashed and compared with this value before it is started, and kept open until it exits.</param>
internal sealed record ElevationRequest(string Executable, AdminVerb Verb, string? HoldAndVerifySha256 = null);

internal sealed record ElevationOutcome(ElevationResult Result, int? ExitCode, string? Error)
{
    public static ElevationOutcome Completed(int exitCode) => new(ElevationResult.Completed, exitCode, null);

    public static ElevationOutcome Cancelled { get; } = new(ElevationResult.Cancelled, null, null);

    public static ElevationOutcome Failed(string error) => new(ElevationResult.Failed, null, error);
}

/// <summary>The only thing in the app that raises a UAC prompt. Production: <c>ShellExecuteEx</c> with the <c>runas</c> verb. Tests: a fake that records the request.</summary>
internal interface IAdminElevator
{
    Task<ElevationOutcome> RunAsync(ElevationRequest request, CancellationToken cancellationToken);
}

/// <summary>The helper file the app ships next to itself, hashed.</summary>
internal sealed record StagedHelper(string Path, string Sha256);

internal interface IHelperStage
{
    /// <summary>The helper beside the app, or null (with a reason) when it is not there or cannot be read.</summary>
    StagedHelper? Locate(out string? problem);
}

/// <summary>What the release check found about the installed gateway.</summary>
/// <param name="Version">The release it was checked against.</param>
/// <param name="GatewaySha256">The SHA-256 of <c>defenseclaw-gateway.exe</c> inside the release zip.</param>
/// <param name="ZipSha256">The zip's SHA-256, equal to its line in the release's checksums.txt.</param>
/// <param name="SignatureLabel">What was and was not verified about checksums.txt, in the words the Updates window uses.</param>
internal sealed record ReleaseGateway(string Version, string GatewaySha256, string ZipSha256, string SignatureLabel, string SignatureState);

/// <summary>Fetches the release zip for a version and says what is inside it. The only network use of this feature, and never elevated.</summary>
internal interface IReleaseGatewaySource
{
    /// <summary>Returns the release's gateway, or null with a reason (no network, no zip, checksum mismatch, a signature that did not verify).</summary>
    Task<(ReleaseGateway? Release, string? Problem)> GetAsync(string version, CancellationToken cancellationToken);
}

/// <summary>Writes the manifest into the user's own folder.</summary>
internal interface IManifestWriter
{
    void Write(AdminLayout layout, ReleaseManifest manifest);
}
