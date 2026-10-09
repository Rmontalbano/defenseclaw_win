using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The AI Discovery tuning dialog (CUST-271) over its <see cref="AiDiscoveryTuningViewModel"/>: a frame and a form around the shared review overlay.
/// All state lives in the view-model. Esc closes the review first and then the dialog; focus goes into the dialog when it opens.
/// </summary>
public partial class AiDiscoveryTuningDialog : System.Windows.Controls.UserControl
{
    public AiDiscoveryTuningDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Scrim.IsVisibleChanged += OnScrimVisibleChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is AiDiscoveryTuningViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    private void OnScrimVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => Scrim.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
        }
    }
}
