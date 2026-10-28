namespace DefenseClaw.Core.Runtime;

/// <summary>
/// The two connectors DefenseClaw 1.0.4 removed (OpenHands and OmniGent; <c>cli/defenseclaw/removed_connectors.py</c> <c>NAMES</c> and <c>is_removed</c>).
/// Gated by <see cref="RuntimeCapability.ConnectorsRetired"/>: a call site hides them only when the connected runtime has the capability, so 0.8.10,
/// 1.0.0 to 1.0.3, the pinned build and a runtime that has not answered keep them exactly as before.
/// </summary>
public static class RemovedConnectors
{
    /// <summary>The wire names, as the runtime keys them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["openhands", "omnigent"];

    /// <summary>
    /// True for a removed connector's name in any spelling the runtime accepts (<c>is_removed</c>: trimmed, case-folded, and the two aliases of OpenHands,
    /// <c>open-hands</c> and <c>open_hands</c>).
    /// </summary>
    public static bool IsRemoved(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var key = name.Trim();
        return key.Equals("openhands", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("omnigent", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("open-hands", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("open_hands", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the connected runtime has removed them (<see cref="RuntimeCapability.ConnectorsRetired"/>).</summary>
    public static bool AreRetired(RuntimeCapabilities? capabilities) =>
        capabilities is not null && capabilities.Has(RuntimeCapability.ConnectorsRetired);

    /// <summary><paramref name="names"/> without the removed connectors when <paramref name="retired"/>; the same sequence otherwise.</summary>
    public static IEnumerable<string> Without(IEnumerable<string> names, bool retired)
    {
        ArgumentNullException.ThrowIfNull(names);
        return retired ? names.Where(static n => !IsRemoved(n)) : names;
    }
}
