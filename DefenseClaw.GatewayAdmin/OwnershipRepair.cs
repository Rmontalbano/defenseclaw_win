using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

/// <summary>What a repair pass did.</summary>
internal sealed record RepairReport(int Examined, int AlreadyUser, int Fixed, int SkippedLinks, int LeftForeign, int Failed, bool Truncated, IReadOnlyList<string> FirstFailures)
{
    public bool Success => Failed == 0 && !Truncated;
}

/// <summary>
/// Gives back to the user the files under their <c>.defenseclaw</c> that an elevated gateway created as the Administrators group. Walks the tree
/// without ever following a link: a reparse point is counted and left alone (its target is not ours), and the walk stays below the one folder
/// it was given. Only objects owned by Administrators are changed; an object owned by anyone else is left and counted, because it is not the
/// elevated gateway's doing. No <c>icacls</c>, no <c>takeown</c>: the owner is set through <see cref="IOwnerFileSystem"/> (Win32, by handle).
/// </summary>
internal static class OwnershipRepair
{
    public const int MaxEntries = 200_000;
    public const int MaxDepth = 40;

    public static RepairReport Run(AdminLayout layout, IOwnerFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fs);

        int examined = 0, already = 0, fixedCount = 0, links = 0, foreign = 0, failed = 0;
        var truncated = false;
        var failures = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((layout.DataDirectory, 0));

        while (stack.Count > 0)
        {
            var (path, depth) = stack.Pop();
            if (!AdminLayout.IsInside(layout.DataDirectory, path))
            {
                continue;
            }

            if (++examined > MaxEntries || depth > MaxDepth)
            {
                truncated = true;
                break;
            }

            var node = fs.Probe(path);
            if (node is null)
            {
                continue;
            }

            if (node.IsReparsePoint)
            {
                links++;
                continue;
            }

            if (string.Equals(node.OwnerSid, layout.UserSid, StringComparison.OrdinalIgnoreCase))
            {
                already++;
            }
            else if (string.Equals(node.OwnerSid, AdminAcl.Administrators, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    fs.SetOwner(path, layout.UserSid);
                    fixedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed++;
                    if (failures.Count < 5)
                    {
                        failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }
            else
            {
                foreign++;
            }

            if (node.IsDirectory)
            {
                foreach (var child in fs.Children(path))
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return new RepairReport(examined, already, fixedCount, links, foreign, failed, truncated, failures);
    }
}
