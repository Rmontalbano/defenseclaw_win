using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>How a read of <c>GET /api/v1/ai-usage/runtime</c> ended.</summary>
public enum AiRuntimeReadStatus
{
    /// <summary>A snapshot arrived and has every field the app relies on.</summary>
    Ok,

    /// <summary>The gateway has no such route (HTTP 404 or 405): a runtime without the planes. A limit of the version, not a fault.</summary>
    Unsupported,

    /// <summary>Nothing answered.</summary>
    Unreachable,

    /// <summary>The gateway refused the app's token.</summary>
    Unauthorized,

    /// <summary>The gateway answered that the subsystem is not wired up.</summary>
    NotConnected,

    /// <summary>It answered, and the answer is not a runtime snapshot (not JSON, or missing <c>enabled</c>, <c>planes</c> or <c>findings</c>).</summary>
    Malformed,

    /// <summary>Any other failure (a 5xx, an error envelope).</summary>
    Failed,
}

/// <summary>The outcome of one read: a snapshot, or the sentence that says why there is none.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Snapshot">Set exactly when <paramref name="Status"/> is <see cref="AiRuntimeReadStatus.Ok"/>.</param>
/// <param name="Message">One sentence for the operator; never carries the bearer token, display-redacted.</param>
public sealed record AiRuntimeRead(AiRuntimeReadStatus Status, AiRuntimeSnapshot? Snapshot, string Message)
{
    /// <summary>True when <see cref="Snapshot"/> is set.</summary>
    public bool IsOk => Status == AiRuntimeReadStatus.Ok;

    /// <summary>
    /// True when the read failed in a way that says nothing about the version: the last good snapshot is kept and marked stale rather
    /// than replaced by an empty one, because an empty list on screen reads as a clean host when it is really a lost connection.
    /// </summary>
    public bool KeepsLastSnapshot => Status is not (AiRuntimeReadStatus.Ok or AiRuntimeReadStatus.Unsupported);
}

/// <summary>
/// Reads the gateway's runtime-plane snapshot into <see cref="AiRuntimeSnapshot"/>. Pure: no I/O.
/// <para>
/// <b>Shape.</b> <c>aiRuntimeResponse</c> in <c>internal/gateway/ai_runtime_api.go</c> of DefenseClaw source commit 95159fd. The three
/// members the gateway always sends - <c>enabled</c>, <c>planes</c>, <c>findings</c> - are required: an answer without them is
/// <see cref="AiRuntimeReadStatus.Malformed"/>, never "no findings". Everything else decodes leniently, the way the macOS companion's
/// does: a field an older gateway leaves out is zero, an element of the wrong kind is skipped and counted
/// (<see cref="AiRuntimeSnapshot.UnreadableEntries"/>), because an app that fails to render on version skew is worse than one that renders
/// less, and one that skips quietly would let a lost entry read as a clean one.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxFindings"/> findings are kept (worst first, unique by id), with at most <see cref="MaxSignals"/>
/// signals and <see cref="MaxProviders"/> providers each, and every string is cut to a limit. The snapshot says how many findings it
/// did not keep.
/// </para>
/// <para>
/// <b>Display redaction.</b> Command lines, reasons and details pass <see cref="Logs.DisplayRedaction"/> (credentials masked, then cut); every
/// string that came from a host's process table also passes <see cref="Text.DisplayNames.Visible"/>, so a process that names itself with a
/// right-to-left override or a newline cannot rearrange what the operator reads.
/// </para>
/// </summary>
public static class AiRuntimeReader
{
    /// <summary>The route, relative to the gateway's base address (authenticated).</summary>
    public const string Route = "api/v1/ai-usage/runtime";

    public const int MaxFindings = 1000;
    public const int MaxPlanes = 16;
    public const int MaxSignals = 64;
    public const int MaxProviders = 64;
    public const int MaxListItems = 32;
    public const int MaxDegradedReasons = 32;

    /// <summary>Longest identifier shown whole (process, user, agent, host, id).</summary>
    public const int NameLimit = AiRuntimeJson.NameLimit;

    /// <summary>Longest command line kept, after masking.</summary>
    public const int CmdlineLimit = 2048;

    /// <summary>Longest reason, detail or title kept, after masking.</summary>
    public const int TextLimit = AiRuntimeJson.TextLimit;

    /// <summary>Turns the gateway client's answer into a read result. The document is not disposed here.</summary>
    public static AiRuntimeRead FromGateway(GatewayResult<JsonDocument> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                return result.Value is { } document
                    ? Parse(document.RootElement)
                    : Fail(AiRuntimeReadStatus.Malformed, "The gateway's answer was empty.");

            case GatewayStatus.Unreachable:
                return Fail(AiRuntimeReadStatus.Unreachable, "The gateway is not answering.");

            case GatewayStatus.Unauthorized:
                return Fail(AiRuntimeReadStatus.Unauthorized, "The gateway refused this app's API token.");

            case GatewayStatus.NotConnected:
                return Fail(AiRuntimeReadStatus.NotConnected, "The gateway is running but the runtime coverage service is not wired up.");

            default:
                if (result.HttpStatusCode is 404 or 405)
                {
                    return Fail(
                        AiRuntimeReadStatus.Unsupported,
                        "This gateway does not serve the runtime planes (HTTP " + result.HttpStatusCode.Value.ToString(CultureInfo.InvariantCulture) + ").");
                }

                // A 2xx the client could not use is an answer that is not a snapshot; anything else is the gateway failing.
                return result.HttpStatusCode is >= 200 and <= 299
                    ? Fail(AiRuntimeReadStatus.Malformed, "The gateway's answer could not be read: " + Prose(result.ErrorMessage, 256))
                    : Fail(
                        AiRuntimeReadStatus.Failed,
                        result.HttpStatusCode is { } code
                            ? "The gateway returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + "."
                            : "The gateway returned an error: " + Prose(result.ErrorMessage, 256));
        }
    }

    /// <summary>Reads a snapshot from JSON text (fixtures and tests); text that is not JSON is <see cref="AiRuntimeReadStatus.Malformed"/>.</summary>
    public static AiRuntimeRead ParseText(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Fail(AiRuntimeReadStatus.Malformed, "The gateway's answer is not valid JSON.");
        }
    }

    /// <summary>Reads the snapshot in <paramref name="root"/>.</summary>
    public static AiRuntimeRead Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(AiRuntimeReadStatus.Malformed, "The runtime answer is not a JSON object.");
        }

        if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("planes", out var planes) || planes.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return Fail(
                AiRuntimeReadStatus.Malformed,
                "The runtime answer is incomplete: it is missing enabled, planes or findings. Nothing here is a clean-host result.");
        }

        var unreadable = 0;

        var planeList = new List<AiRuntimePlane>();
        foreach (var element in planes.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                unreadable++;
                continue;
            }

            if (planeList.Count < MaxPlanes && ReadPlane(element) is { } plane && planeList.All(p => p.Id != plane.Id))
            {
                planeList.Add(plane);
            }
        }

        var raw = new List<AiRuntimeFinding>();
        foreach (var element in findings.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                unreadable++;
                continue;
            }

            raw.Add(ReadFinding(element));
        }

        // Worst first, then the highest score, then by name so the order is the same between polls that score alike. Unique by id
        // (the first, which is the worst, wins), then cut: the cut falls on the least severe.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = raw
            .OrderBy(f => f.SeverityRank)
            .ThenByDescending(f => f.Score)
            .ThenBy(f => f.Process, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Pid)
            .Where(f => seen.Add(f.Id))
            .Take(MaxFindings)
            .ToArray();

        var reasons = new List<string>();
        if (root.TryGetProperty("degraded_reasons", out var reasonList) && reasonList.ValueKind == JsonValueKind.Array)
        {
            foreach (var reason in reasonList.EnumerateArray())
            {
                if (reason.ValueKind == JsonValueKind.String && Prose(reason.GetString(), TextLimit) is { Length: > 0 } line && reasons.Count < MaxDegradedReasons)
                {
                    reasons.Add(line);
                }
            }
        }

        var scannedText = Str(root, "scanned_at");
        var snapshot = new AiRuntimeSnapshot(
            Enabled: enabled.ValueKind == JsonValueKind.True,
            Polled: scannedText.Length > 0,
            ScannedAt: ParseTime(scannedText),
            Planes: planeList,
            Findings: kept,
            FindingsNotShown: raw.Count - kept.Length,
            UnreadableEntries: unreadable,
            ProcessesObserved: Int(root, "processes_observed"),
            ProcessesSkipped: Int(root, "processes_skipped"),
            ConnectionsObserved: Int(root, "connections_observed"),
            ConnectionsUnattributed: Int(root, "connections_unattributed"),
            HostPlaneObservations: Long(root, "host_plane_observations"),
            HostPlaneGated: Long(root, "host_plane_gated"),
            Degraded: root.TryGetProperty("degraded", out var degraded) && degraded.ValueKind == JsonValueKind.True,
            DegradedReasons: reasons);

        return new AiRuntimeRead(AiRuntimeReadStatus.Ok, snapshot, string.Empty);
    }

    private static AiRuntimePlane? ReadPlane(JsonElement element)
    {
        var id = Name(element, "plane");
        var name = Name(element, "name");
        if (id.Length == 0 && name.Length == 0)
        {
            return null;
        }

        return new AiRuntimePlane(
            Id: id.Length > 0 ? id.ToLowerInvariant() : name,
            Name: name.Length > 0 ? name : id,
            Available: Flag(element, "available"),
            Running: Flag(element, "running"),
            Mechanism: Prose(Str(element, "mechanism"), NameLimit),
            Reason: Prose(Str(element, "reason"), TextLimit));
    }

    private static AiRuntimeFinding ReadFinding(JsonElement element)
    {
        var signals = new List<AiRuntimeSignal>();
        var providers = new List<AiRuntimeProvider>();

        if (element.TryGetProperty("signals", out var signalList) && signalList.ValueKind == JsonValueKind.Array)
        {
            foreach (var signal in signalList.EnumerateArray())
            {
                if (signal.ValueKind != JsonValueKind.Object || signals.Count >= MaxSignals)
                {
                    continue;
                }

                var entry = new AiRuntimeSignal(
                    Name(signal, "id"),
                    Prose(Str(signal, "title"), TextLimit),
                    Clean(Str(signal, "detail"), TextLimit),
                    Int(signal, "weight"));
                if (signals.All(s => s.Identity != entry.Identity))
                {
                    signals.Add(entry);
                }
            }
        }

        if (element.TryGetProperty("providers", out var providerList) && providerList.ValueKind == JsonValueKind.Array)
        {
            foreach (var provider in providerList.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object || providers.Count >= MaxProviders)
                {
                    continue;
                }

                var entry = new AiRuntimeProvider(
                    Name(provider, "hostname"),
                    Name(provider, "address"),
                    Int(provider, "port"),
                    Name(provider, "category"),
                    Double(provider, "confidence"),
                    Name(provider, "attribution_source"));

                // Unique on host, address AND port: two ports on one host are two peers.
                if (providers.All(p => p.Identity != entry.Identity))
                {
                    providers.Add(entry);
                }
            }
        }

        var correlation = new AiRuntimeCorrelation(string.Empty, string.Empty, [], []);
        if (element.TryGetProperty("correlation", out var correlationElement) && correlationElement.ValueKind == JsonValueKind.Object)
        {
            correlation = new AiRuntimeCorrelation(
                Name(correlationElement, "verdict"),
                Prose(Str(correlationElement, "reason"), TextLimit),
                Names(correlationElement, "matched_signal_ids"),
                Names(correlationElement, "categories"));
        }

        return new AiRuntimeFinding(
            FindingId: Name(element, "finding_id"),
            Pid: Int(element, "pid"),
            Process: Name(element, "process"),
            Cmdline: Clean(Str(element, "cmdline"), CmdlineLimit),
            User: Name(element, "user"),
            AgentName: Name(element, "agent_name"),
            Score: Int(element, "score"),
            Severity: Name(element, "severity") is { Length: > 0 } severity ? severity : "info",
            Signals: signals,
            Providers: providers,
            Correlation: correlation,
            FirstSeen: ParseTime(Str(element, "first_seen")),
            LastSeen: ParseTime(Str(element, "last_seen")));
    }

    // ---- field helpers: shared with the other runtime-plane readers (AiRuntimeJson) ----------------------------------------

    private static AiRuntimeRead Fail(AiRuntimeReadStatus status, string message) => new(status, null, message);

    private static string Str(JsonElement element, string name) => AiRuntimeJson.Str(element, name);

    private static string Name(JsonElement element, string name) => AiRuntimeJson.Name(element, name);

    private static IReadOnlyList<string> Names(JsonElement element, string name) => AiRuntimeJson.Names(element, name, MaxListItems);

    private static string Clean(string? text, int limit) => AiRuntimeJson.Clean(text, limit);

    private static string Prose(string? text, int limit) => AiRuntimeJson.Prose(text, limit);

    private static bool Flag(JsonElement element, string name) => AiRuntimeJson.Flag(element, name);

    private static int Int(JsonElement element, string name) => AiRuntimeJson.Int(element, name);

    private static long Long(JsonElement element, string name) => AiRuntimeJson.Long(element, name);

    private static double Double(JsonElement element, string name) => AiRuntimeJson.Double(element, name);

    private static DateTimeOffset? ParseTime(string text) => AiRuntimeJson.ParseTime(text);
}
