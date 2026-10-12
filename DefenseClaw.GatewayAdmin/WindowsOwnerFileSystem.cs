using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DefenseClaw.GatewayAdmin;

/// <summary>
/// The owner API for <see cref="OwnershipRepair"/>: reads an owner from the security descriptor, lists a folder without following links, and sets an
/// owner through a handle opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and then checked not to be a reparse point, so the object changed is the
/// one named and never the target of a link swapped in meanwhile. Takes the ownership and restore privileges (an administrator's token has them,
/// disabled) for the length of the call; there is no <c>icacls</c> and no <c>takeown</c> anywhere.
/// </summary>
internal sealed class WindowsOwnerFileSystem : IOwnerFileSystem
{
    private const uint FileAttributeReparsePoint = 0x400;

    public OwnerNode? Probe(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            var isReparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            if (isReparse)
            {
                return new OwnerNode(isDirectory, true, null);
            }

            string? owner = info switch
            {
                DirectoryInfo d => d.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value,
                FileInfo f => f.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value,
                _ => null,
            };
            return new OwnerNode(isDirectory, false, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Children(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public void SetOwner(string path, string ownerSid)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        var handle = Native.CreateFile(
            path,
            Native.WriteOwner,
            Native.FileShareAll,
            IntPtr.Zero,
            Native.OpenExisting,
            Native.FileFlagBackupSemantics | Native.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            throw new IOException("could not be opened (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
        }

        var sid = new SecurityIdentifier(ownerSid);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var sidMemory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            if (!Native.GetFileInformationByHandle(handle, out var information) || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("is a link; it was left alone");
            }

            Marshal.Copy(bytes, 0, sidMemory, bytes.Length);
            var result = Native.SetSecurityInfo(handle, Native.SeFileObject, Native.OwnerSecurityInformation, sidMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
            {
                throw new IOException("owner could not be set (" + new Win32Exception(result).Message + ")");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidMemory);
            _ = Native.CloseHandle(handle);
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenAdjustPrivileges | Native.TokenQuery, out var token))
        {
            throw new InvalidOperationException("The process token could not be opened (" + Marshal.GetLastWin32Error() + ").");
        }

        try
        {
            if (!Native.LookupPrivilegeValue(null, name, out var luid))
            {
                throw new InvalidOperationException(name + " is unknown.");
            }

            var privileges = new Native.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new Native.LuidAndAttributes { Luid = luid, Attributes = Native.SePrivilegeEnabled },
            };
            if (!Native.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
            {
                throw new InvalidOperationException(name + " could not be enabled (" + Marshal.GetLastWin32Error() + ").");
            }
        }
        finally
        {
            _ = Native.CloseHandle(token);
        }
    }
}
