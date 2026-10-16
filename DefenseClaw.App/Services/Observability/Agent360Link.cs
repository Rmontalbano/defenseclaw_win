using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Observability;

/// <summary>
/// The time range an Agent360 link opens with, in Grafana's own words: <c>now-6h</c> to <c>now</c> (the dashboard's default), or two epoch-millisecond
/// instants. Anything else a caller hands in is not put on a link (<see cref="TryCreate"/> refuses it), so the link only ever carries a range Grafana reads.
/// </summary>
internal readonly partial record struct Agent360Range(string From, string To)
{
    /// <summary>The dashboard's own default: the last six hours.</summary>
    public static Agent360Range Default { get; } = new("now-6h", "now");

    /// <summary>The last <paramref name="hours"/> hours (1 to 9999), as <c>now-24h</c> to <c>now</c>.</summary>
    public static Agent360Range Last(int hours) =>
        hours is >= 1 and <= 9999 ? new Agent360Range("now-" + hours.ToString(CultureInfo.InvariantCulture) + "h", "now") : Default;

    /// <summary>The range, or null when either end is not a relative <c>now[-N unit]</c> expression or an epoch-millisecond instant.</summary>
    public static Agent360Range? TryCreate(string? from, string? to) =>
        from is not null && to is not null && IsBound(from) && IsBound(to) ? new Agent360Range(from, to) : null;

    private static bool IsBound(string value) => Bound().IsMatch(value);

    [GeneratedRegex(@"^(now(-\d{1,4}[smhdwMy])?|\d{10,13})$", RegexOptions.CultureInvariant)]
    private static partial Regex Bound();
}

/// <summary>
/// Builds the address of the Agent360 dashboard in the local Grafana (<c>defenseclaw-agent-360</c>, shipped by the 1.0.x local observability stack).
/// <para>
/// <b>Only a loopback address the app built.</b> The base comes from <see cref="TryNormalizeBase"/>, which keeps scheme, host and port of an
/// <c>http(s)://localhost | 127.0.0.1 | [::1]</c> address and nothing else (no user name, path, query or fragment), so a link can never leave the
/// machine, and the variables are escaped one by one, so a connector or agent name cannot add a parameter. The variable names are the dashboard's
/// (<c>connector</c>, <c>agent</c>, <c>scope_label</c>), read from its <c>templating</c> section and from the way the Agent identity dashboard links into it.
/// </para>
/// </summary>
internal static class Agent360Link
{
    /// <summary>The dashboard's uid.</summary>
    public const string DashboardUid = "defenseclaw-agent-360";

    /// <summary>The address <c>setup local-observability url</c> prints for Grafana; the fallback when that command cannot be read.</summary>
    public const string DefaultBaseUrl = "http://localhost:3000";

    /// <summary>The <c>scope_label</c> value that matches the <c>agent</c> variable against the agent's own id (the dashboard's "This agent").</summary>
    public const string AgentScope = "gen_ai_agent_id";

    private const int MaxValueLength = 128;

    /// <summary>
    /// <c>scheme://host:port</c> of a loopback http(s) address, or null for anything else (another host, a user name in the address, no scheme).
    /// </summary>
    public static string? TryNormalizeBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0)
        {
            return null;
        }

        var host = uri.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1" && host != "[::1]")
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>
    /// The dashboard's address on <paramref name="baseUrl"/> (see <see cref="TryNormalizeBase"/>; null when it is not a loopback address), with the
    /// connector - and the agent, which needs a connector to mean anything - as dashboard variables and the time range as <c>from</c> / <c>to</c>.
    /// A name that is empty, longer than 128 characters or holds a control character is left off: the dashboard then shows All.
    /// </summary>
    public static string? Build(string? baseUrl, string? connector = null, string? agent = null, Agent360Range? range = null)
    {
        if (TryNormalizeBase(baseUrl) is not { } root)
        {
            return null;
        }

        var parts = new List<string>();
        var connectorValue = Clean(connector);
        if (connectorValue is not null)
        {
            parts.Add("var-connector=" + Uri.EscapeDataString(connectorValue));
        }

        var agentValue = Clean(agent);
        if (agentValue is not null)
        {
            parts.Add("var-agent=" + Uri.EscapeDataString(agentValue));
            parts.Add("var-scope_label=" + AgentScope);
        }

        var window = range ?? Agent360Range.Default;
        parts.Add("from=" + Uri.EscapeDataString(window.From));
        parts.Add("to=" + Uri.EscapeDataString(window.To));

        return new StringBuilder(root).Append("/d/").Append(DashboardUid).Append('?').AppendJoin('&', parts).ToString();
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxValueLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }
}
