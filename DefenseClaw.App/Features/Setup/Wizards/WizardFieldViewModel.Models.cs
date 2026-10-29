using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// The searchable model picker of a model field (<c>--model</c>, <c>--judge-model</c>): a list of the ids the runtime's catalogue suggests for the
/// provider the wizard has chosen, narrowed as the operator types, with the typed text always the first row ("use as typed") so a model the
/// catalogue has not shipped yet can still be entered. The text box is the answer; the list only fills it in.
/// <para>
/// With no catalogue (<see cref="AttachCatalogue"/> was never given one) the field is the plain text box it always was - nothing about a
/// command depends on the list.
/// </para>
/// </summary>
public sealed partial class WizardFieldViewModel
{
    private ModelCatalogue? _catalogue;

    /// <summary>The suggestions are on screen.</summary>
    [ObservableProperty]
    private bool _isModelListOpen;

    /// <summary>The row Enter would take.</summary>
    [ObservableProperty]
    private ModelPickerRow? _selectedModelRow;

    /// <summary>One line under the box: where the list comes from and how many it holds, or what to do when it holds none.</summary>
    [ObservableProperty]
    private string _modelListNote = string.Empty;

    /// <summary>The rows for what is typed now: the text as typed (unless it is a catalogue model exactly), then the matching models.</summary>
    public ObservableCollection<ModelPickerRow> ModelRows { get; } = new();

    /// <summary>True for a field that can offer models (whether or not a catalogue was found).</summary>
    public bool IsModelPicker => Field.PickerKind == WizardFieldPickers.Model;

    /// <summary>True when the field shows the searchable list: it can, and the runtime has a catalogue.</summary>
    public bool ShowsModelPicker => IsModelPicker && _catalogue is not null;

    /// <summary>The ordinary text box: a text field that is not the model picker.</summary>
    public bool IsPlainText => IsText && !ShowsModelPicker;

    /// <summary>False for a field that is not a command-line option and so has no flag to show.</summary>
    public bool HasFlagDisplay => FlagDisplay.Length > 0;

    public bool HasModelRows => ModelRows.Count > 0;

    public string ModelPickerAutomationName => Label + " (type to search the model list)";

    public string ModelListAutomationName => "Model suggestions for " + Label;

    /// <summary>The catalogue the wizard found for the installed runtime, or null for none; the picker appears or goes back to a text box.</summary>
    internal void AttachCatalogue(ModelCatalogue? catalogue)
    {
        _catalogue = catalogue;
        OnPropertyChanged(nameof(ShowsModelPicker));
        OnPropertyChanged(nameof(IsPlainText));
        if (!ShowsModelPicker)
        {
            IsModelListOpen = false;
        }

        RefreshModelRows();
    }

    /// <summary>Works the rows out again - the text changed, or the provider (or custom instance) the models belong to did.</summary>
    internal void RefreshModelRows()
    {
        if (!ShowsModelPicker)
        {
            ModelRows.Clear();
            SelectedModelRow = null;
            ModelListNote = string.Empty;
            OnPropertyChanged(nameof(HasModelRows));
            return;
        }

        var provider = Answer(Field.PickerProviderFieldId);
        var instance = Answer(Field.PickerInstanceFieldId);
        var models = _catalogue!.ModelsFor(provider, instance);

        ModelRows.Clear();
        foreach (var row in ModelPicker.Rows(Value, models))
        {
            ModelRows.Add(row);
        }

        // The first row is what Enter takes, as in the TUI's picker: after every keystroke the best match.
        SelectedModelRow = ModelRows.FirstOrDefault();
        ModelListNote = NoteFor(provider, instance, models.Count);
        OnPropertyChanged(nameof(HasModelRows));
    }

    private string Answer(string fieldId) => fieldId.Length == 0 ? string.Empty : _values[fieldId].Trim();

    private string NoteFor(string provider, string instance, int count)
    {
        if (provider.Length == 0 && instance.Length == 0)
        {
            return "Choose a provider to see the models the runtime's catalogue lists for it, or type any model id.";
        }

        var of = instance.Length > 0 && count > 0 ? "the instance " + instance : _catalogue?.LabelFor(provider) ?? provider;
        return count == 0
            ? $"The runtime's catalogue lists no models for {of}. Type the model id."
            : string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"{count} model(s) from the runtime's catalogue for {of}. Type to narrow them; any model id works.");
    }

    /// <summary>Shows the suggestions (the box has focus, or the operator is typing).</summary>
    internal void OpenModelList()
    {
        if (ShowsModelPicker)
        {
            IsModelListOpen = true;
        }
    }

    /// <summary>Hides the suggestions; the text stays as it is.</summary>
    internal void CloseModelList() => IsModelListOpen = false;

    /// <summary>Moves the highlighted row up or down, wrapping at the ends (the TUI's picker does). Opens the list if it was closed.</summary>
    internal void MoveModelSelection(int delta)
    {
        if (!ShowsModelPicker || ModelRows.Count == 0)
        {
            return;
        }

        if (!IsModelListOpen)
        {
            IsModelListOpen = true;
            return;
        }

        var at = SelectedModelRow is null ? -1 : ModelRows.IndexOf(SelectedModelRow);
        var next = at < 0 ? (delta >= 0 ? 0 : ModelRows.Count - 1) : ((at + delta) % ModelRows.Count + ModelRows.Count) % ModelRows.Count;
        SelectedModelRow = ModelRows[next];
    }

    /// <summary>Takes the highlighted row, as Enter does. False when the list is not open or has no row, so Enter means what it always did.</summary>
    internal bool AcceptModelSelection()
    {
        if (!IsModelListOpen || SelectedModelRow is not { } row)
        {
            return false;
        }

        ChooseModel(row);
        return true;
    }

    /// <summary>Puts a row's text in the box and closes the list. Choosing the "use as typed" row keeps the text and closes the list.</summary>
    [RelayCommand]
    private void ChooseModel(ModelPickerRow? row)
    {
        if (row is not null)
        {
            Value = row.Value;
        }

        IsModelListOpen = false;
    }
}
