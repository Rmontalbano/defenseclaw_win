using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// One route-only connector of <c>/health</c>'s <c>routing.details.route_only[]</c>: whether the gateway can route that connector's model calls, and when
/// it cannot, the closed <c>cause</c> code with the runtime's own <c>problem</c> and <c>fix</c> sentences. Every text is display-bounded here, never
/// executed and never used to build a command.
/// </summary>
/// <param name="Connector">The connector's name (<c>codex</c>), bounded.</param>
/// <param name="Ready">The runtime's own <c>ready</c> flag; a missing or non-boolean flag is not ready.</param>
/// <param name="Cause">The closed cause code (<c>upstream_not_set</c>); empty when the runtime sends none.</param>
/// <param name="Problem">What is wrong, in the runtime's words; empty when it says nothing.</param>
/// <param name="Fix">What to do about it, in the runtime's words; empty when it says nothing.</param>
/// <param name="Warning">A closed code for a connector that is ready but refuses some callers; empty when none.</param>
public sealed record RouteOnlyState(string Connector, bool Ready, string Cause, string Problem, string Fix, string Warning)
{
    /// <summary>
    /// <c>ready</c>, or <c>not ready (upstream_not_set). routing.route_only.upstreams.codex is not set. Fix: set …</c>. The cause, problem and fix are
    /// the runtime's; each part is present only when the runtime sent it.
    /// </summary>
    public string Text
    {
        get
        {
            if (Ready)
            {
                return Warning.Length > 0 ? $"ready ({Warning})" : "ready";
            }

            var parts = new List<string> { Cause.Length > 0 ? $"not ready ({Cause})" : "not ready" };
            if (Problem.Length > 0)
            {
                parts.Add(Problem);
            }

            if (Fix.Length > 0)
            {
                parts.Add("Fix: " + Fix);
            }

            return string.Join(". ", parts);
        }
    }
}

/// <summary>
/// The <c>routing</c> block of <c>/health</c> (DefenseClaw's model routing), as the runtime words it: <c>{state, since, last_error?, details?}</c>
/// where <c>details</c> is <c>{enabled:false}</c> when routing is off and, when it is on, <c>enabled</c>, <c>mode</c>, <c>version</c>,
/// <c>model_count</c>, <c>port?</c> and (1.0.10) <c>classifier</c> and, only when some connector is routed, <c>route_only[]</c>. Everything past <c>state</c> is
/// optional: a block without the 1.0.10 members reads exactly as it did before them.
/// <para>
/// The block is read from <see cref="GatewayHealth.AdditionalData"/> (see <see cref="GatewayHealth.ModelRoutingBlock"/>), so a block of the wrong shape can never
/// fail the whole <c>/health</c> parse. This record is a view over <see cref="ServiceState"/>: the text of one subsystem, bounded for display.
/// </para>
/// </summary>
/// <param name="State"><c>disabled</c>, <c>starting</c>, <c>running</c>, <c>error</c>, <c>stopped</c>; <c>unknown</c> when the block names none.</param>
/// <param name="Enabled"><c>details.enabled</c>; null when the block does not say.</param>
/// <param name="Classifier"><c>details.classifier</c> (<c>rules</c>, <c>systemone</c>, <c>vllm_sr</c>), 1.0.10; empty when absent.</param>
/// <param name="ModelCount"><c>details.model_count</c>; null when absent.</param>
/// <param name="LastError">The runtime's <c>last_error</c>, one line, at most <see cref="MaxTextLength"/> characters.</param>
/// <param name="RouteOnly">The route-only connectors, at most <see cref="MaxRouteOnly"/>; empty when the runtime lists none.</param>
public sealed record RoutingHealth(
    string State,
    bool? Enabled,
    string Classifier,
    int? ModelCount,
    string LastError,
    IReadOnlyList<RouteOnlyState> RouteOnly)
{
    /// <summary>The longest runtime text shown (last error, problem, fix).</summary>
    public const int MaxTextLength = 200;

    /// <summary>The most route-only connectors read.</summary>
    public const int MaxRouteOnly = 8;

    private const int MaxNameLength = 64;

    /// <summary>Routing is off: the runtime says <c>disabled</c>, or says <c>enabled: false</c>.</summary>
    public bool IsOff =>
        string.Equals(State, "disabled", StringComparison.OrdinalIgnoreCase) || Enabled == false;

    /// <summary>The runtime itself reports a problem: state <c>error</c>, or <c>stopped</c> while routing is on.</summary>
    public bool ReportsProblem =>
        !IsOff && (string.Equals(State, "error", StringComparison.OrdinalIgnoreCase) || string.Equals(State, "stopped", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The row's value: <c>disabled</c> (the status command's own word), or <c>&lt;state&gt; · &lt;classifier&gt; · N models</c> with each part present only
    /// when the runtime reported it, and the last error, bounded, at the end.
    /// </summary>
    public string Text
    {
        get
        {
            if (IsOff)
            {
                return "disabled";
            }

            var parts = new List<string> { State };
            if (Classifier.Length > 0)
            {
                parts.Add(Classifier);
            }

            if (ModelCount is { } count)
            {
                parts.Add(count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " model" : " models"));
            }

            if (LastError.Length > 0)
            {
                parts.Add(LastError);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>The routing block of a gateway answer as the runtime words it; null when the gateway sends none (it is a 0.8.10 gateway, or down).</summary>
    public static RoutingHealth? From(GatewayHealth? health) => From(health?.ModelRoutingBlock);

    /// <summary>The routing block in <paramref name="block"/>; null for none.</summary>
    public static RoutingHealth? From(ServiceState? block)
    {
        if (block is null)
        {
            return null;
        }

        var state = string.IsNullOrWhiteSpace(block.State) ? "unknown" : Bound(block.State, MaxNameLength);
        var routeOnly = new List<RouteOnlyState>();
        if (block.Details is { ValueKind: JsonValueKind.Object } details &&
            details.TryGetProperty("route_only", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.EnumerateArray())
            {
                if (routeOnly.Count >= MaxRouteOnly)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object || StringOf(item, "connector", MaxNameLength) is not { Length: > 0 } name || !seen.Add(name))
                {
                    continue;
                }

                routeOnly.Add(new RouteOnlyState(
                    name,
                    item.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True,
                    StringOf(item, "cause", MaxNameLength),
                    StringOf(item, "problem", MaxTextLength),
                    StringOf(item, "fix", MaxTextLength),
                    StringOf(item, "warning", MaxNameLength)));
            }
        }

        int? modelCount = null;
        if (block.Details is { ValueKind: JsonValueKind.Object } all && all.TryGetProperty("model_count", out var counted) &&
            counted.ValueKind == JsonValueKind.Number && counted.TryGetInt32(out var parsed) && parsed >= 0)
        {
            modelCount = parsed;
        }

        return new RoutingHealth(
            state,
            block.DetailBool("enabled"),
            Bound(block.DetailString("classifier"), MaxNameLength),
            modelCount,
            Bound(block.LastError, MaxTextLength),
            routeOnly);
    }

    /// <summary>
    /// Reads <c>routing</c> out of a health answer's extension data as a <see cref="ServiceState"/>; null when absent, not an object, or not readable as one.
    /// </summary>
    public static ServiceState? ReadBlock(IReadOnlyDictionary<string, JsonElement>? additionalData)
    {
        if (additionalData is null || !additionalData.TryGetValue("routing", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize<ServiceState>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="text"/> as one line of at most <paramref name="limit"/> characters: control characters written out, credentials masked, and
    /// "…" where it was cut. Empty for null or blank.
    /// </summary>
    internal static string Bound(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var visible = DisplayNames.Visible(text.Trim());
        var shown = DisplayRedaction.Prose(visible, limit);
        return visible.Length > limit ? shown.TrimEnd() + "…" : shown;
    }

    private static string StringOf(JsonElement item, string name, int limit) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Bound(value.GetString(), limit) : string.Empty;
}
