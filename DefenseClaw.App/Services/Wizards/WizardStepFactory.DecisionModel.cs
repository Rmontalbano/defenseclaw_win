namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The curated <c>setup decision-model</c> layout (DefenseClaw 1.0.7 and 1.0.8, CUST-446; the card is offered only on a runtime that has it, see
/// <see cref="DecisionModelCard"/>). Curated, not generated, for three reasons the generic pages cannot meet:
/// <list type="bullet">
///   <item><c>--non-interactive</c> must always be sent. The command prompts when it is not (and the app has no terminal to answer with), and
///     naming a value flag does not stop that: only <c>--non-interactive</c> does. It is on the last page, on and not changeable.</item>
///   <item><c>--connectors ""</c> (an empty string) is how the CLI is told to clear the list. A text box left empty sends nothing, so
///     "leave as it is", "all", "only these" and "clear" are four answers of one choice (<see cref="DecisionModelCard.ConnectorsModeFieldId"/>), and
///     the flag is sent from a field per answer.</item>
///   <item>The cascade the <c>--connectors</c> list picks from needs a sentence or two, next to the choice.</item>
/// </list>
/// Every flag the layout names is looked up in the live <c>--help</c> like the other curated pages: one the installed CLI does not have is not offered.
/// What the layout does not place falls to the trailing "More options" page (nothing is left unplaced today).
/// </summary>
public static partial class WizardStepFactory
{
    private static IReadOnlyList<WizardStep> DecisionModelSteps(ParsedHelp help)
    {
        // DefenseClaw 1.0.10 added --protocol (RuntimeCapability.DecisionModelProtocol: the version half is the runtime's, the help half is this).
        // The live help is the marker here because the layout is help-driven and gets no capabilities; a help without it (1.0.7 to 1.0.9) is laid
        // out exactly as before.
        var protocol = DecisionModelHasProtocol(help);

        WizardField? F(string flag, string label, string text, string? placeholder = null) =>
            FieldOf(help, flag, f => f.WithWording(label, text).WithHelp(text, placeholder));

        var steps = new List<WizardStep>
        {
            PageOf(
                "decision-model",
                "Decision model",
                protocol
                    ? "A small, fast model behind a protocol of its own (System One, or Gemini Decision 1). It is off until you turn it on; naming an endpoint or a model turns it on too. " +
                      "When it is down, the rules verdict (and the LLM judge, where it runs) stands."
                    : "A small, fast model that speaks the System One protocol. It is off until you turn it on; naming an endpoint or a model turns it on too. " +
                      "When it is down, the rules verdict (and the LLM judge, where it runs) stands.",
                NoGate,
                protocol ? ProtocolField(help) : null,
                F(
                    "--enable",
                    "Decision model",
                    "Turn the decision model on or off. Leave it unchanged to keep what config.yaml has."),
                F(
                    "--base-url",
                    "Endpoint URL",
                    protocol
                        ? "The full endpoint URL. https is accepted anywhere; http only to a loopback address. The URL must not carry the key (?key= is refused). " +
                          "Examples: http://127.0.0.1:8791/v1/systemone (System One), https://generativelanguage.googleapis.com/v1beta/decisions (Gemini Decision 1). " +
                          "Nothing is filled in for you: the decision model needs a URL and a model when it is turned on."
                        : "The full endpoint URL. https is accepted anywhere; http only to a loopback address.",
                    protocol
                        ? "http://127.0.0.1:8791/v1/systemone or https://generativelanguage.googleapis.com/v1beta/decisions"
                        : "http://127.0.0.1:8791/v1/systemone"),
                F(
                    "--model",
                    "Model",
                    protocol
                        ? "The model name the endpoint serves (gemini-decision-1 for Gemini Decision 1). It is sent in each request."
                        : "The model name the endpoint serves. It is sent in each request."),
                F(
                    "--api-key-env",
                    "Token variable name",
                    protocol
                        ? "The NAME of the environment variable that holds the token (DEFENSECLAW_DECISION_MODEL_KEY when left blank). A local server needs none. " +
                          "For Gemini Decision 1 the token is a Google AI Studio key, sent as x-goog-api-key; System One gets it as a bearer token. " +
                          "The token itself is never typed here: store it in a terminal with `defenseclaw keys set DEFENSECLAW_DECISION_MODEL_KEY` " +
                          "(it goes to ~/.defenseclaw/.env), and restart the gateway after storing a new one."
                        : "The NAME of the environment variable that holds the bearer token (DEFENSECLAW_DECISION_MODEL_KEY when left blank). A local server needs none. " +
                          "The token itself is never typed here: store it in a terminal with `defenseclaw keys set DEFENSECLAW_DECISION_MODEL_KEY` " +
                          "(it goes to ~/.defenseclaw/.env), and restart the gateway after storing a new one.",
                    "DEFENSECLAW_DECISION_MODEL_KEY"),
                F(
                    "--show",
                    "Only show the current settings",
                    "Print the saved decision model settings and exit; nothing is changed, and the answers on the other pages are ignored.")),
        };

        if (help.Option("--connectors") is not null)
        {
            steps.Add(ConnectorsPage(protocol));
        }

        steps.Add(PageOf(
            "decision-model-thresholds",
            "Thresholds",
            "How much the model decides on its own. Leave a field blank to keep the saved value; the CLI's own defaults are in each description.",
            NoGate,
            F(
                "--timeout",
                "Timeout (seconds)",
                "How long one call may take. The CLI's default is 1; it is never more than a quarter of the judge's time.",
                "1"),
            F(
                "--allow-threshold",
                "Allow threshold",
                "A risk at or below this is allowed without asking the LLM judge. The CLI's default is 0.30.",
                "0.30"),
            F(
                "--block-threshold",
                "Block threshold",
                protocol
                    ? "A risk at or above this (with a risk score at or above decision_model.block_risk_score) is a confident block. The CLI's default is 0.75."
                    : "A risk at or above this is a confident block. The CLI's default is 0.75.",
                "0.75"),
            // Retired at 1.0.10 (the CLI still accepts it and ignores it): not offered, never sent, and kept off the "More options" page.
            protocol
                ? null
                : F(
                    "--on-confident-block",
                    "On a confident block",
                    "escalate (the default): the judge still checks a confident block. decide: the block stands without the judge.")));

        steps.Add(PageOf(
            "decision-model-apply",
            "Apply",
            "After saving, the running gateway applies the change without a restart.",
            NoGate,
            F(
                "--ping",
                "Check the endpoint after saving",
                "After saving, send one benign request to the decision model endpoint. The settings are saved whether or not it answers. Off unless you turn it on."),
            NonInteractiveField(help)));

        return Compact(steps.ToArray());
    }

    /// <summary>
    /// The page for <c>--connectors</c>: one choice and, for each of its answers that sends the flag, a field that carries it. Only the field of the
    /// chosen answer is visible, so the command holds the flag at most once.
    /// </summary>
    private static WizardStep ConnectorsPage(bool protocol)
    {
        const string ModeId = DecisionModelCard.ConnectorsModeFieldId;

        var mode = new WizardField
        {
            Id = ModeId,
            Label = "Connector list",
            Kind = WizardFieldKind.Choice,
            Choices = new[]
            {
                new WizardChoice(DecisionModelCard.KeepConnectors, "Leave the list as it is"),
                new WizardChoice(DecisionModelCard.AllConnectors, "All connectors (*)"),
                new WizardChoice(DecisionModelCard.OnlyConnectors, "Only the connectors I name"),
                new WizardChoice(DecisionModelCard.ClearConnectors, "None: empty the list"),
            },
            DefaultValue = DecisionModelCard.KeepConnectors,
            BaselineValue = DecisionModelCard.KeepConnectors,
            IsSynthetic = true,
            Help = protocol
                ? "The decision model answers on its own for the connectors in this list, where the LLM judge does not run: a confident block (a risk at or above the block threshold " +
                  "and a risk score at or above decision_model.block_risk_score, on a call the rules did not flag) is denied, a confident allow is allowed, " +
                  "and anything else asks (Claude Code, Copilot, Amp, Antigravity) or raises a HIGH alert (strict mode blocks). " +
                  "Where the judge does run, every confident block goes to the judge and stands if the judge gives no verdict; the model allows the clear benign calls, " +
                  "and flow_guard can withdraw a confident allow. The last 25 turns (decision_model.history_turns) are sent. " +
                  "Each connector is on one of three paths: rules only; rules then decision model; rules then decision model then judge."
                : "The decision model answers on its own for the connectors in this list, where the LLM judge does not run: a confident allow is allowed, " +
                  "a confident block is denied, and anything else asks (Claude Code, Copilot, Amp, Antigravity) or raises a HIGH alert (strict mode blocks). " +
                  "Where the judge does run, the model sits in front of it: it allows the clear benign calls and sends the rest to the judge. " +
                  "Each connector is on one of three paths: rules only; rules then decision model; rules then decision model then judge.",
        };

        var only = new WizardField
        {
            Id = "connectors",
            Label = "Connectors",
            Kind = WizardFieldKind.Text,
            Flag = "--connectors",
            IsRequired = true,
            Placeholder = "claudecode,codex",
            Help = "Comma-separated connector names, for example claudecode,codex. This replaces the saved list.",
            VisibleWhenFieldId = ModeId,
            VisibleWhenValues = new[] { DecisionModelCard.OnlyConnectors },
        };

        // A box that holds the one value this answer sends, so it cannot be edited into something else.
        var all = new WizardField
        {
            Id = "connectors-all",
            Label = "Connectors",
            Kind = WizardFieldKind.Text,
            Flag = "--connectors",
            DefaultValue = "*",
            BaselineValue = string.Empty,
            DisabledReason = "Sent as * : every connector.",
            Help = "The decision model answers on its own for every connector where the LLM judge does not run.",
            VisibleWhenFieldId = ModeId,
            VisibleWhenValues = new[] { DecisionModelCard.AllConnectors },
        };

        // The empty string is the value (the CLI documents "an empty string clears it"); its baseline is something else so that it is sent.
        var clear = new WizardField
        {
            Id = "connectors-clear",
            Label = "Connectors",
            Kind = WizardFieldKind.Text,
            Flag = "--connectors",
            DefaultValue = string.Empty,
            BaselineValue = DecisionModelCard.ClearBaseline,
            AllowEmptyWhenChanged = true,
            DisabledReason = "Sent as an empty value, which empties the list: the model then answers for no connector on its own.",
            Help = "Empties decision_model.hook_connectors. Do this before going back to a release older than 1.0.7, which refuses the key.",
            VisibleWhenFieldId = ModeId,
            VisibleWhenValues = new[] { DecisionModelCard.ClearConnectors },
        };

        return new WizardStep
        {
            Id = "decision-model-connectors",
            Title = "Where it answers alone",
            Subtitle = "Which connectors the decision model decides for by itself, where the LLM judge does not run.",
            Fields = new[] { mode, only, all, clear },
        };
    }

    /// <summary>
    /// <c>--non-interactive</c>, on and not changeable: without it the CLI prompts for what a flag did not give, and the app has no terminal to
    /// answer with. Null when the installed CLI has no such flag.
    /// </summary>
    private static WizardField? NonInteractiveField(ParsedHelp help) => help.Option("--non-interactive") is null
        ? null
        : new WizardField
        {
            Id = "non-interactive",
            Label = "Do not prompt",
            Kind = WizardFieldKind.Switch,
            Flag = "--non-interactive",
            DefaultValue = ToggleValues.On,
            BaselineValue = ToggleValues.Off,
            DisabledReason = "Always sent: the app has no terminal to answer the CLI's prompts with.",
            Help = "Use the flags given instead of asking questions.",
        };

    /// <summary>True when the live <c>setup decision-model --help</c> has <c>--protocol</c>: DefenseClaw 1.0.10 and later.</summary>
    internal static bool DecisionModelHasProtocol(ParsedHelp help) => help.Option("--protocol") is not null;

    /// <summary>
    /// The help the "More options" page is built from: the 1.0.10 help without <c>--on-confident-block</c>, which that release retired (the layout
    /// deliberately does not offer it, so it must not fall to the generic page either). Every other help, and every other target, is returned as is.
    /// </summary>
    internal static ParsedHelp WithoutRetiredDecisionModelFlags(string target, ParsedHelp help) =>
        string.Equals(target, DecisionModelCard.Target, StringComparison.Ordinal) && DecisionModelHasProtocol(help)
            ? help with { Options = help.Options.Where(o => !o.Names.Contains("--on-confident-block", StringComparer.Ordinal)).ToArray() }
            : help;

    /// <summary>
    /// <c>--protocol</c> as a choice of the two wire protocols, from constants, kept to those the live help lists. Leaving it alone sends nothing
    /// (the CLI's default is System One).
    /// </summary>
    private static WizardField? ProtocolField(ParsedHelp help) =>
        FieldOf(help, "--protocol", f =>
        {
            var listed = help.Option("--protocol")!.Choices;
            var choices = new List<WizardChoice> { WizardChoice.Unchanged };
            if (listed.Contains(DecisionModelCard.SystemOneProtocol, StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(new WizardChoice(DecisionModelCard.SystemOneProtocol, "System One (default)"));
            }

            if (listed.Contains(DecisionModelCard.GeminiProtocol, StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(new WizardChoice(DecisionModelCard.GeminiProtocol, "Gemini Decision 1"));
            }

            return f.AsChoice(choices).WithWording(
                "Protocol",
                "The wire protocol of the endpoint: System One (the default) or Gemini Decision 1 (a Google AI Studio key). Leave it unchanged to keep what config.yaml has.");
        });
}
