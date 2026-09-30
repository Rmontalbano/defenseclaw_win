using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Gateway;

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
/// installer's bin directory (or beside the gateway the PATH resolves to): a name alone is
/// anyone's to pick. The caller adds the second half, that <c>/health</c> parsed as a
/// <see cref="Models.GatewayHealth"/> — <see cref="GatewayClient"/> does.
/// </para>
/// <para>
/// Fails closed: an owner whose image path could not be read is <see cref="PortOwnerTrust.Other"/>.
/// </para>
/// </summary>
public sealed class GatewayPeerVerifier
{
    /// <summary>Image name (without extension) of the sidecar.</summary>
    public const string GatewayProcessName = "defenseclaw-gateway";

    private readonly DefenseClawPaths _paths;
    private readonly IPortOwnerInspector _inspector;

    public GatewayPeerVerifier(DefenseClawPaths paths, IPortOwnerInspector inspector)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    /// <summary>Classifies <paramref name="owner"/>.</summary>
    public PortOwnerTrust Classify(PortOwner? owner)
    {
        if (owner is null)
        {
            return PortOwnerTrust.Unknown;
        }

        if (owner.IsWslRelay)
        {
            return PortOwnerTrust.WslRelay;
        }

        return IsGatewayImage(owner) ? PortOwnerTrust.Gateway : PortOwnerTrust.Other;
    }

    /// <summary>True when <paramref name="owner"/> is the native gateway from the install directory.</summary>
    public bool IsTrustedOwner(PortOwner? owner) => Classify(owner) == PortOwnerTrust.Gateway;

    /// <summary>
    /// A delegate for <see cref="GatewayClient"/>'s peer check on <paramref name="port"/>: looks
    /// the owner up afresh on every call, so a listener swapped after the last poll is caught
    /// on the next authenticated request rather than believed.
    /// </summary>
    public Func<PortOwnerTrust> ForPort(int port) => () => Classify(_inspector.FindListener(port));

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

        return string.Equals(owner.ProcessName, GatewayProcessName, StringComparison.OrdinalIgnoreCase)
            ? $"{who} is not running from the DefenseClaw install directory"
            : $"{who} is not the DefenseClaw gateway";
    }

    private bool IsGatewayImage(PortOwner owner)
    {
        if (!string.Equals(owner.ProcessName, GatewayProcessName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(owner.ImagePath))
        {
            return false;
        }

        // The name comes from the process list and can be anything; the file it runs from
        // must carry the same name and sit where the installer put the gateway.
        if (!string.Equals(
                Path.GetFileNameWithoutExtension(owner.ImagePath),
                GatewayProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var imageDirectory = NormalizeDirectory(Path.GetDirectoryName(owner.ImagePath));
        if (imageDirectory is null)
        {
            return false;
        }

        return InstallDirectories().Any(directory => string.Equals(directory, imageDirectory, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The installer's bin directory, and the directory of the gateway PATH resolves to.</summary>
    private IEnumerable<string> InstallDirectories()
    {
        if (NormalizeDirectory(_paths.BinDirectory) is { } bin)
        {
            yield return bin;
        }

        if (_paths.GatewayCliPath is { } resolved && NormalizeDirectory(Path.GetDirectoryName(resolved)) is { } beside)
        {
            yield return beside;
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
