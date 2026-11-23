using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Gateway;

/// <summary>How a read of <c>GET /usage</c> ended.</summary>
public enum ModelUsageStatus
{
    /// <summary>The gateway's usage store is on and answered (it may hold no rows).</summary>
    Recorded,

    /// <summary>The gateway answered <c>enabled: false</c>: the usage store is off on the running gateway.</summary>
    StoreOff,

    /// <summary>Nothing usable: the route is missing (404), refused (401/403), failed (5xx), timed out, was too large, or is not a usage report.</summary>
    Unreadable,
}

/// <summary>
/// What the gateway's <c>GET /usage?by=model&amp;since=24h</c> said, summed: calls, and the cost of the calls that have a price.
/// </summary>
/// <param name="Status">How the read ended.</param>
/// <param name="Rows">Rows read (by model; the key <c>other</c> is every model with no price-table entry).</param>
/// <param name="Calls">The calls of every row.</param>
/// <param name="PricedCalls">The calls that have a cost: <c>cost_calls</c> of the rows that carry a <c>cost_usd</c> (never more than the row's calls).</param>
/// <param name="CostUsd">The sum of the <c>cost_usd</c> that rows carry; null when no row carries one - an unpriced row has no cost, never zero.</param>
/// <param name="MaybeMore">The gateway returned as many rows as asked for, so there may be more it did not send.</param>
public sealed record ModelUsageSummary(ModelUsageStatus Status, int Rows, long Calls, long PricedCalls, double? CostUsd, bool MaybeMore)
{
    /// <summary>The gateway could not be read.</summary>
    public static ModelUsageSummary Unreadable { get; } = new(ModelUsageStatus.Unreadable, 0, 0, 0, null, false);

    /// <summary>The gateway's usage store is off.</summary>
    public static ModelUsageSummary Off { get; } = new(ModelUsageStatus.StoreOff, 0, 0, 0, null, false);
}

/// <summary>
/// Reads the gateway's authenticated <c>GET /usage</c> (DefenseClaw 1.0.10 <c>handleUsage</c>, <c>internal/gateway/api_usage.go</c>; mounted behind the
/// gateway's token check like every route but <c>GET /health</c>) into one <see cref="ModelUsageSummary"/>. Pure: no I/O of its own; the caller fetches the body
/// with the bounded JSON read (<see cref="GatewayClient.GetBoundedJsonAsync"/>), which reads at most <see cref="MaxBytes"/>.
/// <para>
/// <b>Bounded.</b> The query asks for at most <see cref="RowLimit"/> rows (the store's default is 200 and its cap 1000); a row is about 250 bytes, so
/// <see cref="MaxBytes"/> holds them all. <b>Shape.</b> <c>{enabled, by, scope, from, to, rows[]}</c>; the store off answers 200
/// <c>{"enabled":false,"hint":…,"rows":[]}</c>. A row is <c>{key, calls, errors?, usage_calls, input_tokens, output_tokens, cache_read_tokens,
/// cache_creation_tokens, reasoning_tokens, cost_usd?, cost_calls, latency_ms_sum}</c>; <c>cost_usd</c> is omitted when no call of the row had a known cost.
/// </para>
/// </summary>
public static class ModelUsageReader
{
    /// <summary>The route and query, relative to the gateway's base address: the last 24 hours rolled up by model.</summary>
    public const string Route = "usage?by=model&since=24h&limit=100";

    /// <summary>The most rows asked for. By model there is one row per price-table entry plus <c>other</c>.</summary>
    public const int RowLimit = 100;

    /// <summary>The longest answer read (64 KiB).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Turns the gateway client's answer into a summary. The document is not disposed here.</summary>
    public static ModelUsageSummary FromGateway(GatewayResult<JsonDocument> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsOk && result.Value is { } document ? Parse(document.RootElement) : ModelUsageSummary.Unreadable;
    }

    /// <summary>Reads a body from text (fixtures and tests); text that is not JSON is <see cref="ModelUsageStatus.Unreadable"/>.</summary>
    public static ModelUsageSummary ParseText(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return ModelUsageSummary.Unreadable;
        }
    }

    /// <summary>Reads the usage report in <paramref name="root"/>.</summary>
    public static ModelUsageSummary Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ModelUsageSummary.Unreadable;
        }

        if (enabled.ValueKind == JsonValueKind.False)
        {
            return ModelUsageSummary.Off;
        }

        var rows = 0;
        var calls = 0L;
        var priced = 0L;
        double? cost = null;
        if (root.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                rows++;
                var rowCalls = Count(row, "calls");
                calls = Add(calls, rowCalls);

                // A priced row carries cost_usd; one that does not has no price, which is not a price of zero.
                if (row.TryGetProperty("cost_usd", out var usd) && usd.ValueKind == JsonValueKind.Number &&
                    usd.TryGetDouble(out var amount) && double.IsFinite(amount) && amount >= 0)
                {
                    cost = (cost ?? 0) + amount;
                    var costCalls = row.TryGetProperty("cost_calls", out _) ? Count(row, "cost_calls") : rowCalls;
                    priced = Add(priced, Math.Min(costCalls, rowCalls));
                }
            }
        }

        return new ModelUsageSummary(ModelUsageStatus.Recorded, rows, calls, priced, cost, rows >= RowLimit);
    }

    /// <summary>The summary in plain words: <c>no usage in the last 24 h</c>; <c>12 calls in the last 24 h, $0.04 (9 of 12 calls priced)</c>; no price table, no cost.</summary>
    public static string Describe(ModelUsageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.Calls <= 0)
        {
            return "no usage in the last 24 h";
        }

        var calls = summary.Calls.ToString(CultureInfo.InvariantCulture);
        var head = $"{(summary.MaybeMore ? "at least " : string.Empty)}{calls} {(summary.Calls == 1 ? "call" : "calls")} in the last 24 h";
        if (summary.CostUsd is not { } cost)
        {
            return head + ", no price table for these models, so no cost";
        }

        var money = cost > 0 && cost < 0.01 ? "under $0.01" : "$" + cost.ToString("F2", CultureInfo.InvariantCulture);
        return $"{head}, {money} ({summary.PricedCalls.ToString(CultureInfo.InvariantCulture)} of {calls} calls priced)";
    }

    private static long Count(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n > 0 ? n : 0;

    private static long Add(long total, long next) => next > long.MaxValue - total ? long.MaxValue : total + next;
}
