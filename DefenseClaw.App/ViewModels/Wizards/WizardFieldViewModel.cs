using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// One editable answer. The definition it wraps is immutable; everything the operator types
/// lives here and is written back into the shared <see cref="WizardValues"/> so the review
/// screen's argv is always derived from one place.
/// <para>
/// The <c>Is…</c> properties exist so the XAML can pick a control with the app-level
/// boolean-to-visibility converter instead of shipping a template selector: a panel is two
/// files plus its wizards, and a converter class per control kind would not earn its keep.
/// </para>
/// </summary>
public sealed partial class WizardFieldViewModel : ObservableObject
{
    private readonly WizardValues _values;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _validationError = string.Empty;

    public WizardFieldViewModel(WizardField field, WizardValues values)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        _values = values ?? throw new ArgumentNullException(nameof(values));

        _value = field.DefaultValue;
        _values[field.Id] = _value;
    }

    public WizardField Field { get; }

    public string Id => Field.Id;

    public string Label => Field.Label;

    public string Help => Field.Help;

    public string Placeholder => Field.Placeholder;

    /// <summary>The flag this answer becomes, shown small under the label so nothing is implicit.</summary>
    public string FlagDisplay => Field.FlagDisplay;

    public IReadOnlyList<WizardChoice> Choices => Field.Choices;

    public bool IsRequired => Field.IsRequired;

    public bool HasValidationError => ValidationError.Length > 0;

    // Combos cover both real choices and the tri-state paired flags: "leave unchanged" has to
    // be expressible, or a wizard would silently write a setting nobody asked about.
    public bool IsChoice => Field.Kind is WizardFieldKind.Choice or WizardFieldKind.Toggle;

    public bool IsSwitch => Field.Kind == WizardFieldKind.Switch;

    public bool IsPath => Field.Kind == WizardFieldKind.Path;

    public bool IsSecret => Field.Kind == WizardFieldKind.Secret;

    public bool IsText => Field.Kind is WizardFieldKind.Text or WizardFieldKind.Integer or WizardFieldKind.EnvVarName;

    /// <summary>Two-way binding target for <see cref="WizardFieldKind.Switch"/>.</summary>
    public bool IsOn
    {
        get => string.Equals(Value, ToggleValues.On, StringComparison.OrdinalIgnoreCase);
        set => Value = value ? ToggleValues.On : ToggleValues.Off;
    }

    /// <summary>Two-way binding target for the choice combo.</summary>
    public WizardChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => string.Equals(c.Value, Value, StringComparison.Ordinal));
        set => Value = value?.Value ?? string.Empty;
    }

    /// <summary>Extra line under a secret field, spelling out where the value actually goes.</summary>
    public string SecretNote =>
        "Piped to the command's stdin. It never appears in argv, in the Activity panel, or in captured output.";

    /// <summary>Validates in isolation; returns the message, or empty when the answer is fine.</summary>
    public string Validate()
    {
        var value = Value.Trim();

        if (Field.IsRequired && value.Length == 0)
        {
            return $"{Label} is required.";
        }

        if (Field.Kind == WizardFieldKind.Integer && value.Length > 0 &&
            !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return $"{Label} must be a whole number.";
        }

        if (Field.Kind == WizardFieldKind.EnvVarName && value.Length > 0 &&
            value.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            return $"{Label} is the NAME of an environment variable — letters, digits and underscores only.";
        }

        return string.Empty;
    }

    partial void OnValueChanged(string value)
    {
        _values[Field.Id] = value;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(SelectedChoice));

        if (ValidationError.Length > 0)
        {
            ValidationError = Validate();
        }
    }

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));
}

/// <summary>One page of a wizard, with only the fields whose gates are currently satisfied.</summary>
public sealed partial class WizardStepViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    public WizardStepViewModel(WizardStep step, IReadOnlyList<WizardFieldViewModel> fields)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    public WizardStep Step { get; }

    public string Title => Step.Title;

    public string Subtitle => Step.Subtitle;

    public IReadOnlyList<WizardFieldViewModel> Fields { get; }

    /// <summary>
    /// The page binds every field and hides the gated ones per item, so a gate flipping on
    /// an earlier page never has to rebuild a collection mid-edit.
    /// </summary>
    public bool HasFields => Fields.Count > 0;
}
