using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Services.Observability;

/// <summary>
/// Whether the compiled routing plan (<c>observability plan</c>, CUST-272) sends the local stack everything Agent360 draws on, so the link can say
/// so when it does not. Agent360 reads logs (Loki), metrics (Prometheus) and traces (Tempo) of the <c>local-observability</c> destination; a route that
/// is narrowed - fewer buckets, a missing signal, a selector on severity, source or connector - leaves the dashboard partly empty, and that is the
/// plan's doing, not a fault of the dashboard.
/// </summary>
internal static class Agent360Coverage
{
    /// <summary>The destination <c>setup local-observability up</c> writes into config.yaml.</summary>
    public const string DestinationName = "local-observability";

    /// <summary>The signals Agent360 has panels for.</summary>
    private static readonly string[] Signals = { "logs", "traces", "metrics" };

    /// <summary>
    /// One sentence for the card, or empty when the plan routes everything (or has not been read: nothing is claimed without it).
    /// </summary>
    public static string NoteFor(ObservabilityPlan? plan)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        var destination = plan.Destination(DestinationName);
        if (destination is null || !destination.Enabled)
        {
            return "Partial coverage: the routing plan has no enabled " + DestinationName + " destination, so Agent360 will show little or nothing until the local stack is set up.";
        }

        var gaps = new List<string>();
        var missing = Signals.Where(s => !destination.Signals.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0)
        {
            gaps.Add("it is not sent " + string.Join(", ", missing));
        }

        if (plan.BucketCount > 0 && destination.RoutedBuckets < plan.BucketCount)
        {
            gaps.Add("only " + destination.RoutedBuckets + " of " + plan.BucketCount + " event buckets are routed to it");
        }

        if (destination.HasConditionalRoutes)
        {
            gaps.Add("some routes apply only to certain severities, sources or connectors");
        }

        return gaps.Count == 0
            ? string.Empty
            : "Partial coverage: in the routing plan " + string.Join("; ", gaps) + ", so some Agent360 panels may be empty.";
    }
}
