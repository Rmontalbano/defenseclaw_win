using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The connector batch dialog (CUST-271) over its <see cref="ConnectorBatchViewModel"/>: a frame and a form around the shared review overlay.
/// All state lives in the view-model. Esc closes the review first and then the dialog; focus goes into the dialog when it opens.
/// </summary>
public partial class ConnectorBatchDialog : System.Windows.Controls.UserControl
{
    public ConnectorBatchDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Scrim.IsVisibleChanged += OnScrimVisibleChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ConnectorBatchViewModel viewModel && viewModel.HandleEscape())
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
