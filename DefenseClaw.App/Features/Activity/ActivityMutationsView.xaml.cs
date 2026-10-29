using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The Activity panel's Mutations tab (see the XAML): a table of configuration and policy changes beside a before / after inspector.
/// Its data context is <see cref="ActivityMutationsViewModel"/>. Esc closes the inspector.
/// </summary>
public sealed partial class ActivityMutationsView : UserControl
{
    public ActivityMutationsView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is ActivityMutationsViewModel { HasSelection: true } viewModel)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
