namespace DefenseClaw.Core.Runtime;

/// <summary>
/// The sentences that say what observe mode (and a fail-open hook) does and does not do, in one place so every screen that says it says the same thing
/// (CUST-400). Through 1.0.4 observe "never blocks" on policy; from 1.0.5 it still never blocks on a policy finding but it does hard-block a proven change to
/// DefenseClaw's own active state (<c>provenActiveStateMutation</c>, internal/gateway/agent_hook.go; the changelog: "blocked in every mode. The permissive pack
/// only reports it"), and a call the gateway took but did not answer in time is blocked in every tier. Each member takes the flag that
/// <see cref="BlocksSelfChange"/> decides and returns exactly today's text when the runtime does not have <see cref="RuntimeCapability.ObserveBlocksSelfChange"/>.
/// </summary>
public static class ObserveModeCopy
{
    /// <summary>Whether this runtime blocks a change to its own files in observe mode (1.0.5 or later). False for an unknown runtime.</summary>
    public static bool BlocksSelfChange(RuntimeCapabilities? capabilities) =>
        capabilities is not null && capabilities.Has(RuntimeCapability.ObserveBlocksSelfChange);

    /// <summary>The add-a-connector review: "Observe records what the agent does and ..." (Overview and AI Discovery).</summary>
    public static string AddConnectorSentence(bool selfChangeBlocked) => selfChangeBlocked
        ? "Observe records what the agent does and never blocks on policy findings; DefenseClaw still blocks changes to its own files."
        : "Observe records what the agent does and never blocks it.";

    /// <summary>The mode note under the batch setup's mode choice.</summary>
    public static string BatchObserveNote(bool selfChangeBlocked) => selfChangeBlocked
        ? "Observe records what an agent does and never blocks on policy findings; DefenseClaw still blocks changes to its own files."
        : "Observe records what an agent does and never blocks it.";

    /// <summary>Where the app picks observe because it could not find a mode: "observe, which never blocks ...".</summary>
    public static string ObserveFallbackClause(bool selfChangeBlocked) => selfChangeBlocked
        ? "observe, which never blocks on policy findings"
        : "observe, which never blocks";

    /// <summary>The roster's note on a connector that observes but fails closed.</summary>
    public static string ObserveFailClosedNote(bool selfChangeBlocked) => selfChangeBlocked
        ? "observe + fail-closed: this connector never blocks on policy findings, but does block whenever the gateway errors, and blocks changes to DefenseClaw's own files."
        : "observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.";

    /// <summary>The note on the open fail mode in the guardrail review.</summary>
    public static string FailOpenNote(bool selfChangeBlocked) => selfChangeBlocked
        ? "Open allows the tool call or prompt and logs the failure when the gateway errors or cannot be reached, so an outage does not stop an agent. " +
          "A call the gateway took but did not answer in time is still blocked. Recommended for almost all installs."
        : "Open allows the tool call or prompt and logs the failure, so a misbehaving gateway never blocks an agent. Recommended for almost all installs.";
}
