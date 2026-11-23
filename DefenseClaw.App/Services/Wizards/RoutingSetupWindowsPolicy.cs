using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The Setup "Routing" card (<c>defenseclaw setup routing</c>) on Windows (CUST-457).
/// <para>
/// <c>setup routing --enable</c> raises on native Windows and writes nothing (<c>cmd_setup.py</c>: <c>setup_routing</c> checks
/// <c>_routing_unsupported_on_host()</c>, which is <c>os.name == "nt"</c>, and raises <c>_ROUTING_UNSUPPORTED_ON_WINDOWS</c>); <c>--disable</c> and
/// <c>--status</c> still work, and <c>--status</c> prints the same sentence. The card therefore does not offer <c>--enable</c>, keeps
/// <c>--disable</c> and <c>--status</c>, and says why on its first page.
/// </para>
/// <para>
/// <b>From 1.0.5 only.</b> The refusal is in <c>cmd_setup.py@1.0.5:20840,20941</c> and in the source through 1.0.10, and absent at 1.0.4 (no match for
/// <c>_ROUTING_UNSUPPORTED_ON_WINDOWS</c>): on 1.0.0 to 1.0.4 the command accepts <c>--enable</c>, so the card is exactly what it was. The setup help does not
/// carry the refusal (it is in the command's body, not its help), so the release is the marker: <see cref="RuntimeCapability.SavedFailModeWins"/> is judged at
/// version 1.0.5 and nothing else. A caller that does not know the runtime (null), or a runtime not probed yet, gets the policy. 0.8.10 has no <c>setup routing</c>, so no card.
/// </para>
/// <para>
/// On 1.0.10 (<see cref="RuntimeCapability.RoutingCli"/>) the Overview lists the connectors whose routing is route-only, so the card's sentence says it is this
/// command's semantic routing that native Windows refuses, and that route-only state is reported elsewhere.
/// </para>
/// </summary>
public static class RoutingSetupWindowsPolicy
{
    /// <summary>The card's target, the word after <c>setup</c>.</summary>
    public const string Target = "routing";

    /// <summary>The flag the card leaves out.</summary>
    public const string EnableFlag = "--enable";

    /// <summary>The runtime's own sentence (<c>_ROUTING_UNSUPPORTED_ON_WINDOWS</c>, cmd_setup.py@1.0.5 to 1.0.10), word for word.</summary>
    public const string RuntimeSentence =
        "Semantic routing is not supported on native Windows: it routes OpenClaw and ZeptoClaw proxy traffic, and DefenseClaw runs no guardrail proxy on Windows.";

    /// <summary>The 1.0.10 card's sentence: what this command refuses, and where route-only routing is reported.</summary>
    public const string RuntimeSentenceWithRouteOnly =
        "This command cannot turn semantic routing on here: the CLI refuses it on native Windows, because that routing is for OpenClaw and ZeptoClaw proxy traffic and " +
        "DefenseClaw runs no guardrail proxy on Windows. Route-only routing of hook connectors is a separate path; the Overview's Model routing row reports it.";

    /// <summary>
    /// True when the runtime refuses <c>--enable</c> on Windows (1.0.5 or later), or when <paramref name="capabilities"/> is not known (null, or a runtime that has
    /// not answered yet): the card then leans to the newer releases, where offering the switch would only lead to a refusal. The catalog reads the card again when
    /// a probe changes the answer (<see cref="WizardCatalog"/>, ObserveRuntimeAsync).
    /// </summary>
    public static bool Applies(RuntimeCapabilities? capabilities) =>
        capabilities is null || !capabilities.IsKnown || capabilities.Has(RuntimeCapability.SavedFailModeWins);

    /// <summary>The sentence for <paramref name="capabilities"/>.</summary>
    public static string SentenceFor(RuntimeCapabilities? capabilities) =>
        capabilities is not null && capabilities.Has(RuntimeCapability.RoutingCli) ? RuntimeSentenceWithRouteOnly : RuntimeSentence;

    /// <summary>True for the one field this policy removes: <c>--enable</c> on the routing card, on a runtime that refuses it.</summary>
    public static bool Suppresses(string target, string flag, RuntimeCapabilities? capabilities = null) =>
        string.Equals(target, Target, StringComparison.Ordinal) && string.Equals(flag, EnableFlag, StringComparison.Ordinal) && Applies(capabilities);

    /// <summary>
    /// The routing card's first page, with the runtime's sentence after the CLI's own one-line summary. Every other target, the routing card's later pages, and a
    /// runtime that does not refuse <c>--enable</c> come back as they were.
    /// </summary>
    public static IReadOnlyList<WizardStep> AddNote(string target, IReadOnlyList<WizardStep> steps, RuntimeCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (!string.Equals(target, Target, StringComparison.Ordinal) || steps.Count == 0 || !Applies(capabilities))
        {
            return steps;
        }

        var first = steps[0];
        var sentence = SentenceFor(capabilities);
        var note = first.Subtitle.Length == 0 ? sentence : first.Subtitle + " " + sentence;
        var result = new List<WizardStep>(steps)
        {
            [0] = new WizardStep
            {
                Id = first.Id,
                Title = first.Title,
                Subtitle = note,
                Fields = first.Fields,
                VisibleWhenFieldId = first.VisibleWhenFieldId,
                VisibleWhenValues = first.VisibleWhenValues,
                Guide = first.Guide,
            },
        };
        return result;
    }
}
