namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// The Windows analogue of the Mac app's exit 77 ("this installation has gateway state owned by root"): files under the user's
/// <c>.defenseclaw</c> that the gateway made while it ran elevated and that are owned by the Administrators group instead of the user. DefenseClaw
/// 1.x refuses data its user does not own (<c>safefile</c>: "private path is not owned by the current user"), so a later gateway or CLI that is
/// not elevated cannot use them until the owner is reset. Reads only; the reset is the helper's <c>repair-ownership</c> verb.
/// </summary>
public static class StateOwnership
{
    /// <summary>The files the gateway writes first and keeps open; one wrong owner among them is enough to lock a non-elevated start out.</summary>
    public static IReadOnlyList<string> WatchedFiles { get; } = new[]
    {
        "gateway.pid", "hook_contract_lock.json", "audit.db", ".env", "device.key", "gateway.log", "config.yaml",
    };

    /// <summary>The names of the watched files that exist and are not owned by <paramref name="userSid"/>; empty when there is no problem.</summary>
    public static IReadOnlyList<string> Foreign(AdminLayout layout, IAdminFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fs);

        var found = new List<string>();
        foreach (var name in WatchedFiles)
        {
            var node = fs.Stat(Path.Combine(layout.DataDirectory, name));
            if (!node.Exists || node.IsReparsePoint)
            {
                continue;
            }

            // An owner that cannot be read is reported, not assumed fine: the user can always read the owner of their own file.
            if (node.Security?.OwnerSid is not { } owner || !string.Equals(owner, layout.UserSid, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(name);
            }
        }

        return found;
    }
}
