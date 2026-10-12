using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// The reading half of <see cref="IAdminFileSystem"/> on a real Windows disk: attributes, the owner and the access list read from the raw
/// security descriptor, a reparse-point flag that is never followed, an exclusive-read open that checks it got the file it asked for, and a
/// small text read. The app uses it as is (through <see cref="ReadOnlyAdminFileSystem"/>, whose changing members refuse); the helper's
/// <c>WindowsAdminFileSystem</c> adds the changing half. Reading needs no rights: the access lists of Program Files are readable by Users.
/// </summary>
public abstract class WindowsAdminFileSystemReader : IAdminFileSystem
{
    public FsNode Stat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return FsNode.Missing(path);
            }

            var attributes = info.Attributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
            var length = !isDirectory && info is FileInfo file ? file.Length : 0;

            // The security of a link's target is not the link's, and reading it would follow the link: a reparse point is reported as one and nothing more.
            NodeSecurity? security = isReparse ? null : ReadSecurity(info);
            return new FsNode(path, true, isDirectory, isReparse, length, info.LastWriteTimeUtc, security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A path that cannot be examined is reported as present with no readable security, which every check treats as a fault, not a pass.
            return new FsNode(path, Exists: File.Exists(path) || Directory.Exists(path), IsDirectory: false, IsReparsePoint: false, 0, default, null);
        }
    }

    public Stream OpenReadExclusive(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            var final = FinalPath(stream);
            if (final is null || !AdminLayout.SamePath(final, path))
            {
                throw new IOException("The file that was opened is not the file that was asked for (a link or junction on the way).");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public string? ReadText(string path, int maxBytes)
    {
        try
        {
            using var stream = OpenReadExclusive(path);
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public abstract void EnsureAdminFolder(string path);

    public abstract Stream CreateTemp(string folder, out string tempPath);

    public abstract void Replace(string tempPath, string finalPath);

    public abstract void DeleteFile(string path);

    public abstract void WriteText(string path, string text);

    private static NodeSecurity? ReadSecurity(FileSystemInfo info)
    {
        try
        {
            var security = info switch
            {
                DirectoryInfo d => (FileSystemSecurity)d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
                FileInfo f => f.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
                _ => null,
            };
            if (security is null)
            {
                return null;
            }

            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            var owner = descriptor.Owner?.Value;
            var dacl = descriptor.DiscretionaryAcl;
            if (dacl is null)
            {
                return new NodeSecurity(owner, false, Array.Empty<AclEntry>());
            }

            var entries = new List<AclEntry>(dacl.Count);
            foreach (var ace in dacl)
            {
                if (ace is CommonAce common)
                {
                    var allow = common.AceType is AceType.AccessAllowed;
                    var inheritOnly = (common.AceFlags & AceFlags.InheritOnly) != 0;
                    entries.Add(new AclEntry(common.SecurityIdentifier.Value, allow, unchecked((uint)common.AccessMask), inheritOnly));
                }
                else if (ace is ObjectAce objectAce && objectAce.AceType is AceType.AccessAllowedObject)
                {
                    // Not expected on a file; counted as an allow so that it cannot hide a write right.
                    entries.Add(new AclEntry(objectAce.SecurityIdentifier.Value, true, unchecked((uint)objectAce.AccessMask), (objectAce.AceFlags & AceFlags.InheritOnly) != 0));
                }
            }

            return new NodeSecurity(owner, true, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FinalPath(FileStream stream)
    {
        var handle = stream.SafeFileHandle.DangerousGetHandle();
        var builder = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, builder, builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity)
        {
            return null;
        }

        var text = builder.ToString();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return null;
        }

        return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern int GetFinalPathNameByHandle(IntPtr file, StringBuilder buffer, int bufferLength, int flags);
}

/// <summary>The app's file system: everything the shared reading does, and a refusal for anything that would change a file (only the helper changes files).</summary>
public sealed class ReadOnlyAdminFileSystem : WindowsAdminFileSystemReader
{
    private static NotSupportedException Refused() => new("The app never changes files in Program Files; only the elevated helper does.");

    public override void EnsureAdminFolder(string path) => throw Refused();

    public override Stream CreateTemp(string folder, out string tempPath) => throw Refused();

    public override void Replace(string tempPath, string finalPath) => throw Refused();

    public override void DeleteFile(string path) => throw Refused();

    public override void WriteText(string path, string text) => throw Refused();
}
