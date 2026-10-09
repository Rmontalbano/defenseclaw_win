using System.Collections.ObjectModel;
using System.Globalization;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// How a connector or component fares on this platform, straight from the CLI's own
/// <c>Platform status on windows:</c> line.
/// <para>
/// On the 0.8.7 install this app was built against, exactly two connectors carry no
/// status line and are therefore certified: <c>claude-code</c> and <c>codex</c>. Every
/// other connector declares <c>not_certified</c> or <c>unsupported</c>. That is a fact
/// about the CLI, not a constant in this app — the value here is always whatever the
/// live <c>--help</c> said, and <see cref="Unknown"/> is used when the help could not be
/// read at all rather than guessing.
/// </para>
/// </summary>
public enum PlatformStatus
{
    /// <summary>Help could not be parsed. Neutral badge, never a green light.</summary>
    Unknown = 0,

    /// <summary>No platform-status line: the integration is certified here.</summary>
    Certified,

    /// <summary>Declared <c>not_certified</c>. Launchable, but the warning follows the user in.</summary>
    NotCertified,

    /// <summary>Declared <c>unsupported</c>: the feature cannot work on this platform at all.</summary>
    Unsupported,

    /// <summary>
    /// The target is not a connector, so certification does not apply to it: rotate-token,
    /// webhook, llm, remove, the scanners and the like carry no <c>Platform status on
    /// windows:</c> line because there is nothing to certify — not because they passed.
    /// Appended after <see cref="Unsupported"/> so no existing numeric value moves.
    /// Neutral badge, no in-wizard warning, and never counted as certified.
    /// </summary>
    NotApplicable,
}

/// <summary>The control a field renders as, and how it turns into argv.</summary>
public enum WizardFieldKind
{
    /// <summary><c>--flag VALUE</c> when non-empty.</summary>
    Text,

    /// <summary>Same as <see cref="Text"/>, rendered with a Browse button.</summary>
    Path,

    /// <summary>Same as <see cref="Text"/>, validated as an integer.</summary>
    Integer,

    /// <summary>Combo box; <c>--flag VALUE</c> when a value is picked.</summary>
    Choice,

    /// <summary>Bare switch with no negative form: <c>--flag</c> when on, absent when off.</summary>
    Switch,

    /// <summary>
    /// Paired flag (<c>--restart / --no-restart</c>). Tri-state on purpose: "leave
    /// unchanged" emits nothing, so a wizard never writes a setting the operator did not
    /// ask about.
    /// </summary>
    Toggle,

    /// <summary>
    /// A flag that takes a real secret (or, for <c>setup galileo</c>, the variable a secret with no flag is read from —
    /// see <see cref="WizardSyntheticSecrets"/>). Never reaches argv (<see cref="Core.Cli.CliRunner"/> would refuse
    /// it) and — verified on 0.8.10 — is never read from stdin by the CLI either. The field renders as a
    /// credential card that names the environment variable and points the operator at
    /// <c>defenseclaw keys set</c> in a real terminal; where the CLI also reads the secret from an environment
    /// variable, the card has a password box too, and the value reaches that one run in the child's
    /// environment (<see cref="Core.Cli.CliRunOptions.EnvironmentOverlay"/>), never in <see cref="WizardValues"/>.
    /// See <see cref="SecretRoute"/> for the evidence.
    /// </summary>
    Secret,

    /// <summary>
    /// The NAME of an environment variable that holds a secret. Safe in argv, and it is
    /// what DefenseClaw actually persists: config.yaml stores <c>*_env</c> names, never
    /// values.
    /// </summary>
    EnvVarName,

    /// <summary>
    /// A flag documented "(repeatable)": a multi-line box, one value per line, emitted as one
    /// <c>--flag value</c> pair per non-empty line. Appended after <see cref="EnvVarName"/> so no
    /// existing numeric value moves.
    /// </summary>
    Lines,

    /// <summary>A decimal number (Click <c>FLOAT</c>): rendered as text, validated as a number.</summary>
    Number,
}

/// <summary>Tri-state values a <see cref="WizardFieldKind.Toggle"/> can hold.</summary>
public static class ToggleValues
{
    public const string Unset = "";
    public const string On = "true";
    public const string Off = "false";
}

/// <summary>One option in a <see cref="WizardFieldKind.Choice"/> combo.</summary>
/// <param name="Value">What lands in argv. Empty means "leave unchanged".</param>
/// <param name="Label">What the operator reads.</param>
public sealed record WizardChoice(string Value, string Label)
{
    public static WizardChoice Unchanged { get; } = new(string.Empty, "(leave unchanged)");

    public static WizardChoice Of(string value) => new(value, value);
}

/// <summary>
/// One input in a wizard step, and the CLI flag it maps to.
/// <para>
/// Fields are immutable definitions; the operator's answers live in the view-model, keyed
/// by <see cref="Id"/>. That split is what lets the catalog rebuild definitions from a
/// fresh <c>--help</c> parse without disturbing an open wizard.
/// </para>
/// </summary>
public sealed class WizardField
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required WizardFieldKind Kind { get; init; }

    /// <summary>Long flag, e.g. <c>--mode</c>. Null for a positional argument.</summary>
    public string? Flag { get; init; }

    /// <summary>Negative form of a paired flag, e.g. <c>--no-restart</c>.</summary>
    public string? NegativeFlag { get; init; }

    /// <summary>Help text, taken from the CLI's own description where possible.</summary>
    public string Help { get; init; } = string.Empty;

    public IReadOnlyList<WizardChoice> Choices { get; init; } = Array.Empty<WizardChoice>();

    /// <summary>
    /// The answer the field starts with: the current configuration when it is known
    /// (<see cref="BaselineSource"/> names where it came from), otherwise the CLI's documented default.
    /// For toggles: <see cref="ToggleValues"/>.
    /// </summary>
    public string DefaultValue { get; init; } = string.Empty;

    /// <summary>
    /// What the setting <b>is</b> when this flag is left off the command line: the CLI's own default for
    /// an option that has one (<c>--mode</c> falls back to <c>observe</c>), otherwise the current
    /// configuration (an option that defaults to None leaves the stored value alone). A field is written
    /// into argv only when its answer differs from this, so a wizard run never re-sends — or, worse,
    /// silently overrides — a setting the operator did not touch. See <see cref="WizardDefinition.BuildArgvDefault"/>.
    /// </summary>
    public string BaselineValue { get; init; } = string.Empty;

    /// <summary>Where <see cref="DefaultValue"/> was read from ("config.yaml guardrail.connectors.claudecode"), or empty.</summary>
    public string BaselineSource { get; init; } = string.Empty;

    /// <summary>
    /// True when emptying the field is a real change to send as <c>--flag ""</c> (the CLI documents
    /// "pass empty to clear" for <c>--block-message</c> and <c>--rule-pack-dir</c>).
    /// </summary>
    public bool AllowEmptyWhenChanged { get; init; }

    /// <summary>
    /// For a <see cref="WizardFieldKind.Secret"/> field: how the secret reaches the CLI without passing
    /// through this app. Always set on secret fields by <see cref="SecretRoutes.Annotate"/>.
    /// </summary>
    public SecretRoute? Credential { get; init; }

    /// <summary>
    /// The environment variable a <b>synthetic</b> secret field stands for (a secret the CLI reads from its
    /// environment but has no flag for — <c>setup galileo</c>'s <c>GALILEO_API_KEY</c>). Empty for every field
    /// that maps to a flag or a positional. Only shown, in place of the flag chip.
    /// </summary>
    public string ViaEnvironment { get; init; } = string.Empty;

    public bool IsPositional { get; init; }

    /// <summary>Ordering among positionals; positionals always precede flags in argv.</summary>
    public int PositionalOrder { get; init; }

    public bool IsRequired { get; init; }

    public string Placeholder { get; init; } = string.Empty;

    /// <summary>
    /// Only rendered (and only emitted into argv) when the field named here holds one of
    /// <see cref="VisibleWhenValues"/>. Used for subcommand-scoped flags.
    /// </summary>
    public string? VisibleWhenFieldId { get; init; }

    public IReadOnlyList<string> VisibleWhenValues { get; init; } = Array.Empty<string>();

    /// <summary>True when the value must never appear on the command line.</summary>
    public bool IsSecret => Kind == WizardFieldKind.Secret;

    public string FlagDisplay => Kind == WizardFieldKind.Toggle && NegativeFlag is { Length: > 0 }
        ? $"{Flag} / {NegativeFlag}"
        : Flag ?? (ViaEnvironment.Length > 0 ? "env " + ViaEnvironment : "(positional)");

    /// <summary>
    /// A copy that starts from <paramref name="defaultValue"/> (the current configuration) and treats
    /// <paramref name="baselineValue"/> as "what omitting the flag leaves". Fields are immutable
    /// definitions shared by every open wizard, so applying the operator's configuration means
    /// building new ones, never editing these.
    /// </summary>
    public WizardField WithAnswers(string defaultValue, string baselineValue, string source) =>
        Clone(defaultValue, baselineValue, source, Credential);

    /// <summary>A copy carrying <paramref name="route"/>.</summary>
    public WizardField WithCredential(SecretRoute route) =>
        Clone(DefaultValue, BaselineValue, BaselineSource, route);

    /// <summary>A copy the operator reads differently — the flag, kind and every answer are unchanged.</summary>
    public WizardField WithWording(string label, string help) =>
        Clone(DefaultValue, BaselineValue, BaselineSource, Credential, label, help);

    private WizardField Clone(
        string defaultValue,
        string baselineValue,
        string source,
        SecretRoute? credential,
        string? label = null,
        string? help = null) => new()
    {
        Id = Id,
        Label = label ?? Label,
        Kind = Kind,
        Flag = Flag,
        NegativeFlag = NegativeFlag,
        Help = help ?? Help,
        Choices = Choices,
        DefaultValue = defaultValue,
        BaselineValue = baselineValue,
        BaselineSource = source,
        AllowEmptyWhenChanged = AllowEmptyWhenChanged,
        Credential = credential,
        ViaEnvironment = ViaEnvironment,
        IsPositional = IsPositional,
        PositionalOrder = PositionalOrder,
        IsRequired = IsRequired,
        Placeholder = Placeholder,
        VisibleWhenFieldId = VisibleWhenFieldId,
        VisibleWhenValues = VisibleWhenValues,
    };
}

/// <summary>One page of a wizard. The review page is synthesised by the view-model, not defined here.</summary>
public sealed class WizardStep
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public IReadOnlyList<WizardField> Fields { get; init; } = Array.Empty<WizardField>();

    /// <summary>Step-level gate, same semantics as <see cref="WizardField.VisibleWhenFieldId"/>.</summary>
    public string? VisibleWhenFieldId { get; init; }

    public IReadOnlyList<string> VisibleWhenValues { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set on a guided first step: the cards the page shows (and, for a choice card, the switch it turns on, which is one of
    /// <see cref="Fields"/>). A guide page renders the cards instead of the field list.
    /// </summary>
    public WizardGuide? Guide { get; init; }
}

/// <summary>The answers collected so far, keyed by <see cref="WizardField.Id"/>.</summary>
public sealed class WizardValues
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string this[string fieldId]
    {
        get => _values.TryGetValue(fieldId, out var value) ? value : string.Empty;
        set => _values[fieldId] = value ?? string.Empty;
    }

    public bool Has(string fieldId) => _values.ContainsKey(fieldId);

    public IReadOnlyDictionary<string, string> Snapshot() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_values, StringComparer.Ordinal));
}

/// <summary>Sidebar-style grouping for the Setup hub's card grid.</summary>
public static class WizardGroups
{
    public const string Connectors = "Connectors";
    public const string Scanners = "Scanners";
    public const string GuardrailAndPolicy = "Guardrail & Policy";
    public const string Credentials = "Credentials";
    public const string Observability = "Observability";
    public const string Other = "Other";

    /// <summary>
    /// Targets that exist in the CLI but cannot work here (unsupported connectors, interactive-only
    /// wizards, Docker stacks). Shown last, disabled, each with its reason — never silently hidden.
    /// </summary>
    public const string Unavailable = "Not available on this machine";

    /// <summary>Display order in the hub.</summary>
    public static readonly IReadOnlyList<string> Ordered = new[]
    {
        Connectors,
        GuardrailAndPolicy,
        Scanners,
        Observability,
        Credentials,
        Other,
        Unavailable,
    };

    public static int IndexOf(string group)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (string.Equals(Ordered[i], group, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Ordered.Count;
    }
}

/// <summary>
/// One <c>defenseclaw setup &lt;target&gt;</c> flow, as data.
/// <para>
/// Nothing about a wizard is hard-coded UI: the hub renders a card per definition and the
/// wizard window renders a page per <see cref="Steps"/> entry, so a target that appears in
/// a future CLI release shows up here without an app change. Curated definitions differ
/// from generated ones only in how carefully their steps are grouped and worded.
/// </para>
/// </summary>
public sealed class WizardDefinition
{
    /// <summary>
    /// The CLI noun: the <c>&lt;target&gt;</c> in <c>defenseclaw setup &lt;target&gt;</c>. Words separated by one space name a nested
    /// command (<c>splunk dashboards</c> is <c>defenseclaw setup splunk dashboards</c>, see <see cref="SplunkDashboards"/>); every other
    /// target is a single word.
    /// </summary>
    public required string Target { get; init; }

    public required string Title { get; init; }

    public required string Group { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<WizardStep> Steps { get; init; } = Array.Empty<WizardStep>();

    public PlatformStatus PlatformStatus { get; init; } = PlatformStatus.Unknown;

    /// <summary>The prose after the status word, e.g. why the integration is not certified.</summary>
    public string PlatformNote { get; init; } = string.Empty;

    /// <summary>True once the per-target <c>--help</c> has been parsed for this entry.</summary>
    public bool IsDetailLoaded { get; init; }

    /// <summary>Set when the per-target <c>--help</c> failed; the badge stays neutral.</summary>
    public string? DetailError { get; init; }

    /// <summary>
    /// Why the installed CLI cannot run this flow at all, from what it told us (its help has no such command): the card sits in the "not
    /// available" group with this sentence. Empty for every flow the CLI has; whether the machine can run it right now (Docker, Terraform) is
    /// a different question, asked of a live probe.
    /// </summary>
    public string UnavailableReason { get; init; } = string.Empty;

    /// <summary>True when steps were hand-curated rather than generated from flags.</summary>
    public bool IsCurated { get; init; }

    /// <summary>Raw <c>--help</c> text, shown in the wizard's "what the CLI says" expander.</summary>
    public string HelpText { get; init; } = string.Empty;

    /// <summary>
    /// One sentence saying where the pre-filled answers came from and what is sent ("Pre-filled from
    /// config.yaml → guardrail.connectors.claudecode. Only fields you change are sent."). Empty when
    /// nothing was pre-filled from the configuration.
    /// </summary>
    public string BaselineNote { get; init; } = string.Empty;

    /// <summary>
    /// A caution about the pre-fill itself — set when the configuration could not be read, so the
    /// wizard cannot see what a setting currently is and an omitted flag may reset it. Shown as a warning
    /// on every page.
    /// </summary>
    public string BaselineWarning { get; init; } = string.Empty;

    /// <summary>
    /// Checks that span fields (splunk needs a pipeline picked, say). Returns a message, or null when
    /// the answers are consistent. Run on Next and before the review page is trusted.
    /// </summary>
    public Func<WizardValues, string?>? CrossValidator { get; init; }

    /// <summary>A copy with different pages and pre-fill notes; everything else carries over.</summary>
    public WizardDefinition With(IReadOnlyList<WizardStep> steps, string baselineNote, string baselineWarning) => new()
    {
        Target = Target,
        Title = Title,
        Group = Group,
        Description = Description,
        Steps = steps,
        PlatformStatus = PlatformStatus,
        PlatformNote = PlatformNote,
        IsDetailLoaded = IsDetailLoaded,
        DetailError = DetailError,
        UnavailableReason = UnavailableReason,
        IsCurated = IsCurated,
        HelpText = HelpText,
        FinalArgvBuilder = FinalArgvBuilder,
        BaselineNote = baselineNote,
        BaselineWarning = baselineWarning,
        CrossValidator = CrossValidator,
    };

    /// <summary>
    /// Turns answers into the exact argv handed to <see cref="Core.Cli.CliRunner"/> —
    /// the same list the review screen prints. Overridable per definition; the default is
    /// <see cref="BuildArgvDefault"/>.
    /// </summary>
    public Func<WizardDefinition, WizardValues, IReadOnlyList<string>> FinalArgvBuilder { get; init; } = BuildArgvDefault;

    /// <summary>Every field across every step, in definition order.</summary>
    public IEnumerable<WizardField> AllFields => Steps.SelectMany(s => s.Fields);

    public IReadOnlyList<string> BuildArgv(WizardValues values) => FinalArgvBuilder(this, values);

    /// <summary>Visible secret-taking fields: the credentials this run depends on but never carries.</summary>
    public IEnumerable<WizardField> VisibleCredentials(WizardValues values) =>
        VisibleFields(values).Where(f => f.IsSecret);

    /// <summary>
    /// The fields the operator changed from where they started, as human sentences for the review page
    /// ("Mode: action → observe"). Compares against <see cref="WizardField.DefaultValue"/> — what the
    /// configuration held when the wizard opened — not against the CLI baseline, so a change back to the
    /// CLI default still shows up as the change it is.
    /// </summary>
    public IReadOnlyList<string> DescribeChanges(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var lines = new List<string>();
        foreach (var field in VisibleFields(values))
        {
            if (field.IsSecret || field.IsPositional || WizardFieldBuilder.IsNonInteractiveFlag(field.Flag))
            {
                continue;
            }

            var now = Normalize(field, values[field.Id]);
            var was = Normalize(field, field.DefaultValue);
            if (string.Equals(now, was, StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{field.Label}: {Display(field, was, unsetText: "(not set)")} → {Display(field, now, unsetText: "(cleared)")}"));
        }

        return lines;
    }

    private static string Normalize(WizardField field, string raw) => field.Kind == WizardFieldKind.Lines
        ? string.Join('\n', SplitLines(raw))
        : raw.Trim();

    private static string Display(WizardField field, string value, string unsetText)
    {
        if (value.Length == 0)
        {
            return unsetText;
        }

        if (field.Kind is WizardFieldKind.Toggle or WizardFieldKind.Switch)
        {
            return IsOn(value) ? "on" : "off";
        }

        return field.Kind == WizardFieldKind.Lines ? value.Replace("\n", " | ", StringComparison.Ordinal) : value;
    }

    /// <summary>Non-empty, trimmed lines of a multi-line answer.</summary>
    internal static IReadOnlyList<string> SplitLines(string raw) =>
        raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Fields whose gates are satisfied by the current answers.</summary>
    public IEnumerable<WizardField> VisibleFields(WizardValues values)
    {
        foreach (var step in Steps)
        {
            if (!IsVisible(step.VisibleWhenFieldId, step.VisibleWhenValues, values))
            {
                continue;
            }

            foreach (var field in step.Fields)
            {
                if (IsVisible(field.VisibleWhenFieldId, field.VisibleWhenValues, values))
                {
                    yield return field;
                }
            }
        }
    }

    internal static bool IsVisible(string? gateFieldId, IReadOnlyList<string> gateValues, WizardValues values)
    {
        if (gateFieldId is not { Length: > 0 })
        {
            return true;
        }

        // "a|b" is any-of: visible when ANY of those fields holds one of the values (splunk's index/source/sourcetype
        // belong to both the local and the enterprise pipeline).
        foreach (var id in gateFieldId.Split('|'))
        {
            var actual = values[id];
            for (var i = 0; i < gateValues.Count; i++)
            {
                if (string.Equals(gateValues[i], actual, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The words of a target's noun path, one command-line argument each: <c>["splunk", "dashboards"]</c> for <c>splunk dashboards</c>,
    /// <c>["llm"]</c> for <c>llm</c>.
    /// </summary>
    public static string[] CommandWords(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words : new[] { target };
    }

    /// <summary>
    /// <c>setup &lt;target&gt; [positionals…] [flags…]</c>. Positionals lead because Click
    /// binds a subcommand before its options; flag order is definition order, which is what
    /// the review screen shows. A nested target (<c>splunk dashboards</c>) is one argument per word.
    /// </summary>
    public static IReadOnlyList<string> BuildArgvDefault(WizardDefinition definition, WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);

        var argv = new List<string> { "setup" };
        argv.AddRange(CommandWords(definition.Target));
        var visible = definition.VisibleFields(values).ToList();

        foreach (var field in visible.Where(f => f.IsPositional).OrderBy(f => f.PositionalOrder))
        {
            var value = values[field.Id].Trim();
            if (value.Length > 0)
            {
                argv.Add(value);
            }
        }

        foreach (var field in visible.Where(f => !f.IsPositional))
        {
            Emit(argv, field, values[field.Id]);
        }

        return argv;
    }

    /// <summary>
    /// Writes one field into argv <b>only if its answer differs from <see cref="WizardField.BaselineValue"/></b>
    /// — what the setting already is when the flag is omitted. That is the whole of the S3 fix: a
    /// connector wizard used to re-send <c>--mode observe --restart …</c> from static defaults, which
    /// downgrades a connector that is in action mode. Now an untouched field sends nothing, and
    /// <c>--mode</c> (whose CLI default really does override the stored value) is sent exactly when the
    /// answer is not that default.
    /// </summary>
    private static void Emit(List<string> argv, WizardField field, string raw)
    {
        var value = raw.Trim();
        var baseline = field.BaselineValue.Trim();

        switch (field.Kind)
        {
            // A secret never reaches argv or stdin: it lives in ~/.defenseclaw/.env and the CLI reads
            // it by variable name — or, typed in the app, travels in the child's environment (see
            // WizardViewModel.BuildRunOptions), which is not argv. See SecretRoute.
            case WizardFieldKind.Secret:
                return;

            case WizardFieldKind.Switch:
                // A bare switch can only be turned on; on-by-default ones (--yes) have an "off" baseline
                // so they are still sent.
                if (IsOn(value) && !IsOn(baseline) && field.Flag is { Length: > 0 } switchFlag)
                {
                    argv.Add(switchFlag);
                }

                return;

            case WizardFieldKind.Toggle:
                if (string.Equals(value, baseline, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (IsOn(value) && field.Flag is { Length: > 0 } positive)
                {
                    argv.Add(positive);
                }
                else if (IsOff(value) && field.NegativeFlag is { Length: > 0 } negative)
                {
                    argv.Add(negative);
                }

                return;

            case WizardFieldKind.Lines:
                var lines = SplitLines(raw);
                var repeated = field.Flag;
                if (string.IsNullOrEmpty(repeated) ||
                    lines.SequenceEqual(SplitLines(field.BaselineValue), StringComparer.Ordinal))
                {
                    return;
                }

                foreach (var line in lines)
                {
                    argv.Add(repeated);
                    argv.Add(line);
                }

                return;

            default:
                var flag = field.Flag;
                if (string.IsNullOrEmpty(flag) || string.Equals(value, baseline, StringComparison.Ordinal))
                {
                    return;
                }

                if (value.Length > 0)
                {
                    argv.Add(flag);
                    argv.Add(value);
                }
                else if (field.AllowEmptyWhenChanged)
                {
                    // "Empty means inherit": clearing a stored value is a change worth sending.
                    argv.Add(flag);
                    argv.Add(string.Empty);
                }

                return;
        }
    }

    private static bool IsOn(string value) =>
        string.Equals(value, ToggleValues.On, StringComparison.OrdinalIgnoreCase);

    private static bool IsOff(string value) =>
        string.Equals(value, ToggleValues.Off, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Display helpers shared by the hub badge and the in-wizard warning.</summary>
public static class PlatformStatusText
{
    public static string Badge(PlatformStatus status) => status switch
    {
        PlatformStatus.Certified => "Certified on Windows",
        PlatformStatus.NotCertified => "Not certified on Windows",
        PlatformStatus.Unsupported => "Unsupported on Windows",
        PlatformStatus.NotApplicable => "No platform rating",
        _ => "Certification unknown",
    };

    /// <summary>Ok / Warn / Bad / Neutral — the colour key the XAML triggers on.</summary>
    public static string Key(PlatformStatus status) => status switch
    {
        PlatformStatus.Certified => "Ok",
        PlatformStatus.NotCertified => "Warn",
        PlatformStatus.Unsupported => "Bad",
        _ => "Neutral", // Unknown and NotApplicable: never a green light, never an alarm.
    };

    public static string Warning(PlatformStatus status, string note)
    {
        var detail = note.Length > 0 ? " " + note : string.Empty;
        return status switch
        {
            PlatformStatus.NotCertified =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "The CLI reports this integration as not_certified on Windows.{0} Setup will run, but the connector has not completed native Windows x64 certification.",
                    detail),
            PlatformStatus.Unsupported =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "The CLI reports this integration as unsupported on Windows.{0} Running it here is expected to fail.",
                    detail),
            PlatformStatus.Unknown =>
                "The per-target help could not be read, so certification could not be confirmed. Treat the result as unverified.",
            _ => string.Empty,
        };
    }
}
