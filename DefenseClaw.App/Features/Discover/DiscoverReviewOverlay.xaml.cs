using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The confirm-and-run dialog the Discover panels share: a frame and a run-result area around the shared
/// <see cref="Shell.CommandReviewControl"/>. All state lives in the bound
/// <see cref="ViewModels.DiscoverActionReview"/>; focus (Cancel first for a destructive command, Close when the
/// run finishes), Esc and Copy are the control's.
/// </summary>
public partial class DiscoverReviewOverlay : UserControl
{
    public DiscoverReviewOverlay()
    {
        InitializeComponent();
    }
}
