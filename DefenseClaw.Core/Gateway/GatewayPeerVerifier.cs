using System.Diagnostics;
using System.Net;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Gateway;

/// <summary>
/// Whether a gateway image that is not in the installer's directories may still be sent the token: the administrator copy of the gateway
/// that the "Run gateway as administrator" helper keeps in Program Files. The app answers it only while that file sits in an admin-only
/// folder, is the file the helper recorded and the mode is on. See <c>docs/PARITY-FOUNDATIONS.md</c>, "Run gateway as administrator".
/// </summary>
public interface IAdminCopyTrust
{
    /// <summary>True when <paramref name="imagePath"/> is the trusted administrator copy, judged now.</summary>
    bool IsTrustedImage(string imagePath);

    /// <summary>
    /// Why <paramref name="owner"/> is not the trusted administrator copy, as the rest of a sentence that starts with the process's name and pid
    /// (for example "runs from this account's administrator copy, but that copy did not pass its checks: ..."). Null when there is nothing
    /// specific to say, and the verifier's own words are used.
    /// </summary>
    string? ExplainRefusal(PortOwner owner) => null;
}

/// <summary>
/// Whether one of the installer's directories that lies outside this account's profile is closed to other accounts (CUST-392). The profile is
/// this account's alone, but a folder elsewhere (a <c>DEFENSECLAW_HOME</c> on <c>C:\</c>, whose new folders give Authenticated Users Modify)
/// may be one another account could have put a <c>defenseclaw-gateway.exe</c> in. The app answers it from the owner and access list of the file
/// and every folder above it (<c>InstallFolderTrust</c>). Fails closed: anything unreadable is a problem.
/// </summary>
public interface IInstallFolderTrust
{
    /// <summary>
    /// Null when <paramref name="imagePath"/> and every folder above it are owned by this account, SYSTEM or Administrators and no other principal
    /// may write, delete, re-permission or take them; otherwise the first thing wrong, as the rest of a sentence (for example
    /// "D:\dc\.venv\Scripts lets S-1-5-11 write to it, change it or delete it").
    /// </summary>
    string? Problem(string imagePath);
}

/// <summary>
/// How a listener that <see cref="GatewayPeerVerifier"/> trusts was recognised, for the words the app shows. The token decision is
/// <see cref="PortOwnerTrust"/>'s alone; this only says which of the trusted gateways it is.
/// </summary>
public enum GatewayPeerRoute
{
    /// <summary>Not a trusted gateway (or not a gateway at all).</summary>
    None = 0,

    /// <summary>The gateway from the installer's directories.</summary>
    InstallDirectory,

    /// <summary>The administrator copy this app's helper keeps in Program Files, recognised by the file the process runs, in this app's session.</summary>
    AdministratorCopy,
}

/// <summary>A listener's verdict and how it was reached.</summary>
/// <param name="Trust">Whether it may be sent the token.</param>
/// <param name="Route">Which trusted gateway it is; <see cref="GatewayPeerRoute.None"/> unless <paramref name="Trust"/> is <see cref="PortOwnerTrust.Gateway"/>.</param>
public readonly record struct PeerVerdict(PortOwnerTrust Trust, GatewayPeerRoute Route);

/// <summary>What the process holding the API port is, as far as sending it a credential goes.</summary>
public enum PortOwnerTrust
{
    /// <summary>No owner was found (nothing listening, or the lookup failed). Nothing to trust.</summary>
    Unknown = 0,

    /// <summary>
    /// <c>defenseclaw-gateway</c>, running from the install directory: the one process the
    /// bearer token may be sent to.
    /// </summary>
    Gateway,

    /// <summary>
    /// A WSL port relay. A gateway inside WSL answers <c>/health</c> through it, but it is not
    /// the native install and does not share its token: health only, never a credential.
    /// </summary>
    WslRelay,

    /// <summary>Anything else: another server on the port, or a gateway from outside the install directory.</summary>
    Other,
}

/// <summary>
/// Decides whether the process answering on the API port may be sent the gateway's bearer token.
/// <para>
/// <b>The threat.</b> The API port is a loopback TCP port, and whoever binds it first answers.
/// A dev server, another user's process on a shared machine, or a stale relay would receive
/// <c>Authorization: Bearer …</c> on every <c>/alerts</c> and <c>/status</c> poll — and a 404 on
/// <c>/health</c> used to count as "the gateway is running". So the token goes out only when the
/// port's owner is <c>defenseclaw-gateway</c> <i>and</i> the executable it runs is in the
/// installer's bin directory (never a directory PATH happens to name): a name alone is
/// anyone's to pick. The authority is <see cref="ForConnection"/>, which judges the owner of the very connection the token rides on;
/// <see cref="ForPort"/> is the cheap listener pre-check ahead of it. The caller adds the second half, that <c>/health</c> parsed as a
/// <see cref="Models.GatewayHealth"/> — <see cref="GatewayClient"/> does.
/// </para>
/// <para>
/// <b>Which file, and whose session (CUST-362).</b> The path is read with a handle when Windows grants one, and otherwise from the kernel's
/// process list (<see cref="ProcessImagePath"/>), which names the image of an elevated gateway as well. Without a handle nothing says which
/// account runs the process, so a path read that way counts only for a process in this app's Windows session. The administrator copy needs the
/// same session either way: it sits in Program Files, where every account may run it, so its path alone does not say it is the one the helper
/// started for this account (an elevated start keeps the session). No record of the helper's or the gateway's (<c>last-result.json</c>,
/// <c>gateway.pid</c>) ever stands in for the path.
/// </para>
/// <para>
/// <b>Whose socket, and whose folder (CUST-392).</b> Windows keeps naming a listening socket by its creator's pid after the creator exits while a
/// child it handed the socket to serves it, and a later process may reuse that pid: another account could bind the port, hand it on, exit, and
/// have this account's gateway (started, and failing to bind) answer the pid lookups. So the owner's process must be no younger than its socket
/// (<see cref="PortOwner.PidMatchesSocket"/>), and an owner whose times Windows did not give is refused. And an installer's directory outside this
/// account's profile counts only for a process in this app's session or when <see cref="IInstallFolderTrust"/> finds the file and every folder
/// above it closed to other accounts; the profile itself is this account's alone and keeps the old rule.
/// </para>
/// <para>
/// <b>Not closed.</b> A session is not an account: a process another account starts inside this app's session (<c>runas</c>, with that account's
/// password) and whose path is read without a handle is judged by its path and session; Windows gives its owner only to a handle.
/// </para>
/// <para>
/// Fails closed: an owner whose image path could not be read either way is <see cref="PortOwnerTrust.Other"/>.
/// </para>
/// </summary>
public sealed class GatewayPeerVerifier
{
    /// <summary>Image name (without extension) of the sidecar.</summary>
    public const string GatewayProcessName = "defenseclaw-gateway";

    private readonly DefenseClawPaths _paths;
    private readonly IPortOwnerInspector _inspector;

    private readonly IConnectionOwnerInspector? _connections;
    private readonly IAdminCopyTrust? _adminCopy;
    private readonly IInstallFolderTrust? _installFolders;
    private readonly string? _profile;
    private readonly int? _sessionId;

    /// <param name="adminCopy">
    /// Also trusts the administrator copy of the gateway while it is judged trusted (see <see cref="IAdminCopyTrust"/>); null (the default) trusts
    /// only the installer's directories, exactly as before.
    /// </param>
    /// <param name="connections">
    /// Where the owner of one connection is looked up; defaults to <paramref name="inspector"/> when that is one too (the real
    /// <see cref="PortOwnerInspector"/> is). With none, <see cref="ForConnection"/> refuses everything.
    /// </param>
    /// <param name="currentSessionId">This app's Windows session; null reads this process's own (a test names one).</param>
    /// <param name="installFolders">
    /// Judges an installer's directory outside <paramref name="userProfile"/> for a gateway outside this app's session (CUST-392); null (the default)
    /// trusts such a directory only in this app's session.
    /// </param>
    /// <param name="userProfile">This account's profile folder; null reads it from the system (a test names one). An unreadable profile is no profile.</param>
    public GatewayPeerVerifier(
        DefenseClawPaths paths,
        IPortOwnerInspector inspector,
        IConnectionOwnerInspector? connections = null,
        IAdminCopyTrust? adminCopy = null,
        int? currentSessionId = null,
        IInstallFolderTrust? installFolders = null,
        string? userProfile = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _connections = connections ?? inspector as IConnectionOwnerInspector;
        _adminCopy = adminCopy;
        _installFolders = installFolders;
        _profile = NormalizeDirectory(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _sessionId = currentSessionId ?? OwnSession();
    }

    private static int? OwnSession()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Classifies <paramref name="owner"/>.</summary>
    public PortOwnerTrust Classify(PortOwner? owner) => Judge(owner).Trust;

    /// <summary>Classifies <paramref name="owner"/> and says which trusted gateway it is, for the words shown about it.</summary>
    public PeerVerdict Judge(PortOwner? owner)
    {
        if (owner is null)
        {
            return new PeerVerdict(PortOwnerTrust.Unknown, GatewayPeerRoute.None);
        }

        if (owner.IsWslRelay)
        {
            return new PeerVerdict(PortOwnerTrust.WslRelay, GatewayPeerRoute.None);
        }

        var route = GatewayRoute(owner);
        return route == GatewayPeerRoute.None
            ? new PeerVerdict(PortOwnerTrust.Other, GatewayPeerRoute.None)
            : new PeerVerdict(PortOwnerTrust.Gateway, route);
    }

    /// <summary>True when <paramref name="owner"/> is the native gateway: from the install directory, or this app's verified administrator copy.</summary>
    public bool IsTrustedOwner(PortOwner? owner) => Classify(owner) == PortOwnerTrust.Gateway;

    /// <summary>
    /// A delegate for <see cref="GatewayClient"/>'s peer check on <paramref name="port"/>: looks
    /// the owner up afresh on every call, so a listener swapped after the last poll is caught
    /// on the next authenticated request rather than believed.
    /// </summary>
    public Func<PortOwnerTrust> ForPort(int port)
    {
        // Developer runtime selector, container mode: the port is Docker's published loopback port, so its owner is Docker's relay and
        // never the install directory's gateway. The operator named this exact port as the container's gateway (and the token is the
        // container's own, read from the data copy they chose), so that port, and only that port, is trusted.
        if (_paths.Runtime.Kind == RuntimeKind.Container && _paths.Runtime.TryGetGatewayPort(out var selected) && selected == port)
        {
            return () => PortOwnerTrust.Gateway;
        }

        return () => ClassifyAll(_inspector.FindListeners(port));
    }

    /// <summary>
    /// Several sockets can listen on one wildcard-bound port (SO_REUSEADDR), so the verdict is the worst of all of them: nothing
    /// listening is <see cref="PortOwnerTrust.Unknown"/>, and the port is the gateway's only when every row on it is.
    /// </summary>
    public PortOwnerTrust ClassifyAll(IReadOnlyList<PortOwner> owners)
    {
        if (owners.Count == 0)
        {
            return PortOwnerTrust.Unknown;
        }

        var verdicts = owners.Select(Classify).ToArray();
        if (verdicts.Contains(PortOwnerTrust.Other) || verdicts.Contains(PortOwnerTrust.Unknown))
        {
            return PortOwnerTrust.Other;
        }

        return verdicts.Contains(PortOwnerTrust.WslRelay) ? PortOwnerTrust.WslRelay : PortOwnerTrust.Gateway;
    }

    /// <summary>How many times a connection's table row is looked for, and the pause between looks: the row of a connection that has just completed can lag it by a moment.</summary>
    public const int ConnectionLookupAttempts = 4;

    public static readonly TimeSpan ConnectionLookupPause = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The per-connection check for <see cref="GatewayClient"/>'s connect callback: given the two ends of a connection that was just made
    /// to <paramref name="port"/>, finds the TCP-table row for <i>that</i> connection and classifies the process that owns the far end.
    /// Unlike <see cref="ForPort"/> there is no gap between the lookup and the connection it is about: a listener swapped in after the
    /// last poll is the owner of its own connection and is judged as such. No row, or no way to read the table, is
    /// <see cref="PortOwnerTrust.Unknown"/> - the caller refuses it too (fail closed).
    /// </summary>
    public Func<IPEndPoint, IPEndPoint, PortOwnerTrust> ForConnection(int port)
    {
        // Container mode (see ForPort): the published port is Docker's relay, trusted by the operator's explicit choice.
        if (_paths.Runtime.Kind == RuntimeKind.Container && _paths.Runtime.TryGetGatewayPort(out var selected) && selected == port)
        {
            return (_, _) => PortOwnerTrust.Gateway;
        }

        return (server, client) =>
        {
            if (_connections is null)
            {
                return PortOwnerTrust.Unknown;
            }

            for (var attempt = 0; attempt < ConnectionLookupAttempts; attempt++)
            {
                if (_connections.FindConnectionOwner(server, client) is { } owner)
                {
                    return Classify(owner);
                }

                if (attempt + 1 < ConnectionLookupAttempts)
                {
                    Thread.Sleep(ConnectionLookupPause);
                }
            }

            return PortOwnerTrust.Unknown;
        };
    }

    /// <summary>One-line reason a listener is not trusted, for the banner and the alerts note.</summary>
    public string DescribeUntrusted(PortOwner? owner)
    {
        if (owner is null)
        {
            return "the process on the API port could not be identified";
        }

        var who = $"{owner.ProcessName ?? "an unidentified process"} (pid {owner.Pid})";
        if (owner.IsWslRelay)
        {
            return $"{who} is a WSL relay, not the native gateway";
        }

        if (!string.Equals(owner.ProcessName, GatewayProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{who} is not the DefenseClaw gateway";
        }

        if (string.IsNullOrWhiteSpace(owner.ImagePath))
        {
            return $"{who} could not be verified: Windows does not show this app which file it runs from";
        }

        if (owner.ProcessCreatedUtc is null || owner.SocketCreatedUtc is null)
        {
            return $"{who} could not be verified: Windows did not say when the process started or when its socket on the API port was opened";
        }

        if (!owner.PidMatchesSocket)
        {
            return $"{who} did not open the socket on the API port: the socket is older than the process, so another process opened it with that pid";
        }

        if (!InAppSession(owner))
        {
            if (owner.ImageWithoutHandle)
            {
                return $"{who} runs in {SessionWords(owner)}, and Windows would not let this app open it to see which account runs it";
            }

            if (_adminCopy?.IsTrustedImage(owner.ImagePath) == true)
            {
                return $"{who} runs this account's administrator copy, but in {SessionWords(owner)}, so it is not the gateway the helper started";
            }

            if (InstallDirectoryOf(owner.ImagePath) is { } directory && !InProfile(directory) && FolderProblem(owner.ImagePath) is { } problem)
            {
                return $"{who} runs from {directory}, outside this account's profile, in {SessionWords(owner)}, and {problem}";
            }
        }

        // The administrator trust knows the cases worth naming precisely (its own copy failing its checks, another account's copy); everything
        // else keeps the plain sentence.
        if (_adminCopy?.ExplainRefusal(owner) is { Length: > 0 } why)
        {
            return $"{who} {why}";
        }

        return $"{who} is not running from the DefenseClaw install directory";
    }

    /// <summary>
    /// True when <paramref name="owner"/> runs in this app's Windows session (both known and equal): the session rule the verdict applies, also for
    /// the administrator feature's words about which gateway is the helper's (CUST-392).
    /// </summary>
    public bool InAppSession(PortOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.SessionId is { } session && _sessionId is { } mine && session == mine;
    }

    private string SessionWords(PortOwner owner) =>
        owner.SessionId is { } session && _sessionId is { } mine
            ? $"Windows session {session}, not this app's session {mine}"
            : "a Windows session this app could not read";

    private GatewayPeerRoute GatewayRoute(PortOwner owner)
    {
        if (!string.Equals(owner.ProcessName, GatewayProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return GatewayPeerRoute.None;
        }

        if (string.IsNullOrWhiteSpace(owner.ImagePath))
        {
            // Windows gave no path, with a handle or from its process list. A name is anyone's to pick: refused.
            return GatewayPeerRoute.None;
        }

        // The pid must still name the process that opened the socket: one younger than its socket only reused the pid of a creator that is gone
        // (whose child may still serve the socket). Times Windows did not give are no proof either way: refused (CUST-392).
        if (!owner.PidMatchesSocket)
        {
            return GatewayPeerRoute.None;
        }

        // A path from the process list (the process could not be opened: another account's, or elevated) says which file, not whose process.
        // Only one in this app's session is considered at all.
        var thisSession = InAppSession(owner);
        if (owner.ImageWithoutHandle && !thisSession)
        {
            return GatewayPeerRoute.None;
        }

        // The name comes from the process list and can be anything; the file it runs from
        // must carry the same name and sit where the installer put the gateway.
        if (!string.Equals(
                Path.GetFileNameWithoutExtension(owner.ImagePath),
                GatewayProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            return GatewayPeerRoute.None;
        }

        var imageDirectory = NormalizeDirectory(Path.GetDirectoryName(owner.ImagePath));
        if (imageDirectory is null)
        {
            return GatewayPeerRoute.None;
        }

        if (IsInstallDirectory(imageDirectory))
        {
            // The profile is this account's alone (its owner, SYSTEM and Administrators), so a gateway there was put there by this account. A folder
            // outside it may be one other accounts can write to: the process must be in this app's session, or the folder closed to them (CUST-392).
            return InProfile(imageDirectory) || thisSession || FolderProblem(owner.ImagePath) is null
                ? GatewayPeerRoute.InstallDirectory
                : GatewayPeerRoute.None;
        }

        // The one other place a gateway may run from: the helper's administrator copy, only while it passes its own checks right now, and only
        // in this app's session. Every account may run the file in Program Files; another account's run of it is not the helper's start.
        return thisSession && _adminCopy is not null && _adminCopy.IsTrustedImage(owner.ImagePath)
            ? GatewayPeerRoute.AdministratorCopy
            : GatewayPeerRoute.None;
    }

    private bool IsInstallDirectory(string directory) =>
        InstallDirectories().Any(install => string.Equals(install, directory, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installer's directory <paramref name="imagePath"/> is the gateway in, or null when it is not one (or not the gateway's file name).</summary>
    private string? InstallDirectoryOf(string imagePath) =>
        string.Equals(Path.GetFileNameWithoutExtension(imagePath), GatewayProcessName, StringComparison.OrdinalIgnoreCase) &&
        NormalizeDirectory(Path.GetDirectoryName(imagePath)) is { } directory && IsInstallDirectory(directory)
            ? directory
            : null;

    /// <summary>True when <paramref name="directory"/> (normalised) is this account's profile folder or below it.</summary>
    private bool InProfile(string directory) =>
        _profile is { } profile &&
        (string.Equals(directory, profile, StringComparison.OrdinalIgnoreCase) ||
         (directory.Length > profile.Length && directory[profile.Length] == Path.DirectorySeparatorChar &&
          directory.StartsWith(profile, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Why the gateway at <paramref name="imagePath"/>, in an installer's directory outside the profile, may have been put there by another account;
    /// null only when <see cref="IInstallFolderTrust"/> finds the file and its folders closed to other accounts. No trust object, or one that
    /// throws, is a problem (fail closed).
    /// </summary>
    private string? FolderProblem(string imagePath)
    {
        if (_installFolders is null)
        {
            return "this app does not check who may change that folder";
        }

        try
        {
            return _installFolders.Problem(imagePath);
        }
#pragma warning disable CA1031 // A check that throws is a check that failed: not trusted.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return "who may change that folder could not be read";
        }
    }

    /// <summary>
    /// The installer's directories only. The directory of whichever <c>defenseclaw-gateway</c> PATH resolves to used to count too, which
    /// made trust follow an environment variable any process of the user can edit; it no longer does.
    /// </summary>
    private IEnumerable<string> InstallDirectories()
    {
        if (NormalizeDirectory(_paths.BinDirectory) is { } bin)
        {
            yield return bin;
        }

        // The installer script's directory (%USERPROFILE%\.local\bin) is the other place a gateway is installed to; see
        // DefenseClawPaths.FallbackBinDirectories. Named here too, so the gateway it started is trusted whether or not PATH carries it.
        foreach (var fallback in _paths.FallbackBinDirectories)
        {
            if (NormalizeDirectory(fallback) is { } normalized)
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
