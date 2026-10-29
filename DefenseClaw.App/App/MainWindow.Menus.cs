using DefenseClaw.App.Services;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App;

/// <summary>
/// The menu bar's half of the dashboard window (CUST-384): what its rows are made of and what choosing one does. Every row is a registry command
/// (<see cref="ShellCommandRegistry"/>), the palette's own for everything the palette lists, so a menu row, a palette row and a toolbar button run
/// the same handler and are enabled by the same rule. The window adds only what a window does itself (<see cref="ShellWindowCommands"/>), each
/// through the path its own button already takes.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the window does for its menu bar; made by <see cref="WireMenuBar"/>.</summary>
    private ShellWindowCommands _windowCommands = null!;

    /// <summary>Hooks the menu bar up: its rows come from <see cref="BuildMenus"/>, and a chosen row runs as a palette choice does.</summary>
    private void WireMenuBar()
    {
        _windowCommands = new ShellWindowCommands(
            OpenPalette: OpenPalette,
            CloseToTray: () => HideToTray(hint: true),
            Exit: () => ExitRequested?.Invoke(this, EventArgs.Empty),
            IsSidebarOpen: () => RootNavigation.IsPaneOpen,
            ToggleSidebar: () => RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen);

        MenuBar.Source = BuildMenus;
        MenuBar.CommandChosen += OnMenuCommandChosen;
    }

    /// <summary>
    /// The menus as they are now: the palette's rows (without the CLI registry, which the menus do not list - and so without the Docker, Terraform
    /// and Agent360 looks that the palette starts when it opens) and the menu bar's own. Reading them runs nothing.
    /// </summary>
    internal IReadOnlyList<ShellMenu> BuildMenus()
    {
        var commands = ShellCommandRegistry.Build(_catalog, _actions, NavigateTo, OpenShortcuts, _appearance, curated: null, _connectorScope).ToList();
        commands.AddRange(ShellCommandRegistry.BuildMenuOnlyCommands(_actions, _windowCommands));
        return ShellMenuModel.Build(commands, _catalog, id => ShellMenuModel.CheckedState(id, _appearance, RootNavigation.IsPaneOpen));
    }

    /// <summary>
    /// A row was chosen. An open overlay closes first (the row may be another overlay, or a page the veil would hide), then the command runs the way a
    /// palette choice runs: after the menu has closed and focus is back, with a failure reported rather than thrown.
    /// </summary>
    private void OnMenuCommandChosen(object? sender, ShellCommand command)
    {
        CloseOverlays();
        OnPaletteCommandChosen(sender, command);
    }
}
