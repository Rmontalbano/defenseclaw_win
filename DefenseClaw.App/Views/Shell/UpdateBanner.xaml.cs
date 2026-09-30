using System.Windows.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The update banner in the dashboard's banner region. Purely presentational: every bit of it is bound to the shell view-model
/// (<c>ShowUpdateBanner</c>, <c>UpdateBannerTitle</c>, <c>ReviewUpdateCommand</c> and friends).
/// </summary>
public partial class UpdateBanner : UserControl
{
    public UpdateBanner() => InitializeComponent();
}
