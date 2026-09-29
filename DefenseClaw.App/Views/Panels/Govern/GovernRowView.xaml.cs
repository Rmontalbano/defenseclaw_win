using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// One Govern list row. DataContext is a <see cref="ViewModels.GovernRow"/>; every button goes through
/// its <c>ActionCommand</c>, so this view holds no logic.
/// </summary>
public partial class GovernRowView : UserControl
{
    public GovernRowView()
    {
        InitializeComponent();
    }
}
