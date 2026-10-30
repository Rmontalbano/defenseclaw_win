using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Config;

/// <summary>
/// What decides a hook's fail mode, by runtime (CUST-373, CUST-397). Up to 1.0.3 a connector's saved <c>hook_fail_mode</c> was the mode. In 1.0.4
/// (<c>config.py</c> <c>effective_hook_fail_mode</c>, the <c>_refuse_a_mode_the_policy_tier_does_not_give</c> refusal in <c>cmd_guardrail.py</c>) a
/// <b>per-user</b> host reports its gateway-outage mode from the connector's policy tier instead: <c>closed</c> for a strict rule pack, <c>open</c> for
/// default and permissive, and a saved per-user value changes nothing. From 1.0.5 (<c>hook_outage_fail_mode.go</c> <c>HookOutageFailMode</c>, the same
/// <c>config.py</c> resolver) an explicitly saved value wins again: the connector's own, else the global one, and the policy tier decides only when nothing
/// is saved. A managed configuration (Secure Client or the standalone profile) takes the administrator's explicit value on every release. Whether 1.0.4's
/// rule applies is <see cref="RuntimeCapability.PerUserHookRules"/> without <see cref="RuntimeCapability.SavedFailModeWins"/>; a runtime without either
/// keeps the old reading.
/// </summary>
public static class HookFailModeRule
{
    /// <summary>The command the runtime itself points to for a fail-closed outage when nothing is saved (<c>cmd_guardrail.py</c>, <c>config.py</c> <c>outage_block_hint</c>).</summary>
    public const string StrictPackCommand = "defenseclaw guardrail use-pack strict";

    /// <summary>The command that saves a fail mode (<c>cmd_guardrail.py</c> <c>fail_mode_cmd</c>); <c>--connector NAME</c> scopes it to one connector.</summary>
    public const string FailModeCommand = "defenseclaw guardrail fail-mode";

    /// <summary>The 1.0.4 runtime's own sentence for the rule (<c>_PER_USER_OUTAGE_RULE</c> at tag 1.0.4).</summary>
    public const string PerUserOutageRule =
        "On a per-user install the gateway-outage mode follows the policy tier: the default (balanced) and permissive rule packs let a call run with a warning " +
        "while the gateway is not running, strict blocks it. Every other failure (an error or unusable gateway answer, a missing token, an install or upgrade) blocks.";

    /// <summary>The 1.0.5 runtime's own sentence for the rule (<c>_PER_USER_OUTAGE_RULE</c> at tag 1.0.5, <c>cmd_guardrail.py:2013</c>).</summary>
    public const string SavedWinsOutageRule =
        "On a per-user install an explicit saved mode wins. When unset, the gateway-outage mode follows the policy tier: the default (balanced) and permissive " +
        "rule packs let a call run with a warning while the gateway is not running, strict blocks it. Every other failure (an error or unusable gateway answer, " +
        "a missing token, an install or upgrade) blocks.";

    /// <summary>
    /// True when a saved per-user <c>hook_fail_mode</c> is not what the hooks obey: the runtime follows 1.0.4's policy-tier rule and the installation is not
    /// managed. <paramref name="managed"/> is true for a managed-enterprise installation. False on 1.0.5 and later, where a saved value wins again
    /// (<see cref="SavedValueWins"/>).
    /// </summary>
    public static bool SavedValueIgnored(RuntimeCapabilities? capabilities, bool managed) =>
        !managed && capabilities is not null && capabilities.Has(RuntimeCapability.PerUserHookRules) && !capabilities.Has(RuntimeCapability.SavedFailModeWins);

    /// <summary>
    /// True on a per-user 1.0.5-or-later host: a saved <c>hook_fail_mode</c> decides a connector's gateway-outage mode, and the policy tier only when nothing
    /// is saved. False on a managed install (the administrator's value has always decided there, so nothing about it changed) and on every older runtime.
    /// </summary>
    public static bool SavedValueWins(RuntimeCapabilities? capabilities, bool managed) =>
        !managed && capabilities is not null && capabilities.Has(RuntimeCapability.SavedFailModeWins);

    /// <summary>
    /// Where the saved <c>hook_fail_mode</c> that decides <paramref name="connector"/> lives (<c>config.py</c> <c>saved_hook_fail_mode_scope</c>): the
    /// connector's own entry, else the global <c>guardrail.hook_fail_mode</c>, else none (the policy tier decides). A blank value is not a saved one.
    /// </summary>
    public static SavedFailModeScope SavedScope(GuardrailSection guardrail, string connector)
    {
        ArgumentNullException.ThrowIfNull(guardrail);

        if (guardrail.Connectors.TryGetValue(connector, out var own) && !string.IsNullOrWhiteSpace(own.HookFailMode))
        {
            return SavedFailModeScope.Connector;
        }

        return string.IsNullOrWhiteSpace(guardrail.HookFailMode) ? SavedFailModeScope.None : SavedFailModeScope.Global;
    }

    /// <summary>
    /// The saved mode that decides <paramref name="connector"/>, canonical as the runtime reads it (Go <c>explicitHookFailMode</c>: <c>open</c> in any case is
    /// open, every other non-blank value is closed), or null when nothing is saved.
    /// </summary>
    public static string? SavedMode(GuardrailSection guardrail, string connector)
    {
        ArgumentNullException.ThrowIfNull(guardrail);

        var raw = SavedScope(guardrail, connector) switch
        {
            SavedFailModeScope.Connector => guardrail.Connectors[connector].HookFailMode,
            SavedFailModeScope.Global => guardrail.HookFailMode,
            _ => null,
        };
        return raw is null ? null : string.Equals(raw.Trim(), "open", StringComparison.OrdinalIgnoreCase) ? "open" : "closed";
    }

    /// <summary>
    /// The command that makes a per-user connector's hooks block while the gateway is down (<c>config.py</c> <c>outage_block_hint</c>):
    /// <c>guardrail fail-mode closed --connector NAME</c> when the connector's own saved mode decides it, <c>guardrail fail-mode closed</c> for a saved global one,
    /// and the strict rule pack only when nothing is saved.
    /// </summary>
    public static string OutageBlockHint(SavedFailModeScope scope, string connector) => scope switch
    {
        SavedFailModeScope.Connector => $"{FailModeCommand} closed --connector {connector}",
        SavedFailModeScope.Global => $"{FailModeCommand} closed",
        _ => StrictPackCommand,
    };

    /// <summary>
    /// The built-in policy tier of <paramref name="connector"/>'s rule pack when config.yaml names one plainly (<c>strict</c>, <c>default</c> or
    /// <c>permissive</c>: the connector's <c>rule_pack</c>, else the global one, else the folder name of the connector's <c>rule_pack_dir</c>), or null when it
    /// cannot be told without reading the pack (a custom pack, whose manifest posture decides). Used only to add a note, never to hide one.
    /// </summary>
    public static string? KnownPolicyTier(GuardrailSection guardrail, string connector)
    {
        ArgumentNullException.ThrowIfNull(guardrail);

        guardrail.Connectors.TryGetValue(connector, out var own);
        var name = !string.IsNullOrWhiteSpace(own?.RulePack) ? own!.RulePack : guardrail.RulePack;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return BuiltInTier(name);
        }

        if (string.IsNullOrWhiteSpace(own?.RulePackDir))
        {
            return null;
        }

        var folder = Path.GetFileName(own!.RulePackDir.Trim().TrimEnd('/', '\\'));
        return BuiltInTier(folder);
    }

    private static string? BuiltInTier(string name) => name.Trim().ToLowerInvariant() switch
    {
        "strict" => "strict",
        "default" => "default",
        "permissive" => "permissive",
        _ => null,
    };
}

/// <summary>Where the saved <c>hook_fail_mode</c> that decides a connector lives (<see cref="HookFailModeRule.SavedScope"/>).</summary>
public enum SavedFailModeScope
{
    /// <summary>Nothing is saved: the policy tier decides.</summary>
    None,

    /// <summary>The connector's own <c>guardrail.connectors.NAME.hook_fail_mode</c>.</summary>
    Connector,

    /// <summary>The global <c>guardrail.hook_fail_mode</c> every connector without its own inherits.</summary>
    Global,
}
