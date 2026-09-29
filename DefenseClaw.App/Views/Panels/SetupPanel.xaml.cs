using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Setup panel. Paired with
/// <see cref="ViewModels.SetupPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// Keyboard: <b>Ctrl+F</b> moves focus to the wizard search box; <b>Esc</b> dismisses the guardrail
/// review while it is open. Both are handled on the preview phase so they work wherever focus is
/// inside the panel. F5 is bound by the shell to the view-model's <c>RefreshCommand</c>.
/// </para>
/// </summary>
public sealed partial class SetupPanel : UserControl
{
    public SetupPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel { IsGuardrailReviewOpen: true } viewModel)
        {
            viewModel.CancelGuardrailReviewCommand.Execute(null);
            e.Handled = true;
        }
    }
}
