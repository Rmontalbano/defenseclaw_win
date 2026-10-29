using System.Windows.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The title bar's "Search panels and commands" box (CUST-383): a button that opens the command palette. Its <c>Click</c> bubbles to the
/// window, which opens the palette the way Ctrl+K does; the control itself does nothing else.
/// </summary>
public partial class PaletteLauncher : UserControl
{
    public PaletteLauncher()
    {
        InitializeComponent();
    }

    /// <summary>The button inside (for a test or for focus).</summary>
    internal Button Button => LauncherButton;
}
