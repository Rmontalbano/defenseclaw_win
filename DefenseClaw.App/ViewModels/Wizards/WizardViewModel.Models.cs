using System.IO;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// The model catalogue behind the model boxes (<see cref="ModelCatalogue"/>): looked for off the UI thread when a wizard that has such a box
/// opens, and handed to the boxes when it lands. Until then - and for good when the runtime has none - the box is a plain text box.
/// </summary>
public sealed partial class WizardViewModel
{
    private readonly Func<string?, string, ModelCatalogue?> _loadModels;

    /// <summary>The catalogue the boxes use, or null while it is being read and when the runtime has none.</summary>
    internal ModelCatalogue? Catalogue { get; private set; }

    /// <summary>The fields that offer models.</summary>
    internal IEnumerable<WizardFieldViewModel> ModelFields => _fields.Where(f => f.IsModelPicker);

    private async Task LoadModelCatalogueAsync()
    {
        // A wizard with no model box has nothing to look for; a runtime in a container keeps its files in the container, not beside a CLI here.
        if (!ModelFields.Any() || _services.Paths.Runtime.Kind == RuntimeKind.Container)
        {
            return;
        }

        try
        {
            var cli = await _services.Paths.FindExecutableAsync("defenseclaw").ConfigureAwait(false);
            var dataDirectory = _services.Paths.DataDirectory;
            var catalogue = await Task.Run(() => _loadModels(cli, dataDirectory)).ConfigureAwait(true);

            if (!_disposed)
            {
                UseModelCatalogue(catalogue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No catalogue is a plain text box, which is a fine answer; looking must never be the reason a wizard fails to open.
        }
    }

    /// <summary>Gives the model boxes <paramref name="catalogue"/> (null: back to plain text boxes). Also how a test supplies one.</summary>
    internal void UseModelCatalogue(ModelCatalogue? catalogue)
    {
        Catalogue = catalogue;
        foreach (var field in ModelFields)
        {
            field.AttachCatalogue(catalogue);
        }
    }

    /// <summary>The provider or the custom instance a model box lists the models of just changed: its rows are for another list now.</summary>
    private void RefreshModelBoxes(object? changed)
    {
        if (changed is not WizardFieldViewModel source)
        {
            return;
        }

        foreach (var field in ModelFields)
        {
            if (string.Equals(field.Field.PickerProviderFieldId, source.Id, StringComparison.Ordinal) ||
                string.Equals(field.Field.PickerInstanceFieldId, source.Id, StringComparison.Ordinal))
            {
                field.RefreshModelRows();
            }
        }
    }
}
