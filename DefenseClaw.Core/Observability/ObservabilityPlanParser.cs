using System.Text.Json;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Observability;

/// <summary>The outcome of reading one plan document: a plan, or the one sentence that says why there is none.</summary>
/// <param name="Plan">Set exactly when the document was a plan.</param>
/// <param name="Error">Why not, when <paramref name="Plan"/> is null; it never carries any of the document's text.</param>
public sealed record ObservabilityPlanParseResult(ObservabilityPlan? Plan, string Error)
{
    /// <summary>True when <see cref="Plan"/> is set.</summary>
    public bool IsOk => Plan is not null;
}

/// <summary>
/// Reads the document <c>defenseclaw observability plan --format json</c> prints into an <see cref="ObservabilityPlan"/>. Pure: no I/O, never throws.
/// <para>
/// <b>Shape.</b> <c>observability_plan</c> in <c>commands/cmd_observability.py</c> (line 134 of 0.8.10; the same code at the newer runtime, source commit
/// 95159fd): <c>{basis, config_version, plan_digest, network_validation, delivery[], connector_export_custody, rows[]}</c>. A row is one
/// <c>(bucket, signal, destination)</c> triple with the compiled plan's <c>decision</c> for it - <c>destination_disabled</c>, <c>signal_not_selected</c>,
/// <c>not_collected</c>, <c>send</c>, <c>drop</c>, <c>conditional</c> (with <c>potential_action</c>) or <c>unmatched</c> - and, for a <c>send</c> of logs or
/// traces, the <c>redaction_profile</c>. The plan carries no address, no retention and no path: those are config.yaml's
/// (<see cref="ObservabilityConfigFacts"/>), and what the gateway does now is <c>/health</c>'s.
/// </para>
/// <para>
/// <b>What it demands, and what it forgives.</b> <c>rows</c> must be an array and must name at least one destination: a document without them is not a
/// plan, and an empty one is never read as "nothing is exported" (the local store is always there). Everything else decodes leniently, because an
/// app that cannot render on version skew is worse than one that renders less: a row of the wrong shape is skipped and counted
/// (<see cref="ObservabilityPlan.UnreadableRows"/>), a decision it has never heard of means "enabled, not routed", a limit that is not a positive
/// number is absent. The document is bounded (size, rows, destinations, buckets), every name is written out with its control and bidirectional
/// characters visible, and a banner line before the JSON is skipped.
/// </para>
/// </summary>
public static class ObservabilityPlanParser
{
    /// <summary>The longest document read: a plan is about 350 bytes a row, 42 rows a destination.</summary>
    public const int MaxLength = 8 * 1024 * 1024;

    public const int MaxRows = 50_000;
    public const int MaxDestinations = 256;
    public const int MaxBuckets = 128;
    public const int MaxNameLength = 128;

    private const int MaxDepth = 32;

    private static readonly string[] SignalOrder = { "logs", "traces", "metrics" };

    /// <summary>Reads <paramref name="text"/>, which may start with a banner line before the JSON.</summary>
    public static ObservabilityPlanParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail("The plan was empty.");
        }

        if (text.Length > MaxLength)
        {
            return Fail("The plan was larger than this app reads.");
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return Fail("The plan was not JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start), new JsonDocumentOptions { MaxDepth = MaxDepth });
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return Fail("The plan was not valid JSON.");
        }
    }

    private static ObservabilityPlanParseResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail("The plan was not a JSON object.");
        }

        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return Fail("The plan has no rows.");
        }

        var unreadable = 0;
        var buckets = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var byName = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var seen = 0;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                Name(row, "bucket") is not { Length: > 0 } bucket ||
                Name(row, "signal") is not { Length: > 0 } signal ||
                Name(row, "destination") is not { Length: > 0 } destination ||
                Name(row, "decision") is not { Length: > 0 } decision ||
                seen++ >= MaxRows)
            {
                unreadable++;
                continue;
            }

            if (!byName.TryGetValue(destination, out var accumulator))
            {
                if (byName.Count >= MaxDestinations)
                {
                    unreadable++;
                    continue;
                }

                accumulator = new Accumulator();
                byName[destination] = accumulator;
                order.Add(destination);
            }

            if (buckets.Count < MaxBuckets)
            {
                _ = buckets.Add(bucket);
            }

            accumulator.Add(bucket, signal, decision, Name(row, "redaction_profile"), Name(row, "potential_action"));
        }

        if (byName.Count == 0)
        {
            return Fail("The plan names no destination.");
        }

        var limits = ReadLimits(root);
        var destinations = order
            .Select(name =>
            {
                var accumulator = byName[name];
                limits.TryGetValue(name, out var delivery);
                return new PlanDestination(
                    name,
                    delivery.Kind ?? string.Empty,
                    accumulator.Enabled,
                    SignalOrder.Where(accumulator.Signals.Contains).ToArray(),
                    accumulator.Routed.Count,
                    accumulator.Profiles.Order(StringComparer.Ordinal).ToArray(),
                    accumulator.ConditionalSend,
                    delivery.Limits);
            })
            .ToArray();

        return new ObservabilityPlanParseResult(
            new ObservabilityPlan(
                Name(root, "basis") ?? string.Empty,
                root.TryGetProperty("config_version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) ? number : 0,
                Name(root, "plan_digest") ?? string.Empty,
                buckets.Count,
                destinations,
                unreadable),
            string.Empty);
    }

    /// <summary>The queue and batch limits by destination (<c>delivery[]</c>): the first entry for a name wins; one of the wrong shape is skipped.</summary>
    private static Dictionary<string, (string? Kind, DeliveryLimits? Limits)> ReadLimits(JsonElement root)
    {
        var result = new Dictionary<string, (string? Kind, DeliveryLimits? Limits)>(StringComparer.Ordinal);
        if (!root.TryGetProperty("delivery", out var delivery) || delivery.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in delivery.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Name(entry, "destination") is not { Length: > 0 } name || result.ContainsKey(name) || result.Count >= MaxDestinations)
            {
                continue;
            }

            var limits = new DeliveryLimits(
                Count(entry, "max_queue_size", 1),
                Count(entry, "max_queue_bytes", 1),
                Count(entry, "max_export_batch_size", 1),
                Count(entry, "max_export_batch_bytes", 1),
                Count(entry, "scheduled_delay_ms", 0));
            result[name] = (Name(entry, "kind"), limits.IsEmpty ? null : limits);
        }

        return result;
    }

    /// <summary>
    /// A destination, bucket or profile name as this app keeps and shows it: trimmed, with every control and bidirectional character written out
    /// (<see cref="DisplayNames.Visible"/>). The plan, <c>/health</c> and config.yaml all name a destination, and they are merged on this form of
    /// the name, so a name with an odd character in it is the same destination in all three.
    /// </summary>
    public static string CleanName(string? text) => string.IsNullOrEmpty(text) ? string.Empty : DisplayNames.Visible(text).Trim();

    /// <summary>A string member as <see cref="CleanName"/>; null when it is not a string, or too long to be a name.</summary>
    private static string? Name(JsonElement parent, string member)
    {
        if (!parent.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (text is null || text.Length > MaxNameLength * 2)
        {
            return null;
        }

        var visible = CleanName(text);
        return visible.Length > MaxNameLength ? null : visible;
    }

    private static long? Count(JsonElement parent, string member, long minimum) =>
        parent.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum
            ? number
            : null;

    private static ObservabilityPlanParseResult Fail(string error) => new(null, error);

    /// <summary>What the rows of one destination add up to.</summary>
    private sealed class Accumulator
    {
        public bool Enabled { get; private set; }

        public bool ConditionalSend { get; private set; }

        public HashSet<string> Signals { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Routed { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Profiles { get; } = new(StringComparer.Ordinal);

        public void Add(string bucket, string signal, string decision, string? profile, string? potentialAction)
        {
            if (decision == "destination_disabled")
            {
                return;
            }

            // Any other decision means the destination is on. Only "signal_not_selected" says the signal is not one it takes: "not_collected",
            // "unmatched" and the rest are decisions about a signal it does take.
            Enabled = true;
            if (decision != "signal_not_selected")
            {
                _ = Signals.Add(signal);
            }

            switch (decision)
            {
                case "send":
                    _ = Routed.Add(bucket);
                    if (!string.IsNullOrEmpty(profile))
                    {
                        _ = Profiles.Add(profile);
                    }

                    break;

                case "conditional":
                    // The plan could not settle the route without the event's severity, source or connector; what it names is what the route
                    // would do. The mandatory local floor (floor_only) is the local store's own qualification and its profile is the store's.
                    if (potentialAction is null or "" or "send")
                    {
                        _ = Routed.Add(bucket);
                        ConditionalSend = true;
                    }
                    else if (potentialAction == "floor_only")
                    {
                        _ = Routed.Add(bucket);
                    }

                    break;
            }
        }
    }
}
