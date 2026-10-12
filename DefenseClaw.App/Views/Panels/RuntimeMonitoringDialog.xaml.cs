using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The Runtime monitoring dialog (CUST-348) over its <see cref="RuntimeMonitoringViewModel"/>: a frame, the state and the switches around the shared review
/// overlay. All state lives in the view-model. Esc closes the review first and then the dialog; focus goes into the dialog when it opens.
/// </summary>
public partial class RuntimeMonitoringDialog : System.Windows.Controls.UserControl
{
    public RuntimeMonitoringDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Scrim.IsVisibleChanged += OnScrimVisibleChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is RuntimeMonitoringViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    /// <summary>When the dialog opens, the first control in it takes the focus.</summary>
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
