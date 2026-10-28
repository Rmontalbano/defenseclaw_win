namespace DefenseClaw.Core.Config;

/// <summary>One <c>asset_policy.&lt;type&gt;.allowed</c> or <c>.denied</c> rule: the name, the connector it is scoped to (empty: every connector) and the reason.</summary>
public sealed record OperatorAssetRule(string Name, string Connector, string Reason)
{
    /// <summary>The Tools panel's own spelling of a scoped rule: <c>@connector/name</c>, or the bare name for a rule that applies everywhere.</summary>
    public string Target => Connector.Length == 0 ? Name : "@" + Connector + "/" + Name;
}

/// <summary>
/// The operator block and allow lists in config.yaml <c>asset_policy</c> (config_version 9, runtime 1.0.x): <c>asset_policy.&lt;type&gt;.denied</c> and
/// <c>.allowed</c> for <c>skill</c>, <c>mcp</c>, <c>plugin</c> and <c>tool</c>. On 1.0.x they are the only operator block/allow source: <c>skill block</c>,
/// <c>mcp allow</c>, <c>tool block</c> and the catalog actions write config.yaml, not audit.db (upstream <c>internal/config/asset_policy.go</c>
/// <c>AssetPolicyConfig</c>, <c>AssetToolPolicy</c>, <c>AssetPolicyRule</c>; the Mac reads the same keys in <c>OperatorAssetListsConfig.swift</c>).
/// <para>
/// Read from the text of the config.yaml the app already holds: no file is opened and no process runs. A rule without a name is ignored, as the runtime
/// ignores it. A Secure Client install keeps the audit.db rows as the decision source, so its lists are not shown as the policy
/// (<see cref="SecureClient"/>). Registry rules (<c>registry</c>) are the registries' and are not part of these lists.
/// </para>
/// </summary>
public sealed record OperatorAssetLists
{
    /// <summary>The most rules kept per list; what is cut is counted in <see cref="Truncated"/>.</summary>
    public const int MaxRulesPerList = 500;

    private static readonly IReadOnlyList<OperatorAssetRule> None = Array.Empty<OperatorAssetRule>();

    /// <summary>The asset types that have operator lists, in the order the Mac and the CLI name them.</summary>
    public static IReadOnlyList<string> AssetTypes { get; } = new[] { "skill", "mcp", "plugin", "tool" };

    /// <summary>A config.yaml that holds no lists.</summary>
    public static OperatorAssetLists Empty { get; } = new();

    /// <summary>True on a Secure Client install (<c>deployment_mode: managed_enterprise</c> with <c>enterprise.profile: secure_client</c>): the lists are not the decision source there.</summary>
    public bool SecureClient { get; init; }

    /// <summary>Rules cut because a list held more than <see cref="MaxRulesPerList"/>.</summary>
    public int Truncated { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<OperatorAssetRule>> Allowed { get; init; } = new Dictionary<string, IReadOnlyList<OperatorAssetRule>>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyList<OperatorAssetRule>> Denied { get; init; } = new Dictionary<string, IReadOnlyList<OperatorAssetRule>>(StringComparer.Ordinal);

    /// <summary>Rules across every type and both directions.</summary>
    public int Total => AssetTypes.Sum(type => AllowedFor(type).Count + DeniedFor(type).Count);

    /// <summary>True when there is nothing to show: no rules, or a Secure Client install whose lists are not the policy.</summary>
    public bool IsEmpty => SecureClient || Total == 0;

    public IReadOnlyList<OperatorAssetRule> AllowedFor(string type) => Allowed.TryGetValue(type, out var rules) ? rules : None;

    public IReadOnlyList<OperatorAssetRule> DeniedFor(string type) => Denied.TryGetValue(type, out var rules) ? rules : None;

    /// <summary>The lists in <paramref name="yaml"/> (config.yaml's text). Never throws: text that cannot be read is <see cref="Empty"/>.</summary>
    public static OperatorAssetLists FromYaml(string? yaml)
    {
        if (PolicyYamlTree.Root(yaml) is not { } root)
        {
            return Empty;
        }

        var policy = PolicyYamlTree.Child(root, "asset_policy");
        var allowed = new Dictionary<string, IReadOnlyList<OperatorAssetRule>>(StringComparer.Ordinal);
        var denied = new Dictionary<string, IReadOnlyList<OperatorAssetRule>>(StringComparer.Ordinal);
        var truncated = 0;
        foreach (var type in AssetTypes)
        {
            var block = PolicyYamlTree.Child(policy, type);
            allowed[type] = Rules(block, "allowed", ref truncated);
            denied[type] = Rules(block, "denied", ref truncated);
        }

        return new OperatorAssetLists { SecureClient = IsSecureClient(root), Truncated = truncated, Allowed = allowed, Denied = denied };
    }

    /// <summary>The runtime's <c>cmd_status._enterprise_profile</c>, from config.yaml alone (the service's profile pin lives in the service environment, which the app does not read).</summary>
    private static bool IsSecureClient(IDictionary<object, object?> root)
    {
        var mode = PolicyYamlTree.Raw(PolicyYamlTree.Get(root, "deployment_mode")).ToLowerInvariant();
        if (mode is not ("managed_enterprise" or "managed"))
        {
            return false;
        }

        var configured = PolicyYamlTree.Raw(PolicyYamlTree.Get(PolicyYamlTree.Child(root, "enterprise"), "profile")).ToLowerInvariant();
        return configured.Length == 0 || configured == "secure_client";
    }

    private static List<OperatorAssetRule> Rules(IDictionary<object, object?>? block, string key, ref int truncated)
    {
        var rules = new List<OperatorAssetRule>();
        foreach (var item in PolicyYamlTree.Sequence(block, key))
        {
            if (PolicyYamlTree.Map(item) is not { } fields)
            {
                continue;
            }

            var name = PolicyYamlTree.Shown(fields, "name");
            if (name.Length == 0)
            {
                continue;
            }

            if (rules.Count >= MaxRulesPerList)
            {
                truncated++;
                continue;
            }

            rules.Add(new OperatorAssetRule(name, PolicyYamlTree.Shown(fields, "connector"), PolicyYamlTree.Shown(fields, "reason")));
        }

        return rules;
    }
}
