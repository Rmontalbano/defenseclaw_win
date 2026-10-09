namespace DefenseClaw.App.Services;

/// <summary>
/// When each component AI Discovery lists was first seen: one UTC instant (ticks) per product and per local model, ascending. What the
/// sidebar's AI Discovery badge (CUST-265) is counted from, and all it needs: "how many of these were first seen after the last visit" is a
/// comparison against a marker, so a reader that has the state file in hand (the Overview's agents card does, on its slow cadence) hands this over and
/// nothing is read for the badge. Made by <c>AiDiscoveryNovelty</c>; an empty file, a missing one and a scan that found nothing are all <see cref="None"/>.
/// </summary>
/// <param name="FirstSeenTicks">One entry per component, <see cref="DateTime.Ticks"/> of the UTC instant it was first seen, ascending. A component with no known first-seen time is not here: it cannot be called new.</param>
internal sealed record AiDiscoveryHead(IReadOnlyList<long> FirstSeenTicks)
{
    /// <summary>Nothing discovered.</summary>
    public static AiDiscoveryHead None { get; } = new(Array.Empty<long>());

    /// <summary>How many components were first seen strictly after <paramref name="markerTicks"/>.</summary>
    public int CountAfter(long markerTicks)
    {
        var count = 0;
        foreach (var seen in FirstSeenTicks)
        {
            if (seen > markerTicks)
            {
                count++;
            }
        }

        return count;
    }
}
