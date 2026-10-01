using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Guardrail;

/// <summary>
/// HILT, block message and judge gate; see <see cref="GuardrailControlsViewModel"/>. Esc dismisses the open review first (the
/// shared review control handles it while it has focus; this covers focus elsewhere), F5 re-reads.
/// </summary>
public partial class GuardrailControlsView : UserControl
{
    public GuardrailControlsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>True when Esc was used to close a review, so the hosting window should stay open.</summary>
    public bool HandlesEscape => DataContext is GuardrailControlsViewModel { IsReviewOpen: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not GuardrailControlsViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsReviewOpen)
        {
            viewModel.CancelReviewCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && viewModel.RefreshCommand.CanExecute(null))
        {
            _ = viewModel.RefreshCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
