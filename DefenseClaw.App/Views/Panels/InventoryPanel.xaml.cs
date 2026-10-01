using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Inventory panel. Paired with
/// <see cref="ViewModels.InventoryPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Sizing.</b> Everything under the header is one scrolling page (<c>PageScroll</c>), because at the
/// window's 940 x 620 DIP minimum the toolbar, the scan note, the components grid and the table browser
/// together are taller than the ~498 DIPs the page has. A <see cref="ScrollViewer"/> hands its content
/// unlimited height, and a <see cref="DataGrid"/> given that realizes every row, so the grids are never
/// star-sized inside it: <see cref="UpdateLayoutSizes"/> gives the components block a definite height - the
/// rest of the viewport under the toolbar and banners (so on a tall window it fills the page and the table
/// browser's header sits just under it), and at least <see cref="MinComponentsHeight"/> - and the table
/// browser's grid has a fixed row in the XAML. Each keeps its own bounded, virtualized viewport.
/// </para>
/// <para>
/// <b>Width.</b> Below <see cref="WideLayoutMinWidth"/> the raw-row detail pane moves from beside the grid to under
/// it (the block's <c>Tag</c>, which the XAML styles react to), so the grid's leading columns fit at the minimum window
/// width. The components columns are star widths with a floor each; <see cref="ApplyColumnWidths"/> resolves them to plain
/// widths (<see cref="ColumnSizing"/>) whenever the grid's room changes, because a DataGrid that is first laid out empty -
/// as this one is: its rows arrive after the panel is on screen - never redistributes them once the rows come.
/// Mouse-wheel input over either grid scrolls the page when the grid has nothing further to scroll
/// (<see cref="NestedScroll"/>); without that the wheel would stop dead on every grid.
/// </para>
/// </summary>
public partial class InventoryPanel : UserControl
{
    /// <summary>The components block is never shorter than this, however small the window.</summary>
    private const double MinComponentsHeight = 300;

    /// <summary>
    /// Room kept under the components block on a tall window: the table browser's collapsed header
    /// (~52 DIPs) and the 12 DIP gap above it, plus a little air.
    /// </summary>
    private const double BrowserHeaderReserve = 72;

    /// <summary>
    /// Width of the master-detail block (DIPs) from which the detail pane sits beside the grid. A 260 DIP pane
    /// and the 12 DIP gap leave the grid ~590 DIPs, which shows the first six columns without scrolling.
    /// </summary>
    private const double WideLayoutMinWidth = 860;

    /// <summary>
    /// The columns the grid is read for - vendor, name, ecosystem, framework, version, installs. When the window is too narrow for
    /// all nine, these take the whole width and the other three (identity, presence, last seen) wait past the edge to be scrolled to.
    /// </summary>
    private const int LeadingColumns = 6;

    /// <summary>The star weights the XAML gave the components columns, captured before they are replaced by resolved widths.</summary>
    private readonly Dictionary<DataGridColumn, double> _columnWeights = new();

    public InventoryPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) => UpdateLayoutSizes();
        PageScroll.SizeChanged += (_, _) => UpdateLayoutSizes();
        Chrome.SizeChanged += (_, _) => UpdateLayoutSizes();
        MasterDetail.SizeChanged += (_, _) => UpdateLayoutSizes();

        TableBrowser.Expanded += OnTableBrowserExpanded;

        // The grid's columns are re-resolved whenever the width they share changes: a first layout, a scrollbar appearing
        // as rows arrive, a resize, the detail pane moving beside or under the grid.
        ComponentsGrid.Loaded += (_, _) => ApplyColumnWidths(FindScrollViewer(ComponentsGrid));
        ComponentsGrid.IsVisibleChanged += (_, _) => ApplyColumnWidths(FindScrollViewer(ComponentsGrid));
        ComponentsGrid.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) =>
            {
                if (e.ViewportWidthChange != 0)
                {
                    ApplyColumnWidths(e.OriginalSource as ScrollViewer);
                }
            }));

        NestedScroll.SetForwardWheel(ComponentsGrid, true);
        NestedScroll.SetForwardWheel(BrowserGrid, true);
    }

    /// <summary>Ctrl+F focuses the search box; Esc closes the review dialog.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is InventoryPanelViewModel { Review.IsOpen: true })
            {
                return;
            }

            _ = PageToolbar.FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is InventoryPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Re-derives the two things the window size decides: whether the detail pane is beside or under the grid,
    /// and how tall the components block is. Called whenever the viewport, the toolbar/banner stack or the
    /// block itself changes size; setting the same values again is a no-op, so the resulting layout pass does
    /// not loop.
    /// </summary>
    private void UpdateLayoutSizes()
    {
        var viewport = PageScroll.ActualHeight;
        if (viewport <= 0 || MasterDetail.ActualWidth <= 0)
        {
            return;
        }

        var tag = MasterDetail.ActualWidth >= WideLayoutMinWidth ? "Wide" : "Narrow";
        if (!Equals(MasterDetail.Tag, tag))
        {
            MasterDetail.Tag = tag;
        }

        // Where the block starts inside the scrolled content: everything above it, margins included.
        var top = MasterDetail.TranslatePoint(new Point(0, 0), PageContent).Y;
        var height = Math.Max(MinComponentsHeight, Math.Floor(viewport - top - BrowserHeaderReserve));
        if (Math.Abs(MasterRow.Height.Value - height) > 0.5)
        {
            MasterRow.Height = new GridLength(height);
        }
    }

    /// <summary>
    /// Gives each components column its share of the grid's viewport, never below its <c>MinWidth</c>. The grid keeps
    /// the total within the viewport whenever the floors allow, and scrolls sideways when they do not.
    /// </summary>
    private void ApplyColumnWidths(ScrollViewer? scroll)
    {
        if (scroll is null || scroll.ViewportWidth <= 0 || ComponentsGrid.Columns.Count == 0)
        {
            return;
        }

        var columns = ComponentsGrid.Columns;
        if (_columnWeights.Count == 0)
        {
            foreach (var column in columns)
            {
                _columnWeights[column] = column.Width.IsStar ? column.Width.Value : 1;
            }
        }

        // One DIP short of the viewport, so rounding cannot leave a one-pixel sideways scrollbar.
        var widths = ColumnSizing.Distribute(
            Math.Floor(scroll.ViewportWidth) - 1,
            columns.Select(column => _columnWeights.GetValueOrDefault(column, 1)).ToArray(),
            columns.Select(column => column.MinWidth).ToArray(),
            LeadingColumns);

        for (var i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Width.IsAbsolute || Math.Abs(columns[i].Width.Value - widths[i]) > 0.5)
            {
                columns[i].Width = new DataGridLength(widths[i]);
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

    /// <summary>
    /// Opening the table browser adds ~400 DIPs under the fold; bring it into view so the click visibly does
    /// something. Deferred a beat: the expander has not laid out its content yet when the event fires.
    /// </summary>
    private void OnTableBrowserExpanded(object sender, RoutedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TableBrowser.BringIntoView()));

    /// <summary>
    /// A header click sorts the rows within their groups (<see cref="InventoryPanelViewModel.SortBy"/>) - the grid's own sort would
    /// replace the view's sort descriptions, the ones that keep the groups in order. Ascending first, then descending, then ascending.
    /// </summary>
    private void OnComponentsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not InventoryPanelViewModel viewModel)
        {
            return;
        }

        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in ComponentsGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction;
        viewModel.SortBy(e.Column.SortMemberPath, direction);
    }
}
