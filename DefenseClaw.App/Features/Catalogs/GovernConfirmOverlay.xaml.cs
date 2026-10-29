using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// The confirm dialog of the Govern panels: a frame for the shared
/// <see cref="Shell.CommandReviewControl"/>, which handles focus (Cancel first for a destructive command),
/// Esc, Copy and the accessibility text. Nothing else to do here.
/// </summary>
public partial class GovernConfirmOverlay : UserControl
{
    public GovernConfirmOverlay()
    {
        InitializeComponent();
    }
}
