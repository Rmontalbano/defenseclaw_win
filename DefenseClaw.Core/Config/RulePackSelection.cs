using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.Core.Config;

/// <summary>
/// The rule pack config.yaml selects for one connector, read the way the runtime reads it (<c>EffectiveRulePackRef</c> and <c>rulePackRefOf</c>,
/// internal/config/guardrail_rules.go@1.0.9:64-110, the same since 1.0.0): the connector's own entry first, then the global <c>guardrail</c> block, and at one
/// scope a <c>rule_pack</c> name wins over a <c>rule_pack_dir</c> folder. A connector entry that names either key decides as a whole, so a connector's
/// <c>rule_pack_dir</c> beats a global <c>rule_pack</c>. Nothing set is the built-in <c>default</c> pack.
/// <para>
/// Not modelled: the <c>application_protection</c> overlay (it applies only to connectors not configured by hand) and where a <c>custom_packs</c> name lives,
/// so a custom pack's posture stays unknown here.
/// </para>
/// </summary>
/// <param name="Name">The <c>rule_pack</c> name that decides (a built-in pack or a <c>custom_packs</c> key), or null.</param>
/// <param name="Directory">The <c>rule_pack_dir</c> folder that decides when no name does, or null.</param>
public sealed record RulePackSelection(string? Name, string? Directory)
{
    /// <summary>The selection for <paramref name="connector"/>.</summary>
    public static RulePackSelection For(GuardrailSection guardrail, string connector)
    {
        ArgumentNullException.ThrowIfNull(guardrail);

        if (guardrail.Connectors.TryGetValue(connector, out var own) && Of(own.RulePack, own.RulePackDir) is { } scoped)
        {
            return scoped;
        }

        return Of(guardrail.RulePack, guardrail.RulePackDir) ?? new RulePackSelection(null, null);
    }

    /// <summary>
    /// The pack's name as the Overview prints it: the <c>rule_pack</c> name, else the last segment of the <c>rule_pack_dir</c> folder, else <c>default</c>.
    /// </summary>
    public string DisplayName => Name ?? FolderName ?? "default";

    /// <summary>
    /// The built-in policy tier (<c>strict</c>, <c>default</c> or <c>permissive</c>) when it can be told without the loader: a built-in name; a folder's
    /// <c>defenseclaw-pack.json</c> posture, else a folder named after a built-in pack. Null for a custom pack name, a folder with another name and no usable
    /// manifest, and when nothing is set.
    /// </summary>
    /// <param name="readManifest">Reads a file's text (null when missing); null reads the file system. A test seam.</param>
    public string? KnownTier(Func<string, string?>? readManifest = null)
    {
        if (Name is { } name)
        {
            return BuiltInTier(name);
        }

        if (Directory is not { } dir)
        {
            return null;
        }

        return RulePackPosture.FromManifest(dir, readManifest) ?? BuiltInTier(FolderName ?? string.Empty);
    }

    /// <summary>
    /// <see cref="DisplayName"/>, followed by the known tier in brackets when it differs from the name (a folder or pack whose manifest names another posture).
    /// </summary>
    public string Label(Func<string, string?>? readManifest = null)
    {
        var name = DisplayName;
        return KnownTier(readManifest) is { } tier && !string.Equals(tier, name, StringComparison.OrdinalIgnoreCase) ? $"{name} ({tier})" : name;
    }

    private string? FolderName
    {
        get
        {
            var trimmed = (Directory ?? string.Empty).Trim().TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return null;
            }

            var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
        }
    }

    private static RulePackSelection? Of(string? name, string? dir)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return new RulePackSelection(name.Trim(), null);
        }

        return string.IsNullOrWhiteSpace(dir) ? null : new RulePackSelection(null, dir.Trim());
    }

    internal static string? BuiltInTier(string name) => name.Trim().ToLowerInvariant() switch
    {
        "strict" => "strict",
        "default" => "default",
        "permissive" => "permissive",
        _ => null,
    };
}
