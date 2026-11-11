using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The part of <see cref="WizardCatalog"/> that follows the runtime for the one card whose presence depends on it (<see cref="DecisionModelCard"/>).
/// </summary>
public sealed partial class WizardCatalog
{
    // Where the catalog learns what the runtime can do; null means "nothing known", which holds the card back.
    private Func<RuntimeCapabilities?>? _capabilities;

    // Whether the roster read last held the card back; null until a roster has been read.
    private bool? _decisionModelHeldAtLoad;

    /// <summary>
    /// What the runtime can do right now, as the roster being read sees it; records whether that holds the decision-model card back so a later
    /// change of the answer is noticed (<see cref="ObserveRuntimeAsync"/>).
    /// </summary>
    private RuntimeCapabilities? CapabilitiesForRoster()
    {
        var capabilities = _capabilities?.Invoke();
        _decisionModelHeldAtLoad = DecisionModelCard.IsHeld(DecisionModelCard.Target, capabilities);
        return capabilities;
    }

    /// <summary>
    /// Follows <paramref name="runtime"/> (the shared catalog does): when a probe changes whether the decision-model card is offered, the
    /// catalog is read again as after a CLI upgrade. Does nothing before the first roster read (that read sees the current answer) or when the
    /// answer is the one the last read used.
    /// </summary>
    internal void UseRuntime(RuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _capabilities = () => runtime.Capabilities;
        runtime.Changed += (_, _) => _ = ObserveRuntimeAsync();
    }

    /// <summary>Re-reads the catalog when the runtime's answer to "is the decision-model card offered" differs from the last roster read's. Never faults.</summary>
    internal Task ObserveRuntimeAsync()
    {
        var read = _decisionModelHeldAtLoad;
        if (read is null)
        {
            return Task.CompletedTask;
        }

        var held = DecisionModelCard.IsHeld(DecisionModelCard.Target, _capabilities?.Invoke());
        return held == read ? Task.CompletedTask : RefreshAfterCliChangeAsync();
    }
}
