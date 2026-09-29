using System.Windows.Input;

namespace DefenseClaw.App.Services;

/// <summary>
/// The shell's keyboard map, in one place so the key handler, the sidebar tooltips, the command
/// palette and the shortcuts overlay can never disagree about which chord does what.
/// <para>
/// <b>Panels.</b> The 13 panels are numbered in sidebar order: <c>Ctrl+1</c> … <c>Ctrl+9</c> are
/// panels 1-9, <c>Ctrl+0</c> is panel 10, and <c>Ctrl+Shift+1</c> … <c>Ctrl+Shift+3</c> are panels
/// 11-13. (A plain <c>Ctrl+digit</c> never types a character, so it is safe inside text boxes; the
/// shifted set follows the macOS companion's Cmd-Shift-N convention for the overflow.) A panel
/// past the 13th simply has no shortcut and is reached through the sidebar or the palette.
/// </para>
/// </summary>
internal static class ShellShortcuts
{
    /// <summary>How many panels have a number chord (10 plain + 3 shifted).</summary>
    public const int NumberedPanels = 13;

    public const string PaletteText = "Ctrl+K";

    public const string RefreshText = "F5";

    public const string HelpText = "F1";

    public const string FindText = "Ctrl+F";

    public const string CloseText = "Esc";

    /// <summary>
    /// The chord for the panel at <paramref name="index"/> (0-based, sidebar order) as display
    /// text, e.g. <c>Ctrl+3</c>; null when that panel has none.
    /// </summary>
    public static string? PanelChordText(int index)
    {
        if (index < 0 || index >= NumberedPanels)
        {
            return null;
        }

        return index switch
        {
            < 9 => $"Ctrl+{index + 1}",
            9 => "Ctrl+0",
            _ => $"Ctrl+Shift+{index - 9}",
        };
    }

    /// <summary>
    /// The panel index a key press selects, or null when the chord is not a panel chord.
    /// Both the main row and the numeric keypad count. <paramref name="modifiers"/> must be
    /// exactly Ctrl or exactly Ctrl+Shift — Alt combinations belong to menus and screen readers.
    /// </summary>
    public static int? PanelIndexFor(Key key, ModifierKeys modifiers)
    {
        var digit = DigitOf(key);
        if (digit is null)
        {
            return null;
        }

        if (modifiers == ModifierKeys.Control)
        {
            return digit == 0 ? 9 : digit.Value - 1;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && digit is >= 1 and <= 3)
        {
            return 9 + digit.Value;
        }

        return null;
    }

    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };
}
