using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The "…" button at the end of a table row (the Mac's <c>CatalogActionMenu</c>): it opens the row's own context menu - the one
/// the grid's row style declares, which a right-click on the row opens too - under the button, so the two doors lead to the
/// same list of actions. Every action in that menu that changes something still goes through the panel's review, exactly as the
/// toolbar's buttons do; the menu only chooses the row.
/// <para>
/// Opening it from the button, like right-clicking, selects the row first unless it is already part of the selection (so a
/// multi-row selection survives opening the menu on one of its rows). Keyboard: Space or Enter on the focused button, or the
/// Menu key / Shift+F10 on the row.
/// </para>
/// </summary>
public sealed class DcRowMenuButton : Button
{
    /// <summary>The menu to open; when unset, the context menu of the row the button sits in.</summary>
    public static readonly DependencyProperty MenuProperty = DependencyProperty.Register(
        nameof(Menu),
        typeof(ContextMenu),
        typeof(DcRowMenuButton),
        new PropertyMetadata(null));

    static DcRowMenuButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcRowMenuButton), new FrameworkPropertyMetadata(typeof(DcRowMenuButton)));
    }

    public ContextMenu? Menu
    {
        get => (ContextMenu?)GetValue(MenuProperty);
        set => SetValue(MenuProperty, value);
    }

    /// <summary>Opens the menu now (what a click does). False when there is no menu to open.</summary>
    public bool OpenMenu()
    {
        var row = DcRowMenu.RowOf(this);
        var menu = Menu ?? (row is null ? null : ContextMenuService.GetContextMenu(row));
        if (menu is null)
        {
            return false;
        }

        if (row is not null)
        {
            DcRowMenu.SelectForMenu(row);
        }

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    protected override void OnClick()
    {
        base.OnClick();
        _ = OpenMenu();
    }
}

/// <summary>
/// Row-menu plumbing shared by the tables. <see cref="SelectsOnRightClickProperty"/> (switched on by the grid style for every row)
/// makes a right-click choose the row it lands on - unless that row is already selected, so a Ctrl- or Shift-built selection
/// survives - which a <c>DataGrid</c> does not do by itself and without which "Acknowledge" would act on whatever was selected before.
/// </summary>
public static class DcRowMenu
{
    public static readonly DependencyProperty SelectsOnRightClickProperty = DependencyProperty.RegisterAttached(
        "SelectsOnRightClick",
        typeof(bool),
        typeof(DcRowMenu),
        new PropertyMetadata(false, OnSelectsOnRightClickChanged));

    public static bool GetSelectsOnRightClick(DependencyObject element) => (bool)element.GetValue(SelectsOnRightClickProperty);

    public static void SetSelectsOnRightClick(DependencyObject element, bool value) => element.SetValue(SelectsOnRightClickProperty, value);

    private static void OnSelectsOnRightClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseRightButtonDown -= OnRightButtonDown;
        if (e.NewValue is true)
        {
            element.PreviewMouseRightButtonDown += OnRightButtonDown;
        }
    }

    private static void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            SelectForMenu(row);
        }
    }

    /// <summary>The grid row an element sits in, or null.</summary>
    internal static DataGridRow? RowOf(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is DataGridRow row)
            {
                return row;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>Selects <paramref name="row"/> alone unless it is already selected (then the selection stays whatever it is).</summary>
    internal static void SelectForMenu(DataGridRow row)
    {
        if (row.IsSelected)
        {
            return;
        }

        // A single-selection grid replaces its selection by itself (and refuses SelectedItems changes).
        if (ItemsControl.ItemsControlFromItemContainer(row) is DataGrid { SelectionMode: DataGridSelectionMode.Extended } grid)
        {
            grid.SelectedItems.Clear();
        }

        row.IsSelected = true;
    }
}

/// <summary>How a copy ended.</summary>
public enum ClipboardResult
{
    /// <summary>All of the text is on the clipboard.</summary>
    Copied,

    /// <summary>The text was longer than <see cref="DcClipboard.MaxChars"/>: the start of it is on the clipboard, followed by a note.</summary>
    Truncated,

    /// <summary>Nothing was copied: another program is holding the clipboard (or it refused the write).</summary>
    Failed,
}

/// <summary>
/// The clipboard, without the exception: another process can hold it open, and "Copy" then has nothing useful to say. Every copy
/// in the app goes through here, like the TUI's <c>copy_windows_clipboard</c>: a brief retry while the clipboard is busy
/// (<c>CLIPBRD_E_CANT_OPEN</c>, about half a second), a failure that is reported (<see cref="Notice"/> - the tray's toast in the running
/// app) instead of swallowed, and a very large payload cut to <see cref="MaxChars"/> with a note instead of freezing the UI.
/// </summary>
public static class DcClipboard
{
    /// <summary>The most characters one copy puts on the clipboard (8 MB as UTF-16; the TUI refuses anything over 16 MiB).</summary>
    public const int MaxChars = 4_000_000;

    /// <summary>
    /// The clipboard format that asks clipboard history and cloud clipboard sync not to keep the copy (Windows 10 1809 and later). Every
    /// copy carries it: what is copied here is often command output, argv, environment or log text, which can hold a secret.
    /// </summary>
    public const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>What a failed copy says.</summary>
    public const string FailureText = "Could not use the clipboard: another program is holding it. Try again.";

    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    private static readonly TimeSpan RetryWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Told about a copy that failed or was cut short (a sentence for the operator). The running app turns it into a tray toast;
    /// it is raised on the thread that copied.
    /// </summary>
    public static event Action<string>? Notice;

    /// <summary>Test seam: what writes to the clipboard (default: <see cref="SetClipboard"/>, the text with <see cref="ExcludeFromMonitorFormat"/>). Throws like the real one when it is held.</summary>
    internal static Action<string>? Writer { get; set; }

    /// <summary>Test seam: what waits between retries (default: <see cref="Thread.Sleep(TimeSpan)"/>).</summary>
    internal static Action<TimeSpan>? Sleeper { get; set; }

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when it could not (another program is holding it). A failure or a cut is reported through <see cref="Notice"/>.</summary>
    public static bool TrySetText(string text) => TryCopy(text) != ClipboardResult.Failed;

    /// <summary>
    /// Copies <paramref name="text"/>, retrying briefly while the clipboard is busy. With <paramref name="report"/> false the caller says
    /// the outcome itself (it has a status line of its own); otherwise a failure or a cut goes to <see cref="Notice"/>.
    /// </summary>
    public static ClipboardResult TryCopy(string text, bool report = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        var truncated = text.Length > MaxChars;
        var payload = truncated ? Truncate(text) : text;
        var result = WriteWithRetry(payload) ? (truncated ? ClipboardResult.Truncated : ClipboardResult.Copied) : ClipboardResult.Failed;
        if (report)
        {
            Report(result);
        }

        return result;
    }

    /// <summary>The start of <paramref name="text"/> (<see cref="MaxChars"/> characters, never half a surrogate pair) and a note saying how much was left out.</summary>
    internal static string Truncate(string text)
    {
        if (text.Length <= MaxChars)
        {
            return text;
        }

        var keep = MaxChars;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        var left = text.Length - keep;
        return text[..keep] + Environment.NewLine + "[Truncated: " + left.ToString("N0", CultureInfo.CurrentCulture) + " more characters were not copied.]";
    }

    /// <summary>What a copy that was cut short tells the operator.</summary>
    public static string TruncatedText => "Copied the first " + MaxChars.ToString("N0", CultureInfo.CurrentCulture) + " characters; the rest was too large for the clipboard.";

    private static void Report(ClipboardResult result)
    {
        switch (result)
        {
            case ClipboardResult.Failed:
                Notice?.Invoke(FailureText);
                break;
            case ClipboardResult.Truncated:
                Notice?.Invoke(TruncatedText);
                break;
        }
    }

    /// <summary>
    /// The data a copy puts on the clipboard: the text, and <see cref="ExcludeFromMonitorFormat"/> (its value is ignored; the format's presence is
    /// the request). Built without touching the clipboard, so a test can check it.
    /// </summary>
    internal static DataObject CreateDataObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = new DataObject();
        data.SetText(text);
        data.SetData(ExcludeFromMonitorFormat, new byte[] { 0, 0, 0, 0 });
        return data;
    }

    /// <summary>The real clipboard write: <see cref="CreateDataObject"/>, kept on the clipboard after the app exits (copy: true).</summary>
    private static void SetClipboard(string text) => Clipboard.SetDataObject(CreateDataObject(text), copy: true);

    private static bool WriteWithRetry(string text)
    {
        var write = Writer ?? SetClipboard;
        var sleep = Sleeper ?? Thread.Sleep;
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                write(text);
                return true;
            }
            catch (ExternalException ex) when (ex.HResult == ClipboardCantOpen)
            {
                // Held open by another process: that is usually over in a moment (a clipboard manager reading what was just copied).
                if (started.Elapsed >= RetryWindow)
                {
                    return false;
                }

                sleep(RetryInterval);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// A table cell's text that is trimmed with an ellipsis and says its whole value in a tooltip - but only while it is actually cut
/// off (a full value needs no tooltip and would only sit on the row underneath). Use through the <c>DcCellText</c> style.
/// </summary>
public static class DcCellToolTip
{
    public static readonly DependencyProperty OnlyWhenTrimmedProperty = DependencyProperty.RegisterAttached(
        "OnlyWhenTrimmed",
        typeof(bool),
        typeof(DcCellToolTip),
        new PropertyMetadata(false, OnChanged));

    public static bool GetOnlyWhenTrimmed(DependencyObject element) => (bool)element.GetValue(OnlyWhenTrimmedProperty);

    public static void SetOnlyWhenTrimmed(DependencyObject element, bool value) => element.SetValue(OnlyWhenTrimmedProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.ToolTipOpening -= OnToolTipOpening;
        if (e.NewValue is true)
        {
            element.ToolTipOpening += OnToolTipOpening;
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock block && !IsTrimmed(block))
        {
            e.Handled = true;
        }
    }

    internal static bool IsTrimmed(TextBlock block)
    {
        if (string.IsNullOrEmpty(block.Text))
        {
            return false;
        }

        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var text = new FormattedText(
            block.Text,
            CultureInfo.CurrentCulture,
            block.FlowDirection,
            typeface,
            block.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);

        return text.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5;
    }
}

/// <summary>
/// Star sizing for a table's columns that holds when the grid starts empty (see <see cref="Views.Panels.ColumnSizing"/> for why a
/// <c>DataGrid</c> cannot do it alone): a column keeps the star weight and <c>MinWidth</c> the XAML gave it, and whenever the room the
/// columns share changes - first layout, a scroll bar arriving with the rows, a resize - the weights are resolved to plain widths.
/// A column whose width is a plain number (the row menu, a badge column) keeps it. <see cref="LeadingColumnsProperty"/> says how many
/// of the first columns share the room when not all of them can fit; the rest wait past the edge to be scrolled to.
/// </summary>
public static class DcGridColumns
{
    public static readonly DependencyProperty FitProperty = DependencyProperty.RegisterAttached(
        "Fit",
        typeof(bool),
        typeof(DcGridColumns),
        new PropertyMetadata(false, OnFitChanged));

    public static readonly DependencyProperty LeadingColumnsProperty = DependencyProperty.RegisterAttached(
        "LeadingColumns",
        typeof(int),
        typeof(DcGridColumns),
        new PropertyMetadata(int.MaxValue));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(Dictionary<DataGridColumn, (double Weight, double Minimum)>),
        typeof(DcGridColumns));

    public static bool GetFit(DependencyObject element) => (bool)element.GetValue(FitProperty);

    public static void SetFit(DependencyObject element, bool value) => element.SetValue(FitProperty, value);

    public static int GetLeadingColumns(DependencyObject element) => (int)element.GetValue(LeadingColumnsProperty);

    public static void SetLeadingColumns(DependencyObject element, int value) => element.SetValue(LeadingColumnsProperty, value);

    private static void OnFitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        grid.Loaded -= OnGridEvent;
        grid.IsVisibleChanged -= OnGridVisible;
        grid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        if (e.NewValue is true)
        {
            grid.Loaded += OnGridEvent;
            grid.IsVisibleChanged += OnGridVisible;
            grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        }
    }

    private static void OnGridEvent(object sender, RoutedEventArgs e) => Apply((DataGrid)sender, FindScrollViewer((DataGrid)sender));

    private static void OnGridVisible(object sender, DependencyPropertyChangedEventArgs e) => Apply((DataGrid)sender, FindScrollViewer((DataGrid)sender));

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 && sender is DataGrid grid)
        {
            Apply(grid, e.OriginalSource as ScrollViewer);
        }
    }

    /// <summary>
    /// Resolves the columns' widths against the grid's viewport now - after the grid changed its columns (<see cref="Apply"/> is otherwise only called by
    /// the grid's own events: first layout, a new viewport width). The Alerts and Audit tables add and remove their Connector column this way.
    /// </summary>
    public static void Refit(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Apply(grid, FindScrollViewer(grid));
    }

    /// <summary>
    /// Resolves the columns' widths against the grid's viewport now. A column keeps the weight and floor it was first seen with (its star width in the
    /// XAML) however many times its width has been resolved since - also across being taken out of the grid and put back, which a column that comes and
    /// goes (the Connector column) does; a column that joins the grid later is learned when it first takes part. A column that is not
    /// <see cref="Visibility.Visible"/> takes no share of the room and is left as it is.
    /// </summary>
    public static void Apply(DataGrid grid, ScrollViewer? scroll)
    {
        if (scroll is null || scroll.ViewportWidth <= 0 || grid.Columns.Count == 0)
        {
            return;
        }

        var state = (Dictionary<DataGridColumn, (double Weight, double Minimum)>?)grid.GetValue(StateProperty);
        if (state is null)
        {
            state = new Dictionary<DataGridColumn, (double, double)>();
            grid.SetValue(StateProperty, state);
        }

        foreach (var column in grid.Columns)
        {
            if (!state.ContainsKey(column))
            {
                state[column] = column.Width.IsStar
                    ? (column.Width.Value, column.MinWidth)
                    : (0, column.Width.IsAbsolute ? column.Width.Value : column.MinWidth);
            }
        }

        var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        // One DIP short of the viewport, so rounding cannot leave a one-pixel sideways scroll bar.
        var available = Math.Floor(scroll.ViewportWidth) - 1;
        var widths = Views.Panels.ColumnSizing.Distribute(
            available,
            shown.Select(c => state[c].Weight).ToArray(),
            shown.Select(c => state[c].Minimum).ToArray(),
            GetLeadingColumns(grid));

        // A column within half a DIP of its target is left alone (no relayout for nothing) - unless the columns as they stand add up
        // to more than the room: eight columns each up to half a DIP over used to beat the one-DIP slack and show a sideways scroll bar.
        var current = shown.Sum(c => c.Width.IsAbsolute ? c.Width.Value : double.PositiveInfinity);
        var overflowing = current > available;
        for (var i = 0; i < shown.Count; i++)
        {
            if (overflowing || !shown[i].Width.IsAbsolute || Math.Abs(shown[i].Width.Value - widths[i]) > 0.5)
            {
                shown[i].Width = new DataGridLength(widths[i]);
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
