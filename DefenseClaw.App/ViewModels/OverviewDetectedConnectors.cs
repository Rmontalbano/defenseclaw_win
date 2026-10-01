using System.Text.Json;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Detected but not configured" (CUST-213; the Mac's <c>detectedUnconfiguredConnectors</c>): the agents <c>ai_discovery_state.json</c> is confident
/// about that DefenseClaw has a hook connector for, so the attention list can say none is configured. Discovery alone never changes enforcement.
/// </summary>
internal static class OverviewDetectedConnectors
{
    /// <summary>Confidence, identity and presence must reach this for an agent to count (the Mac's 0.8).</summary>
    internal const double MinimumScore = 0.8;

    /// <summary>The Mac's <c>TUIWizards.hookConnectors</c>, in its order: the connectors that can be added with one click. The proxy ones (OpenClaw, ZeptoClaw) need their own Setup flow.</summary>
    internal static readonly IReadOnlyList<string> HookConnectors = new[]
    {
        "codex", "claudecode", "hermes", "cursor", "devin", "copilot", "openhands", "antigravity", "opencode", "amp", "omnigent",
    };

    /// <summary>A connector wire name in its one spelling: trimmed, lower case, without the separators some files put in (<c>claude-code</c>).</summary>
    internal static string Normalize(string name) =>
        name.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The hook connectors the file reports with enough confidence: not gone, not a local model, a hook connector the Mac or the app's own list knows (so never a proxy connector like OpenClaw), the best
    /// of confidence, identity and presence at least <see cref="MinimumScore"/>, and presence (when the file reports it) agreeing, so a leftover config
    /// directory from an uninstalled agent does not count. Unique, <see cref="HookConnectors"/> order first. A file that is not an object, or has no signals, yields none.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("signals", out var list))
        {
            return Array.Empty<string>();
        }

        var items = list.ValueKind switch
        {
            JsonValueKind.Object => list.EnumerateObject().Select(static p => p.Value),
            JsonValueKind.Array => list.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>(),
        };

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Local models are not agents (the same rule as the first-run window's ConnectorOnboarding.ParseDetected).
            if (string.Equals(Text(item, "category"), "local_model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Normalize(Text(item, "supported_connector"));
            var state = Text(item, "state").ToLowerInvariant();
            var confidence = Number(item, "confidence") ?? 0;
            var identity = Number(item, "identity_score") ?? 0;
            var presence = Number(item, "presence_score");
            var score = Math.Max(confidence, Math.Max(identity, presence ?? 0));

            if (name.Length > 0 &&
                state != "gone" &&
                score >= MinimumScore &&
                (presence is null || presence >= MinimumScore) &&
                (HookConnectors.Contains(name, StringComparer.Ordinal) ||
                 DefenseClaw.App.Services.FirstRun.ConnectorOnboarding.IsKnownHookConnector(name)))
            {
                found.Add(name);
            }
        }

        // The Mac's hook connectors in its order, then any other hook connector the Setup catalog offers here (Windsurf, Gemini CLI): which of
        // them can be added on this machine is the Connectors table's question (ConnectorOnboarding.Unconfigured), not the parser's.
        return HookConnectors.Where(found.Contains)
            .Concat(found.Where(name => !HookConnectors.Contains(name, StringComparer.Ordinal)).OrderBy(static name => name, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>The discovered connectors that are not in <paramref name="managed"/> (config.yaml's roster and the live one), as <see cref="Normalize"/>d names.</summary>
    internal static IReadOnlyList<string> Unconfigured(IReadOnlyList<string> discovered, ISet<string> managed) =>
        discovered.Where(name => !managed.Contains(name)).ToList();

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
