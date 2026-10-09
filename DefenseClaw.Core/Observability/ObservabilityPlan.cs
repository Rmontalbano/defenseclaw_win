using System.Globalization;

namespace DefenseClaw.Core.Observability;

/// <summary>
/// The words the DefenseClaw TUI uses for what a destination does to its events, so the app and the TUI say the same thing about the same install.
/// </summary>
public static class ObservabilityRedaction
{
    /// <summary>A destination that takes no redactable signal (metrics only), or sends nothing.</summary>
    public const string NotApplicable = "not-applicable";

    /// <summary>The built-in profile that sends events as they were recorded.</summary>
    public const string NoRedaction = "none";

    /// <summary>The aggregate label before the plan has been read (the TUI's own words).</summary>
    public const string Loading = "per-route (loading)";

    /// <summary>The aggregate label when the plan could not be read.</summary>
    public const string Unavailable = "per-route (unavailable)";

    /// <summary>
    /// What a destination sends its events as: <c>unredacted (none)</c> when every leg carries the profile <c>none</c>, <c>mixed: none, sensitive</c>
    /// when some do, <c>redacted: sensitive</c> when none does, <see cref="NotApplicable"/> when no leg has a profile
    /// (<c>V8DestinationStatus.redaction_label</c>). <paramref name="conditionalRoutes"/> says a route whose selector the plan could not decide
    /// (a minimum severity, a source, a connector) may send more, with a profile the plan does not show.
    /// </summary>
    public static string DestinationLabel(IReadOnlyCollection<string> profiles, bool conditionalRoutes = false)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (profiles.Count == 0)
        {
            return conditionalRoutes ? "per conditional route" : NotApplicable;
        }

        var sorted = string.Join(", ", profiles.Order(StringComparer.Ordinal));
        var label = profiles.Count == 1 && profiles.Contains(NoRedaction, StringComparer.Ordinal)
            ? "unredacted (none)"
            : profiles.Contains(NoRedaction, StringComparer.Ordinal) ? "mixed: " + sorted : "redacted: " + sorted;
        return conditionalRoutes ? label + " (+ conditional routes)" : label;
    }

    /// <summary>
    /// The one label for the whole install: <c>per-route · unredacted</c>, <c>per-route · whole-content</c> or <c>per-route · a,b</c>
    /// (<c>_v8_redaction_summary</c> of the TUI, used by its Configuration box and its status strip). There is no global switch in version 8:
    /// redaction is a property of each route, so the label says that and lists the profiles in use.
    /// </summary>
    public static string Aggregate(IEnumerable<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var distinct = profiles.Where(static p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0 || (distinct.Length == 1 && distinct[0] == NoRedaction))
        {
            return "per-route · unredacted";
        }

        return distinct.Length == 1 && distinct[0] == "whole" ? "per-route · whole-content" : "per-route · " + string.Join(",", distinct);
    }
}

/// <summary>Number formats shared by the labels (the TUI prints bytes with one decimal, in KiB and MiB).</summary>
public static class ObservabilityFormat
{
    /// <summary><c>512 B</c>, <c>1.5 KiB</c>, <c>64.0 MiB</c> (<c>_format_bytes</c>).</summary>
    public static string Bytes(long value) =>
        value < 1_024
            ? value.ToString(CultureInfo.InvariantCulture) + " B"
            : value < 1_024 * 1_024
                ? (value / 1_024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB"
                : (value / (1_024.0 * 1_024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>
/// The queue and batch limits the compiled plan sets for one destination (<c>delivery[]</c> of <c>observability plan --format json</c>): what the
/// destination is configured to hold, not what it holds now. A member the plan leaves out is null.
/// </summary>
public sealed record DeliveryLimits(
    long? MaxQueueSize,
    long? MaxQueueBytes,
    long? MaxExportBatchSize,
    long? MaxExportBatchBytes,
    long? ScheduledDelayMs)
{
    /// <summary>True when the plan sets none of them.</summary>
    public bool IsEmpty => MaxQueueSize is null && MaxQueueBytes is null && MaxExportBatchSize is null && MaxExportBatchBytes is null && ScheduledDelayMs is null;

    /// <summary><c>queue=2048 items/64.0 MiB; batch=256 items/8.0 MiB; delay=1000ms</c>, or <c>not-applicable</c> (<c>delivery_limits_label</c>).</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            var queue = Join(MaxQueueSize is { } items ? items.ToString(CultureInfo.InvariantCulture) + " items" : null, MaxQueueBytes is { } bytes ? ObservabilityFormat.Bytes(bytes) : null);
            if (queue.Length > 0)
            {
                parts.Add("queue=" + queue);
            }

            var batch = Join(
                MaxExportBatchSize is { } batchItems ? batchItems.ToString(CultureInfo.InvariantCulture) + " items" : null,
                MaxExportBatchBytes is { } batchBytes ? ObservabilityFormat.Bytes(batchBytes) : null);
            if (batch.Length > 0)
            {
                parts.Add("batch=" + batch);
            }

            if (ScheduledDelayMs is { } delay)
            {
                parts.Add("delay=" + delay.ToString(CultureInfo.InvariantCulture) + "ms");
            }

            return parts.Count == 0 ? ObservabilityRedaction.NotApplicable : string.Join("; ", parts);
        }
    }

    private static string Join(string? first, string? second) =>
        string.Join("/", new[] { first, second }.Where(static s => s is not null));
}

/// <summary>
/// One destination as <c>observability plan --format json</c> describes it: a row for every bucket and signal says what the compiled plan does
/// with it, so the destination's policy is read off its rows. Everything here is derived from the plan alone; what the gateway is doing with
/// the destination now comes from <c>/health</c>, and where it sends (its address) from config.yaml.
/// </summary>
/// <param name="Name">The destination's name (<c>local-sqlite</c>, or one the operator chose): the key that merges it with <c>/health</c>.</param>
/// <param name="Kind">The kind, when the plan's <c>delivery</c> list names it (only destinations that queue and batch are listed there); else empty.</param>
/// <param name="Enabled">False when every row of the destination says <c>destination_disabled</c>.</param>
/// <param name="Signals">The signals some bucket reaches the destination for, in the order logs, traces, metrics; empty for a disabled destination (the plan does not say what it would take).</param>
/// <param name="RoutedBuckets">How many buckets have events routed to the destination, under the plan's current collection settings.</param>
/// <param name="RedactionProfiles">The distinct redaction profiles of the routes that send, sorted.</param>
/// <param name="HasConditionalRoutes">True when a route the plan could not decide without the event (a severity, source, connector or action constraint) might send more.</param>
/// <param name="Limits">The queue and batch limits, or null when the plan lists none for the destination.</param>
public sealed record PlanDestination(
    string Name,
    string Kind,
    bool Enabled,
    IReadOnlyList<string> Signals,
    int RoutedBuckets,
    IReadOnlyList<string> RedactionProfiles,
    bool HasConditionalRoutes,
    DeliveryLimits? Limits)
{
    /// <summary>The destination's redaction in the TUI's words; see <see cref="ObservabilityRedaction.DestinationLabel"/>.</summary>
    public string RedactionLabel => ObservabilityRedaction.DestinationLabel(RedactionProfiles, HasConditionalRoutes);

    /// <summary>The destination's configured limits in the TUI's words; <c>not-applicable</c> when there are none.</summary>
    public string LimitsLabel => Limits is { IsEmpty: false } limits ? limits.Label : ObservabilityRedaction.NotApplicable;
}

/// <summary>
/// The compiled routing plan, as <c>defenseclaw observability plan --format json</c> prints it (0.8.10 and the newer runtime print the same document):
/// the destinations with their policy, and how many buckets the catalogue has. Immutable; built by <see cref="ObservabilityPlanParser"/>.
/// </summary>
/// <param name="Basis">What the rows are computed from (<c>canonical_go_compiled_routes</c>).</param>
/// <param name="ConfigVersion">The configuration version the plan was compiled from (8).</param>
/// <param name="PlanDigest">The digest of the compiled plan; two reads with the same digest describe the same policy.</param>
/// <param name="BucketCount">The size of the bucket catalogue the rows cover (14 in catalogue 1).</param>
/// <param name="Destinations">The destinations, in the order the plan lists them.</param>
/// <param name="UnreadableRows">How many rows were skipped because they were not objects or lacked a bucket, signal, destination or decision.</param>
public sealed record ObservabilityPlan(
    string Basis,
    int ConfigVersion,
    string PlanDigest,
    int BucketCount,
    IReadOnlyList<PlanDestination> Destinations,
    int UnreadableRows)
{
    /// <summary>The distinct redaction profiles in use across every destination, sorted.</summary>
    public IReadOnlyList<string> RedactionProfiles =>
        Destinations.SelectMany(static d => d.RedactionProfiles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The aggregate redaction label of the whole install (<c>per-route · unredacted</c>): what the Configuration card shows, and what a status-strip
    /// chip would. See <see cref="ObservabilityRedaction.Aggregate"/>.
    /// </summary>
    public string RedactionSummary => ObservabilityRedaction.Aggregate(RedactionProfiles);

    /// <summary>The destination called <paramref name="name"/> (without case), or null.</summary>
    public PlanDestination? Destination(string name) =>
        Destinations.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}
