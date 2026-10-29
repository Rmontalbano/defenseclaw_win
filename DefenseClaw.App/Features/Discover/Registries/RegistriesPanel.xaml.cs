using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Registries panel. Paired with
/// <see cref="ViewModels.RegistriesPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Sizing.</b> Everything under the header is one scrolling page (<c>PageScroll</c>): at the window's 940 x 620 DIP
/// minimum the toolbar, banners, default-deny card and the two grids together are taller than the ~400 DIPs the page has.
/// A <see cref="ScrollViewer"/> hands its content unlimited height, and a <see cref="DataGrid"/> given that realizes every row,
/// so <see cref="UpdateLayoutSizes"/> gives the sources + details block a definite height - the rest of the viewport under
/// the toolbar and banners (so on a tall window it fills the page), and at least <see cref="MinBlockHeight"/> - and the
/// grids inside stay bounded and virtualized. The page scrolls when the pieces do not fit.
/// </para>
/// <para>
/// <b>Width.</b> The sources columns are star weights with a floor each; <see cref="ApplyColumnWidths"/> resolves them to plain
/// widths (<see cref="ColumnSizing"/>) whenever the grid's room changes, because a DataGrid that is first laid out empty never
/// redistributes them once the rows come (they stayed at 46 DIPs each). When the floors of all seven do not fit, the leading
/// columns take the whole width and the rest wait past the edge.
/// </para>
/// <para>
/// <b>Tabs (CUST-276).</b> Sources shows the master/detail block above; Entries and Approved show a second block, the table of every source's cached
/// entries (<c>EntriesBlock</c>), sized the same way (<see cref="SizeBlock"/>). Only one of the two is on screen, chosen by the view-model's
/// <c>ActiveTab</c>. When the view-model lands an "Open in Registries" link on an entry it raises <c>FocusEntryRequested</c>, and
/// <see cref="FocusSelectedTabEntry"/> brings that row into view and gives its Name cell the keyboard focus.
/// </para>
/// </summary>
public partial class RegistriesPanel : UserControl
{
    /// <summary>
    /// The sources + details block is never shorter than this, however small the window. The details card carries a publisher, a fetch time and the
    /// verdict counts besides what it always had (CUST-276), and the entries under them still get their 150 DIPs.
    /// </summary>
    private const double MinBlockHeight = 500;

    /// <summary>The Entries and Approved table's card is never shorter than this: the table, its action bar and the strip over it.</summary>
    private const double MinEntriesBlockHeight = 380;

    /// <summary>The columns the sources grid is read for - id, enabled, entries - however narrow it is.</summary>
    private const int MinLeadingColumns = 3;

    /// <summary>The star weights the XAML gave the sources columns, captured before they are replaced by resolved widths (0 for a fixed-width column, the row menu).</summary>
    private readonly Dictionary<DataGridColumn, double> _columnWeights = new();

    /// <summary>The floor of each sources column: its <c>MinWidth</c>, or for a fixed-width column that width.</summary>
    private readonly Dictionary<DataGridColumn, double> _columnMinimums = new();

    /// <summary>How tall the raw-fields expander is when closed (just its header), the baseline for how much it adds when open.</summary>
    private double _rawFieldsClosedHeight;

    public RegistriesPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) => UpdateLayoutSizes();
        PageScroll.SizeChanged += (_, _) => UpdateLayoutSizes();
        Chrome.SizeChanged += (_, _) => UpdateLayoutSizes();
        MasterDetail.SizeChanged += (_, _) => UpdateLayoutSizes();
        EntriesBlock.SizeChanged += (_, _) => UpdateLayoutSizes();
        DataContextChanged += OnDataContextChanged;
        RawFields.Expanded += (_, _) => UpdateLayoutSizes();
        RawFields.Collapsed += (_, _) => UpdateLayoutSizes();
        RawFields.SizeChanged += (_, _) =>
        {
            if (!RawFields.IsExpanded && RawFields.ActualHeight > 0)
            {
                _rawFieldsClosedHeight = RawFields.ActualHeight;
            }

            UpdateLayoutSizes();
        };

        // The columns are re-resolved whenever the width they share changes: a first layout, a scrollbar appearing as rows
        // arrive, a resize.
        SourcesGrid.Loaded += (_, _) => ApplyColumnWidths(FindScrollViewer(SourcesGrid));
        SourcesGrid.IsVisibleChanged += (_, _) => ApplyColumnWidths(FindScrollViewer(SourcesGrid));
        SourcesGrid.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) =>
            {
                if (e.ViewportWidthChange != 0)
                {
                    ApplyColumnWidths(e.OriginalSource as ScrollViewer);
                }
            }));

        NestedScroll.SetForwardWheel(SourcesGrid, true);
    }

    /// <summary>Esc closes the add form (the review dialog handles its own Esc).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is RegistriesPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Re-derives how tall the sources + details block is. Called whenever the viewport, the toolbar/banner stack or the block
    /// itself changes size; setting the same value again is a no-op, so the resulting layout pass does not loop.
    /// </summary>
    private void UpdateLayoutSizes()
    {
        var viewport = PageScroll.ActualHeight;
        if (viewport <= 0)
        {
            return;
        }

        // Opening the raw fields adds their body to the details card, so the block grows by as much and the entries keep
        // the room they had. (The body's height does not depend on the block's: its scroller has a MaxHeight.)
        var minimum = MinBlockHeight + (RawFields.IsExpanded ? Math.Max(0, RawFields.ActualHeight - _rawFieldsClosedHeight) : 0);
        SizeBlock(MasterDetail, MasterRow, minimum, viewport);
        SizeBlock(EntriesBlock, EntriesRow, MinEntriesBlockHeight, viewport);
    }

    /// <summary>Gives the one row of a block (the sources + details, or the Entries table) the rest of the viewport under the chrome, never less than <paramref name="minimum"/>. A block that is not on screen is left alone.</summary>
    private void SizeBlock(FrameworkElement block, RowDefinition row, double minimum, double viewport)
    {
        if (block.ActualWidth <= 0)
        {
            return;
        }

        // Where the block starts inside the scrolled content: everything above it, margins included.
        var top = block.TranslatePoint(new Point(0, 0), PageContent).Y;
        var height = Math.Max(minimum, Math.Floor(viewport - top));
        if (Math.Abs(row.Height.Value - height) > 0.5)
        {
            row.Height = new GridLength(height);
        }
    }

    /// <summary>
    /// Gives each sources column its share of the grid's viewport, never below its <c>MinWidth</c>. The columns that fit
    /// (at least the first <see cref="MinLeadingColumns"/>) share the whole viewport; the grid scrolls sideways to the rest.
    /// </summary>
    private void ApplyColumnWidths(ScrollViewer? scroll)
    {
        if (scroll is null || scroll.ViewportWidth <= 0 || SourcesGrid.Columns.Count == 0)
        {
            return;
        }

        var columns = SourcesGrid.Columns;
        if (_columnWeights.Count == 0)
        {
            foreach (var column in columns)
            {
                _columnWeights[column] = column.Width.IsStar ? column.Width.Value : 0;
                _columnMinimums[column] = column.Width.IsStar ? column.MinWidth : column.Width.Value;
            }
        }

        // One DIP short of the viewport, so rounding cannot leave a one-pixel sideways scrollbar.
        var available = Math.Floor(scroll.ViewportWidth) - 1;
        var minimums = columns.Select(column => _columnMinimums.GetValueOrDefault(column, column.MinWidth)).ToArray();
        var widths = ColumnSizing.Distribute(
            available,
            columns.Select(column => _columnWeights.GetValueOrDefault(column, 1)).ToArray(),
            minimums,
            LeadingColumnsThatFit(available, minimums));

        for (var i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Width.IsAbsolute || Math.Abs(columns[i].Width.Value - widths[i]) > 0.5)
            {
                columns[i].Width = new DataGridLength(widths[i]);
            }
        }
    }

    /// <summary>How many columns, from the left, have their floors fit in <paramref name="available"/> - never fewer than <see cref="MinLeadingColumns"/>.</summary>
    private static int LeadingColumnsThatFit(double available, IReadOnlyList<double> minimums)
    {
        var fit = 0;
        var used = 0.0;
        while (fit < minimums.Count && used + minimums[fit] <= available)
        {
            used += minimums[fit];
            fit++;
        }

        return Math.Max(MinLeadingColumns, fit);
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

    // ---- Row menus. The row is already selected (ctl:DcRowMenu), so each item runs the toolbar's command on it.

    private void OnSyncSource(object sender, RoutedEventArgs e) => RunOnSource(v => v.SyncSelectedCommand);

    private void OnToggleSource(object sender, RoutedEventArgs e) => RunOnSource(v => v.ToggleSelectedEnabledCommand);

    private void OnRemoveSource(object sender, RoutedEventArgs e) => RunOnSource(v => v.RemoveSelectedCommand);

    private void OnApproveEntry(object sender, RoutedEventArgs e) => RunOnEntry(sender, v => v.ApproveEntryCommand);

    private void OnRejectEntry(object sender, RoutedEventArgs e) => RunOnEntry(sender, v => v.RejectEntryCommand);

    // The Entries and Approved table's row menu: the same, on the row it was opened on.

    private void OnApproveListedEntry(object sender, RoutedEventArgs e) => RunOnListedEntry(sender, v => v.ApproveListedEntryCommand);

    private void OnRejectListedEntry(object sender, RoutedEventArgs e) => RunOnListedEntry(sender, v => v.RejectListedEntryCommand);

    private void OnShowEntrySource(object sender, RoutedEventArgs e) => RunOnListedEntry(sender, v => v.ShowEntrySourceCommand);

    private void RunOnListedEntry(object sender, Func<RegistriesPanelViewModel, System.Windows.Input.ICommand> command)
    {
        if (DataContext is RegistriesPanelViewModel viewModel)
        {
            // The menu belongs to the row it was opened on; make sure that row is the one the command acts on.
            if (sender is FrameworkElement { DataContext: RegistryEntryRow row })
            {
                viewModel.SelectedTabEntry = row;
            }

            Run(command(viewModel));
        }
    }

    // ---- "Open in Registries": bring the entry the panel selected into sight.

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is RegistriesPanelViewModel old)
        {
            old.FocusEntryRequested -= OnFocusEntryRequested;
        }

        if (e.NewValue is RegistriesPanelViewModel current)
        {
            current.FocusEntryRequested += OnFocusEntryRequested;
        }
    }

    /// <summary>
    /// The view-model landed a link on an entry: scroll it into view in the table and give its row the keyboard focus, so the operator can act on it
    /// (the menu key, Enter on the buttons) without reaching for the mouse. The row is realized after the layout the tab switch caused, so this waits for it.
    /// </summary>
    private void OnFocusEntryRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => FocusSelectedTabEntry()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Brings the selected row of the Entries table into view and gives it the keyboard focus. A <see cref="DataGridRow"/> is not itself a focus
    /// target: the keyboard lives in a cell, so the row is focused through its Name cell, which also makes it the grid's current cell (the arrow keys,
    /// the menu key and Enter start from there). True when a cell took the focus.
    /// </summary>
    internal bool FocusSelectedTabEntry()
    {
        if (TabGrid.SelectedItem is not { } item || TabGrid.Columns.Count == 0)
        {
            return false;
        }

        var column = TabGrid.Columns.FirstOrDefault(c => c.Header as string == "Name") ?? TabGrid.Columns[0];
        TabGrid.UpdateLayout();
        TabGrid.ScrollIntoView(item, column);
        TabGrid.UpdateLayout();
        TabGrid.CurrentCell = new DataGridCellInfo(item, column);

        DependencyObject? node = column.GetCellContent(item);
        while (node is not null and not DataGridCell)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node is DataGridCell cell && cell.Focus();
    }

    private void RunOnSource(Func<RegistriesPanelViewModel, System.Windows.Input.ICommand> command)
    {
        if (DataContext is RegistriesPanelViewModel viewModel)
        {
            Run(command(viewModel));
        }
    }

    private void RunOnEntry(object sender, Func<RegistriesPanelViewModel, System.Windows.Input.ICommand> command)
    {
        if (DataContext is RegistriesPanelViewModel viewModel)
        {
            // The menu belongs to the row it was opened on; make sure that row is the one the command acts on.
            if (sender is FrameworkElement { DataContext: RegistryEntryRow row })
            {
                viewModel.SelectedEntry = row;
            }

            Run(command(viewModel));
        }
    }

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Shows a cell's tooltip only when its text is actually cut off; a full value needs no tooltip and
    /// would only get in the way of the row underneath.
    /// </summary>
    private void OnCellToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock block && !IsTrimmed(block))
        {
            e.Handled = true;
        }
    }

    private static bool IsTrimmed(TextBlock block)
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
