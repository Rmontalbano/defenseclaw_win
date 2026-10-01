using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// <para>
/// <b>Listening follows Loaded / Unloaded, not the DataContext.</b> The view instance is
/// cached and reused for the life of the process, but <see cref="FrameworkElement.Unloaded"/>
/// fires every time the navigation frame swaps it out (and when the window is closed), and
/// <see cref="FrameworkElement.Loaded"/> fires again when it comes back. A subscription made
/// only when the DataContext is set and dropped on Unloaded therefore survived exactly one
/// visit: after navigating away and back the list kept filling but never scrolled again. The
/// subscription is now (re)attached on every Loaded and dropped on every Unloaded, and both
/// are idempotent, so the sequence of events the framework raises does not matter.
/// </para>
/// <para>
/// <b>Keyboard.</b> Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel handles
/// that command by focusing the filter box, so the shell can also aim it at this page from outside
/// (<c>ApplicationCommands.Find.Execute(null, page)</c>). Esc clears the filter text when there is
/// any, otherwise closes the inspector (a control that handled Esc itself first keeps it: the handler is on the bubbling
/// <c>KeyDown</c>). On a narrow panel (<see cref="CompactLayout"/>) the inspector replaces the list, so focus follows it: to
/// its close button when it opens, back to the list when it closes.
/// </para>
/// </summary>
public sealed partial class LogsPanel : UserControl
{
    private LogsPanelViewModel? _viewModel;

    /// <summary>
    /// The view-model whose <c>DisplayedLines</c> this view is currently subscribed to; null
    /// while unsubscribed. Kept separately from <see cref="_viewModel"/> (the DataContext) so
    /// attach and detach can each be repeated safely and always remove exactly what was added.
    /// </summary>
    private LogsPanelViewModel? _attachedTo;
    private bool _scrollScheduled;

    public LogsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
    }

    /// <summary>Moves keyboard focus to the filter box (the toolbar's search) and selects its text, ready to type over.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _viewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.FilterText.Length > 0)
        {
            viewModel.FilterText = string.Empty;
            e.Handled = true;
        }
        else if (viewModel.HasSelection)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>True while the panel is narrow enough that a selected line's inspector replaces the list instead of sitting beside it.</summary>
    public bool IsCompact => CompactLayout.GetIsCompact(this);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogsPanelViewModel.HasSelection) || !IsCompact || sender is not LogsPanelViewModel viewModel)
        {
            return;
        }

        var opened = viewModel.HasSelection;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                UIElement? target = opened ? Inspector.CloseButton : LogListBox;
                _ = target?.Focus();
            }));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as LogsPanelViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // The shell sets the DataContext before the view is ever shown, so usually this only
        // records it and Loaded does the attaching. A DataContext swapped while on screen
        // moves the subscription over immediately.
        if (IsLoaded)
        {
            Attach();
        }
        else
        {
            Detach();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();

        // Lines may have been projected into the collection before this handler was attached
        // (the view-model's catch-up on activation can run ahead of Loaded), and the list must
        // open at the newest line either way.
        ScheduleScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Attach()
    {
        if (ReferenceEquals(_attachedTo, _viewModel))
        {
            return;
        }

        Detach();

        if (_viewModel is not null)
        {
            _viewModel.DisplayedLines.CollectionChanged += OnDisplayedLinesChanged;
            _attachedTo = _viewModel;
        }
    }

    private void Detach()
    {
        if (_attachedTo is not null)
        {
            _attachedTo.DisplayedLines.CollectionChanged -= OnDisplayedLinesChanged;
            _attachedTo = null;
        }
    }

    private void OnDisplayedLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleScrollToEnd();

    private void ScheduleScrollToEnd()
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
