using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// One surface of a connector in <c>/health</c> (<c>connectors[].surfaces.&lt;name&gt;</c>, runtime 1.0.8; internal/gateway/health.go
/// <c>ConnectorSurfaceHealth</c>): how many calls the caller labelled with that surface since the gateway started, and when the newest one and the
/// newest session start were. Claude Cowork is the surface <c>cowork</c> of the <c>claudecode</c> connector.
/// <para>
/// These counters are caller-reported labels, not proof of coverage: the hook sets the <c>X-DefenseClaw-Cowork</c> header, the gateway only counts it, and
/// it never uses it as a policy input or an identity. They are absent until the first labelled call and restart from zero with the gateway.
/// </para>
/// </summary>
/// <param name="Requests">Calls labelled with the surface since the gateway started.</param>
/// <param name="LastActivityAt">The newest labelled call; null when the gateway sent none or one that is not a time.</param>
/// <param name="LastSessionStartAt">The newest labelled session start; null likewise.</param>
public sealed record ConnectorSurfaceHealth(long Requests, DateTimeOffset? LastActivityAt, DateTimeOffset? LastSessionStartAt)
{
    /// <summary>The surface name of Claude Cowork (internal/gateway/connector/cowork.go <c>CoworkSurface</c>).</summary>
    public const string CoworkSurface = "cowork";

    /// <summary>
    /// Reads the <c>surfaces</c> member of a connector block, tolerantly: anything that is not an object of objects reads as no surfaces, a member of
    /// the wrong type reads as zero or null, and none of it is an error, so a malformed block can never fail the whole <c>/health</c> parse.
    /// </summary>
    internal static IReadOnlyDictionary<string, ConnectorSurfaceHealth> Read(IReadOnlyDictionary<string, JsonElement>? additionalData)
    {
        var found = new Dictionary<string, ConnectorSurfaceHealth>(StringComparer.OrdinalIgnoreCase);
        if (additionalData is null ||
            !additionalData.TryGetValue("surfaces", out var surfaces) ||
            surfaces.ValueKind != JsonValueKind.Object)
        {
            return found;
        }

        foreach (var entry in surfaces.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            found[entry.Name.Trim()] = new ConnectorSurfaceHealth(
                Whole(entry.Value, "requests"),
                Stamp(entry.Value, "last_activity_at"),
                Stamp(entry.Value, "last_session_start_at"));
        }

        return found;
    }

    private static long Whole(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (value.TryGetInt64(out var integer))
        {
            return Math.Max(integer, 0);
        }

        return value.TryGetDouble(out var real) && real >= 0 && real < long.MaxValue ? (long)Math.Floor(real) : 0;
    }

    private static DateTimeOffset? Stamp(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
            ? stamp
            : null;
}
