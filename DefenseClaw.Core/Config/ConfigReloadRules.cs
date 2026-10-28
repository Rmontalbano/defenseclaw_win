namespace DefenseClaw.Core.Config;

/// <summary>What a change to config.yaml asks of the running gateway (<see cref="ConfigReloadRules.Classify"/>).</summary>
/// <param name="Parsed">Both texts could be compared. False: the answer is not known and a caller keeps its old assumption (a restart).</param>
/// <param name="ChangedPaths">Every key whose value differs, as dotted paths, in path order.</param>
/// <param name="RestartPaths">The changed keys the running gateway only applies after a restart (a subset of <paramref name="ChangedPaths"/>).</param>
public sealed record ConfigReloadVerdict(bool Parsed, IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> RestartPaths)
{
    /// <summary>Something changed and the gateway applies all of it in place: Save alone is enough.</summary>
    public bool HotOnly => Parsed && ChangedPaths.Count > 0 && RestartPaths.Count == 0;

    /// <summary>The comparison failed or found nothing.</summary>
    public static ConfigReloadVerdict Unknown { get; } = new(false, [], []);
}

/// <summary>
/// Which config.yaml keys the running gateway applies by itself when the file changes and which only a restart picks up (CUST-377), from the 1.0.4 source:
/// <c>configwrite.restartKeys</c> and <c>hotGatewayPath</c> (internal/config/configwrite/configwrite.go, <c>restart_keys.go</c>), the Python mirror
/// <c>config_writer.RESTART_KEYS</c> / <c>restart_required</c> (the CLI's own restart prediction), and the gateway's <c>diffConfigs</c>
/// (internal/gateway/config_manager.go: its <c>hotReloadable</c> sections; <c>awaitConfigReload</c> only holds a read until the reload has been applied).
/// <para>
/// A key is hot unless it is in the restart set or sits in a section the gateway does not reload. The restart set is the process-level keys (data_dir, the
/// gateway block except <c>gateway.watcher</c> and <c>gateway.config_reload.*</c> but its <c>mode</c>, the guardrail listener, enablement, scanner mode,
/// judge-body retention and hook self-heal), what the process captures at start (claw, agent, routing, plugin_dir, identity, the enterprise block), and the
/// fleet connection keys. 1.0.0-1.0.3 had a shorter list (no <c>enterprise.openshell</c>, no <c>fleet.*</c>); the longer one is used for all of them, which can only
/// predict a restart that was not needed. The connector set (<c>guardrail.connectors.*.enabled</c>) applies in process.
/// </para>
/// <para>
/// Two things make everything a restart: <c>gateway.config_reload.mode: restart</c> (the gateway validates the file, records the reload and shuts down for its
/// supervisor), and a section this table does not know (audit_db, judge_bodies_db, a key from a newer schema): unknown is a restart, never hot.
/// </para>
/// </summary>
public static class ConfigReloadRules
{
    /// <summary>The restart set, in the order of <c>configwrite.restartKeys</c> at 1.0.4. <c>*</c> matches one path segment.</summary>
    public static IReadOnlyList<string> RestartKeys { get; } =
    [
        "data_dir",
        "observability.local.path",
        "observability.local.judge_bodies_path",
        "gateway",
        "guardrail.host", "guardrail.port", "guardrail.enabled", "guardrail.connector",
        "guardrail.scanner_mode", "guardrail.retain_judge_bodies",
        "guardrail.hook_self_heal", "guardrail.hook_self_heal_debounce_ms",
        "claw", "agent", "routing", "plugin_dir",
        "deployment_mode", "enterprise.profile", "enterprise.network", "enterprise.openshell",
        "environment", "tenant_id", "workspace_id", "discovery_source",
        "fleet.mqtt_broker_url", "fleet.mqtt_require_tls",
        "fleet.ota_key_env", "fleet.emergency_key_env", "fleet.device_key_env",
        "fleet.mqtt_user_env", "fleet.mqtt_pass_env",
        "fleet.enabled",
    ];

    /// <summary>
    /// The top-level sections <c>diffConfigs</c> lists as reloadable for a per-user gateway (config_manager.go, <c>hotReloadable</c>), plus the two directory
    /// keys it applies in place (<c>policy_dir</c>, <c>quarantine_dir</c>), the fleet block (only its connection keys are in the restart set), and the keys the
    /// gateway never reads (the CLI owns them), which have nothing to apply. A managed host's <c>enterprise</c> block is not edited here and is not hot.
    /// </summary>
    private static readonly HashSet<string> HotSections = new(StringComparer.Ordinal)
    {
        "acp", "guardrail", "webhooks", "observability", "notifications", "registries", "openshell", "ai_discovery", "admission", "asset_policy",
        "llm_providers", "update", "llm", "scanners", "watch", "connector_hooks", "application_protection", "cisco_ai_defense",
        "policy_dir", "quarantine_dir", "fleet",
        "skill_actions", "mcp_actions", "plugin_actions", "config_version",
    };

    /// <summary>True when the running gateway applies a change to <paramref name="segments"/> only after a restart.</summary>
    public static bool NeedsRestart(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return true;
        }

        // gateway.watcher.* and every gateway.config_reload key but mode are read per reload.
        if (segments[0] == "gateway" && segments.Count >= 2 &&
            (segments[1] == "watcher" || (segments[1] == "config_reload" && segments.Count >= 3 && segments[2] != "mode")))
        {
            return false;
        }

        foreach (var key in RestartKeys)
        {
            if (Matches(segments, key.Split('.')))
            {
                return true;
            }
        }

        return !HotSections.Contains(segments[0]);
    }

    /// <summary>A path touches a key when it is the key, under it, or an ancestor of it (the whole block was added or removed).</summary>
    private static bool Matches(IReadOnlyList<string> path, string[] key)
    {
        var n = Math.Min(path.Count, key.Length);
        for (var i = 0; i < n; i++)
        {
            if (key[i] != "*" && !string.Equals(key[i], path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What changing config.yaml from <paramref name="before"/> to <paramref name="after"/> asks of the running gateway. <see cref="ConfigReloadVerdict.Unknown"/>
    /// when either text cannot be compared. Never carries a value, only key paths.
    /// </summary>
    public static ConfigReloadVerdict Classify(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (ConfigYamlFlatten.TryFlatten(before) is not { } oldKeys || ConfigYamlFlatten.TryFlatten(after) is not { } newKeys)
        {
            return ConfigReloadVerdict.Unknown;
        }

        var changed = new List<string>();
        foreach (var (path, value) in newKeys)
        {
            if (!oldKeys.TryGetValue(path, out var previous) || !string.Equals(previous, value, StringComparison.Ordinal))
            {
                changed.Add(path);
            }
        }

        foreach (var path in oldKeys.Keys)
        {
            if (!newKeys.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        changed.Sort(StringComparer.Ordinal);

        // A gateway set to restart on any reload shuts down whatever changed.
        var reloadRestarts = ModeIsRestart(oldKeys) || ModeIsRestart(newKeys);
        var restart = changed.Where(path => reloadRestarts || NeedsRestart(path.Split(ConfigYamlFlatten.Separator))).ToList();
        return new ConfigReloadVerdict(true, changed, restart);
    }

    private static bool ModeIsRestart(IReadOnlyDictionary<string, string> keys) =>
        keys.TryGetValue("gateway.config_reload.mode", out var mode) && string.Equals(ConfigYamlFlatten.Display(mode).Trim(), "restart", StringComparison.OrdinalIgnoreCase);
}
