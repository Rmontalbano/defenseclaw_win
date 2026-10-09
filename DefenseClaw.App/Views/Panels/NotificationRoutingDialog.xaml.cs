using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The notification routing dialog (CUST-271) over its <see cref="NotificationRoutingViewModel"/>: a frame and a form around the shared
/// review overlay. All state lives in the view-model. Esc closes the review first (the overlay's control handles it while it has focus; this covers
/// focus elsewhere in the dialog) and then the dialog. Focus goes into the dialog when it opens, so the keyboard is not left on the page behind it.
/// </summary>
public partial class NotificationRoutingDialog : System.Windows.Controls.UserControl
{
    public NotificationRoutingDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Scrim.IsVisibleChanged += OnScrimVisibleChanged;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is NotificationRoutingViewModel viewModel && viewModel.HandleEscape())
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
