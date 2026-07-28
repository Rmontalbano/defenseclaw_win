using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Logs panel. Paired with
/// <see cref="ViewModels.LogsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Why autoscroll lives here.</b> The view-model has no reference to the ListBox, so it
/// cannot call <see cref="ListBox.ScrollIntoView(object)"/> itself. Instead it exposes a
/// public <c>AutoScroll</c> flag; this code-behind watches the bound collection and scrolls
/// to the newest line whenever that flag is set. Filter changes rebuild the whole collection
/// (a clear followed by many adds), so the scroll is coalesced onto the dispatcher's
/// background queue rather than fired once per added item.
/// </para>
/// </summary>
public sealed partial class LogsPanel : UserControl
{
    private LogsPanelViewModel? _viewModel;
    private bool _scrollScheduled;

    public LogsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.DisplayedLines.CollectionChanged -= OnDisplayedLinesChanged;
        }

        _viewModel = e.NewValue as LogsPanelViewModel;
        if (_viewModel is not null)
        {
            _viewModel.DisplayedLines.CollectionChanged += OnDisplayedLinesChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Panel instances are cached and reused across navigation, so this normally only
        // fires at app shutdown; detaching defensively costs nothing.
        if (_viewModel is not null)
        {
            _viewModel.DisplayedLines.CollectionChanged -= OnDisplayedLinesChanged;
        }
    }

    private void OnDisplayedLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is not { AutoScroll: true } || _scrollScheduled)
        {
            return;
        }

        // A filter change clears the collection and re-adds every surviving line, which
        // would otherwise mean one ScrollIntoView per line. Deferring to the background
        // priority coalesces the whole burst into a single scroll once it settles.
        _scrollScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _scrollScheduled = false;
                if (_viewModel is { AutoScroll: true } && LogListBox.Items.Count > 0)
                {
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                }
            }));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel?.UpdateSelection(LogListBox.SelectedItems.Cast<LogEntry>());
}
