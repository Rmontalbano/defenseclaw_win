namespace DefenseClaw.Core.Config;

/// <summary>
/// One config key (or key pattern) and the first release whose config schema declares it. A key may use <c>*</c> (every value of a map) and <c>[]</c>
/// (every item of a list).
/// </summary>
public sealed record ConfigKeyFloor(string Key, Version FirstRelease)
{
    /// <summary>The key as a path, split at the dots (meaningful for a plain key only).</summary>
    public string[] Path => Key.Split('.');

    internal ConfigKeyPattern Pattern => ConfigKeyPattern.Parse(Key);
}

/// <summary>
/// Which config.yaml keys an older release refuses. The config schema (schemas/config/v8/defenseclaw-config.schema.json) has additionalProperties:false and
/// both the Go and Python loaders validate, so a release refuses a key it does not declare (CHANGELOG.md@1.0.8:18: "Releases before 1.0.8 refuse it; unset it
/// before you go back."). The first release that declares each key, from the schema at each tag. Read-only: it only reads a parsed config.
/// <para>
/// A key counts when the file names it, even with no value (the runtime's own upgrade shim skips a null or empty value; this app deliberately does not).
/// </para>
/// </summary>
public static class ConfigKeyFloors
{
    private static Version Since(int patch) => new(1, 0, patch);

    /// <summary>The table, in the order the keys are reported: this app's rows, then the runtime's own list (upgrade_shim.py).</summary>
    public static IReadOnlyList<ConfigKeyFloor> Floors { get; } =
    [
        new("decision_model", Since(6)),
        new("decision_model.hook_connectors", Since(7)),
        new("observability.local.max_store_mb", Since(6)),
        new("guardrail.judge.max_parallel", Since(6)),
        new("guardrail.protected_files", Since(6)),
        new("guardrail.judge.extra_body", Since(8)),

        // cli/defenseclaw/upgrade_shim.py@1.0.10:89-181 (NEWER_CONFIG_KEYS, NEWER_CONFIG_SUBTREES, NEWER_ITEM_KEYS), in its order.
        new("openshell.trusted_ca_bundle", Since(3)),
        new("guardrail.internal_domains", Since(6)),
        new("guardrail.chains", Since(6)),
        new("connectors", Since(6)),
        new("connectors.openclaw", Since(10)),
        new("guardrail.cascade", Since(10)),
        new("guardrail.secret_flow", Since(10)),
        new("guardrail.effects_rules_tier", Since(10)),
        new("guardrail.judge.intent_scope", Since(10)),
        new("decision_model.block_risk_score", Since(10)),
        new("decision_model.escalate_rule_flagged", Since(10)),
        new("decision_model.flow_guard", Since(10)),
        new("decision_model.history_tool_results", Since(10)),
        new("decision_model.history_turns", Since(10)),
        new("decision_model.protocol", Since(10)),
        new("guardrail.profiles.*.routing", Since(10)),
        new("observability.local.keep_rejected_records", Since(10)),
        new("observability.local.rejected_records_max_rows", Since(10)),
        new("observability.pricing", Since(10)),
        new("observability.tokenomics", Since(10)),
        new("routing.admin", Since(10)),
        new("routing.allow_user_override", Since(10)),
        new("routing.categories", Since(10)),
        new("routing.classifier", Since(10)),
        new("routing.context", Since(10)),
        new("routing.default_alias", Since(10)),
        new("routing.min_confidence", Since(10)),
        new("routing.models[].apis", Since(10)),
        new("routing.models[].bedrock", Since(10)),
        new("routing.models[].credential", Since(10)),
        new("routing.on_classifier_error", Since(10)),
        new("routing.route_only", Since(10)),
        new("routing.sensitive", Since(10)),
        new("routing.systemone", Since(10)),
        new("guardrail.history_seen_max", Since(10)),
        new("guardrail.max_body_values", Since(10)),
        new("openshell.admin.actor_secrets", Since(10)),
        new("openshell.admin.ask_hosts", Since(10)),
        new("openshell.admin.ask_timeout", Since(10)),
        new("openshell.admin.idle_suspend_after", Since(10)),
        new("openshell.admin.idle_suspend_tasks", Since(10)),
        new("openshell.admin.runtimes", Since(10)),
        new("openshell.admin.sandbox_class", Since(10)),
        new("openshell.admin.unattended_asks", Since(10)),
        new("openshell.admin.url_rules", Since(10)),
        new("substrate", Since(10)),
        new("guardrail.connectors.*.cascade", Since(10)),
        new("guardrail.profiles.*.cascade", Since(10)),
        new("guardrail.profiles.*.connectors.*.cascade", Since(10)),
        new("application_protection.guardrail.cascade", Since(10)),
        new("application_protection.connectors.*.guardrail.cascade", Since(10)),
        new("guardrail.profiles.*.sandbox.actor_secrets", Since(10)),
        new("guardrail.profiles.*.sandbox.ask_hosts", Since(10)),
        new("guardrail.profiles.*.sandbox.ask_timeout", Since(10)),
        new("guardrail.profiles.*.sandbox.idle_suspend_after", Since(10)),
        new("guardrail.profiles.*.sandbox.idle_suspend_tasks", Since(10)),
        new("guardrail.profiles.*.sandbox.runtimes", Since(10)),
        new("guardrail.profiles.*.sandbox.sandbox_class", Since(10)),
        new("guardrail.profiles.*.sandbox.unattended_asks", Since(10)),
        new("guardrail.profiles.*.sandbox.url_rules", Since(10)),
        new("guardrail.profile_assignments[].match.substrate", Since(10)),
    ];

    /// <summary>
    /// The keys <paramref name="config"/> sets that <paramref name="target"/> refuses, in table order (a wildcard row in the order of its map names). A target
    /// equal to a key's first release accepts it. When a block and a key inside it are both refused, only the block is reported (unsetting it removes both).
    /// The walk is bounded: at most 64 keys, and a fixed visit budget, so a huge or aliased file cannot stall it.
    /// </summary>
    public static IReadOnlyList<string> RefusedBy(ConfigYamlReader config, Version target)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(target);

        var walk = new ConfigKeyWalk();
        foreach (var floor in Floors)
        {
            if (target >= floor.FirstRelease)
            {
                continue;
            }

            var pattern = floor.Pattern;
            if (pattern.IsPlain)
            {
                if (config.Has(floor.Path))
                {
                    walk.Report(floor.Key);
                }
            }
            else
            {
                pattern.Walk(config.RootNode, walk);
            }

            if (walk.Full)
            {
                break;
            }
        }

        var refused = walk.Found;
        return refused
            .Where(k => !refused.Any(o => !string.Equals(o, k, StringComparison.Ordinal) &&
                (k.StartsWith(o + ".", StringComparison.Ordinal) || k.StartsWith(o + "[", StringComparison.Ordinal))))
            .ToArray();
    }

    /// <summary>
    /// The CLI's own advice for unsetting keys: <c>defenseclaw config unset k1 k2</c>. Only plain dotted keys (letters, digits, '_' and '-' between dots)
    /// are listed: a wildcard, a list index or anything else is dropped. Null when none is left.
    /// </summary>
    public static string? UnsetCommand(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var plain = keys.Where(IsPlainKey).ToArray();
        return plain.Length == 0 ? null : "defenseclaw config unset " + string.Join(' ', plain);
    }

    /// <summary>As <see cref="UnsetCommand(IEnumerable{string})"/>, and also drops a key <paramref name="config"/> does not have.</summary>
    public static string? UnsetCommand(ConfigYamlReader config, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(keys);

        return UnsetCommand(keys.Where(k => IsPlainKey(k) && config.Has(k.Split('.'))));
    }

    private static bool IsPlainKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var c in key)
        {
            if (c == '.')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
            }
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
            {
                segmentLength++;
            }
            else
            {
                return false;
            }
        }

        return segmentLength > 0;
    }
}
