namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The Setup "Routing" card (<c>defenseclaw setup routing</c>) on Windows (CUST-457).
/// <para>
/// <c>setup routing --enable</c> raises on native Windows and writes nothing (<c>cmd_setup.py</c> at 1.0.10: <c>setup_routing</c> checks
/// <c>_routing_unsupported_on_host()</c>, which is <c>os.name == "nt"</c>, and raises <c>_ROUTING_UNSUPPORTED_ON_WINDOWS</c>); <c>--disable</c> and
/// <c>--status</c> still work, and <c>--status</c> prints the same sentence. The card therefore does not offer <c>--enable</c>, keeps
/// <c>--disable</c> and <c>--status</c>, and says why on its first page in the runtime's own words.
/// </para>
/// <para>
/// This is a deliberate fix for every runtime that has the command, not gated: from 1.0.5 on the runtime refuses it (the check is in the 1.0.5 to
/// 1.0.10 source), and on 1.0.0 to 1.0.4 native Windows runs no guardrail proxy, so routing has no traffic to route and the switch could only
/// have left a setting nobody applies. 0.8.10 has no <c>setup routing</c> command, so no card.
/// </para>
/// </summary>
public static class RoutingSetupWindowsPolicy
{
    /// <summary>The card's target, the word after <c>setup</c>.</summary>
    public const string Target = "routing";

    /// <summary>The flag the card leaves out.</summary>
    public const string EnableFlag = "--enable";

    /// <summary>The runtime's own sentence (<c>_ROUTING_UNSUPPORTED_ON_WINDOWS</c>, cmd_setup.py@1.0.10), word for word.</summary>
    public const string RuntimeSentence =
        "Semantic routing is not supported on native Windows: it routes OpenClaw and ZeptoClaw proxy traffic, and DefenseClaw runs no guardrail proxy on Windows.";

    /// <summary>True for the one field this policy removes: <c>--enable</c> on the routing card.</summary>
    public static bool Suppresses(string target, string flag) =>
        string.Equals(target, Target, StringComparison.Ordinal) && string.Equals(flag, EnableFlag, StringComparison.Ordinal);

    /// <summary>
    /// The routing card's first page, with <see cref="RuntimeSentence"/> after the CLI's own one-line summary. Every other target, and the
    /// routing card's later pages, come back as they were.
    /// </summary>
    public static IReadOnlyList<WizardStep> AddNote(string target, IReadOnlyList<WizardStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (!string.Equals(target, Target, StringComparison.Ordinal) || steps.Count == 0)
        {
            return steps;
        }

        var first = steps[0];
        var note = first.Subtitle.Length == 0 ? RuntimeSentence : first.Subtitle + " " + RuntimeSentence;
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
