namespace DefenseClaw.App.Services.Wizards;

/// <summary>The kinds of picker a <see cref="WizardField"/> can open instead of a plain text box.</summary>
public static class WizardFieldPickers
{
    /// <summary>A searchable list of the model ids the runtime ships for the provider the wizard has chosen, with the typed id always usable.</summary>
    public const string Model = "model";
}

/// <summary>
/// The pieces of <see cref="WizardField"/> that exist for the curated layouts: how a field is picked, whether it follows the configuration, and
/// the copies a layout makes of a field it built from <c>--help</c> (a new gate, a list of choices, a different id). Fields are immutable and
/// shared by every open wizard, so each of these returns a new one.
/// </summary>
public sealed partial class WizardField
{
    /// <summary>
    /// Which picker the field opens (<see cref="WizardFieldPickers"/>); empty for a plain control. A model picker needs the provider the model
    /// belongs to (<see cref="PickerProviderFieldId"/>) to know which ids to list, and may name the custom-provider instance field as well.
    /// </summary>
    public string PickerKind { get; init; } = string.Empty;

    /// <summary>The id of the field that holds the provider whose models the picker lists.</summary>
    public string PickerProviderFieldId { get; init; } = string.Empty;

    /// <summary>The id of the field that holds a custom-provider instance name, whose own model list wins over the provider's; empty when the wizard has none.</summary>
    public string PickerInstanceFieldId { get; init; } = string.Empty;

    /// <summary>
    /// A choice that steers the wizard and is never a command-line option - what the operator wants to do, the guardrail's scope. It has no flag
    /// to show, is not listed in the review's changes (the goal) or is listed by its words (the scope), and is always shown whatever the goal.
    /// </summary>
    public bool IsSynthetic { get; init; }

    /// <summary>
    /// True for a field that starts blank - "leave unchanged" - however the configuration reads. A per-connector override is the case: the value
    /// config.yaml holds for the flag is the <i>global</i> one, so starting there and sending only what differs would, for a connector that has
    /// its own value, swallow the very change the operator asked for. A blank start sends only what the operator picks.
    /// </summary>
    public bool IgnoresConfig { get; init; }

    /// <summary>True when the field opens a picker (<see cref="PickerKind"/> is set).</summary>
    public bool HasPicker => PickerKind.Length > 0;

    /// <summary>A copy with another id: the same flag offered twice (a global value and a per-connector one), told apart by the id.</summary>
    public WizardField WithId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Edit(d => d.Id = id);
    }

    /// <summary>A copy shown only when the gate holds, replacing any gate it had.</summary>
    public WizardField WithGate(string? fieldId, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Edit(d =>
        {
            d.VisibleWhenFieldId = fieldId;
            d.VisibleWhenValues = values;
        });
    }

    /// <summary>A copy that is also shown only when <paramref name="more"/> holds, on top of any gate it already had (see <see cref="WizardGate"/>).</summary>
    public WizardField AndGate(params WizardGate.Clause[] more)
    {
        var (id, values) = WizardGate.And(VisibleWhenFieldId, VisibleWhenValues, more);
        return WithGate(id, values);
    }

    /// <summary>A copy that is a choice among <paramref name="choices"/> (a text flag the CLI documents a list of values for, a list narrowed to what is active).</summary>
    public WizardField AsChoice(IReadOnlyList<WizardChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return Edit(d =>
        {
            d.Kind = WizardFieldKind.Choice;
            d.Choices = choices;
            d.Placeholder = string.Empty;
        });
    }

    /// <summary>A copy that offers <paramref name="choices"/> in place of its own.</summary>
    public WizardField WithChoices(IReadOnlyList<WizardChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return Edit(d => d.Choices = choices);
    }

    /// <summary>A copy that opens the picker <paramref name="kind"/> (see <see cref="WizardFieldPickers"/>), reading the provider from <paramref name="providerFieldId"/>.</summary>
    public WizardField WithPicker(string kind, string providerFieldId, string instanceFieldId = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(providerFieldId);
        ArgumentNullException.ThrowIfNull(instanceFieldId);

        return Edit(d =>
        {
            d.PickerKind = kind;
            d.PickerProviderFieldId = providerFieldId;
            d.PickerInstanceFieldId = instanceFieldId;
        });
    }

    /// <summary>A copy that starts blank and sends only what the operator picks (see <see cref="IgnoresConfig"/>).</summary>
    public WizardField StartingBlank() => Edit(d =>
    {
        d.IgnoresConfig = true;
        d.DefaultValue = string.Empty;
        d.BaselineValue = string.Empty;
        d.BaselineSource = string.Empty;
    });

    /// <summary>A copy that steers the wizard and is not a command-line option (see <see cref="IsSynthetic"/>).</summary>
    public WizardField AsSynthetic(string defaultValue) => Edit(d =>
    {
        d.IsSynthetic = true;
        d.Flag = null;
        d.NegativeFlag = null;
        d.DefaultValue = defaultValue;
        d.BaselineValue = defaultValue;
    });

    /// <summary>A copy that is (or is no longer) required.</summary>
    public WizardField WithRequired(bool required) => Edit(d => d.IsRequired = required);

    /// <summary>A copy with different words and, for a text box, a different placeholder.</summary>
    public WizardField WithHelp(string help, string? placeholder = null) => Edit(d =>
    {
        d.Help = help;
        if (placeholder is not null)
        {
            d.Placeholder = placeholder;
        }
    });

    private WizardField Clone(
        string defaultValue,
        string baselineValue,
        string source,
        SecretRoute? credential,
        string? label = null,
        string? help = null) => Edit(d =>
    {
        d.DefaultValue = defaultValue;
        d.BaselineValue = baselineValue;
        d.BaselineSource = source;
        d.Credential = credential;
        if (label is not null)
        {
            d.Label = label;
        }

        if (help is not null)
        {
            d.Help = help;
        }
    });

    private WizardField Edit(Action<Draft> change)
    {
        var draft = new Draft(this);
        change(draft);
        return draft.Build();
    }

    /// <summary>Every property of a field, mutable, so a copy is "start from this one and change these".</summary>
    private sealed class Draft
    {
        public Draft(WizardField f)
        {
            Id = f.Id;
            Label = f.Label;
            Kind = f.Kind;
            Flag = f.Flag;
            NegativeFlag = f.NegativeFlag;
            Help = f.Help;
            Choices = f.Choices;
            DefaultValue = f.DefaultValue;
            BaselineValue = f.BaselineValue;
            BaselineSource = f.BaselineSource;
            AllowEmptyWhenChanged = f.AllowEmptyWhenChanged;
            Credential = f.Credential;
            ViaEnvironment = f.ViaEnvironment;
            IsPositional = f.IsPositional;
            PositionalOrder = f.PositionalOrder;
            IsRequired = f.IsRequired;
            Placeholder = f.Placeholder;
            VisibleWhenFieldId = f.VisibleWhenFieldId;
            VisibleWhenValues = f.VisibleWhenValues;
            PickerKind = f.PickerKind;
            PickerProviderFieldId = f.PickerProviderFieldId;
            PickerInstanceFieldId = f.PickerInstanceFieldId;
            IsSynthetic = f.IsSynthetic;
            IgnoresConfig = f.IgnoresConfig;
        }

        public string Id { get; set; }

        public string Label { get; set; }

        public WizardFieldKind Kind { get; set; }

        public string? Flag { get; set; }

        public string? NegativeFlag { get; set; }

        public string Help { get; set; }

        public IReadOnlyList<WizardChoice> Choices { get; set; }

        public string DefaultValue { get; set; }

        public string BaselineValue { get; set; }

        public string BaselineSource { get; set; }

        public bool AllowEmptyWhenChanged { get; set; }

        public SecretRoute? Credential { get; set; }

        public string ViaEnvironment { get; set; }

        public bool IsPositional { get; set; }

        public int PositionalOrder { get; set; }

        public bool IsRequired { get; set; }

        public string Placeholder { get; set; }

        public string? VisibleWhenFieldId { get; set; }

        public IReadOnlyList<string> VisibleWhenValues { get; set; }

        public string PickerKind { get; set; }

        public string PickerProviderFieldId { get; set; }

        public string PickerInstanceFieldId { get; set; }

        public bool IsSynthetic { get; set; }

        public bool IgnoresConfig { get; set; }

        public WizardField Build() => new()
        {
            Id = Id,
            Label = Label,
            Kind = Kind,
            Flag = Flag,
            NegativeFlag = NegativeFlag,
            Help = Help,
            Choices = Choices,
            DefaultValue = DefaultValue,
            BaselineValue = BaselineValue,
            BaselineSource = BaselineSource,
            AllowEmptyWhenChanged = AllowEmptyWhenChanged,
            Credential = Credential,
            ViaEnvironment = ViaEnvironment,
            IsPositional = IsPositional,
            PositionalOrder = PositionalOrder,
            IsRequired = IsRequired,
            Placeholder = Placeholder,
            VisibleWhenFieldId = VisibleWhenFieldId,
            VisibleWhenValues = VisibleWhenValues,
            PickerKind = PickerKind,
            PickerProviderFieldId = PickerProviderFieldId,
            PickerInstanceFieldId = PickerInstanceFieldId,
            IsSynthetic = IsSynthetic,
            IgnoresConfig = IgnoresConfig,
        };
    }
}
