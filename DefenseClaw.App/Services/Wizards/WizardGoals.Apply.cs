namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The goal-first entry point of the setup wizards (the TUI's "what do you want to do?" menu): which wizards have one, what its choices are,
/// and how a wizard built from <c>--help</c> gets the page that asks.
/// <para>
/// <b>Over the generated fields.</b> A goal never adds a field or a flag. <see cref="Apply"/> reads the steps the catalog built - curated or
/// generated - and keeps of each goal what the installed CLI really has (see <see cref="WizardGoal"/>); a wizard none of whose goals survive keeps
/// its first page as it was. The choice is a field of its own (<see cref="FieldId"/>), a synthetic one that never reaches the command, so the
/// pages, the review and "which fields count" all read it from the answers like any other.
/// </para>
/// </summary>
public static partial class WizardGoals
{
    /// <summary>The id of the field, and of the page, that asks what the operator wants to do.</summary>
    public const string FieldId = "goal";

    /// <summary>The id of the page that asks.</summary>
    public const string StepId = "goal";

    /// <summary>The goal that narrows nothing: every page, every field.</summary>
    public const string AdvancedId = "advanced";

    private const string Question = "What do you want to do?";

    /// <summary>
    /// Gives <paramref name="steps"/> (a wizard for <paramref name="target"/>, after the platform policy and the secret routes) the page that
    /// asks, and returns the goals it offers. A target with no goals, or none the installed CLI can honour, comes back unchanged with no goals.
    /// </summary>
    public static (IReadOnlyList<WizardStep> Steps, IReadOnlyList<WizardGoal> Goals) Apply(string target, IReadOnlyList<WizardStep> steps)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(steps);

        var fields = steps.SelectMany(s => s.Fields).ToArray();
        var table = TableFor(target, fields);
        if (table.Count == 0)
        {
            return (steps, Array.Empty<WizardGoal>());
        }

        var goals = new List<WizardGoal>();
        foreach (var spec in table)
        {
            if (Resolve(spec, steps, fields) is { } goal)
            {
                goals.Add(goal);
            }
        }

        if (goals.Count == 0)
        {
            return (steps, Array.Empty<WizardGoal>());
        }

        goals.Add(new WizardGoal
        {
            Id = AdvancedId,
            Label = "Advanced: show all settings",
            Summary = "Every page and every setting this command has, in the order the CLI lists them.",
        });

        var ask = new WizardStep
        {
            Id = StepId,
            Title = Question,
            Subtitle = "Choose one. The pages after this show only what it needs; Advanced shows everything.",
            Fields = new[]
            {
                new WizardField
                {
                    Id = FieldId,
                    Label = Question,
                    Kind = WizardFieldKind.Choice,
                    Choices = goals.Select(g => new WizardChoice(g.Id, g.Label)).ToArray(),
                    Help = "What you pick here only decides which settings you are asked about; the review at the end still shows the exact command.",
                    IsSynthetic = true,
                },
            },
        };

        return (new[] { ask }.Concat(steps).ToArray(), goals);
    }

    /// <summary>
    /// The goals a definition offers, for a caller that builds a wizard without the catalog (a dialog over <c>agent discovery enable</c>). The same
    /// resolution as <see cref="Apply"/> without the page.
    /// </summary>
    public static IReadOnlyList<WizardGoal> For(string target, IReadOnlyList<WizardStep> steps) => Apply(target, steps).Goals;

    /// <summary>
    /// A goal's presets as the values to put in the fields: each key (a flag, or a field id for a choice that is not a flag) with the fields it
    /// names and the value each can hold. A preset for a field that cannot hold its value, or that is not there, is left out.
    /// </summary>
    internal static IReadOnlyList<(WizardField Field, string Value)> PresetsFor(WizardGoal goal, IEnumerable<WizardField> fields)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(fields);

        var result = new List<(WizardField, string)>();
        foreach (var field in fields)
        {
            if (field.IsSecret)
            {
                continue;
            }

            foreach (var (key, value) in goal.Presets)
            {
                if (!KeyNames(key, field) || Coerce(field, value) is not { } held)
                {
                    continue;
                }

                result.Add((field, held));
            }
        }

        return result;
    }

    /// <summary>
    /// Fits a preset to what the field's control can show: the value itself for text, the choice with that value (any case), on / off for a
    /// switch. A blank leaves a field unset. Null when the control cannot hold it - an unknown choice, a word for a number.
    /// </summary>
    internal static string? Coerce(WizardField field, string value)
    {
        if (field.Kind == WizardFieldKind.Secret)
        {
            return null;
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        return field.Kind == WizardFieldKind.Switch
            ? WizardBaseline.TryBool(value) is { } on ? (on ? ToggleValues.On : ToggleValues.Off) : null
            : WizardBaseline.Coerce(field, value);
    }

    private static bool KeyNames(string key, WizardField field) =>
        string.Equals(key, field.Id, StringComparison.Ordinal) ||
        (field.Flag is { Length: > 0 } flag && string.Equals(key, flag, StringComparison.Ordinal));

    private static IReadOnlyList<WizardGoal> TableFor(string target, IReadOnlyList<WizardField> fields)
    {
        switch (target)
        {
            case "llm":
                return Llm;

            case "guardrail":
                return Guardrail;

            case "splunk":
                return Splunk;
        }

        // Shape, not name, as the curated connector layout: every hook connector shares one option set, so claude-code, codex and the
        // connectors a later CLI adds get the same goals without this file learning their names.
        if (Has(fields, "--mode") && Has(fields, "--fail-mode") && Has(fields, "--rule-pack"))
        {
            return Connector;
        }

        // The tuning flags of `agent discovery enable`: a dialog built over them gets the same goals.
        return Has(fields, "--scan-interval-min") && Has(fields, "--include-shell-history") ? AiDiscovery : Array.Empty<WizardGoal>();
    }

    private static bool Has(IEnumerable<WizardField> fields, string flag) =>
        fields.Any(f => string.Equals(f.Flag, flag, StringComparison.Ordinal));

    /// <summary>The goal as far as the installed CLI can honour it, or null when nothing of it is left.</summary>
    private static WizardGoal? Resolve(WizardGoal spec, IReadOnlyList<WizardStep> steps, IReadOnlyList<WizardField> fields)
    {
        bool HasPage(string id) => steps.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        var flags = spec.Flags.Where(f => Has(fields, f)).ToArray();
        var pages = spec.Pages.Where(HasPage).ToArray();
        if (flags.Length == 0 && pages.Length == 0)
        {
            return null;
        }

        // A goal is about something: a CLI that lacks all of it has nothing to offer under that name.
        if (spec.Needs.Count > 0 && !spec.Needs.Any(n => Has(fields, n) || HasPage(n)))
        {
            return null;
        }

        var presets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in spec.Presets)
        {
            if (fields.Any(f => KeyNames(key, f) && Coerce(f, value) is not null))
            {
                presets[key] = value;
            }
        }

        return new WizardGoal
        {
            Id = spec.Id,
            Label = spec.Label,
            Summary = spec.Summary,
            Presets = presets,
            Flags = flags,
            Pages = pages,
            Needs = spec.Needs,
            Requires = spec.Requires,
        };
    }
}
