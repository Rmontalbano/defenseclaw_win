using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>What kind of WPF control a <see cref="FormField"/> renders as.</summary>
public enum FormFieldKind
{
    Bool,
    Int,
    String,

    /// <summary>An <c>*_env</c> key: the value is an environment-variable *name*, never a secret.</summary>
    EnvName,

    /// <summary>
    /// A literal secret value (e.g. <c>gateway.token</c>). The CLI hands these back masked, so the
    /// field is always read-only in FORM and shows a placeholder — never a value, never a reveal.
    /// </summary>
    Secret,

    /// <summary>
    /// A text value the TUI offers as a list (<c>llm.provider</c>, <c>claw.mode</c>, the severity actions, ...): a combo box over
    /// <see cref="FormField.Choices"/>. A value the list does not have is one more item, selected: never replaced.
    /// </summary>
    Choice,
}

/// <summary>
/// A single leaf value in the generated form. Bound directly by the DataTemplates in
/// <c>Views\ConfigEditor\ConfigEditorWindow.xaml</c> — one template per <see cref="Kind"/>.
/// <para>
/// A field that is not <see cref="IsEditable"/> never commits: the guard lives here as well as in the
/// UI (a disabled control) so that a masked value can not reach config.yaml by any route.
/// </para>
/// <para>
/// <b>Validation.</b> An editable field checks its own value every time the operator changes it
/// (<see cref="ConfigFieldValidator"/>) and reports through <see cref="Validation"/>; the view-model reads that when the
/// commit arrives. A value the operator did not change is never an error (<see cref="FieldValidation.AsExistingValue"/>).
/// </para>
/// </summary>
public sealed partial class FormField : ObservableObject
{
    private readonly Action<FormField> _onCommit;
    private bool _suppressCommit;

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private double _numberValue;

    [ObservableProperty]
    private string _textValue = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>What the field's value last checked out as. Errors are only ever for a changed value; see the class remarks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValidation))]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(ValidationTone))]
    private FieldValidation _validation = FieldValidation.Ok;

    /// <param name="options">For <see cref="FormFieldKind.Choice"/>: the values the list offers (the current value is added to the items if it is not one of them).</param>
    /// <param name="hint">The TUI's one-line hint for the key, or null.</param>
    public FormField(
        string key,
        string displayName,
        string path,
        FormFieldKind kind,
        object? originalValue,
        bool isEditable,
        string? disabledReason,
        Action<FormField> onCommit,
        bool isMasked = false,
        IReadOnlyList<string>? options = null,
        string? hint = null)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
        Kind = kind;
        OriginalValue = originalValue;
        IsEditable = isEditable;
        DisabledReason = disabledReason;
        IsMasked = isMasked || kind == FormFieldKind.Secret;
        Hint = string.IsNullOrWhiteSpace(hint) ? null : hint;
        _onCommit = onCommit;

        _suppressCommit = true;
        switch (kind)
        {
            case FormFieldKind.Bool:
                BoolValue = originalValue is true;
                break;
            case FormFieldKind.Int:
                NumberValue = originalValue switch { int i => i, long l => l, double d => d, _ => 0 };
                break;
            default:
                TextValue = originalValue?.ToString() ?? string.Empty;
                break;
        }

        _suppressCommit = false;

        ChoiceValues = kind == FormFieldKind.Choice ? options ?? Array.Empty<string>() : Array.Empty<string>();
        Choices = kind == FormFieldKind.Choice ? ConfigFieldCatalog.ChoiceItems(ChoiceValues, TextValue) : Array.Empty<ChoiceOption>();
        OriginalText = ValueText();
        Revalidate();
    }

    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>Full dotted path from the document root, e.g. <c>guardrail.connectors.claudecode.mode</c>.</summary>
    public string Path { get; }

    public FormFieldKind Kind { get; }

    public object? OriginalValue { get; }

    /// <summary>False when the key could not be located unambiguously in the raw config, or its value is masked — see <see cref="DisabledReason"/>.</summary>
    public bool IsEditable { get; }

    public string? DisabledReason { get; }

    public bool IsSecret => Kind == FormFieldKind.Secret;

    /// <summary>True when the text this field shows is a CLI masking placeholder (or the field is a secret): display-only, never written back.</summary>
    public bool IsMasked { get; }

    /// <summary>True when the field is locked and there is a reason to show under its label.</summary>
    public bool ShowReadOnlyNote => !IsEditable && !string.IsNullOrEmpty(DisabledReason);

    /// <summary>What a secret field displays in place of a value: never the value, and never a reveal.</summary>
    public string MaskedText => TextValue.Length == 0 ? "(not set)" : SecretValue.Redacted;

    /// <summary>The TUI's one-line hint for this key (<see cref="ConfigFieldCatalog"/>), or null.</summary>
    public string? Hint { get; }

    public bool HasHint => Hint is not null;

    /// <summary>The row's tooltip: the hint, then the dotted path.</summary>
    public string ToolTipText => Hint is null ? Path : Hint + Environment.NewLine + Path;

    /// <summary>The row's UI Automation help text: the hint, then the dotted path.</summary>
    public string HelpText => Hint is null ? Path : Hint + " " + Path;

    /// <summary>For a <see cref="FormFieldKind.Choice"/>: the values the TUI's list offers, in its order. Empty for every other kind.</summary>
    public IReadOnlyList<string> ChoiceValues { get; }

    /// <summary>For a <see cref="FormFieldKind.Choice"/>: what the combo shows. <see cref="ChoiceValues"/> plus, when the field holds a value they do not have, that value.</summary>
    public IReadOnlyList<ChoiceOption> Choices { get; }

    /// <summary>The field's value as text when it was loaded: the baseline "changed" is measured against.</summary>
    public string OriginalText { get; }

    /// <summary>True when the operator has put a value in this field that differs from the one it was loaded with (changing it back is not a change).</summary>
    public bool IsChanged => !string.Equals(ValueText(), OriginalText, StringComparison.Ordinal);

    public bool ShowValidation => !Validation.IsOk;

    /// <summary>The line under the field, severity word first (<see cref="FieldValidation.DisplayText"/>).</summary>
    public string ValidationText => Validation.DisplayText;

    /// <summary>The design system's tone key for the line: <c>Critical</c> for an error, <c>Warn</c> for a warning.</summary>
    public string ValidationTone => Validation.IsError ? "Critical" : "Warn";

    /// <summary>
    /// What a Choice's combo box binds its selection to. It is <see cref="TextValue"/> without the one thing a binding must not do to it:
    /// a combo whose selection is cleared (items replaced, nothing matching) writes null, which would blank the value in config.yaml.
    /// </summary>
    public string? SelectedChoice
    {
        get => TextValue;
        set
        {
            if (value is not null)
            {
                TextValue = value;
            }
        }
    }

    partial void OnBoolValueChanged(bool value) => Commit();

    partial void OnNumberValueChanged(double value) => Commit();

    partial void OnTextValueChanged(string value)
    {
        Commit();
        OnPropertyChanged(nameof(MaskedText));
        OnPropertyChanged(nameof(SelectedChoice));
    }

    private void Commit()
    {
        if (_suppressCommit || !IsEditable)
        {
            return;
        }

        IsDirty = true;
        Revalidate();
        _onCommit(this);
    }

    /// <summary>The current value as the validator reads it: a bool as <c>true</c>/<c>false</c>, an integer in invariant digits, anything else as it is.</summary>
    private string ValueText() => Kind switch
    {
        FormFieldKind.Bool => BoolValue ? "true" : "false",
        FormFieldKind.Int => ((int)Math.Round(NumberValue)).ToString(CultureInfo.InvariantCulture),
        _ => TextValue ?? string.Empty,
    };

    /// <summary>
    /// Checks the current value. A field that cannot be edited is never checked (there is nothing to fix, and a masked placeholder is not a
    /// value), and a value that is still the one the file had never blocks: its error, if any, is shown as the warning it is.
    /// </summary>
    private void Revalidate()
    {
        if (!IsEditable)
        {
            Validation = FieldValidation.Ok;
            return;
        }

        var result = ConfigFieldValidator.Validate(Path, Kind, ValueText(), OriginalText, ChoiceValues);
        Validation = IsChanged ? result : result.AsExistingValue();
    }

    /// <summary>The value to write back, formatted for whichever kind this field is.</summary>
    public string CurrentRawValue => Kind switch
    {
        FormFieldKind.Bool => YamlSectionEditor.FormatScalar(FormFieldKind.Bool, BoolValue),
        FormFieldKind.Int => YamlSectionEditor.FormatScalar(FormFieldKind.Int, (int)Math.Round(NumberValue)),
        _ => YamlSectionEditor.FormatScalar(FormFieldKind.String, TextValue),
    };

    /// <summary>
    /// The name a screen reader announces for the row. Never includes the value, so a secret cannot
    /// be read out through it.
    /// </summary>
    public override string ToString() => Kind switch
    {
        FormFieldKind.Bool => $"{DisplayName}, on/off setting",
        FormFieldKind.Int => $"{DisplayName}, number setting",
        FormFieldKind.EnvName => $"{DisplayName}, environment variable name",
        FormFieldKind.Secret => $"{DisplayName}, secret, masked and read-only",
        FormFieldKind.Choice => $"{DisplayName}, choice setting",
        _ => IsMasked ? $"{DisplayName}, masked and read-only" : $"{DisplayName}, text setting",
    };
}

/// <summary>A simple string list — <c>ai_discovery.scan_roots</c> and its siblings.</summary>
public sealed partial class FormListField : ObservableObject
{
    private readonly Action<FormListField> _onCommit;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _newItemText = string.Empty;

    public FormListField(
        string key,
        string displayName,
        string path,
        IEnumerable<string> items,
        bool isEditable,
        string? disabledReason,
        Action<FormListField> onCommit)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
        IsEditable = isEditable;
        DisabledReason = disabledReason;
        _onCommit = onCommit;
        Items = new ObservableCollection<string>(items);
        Items.CollectionChanged += (_, _) =>
        {
            if (!IsEditable)
            {
                return;
            }

            IsDirty = true;
            _onCommit(this);
        };
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Path { get; }

    public bool IsEditable { get; }

    public string? DisabledReason { get; }

    /// <summary>True when the list is locked and there is a reason to show under its label.</summary>
    public bool ShowReadOnlyNote => !IsEditable && !string.IsNullOrEmpty(DisabledReason);

    public ObservableCollection<string> Items { get; }

    [RelayCommand]
    private void AddItem()
    {
        if (!IsEditable)
        {
            return;
        }

        var text = NewItemText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Items.Add(text);
        NewItemText = string.Empty;
    }

    [RelayCommand]
    private void RemoveItem(string? item)
    {
        if (IsEditable && item is not null)
        {
            Items.Remove(item);
        }
    }

    public override string ToString() => $"{DisplayName}, list of {Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
}

/// <summary>An unmapped or too-complex subtree: pretty-printed YAML, read-only.</summary>
public sealed class RawBlockNode
{
    public RawBlockNode(string key, string displayName, string path, string yaml, string reason)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
        Yaml = yaml;
        Reason = reason;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Path { get; }

    public string Yaml { get; }

    /// <summary>Why this renders read-only instead of as fields — shown as a caption and a tooltip.</summary>
    public string Reason { get; }

    public override string ToString() => $"{DisplayName}, read-only YAML block";
}

/// <summary>A nested mapping: some scalar fields, maybe simple lists, maybe further sub-groups, maybe raw blocks.</summary>
public class FormGroup
{
    public FormGroup(string key, string displayName, string path)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Path { get; }

    public ObservableCollection<FormField> Fields { get; } = new();

    public ObservableCollection<FormListField> Lists { get; } = new();

    public ObservableCollection<FormGroup> SubGroups { get; } = new();

    public ObservableCollection<RawBlockNode> RawBlocks { get; } = new();

    public bool HasContent => Fields.Count > 0 || Lists.Count > 0 || SubGroups.Count > 0 || RawBlocks.Count > 0;

    /// <summary>True when the mapping has nothing to render (e.g. <c>observability: {}</c>) — the card says so instead of showing a blank body.</summary>
    public bool IsEmpty => !HasContent;

    public override string ToString() => $"{DisplayName} group";
}

/// <summary>One top-level YAML key, rendered as a card in the FORM tab.</summary>
public sealed class FormSection : FormGroup
{
    public FormSection(string key, string displayName, string path, bool isKnownToCore, bool existsInRawConfig)
        : base(key, displayName, path)
    {
        IsKnownToCore = isKnownToCore;
        ExistsInRawConfig = existsInRawConfig;
    }

    /// <summary>True when <see cref="DefenseClawConfig.KnownSections"/> recognizes this key.</summary>
    public bool IsKnownToCore { get; }

    /// <summary>True when this top-level key is present in the current RAW text of config.yaml (false once it has been removed there since the form was built).</summary>
    public bool ExistsInRawConfig { get; }

    public override string ToString() => $"{DisplayName} section";
}
