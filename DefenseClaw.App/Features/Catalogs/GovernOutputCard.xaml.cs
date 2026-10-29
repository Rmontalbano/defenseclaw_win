using System.Windows.Controls;
using System.Windows.Threading;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// Card for the output of a read-only Govern command. All state is on the view-model; the only behaviour here is that the
/// card brings itself into view when it opens, because it sits under the list, usually off screen.
/// </summary>
public partial class GovernOutputCard : UserControl
{
    public GovernOutputCard()
    {
        InitializeComponent();

        // Deferred a beat: the card has no size until the layout pass that follows it becoming visible.
        Card.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Card.BringIntoView));
            }
        };
    }
}
