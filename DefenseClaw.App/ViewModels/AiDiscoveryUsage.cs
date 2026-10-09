using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>How a read of <c>GET /api/v1/ai-usage</c> ended.</summary>
public enum AiUsageReadStatus
{
    /// <summary>An AI usage report arrived (it may say that discovery is off).</summary>
    Ok,

    /// <summary>The gateway has no such route (HTTP 404 or 405). A limit of the version, not a fault.</summary>
    Unsupported,

    /// <summary>Nothing answered.</summary>
    Unreachable,

    /// <summary>The gateway refused the app's token.</summary>
    Unauthorized,

    /// <summary>The gateway answered that the subsystem is not wired up.</summary>
    NotConnected,

    /// <summary>The answer is longer than the app reads (<see cref="AiUsageReader.MaxBytes"/>); none of it was kept.</summary>
    TooLarge,

    /// <summary>It answered, and the answer is not an AI usage report (not JSON, not an object, or no <c>enabled</c> flag).</summary>
    Malformed,

    /// <summary>Any other failure (a 5xx, an error envelope).</summary>
    Failed,
}

/// <summary>
/// What the gateway's <c>GET /api/v1/ai-usage</c> said: whether AI discovery is running, whether the running gateway looks model lineage up
/// online, and the signals of its last scan. The signals are read exactly as the state file's are
/// (<see cref="DiscoverySignalParser.FromState"/>): the route answers with the same <c>AISignal</c> the scanner persists.
/// </summary>
/// <param name="Enabled">The gateway's AI discovery service is running.</param>
/// <param name="LookupModelProvenanceOnline">
/// <c>lookup_model_provenance_online</c>: the running gateway asks a public model hub about model names it finds. Null when the answer does
/// not say - 0.8.10 does not - which is not "offline"; the panel shows the diagnostic only when it has the fact.
/// </param>
/// <param name="ScannedAt">When the last scan finished (<c>summary.scanned_at</c>).</param>
/// <param name="Signals">The signals read, at most <see cref="AiUsageReader.MaxSignals"/>.</param>
/// <param name="SignalsNotRead">Signals past that limit.</param>
/// <param name="UnreadableEntries">Elements of <c>signals</c> that were not objects.</param>
public sealed record AiUsageSnapshot(
    bool Enabled,
    bool? LookupModelProvenanceOnline,
    DateTimeOffset? ScannedAt,
    IReadOnlyList<DiscoverySignalRecord> Signals,
    int SignalsNotRead,
    int UnreadableEntries);

/// <summary>The outcome of one read: a snapshot, or the sentence that says why there is none.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Snapshot">Set exactly when <paramref name="Status"/> is <see cref="AiUsageReadStatus.Ok"/>.</param>
/// <param name="Message">One sentence for the operator; never carries the bearer token, display-redacted.</param>
public sealed record AiUsageRead(AiUsageReadStatus Status, AiUsageSnapshot? Snapshot, string Message)
{
    public bool IsOk => Snapshot is not null;
}

/// <summary>
/// Reads the gateway's <c>GET /api/v1/ai-usage</c> into an <see cref="AiUsageSnapshot"/>. Pure: no I/O.
/// <para>
/// <b>Shape.</b> <c>handleAIUsage</c> in <c>internal/gateway/ai_usage.go</c> of DefenseClaw source commit 95159fd: a disabled service
/// answers <c>{"enabled":false,"lookup_model_provenance_online":false,"signals":[],"summary":{"result":"disabled"}}</c>; an enabled one
/// answers <c>enabled</c>, <c>lookup_model_provenance_online</c>, <c>summary</c> (<c>AIDiscoverySummary</c>) and <c>signals</c>
/// (<c>AISignal</c>, per-signal identity and presence scores added). 0.8.10 answers the same without <c>lookup_model_provenance_online</c> and
/// without the model fields a newer runtime adds. The one member always sent is <c>enabled</c>: an answer without it is
/// <see cref="AiUsageReadStatus.Malformed"/>, never "off".
/// </para>
/// <para>
/// <b>Bounded.</b> The body is read by <see cref="GatewayClient.GetBoundedJsonAsync"/> to at most <see cref="MaxBytes"/> (the Mac's own
/// limit), and at most <see cref="MaxSignals"/> signals are parsed; the snapshot says how many it left out. Nothing is polled: the panel
/// asks once per load.
/// </para>
/// </summary>
internal static class AiUsageReader
{
    /// <summary>The route, relative to the gateway's base address (authenticated).</summary>
    public const string Route = "api/v1/ai-usage";

    /// <summary>The longest answer read: 4 MiB, the Mac's <c>GatewayClient.maximumResponseBytes</c>. A report of thousands of signals is about a megabyte.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>Most signals parsed from one answer. The runtime's own cap for an external report is 4,096; its local state can hold more.</summary>
    public const int MaxSignals = 8192;

    /// <summary>Turns the gateway client's answer into a read result. The document is not disposed here.</summary>
    public static AiUsageRead FromGateway(GatewayResult<JsonDocument> result, DiscoveryReadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                return result.Value is { } document
                    ? Parse(document.RootElement, options)
                    : Fail(AiUsageReadStatus.Malformed, "The gateway's answer was empty.");

            case GatewayStatus.Unreachable:
                return Fail(AiUsageReadStatus.Unreachable, "The gateway is not answering.");

            case GatewayStatus.Unauthorized:
                return Fail(AiUsageReadStatus.Unauthorized, "The gateway refused this app's API token.");

            case GatewayStatus.NotConnected:
                return Fail(AiUsageReadStatus.NotConnected, "The gateway is running but its AI discovery service is not wired up.");

            default:
                if (result.HttpStatusCode is 404 or 405)
                {
                    return Fail(
                        AiUsageReadStatus.Unsupported,
                        "This gateway does not serve the AI usage report (HTTP " + result.HttpStatusCode.Value.ToString(CultureInfo.InvariantCulture) + ").");
                }

                if (result.ErrorMessage == GatewayClient.TooLargeMessage(MaxBytes))
                {
                    return Fail(AiUsageReadStatus.TooLarge, "The gateway's answer is larger than 4 MiB, so none of it was read.");
                }

                // A 2xx the client could not use is an answer that is not a report; anything else is the gateway failing.
                return result.HttpStatusCode is >= 200 and <= 299
                    ? Fail(AiUsageReadStatus.Malformed, "The gateway's answer could not be read: " + DisplayRedaction.Prose(result.ErrorMessage, 200))
                    : Fail(
                        AiUsageReadStatus.Failed,
                        result.HttpStatusCode is { } code
                            ? "The gateway returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + "."
                            : "The gateway returned an error.");
        }
    }

    /// <summary>Reads a report from JSON text (fixtures and tests); text that is not JSON is <see cref="AiUsageReadStatus.Malformed"/>.</summary>
    public static AiUsageRead ParseText(string json, DiscoveryReadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, options);
        }
        catch (JsonException)
        {
            return Fail(AiUsageReadStatus.Malformed, "The gateway's answer is not valid JSON.");
        }
    }

    /// <summary>Reads the report in <paramref name="root"/>.</summary>
    public static AiUsageRead Parse(JsonElement root, DiscoveryReadOptions options = default)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Fail(AiUsageReadStatus.Malformed, "The gateway's answer is not an AI usage report: it has no enabled flag. Nothing here is a result.");
        }

        bool? lookup = root.TryGetProperty("lookup_model_provenance_online", out var lookupElement)
            ? lookupElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

        DateTimeOffset? scannedAt = null;
        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object &&
            summary.TryGetProperty("scanned_at", out var scanned) && scanned.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(scanned.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            scannedAt = parsed;
        }

        var signals = new List<DiscoverySignalRecord>();
        var notRead = 0;
        var unreadable = 0;
        if (root.TryGetProperty("signals", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    unreadable++;
                }
                else if (signals.Count >= MaxSignals)
                {
                    notRead++;
                }
                else
                {
                    signals.Add(DiscoverySignalParser.FromState(element, options));
                }
            }
        }

        return new AiUsageRead(
            AiUsageReadStatus.Ok,
            new AiUsageSnapshot(enabled.ValueKind == JsonValueKind.True, lookup, scannedAt, signals, notRead, unreadable),
            string.Empty);
    }

    private static AiUsageRead Fail(AiUsageReadStatus status, string message) => new(status, null, message);
}

/// <summary>
/// Adds what the gateway's report says about each model to the signals the files list - and nothing else. The files (the state file,
/// else <c>inventory.db</c>) stay the list: the cards, the model rows, their states and their evidence are theirs. The gateway's report
/// only fills in the four members a newer runtime puts on a model block (owner, relevance, discovery confidence, lineage), for the signal
/// with the same id and the same model, when the report has them and the file does not or says something else.
/// <para>
/// So a 0.8.10 gateway, whose report has none of those members, changes nothing - not even the order of a list - and a report can add no
/// product, no model and no gone signal (a gone signal exists only in the report of the scan that noticed it, and the panel does not take
/// it). Per-signal identity and presence scores, which the report also has, are left alone for the same reason.
/// </para>
/// </summary>
internal static class DiscoveryUsageOverlay
{
    /// <summary>
    /// <paramref name="files"/>, with the models the report knows more about replaced by copies that have it. <c>Enriched</c> counts those
    /// signals; when it is 0, the list returned is <paramref name="files"/> itself.
    /// </summary>
    public static (IReadOnlyList<DiscoverySignalRecord> Signals, int Enriched) Apply(
        IReadOnlyList<DiscoverySignalRecord> files,
        IReadOnlyList<DiscoverySignalRecord> report)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);

        if (files.Count == 0 || report.Count == 0)
        {
            return (files, 0);
        }

        var known = new Dictionary<string, DiscoveryModelInfo>(StringComparer.Ordinal);
        foreach (var signal in report)
        {
            if (signal.SignalId is { Length: > 0 } id && signal.Model is { } model && HasNewerFields(model) && !known.ContainsKey(id))
            {
                known[id] = model;
            }
        }

        if (known.Count == 0)
        {
            return (files, 0);
        }

        var enriched = 0;
        var result = new List<DiscoverySignalRecord>(files.Count);
        foreach (var signal in files)
        {
            if (signal.SignalId is { Length: > 0 } id && signal.Model is { } model && known.TryGetValue(id, out var live) &&
                DiscoveryModelRow.NormalizeId(model.Id) == DiscoveryModelRow.NormalizeId(live.Id))
            {
                var merged = Merge(model, live);
                if (!merged.Equals(model))
                {
                    result.Add(signal with { Model = merged });
                    enriched++;
                    continue;
                }
            }

            result.Add(signal);
        }

        return enriched == 0 ? (files, 0) : (result, enriched);
    }

    /// <summary>True when the block has any of the four members a newer runtime adds.</summary>
    private static bool HasNewerFields(DiscoveryModelInfo model) => model.HasClassification || model.Provenance is not null;

    /// <summary>The file's block with the report's owner, relevance, confidence and lineage where the report has them (the report is the live answer).</summary>
    private static DiscoveryModelInfo Merge(DiscoveryModelInfo file, DiscoveryModelInfo live) =>
        file with
        {
            OwnerApplication = live.OwnerApplication.Length > 0 ? live.OwnerApplication : file.OwnerApplication,
            Relevance = live.Relevance.Length > 0 ? live.Relevance : file.Relevance,
            DiscoveryConfidence = live.DiscoveryConfidence ?? file.DiscoveryConfidence,
            Provenance = live.Provenance ?? file.Provenance,
        };
}
