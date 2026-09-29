using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// <para>
/// <b>A secret field has no value.</b> <see cref="IsSecret"/> fields never hold, receive or send a
/// secret: they show the credential card (which variable, is it set, how to store it) and nothing else.
/// The reasons are in <see cref="SecretRoute"/>.
/// </para>
/// </summary>
public sealed partial class WizardFieldViewModel : ObservableObject
{
    private readonly WizardValues _values;
    private readonly WizardCredentials? _credentials;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _validationError = string.Empty;

    [ObservableProperty]
    private bool _isChanged;

    [ObservableProperty]
    private string _credentialEnvName = string.Empty;

    [ObservableProperty]
    private string _credentialStatus = string.Empty;

    /// <summary>Ok / Warn / Neutral — the tone key for the credential status badge.</summary>
    [ObservableProperty]
    private string _credentialStatusKey = "Neutral";

    /// <summary>Set after the terminal button is pressed, or when it could not open one.</summary>
    [ObservableProperty]
    private string _credentialMessage = string.Empty;

    public WizardFieldViewModel(WizardField field, WizardValues values, WizardCredentials? credentials = null)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _credentials = credentials;

        // A secret field carries no value at all; every other kind starts from its default (the current
        // configuration when the wizard could read it).
        _value = field.IsSecret ? string.Empty : field.DefaultValue;
        _values[field.Id] = _value;

        RefreshCredential();
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

    /// <summary>The name a screen reader announces for the control: the label, and that it is required.</summary>
    public string AutomationName => IsRequired ? Label + " (required)" : Label;

    /// <summary>True when the starting answer was read from the configuration rather than the CLI's default.</summary>
    public bool IsFromConfig => Field.BaselineSource.Length > 0;

    // Combos cover both real choices and the tri-state paired flags: "leave unchanged" has to
    // be expressible, or a wizard would silently write a setting nobody asked about.
    public bool IsChoice => Field.Kind is WizardFieldKind.Choice or WizardFieldKind.Toggle;

    public bool IsSwitch => Field.Kind == WizardFieldKind.Switch;

    public bool IsPath => Field.Kind == WizardFieldKind.Path;

    /// <summary>The credential card is shown instead of an input. See the type remarks.</summary>
    public bool IsSecret => Field.Kind == WizardFieldKind.Secret;

    /// <summary>A repeatable flag: a multi-line box, one value per line.</summary>
    public bool IsLines => Field.Kind == WizardFieldKind.Lines;

    public bool IsText => Field.Kind is WizardFieldKind.Text or WizardFieldKind.Integer or
        WizardFieldKind.EnvVarName or WizardFieldKind.Number;

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

    // ------------------------------------------------------------------ credential card

    /// <summary>What the secret is: "Splunk Observability Cloud access token".</summary>
    public string CredentialPurpose => Field.Credential?.Purpose ?? Label;

    public bool HasCredentialEnvName => CredentialEnvName.Length > 0;

    /// <summary>The exact command the operator runs in a terminal; never carries a value.</summary>
    public string CredentialCommand => HasCredentialEnvName
        ? WizardCredentials.KeysSetCommand(CredentialEnvName)
        : "defenseclaw keys set <ENV_NAME>";

    /// <summary>What happens if the variable is still unset when the command runs.</summary>
    public string CredentialConsequence => Field.Credential?.IfMissing ?? string.Empty;

    public bool CanOpenTerminal => WizardCredentials.IsValidName(CredentialEnvName);

    public bool HasCredentialMessage => CredentialMessage.Length > 0;

    /// <summary>Why the app does not take the value itself, in one paragraph, for the card.</summary>
    public string CredentialExplanation =>
        "This app never takes the secret itself. The command reads it from a variable in ~/.defenseclaw/.env, and " +
        "`defenseclaw keys set` — the CLI's own hidden prompt — is the only way to store it that does not put it on a command " +
        "line, and on Windows it reads a real console, not a pipe.";

    /// <summary>
    /// Re-derives which variable the command will read (it can depend on other answers, such as the
    /// preset) and whether it currently has a value. Reads <c>.env</c> only to test for a non-empty
    /// entry; the value is never kept.
    /// </summary>
    /// <param name="fresh">True to re-read <c>.env</c> rather than reuse a read from the last couple of seconds.</param>
    public void RefreshCredential(bool fresh = false)
    {
        if (!IsSecret)
        {
            return;
        }

        var name = Field.Credential?.EnvName(_values)?.Trim() ?? string.Empty;
        CredentialEnvName = name;

        if (name.Length == 0)
        {
            CredentialStatus = "No stored variable applies to the current choices. The CLI reports any missing secret when it runs.";
            CredentialStatusKey = "Neutral";
        }
        else
        {
            var presence = _credentials?.Check(name, fresh) ?? CredentialPresence.Unknown;
            (CredentialStatus, CredentialStatusKey) = presence switch
            {
                CredentialPresence.InDotEnv => (name + " has a value in ~/.defenseclaw/.env.", "Ok"),
                CredentialPresence.InEnvironment => (name + " has a value in this app's environment, which every command it starts inherits.", "Ok"),
                CredentialPresence.NotSet => (name + " is not set yet.", "Warn"),
                _ => ("Could not check whether " + name + " is set.", "Neutral"),
            };
        }

        OnPropertyChanged(nameof(HasCredentialEnvName));
        OnPropertyChanged(nameof(CredentialCommand));
        OnPropertyChanged(nameof(CanOpenTerminal));
    }

    [RelayCommand]
    private void CopyCredential()
    {
        try
        {
            System.Windows.Clipboard.SetText(CredentialCommand);
            CredentialMessage = "Copied. Paste it into your own terminal.";
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; the command is on screen to copy by hand.
            CredentialMessage = "The clipboard is busy. Select the command above and copy it by hand.";
        }
    }

    [RelayCommand]
    private void OpenCredentialTerminal()
    {
        if (_credentials is null)
        {
            CredentialMessage = "This wizard has no credential helper to start a console with. Run the command above in your own terminal.";
            return;
        }

        // null means the console opened; anything else is the reason it did not.
        CredentialMessage = _credentials.OpenKeysSetTerminal(CredentialEnvName) ??
            "A console window opened. Paste the value at its hidden prompt, then come back here and press Re-check.";
    }

    [RelayCommand]
    private void RecheckCredential()
    {
        RefreshCredential(fresh: true);
        CredentialMessage = string.Empty;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Validates in isolation; returns the message, or empty when the answer is fine.</summary>
    public string Validate()
    {
        if (IsSecret)
        {
            return string.Empty;
        }

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

        if (Field.Kind == WizardFieldKind.Number && value.Length > 0 &&
            (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
             double.IsNaN(number) || double.IsInfinity(number)))
        {
            return $"{Label} must be a number such as 15 or 2.5 (use a dot for the decimal point).";
        }

        if (Field.Kind == WizardFieldKind.EnvVarName && value.Length > 0 &&
            value.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            return $"{Label} is the NAME of an environment variable — letters, digits and underscores only.";
        }

        if (Field.Kind == WizardFieldKind.Lines)
        {
            var bad = WizardDefinition.SplitLines(Value).FirstOrDefault(l => l.StartsWith('-'));
            if (bad is not null)
            {
                return $"{Label}: a value cannot start with a dash (\"{bad}\") — the CLI would read it as another flag.";
            }
        }

        return string.Empty;
    }

    partial void OnValueChanged(string value)
    {
        _values[Field.Id] = value;
        IsChanged = !string.Equals(
            Field.Kind == WizardFieldKind.Lines ? string.Join('\n', WizardDefinition.SplitLines(value)) : value.Trim(),
            Field.Kind == WizardFieldKind.Lines ? string.Join('\n', WizardDefinition.SplitLines(Field.DefaultValue)) : Field.DefaultValue.Trim(),
            StringComparison.Ordinal);

        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(SelectedChoice));

        if (ValidationError.Length > 0)
        {
            ValidationError = Validate();
        }
    }

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));

    partial void OnCredentialMessageChanged(string value) => OnPropertyChanged(nameof(HasCredentialMessage));

    /// <summary>What a screen reader announces when this row is reached as an item.</summary>
    public override string ToString() => Label + " (" + FlagDisplay + ")";
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

    public override string ToString() => Title;
}
