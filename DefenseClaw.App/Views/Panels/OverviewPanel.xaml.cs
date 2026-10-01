using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Overview panel. Paired with
/// <see cref="ViewModels.OverviewPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>. Esc closes the review dialog (Scan Skills, the gateway actions) from anywhere in the panel.
/// </summary>
public sealed partial class OverviewPanel : UserControl
{
    public OverviewPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is OverviewPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
