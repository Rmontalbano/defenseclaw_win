using System.Windows.Input;

namespace DefenseClaw.App.Services;

/// <summary>
/// The shell's keyboard map, in one place so the key handler, the sidebar tooltips, the command
/// palette and the shortcuts overlay can never disagree about which chord does what.
/// <para>
/// <b>Panels.</b> The 14 panels are numbered in sidebar order: <c>Ctrl+1</c> … <c>Ctrl+9</c> are
/// panels 1-9, <c>Ctrl+0</c> is panel 10, and <c>Ctrl+Shift+1</c> … <c>Ctrl+Shift+4</c> are panels
/// 11-14. (A plain <c>Ctrl+digit</c> never types a character, so it is safe inside text boxes; the
/// shifted set follows the macOS companion's Cmd-Shift-N convention for the overflow.) A panel
/// past the 14th simply has no shortcut and is reached through the sidebar or the palette.
/// </para>
/// </summary>
internal static class ShellShortcuts
{
    /// <summary>How many panels have a number chord (10 plain + 4 shifted).</summary>
    public const int NumberedPanels = 14;

    public const string PaletteText = "Ctrl+K";

    public const string RefreshText = "F5";

    public const string HelpText = "F1";

    public const string FindText = "Ctrl+F";

    /// <summary>Audit's export (a panel chord, handled by the panel like Find; no shell chord uses Ctrl+E).</summary>
    public const string AuditExportText = "Ctrl+E";

    public const string CloseText = "Esc";

    /// <summary>
    /// Light/dark toggle. <c>Ctrl+Shift+L</c>: Ctrl+K, F1, F5 and Ctrl+F are taken, Ctrl+digit and Ctrl+Shift+1..3 are
    /// the panels, and Alt chords belong to the system menu and to screen readers - so this is the free, mnemonic
    /// one (L for light). No panel or text box binds it, and it types no character, so it works from anywhere.
    /// </summary>
    public const string ToggleThemeText = "Ctrl+Shift+L";

    /// <summary>
    /// Opens Settings, the footer panel that is not one of the numbered ones. <c>Ctrl+,</c> is the convention Windows apps (and the Mac's
    /// Cmd+,) use for preferences; it types no character and nothing else in the shell or the panels binds it.
    /// </summary>
    public const string SettingsText = "Ctrl+,";

    /// <summary>Run the health check (the Overview's Doctor flow). <c>Ctrl+Shift+H</c>, the Mac's Shift-Cmd-H.</summary>
    public const string HealthCheckText = "Ctrl+Shift+H";

    /// <summary>Scan AI components (AI Discovery's reviewed scan). <c>Ctrl+Shift+A</c>, the Mac's Shift-Cmd-A.</summary>
    public const string ScanAiText = "Ctrl+Shift+A";

    /// <summary>Diagnose in the background (read-only doctor, result as a toast). <c>Ctrl+Shift+D</c>, the Mac's Shift-Cmd-D.</summary>
    public const string DiagnoseText = "Ctrl+Shift+D";

    /// <summary>
    /// Copy the last command's output. The Mac uses Ctrl+Y; here it is <c>Ctrl+Shift+Y</c>, because a bare Ctrl+Y is Redo in every
    /// text box. Likewise <see cref="ExportOutputText"/> is not the Mac's Ctrl+S (Save).
    /// </summary>
    public const string CopyOutputText = "Ctrl+Shift+Y";

    /// <summary>Export the last command's output to a file. <c>Ctrl+Shift+E</c> (Audit's own export is the plain Ctrl+E, a panel chord).</summary>
    public const string ExportOutputText = "Ctrl+Shift+E";

    /// <summary>A second spelling of F5 (the Mac's Cmd-R). Refreshes the current panel, or the gateway status.</summary>
    public const string RefreshAliasText = "Ctrl+R";

    /// <summary>
    /// Every chord the shell itself claims, for the collision tests and the overlay. Bare Ctrl+M, Ctrl+S and Ctrl+Y are deliberately
    /// absent (text editing owns them), and Ctrl+Shift+M belongs to the connector-scope chip.
    /// </summary>
    public static readonly IReadOnlyList<string> ShellChords = new[]
    {
        PaletteText, RefreshText, RefreshAliasText, HelpText, FindText, CloseText, ToggleThemeText, SettingsText,
        HealthCheckText, ScanAiText, DiagnoseText, CopyOutputText, ExportOutputText,
    };

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

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && digit is >= 1 and <= 4)
        {
            return 9 + digit.Value;
        }

        return null;
    }

    /// <summary>
    /// Steps the shared connector scope: All, first connector, second, ... All (the Mac's Ctrl-M, which on Windows is Enter and which the
    /// app does not use). <c>Ctrl+Shift+M</c>: M for "match" is free, types no character, and is clear of the panel chords and of the
    /// theme (L), help and palette chords.
    /// </summary>
    public const string CycleConnectorText = "Ctrl+Shift+M";

    /// <summary>True for exactly Ctrl+Shift+M (see <see cref="CycleConnectorText"/>).</summary>
    public static bool IsCycleConnectorChord(Key key, ModifierKeys modifiers) =>
        key == Key.M && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>True for exactly Ctrl+Shift+L (see <see cref="ToggleThemeText"/>).</summary>
    public static bool IsToggleThemeChord(Key key, ModifierKeys modifiers) =>
        key == Key.L && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>The shell-level action a Ctrl+Shift chord selects, or null when the key press is none of them.</summary>
    public static ShellChordAction? ActionFor(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control && key == Key.R)
        {
            return ShellChordAction.Refresh;
        }

        if (modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return null;
        }

        return key switch
        {
            Key.H => ShellChordAction.HealthCheck,
            Key.A => ShellChordAction.ScanAi,
            Key.D => ShellChordAction.Diagnose,
            Key.Y => ShellChordAction.CopyOutput,
            Key.E => ShellChordAction.ExportOutput,
            _ => null,
        };
    }

    /// <summary>True for exactly Ctrl+, (see <see cref="SettingsText"/>); the comma key of the main block on any layout.</summary>
    public static bool IsSettingsChord(Key key, ModifierKeys modifiers) =>
        key == Key.OemComma && modifiers == ModifierKeys.Control;

    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };
}

/// <summary>The shell actions with a chord of their own beyond navigation and the overlays (see <see cref="ShellShortcuts.ActionFor"/>).</summary>
internal enum ShellChordAction
{
    Refresh,
    HealthCheck,
    ScanAi,
    Diagnose,
    CopyOutput,
    ExportOutput,
}
