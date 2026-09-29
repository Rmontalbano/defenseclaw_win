using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
}

/// <summary>
/// A single leaf value in the generated form. Bound directly by the DataTemplates in
/// <c>Views\ConfigEditor\ConfigEditorWindow.xaml</c> — one template per <see cref="Kind"/>.
/// <para>
/// A field that is not <see cref="IsEditable"/> never commits: the guard lives here as well as in the
/// UI (a disabled control) so that a masked value can not reach config.yaml by any route.
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

    public FormField(
        string key,
        string displayName,
        string path,
        FormFieldKind kind,
        object? originalValue,
        bool isEditable,
        string? disabledReason,
        Action<FormField> onCommit,
        bool isMasked = false)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
        Kind = kind;
        OriginalValue = originalValue;
        IsEditable = isEditable;
        DisabledReason = disabledReason;
        IsMasked = isMasked || kind == FormFieldKind.Secret;
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

    partial void OnBoolValueChanged(bool value) => Commit();

    partial void OnNumberValueChanged(double value) => Commit();

    partial void OnTextValueChanged(string value)
    {
        Commit();
        OnPropertyChanged(nameof(MaskedText));
    }

    private void Commit()
    {
        if (_suppressCommit || !IsEditable)
        {
            return;
        }

        IsDirty = true;
        _onCommit(this);
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
