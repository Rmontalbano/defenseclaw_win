using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Reads when each component in <c>ai_discovery_state.json</c> was first seen (CUST-265), for the AI Discovery sidebar badge.
/// <para>
/// <b>Why <c>first_seen</c> and not <c>state</c>.</b> A signal's <c>state</c> is what changed since the <em>previous scan</em>: <c>new</c> lasts one scan
/// (a minute or a few) and is <c>seen</c> from then on, so "the signals that are <c>new</c> now" says nothing about what arrived since the operator
/// last looked. <c>first_seen</c> is carried forward by the scanner (<c>sig.FirstSeen = old.FirstSeen</c>) and never moves, so a component first
/// seen after the last visit is new until that visit, however many scans have passed. (The installed TUI's own AI tab count is
/// <c>len(snapshot.agents)</c> on an <c>AIUsageSnapshot</c>, which has no such member; it never lights.)
/// </para>
/// <para>
/// <b>What a component is</b>, so the count matches what the panel lists: a local model is one component per model id (the Models view's row,
/// <see cref="DiscoveryModelRow.NormalizeId"/>), everything else one per (vendor, product) without case (the Products view's card). A component's first
/// seen time is the earliest of its signals' that is known. One signal of a long-known product with a new detector does not make the product new.
/// </para>
/// <para>
/// Read from the text a reader already holds; this opens nothing and starts nothing. Tolerant like the Overview's reader of the same file: a value of
/// the wrong shape is skipped, not fatal. Only a text that is not a JSON object throws, as <see cref="JsonException"/>, the way
/// <c>OverviewAgentReader.Parse</c> does for the same input.
/// </para>
/// </summary>
internal static class AiDiscoveryNovelty
{
    private const string UnknownVendor = "(unknown vendor)";
    private const string UnknownProduct = "(unknown product)";

    /// <exception cref="JsonException">Not JSON, or not a JSON object.</exception>
    public static AiDiscoveryHead Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ai_discovery_state.json is not a JSON object.");
        }

        var earliest = new Dictionary<string, long>(StringComparer.Ordinal);
        if (root.TryGetProperty("signals", out var list))
        {
            // The file keys signals by fingerprint (an object); an array of signals is read too, as the Overview's reader does.
            var items = list.ValueKind switch
            {
                JsonValueKind.Object => list.EnumerateObject().Select(static p => p.Value),
                JsonValueKind.Array => list.EnumerateArray(),
                _ => Enumerable.Empty<JsonElement>(),
            };

            foreach (var item in items)
            {
                if (item.ValueKind == JsonValueKind.Object && FirstSeen(item) is { } ticks)
                {
                    var key = KeyOf(item);
                    earliest[key] = earliest.TryGetValue(key, out var known) ? Math.Min(known, ticks) : ticks;
                }
            }
        }

        return earliest.Count == 0 ? AiDiscoveryHead.None : new AiDiscoveryHead(earliest.Values.Order().ToArray());
    }

    /// <summary>The component a signal belongs to: its model, when it is one the Models view lists, else its product.</summary>
    private static string KeyOf(JsonElement signal)
    {
        var category = Text(signal, "category");
        if (string.Equals(category, "local_model", StringComparison.OrdinalIgnoreCase) &&
            signal.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.Object &&
            Text(model, "id") is { Length: > 0 } id)
        {
            return "model:" + DiscoveryModelRow.NormalizeId(id);
        }

        var vendor = Text(signal, "vendor");
        var product = Text(signal, "product");
        return "product:" +
               (vendor.Length == 0 ? UnknownVendor : vendor).ToUpperInvariant() + "/" +
               (product.Length == 0 ? UnknownProduct : product).ToUpperInvariant();
    }

    /// <summary>The instant the signal was first seen, in ticks; null when the file does not say (or says something that is not a time).</summary>
    private static long? FirstSeen(JsonElement signal) =>
        InventoryTimestamps.TryParse(Text(signal, "first_seen"), out var parsed) ? parsed.UtcTicks : null;

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
