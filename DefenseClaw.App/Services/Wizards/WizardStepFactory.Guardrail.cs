namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The curated <c>setup guardrail</c> layout: a Scope step first, then the pages for the scope it picked (see <see cref="GuardrailScope"/> for why).
/// <para>
/// <b>Which page shows when.</b> The Scope page is always there. Mode and rules, and Human approval, are there in both scopes - in the connector
/// scope as fields that start blank ("leave unchanged"), in the global scope as fields that start from config.yaml. Scanner and detection, the judge
/// (and only for a judge strategy), Cisco AI Defense and the workspace are global-only. Turning the guardrail off hides every one of them but the
/// restart choice. Every condition is a <see cref="WizardGate"/>, built from the same few clauses (<see cref="GuardrailGates"/>).
/// </para>
/// <para>
/// Flags the layout names are looked up in the live help like every curated page: the judge is not gated on a detection strategy the CLI does not
/// have, the Disable branch needs the CLI's <c>--disable</c>, and what the layout does not place is on the trailing "More options" page, which is
/// global-only as well - every flag a connector-scoped command may carry is placed on a page.
/// </para>
/// </summary>
public static partial class WizardStepFactory
{
    private static IReadOnlyList<WizardStep> GuardrailSteps(ParsedHelp help)
    {
        WizardField? F(string flag, Func<WizardField, WizardField>? tweak = null) => FieldOf(help, flag, tweak);

        var hasDisable = help.Option("--disable") is not null;
        var hasStrategy = help.Option("--detection-strategy") is not null;

        WizardGate.Clause[] NotDisabled() => hasDisable ? new[] { GuardrailGates.NotDisabled } : Array.Empty<WizardGate.Clause>();

        (string? FieldId, IReadOnlyList<string> Values) Gate(params WizardGate.Clause[] clauses) => GateOf(clauses.Concat(NotDisabled()).ToArray());

        // A field for the global scope: shown there, and starting from what config.yaml says.
        WizardField Global(WizardField f) => f.AndGate(GuardrailGates.GlobalScope);

        // The same flag for one connector: shown in that scope, starting blank, with an id of its own so the answers do not meet.
        WizardField Connector(WizardField f) => f
            .WithId(GuardrailFields.ConnectorPrefix + f.Id)
            .StartingBlank()
            .AndGate(GuardrailGates.ConnectorScope)
            .WithHelp("For the selected connector only. " + f.Help + " Leave it unchanged to keep what that connector has now.");

        // The flag once for each scope; only one of the two is ever shown.
        WizardField? Both(string flag, out WizardField? connector)
        {
            connector = F(flag, Connector);
            return F(flag, Global);
        }

        var steps = new List<WizardStep>();

        // ---- Scope
        steps.Add(PageOf(
            WizardPages.GuardrailScope,
            "Scope",
            "Change one connector's own policy, or the settings every active connector shares. Turning the guardrail off is chosen here too.",
            NoGate,
            ScopeField(),
            F(
                "--connector",
                f => f.WithHelp(f.Help + " Required when the scope is one connector; a command scoped to a connector never changes its peers.")),
            F(
                "--disable",
                f => f.WithWording(
                    "Turn the guardrail off",
                    "Instead of configuring it, disable the guardrail - for the selected connector, or for every connector when the scope is global - " +
                    "and let the gateway tear the connector's hooks down. The connector's policy is kept for a later enable."))));

        // ---- Mode, rules, approval: in both scopes
        var mode = Both("--mode", out var modeC);
        var pack = Both("--rule-pack", out var packC);
        var packDir = Both("--rule-pack-dir", out var packDirC);
        var message = Both("--block-message", out var messageC);
        steps.Add(PageOf(
            WizardPages.GuardrailMode,
            "Mode and rules",
            "Observe records what would be blocked; action blocks it. For one connector, a field left unchanged keeps what that connector has now.",
            Gate(),
            mode,
            modeC,
            pack,
            packC,
            packDir,
            packDirC,
            message,
            messageC));

        var approval = Both("--human-approval", out var approvalC);
        var severity = Both("--hilt-min-severity", out var severityC);
        steps.Add(PageOf(
            WizardPages.GuardrailApproval,
            "Human approval",
            "Ask a person before a high-risk action goes through, from the severity you choose upward.",
            Gate(),
            approval,
            approvalC,
            severity,
            severityC));

        // ---- Global only: scanners and detection, the judge, Cisco AI Defense
        steps.Add(PageOf(
            WizardPages.GuardrailDetection,
            "Scanner and detection",
            "Scanner lane, proxy port and detection strategy. These are process-wide: they apply to every active connector. Per-direction strategies are opt-in.",
            Gate(GuardrailGates.GlobalScope),
            F("--scanner-mode"),
            F("--port"),
            F("--detection-strategy"),
            F("--detection-strategy-prompt"),
            F("--detection-strategy-completion"),
            F("--detection-strategy-tool-call")));

        var judgeConditions = new List<WizardGate.Clause> { GuardrailGates.GlobalScope };
        if (hasStrategy)
        {
            judgeConditions.Add(GuardrailGates.JudgeStrategy);
        }

        var judgeProvider = WizardFieldBuilder.Identifier("--judge-provider");
        var judgeInstance = WizardFieldBuilder.Identifier("--judge-instance-name");
        steps.Add(PageOf(
            WizardPages.GuardrailJudge,
            "LLM judge",
            "The judge model and how it authenticates; it is used because the detection strategy includes the judge. Key material is referenced by environment-variable NAME, which is what config.yaml stores.",
            Gate(judgeConditions.ToArray()),
            F("--judge-provider", AsProviderChoice),
            F("--judge-model", f => f.WithPicker(WizardFieldPickers.Model, judgeProvider, judgeInstance)),
            F("--judge-api-key-env"),
            F("--judge-api-base"),
            F("--judge-instance-name"),
            F("--judge-hook-connectors"),
            F("--llm-role"),
            F("--inherit-from"),
            F("--inherit-llm")));

        steps.AddRange(
            new CloudGroups
            {
                Field = flag => FieldOf(help, flag),
                IdOf = flag => IdOf(flag),
                FlagPrefix = "--judge-",
                ProviderFieldId = judgeProvider,
                HasProviderField = help.Option("--judge-provider") is not null,
                Conditions = judgeConditions.Concat(NotDisabled()).ToArray(),
                VertexValues = new[] { "vertex_ai", "vertex" },
                BedrockPage = WizardPages.GuardrailJudgeBedrock,
                VertexPage = WizardPages.GuardrailJudgeVertex,
                AzurePage = WizardPages.GuardrailJudgeAzure,
                TlsPage = WizardPages.GuardrailJudgeTls,
                Title = "Judge: ",
            }.Build());

        steps.Add(PageOf(
            WizardPages.GuardrailCisco,
            "Cisco AI Defense",
            "Remote scanning through Cisco AI Defense, shared by every active connector. Only used when the scanner mode includes remote.",
            Gate(GuardrailGates.GlobalScope),
            F("--cisco-endpoint"),
            F("--cisco-api-key-env"),
            F("--cisco-timeout-ms")));

        // ---- Apply: the restart is asked in every case, because turning the guardrail off restarts the gateway too
        steps.Add(PageOf(
            WizardPages.GuardrailApply,
            "Apply",
            "How the change is written. Turning the guardrail off restarts the gateway so the connector's hooks are torn down.",
            NoGate,
            F("--workspace", f => f.AndGate(GuardrailGates.GlobalScope).AndGate(NotDisabled())),
            F("--restart"),
            F("--verify", f => f.AndGate(NotDisabled())),
            F("--non-interactive", f => f.AndGate(NotDisabled()))));

        return Compact(steps.ToArray());
    }

    /// <summary>The trailing "More options" page of the guardrail holds only global-only flags, so it is shown only in the global scope and not while turning the guardrail off.</summary>
    private static (string? FieldId, IReadOnlyList<string> Values) GuardrailRemainderGate(ParsedHelp help) =>
        GateOf(
            help.Option("--disable") is null
                ? new[] { GuardrailGates.GlobalScope }
                : new[] { GuardrailGates.GlobalScope, GuardrailGates.NotDisabled });

    /// <summary>The choice that decides which pages follow. Never a command-line option (see <see cref="WizardField.IsSynthetic"/>).</summary>
    private static WizardField ScopeField() => new()
    {
        Id = GuardrailScope.ScopeFieldId,
        Label = "Scope",
        Kind = WizardFieldKind.Choice,
        Choices = new[]
        {
            new WizardChoice(GuardrailScopes.Connector, "One connector: its own mode, rules and approval"),
            new WizardChoice(GuardrailScopes.Global, "Every active connector: shared settings, scanners and the judge"),
        },
        DefaultValue = GuardrailScopes.Global,
        BaselineValue = GuardrailScopes.Global,
        Help = "Selected connector changes only that connector's policy, and no process-wide setting can ride along. Every active connector " +
               "exposes the settings that apply to all of them, and changes the shared policy for all of them.",
        IsSynthetic = true,
    };
}
