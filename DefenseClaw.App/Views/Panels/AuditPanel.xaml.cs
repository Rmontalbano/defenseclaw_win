using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Audit panel. Paired with
/// <see cref="ViewModels.AuditPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Keyboard.</b> Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel handles
/// that command by focusing the search box, so the shell can also aim it at this page from outside
/// (<c>ApplicationCommands.Find.Execute(null, page)</c>). Esc closes the detail pane; a control that
/// handled Esc itself first (an open ComboBox list) keeps it, because the handler is on the bubbling
/// <c>KeyDown</c>.
/// </para>
/// </summary>
public sealed partial class AuditPanel : UserControl
{
    public AuditPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
    }

    /// <summary>Moves keyboard focus to the search box and selects its text, ready to type over.</summary>
    public void FocusFilter()
    {
        _ = SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is AuditPanelViewModel { HasSelection: true } viewModel)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
