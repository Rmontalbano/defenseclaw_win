using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.FirstRun;

/// <summary>How first run asks the hook failure mode, which depends on the runtime (CUST-390).</summary>
public enum FirstRunFailModeQuestion
{
    /// <summary>
    /// 0.8.x, and a runtime that has not answered yet: asked in both profiles and sent with every plan, <c>open</c> to start with. That is the 0.8.10
    /// contract (<c>tui/panels/first_run.py</c> "Hook fail mode is always passed", default <c>open</c>; the <c>cmd_init.py</c> prompt defaults to
    /// <c>open</c>, "recommended"), unchanged from before.
    /// </summary>
    EveryProfile,

    /// <summary>
    /// 1.0.0 to 1.0.3, and a managed 1.0.4: asked and sent with the Action profile only, <c>closed</c> to start with. The Observe profile leaves
    /// <c>--fail-mode</c> off, and the CLI keeps the current setting, which is <c>closed</c> on a new install (<c>cmd_init.py</c> <c>--fail-mode</c> help,
    /// <c>_current_hook_fail_mode</c>; <c>config.py</c> <c>hook_fail_mode = "closed"</c>; <c>tui/panels/first_run.py</c> <c>args</c>; the Mac's
    /// <c>ConnectorOnboarding.initializationPlan</c>, v1.1.32).
    /// </summary>
    ActionProfileOnly,

    /// <summary>A per-user 1.0.4 host: not asked and never sent, because the policy tier decides the outage mode (<see cref="HookFailModeRule"/>).</summary>
    FollowsPolicy,

    /// <summary>
    /// A per-user 1.0.5 host (CUST-397): a saved fail mode wins over the policy tier, so the form asks it with the Action profile only, as the 1.0.5 TUI does
    /// (<c>tui/panels/first_run.py</c> <c>FAIL_MODE_CHOICES = ("policy", "open", "closed")</c>, default <c>policy</c>), and <c>--fail-mode</c> is sent only for a
    /// typed <c>open</c> or <c>closed</c>. <see cref="FirstRunFailMode.Policy"/> sends nothing: <c>init</c> then saves nothing and keeps a mode saved earlier
    /// (<c>cmd_init.py</c> <c>_KEPT_CONNECTOR_FIELDS</c>), and with none the policy tier decides.
    /// </summary>
    SavedOrPolicy,
}

/// <summary>
/// Which <see cref="FirstRunFailModeQuestion"/> a runtime gets, what the form starts on, and the hint it shows. Pure: no I/O.
/// <para>
/// <b>Never weaker than before.</b> The only answer that changes against the old form is a 1.0.x one: it now starts on <c>closed</c> where it started on
/// <c>open</c>, and an Observe plan sends nothing where it sent the form's value. Leaving the flag off never sends <c>open</c>; the CLI keeps what is saved.
/// On a per-user 1.0.5 the form starts on <see cref="Policy"/> because there a sent value is saved and wins over the policy tier: only a value the operator
/// picks is sent.
/// </para>
/// </summary>
public static class FirstRunFailMode
{
    /// <summary>The TUI's hint for the field on 1.0.x (<c>tui/panels/first_run.py</c>, the "Hook Fail Mode" field).</summary>
    public const string ActionOnlyHint = "Action profile only. If a check can't finish: closed = block where supported, open = allow and log.";

    /// <summary>The value of <see cref="FirstRunFailModeQuestion.SavedOrPolicy"/>'s first choice: save nothing, so the policy tier (or a mode saved earlier) decides.</summary>
    public const string Policy = "policy";

    /// <summary>The 1.0.5 TUI's hint for the field (<c>tui/panels/first_run.py</c>), with what <see cref="Policy"/> does to a mode saved earlier.</summary>
    public const string SavedOrPolicyHint =
        "Action profile only. While the gateway is down: policy = save nothing (a mode saved earlier is kept; with none, default and permissive allow with a warning, " +
        "strict blocks); open = allow with a warning; closed = block. A saved choice wins over the policy tier, and every other hook failure blocks.";

    /// <summary>The first release whose first run asks the fail mode for the Action profile only (the 1.0.0 TUI and <c>cmd_init.py</c>).</summary>
    private static readonly Version ActionOnlyRelease = new(1, 0, 0);

    /// <param name="runtime">The runtime as last probed; one with no version is treated as 0.8.x, the form it always had.</param>
    /// <param name="managed">True for a managed-enterprise installation, where the administrator's explicit value is still applied.</param>
    public static FirstRunFailModeQuestion For(RuntimeSnapshot runtime, bool managed)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (HookFailModeRule.SavedValueWins(runtime.Capabilities, managed))
        {
            return FirstRunFailModeQuestion.SavedOrPolicy;
        }

        if (HookFailModeRule.SavedValueIgnored(runtime.Capabilities, managed))
        {
            return FirstRunFailModeQuestion.FollowsPolicy;
        }

        return runtime.Identity?.ParsedVersion is { } version && version >= ActionOnlyRelease
            ? FirstRunFailModeQuestion.ActionProfileOnly
            : FirstRunFailModeQuestion.EveryProfile;
    }

    /// <summary>
    /// What the form starts on: <c>open</c> on 0.8.x as it always did, <see cref="Policy"/> (save nothing) on a per-user 1.0.5 as its TUI does, <c>closed</c>
    /// wherever else the runtime's own first run starts there.
    /// </summary>
    public static string DefaultFor(FirstRunFailModeQuestion question) => question switch
    {
        FirstRunFailModeQuestion.EveryProfile => "open",
        FirstRunFailModeQuestion.SavedOrPolicy => Policy,
        _ => "closed",
    };

    /// <summary>Whether the Hook failure mode box is shown for this runtime and profile.</summary>
    public static bool IsAsked(FirstRunFailModeQuestion question, bool actionProfile) => question switch
    {
        FirstRunFailModeQuestion.EveryProfile => true,
        FirstRunFailModeQuestion.ActionProfileOnly or FirstRunFailModeQuestion.SavedOrPolicy => actionProfile,
        _ => false,
    };

    /// <summary>The caption under the box: the TUI's hint where the field is the Action profile's, nothing on 0.8.x (whose form never had one).</summary>
    public static string HintFor(FirstRunFailModeQuestion question) => question switch
    {
        FirstRunFailModeQuestion.ActionProfileOnly => ActionOnlyHint,
        FirstRunFailModeQuestion.SavedOrPolicy => SavedOrPolicyHint,
        _ => string.Empty,
    };
}
