using System.Collections.ObjectModel;
using System.Text.Json;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One connector's hook fail mode as <c>defenseclaw status --json</c> reports it (<c>connectors[].fail_mode</c>): the mode the hook
/// really obeys (<see cref="Effective"/>), which source decided it (<see cref="Provenance"/>), what config.yaml asks for
/// (<see cref="Configured"/>), and every place that disagrees (<see cref="Drift"/>). A hook whose
/// <see cref="Current"/> is false is running something other than what was asked.
/// </summary>
internal sealed record HookFailModeState(
    string? Effective,
    string? Provenance,
    string? Configured,
    string? Desired,
    string? Runtime,
    bool? Current,
    IReadOnlyList<string> Drift)
{
    /// <summary>True when the status says the hook is not as configured: <c>current: false</c>, or any drift entry.</summary>
    public bool HasDrift => Current == false || Drift.Count > 0;
}

/// <summary>One leftover of a removed connector: where it is, what it does, and the step that removes it by hand (the runtime's own words).</summary>
internal sealed record RemovedConnectorItem(string Path, string What, string Step);

/// <summary>What one removed connector (<c>openhands</c>, <c>omnigent</c>) left on this host, as <c>status --json</c> lists it.</summary>
internal sealed record RemovedConnectorLeftovers(string Connector, string Label, IReadOnlyList<RemovedConnectorItem> Items);

/// <summary>One entry of <c>connectors[]</c> in <c>defenseclaw status --json</c>.</summary>
internal sealed record StatusConnector(string Name, string? Friendly, string? Mode, bool? Enabled, string? Source, HookFailModeState? FailMode);

/// <summary>
/// What the Overview reads from one <c>defenseclaw status --json</c> (read-only): the facts the gateway's REST API does not carry, per
/// connector fail-mode provenance and drift above all, plus the deployment mode, the environment, and the skill/MCP list counts and scan
/// totals the Mac's Activity summary prints. Every member is optional: an older CLI that lacks a field just leaves it null.
/// </summary>
internal sealed record DefenseClawStatus(
    string? Environment,
    string? DeploymentMode,
    string? DataDirectory,
    string? ConfigPath,
    string? Scope,
    bool? SandboxAvailable,
    bool? SidecarRunning,
    int? BlockedSkills,
    int? AllowedSkills,
    int? BlockedMcps,
    int? AllowedMcps,
    int? TotalScans,
    int? ActiveAlerts,
    bool? ApplicationProtectionEnabled,
    string? ApplicationProtectionState,
    IReadOnlyList<StatusConnector> Connectors)
{
    /// <summary>
    /// <c>removed_connector_cleanup</c> (1.0.4): what the removed OpenHands and OmniGent connectors left on this host, with the step to remove each item by
    /// hand. Empty when the field is absent (every older runtime) or lists nothing. An init member rather than a constructor argument so the
    /// positional form every reader of this record uses is unchanged.
    /// </summary>
    public IReadOnlyList<RemovedConnectorLeftovers> RemovedConnectorCleanup { get; init; } = Array.Empty<RemovedConnectorLeftovers>();

    /// <summary>The entry for <paramref name="name"/> (without case), or null.</summary>
    public StatusConnector? Connector(string name) =>
        Connectors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nothing known: what the panel holds before the first read, and after a failed one.</summary>
    public static DefenseClawStatus Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Array.Empty<StatusConnector>());
}

/// <summary>Parses <c>defenseclaw status --json</c> (0.8.10 to 1.0.4). Tolerant: a missing or oddly typed member is null, never an error.</summary>
internal static class DefenseClawStatusReader
{
    /// <exception cref="JsonException">Not JSON, or not a JSON object.</exception>
    public static DefenseClawStatus Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("status --json is not a JSON object.");
        }

        var enforcement = Object(root, "enforcement");
        var activity = Object(root, "activity");
        var appProtection = Object(root, "application_protection");
        var sandbox = Object(root, "sandbox");
        var sidecar = Object(root, "sidecar");

        var connectors = new List<StatusConnector>();
        if (root.TryGetProperty("connectors", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                connectors.Add(new StatusConnector(
                    name,
                    Text(item, "friendly"),
                    Text(item, "mode"),
                    Bool(item, "enabled"),
                    Text(item, "source"),
                    ReadFailMode(item)));
            }
        }

        var status = new DefenseClawStatus(
            Text(root, "environment"),
            Text(root, "deployment_mode"),
            Text(root, "data_dir"),
            Text(root, "config"),
            Text(root, "scope"),
            sandbox is { } s ? Bool(s, "available") : null,
            sidecar is { } c ? Bool(c, "running") : null,
            enforcement is { } e1 ? Int(e1, "blocked_skills") : null,
            enforcement is { } e2 ? Int(e2, "allowed_skills") : null,
            enforcement is { } e3 ? Int(e3, "blocked_mcps") : null,
            enforcement is { } e4 ? Int(e4, "allowed_mcps") : null,
            activity is { } a1 ? Int(a1, "total_scans") : null,
            activity is { } a2 ? Int(a2, "active_alerts") : null,
            appProtection is { } p1 ? Bool(p1, "enabled") : null,
            appProtection is { } p2 ? Text(p2, "health_state") : null,
            connectors)
        {
            RemovedConnectorCleanup = ReadRemovedConnectorCleanup(root),
        };
        return status;
    }

    /// <summary><c>removed_connector_cleanup: [{connector, label, items: [{path, what, step}]}]</c> (<c>removed_connectors.py</c> <c>as_json</c>); a malformed entry is skipped.</summary>
    private static IReadOnlyList<RemovedConnectorLeftovers> ReadRemovedConnectorCleanup(JsonElement root)
    {
        if (!root.TryGetProperty("removed_connector_cleanup", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RemovedConnectorLeftovers>();
        }

        var found = new List<RemovedConnectorLeftovers>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "connector") is not { Length: > 0 } connector)
            {
                continue;
            }

            var items = new List<RemovedConnectorItem>();
            if (entry.TryGetProperty("items", out var rawItems) && rawItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rawItems.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && Text(item, "path") is { Length: > 0 } path)
                    {
                        items.Add(new RemovedConnectorItem(path, Text(item, "what") ?? string.Empty, Text(item, "step") ?? string.Empty));
                    }
                }
            }

            if (items.Count > 0)
            {
                found.Add(new RemovedConnectorLeftovers(connector, Text(entry, "label") is { Length: > 0 } label ? label : connector, items));
            }
        }

        return found;
    }

    private static HookFailModeState? ReadFailMode(JsonElement connector)
    {
        if (Object(connector, "fail_mode") is not { } mode)
        {
            return null;
        }

        var drift = new List<string>();
        if (mode.TryGetProperty("drift", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } text)
                {
                    drift.Add(text);
                }
            }
        }

        return new HookFailModeState(
            Text(mode, "effective"),
            Text(mode, "provenance"),
            Text(mode, "configured"),
            Text(mode, "desired"),
            Text(mode, "runtime"),
            Bool(mode, "current"),
            new ReadOnlyCollection<string>(drift));
    }

    private static JsonElement? Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
