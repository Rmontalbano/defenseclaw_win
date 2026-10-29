using System.IO;
using System.Security.Principal;
using DefenseClaw.Core.Gateway;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>
/// The peer check's question about an installer's directory outside this account's profile (CUST-392): could another account have put the
/// gateway there? Answered from the owner and access list of the file and every folder above it, read through the same
/// <see cref="IAdminFileSystem"/> and judged by the same <see cref="AdminAcl"/> policy that guards the administrator copy, with this account trusted
/// beside SYSTEM and Administrators.
/// <list type="bullet">
/// <item>The file and every folder up to the volume's root: owned by this account or an administrative principal, and no other principal may
/// write, append, delete, re-permission or take it (<see cref="AdminAcl.WriteLikeMask"/>).</item>
/// <item>The volume's root: the same owner rule, and no other principal may delete, rename or re-permission what it holds
/// (<see cref="AdminAcl.TakeOverMask"/>). Its usual "Authenticated Users may add folders" takes nothing that is there; the Modify those new
/// folders inherit is what the first rule catches.</item>
/// </list>
/// A link or junction anywhere on the way, a missing node, an access list that cannot be read, a network path or an unknown account is a
/// problem: fails closed. The profile itself is never asked about (the verifier trusts it as this account's alone). A closed folder says nobody
/// else put the file there, not whose process runs it (others may still read and run it), so the verifier also wants the process in this app's
/// session.
/// </summary>
internal sealed class InstallFolderTrust : IInstallFolderTrust
{
    private readonly IAdminFileSystem _fs;
    private readonly Func<string?> _accountSid;

    /// <param name="fs">The disk (the app's read-only reader; a test's in-memory one).</param>
    /// <param name="accountSid">This account's SID; null reads it from this process's token (a test names one).</param>
    public InstallFolderTrust(IAdminFileSystem fs, Func<string?>? accountSid = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _accountSid = accountSid ?? CurrentAccountSid;
    }

    public string? Problem(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        try
        {
            return Check(imagePath);
        }
#pragma warning disable CA1031 // A check that throws is a check that failed: not trusted.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return "who may change that folder could not be read";
        }
    }

    private string? Check(string imagePath)
    {
        if (_accountSid() is not { Length: > 0 } sid)
        {
            return "this account could not be identified to check who may change that folder";
        }

        var file = Path.GetFullPath(imagePath);
        if (file.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return $"{file} is on a network or device path, whose access this app does not judge";
        }

        if (Judge(file, mustBeDirectory: false, sid, AdminAcl.WriteLikeMask) is { } fileProblem)
        {
            return fileProblem;
        }

        for (var folder = Path.GetDirectoryName(file); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            var isRoot = string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase);
            if (Judge(folder, mustBeDirectory: true, sid, isRoot ? AdminAcl.TakeOverMask : AdminAcl.WriteLikeMask) is { } folderProblem)
            {
                return folderProblem;
            }
        }

        return null;
    }

    private string? Judge(string path, bool mustBeDirectory, string sid, uint mask)
    {
        var node = _fs.Stat(path);
        if (!node.Exists)
        {
            return $"{path} is not there";
        }

        if (node.IsReparsePoint)
        {
            return $"{path} is a link or junction, which is never followed";
        }

        if (node.IsDirectory != mustBeDirectory)
        {
            return mustBeDirectory ? $"{path} is not a folder" : $"{path} is a folder, not a file";
        }

        if (node.Security is null)
        {
            return $"the access list of {path} could not be read";
        }

        return AdminAcl.Problems(node.Security, path, sid, mask).FirstOrDefault()?.TrimEnd('.');
    }

    private static string? CurrentAccountSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }
}
