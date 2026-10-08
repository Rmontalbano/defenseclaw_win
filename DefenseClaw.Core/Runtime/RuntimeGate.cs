namespace DefenseClaw.Core.Runtime;

/// <summary>Whether a feature may be offered, and if not, the sentence that says why.</summary>
/// <param name="IsAvailable">True when the runtime has what the feature needs.</param>
/// <param name="Reason">Null when available; otherwise <see cref="RuntimeCapabilityCatalog.UnsupportedMessage"/>.</param>
public readonly record struct GateDecision(bool IsAvailable, string? Reason)
{
    /// <summary>The feature may be offered.</summary>
    public static GateDecision Open { get; } = new(true, null);

    /// <summary>The feature stays hidden or disabled, with the standard sentence.</summary>
    public static GateDecision Closed { get; } = new(false, RuntimeCapabilityCatalog.UnsupportedMessage);

    /// <summary>True when the feature must not be offered.</summary>
    public bool IsHidden => !IsAvailable;
}

/// <summary>
/// The one test panels, palette entries and Setup tiles apply before offering something only newer runtimes have:
/// <code>RuntimeGate.Check(services.Runtime.Capabilities, RuntimeCapability.AcpGuard).IsAvailable</code>
/// A null requirement means "0.8.10 already has this", and is always open — the gate can only ever hide new things.
/// An unknown runtime is closed: a feature appears only after the runtime has shown it has it.
/// </summary>
public static class RuntimeGate
{
    /// <summary>Open when <paramref name="required"/> is null or <paramref name="capabilities"/> has it.</summary>
    public static GateDecision Check(RuntimeCapabilities? capabilities, RuntimeCapability? required)
    {
        if (required is null)
        {
            return GateDecision.Open;
        }

        return capabilities is not null && capabilities.Has(required.Value) ? GateDecision.Open : GateDecision.Closed;
    }

    /// <summary>
    /// Open when <c>setup --help</c> lists <paramref name="subcommand"/>. For the per-connector palette entries and Setup tiles
    /// (<c>setup amp</c>, <c>setup kiro</c>) that exist only on runtimes that list them.
    /// </summary>
    public static GateDecision CheckSetupCommand(RuntimeCapabilities? capabilities, string subcommand) =>
        capabilities is not null && capabilities.HasSetupCommand(subcommand) ? GateDecision.Open : GateDecision.Closed;
}
