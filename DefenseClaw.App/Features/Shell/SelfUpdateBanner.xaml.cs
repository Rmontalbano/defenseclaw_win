using System.Windows.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>This app's own update banner. Purely presentational: bound to <c>SelfUpdateViewModel</c>.</summary>
public partial class SelfUpdateBanner : UserControl
{
    public SelfUpdateBanner() => InitializeComponent();
}
