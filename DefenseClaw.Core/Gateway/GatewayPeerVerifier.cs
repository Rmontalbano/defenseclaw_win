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
    public GatewayPeerVerifier(
        DefenseClawPaths paths,
        IPortOwnerInspector inspector,
        IConnectionOwnerInspector? connections = null,
        IAdminCopyTrust? adminCopy = null,
        int? currentSessionId = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _connections = connections ?? inspector as IConnectionOwnerInspector;
        _adminCopy = adminCopy;
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

        if (!InThisSession(owner))
        {
            if (owner.ImageWithoutHandle)
            {
                return $"{who} runs in {SessionWords(owner)}, and Windows would not let this app open it to see which account runs it";
            }

            if (_adminCopy?.IsTrustedImage(owner.ImagePath) == true)
            {
                return $"{who} runs this account's administrator copy, but in {SessionWords(owner)}, so it is not the gateway the helper started";
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

    /// <summary>True when <paramref name="owner"/> runs in this app's Windows session (both known and equal).</summary>
    private bool InThisSession(PortOwner owner) => owner.SessionId is { } session && _sessionId is { } mine && session == mine;

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

        // A path from the process list (the process could not be opened: another account's, or elevated) says which file, not whose process.
        // Only one in this app's session is considered at all.
        var thisSession = InThisSession(owner);
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

        if (InstallDirectories().Any(directory => string.Equals(directory, imageDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return GatewayPeerRoute.InstallDirectory;
        }

        // The one other place a gateway may run from: the helper's administrator copy, only while it passes its own checks right now, and only
        // in this app's session. Every account may run the file in Program Files; another account's run of it is not the helper's start.
        return thisSession && _adminCopy is not null && _adminCopy.IsTrustedImage(owner.ImagePath)
            ? GatewayPeerRoute.AdministratorCopy
            : GatewayPeerRoute.None;
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
