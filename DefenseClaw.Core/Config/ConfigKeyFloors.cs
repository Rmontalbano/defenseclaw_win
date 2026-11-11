namespace DefenseClaw.Core.Config;

/// <summary>One config key and the first release whose config schema declares it.</summary>
public sealed record ConfigKeyFloor(string Key, Version FirstRelease)
{
    /// <summary>The key as a path, split at the dots.</summary>
    public string[] Path => Key.Split('.');
}

/// <summary>
/// Which config.yaml keys an older release refuses. The config schema (schemas/config/v8/defenseclaw-config.schema.json) has additionalProperties:false and
/// both the Go and Python loaders validate, so a release refuses a key it does not declare (CHANGELOG.md@1.0.8:18: "Releases before 1.0.8 refuse it; unset it
/// before you go back."). The first release that declares each key, from the schema at each tag. Read-only: it only reads a parsed config.
/// </summary>
public static class ConfigKeyFloors
{
    /// <summary>The table, in the order the keys are reported.</summary>
    public static IReadOnlyList<ConfigKeyFloor> Floors { get; } =
    [
        new("decision_model", new Version(1, 0, 6)),
        new("decision_model.hook_connectors", new Version(1, 0, 7)),
        new("observability.local.max_store_mb", new Version(1, 0, 6)),
        new("guardrail.judge.max_parallel", new Version(1, 0, 6)),
        new("guardrail.protected_files", new Version(1, 0, 6)),
        new("guardrail.judge.extra_body", new Version(1, 0, 8)),
    ];

    /// <summary>
    /// The keys <paramref name="config"/> sets that <paramref name="target"/> refuses. A target equal to a key's first release accepts it. When a block and a
    /// key inside it are both refused, only the block is reported (unsetting it removes both).
    /// </summary>
    public static IReadOnlyList<string> RefusedBy(ConfigYamlReader config, Version target)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(target);

        var refused = Floors.Where(f => target < f.FirstRelease && config.Has(f.Path)).Select(static f => f.Key).ToArray();
        return refused.Where(k => !refused.Any(o => k.StartsWith(o + ".", StringComparison.Ordinal))).ToArray();
    }

    /// <summary>The CLI's own advice for unsetting keys: <c>defenseclaw config unset k1 k2</c>.</summary>
    public static string UnsetCommand(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return "defenseclaw config unset " + string.Join(' ', keys);
    }
}
