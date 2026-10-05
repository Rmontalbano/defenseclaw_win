using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One telemetry destination or audit sink of the Observability card: where events go, what kind of place it is, whether it is delivering,
/// and which signals it takes. A record so an unchanged row keeps its visuals across a poll.
/// </summary>
public sealed record ObservabilityRow
{
    public required string Name { get; init; }

    /// <summary><c>otel</c> for a telemetry destination, <c>audit_sinks</c> for an audit sink, the Mac's words.</summary>
    public string Target { get; init; } = "otel";

    /// <summary>The destination's kind or preset: <c>sqlite</c>, <c>otlp</c>, <c>splunk_hec</c>…</summary>
    public string Kind { get; init; } = "—";

    /// <summary>What the gateway reports: <c>healthy</c>, <c>enabled</c>, <c>disabled</c>, <c>error</c>…</summary>
    public string State { get; init; } = "unknown";

    /// <summary>Ok / Warn / Bad / Neutral.</summary>
    public string StateKey { get; init; } = "Neutral";

    public string Signals { get; init; } = "none";

    /// <summary>Delivery counters and the reason the destination is in its state, for the tooltip: <c>activated · 0 accepted · 0 delivered</c>.</summary>
    public string Detail { get; init; } = string.Empty;

    public override string ToString() => ServiceRow.JoinSentences($"{Name}, {Target} destination", Kind, State, $"signals {Signals}", Detail);
}

/// <summary>
/// The Observability card (CUST-209): every destination and sink <c>/health</c> reports under <c>telemetry.details.destinations[]</c> (and
/// <c>sinks.details.sinks[]</c> on a build that has them), plus the gateway's own admission that it cannot keep its event history
/// (<c>event_history_failure</c>, for instance <c>sqlite_write_failed</c>), which the Services box used to hide behind a bare "error".
/// Re-derived from the snapshot on every poll; no I/O.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>Where a <c>/health</c> build puts the failure: inside the telemetry details, beside them, or at the top level.</summary>
    private const string EventHistoryFailureKey = "event_history_failure";

    public ObservableCollection<ObservabilityRow> ObservabilityRows { get; } = new();

    /// <summary>The event-history failure the gateway reports, or empty; shown as a warning above the table.</summary>
    [ObservableProperty]
    private string _eventHistoryFailure = string.Empty;

    [ObservableProperty]
    private bool _hasEventHistoryFailure;

    [ObservableProperty]
    private string _observabilityEmptyText = "Nothing has been read from the gateway yet.";

    [ObservableProperty]
    private bool _hasObservabilityRows;

    private void BuildObservability(GatewayHealth? health)
    {
        var rows = new List<ObservabilityRow>();
        string? failure = null;

        if (health is null)
        {
            ObservabilityEmptyText = "The gateway is not answering, so its destinations cannot be listed.";
        }
        else
        {
            if (health.Telemetry?.Details is { ValueKind: JsonValueKind.Object } telemetry)
            {
                foreach (var item in Items(telemetry, "destinations"))
                {
                    if (Destination(item) is { } row)
                    {
                        rows.Add(row);
                    }
                }
            }

            if (Service(health, "sinks")?.Details is { ValueKind: JsonValueKind.Object } sinks)
            {
                foreach (var item in Items(sinks, "sinks"))
                {
                    if (Sink(item) is { } row)
                    {
                        rows.Add(row);
                    }
                }
            }

            failure = FindFailure(health);
            ObservabilityEmptyText = "No runtime-loaded destinations. Configure one in Setup, then restart the gateway.";
        }

        SyncByEquality(ObservabilityRows, rows, static row => row.Target + "/" + row.Name);
        HasObservabilityRows = rows.Count > 0;
        EventHistoryFailure = failure ?? string.Empty;
        HasEventHistoryFailure = failure is not null;
    }

    private static ObservabilityRow? Destination(JsonElement item)
    {
        if (Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var enabled = item.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        var state = Text(item, "state");
        var stateText = state.Length > 0 ? state : enabled ? "enabled" : "disabled";

        return new ObservabilityRow
        {
            Name = string.Equals(name, "galileo", StringComparison.OrdinalIgnoreCase) ? "Galileo" : name,
            Target = "otel",
            Kind = First(Text(item, "preset"), Text(item, "kind")) ?? "otlp",
            State = stateText,
            StateKey = ObservabilityTone(stateText),
            Signals = SignalsOf(item),
            Detail = DetailOf(item),
        };
    }

    private static ObservabilityRow? Sink(JsonElement item)
    {
        if (Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var enabled = item.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        var state = Text(item, "state");
        var stateText = state.Length > 0 ? state : enabled ? "enabled" : "disabled";

        return new ObservabilityRow
        {
            Name = name,
            Target = "audit_sinks",
            Kind = First(Text(item, "kind"), Text(item, "preset")) ?? "unknown",
            State = stateText,
            StateKey = ObservabilityTone(stateText),
            Signals = "audit-events",
            Detail = DetailOf(item),
        };
    }

    /// <summary>The tone of a destination's state: green for one that is delivering, red for one that is failing, amber for one that is in between, grey for one that is off.</summary>
    internal static string ObservabilityTone(string state) => state.Trim().ToUpperInvariant() switch
    {
        "HEALTHY" or "ENABLED" or "OK" or "RUNNING" or "ACTIVE" => "Ok",
        "ERROR" or "FAILED" or "FAILING" or "UNHEALTHY" => "Bad",
        "DEGRADED" or "PENDING" or "STARTING" or "RETRYING" => "Warn",
        _ => "Neutral",
    };

    /// <summary>The signals a destination takes: a JSON array or a string, joined; <c>none</c> when there are none.</summary>
    private static string SignalsOf(JsonElement item)
    {
        if (!item.TryGetProperty("signals", out var signals))
        {
            return "none";
        }

        var text = signals.ValueKind switch
        {
            JsonValueKind.Array => string.Join(", ", signals.EnumerateArray().Where(static s => s.ValueKind == JsonValueKind.String).Select(static s => s.GetString()).Where(static s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.String => signals.GetString() ?? string.Empty,
            _ => string.Empty,
        };

        return text.Length == 0 ? "none" : text;
    }

    /// <summary>The reason and the delivery counters, when there are any: what the gateway says about how it is going.</summary>
    private static string DetailOf(JsonElement item)
    {
        var parts = new List<string>();
        if (Text(item, "reason") is { Length: > 0 } reason)
        {
            parts.Add(reason);
        }

        if (Text(item, "last_error") is { Length: > 0 } error)
        {
            parts.Add("last error: " + error);
        }

        if (item.TryGetProperty("counters", out var counters) && counters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in counters.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
                {
                    parts.Add($"{value.ToString("N0", CultureInfo.CurrentCulture)} {property.Name}");
                }
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The event-history failure, if the gateway reports one: a string (<c>sqlite_write_failed</c>) or an object whose members are listed. Looked
    /// for where the builds put it (the telemetry details, the telemetry block, the top level); null when there is none.
    /// </summary>
    internal static string? FindFailure(GatewayHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        JsonElement? found = null;
        if (health.Telemetry?.Details is { ValueKind: JsonValueKind.Object } details && details.TryGetProperty(EventHistoryFailureKey, out var inDetails))
        {
            found = inDetails;
        }
        else if (health.Telemetry?.AdditionalData is { } block && block.TryGetValue(EventHistoryFailureKey, out var inBlock))
        {
            found = inBlock;
        }
        else if (health.AdditionalData is { } top && top.TryGetValue(EventHistoryFailureKey, out var inTop))
        {
            found = inTop;
        }

        return found is { } value ? Describe(value) : null;
    }

    private static string? Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
        JsonValueKind.Object => DescribeObject(value),
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => null,
        JsonValueKind.True => "reported",
        _ => value.ToString(),
    };

    private static string? DescribeObject(JsonElement value)
    {
        var parts = value.EnumerateObject()
            .Where(static p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .Select(static p => p.Value.ValueKind == JsonValueKind.String ? (p.Name is "reason" or "error" or "code" or "message" ? p.Value.GetString() : $"{p.Name}: {p.Value.GetString()}") : $"{p.Name}: {p.Value}")
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static ServiceState? Service(GatewayHealth health, string key)
    {
        if (health.AdditionalData is not { } extra || !extra.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServiceState>(element.GetRawText());
        }
        catch (JsonException)
        {
            // Keys the health model does not know stay untyped until here, so a block of the wrong shape ("state": 5, "since": "yesterday")
            // is first parsed in this method. Treat it as absent: letting it throw would abort Apply before it re-derives the gateway
            // action, the enforcement cards and the doctor card, and the poll's refresh that follows Apply.
            return null;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object)
            : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static string? First(params string[] candidates) => candidates.FirstOrDefault(static c => c.Length > 0);
}
