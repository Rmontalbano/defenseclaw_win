using System.IO;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>
/// The peer check's second trusted place: the administrator copy of the gateway that "Run gateway as administrator" keeps in
/// <c>%ProgramFiles%\DefenseClaw Companion\AdminGateway\&lt;SID&gt;</c>. Fails closed: anything unreadable is not trusted.
/// <para>
/// <b>Why Program Files makes the copy worth the token.</b> A program of the same account can write anywhere the user can, so a file in the
/// profile says nothing about who put it there. Under Program Files only administrators can write, so a file there that the helper recorded
/// was put there through a UAC prompt the user approved. That is <i>checked, not assumed</i>: <see cref="AdminCopyInspector.InspectGatewayCopy"/>
/// reads the owner and access list of Program Files, the product folder, <c>AdminGateway</c>, the SID folder, the copy and <c>copy.json</c> on
/// every call (<see cref="AdminAcl.Problems"/>: owner an administrator, no other principal with a write-like right), refuses a link anywhere on
/// the way, and requires the copy's SHA-256 to equal the one in <c>copy.json</c>. The SID folder is the signed-in account's own, from its token
/// (<see cref="WindowsAdminLayoutSource"/>), so another account's copy is never this account's gateway.
/// </para>
/// <para>
/// <b>What the file does not say.</b> Users may read and run the copy, so its path says which file runs, not whose process it is. The peer
/// check therefore also wants the process in this app's Windows session (<see cref="GatewayPeerVerifier"/>), and it reads the path without a
/// handle when an elevated gateway cannot be opened (CUST-362). The helper's <c>last-result.json</c> and the gateway's <c>gateway.pid</c> are
/// never a substitute for the path: a gateway whose path Windows gives neither way is refused.
/// </para>
/// </summary>
internal sealed class AdminCopyTrust : IAdminCopyTrust
{
    private readonly IAdminLayoutSource _layout;
    private readonly AdminCopyInspector _inspector;

    public AdminCopyTrust(IAdminLayoutSource layout, IAdminFileSystem fs)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _inspector = new AdminCopyInspector(fs ?? throw new ArgumentNullException(nameof(fs)));
    }

    public bool IsTrustedImage(string imagePath)
    {
        if (_layout.Get() is not { } layout || !layout.IsGatewayCopy(imagePath))
        {
            return false;
        }

        try
        {
            return _inspector.InspectGatewayCopy(layout).Trusted;
        }
#pragma warning disable CA1031 // A check that throws is a check that failed: not trusted.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public string? ExplainRefusal(PortOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        try
        {
            return Explain(owner);
        }
#pragma warning disable CA1031 // Words for a banner: a look that fails leaves the verifier's own sentence.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private string? Explain(PortOwner owner)
    {
        if (string.IsNullOrWhiteSpace(owner.ImagePath) || _layout.Get() is not { } layout)
        {
            return null;
        }

        if (layout.IsGatewayCopy(owner.ImagePath))
        {
            var copy = _inspector.InspectGatewayCopy(layout);
            return copy.Trusted
                ? null
                : "runs from this account's administrator copy, but that copy did not pass its checks: " +
                  (copy.Problems.FirstOrDefault()?.TrimEnd('.') ?? "it is not the file the helper recorded");
        }

        return FolderUnder(layout.AdminGatewayRoot, owner.ImagePath) switch
        {
            null => null,
            { Length: > 0 } sid when AdminLayout.IsValidSid(sid) && !string.Equals(sid, layout.UserSid, StringComparison.OrdinalIgnoreCase) =>
                "runs from another account's administrator copy, not this account's, so it is not sent this account's token",
            _ => "runs from this app's administrator folder, but not from this account's administrator copy",
        };
    }

    /// <summary>The name of the folder directly below <paramref name="root"/> that holds <paramref name="path"/>; null when it is not below it.</summary>
    private static string? FolderUnder(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!AdminLayout.IsInside(root, full) || AdminLayout.SamePath(root, full))
            {
                return null;
            }

            var rest = full[(Path.TrimEndingDirectorySeparator(root).Length + 1)..];
            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            return separator < 0 ? string.Empty : rest[..separator];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
