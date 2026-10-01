using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Alerts panel. Paired with
/// <see cref="ViewModels.AlertsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Keyboard.</b> Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel
/// handles that command by focusing the filter box, so the shell can also aim it at this page from
/// outside (<c>ApplicationCommands.Find.Execute(null, page)</c>) regardless of where focus is. Esc
/// closes the acknowledge/dismiss review if it is open, otherwise the detail pane. A control that
/// handled Esc itself first (an open ComboBox list) keeps it: the handler listens on the bubbling
/// <c>KeyDown</c>, not the tunnelling preview event.
/// </para>
/// </summary>
public sealed partial class AlertsPanel : UserControl
{
    /// <summary>Where focus was before the review dialog took it, so closing the dialog gives it back.</summary>
    private IInputElement? _focusBeforeReview;

    public AlertsPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
        ReviewScrim.IsVisibleChanged += OnReviewVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True while the panel is narrow enough that a selected alert's detail replaces the list instead of sitting beside it.</summary>
    public bool IsCompact => CompactLayout.GetIsCompact(this);

    /// <summary>Moves keyboard focus to the filter box (the toolbar's search) and selects its text, ready to type over.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    /// <summary>The table's selection is a set (Extended): the view-model keeps all of it for the row menu; the first row still drives the detail pane.</summary>
    private void OnAlertSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AlertsPanelViewModel viewModel)
        {
            viewModel.NoteSelection(AlertList.SelectedItems.OfType<AlertItem>().OrderBy(AlertList.Items.IndexOf));
        }
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlertsPanelViewModel { ActionRows.Count: > 0 } viewModel)
        {
            _ = DcClipboard.TrySetText(AlertsPanelViewModel.CopyText(viewModel.ActionRows));
        }
    }

    private void OnAcknowledge(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlertsPanelViewModel viewModel)
        {
            viewModel.OpenAcknowledgeCommand.Execute(null);
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlertsPanelViewModel viewModel)
        {
            viewModel.OpenDismissCommand.Execute(null);
        }
    }

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || DataContext is not AlertsPanelViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsReviewOpen)
        {
            viewModel.CancelReviewCommand.Execute(null);
            e.Handled = true;
        }
        else if (viewModel.HasSelection)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// In the compact layout selecting an alert swaps the list for the detail, so the row that had focus is gone and Esc
    /// (handled here) would never arrive: focus follows the pane - to its close button when it opens, back to the list
    /// when it closes. Beside the list (a wide panel) nothing moves.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AlertsPanelViewModel.HasSelection) || !IsCompact || sender is not AlertsPanelViewModel viewModel)
        {
            return;
        }

        var opened = viewModel.HasSelection;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                UIElement? target = opened ? Inspector.CloseButton : AlertList;
                _ = target?.Focus();
            }));
    }

    /// <summary>
    /// A modal dialog has to take keyboard focus with it: without this the operator opens the review
    /// with the keyboard and is left focused on a button now hidden behind the scrim. Focus goes to
    /// Cancel - the safe default - and comes back to where it was when the dialog closes.
    /// </summary>
    private void OnReviewVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _focusBeforeReview = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ReviewCancelButton.Focus()));
            return;
        }

        var back = _focusBeforeReview;
        _focusBeforeReview = null;
        if (back is UIElement { IsVisible: true } element)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => element.Focus()));
        }
    }
}
