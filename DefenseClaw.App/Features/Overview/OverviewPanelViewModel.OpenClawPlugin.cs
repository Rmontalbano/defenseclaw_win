using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// OpenClaw on native Windows (runtime 1.0.10, G10C-06 and G10C-10): two "What needs attention" rows, both only with
/// <see cref="RuntimeCapability.OpenClawNativeWindows"/> (OpenClaw is unsupported on Windows before it), and neither on a managed installation.
/// <para>
/// The plugin row: <c>/health</c> top-level <c>openclaw_plugin</c> says when the plugin last called in (health.go@1.0.10:211-221). The runtime's own rule
/// (<c>openclaw_plugin_evidence</c>, openclaw_plugin_config.py@1.0.10:419-472) calls it unverified when the key is absent and the gateway has run 90 s or more,
/// or the last call-in is over 180 s old; the CLI's next step is "restart the OpenClaw gateway so it loads the DefenseClaw plugin". Drawn only when OpenClaw is a
/// configured connector and the gateway answered. An absent key with an uptime that is zero or unknown is not a fault. The command is text to copy; the app never runs openclaw.
/// </para>
/// <para>
/// The detected row: discovery is confident OpenClaw is installed and no connector is configured for it. Text only: the Overview has no navigation to a Setup card, and OpenClaw
/// is never added from here (it is not a hook connector, it is not in the batch dialog, and it is never a one-click setup).
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The runtime's own fix for a plugin that has not called in (the OpenClaw docs name it). Copy only.</summary>
    internal const string OpenClawGatewayRestartCommand = "openclaw gateway restart";

    /// <summary>True when the last discovery read was confident OpenClaw is installed (<see cref="OpenClawDiscovery.Found"/>).</summary>
    private bool _openClawDetected;

    private bool HasOpenClawNativeWindows => Services.Runtime.Capabilities.Has(RuntimeCapability.OpenClawNativeWindows);

    /// <summary>Adds the OpenClaw rows. Called from <see cref="AppendOverviewHealthAttention"/>.</summary>
    private void AppendOpenClawAttention(List<AttentionRow> rows)
    {
        if (!HasOpenClawNativeWindows || Services.Installation.Context.IsManaged)
        {
            return;
        }

        if (IsActiveConnector("openclaw"))
        {
            AppendOpenClawPluginRow(rows);
        }
        else if (_openClawDetected && ManagedConnectorNames() is var configured && !configured.Contains("openclaw"))
        {
            // OpenClaw runs alone in 1.0.10: setup openclaw refuses while hook connectors are configured (cmd_setup.py@1.0.10:15501-15524).
            var alone = configured.Any()
                ? " OpenClaw runs alone in this release: its setup refuses while other connectors are configured, and 'setup openclaw --replace' removes them."
                : string.Empty;
            rows.Add(new AttentionRow
            {
                Title = "OpenClaw found, not set up",
                Detail = "DefenseClaw found OpenClaw on this machine, but no connector is configured for it, so its tool calls are not checked. " +
                         "Set it up from Setup -> OpenClaw. Discovery alone never changes enforcement." + alone,
                SeverityKey = "Info",
            });
        }
    }

    private void AppendOpenClawPluginRow(List<AttentionRow> rows)
    {
        if (_snapshot.Health is not { } health || !_snapshot.IsRunning)
        {
            return;
        }

        // An uptime the gateway did not send reads as zero: not known yet, so no row (the runtime's own grace period is 90 s).
        if (!OpenClawPluginHealth.IsStale(health.OpenClawPlugin, health.Uptime, DateTimeOffset.UtcNow, out var age))
        {
            return;
        }

        const string restart = "Restart the OpenClaw gateway so it loads the DefenseClaw plugin; run the command below. The app never runs it.";
        if (age is { } ago)
        {
            var minutes = Math.Max(1, (int)ago.TotalMinutes);
            rows.Add(new AttentionRow
            {
                Title = "The OpenClaw plugin has stopped calling in",
                Detail = $"The OpenClaw plugin last called in {minutes.ToString(CultureInfo.CurrentCulture)} min ago (it normally calls in every minute), " +
                         "so OpenClaw's tool calls may not be checked. " + restart,
                SeverityKey = "High",
                Command = OpenClawGatewayRestartCommand,
            });
            return;
        }

        rows.Add(new AttentionRow
        {
            Title = "The OpenClaw plugin has not called in",
            Detail = "The OpenClaw plugin has not called in since the gateway started, so OpenClaw's tool calls are not checked. " + restart,
            SeverityKey = "High",
            Command = OpenClawGatewayRestartCommand,
        });
    }
}

/// <summary>Whether the AI-discovery file is confident OpenClaw is installed (the same gate as <see cref="OverviewDetectedConnectors"/>: 0.8 on confidence, identity and presence, not gone, not a local model).</summary>
internal static class OpenClawDiscovery
{
    /// <summary>True when <paramref name="json"/> (<c>ai_discovery_state.json</c>) has a signal for openclaw that passes the gate. A file that is not an object, or has no signals, is false.</summary>
    internal static bool Found(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("signals", out var list))
        {
            return false;
        }

        var items = list.ValueKind switch
        {
            JsonValueKind.Object => list.EnumerateObject().Select(static p => p.Value),
            JsonValueKind.Array => list.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>(),
        };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object ||
                string.Equals(Text(item, "category"), "local_model", StringComparison.OrdinalIgnoreCase) ||
                OverviewDetectedConnectors.Normalize(Text(item, "supported_connector")) != "openclaw" ||
                Text(item, "state").Equals("gone", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var presence = Number(item, "presence_score");
            var score = Math.Max(Number(item, "confidence") ?? 0, Math.Max(Number(item, "identity_score") ?? 0, presence ?? 0));
            if (score >= OverviewDetectedConnectors.MinimumScore && (presence is null || presence >= OverviewDetectedConnectors.MinimumScore))
            {
                return true;
            }
        }

        return false;
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
