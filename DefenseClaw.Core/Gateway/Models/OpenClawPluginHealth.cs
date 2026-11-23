using System.Globalization;
using System.Text.Json.Serialization;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// <c>/health</c> top-level <c>openclaw_plugin</c> (runtime 1.0.10, health.go@1.0.10:211-221): when the OpenClaw plugin last called in to the gateway, and the
/// hook mode it reported (<c>hybrid</c>, <c>native</c> or <c>proxy</c>). The key is omitted until the plugin first calls in, and is in memory only, so it is absent after every gateway restart until the plugin calls in again.
/// </summary>
public sealed class OpenClawPluginHealth
{
    /// <summary>How fresh a call-in must be (openclaw_plugin_config.py@1.0.10: <c>OPENCLAW_PLUGIN_SEEN_FRESH_S</c>, 180 s; the plugin calls in every minute).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(180);

    /// <summary>How long after the gateway started an absent key is still not a problem (the CLI's rule: uptime under 90 s is "unverified", not a fault).</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(90);

    /// <summary>The last call-in as the gateway wrote it (RFC 3339 text). A string, so a value of the wrong shape can never fail the whole <c>/health</c> parse; read it with <see cref="LastSeen"/>.</summary>
    [JsonPropertyName("last_seen_at")]
    public string? LastSeenAt { get; init; }

    /// <summary><c>hybrid</c>, <c>native</c> or <c>proxy</c>; absent when the plugin did not say.</summary>
    [JsonPropertyName("hook_mode")]
    public string? HookMode { get; init; }

    /// <summary>The call-in time, or null when absent or not a valid RFC 3339 time.</summary>
    [JsonIgnore]
    public DateTimeOffset? LastSeen =>
        DateTimeOffset.TryParse(LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var seen) ? seen : null;

    /// <summary>
    /// True when the plugin has not called in as the runtime's own rule wants (<c>openclaw_plugin_evidence</c>, openclaw_plugin_config.py@1.0.10:419-472):
    /// <paramref name="plugin"/> is absent and the gateway has been up for at least <see cref="GracePeriod"/>, or the last call-in is older than <see cref="FreshFor"/>.
    /// <paramref name="uptime"/> of zero or less is "not known yet": no problem. <paramref name="age"/> is how long ago the plugin last called in, when it did.
    /// </summary>
    public static bool IsStale(OpenClawPluginHealth? plugin, TimeSpan uptime, DateTimeOffset now, out TimeSpan? age)
    {
        age = null;
        if (plugin is null)
        {
            return uptime >= GracePeriod;
        }

        if (plugin.LastSeen is not { } seen)
        {
            // A key with no readable time: the gateway sent something this app cannot judge. Not drawn as a fault.
            return false;
        }

        var ago = now - seen;
        age = ago < TimeSpan.Zero ? TimeSpan.Zero : ago;
        return age > FreshFor;
    }
}
