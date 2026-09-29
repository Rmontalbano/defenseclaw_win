using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// The confirm dialog of the Govern panels. Moves keyboard focus into the dialog when it opens — onto Cancel
/// for a destructive command so Enter can never run it by accident, onto Run otherwise.
/// </summary>
public partial class GovernConfirmOverlay : UserControl
{
    public GovernConfirmOverlay()
    {
        InitializeComponent();
        Overlay.IsVisibleChanged += OnOverlayVisibleChanged;
    }

    private void OnOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        var destructive = DataContext is GovernPanelViewModelBase { IsConfirmDestructive: true };
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => (destructive ? (Control)CancelButton : RunButton).Focus()));
    }
}
