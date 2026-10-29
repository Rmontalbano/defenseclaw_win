using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// Picks the control template for a generated <see cref="FormField"/> by its
/// <see cref="FormFieldKind"/>. Every <see cref="FormField"/> is the same CLR type
/// regardless of kind, so WPF's usual DataType-implicit-template matching cannot tell a
/// bool toggle from a secret box on its own — this selector is the standard way around
/// that. The named templates it looks up (seven, with the catalog's Info line) live as resources in
/// <c>ConfigEditorView.xaml</c>.
/// </summary>
public sealed class FormFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BoolTemplate { get; set; }

    public DataTemplate? IntTemplate { get; set; }

    public DataTemplate? StringTemplate { get; set; }

    public DataTemplate? EnvNameTemplate { get; set; }

    public DataTemplate? SecretTemplate { get; set; }

    public DataTemplate? ChoiceTemplate { get; set; }

    /// <summary>A read-only line of the runtime's catalog (<see cref="FormFieldKind.Info"/>, CUST-391): a label and a value, no control.</summary>
    public DataTemplate? InfoTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not FormField field)
        {
            return base.SelectTemplate(item, container);
        }

        return field.Kind switch
        {
            FormFieldKind.Bool => BoolTemplate,
            FormFieldKind.Int => IntTemplate,
            FormFieldKind.EnvName => EnvNameTemplate,
            FormFieldKind.Secret => SecretTemplate,
            FormFieldKind.Choice => ChoiceTemplate,
            FormFieldKind.Info => InfoTemplate ?? StringTemplate,
            _ => StringTemplate,
        };
    }
}
