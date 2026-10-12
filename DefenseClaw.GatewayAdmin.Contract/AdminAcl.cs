namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>One access-control entry of a DACL, reduced to what the policy needs.</summary>
/// <param name="Sid">The principal's SID string.</param>
/// <param name="Allow">True for an allow entry. Deny entries are ignored by the policy: a deny can be removed by whoever may write the DACL.</param>
/// <param name="Mask">The Win32 access mask (the same bits as <c>FileSystemRights</c>).</param>
/// <param name="InheritOnly">True when the entry applies only to children, not to the object itself.</param>
public sealed record AclEntry(string Sid, bool Allow, uint Mask, bool InheritOnly = false);

/// <summary>What the policy reads of one file or folder.</summary>
public sealed record NodeSecurity(string? OwnerSid, bool HasDacl, IReadOnlyList<AclEntry> Entries);

/// <summary>
/// The "admin-only" test for a path that is going to be run elevated: its owner is an administrative principal, it has a DACL, and no other
/// principal is allowed to write to it, append to it, delete it or its children, change its permissions or take ownership of it. This is
/// the Windows analogue of the Mac helper's <c>rejectWriteACL</c> and its owner and mode checks. Pure: the real reading of the ACL is
/// <see cref="IAdminFileSystem"/>'s.
/// </summary>
public static class AdminAcl
{
    public const string System = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string Users = "S-1-5-32-545";
    public const string AuthenticatedUsers = "S-1-5-11";
    public const string Everyone = "S-1-1-0";
    public const string CreatorOwner = "S-1-3-0";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>WriteData/AddFile, AppendData/AddSubdirectory, WriteEA, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, WriteDAC, WriteOwner, GenericWrite, GenericAll.</summary>
    public const uint WriteLikeMask = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    /// <summary>ReadData, ReadEA, Execute, ReadAttributes, ReadPermissions, Synchronize: the rights Users get.</summary>
    public const uint ReadExecuteMask = 0x1 | 0x8 | 0x20 | 0x80 | 0x20000 | 0x100000;

    private static readonly HashSet<string> Administrative = new(StringComparer.OrdinalIgnoreCase)
    {
        System, Administrators, TrustedInstaller,
    };

    // CREATOR OWNER is a placeholder the system replaces with the creating account when it inherits; on an object made by an administrator
    // it is an administrator. It is allowed to write for that reason and no other.
    private static readonly HashSet<string> MayWrite = new(StringComparer.OrdinalIgnoreCase)
    {
        System, Administrators, TrustedInstaller, CreatorOwner,
    };

    public static bool IsAdministrative(string? sid) => sid is not null && Administrative.Contains(sid);

    /// <summary>Everything wrong with <paramref name="node"/> for something that will be run elevated; empty when it is admin-only.</summary>
    public static IReadOnlyList<string> Problems(NodeSecurity node, string label)
    {
        ArgumentNullException.ThrowIfNull(node);

        var problems = new List<string>();

        if (!IsAdministrative(node.OwnerSid))
        {
            problems.Add($"{label} is owned by {node.OwnerSid ?? "an unknown owner"}, not an administrator.");
        }

        if (!node.HasDacl)
        {
            problems.Add($"{label} has no access list (everyone may change it).");
            return problems;
        }

        foreach (var entry in node.Entries)
        {
            if (!entry.Allow || entry.InheritOnly || (entry.Mask & WriteLikeMask) == 0)
            {
                continue;
            }

            if (!MayWrite.Contains(entry.Sid))
            {
                problems.Add($"{label} lets {entry.Sid} write to it, change it or delete it.");
            }
        }

        return problems;
    }
}
