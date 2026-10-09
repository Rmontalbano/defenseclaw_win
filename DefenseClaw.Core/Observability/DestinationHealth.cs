using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Observability;

/// <summary>
/// What the gateway says one destination is doing now (one entry of <c>/health</c> <c>telemetry.details.destinations[]</c>), reduced to the parts that
/// are safe to show: closed tokens (a state, a reason, an error class), counters and timestamps. Never free text.
/// </summary>
/// <param name="Name">The destination's name, as <see cref="ObservabilityPlanParser.CleanName"/> writes it.</param>
/// <param name="State">The health state (<c>healthy</c>, <c>degraded</c>, <c>failing</c>…) when the gateway names one that is a plain token; else empty.</param>
/// <param name="Reason">Why it is in that state (<c>activated</c>, <c>retrying</c>…), a plain token; else empty.</param>
/// <param name="FailureClass">The class of its last failure (<c>timeout</c>, <c>tls</c>…), a plain token; <c>details_redacted</c> when the gateway kept only that there was an error; else empty.</param>
/// <param name="LastSuccess">When it last delivered, if the gateway says.</param>
/// <param name="LastFailure">When it last failed, if the gateway says.</param>
public sealed record DestinationHealth(
    string Name,
    string State,
    string Reason,
    string FailureClass,
    long? QueueItems,
    long? QueueBytes,
    long? QueueMaxItems,
    long? QueueMaxBytes,
    long? Dropped,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure)
{
    /// <summary>The word for a field the gateway did not report.</summary>
    public const string Unavailable = "unavailable";

    /// <summary><c>0/2048 items, 0 B/64.0 MiB, 0 dropped</c> (<c>V8DestinationHealth.queue_label</c>); <c>unavailable</c> when the gateway reports none of it.</summary>
    public string QueueLabel
    {
        get
        {
            var parts = new List<string>();
            if (QueueItems is { } items)
            {
                parts.Add(items.ToString(CultureInfo.InvariantCulture) + (QueueMaxItems is { } maxItems ? "/" + maxItems.ToString(CultureInfo.InvariantCulture) + " items" : " items"));
            }

            if (QueueBytes is { } bytes)
            {
                parts.Add(ObservabilityFormat.Bytes(bytes) + (QueueMaxBytes is { } maxBytes ? "/" + ObservabilityFormat.Bytes(maxBytes) : string.Empty));
            }

            if (Dropped is { } dropped)
            {
                parts.Add(dropped.ToString(CultureInfo.InvariantCulture) + " dropped");
            }

            return parts.Count == 0 ? Unavailable : string.Join(", ", parts);
        }
    }

    /// <summary>True when the last failure is the newer of the two results (or there is a failure and no success): the destination's last word was an error.</summary>
    public bool LastResultFailed =>
        (LastFailure is { } failure && (LastSuccess is not { } success || failure >= success)) ||
        (LastFailure is null && LastSuccess is null && FailureClass.Length > 0);

    /// <summary>
    /// <c>ok 14:03:07; error 13:58:02 (timeout)</c> (<c>V8DestinationHealth.activity_label</c>), the times as <paramref name="format"/> writes them;
    /// <c>unavailable</c> when the gateway reports neither.
    /// </summary>
    public string LastResultLabel(Func<DateTimeOffset, string> format)
    {
        ArgumentNullException.ThrowIfNull(format);

        var parts = new List<string>();
        if (LastSuccess is { } success)
        {
            parts.Add("ok " + format(success));
        }

        if (LastFailure is { } failure)
        {
            parts.Add("error " + format(failure) + (FailureClass.Length > 0 ? " (" + FailureClass + ")" : string.Empty));
        }
        else if (FailureClass.Length > 0)
        {
            parts.Add("error " + FailureClass);
        }

        return parts.Count == 0 ? Unavailable : string.Join("; ", parts);
    }
}

/// <summary>The retention reaper's state as the gateway reports it (<c>/health</c> <c>telemetry.details.retention_*</c>).</summary>
/// <param name="State"><c>healthy</c>, <c>waiting_for_readiness</c>, <c>degraded</c>, <c>disabled</c> or <c>stopped</c>; empty when the gateway names none of them.</param>
/// <param name="Failure">The reaper's failure code, a plain token; empty when there is none.</param>
/// <param name="Days">The retention window the gateway runs with; 0 is without limit.</param>
public sealed record RetentionHealth(string State, string Failure, long? Days)
{
    /// <summary>Nothing reported.</summary>
    public static RetentionHealth None { get; } = new(string.Empty, string.Empty, null);

    /// <summary><c>healthy</c>, <c>degraded (sqlite_busy)</c>, or <c>unavailable</c> (<c>storage.retention_health or "unavailable"</c> in the TUI).</summary>
    public string ControllerLabel => State.Length == 0 ? DestinationHealth.Unavailable : Failure.Length > 0 ? State + " (" + Failure + ")" : State;

    /// <summary>True for a reaper that is not doing its job: degraded or stopped (the doctor warns for both).</summary>
    public bool NeedsAttention => State is "degraded" or "stopped";
}

/// <summary>
/// Reads the gateway's <c>/health</c> telemetry block the way the TUI does (<c>destination_health_from_gateway</c>, <c>retention_health_from_gateway</c>):
/// only closed tokens, counters and valid timestamps cross over. A free-text <c>last_error</c>, a header, a response body or an endpoint is
/// ignored, because the gateway builds those from the failing request and they can carry the credential the request used. Pure; never throws.
/// </summary>
public static partial class DestinationHealthReader
{
    private static readonly string[] FailureClassKeys =
    {
        "last_error_class", "last_error_code", "error_code", "failure", "warning", "last_failure_class", "last_failure_code",
    };

    private static readonly string[] RetentionStates = { "waiting_for_readiness", "healthy", "degraded", "disabled", "stopped" };

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,127}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PlainToken();

    [GeneratedRegex(@"^(?<second>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?(?<zone>Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Rfc3339();

    /// <summary>One destination of <c>telemetry.details.destinations[]</c>; null for an entry that is not an object or has no name.</summary>
    public static DestinationHealth? Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = ObservabilityPlanParser.CleanName(Text(item, "name") ?? Text(item, "destination"));
        if (name.Length == 0 || name.Length > ObservabilityPlanParser.MaxNameLength)
        {
            return null;
        }

        var queue = Object(item, "queue");
        var counters = Object(item, "counters");
        var delivery = Object(item, "delivery");

        var failureClass = string.Empty;
        foreach (var key in FailureClassKeys)
        {
            failureClass = Token(item, key);
            if (failureClass.Length > 0)
            {
                break;
            }
        }

        // The older delivery block keeps the last error as free text: only the fact that it failed, and when, crosses over.
        var deliveryFailed = delivery is { } d && Text(d, "last_error") is { } lastError && !string.IsNullOrWhiteSpace(lastError);
        if (failureClass.Length == 0 && deliveryFailed)
        {
            failureClass = "details_redacted";
        }

        var lastSuccess = Time(item, "last_success") ?? Time(item, "last_success_at") ?? (delivery is { } s ? Time(s, "last_success_at") : null);
        var lastFailure = Time(item, "last_failure") ?? Time(item, "last_failure_at");
        if (lastFailure is null && deliveryFailed && delivery is { } f)
        {
            lastFailure = Time(f, "last_attempt_at");
        }

        var state = Token(item, "health_state");
        if (state.Length == 0)
        {
            state = Token(item, "state");
        }

        return new DestinationHealth(
            name,
            state,
            Token(item, "reason"),
            failureClass,
            Count(queue, "items") ?? Count(item, "queue_items"),
            Count(queue, "bytes") ?? Count(item, "queue_bytes"),
            Count(queue, "max_items") ?? Count(item, "queue_max_items") ?? Count(item, "max_queue_items"),
            Count(queue, "max_bytes") ?? Count(item, "queue_max_bytes") ?? Count(item, "max_queue_bytes"),
            Count(queue, "dropped") ?? Count(item, "queue_dropped") ?? Count(counters, "dropped"),
            lastSuccess,
            lastFailure);
    }

    /// <summary>The retention reaper's state from <c>telemetry.details</c>; <see cref="RetentionHealth.None"/> when that block is absent.</summary>
    public static RetentionHealth ReadRetention(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object)
        {
            return RetentionHealth.None;
        }

        var state = Token(details, "retention_state");
        var days = Count(details, "retention_days");
        if (!RetentionStates.Contains(state, StringComparer.Ordinal))
        {
            return new RetentionHealth(string.Empty, string.Empty, days);
        }

        var failure = Token(details, "retention_failure");
        if (failure.Length == 0)
        {
            failure = Token(details, "failure");
        }

        return new RetentionHealth(state, failure, days);
    }

    /// <summary>A member that is a short lower-case token (<c>healthy</c>, <c>sqlite_write_failed</c>); empty for anything else, free text included.</summary>
    private static string Token(JsonElement? parent, string member)
    {
        if (parent is not { } element || !element.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var token = (value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        return PlainToken().IsMatch(token) ? token : string.Empty;
    }

    private static string? Text(JsonElement? parent, string member) =>
        parent is { } element && element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement? Object(JsonElement parent, string member) =>
        parent.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static long? Count(JsonElement? parent, string member) =>
        parent is { } element && element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
            ? number
            : null;

    /// <summary>A timestamp in the form Go's RFC 3339 prints (up to nine fractional digits); null for any other text.</summary>
    private static DateTimeOffset? Time(JsonElement? parent, string member)
    {
        if (Text(parent, member) is not { Length: > 0 and <= 64 } text)
        {
            return null;
        }

        var match = Rfc3339().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"].Value : string.Empty;
        var normalised = match.Groups["second"].Value + "." + fraction.PadRight(7, '0')[..7] + match.Groups["zone"].Value;
        return DateTimeOffset.TryParseExact(normalised, "yyyy-MM-dd'T'HH:mm:ss.fffffffK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Year > 1
            ? parsed
            : null;
    }
}
