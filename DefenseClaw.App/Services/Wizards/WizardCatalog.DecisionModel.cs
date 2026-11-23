using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The part of <see cref="WizardCatalog"/> that follows the runtime: for the one card whose presence depends on it (<see cref="DecisionModelCard"/>), and
/// for the OpenClaw card's first page (<see cref="OpenClawSetupIntro"/>, 1.0.10).
/// </summary>
public sealed partial class WizardCatalog
{
    // Where the catalog learns what the runtime can do; null means "nothing known", which holds the card back.
    private Func<RuntimeCapabilities?>? _capabilities;

    // Whether the roster read last held the card back; null until a roster has been read.
    private bool? _decisionModelHeldAtLoad;

    // Whether the OpenClaw card's detail was last built with its 1.0.10 intro page (OpenClawSetupIntro); null until that detail has been built.
    private bool? _openClawIntroAtLoad;

    // What the Routing card's detail was last built with (RoutingSetupWindowsPolicy: whether --enable is left out, and which sentence); null until built.
    private (bool Applies, string Sentence)? _routingCardAtLoad;

    /// <summary>
    /// Raised (on a pool thread) after <see cref="RefreshAfterCliChangeAsync"/> read the roster again, before the details are warmed. The Setup hub
    /// compares <see cref="LoadedRoster"/> with its cards and redraws when a target came or went.
    /// </summary>
    public event EventHandler? RosterReloaded;

    /// <summary>The roster of the last load that finished, or null while one is running, after it failed, or before the first.</summary>
    internal IReadOnlyList<WizardDefinition>? LoadedRoster
    {
        get
        {
            lock (InstanceGate)
            {
                return _load is { IsCompletedSuccessfully: true } load && load.Result.Count > 0 ? load.Result : null;
            }
        }
    }

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

    /// <summary>
    /// The capabilities the OpenClaw card's intro is built with (<see cref="OpenClawSetupIntro"/>), recording for that target whether the intro is offered,
    /// so a probe that lands after the detail was built (an in-app upgrade to 1.0.10 rebuilds the details before the runtime is probed again) is noticed.
    /// </summary>
    private RuntimeCapabilities? CapabilitiesForOpenClawIntro(string target)
    {
        var capabilities = _capabilities?.Invoke();
        if (string.Equals(target, OpenClawSetupIntro.Target, StringComparison.Ordinal))
        {
            _openClawIntroAtLoad = OpenClawSetupIntro.IsOffered(capabilities);
        }

        return capabilities;
    }

    /// <summary>The capabilities the Routing card is filtered with, recording for that target what <see cref="RoutingSetupWindowsPolicy"/> made of them.</summary>
    private RuntimeCapabilities? CapabilitiesForRoutingCard(string target)
    {
        var capabilities = _capabilities?.Invoke();
        if (string.Equals(target, RoutingSetupWindowsPolicy.Target, StringComparison.Ordinal))
        {
            _routingCardAtLoad = RoutingCard(capabilities);
        }

        return capabilities;
    }

    private static (bool Applies, string Sentence) RoutingCard(RuntimeCapabilities? capabilities) =>
        (RoutingSetupWindowsPolicy.Applies(capabilities), RoutingSetupWindowsPolicy.SentenceFor(capabilities));

    /// <summary>
    /// Re-reads the catalog when the runtime's answer to "is the decision-model card offered" differs from the last roster read's, or the answer to
    /// "does the OpenClaw card open on its intro" or "what does the Routing card offer" differs from the one its detail was built with. Never faults.
    /// </summary>
    internal Task ObserveRuntimeAsync()
    {
        var capabilities = _capabilities?.Invoke();
        var read = _decisionModelHeldAtLoad;
        var heldChanged = read is not null && DecisionModelCard.IsHeld(DecisionModelCard.Target, capabilities) != read;
        var intro = _openClawIntroAtLoad;
        var introChanged = intro is not null && OpenClawSetupIntro.IsOffered(capabilities) != intro;
        var routing = _routingCardAtLoad;
        var routingChanged = routing is not null && RoutingCard(capabilities) != routing;
        return heldChanged || introChanged || routingChanged ? RefreshAfterCliChangeAsync() : Task.CompletedTask;
    }
}
