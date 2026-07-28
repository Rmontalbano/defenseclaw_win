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
    /// A real secret. Never reaches argv — <see cref="Core.Cli.CliRunner"/> would refuse
    /// it anyway — so the value is piped to the child process's stdin instead.
    /// </summary>
    Secret,

    /// <summary>
    /// The NAME of an environment variable that holds a secret. Safe in argv, and it is
    /// what DefenseClaw actually persists: config.yaml stores <c>*_env</c> names, never
    /// values.
    /// </summary>
    EnvVarName,
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

    /// <summary>Pre-filled answer. For toggles: <see cref="ToggleValues"/>.</summary>
    public string DefaultValue { get; init; } = string.Empty;

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
        : Flag ?? "(positional)";
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

    /// <summary>Display order in the hub.</summary>
    public static readonly IReadOnlyList<string> Ordered = new[]
    {
        Connectors,
        GuardrailAndPolicy,
        Scanners,
        Observability,
        Credentials,
        Other,
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
    /// <summary>The CLI noun: the <c>&lt;target&gt;</c> in <c>defenseclaw setup &lt;target&gt;</c>.</summary>
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

    /// <summary>True when steps were hand-curated rather than generated from flags.</summary>
    public bool IsCurated { get; init; }

    /// <summary>Raw <c>--help</c> text, shown in the wizard's "what the CLI says" expander.</summary>
    public string HelpText { get; init; } = string.Empty;

    /// <summary>
    /// Turns answers into the exact argv handed to <see cref="Core.Cli.CliRunner"/> —
    /// the same list the review screen prints. Overridable per definition; the default is
    /// <see cref="BuildArgvDefault"/>.
    /// </summary>
    public Func<WizardDefinition, WizardValues, IReadOnlyList<string>> FinalArgvBuilder { get; init; } = BuildArgvDefault;

    /// <summary>Every field across every step, in definition order.</summary>
    public IEnumerable<WizardField> AllFields => Steps.SelectMany(s => s.Fields);

    public IReadOnlyList<string> BuildArgv(WizardValues values) => FinalArgvBuilder(this, values);

    /// <summary>True when this run will pipe a secret to the child's stdin.</summary>
    public bool UsesStdinSecret(WizardValues values) =>
        VisibleFields(values).Any(f => f.IsSecret && values[f.Id].Length > 0);

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

        var actual = values[gateFieldId];
        for (var i = 0; i < gateValues.Count; i++)
        {
            if (string.Equals(gateValues[i], actual, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>setup &lt;target&gt; [positionals…] [flags…]</c>. Positionals lead because Click
    /// binds a subcommand before its options; flag order is definition order, which is what
    /// the review screen shows.
    /// </summary>
    public static IReadOnlyList<string> BuildArgvDefault(WizardDefinition definition, WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);

        var argv = new List<string> { "setup", definition.Target };
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

    private static void Emit(List<string> argv, WizardField field, string raw)
    {
        var value = raw.Trim();

        switch (field.Kind)
        {
            // Secrets go to stdin. Reaching argv is a hard failure in CliRunner, and the
            // review screen says so out loud rather than quietly dropping the value.
            case WizardFieldKind.Secret:
                return;

            case WizardFieldKind.Switch:
                if (IsOn(value) && field.Flag is { Length: > 0 } switchFlag)
                {
                    argv.Add(switchFlag);
                }

                return;

            case WizardFieldKind.Toggle:
                if (IsOn(value) && field.Flag is { Length: > 0 } positive)
                {
                    argv.Add(positive);
                }
                else if (IsOff(value) && field.NegativeFlag is { Length: > 0 } negative)
                {
                    argv.Add(negative);
                }

                return;

            default:
                if (value.Length > 0 && field.Flag is { Length: > 0 } flag)
                {
                    argv.Add(flag);
                    argv.Add(value);
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
        _ => "Certification unknown",
    };

    /// <summary>Ok / Warn / Bad / Neutral — the colour key the XAML triggers on.</summary>
    public static string Key(PlatformStatus status) => status switch
    {
        PlatformStatus.Certified => "Ok",
        PlatformStatus.NotCertified => "Warn",
        PlatformStatus.Unsupported => "Bad",
        _ => "Neutral",
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
