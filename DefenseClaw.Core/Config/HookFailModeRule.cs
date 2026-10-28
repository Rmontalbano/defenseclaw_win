using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Core.Config;

/// <summary>
/// What decides a hook's fail mode, by runtime (CUST-373). Up to 1.0.3 a connector's saved <c>hook_fail_mode</c> was the mode. From 1.0.4
/// (<c>config.py</c> <c>effective_hook_fail_mode</c>, the <c>_refuse_a_mode_the_policy_tier_does_not_give</c> refusal in <c>cmd_guardrail.py</c>) a
/// <b>per-user</b> host reports its gateway-outage mode from the connector's policy tier instead: <c>closed</c> for a strict rule pack, <c>open</c> for
/// default and permissive, and a saved per-user value changes nothing. A managed configuration (Secure Client or the standalone profile) still takes the
/// administrator's explicit value. Whether the rule applies is <see cref="RuntimeCapability.PerUserHookRules"/>; a runtime without it keeps the old reading.
/// </summary>
public static class HookFailModeRule
{
    /// <summary>The command the runtime itself points to for a fail-closed outage (<c>cmd_guardrail.py</c>, "Fail closed during an outage").</summary>
    public const string StrictPackCommand = "defenseclaw guardrail use-pack strict";

    /// <summary>The runtime's own sentence for the rule (<c>_PER_USER_OUTAGE_RULE</c>).</summary>
    public const string PerUserOutageRule =
        "On a per-user install the gateway-outage mode follows the policy tier: the default (balanced) and permissive rule packs let a call run with a warning " +
        "while the gateway is not running, strict blocks it. Every other failure (an error or unusable gateway answer, a missing token, an install or upgrade) blocks.";

    /// <summary>
    /// True when a saved per-user <c>hook_fail_mode</c> is not what the hooks obey: the runtime follows the policy tier and the installation is not
    /// managed. <paramref name="managed"/> is true for a managed-enterprise installation.
    /// </summary>
    public static bool SavedValueIgnored(RuntimeCapabilities? capabilities, bool managed) =>
        !managed && capabilities is not null && capabilities.Has(RuntimeCapability.PerUserHookRules);
}
