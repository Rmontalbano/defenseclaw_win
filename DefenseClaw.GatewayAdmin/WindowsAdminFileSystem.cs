using System.Security.AccessControl;
using System.Security.Principal;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

/// <summary>
/// The changing half of <see cref="IAdminFileSystem"/> on a real disk. Reached only by the elevated helper, and only after every refusal in
/// <see cref="AdminHelper"/> has passed. Folders are created with an explicit, protected access list (owner Administrators through the elevated
/// token's own default, full control for SYSTEM and Administrators, read and execute for Users), so no parent can pass a looser one down; files
/// are created inside them and inherit it. The helper never changes the token's default owner while it writes here: that is done only around
/// the gateway's own run (<see cref="WindowsTokenOwner"/>), so nothing in Program Files is ever owned by the user.
/// </summary>
internal sealed class WindowsAdminFileSystem : WindowsAdminFileSystemReader
{
    public override void EnsureAdminFolder(string path)
    {
        var missing = new Stack<string>();
        for (var current = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            var folder = missing.Pop();
            new DirectoryInfo(folder).Create(AdminFolderSecurity());
        }
    }

    public override Stream CreateTemp(string folder, out string tempPath)
    {
        tempPath = Path.Combine(folder, ".tmp-" + Guid.NewGuid().ToString("N"));
        return new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
    }

    public override void Replace(string tempPath, string finalPath) => File.Move(tempPath, finalPath, overwrite: true);

    public override void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind, and named ".tmp-": the next run does not look at it.
        }
    }

    public override void WriteText(string path, string text)
    {
        var folder = Path.GetDirectoryName(path)!;
        string tempPath;
        using (var stream = CreateTemp(folder, out tempPath))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        Replace(tempPath, path);
    }

    private static DirectorySecurity AdminFolderSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdminAcl.System), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdminAcl.Administrators), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdminAcl.Users), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
