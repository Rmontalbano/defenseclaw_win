using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The review step for a gateway action started from the tray menu or the command palette: the
/// exact argv, what it does, and a confirm that must be pressed. It is a window around the shared
/// <see cref="CommandReviewControl"/>, so the tier (from <see cref="DefenseClaw.Core.Cli.CommandTiers"/>), the
/// danger-styled confirm for a destructive verb (none of start / stop / restart is destructive today), the initial
/// focus and Esc behave exactly as in the dashboard's overlays.
/// </summary>
public sealed partial class GatewayActionDialog : FluentWindow
{
    private GatewayActionDialog(CommandReview review)
    {
        InitializeComponent();

        ReviewControl.Review = review;
        ReviewControl.ConfirmCommand = new RelayCommand(() => DialogResult = true);
        ReviewControl.CancelCommand = new RelayCommand(() => DialogResult = false);

        // Mica chrome follows the OS theme like the dashboard's does.
        SystemThemeWatcher.Watch(this);

        // Esc backs out from anywhere in the window, not only from inside the control.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    /// <summary>The review this dialog shows for <paramref name="action"/>.</summary>
    internal static CommandReview ReviewFor(GatewayAction action) => new()
    {
        Title = GatewayControl.Title(action) + "?",
        Summary = GatewayControl.ReviewNote(action),
        Steps = new[] { new CommandReviewStep(GatewayControl.Argv(action), executable: GatewayControl.Executable) },
        ConfirmLabel = GatewayControl.Title(action),
    };

    /// <summary>
    /// Shows the review and returns true only if the operator confirmed. Modal, so a second request
    /// cannot open while one is pending (the caller also guards re-entry).
    /// </summary>
    /// <param name="owner">The dashboard when it is showing; null (the tray-only start, before any window exists)
    /// or a hidden or minimised window centres the dialog on screen.</param>
    /// <param name="action">The gateway verb to review.</param>
    public static bool Confirm(Window? owner, GatewayAction action)
    {
        var dialog = new GatewayActionDialog(ReviewFor(action));

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }
}
