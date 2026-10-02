using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;

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
/// <b>A secret field has no <see cref="Value"/>.</b> <see cref="IsSecret"/> fields never put a secret into
/// <see cref="Value"/>, <see cref="WizardValues"/> or argv. They show the credential card (which variable, is it set,
/// how to store it in a real console) and — only where the CLI reads the secret from an environment variable
/// (<see cref="SecretRoute.InAppEnvName"/>) — a password box. What is typed there is held as a
/// <see cref="SecureString"/> (the box's own copy, never a <c>string</c> property), read out only when a run needs it
/// (<see cref="MaterializeSecret"/>) and dropped when the run ends (<see cref="ClearEntry"/>). The reasons and the
/// evidence are in <see cref="SecretRoute"/>.
/// </para>
/// </summary>
public sealed partial class WizardFieldViewModel : ObservableObject
{
    private readonly WizardValues _values;
    private readonly WizardCredentials? _credentials;

    /// <summary>The typed secret, exactly as the password box holds it. Null when nothing is held.</summary>
    private SecureString? _entry;

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

    /// <summary>
    /// The variable the CLI will read the secret from if this app supplies it (empty when the flag has no
    /// in-app route). A <i>name</i>, never a value. Re-derived by <see cref="RefreshCredential"/>.
    /// </summary>
    [ObservableProperty]
    private string _inAppEnvName = string.Empty;

    /// <summary>True while a typed value is held. Says nothing about what the value is.</summary>
    [ObservableProperty]
    private bool _hasEntry;

    /// <summary>Why the typed value cannot be sent (a control character in it), or empty.</summary>
    [ObservableProperty]
    private string _entryProblem = string.Empty;

    /// <summary>
    /// Set when the app itself dropped a typed value — the run ended, or the destination it was typed for
    /// changed — so the operator is told rather than left wondering why the box is empty.
    /// </summary>
    [ObservableProperty]
    private string _entryNotice = string.Empty;

    /// <summary>Whether the variable had a value the last time <see cref="RefreshCredential"/> looked. Never the value.</summary>
    private CredentialPresence _presence = CredentialPresence.Unknown;

    /// <summary>
    /// For a route whose CLI keeps an environment-supplied value only on request (<see cref="SecretRoute.PersistFlag"/>):
    /// whether that choice is on right now. Set by the wizard, which owns the other field; null when nothing has said.
    /// </summary>
    private bool? _persistsEntry;

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

    /// <summary>The credential card is shown instead of an ordinary input. See the type remarks.</summary>
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

    /// <summary>
    /// The terminal route in one paragraph, for the card. Where the app can also take the value
    /// (<see cref="OffersInAppEntry"/>) it is the alternative; otherwise it is the only way.
    /// </summary>
    public string CredentialExplanation => OffersInAppEntry
        ? "Or store it once from a real console instead: `defenseclaw keys set` is the CLI's own hidden prompt, and the " +
          "command then reads the value from ~/.defenseclaw/.env by name. On Windows that prompt reads a console, not a pipe."
        : "This app never takes the secret itself. The command reads it from a variable in ~/.defenseclaw/.env, and " +
          "`defenseclaw keys set` — the CLI's own hidden prompt — is the only way to store it that does not put it on a command " +
          "line, and on Windows it reads a real console, not a pipe.";

    // ------------------------------------------------------------------ in-app entry

    /// <summary>
    /// A password box is shown: the CLI reads this secret from an environment variable, so this app can take the
    /// value and supply it in the child's environment for one run. See <see cref="SecretRoute.InAppEnvName"/>.
    /// </summary>
    public bool OffersInAppEntry => IsSecret && InAppEnvName.Length > 0;

    public bool HasEntryProblem => EntryProblem.Length > 0;

    public bool HasEntryNotice => EntryNotice.Length > 0;

    /// <summary>The name a screen reader announces for the password box.</summary>
    public string InAppAutomationName => CredentialPurpose + " (hidden entry)";

    /// <summary>What supplying the value does, in one paragraph, for the card. Names, never a value.</summary>
    public string InAppExplanation
    {
        get
        {
            var stored = Field.Credential?.InAppStorage is { Length: > 0 } custom
                ? " " + custom
                : CredentialEnvName.Length > 0 && !string.Equals(CredentialEnvName, InAppEnvName, StringComparison.Ordinal)
                    ? $" the CLI stores it as {CredentialEnvName} in ~/.defenseclaw/.env."
                    : " the CLI stores it in ~/.defenseclaw/.env.";

            return $"Type it here and this app supplies it to the command as the environment variable {InAppEnvName} — for that one " +
                   $"run only, never on the command line — and{stored} The box is cleared as soon as the run ends.";
        }
    }

    /// <summary>One line under the box: what is held, or why it cannot be used. Never the value or its length.</summary>
    public string EntryStatus => EntryProblem.Length > 0
        ? EntryProblem
        : HasEntry
            ? $"A value is entered (hidden). It goes to the command as {InAppEnvName}, then is cleared." +
              (PersistSentence.Length > 0 ? " " + PersistSentence : string.Empty)
            : EntryNotice.Length > 0
                ? EntryNotice
                : IsStored
                    ? $"Nothing entered. {CredentialEnvName} is already stored and the command uses it: leave this blank to keep it, or type a value to replace it."
                    : "Nothing entered. The command uses the stored variable, if any.";

    /// <summary>True when the variable the command reads already has a value (in <c>.env</c> or this app's environment).</summary>
    private bool IsStored => _presence is CredentialPresence.InDotEnv or CredentialPresence.InEnvironment;

    /// <summary>
    /// What happens to a typed value once the command has it, for a route whose CLI keeps it only on request:
    /// saved to <c>.env</c>, or used once and gone. Empty when nothing is held or the route has no such choice.
    /// </summary>
    public string PersistSentence
    {
        get
        {
            if (!HasEntry || _persistsEntry is not { } persists || Field.Credential?.PersistFlag is not { Length: > 0 } flag)
            {
                return string.Empty;
            }

            return persists
                ? $"The CLI will save it to ~/.defenseclaw/.env ({flag} is on)."
                : $"It will not be saved ({flag} is off): nothing keeps it after this run, so it cannot authenticate afterwards unless it is stored some other way.";
        }
    }

    /// <summary>
    /// Tells the field whether the choice named by <see cref="SecretRoute.PersistFlag"/> is on. Called by the wizard,
    /// which owns that field; a route with no such choice never calls it.
    /// </summary>
    internal void SetPersistState(bool persists)
    {
        if (_persistsEntry == persists)
        {
            return;
        }

        _persistsEntry = persists;
        NotifyEntryChanged();
    }

    /// <summary>
    /// What the review page says about this credential: how the command will get it. For an in-app secret with
    /// a value it is the sentence the review must carry — supplied as an environment variable NAME, value masked.
    /// </summary>
    public string CredentialReviewNote
    {
        get
        {
            if (!OffersInAppEntry)
            {
                return "This app does not send it; the command reads it by name.";
            }

            return HasEntry
                ? $"Supplied to the command as the environment variable {InAppEnvName}=••• (value masked; this run only, not on the command line)." +
                  (PersistSentence.Length > 0 ? " " + PersistSentence : string.Empty)
                : $"Nothing entered here; the command reads {(CredentialEnvName.Length > 0 ? CredentialEnvName : InAppEnvName)} from ~/.defenseclaw/.env if it is stored. Go back to type a value.";
        }
    }

    /// <summary>Raised when <see cref="ClearEntry"/> dropped a held value, so the password box can empty itself.</summary>
    public event EventHandler? EntryCleared;

    /// <summary>
    /// The password box reports what is typed. <paramref name="value"/> must be a copy the caller hands over
    /// (<c>PasswordBox.SecurePassword</c> is one); this takes ownership and disposes the previous entry.
    /// </summary>
    public void SetEntry(SecureString? value)
    {
        if (!OffersInAppEntry)
        {
            // Not a field that takes a value here: refuse to hold one rather than keep it unused.
            value?.Dispose();
            return;
        }

        var shape = SecretEntry.Inspect(value);
        var usable = !shape.IsEmpty && !shape.HasControlCharacter;

        // A value the CLI would refuse is not held: better an empty state and a message than a secret kept
        // for nothing.
        var previous = _entry;
        _entry = usable ? value : null;
        if (!usable)
        {
            value?.Dispose();
        }

        previous?.Dispose();

        EntryProblem = shape.HasControlCharacter
            ? "That value has a line break or another control character in it, which the CLI refuses. Paste it again."
            : string.Empty;

        // Typing something new answers a "was cleared" notice; the empty report a cleared box sends back
        // (PasswordBox.Clear raises PasswordChanged) must not wipe the notice that explains the clearing.
        if (!shape.IsEmpty)
        {
            EntryNotice = string.Empty;
        }

        HasEntry = usable;
        NotifyEntryChanged();
    }

    /// <summary>
    /// The held value as a <see cref="SecretValue"/> for the run that is about to start, or null when there is
    /// nothing usable (no value, only whitespace, or a control character in it). The caller passes it straight to
    /// <see cref="Core.Cli.CliRunOptions.WithEnvironment"/>; nothing here keeps it.
    /// </summary>
    internal SecretValue? MaterializeSecret() => OffersInAppEntry ? SecretEntry.ToSecret(_entry) : null;

    /// <summary>
    /// Drops the held value and tells the password box to empty itself. Called when a run has ended, when the
    /// wizard closes, and when the destination the value was typed for changes.
    /// </summary>
    /// <param name="notice">A sentence for <see cref="EntryStatus"/> saying why, or empty for none.</param>
    public void ClearEntry(string notice = "")
    {
        var held = _entry is not null || EntryProblem.Length > 0;

        _entry?.Dispose();
        _entry = null;

        EntryProblem = string.Empty;
        EntryNotice = held ? notice : string.Empty;
        HasEntry = false;
        NotifyEntryChanged();

        if (held)
        {
            EntryCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The card's Clear button: forget what was typed.</summary>
    [RelayCommand]
    private void DiscardEntry() => ClearEntry();

    private void NotifyEntryChanged()
    {
        OnPropertyChanged(nameof(EntryStatus));
        OnPropertyChanged(nameof(CredentialReviewNote));
        OnPropertyChanged(nameof(PersistSentence));
    }

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
        var inApp = Field.Credential?.InAppVariable(_values) ?? string.Empty;

        // A value typed for one destination must not follow the operator to another (choosing a different
        // preset changes where the token is stored), and must not outlive its route disappearing.
        if (_entry is not null &&
            (!string.Equals(name, CredentialEnvName, StringComparison.Ordinal) || inApp.Length == 0))
        {
            ClearEntry("The value you entered was cleared because the destination changed. Enter it again.");
        }

        CredentialEnvName = name;
        InAppEnvName = inApp;

        if (name.Length == 0)
        {
            _presence = CredentialPresence.Unknown;
            CredentialStatus = "No stored variable applies to the current choices. The CLI reports any missing secret when it runs.";
            CredentialStatusKey = "Neutral";
        }
        else
        {
            var presence = _credentials?.Check(name, fresh) ?? CredentialPresence.Unknown;
            _presence = presence;
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
        OnPropertyChanged(nameof(InAppExplanation));
        NotifyEntryChanged();
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
        // A secret has no Value to check; the only thing that can be wrong is a typed entry the CLI would refuse.
        if (IsSecret)
        {
            return EntryProblem;
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

    partial void OnInAppEnvNameChanged(string value)
    {
        OnPropertyChanged(nameof(OffersInAppEntry));
        OnPropertyChanged(nameof(CredentialExplanation));
        OnPropertyChanged(nameof(InAppExplanation));
    }

    partial void OnEntryProblemChanged(string value) => OnPropertyChanged(nameof(HasEntryProblem));

    partial void OnEntryNoticeChanged(string value) => OnPropertyChanged(nameof(HasEntryNotice));

    /// <summary>What a screen reader announces when this row is reached as an item.</summary>
    public override string ToString() => Label + " (" + FlagDisplay + ")";
}

/// <summary>One page of a wizard, with only the fields whose gates are currently satisfied.</summary>
public sealed partial class WizardStepViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    public WizardStepViewModel(WizardStep step, IReadOnlyList<WizardFieldViewModel> fields, WizardGuideViewModel? guide = null)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        Guide = guide;
    }

    public WizardStep Step { get; }

    public string Title => Step.Title;

    public string Subtitle => Step.Subtitle;

    public IReadOnlyList<WizardFieldViewModel> Fields { get; }

    /// <summary>
    /// The page binds every field and hides the gated ones per item, so a gate flipping on
    /// an earlier page never has to rebuild a collection mid-edit.
    /// </summary>
    public bool HasFields => Fields.Count > 0 || Guide is not null;

    /// <summary>The cards of a guided first step, or null for an ordinary page. A guide page shows them instead of the field list.</summary>
    public WizardGuideViewModel? Guide { get; }

    public bool IsGuide => Guide is not null;

    public bool ShowsFields => Guide is null;

    public override string ToString() => Title;
}
