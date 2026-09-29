using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The review step for a gateway action started from the tray menu or the command palette: the
/// exact argv, what it does, and a confirm that must be pressed. The tier comes from
/// <see cref="CommandTiers"/>, the same classifier every other confirm surface uses, so a
/// destructive verb would get the "Destructive" badge and a danger-styled primary button here too
/// (none of start / stop / restart is destructive today).
/// </summary>
public sealed partial class GatewayActionDialog : FluentWindow
{
    private GatewayActionDialog(GatewayActionDialogModel model)
    {
        InitializeComponent();
        DataContext = model;

        ConfirmButton.Appearance = model.IsDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;

        // Mica chrome follows the OS theme like the dashboard's does.
        SystemThemeWatcher.Watch(this);

        // The operator asked for this action; the primary button is the natural landing spot and
        // Enter confirms. Esc (IsCancel) and Cancel back out without running anything.
        Loaded += (_, _) => _ = ConfirmButton.Focus();
    }

    /// <summary>
    /// Shows the review and returns true only if the operator confirmed. Modal, so a second request
    /// cannot open while one is pending (the caller also guards re-entry).
    /// </summary>
    /// <param name="owner">The dashboard when it is showing; null centres on screen.</param>
    /// <param name="action">The gateway verb to review.</param>
    public static bool Confirm(Window? owner, GatewayAction action)
    {
        var argv = GatewayControl.Argv(action);
        var tier = CommandTiers.Classify(argv);

        var model = new GatewayActionDialogModel(
            Heading: GatewayControl.Title(action) + "?",
            CommandText: GatewayControl.CommandText(action),
            Note: GatewayControl.ReviewNote(action),
            ConfirmLabel: GatewayControl.Title(action),
            IsDestructive: tier == CommandTier.Destructive,
            IsPlainStateChange: tier == CommandTier.StateChanging);

        var dialog = new GatewayActionDialog(model);

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

    private void OnConfirmClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>What <c>GatewayActionDialog.xaml</c> binds to.</summary>
internal sealed record GatewayActionDialogModel(
    string Heading,
    string CommandText,
    string Note,
    string ConfirmLabel,
    bool IsDestructive,
    bool IsPlainStateChange);
