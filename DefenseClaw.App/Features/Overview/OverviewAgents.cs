using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One line of the Overview's "Discovered AI agents" card: an agent <c>defenseclaw</c> found on this machine, as the Mac's card
/// (<c>AIOverviewGrouping</c>) prints it. A record so an unchanged row keeps its visuals across a refresh.
/// </summary>
public sealed record DiscoveredAgentRow
{
    /// <summary>The dedupe identity: the connector it maps to, else its component, else its vendor and name.</summary>
    public required string Id { get; init; }

    /// <summary><c>[NEW]</c>, <c>[CHG]</c>, <c>[GONE]</c> or <c>[OK ]</c>, the Mac's state tags.</summary>
    public required string Badge { get; init; }

    /// <summary>The tone key of the badge: Ok for a new agent, Warn for a changed one, Neutral for one that is gone, Medium for the rest.</summary>
    public string BadgeKey { get; init; } = "Medium";

    public required string Name { get; init; }

    /// <summary>The vendor, with the version and the connector it maps to: <c>Anthropic (claudecode)</c>.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Confidence as the Mac prints it: <c>98%</c>.</summary>
    public string Confidence { get; init; } = string.Empty;

    /// <summary>When it was last seen: <c>seen 3m ago</c>.</summary>
    public string Seen { get; init; } = string.Empty;

    /// <summary>The state as a word for a screen reader (the tag is punctuation): new, changed, gone, or nothing for an agent that is simply present.</summary>
    private string StateWord => Badge switch
    {
        "[NEW]" => "new",
        "[CHG]" => "changed",
        "[GONE]" => "gone",
        _ => string.Empty,
    };

    /// <summary>The screen-reader sentence for the row (the record's generated dump is not one).</summary>
    public override string ToString() =>
        ServiceRow.JoinSentences(Name, Vendor, StateWord, Confidence.Length == 0 ? string.Empty : $"{Confidence} confidence", Seen);
}

/// <summary>What the agents card shows: the rows, how many more there were, and the headline counts.</summary>
/// <param name="Rows">The first <see cref="OverviewAgentReader.MaxRows"/> agents, in the Mac's order.</param>
/// <param name="Overflow">How many distinct agents did not fit (<c>+7 more</c>).</param>
/// <param name="Active">Agent signals that are not gone (the Mac's "59 active").</param>
/// <param name="New">Agent signals that are new.</param>
/// <param name="Changed">Agent signals that changed.</param>
/// <param name="Gone">Agent signals that are gone.</param>
/// <param name="UpdatedAt">When the file was last written by the scanner; null when it did not say.</param>
internal sealed record DiscoveredAgents(
    IReadOnlyList<DiscoveredAgentRow> Rows,
    int Overflow,
    int Active,
    int New,
    int Changed,
    int Gone,
    DateTimeOffset? UpdatedAt)
{
    public static DiscoveredAgents None { get; } = new(Array.Empty<DiscoveredAgentRow>(), 0, 0, 0, 0, 0, null);

    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// Reads <c>ai_discovery_state.json</c> for the Overview card, with the Mac's rules (<c>AIOverviewGrouping</c>): only agents (local
/// models belong to the full AI Discovery page and never take one of the eight rows), ordered new, changed, present, gone and then by
/// confidence and recency, one row per connector or component however many signals back it.
/// </summary>
internal static class OverviewAgentReader
{
    /// <summary>The card shows this many and says how many more.</summary>
    public const int MaxRows = 8;

    /// <exception cref="JsonException">Not JSON, or not a JSON object.</exception>
    public static DiscoveredAgents Parse(string json, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ai_discovery_state.json is not a JSON object.");
        }

        var signals = new List<Signal>();
        if (root.TryGetProperty("signals", out var list))
        {
            // The file keys signals by fingerprint (an object); an array of signals is read too.
            var items = list.ValueKind switch
            {
                JsonValueKind.Object => list.EnumerateObject().Select(static p => p.Value),
                JsonValueKind.Array => list.EnumerateArray(),
                _ => Enumerable.Empty<JsonElement>(),
            };

            var order = 0;
            foreach (var item in items)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    signals.Add(ReadSignal(item, order++));
                }
            }
        }

        var agents = signals.Where(static s => !string.Equals(s.Category, "local_model", StringComparison.OrdinalIgnoreCase)).ToList();
        var states = agents.Select(static s => s.State).ToList();

        var ordered = agents
            .OrderBy(static s => StateRank(s.State))
            .ThenByDescending(static s => s.Confidence)
            .ThenByDescending(static s => s.LastSeen ?? DateTimeOffset.MinValue)
            .ThenBy(static s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static s => s.Order)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Signal>();
        foreach (var signal in ordered)
        {
            if (seen.Add(signal.Key))
            {
                unique.Add(signal);
            }
        }

        var rows = unique.Take(MaxRows).Select(s => ToRow(s, now)).ToList();
        return new DiscoveredAgents(
            rows,
            Math.Max(0, unique.Count - MaxRows),
            states.Count(static s => s != "gone"),
            states.Count(static s => s == "new"),
            states.Count(static s => s == "changed"),
            states.Count(static s => s == "gone"),
            ParseTime(Text(root, "updated_at")));
    }

    /// <summary>The Mac's <c>formatScanAge</c>: <c>now</c>, <c>50s ago</c>, <c>3m ago</c>, <c>4h ago</c>, <c>2d ago</c>.</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var delta = now - at;
        if (delta < TimeSpan.Zero)
        {
            return "now";
        }

        var seconds = (long)delta.TotalSeconds;
        return seconds switch
        {
            < 60 => $"{seconds}s ago",
            < 3600 => $"{seconds / 60}m ago",
            < 86400 => $"{seconds / 3600}h ago",
            _ => $"{seconds / 86400}d ago",
        };
    }

    private static DiscoveredAgentRow ToRow(Signal signal, DateTimeOffset now)
    {
        var (badge, key) = signal.State switch
        {
            "new" => ("[NEW]", "Ok"),
            "changed" => ("[CHG]", "Warn"),
            "gone" => ("[GONE]", "Neutral"),
            _ => ("[OK ]", "Medium"),
        };

        return new DiscoveredAgentRow
        {
            Id = signal.Key,
            Badge = badge,
            BadgeKey = key,
            Name = signal.DisplayName,
            Vendor = signal.VendorLabel,
            Confidence = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(signal.Confidence * 100, MidpointRounding.AwayFromZero)}%"),
            Seen = signal.LastSeen is { } at ? $"seen {Age(at, now)}" : "seen -",
        };
    }

    private static int StateRank(string state) => state switch
    {
        "new" => 0,
        "changed" => 1,
        "gone" => 3,
        "" or "active" or "seen" => 2,
        _ => 4,
    };

    private static Signal ReadSignal(JsonElement item, int order)
    {
        var name = Text(item, "name");
        var product = Text(item, "product");
        var signature = Text(item, "signature_id");
        var signalId = Text(item, "signal_id");
        var vendor = Text(item, "vendor");
        var category = Text(item, "category");
        var version = Text(item, "version");
        var connector = Text(item, "supported_connector");
        var ecosystem = Text(item, "ecosystem");
        var component = Text(item, "component");

        var displayName = First(name, product, signature, signalId) ?? "(unknown)";
        var vendorLabel = First(vendor, category) ?? "-";
        if (version.Length > 0)
        {
            vendorLabel += " " + version;
        }

        if (connector.Length > 0)
        {
            vendorLabel += $" ({connector})";
        }

        string key;
        if (connector.Length > 0)
        {
            key = "connector:" + connector.ToLowerInvariant();
        }
        else if (ecosystem.Length > 0 || component.Length > 0)
        {
            key = "component:" + ecosystem.ToLowerInvariant() + "/" + component.ToLowerInvariant();
        }
        else
        {
            key = "display:" + vendorLabel.ToLowerInvariant() + "/" + displayName.ToLowerInvariant();
        }

        var confidence = item.TryGetProperty("confidence", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? Math.Clamp(number, 0, 1)
            : 0;

        return new Signal(
            key,
            category,
            Text(item, "state").ToLowerInvariant(),
            displayName,
            vendorLabel,
            confidence,
            ParseTime(Text(item, "last_seen")),
            order);
    }

    private static string? First(params string[] candidates) => candidates.FirstOrDefault(static c => c.Length > 0);

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static DateTimeOffset? ParseTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private sealed record Signal(
        string Key,
        string Category,
        string State,
        string DisplayName,
        string VendorLabel,
        double Confidence,
        DateTimeOffset? LastSeen,
        int Order);
}
