using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>The two scopes a guardrail change can have (the TUI's <c>_GUARDRAIL_SCOPES</c>), spelled as it spells them.</summary>
public static class GuardrailScopes
{
    /// <summary>One active connector's own policy: its mode, rule pack, block message and approval. Nothing process-wide is touched.</summary>
    public const string Connector = "selected-connector";

    /// <summary>Every active connector at once: the process-wide settings (scanner mode, port, detection strategy, the judge, Cisco AI Defense) and the shared policy.</summary>
    public const string Global = "global-all-active";
}

/// <summary>
/// The connectors that are active when a guardrail wizard opens (<c>Config.active_connectors()</c>): the keys of <c>guardrail.connectors</c>, or the
/// one connector <c>guardrail.connector</c> / <c>claw.mode</c> names, or none. With two or more there is a fleet, and a change can be scoped to one
/// member of it - which is the point of the Scope step.
/// </summary>
/// <param name="Active">The roster, normalised (<c>claude-code</c> and <c>claudecode</c> are one connector) and sorted, as the CLI lists it.</param>
/// <param name="Modes">Each member's mode as config.yaml gives it (its own override, else the global one, else observe), for the sentence on the Scope page.</param>
public sealed record GuardrailScopeContext(IReadOnlyList<string> Active, IReadOnlyDictionary<string, string> Modes)
{
    /// <summary>No roster known: a wizard built from <c>--help</c> alone, or a config that could not be read.</summary>
    public static GuardrailScopeContext Empty { get; } = new(Array.Empty<string>(), new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>True when more than one connector is active, so "which one" is a question and a global change reaches them all.</summary>
    public bool HasFleet => Active.Count > 1;

    /// <summary>What the Scope step starts on: one connector when there is a fleet to choose from (the TUI's rule), every connector otherwise.</summary>
    public string DefaultScope => HasFleet ? GuardrailScopes.Connector : GuardrailScopes.Global;

    /// <summary>True when <paramref name="connector"/> is on the roster, however it is spelled.</summary>
    public bool IsActive(string connector) => Active.Contains(Normalize(connector), StringComparer.Ordinal);

    /// <summary>The roster in words: "claudecode (action), codex (observe)", or empty when it is not known.</summary>
    public string Describe() => string.Join(
        ", ",
        Active.Select(name => Modes.TryGetValue(name, out var mode) && mode.Length > 0 ? $"{name} ({mode})" : name));

    /// <summary>The CLI's own comparison of connector names: letters and digits only, lower case.</summary>
    public static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Reads the roster out of config.yaml's typed model (and the global mode out of its text, which the typed model does not carry).</summary>
    public static GuardrailScopeContext From(ConfigDocument config, string? globalMode)
    {
        ArgumentNullException.ThrowIfNull(config);

        var guardrail = config.Config.Guardrail;
        var members = guardrail.Connectors.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToDictionary(k => Normalize(k), k => guardrail.Connectors[k], StringComparer.Ordinal);

        var inherited = string.IsNullOrWhiteSpace(globalMode) ? "observe" : globalMode.Trim().ToLowerInvariant();
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (members.Count > 0)
        {
            foreach (var (name, settings) in members)
            {
                modes[name] = string.IsNullOrWhiteSpace(settings.Mode) ? inherited : settings.Mode.Trim().ToLowerInvariant();
            }

            return new GuardrailScopeContext(members.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), modes);
        }

        // No fleet map: the one connector config.yaml names, if any.
        var single = !string.IsNullOrWhiteSpace(guardrail.Connector) ? guardrail.Connector : config.Config.Claw.Mode;
        if (string.IsNullOrWhiteSpace(single))
        {
            return Empty;
        }

        var only = Normalize(single);
        modes[only] = inherited;
        return new GuardrailScopeContext(new[] { only }, modes);
    }
}

/// <summary>
/// The guardrail wizard's Scope step: the rule that keeps a change to one connector from reaching the others, and the Disable branch.
/// <para>
/// <b>Why a scope.</b> <c>setup guardrail</c> mixes two kinds of setting. Mode, rule pack, block message and human approval can belong to one
/// connector; the scanner mode, the proxy port, the detection strategy, the judge and Cisco AI Defense belong to the process and so to every
/// connector. Run with <c>--connector X</c>, the CLI scopes the first kind to X - and a <c>--port</c> or <c>--judge-model</c> riding along in the
/// same command would still be written for everyone. The wizard therefore never lets the two share a command: the pages that hold global settings
/// are shown only in the global scope (see <see cref="WizardStepFactory"/>), and the command is built through <see cref="BuildArgv"/>, which in
/// the connector scope drops, whatever a page holds, every flag that is not on <see cref="ConnectorFlags"/> - the TUI's
/// <c>_GUARDRAIL_CONNECTOR_SETUP_FLAGS</c> (0.8.10, <c>tui/panels/setup.py</c>) plus the no-prompt switch, which this app sends as a field where
/// the TUI hard-codes it.
/// </para>
/// <para>
/// <b>Disable</b> is a different command, as in the TUI: <c>guardrail disable --yes [--connector X] [--no-restart]</c>. <c>setup guardrail --disable</c>
/// always disables the primary connector's guardrail globally and restarts; <c>guardrail disable --connector X</c> disables one connector and leaves
/// its peers (and keeps its policy for a later enable).
/// </para>
/// </summary>
public static class GuardrailScope
{
    /// <summary>The id of the synthetic field that holds the scope.</summary>
    public const string ScopeFieldId = "scope";

    /// <summary>The flags a command scoped to one connector may carry.</summary>
    public static readonly IReadOnlySet<string> ConnectorFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--connector",
        "--mode",
        "--rule-pack",
        "--rule-pack-dir",
        "--block-message",
        "--human-approval",
        "--hilt-min-severity",
        "--restart",
        "--verify",
        "--non-interactive",
    };

    private static string ConnectorId => WizardFieldBuilder.Identifier("--connector");

    private static string DisableId => WizardFieldBuilder.Identifier("--disable");

    private static string RestartId => WizardFieldBuilder.Identifier("--restart");

    /// <summary>The scope the answers hold. Anything but "every connector" is read as one connector: the narrower reading, the safer one.</summary>
    public static string ScopeOf(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Equals(values[ScopeFieldId], GuardrailScopes.Global, StringComparison.Ordinal)
            ? GuardrailScopes.Global
            : GuardrailScopes.Connector;
    }

    /// <summary>
    /// Fits the Scope step to a roster: the connector list narrows to the active members when there is a fleet (the TUI's
    /// <c>_guardrail_connector_choices</c>), the connector field is asked only for one connector when there is a fleet (a global change reaches them
    /// all, so naming one would be a lie), and the step starts on the scope the roster suggests. The command and the check across fields are the
    /// roster's too. Returns <paramref name="definition"/> unchanged when it has no Scope step.
    /// </summary>
    public static WizardDefinition Install(WizardDefinition definition, GuardrailScopeContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        if (!definition.AllFields.Any(f => string.Equals(f.Id, ScopeFieldId, StringComparison.Ordinal)))
        {
            return definition;
        }

        var steps = definition.Steps.Select(step => Fit(step, context)).ToArray();
        var hasConnector = definition.AllFields.Any(f => string.Equals(f.Id, ConnectorId, StringComparison.Ordinal));

        return definition
            .With(steps, definition.BaselineNote, definition.BaselineWarning)
            .WithCrossValidator(values => Validate(values, context, hasConnector))
            .WithFinalArgvBuilder((def, values) => BuildArgv(def, values));
    }

    private static WizardStep Fit(WizardStep step, GuardrailScopeContext context)
    {
        if (!step.Fields.Any(f => string.Equals(f.Id, ScopeFieldId, StringComparison.Ordinal) || string.Equals(f.Id, ConnectorId, StringComparison.Ordinal)))
        {
            return step;
        }

        var fields = step.Fields.Select(field =>
        {
            if (string.Equals(field.Id, ScopeFieldId, StringComparison.Ordinal))
            {
                return field.WithAnswers(context.DefaultScope, context.DefaultScope, field.BaselineSource);
            }

            if (!string.Equals(field.Id, ConnectorId, StringComparison.Ordinal) || !context.HasFleet)
            {
                return field;
            }

            var members = field.Choices
                .Where(c => c.Value.Length == 0 || context.IsActive(c.Value))
                .ToArray();

            // A fleet needs a deliberate pick, so the field starts blank rather than on the configured primary; and it is a question only
            // for one connector.
            return field
                .WithChoices(members)
                .WithAnswers(string.Empty, string.Empty, field.BaselineSource)
                .WithGate(GuardrailGates.ConnectorScope.FieldIds, GuardrailGates.ConnectorScope.Values);
        }).ToArray();

        return new WizardStep
        {
            Id = step.Id,
            Title = step.Title,
            Subtitle = context.Active.Count > 0
                ? step.Subtitle + " Active connectors: " + context.Describe() + "."
                : step.Subtitle,
            Fields = fields,
            VisibleWhenFieldId = step.VisibleWhenFieldId,
            VisibleWhenValues = step.VisibleWhenValues,
            Guide = step.Guide,
        };
    }

    /// <summary>
    /// The checks across fields. In the connector scope a connector must be named and, when there is a fleet, must be one of its active members
    /// (the TUI's <c>_guardrail_connector_selection_error</c>: "Connector 'x' is not active. Active connectors: a, b."); turning the guardrail off
    /// with no connector named would switch it off for all of them, so that check holds for Disable too. The CLI treats a rule pack and a rule
    /// pack directory as mutually exclusive, so naming both is refused here, before the CLI would.
    /// </summary>
    internal static string? Validate(WizardValues values, GuardrailScopeContext context, bool hasConnectorField)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var connectorScope = string.Equals(ScopeOf(values), GuardrailScopes.Connector, StringComparison.Ordinal);
        if (hasConnectorField && connectorScope)
        {
            var connector = values[ConnectorId].Trim();
            if (connector.Length == 0)
            {
                return "Choose the connector this change is for, or set the scope to every active connector.";
            }

            if (context.HasFleet && !context.IsActive(connector))
            {
                return $"Connector '{connector}' is not active. Active connectors: {string.Join(", ", context.Active)}.";
            }
        }

        if (IsOn(values[DisableId]))
        {
            return null;
        }

        var prefix = connectorScope ? GuardrailFields.ConnectorPrefix : string.Empty;
        if (values[prefix + "rule-pack"].Trim().Length > 0 && values[prefix + "rule-pack-dir"].Trim().Length > 0)
        {
            return "Use either a rule pack or a rule pack directory, not both: the CLI treats them as mutually exclusive.";
        }

        return null;
    }

    /// <summary>
    /// The command. Disable is <c>guardrail disable</c>; everything else is <c>setup guardrail</c>, and in the connector scope it is built over
    /// the allow-listed flags only.
    /// </summary>
    internal static IReadOnlyList<string> BuildArgv(WizardDefinition definition, WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);

        var scope = ScopeOf(values);
        if (IsOn(values[DisableId]))
        {
            return DisableArgv(scope, values[ConnectorId].Trim(), string.Equals(values[RestartId], ToggleValues.Off, StringComparison.OrdinalIgnoreCase));
        }

        var scoped = string.Equals(scope, GuardrailScopes.Connector, StringComparison.Ordinal)
            ? definition.OnlyFields(f => f.Flag is null || ConnectorFlags.Contains(f.Flag))
            : definition;

        return WizardDefinition.BuildArgvDefault(scoped, values);
    }

    /// <summary>
    /// <c>guardrail disable --yes</c>, with <c>--connector</c> in the connector scope and <c>--no-restart</c> when the operator turned the restart
    /// off. <c>--yes</c> answers the CLI's "Continue?", which this app cannot; the review before it is where the operator decides.
    /// </summary>
    internal static IReadOnlyList<string> DisableArgv(string scope, string connector, bool skipRestart)
    {
        var argv = new List<string> { "guardrail", "disable", "--yes" };
        if (string.Equals(scope, GuardrailScopes.Connector, StringComparison.Ordinal) && connector.Length > 0)
        {
            argv.Add("--connector");
            argv.Add(connector);
        }

        if (skipRestart)
        {
            argv.Add("--no-restart");
        }

        return argv;
    }

    /// <summary>True for the command the Disable branch builds.</summary>
    public static bool IsDisableCommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Count >= 2 &&
               string.Equals(argv[0], "guardrail", StringComparison.Ordinal) &&
               string.Equals(argv[1], "disable", StringComparison.Ordinal);
    }

    private static bool IsOn(string value) => string.Equals(value, ToggleValues.On, StringComparison.OrdinalIgnoreCase);

    /// <summary>The words a Scope page uses for one choice (a label, so a screen reader reads what the value means).</summary>
    internal static string Label(string scope) => string.Equals(scope, GuardrailScopes.Global, StringComparison.Ordinal)
        ? "Every active connector (global)"
        : "One connector";
}

/// <summary>The gate clauses of the guardrail layout, in one place so a page and a field cannot spell a condition two ways.</summary>
internal static class GuardrailGates
{
    public static WizardGate.Clause ConnectorScope { get; } = WizardGate.When(GuardrailScope.ScopeFieldId, GuardrailScopes.Connector);

    public static WizardGate.Clause GlobalScope { get; } = WizardGate.When(GuardrailScope.ScopeFieldId, GuardrailScopes.Global);

    /// <summary>"The guardrail is being configured, not turned off": the switch is off or the CLI has no such switch.</summary>
    public static WizardGate.Clause NotDisabled { get; } =
        WizardGate.When(WizardFieldBuilder.Identifier("--disable"), ToggleValues.Off, string.Empty);

    /// <summary>A judge strategy is chosen, so the judge is in use.</summary>
    public static WizardGate.Clause JudgeStrategy { get; } =
        WizardGate.When(WizardFieldBuilder.Identifier("--detection-strategy"), "regex_judge", "judge_first");
}

/// <summary>Ids of the guardrail layout's per-connector variants.</summary>
internal static class GuardrailFields
{
    /// <summary>
    /// A flag that can belong to one connector is offered twice: with the global value from config.yaml for the global scope, and starting blank
    /// ("leave unchanged") under this prefix for one connector, whose own value config.yaml's global one is not.
    /// </summary>
    public const string ConnectorPrefix = "connector-";
}
