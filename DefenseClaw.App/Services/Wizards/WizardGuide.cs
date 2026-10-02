namespace DefenseClaw.App.Services.Wizards;

/// <summary>A documentation page the guide points at. Always https; opened in the operator's browser.</summary>
public sealed record WizardGuideLink(string Label, string Url);

/// <summary>What a card needs from this machine before its option can be chosen.</summary>
public enum WizardGuideRequirement
{
    /// <summary>Nothing local: the pipeline is plain HTTPS to a remote service.</summary>
    None = 0,

    /// <summary>
    /// A reachable Docker engine. The option is shown disabled, with the reason, until a read-only probe
    /// (<see cref="IDockerProbe"/>) says one is there.
    /// </summary>
    Docker,
}

/// <summary>
/// One card on a guide page. A card with a <see cref="FieldId"/> is a choice (picking it turns that switch on and so
/// adds its flag); one without is plain information.
/// </summary>
public sealed class WizardGuideCard
{
    /// <summary>The id of the <see cref="WizardFieldKind.Switch"/> field picking this card turns on, or empty for an information card.</summary>
    public string FieldId { get; init; } = string.Empty;

    public required string Title { get; init; }

    /// <summary>What it does, in one sentence.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The CLI's own sentence(s) for it, lifted from its <c>--help</c> text; empty when the installed CLI says nothing.</summary>
    public string CliSays { get; init; } = string.Empty;

    /// <summary>What the operator has to have in hand.</summary>
    public IReadOnlyList<string> Needs { get; init; } = Array.Empty<string>();

    /// <summary>Where to get it.</summary>
    public IReadOnlyList<WizardGuideLink> Links { get; init; } = Array.Empty<WizardGuideLink>();

    /// <summary>What running the command will actually do, including anything that starts or contacts something.</summary>
    public string WillDo { get; init; } = string.Empty;

    public WizardGuideRequirement Requires { get; init; }

    public bool IsChoice => FieldId.Length > 0;
}

/// <summary>A short guided first step: an introduction and the cards that explain (and, for a choice, pick) what follows.</summary>
public sealed class WizardGuide
{
    public string Intro { get; init; } = string.Empty;

    public IReadOnlyList<WizardGuideCard> Cards { get; init; } = Array.Empty<WizardGuideCard>();
}
